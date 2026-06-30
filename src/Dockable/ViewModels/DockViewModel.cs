using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dockable.Interop;
using Dockable.Localization;
using Dockable.Models;
using Dockable.Services;
using Dockable.Shell;

namespace Dockable.ViewModels;

/// <summary>
/// Top-level view-model. The dock mirrors the Windows taskbar: it shows the Start tile, then the
/// taskbar apps (pinned + running, with a running indicator), then minimized-window tiles after a
/// separator. The item collection is composed from those sections and reconciled in place so live
/// refreshes don't churn the UI.
/// </summary>
public sealed partial class DockViewModel : ObservableObject
{
    /// <summary>Icons are extracted at high resolution so they stay crisp when magnified.</summary>
    private const int IconPixelSize = 256;

    private readonly SettingsStore _store;
    private readonly DockLayoutEngine _layout;

    private DockItemViewModel _startVm = null!;
    private DockItemViewModel _pinSeparatorVm = null!;   // pinned apps | running-unpinned apps
    private DockItemViewModel _separatorVm = null!;       // apps | minimized-window tiles
    private DockItemViewModel _recycleSeparatorVm = null!; // everything | Recycle Bin
    private DockItemViewModel _recycleVm = null!;
    private bool? _recycleEmpty;                          // last-seen state; null forces first load
    private List<DockItemViewModel> _appVms = new();
    private readonly Dictionary<string, DockItemViewModel> _appByKey = new();
    private readonly Dictionary<string, string> _aumidNameCache = new(); // UWP AUMID → friendly app name
    private readonly List<DockItemViewModel> _minimizedVms = new();
    private readonly List<DockItemViewModel> _departing = new(); // apps shrinking out before removal
    private long _appSeq; // monotonic counter stamping each app's first-seen order
    private bool _appsInitialized; // the first refresh records windows without bouncing (apps already open)
    private bool _bounceRequested; // set during a refresh when any app gained a window

    /// <summary>Raised after a refresh in which an app launch bounce began, so the view can start its render loop.</summary>
    public event Action? AnimationRequested;

    public DockViewModel(SettingsStore store)
    {
        _store = store;
        _layout = new DockLayoutEngine(this);
    }

    public ObservableCollection<DockItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private DockSettings _settings = DockSettings.CreateDefault();

    /// <summary>Live mirror of <see cref="DockSettings.ShowRunningIndicators"/>; bound by the dots.</summary>
    [ObservableProperty]
    private bool _showRunningIndicators = true;

    /// <summary>Whether the Dock Preferences window is currently open (drives the Preferences tile's
    /// running dot + whether it appears as a running app when not pinned). Set by the dock; a refresh
    /// reflects it.</summary>
    public bool PreferencesOpen { get; set; }

    /// <summary>The Dock Preferences window's native handle while open (else zero). Tracked on the
    /// Preferences tile so it can minimize into the dock and restore like any other window.</summary>
    public IntPtr PreferencesHwnd { get; set; }

    // --- Geometry driven by the layout engine and consumed by the window/XAML ---

    [ObservableProperty] private double _windowWidth = 200;
    [ObservableProperty] private double _windowHeight = 124;
    [ObservableProperty] private double _barLeft;
    [ObservableProperty] private double _barTop;
    [ObservableProperty] private double _barWidth;
    [ObservableProperty] private double _barHeight;

    /// <summary>Window-Y of the top of a fully-magnified icon (where hover labels anchor above).</summary>
    [ObservableProperty] private double _magnifiedTop;

    /// <summary>True when the dock is on a side edge (Left/Right); items stack vertically.</summary>
    public bool IsVerticalDock => Settings.Edge is DockEdge.Left or DockEdge.Right;

    // --- Edge-derived layout for per-item template bits (running dot, separator), bound via
    // RelativeSource to the window's DataContext. Re-notified on edge change by ApplyEdge. ---

    /// <summary>Gap (DIP) between an icon and its running-indicator dot, on the screen-edge side.</summary>
    private const double DotGap = 10;

    /// <summary>The running dot sits on the screen-edge side of the icon, centered on the other axis.</summary>
    public VerticalAlignment DotVAlign => Settings.Edge switch
    {
        DockEdge.Top => VerticalAlignment.Top,
        DockEdge.Bottom => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Center, // Left / Right
    };

