using Microsoft.Extensions.Logging.Abstractions;

namespace XCloudSSD.Rclone;

/// <summary>
/// Installer/CI check: mounts rclone's in-memory backend with the bundled rclone.exe and WinFsp, writes and reads a
/// file through the drive letter and the local cache, then unmounts. No cloud account is involved.
/// </summary>
public static class RcloneSelfCheck
{
    public static (bool Ok, string Message) Run(string rcloneExe, string workDir)
    {
        var version = RcloneInfo.Version(rcloneExe);
        if (version is null) return (false, $"rclone not usable at {rcloneExe}");
        var used = Environment.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
        var letter = Enumerable.Range('K', 'Z' - 'K' + 1).Select(c => (char)c).Reverse().FirstOrDefault(c => !used.Contains(c));
        if (letter == default) return (false, "no free drive letter");
        Directory.CreateDirectory(workDir);
        var mount = new RcloneMount(new RcloneMountOptions
        {
            RcloneExe = rcloneExe,
            Remote = new RcloneRemote("memory", new Dictionary<string, string>()),
            DriveLetter = letter,
            VolumeLabel = "XCloudSSD selftest",
            CacheDir = Path.Combine(workDir, "cache"),
            LogFile = Path.Combine(workDir, "rclone-selftest.log"),
            WriteBackSeconds = 0,
        }, NullLogger.Instance);
        try
        {
            mount.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var file = $"{letter}:\\selftest.bin";
            var data = new byte[1 << 20];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(file, data);
            var back = File.ReadAllBytes(file);
            var format = new DriveInfo($"{letter}:\\").DriveFormat;
            if (!back.AsSpan().SequenceEqual(data)) return (false, "read-back mismatch");
            return (true, $"rclone {version} mount OK on {letter}: (filesystem {format})");
        }
        catch (Exception ex)
        {
            return (false, "rclone mount failed: " + ex.Message);
        }
        finally
        {
            mount.Stop(TimeSpan.FromSeconds(10));
            mount.Dispose();
            try
            {
                Directory.Delete(workDir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
