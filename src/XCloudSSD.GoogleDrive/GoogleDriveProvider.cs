using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XCloudSSD.Cloud;
using XCloudSSD.Core;

namespace XCloudSSD.GoogleDrive;

/// <summary>
/// Google Drive API v3 adapter (official REST API, https://developers.google.com/drive/api/reference/rest/v3).
/// Reads are HTTP range requests, writes are resumable uploads; nothing is staged on disk.
/// </summary>
public sealed class GoogleDriveProvider : ICloudProvider, IRootOverride
{
    public const string ApiBase = "https://www.googleapis.com/drive/v3/";
    public const string UploadBase = "https://www.googleapis.com/upload/drive/v3/files";
    public const string FolderMime = "application/vnd.google-apps.folder";
    private const string AttrKey = "xcs_attr";
    private const string SizeKey = "xcs_size";
    internal const string FileFields = "id,name,mimeType,size,createdTime,modifiedTime,md5Checksum,appProperties,parents,trashed";

    private readonly HttpClient _http;
    private readonly Func<bool, CancellationToken, Task<string>> _getToken;
    private readonly Metrics _metrics;

    public GoogleDriveProvider(HttpClient http, Func<bool, CancellationToken, Task<string>> getAccessToken, Metrics metrics)
    {
        _http = http;
        _getToken = getAccessToken;
        _metrics = metrics;
    }

    public ProviderKind Kind => ProviderKind.GoogleDrive;
    /// <summary>Real id of "My Drive" (the "root" alias is resolved so change notifications match).</summary>
    public string RootId { get; private set; } = "root";

    public void UseRootId(string rootId) => RootId = rootId;

