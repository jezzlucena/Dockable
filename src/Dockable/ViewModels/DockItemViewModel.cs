using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dockable.Interop;
using Dockable.Models;
using Dockable.Shell;

namespace Dockable.ViewModels;

/// <summary>
/// Runtime view-model for a single dock entry. Wraps the persisted
/// <see cref="DockItem"/> and adds transient state: the loaded icon plus the
/// animation values that drive magnification (Phase 2).
/// </summary>
public sealed partial class DockItemViewModel : ObservableObject
{
    public DockItem Model { get; }

    public DockItemViewModel(DockItem model)
    {
        Model = model;
        _displayName = string.IsNullOrWhiteSpace(model.DisplayName)
            ? DeriveDisplayName(model)
            : model.DisplayName;
    }

    public DockItemKind Kind => Model.Kind;
    public bool IsStartMenu => Model.Kind == DockItemKind.StartMenu;
    public bool IsSeparator => Model.Kind == DockItemKind.Separator;
    public bool IsRemovable => Model.Kind == DockItemKind.Shortcut;
    public bool IsMinimizedWindow => Model.Kind == DockItemKind.MinimizedWindow;
    public bool IsTaskbarApp => Model.Kind == DockItemKind.TaskbarApp;
    public bool IsRecycleBin => Model.Kind == DockItemKind.RecycleBin;
    public bool IsPinnedFolder => Model.Kind == DockItemKind.PinnedFolder;
    public bool IsPinnedFile => Model.Kind == DockItemKind.PinnedFile;

    /// <summary>True for a pinned file or folder tile (the dock's macOS-style right section).</summary>
    public bool IsPinnedPath => IsPinnedFolder || IsPinnedFile;

    /// <summary>The persisted pin behind a <see cref="IsPinnedPath"/> tile (path + folder options).</summary>
    public PinnedPath? PathModel { get; set; }

    /// <summary>The folder's last-write time when its stack icon was last composed; the periodic
    /// refresh recomposes the stack when this drifts (direct children added/removed/renamed).</summary>
    public DateTime PathStamp { get; set; }

    /// <summary>Whether this taskbar app is pinned in the dock (vs. only running).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnpin))]
    private bool _isPinned;

    /// <summary>Right-click "Unpin" applies only to pinned taskbar apps.</summary>
    public bool CanUnpin => IsTaskbarApp && IsPinned;

    /// <summary>True for items that show an icon/thumbnail (and a loading fallback). The Start
    /// tile joins in only while a custom icon is loaded — otherwise it renders its vector glyph.</summary>
    public bool ShowIconArea =>
        Model.Kind is DockItemKind.Shortcut or DockItemKind.MinimizedWindow
            or DockItemKind.TaskbarApp or DockItemKind.RecycleBin
            or DockItemKind.PinnedFolder or DockItemKind.PinnedFile
        || (IsStartMenu && Icon is not null);

    /// <summary>Whether the Start tile's built-in launcher glyph shows (hidden while a user-chosen
    /// custom icon is loaded into <see cref="Icon"/>).</summary>
    public bool ShowStartGlyph => IsStartMenu && Icon is null;

    /// <summary>Native window handle for <see cref="DockItemKind.MinimizedWindow"/> tiles.</summary>
    public IntPtr Hwnd { get; set; }

    // --- Taskbar-app state (Kind == TaskbarApp) ---

    /// <summary>Stable key for reuse across refreshes (target exe path, or .lnk path for UWP).</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>Monotonic order in which this app was first seen; orders unpinned apps by open time.</summary>
    public long SeenOrder { get; set; }

    /// <summary>What to launch when the app has no open windows (the .lnk or exe path).</summary>
    public string LaunchPath { get; set; } = string.Empty;

    /// <summary>Full path of a user-chosen replacement icon (imported into the AppData icon
    /// cache), or null to extract the icon from the target. Set by <see cref="DockViewModel"/>.</summary>
    public string? CustomIconPath { get; set; }

