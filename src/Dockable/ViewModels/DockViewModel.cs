using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dockable.Interop;
using Dockable.Models;
using Dockable.Services;

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
    private readonly List<DockItemViewModel> _minimizedVms = new();
    private long _appSeq; // monotonic counter stamping each app's first-seen order

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

    // --- Geometry driven by the layout engine and consumed by the window/XAML ---

    [ObservableProperty] private double _windowWidth = 200;
    [ObservableProperty] private double _windowHeight = 124;
    [ObservableProperty] private double _barLeft;
    [ObservableProperty] private double _barTop;
    [ObservableProperty] private double _barWidth;
    [ObservableProperty] private double _barHeight;

    /// <summary>Window-Y of the top of a fully-magnified icon (where hover labels anchor above).</summary>
    [ObservableProperty] private double _magnifiedTop;

    public void RecomputeLayout() => _layout.Recompute();
    public bool UpdateMagnification(double mouseX, bool hovering) => _layout.Update(mouseX, hovering);

    // --- Live drag-reorder (driven by DockWindow's mouse capture) ---
    public void BeginItemDrag(DockItemViewModel item) => _layout.BeginDrag(item);
    public void EndItemDrag() => _layout.EndDrag();
    public int DragInsertIndex => _layout.DragInsertIndex;

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

        // First run: seed the dock's pin list from the current taskbar order. Afterwards the
        // dock owns it (reorder / pin / unpin are persisted and don't touch the taskbar).
        if (Settings.PinnedApps is null)
        {
            Settings.PinnedApps = TaskbarApps.GetPinnedOrder().ToList();
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
        var pinnedPaths = Settings.PinnedApps ?? new List<string>();
        var windows = TaskbarApps.EnumerateAppWindows(ownProcessId);
        var claimed = new bool[windows.Count];

        var desired = new List<DockItemViewModel>();

        // Pinned apps first, in the dock's own order. Each claims its matching windows.
        foreach (var path in pinnedPaths)
        {
            var pin = PinMatcher.For(path);
            var handles = new List<IntPtr>();
            for (int i = 0; i < windows.Count; i++)
            {
                if (claimed[i] || !pin.Matches(windows[i]))
                    continue;
                claimed[i] = true;
                handles.Add(windows[i].Hwnd);
            }
            var vm = UpdateApp(pin.Key, Path.GetFileNameWithoutExtension(path), path, handles.Count > 0 ? handles : null);
            vm.IsPinned = true;
            desired.Add(vm);
        }

        // Remaining (unclaimed) windows, grouped by exe, appended as unpinned apps.
        var byExe = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < windows.Count; i++)
        {
            if (claimed[i])
                continue;
            if (!byExe.TryGetValue(windows[i].ExePath, out var handles))
                byExe[windows[i].ExePath] = handles = new List<IntPtr>();
            handles.Add(windows[i].Hwnd);
        }
        // Order unpinned apps by when they were first seen (≈ open order), not by Z-order/focus,
        // so the row stays stable as the user focuses different windows.
        var unpinned = new List<DockItemViewModel>();
        foreach (var (exe, handles) in byExe)
        {
            var vm = UpdateApp(exe.ToLowerInvariant(), SafeName(exe), exe, handles);
            vm.IsPinned = false;
            unpinned.Add(vm);
        }
        unpinned.Sort((a, b) => a.SeenOrder.CompareTo(b.SeenOrder));
        desired.AddRange(unpinned);

        // Drop view-models for apps that disappeared.
        var liveKeys = desired.Select(d => d.AppKey).ToHashSet();
        foreach (var key in _appByKey.Keys.Where(k => !liveKeys.Contains(k)).ToList())
            _appByKey.Remove(key);

        _appVms = desired;
        RefreshRecycleBin();
        ComposeItems();
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
        if (!_appByKey.TryGetValue(key, out var vm))
        {
            var model = DockItem.CreateTaskbarApp(name);
            model.TargetPath = launchPath; // used for icon extraction
            vm = new DockItemViewModel(model) { AppKey = key, LaunchPath = launchPath, SeenOrder = _appSeq++ };
            _appByKey[key] = vm;
            _ = vm.LoadIconAsync(IconPixelSize);
        }

        vm.LaunchPath = launchPath;
        vm.Windows = windows is null ? Array.Empty<IntPtr>() : windows.ToArray();
        vm.IsRunning = vm.Windows.Count > 0;
        return vm;
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

    /// <summary>Pins an app/file at the given index (no-op if already pinned at that spot).</summary>
    public void PinApp(string launchPath, int index)
    {
        if (string.IsNullOrWhiteSpace(launchPath))
            return;
        var list = Settings.PinnedApps ??= new List<string>();
        int current = list.FindIndex(p => string.Equals(p, launchPath, StringComparison.OrdinalIgnoreCase));
        if (current >= 0)
            list.RemoveAt(current);
        list.Insert(Math.Clamp(index, 0, list.Count), launchPath);
        Save();
    }

    public void UnpinApp(string launchPath)
    {
        var list = Settings.PinnedApps;
        if (list is null)
            return;
        if (list.RemoveAll(p => string.Equals(p, launchPath, StringComparison.OrdinalIgnoreCase)) > 0)
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
