using System.Net;
using Microsoft.Extensions.Logging;
using XCloudSSD.Authentication;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.GoogleDrive;
using XCloudSSD.Metadata;
using XCloudSSD.Security;
using XCloudSSD.TeraBox;
using XCloudSSD.Transfer;

namespace XCloudSSD.VirtualDrive;

/// <summary>
/// Owns every account and its virtual disk. Lives inside XCloudSSDService; the desktop app drives it
/// over the control pipe.
/// </summary>
public sealed class DiskManager : IDisposable
{
    private const string OAuthSecretKey = "google-oauth-client";

    private readonly AppDatabase _db;
    private readonly SecretStore _secrets;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly Dictionary<string, VirtualDisk> _disks = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _perfTimer;
    private PerfDto _perf = new();
    private (long Up, long Down, long Ops, long Rd, long Wr, DateTime At) _last;

    public DiskManager(AppDatabase db, SecretStore secrets, ILoggerFactory logs)
    {
        _db = db;
        _secrets = secrets;
        _logs = logs;
        _log = logs.CreateLogger("DiskManager");
        Settings = db.LoadSettings();
        Budget = new MemoryBudget((long)Settings.RamBufferMB << 20);
        Transfers = new TransferTracker(db);
        ApplyThrottles();
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XCloudSSD/1.0 (gzip)");
        var g = Metrics.Global;
        _last = (g.BytesUp, g.BytesDown, g.FsOps, g.FsReadBytes, g.FsWriteBytes, DateTime.UtcNow);
        _perfTimer = new Timer(_ => SamplePerf(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public AppSettings Settings { get; private set; }
    public MemoryBudget Budget { get; }
    public TransferTracker Transfers { get; }

    // ------------------------------------------------------------------ lifecycle

    public async Task StartAsync(CancellationToken ct)
    {
        var interrupted = _db.MarkInFlightAsInterrupted();
        if (interrupted > 0) _log.LogWarning("{Count} upload(s) were interrupted by the previous shutdown/crash.", interrupted);

        foreach (var account in _db.GetAccounts())
        {
            if (!Settings.RestorePreviousConfiguration && !account.Enabled)
            {
                account.Enabled = true;
                _db.UpsertAccount(account);
            }
            var disk = CreateDisk(account);
            _disks[account.Id] = disk;
            if (account.Enabled && Settings.MountAtStartup) _ = StartDiskWithRetryAsync(disk, ct);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task StartDiskWithRetryAsync(VirtualDisk disk, CancellationToken ct)
    {
        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await disk.StartAsync(Settings, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Mount of {Letter}: failed (attempt {Attempt}): {Message}", disk.Account.DriveLetter, attempt, ex.Message);
                if (!Settings.AutoReconnect && attempt >= 3) return;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), ct).ConfigureAwait(false);
            }
        }
    }

    public List<string> StopAll()
    {
        var unsaved = new List<string>();
        foreach (var d in _disks.Values) unsaved.AddRange(d.Stop(TimeSpan.FromSeconds(60)));
        return unsaved;
    }

    private VirtualDisk CreateDisk(AccountRecord account)
    {
        ICloudProvider provider = account.Provider switch
        {
            ProviderKind.GoogleDrive => CreateGoogleProvider(account.Id, out _),
            _ => new TeraBoxProvider(),
        };
        return new VirtualDisk(account, provider, Budget, Transfers, _db, _logs.CreateLogger($"Disk-{account.DriveLetter}"));
    }

    private ICloudProvider CreateGoogleProvider(string accountId, out GoogleAccessTokenSource? tokens)
    {
        tokens = null;
        var client = LoadOAuthClient();
        var secret = _secrets.Load(accountId);
        if (client is null || secret is null || !secret.TryGetValue("refresh_token", out var refresh))
            return new UnconfiguredProvider("Google sign-in data for this account is missing. Remove the account and add it again.");
        var tokenClient = new GoogleTokenClient(_http, client.Value.Id, client.Value.Secret);
        var source = new GoogleAccessTokenSource(tokenClient, _secrets, accountId, refresh);
        tokens = source;
        var metrics = new Metrics();
        return new GoogleDriveProvider(_http, (force, ct) => source.GetAsync(ct, force), metrics);
    }

    // ------------------------------------------------------------------ OAuth client

    public (string Id, string Secret)? LoadOAuthClient()
    {
        var v = _secrets.Load(OAuthSecretKey);
        if (v is null || !v.TryGetValue("client_id", out var id) || !v.TryGetValue("client_secret", out var secret)) return null;
        return (id, secret);
    }

    public OAuthClientDto GetOAuthClient()
    {
        var c = LoadOAuthClient();
        return new OAuthClientDto { ClientId = c?.Id ?? "", HasSecret = c is not null };
    }

    public void SetOAuthClient(OAuthClientDto dto)
    {
        var existing = LoadOAuthClient();
        var secret = string.IsNullOrEmpty(dto.ClientSecret) ? existing?.Secret : dto.ClientSecret;
        if (string.IsNullOrWhiteSpace(dto.ClientId) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Both the OAuth client ID and client secret are required.");
        _secrets.Save(OAuthSecretKey, new Dictionary<string, string> { ["client_id"] = dto.ClientId.Trim(), ["client_secret"] = secret.Trim() });
    }

    // ------------------------------------------------------------------ accounts

    public async Task<AccountDto> AddGoogleAsync(AddGoogleArgs args, CancellationToken ct)
    {
        var client = LoadOAuthClient() ?? throw new InvalidOperationException("Set up the Google OAuth client first (Settings → Google sign-in).");
        var tokenClient = new GoogleTokenClient(_http, client.Id, client.Secret);
        var tokens = await tokenClient.ExchangeCodeAsync(args.Code, args.CodeVerifier, args.RedirectUri, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            throw new InvalidOperationException("Google did not return a refresh token. Remove XCloudSSD's access at myaccount.google.com/permissions and try again.");

        var accountId = "g-" + Guid.NewGuid().ToString("N")[..12];
        var source = new GoogleAccessTokenSource(tokenClient, _secrets, accountId, tokens.RefreshToken);
        source.Seed(tokens.AccessToken, tokens.ExpiresIn);
        var probe = new GoogleDriveProvider(_http, (force, c) => source.GetAsync(c, force), new Metrics());
        var identity = await probe.GetIdentityAsync(ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disks.Values.Any(d => d.Account.Provider == ProviderKind.GoogleDrive &&
                                       string.Equals(d.Account.Email, identity.Email, StringComparison.OrdinalIgnoreCase)))
            {
                await tokenClient.RevokeAsync(tokens.RefreshToken, ct).ConfigureAwait(false);
                throw new InvalidOperationException($"{identity.Email} is already connected. Each Google account gets exactly one virtual disk.");
            }

            var letter = PickLetter(args.DriveLetter);
            var ordinal = NextOrdinal(ProviderKind.GoogleDrive);
            var account = new AccountRecord
            {
                Id = accountId,
                Provider = ProviderKind.GoogleDrive,
                DisplayName = identity.DisplayName,
                Email = identity.Email,
                DriveLetter = letter,
                Label = AccountRecord.DefaultLabel(ProviderKind.GoogleDrive, ordinal),
                Enabled = true,
            };
            _secrets.Save(accountId, new Dictionary<string, string> { ["refresh_token"] = tokens.RefreshToken });
            _db.UpsertAccount(account);
            var disk = new VirtualDisk(account, probe, Budget, Transfers, _db, _logs.CreateLogger($"Disk-{letter}"));
            _disks[accountId] = disk;
            await disk.StartAsync(Settings, ct).ConfigureAwait(false);
            return ToDto(disk);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>TeraBox cannot be mounted without Open Platform credentials; say so instead of pretending.</summary>
    public Task<AccountDto> AddTeraBoxAsync(AddTeraBoxArgs args, CancellationToken ct)
        => throw new NotSupportedException(TeraBoxProvider.UnavailableMessage);

    public async Task RemoveAsync(string accountId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disk = Get(accountId);
            var unsaved = disk.Stop(TimeSpan.FromSeconds(60));
            if (unsaved.Count > 0)
            {
                // Remount so the user's data is not discarded.
                _ = StartDiskWithRetryAsync(disk, CancellationToken.None);
                throw new InvalidOperationException("Some files could not be saved to the cloud yet, so the account was not removed: " + string.Join(", ", unsaved));
            }
            if (disk.Account.Provider == ProviderKind.GoogleDrive && LoadOAuthClient() is { } c && _secrets.Load(accountId) is { } s &&
                s.TryGetValue("refresh_token", out var rt))
            {
                try
                {
                    await new GoogleTokenClient(_http, c.Id, c.Secret).RevokeAsync(rt, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Token revoke failed (account removed locally anyway): {Message}", ex.Message);
                }
            }
            _secrets.Delete(accountId);
            _db.DeleteAccount(accountId);
            disk.Provider.Dispose();
            _disks.Remove(accountId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Rename(string accountId, string label)
    {
        label = label.Trim();
        if (label.Length is 0 or > 32) throw new InvalidOperationException("The disk name must be 1–32 characters.");
        var disk = Get(accountId);
        disk.SetLabel(label);
        _db.UpsertAccount(disk.Account);
    }

    public void Disconnect(string accountId)
    {
        var disk = Get(accountId);
        var unsaved = disk.Stop(TimeSpan.FromSeconds(60));
        disk.Account.Enabled = false;
        _db.UpsertAccount(disk.Account);
        if (unsaved.Count > 0)
            throw new InvalidOperationException("Disconnected, but these files were not saved to the cloud: " + string.Join(", ", unsaved));
    }

    public async Task ReconnectAsync(string accountId, CancellationToken ct)
    {
        var disk = Get(accountId);
        disk.Account.Enabled = true;
        _db.UpsertAccount(disk.Account);
        if (disk.Provider is UnconfiguredProvider)
        {
            var fresh = CreateDisk(disk.Account);
            _disks[accountId] = fresh;
            disk = fresh;
        }
        await disk.StartAsync(Settings, ct).ConfigureAwait(false);
    }

    public async Task RemountAsync(string accountId, CancellationToken ct)
    {
        var disk = Get(accountId);
        var unsaved = disk.Stop(TimeSpan.FromSeconds(60));
        await disk.StartAsync(Settings, ct).ConfigureAwait(false);
        if (unsaved.Count > 0)
            throw new InvalidOperationException("Remounted, but these files were not saved to the cloud: " + string.Join(", ", unsaved));
    }

    public async Task ChangeDriveLetterAsync(string accountId, char letter, CancellationToken ct)
    {
        letter = char.ToUpperInvariant(letter);
        var disk = Get(accountId);
        if (disk.Account.DriveLetter == letter) return;
        if (!FreeLetters().Contains(letter)) throw new InvalidOperationException($"Drive letter {letter}: is not available.");
        var wasMounted = disk.Mounted;
        var unsaved = disk.Stop(TimeSpan.FromSeconds(60));
        if (unsaved.Count > 0)
        {
            await disk.StartAsync(Settings, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Drive letter not changed: some files are not saved to the cloud yet.");
        }
        disk.Account.DriveLetter = letter;
        _db.UpsertAccount(disk.Account);
        if (wasMounted || disk.Account.Enabled) await disk.StartAsync(Settings, ct).ConfigureAwait(false);
    }

    private VirtualDisk Get(string id) => _disks.TryGetValue(id, out var d) ? d : throw new InvalidOperationException("Unknown account.");

    // ------------------------------------------------------------------ drive letters

    public List<char> FreeLetters()
    {
        var used = new HashSet<char>(Environment.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])));
        foreach (var d in _disks.Values) used.Add(char.ToUpperInvariant(d.Account.DriveLetter));
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Where(c => !used.Contains(c)).ToList();
    }

    private char PickLetter(char? requested)
    {
        var free = FreeLetters();
        if (requested is { } r && free.Contains(char.ToUpperInvariant(r))) return char.ToUpperInvariant(r);
        if (free.Count == 0) throw new InvalidOperationException("No free drive letters (D–Z).");
        return free[0];
    }

    private int NextOrdinal(ProviderKind kind)
    {
        var labels = _disks.Values.Where(d => d.Account.Provider == kind).Select(d => d.Account.Label).ToHashSet();
        for (var i = 1; ; i++)
            if (!labels.Contains(AccountRecord.DefaultLabel(kind, i))) return i;
    }

    // ------------------------------------------------------------------ settings

    public void UpdateSettings(AppSettings s)
    {
        s.RamBufferMB = Math.Clamp(s.RamBufferMB, 64, 16384);
        s.ReadChunkKB = Math.Clamp(s.ReadChunkKB, 256, 65536);
        s.UploadChunkMB = Math.Clamp(s.UploadChunkMB, 1, 256);
        s.ReadAheadChunks = Math.Clamp(s.ReadAheadChunks, 0, 64);
        s.MaxParallelRequestsPerDisk = Math.Clamp(s.MaxParallelRequestsPerDisk, 1, 64);
        Settings = s;
        _db.SaveSettings(s);
        Budget.Resize((long)s.RamBufferMB << 20);
        ApplyThrottles();
        foreach (var d in _disks.Values) d.ApplySettings(s);
    }

    private void ApplyThrottles()
    {
        BandwidthThrottle.Upload.SetLimitKBps(Settings.UploadLimitKBps);
        BandwidthThrottle.Download.SetLimitKBps(Settings.DownloadLimitKBps);
    }

    // ------------------------------------------------------------------ status / perf / search

    public ServiceStatusDto GetStatus()
    {
        var (installed, version, _) = WinFspInfo.Detect();
        return new ServiceStatusDto
        {
            Version = typeof(DiskManager).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            InternetAvailable = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(),
            WinFspInstalled = installed,
            WinFspVersion = version,
            OAuthClientConfigured = LoadOAuthClient() is not null,
            Accounts = _disks.Values.Select(ToDto).ToList(),
            Perf = _perf,
        };
    }

    private AccountDto ToDto(VirtualDisk d) => new()
    {
        Id = d.Account.Id,
        Provider = d.Account.Provider,
        Label = d.Account.Label,
        DisplayName = d.Account.DisplayName,
        Email = d.Account.Email,
        DriveLetter = d.Account.DriveLetter,
        Enabled = d.Account.Enabled,
        State = d.State,
        StateMessage = d.StateMessage,
        Mounted = d.Mounted,
        TotalBytes = d.Quota?.TotalBytes,
        UsedBytes = d.Quota?.UsedBytes ?? 0,
        FreeBytes = d.Quota?.FreeBytes,
        QuotaUpdated = d.QuotaUpdated,
        CanMount = d.Provider.Capabilities.CanMount,
        CapabilityNotes = d.Provider.Capabilities.Notes,
        ActiveTransfers = Transfers.ActiveCountFor(d.Account.Id) + d.PendingCommits,
        OpenHandles = d.OpenHandles,
    };

    private void SamplePerf()
    {
        var g = Metrics.Global;
        var now = DateTime.UtcNow;
        var secs = Math.Max(0.001, (now - _last.At).TotalSeconds);
        var cur = (g.BytesUp, g.BytesDown, g.FsOps, g.FsReadBytes, g.FsWriteBytes, now);
        _perf = new PerfDto
        {
            UploadBytesPerSec = (cur.BytesUp - _last.Up) / secs,
            DownloadBytesPerSec = (cur.BytesDown - _last.Down) / secs,
            FsOpsPerSec = (cur.FsOps - _last.Ops) / secs,
            FsReadBytesPerSec = (cur.FsReadBytes - _last.Rd) / secs,
            FsWriteBytesPerSec = (cur.FsWriteBytes - _last.Wr) / secs,
            LatencyMs = g.TakeAverageLatencyMs(),
            ActiveRequests = g.ActiveRequests,
            RequestsTotal = g.Requests,
            RequestErrors = g.RequestErrors,
            RamUsedBytes = Budget.Used,
            RamCapacityBytes = Budget.Capacity,
            RamPeakBytes = Budget.Peak,
            OpenHandles = g.OpenHandles,
            TransferQueue = Transfers.ActiveCount + _disks.Values.Sum(d => d.PendingCommits),
            SampledAt = DateTimeOffset.UtcNow,
        };
        if (_perf.LatencyMs < 0 && g.LastLatencyMs >= 0 && g.ActiveRequests > 0) _perf.LatencyMs = g.LastLatencyMs;
        _last = (cur.BytesUp, cur.BytesDown, cur.FsOps, cur.FsReadBytes, cur.FsWriteBytes, now);
    }

    public async Task<List<SearchResultDto>> SearchAsync(SearchArgs args, CancellationToken ct)
    {
        var disk = Get(args.AccountId);
        var hits = await disk.SearchAsync(args.Text, Math.Clamp(args.Max, 1, 1000), ct).ConfigureAwait(false);
        return hits.Select(h => new SearchResultDto
        {
            Name = NameCodec.ToWindows(h.Item.Name),
            Path = h.Path,
            Size = h.Item.Size,
            IsFolder = h.Item.IsFolder,
            Modified = h.Item.Modified,
        }).ToList();
    }

    public void Dispose()
    {
        _perfTimer.Dispose();
        StopAll();
        _http.Dispose();
    }
}

/// <summary>Placeholder provider for an account whose credentials are missing; it never mounts.</summary>
internal sealed class UnconfiguredProvider(string reason) : ICloudProvider
{
    public ProviderKind Kind => ProviderKind.GoogleDrive;
    public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.None with { Notes = reason };
    public string RootId => "";
    private CloudException Err() => new(CloudErrorKind.Unauthorized, reason);
    public Task InitializeAsync(CancellationToken ct) => throw Err();
    public Task<AccountIdentity> GetIdentityAsync(CancellationToken ct) => throw Err();
    public Task<QuotaInfo> GetQuotaAsync(CancellationToken ct) => throw Err();
    public Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string folderId, CancellationToken ct) => throw Err();
    public Task<CloudItem?> GetItemAsync(string id, CancellationToken ct) => throw Err();
    public Task<int> ReadRangeAsync(string fileId, long offset, Memory<byte> buffer, CancellationToken ct) => throw Err();
    public Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken ct) => throw Err();
    public Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, uint? a, CancellationToken ct) => throw Err();
    public Task<IUploadSession> BeginUploadAsync(UploadTarget target, CancellationToken ct) => throw Err();
    public Task<CloudItem> UpdateMetadataAsync(string id, MetadataUpdate update, CancellationToken ct) => throw Err();
    public Task DeleteAsync(string id, bool permanent, CancellationToken ct) => throw Err();
    public Task<string> GetChangeTokenAsync(CancellationToken ct) => throw Err();
    public Task<ChangeSet> GetChangesAsync(string token, CancellationToken ct) => throw Err();
    public Task<IReadOnlyList<CloudItem>> SearchAsync(string text, int max, CancellationToken ct) => throw Err();
    public void Dispose() { }
}
