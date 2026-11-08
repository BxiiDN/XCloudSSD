using System.Security.Cryptography;
using XCloudSSD.Cloud;
using XCloudSSD.Core;

namespace XCloudSSD.Transfer;

public enum WriteFailure
{
    /// <summary>The RAM buffer is full and nothing could be uploaded or reclaimed.</summary>
    BufferExhausted,
    /// <summary>Write/truncate below data that was already streamed to the cloud and released from RAM.</summary>
    BelowStreamedData,
    /// <summary>Data needed to serve a read was streamed to the cloud but the upload is not committed yet.</summary>
    StreamedDataUnreadable,
}

public sealed class WriteSessionException : Exception
{
    public WriteFailure Failure { get; }
    public WriteSessionException(WriteFailure failure, string message) : base(message) => Failure = failure;
}

public sealed record WriteOptions(
    int UploadChunkBytes,
    long StreamingThresholdBytes,
    bool StreamingEnabled = true);

/// <summary>Where a commit goes: a new file (parent + name) or an existing file id.</summary>
public sealed record CommitTarget(string? ExistingFileId, string? ParentId, string? Name, uint Attributes, DateTimeOffset Modified);

/// <summary>
/// Uncommitted content of one open file, held only in RAM.
///
/// Logical file = base cloud object [0, BaseSize) + RAM pages (64 KiB) + zeros up to LogicalSize.
/// • Files without base content (new or truncated to 0) that are written in order are streamed into a
///   resumable upload once enough contiguous data exists, and the uploaded pages are released, so RAM
///   use stays bounded for files of any size.
/// • Random writes stay in RAM pages until commit; then the whole file is re-uploaded by streaming base
///   ranges from the cloud merged with the RAM pages (Google Drive cannot patch part of a file).
/// • Commit = upload completed + size and MD5 verified against what the cloud reports.
/// All public members are synchronous: the WinFsp dispatcher threads call them directly.
/// </summary>
public sealed class WriteSession : IDisposable
{
    public const int PageSize = 64 * 1024;
    private const string EmptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";

    private sealed class Page
    {
        public required byte[] Data;
        public int Filled; // contiguous bytes written from the page start (decides streamability)
    }

    private readonly ICloudProvider _provider;
    private readonly MemoryBudget _budget;
    private readonly ChunkReadCache _cache;
    private readonly WriteOptions _options;
    private readonly SortedDictionary<long, Page> _pages = new();
    /// <summary>Whole pages written with zeros beyond the stored content: kept sparse (no RAM, never uploaded unless inside data).</summary>
    private readonly HashSet<long> _zeroPages = new();
    /// <summary>End of the last non-zero byte range written (or of the stored content). Upload length = this.</summary>
    private long _dataEnd;
    private readonly object _lock = new();
    private IUploadSession? _stream;
    private IncrementalHash? _md5;
    private long _hashedUpTo;
    private long _frontier;
    private DateTime _pumpRetryAfter = DateTime.MinValue;
    private CommitTarget? _streamTarget;

    public WriteSession(ICloudProvider provider, MemoryBudget budget, ChunkReadCache cache, WriteOptions options,
        string? baseFileId, string? baseVersion, long baseSize, long? logicalSize = null)
    {
        _provider = provider;
        _budget = budget;
        _cache = cache;
        _options = options;
        BaseFileId = baseFileId;
        BaseVersion = baseVersion ?? "";
        BaseSize = baseFileId is null ? 0 : baseSize;
        LogicalSize = Math.Max(BaseSize, logicalSize ?? BaseSize);
        _dataEnd = BaseSize;
        // A brand-new file must be created in the cloud even if nothing is ever written to it.
        Dirty = baseFileId is null;
    }

    public string? BaseFileId { get; private set; }
    public string BaseVersion { get; private set; }
    public long BaseSize { get; private set; }
    public long LogicalSize { get; private set; }
    public bool Dirty { get; private set; }
    /// <summary>Set when streamed data was lost (upload failed permanently after RAM was released).</summary>
    public bool DataLost { get; private set; }
    public long StreamedBytes => Interlocked.Read(ref _frontier);
    public bool IsStreaming => _stream is not null;

