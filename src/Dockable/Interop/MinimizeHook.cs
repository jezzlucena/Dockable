using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Dockable.Interop;

/// <summary>
/// Raises <see cref="WindowMinimizing"/> when any other process's top-level app window starts to
/// minimize and <see cref="WindowUnminimized"/> when one is restored (e.g. via the taskbar or
/// Alt+Tab), via a WinEvent hook over EVENT_SYSTEM_MINIMIZESTART..EVENT_SYSTEM_MINIMIZEEND. With
/// WINEVENT_OUTOFCONTEXT the callback is delivered on the registering (UI) thread, so no marshalling
/// is needed.
/// </summary>
public sealed class MinimizeHook : IDisposable
{
    private readonly WINEVENTPROC _proc; // held to keep the delegate alive for the hook
    private readonly uint _ownProcessId;
    private UnhookWinEventSafeHandle? _hook;

    /// <summary>Fires with the HWND of a window beginning to minimize.</summary>
    public event Action<IntPtr>? WindowMinimizing;

    /// <summary>Fires with the HWND of a window that was just un-minimized (restored) by anything other
    /// than the dock — so a stale minimized tile can be cleared.</summary>
    public event Action<IntPtr>? WindowUnminimized;

    public MinimizeHook()
    {
        _proc = OnWinEvent;
        _ownProcessId = (uint)Environment.ProcessId;
    }

    public void Start()
    {
        if (_hook is { IsInvalid: false })
            return;
        _hook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND,
            default, _proc, idProcess: 0, idThread: 0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild,
        uint idEventThread, uint dwmsEventTime)
    {
        // OBJID_WINDOW (0) / CHILDID_SELF (0): the window itself, not a child UI element.
        if (idObject != 0 || idChild != 0)
            return;
        if (@event == PInvoke.EVENT_SYSTEM_MINIMIZESTART)
        {
            if (WindowFilter.IsEligibleAppWindow(hwnd, _ownProcessId))
                WindowMinimizing?.Invoke(hwnd);
        }
        else // EVENT_SYSTEM_MINIMIZEEND — raise for any window; the dock only acts if it tracks it.
        {
            WindowUnminimized?.Invoke((IntPtr)hwnd);
        }
    }

    public void Dispose()
    {
        _hook?.Dispose(); // SafeHandle calls UnhookWinEvent
        _hook = null;
    }
}
