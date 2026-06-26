using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dockable.Genie;
using Dockable.Interop;
using Dockable.Models;
using Dockable.Shell;
using Dockable.ViewModels;
using H.NotifyIcon;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable;

public partial class DockWindow : Window
{
    // Private window message the shell uses to send the dock AppBar notifications.
    private const uint AppBarCallbackMessage = 0x0400 + 1; // WM_USER + 1
    private const int WM_SETTINGCHANGE = 0x001A;           // OS setting changed (incl. light/dark)
    private const double SliverDip = 2;       // visible strip left peeking when auto-hidden
    private const int HideDelayMs = 450;      // grace period before auto-hide slides away
    private const double SlideSmoothing = 0.3;

    // Drag ghost geometry (matches the DragGhost popup in XAML): the icon's center sits at
    // (GhostCenterX, GhostCenterY) from the popup's top-left, so it tracks under the cursor.
    private const double GhostCenterX = 80;   // half of GhostRoot's 160 width
    private const double GhostCenterY = 72;   // 42px "Remove" row + half the 60px icon
    private const int DragSteadyMs = 500;     // hold still this long to arm "Remove"
    private const double SteadyEpsilon = 4;   // px of motion that counts as "moved"

    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow; // "Dock Preferences…" window (single instance)

    private double _mouseX;
    private bool _hovering;
    private bool _renderingHooked;

    // Shared hover label: the item currently under the cursor, plus the measured label content.
    private const double LabelGap = 4; // gap above a fully-magnified icon
    private DockItemViewModel? _hoveredItem;
    private DockItemViewModel? _labelItem; // item whose text the label currently reflects

    private IntPtr _hwnd;
    private AppBarManager? _appBar;
    private double _alwaysVisibleBottomDip; // bottom edge (DIP) granted by the AppBar reserve
    private bool _windowRegionClipped;      // true while the window is clipped to the resting bar

    // Keep the bar's drop shadow when the window is clipped to the resting bar.
    private const double DockRegionTopPaddingDip = 26;

    // Real window minimize/restore is intercepted and replaced with one of these effects.
    private readonly GenieAnimator _genie = new();
    private readonly ScaleAnimator _scale = new();
    private readonly MinimizeHook _minimizeHook = new();
    // Full-window captures taken while windows are visible (capture-at-minimize is too late).
    private readonly WindowThumbnailCache _thumbnails = new();
    // Windows whose minimize/restore we're currently animating (ignore re-entrant events).
    private readonly HashSet<IntPtr> _busy = new();

    private readonly DispatcherTimer _slideTimer;
    private readonly DispatcherTimer _hideDelayTimer;
    private bool _revealed = true;
    private double _targetTop;

    // Mirror the taskbar: poll running apps + watch the pinned folder.
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly DispatcherTimer _appRefreshTimer;
    private FileSystemWatcher? _pinWatcher;

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
    private double _resizeStartCursorY; // physical-px cursor Y at press
    private double _resizeStartIconSize;

    private bool AutoHide => ViewModel?.Settings.Behavior == DockBehavior.AutoHide;

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

        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _slideTimer.Tick += OnSlideTick;
        _hideDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
        _hideDelayTimer.Tick += OnHideDelayElapsed;
        _appRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _appRefreshTimer.Tick += (_, _) => RefreshTaskbarApps();

