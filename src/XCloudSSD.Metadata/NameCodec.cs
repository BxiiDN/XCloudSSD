using System.Text;

namespace XCloudSSD.Metadata;

/// <summary>
/// Cloud names may contain characters Windows forbids in file names (e.g. ':' or '?').
/// They are shown with their full-width Unicode look-alikes and mapped back when written.
/// </summary>
public static class NameCodec
{
    private const string Forbidden = "<>:\"/\\|?*";
    private const string Replacement = "＜＞：＂／＼｜？＊";

    public static string ToWindows(string cloudName)
    {
        if (string.IsNullOrEmpty(cloudName)) return "_";
        StringBuilder? sb = null;
        for (var i = 0; i < cloudName.Length; i++)
        {
            var c = cloudName[i];
            var idx = Forbidden.IndexOf(c);
            char mapped = idx >= 0 ? Replacement[idx] : c < 32 ? '�' : c;
            if (mapped != c)
            {
                sb ??= new StringBuilder(cloudName, 0, i, cloudName.Length);
            }
            sb?.Append(mapped);
        }
        var result = sb?.ToString() ?? cloudName;
        // Windows strips trailing dots/spaces; keep the name addressable.
        if (result.EndsWith('.') || result.EndsWith(' '))
            result = result[..^1] + (result[^1] == '.' ? '．' : '　');
        return result;
    }

    public static string ToCloud(string windowsName)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < windowsName.Length; i++)
        {
            var c = windowsName[i];
            var idx = Replacement.IndexOf(c);
            if (idx >= 0)
            {
                sb ??= new StringBuilder(windowsName, 0, i, windowsName.Length);
                sb.Append(Forbidden[idx]);
            }
            else
            {
                sb?.Append(c);
            }
        }
        var result = sb?.ToString() ?? windowsName;
        if (result.EndsWith('．')) result = result[..^1] + ".";
        else if (result.EndsWith('　')) result = result[..^1] + " ";
        return result;
    }
}