    public HorizontalAlignment DotHAlign => Settings.Edge switch
    {
        DockEdge.Left => HorizontalAlignment.Left,
        DockEdge.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center, // Top / Bottom
    };

    public Thickness DotMargin => Settings.Edge switch
    {
        DockEdge.Top => new Thickness(0, -DotGap, 0, 0),
        DockEdge.Left => new Thickness(-DotGap, 0, 0, 0),
        DockEdge.Right => new Thickness(0, 0, -DotGap, 0),
        _ => new Thickness(0, 0, 0, -DotGap), // Bottom
    };

    /// <summary>Per-icon hover labels are positioned above the icon, which only suits the Bottom edge
    /// (other edges are a TODO); they're suppressed elsewhere.</summary>
    public bool HoverLabelsEnabled => Settings.Edge == DockEdge.Bottom;

    // Separator: a 2px line across the bar — vertical for horizontal docks, horizontal for vertical
    // docks. NaN width/height = Auto, which stretches under the matching Stretch alignment.
    public double SeparatorWidth => IsVerticalDock ? double.NaN : 2;
    public double SeparatorHeight => IsVerticalDock ? 2 : double.NaN;
    public HorizontalAlignment SeparatorHAlign => IsVerticalDock ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
    public VerticalAlignment SeparatorVAlign => IsVerticalDock ? VerticalAlignment.Center : VerticalAlignment.Stretch;

    /// <summary>Persists a new dock edge, re-lays out, and notifies the edge-derived view bindings.</summary>
    public void ApplyEdge(DockEdge edge)
    {
        Settings.Edge = edge;
        Save();
        RecomputeLayout();
        OnPropertyChanged(nameof(IsVerticalDock));
        OnPropertyChanged(nameof(HoverLabelsEnabled));
        OnPropertyChanged(nameof(DotVAlign));
        OnPropertyChanged(nameof(DotHAlign));
        OnPropertyChanged(nameof(DotMargin));
        OnPropertyChanged(nameof(SeparatorWidth));
        OnPropertyChanged(nameof(SeparatorHeight));
        OnPropertyChanged(nameof(SeparatorHAlign));
        OnPropertyChanged(nameof(SeparatorVAlign));
    }

    public void RecomputeLayout() => _layout.Recompute();

    /// <summary><paramref name="mouseMain"/> is the cursor's main-axis coordinate (window X for a
    /// horizontal dock, window Y for a vertical one).</summary>
    public bool UpdateMagnification(double mouseMain, bool hovering) => _layout.Update(mouseMain, hovering);

    // --- Live drag-reorder (driven by DockWindow's mouse capture) ---
    public void BeginItemDrag(DockItemViewModel item) => _layout.BeginDrag(item);
    public void EndItemDrag() => _layout.EndDrag();
    public int DragInsertIndex => _layout.DragInsertIndex;

    // --- External (Explorer) file drag: a placeholder gap previews where a drop would land ---
    public void UpdateExternalDrop(double mouseMain) => _layout.UpdateExternalDrop(mouseMain);
    public void EndExternalDrop() => _layout.EndExternalDrop();
    public int ComputeDropIndex(double mouseMain) => _layout.ComputeDropIndex(mouseMain);

    /// <summary>Toggles the running-indicator dots (updates the live binding + persists).</summary>
    public void SetShowRunningIndicators(bool show)
    {
        if (Settings.ShowRunningIndicators == show)
            return;
        Settings.ShowRunningIndicators = show;
        ShowRunningIndicators = show; // drives the dot visibility bindings
        Save();
    }

