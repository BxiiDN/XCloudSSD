using System;
using System.Globalization;
using XCloudSSD.Core;

namespace XCloudSSD.Desktop.Infrastructure;

public static class Fmt
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "—";
        double v = bytes.Value;
        var i = 0;
        while (Math.Abs(v) >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return v.ToString(i == 0 ? "0" : v < 10 ? "0.00" : v < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[i];
    }

    public static string Speed(double bytesPerSec) => Bytes((long)bytesPerSec) + "/s";

    public static string Ms(double ms) => ms < 0 ? "—" : ms.ToString("0", CultureInfo.CurrentCulture) + " ms";

    public static string State(ConnectionState s) => L.T("State" + s);

    public static string Provider(ProviderKind k) => k == ProviderKind.GoogleDrive ? "Google Drive" : "TeraBox";

    public static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
