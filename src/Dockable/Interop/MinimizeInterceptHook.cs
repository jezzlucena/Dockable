using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>
/// Catches the user's <em>intent</em> to minimize a window <b>before</b> the OS acts on it, so the
/// dock can paint the captured "frame 0" of the genie/scale warp over the still-visible window and
/// only then drive the minimize itself — eliminating the flash where the real window disappears for
/// a beat before our buffered capture appears.
///
/// There is no OS event that fires <i>before</i> a minimize, so we intercept the two ways a user
/// triggers one:
/// <list type="bullet">
/// <item>A click on a window's <b>minimize button</b> — a low-level mouse hook (WH_MOUSE_LL). On the
/// left-button-<i>down</i> it asks the window under the cursor (via <c>WM_NCHITTEST</c> / DWM caption
/// bounds) whether that point is its minimize button; if so it swallows the down (so the native button
/// never starts its modal press-tracking loop) and arms. On the release it swallows the up too and, if
/// the release is still on that button, raises <see cref="MinimizeRequested"/> — releasing elsewhere
/// cancels, just like a real button. (We must swallow the <i>down</i>, not just the up: a native
/// caption button enters a modal loop on the down and waits for the up, so swallowing only the up would
/// hang that loop and hold the mouse capture, leaving everything unresponsive.)</item>
/// <item>The <b>keyboard shortcuts</b> Win+Down (minimize the active window), Win+M (minimize all) and
/// Win+D (show desktop — a minimize-all/restore-all toggle) — a low-level keyboard hook
/// (WH_KEYBOARD_LL) swallows them and raises the matching event. Win+Down on a <i>maximized</i> window
/// is left for the OS (it un-maximizes, it doesn't minimize).</item>
/// </list>
///
/// Callbacks are delivered on the thread that installed the hooks (the UI thread, which has a message
/// pump), so subscribers should defer the actual minimize work (e.g. <c>Dispatcher.BeginInvoke</c>)
/// to return from the hook quickly — low-level hooks have a strict processing timeout.
/// </summary>
public sealed class MinimizeInterceptHook : IDisposable
{
    private const int HC_ACTION = 0;

    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYUP = 0x0105;

    private const uint WM_NCHITTEST = 0x0084;
    private const nuint HTMINBUTTON = 8;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_SHIFT = 0x10;
    private const int VK_DOWN = 0x28;
    private const int VK_M = 0x4D;
    private const int VK_D = 0x44;
    private const int VK_S = 0x53;
    private const int VK_SNAPSHOT = 0x2C;

    private readonly HOOKPROC _mouseProc;    // held to keep the delegates alive for the hooks
    private readonly HOOKPROC _keyboardProc;
    private readonly uint _ownProcessId;

    private UnhookWindowsHookExSafeHandle? _mouseHook;
    private UnhookWindowsHookExSafeHandle? _keyboardHook;

    // True while a Win+key combo is held, so auto-repeat doesn't re-fire it.
    private bool _winDownArmed;
    private bool _winMArmed;
    private bool _winDArmed;
    private bool _snipArmed;

    // The window whose minimize-button press we swallowed; the release decides whether to minimize it.
    private IntPtr _armedMinimize;

    /// <summary>Fires with the HWND whose minimize the user just triggered (min-button click or Win+Down).</summary>
    public event Action<IntPtr>? MinimizeRequested;

    /// <summary>Fires when the user pressed Win+M (minimize every window).</summary>
    public event Action? MinimizeAllRequested;

    /// <summary>Fires when the user pressed Win+D (show desktop) — a toggle: minimize every window if
    /// any is open, otherwise restore the ones we minimized. The subscriber decides the direction.</summary>
    public event Action? ShowDesktopRequested;

    /// <summary>Fires when the user triggers an OS screen capture — Win+Shift+S or PrintScreen (which
    /// opens the Snipping overlay on Win11 by default). Observe-only: the keystroke is NEVER swallowed;
    /// this only gives the dock a head start to lift its capture exclusion before the overlay grabs
    /// the screen.</summary>
    public event Action? ScreenSnipRequested;

    public MinimizeInterceptHook()
    {
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
        _ownProcessId = (uint)Environment.ProcessId;
    }

