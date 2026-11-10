using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XCloudSSD.Cloud;
using XCloudSSD.Core;

namespace XCloudSSD.TeraBox;

/// <summary>
/// TeraBox adapter using the same web interface as terabox.com, authenticated with the session cookie the
/// user creates by signing in on TeraBox's own page inside XCloudSSD. Request shapes follow the open-source
/// AList TeraBox driver (drivers/terabox/util.go): common query app_id=250528, web=1, channel=dubox,
/// clienttype=0, jsToken; errno 4000023 = refresh jsToken; errno -6 + Url-Domain-Prefix = regional host.
///
/// TeraBox addresses files by path. Internally every item is keyed by its fs_id, and the provider keeps a
/// parent/name map so folder renames and moves never invalidate ids of their contents.
/// </summary>
public sealed class TeraBoxWebProvider : ICloudProvider, IRootOverride
{
    public const string AppId = "250528";
    internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    public const int BlockSize = 4 << 20; // TeraBox upload part size (4 MiB, as AList uses up to 4 GiB)
    private const string RootKey = "root";

    private readonly HttpClient _http;
    private readonly string _cookie;
    private readonly Metrics _metrics;
    private readonly object _mapLock = new();
    private readonly Dictionary<string, (string Parent, string Name, bool IsFolder)> _map = new();
    private readonly Dictionary<string, (string Url, DateTime Expires)> _links = new();
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string _baseUrl = "https://www.terabox.com";
    private string? _jsToken;
    private IReadOnlyList<string>? _uploadHosts;

