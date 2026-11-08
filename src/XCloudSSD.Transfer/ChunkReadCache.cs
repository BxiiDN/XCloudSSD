using System.Collections.Concurrent;
using XCloudSSD.Core;

namespace XCloudSSD.Transfer;

/// <summary>Per-handle access pattern used to detect sequential reads and trigger read-ahead.</summary>
public sealed class ReadPattern
{
    public long NextExpected = -1;
    public int SequentialHits;
    public long BytesRead;
}

/// <summary>
/// RAM-only chunk buffer for cloud reads. Every byte comes from an HTTP range request; chunks are
/// kept in RAM within the shared <see cref="MemoryBudget"/>, evicted LRU when space is needed and
/// dropped after they sit idle. There is no disk tier.
/// </summary>
public sealed class ChunkReadCache : IReclaimable, IDisposable
{
    private readonly record struct Key(string FileId, string Version, long Index);

    private sealed class Chunk
    {
        public required byte[] Data;
        public int Length;
        public long LastUseTicks;
        public bool Accounted;
    }

    private readonly ICloudProvider _provider;
    private readonly MemoryBudget _budget;
    private readonly object _lock = new();
    private readonly Dictionary<Key, Chunk> _chunks = new();
    private readonly ConcurrentDictionary<Key, Lazy<Task<Chunk>>> _inflight = new();
    private readonly SemaphoreSlim _prefetchGate;
    private readonly Timer _idleTimer;

    public ChunkReadCache(ICloudProvider provider, MemoryBudget budget, int chunkSize, int readAheadChunks,
        int maxParallel, TimeSpan idleExpiry)
    {
        _provider = provider;
        _budget = budget;
        ChunkSize = Math.Max(256 * 1024, chunkSize);
        ReadAheadChunks = Math.Max(0, readAheadChunks);
        IdleExpiry = idleExpiry;
        _prefetchGate = new SemaphoreSlim(Math.Max(1, maxParallel / 2));
        budget.Register(this);
        _idleTimer = new Timer(_ => EvictIdle(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public int ChunkSize { get; }
    public int ReadAheadChunks { get; set; }
    public TimeSpan IdleExpiry { get; set; }

    public long CachedBytes
    {
        get
        {
            lock (_lock) return _chunks.Values.Sum(c => (long)c.Data.Length);
        }
    }

    /// <summary>Reads [offset, offset+dest.Length) of a cloud file into <paramref name="dest"/>.</summary>
    public int Read(string fileId, string version, long fileSize, long offset, Span<byte> dest, ReadPattern? pattern, CancellationToken ct)
    {
        if (offset >= fileSize || dest.Length == 0) return 0;
        var want = (int)Math.Min(dest.Length, fileSize - offset);
        var done = 0;
        var firstIndex = offset / ChunkSize;
        var lastIndex = (offset + want - 1) / ChunkSize;

        // Fetch every chunk this request needs in parallel, then copy in order.
        var tasks = new Task<Chunk>[lastIndex - firstIndex + 1];
        for (var i = firstIndex; i <= lastIndex; i++)
            tasks[i - firstIndex] = GetChunkAsync(new Key(fileId, version, i), fileSize, ct);
        Task.WaitAll(tasks, ct);

        for (var i = firstIndex; i <= lastIndex && done < want; i++)
        {
            var chunk = tasks[i - firstIndex].Result;
            var chunkStart = i * (long)ChunkSize;
            var inChunk = (int)(offset + done - chunkStart);
            var n = Math.Min(chunk.Length - inChunk, want - done);
            if (n <= 0) break; // cloud object shorter than metadata said
            chunk.Data.AsSpan(inChunk, n).CopyTo(dest[done..]);
            done += n;
        }

        if (pattern is not null)
        {
            pattern.SequentialHits = offset == pattern.NextExpected ? pattern.SequentialHits + 1 : 0;
            pattern.NextExpected = offset + done;
            pattern.BytesRead += done;
            if (pattern.SequentialHits >= 2 && ReadAheadChunks > 0)
                Prefetch(fileId, version, fileSize, lastIndex + 1, ReadAheadChunks);
        }
        return done;
    }

    private void Prefetch(string fileId, string version, long fileSize, long fromIndex, int count)
    {
        var maxIndex = (fileSize - 1) / ChunkSize;
        for (var i = fromIndex; i <= Math.Min(maxIndex, fromIndex + count - 1); i++)
        {
            var key = new Key(fileId, version, i);
            lock (_lock)
                if (_chunks.ContainsKey(key)) continue;
            if (_inflight.ContainsKey(key)) continue;
            // Only prefetch when it fits in the budget without pushing out other data.
            if (_budget.Used + ChunkSize > _budget.Capacity * 0.8) return;
            if (!_prefetchGate.Wait(0)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await GetChunkAsync(key, fileSize, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Prefetch is opportunistic.
                }
                finally
                {
                    _prefetchGate.Release();
                }
            });
        }
    }

    private Task<Chunk> GetChunkAsync(Key key, long fileSize, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_chunks.TryGetValue(key, out var c))
            {
                c.LastUseTicks = Environment.TickCount64;
                return Task.FromResult(c);
            }
        }
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<Chunk>>(() => FetchAsync(k, fileSize)));
        return lazy.Value.WaitAsync(ct);
    }

    private async Task<Chunk> FetchAsync(Key key, long fileSize)
    {
        try
        {
            var start = key.Index * ChunkSize;
            var len = (int)Math.Min(ChunkSize, fileSize - start);
            var accounted = _budget.TryReserve(len, this);
            var buf = GC.AllocateUninitializedArray<byte>(len);
            int got;
            try
            {
                got = await _provider.ReadRangeAsync(key.FileId, start, buf, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                if (accounted) _budget.Release(len);
                throw;
            }
            var chunk = new Chunk { Data = buf, Length = got, LastUseTicks = Environment.TickCount64, Accounted = accounted };
            if (accounted)
            {
                lock (_lock) _chunks[key] = chunk;
            }
            // When the budget is full the chunk is used for this request only and then garbage-collected.
            return chunk;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public void InvalidateFile(string fileId)
    {
        lock (_lock)
        {
            foreach (var k in _chunks.Keys.Where(k => k.FileId == fileId).ToList())
                Drop(k);
        }
    }

    public void Clear()
    {
        lock (_lock)
            foreach (var k in _chunks.Keys.ToList()) Drop(k);
    }

    public long Reclaim(long bytes)
    {
        long freed = 0;
        lock (_lock)
        {
            foreach (var (k, _) in _chunks.OrderBy(kv => kv.Value.LastUseTicks).ToList())
            {
                if (freed >= bytes) break;
                freed += Drop(k);
            }
        }
        return freed;
    }

    private void EvictIdle()
    {
        var cutoff = Environment.TickCount64 - (long)IdleExpiry.TotalMilliseconds;
        lock (_lock)
            foreach (var (k, c) in _chunks.ToList())
                if (c.LastUseTicks < cutoff) Drop(k);
    }

    private long Drop(Key k)
    {
        if (!_chunks.Remove(k, out var c)) return 0;
        if (c.Accounted) _budget.Release(c.Data.Length);
        return c.Data.Length;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        Clear();
        _budget.Unregister(this);
    }
}
