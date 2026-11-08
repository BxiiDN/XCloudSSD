using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;

namespace XCloudSSD.Desktop.Infrastructure;

/// <summary>Single connection point to XCloudSSDService; polls status for every page.</summary>
public static class ServiceHub
{
    public static PipeClient Client { get; } = new();
    public static ServiceStatusDto? Last { get; private set; }
    public static bool Connected { get; private set; }
    public static event Action<ServiceStatusDto?>? Updated;

    private static DispatcherTimer? _timer;
    private static bool _polling;

    public static void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public static async Task RefreshAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            Last = await Client.CallAsync<ServiceStatusDto>(Commands.Status, null, 4000);
            Connected = true;
        }
        catch (Exception)
        {
            Connected = false;
            Last = null;
        }
        finally
        {
            _polling = false;
        }
        Updated?.Invoke(Last);
    }

    /// <summary>Starts the Windows service (asks for elevation).</summary>
    public static void StartService()
    {
        try
        {
            Process.Start(new ProcessStartInfo("sc.exe", $"start {AppPaths.ServiceName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception)
        {
            // User declined elevation.
        }
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