    public void Load()
    {
        Settings = _store.Load();
        ShowRunningIndicators = Settings.ShowRunningIndicators;

        // First run: seed the dock's pin list from the current taskbar order, resolving each .lnk to
        // its actual target so we pin the destination (exe), not the shortcut file. Afterwards the
        // dock owns it (reorder / pin / unpin are persisted and don't touch the taskbar). On this
        // first launch we replicate silently — the known-pins baseline is set to the same set so the
        // "new pin?" prompt only fires for shortcuts pinned to the taskbar *after* first run.
        if (Settings.PinnedApps is null)
        {
            RecordNamesForTaskbarPins(); // remember each .lnk's name before it's resolved to a target
            var seeded = TaskbarApps.GetPinnedOrder()
                .Select(TaskbarApps.ResolveToTarget)
                .ToList();
            Settings.PinnedApps = seeded;
            Settings.KnownTaskbarPins ??= seeded
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Save();
        }

        // Seed the built-in Dock Preferences pin once, to the right of the taskbar-seeded pins. The
        // flag makes removal stick (once the user unpins it, we never re-add it) and also back-fills
        // it for installs whose pin list was seeded before this feature existed.
        if (!Settings.SeededPreferencesPin)
        {
            var pins = Settings.PinnedApps ??= new List<string>();
            if (!pins.Contains(DockItem.PreferencesLaunchPath, StringComparer.OrdinalIgnoreCase))
                pins.Add(DockItem.PreferencesLaunchPath);
            Settings.SeededPreferencesPin = true;
            Save();
        }

        _startVm = new DockItemViewModel(DockItem.CreateStartMenu());
        _pinSeparatorVm = new DockItemViewModel(DockItem.CreateSeparator("separator-pinned"));
        _separatorVm = new DockItemViewModel(DockItem.CreateSeparator("separator-minimized"));
        _recycleSeparatorVm = new DockItemViewModel(DockItem.CreateSeparator("separator-recycle"));
        _recycleVm = new DockItemViewModel(DockItem.CreateRecycleBin());
        RefreshRecycleBin(); // initial state + icon
        ComposeItems();
        RecomputeLayout();
    }

    public void Save() => _store.Save(Settings);

    // --- Taskbar mirror -----------------------------------------------------------------

    /// <summary>
    /// Rebuilds the taskbar-app section from the current pinned shortcuts and running windows.
    /// Reuses existing tile view-models by key so icons and magnification state survive refreshes;
    /// only updates the item collection when the set or order actually changes.
    /// </summary>
    public void RefreshTaskbarApps(uint ownProcessId)
    {
        _bounceRequested = false;

        var pinnedPaths = Settings.PinnedApps ?? new List<string>();
        var windows = TaskbarApps.EnumerateAppWindows(ownProcessId);
        var claimed = new bool[windows.Count];

        var desired = new List<DockItemViewModel>();

        // Pinned apps first, in the dock's own order. Each claims its matching windows.
        foreach (var path in pinnedPaths)
        {
            // The built-in Dock Preferences pin has no external window to match — it's our own window.
            if (string.Equals(path, DockItem.PreferencesLaunchPath, StringComparison.OrdinalIgnoreCase))
            {
                desired.Add(UpdatePreferencesApp(isPinned: true));
                continue;
            }

            var pin = PinMatcher.For(path);
            var handles = new List<IntPtr>();
            for (int i = 0; i < windows.Count; i++)
            {
                if (claimed[i] || !pin.Matches(windows[i]))
                    continue;
                claimed[i] = true;
                handles.Add(windows[i].Hwnd);
            }
            var vm = UpdateApp(pin.Key, PinDisplayName(path), path, handles.Count > 0 ? handles : null);
            vm.IsPinned = true;
            desired.Add(vm);
        }

        // Remaining (unclaimed) windows, grouped by app identity (UWP AUMID / exe / window), appended
        // as unpinned apps — so UWP apps show their real name+icon and elevated apps still appear.
        var groups = new Dictionary<string, (string Name, string LaunchPath, List<IntPtr> Handles)>();
        var groupOrder = new List<string>();
        for (int i = 0; i < windows.Count; i++)
        {
            if (claimed[i])
                continue;
            var (key, name, launchPath) = IdentifyWindow(windows[i]);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (name, launchPath, new List<IntPtr>());
                groups[key] = group;
                groupOrder.Add(key);
            }
            group.Handles.Add(windows[i].Hwnd);
        }
        // Order unpinned apps by when they were first seen (≈ open order), not by Z-order/focus,
        // so the row stays stable as the user focuses different windows.
        var unpinned = new List<DockItemViewModel>();
        foreach (var key in groupOrder)
        {
            var group = groups[key];
            var vm = UpdateApp(key, group.Name, group.LaunchPath, group.Handles);
            vm.IsPinned = false;
            unpinned.Add(vm);
        }
        unpinned.Sort((a, b) => a.SeenOrder.CompareTo(b.SeenOrder));
        desired.AddRange(unpinned);

        // The Dock Preferences window, when open but not pinned, shows as a running (unpinned) app so
        // the user can refocus or quit it from the dock.
        if (PreferencesOpen
            && !pinnedPaths.Contains(DockItem.PreferencesLaunchPath, StringComparer.OrdinalIgnoreCase)
            && !desired.Exists(d => d.IsPreferences))
            desired.Add(UpdatePreferencesApp(isPinned: false));