    public long BufferedBytes
    {
        get { lock (_lock) return (long)_pages.Count * PageSize; }
    }

    /// <summary>Raised with (bytesSent, logicalSize) whenever data is uploaded.</summary>
    public event Action<long, long>? Progress;

    /// <summary>Provides the destination early so files without base content can stream while being written.</summary>
    public void BindStreamTarget(CommitTarget target)
    {
        lock (_lock) _streamTarget = target;
    }

    // ------------------------------------------------------------------ write / truncate

    public void Write(long offset, ReadOnlySpan<byte> data, CancellationToken ct)
    {
        lock (_lock)
        {
            if (data.Length == 0) return;
            if (offset < _frontier)
                throw new WriteSessionException(WriteFailure.BelowStreamedData,
                    $"Write at offset {offset} is below the {_frontier} bytes already streamed to the cloud; that data is no longer in RAM and there is no disk cache.");

            var pos = 0;
            while (pos < data.Length)
            {
                var abs = offset + pos;
                var pageIndex = abs / PageSize;
                var inPage = (int)(abs % PageSize);
                var n = Math.Min(PageSize - inPage, data.Length - pos);
                var segment = data.Slice(pos, n);
                // Space reservation (e.g. Steam "Reserving space" writing zeros): a whole zero page beyond the stored
                // content stays sparse — no RAM, nothing uploaded.
                if (inPage == 0 && n == PageSize && abs >= BaseSize && !_pages.ContainsKey(pageIndex) && !segment.ContainsAnyExcept((byte)0))
                {
                    _zeroPages.Add(pageIndex);
                    pos += n;
                    continue;
                }
                var page = GetOrCreatePage(pageIndex, ct);
                _zeroPages.Remove(pageIndex);
                segment.CopyTo(page.Data.AsSpan(inPage));
                if (inPage <= page.Filled) page.Filled = Math.Max(page.Filled, inPage + n);
                _dataEnd = Math.Max(_dataEnd, abs + n);
                pos += n;
            }
            LogicalSize = Math.Max(LogicalSize, offset + data.Length);
            Dirty = true;
            TryPumpStream(ct, force: false);
        }
    }

    public void SetSize(long newSize)
    {
        lock (_lock)
        {
            if (newSize == LogicalSize) return;
            if (newSize < _frontier)
                throw new WriteSessionException(WriteFailure.BelowStreamedData,
                    $"Cannot truncate to {newSize}: {_frontier} bytes were already streamed to the cloud.");
            if (newSize < LogicalSize)
            {
                var lastKeptPage = newSize == 0 ? -1 : (newSize - 1) / PageSize;
                foreach (var idx in _pages.Keys.Where(k => k > lastKeptPage).ToList()) DropPage(idx);
                _zeroPages.RemoveWhere(k => k > lastKeptPage || (long)k * PageSize + PageSize > newSize);
                _dataEnd = Math.Min(_dataEnd, newSize);
                if (newSize % PageSize != 0 && _pages.TryGetValue(newSize / PageSize, out var tail))
                {
                    var keep = (int)(newSize % PageSize);
                    Array.Clear(tail.Data, keep, PageSize - keep);
                    tail.Filled = Math.Min(tail.Filled, keep);
                }
                if (newSize < BaseSize) BaseSize = newSize;
            }
            LogicalSize = newSize;
            Dirty = true;
        }
    }

