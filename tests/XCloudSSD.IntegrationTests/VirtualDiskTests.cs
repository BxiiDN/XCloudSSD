using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;
using XCloudSSD.Core.Ipc;
using XCloudSSD.TestSupport;

namespace XCloudSSD.IntegrationTests;

/// <summary>
/// Runs the real filesystem through a real WinFsp mount. The cloud side is the in-memory test double,
/// so these tests prove the Windows integration and the RAM/streaming rules, not Google Drive itself.
/// </summary>
[Collection("WinFsp")]
public class VirtualDiskTests(ITestOutputHelper output)
{
    private static byte[] Rand(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [WinFspFact]
    public void Volume_ReportsCloudCapacity_NotLocalDisk()
    {
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD Google 01", 64L << 20);
        var di = new DriveInfo(disk.Root);
        output.WriteLine($"{disk.Root} type={di.DriveType} fs={di.DriveFormat} label={di.VolumeLabel} total={di.TotalSize} free={di.TotalFreeSpace}");
        Assert.True(di.IsReady);
        Assert.Equal("XCloudSSD Google 01", di.VolumeLabel);
        Assert.Equal("NTFS", di.DriveFormat);
        Assert.Equal(1L << 40, di.TotalSize);               // 1 TiB cloud quota
        Assert.Equal((1L << 40) - (200L << 30), di.TotalFreeSpace); // minus 200 GiB used in the cloud
        Assert.NotEqual(new DriveInfo("C:\\").TotalSize, di.TotalSize);
    }

    [WinFspFact]
    public void Files_And_Folders_FullLifecycle()
    {
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD Test", 64L << 20);
        var dir = Path.Combine(disk.Root, "Docs", "Sub");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "hello.txt");
        File.WriteAllText(file, "Halo dari XCloudSSD");
        Assert.Equal("Halo dari XCloudSSD", Encoding.UTF8.GetString(disk.Cloud.ContentAt("Docs/Sub/hello.txt")!));
        Assert.Equal("Halo dari XCloudSSD", File.ReadAllText(file));

        File.AppendAllText(file, " + append");
        Assert.Equal("Halo dari XCloudSSD + append", Encoding.UTF8.GetString(disk.Cloud.ContentAt("Docs/Sub/hello.txt")!));

        var renamed = Path.Combine(disk.Root, "Docs", "moved.txt");
        File.Move(file, renamed);
        Assert.False(disk.Cloud.Exists("Docs/Sub/hello.txt"));
        Assert.True(disk.Cloud.Exists("Docs/moved.txt"));

        Assert.Contains("moved.txt", Directory.GetFiles(Path.Combine(disk.Root, "Docs")).Select(Path.GetFileName));
        Assert.Contains("Sub", Directory.GetDirectories(Path.Combine(disk.Root, "Docs")).Select(Path.GetFileName));

        File.SetAttributes(renamed, FileAttributes.ReadOnly | FileAttributes.Archive);
        Assert.True(File.GetAttributes(renamed).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(renamed, FileAttributes.Normal);

        File.Delete(renamed);
        Assert.False(disk.Cloud.Exists("Docs/moved.txt"));
        Directory.Delete(Path.Combine(disk.Root, "Docs"), recursive: true);
        Assert.False(disk.Cloud.Exists("Docs"));
    }

    [WinFspFact]
    public void LargeSequentialCopy_StreamsToCloud_WithBoundedRam()
    {
        const long ram = 48L << 20;
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD Big", ram);
        var path = Path.Combine(disk.Root, "big.bin");
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var block = new byte[4 << 20];
        var rnd = new Random(11);
        const int blocks = 64; // 256 MiB through a 48 MiB RAM budget
        var sw = Stopwatch.StartNew();
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            for (var i = 0; i < blocks; i++)
            {
                rnd.NextBytes(block);
                md5.AppendData(block);
                fs.Write(block);
            }
        }
        output.WriteLine($"wrote 256 MiB in {sw.ElapsedMilliseconds} ms; RAM peak {disk.Budget.Peak / (1 << 20)} MiB of {ram / (1 << 20)} MiB");
        var cloud = disk.Cloud.ContentAt("big.bin")!;
        Assert.Equal((long)blocks * block.Length, cloud.LongLength);
        Assert.Equal(md5.GetHashAndReset(), MD5.HashData(cloud));
        Assert.True(disk.Budget.Peak <= ram, $"RAM peak {disk.Budget.Peak} > {ram}");

        // Read back through the disk and verify, again within the RAM budget.
        using var read = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            int n;
            while ((n = fs.Read(block)) > 0) read.AppendData(block, 0, n);
        }
        Assert.Equal(MD5.HashData(cloud), read.GetHashAndReset());
        Assert.True(disk.Budget.Peak <= ram);
    }

    [WinFspFact]
    public void RandomAccess_ReadAndWrite_InExistingFile()
    {
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD RW", 64L << 20);
        var data = Rand(6_000_000, 12);
        var path = Path.Combine(disk.Root, "data.pak");
        File.WriteAllBytes(path, data);

        var rnd = new Random(13);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess))
        {
            var buf = new byte[4096];
            for (var i = 0; i < 40; i++)
            {
                var off = rnd.Next(0, data.Length - buf.Length);
                fs.Position = off;
                Assert.Equal(buf.Length, fs.Read(buf));
                Assert.True(buf.AsSpan().SequenceEqual(data.AsSpan(off, buf.Length)));
            }
            for (var i = 0; i < 20; i++)
            {
                var off = rnd.Next(0, data.Length - 100);
                var patch = Rand(100, 100 + i);
                fs.Position = off;
                fs.Write(patch);
                patch.CopyTo(data, off);
            }
        }
        Assert.True(disk.Cloud.ContentAt("data.pak")!.AsSpan().SequenceEqual(data));
    }

    [WinFspFact]
    public void SteamLikeLayout_PreallocateThenSequential_Works()
    {
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD Steam", 48L << 20);
        var game = Path.Combine(disk.Root, "SteamLibrary", "steamapps", "common", "NARUTO SHIPPUDEN Ultimate Ninja STORM 4");
        Directory.CreateDirectory(Path.Combine(game, "data"));
        Directory.CreateDirectory(Path.Combine(game, "sound"));
        File.WriteAllText(Path.Combine(disk.Root, "SteamLibrary", "steamapps", "appmanifest_349040.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"349040\"\n\t\"name\"\t\t\"Test\"\n}\n");
        for (var i = 0; i < 30; i++) File.WriteAllBytes(Path.Combine(game, "sound", $"s{i}.bin"), Rand(10_000 + i, i));

        // Preallocate (SetLength) then write sequentially — the common installer pattern.
        var pack = Path.Combine(game, "data", "pack.cpk");
        var expected = Rand(96 << 20, 99);
        using (var fs = new FileStream(pack, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            fs.SetLength(expected.Length);
            for (var off = 0; off < expected.Length; off += 1 << 20) fs.Write(expected, off, 1 << 20);
        }
        Assert.True(disk.Cloud.ContentAt("SteamLibrary/steamapps/common/NARUTO SHIPPUDEN Ultimate Ninja STORM 4/data/pack.cpk")!
            .AsSpan().SequenceEqual(expected));
        Assert.True(disk.Budget.Peak <= 48L << 20);
        Assert.Equal(30, Directory.GetFiles(Path.Combine(game, "sound")).Length);
        Assert.Contains("349040", File.ReadAllText(Path.Combine(disk.Root, "SteamLibrary", "steamapps", "appmanifest_349040.acf")));
    }

    [WinFspFact]
    public void SteamReserveSpace_IsInstant_AndNothingIsUploaded()
    {
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD Reserve", 32L << 20);
        var dir = Path.Combine(disk.Root, "SteamLibrary", "steamapps", "common", "Game", "data");
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        // "Reserving space": every file is created at its final size before any data arrives.
        for (var i = 0; i < 10; i++)
        {
            using var fs = new FileStream(Path.Combine(dir, $"pack{i}.cpk"), FileMode.CreateNew, FileAccess.Write);
            fs.SetLength(2L << 30); // 2 GiB each, 20 GiB total
        }
        output.WriteLine($"reserved 20 GiB in {sw.ElapsedMilliseconds} ms, uploaded {disk.Cloud.BytesUploaded} bytes");
        Assert.Equal(0, disk.Cloud.BytesUploaded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal(2L << 30, new System.IO.FileInfo(Path.Combine(dir, "pack3.cpk")).Length);

        // Download phase: the file is opened again and filled in order; only real data is uploaded.
        var data = Rand(48 << 20, 7);
        using (var fs = new FileStream(Path.Combine(dir, "pack0.cpk"), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            for (var off = 0; off < data.Length; off += 1 << 20) fs.Write(data, off, 1 << 20);
        Assert.True(disk.Cloud.ContentAt("SteamLibrary/steamapps/common/Game/data/pack0.cpk")!.AsSpan().SequenceEqual(data));
        Assert.Equal(2L << 30, new System.IO.FileInfo(Path.Combine(dir, "pack0.cpk")).Length);
        Assert.True(disk.Cloud.BytesUploaded < 64L << 20, $"uploaded {disk.Cloud.BytesUploaded}");

        // Reserved, never-written regions read back as zeros.
        using (var fs = new FileStream(Path.Combine(dir, "pack0.cpk"), FileMode.Open, FileAccess.Read))
        {
            fs.Position = 1L << 30;
            var buf = new byte[8192];
            Assert.Equal(buf.Length, fs.Read(buf));
            Assert.All(buf, b => Assert.Equal(0, b));
        }
        Assert.True(disk.Budget.Peak <= 32L << 20);
    }

    [WinFspFact]
    public void LargeOutOfOrderWrite_FailsClearly_InsteadOfUsingDisk()
    {
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD OOO", 16L << 20);
        var path = Path.Combine(disk.Root, "reverse.bin");
        var block = Rand(1 << 20, 31); // real data; all-zero writes would stay sparse and use no RAM
        var ex = Assert.ThrowsAny<IOException>(() =>
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            fs.SetLength(64L << 20);
            for (var i = 63; i >= 0; i--)
            {
                fs.Position = (long)i << 20;
                fs.Write(block);
                fs.Flush(flushToDisk: false);
            }
        });
        output.WriteLine("Expected failure: " + ex.Message);
        Assert.True(disk.Budget.Peak <= 16L << 20);
    }

    [WinFspFact]
    public void FileSharing_And_ByteRangeLocks_AreEnforced()
    {
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD Lock", 32L << 20);
        var path = Path.Combine(disk.Root, "lock.dat");
        File.WriteAllBytes(path, Rand(100_000, 14));

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite).Dispose());

        using var a = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        a.Lock(0, 1000);
        // bufferSize 1 = unbuffered, so the write reaches the filesystem immediately.
        using var b = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1);
        b.Position = 10;
        Assert.ThrowsAny<IOException>(() => b.Write(new byte[10]));
        a.Unlock(0, 1000);
    }

    [WinFspFact]
    public void Offline_KeepsDriveAndUnsavedData_ThenCommitsOnReconnect()
    {
        using var disk = new TestDisk(TestDisk.FreeLetter(), "XCloudSSD Offline", 32L << 20);
        Directory.CreateDirectory(Path.Combine(disk.Root, "work"));
        var existing = Path.Combine(disk.Root, "work", "cloud-only.bin");
        File.WriteAllBytes(existing, Rand(3_000_000, 15));

        disk.Cloud.Offline = true;
        Assert.True(Directory.Exists(disk.Root)); // the drive stays mounted
        Assert.Contains("cloud-only.bin", Directory.GetFiles(Path.Combine(disk.Root, "work")).Select(Path.GetFileName)); // listing from metadata
        // Content that is not in RAM cannot be read offline — reported as an error, never faked.
        disk.Fs.InvalidateMetadata();
        var data = Rand(500_000, 16);
        var pending = Path.Combine(disk.Root, "work", "written-offline.bin");
        File.WriteAllBytes(pending, data); // close succeeds; commit is deferred and kept in RAM
        Assert.False(disk.Cloud.Exists("work/written-offline.bin"));
        Assert.Equal(1, disk.Fs.PendingCommitCount);

        disk.Cloud.Offline = false;
        for (var i = 0; i < 100 && !disk.Cloud.Exists("work/written-offline.bin"); i++) Thread.Sleep(100);
        Assert.True(disk.Cloud.ContentAt("work/written-offline.bin")!.AsSpan().SequenceEqual(data));
        // The cloud may report the file a moment before the commit bookkeeping finishes.
        for (var i = 0; i < 50 && disk.Fs.PendingCommitCount != 0; i++) Thread.Sleep(100);
        Assert.Equal(0, disk.Fs.PendingCommitCount);
    }

    [WinFspFact]
    public void MultipleAccounts_AreIsolated_EachWithOwnLetterAndQuota()
    {
        var l1 = TestDisk.FreeLetter();
        using var g1 = new TestDisk(l1, "XCloudSSD Google 01", 32L << 20, new InMemoryCloudProvider(1L << 40, 200L << 30));
        var l2 = TestDisk.FreeLetter(l1);
        using var g2 = new TestDisk(l2, "XCloudSSD Google 02", 32L << 20, new InMemoryCloudProvider(15L << 30, 1L << 30));

        File.WriteAllText(Path.Combine(g1.Root, "only-in-01.txt"), "one");
        File.WriteAllText(Path.Combine(g2.Root, "only-in-02.txt"), "two");
        Assert.True(g1.Cloud.Exists("only-in-01.txt"));
        Assert.False(g1.Cloud.Exists("only-in-02.txt"));
        Assert.True(g2.Cloud.Exists("only-in-02.txt"));
        Assert.False(g2.Cloud.Exists("only-in-01.txt"));
        Assert.Equal(1L << 40, new DriveInfo(g1.Root).TotalSize);
        Assert.Equal(15L << 30, new DriveInfo(g2.Root).TotalSize);
    }

    [WinFspFact]
    public void Remount_KeepsData_BecauseItLivesInTheCloud()
    {
        var letter = TestDisk.FreeLetter();
        var cloud = new InMemoryCloudProvider();
        using (var disk = new TestDisk(letter, "XCloudSSD Persist", 32L << 20, cloud))
            File.WriteAllText(Path.Combine(disk.Root, "persist.txt"), "still here");
        Assert.False(Directory.Exists($"{letter}:\\"));
        using var again = new TestDisk(letter, "XCloudSSD Persist", 32L << 20, cloud);
        Assert.Equal("still here", File.ReadAllText(Path.Combine(again.Root, "persist.txt")));
    }
}

/// <summary>Runs only in CI after XCloudSSDService was installed and started as a real Windows service.</summary>
public class ServiceSmokeTests
{
    [Fact]
    [Trait("Category", "Service")]
    public async Task InstalledService_AnswersOnControlPipe()
    {
        if (Environment.GetEnvironmentVariable("XCLOUDSSD_SERVICE_TEST") != "1") return;
        var client = new PipeClient();
        Assert.True(await client.IsServiceRunningAsync());
        var status = await client.CallAsync<ServiceStatusDto>(Commands.Status);
        Assert.NotNull(status);
        Assert.True(status!.WinFspInstalled);
        var settings = await client.CallAsync<XCloudSSD.Core.AppSettings>(Commands.GetSettings);
        Assert.NotNull(settings);
        var letters = await client.CallAsync<List<string>>(Commands.FreeDriveLetters);
        Assert.NotEmpty(letters!);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync(Commands.AddTeraBox, new AddTeraBoxArgs()));
        Assert.Contains("sign-in", ex.Message);
    }
}
