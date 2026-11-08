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
    /// <summary>window.jsToken of the signed-in page (needed by TeraBox's web interface).</summary>
    public string? JsToken { get; private set; }
    /// <summary>Origin of the signed-in page (TeraBox may serve some regions from another host).</summary>
    public string? BaseUrl { get; private set; }

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
            var origin = CurrentOrigin();
            var cookies = (await _web.CoreWebView2.CookieManager.GetCookiesAsync(TeraBoxInfo.CookieDomain)).ToList();
            if (origin is not null && !origin.Equals(TeraBoxInfo.CookieDomain, StringComparison.OrdinalIgnoreCase))
                cookies.AddRange(await _web.CoreWebView2.CookieManager.GetCookiesAsync(origin));
            if (!cookies.Any(c => c.Name == "ndus" && !string.IsNullOrEmpty(c.Value))) return;

            _poll.Stop();
            _status.Text = L.T("TeraBoxLoginDone");
            CookieHeader = string.Join("; ", cookies.GroupBy(c => c.Name).Select(g => $"{g.Key}={g.Last().Value}"));
            BaseUrl = origin ?? TeraBoxInfo.CookieDomain;
            JsToken = await ReadJsTokenAsync();
            if (string.IsNullOrEmpty(JsToken))
            {
                // Open the main file page (it defines window.jsToken), then read it again.
                _web.CoreWebView2.Navigate(BaseUrl + "/main");
                for (var i = 0; i < 20 && string.IsNullOrEmpty(JsToken); i++)
                {
                    await Task.Delay(750);
                    JsToken = await ReadJsTokenAsync();
                }
                BaseUrl = CurrentOrigin() ?? BaseUrl;
            }
            DialogResult = true;
        }
        catch (Exception)
        {
            // The page may still be loading; try again on the next tick.
            if (CookieHeader is null) _poll.Start();
        }
        finally
        {
            _checking = false;
        }
    }

    private string? CurrentOrigin()
    {
        var src = _web.CoreWebView2?.Source;
        return Uri.TryCreate(src, UriKind.Absolute, out var u) && u.Scheme == "https" &&
               (u.Host.EndsWith("terabox.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("1024tera.com", StringComparison.OrdinalIgnoreCase) ||
                u.Host.EndsWith("1024terabox.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("terabox.app", StringComparison.OrdinalIgnoreCase))
            ? $"https://{u.Host}"
            : null;
    }

    private async Task<string?> ReadJsTokenAsync()
    {
        try
        {
            var json = await _web.CoreWebView2.ExecuteScriptAsync("(typeof window.jsToken === 'string') ? window.jsToken : null");
            var value = System.Text.Json.JsonSerializer.Deserialize<string?>(json);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
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
