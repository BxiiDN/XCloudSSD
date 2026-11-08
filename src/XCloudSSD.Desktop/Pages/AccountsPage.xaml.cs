using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop.Pages;

public partial class AccountsPage : UserControl, IPage
{
    private string _sig = "";

    public AccountsPage() => InitializeComponent();

    public void OnShown() => _sig = "";

    public void OnStatus(ServiceStatusDto? s)
    {
        var views = s?.Accounts.Select(a => new DiskView(a)).ToList() ?? new();
        var sig = string.Join(";", views.Select(v => v.Signature));
        if (sig == _sig) return;
        _sig = sig;
        Items.ItemsSource = views;
        Empty.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Id(object sender) => (string)((FrameworkElement)sender).Tag;

    private async void AddGoogle_Click(object sender, RoutedEventArgs e) => await AccountActions.AddGoogle();
    private void AddTeraBox_Click(object sender, RoutedEventArgs e) => AccountActions.AddTeraBox();
    private void Info_Click(object sender, RoutedEventArgs e) => AccountActions.Info(Id(sender));
    private async void Rename_Click(object sender, RoutedEventArgs e) => await AccountActions.Rename(Id(sender));
    private async void Toggle_Click(object sender, RoutedEventArgs e) => await AccountActions.Toggle(Id(sender));
    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await AccountActions.Reconnect(Id(sender));
    private async void Remove_Click(object sender, RoutedEventArgs e) => await AccountActions.Remove(Id(sender));
}