        _dragSteadyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragSteadyMs) };
        _dragSteadyTimer.Tick += OnDragSteadyElapsed;
        LostMouseCapture += OnLostMouseCapture; // robust cleanup if a drag is interrupted
    }

    private DockViewModel? ViewModel => DataContext as DockViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DockViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (ViewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyWindowSize();
            ApplyTheme(); // paint the bar for the saved theme before the window is shown
        }
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
            BarShadow.Opacity = 0.4;
        }
        else
        {
            // .macos-dock-light
            Resources["BarBackgroundBrush"] = Brush("#66FFFFFF");
            Resources["BarBorderBrush"] = Brush("#33FFFFFF");
            Resources["SeparatorBrush"] = Brush("#33000000");
            Resources["RunningDotBrush"] = Brush("#B3000000"); // rgba(0,0,0,0.7)
            Resources["FallbackBgBrush"] = Brush("#1F000000");
            Resources["FallbackTextBrush"] = Brush("#CC000000");
            BarShadow.Opacity = 0.15;
        }
    }

    private void SetTheme(DockTheme theme)
    {
        if (ViewModel is null || ViewModel.Settings.Theme == theme)
            return;
        ViewModel.Settings.Theme = theme;
        ViewModel.Save();
        ApplyTheme();
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
        _minimizeHook.Start();

        ApplyBehavior();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateTrayIcon();
        ApplyWindowSize();
        ApplyTaskbarVisibility();
        StartTaskbarMirror();
    }

    // --- Taskbar mirror: live pinned + running apps ---

    private void StartTaskbarMirror()
    {
        RefreshTaskbarApps();
        _appRefreshTimer.Start(); // pick up apps opening/closing

        try
        {
            _pinWatcher = new FileSystemWatcher(TaskbarApps.PinnedFolder, "*.lnk")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChange = (_, _) => Dispatcher.BeginInvoke(() => RefreshTaskbarApps());
            _pinWatcher.Created += onChange;
            _pinWatcher.Deleted += onChange;
            _pinWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => RefreshTaskbarApps());
        }
        catch
        {
            // Watching is a nicety; the 1s timer still keeps pins reasonably fresh.
        }
    }

    private void RefreshTaskbarApps() => ViewModel?.RefreshTaskbarApps(_ownProcessId);

    /// <summary>Click a taskbar app: focus its window if running, otherwise launch it.</summary>
    private void ActivateOrLaunch(DockItemViewModel app)
    {
        if (app.Windows.Count == 0)
        {
            ShortcutService.Launch(app.LaunchPath);
            return;
        }

        // Windows minimized into the dock restore with the genie effect (warping out of their
        // tile); the rest of the group is just brought forward. Only one genie can play at a time
        // (shared overlay), so if several are minimized, the first genies and the rest restore
        // instantly — none get left behind.
        var toRaise = new List<IntPtr>();
        bool geniePlayed = false;
        foreach (var hwnd in app.Windows)
        {
            var tile = ViewModel?.FindMinimizedWindow(hwnd);
            if (tile is null)
                toRaise.Add(hwnd);
            else if (!geniePlayed)
            {
                RestoreMinimized(tile);
                geniePlayed = true;
            }
            else
            {
                RestoreMinimizedInstant(tile);
            }
        }
        if (toRaise.Count > 0)
            WindowControl.ActivateAll(toRaise);
    }

    private void ApplyTaskbarVisibility()
    {
        if (ViewModel is null)
            return;

        // Native auto-hide: the taskbar slides away but reveals when the cursor reaches the
        // bottom edge. Self-restoring — a force-kill leaves it usable, so no watchdog is needed.
        Taskbar.SetAutoHide(ViewModel.Settings.HideTaskbar);
    }

    /// <summary>
    /// Centers the dock horizontally on the work area and anchors it to the bottom edge.
    /// In auto-hide mode the vertical target alternates between revealed and a thin sliver.
    /// </summary>
    private void PositionDock()
    {
        var (left, shownTop, hiddenTop) = ComputePlacement();
        Left = left;
        _targetTop = (AutoHide && !_revealed) ? hiddenTop : shownTop;

        // When not animating, snap straight to the target.
        if (!_slideTimer.IsEnabled)
            Top = _targetTop;
    }

    private (double Left, double ShownTop, double HiddenTop) ComputePlacement()
    {
        double height = ActualHeight > 0 ? ActualHeight : (ViewModel?.WindowHeight ?? 0);
        double width = ActualWidth > 0 ? ActualWidth : (ViewModel?.WindowWidth ?? 0);
        bool hideTaskbar = ViewModel?.Settings.HideTaskbar ?? false;

        // Always-visible: center on the dock's monitor. When the taskbar is hidden, anchor to
        // the full monitor bottom (the freed space); otherwise to the AppBar's granted strip
        // (deterministic — avoids the asynchronous work-area shrink after ABM_SETPOS).
        if (!AutoHide && _appBar?.IsRegistered == true && _hwnd != IntPtr.Zero)
        {
            var info = Monitors.ForWindow(_hwnd);
            double scale = info.Scale;
            double left = info.MonitorPx.Left / scale + (info.MonitorPx.Width / scale - width) / 2;
            double bottom = hideTaskbar ? info.MonitorPx.Bottom / scale : _alwaysVisibleBottomDip;
            double shownTop = bottom - height;
            return (left, shownTop, shownTop);
        }

        // Auto-hide: center on the primary work area. With the taskbar hidden, drop to the full
        // screen bottom so the dock sits flush at the edge rather than above the taskbar's gap.
        var work = SystemParameters.WorkArea;
        double screenBottom = hideTaskbar ? SystemParameters.PrimaryScreenHeight : work.Bottom;
        double autoLeft = work.Left + (work.Width - width) / 2;
        double autoShownTop = screenBottom - height;
        double autoHiddenTop = screenBottom - SliverDip;
        return (autoLeft, autoShownTop, autoHiddenTop);
    }

    // --- Magnification render loop ---

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizePressed || _separatorResize)
        {
            _hovering = true; // keep the dock revealed while resizing
            HandleSeparatorResize(PointToScreen(e.GetPosition(this)).Y);
            return;
        }

        _mouseX = e.GetPosition(this).X;
        _lastCursor = e.GetPosition(RootCanvas);
        _hovering = true;
        HookRendering();

        if (_dragInitiated)
        {
            PositionGhost(_lastCursor);
            // Real motion cancels a pending/active "Remove" and restarts the hold countdown.
            if (Distance(_lastCursor, _steadyAnchor) > SteadyEpsilon)
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
        GhostRemoveTag.Visibility = Visibility.Hidden;
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
        GhostRemoveTag.Visibility = Visibility.Visible;
    }

    private void DisarmRemove()
    {
        _removeArmed = false;
        GhostRemoveTag.Visibility = Visibility.Hidden;
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
            GhostRemoveTag.Visibility = Visibility.Hidden;
        };
        GhostRoot.BeginAnimation(OpacityProperty, fade);
    }

    private static bool IsRemovable(DockItemViewModel? item) => item is { IsTaskbarApp: true, IsPinned: true };

    // --- Separator drag = resize the dock (the Size setting) ---

    // screenY is the cursor's screen-Y in device px (PointToScreen stays correct even as the dock
    // window moves upward while growing).
    private void HandleSeparatorResize(double screenY)
    {
        if (ViewModel is null)
            return;

        double dpiY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        double deltaDip = (_resizeStartCursorY - screenY) / dpiY; // dragging up = larger

        if (!_separatorResize)
        {
            if (Math.Abs(deltaDip) < SystemParameters.MinimumVerticalDragDistance)
                return; // not a drag yet
            _separatorResize = true;
            _resizePressed = false;
            _hoveredItem = null;       // suppress the hover label during a resize
            CaptureMouse();
            Cursor = Cursors.SizeNS;
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
            ViewModel?.Save(); // persist the new Size
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
        _dragInitiated = false;     // clear before releasing capture so OnLostMouseCapture no-ops
        ReleaseMouseCapture();

        if (ViewModel is not null && item is not null)
        {
            bool removable = IsRemovable(item);
            var pos = e.GetPosition(RootCanvas);
            bool overDock = pos.X >= 0 && pos.Y >= 0 && pos.X <= ActualWidth && pos.Y <= ActualHeight;

            if (armed && removable)
                ViewModel.UnpinApp(item.LaunchPath);                            // hold-to-Remove
            else if (removable && overDock)
                ViewModel.MovePin(item.LaunchPath, ViewModel.DragInsertIndex);  // reorder pins
            // Otherwise (unpinned app, minimized window, or dropped away): no change → snaps back.

            ViewModel.EndItemDrag();    // the in-canvas tile reappears and settles to its slot
            RefreshTaskbarApps();
        }

        EndGhost(poof: armed);
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
        if (AutoHide)
            SlideIn();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Keep the loop running so the dock eases back to rest, then it self-detaches.
        _hovering = false;
        _hoveredItem = null; // hide the hover label once the cursor leaves the dock
        // Don't auto-hide mid-drag/resize: the cursor legitimately roams off the dock then.
        if (AutoHide && !_dragInitiated && !_separatorResize)
            _hideDelayTimer.Start(); // grace period, then slide away
    }

    // --- Auto-hide slide ---

    private void SlideIn()
    {
        _hideDelayTimer.Stop();
        if (_revealed)
            return;
        _revealed = true;
        StartSlideToTarget();
    }

    private void SlideOut()
    {
        if (!_revealed)
            return;
        _revealed = false;
        StartSlideToTarget();
    }

    private void OnHideDelayElapsed(object? sender, EventArgs e)
    {
        _hideDelayTimer.Stop();
        // Don't hide if the cursor wandered back over the dock in the meantime.
        if (!IsMouseOver)
            SlideOut();
    }

    private void StartSlideToTarget()
    {
        var (_, shownTop, hiddenTop) = ComputePlacement();
        _targetTop = _revealed ? shownTop : hiddenTop;
        if (!_slideTimer.IsEnabled)
            _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        double delta = _targetTop - Top;
        if (Math.Abs(delta) < 0.5)
        {
            Top = _targetTop;
            _slideTimer.Stop();
            return;
        }
        Top += delta * SlideSmoothing;
    }

    // --- Docking behavior (always-visible reserve vs auto-hide) ---

    private void ApplyBehavior()
    {
        if (_hwnd == IntPtr.Zero || ViewModel is null)
            return;

        _hideDelayTimer.Stop();
        _slideTimer.Stop();

        if (AutoHide)
        {
            _appBar?.Unregister();
            _revealed = false; // start tucked away; reveal on edge hover
            PositionDock();
        }
        else // AlwaysVisible: reserve a strip so other windows don't overlap the bar
        {
            ReserveAppBarSpace();
            _revealed = true;
            PositionDock();
            _appBar?.NotifyPosChanged();
        }

        ApplyIdleRegion(); // AlwaysVisible → clip to the bar; AutoHide → cleared (sliver stays live)
    }

    private void ReserveAppBarSpace()
    {
        if (_appBar is null || ViewModel is null)
            return;

        double thicknessDip = ViewModel.BarHeight > 0 ? ViewModel.BarHeight : 64;
        var info = Monitors.ForWindow(_hwnd);
        int thicknessPx = (int)Math.Round(thicknessDip * info.Scale);

        _appBar.Register();
        var granted = _appBar.ReserveBottom(info.MonitorPx, thicknessPx);
        _alwaysVisibleBottomDip = (granted.Y + granted.Height) / info.Scale;
    }

    /// <summary>
    /// When the dock is idle in AlwaysVisible mode, clips the window down to the resting bar so the
    /// magnification-overflow area above it is click-through (the AppBar only reserves the resting
    /// bar; the taller window must not block windows underneath). Cleared while hovering so the
    /// magnified icons render above the bar and stay clickable. In AutoHide the whole window stays
    /// hit-testable (the edge sliver triggers the reveal), so no clip is applied.
    /// </summary>
    private void ApplyIdleRegion()
    {
        if (_hwnd == IntPtr.Zero || ViewModel is null)
            return;
        if (AutoHide || _hovering || ViewModel.WindowWidth <= 0 || ViewModel.WindowHeight <= 0)
        {
            ClearWindowRegion();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double topDip = Math.Max(0, ViewModel.BarTop - DockRegionTopPaddingDip);
        int top = (int)Math.Floor(topDip * dpi.DpiScaleY);
        int right = (int)Math.Ceiling(ViewModel.WindowWidth * dpi.DpiScaleX);
        int bottom = (int)Math.Ceiling(ViewModel.WindowHeight * dpi.DpiScaleY) + 1;

        var region = PInvoke.CreateRectRgn(0, top, right, bottom);
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

    private void SetBehavior(DockBehavior behavior)
    {
        if (ViewModel is null || ViewModel.Settings.Behavior == behavior)
            return;
        ViewModel.Settings.Behavior = behavior;
        ViewModel.Save();
        ApplyBehavior();
    }

    private void SetHideTaskbar(bool hide)
    {
        if (ViewModel is null || ViewModel.Settings.HideTaskbar == hide)
            return;
        ViewModel.Settings.HideTaskbar = hide;
        ViewModel.Save();
        ApplyTaskbarVisibility();
        PositionDock(); // bottom reference changed (work area vs full screen)
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
                    if (!AutoHide)
                        ReserveAppBarSpace();
                    PositionDock();
                    handled = true;
                    break;
                case PInvoke.ABN_FULLSCREENAPP:
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    // --- Minimize → dock tile, and tile click → restore (genie or scale per the setting) ---

    /// <summary>The minimize/restore animator selected by <see cref="MinimizeEffect"/>.</summary>
    private IMinimizeAnimator MinimizeAnimator =>
        ViewModel?.Settings.MinimizeEffect == MinimizeEffect.Scale ? _scale : _genie;

    /// <summary>A real window started minimizing: capture it, add a dock tile, and animate into it.</summary>
    private void OnWindowMinimizing(IntPtr hwnd)
    {
        if (ViewModel is null || _busy.Contains(hwnd) || ViewModel.FindMinimizedWindow(hwnd) is not null)
            return;
        _busy.Add(hwnd);

        // Use the capture taken while the window was still visible; capturing now (it's
        // already minimizing) would grab a tiny black sliver.
        var capture = _thumbnails.TryGet(hwnd) ?? WindowCapture.Capture(hwnd);
        WindowControl.SuppressTransitions(hwnd); // future restore won't play the OS animation

        if (capture is null)
        {
            _busy.Remove(hwnd);
            return;
        }

        var info = Monitors.ForWindow(hwnd);
        var sourceDip = ToDip(capture.Value.ScreenRectPx, info.Scale);
        var monitorDip = ToDip(info.MonitorPx, info.Scale);

        // Put the captured image exactly where the window was so the spot is never empty between the
        // real window vanishing and the effect. This also uploads the texture now.
        var animator = MinimizeAnimator;
        animator.ShowAtSource(capture.Value.Bitmap, sourceDip, monitorDip);
        var bitmap = capture.Value.Bitmap;

        // Defer the heavier tile/relayout + animation start until just after a render pass, so the
        // held capture actually paints first (no empty gap), then the warp begins from it.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var tile = ViewModel.AddMinimizedWindow(hwnd, bitmap, TaskbarApps.GetWindowTitle(hwnd));
            LoadOverlayIcon(tile, hwnd);
            if (AutoHide)
                SlideIn(); // reveal so the tile is visible
            animator.AnimateTo(TileScreenCenter(tile), reverse: false, onCompleted: () => _busy.Remove(hwnd));
        });
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
    {
        IntPtr hwnd = tile.Hwnd;
        if (ViewModel is null || _busy.Contains(hwnd))
            return;

        if (!WindowControl.IsWindow(hwnd))
        {
            ViewModel.RemoveMinimizedWindow(tile); // window is gone; drop the stale tile
            return;
        }

        _busy.Add(hwnd);
        WindowControl.SuppressTransitions(hwnd);

        var info = Monitors.ForWindow(hwnd);
        var restoreRectPx = WindowControl.GetRestoreRect(hwnd) ?? new Int32Rect(0, 0, 600, 400);
        var windowDip = ToDip(restoreRectPx, info.Scale);
        var monitorDip = ToDip(info.MonitorPx, info.Scale);
        var tileCenter = TileScreenCenter(tile);

        var bitmap = tile.Icon as System.Windows.Media.Imaging.BitmapSource;
        if (bitmap is null)
        {
            WindowControl.Restore(hwnd);
            ViewModel.RemoveMinimizedWindow(tile);
            _busy.Remove(hwnd);
            return;
        }

        MinimizeAnimator.Play(bitmap, windowDip, tileCenter, monitorDip, reverse: true, onCompleted: () =>
        {
            WindowControl.Restore(hwnd);
            ViewModel.RemoveMinimizedWindow(tile);
            _busy.Remove(hwnd);
        });
    }

    /// <summary>Restores a minimized-window tile without the genie (used for extra windows in a
    /// group click, since only one genie can play at once).</summary>
    private void RestoreMinimizedInstant(DockItemViewModel tile)
    {
        IntPtr hwnd = tile.Hwnd;
        if (ViewModel is null || _busy.Contains(hwnd))
            return;
        if (WindowControl.IsWindow(hwnd))
            WindowControl.Restore(hwnd);
        ViewModel.RemoveMinimizedWindow(tile);
    }

    private static Rect ToDip(Int32Rect r, double scale)
        => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);

    private static Rect ToDip(Rect r, double scale)
        => new(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);

    /// <summary>Screen-space (DIP) center of a tile, using the dock's shown (not hidden) position.</summary>
    private Point TileScreenCenter(DockItemViewModel tile)
    {
        var (left, shownTop, _) = ComputePlacement();
        return new Point(left + tile.X + tile.RenderSize / 2, shownTop + tile.Y + tile.RenderSize / 2);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Suppress magnification while resizing via a separator (icons stay at the new resting size).
        bool animating = ViewModel?.UpdateMagnification(_mouseX, _hovering && !_separatorResize) ?? false;
        UpdateHoverLabel(); // track the hovered icon's live center each frame
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
            _resizeStartCursorY = PointToScreen(e.GetPosition(this)).Y;
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

    // --- Shared hover label ---

    // Persist the hovered item across the small gaps between icons; it's cleared when the cursor
    // leaves the dock (OnMouseLeave) or moves onto another labelled icon.
    private void DockItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DockItemViewModel item } && item.ShowLabel)
            _hoveredItem = item;
    }

    // Positioned every frame from the render loop: centered on the hovered icon's live center, with
    // the arrow just above the icon's fully-magnified top so it never overlaps as magnification grows.
    private void UpdateHoverLabel()
    {
        var item = _hoveredItem;
        if (item is null || _dragInitiated || _separatorResize || ViewModel is null || !item.ShowLabel)
        {
            if (LabelPopup.IsOpen)
                LabelPopup.IsOpen = false;
            _labelItem = null;
            return;
        }

        if (!ReferenceEquals(_labelItem, item))
        {
            LabelText.Text = item.DisplayName;
            _labelItem = item;
        }
        if (!LabelPopup.IsOpen)
            LabelPopup.IsOpen = true;

        // Use the content's real laid-out size (measuring a not-yet-open popup child is unreliable
        // and gave a roughly-constant width — the cause of the off-center, entry-direction-dependent
        // placement). ActualWidth is valid once the popup has laid out; fall back to a measure only
        // for the first frame after opening.
        var content = (FrameworkElement)LabelPopup.Child;
        double w = content.ActualWidth, h = content.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            w = content.DesiredSize.Width;
            h = content.DesiredSize.Height;
        }

        double centerX = item.X + item.RenderWidth / 2;          // hovered icon's live center
        double arrowTipY = ViewModel.MagnifiedTop - LabelGap;    // just above a fully-magnified icon
        LabelPopup.HorizontalOffset = centerX - w / 2;
        LabelPopup.VerticalOffset = arrowTipY - h;
    }

    // The popup is its own top-level window; WS_EX_TRANSPARENT lets the cursor fall through to the
    // icon beneath, so the label never competes for hover (which caused magnify jitter/flicker).
    private void LabelPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: { } child } && PresentationSource.FromVisual(child) is HwndSource source)
        {
            var hwnd = (HWND)source.Handle;
            var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            ex |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)ex);
        }
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
        else
            item.Activate(); // Start tile
    }

    private void UnpinItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DockItemViewModel item } && ViewModel is not null)
        {
            ViewModel.UnpinApp(item.LaunchPath);
            RefreshTaskbarApps();
        }
    }

    // Opened from a separator's right-click menu or the tray menu.
    private void OpenDockPreferences_Click(object sender, RoutedEventArgs e) => OpenDockPreferences();

    /// <summary>Shows the "Dock Preferences" window, focusing it if already open (single instance).</summary>
    private void OpenDockPreferences()
    {
        if (ViewModel is null)
            return;

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(ViewModel, SetTheme);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    // --- Drag to reorder / pin ---

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.None;
        else
            e.Effects = IsOverRecycleBin(e.GetPosition(this).X) ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    // External files dragged from Explorer (internal reorder uses the custom mouse drag above).
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        // Dropped onto the Recycle Bin → move them there; anywhere else → pin them.
        if (IsOverRecycleBin(e.GetPosition(this).X))
        {
            RecycleBin.SendToRecycleBin(paths);
            RefreshTaskbarApps(); // refresh the bin's empty/full icon promptly
            return;
        }

        int index = ComputePinIndex(e.GetPosition(this).X);
        foreach (var path in paths)
            ViewModel.PinApp(path, index++);

        RefreshTaskbarApps();
    }

    /// <summary>True if the given window-X falls within the Recycle Bin tile's column.</summary>
    private bool IsOverRecycleBin(double cursorX)
    {
        var bin = ViewModel?.Items.FirstOrDefault(i => i.IsRecycleBin);
        return bin is not null && cursorX >= bin.X && cursorX <= bin.X + bin.RenderWidth;
    }

    /// <summary>Insertion index among the pinned tiles for a given cursor X (window coords).</summary>
    private int ComputePinIndex(double cursorX)
    {
        var pinned = ViewModel?.Items.Where(i => i.IsTaskbarApp && i.IsPinned).ToList();
        if (pinned is null)
            return 0;
        for (int i = 0; i < pinned.Count; i++)
            if (cursorX < pinned[i].X + pinned[i].RenderSize / 2)
                return i;
        return pinned.Count;
    }

    // --- Tray icon ---

    private void CreateTrayIcon()
    {
        var menu = new ContextMenu();

        var toggle = new MenuItem { Header = "Show / Hide Dock" };
        toggle.Click += (_, _) => ToggleVisibility();
        menu.Items.Add(toggle);

        var autoHideItem = new MenuItem { Header = "Auto-hide", IsCheckable = true };
        autoHideItem.Click += (_, _) =>
            SetBehavior(autoHideItem.IsChecked ? DockBehavior.AutoHide : DockBehavior.AlwaysVisible);
        menu.Items.Add(autoHideItem);

        var hideTaskbarItem = new MenuItem { Header = "Hide taskbar", IsCheckable = true };
        hideTaskbarItem.Click += (_, _) => SetHideTaskbar(hideTaskbarItem.IsChecked);
        menu.Items.Add(hideTaskbarItem);

        // Theme submenu: Light / Dark / Auto (radio-style checks). "Auto" == DockTheme.System.
        var themeItem = new MenuItem { Header = "Theme" };
        var themeLight = new MenuItem { Header = "Light", IsCheckable = true };
        var themeDark = new MenuItem { Header = "Dark", IsCheckable = true };
        var themeAuto = new MenuItem { Header = "Auto", IsCheckable = true };
        themeLight.Click += (_, _) => SetTheme(DockTheme.Light);
        themeDark.Click += (_, _) => SetTheme(DockTheme.Dark);
        themeAuto.Click += (_, _) => SetTheme(DockTheme.System);
        themeItem.Items.Add(themeLight);
        themeItem.Items.Add(themeDark);
        themeItem.Items.Add(themeAuto);
        menu.Items.Add(themeItem);

        var prefsItem = new MenuItem { Header = "Dock Preferences…" };
        prefsItem.Click += (_, _) => OpenDockPreferences();
        menu.Items.Add(prefsItem);

        // Reflect current settings whenever the menu opens.
        menu.Opened += (_, _) =>
        {
            autoHideItem.IsChecked = AutoHide;
            hideTaskbarItem.IsChecked = ViewModel?.Settings.HideTaskbar ?? false;
            var theme = ViewModel?.Settings.Theme ?? DockTheme.System;
            themeLight.IsChecked = theme == DockTheme.Light;
            themeDark.IsChecked = theme == DockTheme.Dark;
            themeAuto.IsChecked = theme == DockTheme.System;
        };

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Dockable",
            ContextMenu = menu,
            // GeneratedIconSource (a BitmapSource) renders the tray glyph in-process,
            // avoiding H.NotifyIcon's URI-only path that rejects arbitrary bitmaps.
            IconSource = new GeneratedIconSource
            {
                Text = "▦", // squared-grid glyph, evoking a dock of tiles
                Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0xC0, 0xF0)),
                Background = Brushes.Transparent,
                FontSize = 28,
                Size = 32,
            },
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ToggleVisibility();
        _trayIcon.ForceCreate();
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
            Hide();
        else
        {
            Show();
            PositionDock();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _slideTimer.Stop();
        _hideDelayTimer.Stop();
        _appRefreshTimer.Stop();
        _pinWatcher?.Dispose();
        _minimizeHook.Dispose();
        _thumbnails.Dispose();
        _appBar?.Unregister(); // release reserved screen space
        Taskbar.Restore(); // restore the taskbar to its pre-launch state
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }
}
