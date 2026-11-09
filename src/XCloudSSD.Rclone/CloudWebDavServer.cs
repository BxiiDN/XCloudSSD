using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using XCloudSSD.Cloud;
using XCloudSSD.Core;

namespace XCloudSSD.Rclone;

/// <summary>
/// Minimal WebDAV server (RFC 4918 subset used by rclone's "webdav" backend) on 127.0.0.1 that exposes one
/// cloud account through its <see cref="ICloudProvider"/>. This is how accounts that rclone has no backend for
/// (TeraBox) are mounted by rclone: rclone mount → webdav → this bridge → TeraBox.
/// Only reachable from this PC and protected with a random user/password generated per start.
/// Supported: OPTIONS, PROPFIND (depth 0/1, RFC 4331 quota), GET/HEAD with Range, PUT (streamed straight
/// into the provider's chunked upload), MKCOL, DELETE, MOVE.
/// </summary>
public sealed class CloudWebDavServer : IDisposable
{
    private const int ReadBlock = 8 << 20;
    private readonly ICloudProvider _provider;
    private readonly ILogger _log;
    private readonly bool _permanentDelete;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (List<CloudItem> Items, DateTime Expires)> _children = new();
    private readonly TimeSpan _listTtl;
    private (QuotaInfo Quota, DateTime At)? _quota;
    private readonly string _expectedAuth;

