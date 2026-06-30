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

    public static bool IsIconic(IntPtr hwnd) => PInvoke.IsIconic((HWND)hwnd);

    private const uint WM_CLOSE = 0x0010;

    /// <summary>Gracefully asks a window to close (posts WM_CLOSE; the app can prompt to save).</summary>
    public static void Close(IntPtr hwnd) => PInvoke.PostMessage((HWND)hwnd, WM_CLOSE, default, default);

    /// <summary>The id of the process that owns a window (0 if it can't be determined).</summary>
    public static unsafe uint GetProcessId(IntPtr hwnd)
    {
        uint pid = 0;
        PInvoke.GetWindowThreadProcessId((HWND)hwnd, &pid);
        return pid;
    }

    /// <summary>
    /// Restores a minimized window to its previous bounds WITHOUT activating it (focus and z-order
    /// to top are left untouched). Used to momentarily refill the spot a just-minimized window left
    /// behind while the genie overlay paints its first frame — with transitions suppressed it's
    /// instant, so no OS restore animation plays.
    /// </summary>
    public static void ShowNoActivate(IntPtr hwnd) =>
        PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

    /// <summary>
    /// Minimizes a window WITHOUT activating the next window (focus is left untouched). With OS
    /// transitions suppressed it's instant, so it can run hidden behind the genie overlay.
    /// </summary>
    public static void MinimizeNoActivate(IntPtr hwnd) =>
        PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE);

    /// <summary>
    /// Minimizes a window and activates the next top-level window (normal minimize focus behaviour).
    /// With OS transitions suppressed it's instant — used to drive the dock's own Preferences window
    /// into the genie/scale warp.
    /// </summary>
    public static void Minimize(IntPtr hwnd) =>
        PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);

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

    /// <summary>Un-minimizes a window without forcing it to the foreground — used to restore every
    /// dock-minimized window on exit without thrashing focus across all of them.</summary>
    public static void RestoreNoForeground(IntPtr hwnd) =>
        PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

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
