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

    /// <summary>Whether this taskbar app is pinned in the dock (vs. only running).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnpin))]
    private bool _isPinned;

    /// <summary>Right-click "Unpin" applies only to pinned taskbar apps.</summary>
    public bool CanUnpin => IsTaskbarApp && IsPinned;

    /// <summary>True for items that show an icon/thumbnail (and a loading fallback).</summary>
    public bool ShowIconArea =>
        Model.Kind is DockItemKind.Shortcut or DockItemKind.MinimizedWindow
            or DockItemKind.TaskbarApp or DockItemKind.RecycleBin;

    /// <summary>Native window handle for <see cref="DockItemKind.MinimizedWindow"/> tiles.</summary>
    public IntPtr Hwnd { get; set; }

    // --- Taskbar-app state (Kind == TaskbarApp) ---

    /// <summary>Stable key for reuse across refreshes (target exe path, or .lnk path for UWP).</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>Monotonic order in which this app was first seen; orders unpinned apps by open time.</summary>
    public long SeenOrder { get; set; }

    /// <summary>What to launch when the app has no open windows (the .lnk or exe path).</summary>
    public string LaunchPath { get; set; } = string.Empty;

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

    /// <summary>The loaded icon, or null until <see cref="Services.IconLoader"/> populates it.</summary>
    [ObservableProperty]
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

        if (Model.Kind is not (DockItemKind.Shortcut or DockItemKind.TaskbarApp)
            || string.IsNullOrWhiteSpace(Model.TargetPath))
            return;

        Icon = await ShortcutService.LoadIconAsync(Model.TargetPath, pixelSize);
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
