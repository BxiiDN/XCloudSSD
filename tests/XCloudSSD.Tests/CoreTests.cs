using System.Security.Cryptography;
using System.Text;
using XCloudSSD.Authentication;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Metadata;
using XCloudSSD.Security;
using XCloudSSD.TestSupport;

namespace XCloudSSD.Tests;

public class CoreTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "xcloudssd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [InlineData("a:b?c*d", "a：b？c＊d")]
    [InlineData("normal.txt", "normal.txt")]
    [InlineData("ends.", "ends．")]
    public void NameCodec_RoundTrips(string cloud, string windows)
    {
        Assert.Equal(windows, NameCodec.ToWindows(cloud));
        Assert.Equal(cloud, NameCodec.ToCloud(windows));
    }

    [Fact]
    public void SecretStore_EncryptsAndIsolatesAccounts()
    {
        var dir = TempDir();
        var store = new SecretStore(dir, machineScope: false);
        store.Save("acc-a", new Dictionary<string, string> { ["refresh_token"] = "very-secret-token-123" });
        store.Save("acc-b", new Dictionary<string, string> { ["refresh_token"] = "other" });

        Assert.Equal("very-secret-token-123", store.Load("acc-a")!["refresh_token"]);
        var raw = File.ReadAllBytes(Path.Combine(dir, "acc-a.secret"));
        Assert.DoesNotContain("very-secret-token-123", Encoding.UTF8.GetString(raw));

        // A blob copied to another account's slot must not decrypt (per-account entropy).
        File.Copy(Path.Combine(dir, "acc-a.secret"), Path.Combine(dir, "acc-b.secret"), overwrite: true);
        Assert.ThrowsAny<CryptographicException>(() => store.Load("acc-b"));

        store.Delete("acc-a");
        Assert.Null(store.Load("acc-a"));
    }

    [Fact]
    public void AppDatabase_AccountsSettingsAndJournal()
    {
        var db = new AppDatabase(Path.Combine(TempDir(), "t.db"));
        var a = new AccountRecord { Id = "g-1", Provider = ProviderKind.GoogleDrive, Email = "a@x", DriveLetter = 'D', Label = "XCloudSSD Google 01" };
        var b = new AccountRecord { Id = "g-2", Provider = ProviderKind.GoogleDrive, Email = "b@x", DriveLetter = 'E', Label = "XCloudSSD Google 02" };
        db.UpsertAccount(a);
        db.UpsertAccount(b);
        Assert.Equal(2, db.GetAccounts().Count);

        // Drive letters are unique per account.
        var clash = new AccountRecord { Id = "g-3", Provider = ProviderKind.GoogleDrive, DriveLetter = 'D' };
        Assert.ThrowsAny<Exception>(() => db.UpsertAccount(clash));

        var s = new AppSettings { RamBufferMB = 1024, DeleteMode = DeleteMode.Permanent };
        db.SaveSettings(s);
        Assert.Equal(1024, db.LoadSettings().RamBufferMB);
        Assert.Equal(DeleteMode.Permanent, db.LoadSettings().DeleteMode);

        var id = db.InsertTransfer(new TransferDto { AccountId = "g-1", Path = "\\x.bin", State = TransferState.Uploading, Started = DateTimeOffset.UtcNow, Updated = DateTimeOffset.UtcNow });
        Assert.Equal(1, db.MarkInFlightAsInterrupted());
        Assert.Equal(TransferState.Interrupted, db.GetTransfers().Single(t => t.Id == id).State);

        db.DeleteAccount("g-1");
        Assert.Single(db.GetAccounts());
    }

    [Fact]
    public async Task MetadataTree_ResolvesAndAppliesChanges()
    {
        var p = new InMemoryCloudProvider();
        var folder = await p.CreateFolderAsync(p.RootId, "Games", default);
        p.Seed(folder.Id, "a.txt", new byte[] { 1, 2, 3 });
        var tree = new MetadataTree(p, TimeSpan.FromMinutes(5));
        var token = await p.GetChangeTokenAsync(default);

        var node = await tree.ResolveAsync("\\Games\\A.TXT", default); // case-insensitive like NTFS
        Assert.NotNull(node);
        Assert.Equal(3, node!.Size);
        Assert.Null(await tree.ResolveAsync("\\Games\\missing", default));

        // Another device renames and adds files; the change feed updates the tree without a rescan.
        await p.UpdateMetadataAsync(node.Id, new MetadataUpdate { NewName = "renamed.txt" }, default);
        p.Seed(folder.Id, "new.bin", new byte[10]);
        tree.ApplyChanges(await p.GetChangesAsync(token, default));
        Assert.NotNull(await tree.ResolveAsync("\\Games\\renamed.txt", default));
        Assert.NotNull(await tree.ResolveAsync("\\Games\\new.bin", default));
        Assert.Null(await tree.ResolveAsync("\\Games\\a.txt", default));
    }

    [Fact]
    public void Pkce_ChallengeMatchesVerifier()
    {
        var (verifier, challenge) = GoogleOAuth.CreatePkce();
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
        var url = GoogleOAuth.BuildAuthorizeUrl("cid", "http://127.0.0.1:5000/", challenge, "st");
        Assert.StartsWith(GoogleOAuth.AuthorizeEndpoint, url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains(Uri.EscapeDataString(GoogleOAuth.DriveScope), url);
    }

    [Fact]
    public async Task BandwidthThrottle_LimitsRate()
    {
        var t = new BandwidthThrottle();
        t.SetLimitKBps(512);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 8; i++) await t.WaitAsync(256 * 1024, default);
        // 2 MiB at 512 KiB/s with a one-second burst allowance needs well over one second.
        Assert.True(sw.Elapsed.TotalSeconds >= 1.0, $"took {sw.Elapsed.TotalSeconds:0.00}s");
    }
}
