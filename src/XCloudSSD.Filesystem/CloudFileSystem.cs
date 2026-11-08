using System.Security.AccessControl;
using Fsp;
using Microsoft.Extensions.Logging;
using XCloudSSD.Cloud;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Metadata;
using XCloudSSD.Transfer;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace XCloudSSD.Filesystem;

public sealed class FsOptions
{
    public required string AccountId { get; init; }
    public char DriveLetter { get; set; }
    public string VolumeLabel { get; set; } = "XCloudSSD";
    public bool ReportNtfs { get; set; } = true;
    public DeleteMode DeleteMode { get; set; } = DeleteMode.Trash;
    public int UploadChunkBytes { get; set; } = 16 << 20;
    public long StreamingThresholdBytes { get; set; } = 64L << 20;
    /// <summary>How often failed commits held in RAM are retried.</summary>
    public TimeSpan CommitRetryInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Synchronous attempts at close before handing the commit to the background retry loop.</summary>
    public int SyncCommitAttempts { get; set; } = 3;
}

/// <summary>
/// WinFsp user-mode filesystem backed directly by one cloud account. Every operation goes to the
/// cloud provider: metadata through <see cref="MetadataTree"/>, reads through range requests into
/// a bounded RAM buffer, writes through <see cref="WriteSession"/> (RAM + streaming upload).
/// No file content is written to any physical disk.
/// </summary>
public sealed unsafe class CloudFileSystem : FileSystemBase, IDisposable
{
    private const uint FileDirectoryFile = 0x00000001;
    private const uint AttrReadOnly = 0x1, AttrHidden = 0x2, AttrSystem = 0x4, AttrDirectory = 0x10, AttrArchive = 0x20, AttrNormal = 0x80;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;

    private readonly ICloudProvider _provider;
    private readonly MetadataTree _tree;
    private readonly ChunkReadCache _cache;
    private readonly MemoryBudget _budget;
    private readonly TransferTracker _transfers;
    private readonly Metrics _metrics;
    private readonly ILogger _log;
    private readonly Func<QuotaInfo?> _quota;
    private readonly byte[] _securityDescriptor;
    private readonly Dictionary<MetaNode, NodeState> _states = new(ReferenceEqualityComparer.Instance);
    private readonly object _statesLock = new();
    private readonly List<NodeState> _pendingCommits = new();
    private readonly Timer _commitRetryTimer;
    private readonly CancellationTokenSource _shutdown = new();

