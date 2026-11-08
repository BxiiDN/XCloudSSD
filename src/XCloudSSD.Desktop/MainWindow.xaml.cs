using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;
using XCloudSSD.Desktop.Pages;

namespace XCloudSSD.Desktop;

public interface IPage
{
    void OnShown();
    void OnStatus(ServiceStatusDto? status);
}

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private IPage? _current;
    private Action? _bannerAction;
    private readonly Dictionary<long, TransferState> _lastTransferStates = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        ServiceHub.Updated += OnStatus;
        Loc.Instance.PropertyChanged += (_, _) => OnStatus(ServiceHub.Last);
        Navigate("dashboard");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps XCloudSSD in the tray; the disks are served by the background service anyway.
        if (App.Exiting) return;
        e.Cancel = true;
        Hide();
    }

    public void Navigate(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "dashboard" => new DashboardPage(),
                "disks" => new DisksPage(),
                "accounts" => new AccountsPage(),
                "files" => new FileManagerPage(),
                "games" => new GameStoragePage(),
                "transfers" => new TransfersPage(),
                "performance" => new PerformancePage(),
                _ => new SettingsPage(),
            };
            _pages[tag] = page;
        }
        foreach (var rb in Nav.Children.OfType<RadioButton>())
            if ((string)rb.Tag == tag && rb.IsChecked != true) rb.IsChecked = true;
        PageHost.Content = page;
        _current = page as IPage;
        _current?.OnShown();
        _current?.OnStatus(ServiceHub.Last);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && PageHost is not null) Navigate(tag);
    }

    private void OnStatus(ServiceStatusDto? s)
    {
        if (s is null)
        {
            ServiceDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ErrBrush");
            ServiceText.Text = L.T("ServiceStopped");
            ServiceSub.Text = "";
            ShowBanner(L.T("BannerServiceStopped"), L.T("StartService"), ServiceHub.StartService);
        }
        else
        {
            var online = s.Accounts.Count(a => a.State == XCloudSSD.Core.ConnectionState.Online);
            ServiceDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, s.InternetAvailable ? "OkBrush" : "WarnBrush");
            ServiceText.Text = L.T("ServiceRunning");
            ServiceSub.Text = L.F("ServiceSub", online, s.Accounts.Count, s.WinFspVersion ?? "—");
            if (!s.WinFspInstalled)
                ShowBanner(L.T("BannerNoWinFsp"), L.T("Download"), () => ServiceHub.OpenUrl("https://winfsp.dev/rel/"));
            else if (!s.OAuthClientConfigured)
                ShowBanner(L.T("BannerNoOAuth"), L.T("OpenSettings"), () => Navigate("settings"));
            else
                Banner.Visibility = Visibility.Collapsed;
            NotifyTransferFailures();
        }
        _current?.OnStatus(s);
    }

    private async void NotifyTransferFailures()
    {
        if (!App.Prefs.Notifications) return;
        try
        {
            var list = await ServiceHub.Client.CallAsync<List<TransferDto>>(Commands.Transfers, null, 4000) ?? new();
            foreach (var t in list.Take(50))
            {
                var known = _lastTransferStates.TryGetValue(t.Id, out var prev);
                _lastTransferStates[t.Id] = t.State;
                if (known && prev != t.State && t.State is TransferState.Failed or TransferState.Offline)
                    ((App)Application.Current).Notify(L.T("NotifyTransferProblem"), $"{t.DriveLetter}:{t.Path} — {t.Error}");
            }
        }
        catch (Exception)
        {
        }
    }

    private void ShowBanner(string text, string button, Action action)
    {
        BannerText.Text = text;
        BannerButton.Content = button;
        _bannerAction = action;
        Banner.Visibility = Visibility.Visible;
    }

    private void BannerButton_Click(object sender, RoutedEventArgs e) => _bannerAction?.Invoke();
}
