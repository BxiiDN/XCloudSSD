using System.Net;
using System.Text;
using XCloudSSD.Core;
using XCloudSSD.GoogleDrive;

namespace XCloudSSD.Tests;

/// <summary>
/// Verifies the exact HTTP requests the Google Drive adapter sends (Drive API v3 REST + resumable upload
/// protocol) against a scripted handler. This checks our side of the protocol; it is not a live Google test.
/// </summary>
public class GoogleDriveProtocolTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, byte[] Body)> Requests = new();
        public Func<HttpRequestMessage, byte[], HttpResponseMessage> Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add((request, body));
            return Respond(request, body);
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GoogleDriveProvider P, ScriptedHandler H, List<bool> TokenCalls) Create()
    {
        var h = new ScriptedHandler();
        var calls = new List<bool>();
        var p = new GoogleDriveProvider(new HttpClient(h), (force, _) =>
        {
            calls.Add(force);
            return Task.FromResult(force ? "fresh-token" : "token");
        }, new Metrics());
        return (p, h, calls);
    }

    [Fact]
    public async Task Quota_IsReadFromAboutStorageQuota()
    {
        var (p, h, _) = Create();
        h.Respond = (_, _) => JsonResponse("""{"storageQuota":{"limit":"1099511627776","usage":"214748364800"}}""");
        var q = await p.GetQuotaAsync(default);
        Assert.Equal(1099511627776, q.TotalBytes);
        Assert.Equal(214748364800, q.UsedBytes);
        Assert.Equal(1099511627776 - 214748364800, q.FreeBytes);
        Assert.Contains("about?fields=storageQuota", h.Requests[0].Req.RequestUri!.ToString());
        Assert.Equal("Bearer", h.Requests[0].Req.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task ListChildren_QueriesParentAndParsesItems()
    {
        var (p, h, _) = Create();
        h.Respond = (_, _) => JsonResponse("""
            {"files":[
              {"id":"f1","name":"game.pak","mimeType":"application/octet-stream","size":"123","md5Checksum":"abc",
               "createdTime":"2026-01-01T00:00:00.000Z","modifiedTime":"2026-01-02T00:00:00.000Z","parents":["P"],"appProperties":{"xcs_attr":"1"}},
              {"id":"d1","name":"data","mimeType":"application/vnd.google-apps.folder","parents":["P"]},
              {"id":"g1","name":"Notes","mimeType":"application/vnd.google-apps.document","parents":["P"]}
            ]}
            """);
        var items = await p.ListChildrenAsync("P", default);
        var uri = Uri.UnescapeDataString(h.Requests[0].Req.RequestUri!.ToString());
        Assert.Contains("'P' in parents and trashed = false", uri);
        Assert.Contains("pageSize=1000", uri);
        Assert.Equal(3, items.Count);
        var file = items.Single(i => i.Id == "f1");
        Assert.Equal(123, file.Size);
        Assert.Equal(1u, file.WindowsAttributes);
        Assert.True(items.Single(i => i.Id == "d1").IsFolder);
        Assert.True(items.Single(i => i.Id == "g1").IsVirtualDocument);
    }

    [Fact]
    public async Task ReadRange_SendsRangeHeader()
    {
        var (p, h, _) = Create();
        h.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Enumerable.Range(0, 10).Select(i => (byte)i).ToArray()) };
        var buf = new byte[10];
        var n = await p.ReadRangeAsync("f1", 100, buf, default);
        Assert.Equal(10, n);
        var req = h.Requests[0].Req;
        Assert.Equal("bytes=100-109", req.Headers.Range!.ToString());
        Assert.EndsWith("files/f1?alt=media", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task ResumableUpload_FollowsProtocol_AndResendsUnacknowledgedBytes()
    {
        var (p, h, _) = Create();
        var session = new Uri("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=XYZ");
        var putCount = 0;
        h.Respond = (req, body) =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Headers.Location = session;
                return r;
            }
            putCount++;
            var range = req.Content!.Headers.GetValues("Content-Range").Single();
            if (putCount == 1)
            {
                // Server only persisted the first 256 KiB of the 512 KiB chunk.
                Assert.Equal("bytes 0-524287/*", range);
                var r = new HttpResponseMessage((HttpStatusCode)308);
                r.Headers.TryAddWithoutValidation("Range", "bytes=0-262143");
                return r;
            }
            if (putCount == 2)
            {
                Assert.Equal("bytes 262144-524287/*", range);
                Assert.Equal(262144, body.Length);
                var r = new HttpResponseMessage((HttpStatusCode)308);
                r.Headers.TryAddWithoutValidation("Range", "bytes=0-524287");
                return r;
            }
            Assert.Equal("bytes 524288-524297/524298", range);
            return JsonResponse("""{"id":"new1","name":"x.bin","mimeType":"application/octet-stream","size":"524298","md5Checksum":"m","parents":["P"]}""");
        };

        var up = await p.BeginUploadAsync(UploadTarget.NewFile("P", "x.bin", 0x20, DateTimeOffset.UtcNow), default);
        var post = h.Requests[0];
        Assert.Contains("upload/drive/v3/files?uploadType=resumable", post.Req.RequestUri!.ToString());
        var meta = Encoding.UTF8.GetString(post.Body);
        Assert.Contains("\"name\":\"x.bin\"", meta);
        Assert.Contains("\"parents\":[\"P\"]", meta);
        Assert.Contains("\"xcs_attr\":\"32\"", meta);

        await up.AppendAsync(new byte[512 * 1024], default);
        Assert.Equal(524288, up.Committed);
        var item = await up.CompleteAsync(new byte[10], 524298, default);
        Assert.Equal("new1", item.Id);
        Assert.Equal(524298, item.Size);
        Assert.Equal(3, putCount);
    }

    [Fact]
    public async Task Unauthorized_RefreshesTokenOnce_AndRetries()
    {
        var (p, h, calls) = Create();
        var n = 0;
        h.Respond = (req, _) => ++n == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }
            : JsonResponse("""{"user":{"displayName":"A","emailAddress":"a@example.com"}}""");
        var id = await p.GetIdentityAsync(default);
        Assert.Equal("a@example.com", id.Email);
        Assert.Contains(true, calls); // forced refresh happened
        Assert.Equal("fresh-token", h.Requests[1].Req.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task QuotaExceeded_MapsToDiskFull()
    {
        var (p, h, _) = Create();
        h.Respond = (_, _) => JsonResponse("""{"error":{"code":403,"message":"The user's Drive storage quota has been exceeded.","errors":[{"reason":"storageQuotaExceeded"}]}}""", HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<CloudException>(() => p.CreateFolderAsync("P", "x", default));
        Assert.Equal(CloudErrorKind.QuotaExceeded, ex.Kind);
    }
}