    public TeraBoxWebProvider(string cookie, Metrics metrics, HttpMessageHandler? handler = null,
        string? jsToken = null, string? baseUrl = null, ISparseSizeStore? sparse = null)
    {
        _sparse = sparse;
        Capabilities = new ProviderCapabilities(
            CanMount: true, RangeRead: true, ResumableUpload: false, Rename: true, Move: true, Trash: true,
            QuotaInfo: true, ChangeFeed: false, CustomProperties: sparse is not null,
            Notes: "TeraBox via its web interface (unofficial, may change). Uploads go in 4 MiB parts; free accounts limit single files to 4 GB. " +
                   "No change feed (folders refresh on a timer); reserved (sparse) sizes are remembered on this PC only; deletes go to the TeraBox recycle bin.");
        if (!string.IsNullOrWhiteSpace(jsToken)) _jsToken = jsToken.Trim();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && b.Scheme == "https" && IsTeraBoxHost(b.Host))
            _baseUrl = $"https://{b.Host}";
        _cookie = cookie;
        _metrics = metrics;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,                 // the session cookie is sent explicitly, only to TeraBox
            AllowAutoRedirect = false,          // download links answer with a redirect we read ourselves
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 32,
        })
        { Timeout = TimeSpan.FromMinutes(10) };
        _map[RootKey] = ("", "", true);
    }

    public ProviderKind Kind => ProviderKind.TeraBox;
    public string RootId => RootKey;

    private readonly ISparseSizeStore? _sparse;

    public ProviderCapabilities Capabilities { get; }

    public void UseRootId(string rootId)
    {
    }

    // ------------------------------------------------------------------ session

    public async Task InitializeAsync(CancellationToken ct)
    {
        // A token captured from the signed-in page is used as-is; otherwise read it from the TeraBox web page.
        if (_jsToken is null) await RefreshJsTokenAsync(expired: false, ct).ConfigureAwait(false);
        // Verifies the session: listing the root fails when it is not signed in.
        await ListChildrenAsync(RootKey, ct).ConfigureAwait(false);
    }

    /// <summary>The jsToken currently in use (persisted by the service so restarts do not need a new sign-in).</summary>
    public string? JsToken => _jsToken;
    public string BaseUrl => _baseUrl;

    /// <summary>Raised when a new jsToken was obtained so it can be saved.</summary>
    public event Action<string>? JsTokenRefreshed;

    internal static bool IsTeraBoxHost(string host) =>
        host.Equals("terabox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".terabox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("1024tera.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("1024terabox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("terabox.app", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("teraboxapp.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads window.jsToken from the TeraBox web page. The page moved from /main to /&lt;language&gt;/main, so redirects
    /// are followed (cookie only ever sent to TeraBox hosts). The token sits in an inline script: fn("&lt;token&gt;"),
    /// URL-encoded as fn%28%22&lt;token&gt;%22%29.
    /// </summary>
    private async Task RefreshJsTokenAsync(bool expired, CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var start in new[] { _baseUrl + "/main", _baseUrl + "/" })
            {
                var url = start;
                for (var hop = 0; hop < 6; hop++)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Cookie", _cookie);
                    req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                    req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                    using var resp = await SendRawAsync(req, ct).ConfigureAwait(false);
                    if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                    {
                        var next = loc.IsAbsoluteUri ? loc : new Uri(new Uri(url), loc);
                        if (!IsTeraBoxHost(next.Host) || next.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)) break;
                        _baseUrl = $"https://{next.Host}";
                        url = next.ToString();
                        continue;
                    }
                    var token = ExtractJsToken(await resp.Content.ReadUtf8Async(ct).ConfigureAwait(false));
                    if (!string.IsNullOrEmpty(token))
                    {
                        _jsToken = token;
                        JsTokenRefreshed?.Invoke(token);
                        return;
                    }
                    break;
                }
            }
            if (_jsToken is not null && !expired) return; // keep the token captured at sign-in
            throw new CloudException(CloudErrorKind.Unauthorized,
                expired
                    ? "TeraBox session expired (jsToken could not be renewed). Remove the TeraBox account and sign in again."
                    : "TeraBox session is not signed in (no jsToken). Remove the account and sign in again.");
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    internal static string? ExtractJsToken(string html)
    {
        var t = Between(html, "fn%28%22", "%22%29");
        if (!string.IsNullOrEmpty(t)) return Uri.UnescapeDataString(t);
        t = Between(html, "window.jsToken%20%3D%20", "%3B");
        if (!string.IsNullOrEmpty(t) && !t.StartsWith('a')) return Uri.UnescapeDataString(t).Trim('"');
        t = Between(html, "\"jsToken\":\"", "\"");
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static string? Between(string text, string start, string end)
    {
        var i = text.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        var j = text.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? null : text[i..j];
    }

    // ------------------------------------------------------------------ identity / quota

    /// <summary>
    /// Signed-in user from /api/user/getinfo (records[0]: uk, nick_name/uname, masked email). "uk" identifies the
    /// account, so the same TeraBox account cannot be mounted twice by mistake.
    /// </summary>
    public async Task<AccountIdentity> GetIdentityAsync(CancellationToken ct)
    {
        var json = await ApiAsync(HttpMethod.Get, "/api/user/getinfo", new() { ["need_relation"] = "0", ["need_secret_info"] = "1" }, null, ct)
            .ConfigureAwait(false);
        var rec = (json["records"] as JsonArray)?.FirstOrDefault() as JsonObject ?? json["data"] as JsonObject;
        string? Str(string key) => rec?[key] is JsonValue v ? v.ToString() : null;
        var uk = Str("uk");
        var name = Str("nick_name") is { Length: > 0 } n ? n : Str("uname") is { Length: > 0 } u ? u : "TeraBox";
        var email = Str("email") is { Length: > 0 } e ? e : Str("uname") ?? "";
        return new AccountIdentity(name, email) { UserId = string.IsNullOrEmpty(uk) ? null : uk };
    }

    /// <summary>Value of the "ndus" session cookie (identifies the signed-in session).</summary>
    public static string? SessionId(string cookieHeader) =>
        cookieHeader.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("ndus=", StringComparison.Ordinal))?[5..];

    public async Task<QuotaInfo> GetQuotaAsync(CancellationToken ct)
    {
        var json = await ApiAsync(HttpMethod.Get, "/api/quota", new() { ["checkexpire"] = "1", ["checkfree"] = "1" }, null, ct).ConfigureAwait(false);
        long? total = json["total"] is { } t ? t.GetValue<long>() : null;
        var used = json["used"] is { } u ? u.GetValue<long>() : 0;
        return new QuotaInfo(total, used);
    }

    // ------------------------------------------------------------------ paths

    private string PathOf(string id)
    {
        lock (_mapLock)
        {
            if (id == RootKey) return "/";
            var parts = new Stack<string>();
            var cur = id;
            for (var guard = 0; cur != RootKey && guard < 256; guard++)
            {
                if (!_map.TryGetValue(cur, out var e)) throw new CloudException(CloudErrorKind.NotFound, $"Unknown TeraBox item {cur}.");
                parts.Push(e.Name);
                cur = e.Parent;
            }
            return "/" + string.Join('/', parts);
        }
    }

    private CloudItem Register(JsonObject f, string parentId)
    {
        var id = f["fs_id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture);
        var name = f["server_filename"]?.GetValue<string>() ?? Path.GetFileName(f["path"]?.GetValue<string>() ?? id);
        var isDir = f["isdir"]?.GetValue<int>() == 1;
        lock (_mapLock) _map[id] = (parentId, name, isDir);
        var mtime = f["server_mtime"] is { } m ? DateTimeOffset.FromUnixTimeSeconds(m.GetValue<long>()) : DateTimeOffset.UnixEpoch;
        var ctime = f["server_ctime"] is { } c ? DateTimeOffset.FromUnixTimeSeconds(c.GetValue<long>()) : mtime;
        var stored = isDir ? 0 : f["size"]?.GetValue<long>() ?? 0;
        var logical = isDir ? null : _sparse?.Get(id);
        return new CloudItem
        {
            Id = id,
            Name = name,
            IsFolder = isDir,
            Size = logical is { } l && l > stored ? l : stored,
            StoredSize = stored,
            Created = ctime,
            Modified = mtime,
            Parents = new[] { parentId },
        };
    }

    // ------------------------------------------------------------------ listing

    public async Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string folderId, CancellationToken ct)
    {
        var dir = PathOf(folderId);
        var result = new List<CloudItem>();
        for (var page = 1; page < 10_000; page++)
        {
            var json = await ApiAsync(HttpMethod.Get, "/api/list", new()
            {
                ["dir"] = dir,
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["num"] = "100",
                ["order"] = "name",
            }, null, ct).ConfigureAwait(false);
            if (json["list"] is not JsonArray list || list.Count == 0) break;
            foreach (var f in list)
                if (f is JsonObject o) result.Add(Register(o, folderId));
            if (list.Count < 100) break;
        }
        return result;
    }

    public async Task<CloudItem?> GetItemAsync(string id, CancellationToken ct)
    {
        string parent, name;
        lock (_mapLock)
        {
            if (!_map.TryGetValue(id, out var e)) return null;
            (parent, name) = (e.Parent, e.Name);
        }
        var siblings = await ListChildrenAsync(parent, ct).ConfigureAwait(false);
        return siblings.FirstOrDefault(s => s.Id == id) ?? siblings.FirstOrDefault(s => s.Name == name);
    }

    /// <summary>Searches folders that have already been listed (the web search API is not used).</summary>
    public Task<IReadOnlyList<CloudItem>> SearchAsync(string text, int max, CancellationToken ct)
    {
        lock (_mapLock)
        {
            IReadOnlyList<CloudItem> hits = _map.Where(kv => kv.Key != RootKey && kv.Value.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(max)
                .Select(kv => new CloudItem { Id = kv.Key, Name = kv.Value.Name, IsFolder = kv.Value.IsFolder, Parents = new[] { kv.Value.Parent } })
                .ToList();
            return Task.FromResult(hits);
        }
    }

    // ------------------------------------------------------------------ read

    public async Task<int> ReadRangeAsync(string fileId, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length == 0) return 0;
        await BandwidthThrottle.Download.WaitAsync(buffer.Length, ct).ConfigureAwait(false);
        return await Retry.RunAsync(async attempt =>
        {
            var url = await GetDownloadUrlAsync(fileId, attempt > 1, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.Range = new RangeHeaderValue(offset, offset + buffer.Length - 1);
            using var resp = await SendRawAsync(req, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return 0;
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                lock (_links) _links.Remove(fileId);
                throw new CloudException(CloudErrorKind.Transient, $"TeraBox download link expired ({(int)resp.StatusCode}).", (int)resp.StatusCode);
            }
            if (!resp.IsSuccessStatusCode)
                throw new CloudException(Retry.KindForStatus(resp.StatusCode), $"TeraBox download failed: {(int)resp.StatusCode}", (int)resp.StatusCode);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.OK && offset > 0)
                throw new CloudException(CloudErrorKind.NotSupported, "TeraBox download server ignored the byte range.");
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
                _metrics.AddDown(n);
            }
            return total;
        }, 4, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Official web download flow (AList "linkOfficial"): sign = RC4(sign3, sign1) from /api/home/info,
    /// /api/download?type=dlink returns a dlink, and requesting the dlink with the cookie redirects to the file server.
    /// </summary>
    private async Task<string> GetDownloadUrlAsync(string fileId, bool forceRefresh, CancellationToken ct)
    {
        lock (_links)
            if (!forceRefresh && _links.TryGetValue(fileId, out var cached) && cached.Expires > DateTime.UtcNow) return cached.Url;

        var home = await ApiAsync(HttpMethod.Get, "/api/home/info", new(), null, ct).ConfigureAwait(false);
        var sign1 = home["data"]?["sign1"]?.GetValue<string>() ?? "";
        var sign3 = home["data"]?["sign3"]?.GetValue<string>() ?? "";
        var dl = await ApiAsync(HttpMethod.Get, "/api/download", new()
        {
            ["type"] = "dlink",
            ["fidlist"] = $"[{fileId}]",
            ["sign"] = Sign(sign3, sign1),
            ["vip"] = "2",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        }, null, ct).ConfigureAwait(false);
        var dlink = dl["dlink"]?[0]?["dlink"]?.GetValue<string>()
                    ?? throw new CloudException(CloudErrorKind.NotFound, $"TeraBox returned no download link (errno {dl["errno"]}).");

        using var req = NewRequest(HttpMethod.Get, dlink);
        using var resp = await SendRawAsync(req, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var url = resp.Headers.Location?.ToString() ?? dlink;
        lock (_links) _links[fileId] = (url, DateTime.UtcNow.AddHours(4));
        return url;
    }

    /// <summary>RC4-style keystream XOR + base64, as in AList's sign().</summary>
    internal static string Sign(string s1, string s2)
    {
        if (s1.Length == 0) return "";
        var a = new int[256];
        var p = new int[256];
        for (var q = 0; q < 256; q++)
        {
            a[q] = s1[q % s1.Length];
            p[q] = q;
        }
        for (int u = 0, q = 0; q < 256; q++)
        {
            u = (u + p[q] + a[q]) % 256;
            (p[q], p[u]) = (p[u], p[q]);
        }
        var o = new byte[s2.Length];
        for (int i = 0, u = 0, q = 0; q < s2.Length; q++)
        {
            i = (i + 1) % 256;
            u = (u + p[i]) % 256;
            (p[i], p[u]) = (p[u], p[i]);
            var k = p[(p[i] + p[u]) % 256];
            o[q] = (byte)(s2[q] ^ k);
        }
        return Convert.ToBase64String(o);
    }

    // ------------------------------------------------------------------ create / manage

    public async Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        var path = Combine(PathOf(parentId), name);
        await ApiAsync(HttpMethod.Post, "/api/create", new() { ["a"] = "commit" }, new()
        {
            ["path"] = path,
            ["isdir"] = "1",
            ["block_list"] = "[]",
        }, ct).ConfigureAwait(false);
        return await FindChildAsync(parentId, name, ct).ConfigureAwait(false);
    }

    public async Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, uint? windowsAttributes, CancellationToken ct)
    {
        var s = await BeginUploadAsync(UploadTarget.NewFile(parentId, name), ct).ConfigureAwait(false);
        return await s.CompleteAsync(ReadOnlyMemory<byte>.Empty, 0, ct).ConfigureAwait(false);
    }

    public async Task<CloudItem> UpdateMetadataAsync(string id, MetadataUpdate u, CancellationToken ct)
    {
        string parent, name;
        lock (_mapLock)
        {
            if (!_map.TryGetValue(id, out var e)) throw new CloudException(CloudErrorKind.NotFound, "Unknown TeraBox item.");
            (parent, name) = (e.Parent, e.Name);
        }
        var path = PathOf(id);
        var newName = u.NewName ?? name;
        if (u.AddParentId is { } newParent && newParent != parent)
        {
            var dest = PathOf(newParent);
            await ManageAsync("move", new JsonArray(new JsonObject { ["path"] = path, ["dest"] = dest, ["newname"] = newName }), ct).ConfigureAwait(false);
            lock (_mapLock) _map[id] = (newParent, newName, _map[id].IsFolder);
            parent = newParent;
        }
        else if (newName != name)
        {
            await ManageAsync("rename", new JsonArray(new JsonObject { ["path"] = path, ["newname"] = newName }), ct).ConfigureAwait(false);
            lock (_mapLock) _map[id] = (parent, newName, _map[id].IsFolder);
        }
        // Attributes and times cannot be stored on TeraBox. A logical (sparse) size is remembered locally.
        if (u.LogicalSize is { } logical && _sparse is not null) _sparse.Set(id, logical);
        return await FindChildAsync(parent, newName, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, bool permanent, CancellationToken ct)
    {
        string path;
        try
        {
            path = PathOf(id);
        }
        catch (CloudException)
        {
            return;
        }
        // The web interface moves deleted items to the TeraBox recycle bin.
        await ManageAsync("delete", new JsonArray(JsonValue.Create(path)), ct).ConfigureAwait(false);
        lock (_mapLock) _map.Remove(id);
        _sparse?.Set(id, null);
    }

    private Task ManageAsync(string opera, JsonArray filelist, CancellationToken ct)
        => ApiAsync(HttpMethod.Post, "/api/filemanager", new() { ["opera"] = opera, ["onnest"] = "fail" }, new()
        {
            ["async"] = "0",
            ["filelist"] = filelist.ToJsonString(),
            ["ondup"] = "newcopy",
        }, ct);

    private async Task<CloudItem> FindChildAsync(string parentId, string name, CancellationToken ct)
    {
        var children = await ListChildrenAsync(parentId, ct).ConfigureAwait(false);
        return children.FirstOrDefault(c => c.Name == name)
               ?? throw new CloudException(CloudErrorKind.NotFound, $"TeraBox did not list '{name}' after the operation.");
    }

    private static string Combine(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public Task<string> GetChangeTokenAsync(CancellationToken ct) => Task.FromResult("none");

    public Task<ChangeSet> GetChangesAsync(string token, CancellationToken ct)
        => Task.FromResult(new ChangeSet("none", Array.Empty<ChangedItem>()));

    // ------------------------------------------------------------------ upload

    public Task<IUploadSession> BeginUploadAsync(UploadTarget target, CancellationToken ct)
    {
        string parentId, name;
        if (target.ExistingFileId is { } id)
        {
            lock (_mapLock)
            {
                if (!_map.TryGetValue(id, out var e)) throw new CloudException(CloudErrorKind.NotFound, "Unknown TeraBox file.");
                (parentId, name) = (e.Parent, e.Name);
            }
        }
        else
        {
            parentId = target.ParentId!;
            name = target.Name!;
        }
        return Task.FromResult<IUploadSession>(new TeraBoxUploadSession(this, parentId, name, overwrite: target.ExistingFileId is not null,
            chunkSize: ChunkSizeFor(target.SizeHint)));
    }

    /// <summary>
    /// TeraBox serves accounts from regional clusters on several domains (terabox.com, 1024tera.com, …). The upload
    /// server must belong to the account's cluster and domain, or it answers 403 errno 31045 "user not exists".
    /// Like the TeraBox web client (and AList), ask &lt;prefix&gt;-data.&lt;domain&gt; where to upload, using the
    /// prefix and domain of the signed-in page.
    /// </summary>
    internal static (string Prefix, string Root) RegionOf(string baseUrl)
    {
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "www.terabox.com";
        var labels = host.Split('.');
        var root = labels.Length >= 2 ? $"{labels[^2]}.{labels[^1]}" : "terabox.com";
        var prefix = labels.Length >= 3 && labels[0] is not ("www" or "") ? labels[0] : "jp";
        return (prefix, root);
    }

    internal async Task<IReadOnlyList<string>> UploadHostsAsync(CancellationToken ct)
    {
        lock (_mapLock)
            if (_uploadHosts is not null) return _uploadHosts;
        var (prefix, root) = RegionOf(_baseUrl);
        var locators = new List<string> { $"{prefix}-data.{root}" };
        if (root != "terabox.com") locators.Add($"{prefix}-data.terabox.com");
        if (prefix != "jp") locators.Add($"jp-data.{root}");
        locators.Add("d.terabox.com");

        var hosts = new List<string>();
        foreach (var locator in locators.Distinct())
        {
            try
            {
                using var req = NewRequest(HttpMethod.Get, $"https://{locator}/rest/2.0/pcs/file?method=locateupload");
                using var resp = await SendRawAsync(req, ct).ConfigureAwait(false);
                var json = JsonNode.Parse(await resp.Content.ReadUtf8Async(ct).ConfigureAwait(false));
                var host = json?["host"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(host) || !IsTeraBoxHost(host)) continue;
                AddHost(hosts, host, root);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or CloudException)
            {
                // DNS name not used by this cluster, or no answer: try the next locator.
            }
        }
        AddHost(hosts, $"c-{prefix}.{root}", root);
        AddHost(hosts, $"c-all.{root}", root);
        AddHost(hosts, "c-all.terabox.com", root);
        lock (_mapLock) _uploadHosts ??= hosts;
        return _uploadHosts;
    }

    /// <summary>Adds an upload host; a host on another TeraBox domain is tried on the account's own domain first.</summary>
    private static void AddHost(List<string> hosts, string host, string root)
    {
        var labels = host.Split('.');
        if (labels.Length >= 3)
        {
            var sameDomain = $"{string.Join('.', labels[..^2])}.{root}";
            if (!hosts.Contains(sameDomain, StringComparer.OrdinalIgnoreCase)) hosts.Add(sameDomain);
        }
        if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) hosts.Add(host);
    }

    private void PreferUploadHost(string host)
    {
        lock (_mapLock)
        {
            if (_uploadHosts is null || _uploadHosts[0] == host) return;
            var list = _uploadHosts.ToList();
            list.Remove(host);
            list.Insert(0, host);
            _uploadHosts = list;
        }
    }

    /// <summary>Part size like the TeraBox web client / AList: 4 MiB up to 4 GiB, doubled for every doubling above.</summary>
    internal static int ChunkSizeFor(long? size)
    {
        long chunk = BlockSize, threshold = 4L << 30;
        while (size is { } s && s > threshold && chunk < (256 << 20))
        {
            chunk <<= 1;
            threshold <<= 1;
        }
        return (int)chunk;
    }

    // Placeholder block list the TeraBox web client sends to precreate a multi-part upload (as in AList). With a single
    // entry TeraBox prepares a one-part upload and /api/create later fails with errno 2 "param err".
    private const string MultiPartPrecreateBlocks = "[\"5910a591dd8fc18c32a8f3df4fdc1761\",\"a5fc157d78e6ad1c7e114b056c92821e\"]";

    internal async Task<string> PrecreateAsync(string path, string dir, string firstBlockMd5, bool multiPart, CancellationToken ct)
    {
        var json = await ApiAsync(HttpMethod.Post, "/api/precreate", new(), new()
        {
            ["path"] = path,
            ["autoinit"] = "1",
            ["target_path"] = dir,
            ["block_list"] = multiPart ? MultiPartPrecreateBlocks : new JsonArray(JsonValue.Create(firstBlockMd5)).ToJsonString(),
            ["local_mtime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["file_limit_switch_v34"] = "true",
        }, ct).ConfigureAwait(false);
        return json["uploadid"]?.GetValue<string>() ?? throw new CloudException(CloudErrorKind.Unknown, "TeraBox precreate returned no uploadid.");
    }

    internal async Task UploadPartAsync(string path, string uploadId, int partSeq, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await BandwidthThrottle.Upload.WaitAsync(data.Length, ct).ConfigureAwait(false);
        var hosts = await UploadHostsAsync(ct).ConfigureAwait(false);
        CloudException? wrongCluster = null;
        foreach (var host in hosts)
        {
            try
            {
                await UploadPartToAsync(host, path, uploadId, partSeq, data, ct).ConfigureAwait(false);
                PreferUploadHost(host);
                return;
            }
            catch (CloudException ex) when (IsWrongCluster(ex))
            {
                wrongCluster = ex; // this server does not know the account: try the next one
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.NameResolutionError)
            {
                // host name not used by this domain
            }
        }
        throw new CloudException(CloudErrorKind.Unauthorized,
            "No TeraBox upload server accepted this session (" + (wrongCluster?.Message ?? "no upload server reachable") +
            "). Remove the TeraBox account in XCloudSSD and add it again (sign in once more).");
    }

    private static bool IsWrongCluster(CloudException ex) =>
        ex.Message.Contains("31045", StringComparison.Ordinal) || ex.Message.Contains("user not exists", StringComparison.OrdinalIgnoreCase);

    private async Task UploadPartToAsync(string host, string path, string uploadId, int partSeq, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await Retry.RunAsync(async _ =>
        {
            var q = Query(new()
            {
                ["method"] = "upload",
                ["path"] = path,
                ["uploadid"] = uploadId,
                ["partseq"] = partSeq.ToString(CultureInfo.InvariantCulture),
            });
            using var req = NewRequest(HttpMethod.Post, $"https://{host}/rest/2.0/pcs/superfile2?{q}");
            req.Headers.TryAddWithoutValidation("Origin", _baseUrl);
            var form = new MultipartFormDataContent();
            var file = new ReadOnlyMemoryContent(data);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", "blob");
            req.Content = form;
            using var resp = await SendRawAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadUtf8Async(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new CloudException(Retry.KindForStatus(resp.StatusCode), $"TeraBox part upload failed ({host}): {(int)resp.StatusCode} {Trim(body)}", (int)resp.StatusCode);
            var errno = TryErrno(body);
            if (errno is { } e && e != 0)
            {
                if (e == 31045) throw new CloudException(CloudErrorKind.Forbidden, $"TeraBox part upload failed ({host}): errno 31045 user not exists");
                throw new CloudException(CloudErrorKind.Transient, $"TeraBox part upload errno {e}: {Trim(body)}");
            }
            _metrics.AddUp(data.Length);
        }, 5, ct).ConfigureAwait(false);
    }

    internal async Task<CloudItem> CreateFileAsync(string parentId, string name, string path, string dir, long size,
        string uploadId, IReadOnlyList<string> blockList, bool overwrite, CancellationToken ct)
    {
        await ApiAsync(HttpMethod.Post, "/api/create", new() { ["isdir"] = "0", ["rtype"] = overwrite ? "3" : "1" }, new()
        {
            ["path"] = path,
            ["size"] = size.ToString(CultureInfo.InvariantCulture),
            ["uploadid"] = uploadId,
            ["target_path"] = dir,
            ["block_list"] = new JsonArray(blockList.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()).ToJsonString(),
            ["local_mtime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);
        lock (_links)
            foreach (var k in _links.Keys.ToList()) _links.Remove(k);
        return await FindChildAsync(parentId, name, ct).ConfigureAwait(false);
    }

    internal string PathForNew(string parentId, string name) => Combine(PathOf(parentId), name);
    internal string DirOf(string parentId) => PathOf(parentId);

    // ------------------------------------------------------------------ http

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Cookie", _cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Referer", _baseUrl + "/");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        return req;
    }

    private string Query(Dictionary<string, string> extra)
    {
        var all = new Dictionary<string, string>
        {
            ["app_id"] = AppId,
            ["web"] = "1",
            ["channel"] = "dubox",
            ["clienttype"] = "0",
            ["jsToken"] = _jsToken ?? "",
        };
        foreach (var (k, v) in extra) all[k] = v;
        return string.Join("&", all.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    private async Task<JsonObject> ApiAsync(HttpMethod method, string path, Dictionary<string, string> query,
        Dictionary<string, string>? form, CancellationToken ct)
    {
        if (_jsToken is null) await RefreshJsTokenAsync(expired: false, ct).ConfigureAwait(false);
        return await Retry.RunAsync(async _ =>
        {
            for (var hop = 0; ; hop++)
            {
                using var req = NewRequest(method, $"{_baseUrl}{path}?{Query(query)}");
                if (form is not null) req.Content = new FormUrlEncodedContent(form);
                using var resp = await SendRawAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadUtf8Async(ct).ConfigureAwait(false);
                if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new CloudException(Retry.KindForStatus(resp.StatusCode), $"TeraBox {path}: HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
                JsonObject json;
                try
                {
                    json = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
                }
                catch (JsonException)
                {
                    throw new CloudException(CloudErrorKind.Unauthorized,
                        $"TeraBox {path} did not return data (HTTP {(int)resp.StatusCode}); the session is probably signed out.", (int)resp.StatusCode);
                }
                var errno = json["errno"]?.GetValue<int>() ?? 0;
                if (errno == 0) return json;
                if (errno == 4000023 && hop < 1)
                {
                    await RefreshJsTokenAsync(expired: true, ct).ConfigureAwait(false);
                    continue;
                }
                if (errno == -6 && hop < 2 && resp.Headers.TryGetValues("Url-Domain-Prefix", out var prefixes) &&
                    prefixes.FirstOrDefault() is { Length: > 0 } prefix)
                {
                    _baseUrl = $"https://{prefix}.terabox.com";
                    continue;
                }
                throw MapErrno(errno, path, text);
            }
        }, 4, ct).ConfigureAwait(false);
    }

    private static CloudException MapErrno(int errno, string path, string body)
    {
        var kind = errno switch
        {
            -6 or 4000023 or 9019 or 400210 => CloudErrorKind.Unauthorized,
            9000 => CloudErrorKind.Forbidden,
            -9 or 31066 => CloudErrorKind.NotFound,
            -8 or 31061 => CloudErrorKind.AlreadyExists,
            -10 or 31112 or 58 => CloudErrorKind.QuotaExceeded,
            31034 or 9013 => CloudErrorKind.RateLimited,
            _ => CloudErrorKind.Unknown,
        };
        var hint = errno switch
        {
            9000 => " (TeraBox is not available in this region)",
            58 => " (file is larger than this TeraBox account allows per file — free accounts: 4 GB)",
            _ => kind == CloudErrorKind.Unauthorized ? " (sign in again)" : "",
        };
        return new CloudException(kind, $"TeraBox {path}: errno {errno}{hint}. {Trim(body)}");
    }

    private static int? TryErrno(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["errno"]?.GetValue<int>() ?? n?["error_code"]?.GetValue<int>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage req, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var scope = _metrics.BeginRequest();
        try
        {
            var resp = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
            scope.ResponseReceived();
            if ((int)resp.StatusCode >= 400) scope.Fail();
            return resp;
        }
        catch (Exception ex) when (ex is not CloudException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            scope.Fail();
            throw Retry.Classify(ex, ct);
        }
        finally
        {
            scope.Dispose();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        (_sparse as IDisposable)?.Dispose();
    }
}

/// <summary>
/// Streams a file to TeraBox in 4 MiB parts: precreate when the first part is ready, superfile2 per part
/// (in order), create with the list of part MD5s at the end. Only one part is held at a time.
/// </summary>
internal sealed class TeraBoxUploadSession(TeraBoxWebProvider owner, string parentId, string name, bool overwrite,
    int chunkSize = TeraBoxWebProvider.BlockSize) : IUploadSession
{
    private readonly List<string> _blocks = new();
    private string? _uploadId;
    private long _committed;
    private string? _path;
    private string? _dir;

    public long Committed => _committed;
    public int Granularity => chunkSize;

    public async Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // Non-final data: more parts always follow.
        for (var off = 0; off < data.Length; off += chunkSize)
            await SendBlockAsync(data.Slice(off, Math.Min(chunkSize, data.Length - off)), multiPart: true, ct).ConfigureAwait(false);
    }

    public async Task<CloudItem> CompleteAsync(ReadOnlyMemory<byte> finalData, long totalSize, CancellationToken ct)
    {
        var multiPart = totalSize > chunkSize;
        for (var off = 0; off < finalData.Length; off += chunkSize)
            await SendBlockAsync(finalData.Slice(off, Math.Min(chunkSize, finalData.Length - off)), multiPart, ct).ConfigureAwait(false);
        if (_blocks.Count == 0) await SendBlockAsync(ReadOnlyMemory<byte>.Empty, false, ct).ConfigureAwait(false);
        if (_committed != totalSize) throw new CloudException(CloudErrorKind.IntegrityMismatch, $"TeraBox upload size mismatch: sent {_committed}, expected {totalSize}.");
        return await owner.CreateFileAsync(parentId, name, _path!, _dir!, totalSize, _uploadId!, _blocks, overwrite, ct).ConfigureAwait(false);
    }

    private async Task SendBlockAsync(ReadOnlyMemory<byte> block, bool multiPart, CancellationToken ct)
    {
        var md5 = Convert.ToHexStringLower(MD5.HashData(block.Span));
        if (_uploadId is null)
        {
            _path = owner.PathForNew(parentId, name);
            _dir = owner.DirOf(parentId);
            _uploadId = await owner.PrecreateAsync(_path, _dir, md5, multiPart, ct).ConfigureAwait(false);
        }
        await owner.UploadPartAsync(_path!, _uploadId, _blocks.Count, block, ct).ConfigureAwait(false);
        _blocks.Add(md5);
        _committed += block.Length;
    }

    public Task AbortAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
