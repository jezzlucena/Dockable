using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dockable.Interop;
using Dockable.Localization;
using Dockable.Models;
using Dockable.Shell;
using Dockable.ViewModels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable;

/// <summary>
/// A macOS-style menu bar: a thin always-visible strip docked at the top of the primary monitor. It
/// reserves a strip via its own <see cref="AppBarManager"/> (independent of the dock's bottom AppBar),
/// shows the focused window's title, the keyboard layout, and a clock, and follows the dock's theme +
/// glass settings. Unlike the dock it has no magnification/overflow, so the window rect equals the bar
/// rect — no <c>SetWindowRgn</c> clipping is needed.
/// </summary>
public partial class MenuBarWindow : Window
{
    // Private window message the shell uses for this AppBar's ABN_* notifications. Must differ from the
    // dock's (WM_USER + 1) so the two AppBars don't cross wires.
    private const uint MenuBarCallbackMessage = 0x0400 + 2; // WM_USER + 2
    private const int WM_SETTINGCHANGE = 0x001A;
    private const uint WM_NCRBUTTONDOWN = 0x00A4;
    private const uint WM_NCRBUTTONUP = 0x00A5;
    private const nuint HTCAPTION = 2; // non-client hit-test code for the title bar
    private const double MenuBarHeight = 28; // resting strip height (DIP) — a blind constant; tune on feedback

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly TitleWatcher _title = new();
    private readonly AcrylicBackdrop _acrylic = new();
    private readonly DispatcherTimer _clockTimer;

    private IntPtr _hwnd;
    private AppBarManager? _appBar;
    private IntPtr _appHwnd; // the focused app window the bar currently represents (its name + system menu)
    private int _menuGen; // stale-guard: async (UIA) app-menu reads only apply if still the latest
    private bool _fullscreenActive; // hidden while a full-screen app owns our monitor

    public MenuBarWindow()
    {
        InitializeComponent();
        // Start on the primary monitor (top-left); PositionMenuBar() snaps to its exact bounds once the
        // HWND exists and DPI is known.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = MenuBarHeight;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { UpdateClock(); UpdateKeyboard(); UpdateFullscreenState(); };

        Loaded += OnLoaded;
        DataContextChanged += (_, _) => ApplyTheme();
    }

    private MenuBarViewModel? ViewModel => DataContext as MenuBarViewModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _appBar = new AppBarManager(_hwnd, MenuBarCallbackMessage);

        // Tool window: never in the Alt+Tab switcher.
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr((HWND)_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr((HWND)_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)exStyle);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // Live acrylic blur behind the bar, in its own window just below the menu bar.
        try
        {
            _acrylic.Initialize();
            ApplyGlassEffect();
        }
        catch
        {
            // Glass is a nicety; never let its setup block the bar from showing.
        }

        ApplyBehavior();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        _title.TitleChanged += OnTitleChanged;
        _title.Start();
        UpdateAppName();
        UpdateKeyboard();
        UpdateClock();
        UpdateFullscreenState(); // hide immediately if launched while a full-screen app is active
        _clockTimer.Start();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    // --- Live content ---

    private void OnTitleChanged()
    {
        UpdateAppName();
        UpdateKeyboard(); // the layout is tracked per foreground thread, so it can change with focus
        UpdateFullscreenState(); // a full-screen app coming forward is a foreground change
    }

    /// <summary>Hides the menu bar (and its backdrop) while a full-screen / borderless-fullscreen app
    /// owns our monitor, and restores it when that window goes away — so it never sits over games/video.</summary>
    private void UpdateFullscreenState()
    {
        // Ignore our own UI being focused (clicking the bar / its menus) — otherwise it would un-hide
        // over a full-screen game.
        if (Fullscreen.IsForegroundOwnProcess(_ownProcessId))
            return;

        bool fullscreen = Fullscreen.IsForegroundFullscreenOnMonitorOf(_hwnd, _ownProcessId);
        if (fullscreen == _fullscreenActive)
            return;
        _fullscreenActive = fullscreen;

        if (fullscreen)
        {
            // Release the reserved top strip so the game can take the FULL monitor — otherwise it
            // resizes to avoid our strip, stops covering the monitor, and we'd flip back to visible.
            _appBar?.Unregister();
            Hide();
            _acrylic.Hide();
        }
        else
        {
            ReserveTopStrip(); // re-register + reserve the strip
            Show();
            PositionMenuBar();
            ApplyGlassEffect();
        }
    }

