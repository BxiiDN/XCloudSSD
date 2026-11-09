using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Transfer;

namespace XCloudSSD.Rclone;

/// <summary>An rclone remote defined only through environment variables (no rclone.conf on disk).</summary>
public sealed record RcloneRemote(string Type, IReadOnlyDictionary<string, string> Options);

public sealed class RcloneMountOptions
{
    public required string RcloneExe { get; init; }
    public required RcloneRemote Remote { get; init; }
    public required char DriveLetter { get; init; }
    public required string VolumeLabel { get; init; }
    public required string CacheDir { get; init; }
    public required string LogFile { get; init; }
    public int CacheMaxGB { get; init; } = 100;
    public int CacheMinFreeGB { get; init; } = 10;
    public int CacheMaxAgeHours { get; init; } = 168;
    public int WriteBackSeconds { get; init; } = 5;
    public int Transfers { get; init; } = 4;
    public int DirCacheSeconds { get; init; } = 60;
    /// <summary>Change polling (Google Drive). 0 = provider has no change feed.</summary>
    public int PollSeconds { get; init; }
    public bool ReportNtfs { get; init; } = true;
    public int UploadLimitKBps { get; init; }
    public int DownloadLimitKBps { get; init; }
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}

/// <summary>Live state of the rclone VFS cache and upload queue, read from rclone's remote-control API.</summary>
public sealed record RcloneStatus(long CacheBytes, int CacheFiles, int UploadsInProgress, int UploadsQueued, int ErroredFiles,
    bool OutOfSpace, double SpeedBytesPerSec, long TotalBytes, string? LastError);

/// <summary>
/// One "rclone mount" process: the drive letter, the VFS cache on the local disk and uploads in the background.
/// rclone is configured only through environment variables of the child process (in-memory config), so tokens
/// and passwords are never written to an rclone.conf file. Status is read via rclone's rc API on 127.0.0.1 with a
/// random password.
/// </summary>
public sealed class RcloneMount : IDisposable
{
    public const string RemoteName = "xcs";
    private readonly RcloneMountOptions _opt;
    private readonly ILogger _log;
    private readonly TransferTracker? _transfers;
    private readonly string _accountId;
    private readonly HttpClient _rc;
    private readonly string _rcUrl;
    private readonly Dictionary<string, TransferHandle> _tracked = new(StringComparer.Ordinal);
    private Process? _process;
    private Timer? _poll;
    private int _polling;