    private Page GetOrCreatePage(long index, CancellationToken ct)
    {
        if (_pages.TryGetValue(index, out var p)) return p;
        if (!_budget.TryReserve(PageSize))
        {
            // Make room by uploading streamable data, then try once more.
            TryPumpStream(ct, force: true);
            if (!_budget.TryReserve(PageSize))
                throw new WriteSessionException(WriteFailure.BufferExhausted,
                    "The RAM write buffer is full. XCloudSSD never spills file data to a physical disk; increase the RAM buffer in Settings or write the file sequentially.");
        }
        var page = new Page { Data = new byte[PageSize] };
        var pageStart = index * PageSize;
        if (pageStart < BaseSize && BaseFileId is not null)
        {
            var len = (int)Math.Min(PageSize, BaseSize - pageStart);
            try
            {
                var got = _cache.Read(BaseFileId, BaseVersion, BaseSize, pageStart, page.Data.AsSpan(0, len), null, ct);
                if (got < len) Array.Clear(page.Data, got, len - got);
            }
            catch
            {
                _budget.Release(PageSize);
                throw;
            }
            page.Filled = PageSize;
        }
        _pages[index] = page;
        return page;
    }

    private void DropPage(long index)
    {
        if (_pages.Remove(index)) _budget.Release(PageSize);
        _zeroPages.Remove(index);
    }

    /// <summary>Bytes that really have to be stored in the cloud; the rest of LogicalSize is a sparse zero tail.</summary>
    public long DataLength
    {
        get { lock (_lock) return Math.Min(LogicalSize, Math.Max(_dataEnd, Math.Max(_frontier, BaseSize))); }
    }

    // ------------------------------------------------------------------ streaming

    private long ContiguousCompleteFromFrontier()
    {
        long bytes = 0;
        var idx = _frontier / PageSize;
        while ((_pages.TryGetValue(idx, out var p) && p.Filled == PageSize) || _zeroPages.Contains(idx))
        {
            bytes += PageSize;
            idx++;
        }
        return bytes;
    }

    private void TryPumpStream(CancellationToken ct, bool force)
    {
        if (!_options.StreamingEnabled || BaseSize != 0 || _streamTarget is null || DataLost) return;
        if (DateTime.UtcNow < _pumpRetryAfter) return;
        var pendingTotal = (long)_pages.Count * PageSize;
        if (_stream is null && !force && pendingTotal < _options.StreamingThresholdBytes) return;

        var granularity = _stream?.Granularity ?? 256 * 1024;
        var chunk = Math.Max(granularity, _options.UploadChunkBytes / granularity * granularity);
        var ready = ContiguousCompleteFromFrontier() / granularity * granularity;
        // Never stream a trailing run of reserved (zero) space: it stays sparse.
        if (_provider.Capabilities.CustomProperties)
            ready = Math.Min(ready, Math.Max(0, _dataEnd - _frontier) / granularity * granularity);
        if (ready < (force ? granularity : chunk)) return;

        try
        {
            if (_stream is null)
            {
                _stream = _provider.BeginUploadAsync(ToUploadTarget(_streamTarget), ct).GetAwaiter().GetResult();
                _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                _hashedUpTo = 0;
            }
            if (!force) ready = ready / chunk * chunk;
            while (ready > 0)
            {
                var size = (int)Math.Min(ready, chunk);
                var buf = new byte[size];
                Compose(_frontier, buf, ct);
                HashRange(_frontier, buf);
                _stream.AppendAsync(buf, ct).GetAwaiter().GetResult();
                var newFrontier = _frontier + size;
                // These pages are now held by the cloud upload session; release their RAM.
                for (var idx = _frontier / PageSize; idx < newFrontier / PageSize; idx++) DropPage(idx);
                Interlocked.Exchange(ref _frontier, newFrontier);
                ready -= size;
                Progress?.Invoke(_frontier, LogicalSize);
            }
        }
        catch (Exception ex) when (Retry.IsRetryable(ex))
        {
            // Offline or throttled: keep data in RAM and try again a bit later.
            _pumpRetryAfter = DateTime.UtcNow.AddSeconds(5);
        }
    }

    private void HashRange(long at, ReadOnlySpan<byte> data)
    {
        if (_md5 is null || at + data.Length <= _hashedUpTo) return;
        var skip = (int)Math.Max(0, _hashedUpTo - at);
        _md5.AppendData(data[skip..]);
        _hashedUpTo = at + data.Length;
    }

