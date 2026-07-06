using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dockable.Accessibility;
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
// Aliased (not namespace-imported): System.Windows.Shapes.Path would collide with System.IO.Path.
using Ellipse = System.Windows.Shapes.Ellipse;
using ShapePath = System.Windows.Shapes.Path;

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
    private SettingsWindow? _settingsWindow; // "Dockable Preferences…" window (single instance)
    private HwndSource? _prefsSource;        // message hook on the Preferences window (intercepts minimize)

    private double _mouseX;
    private double _mouseY;
    private bool _hovering;
    private bool _renderingHooked;
    private TimeSpan _lastRenderTime; // last processed OnRendering frame, for the frame-rate cap
    // Last acrylic/capture rect pushed to native (device px + corner), to skip no-op SetWindowPos/SetRect
    // on the many render frames where the bar geometry hasn't actually changed.
    private int _lastSyncX, _lastSyncY, _lastSyncW, _lastSyncH;
    private float _lastSyncCorner;
    private bool _haveSynced;
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
    // Re-hides the taskbar whenever Explorer re-shows it, while the "Never" mode is active.
    private readonly TaskbarHideWatcher _taskbarHideWatcher = new();
    // Full-window captures taken while windows are visible (capture-at-minimize is too late).
    private readonly WindowThumbnailCache _thumbnails = new();
    // Live acrylic blur rendered in a separate window directly behind the bar.
    private readonly AcrylicBackdrop _acrylic = new();
    private const double BarCornerRadius = 24; // matches DockBackground's CornerRadius

    // Liquid Glass (real refraction): a background thread captures the backdrop behind the bar and hands
    // changed frames to the UI thread to feed the shader (see BackdropCapturer).
    private bool _glassRefractReady; // the refraction effect has been attached to GlassRefract
    private WriteableBitmap? _glassBitmap; // reused source for the refraction (written in place on change)
    private BackdropCapturer? _backdropCapturer; // threaded screen capture + frame diff
    private int _glassW, _glassH;          // current capture size (physical px)
    private int _glassCapX, _glassCapY;    // screen origin (physical px) of the fixed capture rect
    private RefractionEffect? _glassRefractInner; // pass 1: rim refraction + chromatic aberration
    private RefractionEffect? _glassSpecEffect; // the final pass, whose rim specular tracks the cursor
    private double _glassSpecAmount = GlassSpecRestAmount; // eased activation (rest ↔ 1.0 on hover)
    private double _glassLightUv;           // eased glint position (bar UV); rests at 0 (left end) when idle
    // Rim-specular tuning: light sits this far above the bar (UV), and the glint eases between a
    // visible resting sheen and a bright peak as the dock is hovered/magnified. The light's X overshoots
    // the bar ends by GlassLightOvershoot (UV), so a pointer at the far end pushes the light diagonally
    // past the corner and the glint wraps onto the dock's side edge instead of stopping at the top rim.
    private const double GlassLightHeight = -0.18;
    private const double GlassLightOvershoot = 0.5;
    private const double GlassSpecAmbient = 0.12;
    // With no pointer on the dock the glint stays visible at this activation (0 = ambient-only faint,
    // 1 = full hover peak) while resting at the bar's left end. A blind constant; tune on feedback.
    private const double GlassSpecRestAmount = 0.5;
    private double _glassSpecPeak = 0.5; // peak hover sheen; driven by Settings.GlassRimHighlight

    // Optional profiling for the glass capture loop, on when the env var DOCKABLE_GLASS_PROFILE is set
    // (writes %APPDATA%\Dockable\glass_profile.log). Null — and thus free — when disabled.
    private readonly GlassProfiler? _glassProfiler = GlassProfiler.Enabled ? new GlassProfiler() : null;

    // Hide the dock entirely while a full-screen app / borderless-fullscreen game owns the screen.
    private readonly ForegroundWatcher _foreground = new();
    private bool _fullscreenActive;
    // Windows whose minimize/restore we're currently animating (ignore re-entrant events).
    private readonly HashSet<IntPtr> _busy = new();
    // Reactive minimizes (taskbar/menu/Show-Desktop button) awaiting their warp. Drained one at a time so
    // a burst (e.g. the Show-Desktop corner button minimizing everything at once) cascades sequentially
    // instead of all grabbing the single shared warp overlay and stomping each other.
    private readonly Queue<IntPtr> _minimizeQueue = new();
    private bool _minimizeDraining;
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
    private readonly DispatcherTimer _autoHideTimer;    // auto-hide idle/edge watcher (runs only when enabled)

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
            CheckCaptureFriendlyExit(); // re-exclude the dock from capture once the Snipping Tool is gone
            CheckAndPromptNewPins(); // reliable poll for new taskbar pins (the folder watcher often doesn't fire on Win11)
        };

        _dragSteadyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragSteadyMs) };
        _dragSteadyTimer.Tick += OnDragSteadyElapsed;

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _autoHideTimer.Tick += OnAutoHideTick;
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
            PerformanceProfile.Configure(vm.Settings.PerformanceMode); // resolve effect-reduction policy first
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
        bool dark = SystemTheme.IsDarkEffective(ViewModel?.Settings.Theme ?? DockTheme.System);

        if (dark)
        {
            // .macos-dock-dark
            Resources["BarBackgroundBrush"] = BarTintBrush(0x66, 0x24, 0x24, 0x24);
            Resources["BarBorderBrush"] = UiBrushes.Frozen("#14FFFFFF");
            Resources["SeparatorBrush"] = UiBrushes.Frozen("#40FFFFFF");
            Resources["RunningDotBrush"] = UiBrushes.Frozen("#CCFFFFFF"); // rgba(255,255,255,0.8)
            Resources["FallbackBgBrush"] = UiBrushes.Frozen("#33FFFFFF");
            Resources["FallbackTextBrush"] = UiBrushes.Frozen("#FFFFFFFF");
            Resources["LabelBgBrush"] = UiBrushes.Frozen("#F22A2A30");
            Resources["LabelBorderBrush"] = UiBrushes.Frozen("#33FFFFFF");
            Resources["LabelTextBrush"] = UiBrushes.Frozen("#FFF2F2F2");
            Resources["IconShadowInnerOpacity"] = 0.18;
            BarShadow.Opacity = 0.4;
            DockBackground.BorderThickness = new Thickness(1.5);
        }
        else
        {
            // .macos-dock-light (background/border swapped from the original tints)
            Resources["BarBackgroundBrush"] = BarTintBrush(0x33, 0xFF, 0xFF, 0xFF);
            Resources["BarBorderBrush"] = UiBrushes.Frozen("#66FFFFFF");
            Resources["SeparatorBrush"] = UiBrushes.Frozen("#33000000");
            Resources["RunningDotBrush"] = UiBrushes.Frozen("#B3000000"); // rgba(0,0,0,0.7)
            Resources["FallbackBgBrush"] = UiBrushes.Frozen("#1F000000");
            Resources["FallbackTextBrush"] = UiBrushes.Frozen("#CC000000");
            Resources["LabelBgBrush"] = UiBrushes.Frozen("#F2F7F7FA");
            Resources["LabelBorderBrush"] = UiBrushes.Frozen("#22000000");
            Resources["LabelTextBrush"] = UiBrushes.Frozen(UiBrushes.InkHex);
            Resources["IconShadowInnerOpacity"] = 0.14;
            BarShadow.Opacity = 0.15;
            DockBackground.BorderThickness = new Thickness(1.5); // slightly thicker border on light glass
        }

        // Outer icon drop-shadow is a shared, swappable effect: dropped entirely in reduced/Performance
        // mode (the wide BlurRadius=20 pass re-rasterizes per icon per magnify frame — the biggest GPU cost).
        Resources["IconOuterShadowEffect"] = PerformanceProfile.ReduceIconShadows
            ? null
            : FrozenOuterIconShadow(dark ? 0.12 : 0.10);

        // Windows 11-style context menus (Themes/ModernMenu.xaml) live at APPLICATION scope so
        // every menu follows the theme — the dock's, the tray's, and the menu bar's alike.
        var app = Application.Current.Resources;
        if (dark)
        {
            app["PopupMenuBackgroundBrush"] = UiBrushes.Frozen("#F52C2C2C");
            app["PopupMenuBorderBrush"] = UiBrushes.Frozen("#14FFFFFF");
            app["PopupMenuForegroundBrush"] = UiBrushes.Frozen("#FFF2F2F2");
            app["PopupMenuDisabledBrush"] = UiBrushes.Frozen("#77FFFFFF");
            app["PopupMenuHoverBrush"] = UiBrushes.Frozen("#17FFFFFF");
            app["PopupMenuSeparatorBrush"] = UiBrushes.Frozen("#1FFFFFFF");
        }
        else
        {
            app["PopupMenuBackgroundBrush"] = UiBrushes.Frozen("#F5F9F9F9");
            app["PopupMenuBorderBrush"] = UiBrushes.Frozen("#1F000000");
            app["PopupMenuForegroundBrush"] = UiBrushes.Frozen("#E4000000");
            app["PopupMenuDisabledBrush"] = UiBrushes.Frozen("#72000000");
            app["PopupMenuHoverBrush"] = UiBrushes.Frozen("#0F000000");
            app["PopupMenuSeparatorBrush"] = UiBrushes.Frozen("#1A000000");
        }
    }

    /// <summary>Builds the (frozen, shared) outer icon drop-shadow at the given opacity.</summary>
    private static DropShadowEffect FrozenOuterIconShadow(double opacity)
    {
        var effect = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 10,
            Direction = 270,
            Opacity = opacity,
            Color = Colors.Black,
        };
        effect.Freeze();
        return effect;
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

    /// <summary>Builds the bar tint brush from its base ARGB, scaling the alpha by the user's
    /// Liquid Glass tint-opacity multiplier (1.0 = the base tint) and clamping to a valid byte.</summary>
    private SolidColorBrush BarTintBrush(byte baseAlpha, byte r, byte g, byte b)
    {
        double mult = ViewModel?.Settings.GlassTintOpacity ?? 1.0;
        byte a = (byte)Math.Clamp(Math.Round(baseAlpha * mult), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Binding Window.Width/Height directly is unreliable, so mirror the view-model here.
        if (e.PropertyName is nameof(DockViewModel.WindowWidth) or nameof(DockViewModel.WindowHeight))
            ApplyWindowSize();
    }

    private double _lastReservedCross; // cross-axis size at the last AppBar reservation

    private void ApplyWindowSize()
    {
        if (ViewModel is null)
            return;
        Width = ViewModel.WindowWidth;
        Height = ViewModel.WindowHeight;
        PositionDock();
        ApplyIdleRegion(); // re-clip to the new resting bar when the layout size changes (if idle)
        // Keep the reserved strip in step with the dock's size (e.g. the Preferences Size slider).
        // Deferred during a separator drag (to the drop, EndSeparatorResize) and skipped while only
        // the main-axis size is gliding (tile add/remove eases the width per frame; the reserved
        // strip spans the full edge, so it only depends on the cross size) — otherwise maximized
        // windows would reflow every frame.
        double cross = IsVerticalDock ? ViewModel.WindowWidth : ViewModel.WindowHeight;
        if (!_separatorResize && Math.Abs(cross - _lastReservedCross) > 0.5)
        {
            _lastReservedCross = cross;
            ReserveAppBarSpace();
        }
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
        _thumbnails.ShouldSuspend = () => _busy.Count > 0; // don't capture mid-warp (it stutters the frames)
        _thumbnails.Start();
        _minimizeHook.WindowMinimizing += OnWindowMinimizing;
        _minimizeHook.WindowUnminimized += OnWindowUnminimized;
        _minimizeHook.Start();

        // Pre-empt the minimize gesture so the warp's frame 0 is on screen before the OS minimizes.
        // Defer the real work off the hook callback so the low-level hook returns immediately.
        _minimizeIntercept.MinimizeRequested += hwnd => Dispatcher.BeginInvoke(() => InterceptedMinimize(hwnd));
        _minimizeIntercept.MinimizeAllRequested += () => Dispatcher.BeginInvoke(OnMinimizeAllRequested);
        _minimizeIntercept.ShowDesktopRequested += () => Dispatcher.BeginInvoke(OnShowDesktopRequested);
        // Win+Shift+S / PrintScreen: lift the glass capture exclusion BEFORE the snip overlay grabs the
        // screen, so the dock shows up in the user's capture. Send priority — this must win that race.
        _minimizeIntercept.ScreenSnipRequested +=
            () => Dispatcher.BeginInvoke(EnterCaptureFriendlyMode, DispatcherPriority.Send);
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
        ApplyAutoHide(); // start the idle/edge watcher (and drop the AppBar strip) if enabled
        StartTaskbarMirror();

        // Subscribe to shell-hook notifications (taskbar-flash → attention bounce).
        if (_hwnd != IntPtr.Zero)
        {
            _shellHookMsg = PInvoke.RegisterWindowMessage("SHELLHOOK");
            PInvoke.RegisterShellHookWindow((HWND)_hwnd);
        }

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
        // The initial layout snapped with just the Start tile; the population above widened the
        // window TARGET, which normally glides there over frames. Snap now, before first paint, so
        // the dock appears at its full (max-magnified) width instead of clipped at the sides — and
        // re-sync the backdrop so the glass capture rect spans that full width from the start.
        ViewModel?.SnapWindowSize();
        SyncAcrylic();
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
        RestoreQueueNext(toRestore, 0, app);
    }

    /// <summary>Restores the queued minimized windows one at a time, chaining each animation to the
    /// next. A tile aims at itself; an icon-stashed window aims at <paramref name="fallbackApp"/> (the
    /// clicked app icon) or, when null, at whatever app icon currently claims it — or (0,0) if none.</summary>
    private void RestoreQueueNext(List<(IntPtr Hwnd, DockItemViewModel? Tile, BitmapSource? Bitmap)> queue,
        int index, DockItemViewModel? fallbackApp)
    {
        if (index >= queue.Count)
            return;
        var (hwnd, tile, bitmap) = queue[index];
        Point target = tile is not null ? TileScreenCenter(tile)
            : fallbackApp is not null ? TileScreenCenter(fallbackApp)
            : ViewModel?.FindAppForWindow(hwnd) is { } app ? TileScreenCenter(app)
            : new Point(0, 0);
        RestoreWindowAnimated(hwnd, tile, target, bitmap, () => RestoreQueueNext(queue, index + 1, fallbackApp));
    }

    private void OnForegroundChanged()
    {
        // Snipping app took focus (launched from Start, or its editor/recording UI) — make the dock
        // capturable. Runs before the fullscreen check so its overlay guard is already armed.
        if (IsForegroundSnipApp())
            EnterCaptureFriendlyMode();
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

        // The Snipping Tool's region-select overlay covers the whole monitor — don't let it trigger
        // the fullscreen hide while the user is trying to capture the dock.
        if (_captureFriendly && IsForegroundSnipApp())
            return;

        bool fullscreen = (ViewModel?.Settings.HideOnFullscreen ?? true)
            && Fullscreen.IsForegroundFullscreenOnMonitorOf(_hwnd, _ownProcessId);
        SetFullscreenHidden(fullscreen);
    }

    /// <summary>Applies a HideOnFullscreen setting change now: turning it off restores a dock that's
    /// currently hidden for a full-screen app (bypassing the own-process foreground guard — the click
    /// came from our Preferences window). Turning it on takes effect on the next fullscreen check.</summary>
    internal void ApplyHideOnFullscreen()
    {
        if (ViewModel is { Settings.HideOnFullscreen: false })
            SetFullscreenHidden(false);
    }

    private void SetFullscreenHidden(bool fullscreen)
    {
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
        var mode = ViewModel.Settings.TaskbarVisibility;
        if (mode == TaskbarVisibility.Never)
        {
            // Hide, then keep it hidden: Explorer re-shows the tray on its own (attention flashes, Win+M,
            // Explorer/Terminal minimizing), so the watcher re-hides it the instant that happens.
            Taskbar.SetVisibility(mode);
            _taskbarHideWatcher.Start();
        }
        else
        {
            // Stop watching BEFORE showing the tray, so the SW_SHOW we're about to do isn't caught and
            // immediately re-hidden by an in-flight event.
            _taskbarHideWatcher.Stop();
            Taskbar.SetVisibility(mode);
        }
    }

    /// <summary>Anchors the dock flush to its monitor's docked edge, centered along the other axis.</summary>
    private void PositionDock()
    {
        // Pin the window's main-axis size to the full monitor edge (a fixed strip): resizing the
        // window as tiles grow in/out desynced the native resize+recenter from the layered bitmap
        // for a frame each step — the whole dock jittered. With the strip pinned, only monitor/DPI/
        // edge changes ever resize the window. (Re-entry note: a changed extent recomputes layout →
        // ApplyWindowSize → PositionDock again; the second pass sees no change and falls through.)
        if (_hwnd != IntPtr.Zero && ViewModel is not null)
        {
            var info = Monitors.ForWindow(_hwnd);
            double extent = (IsVerticalDock ? info.MonitorPx.Height : info.MonitorPx.Width) / info.Scale;
            ViewModel.SetFixedMainExtent(extent);
        }

        var (left, top) = ComputePlacement();

        // Auto-hide: slide the window off-screen along its edge (HideProgress 0 shown → 1 hidden).
        double hidden = HideProgress;
        if (hidden > 0 && ViewModel is not null)
        {
            switch (ViewModel.Settings.Edge)
            {
                case DockEdge.Top: top -= hidden * ViewModel.WindowHeight; break;
                case DockEdge.Left: left -= hidden * ViewModel.WindowWidth; break;
                case DockEdge.Right: left += hidden * ViewModel.WindowWidth; break;
                default: top += hidden * ViewModel.WindowHeight; break; // Bottom
            }
        }

        Left = left;
        Top = top;
        SyncAcrylic();
    }

    // --- Auto-hide ("Automatically hide and show the Dock") --------------------------------

    /// <summary>0 = fully shown, 1 = fully slid off-screen; animated by SlideDock, applied by
    /// PositionDock (registered as a DP so a WPF DoubleAnimation can drive the slide).</summary>
    private static readonly DependencyProperty HideProgressProperty = DependencyProperty.Register(
        nameof(HideProgress), typeof(double), typeof(DockWindow),
        new PropertyMetadata(0.0, static (d, _) => ((DockWindow)d).PositionDock()));

    private double HideProgress
    {
        get => (double)GetValue(HideProgressProperty);
        set => SetValue(HideProgressProperty, value);
    }

    private bool _dockHidden;              // target state: true = slid off-screen
    private DateTime _lastDockActivity;    // last time the dock was hovered/interacted with
    private const int AutoHideIdleMs = 600;   // cursor away this long → slide out
    private const int AutoHideRevealPx = 2;   // physical-px sliver at the screen edge that reveals

    /// <summary>Applies the auto-hide setting: on = release the AppBar reservation (it stays off
    /// even while the dock is revealed) and start the idle/edge watcher; off = slide back in and
    /// restore the reservation.</summary>
    internal void ApplyAutoHide()
    {
        if (ViewModel is null)
            return;
        if (ViewModel.Settings.AutoHideDock)
        {
            _appBar?.Unregister(); // maximized windows reclaim the strip
            _lastDockActivity = DateTime.UtcNow;
            _autoHideTimer.Start();
        }
        else
        {
            _autoHideTimer.Stop();
            if (_dockHidden)
                SlideDock(hide: false);
            ApplyBehavior(); // re-register the AppBar + reposition
        }
    }

    /// <summary>Animates the dock off-screen along its edge, or back in.</summary>
    private void SlideDock(bool hide)
    {
        if (_dockHidden == hide)
            return;
        _dockHidden = hide;
        if (!hide)
            _lastDockActivity = DateTime.UtcNow; // a fresh idle grace once revealed
        var slide = new DoubleAnimation(hide ? 1.0 : 0.0, TimeSpan.FromMilliseconds(hide ? 260 : 200))
        {
            EasingFunction = new QuadraticEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
        BeginAnimation(HideProgressProperty, slide);
    }

    // Watches the cursor: hidden → reveal when it presses the screen edge's 2px sliver; shown →
    // slide out once it has been away from the dock (and nothing is mid-interaction) for the grace.
    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        if (ViewModel is null || !ViewModel.Settings.AutoHideDock || _hwnd == IntPtr.Zero
            || !PInvoke.GetCursorPos(out var cursor))
            return;

        if (_dockHidden)
        {
            var monitor = Monitors.ForWindow(_hwnd).MonitorPx;
            bool onEdge = ViewModel.Settings.Edge switch
            {
                DockEdge.Top => cursor.Y <= monitor.Top + AutoHideRevealPx
                    && cursor.X >= monitor.Left && cursor.X <= monitor.Right,
                DockEdge.Left => cursor.X <= monitor.Left + AutoHideRevealPx
                    && cursor.Y >= monitor.Top && cursor.Y <= monitor.Bottom,
                DockEdge.Right => cursor.X >= monitor.Right - AutoHideRevealPx
                    && cursor.Y >= monitor.Top && cursor.Y <= monitor.Bottom,
                _ => cursor.Y >= monitor.Bottom - AutoHideRevealPx
                    && cursor.X >= monitor.Left && cursor.X <= monitor.Right, // Bottom
            };
            if (onEdge)
                SlideDock(hide: false);
            return;
        }

        bool overDock;
        try
        {
            var p = PointFromScreen(new Point(cursor.X, cursor.Y));
            overDock = p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        }
        catch
        {
            overDock = true; // can't map (window mid-teardown) — err on staying visible
        }

        // Anything mid-interaction counts as activity: drags/resizes, an open flyout, any menu
        // (menus hold the thread's mouse capture), or an in-flight minimize warp.
        bool active = overDock || _dragInitiated || _separatorResize || FanPopup.IsOpen
            || Mouse.Captured is not null || _busy.Count > 0;
        if (active)
        {
            _lastDockActivity = DateTime.UtcNow;
            return;
        }
        if ((DateTime.UtcNow - _lastDockActivity).TotalMilliseconds >= AutoHideIdleMs)
            SlideDock(hide: true);
    }

    /// <summary>Shows/hides and configures the acrylic backdrop for the selected Glass Effect: Simple
    /// hides it (the bar keeps its plain translucent brush); Acrylic/Liquid Glass show it with the
    /// matching backdrop brush.</summary>
    private void ApplyGlassEffect()
    {
        // Reduced mode keeps LiquidGlass (throttled to a low capture rate — PerformanceProfile
        // .GlassCaptureFps) rather than downgrading it to Acrylic; only shader unavailability falls back.
        var mode = ViewModel?.Settings.GlassEffect ?? GlassEffect.Acrylic;
        bool liquid = mode == GlassEffect.LiquidGlass;
        bool refract = liquid && RefractionEffect.IsAvailable; // real pixel-shader refraction

        // Refraction layer: a background thread captures the backdrop behind the bar; changed frames
        // feed the shader on the UI thread.
        if (refract)
        {
            EnsureGlassRefraction();
            SetCaptureExclusion(true); // omit the dock from capture so it doesn't refract itself
            GlassRefractOuter.Visibility = Visibility.Visible;
            StartGlassCapture();
        }
        else
        {
            _backdropCapturer?.Stop();
            GlassRefractOuter.Visibility = Visibility.Collapsed;
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
        _haveSynced = false; // force a reposition of the freshly-shown backdrop even if the rect matches
        SyncAcrylic();
    }

    /// <summary>Creates (once) and starts the background backdrop capturer, publishing the current bar
    /// rect so it has something to grab even before the first render frame.</summary>
    private void StartGlassCapture()
    {
        if (_backdropCapturer is null)
        {
            // Capture at the (hard-capped) glass rate while content moves behind the dock; the capturer
            // itself backs off to a lower idle rate once the backdrop is static. The GDI screen read-back
            // is a fixed ~4 ms regardless of size, so it runs on its own thread and never blocks the
            // magnification render loop.
            int refresh = GetRefreshRateHz();
            int fastFps = PerformanceProfile.GlassCaptureFps;
            int idleFps = Math.Min(2, fastFps); // never idle FASTER than the active rate
            _glassProfiler?.Begin(fastFps, refresh, PerformanceProfile.Tier); // tier 0/1/2 = none/partial/full GPU
            _backdropCapturer = new BackdropCapturer(Dispatcher, UploadGlassFrame, fastFps, idleFps, _glassProfiler);
        }
        PublishGlassRect();
        _backdropCapturer.Start();
        if (_captureFriendly)
            _backdropCapturer.EnterCaptureFriendly(); // a rebuilt capturer must not CAPTUREBLT the visible dock
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

    /// <summary>Attaches the two-pass separable-Gaussian refraction shader to the glass layer (once):
    /// the inner Border refracts the backdrop at the rim and blurs it horizontally, the outer Border
    /// blurs that result vertically — together a true 2-D Gaussian frost.</summary>
    private void EnsureGlassRefraction()
    {
        if (_glassRefractReady || !RefractionEffect.IsAvailable)
            return;

        var s = ViewModel?.Settings;
        double sigma = s?.GlassBlurRadius ?? 1.4; // Gaussian blur sigma in device px (kernel reaches ~2.5x this)
        _glassSpecPeak = s?.GlassRimHighlight ?? 0.5;

        // Pass 1 (inner): rim refraction + chromatic aberration + horizontal blur (saturation applied once, below).
        // The rounded-rect rim is computed analytically in the shader (aspect-correct), so corner/bezel
        // are passed as fractions of the bar height — kept in sync with the live bar by UpdateGlassShape.
        _glassRefractInner = new RefractionEffect
        {
            DistortionAmount = s?.GlassDistortion ?? 34.0, // px the sample is pulled inward at the rim
            Aberration = s?.GlassAberration ?? 0.5,        // subtle rim colour fringing (0 = off)
            BlurRadius = sigma,
            BlurDirection = new Vector(1, 0),
        };
        GlassRefract.Effect = _glassRefractInner;
        // Pass 2 (outer): vertical blur only (no further displacement) + the vibrance boost + the rim
        // specular (LightPosition / SpecularIntensity are driven per-frame from the cursor — see
        // UpdateGlassSpecular). Shininess sets the glint tightness.
        _glassSpecEffect = new RefractionEffect
        {
            DistortionAmount = 0.0,
            BlurRadius = sigma,
            BlurDirection = new Vector(0, 1),
            Saturation = s?.GlassSaturation ?? 1.8, // 180% — richer colour behind the glass
            Shininess = 3.0, // broad lobe so the glint carries around the rim into the corners
            RimSharpness = 4.0, // thin, border-like glint hugging the very edge
            SpecularIntensity = GlassSpecAmbient
                + (_glassSpecPeak - GlassSpecAmbient) * GlassSpecRestAmount, // start at the resting sheen
            // Resting spot with no pointer on the dock: the bar's left-most end.
            LightPosition = new Point(-GlassLightOvershoot, GlassLightHeight),
        };
        GlassRefractOuter.Effect = _glassSpecEffect;
        _glassRefractReady = true;
        UpdateGlassShape();
    }

    /// <summary>Pushes the current Liquid Glass tuning settings into the live shader passes and the bar
    /// tint. Called when the user changes a Liquid Glass slider in Preferences; a safe no-op until the
    /// refraction shader has been attached (EnsureGlassRefraction).</summary>
    public void ApplyLiquidGlassSettings()
    {
        var s = ViewModel?.Settings;
        if (s is not null && _glassRefractReady)
        {
            _glassSpecPeak = s.GlassRimHighlight;
            if (_glassRefractInner is not null)
            {
                _glassRefractInner.DistortionAmount = s.GlassDistortion;
                _glassRefractInner.Aberration = s.GlassAberration;
                _glassRefractInner.BlurRadius = s.GlassBlurRadius;
            }
            if (_glassSpecEffect is not null)
            {
                _glassSpecEffect.BlurRadius = s.GlassBlurRadius;
                _glassSpecEffect.Saturation = s.GlassSaturation;
            }
        }
        // Tint opacity affects the bar background brush regardless of shader availability.
        ApplyTheme();
    }

    /// <summary>Keeps the shader's rounded-rect corner/bezel matched to the live bar. They're expressed
    /// as fractions of the bar height so the corners stay circular (and DPI-independent) at any width;
    /// only changes when the bar's thickness changes (icon size), so the DP-equality guard makes the
    /// per-frame call from the render loop free.</summary>
    private void UpdateGlassShape()
    {
        // Both effects exist only while Liquid Glass refraction is active — skip the math (this is
        // called per rendered frame) and don't allocate a temp array for the two fields.
        if ((_glassRefractInner is null && _glassSpecEffect is null)
            || ViewModel is null || ViewModel.BarHeight <= 0)
            return;
        double cornerFrac = BarCornerRadius / ViewModel.BarHeight;
        const double bezelFrac = 0.5; // rim band ≈ half the bar height
        if (_glassRefractInner is not null)
        {
            _glassRefractInner.CornerFraction = cornerFrac;
            _glassRefractInner.BezelFraction = bezelFrac;
        }
        if (_glassSpecEffect is not null)
        {
            _glassSpecEffect.CornerFraction = cornerFrac;
            _glassSpecEffect.BezelFraction = bezelFrac;
        }
    }

    /// <summary>
    /// Publishes the capture rect (physical px) to the background capturer: the ENTIRE monitor edge
    /// along the bar's main axis, at the bar's constant (unmagnified) cross band. The rect depends
    /// only on the monitor and the bar's cross placement — NOT on the window's main-axis size, which
    /// glides whenever tiles appear/shrink and used to retarget the capture every frame of that
    /// glide (the capturer recreated its DIB and reset its frame diff mid-animation — visible
    /// backdrop jitter). Now it only changes when the dock changes monitor/edge or its cross band
    /// (icon size); the live bar's slice is cropped out via the brush viewbox (UpdateGlassClip).
    /// Runs on the UI thread (PointToScreen / DPI are UI-only).
    /// </summary>
    private void PublishGlassRect()
    {
        if (_backdropCapturer is null || ViewModel is null || _hwnd == IntPtr.Zero
            || ViewModel.BarWidth <= 0 || ViewModel.BarHeight <= 0)
            return;
        try
        {
            var dpi = Dpi;
            var monitor = Monitors.ForWindow(_hwnd).MonitorPx;
            if (ViewModel.Settings.Edge is DockEdge.Left or DockEdge.Right)
            {
                // Full monitor height at the bar's horizontal band.
                double barLeftPx = PointToScreen(new Point(ViewModel.BarLeft, 0)).X;
                int w = (int)Math.Round(ViewModel.BarWidth * dpi.DpiScaleX);
                if (w <= 0)
                    return;
                _glassCapX = (int)Math.Round(barLeftPx);
                _glassCapY = (int)Math.Round(monitor.Y);
                _backdropCapturer.SetRect(_glassCapX, _glassCapY, w, (int)Math.Round(monitor.Height));
            }
            else
            {
                // Full monitor width at the bar's vertical band.
                double barTopPx = PointToScreen(new Point(0, ViewModel.BarTop)).Y;
                int h = (int)Math.Round(ViewModel.BarHeight * dpi.DpiScaleY);
                if (h <= 0)
                    return;
                _glassCapX = (int)Math.Round(monitor.X);
                _glassCapY = (int)Math.Round(barTopPx);
                _backdropCapturer.SetRect(_glassCapX, _glassCapY, (int)Math.Round(monitor.Width), h);
            }
        }
        catch
        {
            // PointToScreen before the window is sourced — try again next frame.
        }
    }

    /// <summary>Uploads a changed backdrop frame into the shader's source bitmap. Invoked on the UI thread
    /// by the background capturer (only when the pixels actually changed), so the shader re-runs no more
    /// often than the backdrop truly moves. The bitmap is (re)sized to the captured frame.</summary>
    private void UploadGlassFrame(byte[] buffer, int w, int h)
    {
        if (w <= 0 || h <= 0 || buffer.Length < w * 4 * h)
            return;
        if (_glassBitmap is null || w != _glassW || h != _glassH)
        {
            _glassW = w;
            _glassH = h;
            _glassBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            GlassBackdropBrush.ImageSource = _glassBitmap;
        }
        _glassBitmap.WritePixels(new Int32Rect(0, 0, w, h), buffer, w * 4, 0);
        UpdateGlassClip();
    }

    /// <summary>
    /// Drives the rim specular each render frame: anchors the (virtual) light above the cursor's X over
    /// the bar so the bright glint sweeps along the rim as the pointer moves, and eases its intensity
    /// between a faint resting sheen and a bright peak with hover (which also tracks magnification, since
    /// the icons grow on hover). Only runs while Liquid Glass refraction is on screen.
    /// </summary>
    private void UpdateGlassSpecular()
    {
        if (_glassSpecEffect is null || ViewModel is null
            || GlassRefractOuter.Visibility != Visibility.Visible || ViewModel.BarWidth <= 0)
            return;

        // Follow the cursor directly while hovering; with no pointer on the dock, glide the glint
        // back to its resting spot at the bar's left-most end.
        if (_hovering)
        {
            _glassLightUv = Math.Clamp((_mouseX - ViewModel.BarLeft) / ViewModel.BarWidth, 0.0, 1.0);
        }
        else
        {
            _glassLightUv += (0.0 - _glassLightUv) * 0.2; // same damped ease as the intensity ramp
            if (_glassLightUv < 0.005)
                _glassLightUv = 0.0; // snap once home so the settle test below can unhook the loop
        }
        // Map [0,1] → [-overshoot, 1+overshoot] (centre fixed) so the ends carry the light past the corner.
        double lightX = _glassLightUv * (1.0 + 2.0 * GlassLightOvershoot) - GlassLightOvershoot;
        _glassSpecEffect.LightPosition = new Point(lightX, GlassLightHeight);

        // The sheen dims from the hover peak to a still-visible resting level, never fully out.
        double target = _hovering ? 1.0 : GlassSpecRestAmount;
        _glassSpecAmount += (target - _glassSpecAmount) * 0.2; // critically-ish damped ease
        if (!_hovering && Math.Abs(_glassSpecAmount - GlassSpecRestAmount) < 0.005)
            _glassSpecAmount = GlassSpecRestAmount;
        _glassSpecEffect.SpecularIntensity =
            GlassSpecAmbient + (_glassSpecPeak - GlassSpecAmbient) * _glassSpecAmount;
    }

    /// <summary>Whether the rim glint has finished gliding home (position at the left end, sheen down
    /// to the resting level) — the render loop must stay hooked until then, or it freezes mid-return.</summary>
    private bool GlassSpecularSettled()
        => _glassSpecEffect is null || GlassRefractOuter.Visibility != Visibility.Visible
           || (_glassSpecAmount == GlassSpecRestAmount && _glassLightUv == 0.0);

    /// <summary>Keeps the refraction clip geometry matched to the (magnifying) bar's rounded rect, and
    /// crops the backdrop brush's viewbox to the bar's slice of the fixed capture strip (the capture
    /// spans the whole monitor edge along the main axis — see PublishGlassRect). The bitmap is 96 DPI,
    /// so absolute viewbox units equal capture pixels.</summary>
    /// <param name="barTopLeftPx">The bar's screen top-left (device px) when the caller already
    /// projected it this frame (SyncAcrylic); null → compute it here.</param>
    private void UpdateGlassClip(Point? barTopLeftPx = null)
    {
        if (ViewModel is null || ViewModel.BarWidth <= 0 || ViewModel.BarHeight <= 0)
            return;
        GlassClipGeometry.Rect = new Rect(0, 0, ViewModel.BarWidth, ViewModel.BarHeight);

        try
        {
            var dpi = Dpi;
            var barTopLeft = barTopLeftPx ?? PointToScreen(new Point(ViewModel.BarLeft, ViewModel.BarTop)); // device px
            double barW = ViewModel.BarWidth * dpi.DpiScaleX;
            double barH = ViewModel.BarHeight * dpi.DpiScaleY;
            // The bar slice is what the user sees: the capturer's black-glitch test judges it alone.
            _backdropCapturer?.SetInterestRect((int)Math.Round(barTopLeft.X), (int)Math.Round(barTopLeft.Y),
                (int)Math.Round(barW), (int)Math.Round(barH));
            if (_glassBitmap is not null)
                GlassBackdropBrush.Viewbox = new Rect(
                    barTopLeft.X - _glassCapX, barTopLeft.Y - _glassCapY, barW, barH);
        }
        catch
        {
            // PointToScreen before the window is sourced — the next sync fixes it.
        }
    }

    /// <summary>Excludes (or restores) the dock from screen capture via display affinity. While
    /// capture-friendly mode is on, exclusion requests are downgraded to none (re-applied on exit).</summary>
    private void SetCaptureExclusion(bool exclude)
    {
        if (_hwnd == IntPtr.Zero)
            return;
        PInvoke.SetWindowDisplayAffinity((HWND)_hwnd,
            exclude && !_captureFriendly
                ? WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE : WINDOW_DISPLAY_AFFINITY.WDA_NONE);
    }

    // --- Capture-friendly mode (Snipping Tool) ---------------------------------------------------
    // The Liquid Glass exclusion (above) hides the dock from EVERY capture API — including the user's
    // Snipping Tool. While the user is capturing, the exclusion is lifted so the dock shows up in
    // their snips/recordings, and the backdrop capturer either keeps running live (if a plain SRCCOPY
    // blit omits layered windows on this build — it probes) or freezes on its last frame. Entered by
    // Win+Shift+S / PrintScreen (keyboard hook) or a snipping app coming foreground; exits once no
    // snipping-app window has been visible for a beat (1 s tick).

    private static readonly string[] SnipProcessNames = { "SnippingTool", "ScreenClippingHost", "ScreenSketch" };
    private bool _captureFriendly;
    private DateTime _captureFriendlyHoldUntil; // minimum stay — the overlay takes a moment to appear

    private void EnterCaptureFriendlyMode()
    {
        _captureFriendlyHoldUntil = DateTime.UtcNow.AddSeconds(3);
        if (_captureFriendly)
            return;
        _captureFriendly = true;
        SetCaptureExclusion(false);              // visible to the Snipping Tool
        _backdropCapturer?.EnterCaptureFriendly(); // stop the glass from refracting the now-visible dock
    }

    /// <summary>Polled from the 1 s tick: leaves capture-friendly mode once the hold expired and no
    /// snipping-app window is visible (the recording toolbar keeps one visible for a whole recording).</summary>
    private void CheckCaptureFriendlyExit()
    {
        if (!_captureFriendly || DateTime.UtcNow < _captureFriendlyHoldUntil || AnySnipWindowVisible())
            return;
        _captureFriendly = false;
        _backdropCapturer?.ExitCaptureFriendly();
        if (ViewModel?.Settings.GlassEffect == GlassEffect.LiquidGlass && RefractionEffect.IsAvailable)
            SetCaptureExclusion(true); // the black-flash DWM causes on re-exclusion is skipped by the capturer
    }

    private static bool IsForegroundSnipApp()
    {
        var fg = PInvoke.GetForegroundWindow();
        return !fg.IsNull && IsSnipExe(TaskbarApps.GetWindowExePath((IntPtr)fg));
    }

    private static bool IsSnipExe(string exePath)
    {
        if (string.IsNullOrEmpty(exePath))
            return false;
        string stem = Path.GetFileNameWithoutExtension(exePath);
        foreach (string name in SnipProcessNames)
        {
            if (stem.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Any visible, non-cloaked top-level window owned by a snipping app? (The packaged
    /// Snipping Tool lingers suspended — with cloaked windows — after its UI closes, so a bare
    /// process-exists test would never let the mode end.) Only runs while the mode is on.</summary>
    private static unsafe bool AnySnipWindowVisible()
    {
        var pids = new HashSet<uint>();
        foreach (string name in SnipProcessNames)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (p)
                    pids.Add((uint)p.Id);
            }
        }
        if (pids.Count == 0)
            return false;

        bool found = false;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            uint pid = 0;
            PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            if (!pids.Contains(pid) || !PInvoke.IsWindowVisible(hwnd))
                return true;
            uint cloaked = 0;
            PInvoke.DwmGetWindowAttribute(hwnd,
                Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
            if (cloaked != 0)
                return true;
            found = true;
            return false; // stop enumerating
        }, default);
        return found;
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

    /// <summary>Applies a new Performance mode live: re-resolves the effect-reduction policy and re-applies
    /// everything it drives (icon shadows, effective glass path + capture rate, genie mesh resolution).</summary>
    private void SetPerformanceMode(PerformanceMode mode)
    {
        if (ViewModel is null || ViewModel.Settings.PerformanceMode == mode)
            return;
        ViewModel.Settings.PerformanceMode = mode;
        ViewModel.Save();

        PerformanceProfile.Configure(mode);
        ApplyTheme();          // rebuild (or drop) the outer icon drop-shadow
        _genie.RefreshQuality(); // rebuild the mesh at the new resolution on the next warp

        // Recreate the backdrop capturer so a changed capture rate takes effect (its fast FPS is fixed
        // at construction), then re-apply the glass path.
        _backdropCapturer?.Stop();
        _backdropCapturer = null;
        ApplyGlassEffect();
    }

    // The window's DPI, cached because SyncAcrylic/PublishGlassRect/UpdateGlassClip each read it per
    // rendered frame; VisualTreeHelper.GetDpi walks up the visual tree every call. Refreshed by
    // OnDpiChanged (per-monitor-v2 aware) on monitor moves / scale changes.
    private DpiScale _dpi;

    private DpiScale Dpi
    {
        get
        {
            if (_dpi.DpiScaleX == 0)
                _dpi = VisualTreeHelper.GetDpi(this);
            return _dpi;
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi = newDpi;
    }

    /// <summary>Tracks the acrylic backdrop window to the bar's current screen rect (physical px) and
    /// keeps it z-ordered just below the dock. Called whenever the bar moves or resizes.</summary>
    private void SyncAcrylic()
    {
        if (ViewModel is null || _hwnd == IntPtr.Zero || ViewModel.BarWidth <= 0 || ViewModel.BarHeight <= 0)
            return;
        var dpi = Dpi;
        var topLeft = PointToScreen(new Point(ViewModel.BarLeft, ViewModel.BarTop)); // device pixels
        int x = (int)Math.Round(topLeft.X);
        int y = (int)Math.Round(topLeft.Y);
        int w = (int)Math.Round(ViewModel.BarWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(ViewModel.BarHeight * dpi.DpiScaleY);
        float corner = (float)(BarCornerRadius * dpi.DpiScaleX);

        // Skip the native reposition when nothing moved: SyncAcrylic runs every render frame, but the bar
        // geometry only changes while magnifying/resizing. Avoids a SetWindowPos + clip rebuild per idle frame.
        if (_haveSynced && x == _lastSyncX && y == _lastSyncY && w == _lastSyncW && h == _lastSyncH
            && corner == _lastSyncCorner)
            return;
        _lastSyncX = x; _lastSyncY = y; _lastSyncW = w; _lastSyncH = h; _lastSyncCorner = corner;
        _haveSynced = true;

        _acrylic.SetBounds(x, y, w, h, corner, _hwnd);
        // Re-publish the fixed capture rect (only actually changes when the dock moves or is resized —
        // the capturer keeps grabbing the same max-extent region across a magnification gesture), THEN
        // the clip + viewbox crop, which anchor to that rect's origin. The bar's screen top-left was
        // just computed above — hand it down so the clip doesn't re-project the same point.
        PublishGlassRect();
        UpdateGlassClip(topLeft);
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
        // Un-clip on move, not just MouseEnter: while a folder flyout held the mouse capture, Enter
        // couldn't re-fire after the loop settled + clipped, so magnified icons rendered cut off at
        // the bar top. Flag-guarded — a no-op when already unclipped.
        ClearWindowRegion();
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

    private static bool IsRemovable(DockItemViewModel? item)
        => item is { IsTaskbarApp: true, IsPinned: true } or { IsPinnedPath: true };

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
                // Drop on the Recycle Bin, or hold-to-Remove. For a path tile this removes the PIN
                // only — the file/folder itself is untouched.
                if (item.IsPinnedPath)
                    ViewModel.UnpinPath(item);
                else
                    ViewModel.UnpinApp(item.LaunchPath);
                Sounds.Play(Sounds.Remove);
                removed = true;
            }
            else if (item.IsPinnedPath && overDock)
                ViewModel.MovePinnedPath(item, ViewModel.DragInsertIndex);      // reorder the right section
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

    /// <summary>A window flashed its taskbar button: bounce its dock icon 3× (macOS's attention
    /// bounce). Gated by the same "Animate opening applications" setting as the launch bounce;
    /// repeat flash messages don't restart a bounce that's still playing.</summary>
    private void OnAppAttentionFlash(IntPtr flashingHwnd)
    {
        if (ViewModel is null || !ViewModel.Settings.AnimateOpeningApps)
            return;
        var app = ViewModel.FindAppForWindow(flashingHwnd);
        if (app is null || app.IsBouncing)
            return;
        app.StartBounce(hops: 3);
        ClearWindowRegion(); // the hop renders above the resting bar
        HookRendering();
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

        // Auto-hide never reserves screen space — not even while the dock is revealed.
        if (ViewModel.Settings.AutoHideDock)
        {
            _appBar.Unregister();
            return;
        }

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

    /// <summary>Shell-hook code: a window flashed its taskbar button (FlashWindowEx — the "needs
    /// attention" colored-taskbar state). HSHELL_REDRAW (6) with the HSHELL_HIGHBIT flag.</summary>
    private const int HSHELL_FLASH = 0x8006;

    private uint _shellHookMsg; // RegisterWindowMessage("SHELLHOOK"); 0 until registered

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The OS light/dark setting changed — re-theme if we're following the system.
        if (msg == WM_SETTINGCHANGE
            && ViewModel?.Settings.Theme == DockTheme.System
            && SystemTheme.IsImmersiveColorChange(lParam))
        {
            ApplyTheme();
        }

        // An app is requesting attention (flashing its taskbar button): bounce its dock icon.
        if (_shellHookMsg != 0 && (uint)msg == _shellHookMsg && wParam.ToInt64() == HSHELL_FLASH)
            OnAppAttentionFlash(lParam);

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

    /// <summary>A real (external) window started minimizing (taskbar/menu/Show-Desktop button). Queue it
    /// and drain sequentially: a batch minimize (e.g. the Show-Desktop corner button) fires one of these
    /// per window almost simultaneously, and letting them all warp at once stomps the single shared
    /// overlay (leaving windows stuck in <see cref="_busy"/> — unresponsive tiles, frozen magnification).
    /// Each window's warp now completes before the next begins.</summary>
    private void OnWindowMinimizing(IntPtr hwnd)
    {
        _minimizeQueue.Enqueue(hwnd);
        if (!_minimizeDraining)
            DrainMinimizeQueue();
    }

    /// <summary>Warps the next queued reactive minimize into the dock, resuming (via its completion
    /// callback) with the one after it — so the batch cascades one at a time.</summary>
    private void DrainMinimizeQueue()
    {
        if (ViewModel is null)
        {
            _minimizeQueue.Clear();
            _minimizeDraining = false;
            return;
        }

        while (_minimizeQueue.Count > 0)
        {
            var hwnd = _minimizeQueue.Dequeue();
            // Skip anything we're already handling (our own intercepted/cascade minimizes fire this event
            // too) or have already represented.
            if (_busy.Contains(hwnd) || IsWindowRepresented(hwnd))
                continue;
            // Use the capture taken while the window was still visible — capturing now (it's already
            // minimized) would grab a black sliver.
            var capture = _thumbnails.TryGet(hwnd) ?? WindowCapture.Capture(hwnd);
            if (capture is null)
                continue; // nothing to warp
            _minimizeDraining = true;
            MinimizeToDock(hwnd, capture, onDone: DrainMinimizeQueue);
            return; // resume once this window's warp finishes (MinimizeToDock always invokes onDone)
        }
        _minimizeDraining = false;
    }

    /// <summary>Whether the window already has a dock representation (a minimized tile or an
    /// icon-stashed capture). Callers keep their own <c>_busy</c> and ViewModel-null checks.</summary>
    private bool IsWindowRepresented(IntPtr hwnd)
        => ViewModel!.FindMinimizedWindow(hwnd) is not null || _iconMinimized.ContainsKey(hwnd);

    /// <summary>Drops all dock minimize tracking for a window: its tile (if any), its icon-stashed
    /// capture, and its captured source rect.</summary>
    private void DropMinimizedTracking(IntPtr hwnd, DockItemViewModel? tile)
    {
        if (tile is not null)
            ViewModel!.RemoveMinimizedWindow(tile);
        _iconMinimized.Remove(hwnd);
        _minimizedSourcePx.Remove(hwnd);
    }

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

        DropMinimizedTracking(hwnd, tile);
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
        if (ViewModel is null || _busy.Contains(hwnd) || IsWindowRepresented(hwnd))
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
    private void OnMinimizeAllRequested() => MinimizeAllSequential(raiseEach: false);

    /// <summary>Win+D (show desktop): a toggle. If any app window is still open, minimize them all with
    /// the sequential focus-then-warp cascade; if everything is already minimized, reverse it — restore
    /// every dock-minimized window, one at a time. (We fully own Win+D since we swallowed the keystroke.)</summary>
    private void OnShowDesktopRequested()
    {
        if (ViewModel is null)
            return;
        uint own = (uint)Environment.ProcessId;
        bool anyOpen = TaskbarApps.EnumerateAppWindows(own).Any(w => !WindowControl.IsIconic(w.Hwnd));
        if (anyOpen)
            MinimizeAllSequential(raiseEach: true); // focus each window before its effect (macOS-style cascade)
        else
            RestoreAllAnimated();
    }

    /// <summary>Minimizes every open app window one at a time (Z-order, top first). <paramref name="raiseEach"/>
    /// brings each window to the foreground before its warp (Win+D's "focus each, then effect" cascade);
    /// Win+M passes false — it walks top-down so each is already the topmost window and avoids the focus
    /// changes that would flash the taskbar.</summary>
    private void MinimizeAllSequential(bool raiseEach)
    {
        if (ViewModel is null)
            return;
        uint own = (uint)Environment.ProcessId;
        var windows = TaskbarApps.EnumerateAppWindows(own) // Z-order, top first
            .Select(w => w.Hwnd)
            .Where(h => !WindowControl.IsIconic(h))
            .ToList();
        MinimizeListSequential(windows, 0, raiseEach);
    }

    private void MinimizeListSequential(List<IntPtr> windows, int index, bool raiseEach)
    {
        if (ViewModel is null || index >= windows.Count)
            return;
        var hwnd = windows[index];
        void Next() => MinimizeListSequential(windows, index + 1, raiseEach);
        if (!WindowControl.IsWindow(hwnd) || WindowControl.IsIconic(hwnd) || _busy.Contains(hwnd)
            || IsWindowRepresented(hwnd))
        {
            Next();
            return;
        }
        MinimizeOneAnimated(hwnd, Next, raiseIfNeeded: raiseEach, focusNext: false);
    }

    /// <summary>Restores every dock-minimized window (tiles + into-icon), one at a time with the reverse
    /// warp — the mirror of the minimize-all cascade, used by Win+D when everything is already minimized.</summary>
    private void RestoreAllAnimated()
    {
        if (ViewModel is null)
            return;
        var queue = new List<(IntPtr Hwnd, DockItemViewModel? Tile, BitmapSource? Bitmap)>();
        foreach (var tile in ViewModel.MinimizedWindows.ToArray())
            queue.Add((tile.Hwnd, tile, tile.Icon as BitmapSource));
        foreach (var kv in _iconMinimized.ToArray())
            queue.Add((kv.Key, null, kv.Value));
        RestoreQueueNext(queue, 0, null);
    }

    /// <summary>Warps a just-minimized window's <paramref name="capture"/> into its app icon (when
    /// "minimize into icon" is on and the app has a dock icon) or into a new thumbnail tile. Shared by
    /// external windows (via the minimize hook) and the dock's own Preferences window. When
    /// <paramref name="windowStillVisible"/> is true the window hasn't been minimized yet (the gesture
    /// was intercepted): frame 0 is painted first, then the window is minimized behind it.</summary>
    private void MinimizeToDock(IntPtr hwnd, WindowCapture.Result? capture, bool windowStillVisible = false,
        Action? onDone = null, bool focusNext = true)
    {
        if (ViewModel is null || _busy.Contains(hwnd) || IsWindowRepresented(hwnd))
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
            // The tile grows its slot in (so the dock width doesn't jump), so its live position is mid-
            // animation — aim the warp at where the tile will settle, not its current collapsed spot.
            target = TileRestingScreenCenter(tile);
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
            if (_busy.Contains(hwnd) || IsWindowRepresented(hwnd))
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
                // These were already minimized before launch — show them at once (no grow-in animation).
                var tile = ViewModel.AddMinimizedWindow(hwnd, appVm?.Icon, TaskbarApps.GetWindowTitle(hwnd), animate: false);
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

    /// <summary>The dock width (DIP) a window lands at, for sizing the minimize warp's final width.
    /// A freshly-added tile is still growing its slot in (AppearScale 0→1), so its live RenderWidth
    /// reads near zero at play time and the genie would neck to a dot — project the SETTLED width
    /// (RenderWidth scales linearly with AppearScale) so the warp's thinnest point is the final
    /// tile width, matching the slot that's opening beneath it.</summary>
    private double TileWidthOf(DockItemViewModel tile)
    {
        double width = tile.RenderWidth;
        if (tile.AppearScale is > 0.01 and < 0.999)
            width /= tile.AppearScale; // mid grow-in → the width it will settle at
        return width > 1 ? width : (ViewModel?.Settings.IconSize ?? 48);
    }

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
            DropMinimizedTracking(hwnd, tile);
            onDone();
            return;
        }

        if (bitmap is null)
        {
            WindowControl.Restore(hwnd);
            DropMinimizedTracking(hwnd, tile);
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
            DropMinimizedTracking(hwnd, tile);
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

    /// <summary>Screen-space (DIP) center of a tile's RESTING (fully grown-in) slot. Used to aim the
    /// minimize warp at a tile that's still growing its slot in — its live position is mid-animation.</summary>
    private Point TileRestingScreenCenter(DockItemViewModel tile)
    {
        var (left, top) = ComputePlacement();
        var (x, y) = ViewModel!.RestingCenterOf(tile);
        return new Point(left + x, top + y);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // While a minimize/restore warp is animating, pause the dock's own per-frame work (magnification,
        // acrylic tracking, glass shader updates). It shares CompositionTarget.Rendering with the warp, and
        // on a restore the click leaves the cursor over the dock — so without this, this loop would run
        // every frame alongside the warp and starve it of render time (measurably fewer warp frames, hence
        // the restore-only stutter). Stays hooked; the next tick after the warp frees _busy resumes it.
        if (_busy.Count > 0)
            return;

        // Frame-rate cap: throttle the per-frame magnification + glass work to the target FPS (shaved a
        // little below the exact interval so a 60 Hz panel isn't halved by vsync jitter; a faster panel is
        // throttled down toward it). The loop stays hooked and keeps firing at vsync — a throttled tick
        // just returns, and the next eligible tick runs the full body below (including the settle/detach
        // test), so idle self-detach still happens within one frame interval.
        var renderNow = ((RenderingEventArgs)e).RenderingTime;
        double dtMs;
        if (_lastRenderTime == TimeSpan.Zero)
        {
            dtMs = 1000.0 / 60.0; // first frame after (re)hook: one nominal step, no catch-up jump
        }
        else
        {
            double gap = (renderNow - _lastRenderTime).TotalMilliseconds;
            if (gap < PerformanceProfile.MinFrameIntervalMs)
                return;
            // Real time since the last PROCESSED frame drives the eases (so they're frame-rate
            // independent). Clamp so a stall (GC pause, load spike) takes one big step, not a wild jump.
            dtMs = Math.Min(gap, 100.0);
        }
        _lastRenderTime = renderNow;

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
            // The window spans the whole monitor edge, so "inside the window" would hover the entire
            // screen band; test the centered footprint a fully-magnified bar can cover instead.
            double mainPos = IsVerticalDock ? w.Y : w.X;
            double mainSize = IsVerticalDock ? ViewModel.WindowHeight : ViewModel.WindowWidth;
            double half = (ViewModel.HoverExtentMain > 0 ? ViewModel.HoverExtentMain : mainSize) / 2;
            _hovering = w.X >= 0 && w.Y >= 0 && w.X <= ViewModel.WindowWidth && w.Y <= ViewModel.WindowHeight
                && Math.Abs(mainPos - mainSize / 2) <= half;
        }

        // Suppress magnification while resizing via a separator (icons stay at the new resting size).
        double mouseMain = IsVerticalDock ? _mouseY : _mouseX;
        bool animating = ViewModel?.UpdateMagnification(mouseMain, _hovering && !_separatorResize, dtMs) ?? false;
        SyncAcrylic();      // track the acrylic backdrop to the (magnifying) bar each frame
        UpdateGlassShape();    // keep the shader's rounded-rect matched to the bar (cheap; DP-guarded)
        UpdateGlassSpecular(); // sweep the rim glint toward the cursor and ramp it with hover

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

        if (!animating && !_hovering && GlassSpecularSettled())
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
        _lastRenderTime = TimeSpan.Zero; // next hook starts a fresh frame-delta clock (nominal first step)
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

        // Taskbar apps (pinned or running), minimized-window tiles, and pinned files/folders drag.
        _dragCandidate = item is not null && (item.IsTaskbarApp || item.IsMinimizedWindow || item.IsPinnedPath)
            ? item : null;
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

        if (sender is not FrameworkElement { DataContext: DockItemViewModel item } target)
            return;

        ActivateItem(item, target);
    }

    /// <summary>Performs an item's click action — also the UIA Invoke entry point
    /// (Accessibility/DockItemElement). <paramref name="anchor"/> places the folder List menu.</summary>
    internal void ActivateItem(DockItemViewModel item, FrameworkElement? anchor)
    {
        if (item.IsMinimizedWindow)
            RestoreMinimized(item);
        else if (item.IsTaskbarApp)
            ActivateOrLaunch(item);
        else if (item.IsStartMenu)
            OpenStartMenu();
        else if (item.IsPinnedFolder && item.PathModel?.ViewContentAs == FolderViewContentAs.List)
            OpenFolderListMenu(item, anchor);
        else if (item.IsPinnedFolder && item.PathModel is not null)
            ToggleFolderFan(item); // fan/grid flyout (Automatic picks by count)
        else
            item.Activate(); // shortcut / Recycle Bin / pinned file
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
            { IsPinnedFolder: true } => BuildFolderMenu(item),
            { IsPinnedFile: true } => BuildFileMenu(item),
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
        MenuBuilder.AddItem(menu, Loc.T("Menu_TaskManager"), () => ShortcutService.Launch("taskmgr.exe"));
        menu.Items.Add(new Separator());

        var s = ViewModel!.Settings;

        // Turn Hiding On/Off — flips the AutoHideDock setting (mirrors the Preferences toggle).
        MenuBuilder.AddItem(menu, Loc.T(s.AutoHideDock ? "Menu_TurnHidingOff" : "Menu_TurnHidingOn"), () =>
        {
            ViewModel!.Settings.AutoHideDock = !ViewModel.Settings.AutoHideDock;
            ViewModel.Save();
            ApplyAutoHide();
            _settingsWindow?.SyncFromSettings();
        });

        MenuBuilder.AddItem(menu, Loc.T(s.MagnificationEnabled ? "Menu_TurnMagnificationOff" : "Menu_TurnMagnificationOn"), () =>
        {
            var settings = ViewModel!.Settings;
            settings.MagnificationEnabled = !settings.MagnificationEnabled;
            // Re-enabling with a stale max size would read as "on" yet magnify nothing.
            if (settings.MagnificationEnabled && settings.MaxIconSize <= settings.IconSize)
                settings.MaxIconSize = settings.IconSize * 2;
            ViewModel.RecomputeLayout();
            ViewModel.Save();
            _settingsWindow?.SyncFromSettings();
        });

        var position = new MenuItem { Header = Loc.T("Menu_PositionOnScreen") };
        position.Items.Add(MenuChoice("Position_Left", s.Edge == DockEdge.Left, () => PickEdge(DockEdge.Left)));
        position.Items.Add(MenuChoice("Position_Bottom", s.Edge == DockEdge.Bottom, () => PickEdge(DockEdge.Bottom)));
        position.Items.Add(MenuChoice("Position_Right", s.Edge == DockEdge.Right, () => PickEdge(DockEdge.Right)));
        menu.Items.Add(position);

        var minimizeUsing = new MenuItem { Header = Loc.T("Menu_MinimizeUsing") };
        minimizeUsing.Items.Add(MenuChoice("Minimize_Genie", s.MinimizeEffect == MinimizeEffect.Genie, () => PickMinimizeEffect(MinimizeEffect.Genie)));
        minimizeUsing.Items.Add(MenuChoice("Minimize_Suck", s.MinimizeEffect == MinimizeEffect.Suck, () => PickMinimizeEffect(MinimizeEffect.Suck)));
        minimizeUsing.Items.Add(MenuChoice("Minimize_Scale", s.MinimizeEffect == MinimizeEffect.Scale, () => PickMinimizeEffect(MinimizeEffect.Scale)));
        menu.Items.Add(minimizeUsing);

        menu.Items.Add(new Separator());
        MenuBuilder.AddItem(menu, Loc.T("Menu_DockPreferences"), () => OpenDockPreferences());
        MenuBuilder.AddItem(menu, Loc.T("Menu_AboutDockable"), OpenAbout);
        menu.Items.Add(new Separator());
        MenuBuilder.AddItem(menu, Loc.T("Menu_QuitDockable"), () => Application.Current.Shutdown());
        return menu;
    }

    /// <summary>Context-menu pick of a dock edge: applies it and syncs the open Preferences combo.</summary>
    private void PickEdge(DockEdge edge)
    {
        SetEdge(edge);
        _settingsWindow?.SyncFromSettings();
    }

    /// <summary>Context-menu pick of a minimize effect: persists it and syncs the open Preferences combo.</summary>
    private void PickMinimizeEffect(MinimizeEffect effect)
    {
        if (ViewModel is null)
            return;
        ViewModel.Settings.MinimizeEffect = effect;
        ViewModel.Save();
        _settingsWindow?.SyncFromSettings();
    }

    // Built-in Dock Preferences tile: Keep in Dock (pin toggle), and — while the window is open —
    // Quit (closes the Preferences window only, never the dock).
    private ContextMenu BuildPreferencesMenu(DockItemViewModel app)
    {
        var menu = new ContextMenu();

        MenuBuilder.AddCheckable(menu, Loc.T("Menu_KeepInDock"), app.IsPinned, () =>
        {
            if (app.IsPinned)
                ViewModel!.UnpinApp(DockItem.PreferencesLaunchPath);
            else
                ViewModel!.PinApp(DockItem.PreferencesLaunchPath, int.MaxValue);
            RefreshTaskbarApps();
        });

        if (_settingsWindow is not null)
        {
            menu.Items.Add(new Separator());
            // Closes Preferences only; the dock keeps running.
            MenuBuilder.AddItem(menu, Loc.T("Menu_Quit"), () => _settingsWindow?.Close());
        }

        return menu;
    }

    // --- Pinned file/folder menus (the macOS-style right section) ---

    /// <summary>A non-clickable gray section title, macOS-menu-style (e.g. "Sort by").</summary>
    private static MenuItem MenuSection(string locKey) => new() { Header = Loc.T(locKey), IsEnabled = false };

    /// <summary>A single-select option row: a check mark marks the current choice, clicking picks
    /// it (and persists). The presentation the choice controls comes later; only the selection is live.</summary>
    private MenuItem MenuChoice(string locKey, bool selected, Action pick)
    {
        var choice = new MenuItem { Header = Loc.T(locKey), IsChecked = selected };
        choice.Click += (_, _) => { pick(); ViewModel?.Save(); };
        return choice;
    }

    /// <summary>Re-renders a pinned folder/file tile's icon (stack ↔ plain folder, new sort order).</summary>
    private static void RefreshPathTileIcon(DockItemViewModel item) => _ = item.LoadIconAsync(256);

    /// <summary>Options ▶ Remove from Dock / Show in File Explorer, shared by files and folders.</summary>
    private MenuItem BuildPathOptions(DockItemViewModel item)
    {
        var options = new MenuItem { Header = Loc.T("Menu_Options") };
        MenuBuilder.AddItem(options, Loc.T("Menu_RemoveFromDock"), () => ViewModel?.UnpinPath(item));
        MenuBuilder.AddItem(options, Loc.T("Menu_ShowInFileExplorer"), () => ShortcutService.RevealInExplorer(item.Model.TargetPath));
        return options;
    }

    /// <summary>Open "Name": launches the pinned path (folder in Explorer, file with its default app).</summary>
    private static MenuItem BuildOpenPath(DockItemViewModel item)
    {
        var open = new MenuItem { Header = string.Format(Loc.T("Menu_OpenNamed"), item.DisplayName) };
        open.Click += (_, _) => item.Activate();
        return open;
    }

    // Pinned-folder menu: single-select Sort by / Display as / View content as sections (each shows
    // a check mark on the current choice), Options ▶, then Open "Name".
    private ContextMenu BuildFolderMenu(DockItemViewModel item)
    {
        var menu = new ContextMenu();
        if (item.PathModel is not { } pin)
            return menu;

        AddFolderConfigSections(menu, item, pin);
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildPathOptions(item));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildOpenPath(item));
        return menu;
    }

    /// <summary>The Sort by / Display as / View content as single-select sections, shared between
    /// the folder right-click menu and the List view's Options ▶ submenu.</summary>
    private void AddFolderConfigSections(ItemsControl menu, DockItemViewModel item, PinnedPath pin)
    {
        // Sort by / Display as picks re-render the tile (they change the stack's content / whether
        // the tile is a stack at all).
        void SetSort(FolderSortBy sort) { pin.SortBy = sort; RefreshPathTileIcon(item); }
        void SetDisplay(FolderDisplayAs display) { pin.DisplayAs = display; RefreshPathTileIcon(item); }

        menu.Items.Add(MenuSection("Menu_SortBy"));
        menu.Items.Add(MenuChoice("SortBy_Name", pin.SortBy == FolderSortBy.Name, () => SetSort(FolderSortBy.Name)));
        menu.Items.Add(MenuChoice("SortBy_DateAdded", pin.SortBy == FolderSortBy.DateAdded, () => SetSort(FolderSortBy.DateAdded)));
        menu.Items.Add(MenuChoice("SortBy_DateModified", pin.SortBy == FolderSortBy.DateModified, () => SetSort(FolderSortBy.DateModified)));
        menu.Items.Add(MenuChoice("SortBy_DateCreated", pin.SortBy == FolderSortBy.DateCreated, () => SetSort(FolderSortBy.DateCreated)));
        menu.Items.Add(MenuChoice("SortBy_Kind", pin.SortBy == FolderSortBy.Kind, () => SetSort(FolderSortBy.Kind)));
        menu.Items.Add(new Separator());

        menu.Items.Add(MenuSection("Menu_DisplayAs"));
        menu.Items.Add(MenuChoice("DisplayAs_Folder", pin.DisplayAs == FolderDisplayAs.Folder, () => SetDisplay(FolderDisplayAs.Folder)));
        menu.Items.Add(MenuChoice("DisplayAs_Stack", pin.DisplayAs == FolderDisplayAs.Stack, () => SetDisplay(FolderDisplayAs.Stack)));
        menu.Items.Add(new Separator());

        menu.Items.Add(MenuSection("Menu_ViewContentAs"));
        menu.Items.Add(MenuChoice("ViewAs_Fan", pin.ViewContentAs == FolderViewContentAs.Fan, () => pin.ViewContentAs = FolderViewContentAs.Fan));
        menu.Items.Add(MenuChoice("ViewAs_Grid", pin.ViewContentAs == FolderViewContentAs.Grid, () => pin.ViewContentAs = FolderViewContentAs.Grid));
        menu.Items.Add(MenuChoice("ViewAs_List", pin.ViewContentAs == FolderViewContentAs.List, () => pin.ViewContentAs = FolderViewContentAs.List));
        menu.Items.Add(MenuChoice("ViewAs_Automatic", pin.ViewContentAs == FolderViewContentAs.Automatic, () => pin.ViewContentAs = FolderViewContentAs.Automatic));
    }

    // Pinned-file menu: Options ▶ (Remove from Dock / Show in File Explorer), then Open "Name".
    private ContextMenu BuildFileMenu(DockItemViewModel item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(BuildPathOptions(item));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildOpenPath(item));
        return menu;
    }

    // --- Folder fan-out ("View content as: Fan") -------------------------------------------
    //
    // Clicking a fan folder fans its top items upward out of the tile: slot 0 (the stack's top
    // item) nearest the dock, the deepest item highest, crowned by an "N more in File Explorer"
    // tail. Items rise from the tile with a stagger; when the tile is displayed as a plain Folder
    // there's no stack for them to visually emerge from, so they also fade in.

    private DockItemViewModel? _fanItem;        // the folder tile whose fan is open
    private DockItemViewModel? _fanLastClosed;  // StaysOpen=False closes on the toggle click's DOWN...
    private DateTime _fanClosedAt;              // ...so the UP must not instantly reopen it
    private int _fanGen;                        // stale-guard for the async enumeration
    private ImageSource? _fanPrevIcon;          // the tile's real icon, restored when the fan closes
    private bool _fanClosing;                   // the retraction (fan-in) is playing
    private bool _fanIsGrid;                    // the open flyout is the grid balloon, not the fan
    private string? _flyoutDragPath;            // path being OS-dragged out of the fan/grid (OnDrop pins it as-is)
    private bool? _externalDragToPathSection;   // cached payload classification for the current external drag
    private double _fanAnchorDelta;             // popup left minus the tile's center, fixed at open
    private System.ComponentModel.PropertyChangedEventHandler? _fanAnchorFollow; // tracks the tile as it drifts

    private const double FanIconSize = 46;      // entry icon (and the "more" disc) size, DIP
    private const double FanSpacing = 58;       // vertical distance between fan slots
    private const double FanArcPerSlot = 0.45;  // rightward drift (px per slot²): a subtle arc
    private const double FanTopPad = 40;        // canvas headroom above the top slot (rotated labels swing up)

    /// <summary>Click on a fan-enabled folder tile: opens its fan, or retracts an already-open one.</summary>
    private void ToggleFolderFan(DockItemViewModel item)
    {
        if (_fanClosing)
            return; // mid retraction — let it finish
        if (FanPopup.IsOpen)
        {
            if (_fanItem == item)
            {
                BeginCloseFan();
                return;
            }
            FanPopup.IsOpen = false; // switching folders: drop the old fan instantly
        }
        // The fan retracted on this click's mouse-down (outside-capture dismiss); swallow the up.
        if (_fanLastClosed == item && (DateTime.UtcNow - _fanClosedAt).TotalMilliseconds < 400)
        {
            _fanLastClosed = null;
            return;
        }
        OpenFolderFlyout(item);
    }

    /// <summary>Enumerates the folder once, then shows the fan or the grid per its "View content
    /// as" — Automatic picks the fan for up to 9 items and the grid for 10 or more (macOS-style).</summary>
    private async void OpenFolderFlyout(DockItemViewModel item)
    {
        if (ViewModel is null || item.PathModel is not { } pin)
            return;

        int gen = ++_fanGen;
        var entries = await Task.Run(() => FolderContents.GetSorted(pin.Path, pin.SortBy));
        if (gen != _fanGen || ViewModel is null)
            return; // superseded by a newer open/close while enumerating

        var view = pin.ViewContentAs;
        if (view == FolderViewContentAs.Automatic)
            view = entries.Count <= 9 ? FolderViewContentAs.Fan : FolderViewContentAs.Grid;

        if (view == FolderViewContentAs.Grid)
            ShowFolderGrid(item, pin, entries);
        else
            ShowFolderFan(item, pin, entries);
    }

    private void ShowFolderFan(DockItemViewModel item, PinnedPath pin, List<FileSystemInfo> entries)
    {
        if (ViewModel is null)
            return;

        var top = entries.Take(FolderContents.MaxItems).ToList();
        int remaining = entries.Count - top.Count;

        FanCanvas.Children.Clear();

        // Slot 0 is the bottom of the fan (nearest the dock) and holds the TOP of the stack; the
        // "more in File Explorer" tail takes the highest slot.
        int slots = top.Count + 1;
        double fanHeight = slots * FanSpacing + FanTopPad; // headroom: rotated labels swing upward

        // Labels sit LEFT of the icon column, so each row is right-anchored on its icon. Build and
        // measure every row first: the widest label sets how far left of the icons the canvas (and
        // the popup) must extend for all the icons to stay on the arc.
        var rows = new List<FrameworkElement>(slots);
        var rowWidths = new List<double>(slots);
        double labelExtent = 0; // the widest (label + gap) portion left of an icon
        for (int k = 0; k < slots; k++)
        {
            bool isTail = k == slots - 1;
            // Fan labels show the real file name, extension included (folders have none — ha!).
            FrameworkElement entry = isTail
                ? BuildFanEntry(BuildFanTailIcon(), FanTailLabel(remaining), pin.Path, draggable: false)
                : BuildFanEntry(BuildFanItemIcon(top[k].FullName), top[k].Name, top[k].FullName, draggable: true);
            entry.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            rows.Add(entry);
            rowWidths.Add(entry.DesiredSize.Width);
            labelExtent = Math.Max(labelExtent, entry.DesiredSize.Width - FanIconSize);
        }

        for (int k = 0; k < slots; k++)
        {
            var entry = rows[k];
            double iconLeft = labelExtent + FanArcPerSlot * k * k; // the icon column follows the arc
            Canvas.SetLeft(entry, iconLeft - (rowWidths[k] - FanIconSize)); // right-anchor on the icon
            Canvas.SetTop(entry, fanHeight - (k + 1) * FanSpacing);

            // Each row (label + icon together) tilts with the fan: 0° at the bottom, growing to the
            // arc's own tangent at the top, pivoting on the icon's center — now the row's RIGHT end.
            // The rise translation is applied after the rotation so entries still travel straight up.
            double angle = Math.Atan2(2 * FanArcPerSlot * k, FanSpacing) * 180.0 / Math.PI;
            var rise = new TranslateTransform(0, k * FanSpacing);
            entry.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new RotateTransform(angle, rowWidths[k] - FanIconSize / 2, FanIconSize / 2),
                    rise,
                },
            };
            var delay = TimeSpan.FromMilliseconds(k * 18);
            rise.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(k * FanSpacing, 0, TimeSpan.FromMilliseconds(280))
                {
                    BeginTime = delay,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
            // The icons fade in as the fan starts (the shorter fade finishes while the rise is
            // still traveling, so entries materialize first, then glide into place).
            entry.Opacity = 0;
            entry.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = delay });

            FanCanvas.Children.Add(entry);
        }

        // The popup's hwnd clips at the canvas size — cover the widest label, the arc's drift, and
        // shadow headroom so no label is ever cut off.
        FanCanvas.Width = labelExtent + FanIconSize + FanArcPerSlot * (slots - 1) * (slots - 1) + 32;
        FanCanvas.Height = fanHeight;

        // Anchor the icon column over the tile (labels extend left of it). Vertically, the first
        // (bottom) fan icon's bottom edge sits 20px above the top of a fully-magnified dock icon,
        // so the fan clears the folder tile even at full magnification.
        FanPopup.HorizontalOffset = item.X + item.RenderWidth / 2 - FanIconSize / 2 - labelExtent;
        double bottomIconBottom = fanHeight - FanSpacing + FanIconSize; // canvas-Y of that edge
        FanPopup.VerticalOffset = ViewModel.MagnifiedTop - 20 - bottomIconBottom;
        _fanIsGrid = false;
        ShowFlyoutPopup(item);
    }

    /// <summary>Opens the flyout popup for <paramref name="item"/>: swaps the tile to the
    /// open-stack indicator and takes the subtree mouse capture that drives manual light-dismiss.</summary>
    private void ShowFlyoutPopup(DockItemViewModel item)
    {
        _fanItem = item;
        // While the flyout is open the tile shows the macOS open-stack indicator instead of its icon.
        _fanPrevIcon = item.Icon;
        item.Icon = FanOpenTileIcon();

        // Follow the tile: magnification keeps drifting the icons (the cursor still sweeps the bar
        // while the flyout is up), so re-anchor the popup whenever the tile's center moves.
        _fanAnchorDelta = FanPopup.HorizontalOffset - (item.X + item.RenderWidth / 2);
        _fanAnchorFollow = (_, e) =>
        {
            if (e.PropertyName is nameof(DockItemViewModel.X) or nameof(DockItemViewModel.RenderWidth)
                && _fanItem is { } anchored)
                FanPopup.HorizontalOffset = anchored.X + anchored.RenderWidth / 2 + _fanAnchorDelta;
        };
        item.PropertyChanged += _fanAnchorFollow;

        FanPopup.IsOpen = true;

        // Manual light-dismiss: once the popup has rendered, a subtree capture routes any click
        // outside the flyout to OnFanOutsideClick so the retraction can play before the close.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (FanPopup.IsOpen && !_fanClosing)
                Mouse.Capture(FanCanvas, CaptureMode.SubTree);
        }, DispatcherPriority.Input);
    }

    /// <summary>Retracts the fan — the opening animation in reverse (entries sink back into the
    /// tile, top rows first; Folder mode also fades them out) — then really closes the popup.</summary>
    private void BeginCloseFan()
    {
        if (!FanPopup.IsOpen || _fanClosing)
            return;
        _fanClosing = true;
        Mouse.Capture(null); // give input back immediately; the retraction is purely visual

        double totalMs;
        if (_fanIsGrid)
        {
            // The grid balloon scales back into the folder tile (the opening mirrored) and fades.
            foreach (UIElement child in FanCanvas.Children)
            {
                child.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
                if (child is FrameworkElement { RenderTransform: ScaleTransform scale })
                {
                    var shrink = new DoubleAnimation(0.15, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
                }
            }
            totalMs = 190;
        }
        else
        {
            int slots = FanCanvas.Children.Count;
            const double durationMs = 240;
            double maxDelay = 0;
            for (int k = 0; k < slots; k++)
            {
                var entry = (FrameworkElement)FanCanvas.Children[k];
                double delay = (slots - 1 - k) * 10; // top rows start first — the opening mirrored
                maxDelay = Math.Max(maxDelay, delay);

                // Animate from the CURRENT offset (an interrupted opening reverses mid-flight).
                if (entry.RenderTransform is TransformGroup { Children: [_, TranslateTransform rise] })
                    rise.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(k * FanSpacing, TimeSpan.FromMilliseconds(durationMs))
                        {
                            BeginTime = TimeSpan.FromMilliseconds(delay),
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                        });
                // The opening mirrored: icons fade out at the END of the retraction, vanishing just
                // as they land back in the tile.
                entry.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(delay + 100),
                    });
            }
            totalMs = maxDelay + durationMs + 20;
        }

        var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(totalMs) };
        done.Tick += (_, _) =>
        {
            done.Stop();
            FanPopup.IsOpen = false; // OnFanClosed does the state cleanup + tile-icon restore
        };
        done.Start();
    }

    // Any mouse-down outside the fan (we hold a subtree capture while it's open) retracts it.
    private void OnFanOutsideClick(object sender, MouseButtonEventArgs e) => BeginCloseFan();

    // Capture stolen (a dock drag, another popup, Alt-Tab): retract rather than linger open.
    private void OnFanLostCapture(object sender, MouseEventArgs e)
    {
        if (!FanPopup.IsOpen || _fanClosing)
            return;
        // A child inside the flyout taking capture (the grid's scrollbar being dragged) is fine —
        // OnFanPreviewMouseUp retakes the subtree capture when it releases.
        if (Mouse.Captured is Visual captured && FanCanvas.IsAncestorOf(captured))
            return;
        BeginCloseFan();
    }

    // A child (e.g. the grid's scrollbar) released its capture: retake the subtree capture that
    // drives light-dismiss, once the release has fully processed.
    private void OnFanPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (FanPopup.IsOpen && !_fanClosing && Mouse.Captured is null)
                Mouse.Capture(FanCanvas, CaptureMode.SubTree);
        }, DispatcherPriority.Input);
    }

    // Records what just closed so the tile click that dismissed the popup doesn't reopen it.
    private void OnFanClosed(object? sender, EventArgs e)
    {
        if (_fanItem is { } openItem)
        {
            openItem.Icon = _fanPrevIcon; // put the folder/stack icon back
            if (_fanAnchorFollow is not null)
                openItem.PropertyChanged -= _fanAnchorFollow;
        }
        _fanAnchorFollow = null;
        _fanPrevIcon = null;
        _fanLastClosed = _fanItem;
        _fanClosedAt = DateTime.UtcNow;
        _fanItem = null;
        _fanClosing = false;
        _fanGen++; // cancel any in-flight enumeration
    }

    private static ImageSource? _fanOpenTileIcon;

    /// <summary>The tile icon shown while a folder's fan is open: a semi-transparent rounded square
    /// with a dark downward chevron (macOS's open-stack indicator). Rendered once and cached.</summary>
    private static ImageSource FanOpenTileIcon()
    {
        if (_fanOpenTileIcon is not null)
            return _fanOpenTileIcon;

        const int size = 256;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Translucent light square with rounded corners and a faint rim.
            var fill = new SolidColorBrush(Color.FromArgb(0x59, 0xC9, 0xC9, 0xC9));
            var rim = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 4);
            dc.DrawRoundedRectangle(fill, rim, new Rect(4, 4, size - 8, size - 8), 52, 52);

            // Downward chevron, centered.
            var chevron = new StreamGeometry();
            using (var g = chevron.Open())
            {
                g.BeginFigure(new Point(84, 112), isFilled: false, isClosed: false);
                g.LineTo(new Point(128, 156), isStroked: true, isSmoothJoin: true);
                g.LineTo(new Point(172, 112), isStroked: true, isSmoothJoin: true);
            }
            chevron.Freeze();
            var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xD9, 0x1E, 0x1E, 0x1E)), 18)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, stroke, chevron);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return _fanOpenTileIcon = bitmap;
    }

    /// <summary>
    /// Makes a fan/grid row draggable as a real file (OS drag): dropping it on the dock pins it to
    /// the right section (see OnDrop), dropping it in Explorer copies it, dropping it on the dock's
    /// Recycle Bin recycles it. The flyout retracts on its own when the drag starts (the OS drag
    /// steals the light-dismiss capture).
    /// </summary>
    private void AttachFlyoutDrag(FrameworkElement row, string path)
    {
        Point down = default;
        bool pressed = false;
        row.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(row); pressed = true; };
        row.PreviewMouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed)
                return;
            var p = e.GetPosition(row);
            if (Math.Abs(p.X - down.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - down.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            pressed = false;
            _flyoutDragPath = path;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop(row, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            finally
            {
                _flyoutDragPath = null;
            }
        };
    }

    /// <summary>Height of a fan label pill; its corner radius is half this, so it's fully rounded.</summary>
    private const double FanPillHeight = 26;

    /// <summary>One fan row: the icon with a fully-rounded name pill; click launches; item rows
    /// (not the tail) can be dragged out onto the dock or into Explorer.</summary>
    private FrameworkElement BuildFanEntry(UIElement icon, string label, string launchPath, bool draggable)
    {
        var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "LabelTextBrush");

        var pill = new Border
        {
            Height = FanPillHeight,
            CornerRadius = new CornerRadius(FanPillHeight / 2),
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 8, 0),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = TranslucentLabelBackground(),
            Child = text,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.4, Color = Colors.Black },
        };
        pill.SetResourceReference(Border.BorderBrushProperty, "LabelBorderBrush");

        // InvokableRow = a StackPanel with a UIA peer, so screen readers can read + invoke the row.
        var row = new InvokableRow
        {
            Orientation = Orientation.Horizontal,
            Height = FanIconSize,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent, // hit-testable across the whole row
        };
        AutomationProperties.SetName(row, label);
        row.Children.Add(pill); // label to the LEFT of the icon
        row.Children.Add(icon);
        row.MouseLeftButtonUp += (_, _) =>
        {
            BeginCloseFan(); // retract while the pick launches
            ShortcutService.Launch(launchPath);
        };
        if (draggable)
            AttachFlyoutDrag(row, launchPath);
        return row;
    }

    /// <summary>The theme's label colour (light/dark/auto via LabelBgBrush) made slightly
    /// transparent. Resolved per fan open — entries are rebuilt each time — so theme switches stick.</summary>
    private Brush TranslucentLabelBackground()
    {
        var color = TryFindResource("LabelBgBrush") is SolidColorBrush themed
            ? themed.Color
            : Color.FromRgb(0xF7, 0xF7, 0xFA);
        var brush = new SolidColorBrush(Color.FromArgb(0xCC, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>A fan item's shell icon (loaded async so the fan opens instantly).</summary>
    private static UIElement BuildFanItemIcon(string path)
    {
        var image = new Image { Width = FanIconSize, Height = FanIconSize, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black };
        _ = LoadFanIconAsync(image, path, 96);
        return image;
    }

    private static async Task LoadFanIconAsync(Image image, string path, int pixelSize)
        => image.Source = await ShortcutService.LoadIconAsync(path, pixelSize);

    /// <summary>The shortcut arrow (↗: shaft + open head) shared by the fan/grid tail discs,
    /// uniform-scaled into ~30% of the disc and themed via LabelTextBrush.</summary>
    private static ShapePath BuildTailArrow(double boxSize, double strokeThickness)
    {
        var arrow = new ShapePath
        {
            Data = Geometry.Parse("M 0,12 L 12,0 M 3.5,0 H 12 V 8.5"),
            Stretch = Stretch.Uniform,
            Width = boxSize * 0.3,
            Height = boxSize * 0.3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        arrow.SetResourceReference(ShapePath.StrokeProperty, "LabelTextBrush");
        return arrow;
    }

    /// <summary>The fan tail's disc: a semi-transparent circle with a shortcut arrow (↗), both
    /// following the light/dark/auto theme (the label brushes, resolved per fan open).</summary>
    private UIElement BuildFanTailIcon()
    {
        var disc = new Ellipse
        {
            Fill = TranslucentLabelBackground(),
            StrokeThickness = 1,
        };
        disc.SetResourceReference(Ellipse.StrokeProperty, "LabelBorderBrush");

        var grid = new Grid { Width = FanIconSize, Height = FanIconSize };
        grid.Children.Add(disc);
        grid.Children.Add(BuildTailArrow(FanIconSize, 3));
        return grid;
    }

    /// <summary>"N more in File Explorer", or "Open in File Explorer" when everything fit the fan.</summary>
    private static string FanTailLabel(int remaining)
        => remaining > 0 ? string.Format(Loc.T("Fan_MoreInExplorer"), remaining) : Loc.T("Fan_OpenInExplorer");

    // --- Folder grid ("View content as: Grid") ---------------------------------------------

    private const double GridIconSize = 94;    // file icon size in the grid, per spec
    private const double GridCellWidth = 132;
    private const double GridCellHeight = 152; // icon + up to two label lines
    private const int GridMaxColumns = 8;
    private const int GridVisibleRows = 6;     // taller folders scroll

    /// <summary>Opens the grid balloon: ALL of the folder's items (in its "Sort by" order) in a
    /// rounded (24px), theme-tinted, vertically scrollable grid, plus an "Open in File Explorer"
    /// tail cell. Columns scale square-ish with the item count (5 files + tail → 3×2), capped at 8.</summary>
    private void ShowFolderGrid(DockItemViewModel item, PinnedPath pin, List<FileSystemInfo> entries)
    {
        if (ViewModel is null)
            return;

        FanCanvas.Children.Clear();

        int total = entries.Count + 1; // + the "Open in File Explorer" cell
        int cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(total)), 1, GridMaxColumns);
        int rows = (int)Math.Ceiling(total / (double)cols);

        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = GridCellWidth,
            ItemHeight = GridCellHeight,
            Width = cols * GridCellWidth,
        };
        foreach (var entry in entries)
            panel.Children.Add(BuildGridCell(BuildGridItemIcon(entry.FullName), entry.Name, entry.FullName, draggable: true));
        panel.Children.Add(BuildGridCell(BuildGridTailIcon(), Loc.T("Fan_OpenInExplorer"), pin.Path, draggable: false));

        // At most 6 rows visible, and never taller than the screen space left above the dock.
        double displayCap = SystemParameters.WorkArea.Height
            - (ViewModel.WindowHeight - ViewModel.MagnifiedTop) - 72;
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = rows > GridVisibleRows
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(GridCellHeight, Math.Min(GridVisibleRows * GridCellHeight, displayCap)),
        };

        var tint = TranslucentLabelBackground();
        var balloon = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = tint,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Child = scroll,
        };
        balloon.SetResourceReference(Border.BorderBrushProperty, "LabelBorderBrush");

        // Callout pointer: a small downward triangle at the balloon's bottom center, in the same
        // tint, aiming at the folder tile.
        var pointer = new ShapePath
        {
            Data = Geometry.Parse("M 0,0 H 28 L 14,13 Z"),
            Fill = tint,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0), // tuck under the balloon so no seam shows
        };

        var root = new StackPanel();
        root.Children.Add(balloon);
        root.Children.Add(pointer);

        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = root.DesiredSize.Width;
        double height = root.DesiredSize.Height;

        Canvas.SetLeft(root, 0);
        Canvas.SetTop(root, 0);
        FanCanvas.Children.Add(root);
        FanCanvas.Width = width;
        FanCanvas.Height = height;

        // Pop in: scale out of the folder tile — anchored at the pointer's tip (bottom center),
        // which sits directly over the tile — with a quick fade.
        var scale = new ScaleTransform(0.15, 0.15);
        root.RenderTransform = scale;
        root.RenderTransformOrigin = new Point(0.5, 1.0);
        root.Opacity = 0;
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        var grow = new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        // Centered over the tile, its bottom 20px above the top of a fully-magnified dock icon.
        FanPopup.HorizontalOffset = item.X + item.RenderWidth / 2 - width / 2;
        FanPopup.VerticalOffset = ViewModel.MagnifiedTop - 20 - height;
        _fanIsGrid = true;
        ShowFlyoutPopup(item);
    }

    /// <summary>One grid cell: the icon with the file name (extension included) centered beneath;
    /// item cells (not the tail) can be dragged out onto the dock or into Explorer.</summary>
    private FrameworkElement BuildGridCell(UIElement icon, string label, string launchPath, bool draggable)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34, // ~two lines, then ellipsis
            Margin = new Thickness(4, 6, 4, 0),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "LabelTextBrush");

        // InvokableRow = a StackPanel with a UIA peer, so screen readers can read + invoke the cell.
        var cell = new InvokableRow { Cursor = Cursors.Hand, Background = Brushes.Transparent };
        AutomationProperties.SetName(cell, label);
        cell.Children.Add(icon);
        cell.Children.Add(text);
        cell.MouseLeftButtonUp += (_, _) =>
        {
            BeginCloseFan(); // fade the balloon while the pick launches
            ShortcutService.Launch(launchPath);
        };
        if (draggable)
            AttachFlyoutDrag(cell, launchPath);
        return cell;
    }

    /// <summary>A grid item's shell icon (loaded async so the balloon opens instantly).</summary>
    private static UIElement BuildGridItemIcon(string path)
    {
        var image = new Image
        {
            Width = GridIconSize,
            Height = GridIconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _ = LoadFanIconAsync(image, path, 192); // 94 DIP stays crisp on HiDPI
        return image;
    }

    // --- Folder list ("View content as: List") ---------------------------------------------

    /// <summary>
    /// "View content as: List": the folder opens as a context menu above its tile — every item as
    /// a row (icon left, full file name right, never ellipsized; menus size to their content),
    /// then a separator, Options ▶ (the folder's Sort by / Display as / View content as sections),
    /// and Open in File Explorer.
    /// </summary>
    private async void OpenFolderListMenu(DockItemViewModel item, FrameworkElement? placementTarget)
    {
        if (item.PathModel is not { } pin)
            return;

        var entries = await Task.Run(() => FolderContents.GetSorted(pin.Path, pin.SortBy));

        var menu = new ContextMenu();
        foreach (var entry in entries)
        {
            var icon = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            _ = LoadFanIconAsync(icon, entry.FullName, 48);

            // A TextBlock header keeps underscores literal (a string Header treats "_" as an
            // access-key marker and swallows it).
            var row = new MenuItem { Header = new TextBlock { Text = entry.Name }, Icon = icon };
            string path = entry.FullName;
            row.Click += (_, _) => ShortcutService.Launch(path);
            menu.Items.Add(row);
        }

        if (entries.Count > 0)
            menu.Items.Add(new Separator());

        var options = new MenuItem { Header = Loc.T("Menu_Options") };
        AddFolderConfigSections(options, item, pin);
        menu.Items.Add(options);

        MenuBuilder.AddItem(menu, Loc.T("Fan_OpenInExplorer"), () => ShortcutService.Launch(pin.Path));

        menu.PlacementTarget = placementTarget ?? this;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>The grid tail icon: a transparent circle with a 2px font-coloured ring and a
    /// shortcut arrow in the center — clicking it opens the folder in File Explorer.</summary>
    private FrameworkElement BuildGridTailIcon()
    {
        var disc = new Ellipse
        {
            Width = GridIconSize,
            Height = GridIconSize,
            Fill = Brushes.Transparent,
            StrokeThickness = 2,
        };
        disc.SetResourceReference(Ellipse.StrokeProperty, "LabelTextBrush");

        var grid = new Grid { Width = GridIconSize, Height = GridIconSize, HorizontalAlignment = HorizontalAlignment.Center };
        grid.Children.Add(disc);
        grid.Children.Add(BuildTailArrow(GridIconSize, 5));
        return grid;
    }

    // Open/pinned app menu: Options ▶ (Keep in Dock / Open at Login / Show in Explorer), then — for
    // running apps — Show All Windows and Quit (Force Quit while Alt is held).
    private ContextMenu BuildAppMenu(DockItemViewModel app)
    {
        var menu = new ContextMenu();

        // New Window: launch another instance of the app (most apps open a fresh window).
        MenuBuilder.AddItem(menu, Loc.T("Menu_NewWindow"), () => ShortcutService.Launch(app.LaunchPath));

        // Rename: change a pinned shortcut's display label (persisted via PinNames).
        if (app.IsPinned)
            MenuBuilder.AddItem(menu, Loc.T("Menu_Rename"), () => RenamePin(app));

        menu.Items.Add(new Separator());

        var options = new MenuItem { Header = Loc.T("Menu_Options") };

        MenuBuilder.AddCheckable(options, Loc.T("Menu_KeepInDock"), app.IsPinned, () =>
        {
            if (app.IsPinned)
                ViewModel!.UnpinApp(app.LaunchPath);
            else
                ViewModel!.PinApp(app.LaunchPath, int.MaxValue, app.DisplayName); // append to the pinned list (keep its name)
            RefreshTaskbarApps();
        });

        string exe = ResolveExecutable(app);
        string startupName = StartupEntryName(app, exe);
        MenuBuilder.AddCheckable(options, Loc.T("Menu_OpenAtLogin"), StartupManager.IsEnabled(startupName), () =>
        {
            if (StartupManager.IsEnabled(startupName))
                StartupManager.Disable(startupName);
            else
                StartupManager.Enable(startupName, exe);
        });

        MenuBuilder.AddItem(options, Loc.T("Menu_ShowInExplorer"), () => ShortcutService.RevealInExplorer(app.LaunchPath));

        menu.Items.Add(options);

        // Running-app actions only make sense when the app has open windows.
        if (app.Windows.Count > 0)
        {
            menu.Items.Add(new Separator());

            MenuBuilder.AddItem(menu, Loc.T("Menu_ShowAllWindows"), () => ActivateOrLaunch(app));

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

    /// <summary>Shows the "Dockable Preferences" window, focusing it if already open (single instance).
    /// An optional <paramref name="section"/> id (e.g. "About") navigates the window to that page.
    /// Internal so the menu bar's context menu can open it too.</summary>
    internal void OpenDockPreferences(string? section = null)
    {
        if (ViewModel is null)
            return;

        if (_settingsWindow is not null)
        {
            // Already open: if it's minimized into the dock, bring it back (this path skips the warp).
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            CleanUpPreferencesMinimize(ViewModel.PreferencesHwnd);
            if (section is not null)
                _settingsWindow.NavigateTo(section);
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(ViewModel, SetTheme, SetEdge, SetTaskbarVisibility, SetGlassEffect, SetShowMenuBar, ApplyLiquidGlassSettings, SetPerformanceMode);
        if (section is not null)
            _settingsWindow.NavigateTo(section);

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
        DropMinimizedTracking(hwnd, ViewModel?.FindMinimizedWindow(hwnd));
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

    /// <summary>Opens the About section of the Preferences window (the standalone About window was
    /// folded into Preferences).</summary>
    private void OpenAbout() => OpenDockPreferences("About");

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
            // Part the tiles to preview where the drop would land: folders/documents open the gap
            // in the right section (the left side is reserved for executable-like shortcuts).
            ShowExternalDropGap(main, ExternalDragTargetsPathSection(e));
        }
        e.Handled = true;
    }

    // The external drag left the dock without dropping: close the placeholder gap.
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        ClearExternalDropGap();
        _externalDragToPathSection = null; // re-classify the next drag session
        e.Handled = true;
    }

    /// <summary>Which section the current external drag previews into: the right (files/folders)
    /// section unless every dragged item is executable-like. Classified once per drag session and
    /// cached — DragOver fires continuously and reading the payload isn't free.</summary>
    private bool ExternalDragTargetsPathSection(DragEventArgs e)
    {
        if (_externalDragToPathSection is { } cached)
            return cached;
        bool path = true; // default: folders/documents → the right section
        if (_flyoutDragPath is null
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            path = !paths.Any(p => IsAppLike(TaskbarApps.ResolveToTarget(p)));
        _externalDragToPathSection = path;
        return path;
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
        int pathIndex = ViewModel.ComputeDropPathIndex(main); // where the right-section gap previewed
        foreach (var path in paths)
        {
            // An item dragged out of our own fan/grid always pins to the right section, as-is
            // (even a launchable — it's a file from a folder, not an app shortcut).
            if (_flyoutDragPath is not null)
            {
                ViewModel.PinPath(path, pathIndex++);
                continue;
            }

            // Pin a .lnk's destination, not the .lnk — but keep the shortcut's name (e.g. "Chrome.lnk" → "Chrome").
            string target = TaskbarApps.ResolveToTarget(path);
            if (IsAppLike(target))
                ViewModel.PinApp(target, index++, System.IO.Path.GetFileNameWithoutExtension(path));
            else
                // Folders and documents pin to the right section (macOS keeps them out of the app
                // row), at the slot where the placeholder gap was showing.
                ViewModel.PinPath(target, pathIndex++);
        }

        _externalDragToPathSection = null; // this drag session is over
        RefreshTaskbarApps();
    }

    // Opens / refreshes the placeholder gap and keeps the render loop running so the tiles part and
    // track the cursor; cleared on leave/drop so they glide back together.
    private void ShowExternalDropGap(double main, bool pathSection)
    {
        ClearWindowRegion(); // unclip so the widening bar and parted tiles render (and receive the drag)
        ViewModel?.UpdateExternalDrop(main, pathSection);
        HookRendering();
    }

    private void ClearExternalDropGap()
    {
        ViewModel?.EndExternalDrop();
        HookRendering(); // run the loop so the parted tiles settle back
    }

    /// <summary>Extensions that pin as launchable apps; any other dropped path (a folder or a
    /// document) pins as a file/folder tile in the dock's right section instead.</summary>
    private static readonly HashSet<string> AppExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".com", ".bat", ".cmd", ".scr", ".msi", ".appref-ms", ".url" };

    private static bool IsAppLike(string path)
        => !Directory.Exists(path) && AppExtensions.Contains(Path.GetExtension(path));

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
            // The multi-size .ico (a URI-backed resource, which H.NotifyIcon accepts) renders the
            // tray glyph — its 16/32px frames stay crisp at notification-area size.
            IconSource = AppIcon.Tray,
            // Left and right click both open the menu; there's no separate click action.
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.LeftOrRightClick,
        };
        // The tray shows the same dock-wide menu as the dock's empty-space / separator right-click.
        // That menu bakes labels + check state at build time, so rebuild it fresh on the button-down
        // of the press that's about to open it (H.NotifyIcon shows the assigned menu on button-up).
        _trayIcon.TrayLeftMouseDown += (_, _) => RefreshTrayMenu();
        _trayIcon.TrayRightMouseDown += (_, _) => RefreshTrayMenu();
        _trayIcon.ForceCreate();
    }

    private void RefreshTrayMenu()
    {
        if (_trayIcon is not null && ViewModel is not null)
            _trayIcon.ContextMenu = BuildSeparatorMenu();
    }

    protected override void OnClosed(EventArgs e)
    {
        RestoreAllMinimized(); // don't leave the user's windows stranded in (now-gone) dock tiles
        _appRefreshTimer.Stop();
        _startWatchTimer.Stop();
        _pinCheckTimer.Stop();
        _backdropCapturer?.Stop(); // joins the capture thread before we tear down the profiler
        _glassProfiler?.Dispose();
        SetCaptureExclusion(false); // don't leave the dock excluded from capture
        _pinWatcher?.Dispose();
        _minimizeHook.Dispose();
        _minimizeIntercept.Dispose();
        _taskbarHideWatcher.Dispose();
        _thumbnails.Dispose();
        _foreground.Dispose();
        _acrylic.Dispose();
        if (_shellHookMsg != 0 && _hwnd != IntPtr.Zero)
            PInvoke.DeregisterShellHookWindow((HWND)_hwnd);
        _appBar?.Unregister(); // release reserved screen space
        Taskbar.Restore(); // restore the taskbar to its pre-launch state
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }
}