    /// <summary>True for the built-in Dock Preferences tile (opens the dock's own window, not a shell launch).</summary>
    public bool IsPreferences => string.Equals(LaunchPath, DockItem.PreferencesLaunchPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Handles of this app's open windows (empty when not running).</summary>
    public IReadOnlyList<IntPtr> Windows { get; set; } = Array.Empty<IntPtr>();

    /// <summary>Whether a running indicator (dot) should show under the tile.</summary>
    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLabel))]
    private string _displayName;

    /// <summary>Whether a hover label should show: any titled, non-separator item.</summary>
    public bool ShowLabel => !IsSeparator && !string.IsNullOrWhiteSpace(DisplayName);

    /// <summary>The loaded icon, or null until <see cref="Services.IconLoader"/> populates it.
    /// The Start tile's glyph/icon swap tracks it, hence the two extra notifications.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIconArea))]
    [NotifyPropertyChangedFor(nameof(ShowStartGlyph))]
    private ImageSource? _icon;

    /// <summary>
    /// App icon overlaid on the bottom-right of a minimized-window thumbnail so the user can
    /// tell which app a tile belongs to. Null until loaded (and for non-minimized tiles).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlayIcon))]
    private ImageSource? _overlayIcon;

    /// <summary>Whether the app-icon overlay should render (a minimized tile with a loaded icon).</summary>
    public bool ShowOverlayIcon => IsMinimizedWindow && OverlayIcon is not null;

    // --- Magnification state (driven per-frame by the layout engine) ---

    /// <summary>Current rendered height in DIPs; animates between base and max icon size.</summary>
    [ObservableProperty]
    private double _renderSize;

    /// <summary>Current rendered width in DIPs. Equals <see cref="RenderSize"/> for icons; narrower for separators.</summary>
    [ObservableProperty]
    private double _renderWidth;

    /// <summary>Canvas X (left) of the item in window coordinates.</summary>
    [ObservableProperty]
    private double _x;

    /// <summary>Canvas Y (top) of the item in window coordinates (bottom-anchored growth).</summary>
    [ObservableProperty]
    private double _y;

    /// <summary>Smoothed magnification scale (1 = resting). Plain field; not bound.</summary>
    public double CurrentScale { get; set; } = 1.0;

    /// <summary>Appearance scale (0..1): eases 0→1 as an item is added and 1→0 as it's removed, so the
    /// dock's width grows/shrinks smoothly instead of jumping. Plain field; driven by the layout engine.</summary>
    public double AppearScale { get; set; } = 1.0;

    /// <summary>True while the item is animating out; it's removed from the dock once it reaches scale 0.</summary>
    public bool Departing { get; set; }

    // --- Launch bounce (driven per-frame by the layout engine) ---

    /// <summary>Window handles seen at the last refresh, to detect a newly-opened window.</summary>
    private readonly HashSet<IntPtr> _knownWindows = new();

    /// <summary>UTC ticks the current bounce started; 0 when not bouncing.</summary>
    private long _bounceStartTicks;

    /// <summary>How many hops the current bounce plays (1 = launch, 3 = attention request).</summary>
    private int _bounceHops = 1;

    /// <summary>Current bounce lift in DIPs (0 at rest); the engine maps it into <see cref="BounceX"/>/
    /// <see cref="BounceY"/> per edge so ONLY the icon lifts (the running dot stays put).</summary>
    public double BounceOffset { get; private set; }

    /// <summary>True while a bounce is playing (used to not restart mid-flight on repeat flashes).</summary>
    public bool IsBouncing => _bounceStartTicks != 0;

    // Render-transform offsets for the icon image during a bounce (bound by the item template).
    [ObservableProperty] private double _bounceX;
    [ObservableProperty] private double _bounceY;

    /// <summary>True if <paramref name="hwnd"/> was already present at the last refresh.</summary>
    public bool HasWindow(IntPtr hwnd) => _knownWindows.Contains(hwnd);

    /// <summary>Records the app's current window set (the baseline for the next refresh's diff).</summary>
    public void SetKnownWindows(IReadOnlyList<IntPtr> windows)
    {
        _knownWindows.Clear();
        foreach (var h in windows)
            _knownWindows.Add(h);
    }

    /// <summary>Starts (or restarts) a bounce: one hop for an app launch, more for an attention
    /// request (the taskbar-flash equivalent).</summary>
    public void StartBounce(int hops = 1)
    {
        _bounceHops = Math.Max(1, hops);
        _bounceStartTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Advances the bounce, setting <see cref="BounceOffset"/> for the current time. The icon hops
    /// up and back down the requested number of times; <paramref name="amplitude"/> is the peak
    /// lift in DIPs. Returns true while still bouncing.
    /// </summary>
    public bool UpdateBounce(double amplitude)
    {
        if (_bounceStartTicks == 0)
            return false;

        const double hopMs = 560; // one up-and-down hop (2x slower than the original 280ms)
        double elapsed = (DateTime.UtcNow.Ticks - _bounceStartTicks) / (double)TimeSpan.TicksPerMillisecond;
        if (elapsed >= hopMs * _bounceHops)
        {
            _bounceStartTicks = 0;
            BounceOffset = 0;
            return false;
        }

        double phase = (elapsed % hopMs) / hopMs;       // 0..1 within a hop
        BounceOffset = amplitude * Math.Sin(Math.PI * phase); // smooth arch up then down
        return true;
    }

    /// <summary>True while this tile is being dragged (lifts it above the row, drives ZIndex).</summary>
    [ObservableProperty]
    private bool _isDragging;

    /// <summary>Handles a click on this item: opens Start, or launches the shortcut target.</summary>
    public void Activate()
    {
        switch (Model.Kind)
        {
            case DockItemKind.StartMenu:
                StartMenu.Open();
                break;
            case DockItemKind.Shortcut:
                ShortcutService.Launch(Model);
                break;
            case DockItemKind.RecycleBin:
                RecycleBin.Open();
                break;
            case DockItemKind.PinnedFolder:
            case DockItemKind.PinnedFile:
                // Shell-launch the path: folders open in File Explorer, files in their default app.
                ShortcutService.Launch(Model.TargetPath);
                break;
        }
    }

    /// <summary>Loads this item's icon asynchronously from its target path (shortcuts and taskbar apps).</summary>
    public async Task LoadIconAsync(int pixelSize)
    {
        // The Recycle Bin has no file path; the shell extracts its (state-aware) icon from the
        // namespace item, so re-calling this after the bin empties/fills swaps the icon.
        if (Model.Kind == DockItemKind.RecycleBin)
        {
            Icon = await ShortcutService.LoadIconAsync(RecycleBin.ParsingName, pixelSize);
            return;
        }

        if (Model.Kind is not (DockItemKind.Shortcut or DockItemKind.TaskbarApp
            or DockItemKind.PinnedFolder or DockItemKind.PinnedFile or DockItemKind.StartMenu))
            return;

        // A user-chosen replacement icon overrides extraction (the funnel below renders the
        // actual .png/.svg artwork); an unreadable cached image falls through to the default.
        if (CustomIconPath is not null)
        {
            var custom = await ShortcutService.LoadIconAsync(CustomIconPath, pixelSize);
            if (custom is not null)
            {
                Icon = custom;
                return;
            }
        }

        // The Start tile has nothing to extract from: with no (loadable) custom icon it clears
        // Icon so its built-in launcher glyph shows again (e.g. after "Reset Icon").
        if (Model.Kind == DockItemKind.StartMenu)
        {
            Icon = null;
            return;
        }

        // The Dock Preferences pseudo-app's default is the bundled glyph, not an extraction.
        if (IsPreferences)
        {
            Icon = AppIcon.Preferences;
            return;
        }

        // A folder displayed as a Stack composes its top items' icons instead of the folder glyph;
        // an empty/unreadable folder falls back to the plain File Explorer folder icon below.
        if (Model.Kind == DockItemKind.PinnedFolder && PathModel is { DisplayAs: FolderDisplayAs.Stack } pin)
        {
            var stacked = await StackIcon.RenderAsync(Model.TargetPath, pin.SortBy, pixelSize);
            if (stacked is not null)
            {
                Icon = stacked;
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(Model.TargetPath))
            Icon = await ShortcutService.LoadIconAsync(Model.TargetPath, pixelSize);
        else if (Windows.Count > 0)
            Icon = await ShortcutService.LoadWindowIconAsync(Windows[0]); // no readable exe → the window's own icon
    }

    private static string DeriveDisplayName(DockItem model) => model.Kind switch
    {
        DockItemKind.StartMenu => "Start",
        DockItemKind.Separator => string.Empty,
        _ => string.IsNullOrEmpty(model.TargetPath)
            ? "Item"
            : System.IO.Path.GetFileNameWithoutExtension(model.TargetPath),
    };
}
