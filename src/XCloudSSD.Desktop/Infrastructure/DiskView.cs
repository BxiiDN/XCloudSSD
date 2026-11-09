using System.Windows;
using System.Windows.Media;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;

namespace XCloudSSD.Desktop.Infrastructure;

/// <summary>Display model for one account/disk (shared by several pages).</summary>
public sealed class DiskView
{
    public DiskView(AccountDto a)
    {
        Dto = a;
    }

    public AccountDto Dto { get; }
    public string Id => Dto.Id;
    public string Letter => $"{Dto.DriveLetter}:";
    public string Title => $"{Dto.Label} ({Dto.DriveLetter}:)";
    public string Subtitle => $"{Fmt.Provider(Dto.Provider)} · {(string.IsNullOrEmpty(Dto.Email) ? Dto.DisplayName : Dto.Email)}";
    public string Provider => Fmt.Provider(Dto.Provider);
    public string Email => Dto.Email;
    public string StateText => Fmt.State(Dto.State) + (Dto.Mounted ? "" : " · " + L.T("NotMounted"))
                               + (Dto.Engine == "Rclone" ? " · rclone · " + L.F("CacheUsage", Fmt.Bytes(Dto.CacheBytes)) : "");
    public string StateDetail => Dto.StateMessage;
    public bool IsEnabled => Dto.Enabled;
    public string ToggleText => Dto.Enabled ? L.T("Disconnect") : L.T("Reconnect");
    public string Signature => $"{Dto.Id}|{Dto.Label}|{Dto.DriveLetter}|{Dto.State}|{Dto.Mounted}|{Dto.Enabled}|{Dto.UsedBytes}|{Dto.TotalBytes}|{Dto.StateMessage}|{Dto.Engine}|{Dto.CacheBytes / (16 << 20)}|{Loc.Instance.Code}";

    public double UsedFraction => Dto.TotalBytes is > 0 ? (double)Dto.UsedBytes / Dto.TotalBytes.Value : 0;

    public string CapacityText => Dto.TotalBytes is null && Dto.UsedBytes == 0 && Dto.QuotaUpdated is null
        ? L.T("QuotaUnknown")
        : L.F("CapacityFmt", Fmt.Bytes(Dto.UsedBytes),
            Dto.TotalBytes is null ? L.T("Unlimited") : Fmt.Bytes(Dto.TotalBytes),
            Dto.FreeBytes is null ? L.T("Unlimited") : Fmt.Bytes(Dto.FreeBytes));

    public Brush StateBrush => (Brush)Application.Current.FindResource(Dto.State switch
    {
        ConnectionState.Online => "OkBrush",
        ConnectionState.Connecting or ConnectionState.Offline => "WarnBrush",
        ConnectionState.Disconnected => "DimBrush",
        _ => "ErrBrush",
    });
}
