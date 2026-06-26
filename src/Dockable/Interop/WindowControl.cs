using System.Runtime.InteropServices;
using System.Windows;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>Helpers for driving a foreign window's minimize/restore for the genie effect.</summary>
public static class WindowControl
{
    /// <summary>
    /// Enables or disables the window's DWM min/max/restore transition animations. Disabling
    /// (before a minimize) lets our genie replace the OS animation instead of racing it.
    /// </summary>
    public static unsafe void SetTransitionsEnabled(IntPtr hwnd, bool enabled)
    {
        int forceDisabled = enabled ? 0 : 1;
        PInvoke.DwmSetWindowAttribute((HWND)hwnd,
            DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &forceDisabled, sizeof(int));
    }

    /// <summary>Disables the window's OS min/max/restore animations.</summary>
    public static void SuppressTransitions(IntPtr hwnd) => SetTransitionsEnabled(hwnd, false);

    public static bool IsWindow(IntPtr hwnd) => PInvoke.IsWindow((HWND)hwnd);

    /// <summary>The window's restored (non-minimized) bounds in physical pixels, or null.</summary>
    public static Int32Rect? GetRestoreRect(IntPtr hwnd)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!PInvoke.GetWindowPlacement((HWND)hwnd, ref placement))
            return null;

        var r = placement.rcNormalPosition;
        return new Int32Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    public static void Restore(IntPtr hwnd)
    {
        PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.SetForegroundWindow((HWND)hwnd);
    }

    /// <summary>Brings a window forward (restoring it first if minimized).</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (PInvoke.IsIconic((HWND)hwnd))
            PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.SetForegroundWindow((HWND)hwnd);
    }

    /// <summary>
    /// Brings every window in a group to the top of the z-order (restoring any minimized ones),
    /// then focuses the first — so clicking a grouped dock icon raises all its windows, not one.
    /// </summary>
    public static void ActivateAll(IReadOnlyList<IntPtr> hwnds)
    {
        if (hwnds.Count <= 1)
        {
            if (hwnds.Count == 1)
                Activate(hwnds[0]);
            return;
        }

        var hwndTop = new HWND(0); // HWND_TOP: top of the (non-topmost) z-order
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;

        // Raise back-to-front so hwnds[0] lands on top, then give it focus.
        for (int i = hwnds.Count - 1; i >= 0; i--)
        {
            var h = (HWND)hwnds[i];
            if (PInvoke.IsIconic(h))
                PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
            PInvoke.SetWindowPos(h, hwndTop, 0, 0, 0, 0, flags);
        }
        PInvoke.SetForegroundWindow((HWND)hwnds[0]);
    }
}
