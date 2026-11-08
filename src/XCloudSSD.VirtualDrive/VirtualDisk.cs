using System.Globalization;
using System.Text.Json;
using Fsp;
using Microsoft.Extensions.Logging;
using XCloudSSD.Cloud;
using XCloudSSD.Core;
using XCloudSSD.Filesystem;
using XCloudSSD.Metadata;
using XCloudSSD.Transfer;

namespace XCloudSSD.VirtualDrive;

/// <summary>
/// One account mounted as one drive letter: its own provider, credentials, metadata tree, read buffer,
/// filesystem instance, transfer state and connection status. Nothing is shared with other disks
/// except the global RAM budget and bandwidth limits.
/// </summary>
public sealed class VirtualDisk : IDisposable
{
    private readonly ILogger _log;
    private readonly MemoryBudget _budget;
    private readonly TransferTracker _transfers;
    private readonly AppDatabase? _db;
    private readonly object _lock = new();
    private CancellationTokenSource? _loopCts;
    private FileSystemHost? _host;
    private CloudFileSystem? _fs;
    private ChunkReadCache? _cache;
    private MetadataTree? _tree;
    private string? _changeToken;

    public VirtualDisk(AccountRecord account, ICloudProvider provider, MemoryBudget budget, TransferTracker transfers,
        AppDatabase? db, ILogger log)
    {
        Account = account;
        Provider = provider;
        _budget = budget;
        _transfers = transfers;
        _db = db;
        _log = log;
        LoadCachedQuota();
    }

    public AccountRecord Account { get; }
    public ICloudProvider Provider { get; }
    public Metrics Metrics { get; } = new();
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StateMessage { get; private set; } = "";
    public QuotaInfo? Quota { get; private set; }
    public DateTimeOffset? QuotaUpdated { get; private set; }
    public bool Mounted => _host is not null;
    public int OpenHandles => _fs?.OpenHandleCount ?? 0;
    public MetadataTree? Tree => _tree;
    public AppSettings Settings { get; private set; } = new();

    private void SetState(ConnectionState s, string message = "")
    {
        if (State != s) _log.LogInformation("{Letter}: state {Old} -> {New} {Message}", Account.DriveLetter, State, s, message);
        State = s;
        StateMessage = message;
    }

    /// <summary>Connects to the provider and mounts the drive letter. Works offline with cached ids.</summary>
    public async Task StartAsync(AppSettings settings, CancellationToken ct)
    {
        Settings = settings;
        if (!Provider.Capabilities.CanMount)
        {
            SetState(ConnectionState.Unavailable, Provider.Capabilities.Notes);
            return;
        }
        if (Mounted) return;
        SetState(ConnectionState.Connecting);

        var online = false;
        string rootId;
        try
        {
            await Provider.InitializeAsync(ct).ConfigureAwait(false);
            rootId = Provider.RootId;
            _db?.SetValue($"root:{Account.Id}", rootId);
            await RefreshQuotaAsync(ct).ConfigureAwait(false);
            _changeToken = await Provider.GetChangeTokenAsync(ct).ConfigureAwait(false);
            online = true;
        }
        catch (CloudException ex) when (ex.Kind == CloudErrorKind.Unauthorized)
        {
            SetState(ConnectionState.AuthRequired, ex.Message);
            if (!settings.KeepMounted) return;
            rootId = _db?.GetValue($"root:{Account.Id}") ?? throw new CloudException(ex.Kind, ex.Message, ex.HttpStatus, ex);
        }
        catch (CloudException ex) when (ex.IsRetryable)
        {
            // Offline at start: keep the drive registered using the last known root id.
            rootId = _db?.GetValue($"root:{Account.Id}")
                     ?? throw new CloudException(ex.Kind, "Cannot reach the cloud and no cached state exists yet: " + ex.Message, ex.HttpStatus, ex);
            SetState(ConnectionState.Offline, ex.Message);
            if (!settings.KeepMounted) return;
        }

        if (Provider is IRootOverride ro) ro.UseRootId(rootId);
        try
        {
            Mount(settings);
        }
        catch (Exception ex)
        {
            // Never leave the disk stuck in "Connecting": show the real reason.
            SetState(ConnectionState.Error, ex.Message);
            _log.LogError(ex, "Mount of {Letter}: failed", Account.DriveLetter);
            throw;
        }
        if (online) SetState(ConnectionState.Online);
        StartLoop();
    }

