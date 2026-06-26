using System.Windows;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Dockable.Interop;

/// <summary>
/// Per-monitor geometry and DPI for the monitor a window currently sits on.
/// Rectangles are in physical pixels (virtual-screen coordinates), as required by
/// the AppBar APIs; <see cref="Info.Scale"/> converts between pixels and WPF DIPs.
/// </summary>
public static class Monitors
{
    public readonly record struct Info(Rect MonitorPx, Rect WorkPx, double Dpi)
    {
        /// <summary>Pixels-per-DIP (1.0 at 96 DPI / 100% scaling).</summary>
        public double Scale => Dpi / 96.0;
    }

    public static Info ForWindow(IntPtr hwnd)
    {
        var monitor = PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        PInvoke.GetMonitorInfo(monitor, ref mi);

        uint dpi = PInvoke.GetDpiForWindow((HWND)hwnd);
        if (dpi == 0)
            dpi = 96;

        return new Info(ToRect(mi.rcMonitor), ToRect(mi.rcWork), dpi);
    }

    private static Rect ToRect(RECT r) => new(r.left, r.top, r.right - r.left, r.bottom - r.top);
}
