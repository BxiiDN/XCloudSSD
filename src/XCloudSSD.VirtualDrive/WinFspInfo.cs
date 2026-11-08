using System.Diagnostics;
using Microsoft.Win32;

namespace XCloudSSD.VirtualDrive;

/// <summary>Detects the installed WinFsp runtime (required for mounting).</summary>
public static class WinFspInfo
{
    public static (bool Installed, string? Version, string? InstallDir) Detect()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            var dir = key?.GetValue("InstallDir") as string;
            if (string.IsNullOrEmpty(dir)) return (false, null, null);
            var dll = Path.Combine(dir, "bin", "winfsp-x64.dll");
            if (!File.Exists(dll)) return (false, null, dir);
            var v = FileVersionInfo.GetVersionInfo(dll);
            return (true, $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}", dir);
        }
        catch
        {
            return (false, null, null);
        }
    }
}
