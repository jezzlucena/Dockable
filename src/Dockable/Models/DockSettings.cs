namespace Dockable.Models;

/// <summary>Which screen edge the dock is anchored to.</summary>
public enum DockEdge
{
    Bottom,
    Left,
    Right,
    Top,
}

/// <summary>How the dock occupies the screen.</summary>
public enum DockBehavior
{
    /// <summary>Always visible; reserves screen space via a Win32 AppBar.</summary>
    AlwaysVisible,

    /// <summary>Floats on top and slides away when idle, revealing on mouse-to-edge.</summary>
    AutoHide,
}

/// <summary>Which color theme the dock paints with.</summary>
public enum DockTheme
{
    /// <summary>Follow the current Windows light/dark setting.</summary>
    System,
    Light,
    Dark,
}

/// <summary>How a window animates as it minimizes into / restores from the dock.</summary>
public enum MinimizeEffect
{
    /// <summary>macOS-style 3D mesh warp into the dock tile.</summary>
    Genie,

    /// <summary>Simple scale-down to the dock tile (and reverse on restore).</summary>
    Scale,
}

/// <summary>
/// Root persisted configuration for Dockable. Serialized to
/// %APPDATA%\Dockable\settings.json by <see cref="Services.SettingsStore"/>.
/// </summary>
public sealed class DockSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    public DockBehavior Behavior { get; set; } = DockBehavior.AutoHide;

    /// <summary>Color theme: follow the OS (System) or force Light/Dark.</summary>
    public DockTheme Theme { get; set; } = DockTheme.System;

    /// <summary>Window minimize/restore animation style.</summary>
    public MinimizeEffect MinimizeEffect { get; set; } = MinimizeEffect.Genie;

    /// <summary>Base (un-magnified) icon size in DIPs.</summary>
    public double IconSize { get; set; } = 48;

    /// <summary>Maximum magnified icon size in DIPs (Phase 2).</summary>
    public double MaxIconSize { get; set; } = 96;

    /// <summary>Cursor influence radius for magnification, in DIPs (Phase 2).</summary>
    public double MagnificationRadius { get; set; } = 160;

    /// <summary>Whether the macOS-style fisheye magnification is enabled (Phase 2).</summary>
    public bool MagnificationEnabled { get; set; } = true;

    /// <summary>Forcefully hide the Windows taskbar while Dockable is running.</summary>
    public bool HideTaskbar { get; set; } = true;

    /// <summary>Show the running-indicator dot under apps that have open windows.</summary>
    public bool ShowRunningIndicators { get; set; } = true;

    /// <summary>The user's pinned items, in display order. The Start item is added implicitly.</summary>
    public List<DockItem> Items { get; set; } = new();

    /// <summary>
    /// Dock-owned pinned apps (launch paths: .lnk or .exe), in display order. Null means
    /// "not yet seeded" — on first run it's populated from the current taskbar pin order,
    /// after which the dock owns it (reorder / pin / unpin don't touch the Windows taskbar).
    /// </summary>
    public List<string>? PinnedApps { get; set; }

    public static DockSettings CreateDefault() => new();
}
