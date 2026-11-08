using System.Net;
using System.Text;
using XCloudSSD.Core;
using XCloudSSD.TeraBox;
using XCloudSSD.TestSupport;
using XCloudSSD.Transfer;

namespace XCloudSSD.Tests;

public class SparseTests
{
    private static (InMemoryCloudProvider P, MemoryBudget B, ChunkReadCache C) Setup(long ram)
    {
        var p = new InMemoryCloudProvider();
        var b = new MemoryBudget(ram);
        return (p, b, new ChunkReadCache(p, b, 1 << 20, 2, 4, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ReservedSpace_ViaSetSize_IsNotUploaded()
    {
        var (p, b, c) = Setup(16L << 20);
        var s = new WriteSession(p, b, c, new WriteOptions(4 << 20, 8L << 20), null, null, 0);
        s.SetSize(5L << 30); // Steam "Reserving space": 5 GiB file, nothing written yet
        var item = s.Commit(new CommitTarget(null, p.RootId, "pack.cpk", 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal(5L << 30, item!.Size);
        Assert.Equal(0, item.ContentLength);
        Assert.Equal(0, p.BytesUploaded);
        Assert.Equal(5L << 30, p.LogicalSizeAt("pack.cpk"));
    }

    [Fact]
    public void ReservedSpace_ViaZeroWrites_IsNotUploaded_AndUsesNoRam()
    {
        var (p, b, c) = Setup(8L << 20);
        var s = new WriteSession(p, b, c, new WriteOptions(4 << 20, 8L << 20), null, null, 0);
        s.BindStreamTarget(new CommitTarget(null, p.RootId, "z.bin", 0, DateTimeOffset.UtcNow));
        var zeros = new byte[1 << 20];
        for (var i = 0; i < 256; i++) s.Write((long)i << 20, zeros, CancellationToken.None); // 256 MiB of zeros through 8 MiB of RAM
        Assert.True(b.Peak <= b.Capacity);
        var item = s.Commit(new CommitTarget(null, p.RootId, "z.bin", 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal(256L << 20, item!.Size);
        Assert.Equal(0, p.BytesUploaded);
    }

    [Fact]
    public void SparseFile_ThenSequentialFill_StreamsRealDataOnly()
    {
        var (p, b, c) = Setup(24L << 20);
        var first = new WriteSession(p, b, c, new WriteOptions(4 << 20, 8L << 20), null, null, 0);
        first.SetSize(200L << 20);
        var created = first.Commit(new CommitTarget(null, p.RootId, "game.pak", 0, DateTimeOffset.UtcNow), CancellationToken.None)!;

        // Re-open (as the download phase does) and write the first 64 MiB in order.
        var s = new WriteSession(p, b, c, new WriteOptions(4 << 20, 8L << 20), created.Id, created.Md5, created.ContentLength, created.Size);
        s.BindStreamTarget(new CommitTarget(created.Id, null, null, 0, DateTimeOffset.UtcNow));
        var data = new byte[64 << 20];
        new Random(5).NextBytes(data);
        for (var off = 0; off < data.Length; off += 1 << 20) s.Write(off, data.AsSpan(off, 1 << 20), CancellationToken.None);
        Assert.True(s.StreamedBytes > 0, "sparse file with no stored content must stream");
        var item = s.Commit(new CommitTarget(created.Id, null, null, 0, DateTimeOffset.UtcNow), CancellationToken.None)!;

        Assert.Equal(200L << 20, item.Size);
        Assert.Equal(64L << 20, item.ContentLength);
        Assert.True(p.ContentAt("game.pak")!.AsSpan().SequenceEqual(data));
        Assert.True(b.Peak <= b.Capacity);

        // The unwritten tail still reads as zeros.
        var tail = new byte[4096];
        var reader = new WriteSession(p, b, c, new WriteOptions(4 << 20, 8L << 20), item.Id, item.Md5, item.ContentLength, item.Size);
        Assert.Equal(4096, reader.Read((150L << 20), tail, null, CancellationToken.None));
        Assert.All(tail, x => Assert.Equal(0, x));
    }
}

public class TeraBoxProtocolTests
{
    private sealed class Scripted : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, string Body)> Requests = new();
        public Func<HttpRequestMessage, string, HttpResponseMessage> Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return Respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string MainPage = "<html><script>function%20fn%28a%29%7Bwindow.jsToken%20%3D%20a%7D%3Bfn%28%22TOKEN123%22%29</script></html>";

    [Fact]
    public void Sign_MatchesAListAlgorithm()
    {
        // Reference value computed with a line-by-line port of AList's Go sign() function.
        Assert.Equal("JvAxKFcfMGJKkMOM", TeraBoxWebProvider.Sign("abc123key", "d4f1e9a0b7c2"));
    }

    [Fact]
    public async Task Initialize_ReadsJsToken_AndListsWithCommonParameters()
    {
        var h = new Scripted();
        h.Respond = (req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/main" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MainPage) },
            "/api/list" => Json("""{"errno":0,"list":[{"fs_id":11,"server_filename":"Games","isdir":1,"size":0,"server_mtime":1700000000},{"fs_id":12,"server_filename":"a.bin","isdir":0,"size":42,"server_mtime":1700000000}]}"""),
            _ => Json("""{"errno":0}"""),
        };
        var p = new TeraBoxWebProvider("ndus=abc", new Metrics(), h);
        await p.InitializeAsync(default);
        var items = await p.ListChildrenAsync(p.RootId, default);

        var list = h.Requests.Last(r => r.Req.RequestUri!.AbsolutePath == "/api/list").Req;
        var q = Uri.UnescapeDataString(list.RequestUri!.Query);
        Assert.Contains("app_id=250528", q);
        Assert.Contains("channel=dubox", q);
        Assert.Contains("web=1", q);
        Assert.Contains("jsToken=TOKEN123", q);
        Assert.Contains("dir=/", q);
        Assert.Equal("ndus=abc", list.Headers.GetValues("Cookie").Single());
        Assert.Equal(2, items.Count);
        Assert.True(items.Single(i => i.Id == "11").IsFolder);
        Assert.Equal(42, items.Single(i => i.Id == "12").Size);
    }

    [Fact]
    public async Task ExpiredJsToken_IsRefreshed_AndRequestRetried()
    {
        var h = new Scripted();
        var listCalls = 0;
        h.Respond = (req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/main" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MainPage) },
            "/api/list" => ++listCalls == 1 ? Json("""{"errno":4000023}""") : Json("""{"errno":0,"list":[]}"""),
            _ => Json("""{"errno":0}"""),
        };
        var p = new TeraBoxWebProvider("ndus=abc", new Metrics(), h);
        await p.InitializeAsync(default);
        Assert.Equal(2, listCalls);
        Assert.Equal(2, h.Requests.Count(r => r.Req.RequestUri!.AbsolutePath == "/main"));
    }

    [Theory]
    [InlineData("https://www.terabox.com", "jp", "terabox.com")]
    [InlineData("https://dm.terabox.com", "dm", "terabox.com")]
    [InlineData("https://www.1024tera.com", "jp", "1024tera.com")]
    [InlineData("https://www.1024terabox.com", "jp", "1024terabox.com")]
    public void UploadRegion_FollowsSignedInDomain(string baseUrl, string prefix, string root)
    {
        Assert.Equal((prefix, root), TeraBoxWebProvider.RegionOf(baseUrl));
    }

    [Fact]
    public async Task Upload_UsesAccountsOwnDomain_AndSkipsServersThatSayUserNotExists()
    {
        // Session signed in on www.1024tera.com: servers on terabox.com answer 403 errno 31045 "user not exists".
        var h = new Scripted();
        h.Respond = (req, body) =>
        {
            var uri = req.RequestUri!;
            if (uri.AbsolutePath == "/rest/2.0/pcs/file") return Json("""{"host":"c-jp.terabox.com"}""");
            if (uri.AbsolutePath == "/api/precreate") return Json("""{"errno":0,"uploadid":"UP1","return_type":1,"block_list":[0]}""");
            if (uri.AbsolutePath == "/rest/2.0/pcs/superfile2")
                return uri.Host.EndsWith("1024tera.com")
                    ? Json("""{"md5":"x"}""")
                    : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error_code":31045,"error_msg":"user not exists","request_id":1}""") };
            if (uri.AbsolutePath == "/api/create") return Json("""{"errno":0}""");
            if (uri.AbsolutePath == "/api/list") return Json("""{"errno":0,"list":[{"fs_id":7,"server_filename":"g.bin","isdir":0,"size":10,"server_mtime":1700000000}]}""");
            return Json("""{"errno":0}""");
        };
        var p = new TeraBoxWebProvider("ndus=abc", new Metrics(), h, jsToken: "T", baseUrl: "https://www.1024tera.com");
        await p.InitializeAsync(default);
        var up = await p.BeginUploadAsync(UploadTarget.NewFile(p.RootId, "g.bin"), default);
        await up.CompleteAsync(new byte[10], 10, default);

        var locate = h.Requests.First(r => r.Req.RequestUri!.AbsolutePath == "/rest/2.0/pcs/file").Req;
        Assert.Equal("jp-data.1024tera.com", locate.RequestUri!.Host);
        var part = h.Requests.Last(r => r.Req.RequestUri!.AbsolutePath == "/rest/2.0/pcs/superfile2").Req;
        Assert.Equal("c-jp.1024tera.com", part.RequestUri!.Host);
        Assert.Contains("ndus=abc", part.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task Upload_WhenNoServerKnowsTheUser_AsksToSignInAgain()
    {
        var h = new Scripted();
        h.Respond = (req, body) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/rest/2.0/pcs/file") return Json("""{"host":"c-jp.terabox.com"}""");
            if (path == "/api/precreate") return Json("""{"errno":0,"uploadid":"UP1","return_type":1,"block_list":[0]}""");
            if (path == "/rest/2.0/pcs/superfile2")
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error_code":31045,"error_msg":"user not exists"}""") };
            return Json("""{"errno":0,"list":[]}""");
        };
        var p = new TeraBoxWebProvider("ndus=abc", new Metrics(), h, jsToken: "T");
        await p.InitializeAsync(default);
        var up = await p.BeginUploadAsync(UploadTarget.NewFile(p.RootId, "x.bin"), default);
        var ex = await Assert.ThrowsAsync<CloudException>(() => up.CompleteAsync(new byte[10], 10, default));
        Assert.Equal(CloudErrorKind.Unauthorized, ex.Kind);
        Assert.Contains("add it again", ex.Message);
    }

    [Fact]
    public async Task Upload_Precreate_Parts_Create()
    {
        var h = new Scripted();
        var parts = 0;
        h.Respond = (req, body) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/main") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MainPage) };
            if (path == "/rest/2.0/pcs/file") return Json("""{"host":"c-test.terabox.com"}""");
            if (path == "/api/precreate") return Json("""{"errno":0,"uploadid":"UP1","return_type":1,"block_list":[0]}""");
            if (path == "/rest/2.0/pcs/superfile2") { parts++; return Json("""{"md5":"x"}"""); }
            if (path == "/api/create") return Json("""{"errno":0}""");
            if (path == "/api/list") return Json("""{"errno":0,"list":[{"fs_id":99,"server_filename":"f.bin","isdir":0,"size":5242880,"server_mtime":1700000000}]}""");
            return Json("""{"errno":0}""");
        };
        var p = new TeraBoxWebProvider("ndus=abc", new Metrics(), h);
        await p.InitializeAsync(default);
        var up = await p.BeginUploadAsync(UploadTarget.NewFile(p.RootId, "f.bin"), default);
        await up.AppendAsync(new byte[4 << 20], default);
        var item = await up.CompleteAsync(new byte[1 << 20], 5L << 20, default);

        Assert.Equal(2, parts);
        Assert.Equal("99", item.Id);
        var part = h.Requests.First(r => r.Req.RequestUri!.AbsolutePath == "/rest/2.0/pcs/superfile2").Req;
        Assert.Equal("c-test.terabox.com", part.RequestUri!.Host);
        var pq = Uri.UnescapeDataString(part.RequestUri.Query);
        Assert.Contains("uploadid=UP1", pq);
        Assert.Contains("partseq=0", pq);
        Assert.Contains("path=/f.bin", pq);
        var create = h.Requests.Single(r => r.Req.RequestUri!.AbsolutePath == "/api/create");
        var form = Uri.UnescapeDataString(create.Body.Replace('+', ' '));
        Assert.Contains("size=5242880", form);
        Assert.Contains("uploadid=UP1", form);
        Assert.Contains("block_list=[\"", form);
    }

    [Fact]
    public async Task JsToken_IsFound_AfterLanguageRedirect()
    {
        // TeraBox now answers /main with a redirect to /<language>/main; the token is on that page.
        var h = new Scripted();
        h.Respond = (req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/main" => Redirect("https://www.terabox.com/id/main?category=all"),
            "/id/main" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MainPage) },
            "/api/list" => Json("""{"errno":0,"list":[]}"""),
            _ => Json("""{"errno":0}"""),
        };
        var p = new TeraBoxWebProvider("ndus=abc", new Metrics(), h);
        await p.InitializeAsync(default);
        Assert.Equal("TOKEN123", p.JsToken);
        Assert.Contains("jsToken=TOKEN123", h.Requests.Last().Req.RequestUri!.Query);
    }

    [Fact]
    public async Task TokenCapturedAtSignIn_IsUsedDirectly()
    {
        var h = new Scripted();
        h.Respond = (req, _) => req.RequestUri!.AbsolutePath == "/api/list" ? Json("""{"errno":0,"list":[]}""") : Json("""{"errno":0}""");
        var p = new TeraBoxWebProvider("ndus=abc", new Metrics(), h, jsToken: "FROMPAGE", baseUrl: "https://dm.terabox.com");
        await p.InitializeAsync(default);
        var list = h.Requests.Single().Req;
        Assert.Equal("dm.terabox.com", list.RequestUri!.Host);
        Assert.Contains("jsToken=FROMPAGE", list.RequestUri.Query);
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    [Fact]
    public async Task SignedOutSession_IsReportedAsUnauthorized()
    {
        var h = new Scripted { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") } };
        var p = new TeraBoxWebProvider("ndus=expired", new Metrics(), h);
        var ex = await Assert.ThrowsAsync<CloudException>(() => p.InitializeAsync(default));
        Assert.Equal(CloudErrorKind.Unauthorized, ex.Kind);
    }
}
