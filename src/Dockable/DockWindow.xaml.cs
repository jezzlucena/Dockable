using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dockable.Genie;
using Dockable.Interop;
using Dockable.Localization;
using Dockable.Models;
using Dockable.Shell;
using Dockable.ViewModels;
using H.NotifyIcon;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable;

public partial class DockWindow : Window
{
    // Private window message the shell uses to send the dock AppBar notifications.
    private const uint AppBarCallbackMessage = 0x0400 + 1; // WM_USER + 1
    private const int WM_SETTINGCHANGE = 0x001A;           // OS setting changed (incl. light/dark)

    // Drag ghost geometry (matches the DragGhost popup in XAML): the icon's center sits at
    // (GhostCenterX, GhostCenterY) from the popup's top-left, so it tracks under the cursor.
    private const double GhostCenterX = 80;   // half of GhostRoot's 160 width
    private const double GhostCenterY = 72;   // 42px "Remove" row + half the 60px icon
    private const int DragSteadyMs = 500;     // hold still this long to arm "Remove"
    private const double SteadyEpsilon = 4;   // px of motion that counts as "moved"

    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow; // "Dock Preferences…" window (single instance)
    private AboutWindow? _aboutWindow;       // "About Dockable" window (single instance)
    private HwndSource? _prefsSource;        // message hook on the Preferences window (intercepts minimize)

    private double _mouseX;
    private double _mouseY;
    private bool _hovering;
    private bool _renderingHooked;
    private bool _finalizeScheduled; // a deferred FinalizeDeparted is queued (avoid mutating Items mid-render)

    private IntPtr _hwnd;
    private AppBarManager? _appBar;
    private bool _windowRegionClipped;      // true while the window is clipped to the resting bar

    /// <summary>True when the dock is on a side edge (Left/Right); the main axis is then screen-Y.</summary>
    private bool IsVerticalDock => ViewModel?.IsVerticalDock ?? false;

    // Keep the bar's drop shadow when the window is clipped to the resting bar.
    private const double DockRegionTopPaddingDip = 26;

    // Real window minimize/restore is intercepted and replaced with one of these effects.
    private readonly GenieAnimator _genie = new();
    private readonly ScaleAnimator _scale = new();
    private readonly MinimizeHook _minimizeHook = new();
    // Catches a minimize the user is *about* to trigger (min-button click / Win+Down / Win+M) so we
    // paint the captured frame 0 before the OS minimizes — no disappear-then-reappear flash.
    private readonly MinimizeInterceptHook _minimizeIntercept = new();
    // Full-window captures taken while windows are visible (capture-at-minimize is too late).
    private readonly WindowThumbnailCache _thumbnails = new();
    // Live acrylic blur rendered in a separate window directly behind the bar.
    private readonly AcrylicBackdrop _acrylic = new();
    private const double BarCornerRadius = 24; // matches DockBackground's CornerRadius

    // Liquid Glass (real refraction): periodic capture of the backdrop behind the bar, fed to the shader.
    private DispatcherTimer? _glassCaptureTimer;
    private bool _glassRefractReady; // the refraction effect has been attached to GlassRefract
    private WriteableBitmap? _glassBitmap; // reused source for the refraction (written in place on change)
    private byte[]? _glassBuf;             // latest capture
    private byte[]? _glassPrev;            // previous capture (to detect "no change" → skip the shader)
    private int _glassW, _glassH;          // current capture size (physical px)

    // Hide the dock entirely while a full-screen app / borderless-fullscreen game owns the screen.
    private readonly ForegroundWatcher _foreground = new();
    private bool _fullscreenActive;
    // Windows whose minimize/restore we're currently animating (ignore re-entrant events).
    private readonly HashSet<IntPtr> _busy = new();
    // Windows minimized "into" their app icon (no thumbnail tile): hwnd → its capture for restore.
    private readonly Dictionary<IntPtr, BitmapSource> _iconMinimized = new();
    // The visual bounds (screen px, sans shadow/border) each window was captured at when minimized, so
    // the restore warp ends at the captured size — not the slightly larger GetWindowPlacement rect.
    private readonly Dictionary<IntPtr, Int32Rect> _minimizedSourcePx = new();

    // Mirror the taskbar: poll running apps + watch the pinned folder.
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly DispatcherTimer _appRefreshTimer;
    private FileSystemWatcher? _pinWatcher;
    private readonly DispatcherTimer _pinCheckTimer; // debounces taskbar-pin checks after folder changes
    private bool _promptOpen;                          // guards against overlapping prompt dialogs

    // While the Start menu (opened from the dock) is up, the taskbar is fully hidden; this watches
    // for it closing so we can restore the configured taskbar state.
    private readonly DispatcherTimer _startWatchTimer;
    private bool _startSeen;       // confirmed the Start menu actually appeared
    private int _startWatchTicks;  // grace ticks so we never leave the taskbar stuck hidden
    private const int StartWatchMaxTicks = 12; // ~1.8s for Start to appear before giving up

    // Drag to reorder / pin / remove.
    private Point _dragStart;
    private DockItemViewModel? _dragCandidate;
    private bool _dragInitiated;
    private Point _lastCursor;                          // cursor relative to RootCanvas
    private Point _steadyAnchor;                        // position last considered "moved"
    private bool _removeArmed;                          // dragged pinned shortcut held steady → "Remove"
    private readonly DispatcherTimer _dragSteadyTimer;  // fires after DragSteadyMs of no motion

    // Separator drag = resize the dock's Size setting (kept in [SizeMin, SizeMax], matching Dock Preferences).
    private const double SizeMin = 12;
    private const double SizeMax = 64;
    private bool _resizePressed;        // pressed a separator, not yet past the drag threshold
    private bool _separatorResize;      // actively resizing
    private double _resizeStartCursorCross; // physical-px cursor coord on the cross axis at press
    private double _resizeStartIconSize;

    public DockWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => PositionDock();
        DataContextChanged += OnDataContextChanged;

        MouseMove += OnMouseMove;
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonUp += OnDockMouseUp; // ends a custom drag (no-op for normal clicks)

        _appRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _appRefreshTimer.Tick += (_, _) =>
        {
            RefreshTaskbarApps();
            UpdateFullscreenState(); // backstop in case a fullscreen transition didn't raise an event
            CheckAndPromptNewPins(); // reliable poll for new taskbar pins (the folder watcher often doesn't fire on Win11)
        };

