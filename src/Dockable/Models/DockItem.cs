namespace Dockable.Models;

/// <summary>
/// A persisted dock entry. Serialized to settings.json. Runtime-only state
/// (loaded icon image, animation values) lives on the matching view-model.
/// </summary>
public sealed class DockItem
{
    /// <summary>Stable identity, used to match persisted items to view-models.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DockItemKind Kind { get; set; } = DockItemKind.Shortcut;

    /// <summary>Target path: an .exe, document, folder, or .lnk file. Empty for non-shortcuts.</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Optional command-line arguments passed when launching.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Optional working directory; defaults to the target's folder when empty.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Display label (tooltip / accessibility). Falls back to the file name when empty.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public static DockItem CreateStartMenu() => new()
    {
        Id = "start-menu",
        Kind = DockItemKind.StartMenu,
        DisplayName = "Start",
    };

    public static DockItem CreateSeparator(string id = "separator") => new()
    {
        Id = id,
        Kind = DockItemKind.Separator,
    };

    public static DockItem CreateMinimizedWindow(string title) => new()
    {
        Kind = DockItemKind.MinimizedWindow,
        DisplayName = title,
    };

    public static DockItem CreateTaskbarApp(string displayName) => new()
    {
        Kind = DockItemKind.TaskbarApp,
        DisplayName = displayName,
    };

    public static DockItem CreateRecycleBin() => new()
    {
        Id = "recycle-bin",
        Kind = DockItemKind.RecycleBin,
        DisplayName = "Recycle Bin",
    };
}
