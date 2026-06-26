using System.Windows;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Dockable.Interop;

/// <summary>
/// Registers the dock as a Win32 AppBar so it can reserve screen space along an edge
/// (the always-visible docking mode), pushing maximized windows out of the way.
/// Coordinates are physical pixels. Auto-hide is handled separately by the window
/// (a custom slide), not via ABM_SETAUTOHIDEBAR.
/// </summary>
public sealed class AppBarManager
{
    private readonly IntPtr _hwnd;
    private readonly uint _callbackMessage;
    private bool _registered;

    /// <param name="callbackMessage">Private window message the shell uses for ABN_* notifications.</param>
    public AppBarManager(IntPtr hwnd, uint callbackMessage)
    {
        _hwnd = hwnd;
        _callbackMessage = callbackMessage;
    }

    public bool IsRegistered => _registered;

    public unsafe void Register()
    {
        if (_registered)
            return;
        var data = NewData();
        PInvoke.SHAppBarMessage(PInvoke.ABM_NEW, &data);
        _registered = true;
    }

    public unsafe void Unregister()
    {
        if (!_registered)
            return;
        var data = NewData();
        PInvoke.SHAppBarMessage(PInvoke.ABM_REMOVE, &data);
        _registered = false;
    }

    /// <summary>
    /// Reserves a strip of <paramref name="thicknessPx"/> along the bottom of
    /// <paramref name="monitorPx"/> and returns the granted rectangle (physical pixels).
    /// </summary>
    public unsafe Int32Rect ReserveBottom(Rect monitorPx, int thicknessPx)
    {
        var data = NewData();
        data.uEdge = PInvoke.ABE_BOTTOM;
        data.rc = new RECT
        {
            left = (int)Math.Round(monitorPx.Left),
            top = (int)Math.Round(monitorPx.Bottom) - thicknessPx,
            right = (int)Math.Round(monitorPx.Right),
            bottom = (int)Math.Round(monitorPx.Bottom),
        };

        // QUERYPOS lets the shell adjust the proposed rect around existing appbars
        // (e.g. the taskbar); we then re-assert our thickness before SETPOS grants it.
        PInvoke.SHAppBarMessage(PInvoke.ABM_QUERYPOS, &data);
        data.rc.top = data.rc.bottom - thicknessPx;
        PInvoke.SHAppBarMessage(PInvoke.ABM_SETPOS, &data);

        return new Int32Rect(data.rc.left, data.rc.top,
            data.rc.right - data.rc.left, data.rc.bottom - data.rc.top);
    }

    /// <summary>Tells the shell the appbar window moved, so it re-validates the layout.</summary>
    public unsafe void NotifyPosChanged()
    {
        if (!_registered)
            return;
        var data = NewData();
        PInvoke.SHAppBarMessage(PInvoke.ABM_WINDOWPOSCHANGED, &data);
    }

    private unsafe APPBARDATA NewData() => new()
    {
        cbSize = (uint)sizeof(APPBARDATA),
        hWnd = (HWND)_hwnd,
        uCallbackMessage = _callbackMessage,
    };
}
