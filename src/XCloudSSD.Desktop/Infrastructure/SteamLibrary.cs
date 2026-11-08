using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace XCloudSSD.Desktop.Infrastructure;

public sealed class SteamGame
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public long SizeOnDisk { get; init; }
    public int StateFlags { get; init; }
    public string Folder { get; init; } = "";
    public string SizeText => Fmt.Bytes(SizeOnDisk);
    // StateFlags 4 = fully installed; other bits mean update/download pending.
    public string StateText => (StateFlags & 4) != 0 && StateFlags == 4 ? L.T("SteamInstalled") : L.F("SteamStateFlags", StateFlags);
}

/// <summary>Reads Steam's own files (libraryfolders.vdf, appmanifest_*.acf). Read-only.</summary>
public static partial class SteamLibrary
{
    [GeneratedRegex("\"(?<k>[^\"]+)\"\\s+\"(?<v>[^\"]*)\"")]
    private static partial Regex KeyValue();

    public static string? SteamPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = k?.GetValue("SteamPath") as string;
            return string.IsNullOrEmpty(p) ? null : p.Replace('/', '\\');
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Library folders Steam itself has registered.</summary>
    public static List<string> RegisteredLibraries()
    {
        var result = new List<string>();
        var steam = SteamPath();
        if (steam is null) return result;
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return result;
        foreach (Match m in KeyValue().Matches(File.ReadAllText(vdf)))
            if (m.Groups["k"].Value.Equals("path", StringComparison.OrdinalIgnoreCase))
                result.Add(m.Groups["v"].Value.Replace("\\\\", "\\"));
        return result;
    }

    /// <summary>Steam library roots found on a drive (folders containing steamapps).</summary>
    public static List<string> LibrariesOnDrive(char letter)
    {
        var root = $"{letter}:\\";
        var found = new List<string>();
        if (Directory.Exists(Path.Combine(root, "steamapps"))) found.Add(root.TrimEnd('\\'));
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root))
                if (Directory.Exists(Path.Combine(d, "steamapps"))) found.Add(d);
        }
        catch (Exception)
        {
        }
        foreach (var reg in RegisteredLibraries())
            if (reg.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !found.Contains(reg, StringComparer.OrdinalIgnoreCase))
                found.Add(reg);
        return found;
    }

    public static List<SteamGame> GamesIn(string library)
    {
        var games = new List<SteamGame>();
        var apps = Path.Combine(library, "steamapps");
        if (!Directory.Exists(apps)) return games;
        foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
        {
            try
            {
                var kv = KeyValue().Matches(File.ReadAllText(acf))
                    .GroupBy(m => m.Groups["k"].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Groups["v"].Value, StringComparer.OrdinalIgnoreCase);
                games.Add(new SteamGame
                {
                    AppId = kv.GetValueOrDefault("appid", ""),
                    Name = kv.GetValueOrDefault("name", Path.GetFileName(acf)),
                    SizeOnDisk = long.TryParse(kv.GetValueOrDefault("SizeOnDisk", "0"), out var s) ? s : 0,
                    StateFlags = int.TryParse(kv.GetValueOrDefault("StateFlags", "0"), out var f) ? f : 0,
                    Folder = Path.Combine(apps, "common", kv.GetValueOrDefault("installdir", "")),
                });
            }
            catch (Exception)
            {
                // Skip unreadable manifest.
            }
        }
        return games;
    }
}
