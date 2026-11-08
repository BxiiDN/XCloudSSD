using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace XCloudSSD.Desktop.Infrastructure;

/// <summary>Per-user UI preferences (language, theme, start with Windows).</summary>
public sealed class DesktopSettings
{
    public string Language { get; set; } = "";
    public string Theme { get; set; } = "Dark";
    public bool Notifications { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCloudSSD", "desktop.json");

    public static DesktopSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new DesktopSettings();
        }
        catch (Exception)
        {
        }
        return new DesktopSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "XCloudSSD";

    /// <summary>Starts the XCloudSSD window (in the tray) when the user signs in.</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(RunValue, false);
        }
    }
}
