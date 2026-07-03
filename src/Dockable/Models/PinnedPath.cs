namespace Dockable.Models;

/// <summary>How a pinned folder's contents are ordered when it opens (macOS "Sort by").</summary>
public enum FolderSortBy
{
    Name,
    DateAdded,
    DateModified,
    DateCreated,
    Kind,
}

/// <summary>How a pinned folder's dock tile is drawn (macOS "Display as").</summary>
public enum FolderDisplayAs
{
    /// <summary>The plain folder icon.</summary>
    Folder,

    /// <summary>A stack: the folder's front items fanned into a pile.</summary>
    Stack,
}

/// <summary>How a pinned folder presents its contents when clicked (macOS "View content as").</summary>
public enum FolderViewContentAs
{
    Fan,
    Grid,
    List,
    Automatic,
}

/// <summary>
/// A file or folder pinned to the dock's right section (between the app shortcuts and the Recycle
/// Bin, macOS-style). Persisted in settings.json. Whether it's a folder or a file is derived from
/// the path at load time; the Sort/Display/View options only apply to folders.
/// </summary>
public sealed class PinnedPath
{
    /// <summary>Absolute path of the pinned file or folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Content ordering for folders (macOS default: Date Added).</summary>
    public FolderSortBy SortBy { get; set; } = FolderSortBy.DateAdded;

    /// <summary>Tile appearance for folders (macOS default: Stack).</summary>
    public FolderDisplayAs DisplayAs { get; set; } = FolderDisplayAs.Stack;

    /// <summary>Content presentation for folders (macOS default: Automatic).</summary>
    public FolderViewContentAs ViewContentAs { get; set; } = FolderViewContentAs.Automatic;
}