        _dragSteadyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragSteadyMs) };
        _dragSteadyTimer.Tick += OnDragSteadyElapsed;
        LostMouseCapture += OnLostMouseCapture; // robust cleanup if a drag is interrupted

        _startWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _startWatchTimer.Tick += OnStartWatchTick;

        _pinCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _pinCheckTimer.Tick += (_, _) => { _pinCheckTimer.Stop(); CheckAndPromptNewPins(); };
    }

    private DockViewModel? ViewModel => DataContext as DockViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DockViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.AnimationRequested -= OnAnimationRequested;
        }
        if (ViewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.AnimationRequested += OnAnimationRequested;
            ApplyWindowSize();
            ApplyTheme(); // paint the bar for the saved theme before the window is shown
        }
    }

    // An app launch bounce started: unclip so the hop can render above the bar, and run the loop.
    private void OnAnimationRequested()
    {
        ClearWindowRegion();
        HookRendering();
    }

    // --- Light / dark theme ---

    /// <summary>Repaints the dock bar (and its theme-dependent elements) for the effective theme.</summary>
    private void ApplyTheme()
    {
        bool dark = ViewModel?.Settings.Theme switch
        {
            DockTheme.Dark => true,
            DockTheme.Light => false,
            _ => !SystemTheme.IsLight(), // System: follow Windows
        };

        if (dark)
        {
            // .macos-dock-dark
            Resources["BarBackgroundBrush"] = Brush("#66242424");
            Resources["BarBorderBrush"] = Brush("#14FFFFFF");
            Resources["SeparatorBrush"] = Brush("#40FFFFFF");
            Resources["RunningDotBrush"] = Brush("#CCFFFFFF"); // rgba(255,255,255,0.8)
            Resources["FallbackBgBrush"] = Brush("#33FFFFFF");
            Resources["FallbackTextBrush"] = Brush("#FFFFFFFF");
            Resources["LabelBgBrush"] = Brush("#F22A2A30");
            Resources["LabelBorderBrush"] = Brush("#33FFFFFF");
            Resources["LabelTextBrush"] = Brush("#FFF2F2F2");
            Resources["IconShadowOuterOpacity"] = 0.12;
            Resources["IconShadowInnerOpacity"] = 0.18;
            BarShadow.Opacity = 0.4;
            DockBackground.BorderThickness = new Thickness(1.5);
        }
        else
        {
            // .macos-dock-light (background/border swapped from the original tints)
            Resources["BarBackgroundBrush"] = Brush("#33FFFFFF");
            Resources["BarBorderBrush"] = Brush("#66FFFFFF");
            Resources["SeparatorBrush"] = Brush("#33000000");
            Resources["RunningDotBrush"] = Brush("#B3000000"); // rgba(0,0,0,0.7)
            Resources["FallbackBgBrush"] = Brush("#1F000000");
            Resources["FallbackTextBrush"] = Brush("#CC000000");
            Resources["LabelBgBrush"] = Brush("#F2F7F7FA");
            Resources["LabelBorderBrush"] = Brush("#22000000");
            Resources["LabelTextBrush"] = Brush("#1D1D1F");
            Resources["IconShadowOuterOpacity"] = 0.10; // subtle icon shadows on light glass
            Resources["IconShadowInnerOpacity"] = 0.14;
            BarShadow.Opacity = 0.15;
            DockBackground.BorderThickness = new Thickness(1.5); // slightly thicker border on light glass
        }
    }

    private void SetTheme(DockTheme theme)
    {
        if (ViewModel is null || ViewModel.Settings.Theme == theme)
            return;
        ViewModel.Settings.Theme = theme;
        ViewModel.Save();
        ApplyTheme();
        App.Current.RefreshMenuBarTheme(); // keep the menu bar's colours in step with the dock
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Binding Window.Width/Height directly is unreliable, so mirror the view-model here.
        if (e.PropertyName is nameof(DockViewModel.WindowWidth) or nameof(DockViewModel.WindowHeight))
            ApplyWindowSize();
    }

    private void ApplyWindowSize()
    {
        if (ViewModel is null)
            return;
        Width = ViewModel.WindowWidth;
        Height = ViewModel.WindowHeight;
        PositionDock();
        ApplyIdleRegion(); // re-clip to the new resting bar when the layout size changes (if idle)
        // Keep the reserved strip in step with the dock's size (e.g. the Preferences Size slider). During
        // a separator drag this is deferred to the drop (EndSeparatorResize) so maximized windows don't
        // reflow on every frame.
        if (!_separatorResize)
            ReserveAppBarSpace();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _appBar = new AppBarManager(_hwnd, AppBarCallbackMessage);

        // Mark the dock a tool window so it never shows in the Alt+Tab switcher.
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr((HWND)_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr((HWND)_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)exStyle);

        // Listen for shell AppBar notifications (taskbar moved, full-screen app, etc.).
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        _genie.Prewarm(); // build the reusable overlays now so the first minimize is instant
        _scale.Prewarm();
        _thumbnails.Start();
        _minimizeHook.WindowMinimizing += OnWindowMinimizing;
        _minimizeHook.WindowUnminimized += OnWindowUnminimized;
        _minimizeHook.Start();

        // Pre-empt the minimize gesture so the warp's frame 0 is on screen before the OS minimizes.
        // Defer the real work off the hook callback so the low-level hook returns immediately.
        _minimizeIntercept.MinimizeRequested += hwnd => Dispatcher.BeginInvoke(() => InterceptedMinimize(hwnd));
        _minimizeIntercept.MinimizeAllRequested += () => Dispatcher.BeginInvoke(OnMinimizeAllRequested);
        _minimizeIntercept.Start();

        _foreground.ForegroundChanged += OnForegroundChanged;
        _foreground.Start();

        // Live acrylic blur behind the bar, in its own window just below the dock.
        try
        {
            _acrylic.Initialize();
            ApplyGlassEffect();
        }
        catch
        {
            // Acrylic is a nicety; never let its setup block the dock from showing.
        }

        ApplyBehavior();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateTrayIcon();
        ApplyWindowSize();
        ApplyTaskbarVisibility();
        StartTaskbarMirror();

        // After the dock is up, run the one-time startup prompts (defer so the dock renders first).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            PromptAddToStartupIfNeeded();
            CheckAndPromptNewPins();
        });
    }

    // --- Taskbar mirror: live pinned + running apps ---

    private void StartTaskbarMirror()
    {
        RefreshTaskbarApps();
        SyncPreMinimizedWindows(); // adopt windows already minimized before the dock launched
        _appRefreshTimer.Start(); // pick up apps opening/closing

        try
        {
            _pinWatcher = new FileSystemWatcher(TaskbarApps.PinnedFolder, "*.lnk")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            // A new pin (created/renamed .lnk) also triggers a debounced check to offer replication.
            FileSystemEventHandler refresh = (_, _) => Dispatcher.BeginInvoke(() => RefreshTaskbarApps());
            FileSystemEventHandler refreshAndCheck = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                RefreshTaskbarApps();
                _pinCheckTimer.Stop();
                _pinCheckTimer.Start(); // debounce: let the taskband registry catch up before checking
            });
            _pinWatcher.Created += refreshAndCheck;
            _pinWatcher.Deleted += refresh;
            _pinWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                RefreshTaskbarApps();
                _pinCheckTimer.Stop();
                _pinCheckTimer.Start();
            });
        }
        catch
        {
            // Watching is a nicety; the 1s timer still keeps pins reasonably fresh.
        }
    }

    private void RefreshTaskbarApps() => ViewModel?.RefreshTaskbarApps(_ownProcessId);

    /// <summary>If new shortcuts have been pinned to the taskbar, offer to replicate them on the dock.</summary>
    private void CheckAndPromptNewPins()
    {
        if (ViewModel is null || _promptOpen || !ViewModel.Settings.AskReplicateTaskbarPins)
            return;
        var newPins = ViewModel.FindNewTaskbarPins();
        if (newPins.Count == 0)
            return;

        _promptOpen = true;
        try
        {
            var dialog = new ConfirmDialog(Loc.T(newPins.Count > 1
                ? "Dialog_ReplicatePinsPlural"
                : "Dialog_ReplicatePinsSingular")) { Owner = this };
            bool replicate = dialog.ShowDialog() == true;

            if (dialog.DoNotAskAgain)
                ViewModel.Settings.AskReplicateTaskbarPins = false;

            if (replicate)
            {
                ViewModel.ReplicateTaskbarPins(newPins); // pins + remembers (saves)
                RefreshTaskbarApps();
            }
            else
            {
                ViewModel.RememberTaskbarPins(newPins); // don't offer these again (saves)
            }
            ViewModel.Save();
        }
        finally
        {
            _promptOpen = false;
        }
    }

    /// <summary>Offers (once) to add Dockable to the Windows startup sequence, unless already there.</summary>
    private void PromptAddToStartupIfNeeded()
    {
        if (ViewModel is null || _promptOpen || !ViewModel.Settings.AskAddToStartup)
            return;
        if (StartupManager.IsEnabled(DockableStartupName)) // already runs at login
            return;

        _promptOpen = true;
        try
        {
            var dialog = new ConfirmDialog(Loc.T("Dialog_AddToStartup")) { Owner = this };
            bool add = dialog.ShowDialog() == true;
            if (add)
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exe))
                    StartupManager.Enable(DockableStartupName, exe);
                ViewModel.Settings.AskAddToStartup = false; // answered; and IsEnabled will short-circuit anyway
            }
            else if (dialog.DoNotAskAgain)
            {
                ViewModel.Settings.AskAddToStartup = false;
            }
            ViewModel.Save();
        }
        finally
        {
            _promptOpen = false;
        }
    }

    private const string DockableStartupName = "Dockable"; // HKCU Run-key value name (matches Dock Preferences)

    /// <summary>Click a taskbar app: focus its windows if running, otherwise launch it. Minimized
    /// windows (in a thumbnail tile or into this icon) restore with the animation, one after another
    /// (only one effect can play at a time); non-minimized windows are just brought forward.</summary>
    private void ActivateOrLaunch(DockItemViewModel app)
    {
        // The built-in Dock Preferences tile opens the dock's own window when it isn't open yet. While
        // it IS open, fall through to the generic path so a minimized window restores (with the warp).
        if (app.IsPreferences && app.Windows.Count == 0)
        {
            OpenDockPreferences();
            return;
        }

        if (app.Windows.Count == 0)
        {
            ShortcutService.Launch(app.LaunchPath);
            return;
        }

        var toRaise = new List<IntPtr>();
        var toRestore = new List<(IntPtr Hwnd, DockItemViewModel? Tile, BitmapSource? Bitmap)>();
        foreach (var hwnd in app.Windows)
        {
            var tile = ViewModel?.FindMinimizedWindow(hwnd);
            if (tile is not null)
                toRestore.Add((hwnd, tile, tile.Icon as BitmapSource));               // minimized as a tile
            else if (WindowControl.IsIconic(hwnd))
                toRestore.Add((hwnd, null, _iconMinimized.GetValueOrDefault(hwnd)
                    ?? _thumbnails.TryGet(hwnd)?.Bitmap));                              // minimized into the icon
            else
                toRaise.Add(hwnd);
        }

        if (toRaise.Count > 0)
            WindowControl.ActivateAll(toRaise);
        RestoreNext(app, toRestore, 0);
    }

    /// <summary>Restores the queued minimized windows one at a time, chaining each animation to the next.</summary>
    private void RestoreNext(DockItemViewModel app, List<(IntPtr Hwnd, DockItemViewModel? Tile, BitmapSource? Bitmap)> queue, int index)
    {
        if (index >= queue.Count)
            return;
        var (hwnd, tile, bitmap) = queue[index];
        Point target = tile is not null ? TileScreenCenter(tile) : TileScreenCenter(app);
        RestoreWindowAnimated(hwnd, tile, target, bitmap, () => RestoreNext(app, queue, index + 1));
    }

    private void OnForegroundChanged()
    {
        UpdateFullscreenState();
        if (StartMenu.IsOpen())
            RaiseDockAboveStartMenu(); // Start menu just came forward — keep the dock above it
    }

    /// <summary>Re-asserts the dock at the top of the topmost band (without stealing focus) so the
    /// Windows Start menu appears behind it. Re-seats the acrylic backdrop just beneath the dock.</summary>
    private void RaiseDockAboveStartMenu()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        PInvoke.SetWindowPos((HWND)_hwnd, new HWND(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        SyncAcrylic();
    }

    /// <summary>
    /// Hides the whole dock (and its acrylic backdrop) while a full-screen app or borderless-fullscreen
    /// game owns the dock's monitor, and restores it when that window goes away — so the dock never
    /// competes with full-screen content.
    /// </summary>
    private void UpdateFullscreenState()
    {
        // Ignore our own UI being focused (a dock click / context menu / popup) — otherwise interacting
        // with the dock over a full-screen game would un-hide it.
        if (Fullscreen.IsForegroundOwnProcess(_ownProcessId))
            return;

        bool fullscreen = Fullscreen.IsForegroundFullscreenOnMonitorOf(_hwnd, _ownProcessId);
        if (fullscreen == _fullscreenActive)
            return;
        _fullscreenActive = fullscreen;

        if (fullscreen)
        {
            // Release the reserved AppBar strip so the game can take the FULL monitor. If we keep it
            // reserved, the game shrinks to the work area to avoid our strip, stops covering the monitor,
            // and we'd flip back to visible — the feedback loop. Freeing it keeps the game full-screen.
            _appBar?.Unregister();
            Hide();
            _acrylic.Hide();
        }
        else
        {
            ReserveAppBarSpace(); // re-register + reserve the strip
            Show();
            PositionDock();
            ApplyGlassEffect();
        }
    }

    private void ApplyTaskbarVisibility()
    {
        if (ViewModel is null)
            return;

        // Always / Auto (native auto-hide, reveals on edge hover) / Never (fully hidden). The pre-launch
        // state is restored on exit/crash.
        Taskbar.SetVisibility(ViewModel.Settings.TaskbarVisibility);
    }

    /// <summary>Anchors the dock flush to its monitor's docked edge, centered along the other axis.</summary>
    private void PositionDock()
    {
        var (left, top) = ComputePlacement();
        Left = left;
        Top = top;
        SyncAcrylic();
    }

    /// <summary>Shows/hides and configures the acrylic backdrop for the selected Glass Effect: Simple
    /// hides it (the bar keeps its plain translucent brush); Acrylic/Liquid Glass show it with the
    /// matching backdrop brush.</summary>
    private void ApplyGlassEffect()
    {
        var mode = ViewModel?.Settings.GlassEffect ?? GlassEffect.Acrylic;
        bool liquid = mode == GlassEffect.LiquidGlass;
        bool refract = liquid && RefractionEffect.IsAvailable; // real pixel-shader refraction

        // Refraction layer: capture the backdrop behind the bar and run the shader over it.
        if (refract)
        {
            EnsureGlassRefraction();
            SetCaptureExclusion(true); // omit the dock from capture so it doesn't refract itself
            GlassRefract.Visibility = Visibility.Visible;
            CaptureGlassBackdrop();
            _glassCaptureTimer ??= CreateGlassCaptureTimer();
            _glassCaptureTimer.Start();
        }
        else
        {
            _glassCaptureTimer?.Stop();
            GlassRefract.Visibility = Visibility.Collapsed;
            SetCaptureExclusion(false); // the dock is visible to capture again
        }

        // If Liquid Glass was chosen but the shader couldn't compile/load, fall back to the rim sheen.
        ApplyGlassRim(liquid && !refract);

        // Acrylic backdrop window: hidden for Simple and when refraction has replaced it; shown for
        // Acrylic and as the base behind the rim fallback.
        if (mode == GlassEffect.Simple || refract)
        {
            _acrylic.Hide();
            return;
        }
        _acrylic.SetEffect(mode);
        _acrylic.Show();
        SyncAcrylic();
    }

    private DispatcherTimer CreateGlassCaptureTimer()
    {
        // Poll at up to 120 fps, but no faster than the monitor refreshes (a faster poll can't show new
        // frames anyway). The shader only re-runs when the captured backdrop actually changed (see below).
        int fps = Math.Min(120, GetRefreshRateHz());
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / fps) };
        timer.Tick += (_, _) => CaptureGlassBackdrop();
        return timer;
    }

    /// <summary>The display's vertical refresh rate in Hz (60 if unknown).</summary>
    private static int GetRefreshRateHz()
    {
        HDC dc = PInvoke.GetDC((HWND)IntPtr.Zero);
        if (dc.IsNull)
            return 60;
        try
        {
            int hz = PInvoke.GetDeviceCaps(dc, GET_DEVICE_CAPS_INDEX.VREFRESH);
            return hz > 1 ? hz : 60; // VREFRESH reports 0/1 for the hardware default
        }
        finally
        {
            PInvoke.ReleaseDC((HWND)IntPtr.Zero, dc);
        }
    }

    /// <summary>Attaches the refraction pixel-shader + rim displacement map to the glass layer (once).</summary>
    private void EnsureGlassRefraction()
    {
        if (_glassRefractReady || !RefractionEffect.IsAvailable)
            return;
        GlassRefract.Effect = new RefractionEffect
        {
            DisplacementMap = new ImageBrush(RefractionEffect.BuildRimMap(256, 256)) { Stretch = Stretch.Fill },
            DistortionAmount = 0.22, // pronounced refraction at the rim
            BlurRadius = 4.0,        // frosted-glass blur (device pixels; outer taps reach ~2x this)
        };
        _glassRefractReady = true;
    }

    /// <summary>
    /// Captures the screen region behind the bar (dock excluded) and feeds it to the shader. Polled at
    /// 60 fps, but the captured pixels are diffed against the previous frame and the shader only re-runs
    /// when they actually changed — so a static backdrop costs just a BitBlt + memcmp, no GPU work.
    /// </summary>
    private void CaptureGlassBackdrop()
    {
        if (ViewModel is null || _hwnd == IntPtr.Zero || ViewModel.BarWidth <= 0 || ViewModel.BarHeight <= 0)
            return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var topLeft = PointToScreen(new Point(ViewModel.BarLeft, ViewModel.BarTop)); // device px
            int w = (int)Math.Round(ViewModel.BarWidth * dpi.DpiScaleX);
            int h = (int)Math.Round(ViewModel.BarHeight * dpi.DpiScaleY);
            if (w <= 0 || h <= 0)
                return;

            bool sizeChanged = _glassBitmap is null || w != _glassW || h != _glassH;
            if (sizeChanged)
            {
                _glassW = w;
                _glassH = h;
                _glassBuf = new byte[w * 4 * h];
                _glassPrev = new byte[w * 4 * h];
                _glassBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                GlassBackdropBrush.ImageSource = _glassBitmap;
            }

            if (!WindowCapture.CaptureScreenRectInto((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), w, h, _glassBuf!))
                return;

            // Skip the upload + shader re-render when nothing behind the bar changed.
            if (!sizeChanged && _glassBuf!.AsSpan().SequenceEqual(_glassPrev))
            {
                UpdateGlassClip();
                return;
            }

            _glassBitmap!.WritePixels(new Int32Rect(0, 0, w, h), _glassBuf!, w * 4, 0);
            (_glassPrev, _glassBuf) = (_glassBuf, _glassPrev); // remember this frame for the next diff
            UpdateGlassClip();
        }
        catch
        {
            // PointToScreen before the window is sourced, or a transient capture failure — try again next tick.
        }
    }

    /// <summary>Keeps the refraction clip geometry matched to the (magnifying) bar's rounded rect.</summary>
    private void UpdateGlassClip()
    {
        if (ViewModel is not null && ViewModel.BarWidth > 0 && ViewModel.BarHeight > 0)
            GlassClipGeometry.Rect = new Rect(0, 0, ViewModel.BarWidth, ViewModel.BarHeight);
    }

    /// <summary>Excludes (or restores) the dock from screen capture via display affinity.</summary>
    private void SetCaptureExclusion(bool exclude)
    {
        if (_hwnd == IntPtr.Zero)
            return;
        PInvoke.SetWindowDisplayAffinity((HWND)_hwnd,
            exclude ? WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE : WINDOW_DISPLAY_AFFINITY.WDA_NONE);
    }

    /// <summary>Shows/hides the Liquid Glass rim overlay and runs (or stops) its drifting shimmer.</summary>
    private void ApplyGlassRim(bool on)
    {
        GlassRim.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        GlassShimmer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            // Sweep the bright band corner-to-corner and back — gentle, so it doesn't distract.
            var sweep = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromSeconds(5)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            ShimmerStop.BeginAnimation(GradientStop.OffsetProperty, sweep);
        }
        else
        {
            ShimmerStop.BeginAnimation(GradientStop.OffsetProperty, null); // stop animating (idle-cheap)
        }
    }

    private void SetGlassEffect(GlassEffect mode)
    {
        if (ViewModel is null || ViewModel.Settings.GlassEffect == mode)
            return;
        ViewModel.Settings.GlassEffect = mode;
        ViewModel.Save();
        ApplyGlassEffect();
    }

    /// <summary>Tracks the acrylic backdrop window to the bar's current screen rect (physical px) and
    /// keeps it z-ordered just below the dock. Called whenever the bar moves or resizes.</summary>
    private void SyncAcrylic()
    {
        if (ViewModel is null || _hwnd == IntPtr.Zero || ViewModel.BarWidth <= 0 || ViewModel.BarHeight <= 0)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = PointToScreen(new Point(ViewModel.BarLeft, ViewModel.BarTop)); // device pixels
        int w = (int)Math.Round(ViewModel.BarWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(ViewModel.BarHeight * dpi.DpiScaleY);
        float corner = (float)(BarCornerRadius * dpi.DpiScaleX);
        _acrylic.SetBounds((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), w, h, corner, _hwnd);
        UpdateGlassClip(); // track the bar so the refraction clip stays rounded as it magnifies
    }

    private (double Left, double Top) ComputePlacement()
    {
        double height = ActualHeight > 0 ? ActualHeight : (ViewModel?.WindowHeight ?? 0);
        double width = ActualWidth > 0 ? ActualWidth : (ViewModel?.WindowWidth ?? 0);
        var edge = ViewModel?.Settings.Edge ?? DockEdge.Bottom;

        // Monitor bounds (DIP) for the screen the dock currently sits on.
        double mLeft, mTop, mWidth, mHeight;
        if (_hwnd != IntPtr.Zero)
        {
            var info = Monitors.ForWindow(_hwnd);
            double scale = info.Scale;
            mLeft = info.MonitorPx.Left / scale;
            mTop = info.MonitorPx.Top / scale;
            mWidth = info.MonitorPx.Width / scale;
            mHeight = info.MonitorPx.Height / scale;
        }
        else // before the HWND exists: fall back to the primary screen
        {
            mLeft = 0;
            mTop = 0;
            mWidth = SystemParameters.PrimaryScreenWidth;
            mHeight = SystemParameters.PrimaryScreenHeight;
        }

        // Anchor flush to the docked edge; center along the perpendicular axis. The AppBar reserves
        // the strip so other windows don't overlap (when the taskbar is hidden the work area is full,
        // so the monitor edge is the freed space).
        return edge switch
        {
            DockEdge.Top => (mLeft + (mWidth - width) / 2, mTop),
            DockEdge.Left => (mLeft, mTop + (mHeight - height) / 2),
            DockEdge.Right => (mLeft + mWidth - width, mTop + (mHeight - height) / 2),
            _ => (mLeft + (mWidth - width) / 2, mTop + mHeight - height), // Bottom
        };
    }

    // --- Magnification render loop ---

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizePressed || _separatorResize)
        {
            _hovering = true; // keep the dock revealed while resizing
            var sp = PointToScreen(e.GetPosition(this));
            HandleSeparatorResize(IsVerticalDock ? sp.X : sp.Y); // resize is along the cross axis
            return;
        }

        var p = e.GetPosition(this);
        _mouseX = p.X;
        _mouseY = p.Y;
        _lastCursor = e.GetPosition(RootCanvas);
        _hovering = true;
        HookRendering();

        if (_dragInitiated)
        {
            PositionGhost(_lastCursor);
            // Hovering the Recycle Bin with a pinned shortcut arms "Remove" (drop there to unpin).
            bool overBin = IsRemovable(_dragCandidate)
                && IsOverRecycleBin(IsVerticalDock ? _lastCursor.Y : _lastCursor.X);
            if (overBin)
            {
                if (!_removeArmed)
                    ArmRemove();
                _dragSteadyTimer.Stop(); // the bin, not stillness, drives the arm here
            }
            // Real motion (away from the bin) cancels a pending/active "Remove" and restarts the hold.
            else if (Distance(_lastCursor, _steadyAnchor) > SteadyEpsilon)
            {
                _steadyAnchor = _lastCursor;
                if (_removeArmed)
                    DisarmRemove();
                RestartSteady();
            }
        }
        else
        {
            MaybeStartDrag(e);
        }
    }

    private void MaybeStartDrag(MouseEventArgs e)
    {
        if (_dragCandidate is null || _dragInitiated || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        StartDrag();
    }

    // Begins the custom (non-modal) drag: lift the in-canvas tile into a gap and show the
    // free-roaming ghost popup. Used both on movement and on a 500ms long-press.
    private void StartDrag()
    {
        if (_dragCandidate is null)
            return;

        _dragInitiated = true;
        ViewModel?.BeginItemDrag(_dragCandidate);
        CaptureMouse();

        GhostIcon.Source = _dragCandidate.Icon;
        GhostRemoveTag.BeginAnimation(OpacityProperty, null);
        GhostRemoveTag.Opacity = 0; // start disarmed; ArmRemove fades it in
        GhostRoot.BeginAnimation(OpacityProperty, null); // drop any leftover fade-out hold
        GhostRoot.Opacity = 1;
        DragGhost.IsOpen = true;
        PositionGhost(_lastCursor);

        _steadyAnchor = _lastCursor;
        RestartSteady();
        HookRendering();
    }

    // After DragSteadyMs of stillness: if the gesture hasn't lifted yet (pure long-press),
    // start it now; then arm "Remove" for pinned shortcuts.
    private void OnDragSteadyElapsed(object? sender, EventArgs e)
    {
        _dragSteadyTimer.Stop();
        if (_dragCandidate is null)
            return;
        if (!_dragInitiated)
            StartDrag();
        if (IsRemovable(_dragCandidate))
            ArmRemove();
    }

    private void RestartSteady()
    {
        _dragSteadyTimer.Stop();
        if (IsRemovable(_dragCandidate)) // only pinned shortcuts can be removed
            _dragSteadyTimer.Start();
    }

    private void ArmRemove()
    {
        _removeArmed = true;
        FadeRemoveTag(true);
    }

    private void DisarmRemove()
    {
        _removeArmed = false;
        FadeRemoveTag(false);
    }

    private void FadeRemoveTag(bool show)
    {
        var fade = new DoubleAnimation(show ? 1.0 : 0.0, new Duration(TimeSpan.FromMilliseconds(show ? 120 : 140)));
        GhostRemoveTag.BeginAnimation(OpacityProperty, fade);
    }

    private void PositionGhost(Point cursorInCanvas)
    {
        DragGhost.HorizontalOffset = cursorInCanvas.X - GhostCenterX;
        DragGhost.VerticalOffset = cursorInCanvas.Y - GhostCenterY;
    }

    // Fades the ghost out, then closes it. A slightly longer fade reads as a "poof" on remove.
    private void EndGhost(bool poof)
    {
        if (!DragGhost.IsOpen)
            return;
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(poof ? 180 : 130)));
        fade.Completed += (_, _) =>
        {
            DragGhost.IsOpen = false;
            GhostRoot.BeginAnimation(OpacityProperty, null); // release the hold so Opacity sticks
            GhostRoot.Opacity = 1;
            GhostRemoveTag.BeginAnimation(OpacityProperty, null);
            GhostRemoveTag.Opacity = 0; // reset for the next drag
        };
        GhostRoot.BeginAnimation(OpacityProperty, fade);
    }

    private static bool IsRemovable(DockItemViewModel? item) => item is { IsTaskbarApp: true, IsPinned: true };

    // --- Separator drag = resize the dock (the Size setting) ---

    // screenCross is the cursor's screen coord on the cross axis in device px (Y for a horizontal
    // dock, X for a vertical one). PointToScreen stays correct even as the dock window moves while
    // growing. The dock grows when dragging away from the docked edge (into the depth).
    private void HandleSeparatorResize(double screenCross)
    {
        if (ViewModel is null)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiScale = IsVerticalDock ? dpi.DpiScaleX : dpi.DpiScaleY;
        double rawDelta = ViewModel.Settings.Edge switch
        {
            DockEdge.Top => screenCross - _resizeStartCursorCross,  // drag down = larger
            DockEdge.Left => screenCross - _resizeStartCursorCross, // drag right = larger
            DockEdge.Right => _resizeStartCursorCross - screenCross, // drag left = larger
            _ => _resizeStartCursorCross - screenCross,             // Bottom: drag up = larger
        };
        double deltaDip = rawDelta / dpiScale;

        if (!_separatorResize)
        {
            if (Math.Abs(deltaDip) < SystemParameters.MinimumVerticalDragDistance)
                return; // not a drag yet
            _separatorResize = true;
            _resizePressed = false;
            CaptureMouse();
            Cursor = IsVerticalDock ? Cursors.SizeWE : Cursors.SizeNS;
        }

        double newSize = Math.Round(Math.Clamp(_resizeStartIconSize + deltaDip, SizeMin, SizeMax));
        if (newSize != ViewModel.Settings.IconSize)
        {
            ViewModel.Settings.IconSize = newSize;
            ViewModel.RecomputeLayout();            // resize the dock live
            _settingsWindow?.SyncSizeFromSettings(); // keep the Dock Preferences slider in sync
        }
    }

    private void EndSeparatorResize()
    {
        bool wasResizing = _separatorResize;
        _separatorResize = false;
        _resizePressed = false;
        if (wasResizing)
        {
            ReleaseMouseCapture();
            Cursor = null;
            ReserveAppBarSpace(); // update the reserved strip to the dropped dock size (deferred during the drag)
            ViewModel?.Save();    // persist the new Size
        }
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void OnDockMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_separatorResize || _resizePressed)
        {
            EndSeparatorResize();
            e.Handled = true;
            return;
        }

        _dragSteadyTimer.Stop();
        if (!_dragInitiated)
        {
            _dragCandidate = null;
            return;
        }

        var item = _dragCandidate;
        bool armed = _removeArmed;
        bool removed = false;       // a pin was removed this release (drop on the bin / hold-to-Remove) → "poof"
        _dragInitiated = false;     // clear before releasing capture so OnLostMouseCapture no-ops
        ReleaseMouseCapture();

        if (ViewModel is not null && item is not null)
        {
            bool removable = IsRemovable(item);
            bool unpinnedApp = item is { IsTaskbarApp: true, IsPinned: false };
            var pos = e.GetPosition(RootCanvas);
            bool overDock = pos.X >= 0 && pos.Y >= 0 && pos.X <= ActualWidth && pos.Y <= ActualHeight;
            bool overRecycle = IsOverRecycleBin(IsVerticalDock ? pos.Y : pos.X);

            if (removable && (overRecycle || armed))
            {
                ViewModel.UnpinApp(item.LaunchPath);                            // drop on the Recycle Bin, or hold-to-Remove
                Sounds.Play(Sounds.Remove);
                removed = true;
            }
            else if (removable && overDock)
                ViewModel.MovePin(item.LaunchPath, ViewModel.DragInsertIndex);  // reorder pins
            else if (unpinnedApp && overDock)
                ViewModel.PinApp(item.LaunchPath, ViewModel.DragInsertIndex, item.DisplayName); // pin a running app where dropped (keep its open name)
            // Otherwise (minimized window, or dropped away): no change → snaps back.

            ViewModel.EndItemDrag();    // the in-canvas tile reappears and settles to its slot
            RefreshTaskbarApps();
        }

        EndGhost(poof: removed);
        _dragCandidate = null;
        _removeArmed = false;
        e.Handled = true;
    }

    // A drag can be interrupted (Alt+Tab, another app grabs capture). Tidy up like a snap-back.
    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_separatorResize)
        {
            EndSeparatorResize();
            return;
        }
        if (!_dragInitiated)
            return; // a normal release already handled things
        _dragInitiated = false;
        _dragSteadyTimer.Stop();
        ViewModel?.EndItemDrag();
        RefreshTaskbarApps();
        EndGhost(poof: false);
        _dragCandidate = null;
        _removeArmed = false;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        ClearWindowRegion(); // un-clip so magnified icons can render above the bar and stay clickable
        HookRendering();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Keep the loop running so the dock eases back to rest, then it self-detaches.
        _hovering = false;
    }

    // --- Docking behavior (always-visible: reserve a strip via the AppBar) ---

    private void ApplyBehavior()
    {
        if (_hwnd == IntPtr.Zero || ViewModel is null)
            return;

        // Reserve a strip so other windows don't overlap the resting bar.
        ReserveAppBarSpace();
        PositionDock();
        _appBar?.NotifyPosChanged();

        ApplyIdleRegion(); // clip to the resting bar so the overflow area stays click-through
    }

    private void ReserveAppBarSpace()
    {
        if (_appBar is null || ViewModel is null)
            return;

        // Reserve exactly the resting (un-magnified) dock: from the docked edge to the bar's FAR edge
        // (its small margin from the screen edge included), so a maximized window abuts the dock with no
        // gap and no overlap. Not just BarHeight — that omits the bar's edge margin and would leave the
        // window slightly under the bar.
        bool ready = ViewModel.BarHeight > 0 && ViewModel.BarWidth > 0
            && ViewModel.WindowHeight > 0 && ViewModel.WindowWidth > 0;
        double thicknessDip = !ready ? 64 : ViewModel.Settings.Edge switch
        {
            DockEdge.Top => ViewModel.BarTop + ViewModel.BarHeight,
            DockEdge.Left => ViewModel.BarLeft + ViewModel.BarWidth,
            DockEdge.Right => ViewModel.WindowWidth - ViewModel.BarLeft,
            _ => ViewModel.WindowHeight - ViewModel.BarTop, // Bottom
        };
        if (thicknessDip <= 0)
            thicknessDip = 64;
        var info = Monitors.ForWindow(_hwnd);
        int thicknessPx = (int)Math.Round(thicknessDip * info.Scale);

        _appBar.Register();
        _appBar.ReserveEdge(ViewModel.Settings.Edge, info.MonitorPx, thicknessPx);
    }

    /// <summary>
    /// When the dock is idle, clips the window down to the resting bar so the magnification-overflow
    /// area above it is click-through (the AppBar only reserves the resting bar; the taller window
    /// must not block windows underneath). Cleared while hovering so the magnified icons render above
    /// the bar and stay clickable.
    /// </summary>
    private void ApplyIdleRegion()
    {
        if (_hwnd == IntPtr.Zero || ViewModel is null)
            return;
        if (_hovering || ViewModel.WindowWidth <= 0 || ViewModel.WindowHeight <= 0)
        {
            ClearWindowRegion();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double pad = DockRegionTopPaddingDip; // keep the bar's drop shadow
        // Keep the strip from the screen edge through the bar (+shadow pad); clip the magnification
        // overflow that bleeds away from the docked edge so it stays click-through.
        double l = 0, t = 0, r = ViewModel.WindowWidth, b = ViewModel.WindowHeight;
        switch (ViewModel.Settings.Edge)
        {
            case DockEdge.Top: b = ViewModel.BarTop + ViewModel.BarHeight + pad; break;
            case DockEdge.Left: r = ViewModel.BarLeft + ViewModel.BarWidth + pad; break;
            case DockEdge.Right: l = Math.Max(0, ViewModel.BarLeft - pad); break;
            default: t = Math.Max(0, ViewModel.BarTop - pad); break; // Bottom
        }

        int left = (int)Math.Floor(l * dpi.DpiScaleX);
        int top = (int)Math.Floor(t * dpi.DpiScaleY);
        int right = (int)Math.Ceiling(r * dpi.DpiScaleX) + 1;
        int bottom = (int)Math.Ceiling(b * dpi.DpiScaleY) + 1;

        var region = PInvoke.CreateRectRgn(left, top, right, bottom);
        PInvoke.SetWindowRgn((HWND)_hwnd, region, true); // the window takes ownership of the region
        _windowRegionClipped = true;
    }

    /// <summary>Removes any window-region clip so the full window is rendered and hit-testable.</summary>
    private void ClearWindowRegion()
    {
        if (_hwnd == IntPtr.Zero || !_windowRegionClipped)
            return;
        PInvoke.SetWindowRgn((HWND)_hwnd, default, true);
        _windowRegionClipped = false;
    }

    private void SetEdge(DockEdge edge)
    {
        if (ViewModel is null || ViewModel.Settings.Edge == edge)
            return;
        ViewModel.ApplyEdge(edge); // persist, re-lay out, and notify edge-derived view bindings
        ApplyBehavior();           // move the AppBar reservation, reposition, and re-clip
    }

    private void SetTaskbarVisibility(TaskbarVisibility mode)
    {
        if (ViewModel is null || ViewModel.Settings.TaskbarVisibility == mode)
            return;
        ViewModel.Settings.TaskbarVisibility = mode;
        ViewModel.Save();
        ApplyTaskbarVisibility();
        PositionDock(); // bottom reference changed (work area vs full screen)
    }

    private void SetShowMenuBar(bool show)
    {
        if (ViewModel is null || ViewModel.Settings.ShowMenuBar == show)
            return;
        ViewModel.Settings.ShowMenuBar = show;
        ViewModel.Save();
        App.Current.SetMenuBarVisible(show); // the app owns the menu bar window's lifetime
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The OS light/dark setting changed — re-theme if we're following the system.
        if (msg == WM_SETTINGCHANGE
            && ViewModel?.Settings.Theme == DockTheme.System
            && Marshal.PtrToStringAuto(lParam) == "ImmersiveColorSet")
        {
            ApplyTheme();
        }

        if ((uint)msg == AppBarCallbackMessage)
        {
            switch ((uint)wParam.ToInt64())
            {
                case PInvoke.ABN_POSCHANGED:
                    // The taskbar or another appbar moved; re-reserve and reposition.
                    ReserveAppBarSpace();
                    PositionDock();
                    handled = true;
                    break;
                case PInvoke.ABN_FULLSCREENAPP:
                    UpdateFullscreenState(); // a full-screen app opened/closed — clear or restore the dock
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    // --- Minimize → dock tile, and tile click → restore (genie or scale per the setting) ---

    /// <summary>The minimize/restore animator selected by <see cref="MinimizeEffect"/>. Suck and Genie
    /// share the mesh animator, differing only in its <see cref="GenieAnimator.Style"/> curve.</summary>
    private IMinimizeAnimator MinimizeAnimator
    {
        get
        {
            var effect = ViewModel?.Settings.MinimizeEffect ?? MinimizeEffect.Genie;
            double speed = ViewModel?.Settings.EffectSpeed ?? 1.0;
            if (effect == MinimizeEffect.Scale)
            {
                _scale.SpeedMultiplier = speed;
                return _scale;
            }
            _genie.Style = effect == MinimizeEffect.Genie
                ? GenieAnimator.GenieStyle.Genie
                : GenieAnimator.GenieStyle.Suck;
            _genie.SpeedMultiplier = speed;
            return _genie;
        }
    }

    /// <summary>A real (external) window started minimizing: use the capture taken while it was still
    /// visible — capturing now would grab a black sliver — and warp it into the dock.</summary>
    private void OnWindowMinimizing(IntPtr hwnd)
        => MinimizeToDock(hwnd, _thumbnails.TryGet(hwnd) ?? WindowCapture.Capture(hwnd));

    /// <summary>A window we have a minimized tile for was restored by something other than the dock
    /// (taskbar, Alt+Tab, the app itself). The OS already brought it back — and since its transitions
    /// are suppressed it popped in instantly, which is exactly what's expected from those gestures — so
    /// we just drop the now-stale tile/tracking rather than play a reverse warp over the visible window.</summary>
    private void OnWindowUnminimized(IntPtr hwnd)
    {
        if (ViewModel is null || _busy.Contains(hwnd))
            return; // our own click-to-restore drives the warp and cleans up itself

        var tile = ViewModel.FindMinimizedWindow(hwnd);
        if (tile is null && !_iconMinimized.ContainsKey(hwnd))
            return; // not a window we're tracking

        if (tile is not null)
            ViewModel.RemoveMinimizedWindow(tile);
        _iconMinimized.Remove(hwnd);
        _minimizedSourcePx.Remove(hwnd);
    }

    /// <summary>On exit, un-minimizes every window the dock had minimized (tile or into-icon) so the user
    /// isn't left with windows stranded behind a dock that's no longer there. Also re-enables the OS
    /// min/max transitions we suppressed on them, leaving each app's animations as we found them.</summary>
    private void RestoreAllMinimized()
    {
        if (ViewModel is null)
            return;
        var hwnds = new HashSet<IntPtr>();
        foreach (var tile in ViewModel.MinimizedWindows)
            hwnds.Add(tile.Hwnd);
        foreach (var hwnd in _iconMinimized.Keys)
            hwnds.Add(hwnd);

        foreach (var hwnd in hwnds)
        {
            if (!WindowControl.IsWindow(hwnd))
                continue;
            WindowControl.SetTransitionsEnabled(hwnd, true);
            WindowControl.RestoreNoForeground(hwnd);
        }
    }

    /// <summary>The user is about to minimize <paramref name="hwnd"/> (its min-button release or Win+Down
    /// was intercepted before the OS acted). Drives the no-flash warp and focuses the next window after,
    /// like a normal minimize; see <see cref="MinimizeOneAnimated"/>.</summary>
    private void InterceptedMinimize(IntPtr hwnd) => MinimizeOneAnimated(hwnd, null);

    /// <summary>How long to let a just-raised window repaint on top before capturing it (blind constant).</summary>
    private const int ForegroundSettleMs = 110;

    /// <summary>Minimizes one still-visible window with the warp: capture it fresh, paint that as frame 0,
    /// then minimize behind the overlay (no flash). Runs <paramref name="onDone"/> once the warp finishes
    /// (or immediately if there's nothing to do) — used to chain a sequential Win+M.</summary>
    /// <param name="raiseIfNeeded">When the window isn't already foreground, raise + settle it so the
    /// capture isn't occluded (single minimize). The Win+M cascade passes false: it walks windows top-down
    /// so each is already the topmost non-minimized one, and raising would need a focus change we avoid.</param>
    /// <param name="focusNext">After minimizing, focus the next app window (single minimize, matching the
    /// OS). Win+M passes false: it ends on the desktop, and forcing foreground from our non-foreground
    /// process makes the next window's taskbar button flash instead of focusing.</param>
    private void MinimizeOneAnimated(IntPtr hwnd, Action? onDone, bool raiseIfNeeded = true, bool focusNext = true)
    {
        if (ViewModel is null || _busy.Contains(hwnd) || ViewModel.FindMinimizedWindow(hwnd) is not null
            || _iconMinimized.ContainsKey(hwnd))
        {
            onDone?.Invoke();
            return;
        }

        if (!raiseIfNeeded || (IntPtr)PInvoke.GetForegroundWindow() == hwnd)
        {
            // Already the top-most window (foreground, or the cascade has minimized everything above it) —
            // capture now.
            CaptureThenMinimize(hwnd, onDone, focusNext);
            return;
        }

        // Raise it so the capture doesn't include whatever was covering it, then warp once it has had a
        // moment to repaint on top.
        PInvoke.SetForegroundWindow((HWND)hwnd);
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ForegroundSettleMs) };
        settle.Tick += (_, _) => { settle.Stop(); CaptureThenMinimize(hwnd, onDone, focusNext); };
        settle.Start();
    }

    private void CaptureThenMinimize(IntPtr hwnd, Action? onDone, bool focusNext)
    {
        if (!WindowControl.IsWindow(hwnd))
        {
            onDone?.Invoke();
            return;
        }
        var capture = WindowCapture.Capture(hwnd);
        if (capture is null)
        {
            // No image to warp — still honour the user's intent with a plain minimize (we swallowed the click).
            if (focusNext)
                MinimizeAndFocusNext(hwnd);
            else
                WindowControl.Minimize(hwnd);
            onDone?.Invoke();
            return;
        }
        MinimizeToDock(hwnd, capture, windowStillVisible: true, onDone, focusNext);
    }

    /// <summary>Win+M: minimize every window, one at a time. Walks the windows in Z-order (top first) so
    /// each is captured cleanly as the topmost remaining one, with no focus changes (which would flash the
    /// taskbar) — it ends on the desktop, like the OS Win+M.</summary>
    private void OnMinimizeAllRequested()
    {
        if (ViewModel is null)
            return;
        uint own = (uint)Environment.ProcessId;
        var windows = TaskbarApps.EnumerateAppWindows(own) // Z-order, top first
            .Select(w => w.Hwnd)
            .Where(h => !WindowControl.IsIconic(h))
            .ToList();
        MinimizeListSequential(windows, 0);
    }

    private void MinimizeListSequential(List<IntPtr> windows, int index)
    {
        if (ViewModel is null || index >= windows.Count)
            return;
        var hwnd = windows[index];
        void Next() => MinimizeListSequential(windows, index + 1);
        if (!WindowControl.IsWindow(hwnd) || WindowControl.IsIconic(hwnd) || _busy.Contains(hwnd)
            || ViewModel.FindMinimizedWindow(hwnd) is not null || _iconMinimized.ContainsKey(hwnd))
        {
            Next();
            return;
        }
        MinimizeOneAnimated(hwnd, Next, raiseIfNeeded: false, focusNext: false);
    }

    /// <summary>Warps a just-minimized window's <paramref name="capture"/> into its app icon (when
    /// "minimize into icon" is on and the app has a dock icon) or into a new thumbnail tile. Shared by
    /// external windows (via the minimize hook) and the dock's own Preferences window. When
    /// <paramref name="windowStillVisible"/> is true the window hasn't been minimized yet (the gesture
    /// was intercepted): frame 0 is painted first, then the window is minimized behind it.</summary>
    private void MinimizeToDock(IntPtr hwnd, WindowCapture.Result? capture, bool windowStillVisible = false,
        Action? onDone = null, bool focusNext = true)
    {
        if (ViewModel is null || _busy.Contains(hwnd) || ViewModel.FindMinimizedWindow(hwnd) is not null
            || _iconMinimized.ContainsKey(hwnd))
        {
            onDone?.Invoke();
            return;
        }
        _busy.Add(hwnd);

        WindowControl.SuppressTransitions(hwnd); // future restore won't play the OS animation

        if (capture is null)
        {
            _busy.Remove(hwnd);
            onDone?.Invoke();
            return;
        }

        var info = Monitors.ForWindow(hwnd);
        var sourceDip = ToDip(capture.Value.ScreenRectPx, info.Scale);
        var monitorDip = ToDip(info.MonitorPx, info.Scale);
        _minimizedSourcePx[hwnd] = capture.Value.ScreenRectPx; // restore warp ends at this exact size

        var animator = MinimizeAnimator;
        var bitmap = capture.Value.Bitmap;

        // "Minimize into icon": warp into the app's dock icon (pinned or running) instead of a separate
        // thumbnail tile. Only when an app icon actually exists for this window — otherwise fall back
        // to a thumbnail tile (resolved in the deferred step below).
        var appTile = ViewModel.Settings.MinimizeIntoIcon ? ViewModel.FindAppForWindow(hwnd) : null;

        // The minimize-start event reaches us only AFTER the OS has already minimized the window, and
        // transitions are suppressed, so it vanished instantly (no OS scale animation). Don't restore
        // it — just paint the captured frame at the window's old spot and warp it into the dock right
        // away: a single minimize, no restore/re-minimize dance.
        animator.ShowAtSource(bitmap, sourceDip, monitorDip);

        // Intercepted gesture: the real window is still up. Frame 0 is now queued to paint over it.
        // Wait until that overlay frame has actually been rendered/presented before minimizing the
        // real window behind it (transitions are suppressed, so it vanishes instantly with no OS
        // animation) — otherwise the window can disappear a beat before the capture is on screen.
        if (windowStillVisible)
            AfterRendered(() =>
            {
                if (!WindowControl.IsWindow(hwnd))
                    return;
                if (focusNext)
                    MinimizeAndFocusNext(hwnd); // single minimize: hand focus to the next window
                else
                    WindowControl.Minimize(hwnd); // Win+M: no focus change (avoids the taskbar-flash)
            });

        Point target;
        DockItemViewModel landing;
        if (appTile is not null)
        {
            // Into the app icon: remember the capture so the icon click can warp it back out.
            _iconMinimized[hwnd] = bitmap;
            landing = appTile;
            target = TileScreenCenter(appTile);
        }
        else
        {
            var tile = ViewModel.AddMinimizedWindow(hwnd, bitmap, TaskbarApps.GetWindowTitle(hwnd));
            LoadOverlayIcon(tile, hwnd);
            landing = tile;
            target = TileScreenCenter(tile);
        }
        animator.TargetTileWidth = TileWidthOf(landing); // shrink the window down to the tile's width
        animator.AnimateTo(target, reverse: false, onCompleted: () => { _busy.Remove(hwnd); onDone?.Invoke(); });
    }

    /// <summary>Minimizes <paramref name="hwnd"/> and explicitly activates the next app window. SW_MINIMIZE
    /// alone often hands activation to the topmost dock (our own window) rather than the user's next
    /// window, which both leaves focus stranded and stalls the Win+M cascade (its next step reads the
    /// foreground) — so we pick the next real window beforehand and foreground it ourselves.</summary>
    private void MinimizeAndFocusNext(IntPtr hwnd)
    {
        var next = NextAppWindow(hwnd);
        WindowControl.Minimize(hwnd);
        if (next != IntPtr.Zero && WindowControl.IsWindow(next) && !WindowControl.IsIconic(next))
            PInvoke.SetForegroundWindow((HWND)next);
    }

    /// <summary>The top-most taskbar-eligible app window other than <paramref name="exclude"/> that isn't
    /// minimized — i.e. the window that should gain focus when <paramref name="exclude"/> minimizes.</summary>
    private static IntPtr NextAppWindow(IntPtr exclude)
    {
        uint own = (uint)Environment.ProcessId;
        foreach (var w in TaskbarApps.EnumerateAppWindows(own)) // returned in Z-order, top first
            if (w.Hwnd != exclude && !WindowControl.IsIconic(w.Hwnd))
                return w.Hwnd;
        return IntPtr.Zero;
    }

    /// <summary>At startup, adopts windows that were already minimized before the dock launched so they
    /// match what a live minimize would have produced: a thumbnail tile, or — in "minimize into icon"
    /// mode — owned by their app's dock icon. An already-minimized window can't be captured, so the app's
    /// icon stands in for the missing window thumbnail.</summary>
    private void SyncPreMinimizedWindows()
    {
        if (ViewModel is null)
            return;
        uint own = (uint)Environment.ProcessId;
        foreach (var w in TaskbarApps.EnumerateAppWindows(own))
        {
            var hwnd = w.Hwnd;
            if (!WindowControl.IsIconic(hwnd))
                continue; // only windows that are already minimized
            if (_busy.Contains(hwnd) || ViewModel.FindMinimizedWindow(hwnd) is not null
                || _iconMinimized.ContainsKey(hwnd))
                continue; // already represented

            // A later restore should play our warp, not the OS animation (as for windows we minimize).
            WindowControl.SuppressTransitions(hwnd);

            var appVm = ViewModel.FindAppForWindow(hwnd);
            if (ViewModel.Settings.MinimizeIntoIcon && appVm is not null)
            {
                // Into-icon mode: it already shows under its app icon; register it so a click warps it
                // back out (using the app icon as the stand-in image — no window capture is possible).
                AdoptIntoIcon(hwnd, appVm);
            }
            else
            {
                // Tile mode: a standalone minimized tile, showing the app's icon in place of a thumbnail.
                var tile = ViewModel.AddMinimizedWindow(hwnd, appVm?.Icon, TaskbarApps.GetWindowTitle(hwnd));
                if (tile.Icon is null)
                    LoadTileIconFromApp(tile, hwnd);
            }
        }
    }

    /// <summary>Records an already-minimized window as stashed into its app icon, using the app's icon as
    /// the warp-out image (an iconic window can't be captured).</summary>
    private async void AdoptIntoIcon(IntPtr hwnd, DockItemViewModel appVm)
    {
        var icon = appVm.Icon as BitmapSource
            ?? await ShortcutService.LoadIconAsync(TaskbarApps.GetWindowExePath(hwnd), 256) as BitmapSource
            ?? await ShortcutService.LoadWindowIconAsync(hwnd) as BitmapSource;
        if (icon is not null && WindowControl.IsIconic(hwnd) && !_iconMinimized.ContainsKey(hwnd))
            _iconMinimized[hwnd] = icon;
    }

    /// <summary>Fills a pre-minimized tile's image with its app icon (no window capture exists for it).</summary>
    private static async void LoadTileIconFromApp(DockItemViewModel tile, IntPtr hwnd)
    {
        string exe = TaskbarApps.GetWindowExePath(hwnd);
        var icon = (string.IsNullOrEmpty(exe) ? null : await ShortcutService.LoadIconAsync(exe, 256))
            ?? await ShortcutService.LoadWindowIconAsync(hwnd);
        if (icon is not null)
            tile.Icon = icon;
    }

    /// <summary>The dock width (DIP) a window lands at, for sizing the minimize warp's final width.</summary>
    private double TileWidthOf(DockItemViewModel tile)
        => tile.RenderWidth > 1 ? tile.RenderWidth : (ViewModel?.Settings.IconSize ?? 48);

    /// <summary>Loads the app icon for a minimized tile and badges it onto the thumbnail.</summary>
    private static async void LoadOverlayIcon(DockItemViewModel tile, IntPtr hwnd)
    {
        string exe = TaskbarApps.GetWindowExePath(hwnd);
        if (string.IsNullOrEmpty(exe))
            return;
        tile.OverlayIcon = await ShortcutService.LoadIconAsync(exe, 64);
    }

    /// <summary>A minimized-window tile was clicked: reverse-warp out and restore the window.</summary>
    private void RestoreMinimized(DockItemViewModel tile)
        => RestoreWindowAnimated(tile.Hwnd, tile, TileScreenCenter(tile), tile.Icon as BitmapSource, static () => { });

    /// <summary>
    /// Restores one minimized window, reverse-warping out of <paramref name="target"/> (its tile or
    /// app icon), then runs <paramref name="onDone"/> (used to chain a sequential group restore).
    /// Falls back to a plain restore when there's no captured bitmap to animate.
    /// </summary>
    private void RestoreWindowAnimated(IntPtr hwnd, DockItemViewModel? tile, Point target, BitmapSource? bitmap, Action onDone)
    {
        if (ViewModel is null || _busy.Contains(hwnd))
        {
            onDone();
            return;
        }

        if (!WindowControl.IsWindow(hwnd))
        {
            // Window is gone; drop any stale tile / tracking and move on.
            if (tile is not null)
                ViewModel.RemoveMinimizedWindow(tile);
            _iconMinimized.Remove(hwnd);
            _minimizedSourcePx.Remove(hwnd);
            onDone();
            return;
        }

        if (bitmap is null)
        {
            WindowControl.Restore(hwnd);
            if (tile is not null)
                ViewModel.RemoveMinimizedWindow(tile);
            _iconMinimized.Remove(hwnd);
            _minimizedSourcePx.Remove(hwnd);
            onDone();
            return;
        }

        _busy.Add(hwnd);
        WindowControl.SuppressTransitions(hwnd);

        var info = Monitors.ForWindow(hwnd);
        // Prefer the rect captured at minimize (the window's true visual bounds, sans shadow/border) so
        // the reverse warp ends at the size the bitmap was grabbed — the placement rect is larger by the
        // invisible border and would make the window look enlarged just before it lands.
        var restoreRectPx = _minimizedSourcePx.TryGetValue(hwnd, out var capturedPx)
            ? capturedPx
            : (WindowControl.GetRestoreRect(hwnd) ?? new Int32Rect(0, 0, 600, 400));
        var windowDip = ToDip(restoreRectPx, info.Scale);
        var monitorDip = ToDip(info.MonitorPx, info.Scale);

        var restoreAnimator = MinimizeAnimator;
        restoreAnimator.TargetTileWidth = tile is not null ? TileWidthOf(tile) : (ViewModel.Settings.IconSize);
        restoreAnimator.Play(bitmap, windowDip, target, monitorDip, reverse: true, onCompleted: () =>
        {
            WindowControl.Restore(hwnd);
            if (tile is not null)
                ViewModel.RemoveMinimizedWindow(tile);
            _iconMinimized.Remove(hwnd);
            _minimizedSourcePx.Remove(hwnd);
            _busy.Remove(hwnd);
            onDone();
        });
    }

    /// <summary>Runs <paramref name="action"/> once the next overlay frame has actually been rendered
    /// and presented (waits two <see cref="CompositionTarget.Rendering"/> ticks: the first renders the
    /// just-queued frame, the second runs after it's on screen) — so a just-shown capture covers the
    /// real window before we minimize it behind the overlay.</summary>
    private static void AfterRendered(Action action)
    {
        int ticks = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (++ticks < 2)
                return;
            CompositionTarget.Rendering -= handler;
            action();
        };
        CompositionTarget.Rendering += handler;
    }

    private static Rect ToDip(Int32Rect r, double scale)
        => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);

    private static Rect ToDip(Rect r, double scale)
        => new(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);

    /// <summary>Screen-space (DIP) center of a tile.</summary>
    private Point TileScreenCenter(DockItemViewModel tile)
    {
        var (left, top) = ComputePlacement();
        return new Point(left + tile.X + tile.RenderSize / 2, top + tile.Y + tile.RenderSize / 2);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Track hover from the real cursor position rather than trusting WPF's MouseLeave: on this
        // transparent layered window the cursor crossing a fully-transparent overflow pixel spuriously
        // fires MouseLeave, which would drop _hovering and make magnification stutter. The window's
        // footprint (full rect, including the overflow above the bar) is a stable hover test. Skipped
        // during drag/resize, which pin _hovering themselves.
        if (ViewModel is not null && !_separatorResize && !_resizePressed && !_dragInitiated
            && PInvoke.GetCursorPos(out var cursor))
        {
            var w = PointFromScreen(new Point(cursor.X, cursor.Y));
            _mouseX = w.X;
            _mouseY = w.Y;
            _hovering = w.X >= 0 && w.Y >= 0 && w.X <= ViewModel.WindowWidth && w.Y <= ViewModel.WindowHeight;
        }

        // Suppress magnification while resizing via a separator (icons stay at the new resting size).
        double mouseMain = IsVerticalDock ? _mouseY : _mouseX;
        bool animating = ViewModel?.UpdateMagnification(mouseMain, _hovering && !_separatorResize) ?? false;
        SyncAcrylic();      // track the acrylic backdrop to the (magnifying) bar each frame

        // A shrunk-out (departed) item is removed off the render pass to avoid mutating Items mid-frame.
        if (ViewModel?.HasFinishedDeparting == true && !_finalizeScheduled)
        {
            _finalizeScheduled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _finalizeScheduled = false;
                ViewModel?.FinalizeDeparted();
            });
        }

        if (!animating && !_hovering)
        {
            UnhookRendering();
            ApplyIdleRegion(); // settled at rest → clip back to the bar so the overflow is click-through
        }
    }

    private void HookRendering()
    {
        if (_renderingHooked)
            return;
        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    // --- Item interaction ---

    private void DockItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragInitiated = false;
        _removeArmed = false;
        var item = (sender as FrameworkElement)?.DataContext as DockItemViewModel;

        // Dragging a separator up/down resizes the dock (the Size setting).
        if (item is { IsSeparator: true } && ViewModel is not null)
        {
            _resizePressed = true;
            var sp = PointToScreen(e.GetPosition(this));
            _resizeStartCursorCross = IsVerticalDock ? sp.X : sp.Y;
            _resizeStartIconSize = ViewModel.Settings.IconSize;
            _dragCandidate = null;
            return;
        }

        // Taskbar apps (pinned or running) and minimized-window tiles can be dragged.
        _dragCandidate = item is not null && (item.IsTaskbarApp || item.IsMinimizedWindow) ? item : null;
        _dragStart = e.GetPosition(this);
        _lastCursor = e.GetPosition(RootCanvas);
        _steadyAnchor = _lastCursor;
        RestartSteady(); // arms the hold-to-Remove countdown for pinned shortcuts only
    }

    private void DockItem_Click(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (_dragInitiated) // this gesture was a drag, not a click
        {
            _dragInitiated = false;
            return;
        }

        if (sender is not FrameworkElement { DataContext: DockItemViewModel item })
            return;

        if (item.IsMinimizedWindow)
            RestoreMinimized(item);
        else if (item.IsTaskbarApp)
            ActivateOrLaunch(item);
        else if (item.IsStartMenu)
            OpenStartMenu();
        else
            item.Activate(); // shortcut / Recycle Bin
    }

    /// <summary>
    /// Opens the Start menu from the dock and fully hides the taskbar while it's up — overriding
    /// auto-hide so the taskbar can't reveal itself alongside Start. The configured taskbar state is
    /// restored once Start closes (watched by <see cref="OnStartWatchTick"/>).
    /// </summary>
    private void OpenStartMenu()
    {
        // Keep the taskbar from popping up alongside Start — unless the user wants it always visible.
        if (ViewModel?.Settings.TaskbarVisibility != TaskbarVisibility.Always)
            Taskbar.Hide();
        StartMenu.Open(); // synthesize the Win key
        _startSeen = false;
        _startWatchTicks = 0;
        _startWatchTimer.Start();
    }

    private void OnStartWatchTick(object? sender, EventArgs e)
    {
        _startWatchTicks++;
        if (StartMenu.IsOpen())
        {
            _startSeen = true;            // Start is up — keep the taskbar hidden
            RaiseDockAboveStartMenu();    // ...and keep the dock in front, so Start sits behind it
            return;
        }

        // Start isn't showing. Restore once we've actually seen it open (the user dismissed it), or
        // after the grace period if it never appeared, so the taskbar is never left stuck hidden.
        if (_startSeen || _startWatchTicks >= StartWatchMaxTicks)
        {
            _startWatchTimer.Stop();
            ApplyTaskbarVisibility(); // back to the configured auto-hide / visible state
        }
    }

    // --- Per-item right-click context menus (built in code for live state / Alt handling) ---

    private void DockItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DockItemViewModel item } target || ViewModel is null)
            return;

        // Consume the right-click on the item so it doesn't fall through to the empty-space dock menu
        // (items without their own menu — Start / Recycle Bin / minimized tiles — simply show nothing).
        e.Handled = true;

        ContextMenu? menu = item switch
        {
            { IsPreferences: true } => BuildPreferencesMenu(item),
            { IsTaskbarApp: true } => BuildAppMenu(item),
            { IsSeparator: true } => BuildSeparatorMenu(),
            { IsRecycleBin: true } => BuildRecycleMenu(),
            _ => null,
        };
        if (menu is null)
            return;

        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // Right-clicking empty space inside the dock (the bar background) shows the dock-wide menu.
    private void Dock_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null)
            return;
        var menu = BuildSeparatorMenu();
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // Recycle Bin menu: empty it (with the OS confirmation prompt). Disabled when already empty.
    private ContextMenu BuildRecycleMenu()
    {
        var menu = new ContextMenu();
        var empty = new MenuItem { Header = Loc.T("Menu_EmptyRecycleBin"), IsEnabled = !RecycleBin.IsEmpty() };
        empty.Click += (_, _) => EmptyRecycleBin();
        menu.Items.Add(empty);
        return menu;
    }

    private void EmptyRecycleBin()
    {
        if (RecycleBin.IsEmpty())
            return;
        if (RecycleBin.Empty(_hwnd)) // shows the OS "permanently delete?" prompt
        {
            Sounds.Play(Sounds.EmptyTrash);
            RefreshTaskbarApps(); // refresh the bin's empty/full icon
        }
    }

    // Separator menu: dock-wide actions (matches the old shared menu).
    private ContextMenu BuildSeparatorMenu()
    {
        var menu = new ContextMenu();
        var prefs = new MenuItem { Header = Loc.T("Menu_DockPreferences") };
        prefs.Click += (_, _) => OpenDockPreferences();
        menu.Items.Add(prefs);
        var about = new MenuItem { Header = Loc.T("Menu_AboutDockable") };
        about.Click += (_, _) => OpenAbout();
        menu.Items.Add(about);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = Loc.T("Menu_QuitDockable") };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);
        return menu;
    }

    // Built-in Dock Preferences tile: Keep in Dock (pin toggle), and — while the window is open —
    // Quit (closes the Preferences window only, never the dock).
    private ContextMenu BuildPreferencesMenu(DockItemViewModel app)
    {
        var menu = new ContextMenu();

        var keep = new MenuItem { Header = Loc.T("Menu_KeepInDock"), IsCheckable = true, IsChecked = app.IsPinned };
        keep.Click += (_, _) =>
        {
            if (app.IsPinned)
                ViewModel!.UnpinApp(DockItem.PreferencesLaunchPath);
            else
                ViewModel!.PinApp(DockItem.PreferencesLaunchPath, int.MaxValue);
            RefreshTaskbarApps();
        };
        menu.Items.Add(keep);

        if (_settingsWindow is not null)
        {
            menu.Items.Add(new Separator());
            var quit = new MenuItem { Header = Loc.T("Menu_Quit") };
            quit.Click += (_, _) => _settingsWindow?.Close(); // closes Preferences only; the dock keeps running
            menu.Items.Add(quit);
        }

        return menu;
    }

    // Open/pinned app menu: Options ▶ (Keep in Dock / Open at Login / Show in Explorer), then — for
    // running apps — Show All Windows and Quit (Force Quit while Alt is held).
    private ContextMenu BuildAppMenu(DockItemViewModel app)
    {
        var menu = new ContextMenu();

        // New Window: launch another instance of the app (most apps open a fresh window).
        var newWindow = new MenuItem { Header = Loc.T("Menu_NewWindow") };
        newWindow.Click += (_, _) => ShortcutService.Launch(app.LaunchPath);
        menu.Items.Add(newWindow);

        // Rename: change a pinned shortcut's display label (persisted via PinNames).
        if (app.IsPinned)
        {
            var rename = new MenuItem { Header = Loc.T("Menu_Rename") };
            rename.Click += (_, _) => RenamePin(app);
            menu.Items.Add(rename);
        }

        menu.Items.Add(new Separator());

        var options = new MenuItem { Header = Loc.T("Menu_Options") };

        var keep = new MenuItem { Header = Loc.T("Menu_KeepInDock"), IsCheckable = true, IsChecked = app.IsPinned };
        keep.Click += (_, _) =>
        {
            if (app.IsPinned)
                ViewModel!.UnpinApp(app.LaunchPath);
            else
                ViewModel!.PinApp(app.LaunchPath, int.MaxValue, app.DisplayName); // append to the pinned list (keep its name)
            RefreshTaskbarApps();
        };
        options.Items.Add(keep);

        string exe = ResolveExecutable(app);
        string startupName = StartupEntryName(app, exe);
        var login = new MenuItem { Header = Loc.T("Menu_OpenAtLogin"), IsCheckable = true, IsChecked = StartupManager.IsEnabled(startupName) };
        login.Click += (_, _) =>
        {
            if (StartupManager.IsEnabled(startupName))
                StartupManager.Disable(startupName);
            else
                StartupManager.Enable(startupName, exe);
        };
        options.Items.Add(login);

        var reveal = new MenuItem { Header = Loc.T("Menu_ShowInExplorer") };
        reveal.Click += (_, _) => ShortcutService.RevealInExplorer(app.LaunchPath);
        options.Items.Add(reveal);

        menu.Items.Add(options);

        // Running-app actions only make sense when the app has open windows.
        if (app.Windows.Count > 0)
        {
            menu.Items.Add(new Separator());

            var showAll = new MenuItem { Header = Loc.T("Menu_ShowAllWindows") };
            showAll.Click += (_, _) => ActivateOrLaunch(app);
            menu.Items.Add(showAll);

            var quit = new MenuItem();
            SetQuitHeader(quit, AltHeld);
            quit.Click += (_, _) => QuitApp(app, force: AltHeld);
            menu.Items.Add(quit);

            // Live-toggle the Quit / Force Quit label as Alt is pressed/released with the menu open.
            menu.PreviewKeyDown += (_, _) => SetQuitHeader(quit, AltHeld);
            menu.PreviewKeyUp += (_, _) => SetQuitHeader(quit, AltHeld);
        }

        return menu;
    }

    /// <summary>Prompts for a new display label for a pinned shortcut and applies it.</summary>
    private void RenamePin(DockItemViewModel app)
    {
        var dialog = new InputDialog(Loc.T("Dialog_RenameShortcut"), app.DisplayName) { Owner = this };
        if (dialog.ShowDialog() == true)
            ViewModel?.RenamePin(app.LaunchPath, dialog.Value);
    }

    private static bool AltHeld => (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

    private static void SetQuitHeader(MenuItem item, bool force) => item.Header = Loc.T(force ? "Menu_ForceQuit" : "Menu_Quit");

    /// <summary>The app's executable path (from a running window if possible, else its launch path).</summary>
    private static string ResolveExecutable(DockItemViewModel app)
    {
        if (app.Windows.Count > 0)
        {
            string exe = TaskbarApps.GetWindowExePath(app.Windows[0]);
            if (!string.IsNullOrEmpty(exe))
                return exe;
        }
        return app.LaunchPath;
    }

    /// <summary>A stable HKCU Run value name for the app (its executable file name, else display name).</summary>
    private static string StartupEntryName(DockItemViewModel app, string exe)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        catch { /* fall through */ }
        return app.DisplayName;
    }

    /// <summary>Gracefully closes (or, when forced, kills) every window/process backing the app.</summary>
    private void QuitApp(DockItemViewModel app, bool force)
    {
        if (!force)
        {
            foreach (var hwnd in app.Windows.ToArray())
                WindowControl.Close(hwnd); // WM_CLOSE — lets the app prompt to save
            return;
        }

        var pids = new HashSet<uint>();
        foreach (var hwnd in app.Windows)
        {
            uint pid = WindowControl.GetProcessId(hwnd);
            if (pid != 0)
                pids.Add(pid);
        }
        foreach (uint pid in pids)
        {
            try { System.Diagnostics.Process.GetProcessById((int)pid).Kill(); }
            catch { /* already gone / no access */ }
        }
    }

    /// <summary>Shows the "Dock Preferences" window, focusing it if already open (single instance).</summary>
    private void OpenDockPreferences()
    {
        if (ViewModel is null)
            return;

        if (_settingsWindow is not null)
        {
            // Already open: if it's minimized into the dock, bring it back (this path skips the warp).
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            CleanUpPreferencesMinimize(ViewModel.PreferencesHwnd);
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(ViewModel, SetTheme, SetEdge, SetTaskbarVisibility, SetGlassEffect, SetShowMenuBar);

        // Realize the handle now so we can hook its minimize and track it as a running window.
        IntPtr prefsHwnd = new WindowInteropHelper(_settingsWindow).EnsureHandle();
        ViewModel.PreferencesHwnd = prefsHwnd;
        _prefsSource = HwndSource.FromHwnd(prefsHwnd);
        _prefsSource?.AddHook(PreferencesWndProc);
        WindowControl.SuppressTransitions(prefsHwnd); // its minimize is instant; the warp replaces it

        _settingsWindow.Closed += (_, _) =>
        {
            _prefsSource?.RemoveHook(PreferencesWndProc);
            _prefsSource = null;
            CleanUpPreferencesMinimize(prefsHwnd);
            _settingsWindow = null;
            if (ViewModel is not null)
            {
                ViewModel.PreferencesOpen = false;
                ViewModel.PreferencesHwnd = IntPtr.Zero;
                RefreshTaskbarApps();
            }
        };
        _settingsWindow.Show();

        // Reflect the open window as a running tile on the dock (running dot + an "open app" slot).
        ViewModel.PreferencesOpen = true;
        RefreshTaskbarApps();
    }

    // Drops any dock minimize tracking for the Preferences window (an icon-stashed capture and/or a
    // thumbnail tile) — e.g. when it's restored via the menu/tray or closed while minimized.
    private void CleanUpPreferencesMinimize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        _iconMinimized.Remove(hwnd);
        _minimizedSourcePx.Remove(hwnd);
        var tile = ViewModel?.FindMinimizedWindow(hwnd);
        if (tile is not null)
            ViewModel!.RemoveMinimizedWindow(tile);
    }

    // The dock's own Preferences window lives in our process, so the global minimize hook (which skips
    // the own process) never sees it. Intercept its SC_MINIMIZE: capture it while still visible,
    // minimize instantly (transitions suppressed), then warp it into the dock like any other window.
    private IntPtr PreferencesWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MINIMIZE = 0xF020;
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE
            && ViewModel is not null && !_busy.Contains(hwnd))
        {
            var capture = WindowCapture.Capture(hwnd);
            // Paint frame 0 over the still-visible window, then minimize behind it (no flash).
            MinimizeToDock(hwnd, capture, windowStillVisible: true);
            handled = true;                 // we drove the minimize + warp
        }
        return IntPtr.Zero;
    }

    /// <summary>Shows the "About Dockable" window, focusing it if already open (single instance).</summary>
    private void OpenAbout()
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
    }

    // --- Drag to reorder / pin ---

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            ClearExternalDropGap();
            e.Handled = true;
            return;
        }

        double main = DropMain(e.GetPosition(this));
        if (IsOverRecycleBin(main))
        {
            e.Effects = DragDropEffects.Move;
            ClearExternalDropGap(); // no placeholder when hovering the Recycle Bin
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
            ShowExternalDropGap(main); // part the tiles to preview where the icon would land
        }
        e.Handled = true;
    }

    // The external drag left the dock without dropping: close the placeholder gap.
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        ClearExternalDropGap();
        e.Handled = true;
    }

    // External files dragged from Explorer (internal reorder uses the custom mouse drag above).
    private void OnDrop(object sender, DragEventArgs e)
    {
        ClearExternalDropGap();
        if (ViewModel is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        double main = DropMain(e.GetPosition(this));

        // Dropped onto the Recycle Bin → move them there; anywhere else → pin them where the gap was.
        if (IsOverRecycleBin(main))
        {
            if (RecycleBin.SendToRecycleBin(paths))
                Sounds.Play(Sounds.DragToTrash);
            RefreshTaskbarApps(); // refresh the bin's empty/full icon promptly
            return;
        }

        int index = ViewModel.ComputeDropIndex(main);
        foreach (var path in paths)
            // Pin a .lnk's destination, not the .lnk — but keep the shortcut's name (e.g. "Chrome.lnk" → "Chrome").
            ViewModel.PinApp(TaskbarApps.ResolveToTarget(path), index++, System.IO.Path.GetFileNameWithoutExtension(path));

        RefreshTaskbarApps();
    }

    // Opens / refreshes the placeholder gap and keeps the render loop running so the tiles part and
    // track the cursor; cleared on leave/drop so they glide back together.
    private void ShowExternalDropGap(double main)
    {
        ClearWindowRegion(); // unclip so the widening bar and parted tiles render (and receive the drag)
        ViewModel?.UpdateExternalDrop(main);
        HookRendering();
    }

    private void ClearExternalDropGap()
    {
        ViewModel?.EndExternalDrop();
        HookRendering(); // run the loop so the parted tiles settle back
    }

    /// <summary>The cursor's main-axis (window) coordinate: X for a horizontal dock, Y for a vertical one.</summary>
    private double DropMain(Point p) => IsVerticalDock ? p.Y : p.X;

    /// <summary>Main-axis position/size of an item along the bar (handles both orientations).</summary>
    private (double Pos, double Size) ItemMain(DockItemViewModel item) =>
        IsVerticalDock ? (item.Y, item.RenderSize) : (item.X, item.RenderWidth);

    /// <summary>True if the given main-axis coordinate falls within the Recycle Bin tile.</summary>
    private bool IsOverRecycleBin(double cursorMain)
    {
        var bin = ViewModel?.Items.FirstOrDefault(i => i.IsRecycleBin);
        if (bin is null)
            return false;
        var (pos, size) = ItemMain(bin);
        return cursorMain >= pos && cursorMain <= pos + size;
    }


    // --- Tray icon ---

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Dockable",
            ContextMenu = BuildTrayMenu(),
            // The app icon (a URI-backed resource, which H.NotifyIcon accepts) renders the tray glyph.
            IconSource = AppIcon.Tray,
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ToggleVisibility();
        _trayIcon.ForceCreate();

        // The tray menu is built once (its labels are baked in), so rebuild it on a language change.
        Loc.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_trayIcon is not null)
            _trayIcon.ContextMenu = BuildTrayMenu();
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        var toggle = new MenuItem { Header = Loc.T("Tray_ShowHideDock") };
        toggle.Click += (_, _) => ToggleVisibility();
        menu.Items.Add(toggle);

        // Windows taskbar submenu: Always / Auto / Never (radio-style checks).
        var taskbarItem = new MenuItem { Header = Loc.T("Tray_Taskbar") };
        var tbAlways = new MenuItem { Header = Loc.T("Taskbar_Always"), IsCheckable = true };
        var tbAuto = new MenuItem { Header = Loc.T("Taskbar_Auto"), IsCheckable = true };
        var tbNever = new MenuItem { Header = Loc.T("Taskbar_Never"), IsCheckable = true };
        tbAlways.Click += (_, _) => SetTaskbarVisibility(TaskbarVisibility.Always);
        tbAuto.Click += (_, _) => SetTaskbarVisibility(TaskbarVisibility.Auto);
        tbNever.Click += (_, _) => SetTaskbarVisibility(TaskbarVisibility.Never);
        taskbarItem.Items.Add(tbAlways);
        taskbarItem.Items.Add(tbAuto);
        taskbarItem.Items.Add(tbNever);
        menu.Items.Add(taskbarItem);

        var menuBarItem = new MenuItem { Header = Loc.T("Toggle_ShowMenuBar"), IsCheckable = true };
        menuBarItem.Click += (_, _) => SetShowMenuBar(menuBarItem.IsChecked);
        menu.Items.Add(menuBarItem);

        // Theme submenu: Light / Dark / Auto (radio-style checks). "Auto" == DockTheme.System.
        var themeItem = new MenuItem { Header = Loc.T("Tray_Theme") };
        var themeLight = new MenuItem { Header = Loc.T("Theme_Light"), IsCheckable = true };
        var themeDark = new MenuItem { Header = Loc.T("Theme_Dark"), IsCheckable = true };
        var themeAuto = new MenuItem { Header = Loc.T("Theme_Auto"), IsCheckable = true };
        themeLight.Click += (_, _) => SetTheme(DockTheme.Light);
        themeDark.Click += (_, _) => SetTheme(DockTheme.Dark);
        themeAuto.Click += (_, _) => SetTheme(DockTheme.System);
        themeItem.Items.Add(themeLight);
        themeItem.Items.Add(themeDark);
        themeItem.Items.Add(themeAuto);
        menu.Items.Add(themeItem);

        var prefsItem = new MenuItem { Header = Loc.T("Menu_DockPreferences") };
        prefsItem.Click += (_, _) => OpenDockPreferences();
        menu.Items.Add(prefsItem);

        var aboutItem = new MenuItem { Header = Loc.T("Menu_AboutDockable") };
        aboutItem.Click += (_, _) => OpenAbout();
        menu.Items.Add(aboutItem);

        // Reflect current settings whenever the menu opens.
        menu.Opened += (_, _) =>
        {
            var tv = ViewModel?.Settings.TaskbarVisibility ?? TaskbarVisibility.Auto;
            tbAlways.IsChecked = tv == TaskbarVisibility.Always;
            tbAuto.IsChecked = tv == TaskbarVisibility.Auto;
            tbNever.IsChecked = tv == TaskbarVisibility.Never;
            menuBarItem.IsChecked = ViewModel?.Settings.ShowMenuBar ?? false;
            var theme = ViewModel?.Settings.Theme ?? DockTheme.System;
            themeLight.IsChecked = theme == DockTheme.Light;
            themeDark.IsChecked = theme == DockTheme.Dark;
            themeAuto.IsChecked = theme == DockTheme.System;
        };

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = Loc.T("Menu_Exit") };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        return menu;
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
            _acrylic.Hide();
        }
        else
        {
            Show();
            PositionDock();
            ApplyGlassEffect();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        RestoreAllMinimized(); // don't leave the user's windows stranded in (now-gone) dock tiles
        _appRefreshTimer.Stop();
        _startWatchTimer.Stop();
        _pinCheckTimer.Stop();
        _glassCaptureTimer?.Stop();
        SetCaptureExclusion(false); // don't leave the dock excluded from capture
        _pinWatcher?.Dispose();
        _minimizeHook.Dispose();
        _minimizeIntercept.Dispose();
        _thumbnails.Dispose();
        _foreground.Dispose();
        _acrylic.Dispose();
        _appBar?.Unregister(); // release reserved screen space
        Taskbar.Restore(); // restore the taskbar to its pre-launch state
        Loc.LanguageChanged -= OnLanguageChanged;
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }
}
