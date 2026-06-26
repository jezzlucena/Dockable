using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Dockable.Interop;

/// <summary>
/// Helpers for the Windows Recycle Bin dock tile. The shell supplies the state-aware (empty vs.
/// full) icon itself when we extract the icon from the bin's namespace item, so the dock only
/// needs to know <em>when</em> the state flips (<see cref="IsEmpty"/>) to refresh that icon.
/// </summary>
public static class RecycleBin
{
    /// <summary>Shell parsing name of the Recycle Bin folder (its CLSID), for icon extraction.</summary>
    public const string ParsingName = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>True when the Recycle Bin holds no items (also the safe fallback on query failure).</summary>
    public static bool IsEmpty()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            // Null root path queries every drive's bin at once.
            return PInvoke.SHQueryRecycleBin(default, ref info).Failed || info.i64NumItems == 0;
        }
        catch
        {
            return true;
        }
    }

    // SHFILEOPSTRUCT.wFunc / fFlags values (CsWin32 leaves these as plain uint/ushort).
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010; // no "are you sure?" prompt
    private const ushort FOF_ALLOWUNDO = 0x0040;       // recycle instead of permanently delete
    private const ushort FOF_WANTNUKEWARNING = 0x4000; // still warn if it can't be recycled

    /// <summary>
    /// Moves the given files/folders to the Recycle Bin (FOF_ALLOWUNDO = recycle, not delete).
    /// Returns true if the shell reported success (0). A nuke warning still shows when an item can't
    /// be recycled and would be permanently destroyed.
    /// </summary>
    public static unsafe bool SendToRecycleBin(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return true;
        try
        {
            // pFrom is a double-null-terminated list: "path1\0path2\0\0".
            var sb = new StringBuilder();
            foreach (var p in paths)
                sb.Append(p).Append('\0');
            sb.Append('\0');

            fixed (char* from = sb.ToString())
            {
                var op = new SHFILEOPSTRUCTW
                {
                    wFunc = FO_DELETE,
                    pFrom = new PCZZWSTR(from),
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
                };
                return PInvoke.SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Recycle failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Opens the Recycle Bin in Explorer.</summary>
    public static void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Failed to open Recycle Bin: {ex.Message}");
        }
    }
}