    // ------------------------------------------------------------------ read

    public int Read(long offset, Span<byte> dest, ReadPattern? pattern, CancellationToken ct)
    {
        lock (_lock)
        {
            if (offset >= LogicalSize) return 0;
            var want = (int)Math.Min(dest.Length, LogicalSize - offset);
            Compose(offset, dest[..want], ct, pattern);
            return want;
        }
    }

    /// <summary>Fills <paramref name="dest"/> with logical bytes starting at <paramref name="offset"/>.</summary>
    private void Compose(long offset, Span<byte> dest, CancellationToken ct, ReadPattern? pattern = null)
    {
        var pos = 0;
        while (pos < dest.Length)
        {
            var abs = offset + pos;
            var pageIndex = abs / PageSize;
            var inPage = (int)(abs % PageSize);
            if (_pages.TryGetValue(pageIndex, out var page))
            {
                var n = Math.Min(PageSize - inPage, dest.Length - pos);
                page.Data.AsSpan(inPage, n).CopyTo(dest[pos..]);
                pos += n;
                continue;
            }
            if (abs < _frontier)
                throw new WriteSessionException(WriteFailure.StreamedDataUnreadable,
                    "This part of the file is inside an unfinished cloud upload and is no longer in RAM; it becomes readable after the file is closed.");

            // A run of bytes not covered by RAM pages: base content or zeros.
            var runEnd = dest.Length;
            foreach (var k in _pages.Keys)
            {
                if (k <= pageIndex) continue;
                runEnd = (int)Math.Min(runEnd, k * PageSize - offset);
                break;
            }
            var run = dest[pos..runEnd];
            if (abs < BaseSize && BaseFileId is not null)
            {
                var fromBase = (int)Math.Min(run.Length, BaseSize - abs);
                var got = _cache.Read(BaseFileId, BaseVersion, BaseSize, abs, run[..fromBase], pattern, ct);
                if (got < fromBase) run[got..fromBase].Clear();
                run[fromBase..].Clear();
            }
            else
            {
                run.Clear();
            }
            pos = runEnd;
        }
    }

    // ------------------------------------------------------------------ commit

