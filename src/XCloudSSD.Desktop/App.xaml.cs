using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop;

public partial class App : Application
{
    private const string InstanceMutex = "Local\\XCloudSSD.Desktop.SingleInstance";
    private const string ShowEvent = "Local\\XCloudSSD.Desktop.Show";
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private System.Windows.Forms.NotifyIcon? _tray;

    public static DesktopSettings Prefs { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, InstanceMutex, out var first);
        if (!first)
        {
            // Another instance is running: ask it to show its window.
            try
            {
                EventWaitHandle.OpenExisting(ShowEvent).Set();
            }
            catch (Exception)
            {
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (MainWindow is not null) Dialogs.Error(MainWindow, args.Exception);
        };

        Prefs = DesktopSettings.Load();
        var lang = string.IsNullOrEmpty(Prefs.Language)
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "id" ? "id" : "en")
            : Prefs.Language;
        Loc.Instance.Load(lang);
        ApplyTheme(Prefs.Theme);

        ServiceHub.Start();
        var main = new MainWindow();
        MainWindow = main;
        CreateTray(main);

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        var waiter = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.Invoke(() => ShowMain(main));
        }) { IsBackground = true };
        waiter.Start();

        if (!e.Args.Contains("--tray")) main.Show();
    }

    public static void ShowMain(Window main)
    {
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }

    private void CreateTray(MainWindow main)
    {
        var icon = GetResourceStream(new Uri("pack://application:,,,/Assets/xcloudssd.ico"))?.Stream;
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "XCloudSSD",
            Icon = icon is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(icon),
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(L.T("TrayOpen"), null, (_, _) => ShowMain(main));
        menu.Items.Add(L.T("TrayExit"), null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain(main);
    }

    public void Notify(string title, string text)
    {
        if (!Prefs.Notifications || _tray is null) return;
        _tray.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    public void ExitApp()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    // ------------------------------------------------------------------ theme

    public static void ApplyTheme(string theme)
    {
        var dark = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        var app = Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        void Set(string key, string hex) => app.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        if (dark)
        {
            Set("AppBackground", "#0B0B0C"); Set("SidebarBrush", "#111113"); Set("SurfaceBrush", "#18181B");
            Set("Surface2Brush", "#232327"); Set("LineBrush", "#2E2E33"); Set("TextBrush", "#F4F4F5");
            Set("DimBrush", "#A1A1AA"); Set("AccentBrush", "#F4F4F5"); Set("AccentFgBrush", "#0B0B0C");
        }
        else
        {
            Set("AppBackground", "#F4F4F5"); Set("SidebarBrush", "#EBEBED"); Set("SurfaceBrush", "#FFFFFF");
            Set("Surface2Brush", "#E4E4E7"); Set("LineBrush", "#D4D4D8"); Set("TextBrush", "#0B0B0C");
            Set("DimBrush", "#52525B"); Set("AccentBrush", "#18181B"); Set("AccentFgBrush", "#FAFAFA");
        }

        // Styles derived from the Fluent control styles (created after ThemeMode loaded them).
        if (!app.Resources.Contains("AccentButton"))
        {
            var baseButton = app.TryFindResource(typeof(Button)) as Style;
            var accent = new Style(typeof(Button), baseButton);
            accent.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("AccentBrush")));
            accent.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AccentFgBrush")));
            accent.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            accent.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 6, 14, 6)));
            app.Resources["AccentButton"] = accent;

            var tool = new Style(typeof(Button), baseButton);
            tool.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 6)));
            tool.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 5, 12, 5)));
            app.Resources["ToolButton"] = tool;

            var meter = new Style(typeof(ProgressBar), app.TryFindResource(typeof(ProgressBar)) as Style);
            meter.Setters.Add(new Setter(FrameworkElement.HeightProperty, 6.0));
            meter.Setters.Add(new Setter(RangeBase.MinimumProperty, 0.0));
            meter.Setters.Add(new Setter(RangeBase.MaximumProperty, 1.0));
            meter.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 10, 0, 6)));
            app.Resources["Meter"] = meter;
        }

        WindowEffects.Dark = dark;
        foreach (Window w in app.Windows) WindowEffects.Apply(w);
    }
}