    private void Mount(AppSettings settings)
    {
        lock (_lock)
        {
            if (_host is not null) return;
            _tree = new MetadataTree(Provider, TimeSpan.FromSeconds(Math.Max(5, settings.MetadataCacheSeconds)));
            _cache = new ChunkReadCache(Provider, _budget, settings.ReadChunkKB * 1024, settings.ReadAheadChunks,
                settings.MaxParallelRequestsPerDisk, TimeSpan.FromSeconds(60));
            var options = new FsOptions
            {
                AccountId = Account.Id,
                DriveLetter = Account.DriveLetter,
                VolumeLabel = Account.Label,
                ReportNtfs = settings.ReportNtfs,
                DeleteMode = settings.DeleteMode,
                UploadChunkBytes = Math.Max(1, settings.UploadChunkMB) << 20,
            };
            _fs = new CloudFileSystem(Provider, _tree, _cache, _budget, _transfers, Metrics, _log, options, () => Quota);
            var host = new FileSystemHost(_fs);
            var mountPoint = $"{char.ToUpperInvariant(Account.DriveLetter)}:";
            int status;
            try
            {
                // Extra dispatcher threads: a write waiting for RAM must not stall reads/listings on the same drive.
                status = host.MountEx(mountPoint, 32, null, false, 0);
            }
            catch (TypeInitializationException ex)
            {
                _fs.Dispose();
                _cache.Dispose();
                _fs = null;
                _cache = null;
                throw new InvalidOperationException(
                    "The WinFsp .NET layer could not start: " + (ex.InnerException?.Message ?? ex.Message) +
                    " Reinstall XCloudSSD (it installs WinFsp 2.1 or newer).", ex);
            }
            if (status < 0)
            {
                host.Dispose();
                _fs.Dispose();
                _cache.Dispose();
                _fs = null;
                _cache = null;
                throw new InvalidOperationException(
                    $"WinFsp could not mount {mountPoint} (NTSTATUS 0x{status:X8}). Is the drive letter in use and is WinFsp installed?");
            }
            _host = host;
            _log.LogInformation("Mounted {Mount} as '{Label}'", mountPoint, Account.Label);
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        if (_fs is { } fs)
        {
            fs.Options.ReportNtfs = settings.ReportNtfs;
            fs.Options.DeleteMode = settings.DeleteMode;
            fs.Options.UploadChunkBytes = Math.Max(1, settings.UploadChunkMB) << 20;
        }
        if (_cache is { } c) c.ReadAheadChunks = settings.ReadAheadChunks;
        if (_tree is { } t) t.ChildrenTtl = TimeSpan.FromSeconds(Math.Max(5, settings.MetadataCacheSeconds));
    }

    public void SetLabel(string label)
    {
        Account.Label = label;
        if (_fs is { } fs) fs.Options.VolumeLabel = label;
    }

    /// <summary>Commits pending writes (up to the timeout) and unmounts.</summary>
    public List<string> Stop(TimeSpan flushTimeout)
    {
        _loopCts?.Cancel();
        _loopCts = null;
        List<string> unsaved = new();
        lock (_lock)
        {
            if (_fs is not null) unsaved = _fs.FlushAll(flushTimeout);
            _host?.Unmount();
            _host?.Dispose();
            _host = null;
            _fs?.Dispose();
            _fs = null;
            _cache?.Dispose();
            _cache = null;
            _tree = null;
        }
        if (unsaved.Count > 0) _log.LogError("Unmounted {Letter}: with {Count} unsaved file(s): {Files}", Account.DriveLetter, unsaved.Count, string.Join(", ", unsaved));
        SetState(ConnectionState.Disconnected);
        return unsaved;
    }

    public int PendingCommits => _fs?.PendingCommitCount ?? 0;

    // ------------------------------------------------------------------ background: quota, change feed, reconnect

    private void StartLoop()
    {
        _loopCts?.Cancel();
        var cts = _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var lastQuota = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, Settings.ChangePollSeconds)), ct).ConfigureAwait(false);
                if (State is ConnectionState.Offline or ConnectionState.AuthRequired or ConnectionState.Error)
                {
                    if (!Settings.AutoReconnect) continue;
                    await ReconnectProbeAsync(ct).ConfigureAwait(false);
                    continue;
                }
                await PollChangesAsync(ct).ConfigureAwait(false);
                if ((DateTime.UtcNow - lastQuota).TotalSeconds >= 60)
                {
                    await RefreshQuotaAsync(ct).ConfigureAwait(false);
                    lastQuota = DateTime.UtcNow;
                }
                if (State != ConnectionState.Online) SetState(ConnectionState.Online);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (CloudException ex) when (ex.Kind == CloudErrorKind.Unauthorized)
            {
                SetState(ConnectionState.AuthRequired, ex.Message);
            }
            catch (Exception ex) when (Retry.IsRetryable(ex))
            {
                SetState(ConnectionState.Offline, ex.Message);
            }
            catch (Exception ex)
            {
                SetState(ConnectionState.Error, ex.Message);
            }
        }
    }

    private async Task ReconnectProbeAsync(CancellationToken ct)
    {
        try
        {
            await RefreshQuotaAsync(ct).ConfigureAwait(false);
            _changeToken ??= await Provider.GetChangeTokenAsync(ct).ConfigureAwait(false);
            _tree?.InvalidateAll();
            SetState(ConnectionState.Online, "Reconnected");
        }
        catch (CloudException ex) when (ex.Kind == CloudErrorKind.Unauthorized)
        {
            SetState(ConnectionState.AuthRequired, ex.Message);
        }
        catch (Exception ex) when (Retry.IsRetryable(ex))
        {
            SetState(ConnectionState.Offline, ex.Message);
        }
    }

    private async Task PollChangesAsync(CancellationToken ct)
    {
        if (_tree is null) return;
        if (_changeToken is null)
        {
            _changeToken = await Provider.GetChangeTokenAsync(ct).ConfigureAwait(false);
            return;
        }
        try
        {
            var changes = await Provider.GetChangesAsync(_changeToken, ct).ConfigureAwait(false);
            if (changes.Changes.Count > 0)
            {
                _tree.ApplyChanges(changes);
                foreach (var c in changes.Changes) _cache?.InvalidateFile(c.Id);
            }
            _changeToken = changes.NextToken;
        }
        catch (CloudException ex) when (ex.Kind is CloudErrorKind.NotFound or CloudErrorKind.Unknown)
        {
            // Token expired: re-list lazily.
            _tree.InvalidateAll();
            _changeToken = await Provider.GetChangeTokenAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task RefreshQuotaAsync(CancellationToken ct)
    {
        var q = await Provider.GetQuotaAsync(ct).ConfigureAwait(false);
        Quota = q;
        QuotaUpdated = DateTimeOffset.UtcNow;
        _db?.SetValue($"quota:{Account.Id}", JsonSerializer.Serialize(new[] { q.TotalBytes ?? -1, q.UsedBytes }));
    }

    private void LoadCachedQuota()
    {
        try
        {
            if (_db?.GetValue($"quota:{Account.Id}") is { } json && JsonSerializer.Deserialize<long[]>(json) is [var total, var used])
                Quota = new QuotaInfo(total < 0 ? null : total, used);
        }
        catch
        {
            // Ignore a corrupt cache entry.
        }
    }

    public async Task<List<(CloudItem Item, string Path)>> SearchAsync(string text, int max, CancellationToken ct)
    {
        var hits = await Provider.SearchAsync(text, max, ct).ConfigureAwait(false);
        var names = new Dictionary<string, (string Name, string? Parent)>();
        var result = new List<(CloudItem, string)>();
        foreach (var h in hits.Where(h => !h.IsVirtualDocument).Take(max))
        {
            var path = _tree?.TryGetPath(h.Id) ?? await BuildPathAsync(h, names, ct).ConfigureAwait(false);
            if (path is not null) result.Add((h, $"{Account.DriveLetter}:{path}"));
        }
        return result;
    }

    private async Task<string?> BuildPathAsync(CloudItem item, Dictionary<string, (string Name, string? Parent)> names, CancellationToken ct)
    {
        var parts = new List<string> { NameCodec.ToWindows(item.Name) };
        var parent = item.Parents.FirstOrDefault();
        for (var depth = 0; parent is not null && parent != Provider.RootId && depth < 32; depth++)
        {
            if (!names.TryGetValue(parent, out var p))
            {
                var pi = await Provider.GetItemAsync(parent, ct).ConfigureAwait(false);
                if (pi is null) return null;
                p = (NameCodec.ToWindows(pi.Name), pi.Parents.FirstOrDefault());
                names[parent] = p;
            }
            parts.Insert(0, p.Name);
            parent = p.Parent;
        }
        return parent == Provider.RootId ? "\\" + string.Join('\\', parts) : null; // outside My Drive (shared)
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(30));
        Provider.Dispose();
    }

    internal static string Invariant(long v) => v.ToString(CultureInfo.InvariantCulture);
}