    /// <summary>
    /// Uploads the logical file and verifies it. Returns the committed cloud item, or null when there was
    /// nothing to commit. On a retryable failure the data stays in RAM so the commit can be retried.
    /// </summary>
    public CloudItem? Commit(CommitTarget target, CancellationToken ct)
    {
        lock (_lock)
        {
            if (DataLost) throw LostException();
            if (!Dirty && target.ExistingFileId is not null) return null;
            try
            {
                // Only real data is uploaded; a reserved zero tail stays sparse (recorded as the logical size).
                var length = Math.Min(LogicalSize, Math.Max(_dataEnd, Math.Max(_frontier, BaseSize)));
                // Providers that cannot store a logical size (e.g. TeraBox) get the zero tail uploaded for real.
                if (!_provider.Capabilities.CustomProperties) length = LogicalSize;
                var item = Upload(target, length, ct, out var expectedMd5);
                if (item.ContentLength != length)
                    throw new CloudException(CloudErrorKind.IntegrityMismatch, $"Size check failed after upload: cloud {item.ContentLength} bytes, local {length} bytes.");
                if (item.Md5 is { } cloudMd5 && !string.Equals(cloudMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                    throw new CloudException(CloudErrorKind.IntegrityMismatch, $"MD5 check failed after upload: cloud {cloudMd5}, local {expectedMd5}.");
                if (item.Size != LogicalSize && _provider.Capabilities.CustomProperties)
                    item = _provider.UpdateMetadataAsync(item.Id, new MetadataUpdate { LogicalSize = LogicalSize }, ct).GetAwaiter().GetResult();

                // Committed and verified: release RAM and make the cloud object the new base.
                foreach (var idx in _pages.Keys.ToList()) DropPage(idx);
                if (BaseFileId is not null) _cache.InvalidateFile(BaseFileId);
                BaseFileId = item.Id;
                BaseVersion = item.Md5 ?? item.Modified.UtcTicks.ToString();
                BaseSize = length;
                _dataEnd = length;
                _zeroPages.Clear();
                Dirty = false;
                ResetStream();
                Progress?.Invoke(LogicalSize, LogicalSize);
                return item;
            }
            catch (Exception ex)
            {
                var released = _frontier > 0 && LostStreamedData();
                if (released)
                {
                    // The session can still be resumed after a network error; anything else loses the released data.
                    if (Retry.IsRetryable(ex)) throw;
                    DataLost = true;
                    ResetStream();
                    throw LostException(ex);
                }
                ResetStream();
                throw;
            }
        }
    }

    private CloudItem Upload(CommitTarget target, long length, CancellationToken ct, out string expectedMd5)
    {
        if (length == 0 && _stream is null)
        {
            expectedMd5 = EmptyMd5;
            if (target.ExistingFileId is null)
                return _provider.CreateEmptyFileAsync(target.ParentId!, target.Name!, target.Attributes, ct).GetAwaiter().GetResult();
            var s = _provider.BeginUploadAsync(ToUploadTarget(target), ct).GetAwaiter().GetResult();
            return s.CompleteAsync(ReadOnlyMemory<byte>.Empty, 0, ct).GetAwaiter().GetResult();
        }

        if (_stream is null)
        {
            _stream = _provider.BeginUploadAsync(ToUploadTarget(target), ct).GetAwaiter().GetResult();
            _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            _hashedUpTo = 0;
        }
        var stream = _stream;
        var granularity = stream.Granularity;
        var chunk = Math.Max(granularity, _options.UploadChunkBytes / granularity * granularity);
        // Resume exactly where the server is (it may have persisted part of a previous final chunk).
        var sent = Math.Max(_frontier, stream.Committed);
        var buf = new byte[(int)Math.Max(1, Math.Min(chunk, length - sent))];
        while (true)
        {
            var remaining = length - sent;
            var size = (int)Math.Min(chunk, remaining);
            var data = buf.AsMemory(0, size);
            Compose(sent, data.Span, ct);
            HashRange(sent, data.Span);
            if (remaining <= chunk)
            {
                var item = stream.CompleteAsync(data, length, ct).GetAwaiter().GetResult();
                expectedMd5 = Convert.ToHexStringLower(_md5!.GetCurrentHash());
                return item;
            }
            stream.AppendAsync(data, ct).GetAwaiter().GetResult();
            sent += size;
            Progress?.Invoke(sent, length);
        }
    }

    private static CloudException LostException(Exception? inner = null) => new(CloudErrorKind.Conflict,
        "The upload failed after part of the file had already been streamed and released from RAM. The file was not saved; copy it again.",
        null, inner);

    private bool LostStreamedData()
    {
        for (var idx = 0L; idx < _frontier / PageSize; idx++)
            if (!_pages.ContainsKey(idx)) return true;
        return false;
    }

    private void ResetStream()
    {
        if (_stream is not null) _ = _stream.AbortAsync();
        _stream = null;
        _md5?.Dispose();
        _md5 = null;
        _hashedUpTo = 0;
        Interlocked.Exchange(ref _frontier, 0);
    }

    private static UploadTarget ToUploadTarget(CommitTarget t) =>
        t.ExistingFileId is { } id
            ? UploadTarget.Replace(id, t.Attributes, t.Modified)
            : UploadTarget.NewFile(t.ParentId!, t.Name!, t.Attributes, t.Modified);

    /// <summary>Drops all uncommitted data (e.g. the file was deleted before it was closed).</summary>
    public void Discard()
    {
        lock (_lock)
        {
            foreach (var idx in _pages.Keys.ToList()) DropPage(idx);
            ResetStream();
            Dirty = false;
        }
    }

    public void Dispose() => Discard();
}
