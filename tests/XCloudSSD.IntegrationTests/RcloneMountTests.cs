using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Rclone;
using XCloudSSD.TestSupport;
using XCloudSSD.Transfer;
using Xunit.Abstractions;

namespace XCloudSSD.IntegrationTests;

/// <summary>
/// Real "rclone mount" (bundled rclone.exe + WinFsp) of a cloud account through XCloudSSD's WebDAV bridge — the path
/// TeraBox uses. The cloud is the in-memory provider, so every byte that reaches "the cloud" can be checked.
/// </summary>
[Collection("WinFsp")]
public sealed class RcloneMountTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "xcs-rclone-" + Guid.NewGuid().ToString("N")[..8]);

    private static string RcloneExe =>
        Environment.GetEnvironmentVariable("XCS_RCLONE_EXE") is { Length: > 0 } p && File.Exists(p)
            ? p
            : throw new InvalidOperationException("Set XCS_RCLONE_EXE to the bundled rclone.exe (CI downloads it).");

    private (RcloneMount Mount, CloudWebDavServer Dav, TransferTracker Transfers) MountBridge(InMemoryCloudProvider cloud, char letter, int writeBack = 1)
    {
        var dav = new CloudWebDavServer(cloud, NullLogger.Instance, permanentDelete: true, listCacheTtl: TimeSpan.FromSeconds(1));
        dav.Start();
        var transfers = new TransferTracker(null);
        var mount = new RcloneMount(new RcloneMountOptions
        {
            RcloneExe = RcloneExe,
            Remote = new RcloneRemote("webdav", new Dictionary<string, string>
            {
                ["url"] = dav.Url, ["vendor"] = "other", ["user"] = dav.User, ["pass"] = RcloneObscure.Obscure(dav.Password),
            }),
            DriveLetter = letter,
            VolumeLabel = "XCloudSSD TeraBox 01",
            CacheDir = Path.Combine(_work, "cache"),
            LogFile = Path.Combine(_work, "rclone.log"),
            WriteBackSeconds = writeBack,
            DirCacheSeconds = 2,
            ExtraArgs = ["--log-level", "DEBUG"],
        }, NullLogger.Instance, transfers, "t-test");
        mount.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (mount, dav, transfers);
    }

    private static void WaitForUploads(RcloneMount mount, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var s = mount.RefreshStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (s is { UploadsInProgress: 0, UploadsQueued: 0 }) return;
            Thread.Sleep(500);
        }
        throw new TimeoutException("rclone uploads did not finish.");
    }

    [Fact]
    public void ObscuredPassword_IsAcceptedByRclone()
    {
        var psi = new ProcessStartInfo(RcloneExe) { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        psi.ArgumentList.Add("reveal");
        psi.ArgumentList.Add(RcloneObscure.Obscure("s3cret-Ünï"));
        using var p = Process.Start(psi)!;
        var revealed = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Equal("s3cret-Ünï", revealed);
    }

    [Fact]
    public void TeraBoxPath_GameInstallPattern_IsUploadedToTheCloud()
    {
        var cloud = new InMemoryCloudProvider(quotaBytes: 2L << 40, baseUsage: 300L << 30);
        var letter = TestDisk.FreeLetter();
        var (mount, dav, transfers) = MountBridge(cloud, letter);
        try
        {
            var root = $"{letter}:\\";
            var drive = new DriveInfo(root);
            output.WriteLine($"{root} format={drive.DriveFormat} type={drive.DriveType} total={drive.TotalSize} free={drive.AvailableFreeSpace}");
            Assert.Equal(DriveType.Fixed, drive.DriveType);
            Assert.Equal("NTFS", drive.DriveFormat);
            Assert.InRange(drive.TotalSize, (2L << 40) - (1L << 30), 2L << 40);

            // Steam-like: reserve the final size, then fill it out of order, plus small files and a rename.
            var dir = Path.Combine(root, "SteamLibrary", "steamapps", "common", "Game");
            Directory.CreateDirectory(dir);
            var data = new byte[96 << 20];
            new Random(7).NextBytes(data);
            var big = Path.Combine(dir, "data.cpk");
            var sw = Stopwatch.StartNew();
            using (var fs = new FileStream(big + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                fs.SetLength(data.Length);
                var order = Enumerable.Range(0, data.Length >> 20).OrderBy(i => (i * 37) % 96).ToList();
                foreach (var mb in order)
                {
                    fs.Position = (long)mb << 20;
                    fs.Write(data, mb << 20, 1 << 20);
                }
            }
            File.Move(big + ".tmp", big);
            File.WriteAllText(Path.Combine(dir, "config.ini"), "[game]\nlang=id\n");
            output.WriteLine($"wrote 96 MiB out of order through the cache in {sw.ElapsedMilliseconds} ms");

            // Read-your-writes before the upload finishes (served from the local cache).
            var back = File.ReadAllBytes(big);
            Assert.True(back.AsSpan().SequenceEqual(data));

            WaitForUploads(mount, TimeSpan.FromMinutes(3));
            var inCloud = cloud.ContentAt("/SteamLibrary/steamapps/common/Game/data.cpk");
            Assert.NotNull(inCloud);
            Assert.Equal(Convert.ToHexString(MD5.HashData(data)), Convert.ToHexString(MD5.HashData(inCloud!)));
            Assert.Equal("[game]\nlang=id\n", System.Text.Encoding.UTF8.GetString(cloud.ContentAt("/SteamLibrary/steamapps/common/Game/config.ini")!));
            Assert.Null(cloud.ContentAt("/SteamLibrary/steamapps/common/Game/data.cpk.tmp"));
            foreach (var t in transfers.List()) output.WriteLine($"transfer {t.Path} {t.State} {t.BytesDone}/{t.BytesTotal}");

            File.Delete(Path.Combine(dir, "config.ini"));
            var sw2 = Stopwatch.StartNew();
            while (cloud.ContentAt("/SteamLibrary/steamapps/common/Game/config.ini") is not null && sw2.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(200);
            Assert.Null(cloud.ContentAt("/SteamLibrary/steamapps/common/Game/config.ini"));
            Assert.True(mount.Status?.CacheBytes > 0, "the local cache should hold the written file");
        }
        finally
        {
            mount.Stop(TimeSpan.FromSeconds(30));
            mount.Dispose();
            dav.Dispose();
        }
        Assert.False(Directory.Exists($"{letter}:\\"));
    }

    [Fact]
    public void FilesWaitingInTheCache_AreUploadedAfterARestart()
    {
        var cloud = new InMemoryCloudProvider();
        var letter = TestDisk.FreeLetter();
        // Long write-back delay: the file is still waiting in the cache when the drive is unmounted.
        var (mount, dav, _) = MountBridge(cloud, letter, writeBack: 600);
        var data = RandomNumberGenerator.GetBytes(3 << 20);
        try
        {
            File.WriteAllBytes($"{letter}:\\pending.bin", data);
            Thread.Sleep(1500);
            Assert.Null(cloud.ContentAt("/pending.bin"));
        }
        finally
        {
            var waiting = mount.Stop(TimeSpan.Zero);
            output.WriteLine($"waiting at unmount: {waiting}");
            mount.Dispose();
            dav.Dispose();
        }
        Assert.Null(cloud.ContentAt("/pending.bin"));

        var (mount2, dav2, _) = MountBridge(cloud, letter);
        try
        {
            var sw = Stopwatch.StartNew();
            while (cloud.ContentAt("/pending.bin") is null && sw.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(500);
            Assert.Equal(data, cloud.ContentAt("/pending.bin"));
        }
        finally
        {
            mount2.Stop(TimeSpan.FromSeconds(30));
            mount2.Dispose();
            dav2.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var log in new[] { "rclone.log.1", "rclone.log" })
                if (File.Exists(Path.Combine(_work, log)))
                    output.WriteLine($"--- {log}\n" + string.Join('\n', File.ReadLines(Path.Combine(_work, log))
                        .Where(l => l.Contains("vfs cache") || l.Contains("ERROR") || l.Contains("pending") || l.Contains("Exit") || l.Contains("NOTICE")).TakeLast(60)));
            Directory.Delete(_work, true);
        }
        catch (Exception)
        {
        }
    }
}