        // Apps that disappeared this refresh fade out (shrink-out) instead of vanishing and snapping the
        // row's width. They stay in the layout (kept roughly in place) until the shrink finishes, then
        // FinalizeDeparted removes them.
        var liveKeys = desired.Select(d => d.AppKey).ToHashSet();
        var previousOrder = _appVms; // before reassignment, to keep a departing icon near its old slot
        foreach (var vm in _appByKey.Values)
        {
            if (liveKeys.Contains(vm.AppKey) || vm.Departing)
                continue;
            vm.Departing = true;
            _departing.Add(vm);
        }
        foreach (var vm in _departing)
        {
            if (!vm.IsTaskbarApp || desired.Contains(vm))
                continue;
            int at = previousOrder.IndexOf(vm);
            desired.Insert(at >= 0 && at <= desired.Count ? at : desired.Count, vm);
        }

        _appVms = desired;
        RefreshRecycleBin();
        ComposeItems();

        // After the first refresh, apps already open are the baseline; subsequent new windows bounce.
        _appsInitialized = true;
        if (_bounceRequested)
            AnimationRequested?.Invoke();
    }

    /// <summary>
    /// Reloads the Recycle Bin icon when its empty/full state flips (and on first run). The shell
    /// hands back the state-appropriate icon, so we only need to detect the transition.
    /// </summary>
    private void RefreshRecycleBin()
    {
        bool empty = Interop.RecycleBin.IsEmpty();
        if (_recycleEmpty == empty)
            return;
        _recycleEmpty = empty;
        _ = _recycleVm.LoadIconAsync(IconPixelSize);
    }

    private DockItemViewModel UpdateApp(string key, string name, string launchPath, List<IntPtr>? windows)
    {
        bool isNew = !_appByKey.TryGetValue(key, out var vm);
        if (isNew)
        {
            var model = DockItem.CreateTaskbarApp(name);
            model.TargetPath = launchPath; // used for icon extraction
            vm = new DockItemViewModel(model) { AppKey = key, LaunchPath = launchPath, SeenOrder = _appSeq++ };
            vm.AppearScale = _appsInitialized ? 0.0 : 1.0; // grow in (but not for apps already open at startup)
            _appByKey[key] = vm;
        }
        else if (vm!.Departing)
        {
            // The app came back before its shrink-out finished — cancel the departure (it eases back in).
            vm.Departing = false;
            _departing.Remove(vm);
        }

        vm!.LaunchPath = launchPath;
        var handles = windows ?? (IReadOnlyList<IntPtr>)Array.Empty<IntPtr>();

        // Bounce the icon when the app gains a window it didn't have last refresh (e.g. on launch).
        // Skipped on the first refresh (apps already open shouldn't all bounce at startup).
        if (_appsInitialized && Settings.AnimateOpeningApps)
        {
            foreach (var h in handles)
            {
                if (vm.HasWindow(h))
                    continue;
                vm.StartBounce();
                _bounceRequested = true;
                break;
            }
        }
        vm.SetKnownWindows(handles);

        vm.Windows = handles is IntPtr[] arr ? arr : handles.ToArray();
        vm.IsRunning = vm.Windows.Count > 0;

        // Load the icon after Windows is set, so apps with no readable exe (launchPath empty) can fall
        // back to their window's own icon.
        if (isNew)
            _ = vm.LoadIconAsync(IconPixelSize);
        return vm;
    }

    /// <summary>
    /// Creates/updates the built-in "Dock Preferences" tile — a pseudo taskbar app backed by the
    /// dock's own Preferences window rather than an external process. Its icon is the bundled System
    /// Preferences glyph; it reads as "running" while the window is open. The dock special-cases its
    /// click (open/focus the window) and its context menu (Keep in Dock / Quit).
    /// </summary>
    private DockItemViewModel UpdatePreferencesApp(bool isPinned)
    {
        const string key = "dockable:preferences";
        string name = Loc.T("Window_DockPreferences");
        if (!_appByKey.TryGetValue(key, out var vm))
        {
            var model = DockItem.CreateTaskbarApp(name);
            vm = new DockItemViewModel(model)
            {
                AppKey = key,
                LaunchPath = DockItem.PreferencesLaunchPath,
                SeenOrder = _appSeq++,
                Icon = global::Dockable.AppIcon.Preferences,
            };
            vm.AppearScale = _appsInitialized ? 0.0 : 1.0; // grow in like any other tile
            _appByKey[key] = vm;
        }
        else if (vm.Departing)
        {
            vm.Departing = false;
            _departing.Remove(vm);
        }

        vm.DisplayName = name;                 // keep the label localized live
        vm.LaunchPath = DockItem.PreferencesLaunchPath;
        vm.Icon ??= global::Dockable.AppIcon.Preferences;
        vm.IsPinned = isPinned;
        // Track the real window handle while open, so the tile can minimize into the dock and restore
        // through the same machinery as any app (FindAppForWindow, into-icon, click-to-restore).
        vm.Windows = PreferencesHwnd != IntPtr.Zero ? new[] { PreferencesHwnd } : Array.Empty<IntPtr>();
        vm.IsRunning = PreferencesOpen;
        return vm;
    }

    /// <summary>
    /// Derives a running window's app identity the way the taskbar does: packaged (UWP/Store) apps —
    /// including those hosted by ApplicationFrameHost — are keyed by their AppUserModelID with the
    /// name/icon coming from the AppsFolder shell item; normal apps by their executable; and apps whose
    /// exe we can't read (elevated, e.g. Task Manager) by their window (title + the window's own icon).
    /// </summary>
    private (string Key, string Name, string LaunchPath) IdentifyWindow(TaskbarApps.RunningWindow w)
    {
        if (TaskbarApps.IsPackagedAumid(w.Aumid))
        {
            string launchPath = $"shell:AppsFolder\\{w.Aumid}";
            return ("uwp:" + w.Aumid.ToLowerInvariant(), AumidDisplayName(w.Aumid, w.Title), launchPath);
        }
        if (!string.IsNullOrEmpty(w.ExePath))
            return (w.ExePath.ToLowerInvariant(), SafeName(w.ExePath), w.ExePath);

        string id = string.IsNullOrEmpty(w.Aumid) ? w.Title : w.Aumid;
        return ("win:" + id.ToLowerInvariant(), w.Title, string.Empty); // empty launch path → window icon
    }

    /// <summary>The AppsFolder display name for an AUMID (cached); falls back to the window title.</summary>
    private string AumidDisplayName(string aumid, string fallbackTitle)
    {
        if (_aumidNameCache.TryGetValue(aumid, out string? cached))
            return cached;
        string? name = ShortcutService.GetShellDisplayName($"shell:AppsFolder\\{aumid}");
        if (!string.IsNullOrWhiteSpace(name))
        {
            _aumidNameCache[aumid] = name;
            return name;
        }
        return string.IsNullOrWhiteSpace(fallbackTitle) ? aumid : fallbackTitle;
    }

    // --- Dock-owned pin mutations (persisted; do not touch the Windows taskbar) ---

    /// <summary>Moves an already-pinned app to a new index among the pinned apps.</summary>
    public void MovePin(string launchPath, int index)
    {
        var list = Settings.PinnedApps ??= new List<string>();
        int current = list.FindIndex(p => string.Equals(p, launchPath, StringComparison.OrdinalIgnoreCase));
        if (current < 0)
            return;
        list.RemoveAt(current);
        list.Insert(Math.Clamp(index, 0, list.Count), launchPath);
        Save();
    }

    /// <summary>Pins an app/file at the given index (no-op if already pinned at that spot).
    /// <paramref name="displayName"/> is the label to remember for the pin (e.g. the dropped app's open
    /// name); when empty it's derived from the path.</summary>
    public void PinApp(string launchPath, int index, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(launchPath))
            return;
        RecordPinName(launchPath, displayName);
        var list = Settings.PinnedApps ??= new List<string>();
        int current = list.FindIndex(p => string.Equals(p, launchPath, StringComparison.OrdinalIgnoreCase));
        if (current >= 0)
            list.RemoveAt(current);
        list.Insert(Math.Clamp(index, 0, list.Count), launchPath);
        Save();
    }

    /// <summary>Remembers a friendly display name for a pinned launch path (ignored when blank).</summary>
    public void RecordPinName(string launchPath, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(launchPath) || string.IsNullOrWhiteSpace(displayName))
            return;
        (Settings.PinNames ??= new Dictionary<string, string>())[launchPath] = displayName;
    }

    /// <summary>The label for a pinned launch path: the remembered name if any, else derived from the path.</summary>
    public string PinDisplayName(string launchPath)
    {
        if (Settings.PinNames is { } names)
            foreach (var kv in names)
                if (string.Equals(kv.Key, launchPath, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
        return DerivePinName(launchPath);
    }

    /// <summary>Best-effort name from a launch path alone: an AppsFolder app's shell name, else the file
    /// name (without extension, case preserved — so a resolved <c>chrome.exe</c> still reads "chrome"
    /// only when no original shortcut name was captured).</summary>
    private static string DerivePinName(string launchPath)
    {
        if (launchPath.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
        {
            string? shellName = ShortcutService.GetShellDisplayName(launchPath);
            if (!string.IsNullOrWhiteSpace(shellName))
                return shellName;
        }
        return Path.GetFileNameWithoutExtension(launchPath);
    }

    /// <summary>The friendly name for a taskbar pin <em>source</em> (a .lnk's file name, or an
    /// AppsFolder app's shell name) — captured before resolving the .lnk to its target.</summary>
    private static string SourcePinName(string source)
    {
        if (source.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            return DerivePinName(source);
        return Path.GetFileNameWithoutExtension(source); // "Chrome.lnk" → "Chrome" (case preserved)
    }

    /// <summary>Records friendly names for the current taskbar pins (keyed by their resolved target), so a
    /// resolved <c>chrome.exe</c> pin still shows "Chrome". Called when seeding / replicating taskbar pins.</summary>
    private void RecordNamesForTaskbarPins()
    {
        foreach (var source in TaskbarApps.GetPinnedOrder())
        {
            string target = TaskbarApps.ResolveToTarget(source);
            if (!string.IsNullOrWhiteSpace(target))
                RecordPinName(target, SourcePinName(source));
        }
    }

    // --- Taskbar-pin replication (offer to mirror newly-pinned taskbar shortcuts) ---

    /// <summary>
    /// Taskbar pins (resolved to their targets) that have appeared since we last looked. On the very
    /// first call it just records the current set as the baseline and returns none, so we don't offer
    /// to replicate everything the user already had pinned.
    /// </summary>
    public IReadOnlyList<string> FindNewTaskbarPins()
    {
        var current = TaskbarApps.GetPinnedOrder()
            .Select(TaskbarApps.ResolveToTarget)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (Settings.KnownTaskbarPins is null)
        {
            Settings.KnownTaskbarPins = current;
            Save();
            return Array.Empty<string>();
        }

        var known = new HashSet<string>(Settings.KnownTaskbarPins, StringComparer.OrdinalIgnoreCase);
        return current.Where(p => !known.Contains(p)).ToList();
    }

    /// <summary>Records pins as "seen" so they don't prompt again, regardless of the user's choice.</summary>
    public void RememberTaskbarPins(IEnumerable<string> pins)
    {
        var known = Settings.KnownTaskbarPins ??= new List<string>();
        foreach (var p in pins)
            if (!known.Contains(p, StringComparer.OrdinalIgnoreCase))
                known.Add(p);
        Save();
    }

    /// <summary>Adds the given taskbar pins to the dock (appended) and marks them as seen.</summary>
    public void ReplicateTaskbarPins(IReadOnlyList<string> pins)
    {
        RecordNamesForTaskbarPins(); // capture each .lnk's name for its resolved target
        var list = Settings.PinnedApps ??= new List<string>();
        foreach (var p in pins)
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase))
                list.Add(p);
        RememberTaskbarPins(pins); // also saves
    }

    public void UnpinApp(string launchPath)
    {
        var list = Settings.PinnedApps;
        if (list is null)
            return;
        if (list.RemoveAll(p => string.Equals(p, launchPath, StringComparison.OrdinalIgnoreCase)) > 0)
            Save();
    }

    /// <summary>Renames a pinned shortcut's display label: persists the new name and updates the live tile.</summary>
    public void RenamePin(string launchPath, string newName)
    {
        newName = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(newName))
            return;
        RecordPinName(launchPath, newName);
        var vm = _appByKey.Values.FirstOrDefault(
            a => string.Equals(a.LaunchPath, launchPath, StringComparison.OrdinalIgnoreCase));
        if (vm is not null)
            vm.DisplayName = newName;
        Save();
    }

    private static string SafeName(string exePath)
    {
        try { return Path.GetFileNameWithoutExtension(exePath); }
        catch { return exePath; }
    }

    // --- Minimized-window tiles ---------------------------------------------------------

    public DockItemViewModel AddMinimizedWindow(IntPtr hwnd, ImageSource? thumbnail, string title)
    {
        var vm = new DockItemViewModel(DockItem.CreateMinimizedWindow(title)) { Hwnd = hwnd, Icon = thumbnail };
        _minimizedVms.Add(vm);
        ComposeItems();
        return vm;
    }

    public void RemoveMinimizedWindow(DockItemViewModel item)
    {
        if (_minimizedVms.Remove(item))
            ComposeItems();
    }

    public DockItemViewModel? FindMinimizedWindow(IntPtr hwnd)
        => _minimizedVms.FirstOrDefault(i => i.Hwnd == hwnd);

    /// <summary>The current minimized-window tiles (snapshot), e.g. to restore them all on exit.</summary>
    public IReadOnlyList<DockItemViewModel> MinimizedWindows => _minimizedVms.ToList();

    /// <summary>The taskbar-app tile (pinned or running) whose window list contains <paramref name="hwnd"/>.</summary>
    public DockItemViewModel? FindAppForWindow(IntPtr hwnd)
        => _appVms.FirstOrDefault(a => a.Windows.Contains(hwnd));

    // --- Item composition / reconciliation ----------------------------------------------

    private void ComposeItems()
    {
        var desired = new List<DockItemViewModel>(2 + _appVms.Count + 1 + _minimizedVms.Count) { _startVm };

        // Apps are ordered pinned-first, then running-unpinned. Insert a separator at the
        // boundary, but only when both groups are non-empty.
        int firstUnpinned = _appVms.FindIndex(a => !a.IsPinned);
        for (int i = 0; i < _appVms.Count; i++)
        {
            if (i == firstUnpinned && firstUnpinned > 0)
                desired.Add(_pinSeparatorVm);
            desired.Add(_appVms[i]);
        }

        if (_minimizedVms.Count > 0)
        {
            desired.Add(_separatorVm);
            desired.AddRange(_minimizedVms);
        }

        // The Recycle Bin is always pinned to the far right. Give it its own separator only when
        // minimized tiles aren't already the rightmost group (no separator right of minimized).
        if (_minimizedVms.Count == 0)
            desired.Add(_recycleSeparatorVm);
        desired.Add(_recycleVm);

        if (ReconcileItems(desired))
            RecomputeLayout();

        // If anything is mid grow-in / shrink-out, make sure the render loop runs to animate it.
        foreach (var item in desired)
            if (item.Departing || Math.Abs(item.AppearScale - 1.0) > 0.01)
            {
                AnimationRequested?.Invoke();
                break;
            }
    }

    /// <summary>True once a departing item has shrunk to nothing and is ready to be removed.</summary>
    public bool HasFinishedDeparting => _departing.Exists(d => d.AppearScale < 0.02);

    /// <summary>Removes items whose shrink-out has finished, then recomposes. Driven from the render loop.</summary>
    public void FinalizeDeparted()
    {
        if (_departing.Count == 0)
            return;
        var done = _departing.Where(d => d.AppearScale < 0.02).ToList();
        if (done.Count == 0)
            return;
        foreach (var d in done)
        {
            _departing.Remove(d);
            d.Departing = false;
            _appByKey.Remove(d.AppKey);
            _appVms.Remove(d);
        }
        ComposeItems();
    }

    /// <summary>Updates <see cref="Items"/> to match <paramref name="desired"/> with minimal changes.</summary>
    private bool ReconcileItems(List<DockItemViewModel> desired)
    {
        if (Items.Count == desired.Count)
        {
            bool identical = true;
            for (int i = 0; i < desired.Count; i++)
                if (!ReferenceEquals(Items[i], desired[i])) { identical = false; break; }
            if (identical)
                return false;
        }

        var desiredSet = desired.ToHashSet();
        for (int i = Items.Count - 1; i >= 0; i--)
            if (!desiredSet.Contains(Items[i]))
                Items.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], desired[i]))
                continue;
            int existing = Items.IndexOf(desired[i]);
            if (existing >= 0)
                Items.Move(existing, i);
            else
                Items.Insert(i, desired[i]);
        }
        return true;
    }
}
