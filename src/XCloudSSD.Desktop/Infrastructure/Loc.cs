using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;
using XCloudSSD.Core;

namespace XCloudSSD.Desktop.Infrastructure;

/// <summary>
/// UI strings. English and Bahasa Indonesia are built in; any "*.json" file placed in
/// %AppData%\XCloudSSD\languages is offered as a custom language (same keys, "_name" = display name).
/// XAML binds with {Binding [Key], Source={x:Static i:Loc.Instance}}.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, string> _english;
    private Dictionary<string, string> _current;

    private Loc()
    {
        _english = LoadEmbedded("en");
        _current = _english;
        Code = "en";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Code { get; private set; }

    public string this[string key] =>
        _current.TryGetValue(key, out var v) ? v :
        _english.TryGetValue(key, out var e) ? e : key;

    public void Load(string code)
    {
        Dictionary<string, string>? dict = code switch
        {
            "en" => _english,
            "id" => LoadEmbedded("id"),
            _ => LoadCustom(code),
        };
        if (dict is null) return;
        _current = dict;
        Code = code;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code)));
    }

    public static List<KeyValuePair<string, string>> Available()
    {
        var list = new List<KeyValuePair<string, string>>
        {
            new("en", "English"),
            new("id", "Bahasa Indonesia"),
        };
        try
        {
            foreach (var f in Directory.GetFiles(AppPaths.UserLanguagesDir, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(f);
                if (code is "en" or "id") continue;
                var d = ReadFile(f);
                list.Add(new(code, d is not null && d.TryGetValue("_name", out var n) ? n + " (custom)" : code + " (custom)"));
            }
        }
        catch (IOException)
        {
        }
        return list;
    }

    /// <summary>Writes the English strings as a starting point for a custom translation.</summary>
    public string ExportTemplate()
    {
        var path = Path.Combine(AppPaths.UserLanguagesDir, "custom-template.json");
        var copy = new Dictionary<string, string>(_english) { ["_name"] = "My language" };
        File.WriteAllText(path, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static Dictionary<string, string> LoadEmbedded(string code)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Languages.{code}.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return new Dictionary<string, string>();
        using var s = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new Dictionary<string, string>();
    }

    private static Dictionary<string, string>? LoadCustom(string code)
    {
        var f = Path.Combine(AppPaths.UserLanguagesDir, code + ".json");
        return File.Exists(f) ? ReadFile(f) : null;
    }

    private static Dictionary<string, string>? ReadFile(string f)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f));
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class L
{
    public static string T(string key) => Loc.Instance[key];
    public static string F(string key, params object?[] args) => string.Format(Loc.Instance[key], args);
}
