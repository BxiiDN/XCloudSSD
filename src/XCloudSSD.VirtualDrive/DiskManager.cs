using System.Net;
using Microsoft.Extensions.Logging;
using XCloudSSD.Authentication;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.GoogleDrive;
using XCloudSSD.Metadata;
using XCloudSSD.Rclone;
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
        Settings.Migrate();
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
            catch (DuplicateAccountException ex)
            {
                _log.LogError("{Letter}: not mounted: {Message}", disk.Account.DriveLetter, ex.Message);
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
            _ => CreateTeraBoxProvider(account.Id),
        };
        return Attach(new VirtualDisk(account, provider, Budget, Transfers, _db, _logs.CreateLogger($"Disk-{account.DriveLetter}")));
    }

    /// <summary>Google Drive is mounted with rclone's own "drive" backend; TeraBox goes through the WebDAV bridge.</summary>
    private VirtualDisk Attach(VirtualDisk disk)
    {
        var accountId = disk.Account.Id;
        if (disk.Account.Provider == ProviderKind.GoogleDrive)
            disk.NativeRcloneRemote = () => GoogleRcloneRemote(accountId);
        disk.BridgePassword = () => BridgePassword(accountId);
        disk.IdentityCheck = identity => ClaimIdentity(disk, identity);
        return disk;
    }

    private string? NdusOf(string accountId) =>
        _secrets.Load(accountId)?.GetValueOrDefault("cookie") is { } c ? TeraBoxWebProvider.SessionId(c) : null;

    private readonly object _identityLock = new();

    /// <summary>
    /// Called by a disk once it knows which cloud user it is signed in as. Two drives for the same TeraBox account
    /// would show the same files, so only the earliest-added one is mounted; the other reports why.
    /// Returns an error message for <paramref name="disk"/> or null when it may mount.
    /// </summary>
    private string? ClaimIdentity(VirtualDisk disk, AccountIdentity identity)
    {
        List<VirtualDisk> toStop = new();
        string? error = null;
        lock (_identityLock)
        {
            var a = disk.Account;
            var changed = false;
            if (identity.UserId is { Length: > 0 } uid && a.RemoteUserId != uid) { a.RemoteUserId = uid; changed = true; }
            if (a.Provider == ProviderKind.TeraBox && !string.IsNullOrEmpty(identity.Email) && a.Email.StartsWith("TeraBox account", StringComparison.Ordinal))
            {
                a.Email = identity.Email;
                a.DisplayName = identity.DisplayName;
                changed = true;
            }
            if (changed) _db.UpsertAccount(a);
            if (a.Provider != ProviderKind.TeraBox) return null;
            var session = NdusOf(a.Id);
            foreach (var other in _disks.Values.Where(d => d != disk && d.Account.Provider == ProviderKind.TeraBox && d.Account.Enabled))
            {
                var same = (!string.IsNullOrEmpty(a.RemoteUserId) && a.RemoteUserId == other.Account.RemoteUserId)
                           || (session is not null && session == NdusOf(other.Account.Id));
                if (!same) continue;
                if (other.Account.AddedAt <= a.AddedAt)
                {
                    error = $"Same TeraBox account as {other.Account.DriveLetter}: ({other.Account.Label}) — both drives would show the same files. " +
                            $"Remove {a.DriveLetter}: in Cloud Accounts and add it again, signing in with a different TeraBox account.";
                    break;
                }
                toStop.Add(other); // this disk was added first: it keeps the account
            }
        }
        foreach (var o in toStop)
            _ = Task.Run(() => o.MarkDuplicate($"Same TeraBox account as {disk.Account.DriveLetter}: ({disk.Account.Label}) — both drives would show the same files. " +
                                               $"Remove {o.Account.DriveLetter}: in Cloud Accounts and add it again, signing in with a different TeraBox account."));
        return error;
    }

    private string BridgePassword(string accountId)
    {
        var s = _secrets.Load(accountId) ?? new Dictionary<string, string>();
        if (s.TryGetValue("bridge_pass", out var p) && p.Length >= 32) return p;
        p = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        s["bridge_pass"] = p;
        _secrets.Save(accountId, s);
        return p;
    }

    /// <summary>
    /// rclone "drive" remote using the user's own OAuth client and this account's refresh token. Passed to rclone
    /// only through its environment (in-memory config); rclone refreshes the access token itself.
    /// </summary>
    internal RcloneRemote GoogleRcloneRemote(string accountId)
    {
        var client = LoadOAuthClient() ?? throw new InvalidOperationException("Google OAuth client is not configured (Settings → Google OAuth client).");
        var secret = _secrets.Load(accountId);
        if (secret is null || !secret.TryGetValue("refresh_token", out var refresh) || string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException("Google sign-in data for this account is missing. Remove the account and add it again.");
        var token = new System.Text.Json.Nodes.JsonObject
        {
            ["access_token"] = "",
            ["token_type"] = "Bearer",
            ["refresh_token"] = refresh,
            ["expiry"] = "2000-01-01T00:00:00Z",
        };
        return new RcloneRemote("drive", new Dictionary<string, string>
        {
            ["client_id"] = client.Id,
            ["client_secret"] = client.Secret,
            ["scope"] = "drive",
            ["token"] = token.ToJsonString(),
        });
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

    private ICloudProvider CreateTeraBoxProvider(string accountId)
    {
        var secret = _secrets.Load(accountId);
        if (secret is null || !secret.TryGetValue("cookie", out var cookie) || string.IsNullOrEmpty(cookie))
            return new UnconfiguredProvider("TeraBox sign-in data for this account is missing. Remove the account and add it again.");
        var provider = new TeraBoxWebProvider(cookie, new Metrics(), null,
            secret.GetValueOrDefault("jstoken"), secret.GetValueOrDefault("base"), new SparseSizeStore(AppPaths.StoreDir, accountId));
        PersistTeraBoxToken(accountId, provider);
        return provider;
    }

    /// <summary>Saves renewed jsTokens so a service restart does not need a new sign-in.</summary>
    private void PersistTeraBoxToken(string accountId, TeraBoxWebProvider provider)
    {
        provider.JsTokenRefreshed += token =>
        {
            try
            {
                var s = _secrets.Load(accountId) ?? new Dictionary<string, string>();
                s["jstoken"] = token;
                s["base"] = provider.BaseUrl;
                _secrets.Save(accountId, s);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not save renewed TeraBox token: {Message}", ex.Message);
            }
        };
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
            var disk = Attach(new VirtualDisk(account, probe, Budget, Transfers, _db, _logs.CreateLogger($"Disk-{letter}")));
            _disks[accountId] = disk;
            await disk.StartAsync(Settings, ct).ConfigureAwait(false);
            return ToDto(disk);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a TeraBox account from the session cookie captured after the user signed in on terabox.com inside
    /// the app. The cookie is verified against TeraBox before anything is saved.
    /// </summary>
    public async Task<AccountDto> AddTeraBoxAsync(AddTeraBoxArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Cookie) || !args.Cookie.Contains("ndus=", StringComparison.Ordinal))
            throw new InvalidOperationException("TeraBox sign-in was not completed (no session cookie). Sign in again.");
        var newId = "t-" + Guid.NewGuid().ToString("N")[..12];
        var provider = new TeraBoxWebProvider(args.Cookie.Trim(), new Metrics(), null, args.JsToken, args.BaseUrl,
            new SparseSizeStore(AppPaths.StoreDir, newId));
        AccountIdentity? identity = null;
        try
        {
            await provider.InitializeAsync(ct).ConfigureAwait(false);
            await provider.GetQuotaAsync(ct).ConfigureAwait(false);
            try
            {
                identity = await provider.GetIdentityAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CloudException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException && ex is not DuplicateAccountException)
            {
                _log.LogWarning("TeraBox user info not available: {Message}", ex.Message);
            }
            // Multi-account means different accounts: refuse a second drive for the same TeraBox account/session.
            var session = TeraBoxWebProvider.SessionId(args.Cookie);
            foreach (var other in _disks.Values.Where(d => d.Account.Provider == ProviderKind.TeraBox))
            {
                var sameUser = identity?.UserId is { } uid && uid == other.Account.RemoteUserId;
                var sameSession = session is not null && session == NdusOf(other.Account.Id);
                if (sameUser || sameSession)
                    throw new InvalidOperationException(
                        $"This TeraBox account{(string.IsNullOrEmpty(identity?.Email) ? "" : " (" + identity!.Email + ")")} is already connected as " +
                        $"{other.Account.DriveLetter}: ({other.Account.Label}). Sign in with a different TeraBox account.");
            }
        }
        catch
        {
            provider.Dispose();
            throw;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var accountId = newId;
            var letter = PickLetter(args.DriveLetter);
            var ordinal = NextOrdinal(ProviderKind.TeraBox);
            var account = new AccountRecord
            {
                Id = accountId,
                Provider = ProviderKind.TeraBox,
                DisplayName = identity?.DisplayName ?? "TeraBox",
                Email = string.IsNullOrEmpty(identity?.Email) ? $"TeraBox account {ordinal:00}" : identity!.Email,
                RemoteUserId = identity?.UserId ?? "",
                DriveLetter = letter,
                Label = AccountRecord.DefaultLabel(ProviderKind.TeraBox, ordinal),
                Enabled = true,
            };
            _secrets.Save(accountId, new Dictionary<string, string>
            {
                ["cookie"] = args.Cookie.Trim(),
                ["jstoken"] = provider.JsToken ?? "",
                ["base"] = provider.BaseUrl,
            });
            PersistTeraBoxToken(accountId, provider);
            _db.UpsertAccount(account);
            var disk = Attach(new VirtualDisk(account, provider, Budget, Transfers, _db, _logs.CreateLogger($"Disk-{letter}")));
            _disks[accountId] = disk;
            await disk.StartAsync(Settings, ct).ConfigureAwait(false);
            return ToDto(disk);
        }
        finally
        {
            _gate.Release();
        }
    }

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
        s.CacheMaxGB = Math.Clamp(s.CacheMaxGB, 1, 100_000);
        s.CacheMinFreeGB = Math.Clamp(s.CacheMinFreeGB, 0, 10_000);
        s.CacheMaxAgeHours = Math.Clamp(s.CacheMaxAgeHours, 1, 24 * 3650);
        s.WriteBackSeconds = Math.Clamp(s.WriteBackSeconds, 0, 3600);
        s.RcloneTransfers = Math.Clamp(s.RcloneTransfers, 1, 32);
        s.CacheDirectory = (s.CacheDirectory ?? "").Trim();
        if (s.CacheDirectory.Length > 0 && !Path.IsPathFullyQualified(s.CacheDirectory))
            throw new InvalidOperationException("The cache folder must be a full path, e.g. C:\\XCloudSSDCache.");
        var old = Settings;
        var remount = old.Engine != s.Engine || old.EffectiveCacheDirectory != s.EffectiveCacheDirectory
                      || old.CacheMaxGB != s.CacheMaxGB || old.CacheMinFreeGB != s.CacheMinFreeGB
                      || old.CacheMaxAgeHours != s.CacheMaxAgeHours || old.WriteBackSeconds != s.WriteBackSeconds
                      || old.RcloneTransfers != s.RcloneTransfers || old.ReportNtfs != s.ReportNtfs
                      || (s.Engine == MountEngine.Rclone && (old.UploadLimitKBps != s.UploadLimitKBps || old.DownloadLimitKBps != s.DownloadLimitKBps));
        Settings = s;
        _db.SaveSettings(s);
        Budget.Resize((long)s.RamBufferMB << 20);
        ApplyThrottles();
        foreach (var d in _disks.Values) d.ApplySettings(s);
        // Engine and rclone options are command-line flags: mounted drives are remounted to apply them.
        if (remount)
            foreach (var d in _disks.Values.Where(d => d.Mounted).ToList())
                _ = Task.Run(async () =>
                {
                    d.Stop(TimeSpan.FromSeconds(120));
                    await StartDiskWithRetryAsync(d, CancellationToken.None).ConfigureAwait(false);
                });
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
            RcloneVersion = RcloneInfo.Version(AppPaths.RcloneExe),
            Engine = Settings.Engine.ToString(),
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
        ActiveTransfers = d.Engine == MountEngine.Rclone ? d.PendingCommits : Transfers.ActiveCountFor(d.Account.Id) + d.PendingCommits,
        OpenHandles = d.OpenHandles,
        Engine = d.Mounted ? d.Engine.ToString() : "",
        CacheDirectory = d.CacheDirectory ?? "",
        CacheBytes = d.CacheStatus?.CacheBytes ?? 0,
        CacheOutOfSpace = d.CacheStatus?.OutOfSpace ?? false,
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
