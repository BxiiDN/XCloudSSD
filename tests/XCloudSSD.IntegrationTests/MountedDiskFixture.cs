using Fsp;
using Microsoft.Extensions.Logging.Abstractions;
using XCloudSSD.Core;
using XCloudSSD.Filesystem;
using XCloudSSD.Metadata;
using XCloudSSD.TestSupport;
using XCloudSSD.Transfer;
using XCloudSSD.VirtualDrive;

namespace XCloudSSD.IntegrationTests;

/// <summary>
/// A real WinFsp mount of <see cref="CloudFileSystem"/> backed by the in-memory test cloud.
/// Exercises the production filesystem code through the Windows I/O stack (System.IO → NTFS-like volume).
/// </summary>
public sealed class TestDisk : IDisposable
{
    public InMemoryCloudProvider Cloud { get; }
    public MemoryBudget Budget { get; }
    public CloudFileSystem Fs { get; private set; } = null!;
    public TransferTracker Transfers { get; } = new(null);
    public char Letter { get; }
    public string Root => $"{Letter}:\\";
    private FileSystemHost? _host;
    private ChunkReadCache? _cache;
    private readonly long _ramBytes;
    private readonly string _label;

    public TestDisk(char letter, string label, long ramBytes, InMemoryCloudProvider? cloud = null)
    {
        Letter = letter;
        _label = label;
        _ramBytes = ramBytes;
        Cloud = cloud ?? new InMemoryCloudProvider(quotaBytes: 1L << 40, baseUsage: 200L << 30);
        Budget = new MemoryBudget(ramBytes);
        Mount();
    }

    public void Mount()
    {
        var tree = new MetadataTree(Cloud, TimeSpan.FromSeconds(30));
        _cache = new ChunkReadCache(Cloud, Budget, 1 << 20, 4, 8, TimeSpan.FromSeconds(60));
        var options = new FsOptions
        {
            AccountId = "test-" + Letter,
            DriveLetter = Letter,
            VolumeLabel = _label,
            UploadChunkBytes = 4 << 20,
            StreamingThresholdBytes = 8L << 20,
            CommitRetryInterval = TimeSpan.FromSeconds(1),
            SyncCommitAttempts = 1,
            BufferWaitSeconds = 20,
            DeleteMode = DeleteMode.Permanent,
        };
        Fs = new CloudFileSystem(Cloud, tree, _cache, Budget, Transfers, new Metrics(), NullLogger.Instance, options,
            () => Cloud.GetQuotaAsync(default).GetAwaiter().GetResult());
        _host = new FileSystemHost(Fs);
        var status = _host.MountEx($"{Letter}:", 16, null, false, 0);
        if (status < 0) throw new InvalidOperationException($"WinFsp mount of {Letter}: failed: 0x{status:X8}");
        // Wait until Windows sees the volume.
        for (var i = 0; i < 50 && !Directory.Exists(Root); i++) Thread.Sleep(100);
    }

    public void Unmount()
    {
        Fs.FlushAll(TimeSpan.FromSeconds(30));
        _host?.Unmount();
        _host?.Dispose();
        _host = null;
        Fs.Dispose();
        _cache?.Dispose();
    }

    public void Dispose() => Unmount();

    public static char FreeLetter(params char[] exclude)
    {
        var used = Environment.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
        for (var c = 'Z'; c >= 'K'; c--)
            if (!used.Contains(c) && !exclude.Contains(c)) return c;
        throw new InvalidOperationException("No free drive letter.");
    }
}

public sealed class WinFspFact : FactAttribute
{
    public WinFspFact()
    {
        if (!WinFspInfo.Detect().Installed) Skip = "WinFsp is not installed on this machine.";
    }
}
