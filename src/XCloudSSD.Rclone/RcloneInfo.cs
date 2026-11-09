using System.Diagnostics;

namespace XCloudSSD.Rclone;

/// <summary>Detects the bundled rclone.exe and its version ("rclone v1.75.2" → "1.75.2").</summary>
public static class RcloneInfo
{
    private static (string Exe, string? Version)? _cached;

    public static string? Version(string exe)
    {
        if (_cached is { } c && c.Exe == exe) return c.Version;
        string? version = null;
        try
        {
            if (File.Exists(exe))
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("version");
                using var p = Process.Start(psi)!;
                var first = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(10_000);
                version = ParseVersion(first);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            version = null;
        }
        _cached = (exe, version);
        return version;
    }

    internal static string? ParseVersion(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "rclone" && parts[1].StartsWith('v') ? parts[1][1..] : null;
    }
}