    public CloudFileSystem(ICloudProvider provider, MetadataTree tree, ChunkReadCache cache, MemoryBudget budget,
        TransferTracker transfers, Metrics metrics, ILogger log, FsOptions options, Func<QuotaInfo?> quota)
    {
        _provider = provider;
        _tree = tree;
        _cache = cache;
        _budget = budget;
        _transfers = transfers;
        _metrics = metrics;
        _log = log;
        Options = options;
        _quota = quota;
        // Owner/group Administrators; full access for SYSTEM, Administrators and Authenticated Users.
        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;AU)");
        _securityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_securityDescriptor, 0);
        _commitRetryTimer = new Timer(_ => RetryPendingCommits(), null, options.CommitRetryInterval, options.CommitRetryInterval);
    }

    public FsOptions Options { get; }
    public int OpenHandleCount { get { lock (_statesLock) return _states.Values.Sum(s => s.OpenCount); } }
    public int PendingCommitCount { get { lock (_pendingCommits) return _pendingCommits.Count; } }

    private CancellationToken Ct => _shutdown.Token;

    // ================================================================= per-file state

    private sealed class NodeState
    {
        public required MetaNode Node;
        public int OpenCount;
        public int CleanupCount;
        public WriteSession? Session;
        public TransferHandle? Upload;
        public bool Deleted;
        public int CommitFailures;
        /// <summary>A write failed (e.g. RAM buffer full); the incomplete content is never committed.</summary>
        public string? WriteError;
    }

    private sealed class FileHandle
    {
        public required NodeState State;
        public readonly ReadPattern Pattern = new();
        public bool Wrote;
        public TransferHandle? Download;
    }

    private sealed class DirEnum
    {
        public required List<(string Name, FileInfo Info)> Items;
        public int Index;
    }

    private NodeState Acquire(MetaNode node)
    {
        lock (_statesLock)
        {
            if (!_states.TryGetValue(node, out var s))
            {
                s = new NodeState { Node = node };
                _states[node] = s;
            }
            s.OpenCount++;
            s.CleanupCount++;
            _metrics.HandleOpened();
            return s;
        }
    }

    private void ReleaseState(NodeState s)
    {
        lock (_statesLock)
        {
            s.OpenCount--;
            _metrics.HandleClosed();
            if (s.OpenCount <= 0 && (s.Session is null || !s.Session.Dirty))
            {
                if (s.Session is not null)
                {
                    s.Session.Dispose();
                    s.Session = null;
                    Interlocked.Decrement(ref s.Node.PendingWriters);
                }
                _states.Remove(s.Node);
            }
        }
    }

    private WriteSession EnsureSession(NodeState s)
    {
        lock (s)
        {
            if (s.Session is not null) return s.Session;
            var n = s.Node;
            var session = n.IsLocalOnly
                ? new WriteSession(_provider, _budget, _cache, WriteOptionsFor(), null, null, 0)
                : new WriteSession(_provider, _budget, _cache, WriteOptionsFor(), n.Id, Version(n), n.ContentSize, n.Size);
            Interlocked.Increment(ref n.PendingWriters);
            BindStreamTarget(s, session);
            s.Session = session;
            return session;
        }
    }

    private void BindStreamTarget(NodeState s, WriteSession session)
    {
        var n = s.Node;
        if (n.Parent is null) return;
        session.BindStreamTarget(n.IsLocalOnly
            ? new CommitTarget(null, n.Parent.Id, NameCodec.ToCloud(n.Name), n.StoredAttributes, n.Modified)
            : new CommitTarget(n.Id, null, null, n.StoredAttributes, n.Modified));
    }

    private WriteOptions WriteOptionsFor() => new(Options.UploadChunkBytes, Options.StreamingThresholdBytes);

    private static string Version(MetaNode n) => n.Md5 ?? n.Modified.UtcTicks.ToString();

    // ================================================================= FileInfo helpers

    private FileInfo InfoFor(MetaNode n, NodeState? s = null)
    {
        s ??= TryGetState(n);
        var size = n.IsFolder ? 0 : s?.Session?.LogicalSize ?? n.Size;
        return new FileInfo
        {
            FileAttributes = AttributesFor(n),
            ReparseTag = 0,
            FileSize = (ulong)size,
            AllocationSize = (ulong)((size + 4095) / 4096 * 4096),
            CreationTime = (ulong)n.Created.UtcDateTime.ToFileTimeUtc(),
            LastAccessTime = (ulong)(n.Accessed > n.Modified ? n.Accessed : n.Modified).UtcDateTime.ToFileTimeUtc(),
            LastWriteTime = (ulong)n.Modified.UtcDateTime.ToFileTimeUtc(),
            ChangeTime = (ulong)n.Modified.UtcDateTime.ToFileTimeUtc(),
            IndexNumber = (ulong)n.IndexNumber,
            HardLinks = 0,
            EaSize = 0,
        };
    }

    private static uint AttributesFor(MetaNode n)
    {
        var stored = n.StoredAttributes & ~(AttrDirectory | AttrNormal);
        if (n.IsFolder) return stored | AttrDirectory;
        return stored == 0 ? AttrArchive : stored;
    }

    private NodeState? TryGetState(MetaNode n)
    {
        lock (_statesLock) return _states.TryGetValue(n, out var s) ? s : null;
    }

    private static (string Parent, string Name) SplitParent(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var idx = trimmed.LastIndexOf('\\');
        return idx <= 0 ? ("\\", trimmed[(idx + 1)..]) : (trimmed[..idx], trimmed[(idx + 1)..]);
    }

    private MetaNode? Resolve(string path) => _tree.ResolveAsync(path, Ct).GetAwaiter().GetResult();

    private int Fail(Exception ex, string op, string? path)
    {
        var status = NtStatus.FromException(ex);
        var root = ex is AggregateException { InnerException: { } i } ? i : ex;
        if (status is NtStatus.ObjectNameNotFound) _log.LogDebug("{Op} {Path}: not found", op, path);
        else _log.LogWarning("{Op} {Path} failed: 0x{Status:X8} {Message}", op, path, status, root.Message);
        return status;
    }

    // ================================================================= volume

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = false; // commit tracking needs every Cleanup
        host.PassQueryDirectoryPattern = false;
        host.FlushAndPurgeOnCleanup = false;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)(Options.AccountId.GetHashCode() & 0x7FFFFFFF);
        host.FileSystemName = Options.ReportNtfs ? "NTFS" : "XCloudSSD";
        return NtStatus.Success;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        var q = _quota();
        if (q is not null)
        {
            // Unlimited quotas are shown as used + 1 PiB free.
            var total = q.TotalBytes ?? q.UsedBytes + (1L << 50);
            volumeInfo.TotalSize = (ulong)Math.Max(total, 0);
            volumeInfo.FreeSize = (ulong)Math.Max(0, total - q.UsedBytes);
        }
        volumeInfo.SetVolumeLabel(Options.VolumeLabel);
        return NtStatus.Success;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        Options.VolumeLabel = volumeLabel.Length > 32 ? volumeLabel[..32] : volumeLabel;
        return GetVolumeInfo(out volumeInfo);
    }

    public override int ExceptionHandler(Exception ex) => NtStatus.FromException(ex);

    // ================================================================= lookup / security

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        _metrics.FsOp();
        try
        {
            var node = Resolve(fileName);
            if (node is null)
            {
                var (parent, _) = SplitParent(fileName);
                return Resolve(parent) is { IsFolder: true } ? NtStatus.ObjectNameNotFound : NtStatus.ObjectPathNotFound;
            }
            fileAttributes = AttributesFor(node);
            if (securityDescriptor is not null) securityDescriptor = _securityDescriptor;
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "GetSecurityByName", fileName);
        }
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _securityDescriptor;
        return NtStatus.Success;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor)
        => NtStatus.Success; // ACLs are not stored in the cloud; the fixed descriptor applies to every file.

    // ================================================================= create / open

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc,
        out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        _metrics.FsOp();
        try
        {
            var (parentPath, name) = SplitParent(fileName);
            if (name.Length == 0 || name.Length > 255) return NtStatus.ObjectNameInvalid;
            var parent = Resolve(parentPath);
            if (parent is null || !parent.IsFolder) return NtStatus.ObjectPathNotFound;
            _tree.GetChildrenAsync(parent, Ct).GetAwaiter().GetResult();
            if (_tree.TryGetChild(parent, name) is not null) return NtStatus.ObjectNameCollision;

            MetaNode node;
            var attrs = fileAttributes & ~(AttrDirectory | AttrNormal);
            if ((createOptions & FileDirectoryFile) != 0)
            {
                var item = _provider.CreateFolderAsync(parent.Id, NameCodec.ToCloud(name), Ct).GetAwaiter().GetResult();
                node = _tree.AddChild(parent, item, name);
                if (attrs != 0)
                {
                    node.StoredAttributes = attrs;
                    _provider.UpdateMetadataAsync(node.Id, new MetadataUpdate { WindowsAttributes = attrs }, Ct).GetAwaiter().GetResult();
                }
                var ds = Acquire(node);
                fileNode = ds;
                fileDesc = new FileHandle { State = ds };
                fileInfo = InfoFor(node, ds);
                return NtStatus.Success;
            }

            // New files exist only as a RAM placeholder until their content is committed at close.
            node = _tree.AddLocalFile(parent, name, attrs);
            var s = Acquire(node);
            var session = EnsureSession(s);
            if (allocationSize > 0) { /* allocation hints do not reserve cloud space */ }
            _ = session;
            fileNode = s;
            fileDesc = new FileHandle { State = s };
            fileInfo = InfoFor(node, s);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "Create", fileName);
        }
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode,
        out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        _metrics.FsOp();
        try
        {
            var node = Resolve(fileName);
            if (node is null)
            {
                var (parent, _) = SplitParent(fileName);
                return Resolve(parent) is { IsFolder: true } ? NtStatus.ObjectNameNotFound : NtStatus.ObjectPathNotFound;
            }
            var s = Acquire(node);
            fileNode = s;
            fileDesc = new FileHandle { State = s };
            fileInfo = InfoFor(node, s);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "Open", fileName);
        }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes,
        ulong allocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        _metrics.FsOp();
        var s = (NodeState)fileNode;
        try
        {
            var attrs = fileAttributes & ~(AttrDirectory | AttrNormal);
            s.Node.StoredAttributes = replaceFileAttributes ? attrs : s.Node.StoredAttributes | attrs;
            s.Node.Modified = DateTimeOffset.UtcNow;
            var session = EnsureSession(s);
            session.SetSize(0);
            BindStreamTarget(s, session);
            ((FileHandle)fileDesc).Wrote = true;
            fileInfo = InfoFor(s.Node, s);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "Overwrite", s.Node.FullPath);
        }
    }

    // ================================================================= read / write

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        _metrics.FsOp();
        var s = (NodeState)fileNode;
        var h = (FileHandle)fileDesc;
        try
        {
            var n = s.Node;
            if (n.IsFolder) return NtStatus.InvalidDeviceRequest;
            var dest = new Span<byte>((void*)buffer, (int)length);
            var session = s.Session;
            var size = session?.LogicalSize ?? n.Size;
            if ((long)offset >= size) return NtStatus.EndOfFile;

            int got;
            if (session is not null)
            {
                got = session.Read((long)offset, dest, h.Pattern, Ct);
            }
            else
            {
                // Sparse files: bytes after the stored content (reserved space) read as zeros.
                var want = (int)Math.Min(dest.Length, size - (long)offset);
                var fromCloud = (int)Math.Clamp(n.ContentSize - (long)offset, 0, want);
                got = fromCloud > 0 ? _cache.Read(n.Id, Version(n), n.ContentSize, (long)offset, dest[..fromCloud], h.Pattern, Ct) : 0;
                if (got < want) dest[got..want].Clear();
                got = want;
            }
            bytesTransferred = (uint)got;
            _metrics.FsRead(got);
            n.Accessed = DateTimeOffset.UtcNow;
            TrackDownload(s, h);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "Read", s.Node.FullPath);
        }
    }

    /// <summary>Large streaming reads (e.g. copying a file off the disk) appear in the Transfer Manager.</summary>
    private void TrackDownload(NodeState s, FileHandle h)
    {
        if (h.Pattern.BytesRead < 32L << 20) return;
        if (h.Download is null)
        {
            h.Download = _transfers.Begin(Options.AccountId, Options.DriveLetter, s.Node.FullPath, TransferDirection.Download, s.Node.Size);
            h.Download.SetState(TransferState.Downloading);
        }
        h.Download.Progress(h.Pattern.BytesRead, s.Node.Size);
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        _metrics.FsOp();
        var s = (NodeState)fileNode;
        var h = (FileHandle)fileDesc;
        try
        {
            if (s.Node.IsFolder) return NtStatus.InvalidDeviceRequest;
            var session = EnsureSession(s);
            var size = session.LogicalSize;
            var off = writeToEndOfFile ? size : (long)offset;
            var len = (long)length;
            if (constrainedIo)
            {
                // Paging I/O must not extend the file.
                if (off >= size)
                {
                    fileInfo = InfoFor(s.Node, s);
                    return NtStatus.Success;
                }
                len = Math.Min(len, size - off);
            }
            var src = new ReadOnlySpan<byte>((void*)buffer, (int)len);
            EnsureUploadTransfer(s, session);
            session.Write(off, src, Ct);
            bytesTransferred = (uint)len;
            h.Wrote = true;
            s.Node.Modified = DateTimeOffset.UtcNow;
            _metrics.FsWrite(len);
            fileInfo = InfoFor(s.Node, s);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            if (ex is WriteSessionException)
            {
                s.WriteError = ex.Message;
                s.Upload?.SetState(TransferState.Failed, ex.Message);
            }
            return Fail(ex, "Write", s.Node.FullPath);
        }
    }

    private void EnsureUploadTransfer(NodeState s, WriteSession session)
    {
        if (s.Upload is not null && !s.Upload.IsFinished) return;
        lock (s)
        {
            if (s.Upload is not null && !s.Upload.IsFinished) return;
            var t = _transfers.Begin(Options.AccountId, Options.DriveLetter, s.Node.FullPath, TransferDirection.Upload, session.LogicalSize);
            s.Upload = t;
            session.Progress += (done, total) => t.Progress(done, total);
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        _metrics.FsOp();
        if (fileNode is null) return NtStatus.Success; // volume flush: each file commits on its own close/flush
        var s = (NodeState)fileNode;
        try
        {
            // FlushFileBuffers promises durability, so commit now and report failures to the caller.
            CommitNow(s, synchronousAttempts: 1, throwOnFailure: true);
            fileInfo = InfoFor(s.Node, s);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "Flush", s.Node.FullPath);
        }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        var s = (NodeState)fileNode;
        fileInfo = InfoFor(s.Node, s);
        return NtStatus.Success;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime,
        ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        fileInfo = default;
        _metrics.FsOp();
        var s = (NodeState)fileNode;
        var n = s.Node;
        try
        {
            var update = false;
            uint? newAttrs = null;
            DateTimeOffset? newModified = null;
            if (fileAttributes != InvalidFileAttributes)
            {
                var a = fileAttributes & ~(AttrDirectory | AttrNormal);
                if (a != n.StoredAttributes)
                {
                    n.StoredAttributes = a;
                    newAttrs = a;
                    update = true;
                }
            }
            if (creationTime != 0) n.Created = DateTimeOffset.FromFileTime((long)creationTime);
            if (lastAccessTime != 0) n.Accessed = DateTimeOffset.FromFileTime((long)lastAccessTime);
            if (lastWriteTime != 0)
            {
                var m = DateTimeOffset.FromFileTime((long)lastWriteTime);
                if (m != n.Modified)
                {
                    n.Modified = m;
                    newModified = m;
                    update = true;
                }
            }
            // Uncommitted files carry attributes/times in their commit; others are updated in the cloud now.
            if (update && !n.IsLocalOnly && s.Session is not { Dirty: true })
                _provider.UpdateMetadataAsync(n.Id, new MetadataUpdate { WindowsAttributes = newAttrs, Modified = newModified }, Ct)
                    .GetAwaiter().GetResult();
            if (s.Session is { } session) BindStreamTarget(s, session);
            fileInfo = InfoFor(n, s);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "SetBasicInfo", n.FullPath);
        }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        _metrics.FsOp();
        var s = (NodeState)fileNode;
        try
        {
            if (s.Node.IsFolder) return NtStatus.InvalidDeviceRequest;
            var current = s.Session?.LogicalSize ?? s.Node.Size;
            if (setAllocationSize)
            {
                // Shrinking allocation below EOF truncates; growing it is only a hint (cloud space is not reserved).
                if ((long)newSize < current) EnsureSession(s).SetSize((long)newSize);
            }
            else if ((long)newSize != current)
            {
                var session = EnsureSession(s);
                EnsureUploadTransfer(s, session);
                session.SetSize((long)newSize);
                s.Node.Modified = DateTimeOffset.UtcNow;
            }
            fileInfo = InfoFor(s.Node, s);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "SetFileSize", s.Node.FullPath);
        }
    }

    // ================================================================= delete / rename

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        _metrics.FsOp();
        var s = (NodeState)fileNode;
        try
        {
            if (s.Node.Parent is null) return NtStatus.AccessDenied;
            if (s.Node.IsFolder && _tree.GetChildrenAsync(s.Node, Ct).GetAwaiter().GetResult().Count > 0)
                return NtStatus.DirectoryNotEmpty;
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "CanDelete", fileName);
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        _metrics.FsOp();
        var s = (NodeState)fileNode;
        var n = s.Node;
        try
        {
            var (newParentPath, newName) = SplitParent(newFileName);
            var newParent = Resolve(newParentPath);
            if (newParent is null || !newParent.IsFolder) return NtStatus.ObjectPathNotFound;
            for (var p = newParent; p is not null; p = p.Parent)
                if (ReferenceEquals(p, n)) return NtStatus.InvalidParameter; // folder into itself

            _tree.GetChildrenAsync(newParent, Ct).GetAwaiter().GetResult();
            var existing = _tree.TryGetChild(newParent, newName);
            if (existing is not null && !ReferenceEquals(existing, n))
            {
                if (!replaceIfExists) return NtStatus.ObjectNameCollision;
                if (existing.IsFolder) return NtStatus.AccessDenied;
                if (TryGetState(existing) is { OpenCount: > 0 }) return NtStatus.AccessDenied;
                if (!existing.IsLocalOnly) _provider.DeleteAsync(existing.Id, Options.DeleteMode == DeleteMode.Permanent, Ct).GetAwaiter().GetResult();
                _tree.Remove(existing);
            }

            var oldParent = n.Parent!;
            if (!n.IsLocalOnly)
            {
                var parentChanged = !ReferenceEquals(oldParent, newParent);
                var update = new MetadataUpdate
                {
                    NewName = NameCodec.ToCloud(newName),
                    AddParentId = parentChanged ? newParent.Id : null,
                    RemoveParentId = parentChanged ? oldParent.Id : null,
                };
                _provider.UpdateMetadataAsync(n.Id, update, Ct).GetAwaiter().GetResult();
            }
            _tree.Move(n, newParent, newName);
            if (s.Session is { } session) BindStreamTarget(s, session);
            s.Upload?.SetPath(n.FullPath);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            return Fail(ex, "Rename", fileName);
        }
    }

    // ================================================================= directory listing

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        fileName = null!;
        fileInfo = default;
        var s = (NodeState)fileNode;
        if (context is not DirEnum e)
        {
            _metrics.FsOp();
            List<MetaNode> children;
            try
            {
                children = _tree.GetChildrenAsync(s.Node, Ct).GetAwaiter().GetResult().ToList();
            }
            catch (Exception ex)
            {
                Fail(ex, "ReadDirectory", s.Node.FullPath);
                return false;
            }
            var items = new List<(string, FileInfo)>(children.Count + 2);
            if (s.Node.Parent is not null)
            {
                items.Add((".", InfoFor(s.Node)));
                items.Add(("..", InfoFor(s.Node.Parent)));
            }
            foreach (var c in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                items.Add((c.Name, InfoFor(c)));
            e = new DirEnum { Items = items };
            if (marker is not null)
            {
                while (e.Index < items.Count && string.Compare(items[e.Index].Item1, marker, StringComparison.OrdinalIgnoreCase) <= 0)
                    e.Index++;
            }
            context = e;
        }

        if (e.Index >= e.Items.Count) return false;
        (fileName, fileInfo) = e.Items[e.Index++];
        return true;
    }

    // ================================================================= cleanup / close

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        _metrics.FsOp();
        var s = (NodeState)fileNode;
        var h = (FileHandle)fileDesc;
        int remaining;
        lock (s) remaining = --s.CleanupCount;
        try
        {
            if ((flags & CleanupDelete) != 0)
            {
                DeleteNode(s);
                return;
            }
            if ((flags & CleanupSetLastWriteTime) != 0 && h.Wrote) s.Node.Modified = DateTimeOffset.UtcNow;
            if (remaining <= 0 && s.WriteError is not null)
            {
                // Never upload a file whose writes failed: the cloud keeps the previous version (or nothing).
                _log.LogError("Discarding incomplete content of {Path}: {Error}", s.Node.FullPath, s.WriteError);
                s.Upload?.SetState(TransferState.Failed, s.WriteError);
                AbandonSession(s);
                s.WriteError = null;
                return;
            }
            // Commit when the last handle is closed. The close blocks until the cloud has the verified data.
            if (remaining <= 0 && s.Session is { Dirty: true })
                CommitNow(s, Options.SyncCommitAttempts, throwOnFailure: false);
        }
        catch (Exception ex)
        {
            Fail(ex, "Cleanup", fileName);
        }
        finally
        {
            if (h.Download is { } d)
            {
                d.Progress(h.Pattern.BytesRead, Math.Max(h.Pattern.BytesRead, s.Node.Size));
                d.SetState(TransferState.Completed);
                h.Download = null;
            }
        }
    }

    public override void Close(object fileNode, object fileDesc) => ReleaseState((NodeState)fileNode);

    private void DeleteNode(NodeState s)
    {
        var n = s.Node;
        lock (s)
        {
            if (s.Session is not null)
            {
                s.Session.Discard();
                s.Session = null;
                Interlocked.Decrement(ref n.PendingWriters);
            }
            s.Deleted = true;
            s.Upload?.SetState(TransferState.Completed);
        }
        lock (_pendingCommits) _pendingCommits.Remove(s);
        if (!n.IsLocalOnly)
            _provider.DeleteAsync(n.Id, Options.DeleteMode == DeleteMode.Permanent, Ct).GetAwaiter().GetResult();
        if (!n.IsFolder) _cache.InvalidateFile(n.Id);
        _tree.Remove(n);
    }

    // ================================================================= commit

    /// <summary>Uploads and verifies the node's pending content. Returns true when committed.</summary>
    private bool CommitNow(NodeState s, int synchronousAttempts, bool throwOnFailure)
    {
        var session = s.Session;
        if (session is null || !session.Dirty || s.Deleted) return true;
        var n = s.Node;
        var upload = s.Upload ?? _transfers.Begin(Options.AccountId, Options.DriveLetter, n.FullPath, TransferDirection.Upload, session.LogicalSize);
        s.Upload = upload;
        upload.SetState(TransferState.Processing);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var wasLocal = n.IsLocalOnly;
                var target = wasLocal
                    ? new CommitTarget(null, n.Parent!.Id, NameCodec.ToCloud(n.Name), n.StoredAttributes, n.Modified)
                    : new CommitTarget(n.Id, null, null, n.StoredAttributes, n.Modified);
                var item = session.Commit(target, Ct);
                if (item is not null)
                {
                    if (wasLocal) _tree.Rekey(n, item.Id);
                    ReconcileAfterCommit(n, item);
                    n.Size = item.Size;
                    n.ContentSize = item.ContentLength;
                    n.Md5 = item.Md5;
                }
                lock (s)
                {
                    if (!session.Dirty)
                    {
                        if (s.OpenCount <= 0 || s.CleanupCount <= 0)
                        {
                            session.Dispose();
                            if (ReferenceEquals(s.Session, session)) s.Session = null;
                            Interlocked.Decrement(ref n.PendingWriters);
                        }
                        else
                        {
                            BindStreamTarget(s, session);
                        }
                    }
                }
                s.CommitFailures = 0;
                lock (_pendingCommits) _pendingCommits.Remove(s);
                upload.SetPath(n.FullPath);
                upload.SetState(TransferState.Completed);
                _log.LogInformation("Committed {Path} ({Size} bytes, md5 {Md5})", n.FullPath, n.Size, n.Md5);
                return true;
            }
            catch (Exception ex)
            {
                var root = ex is AggregateException { InnerException: { } i } ? i : ex;
                var retryable = Retry.IsRetryable(root) || root is CloudException { Kind: CloudErrorKind.IntegrityMismatch };
                if (session.DataLost || !retryable)
                {
                    upload.SetState(TransferState.Failed, root.Message);
                    _log.LogError("Commit of {Path} failed permanently: {Message}", n.FullPath, root.Message);
                    if (session.DataLost) AbandonSession(s);
                    if (throwOnFailure) throw;
                    return false;
                }
                if (attempt < synchronousAttempts)
                {
                    upload.SetState(TransferState.Retrying, root.Message);
                    Thread.Sleep(Retry.Backoff(attempt, root));
                    continue;
                }
                s.CommitFailures++;
                var offline = root is CloudException { Kind: CloudErrorKind.Offline };
                upload.SetState(offline ? TransferState.Offline : TransferState.Retrying,
                    root.Message + " — kept in RAM and retried automatically.");
                lock (_pendingCommits)
                    if (!_pendingCommits.Contains(s)) _pendingCommits.Add(s);
                _log.LogWarning("Commit of {Path} deferred: {Message}", n.FullPath, root.Message);
                if (throwOnFailure) throw;
                return false;
            }
        }
    }

    /// <summary>If the file was renamed/moved while its upload streamed, apply the final name/parent now.</summary>
    private void ReconcileAfterCommit(MetaNode n, CloudItem item)
    {
        if (n.Parent is null) return;
        var cloudName = NameCodec.ToCloud(n.Name);
        var parentOk = item.Parents.Count == 0 || item.Parents.Contains(n.Parent.Id);
        if (item.Name == cloudName && parentOk) return;
        var update = new MetadataUpdate
        {
            NewName = item.Name == cloudName ? null : cloudName,
            AddParentId = parentOk ? null : n.Parent.Id,
            RemoveParentId = parentOk ? null : item.Parents.FirstOrDefault(),
        };
        _provider.UpdateMetadataAsync(item.Id, update, Ct).GetAwaiter().GetResult();
    }

    private void AbandonSession(NodeState s)
    {
        lock (s)
        {
            if (s.Session is not null)
            {
                s.Session.Discard();
                s.Session = null;
                Interlocked.Decrement(ref s.Node.PendingWriters);
            }
        }
        lock (_pendingCommits) _pendingCommits.Remove(s);
        if (s.Node.IsLocalOnly) _tree.Remove(s.Node);
    }

    private int _retrying;

    private void RetryPendingCommits()
    {
        if (Interlocked.Exchange(ref _retrying, 1) == 1) return;
        try
        {
            List<NodeState> pending;
            lock (_pendingCommits) pending = _pendingCommits.ToList();
            foreach (var s in pending)
            {
                if (_shutdown.IsCancellationRequested) return;
                if (s.CleanupCount > 0) continue; // still open: commits again when closed
                if (CommitNow(s, 1, throwOnFailure: false))
                    ReleaseIfUnused(s);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Background commit retry failed: {Message}", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _retrying, 0);
        }
    }

    private void ReleaseIfUnused(NodeState s)
    {
        lock (_statesLock)
            if (s.OpenCount <= 0 && s.Session is null) _states.Remove(s.Node);
    }

    /// <summary>Commits everything still dirty (used before unmount). Returns paths that could not be saved.</summary>
    public List<string> FlushAll(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var failed = new List<string>();
        List<NodeState> dirty;
        lock (_statesLock) dirty = _states.Values.Where(s => s.Session is { Dirty: true }).ToList();
        foreach (var s in dirty)
        {
            var ok = DateTime.UtcNow < deadline && CommitNow(s, 2, throwOnFailure: false);
            if (!ok) failed.Add(s.Node.FullPath);
        }
        return failed;
    }

    public void InvalidateMetadata() => _tree.InvalidateAll();

    public void Dispose()
    {
        _shutdown.Cancel();
        _commitRetryTimer.Dispose();
    }
}
