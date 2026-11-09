using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using XCloudSSD.Rclone;
using XCloudSSD.TestSupport;

namespace XCloudSSD.Tests;

/// <summary>The local WebDAV bridge that lets rclone mount providers it has no backend for (TeraBox).</summary>
public class RcloneBridgeTests
{
    private static readonly XNamespace D = "DAV:";

    private static (InMemoryCloudProvider Cloud, CloudWebDavServer Dav, HttpClient Http) Start()
    {
        var cloud = new InMemoryCloudProvider(quotaBytes: 1L << 40, baseUsage: 5L << 30);
        var dav = new CloudWebDavServer(cloud, NullLogger.Instance, permanentDelete: true, listCacheTtl: TimeSpan.Zero);
        dav.Start();
        var http = new HttpClient { BaseAddress = new Uri(dav.Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{dav.User}:{dav.Password}")));
        return (cloud, dav, http);
    }

    private static async Task<XDocument> PropFind(HttpClient http, string path, string depth)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), path);
        req.Headers.Add("Depth", depth);
        using var resp = await http.SendAsync(req);
        Assert.Equal(207, (int)resp.StatusCode);
        return XDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Obscure_RoundTrips()
    {
        foreach (var s in new[] { "", "p", "a long password with ünïcode ✓ and more than sixteen bytes" })
            Assert.Equal(s, RcloneObscure.Reveal(RcloneObscure.Obscure(s)));
        Assert.NotEqual(RcloneObscure.Obscure("x"), RcloneObscure.Obscure("x")); // random IV
    }

    [Theory]
    [InlineData("bytes=0-9", 100, 0, 9)]
    [InlineData("bytes=90-", 100, 90, 99)]
    [InlineData("bytes=-10", 100, 90, 99)]
    [InlineData("bytes=50-500", 100, 50, 99)]
    public void Range_IsParsed(string header, long size, long start, long end)
    {
        Assert.True(CloudWebDavServer.TryParseRange(header, size, out var s, out var e));
        Assert.Equal((start, end), (s, e));
    }

    [Fact]
    public async Task RequiresThePerStartPassword()
    {
        var (_, dav, http) = Start();
        using (dav)
        using (http)
        {
            using var anon = new HttpClient { BaseAddress = new Uri(dav.Url) };
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/");
            var resp = await anon.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.StartsWith("http://127.0.0.1:", dav.Url);
        }
    }

    [Fact]
    public async Task Put_Get_PropFind_Move_Delete_WorkAgainstTheProvider()
    {
        var (cloud, dav, http) = Start();
        using (dav)
        using (http)
        {
            Assert.Equal(201, (int)(await http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), "/Steam%20Library/"))).StatusCode);
            var data = new byte[20_000_123];
            new Random(5).NextBytes(data);
            var put = await http.PutAsync("/Steam%20Library/game%20data.pak", new ByteArrayContent(data));
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            Assert.True(cloud.ContentAt("/Steam Library/game data.pak")!.AsSpan().SequenceEqual(data));

            // Overwrite keeps one file.
            var smaller = data.AsSpan(0, 1000).ToArray();
            Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsync("/Steam%20Library/game%20data.pak", new ByteArrayContent(smaller))).StatusCode);
            Assert.Equal(1000, cloud.ContentAt("/Steam Library/game data.pak")!.Length);

            var list = await PropFind(http, "/Steam%20Library/", "1");
            var responses = list.Descendants(D + "response").ToList();
            Assert.Equal(2, responses.Count);
            var file = responses.Single(r => r.Element(D + "href")!.Value == "/Steam%20Library/game%20data.pak");
            Assert.Equal("1000", file.Descendants(D + "getcontentlength").Single().Value);
            Assert.Contains(responses, r => r.Element(D + "href")!.Value == "/Steam%20Library/" && r.Descendants(D + "collection").Any());

            var root = await PropFind(http, "/", "0");
            Assert.Equal(((5L << 30) + 1000).ToString(), root.Descendants(D + "quota-used-bytes").Single().Value);
            Assert.NotEmpty(root.Descendants(D + "quota-available-bytes"));

            using (var get = new HttpRequestMessage(HttpMethod.Get, "/Steam%20Library/game%20data.pak"))
            {
                get.Headers.Range = new RangeHeaderValue(100, 199);
                var resp = await http.SendAsync(get);
                Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
                Assert.Equal(smaller.AsSpan(100, 100).ToArray(), await resp.Content.ReadAsByteArrayAsync());
            }

            using (var move = new HttpRequestMessage(new HttpMethod("MOVE"), "/Steam%20Library/game%20data.pak"))
            {
                move.Headers.Add("Destination", dav.Url + "renamed.pak");
                move.Headers.Add("Overwrite", "T");
                Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(move)).StatusCode);
            }
            Assert.NotNull(cloud.ContentAt("/renamed.pak"));
            Assert.Null(cloud.ContentAt("/Steam Library/game data.pak"));

            Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync("/renamed.pak")).StatusCode);
            Assert.Null(cloud.ContentAt("/renamed.pak"));
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/renamed.pak")).StatusCode);
        }
    }

    [Fact]
    public void MountArguments_UseInMemoryConfig_CacheOnDisk_AndNoSecretsOnTheCommandLine()
    {
        var mount = new RcloneMount(new RcloneMountOptions
        {
            RcloneExe = "rclone.exe",
            Remote = new RcloneRemote("drive", new Dictionary<string, string> { ["client_secret"] = "SECRET", ["token"] = "{\"refresh_token\":\"RT\"}" }),
            DriveLetter = 'G',
            VolumeLabel = "XCloudSSD Google 01",
            CacheDir = @"C:\ProgramData\XCloudSSD\Cache\g-1",
            LogFile = @"C:\ProgramData\XCloudSSD\logs\rclone-G.log",
        }, NullLogger.Instance);
        var args = mount.BuildArguments();
        Assert.Equal(new[] { "mount", "xcs:", "G:" }, args.Take(3));
        Assert.Equal("NUL", args[args.IndexOf("--config") + 1]);
        Assert.Equal("full", args[args.IndexOf("--vfs-cache-mode") + 1]);
        Assert.Equal(@"C:\ProgramData\XCloudSSD\Cache\g-1", args[args.IndexOf("--cache-dir") + 1]);
        Assert.Contains("--fuse-flag=--ExactFileSystemName=NTFS", args);
        Assert.DoesNotContain(args, a => a.Contains("SECRET") || a.Contains("RT"));

        var env = RcloneMount.BuildEnvironment(new RcloneRemote("drive", new Dictionary<string, string> { ["client_secret"] = "SECRET" }));
        Assert.Equal("drive", env["RCLONE_CONFIG_XCS_TYPE"]);
        Assert.Equal("SECRET", env["RCLONE_CONFIG_XCS_CLIENT_SECRET"]);
        Assert.Equal("1.75.2", RcloneInfo.ParseVersion("rclone v1.75.2"));
    }
}
