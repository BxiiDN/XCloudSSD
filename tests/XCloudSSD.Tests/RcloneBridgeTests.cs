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

    [Fact]
    public void DeterministicObscure_IsStable_AndReversible()
    {
        Assert.Equal(RcloneObscure.Obscure("bridge-pass", deterministic: true), RcloneObscure.Obscure("bridge-pass", deterministic: true));
        Assert.Equal("bridge-pass", RcloneObscure.Reveal(RcloneObscure.Obscure("bridge-pass", deterministic: true)));
    }

    [Fact]
    public void CachedFiles_FromAnOlderCacheFolder_AreMovedIntoTheCurrentOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "xcs-orphan-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string F(params string[] p) => Path.Combine([root, .. p]);
            Directory.CreateDirectory(F("vfs", "xcs{NEW01}"));
            Directory.CreateDirectory(F("vfsMeta", "xcs{NEW01}"));
            Directory.CreateDirectory(F("vfs", "xcs{OLD01}", "Game"));
            Directory.CreateDirectory(F("vfsMeta", "xcs{OLD01}", "Game"));
            File.WriteAllText(F("vfs", "xcs{OLD01}", "Game", "save.dat"), "data");
            File.WriteAllText(F("vfsMeta", "xcs{OLD01}", "Game", "save.dat"), "{\"Dirty\":true}");
            Directory.CreateDirectory(F("vfsMeta", "other"));
            File.WriteAllText(F("vfsMeta", "other", "x"), "not ours");

            var orphan = RcloneMount.FindOrphans(F("vfs", "xcs{NEW01}"), F("vfsMeta", "xcs{NEW01}"));
            Assert.NotNull(orphan);
            Assert.Equal(1, orphan!.Files);
            RcloneMount.MergeCache(orphan);

            Assert.Equal("data", File.ReadAllText(F("vfs", "xcs{NEW01}", "Game", "save.dat")));
            Assert.True(File.Exists(F("vfsMeta", "xcs{NEW01}", "Game", "save.dat")));
            Assert.False(Directory.Exists(F("vfsMeta", "xcs{OLD01}")));
            Assert.True(File.Exists(F("vfsMeta", "other", "x")));
            Assert.Null(RcloneMount.FindOrphans(F("vfs", "xcs{NEW01}"), F("vfsMeta", "xcs{NEW01}")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("SteamLibrary/steamapps/downloading/1817190/asset_archive/g00s001", true)]
    [InlineData("SteamLibrary/steamapps/temp/1817190/x.bin", true)]
    [InlineData("SteamLibrary/steamapps/workshop/downloads/1/a.pak", true)]
    [InlineData("SteamLibrary/steamapps/common/Marvel's Spider-Man Miles Morales/MilesMorales.exe", false)]
    [InlineData("SteamLibrary/steamapps/appmanifest_1817190.acf", false)]
    [InlineData("Games/downloading/file.bin", false)]
    public void SteamStagingFiles_WaitInTheCache(string name, bool held)
    {
        Assert.Equal(held, RcloneMount.IsHeld(name, RcloneMountOptions.DefaultHoldFolders));
    }

    [Fact]
    public void ChunkedRemote_WrapsTheBridge_ThroughEnvironmentOnly()
    {
        var env = RcloneMount.BuildEnvironment(RcloneRemote.Chunked(
            new RcloneRemote("webdav", new Dictionary<string, string> { ["url"] = "http://127.0.0.1:1/" }), "3G"));
        Assert.Equal("chunker", env["RCLONE_CONFIG_XCS_TYPE"]);
        Assert.Equal("xcsraw:", env["RCLONE_CONFIG_XCS_REMOTE"]);
        Assert.Equal("3G", env["RCLONE_CONFIG_XCS_CHUNK_SIZE"]);
        Assert.Equal("webdav", env["RCLONE_CONFIG_XCSRAW_TYPE"]);
        Assert.Equal("http://127.0.0.1:1/", env["RCLONE_CONFIG_XCSRAW_URL"]);
    }
}
