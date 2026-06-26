using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>Shared test for "a normal, user-facing top-level window we should manage."</summary>
internal static class WindowFilter
{
    internal static unsafe bool IsEligibleAppWindow(HWND hwnd, uint ownProcessId)
    {
        if (hwnd.IsNull)
            return false;

        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0 || pid == ownProcessId)
            return false;

        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.GetWindowTextLength(hwnd) == 0)
            return false;

        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        return (exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) == 0;
    }
}
