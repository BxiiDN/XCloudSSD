using System.Security.Cryptography;
using XCloudSSD.Core;

namespace XCloudSSD.TestSupport;

/// <summary>
/// TEST DOUBLE ONLY — never shipped. An in-memory cloud with the same contract as a real provider
/// (immutable objects, ordered resumable uploads with 256 KiB granularity, quotas, change feed),
/// plus switches to simulate offline periods and transient failures.
/// </summary>
public sealed class InMemoryCloudProvider : ICloudProvider
{
    private sealed class Obj
    {
        public required string Id;
        public required string Name;
        public bool IsFolder;
        public string? Parent;
        public byte[] Content = Array.Empty<byte>();
        public long Logical;
        public uint? Attrs;
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
        public DateTimeOffset Modified = DateTimeOffset.UtcNow;
        public bool Trashed;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Obj> _objects = new();
    private readonly List<ChangedItem> _changes = new();
    private int _nextId;
    private int _activeReads;

    public InMemoryCloudProvider(long? quotaBytes = 1L << 40, long baseUsage = 0)
    {
        QuotaBytes = quotaBytes;
        BaseUsage = baseUsage;
        _objects["root-id"] = new Obj { Id = "root-id", Name = "", IsFolder = true };
    }

    public long? QuotaBytes { get; set; }
    public long BaseUsage { get; set; }
    public volatile bool Offline;
    /// <summary>Number of upcoming chunk uploads that fail with a transient error.</summary>
    public int FailNextChunks;
    public int ReadRangeCalls;
    public long BytesRead;
    public int MaxConcurrentReads;
    public int UploadSessionsStarted;
    public int CompletedUploads;

    public ProviderKind Kind => ProviderKind.GoogleDrive;
    public string RootId => "root-id";

    public ProviderCapabilities Capabilities { get; } = new(true, true, true, true, true, true, true, true, true, "In-memory test provider");

    public Task InitializeAsync(CancellationToken ct) => Check();

    private Task Check()
    {
        if (Offline) throw new CloudException(CloudErrorKind.Offline, "Simulated offline.");
        return Task.CompletedTask;
    }

    private CloudItem ToItem(Obj o) => new()
    {
        Id = o.Id,
        Name = o.Name,
        IsFolder = o.IsFolder,
        Size = Math.Max(o.Content.LongLength, o.Logical),
        StoredSize = o.Content.LongLength,
        Created = o.Created,
        Modified = o.Modified,
        Md5 = o.IsFolder ? null : Convert.ToHexStringLower(MD5.HashData(o.Content)),
        WindowsAttributes = o.Attrs,
        Parents = o.Parent is null ? Array.Empty<string>() : new[] { o.Parent },
    };

    private void Record(Obj o, bool removed = false) => _changes.Add(new ChangedItem(o.Id, removed || o.Trashed, removed || o.Trashed ? null : ToItem(o)));

    public async Task<AccountIdentity> GetIdentityAsync(CancellationToken ct)
    {
        await Check();
        return new AccountIdentity("Test User", "test@example.com");
    }

    public async Task<QuotaInfo> GetQuotaAsync(CancellationToken ct)
    {
        await Check();
        lock (_lock) return new QuotaInfo(QuotaBytes, BaseUsage + _objects.Values.Where(o => !o.Trashed).Sum(o => o.Content.LongLength));
    }

    public async Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string folderId, CancellationToken ct)
    {
        await Check();
        lock (_lock) return _objects.Values.Where(o => o.Parent == folderId && !o.Trashed).Select(ToItem).ToList();
    }

    public async Task<CloudItem?> GetItemAsync(string id, CancellationToken ct)
    {
        await Check();
        lock (_lock) return _objects.TryGetValue(id, out var o) && !o.Trashed ? ToItem(o) : null;
    }

    public async Task<int> ReadRangeAsync(string fileId, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        await Check();
        var now = Interlocked.Increment(ref _activeReads);
        try
        {
            InterlockedMax(ref MaxConcurrentReads, now);
            Interlocked.Increment(ref ReadRangeCalls);
            byte[] content;
            lock (_lock)
            {
                if (!_objects.TryGetValue(fileId, out var o) || o.Trashed) throw new CloudException(CloudErrorKind.NotFound, "No such file.");
                content = o.Content;
            }
            await Task.Yield();
            if (offset >= content.Length) return 0;
            var n = (int)Math.Min(buffer.Length, content.Length - offset);
            content.AsMemory((int)offset, n).CopyTo(buffer);
            Interlocked.Add(ref BytesRead, n);
            return n;
        }
        finally
        {
            Interlocked.Decrement(ref _activeReads);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while ((cur = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, cur) != cur)
        {
        }
    }

    private string NewId() => "obj-" + Interlocked.Increment(ref _nextId);

    public async Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        await Check();
        lock (_lock)
        {
            var o = new Obj { Id = NewId(), Name = name, IsFolder = true, Parent = parentId };
            _objects[o.Id] = o;
            Record(o);
            return ToItem(o);
        }
    }

