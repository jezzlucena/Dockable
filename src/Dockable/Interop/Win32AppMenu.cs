using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>
/// Tier-1 "global menu": reads another window's classic Win32 menu bar and hosts its dropdowns from
/// our own window. Menus are shared USER objects, so <c>GetMenu</c>/<c>GetMenuString</c> work
/// cross-process without injection, and a foreign HMENU can be tracked locally with
/// <c>TrackPopupMenuEx</c>; the chosen command is then posted back to the owning window.
/// </summary>
internal static class Win32AppMenu
{
    private const uint WM_NULL = 0x0000;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_INITMENUPOPUP = 0x0117;
    private const uint WM_UNINITMENUPOPUP = 0x0125;
    private const uint WM_MENUCOMMAND = 0x0126;

    // Sends into the target app are timeout-guarded so a hung app can never freeze the bar.
    private const uint SendTimeoutMs = 300;

    // The window whose menu we're currently tracking; WndProc-relayed menu messages go here.
    private static HWND _forwardTarget;

    /// <summary>Reads the top-level items of <paramref name="hwnd"/>'s Win32 menu bar, or null when
    /// the window has none (WPF/UWP/Electron apps — try the UIA tier next).</summary>
    public static unsafe List<AppMenuEntry>? TryRead(IntPtr hwnd)
    {
        HMENU bar = PInvoke.GetMenu((HWND)hwnd);
        if (bar.IsNull || !PInvoke.IsMenu(bar))
            return null;
        int count = PInvoke.GetMenuItemCount(bar);
        if (count <= 0)
            return null;

        var entries = new List<AppMenuEntry>(count);
        Span<char> buffer = stackalloc char[128];
        for (int i = 0; i < count; i++)
        {
            int len;
            fixed (char* p = buffer)
                len = PInvoke.GetMenuString(bar, (uint)i, new PWSTR(p), buffer.Length, MENU_ITEM_FLAGS.MF_BYPOSITION);
            if (len <= 0)
                continue; // separator or owner-drawn item (no string) — nothing we can render
            string label = CleanLabel(buffer[..len]);
            if (label.Length > 0)
                entries.Add(new AppMenuEntry(label, i, AppMenuSource.Win32));
        }
        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    /// Opens the dropdown at position <paramref name="index"/> of <paramref name="targetHwnd"/>'s menu
    /// bar, anchored at screen pixel (<paramref name="xPx"/>, <paramref name="yPx"/>), hosted by
    /// <paramref name="ownerHwnd"/> (the menu-bar window). Blocks in the menu's modal loop; call on the
    /// UI thread. The chosen command is posted back to the target.
    /// </summary>
    public static unsafe void Show(IntPtr targetHwnd, int index, int xPx, int yPx, IntPtr ownerHwnd)
    {
        var target = (HWND)targetHwnd;
        var owner = (HWND)ownerHwnd;
        if (!PInvoke.IsWindow(target))
            return;
        HMENU bar = PInvoke.GetMenu(target);
        if (bar.IsNull)
            return;

        HMENU sub = PInvoke.GetSubMenu(bar, index);
        if (sub.IsNull)
        {
            // A top-level command item with no dropdown (some apps put e.g. "Help" directly on the bar).
            uint id = PInvoke.GetMenuItemID(bar, index);
            if (id != uint.MaxValue)
            {
                PInvoke.SetForegroundWindow(target);
                PInvoke.PostMessage(target, WM_COMMAND, new WPARAM(id), default);
            }
            return;
        }

        // Many apps populate/enable items lazily in WM_INITMENUPOPUP (recent files, Copy enabled per
        // selection, …) — send it so the dropdown we host reflects live state.
        PInvoke.SendMessageTimeout(target, WM_INITMENUPOPUP, new WPARAM((nuint)sub.Value),
            new LPARAM(index & 0xFFFF), SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, SendTimeoutMs, null);

        // MNS_NOTIFYBYPOS menus report the pick to the owner as WM_MENUCOMMAND (by position) instead
        // of a WM_COMMAND id — those are tracked without TPM_RETURNCMD and the message is relayed.
        bool byPos = UsesNotifyByPos(sub) || UsesNotifyByPos(bar);

        var flags = (uint)(TRACK_POPUP_MENU_FLAGS.TPM_LEFTALIGN | TRACK_POPUP_MENU_FLAGS.TPM_TOPALIGN
            | TRACK_POPUP_MENU_FLAGS.TPM_LEFTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_VERTICAL);
        if (!byPos)
            flags |= (uint)TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD;

        // The popup needs a foreground owner or it won't dismiss on an outside click; posting WM_NULL
        // afterwards is the canonical cleanup nudge (same dance as tray-icon menus).
        PInvoke.SetForegroundWindow(owner);
        _forwardTarget = target;
        int cmd;
        try
        {
            cmd = PInvoke.TrackPopupMenuEx(sub, flags, xPx, yPx, owner, null).Value;
        }
        finally
        {
            // Menu messages (WM_UNINITMENUPOPUP, a NOTIFYBYPOS pick) can still be in flight as the
            // modal loop unwinds — stop forwarding only once the dispatcher drains.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                () => _forwardTarget = default);
            PInvoke.PostMessage(owner, WM_NULL, default, default);
        }

        if (!byPos && cmd != 0)
        {
            // Give focus back first so the command acts on the window the user meant.
            PInvoke.SetForegroundWindow(target);
            PInvoke.PostMessage(target, WM_COMMAND, new WPARAM((uint)cmd), default);
        }
    }

    /// <summary>
    /// Relays menu lifecycle messages that arrive at the hosting (menu-bar) window while a foreign
    /// dropdown is tracking: nested submenus fire WM_INITMENUPOPUP at the popup's OWNER (us), but the
    /// target app is the one that must populate them; NOTIFYBYPOS picks arrive as WM_MENUCOMMAND.
    /// Call from the host's WndProc; returns true when the message was forwarded.
    /// </summary>
    public static unsafe bool ForwardMenuMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is not ((int)WM_INITMENUPOPUP or (int)WM_UNINITMENUPOPUP or (int)WM_MENUCOMMAND))
            return false;
        HWND target = _forwardTarget;
        if (target.IsNull || !PInvoke.IsWindow(target))
            return false;

        if (msg == (int)WM_MENUCOMMAND)
            PInvoke.SetForegroundWindow(target); // the pick should act on the window the user meant
        PInvoke.SendMessageTimeout(target, (uint)msg, new WPARAM((nuint)wParam), new LPARAM(lParam),
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, SendTimeoutMs, null);
        return true;
    }

    private static unsafe bool UsesNotifyByPos(HMENU menu)
    {
        var info = new MENUINFO { cbSize = (uint)sizeof(MENUINFO), fMask = MENUINFO_MASK.MIM_STYLE };
        return PInvoke.GetMenuInfo(menu, &info)
            && (info.dwStyle & MENUINFO_STYLE.MNS_NOTIFYBYPOS) != 0;
    }

    // Strips the accelerator column (after '\t') and mnemonic markers: '&' is dropped, "&&" → '&'.
    private static string CleanLabel(ReadOnlySpan<char> raw)
    {
        int tab = raw.IndexOf('\t');
        if (tab >= 0)
            raw = raw[..tab];
        Span<char> cleaned = stackalloc char[raw.Length];
        int n = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '&')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '&')
                    cleaned[n++] = raw[++i];
                continue;
            }
            cleaned[n++] = raw[i];
        }
        return new string(cleaned[..n]).Trim();
    }
}