    private unsafe void UpdateAppName()
    {
        if (ViewModel is null)
            return;
        HWND fg = PInvoke.GetForegroundWindow();
        uint pid = 0;
        if (!fg.IsNull)
            PInvoke.GetWindowThreadProcessId(fg, &pid);

        // Don't follow our own windows (the dock/menu bar can briefly take focus) — keep representing
        // the last real app instead. At startup there IS no "last real app" yet (launching the dock
        // made US foreground), so seed from the top-most real app window in Z-order — the window the
        // user was in before starting Dockable.
        if (fg.IsNull || pid == _ownProcessId)
        {
            // Exception: the Dock Preferences window is a real, user-facing window — represent it
            // like any app, under the same name as its dock tile. (It's WPF, so it has no HMENU, and
            // UIA-scanning our own process is deadlock-prone — no mirrored app menus for it.)
            IntPtr prefs = ViewModel.PreferencesHwnd;
            if (!fg.IsNull && prefs != IntPtr.Zero && (IntPtr)fg == prefs)
            {
                if (prefs == _appHwnd)
                    return;
                _appHwnd = prefs;
                ViewModel.AppName = Loc.T("Window_DockPreferences");
                _menuGen++; // invalidate any in-flight UIA read for the previous app
                SetMenuEntries(null);
                return;
            }

            // The window we represented may be gone (e.g. Preferences closed, focus fell to the dock).
            if (_appHwnd != IntPtr.Zero && !PInvoke.IsWindow((HWND)_appHwnd))
            {
                _appHwnd = IntPtr.Zero;
                ViewModel.AppName = string.Empty;
                _menuGen++;
                SetMenuEntries(null);
            }
            if (_appHwnd == IntPtr.Zero)
                SeedFromTopmostAppWindow();
            return;
        }

        IntPtr fgPtr = fg;
        if (fgPtr == _appHwnd)
            return; // same app still focused; its display name doesn't change with the window title

        _appHwnd = fgPtr;
        ViewModel.AppName = ForegroundApp.DisplayName(fgPtr, pid);
        RefreshAppMenus(fgPtr);
    }

    private void SeedFromTopmostAppWindow()
    {
        foreach (var w in TaskbarApps.EnumerateAppWindows(_ownProcessId))
        {
            if (PInvoke.IsIconic((HWND)w.Hwnd))
                continue; // minimized windows keep their Z-slot but aren't what the user sees
            _appHwnd = w.Hwnd;
            ViewModel!.AppName = ForegroundApp.DisplayName(w.Hwnd, WindowControl.GetProcessId(w.Hwnd));
            RefreshAppMenus(w.Hwnd);
            return;
        }
    }

