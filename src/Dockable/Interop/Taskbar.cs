using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>
/// Controls the Windows taskbar's native auto-hide state via the shell AppBar API. Auto-hide is
/// far safer than force-hiding the tray window: even if Dockable is force-killed without running
/// its exit handlers, the taskbar is left in a normal, usable auto-hide mode (it reveals when the
/// cursor reaches the screen edge) rather than vanishing entirely.
/// </summary>
public static class Taskbar
{
    private const string PrimaryClass = "Shell_TrayWnd";
    private const string SecondaryClass = "Shell_SecondaryTrayWnd";

    // ABM_SETSTATE/ABM_GETSTATE flags (not surfaced as CsWin32 constants).
    private const int ABS_AUTOHIDE = 0x1;
    private const int ABS_ALWAYSONTOP = 0x2;

    // The taskbar's auto-hide state when the app first touched it, so we can restore it on exit
    // (rather than assuming a default). TODO: this will be driven by an app setting later.
    private static bool? _originalAutoHide;

    /// <summary>Whether the taskbar currently has native auto-hide enabled.</summary>
    public static bool IsAutoHide()
    {
        try
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = PInvoke.FindWindow(PrimaryClass, null!),
            };
            uint state = (uint)PInvoke.SHAppBarMessage(PInvoke.ABM_GETSTATE, ref data);
            return (state & ABS_AUTOHIDE) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Remembers the taskbar's auto-hide state at startup (once) so <see cref="Restore"/> can put
    /// it back to whatever the user had, instead of assuming a default.
    /// </summary>
    public static void CaptureOriginalState() => _originalAutoHide ??= IsAutoHide();

    /// <summary>Restores the taskbar to the state captured by <see cref="CaptureOriginalState"/>.</summary>
    public static void Restore()
    {
        if (_originalAutoHide is bool original)
            SetAutoHide(original);
    }

    /// <summary>
    /// Turns the taskbar's auto-hide on (it slides away and reveals on edge hover) or off
    /// (restores the normal always-visible taskbar). Best-effort; non-fatal on failure.
    /// </summary>
    public static void SetAutoHide(bool enable)
    {
        try
        {
            // Undo any legacy force-hide (older builds used ShowWindow(SW_HIDE)); auto-hide only
            // works on a window that is itself shown.
            EnsureTrayWindowsShown();

            HWND tray = PInvoke.FindWindow(PrimaryClass, null!);
            if (tray.IsNull)
                return;

            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = tray,
                lParam = new LPARAM(enable ? ABS_AUTOHIDE : ABS_ALWAYSONTOP),
            };
            PInvoke.SHAppBarMessage(PInvoke.ABM_SETSTATE, ref data);
        }
        catch
        {
            // Toggling auto-hide is best-effort; never let it crash the app.
        }
    }

    private static void EnsureTrayWindowsShown()
    {
        HWND primary = PInvoke.FindWindow(PrimaryClass, null!);
        if (!primary.IsNull)
            PInvoke.ShowWindow(primary, SHOW_WINDOW_CMD.SW_SHOW);

        // Secondary taskbars (one per additional monitor) have no findable title.
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (GetClassName(hwnd) == SecondaryClass)
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOW);
            return true; // keep enumerating
        }, default);
    }

    private static unsafe string GetClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length = PInvoke.GetClassName(hwnd, buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }
}