    public RcloneMount(RcloneMountOptions options, ILogger log, TransferTracker? transfers = null, string accountId = "")
    {
        _opt = options;
        _log = log;
        _transfers = transfers;
        _accountId = accountId;
        var port = FreePort();
        _rcUrl = $"http://127.0.0.1:{port}/";
        RcUser = "xcs";
        RcPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _rc = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(_rcUrl), Timeout = TimeSpan.FromSeconds(15) };
        _rc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RcUser}:{RcPassword}")));
    }

    internal string RcUser { get; }
    internal string RcPassword { get; }
    public RcloneStatus? Status { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public string Root => $"{char.ToUpperInvariant(_opt.DriveLetter)}:\\";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Command-line arguments (without secrets; those are in the environment).</summary>
    internal List<string> BuildArguments()
    {
        var o = _opt;
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "mount", RemoteName + ":", $"{char.ToUpperInvariant(o.DriveLetter)}:",
            "--config", "NUL",
            "--volname", o.VolumeLabel,
            "--cache-dir", o.CacheDir,
            "--vfs-cache-mode", "full",
            "--vfs-cache-max-size", $"{Math.Max(1, o.CacheMaxGB)}G",
            "--vfs-cache-min-free-space", $"{Math.Max(0, o.CacheMinFreeGB)}G",
            "--vfs-cache-max-age", $"{Math.Max(1, o.CacheMaxAgeHours)}h",
            "--vfs-write-back", $"{Math.Max(0, o.WriteBackSeconds)}s",
            "--vfs-read-chunk-size", "32M",
            "--buffer-size", "32M",
            "--dir-cache-time", $"{Math.Max(1, o.DirCacheSeconds)}s",
            "--poll-interval", o.PollSeconds > 0 ? $"{o.PollSeconds}s" : "0",
            "--transfers", Math.Clamp(o.Transfers, 1, 32).ToString(inv),
            "--low-level-retries", "20",
            "--retries", "5",
            // Everyone gets full access (incl. "write extended attributes"): launchers such as Steam check this.
            "--file-perms", "0777",
            "--dir-perms", "0777",
            "--option", "FileSecurity=D:P(A;;FA;;;WD)",
            "--rc",
            "--rc-addr", _rcUrl["http://".Length..].TrimEnd('/'),
            "--rc-user", RcUser,
            "--rc-pass", RcPassword,
            "--log-file", o.LogFile,
            "--log-level", "INFO",
        };
        // WinFsp prefixes FileSystemName with "FUSE-"; ExactFileSystemName reports plain "NTFS" (Steam/launcher checks).
        if (o.ReportNtfs) args.Add("--fuse-flag=--ExactFileSystemName=NTFS");
        if (o.UploadLimitKBps > 0 || o.DownloadLimitKBps > 0)
        {
            string L(int k) => k > 0 ? $"{k}k" : "off";
            args.Add("--bwlimit");
            args.Add($"{L(o.UploadLimitKBps)}:{L(o.DownloadLimitKBps)}");
        }
        args.AddRange(o.ExtraArgs);
        return args;
    }

    internal static Dictionary<string, string> BuildEnvironment(RcloneRemote remote)
    {
        var prefix = $"RCLONE_CONFIG_{RemoteName.ToUpperInvariant()}_";
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [prefix + "TYPE"] = remote.Type };
        foreach (var (k, v) in remote.Options) env[prefix + k.ToUpperInvariant()] = v;
        return env;
    }

    /// <summary>Starts rclone and waits until the drive letter is visible. Throws with rclone's own error otherwise.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        await StartProcessAsync(ct).ConfigureAwait(false);
        // rclone names the cache folder after the remote plus a hash of its settings. If a setting changed (e.g. the
        // bridge port), files still waiting for upload sit in the old folder: move them over and start again so
        // rclone uploads them.
        try
        {
            if (await FindOrphanedCacheAsync(ct).ConfigureAwait(false) is { } adopt)
            {
                _log.LogWarning("{Letter}: adopting {Count} cached file(s) from an earlier rclone cache folder.", _opt.DriveLetter, adopt.Files);
                QuitProcess();
                MergeCache(adopt);
                await StartProcessAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or JsonException)
        {
            _log.LogWarning("{Letter}: could not check older cache folders: {Message}", _opt.DriveLetter, ex.Message);
            if (!IsRunning) await StartProcessAsync(ct).ConfigureAwait(false);
        }
        _poll = new Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    internal sealed record OrphanCache(string DataRoot, string MetaRoot, List<(string Data, string Meta)> Orphans, int Files);

    private async Task<OrphanCache?> FindOrphanedCacheAsync(CancellationToken ct)
    {
        var vfs = await RcAsync("vfs/stats", ct).ConfigureAwait(false);
        var dc = vfs?["diskCache"] as JsonObject;
        var data = StripLongPrefix(dc?["path"]?.GetValue<string>());
        var meta = StripLongPrefix(dc?["pathMeta"]?.GetValue<string>());
        if (data is null || meta is null) return null;
        return FindOrphans(data, meta);
    }

    internal static OrphanCache? FindOrphans(string dataRoot, string metaRoot)
    {
        var dataParent = Path.GetDirectoryName(dataRoot);
        var metaParent = Path.GetDirectoryName(metaRoot);
        if (dataParent is null || metaParent is null || !Directory.Exists(metaParent)) return null;
        var current = Path.GetFileName(metaRoot);
        var orphans = new List<(string, string)>();
        var files = 0;
        foreach (var dir in Directory.GetDirectories(metaParent, RemoteName + "*"))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
            if (name != RemoteName && !name.StartsWith(RemoteName + "{", StringComparison.Ordinal)) continue;
            var count = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
            if (count == 0) continue;
            orphans.Add((Path.Combine(dataParent, name), dir));
            files += count;
        }
        return orphans.Count == 0 ? null : new OrphanCache(dataRoot, metaRoot, orphans, files);
    }

    /// <summary>Moves cached files (data + metadata) from older cache folders into the current one; never overwrites.</summary>
    internal static void MergeCache(OrphanCache c)
    {
        foreach (var (data, meta) in c.Orphans)
        {
            foreach (var metaFile in Directory.EnumerateFiles(meta, "*", SearchOption.AllDirectories).ToList())
            {
                var rel = Path.GetRelativePath(meta, metaFile);
                var dataFile = Path.Combine(data, rel);
                var targetMeta = Path.Combine(c.MetaRoot, rel);
                var targetData = Path.Combine(c.DataRoot, rel);
                if (File.Exists(targetMeta)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(targetMeta)!);
                if (File.Exists(dataFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetData)!);
                    File.Move(dataFile, targetData, overwrite: true);
                }
                File.Move(metaFile, targetMeta);
            }
            TryDelete(meta);
            TryDelete(data);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any()) Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string? StripLongPrefix(string? p) =>
        p is null ? null : p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + p[8..]
            : p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;

    /// <summary>Asks rclone to unmount and exit now (no waiting for uploads; they stay in the cache).</summary>
    private void QuitProcess()
    {
        try
        {
            RcAsync("core/quit", CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
        if (_process is { } p && !p.WaitForExit(20_000)) Kill();
        _process?.Dispose();
        _process = null;
        for (var i = 0; i < 40 && Directory.Exists(Root); i++) Thread.Sleep(250);
    }

    private async Task StartProcessAsync(CancellationToken ct)
    {
        if (!File.Exists(_opt.RcloneExe)) throw new FileNotFoundException("rclone.exe was not found. Reinstall XCloudSSD.", _opt.RcloneExe);
        Directory.CreateDirectory(_opt.CacheDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_opt.LogFile)!);
        RotateLog(_opt.LogFile);

        var psi = new ProcessStartInfo(_opt.RcloneExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in BuildArguments()) psi.ArgumentList.Add(a);
        // rclone reads any RCLONE_* variable as a flag/config: start from a clean slate, then add only ours.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        foreach (var (k, v) in BuildEnvironment(_opt.Remote)) psi.Environment[k] = v;

        var stderr = new StringBuilder();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } line) lock (stderr) if (stderr.Length < 8000) stderr.AppendLine(line); };
        p.OutputDataReceived += (_, e) => { };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        _process = p;
        _log.LogInformation("rclone started (pid {Pid}) for {Letter}:", p.Id, _opt.DriveLetter);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (p.HasExited)
            {
                var detail = LastErrorLines(_opt.LogFile) ?? stderr.ToString().Trim();
                _process = null;
                throw new InvalidOperationException($"rclone could not mount {_opt.DriveLetter}: (exit code {p.ExitCode}). {detail}".Trim());
            }
            if (Directory.Exists(Root)) break;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        if (!Directory.Exists(Root))
        {
            Kill();
            throw new TimeoutException($"rclone did not mount {_opt.DriveLetter}: within 60 seconds. {LastErrorLines(_opt.LogFile)}".Trim());
        }
    }

    private static void RotateLog(string file)
    {
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > 0) File.Move(file, file + ".1", overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    internal static string? LastErrorLines(string logFile)
    {
        try
        {
            if (!File.Exists(logFile)) return null;
            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var errors = lines.Where(l => l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("CRITICAL", StringComparison.Ordinal)
                                          || l.Contains("Fatal", StringComparison.OrdinalIgnoreCase)).TakeLast(3).ToList();
            return errors.Count > 0 ? string.Join(" | ", errors) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ rc

    private async Task<JsonObject?> RcAsync(string method, CancellationToken ct, JsonObject? body = null)
    {
        using var content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _rc.PostAsync(method, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"rclone rc {method}: HTTP {(int)resp.StatusCode} {text}");
        return string.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : JsonNode.Parse(text) as JsonObject;
    }

    public async Task<RcloneStatus?> RefreshStatusAsync(CancellationToken ct)
    {
        if (!IsRunning) return null;
        var vfs = await RcAsync("vfs/stats", ct).ConfigureAwait(false);
        var stats = await RcAsync("core/stats", ct).ConfigureAwait(false);
        var dc = vfs?["diskCache"] as JsonObject;
        long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : 0;
        var status = new RcloneStatus(
            L(dc?["bytesUsed"]), (int)L(dc?["files"]), (int)L(dc?["uploadsInProgress"]), (int)L(dc?["uploadsQueued"]),
            (int)L(dc?["erroredFiles"]), dc?["outOfSpace"]?.GetValue<bool>() ?? false,
            stats?["speed"] is JsonValue sp && sp.TryGetValue<double>(out var speed) ? speed : 0,
            L(stats?["bytes"]),
            stats?["lastError"]?.GetValue<string>() is { Length: > 0 } le ? le : null);
        Status = status;
        await SyncTransfersAsync(stats, ct).ConfigureAwait(false);
        return status;
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            await RefreshStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            // rclone starting/stopping: try again on the next tick.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    /// <summary>Mirrors rclone's upload queue into the Transfer Manager.</summary>
    private async Task SyncTransfersAsync(JsonObject? stats, CancellationToken ct)
    {
        if (_transfers is null) return;
        var queue = (await RcAsync("vfs/queue", ct).ConfigureAwait(false))?["queue"] as JsonArray ?? [];
        var progress = new Dictionary<string, (long Bytes, long Size)>(StringComparer.Ordinal);
        if (stats?["transferring"] is JsonArray active)
            foreach (var t in active.OfType<JsonObject>())
                if (t["name"]?.GetValue<string>() is { } n)
                    progress[n] = ((long)(t["bytes"]?.GetValue<double>() ?? 0), (long)(t["size"]?.GetValue<double>() ?? 0));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in queue.OfType<JsonObject>())
        {
            var name = q["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            seen.Add(name);
            var size = (long)(q["size"]?.GetValue<double>() ?? 0);
            var tries = (int)(q["tries"]?.GetValue<double>() ?? 0);
            var uploading = q["uploading"]?.GetValue<bool>() ?? false;
            if (!_tracked.TryGetValue(name, out var h) || h.IsFinished)
            {
                h = _transfers.Begin(_accountId, _opt.DriveLetter, "\\" + name.Replace('/', '\\'), TransferDirection.Upload, size);
                _tracked[name] = h;
            }
            if (uploading && progress.TryGetValue(name, out var p)) h.Progress(p.Bytes, p.Size > 0 ? p.Size : size);
            var state = uploading ? (tries > 0 ? TransferState.Retrying : TransferState.Uploading) : TransferState.Pending;
            if (h.Snapshot().State != state) h.SetState(state, tries > 0 ? Status?.LastError : null);
        }
        foreach (var (name, h) in _tracked.ToList())
        {
            if (seen.Contains(name)) continue;
            if (!h.IsFinished)
            {
                var size = h.Snapshot().BytesTotal;
                h.Progress(size, size);
                h.SetState(TransferState.Completed);
            }
            _tracked.Remove(name);
        }
    }

    // ------------------------------------------------------------------ stop

    /// <summary>
    /// Waits (up to <paramref name="flushTimeout"/>) for queued uploads, then asks rclone to unmount and exit.
    /// Files still waiting are kept in the cache folder and uploaded the next time the drive is mounted.
    /// Returns the number of files still waiting for upload.
    /// </summary>
    public int Stop(TimeSpan flushTimeout)
    {
        _poll?.Dispose();
        _poll = null;
        var waiting = 0;
        if (IsRunning)
        {
            var deadline = DateTime.UtcNow + flushTimeout;
            while (true)
            {
                try
                {
                    var s = RefreshStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
                    waiting = (s?.UploadsInProgress ?? 0) + (s?.UploadsQueued ?? 0);
                }
                catch (Exception)
                {
                    break;
                }
                if (waiting == 0 || DateTime.UtcNow >= deadline) break;
                Thread.Sleep(500);
            }
            QuitProcess();
        }
        if (waiting > 0) _log.LogWarning("{Letter}: unmounted with {Count} file(s) still waiting in the cache; they upload on the next mount.", _opt.DriveLetter, waiting);
        return waiting;
    }

    private void Kill()
    {
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        Stop(TimeSpan.Zero);
        _rc.Dispose();
    }
}
