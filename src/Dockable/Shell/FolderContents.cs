using System.IO;
using Dockable.Models;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Dockable.Shell;

/// <summary>
/// Enumerates a pinned folder's top-level contents ordered per its "Sort by" choice — the order
/// that drives both the stacked tile icon (top of the stack first) and the fan-out list.
/// </summary>
internal static class FolderContents
{
    /// <summary>How many items a stack tile / fan shows; the rest collapse into "N more".</summary>
    public const int MaxItems = 10;

    /// <summary>
    /// The folder's visible entries (files + subfolders; hidden/system skipped, like Explorer),
    /// sorted with the top-of-stack item FIRST. Dates sort newest-first; "Kind" groups by the
    /// shell's friendly type name alphabetically, then by name within each kind. Returns an empty
    /// list when the folder is missing or unreadable. IO-bound — call off the UI thread.
    /// </summary>
    public static List<FileSystemInfo> GetSorted(string folder, FolderSortBy sortBy)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folder)
                .EnumerateFileSystemInfos()
                .Where(e => (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .ToList();
        }
        catch
        {
            return new List<FileSystemInfo>();
        }

        switch (sortBy)
        {
            case FolderSortBy.Name:
                entries.Sort((a, b) => CompareNames(a, b));
                break;
            // NTFS has no true macOS-style "date added"; the creation time is the closest proxy
            // (a copy into the folder re-stamps it; only a same-volume move preserves the old one).
            case FolderSortBy.DateAdded:
            case FolderSortBy.DateCreated:
                entries.Sort((a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                break;
            case FolderSortBy.DateModified:
                entries.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                break;
            case FolderSortBy.Kind:
                var kinds = new Dictionary<string, string>();
                foreach (var e in entries)
                    kinds[e.FullName] = KindName(e.FullName);
                entries.Sort((a, b) =>
                {
                    int byKind = string.Compare(kinds[a.FullName], kinds[b.FullName],
                        StringComparison.CurrentCultureIgnoreCase);
                    return byKind != 0 ? byKind : CompareNames(a, b);
                });
                break;
        }
        return entries;
    }

    /// <summary>Display-name comparison (files compare without their extension, like the dock labels).</summary>
    private static int CompareNames(FileSystemInfo a, FileSystemInfo b)
        => string.Compare(DisplayName(a), DisplayName(b), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The label an entry shows in the dock/fan: folders keep their name, files drop the extension.</summary>
    public static string DisplayName(FileSystemInfo entry)
        => entry is DirectoryInfo || Path.GetFileNameWithoutExtension(entry.Name) is not { Length: > 0 } stem
            ? entry.Name
            : stem;

    /// <summary>
    /// The shell's friendly type name for a path ("Application", "JSON Source File", "File folder"),
    /// used by the "Kind" sort. Empty when the shell has none.
    /// </summary>
    public static unsafe string KindName(string path)
    {
        var info = new SHFILEINFOW();
        nuint ok = PInvoke.SHGetFileInfo(path, 0, &info, (uint)sizeof(SHFILEINFOW), SHGFI_FLAGS.SHGFI_TYPENAME);
        return ok != 0 ? info.szTypeName.ToString() : string.Empty;
    }
}