    /// <summary>Mirrors the focused window's in-window menu ("File", "Edit", …) after its name:
    /// Tier 1 reads a classic Win32 menu bar synchronously (cheap); windows without one fall back to
    /// a UI Automation scan on a background thread (slow on huge accessibility trees).</summary>
    private void RefreshAppMenus(IntPtr hwnd)
    {
        int gen = ++_menuGen;

        var win32 = Win32AppMenu.TryRead(hwnd);
        if (win32 is not null)
        {
            SetMenuEntries(win32);
            return;
        }

        SetMenuEntries(null); // clear the previous app's labels while the UIA read runs
        Task.Run(() =>
        {
            var uia = UiaAppMenu.TryRead(hwnd);
            if (uia is null)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (gen == _menuGen && hwnd == _appHwnd)
                    SetMenuEntries(uia);
            });
        });
    }

    private void SetMenuEntries(List<AppMenuEntry>? entries)
    {
        var collection = ViewModel?.MenuEntries;
        if (collection is null)
            return;
        collection.Clear();
        if (entries is null)
            return;
        foreach (var entry in entries)
            collection.Add(entry);
    }

    // A mirrored menu label was clicked: Win32 menus are hosted as a real dropdown anchored under the
    // label (flush with the bar's bottom edge); UIA menus can only be expanded in the app itself.
    private void AppMenu_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppMenuEntry entry)
            return;
        IntPtr target = _appHwnd;
        if (target == IntPtr.Zero || !PInvoke.IsWindow((HWND)target))
            return;

        if (entry.Source == AppMenuSource.Win32)
        {
            var labelOrigin = element.PointToScreen(new Point(0, 0));
            var barBottom = PointToScreen(new Point(0, ActualHeight)); // screen px, DPI-scaled
            Win32AppMenu.Show(target, entry.Index,
                (int)Math.Round(labelOrigin.X), (int)Math.Round(barBottom.Y), _hwnd);
        }
        else
        {
            // Activate first (from the UI thread, while we're foreground and allowed to hand it off)
            // so the app's menu tracks/dismisses correctly, then expand off-thread — UIA can stall.
            PInvoke.SetForegroundWindow((HWND)target);
            Task.Run(() => UiaAppMenu.Invoke(target, entry.Index, entry.Label));
        }
    }

    private void UpdateKeyboard()
    {
        if (ViewModel is not null)
            ViewModel.KeyboardLabel = KeyboardLayouts.CurrentTwoLetter();
    }

    private void UpdateClock()
    {
        if (ViewModel is null)
            return;
        var culture = CultureFor(Loc.Instance.CurrentCode);
        var now = DateTime.Now;
        // e.g. "Sat 28 Jun  3:42 PM" (en) — weekday + day/month, then the culture's short time.
        ViewModel.TimeText = $"{now.ToString("ddd d MMM", culture)}  {now.ToString("t", culture)}";
    }

    private static CultureInfo CultureFor(string code)
    {
        try { return CultureInfo.GetCultureInfo(code); }
        catch (CultureNotFoundException) { return CultureInfo.CurrentCulture; }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateClock(); // re-format the date/time for the new language right away
    }

    // --- Interactive controls (Phase B) ---

    // The Windows logo opens an Apple-menu-style command center (system shortcuts + power/session).
    private void WindowsLogo_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = BuildWindowsMenu();
        menu.PlacementTarget = StartButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu BuildWindowsMenu()
    {
        var menu = new ContextMenu();

        AddItem(menu, Loc.T("Menu_AboutThisPC"), () => ShortcutService.Launch("ms-settings:about"));
        menu.Items.Add(new Separator());
        AddItem(menu, Loc.T("Menu_SystemSettings"), () => ShortcutService.Launch("ms-settings:"));
        AddItem(menu, Loc.T("Menu_MicrosoftStore"), () => ShortcutService.Launch("ms-windows-store://home"));
        menu.Items.Add(new Separator());

        // Recent (currently open) apps → bring all of an app's windows to the front.
        var recent = new MenuItem { Header = Loc.T("Menu_RecentApps") };
        PopulateRecentApps(recent);
        menu.Items.Add(recent);
        menu.Items.Add(new Separator());

        // Force Quit the focused app (the one the bar represents).
        IntPtr appHwnd = _appHwnd;
        string appName = ViewModel?.AppName ?? string.Empty;
        var forceQuit = new MenuItem
        {
            Header = string.IsNullOrEmpty(appName)
                ? Loc.T("Menu_ForceQuit")
                : string.Format(Loc.T("Menu_ForceQuitApp"), appName),
            IsEnabled = appHwnd != IntPtr.Zero && WindowControl.IsWindow(appHwnd),
        };
        forceQuit.Click += (_, _) => ForceQuit(appHwnd);
        menu.Items.Add(forceQuit);
        menu.Items.Add(new Separator());

        AddItem(menu, Loc.T("Menu_Sleep"), SystemActions.Sleep);
        AddItem(menu, Loc.T("Menu_Restart"), () => ConfirmThen("Confirm_Restart", SystemActions.Restart));
        AddItem(menu, Loc.T("Menu_ShutDown"), () => ConfirmThen("Confirm_ShutDown", SystemActions.ShutDown));
        menu.Items.Add(new Separator());

        AddItem(menu, Loc.T("Menu_LockScreen"), SystemActions.Lock);
        AddItem(menu, string.Format(Loc.T("Menu_LogOut"), SystemActions.CurrentUserDisplayName()),
            () => ConfirmThen("Confirm_LogOut", SystemActions.LogOut));

        return menu;
    }

    private static void AddItem(ContextMenu menu, string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    // Builds the "Recent Apps" submenu: one entry per open app (windows grouped by executable/AUMID),
    // each raising all of that app's windows when chosen.
    private void PopulateRecentApps(MenuItem parent)
    {
        var order = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var windowsByApp = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in TaskbarApps.EnumerateAppWindows(_ownProcessId))
        {
            string key = !string.IsNullOrEmpty(w.Aumid) ? "aumid:" + w.Aumid
                       : !string.IsNullOrEmpty(w.ExePath) ? w.ExePath
                       : "title:" + w.Title;
            if (!windowsByApp.TryGetValue(key, out var list))
            {
                list = new List<IntPtr>();
                windowsByApp[key] = list;
                order.Add(key);
                names[key] = ForegroundApp.DisplayName(w.Hwnd, WindowControl.GetProcessId(w.Hwnd));
            }
            list.Add(w.Hwnd);
        }

        if (order.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = Loc.T("Menu_RecentAppsEmpty"), IsEnabled = false });
            return;
        }

        foreach (string key in order.OrderBy(k => names[k], StringComparer.CurrentCultureIgnoreCase))
        {
            List<IntPtr> hwnds = windowsByApp[key];
            var item = new MenuItem { Header = names[key] };
            item.Click += (_, _) => WindowControl.ActivateAll(hwnds);
            parent.Items.Add(item);
        }
    }

    private static void ForceQuit(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        uint pid = WindowControl.GetProcessId(hwnd);
        if (pid == 0)
            return;
        try { System.Diagnostics.Process.GetProcessById((int)pid).Kill(); }
        catch { /* already exited / access denied (e.g. elevated) */ }
    }

    private void ConfirmThen(string messageKey, Action action)
    {
        var dialog = new ConfirmDialog(Loc.T(messageKey), showDoNotAskAgain: false) { Owner = this };
        if (dialog.ShowDialog() == true)
            action();
    }

    // Clicking the app name opens the focused window's title-bar menu — exactly what right-clicking that
    // window's title bar does. We replay that gesture by posting the non-client right-click messages
    // (HTCAPTION) to the target so its OWN DefWindowProc shows the menu in its process (a cross-process
    // GetSystemMenu + TrackPopupMenu doesn't work — the menu belongs to the other process). This also
    // honours custom title-bar menus (e.g. Chrome's) since it's the same message a real click sends.
    private void AppName_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_appHwnd == IntPtr.Zero)
            return;
        var target = (HWND)_appHwnd;
        if (!PInvoke.IsWindow(target))
            return;

        // Anchor the menu just below the app-name label (screen/physical pixels — NC messages use them).
        var origin = AppNameText.PointToScreen(new Point(0, AppNameText.ActualHeight));
        int x = (int)Math.Round(origin.X);
        int y = (int)Math.Round(origin.Y);
        var lParam = (LPARAM)(nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF)); // MAKELPARAM(x, y)
        var wParam = (WPARAM)(nuint)HTCAPTION;

        // The window must be active for its menu to track/dismiss correctly and to act on the focused
        // window; activating it matches what a real title-bar click does.
        PInvoke.SetForegroundWindow(target);
        PInvoke.PostMessage(target, WM_NCRBUTTONDOWN, wParam, lParam);
        PInvoke.PostMessage(target, WM_NCRBUTTONUP, wParam, lParam);
    }

    // TrayOverflow.Open synthesizes Win+B then (after a short delay) Enter — run it off the UI thread so
    // the in-between sleep doesn't block the bar.
    private void TrayOverflow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => System.Threading.Tasks.Task.Run(TrayOverflow.Open);

    private void QuickSettings_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => QuickSettings.Open();

    private void Notifications_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Notifications.Open();

    // Opens the Windows "Date & time" settings page (Settings > Time & language > Date & time).
    private void Clock_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:dateandtime")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort; the settings deep-link may be unavailable on some SKUs.
        }
    }

    private void Keyboard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var layouts = KeyboardLayouts.Installed();
        if (layouts.Count == 0)
            return;

        // Single layout → nothing to switch between; just no-op.
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var (hkl, label) in layouts)
        {
            // Full name ("English (United States) — United States-International") so same-language
            // layouts are tellable apart; the two-letter label is only a last-resort fallback.
            string full = KeyboardLayouts.DisplayNameFor(hkl);
            var item = new System.Windows.Controls.MenuItem
            {
                Header = full.Length > 0 ? full : (string.IsNullOrEmpty(label) ? "??" : label),
            };
            nint handle = hkl;
            item.Click += (_, _) => KeyboardLayouts.Switch(handle);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = KeyboardText;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // --- Theme ---

    /// <summary>Re-applies the bar's colours for the current theme — called when the user switches the
    /// Light/Dark/Auto setting while the menu bar is open, so it tracks the dock.</summary>
    public void RefreshTheme() => ApplyTheme();

    private void ApplyTheme()
    {
        bool dark = ViewModel?.Settings.Theme switch
        {
            DockTheme.Dark => true,
            DockTheme.Light => false,
            _ => !SystemTheme.IsLight(), // System: follow Windows
        };

        // Same bar colours as the dock, at 50% transparency (the acrylic blur shows through). Text
        // contrasts with the background per the Appearance (theme) setting.
        if (dark)
        {
            Resources["BarBackgroundBrush"] = Brush("#80242424"); // dock dark bg (#242424) @ 50%
            Resources["MenuTextBrush"] = Brush("#FFF2F2F2");
        }
        else
        {
            Resources["BarBackgroundBrush"] = Brush("#80FFFFFF"); // dock light bg (#FFFFFF) @ 50%
            Resources["MenuTextBrush"] = Brush("#FF1D1D1F");
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // --- Glass backdrop ---

    private void ApplyGlassEffect()
    {
        // The menu bar is always acrylic (a translucent blur behind its tint), independent of the dock's
        // Glass Effect setting — the bar's own brush is translucent so the blur shows through.
        _acrylic.SetEffect(GlassEffect.Acrylic);
        SyncAcrylic();
        _acrylic.Show();
    }

    private void SyncAcrylic()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = PointToScreen(new Point(0, 0)); // device pixels
        int w = (int)Math.Round(Width * dpi.DpiScaleX);
        int h = (int)Math.Round(Height * dpi.DpiScaleY);
        _acrylic.SetBounds((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), w, h, 0f, _hwnd);
    }

    // --- Docking (top AppBar) ---

    private void ApplyBehavior()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        ReserveTopStrip();
        PositionMenuBar();
        _appBar?.NotifyPosChanged();
    }

    private void ReserveTopStrip()
    {
        if (_appBar is null)
            return;
        var info = Monitors.ForWindow(_hwnd);
        int thicknessPx = (int)Math.Round(MenuBarHeight * info.Scale);
        _appBar.Register();
        _appBar.ReserveEdge(DockEdge.Top, info.MonitorPx, thicknessPx);
    }

    private void PositionMenuBar()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        var info = Monitors.ForWindow(_hwnd);
        double scale = info.Scale;
        Left = info.MonitorPx.Left / scale;
        Top = info.MonitorPx.Top / scale;
        Width = info.MonitorPx.Width / scale;
        Height = MenuBarHeight;
        SyncAcrylic();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // While we host another app's Win32 dropdown, relay the menu lifecycle messages it expects
        // (nested-submenu WM_INITMENUPOPUP, NOTIFYBYPOS picks) — they arrive at us as the popup owner.
        Win32AppMenu.ForwardMenuMessage(msg, wParam, lParam);

        if (msg == WM_SETTINGCHANGE
            && ViewModel?.Settings.Theme == DockTheme.System
            && Marshal.PtrToStringAuto(lParam) == "ImmersiveColorSet")
        {
            ApplyTheme();
        }

        if ((uint)msg == MenuBarCallbackMessage)
        {
            switch ((uint)wParam.ToInt64())
            {
                case PInvoke.ABN_POSCHANGED:
                    // Another appbar (e.g. the taskbar) moved; re-assert our reserved strip and position.
                    ReserveTopStrip();
                    PositionMenuBar();
                    handled = true;
                    break;
                case PInvoke.ABN_FULLSCREENAPP:
                    UpdateFullscreenState(); // a full-screen app opened/closed — hide or restore the bar
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        Loc.LanguageChanged -= OnLanguageChanged;
        _title.TitleChanged -= OnTitleChanged;
        _title.Dispose();
        _appBar?.Unregister(); // release the reserved top strip
        _acrylic.Dispose();
        base.OnClosed(e);
    }
}
