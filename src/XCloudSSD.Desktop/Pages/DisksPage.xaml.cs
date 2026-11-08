using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop.Pages;

public partial class DisksPage : UserControl, IPage
{
    private string _sig = "";

    public DisksPage() => InitializeComponent();

    public void OnShown() => _sig = "";

    public void OnStatus(ServiceStatusDto? s)
    {
        var views = s?.Accounts.Select(a => new DiskView(a)).ToList() ?? new();
        var sig = string.Join(";", views.Select(v => v.Signature));
        if (sig == _sig) return;
        _sig = sig;
        List.ItemsSource = views;
        Empty.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Id(object sender) => (string)((FrameworkElement)sender).Tag;

    private void Open_Click(object sender, RoutedEventArgs e) => AccountActions.OpenInExplorer(Id(sender));
    private async void Remount_Click(object sender, RoutedEventArgs e) => await AccountActions.Remount(Id(sender));
    private async void Letter_Click(object sender, RoutedEventArgs e) => await AccountActions.ChangeLetter(Id(sender));
    private async void Rename_Click(object sender, RoutedEventArgs e) => await AccountActions.Rename(Id(sender));
    private async void Toggle_Click(object sender, RoutedEventArgs e) => await AccountActions.Toggle(Id(sender));
}
