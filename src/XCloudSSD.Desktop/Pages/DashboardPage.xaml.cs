using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop.Pages;

public partial class DashboardPage : UserControl, IPage
{
    public DashboardPage() => InitializeComponent();

    public void OnShown()
    {
    }

    public void OnStatus(ServiceStatusDto? s)
    {
        if (s is null)
        {
            foreach (var t in new[] { TotalDisks, Connected, Mounted, Transfers, Capacity, Used, Available }) t.Text = "—";
            Connection.Text = L.T("ServiceStopped");
            DiskList.ItemsSource = null;
            return;
        }
        var accounts = s.Accounts;
        TotalDisks.Text = accounts.Count.ToString();
        Connected.Text = $"{accounts.Count(a => a.State == ConnectionState.Online)} / {accounts.Count}";
        Mounted.Text = accounts.Count(a => a.Mounted).ToString();
        Transfers.Text = s.Perf.TransferQueue.ToString();

        // Sums only use quotas reported by the providers; unknown/unlimited quotas are excluded and flagged.
        var known = accounts.Where(a => a.TotalBytes is not null).ToList();
        var unlimited = accounts.Any(a => a.TotalBytes is null && a.QuotaUpdated is not null);
        Capacity.Text = Fmt.Bytes(known.Sum(a => a.TotalBytes!.Value)) + (unlimited ? " +" : "");
        Used.Text = Fmt.Bytes(accounts.Sum(a => a.UsedBytes));
        Available.Text = Fmt.Bytes(known.Sum(a => a.FreeBytes ?? 0)) + (unlimited ? " +" : "");
        Connection.Text = !s.InternetAvailable ? L.T("Offline")
            : accounts.Any(a => a.State is ConnectionState.Offline or ConnectionState.AuthRequired or ConnectionState.Error) ? L.T("Degraded")
            : L.T("Online");

        DiskList.ItemsSource = accounts.Select(a => new DiskView(a)).ToList();
        NoDisks.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
