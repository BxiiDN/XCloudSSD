using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop.Pages;

public partial class GameStoragePage : UserControl, IPage
{
    private string _drivesSig = "";

    public GameStoragePage() => InitializeComponent();

    private Window Owner => Window.GetWindow(this)!;
    private char? Letter => (DriveBox.SelectedItem as DiskView)?.Dto.DriveLetter;

    public void OnShown() => _ = ScanAsync();

    public void OnStatus(ServiceStatusDto? s)
    {
        var drives = s?.Accounts.Where(a => a.Mounted).Select(a => new DiskView(a)).ToList() ?? new();
        var sig = string.Join(";", drives.Select(d => d.Id + d.Dto.DriveLetter));
        if (sig == _drivesSig) return;
        _drivesSig = sig;
        var prev = Letter;
        DriveBox.ItemsSource = drives;
        DriveBox.SelectedItem = drives.FirstOrDefault(d => d.Dto.DriveLetter == prev) ?? drives.FirstOrDefault();
    }

    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = ScanAsync();
    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (Letter is not { } letter)
        {
            SteamInfo.Text = L.T("NoMountedDrives");
            Games.ItemsSource = null;
            return;
        }
        SteamInfo.Text = L.T("Scanning");
        try
        {
            var (libs, games, registered, steamPath) = await Task.Run(() =>
            {
                var l = SteamLibrary.LibrariesOnDrive(letter);
                var g = l.SelectMany(SteamLibrary.GamesIn).ToList();
                var r = SteamLibrary.RegisteredLibraries();
                return (l, g, r, SteamLibrary.SteamPath());
            });
            Games.ItemsSource = games;
            var sb = new StringBuilder();
            sb.AppendLine(steamPath is null ? L.T("SteamNotFound") : L.F("SteamFoundAt", steamPath));
            if (libs.Count == 0) sb.AppendLine(L.F("NoSteamLibraryOnDrive", letter));
            foreach (var lib in libs)
            {
                var isRegistered = registered.Any(r => string.Equals(r.TrimEnd('\\'), lib.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                sb.AppendLine(L.F(isRegistered ? "LibraryRegistered" : "LibraryNotRegistered", lib));
            }
            SteamInfo.Text = sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            SteamInfo.Text = ex.Message;
        }
    }

    private void CreateSteam_Click(object sender, RoutedEventArgs e)
    {
        if (Letter is not { } letter) return;
        try
        {
            var path = $"{letter}:\\SteamLibrary";
            Directory.CreateDirectory(path);
            Dialogs.Info(Owner, L.T("CreateSteamLibrary"), L.F("SteamLibraryCreated", path));
            _ = ScanAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    private void OpenSteam_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Steam's own client handles library registration; this opens Steam so the user can add the drive.
            ServiceHub.OpenUrl("steam://open/main");
            Dialogs.Info(Owner, L.T("OpenSteamStorage"), L.T("SteamAddDriveSteps"));
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    private void CreateGames_Click(object sender, RoutedEventArgs e)
    {
        if (Letter is not { } letter) return;
        try
        {
            var path = $"{letter}:\\Games";
            Directory.CreateDirectory(path);
            Dialogs.Info(Owner, L.T("CreateGamesFolder"), L.F("GamesFolderCreated", path));
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    /// <summary>
    /// Real I/O test on the selected virtual disk using the same patterns games and launchers use:
    /// create, sequential write, random reads, small-file burst, rename, delete. Results are measured, not estimated.
    /// </summary>
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (Letter is not { } letter) return;
        CheckOutput.Text = "";
        void Log(string s) => Dispatcher.Invoke(() => CheckOutput.AppendText(s + Environment.NewLine));
        try
        {
            await Task.Run(() => RunCheck(letter, Log));
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
        }
    }

    private static void RunCheck(char letter, Action<string> log)
    {
        var dir = $"{letter}:\\.xcloudssd-check-{Guid.NewGuid():N}"[..40];
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(dir);
        log($"[ok] create folder ({sw.ElapsedMilliseconds} ms)");
        try
        {
            // 1) Sequential write of 32 MiB and close (= commit to the cloud, verified).
            var data = RandomNumberGenerator.GetBytes(32 << 20);
            var file = Path.Combine(dir, "seq.bin");
            sw.Restart();
            using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                for (var off = 0; off < data.Length; off += 1 << 20) fs.Write(data, off, 1 << 20);
            var writeMs = sw.ElapsedMilliseconds;
            log($"[ok] write+commit 32 MiB: {writeMs} ms ({32.0 / Math.Max(0.001, writeMs / 1000.0):0.0} MiB/s)");

            // 2) Random reads (game-like access) and verification.
            var rnd = new Random(42);
            sw.Restart();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess))
            {
                var buf = new byte[64 * 1024];
                for (var i = 0; i < 64; i++)
                {
                    var pos = rnd.NextInt64(0, data.Length - buf.Length);
                    fs.Position = pos;
                    var n = fs.Read(buf, 0, buf.Length);
                    if (n != buf.Length || !buf.AsSpan().SequenceEqual(data.AsSpan((int)pos, buf.Length)))
                        throw new IOException($"Random read mismatch at offset {pos}.");
                }
            }
            log($"[ok] 64 random 64 KiB reads verified: {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / 64.0:0} ms avg)");

            // 3) Full sequential read and hash comparison.
            sw.Restart();
            var hash = SHA256.HashData(File.ReadAllBytes(file));
            log(hash.SequenceEqual(SHA256.HashData(data))
                ? $"[ok] full read + SHA-256 match: {sw.ElapsedMilliseconds} ms"
                : "[FAIL] full read hash mismatch");

            // 4) Many small files (manifests, configs).
            sw.Restart();
            for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(dir, $"small{i}.cfg"), $"value={i}");
            log($"[ok] 20 small files: {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / 20.0:0} ms per file)");

            // 5) In-place modification of an existing file (re-upload path).
            sw.Restart();
            using (var fs = new FileStream(Path.Combine(dir, "small0.cfg"), FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = 6;
                fs.WriteByte((byte)'X');
            }
            var check = File.ReadAllText(Path.Combine(dir, "small0.cfg"));
            log(check == "value=X" ? $"[ok] in-place edit: {sw.ElapsedMilliseconds} ms" : $"[FAIL] in-place edit read back '{check}'");

            // 6) Rename + enumerate.
            sw.Restart();
            File.Move(file, Path.Combine(dir, "renamed.bin"));
            var count = Directory.EnumerateFileSystemEntries(dir).Count();
            log($"[ok] rename + list ({count} entries): {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            sw.Restart();
            Directory.Delete(dir, recursive: true);
            log($"[ok] delete test folder: {sw.ElapsedMilliseconds} ms");
        }
        log("Done. These numbers depend on your connection and the provider's limits.");
    }
}
