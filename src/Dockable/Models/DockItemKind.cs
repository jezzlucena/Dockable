namespace Dockable.Models;

/// <summary>
/// The category of a dock entry. Determines how it is launched and rendered.
/// </summary>
public enum DockItemKind
{
    /// <summary>Special item that opens the Windows Start menu.</summary>
    StartMenu,

    /// <summary>An executable, document, folder, or .lnk shortcut launched via the shell.</summary>
    Shortcut,

    /// <summary>A visual gap between groups of items.</summary>
    Separator,

    /// <summary>A minimized window, shown as a thumbnail tile; click restores it.</summary>
    MinimizedWindow,

    /// <summary>A taskbar app (pinned and/or running), mirrored from the Windows taskbar.</summary>
    TaskbarApp,

    /// <summary>The Windows Recycle Bin; pinned far-right, with a state-aware empty/full icon.</summary>
    RecycleBin,
}