    public async Task InitializeAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("files/root?fields=id", ct).ConfigureAwait(false);
        RootId = json["id"]!.GetValue<string>();
    }

    public ProviderCapabilities Capabilities { get; } = new(
        CanMount: true, RangeRead: true, ResumableUpload: true, Rename: true, Move: true, Trash: true,
        QuotaInfo: true, ChangeFeed: true, CustomProperties: true,
        Notes: "Google Drive API v3. Files are whole objects: changing part of an existing file re-uploads the whole file. Google Docs/Sheets/Slides are hidden (no binary content).");

    // ---------------------------------------------------------------- identity / quota

    public async Task<AccountIdentity> GetIdentityAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("about?fields=user(displayName,emailAddress)", ct).ConfigureAwait(false);
        var user = json["user"];
        return new AccountIdentity(user?["displayName"]?.GetValue<string>() ?? "", user?["emailAddress"]?.GetValue<string>() ?? "");
    }

    public async Task<QuotaInfo> GetQuotaAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("about?fields=storageQuota(limit,usage)", ct).ConfigureAwait(false);
        var q = json["storageQuota"];
        long? limit = q?["limit"] is { } l ? long.Parse(l.GetValue<string>(), CultureInfo.InvariantCulture) : null;
        var usage = q?["usage"] is { } u ? long.Parse(u.GetValue<string>(), CultureInfo.InvariantCulture) : 0;
        return new QuotaInfo(limit, usage);
    }

    // ---------------------------------------------------------------- listing

    public async Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string folderId, CancellationToken ct)
    {
        var q = $"'{EscapeQ(folderId)}' in parents and trashed = false";
        return await ListAsync(q, int.MaxValue, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudItem>> SearchAsync(string text, int max, CancellationToken ct)
    {
        var q = $"name contains '{EscapeQ(text)}' and trashed = false";
        return await ListAsync(q, max, ct).ConfigureAwait(false);
    }

    private async Task<List<CloudItem>> ListAsync(string q, int max, CancellationToken ct)
    {
        var result = new List<CloudItem>();
        string? page = null;
        do
        {
            var url = $"files?q={Uri.EscapeDataString(q)}&pageSize=1000&spaces=drive" +
                      $"&fields={Uri.EscapeDataString($"nextPageToken,files({FileFields})")}" +
                      (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (json["files"] is JsonArray files)
                foreach (var f in files)
                    if (f is JsonObject o) result.Add(ParseItem(o));
            page = json["nextPageToken"]?.GetValue<string>();
        } while (page is not null && result.Count < max);
        return result;
    }

    public async Task<CloudItem?> GetItemAsync(string id, CancellationToken ct)
    {
        try
        {
            var json = await GetJsonAsync($"files/{Uri.EscapeDataString(id)}?fields={Uri.EscapeDataString(FileFields)}", ct).ConfigureAwait(false);
            var item = ParseItem(json);
            return json["trashed"]?.GetValue<bool>() == true ? null : item;
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- read

    public async Task<int> ReadRangeAsync(string fileId, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length == 0) return 0;
        await BandwidthThrottle.Download.WaitAsync(buffer.Length, ct).ConfigureAwait(false);
        return await Retry.RunAsync(async attempt =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}files/{Uri.EscapeDataString(fileId)}?alt=media");
            req.Headers.Range = new RangeHeaderValue(offset, offset + buffer.Length - 1);
            using var resp = await SendAsync(req, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return 0;
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var skip = resp.StatusCode == HttpStatusCode.OK ? offset : 0; // server ignored the Range header
            var total = 0;
            var scratch = skip > 0 ? new byte[81920] : null;
            while (skip > 0)
            {
                var n = await stream.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch!.Length, skip)), ct).ConfigureAwait(false);
                if (n == 0) return 0;
                skip -= n;
            }
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
                _metrics.AddDown(n);
            }
            return total;
        }, 5, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- create / update / delete

    public async Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["mimeType"] = FolderMime,
            ["parents"] = new JsonArray(parentId),
        };
        var json = await SendJsonAsync(HttpMethod.Post, $"files?fields={Uri.EscapeDataString(FileFields)}", body, ct).ConfigureAwait(false);
        return ParseItem(json);
    }

    public async Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, uint? windowsAttributes, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["mimeType"] = "application/octet-stream",
            ["parents"] = new JsonArray(parentId),
        };
        if (windowsAttributes is { } a) body["appProperties"] = new JsonObject { [AttrKey] = a.ToString(CultureInfo.InvariantCulture) };
        var json = await SendJsonAsync(HttpMethod.Post, $"files?fields={Uri.EscapeDataString(FileFields)}", body, ct).ConfigureAwait(false);
        return ParseItem(json);
    }

    public async Task<CloudItem> UpdateMetadataAsync(string id, MetadataUpdate u, CancellationToken ct)
    {
        var body = new JsonObject();
        if (u.NewName is not null) body["name"] = u.NewName;
        if (u.WindowsAttributes is not null || u.LogicalSize is not null)
        {
            // appProperties are merged by Drive: only the keys sent here change.
            var props = new JsonObject();
            if (u.WindowsAttributes is { } a) props[AttrKey] = a.ToString(CultureInfo.InvariantCulture);
            if (u.LogicalSize is { } l) props[SizeKey] = l.ToString(CultureInfo.InvariantCulture);
            body["appProperties"] = props;
        }
        if (u.Modified is { } m) body["modifiedTime"] = m.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var url = new StringBuilder($"files/{Uri.EscapeDataString(id)}?fields={Uri.EscapeDataString(FileFields)}");
        if (u.AddParentId is not null) url.Append("&addParents=").Append(Uri.EscapeDataString(u.AddParentId));
        if (u.RemoveParentId is not null) url.Append("&removeParents=").Append(Uri.EscapeDataString(u.RemoveParentId));
        var json = await SendJsonAsync(HttpMethod.Patch, url.ToString(), body, ct).ConfigureAwait(false);
        return ParseItem(json);
    }

    public async Task DeleteAsync(string id, bool permanent, CancellationToken ct)
    {
        try
        {
            if (permanent)
            {
                await Retry.RunAsync(async _ =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBase}files/{Uri.EscapeDataString(id)}");
                    using var resp = await SendAsync(req, ct).ConfigureAwait(false);
                    await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
                }, 4, ct).ConfigureAwait(false);
            }
            else
            {
                await SendJsonAsync(HttpMethod.Patch, $"files/{Uri.EscapeDataString(id)}?fields=id", new JsonObject { ["trashed"] = true }, ct).ConfigureAwait(false);
            }
        }
        catch (CloudException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            // Already gone.
        }
    }

    // ---------------------------------------------------------------- upload

    public async Task<IUploadSession> BeginUploadAsync(UploadTarget target, CancellationToken ct)
    {
        var meta = new JsonObject();
        if (target.WindowsAttributes is { } a) meta["appProperties"] = new JsonObject { [AttrKey] = a.ToString(CultureInfo.InvariantCulture) };
        if (target.Modified is { } m) meta["modifiedTime"] = m.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        HttpMethod method;
        string url;
        if (target.ExistingFileId is { } existing)
        {
            method = HttpMethod.Patch;
            url = $"{UploadBase}/{Uri.EscapeDataString(existing)}?uploadType=resumable&fields={Uri.EscapeDataString(FileFields)}";
        }
        else
        {
            method = HttpMethod.Post;
            url = $"{UploadBase}?uploadType=resumable&fields={Uri.EscapeDataString(FileFields)}";
            meta["name"] = target.Name ?? throw new ArgumentException("Name required for new file.");
            meta["parents"] = new JsonArray(target.ParentId ?? throw new ArgumentException("Parent required for new file."));
        }

        var location = await Retry.RunAsync(async _ =>
        {
            using var req = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
            return resp.Headers.Location ?? throw new CloudException(CloudErrorKind.Unknown, "Drive did not return an upload session.");
        }, 5, ct).ConfigureAwait(false);

        return new DriveUploadSession(this, location);
    }

    internal async Task<HttpResponseMessage> PutChunkAsync(Uri session, ReadOnlyMemory<byte> data, string contentRange, CancellationToken ct)
    {
        await BandwidthThrottle.Upload.WaitAsync(data.Length, ct).ConfigureAwait(false);
        var req = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ReadOnlyMemoryContent(data) };
        req.Content.Headers.ContentLength = data.Length;
        req.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        try
        {
            // Session URIs carry their own authorization; a bearer token is still accepted and harmless.
            var resp = await SendAsync(req, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is 200 or 201 or 308) _metrics.AddUp(data.Length);
            return resp;
        }
        finally
        {
            req.Dispose();
        }
    }

    internal static CloudItem ParseItemFromJson(string json) => ParseItem(JsonNode.Parse(json)!.AsObject());

    internal Task EnsureSuccessPublicAsync(HttpResponseMessage r, CancellationToken ct) => EnsureSuccessAsync(r, ct);

    // ---------------------------------------------------------------- changes

    public async Task<string> GetChangeTokenAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("changes/startPageToken", ct).ConfigureAwait(false);
        return json["startPageToken"]!.GetValue<string>();
    }

    public async Task<ChangeSet> GetChangesAsync(string token, CancellationToken ct)
    {
        var list = new List<ChangedItem>();
        var page = token;
        while (true)
        {
            var url = $"changes?pageToken={Uri.EscapeDataString(page)}&pageSize=1000&spaces=drive&includeRemoved=true" +
                      $"&fields={Uri.EscapeDataString($"nextPageToken,newStartPageToken,changes(fileId,removed,file({FileFields}))")}";
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (json["changes"] is JsonArray changes)
            {
                foreach (var c in changes)
                {
                    if (c is not JsonObject o) continue;
                    var id = o["fileId"]?.GetValue<string>();
                    if (id is null) continue;
                    var removed = o["removed"]?.GetValue<bool>() == true;
                    CloudItem? item = null;
                    if (!removed && o["file"] is JsonObject f)
                    {
                        if (f["trashed"]?.GetValue<bool>() == true) removed = true;
                        else item = ParseItem(f);
                    }
                    list.Add(new ChangedItem(id, removed, item));
                }
            }
            if (json["newStartPageToken"]?.GetValue<string>() is { } next) return new ChangeSet(next, list);
            page = json["nextPageToken"]?.GetValue<string>() ?? throw new CloudException(CloudErrorKind.Unknown, "Change feed returned no token.");
        }
    }

    // ---------------------------------------------------------------- http helpers

    private async Task<JsonObject> GetJsonAsync(string relative, CancellationToken ct)
        => await Retry.RunAsync(async _ =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + relative);
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadUtf8Async(ct).ConfigureAwait(false);
            return JsonNode.Parse(text)!.AsObject();
        }, 5, ct).ConfigureAwait(false);

    private async Task<JsonObject> SendJsonAsync(HttpMethod method, string relative, JsonObject body, CancellationToken ct)
        => await Retry.RunAsync(async _ =>
        {
            using var req = new HttpRequestMessage(method, ApiBase + relative)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadUtf8Async(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
        }, 5, ct).ConfigureAwait(false);

    /// <summary>Sends with a bearer token; on 401 refreshes the token once and retries.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var scope = _metrics.BeginRequest();
        try
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _getToken(false, ct).ConfigureAwait(false));
            var resp = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
            scope.ResponseReceived();
            if (resp.StatusCode == HttpStatusCode.Unauthorized && req.Content is null or StringContent)
            {
                resp.Dispose();
                using var again = await CloneAsync(req).ConfigureAwait(false);
                again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _getToken(true, ct).ConfigureAwait(false));
                resp = await _http.SendAsync(again, completion, ct).ConfigureAwait(false);
            }
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 308 && (int)resp.StatusCode != 416) scope.Fail();
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

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        foreach (var h in req.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (req.Content is not null)
        {
            var bytes = await req.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in req.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return clone;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string? reason = null, message = null;
        try
        {
            var text = await resp.Content.ReadUtf8Async(ct).ConfigureAwait(false);
            var err = JsonNode.Parse(text)?["error"];
            message = err?["message"]?.GetValue<string>();
            reason = err?["errors"]?[0]?["reason"]?.GetValue<string>();
        }
        catch (Exception)
        {
            // Non-JSON error body.
        }
        var kind = Retry.KindForStatus(resp.StatusCode, reason);
        throw new CloudException(kind, $"Google Drive: {(int)resp.StatusCode} {reason ?? resp.ReasonPhrase} {message}".Trim(), (int)resp.StatusCode);
    }

    private static string EscapeQ(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static CloudItem ParseItem(JsonObject o)
    {
        var mime = o["mimeType"]?.GetValue<string>() ?? "";
        var isFolder = mime == FolderMime;
        uint? attrs = null;
        if (o["appProperties"]?[AttrKey]?.GetValue<string>() is { } a &&
            uint.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            attrs = parsed;
        long? logical = null;
        if (o["appProperties"]?[SizeKey]?.GetValue<string>() is { } ls &&
            long.TryParse(ls, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv))
            logical = lv;
        var stored = o["size"] is { } sz ? long.Parse(sz.GetValue<string>(), CultureInfo.InvariantCulture) : 0;
        var parents = o["parents"] is JsonArray p ? p.Select(x => x!.GetValue<string>()).ToList() : new List<string>();
        return new CloudItem
        {
            Id = o["id"]!.GetValue<string>(),
            Name = o["name"]?.GetValue<string>() ?? "",
            IsFolder = isFolder,
            // Sparse files: Windows sees the recorded logical size, only `stored` bytes exist in the cloud.
            Size = logical is { } lsz && lsz > stored ? lsz : stored,
            StoredSize = stored,
            Created = ParseTime(o["createdTime"]),
            Modified = ParseTime(o["modifiedTime"]),
            Md5 = o["md5Checksum"]?.GetValue<string>(),
            WindowsAttributes = attrs,
            Parents = parents,
            IsVirtualDocument = !isFolder && mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal),
        };
    }

    private static DateTimeOffset ParseTime(JsonNode? n)
        => n is null ? DateTimeOffset.UnixEpoch
            : DateTimeOffset.Parse(n.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public void Dispose()
    {
    }
}