    public async Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, uint? windowsAttributes, CancellationToken ct)
    {
        await Check();
        lock (_lock)
        {
            var o = new Obj { Id = NewId(), Name = name, Parent = parentId, Attrs = windowsAttributes };
            _objects[o.Id] = o;
            Record(o);
            return ToItem(o);
        }
    }

    public async Task<IUploadSession> BeginUploadAsync(UploadTarget target, CancellationToken ct)
    {
        await Check();
        Interlocked.Increment(ref UploadSessionsStarted);
        return new Session(this, target);
    }

    public async Task<CloudItem> UpdateMetadataAsync(string id, MetadataUpdate u, CancellationToken ct)
    {
        await Check();
        lock (_lock)
        {
            if (!_objects.TryGetValue(id, out var o) || o.Trashed) throw new CloudException(CloudErrorKind.NotFound, "No such object.");
            if (u.NewName is not null) o.Name = u.NewName;
            if (u.AddParentId is not null) o.Parent = u.AddParentId;
            if (u.WindowsAttributes is { } a) o.Attrs = a;
            if (u.Modified is { } m) o.Modified = m;
            if (u.LogicalSize is { } l) o.Logical = l;
            Record(o);
            return ToItem(o);
        }
    }

    public async Task DeleteAsync(string id, bool permanent, CancellationToken ct)
    {
        await Check();
        lock (_lock)
        {
            if (!_objects.TryGetValue(id, out var o)) return;
            if (permanent) _objects.Remove(id);
            else o.Trashed = true;
            Record(o, removed: true);
        }
    }

    public async Task<string> GetChangeTokenAsync(CancellationToken ct)
    {
        await Check();
        lock (_lock) return _changes.Count.ToString();
    }

    public async Task<ChangeSet> GetChangesAsync(string token, CancellationToken ct)
    {
        await Check();
        lock (_lock)
        {
            var from = int.Parse(token);
            return new ChangeSet(_changes.Count.ToString(), _changes.Skip(from).ToList());
        }
    }

    public async Task<IReadOnlyList<CloudItem>> SearchAsync(string text, int max, CancellationToken ct)
    {
        await Check();
        lock (_lock)
            return _objects.Values.Where(o => !o.Trashed && o.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(max).Select(ToItem).ToList();
    }

    // ------------------------------------------------------------------ test helpers

    /// <summary>Content of the object at a "/a/b.txt" path, or null.</summary>
    public byte[]? ContentAt(string path)
    {
        lock (_lock)
        {
            var cur = "root-id";
            foreach (var part in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = _objects.Values.FirstOrDefault(o => o.Parent == cur && !o.Trashed && o.Name == part);
                if (next is null) return null;
                cur = next.Id;
            }
            return _objects[cur].Content;
        }
    }

    public bool Exists(string path) => ContentAt(path) is not null;

    /// <summary>Logical (Windows-visible) size recorded for the object at a path.</summary>
    public long LogicalSizeAt(string path)
    {
        lock (_lock)
        {
            var cur = "root-id";
            foreach (var part in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
                cur = _objects.Values.First(o => o.Parent == cur && !o.Trashed && o.Name == part).Id;
            return Math.Max(_objects[cur].Content.LongLength, _objects[cur].Logical);
        }
    }

    public long BytesUploaded => Interlocked.Read(ref _bytesUploaded);
    private long _bytesUploaded;

    public CloudItem Seed(string parentId, string name, byte[] content)
    {
        lock (_lock)
        {
            var o = new Obj { Id = NewId(), Name = name, Parent = parentId, Content = content };
            _objects[o.Id] = o;
            Record(o);
            return ToItem(o);
        }
    }

    public int ObjectCount
    {
        get { lock (_lock) return _objects.Values.Count(o => !o.Trashed) - 1; }
    }

    public void Dispose()
    {
    }

    private sealed class Session(InMemoryCloudProvider owner, UploadTarget target) : IUploadSession
    {
        private readonly MemoryStream _data = new();
        private bool _done;

        public long Committed => _data.Length;
        public int Granularity => 256 * 1024;

        private void Fault()
        {
            if (owner.Offline) throw new CloudException(CloudErrorKind.Offline, "Simulated offline.");
            if (Interlocked.Decrement(ref owner.FailNextChunks) >= 0)
                throw new CloudException(CloudErrorKind.Transient, "Simulated transient upload failure.");
            Interlocked.Exchange(ref owner.FailNextChunks, Math.Max(0, owner.FailNextChunks));
        }

        public Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (_done) throw new InvalidOperationException("Session completed.");
            if (data.Length % Granularity != 0) throw new ArgumentException("Chunk not aligned to 256 KiB.");
            Fault();
            _data.Write(data.Span);
            Interlocked.Add(ref owner._bytesUploaded, data.Length);
            return Task.CompletedTask;
        }

        public Task<CloudItem> CompleteAsync(ReadOnlyMemory<byte> finalData, long totalSize, CancellationToken ct)
        {
            if (_done) throw new InvalidOperationException("Session completed.");
            if (_data.Length + finalData.Length != totalSize) throw new ArgumentException("Size mismatch.");
            Fault();
            _data.Write(finalData.Span);
            Interlocked.Add(ref owner._bytesUploaded, finalData.Length);
            _done = true;
            lock (owner._lock)
            {
                Obj o;
                if (target.ExistingFileId is { } id)
                {
                    o = owner._objects.TryGetValue(id, out var existing) ? existing : throw new CloudException(CloudErrorKind.NotFound, "No such file.");
                }
                else
                {
                    o = new Obj { Id = owner.NewId(), Name = target.Name!, Parent = target.ParentId };
                    owner._objects[o.Id] = o;
                }
                o.Content = _data.ToArray();
                if (target.WindowsAttributes is { } a) o.Attrs = a;
                o.Modified = target.Modified ?? DateTimeOffset.UtcNow;
                owner.Record(o);
                Interlocked.Increment(ref owner.CompletedUploads);
                return Task.FromResult(owner.ToItem(o));
            }
        }

        public Task AbortAsync()
        {
            _done = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
