namespace Dockable.Models;

/// <summary>
/// A persisted dock entry. Serialized to settings.json. Runtime-only state
/// (loaded icon image, animation values) lives on the matching view-model.
/// </summary>
public sealed class DockItem
{
    /// <summary>Sentinel launch path identifying the built-in "Dock Preferences" pseudo-app/pin
    /// (it isn't a real file — the dock opens its own Preferences window instead of shell-launching).</summary>
    public const string PreferencesLaunchPath = "dockable://preferences";

    /// <summary>Sentinel launch path for the Start tile — not launchable; it keys the tile's
    /// custom icon (if any) in <c>DockSettings.PinIcons</c>.</summary>
    public const string StartLaunchPath = "dockable://start";

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

    /// <summary>A pinned file or folder tile for the dock's right section. The display name is the
    /// path's leaf (folders keep their name; files drop the extension, macOS-style).</summary>
    public static DockItem CreatePinnedPath(string path, bool isFolder) => new()
    {
        Kind = isFolder ? DockItemKind.PinnedFolder : DockItemKind.PinnedFile,
        TargetPath = path,
        DisplayName = isFolder
            ? (System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path)
            : System.IO.Path.GetFileNameWithoutExtension(path),
    };
}