    public void Start()
    {
        if (_mouseHook is { IsInvalid: false })
            return;
        // hMod = our module; dwThreadId = 0 makes both global low-level hooks.
        var module = PInvoke.GetModuleHandle((string?)null);
        _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardProc, module, 0);
    }

    private LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code == HC_ACTION)
        {
            if (wParam.Value == WM_LBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>((IntPtr)lParam.Value);
                if (IsOnMinimizeButton(data.pt, out IntPtr hwnd))
                {
                    // Swallow the press so the native caption button never enters its modal tracking
                    // loop; arm and let the release decide. (Hover/highlight still passes through — we
                    // only swallow the button events, not moves.)
                    _armedMinimize = hwnd;
                    return new LRESULT(1);
                }
                _armedMinimize = IntPtr.Zero;
            }
            else if (wParam.Value == WM_LBUTTONUP && _armedMinimize != IntPtr.Zero)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>((IntPtr)lParam.Value);
                IntPtr armed = _armedMinimize;
                _armedMinimize = IntPtr.Zero;
                // Minimize only if the release is still on that same window's minimize button; releasing
                // anywhere else cancels (drag-off), exactly like a real button. Either way the release is
                // swallowed to balance the swallowed press.
                if (IsOnMinimizeButton(data.pt, out IntPtr upHwnd) && upHwnd == armed)
                    MinimizeRequested?.Invoke(armed);
                return new LRESULT(1);
            }
        }
        return PInvoke.CallNextHookEx((HHOOK)default, code, wParam, lParam);
    }

    /// <summary>
    /// Is the screen point <paramref name="pt"/> over the minimize button of a normal app window?
    /// Two strategies, because no single one covers every title bar:
    /// <list type="number">
    /// <item><c>WM_NCHITTEST</c> == HTMINBUTTON — classic frames plus apps that hit-test their own
    /// caption buttons (Chrome, Cursor).</item>
    /// <item>The click falls in the left third of DWM's caption-button cluster
    /// (<c>DWMWA_CAPTION_BUTTON_BOUNDS</c>) — apps whose buttons DWM lays out but that report
    /// HTCAPTION/HTCLIENT to a cross-process hit-test (File Explorer, Windows Terminal).</item>
    /// </list>
    /// Anything neither catches falls through to the existing post-minimize path.
    /// </summary>
    private unsafe bool IsOnMinimizeButton(System.Drawing.Point pt, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        var under = PInvoke.WindowFromPoint(pt);
        if (under.IsNull)
            return false;
        var root = PInvoke.GetAncestor(under, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (!WindowFilter.IsEligibleAppWindow(root, _ownProcessId))
            return false;

        // Pack the screen point as a WM_NCHITTEST LPARAM (low word = x, high word = y; both masked so
        // negative multi-monitor coordinates round-trip). Use a short timeout so a hung target can't
        // stall the hook (which the OS would then silently remove).
        nint hitLParam = (int)(((uint)(pt.Y & 0xFFFF) << 16) | (uint)(pt.X & 0xFFFF));
        nuint result = 0;
        var ret = PInvoke.SendMessageTimeout(root, WM_NCHITTEST, default, new LPARAM(hitLParam),
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 60, &result);

        if ((ret != 0 && result == HTMINBUTTON) || IsInCaptionMinButton(root, pt))
        {
            hwnd = root;
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="pt"/> (screen px) lands on the minimize button as laid out by DWM.
    /// <c>DWMWA_CAPTION_BUTTON_BOUNDS</c> gives the whole min/max/close cluster in window-relative
    /// coordinates; minimize is the leftmost of the three (LTR), so we take the left third.
    /// </summary>
    private static unsafe bool IsInCaptionMinButton(HWND root, System.Drawing.Point pt)
    {
        var bounds = default(RECT);
        var hr = PInvoke.DwmGetWindowAttribute(root, DWMWINDOWATTRIBUTE.DWMWA_CAPTION_BUTTON_BOUNDS,
            &bounds, (uint)sizeof(RECT));
        int w = bounds.right - bounds.left, h = bounds.bottom - bounds.top;
        if (hr.Failed || w <= 0 || h <= 0)
            return false;

        // Bounds are relative to the window origin (GetWindowRect, which includes the shadow border —
        // the same origin DWM measures from). Offset into screen space to test the cursor.
        RECT wr;
        if (!PInvoke.GetWindowRect(root, &wr))
            return false;
        int left = wr.left + bounds.left, top = wr.top + bounds.top;
        int right = left + w, bottom = top + h;
        bool inCluster = pt.X >= left && pt.X < right && pt.Y >= top && pt.Y < bottom;
        return inCluster && pt.X < left + w / 3; // minimize is the leftmost of the LTR cluster
    }

    private LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code == HC_ACTION)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>((IntPtr)lParam.Value);
            uint msg = (uint)wParam.Value;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (data.vkCode == VK_DOWN)
            {
                if (up) _winDownArmed = false;
                else if (down && !_winDownArmed && WinHeld() && TryMinimizeForeground())
                {
                    _winDownArmed = true;
                    return new LRESULT(1); // handled Win+Down ourselves
                }
            }
            else if (data.vkCode == VK_M)
            {
                if (up) _winMArmed = false;
                else if (down && !_winMArmed && WinHeld())
                {
                    _winMArmed = true;
                    MinimizeAllRequested?.Invoke();
                    return new LRESULT(1); // handled Win+M ourselves
                }
            }
            else if (data.vkCode == VK_D)
            {
                if (up) _winDArmed = false;
                else if (down && !_winDArmed && WinHeld())
                {
                    _winDArmed = true;
                    ShowDesktopRequested?.Invoke();
                    return new LRESULT(1); // handled Win+D (show desktop) ourselves
                }
            }
            else if (data.vkCode == VK_S || data.vkCode == VK_SNAPSHOT)
            {
                if (up) _snipArmed = false;
                else if (down && !_snipArmed
                    && (data.vkCode == VK_SNAPSHOT || (WinHeld() && ShiftHeld())))
                {
                    _snipArmed = true;
                    ScreenSnipRequested?.Invoke(); // observe-only — fall through, never swallow
                }
            }
        }
        return PInvoke.CallNextHookEx((HHOOK)default, code, wParam, lParam);
    }

    private static bool WinHeld()
        => (PInvoke.GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
        || (PInvoke.GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    private static bool ShiftHeld()
        => (PInvoke.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    /// <summary>
    /// Win+Down: if the foreground window is a normal app window, claim the minimize (returns true so we
    /// swallow the keystroke and drive the warp). Minimizes maximized windows too — a single Win+Down
    /// always minimizes, rather than the OS two-stage "un-maximize first, minimize on the second press."
    /// </summary>
    private bool TryMinimizeForeground()
    {
        var fg = PInvoke.GetForegroundWindow();
        if (!WindowFilter.IsEligibleAppWindow(fg, _ownProcessId))
            return false;
        MinimizeRequested?.Invoke((IntPtr)fg);
        return true;
    }

    public void Dispose()
    {
        _mouseHook?.Dispose();
        _keyboardHook?.Dispose();
        _mouseHook = null;
        _keyboardHook = null;
    }
}