    public CloudWebDavServer(ICloudProvider provider, ILogger log, bool permanentDelete = false, TimeSpan? listCacheTtl = null,
        string? password = null, int preferredPort = 0)
    {
        _provider = provider;
        _log = log;
        _permanentDelete = permanentDelete;
        _listTtl = listCacheTtl ?? TimeSpan.FromSeconds(10);
        User = "xcs";
        Password = string.IsNullOrEmpty(password) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) : password;
        _expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        Port = preferredPort > 0 && IsFree(preferredPort) ? preferredPort : FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public int Port { get; }
    public string User { get; }
    public string Password { get; }
    public string Url => $"http://127.0.0.1:{Port}/";

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private static bool IsFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException ex)
            {
                _log.LogWarning("WebDAV accept failed: {Message}", ex.Message);
                continue;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    // ------------------------------------------------------------------ dispatch

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        var ct = _cts.Token;
        try
        {
            if (!string.Equals(req.Headers["Authorization"], _expectedAuth, StringComparison.Ordinal))
            {
                resp.StatusCode = 401;
                resp.AddHeader("WWW-Authenticate", "Basic realm=\"XCloudSSD\"");
                return;
            }
            var path = PathOf(req.Url!);
            switch (req.HttpMethod.ToUpperInvariant())
            {
                case "OPTIONS":
                    resp.AddHeader("DAV", "1");
                    resp.AddHeader("Allow", "OPTIONS, PROPFIND, GET, HEAD, PUT, MKCOL, DELETE, MOVE");
                    resp.StatusCode = 200;
                    break;
                case "PROPFIND": await PropFindAsync(path, req, resp, ct).ConfigureAwait(false); break;
                case "GET": await GetAsync(path, req, resp, headOnly: false, ct).ConfigureAwait(false); break;
                case "HEAD": await GetAsync(path, req, resp, headOnly: true, ct).ConfigureAwait(false); break;
                case "PUT": await PutAsync(path, req, resp, ct).ConfigureAwait(false); break;
                case "MKCOL": await MkColAsync(path, resp, ct).ConfigureAwait(false); break;
                case "DELETE": await DeleteAsync(path, resp, ct).ConfigureAwait(false); break;
                case "MOVE": await MoveAsync(path, req, resp, ct).ConfigureAwait(false); break;
                case "PROPPATCH":
                    // Times/attributes cannot be stored on every provider; accept and ignore.
                    await WriteXmlAsync(resp, 207, w => WritePropResponse(w, req.Url!.AbsolutePath, null, null)).ConfigureAwait(false);
                    break;
                default:
                    resp.StatusCode = 501;
                    break;
            }
        }
        catch (Exception ex)
        {
            var status = StatusFor(ex);
            if (status >= 500) _log.LogWarning("WebDAV {Method} {Path} failed: {Message}", req.HttpMethod, req.Url?.AbsolutePath, ex.Message);
            try
            {
                resp.StatusCode = status;
                var body = Encoding.UTF8.GetBytes(ex.Message);
                resp.ContentType = "text/plain; charset=utf-8";
                resp.ContentLength64 = body.Length;
                await resp.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Headers already sent or client gone.
            }
        }
        finally
        {
            try
            {
                resp.Close();
            }
            catch (Exception)
            {
                // client gone
            }
        }
    }

    internal static int StatusFor(Exception ex) => ex switch
    {
        CloudException { Kind: CloudErrorKind.NotFound } => 404,
        CloudException { Kind: CloudErrorKind.AlreadyExists or CloudErrorKind.Conflict } => 409,
        CloudException { Kind: CloudErrorKind.QuotaExceeded } => 507,
        CloudException { Kind: CloudErrorKind.Forbidden or CloudErrorKind.Unauthorized } => 403,
        CloudException { Kind: CloudErrorKind.NotSupported } => 501,
        CloudException { Kind: CloudErrorKind.RateLimited } => 429,
        CloudException c when c.IsRetryable => 503,
        HttpRequestException or IOException or TaskCanceledException => 503,
        DavException d => d.Status,
        _ => 500,
    };

    private sealed class DavException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    // ------------------------------------------------------------------ paths

    /// <summary>Decoded path segments of a request URL ("/a/b c/" → ["a", "b c"]).</summary>
    internal static string[] PathOf(Uri url) =>
        url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

    internal static string Href(IEnumerable<string> segments, bool folder)
    {
        var s = "/" + string.Join('/', segments.Select(Uri.EscapeDataString));
        return folder && s != "/" ? s + "/" : s;
    }

    private async Task<List<CloudItem>> ChildrenAsync(string folderId, CancellationToken ct)
    {
        lock (_cacheLock)
            if (_children.TryGetValue(folderId, out var c) && c.Expires > DateTime.UtcNow) return c.Items;
        var items = (await _provider.ListChildrenAsync(folderId, ct).ConfigureAwait(false))
            .Where(i => !i.IsVirtualDocument).ToList();
        lock (_cacheLock) _children[folderId] = (items, DateTime.UtcNow + _listTtl);
        return items;
    }

    private void Invalidate(string folderId)
    {
        lock (_cacheLock) _children.Remove(folderId);
    }

    private CloudItem RootItem() => new() { Id = _provider.RootId, Name = "", IsFolder = true, Modified = DateTimeOffset.UtcNow };

    /// <summary>Resolves a path to an item; null when the last segment does not exist (parent must exist).</summary>
    private async Task<(CloudItem? Item, CloudItem Parent)> ResolveAsync(string[] segs, CancellationToken ct)
    {
        var current = RootItem();
        if (segs.Length == 0) return (current, current);
        for (var i = 0; i < segs.Length; i++)
        {
            var parent = current;
            var kids = await ChildrenAsync(parent.Id, ct).ConfigureAwait(false);
            var next = kids.FirstOrDefault(k => k.Name == segs[i]);
            if (i == segs.Length - 1) return (next, parent);
            if (next is null || !next.IsFolder) throw new DavException(409, "Parent folder does not exist.");
            current = next;
        }
        throw new InvalidOperationException();
    }

    // ------------------------------------------------------------------ PROPFIND

    private async Task PropFindAsync(string[] segs, HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
    {
        if (req.HasEntityBody)
        {
            using var sink = new MemoryStream();
            await req.InputStream.CopyToAsync(sink, ct).ConfigureAwait(false);
        }
        CloudItem? item;
        try
        {
            (item, _) = await ResolveAsync(segs, ct).ConfigureAwait(false);
        }
        catch (DavException)
        {
            item = null;
        }
        if (item is null)
        {
            resp.StatusCode = 404;
            return;
        }
        var depth = req.Headers["Depth"] ?? "1";
        var children = item.IsFolder && depth != "0" ? await ChildrenAsync(item.Id, ct).ConfigureAwait(false) : null;
        QuotaInfo? quota = null;
        if (segs.Length == 0) quota = await QuotaAsync(ct).ConfigureAwait(false);

        await WriteXmlAsync(resp, 207, w =>
        {
            WritePropResponse(w, Href(segs, item.IsFolder), item, quota);
            foreach (var c in children ?? [])
                WritePropResponse(w, Href(segs.Append(c.Name), c.IsFolder), c, null);
        }).ConfigureAwait(false);
    }

    private async Task<QuotaInfo?> QuotaAsync(CancellationToken ct)
    {
        if (_quota is { } q && DateTime.UtcNow - q.At < TimeSpan.FromSeconds(60)) return q.Quota;
        try
        {
            var fresh = await _provider.GetQuotaAsync(ct).ConfigureAwait(false);
            _quota = (fresh, DateTime.UtcNow);
            return fresh;
        }
        catch (CloudException)
        {
            return _quota?.Quota;
        }
    }

    private static void WritePropResponse(XmlWriter w, string href, CloudItem? item, QuotaInfo? quota)
    {
        w.WriteStartElement("D", "response", "DAV:");
        w.WriteElementString("D", "href", "DAV:", href);
        w.WriteStartElement("D", "propstat", "DAV:");
        w.WriteStartElement("D", "prop", "DAV:");
        if (item is not null)
        {
            w.WriteElementString("D", "displayname", "DAV:", item.Name);
            w.WriteStartElement("D", "resourcetype", "DAV:");
            if (item.IsFolder) w.WriteElementString("D", "collection", "DAV:", "");
            w.WriteEndElement();
            if (!item.IsFolder)
            {
                // Bytes really stored and readable (no sparse tail when served to rclone).
                w.WriteElementString("D", "getcontentlength", "DAV:", item.ContentLength.ToString(CultureInfo.InvariantCulture));
                w.WriteElementString("D", "getcontenttype", "DAV:", "application/octet-stream");
                if (item.Md5 is { Length: 32 } md5) w.WriteElementString("D", "getetag", "DAV:", $"\"{md5}\"");
            }
            var modified = item.Modified == default ? DateTimeOffset.UtcNow : item.Modified;
            w.WriteElementString("D", "getlastmodified", "DAV:", modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
            if (item.Created != default)
                w.WriteElementString("D", "creationdate", "DAV:", item.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
        if (quota is not null)
        {
            if (quota.FreeBytes is { } free) w.WriteElementString("D", "quota-available-bytes", "DAV:", free.ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("D", "quota-used-bytes", "DAV:", quota.UsedBytes.ToString(CultureInfo.InvariantCulture));
        }
        w.WriteEndElement(); // prop
        w.WriteElementString("D", "status", "DAV:", "HTTP/1.1 200 OK");
        w.WriteEndElement(); // propstat
        w.WriteEndElement(); // response
    }

    private static async Task WriteXmlAsync(HttpListenerResponse resp, int status, Action<XmlWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("D", "multistatus", "DAV:");
            body(w);
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        resp.StatusCode = status;
        resp.ContentType = "application/xml; charset=utf-8";
        resp.ContentLength64 = ms.Length;
        ms.Position = 0;
        await ms.CopyToAsync(resp.OutputStream).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ GET / HEAD

    private async Task GetAsync(string[] segs, HttpListenerRequest req, HttpListenerResponse resp, bool headOnly, CancellationToken ct)
    {
        var (item, _) = await ResolveAsync(segs, ct).ConfigureAwait(false);
        if (item is null) throw new DavException(404, "Not found.");
        if (item.IsFolder) throw new DavException(405, "Is a folder.");
        var size = item.ContentLength;
        long start = 0, end = size - 1;
        var partial = false;
        if (req.Headers["Range"] is { } range && TryParseRange(range, size, out var rs, out var re))
        {
            if (rs >= size && size > 0)
            {
                resp.StatusCode = 416;
                resp.AddHeader("Content-Range", $"bytes */{size}");
                return;
            }
            (start, end, partial) = (rs, re, true);
        }
        var length = Math.Max(0, end - start + 1);
        resp.StatusCode = partial ? 206 : 200;
        resp.ContentType = "application/octet-stream";
        resp.AddHeader("Accept-Ranges", "bytes");
        resp.AddHeader("Last-Modified", item.Modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        if (partial) resp.AddHeader("Content-Range", $"bytes {start}-{end}/{size}");
        resp.ContentLength64 = length;
        if (headOnly || length == 0) return;

        // Double-buffered: the next block is fetched from the cloud while the current one is sent.
        var pos = start;
        var stop = start + length;
        Task<(byte[] Buf, int N)> Fetch(long at)
        {
            var n = (int)Math.Min(ReadBlock, stop - at);
            var buf = new byte[n];
            return FetchAsync(item.Id, at, buf, ct);
        }
        var pending = Fetch(pos);
        while (pos < stop)
        {
            var (buf, n) = await pending.ConfigureAwait(false);
            if (n <= 0) throw new IOException("The cloud returned fewer bytes than expected.");
            var next = pos + n;
            if (next < stop) pending = Fetch(next);
            await resp.OutputStream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            pos = next;
        }
    }

    private async Task<(byte[], int)> FetchAsync(string id, long at, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await _provider.ReadRangeAsync(id, at + total, buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
        }
        return (buf, total);
    }

    internal static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0;
        end = size - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var a = spec[..dash].Trim();
        var b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix)) return false;
            start = Math.Max(0, size - suffix);
            return true;
        }
        if (!long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out start)) return false;
        if (b.Length > 0 && long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var e)) end = Math.Min(e, size - 1);
        return true;
    }

    // ------------------------------------------------------------------ PUT

    private async Task PutAsync(string[] segs, HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
    {
        if (segs.Length == 0) throw new DavException(405, "Cannot write the root.");
        var (existing, parent) = await ResolveAsync(segs, ct).ConfigureAwait(false);
        if (existing is { IsFolder: true }) throw new DavException(405, "A folder with this name exists.");
        var name = segs[^1];
        var modified = DateTimeOffset.UtcNow;
        var target = (existing is not null ? UploadTarget.Replace(existing.Id, null, modified) : UploadTarget.NewFile(parent.Id, name, null, modified))
            with { SizeHint = req.ContentLength64 >= 0 ? req.ContentLength64 : null };

        var input = req.InputStream;
        var first = new byte[1];
        var gotFirst = await ReadFullAsync(input, first, ct).ConfigureAwait(false);
        if (gotFirst == 0)
        {
            if (existing is null) await _provider.CreateEmptyFileAsync(parent.Id, name, null, ct).ConfigureAwait(false);
            else
            {
                await using var s0 = await _provider.BeginUploadAsync(target, ct).ConfigureAwait(false);
                await s0.CompleteAsync(ReadOnlyMemory<byte>.Empty, 0, ct).ConfigureAwait(false);
            }
        }
        else
        {
            await using var session = await _provider.BeginUploadAsync(target, ct).ConfigureAwait(false);
            var g = Math.Max(1, session.Granularity);
            var block = Math.Max(g, ReadBlock / g * g);
            var cur = new byte[block];
            cur[0] = first[0];
            var curLen = 1 + await ReadFullAsync(input, cur.AsMemory(1), ct).ConfigureAwait(false);
            long total = curLen;
            try
            {
                while (true)
                {
                    if (curLen < block)
                    {
                        await session.CompleteAsync(cur.AsMemory(0, curLen), total, ct).ConfigureAwait(false);
                        break;
                    }
                    // Full block: only send it as non-final if more data follows.
                    var next = new byte[block];
                    var nextLen = await ReadFullAsync(input, next, ct).ConfigureAwait(false);
                    if (nextLen == 0)
                    {
                        await session.CompleteAsync(cur, total, ct).ConfigureAwait(false);
                        break;
                    }
                    await session.AppendAsync(cur, ct).ConfigureAwait(false);
                    cur = next;
                    curLen = nextLen;
                    total += nextLen;
                }
            }
            catch
            {
                await session.AbortAsync().ConfigureAwait(false);
                throw;
            }
            if (req.ContentLength64 >= 0 && req.ContentLength64 != total)
                throw new DavException(400, $"Received {total} bytes, expected {req.ContentLength64}.");
        }
        Invalidate(parent.Id);
        resp.StatusCode = existing is null ? 201 : 204;
    }

    private static async Task<int> ReadFullAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // ------------------------------------------------------------------ MKCOL / DELETE / MOVE

    private async Task MkColAsync(string[] segs, HttpListenerResponse resp, CancellationToken ct)
    {
        if (segs.Length == 0) throw new DavException(405, "Root exists.");
        var (existing, parent) = await ResolveAsync(segs, ct).ConfigureAwait(false);
        if (existing is not null) throw new DavException(405, "Already exists.");
        await _provider.CreateFolderAsync(parent.Id, segs[^1], ct).ConfigureAwait(false);
        Invalidate(parent.Id);
        resp.StatusCode = 201;
    }

    private async Task DeleteAsync(string[] segs, HttpListenerResponse resp, CancellationToken ct)
    {
        if (segs.Length == 0) throw new DavException(403, "Cannot delete the root.");
        var (item, parent) = await ResolveAsync(segs, ct).ConfigureAwait(false);
        if (item is null) throw new DavException(404, "Not found.");
        await _provider.DeleteAsync(item.Id, _permanentDelete, ct).ConfigureAwait(false);
        Invalidate(parent.Id);
        Invalidate(item.Id);
        resp.StatusCode = 204;
    }

    private async Task MoveAsync(string[] segs, HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
    {
        if (segs.Length == 0) throw new DavException(403, "Cannot move the root.");
        if (!Uri.TryCreate(req.Headers["Destination"], UriKind.Absolute, out var dest)) throw new DavException(400, "Missing Destination.");
        var destSegs = PathOf(dest);
        if (destSegs.Length == 0) throw new DavException(403, "Cannot replace the root.");
        var overwrite = !string.Equals(req.Headers["Overwrite"], "F", StringComparison.OrdinalIgnoreCase);

        var (item, parent) = await ResolveAsync(segs, ct).ConfigureAwait(false);
        if (item is null) throw new DavException(404, "Not found.");
        var (destItem, destParent) = await ResolveAsync(destSegs, ct).ConfigureAwait(false);
        if (destItem is not null && destItem.Id == item.Id)
        {
            resp.StatusCode = 204;
            return;
        }
        if (destItem is not null)
        {
            if (!overwrite) throw new DavException(412, "Destination exists.");
            await _provider.DeleteAsync(destItem.Id, _permanentDelete, ct).ConfigureAwait(false);
        }
        var update = new MetadataUpdate
        {
            NewName = destSegs[^1] == item.Name ? null : destSegs[^1],
            AddParentId = destParent.Id == parent.Id ? null : destParent.Id,
            RemoveParentId = destParent.Id == parent.Id ? null : parent.Id,
        };
        await _provider.UpdateMetadataAsync(item.Id, update, ct).ConfigureAwait(false);
        Invalidate(parent.Id);
        Invalidate(destParent.Id);
        resp.StatusCode = destItem is null ? 201 : 204;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _cts.Dispose();
    }
}
