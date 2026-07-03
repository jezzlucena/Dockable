using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Dockable.Interop;

/// <summary>
/// Resolves known-folder paths via the shell. Used for the default Downloads pin — the folder can
/// be relocated by the user, so <c>%USERPROFILE%\Downloads</c> isn't reliable.
/// </summary>
internal static class KnownFolders
{
    /// <summary>The user's Downloads folder, or null when it can't be resolved.</summary>
    public static unsafe string? Downloads()
    {
        try
        {
            var hr = PInvoke.SHGetKnownFolderPath(
                PInvoke.FOLDERID_Downloads, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, null, out PWSTR path);
            if (hr.Failed)
                return null;
            try { return path.ToString(); }
            finally { Marshal.FreeCoTaskMem((IntPtr)path.Value); }
        }
        catch
        {
            return null;
        }
    }
}
