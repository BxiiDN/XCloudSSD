using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace XCloudSSD.Desktop.Infrastructure;

/// <summary>Windows 11 window chrome: dark title bar and rounded corners (ignored on older builds).</summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool Dark { get; set; } = true;

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows: keep default chrome.
        }
    }
}
