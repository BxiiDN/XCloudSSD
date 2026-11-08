using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XCloudSSD.TeraBox;

namespace XCloudSSD.Desktop.Infrastructure;

/// <summary>
/// Shows TeraBox's own website inside XCloudSSD. The user signs in there (XCloudSSD never sees the password);
/// once TeraBox has created its session cookie ("ndus"), the window closes and returns the cookie header so the
/// service can mount the account. Each sign-in uses a fresh, temporary browser profile that is deleted afterwards,
/// so several different TeraBox accounts can be added.
/// </summary>
public sealed class TeraBoxLoginWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 8, 12, 8), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _profileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XCloudSSD", "WebView2", Guid.NewGuid().ToString("N"));

    public string? CookieHeader { get; private set; }

    public TeraBoxLoginWindow(Window owner)
    {
        Owner = owner;
        Title = L.T("TeraBoxLoginTitle");
        Width = 1100;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackground");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
        _status.Text = L.T("TeraBoxLoginHint");
        var dock = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        dock.Children.Add(_status);
        dock.Children.Add(_web);
        Content = dock;
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Cleanup();
        _poll.Tick += async (_, _) => await CheckCookiesAsync();
    }

    public static bool RuntimeAvailable(out string? version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch (Exception)
        {
            version = null;
            return false;
        }
    }

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(_profileDir);
            var env = await CoreWebView2Environment.CreateAsync(null, _profileDir);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Navigate(TeraBoxInfo.LoginUrl);
            _poll.Start();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private bool _checking;

    private async Task CheckCookiesAsync()
    {
        if (_checking || _web.CoreWebView2 is null) return;
        _checking = true;
        try
        {
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(TeraBoxInfo.CookieDomain);
            if (cookies.Any(c => c.Name == "ndus" && !string.IsNullOrEmpty(c.Value)))
            {
                _poll.Stop();
                CookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                _status.Text = L.T("TeraBoxLoginDone");
                DialogResult = true;
            }
        }
        catch (Exception)
        {
            // The page may still be loading; try again on the next tick.
        }
        finally
        {
            _checking = false;
        }
    }

    private void Cleanup()
    {
        _poll.Stop();
        try
        {
            _web.Dispose();
        }
        catch (Exception)
        {
        }
        // Remove the temporary browser profile (the session lives on only as the encrypted cookie in the service).
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, recursive: true);
                    return;
                }
                catch (Exception)
                {
                    await Task.Delay(1000);
                }
            }
        });
    }
}
