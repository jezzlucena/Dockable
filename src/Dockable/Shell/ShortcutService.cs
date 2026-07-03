using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dockable.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Shell;

/// <summary>
/// Launches dock shortcuts via the shell and extracts crisp, alpha-correct icons
/// using <c>IShellItemImageFactory</c> (works for .exe, .lnk, documents, and folders).
/// </summary>
public static class ShortcutService
{
    /// <summary>HRESULT E_PENDING: the shell is still extracting the image.</summary>
    private const uint EPending = 0x8000000A;
    private const int MaxIconAttempts = 12;
    private const int IconRetryDelayMs = 100;


    /// <summary>
    /// Launches the target of a shortcut item using the shell, so .lnk files,
    /// documents, folders, and executables all resolve correctly.
    /// </summary>
    public static bool Launch(DockItem item)
    {
        if (item.Kind != DockItemKind.Shortcut || string.IsNullOrWhiteSpace(item.TargetPath))
            return false;
        return Launch(item.TargetPath, item.Arguments, item.WorkingDirectory);
    }

    /// <summary>Launches a path (exe, .lnk, document, folder) through the shell.</summary>
    public static bool Launch(string targetPath, string arguments = "", string workingDirectory = "")
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true,
            };

            if (!string.IsNullOrWhiteSpace(arguments))
                psi.Arguments = arguments;

            psi.WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : SafeDirectoryOf(targetPath);

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Launch failed for '{targetPath}': {ex.Message}");
            return false;
        }
    }

    private const uint WM_GETICON = 0x007F;

    /// <summary>
    /// The shell display name for a parsing name — e.g. <c>shell:AppsFolder\{aumid}</c> resolves to a
    /// UWP/Store app's friendly name (like "Settings"). Null if it can't be resolved.
    /// </summary>
    public static unsafe string? GetShellDisplayName(string parsingName)
    {
        object? shellItem = null;
        try
        {
            Guid iid = typeof(IShellItem).GUID;
            if (PInvoke.SHCreateItemFromParsingName(parsingName, null!, iid, out shellItem).Failed
                || shellItem is not IShellItem item)
                return null;

            item.GetDisplayName(SIGDN.SIGDN_NORMALDISPLAY, out PWSTR name);
            try { return name.ToString(); }
            finally { Marshal.FreeCoTaskMem((IntPtr)name.Value); }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem))
                Marshal.ReleaseComObject(shellItem);
        }
    }

    /// <summary>
    /// Extracts a window's own icon (its big/small icon, else its window-class icon) on a background
    /// thread — the fallback for apps whose executable we can't read (e.g. elevated, like Task Manager).
    /// </summary>
    public static Task<ImageSource?> LoadWindowIconAsync(IntPtr hwnd) => Task.Run(() => LoadWindowIcon(hwnd));

    private static ImageSource? LoadWindowIcon(IntPtr hwnd)
    {
        try
        {
            IntPtr hicon = QueryWindowIcon((HWND)hwnd);
            if (hicon == IntPtr.Zero)
                return null;
            // The HICON is owned by the window/class — don't destroy it; CreateBitmapSourceFromHIcon copies.
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Window icon load failed: {ex.Message}");
            return null;
        }
    }

    private static unsafe IntPtr QueryWindowIcon(HWND hwnd)
    {
        // Prefer the window's big icon, then small, then the class icon. SendMessageTimeout avoids
        // hanging if the target window isn't pumping messages.
        nuint result = 0;
        PInvoke.SendMessageTimeout(hwnd, WM_GETICON, new WPARAM(1 /* ICON_BIG */), new LPARAM(0),
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 250, &result);
        if (result != 0) return (nint)result;

        result = 0;
        PInvoke.SendMessageTimeout(hwnd, WM_GETICON, new WPARAM(2 /* ICON_SMALL2 */), new LPARAM(0),
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 250, &result);
        if (result != 0) return (nint)result;

        nint cls = (nint)PInvoke.GetClassLongPtr(hwnd, GET_CLASS_LONG_INDEX.GCLP_HICON);
        if (cls != 0) return cls;
        return (nint)PInvoke.GetClassLongPtr(hwnd, GET_CLASS_LONG_INDEX.GCLP_HICONSM);
    }

    /// <summary>Opens an Explorer window at the file's folder with the file itself selected.</summary>
    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            // /select, opens the containing folder and highlights the item.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] RevealInExplorer failed for '{path}': {ex.Message}");
        }
    }

    private static string SafeDirectoryOf(string path)
    {
        try { return Path.GetDirectoryName(path) ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Extracts an icon for <paramref name="path"/> at the requested pixel size on a
    /// background thread (shell icon extraction must never run on the UI thread).
    /// Returns a frozen, cross-thread-usable bitmap, or null if extraction fails.
    /// </summary>
    public static Task<ImageSource?> LoadIconAsync(string path, int pixelSize)
        => Task.Run(() => LoadIcon(path, pixelSize));

    private static unsafe ImageSource? LoadIcon(string path, int pixelSize)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Internet shortcuts (.url) — e.g. Steam game shortcuts — aren't images themselves, so the
        // shell hands back a blank page icon. Resolve the icon the desktop actually shows by reading
        // the [InternetShortcut] IconFile / IconIndex and extracting from there.
        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = LoadUrlShortcutIcon(path, pixelSize);
            if (resolved is not null)
                return resolved;
            // Fall through to the default shell extraction (a blank page beats nothing).
        }

        // SVGs: the shell has no native SVG thumbnailer and returns the generic association icon.
        // Render the actual vector artwork instead, so a stack/fan full of .svg files stays
        // meaningful. Unparsable SVGs fall through to the shell icon.
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            var svg = SvgIcon.Render(path, pixelSize);
            if (svg is not null)
                return svg;
        }

        // Executables carry their icon in the PE resource. Extract it directly (deterministic, full-res)
        // instead of via the shell image factory, whose async cache occasionally returns a tiny low-res
        // placeholder for some apps (e.g. Cursor) — crisp on one run, favicon-sized in a square the next.
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var fromExe = ExtractIconAtIndex(path, 0, pixelSize);
            if (fromExe is not null)
                return fromExe;
            // Fall through to the shell factory if the exe has no extractable icon resource.
        }

        object? shellItem = null;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            var hr = PInvoke.SHCreateItemFromParsingName(path, null!, iid, out shellItem);
            if (hr.Failed || shellItem is not IShellItemImageFactory factory)
                return null;

            bool isUwp = path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);

            var size = new Windows.Win32.Foundation.SIZE(pixelSize, pixelSize);

            // RESIZETOFIT + BIGGERSIZEOK lets the shell hand back the largest available
            // asset (up to 256px) and scale to fit, giving crisp results on HiDPI.
            SIIGBF flags = SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK;

            // For UWP/Store apps, force the icon (logo) representation. Without this the shell may return
            // a "plated" thumbnail with a light square background baked behind the logo (e.g. Settings).
            if (isUwp)
                flags |= SIIGBF.SIIGBF_ICONONLY;

            // For uncached items the shell extracts the image asynchronously and the first
            // call returns E_PENDING. Poll briefly until the image becomes available.
            for (int attempt = 0; attempt < MaxIconAttempts; attempt++)
            {
                HBITMAP hbmp;
                try
                {
                    factory.GetImage(size, flags, &hbmp);
                }
                catch (COMException ex) when ((uint)ex.HResult == EPending)
                {
                    Thread.Sleep(IconRetryDelayMs);
                    continue;
                }

                try
                {
                    return ConvertHBitmap(hbmp);
                }
                finally
                {
                    PInvoke.DeleteObject((HGDIOBJ)(void*)hbmp);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Icon load failed for '{path}': {ex.Message}");
            return null;
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem))
                Marshal.ReleaseComObject(shellItem);
        }
    }

    /// <summary>
    /// Reads an Internet shortcut's (.url) <c>IconFile</c>/<c>IconIndex</c> and extracts that icon —
    /// the same one Explorer shows on the desktop. Returns null if the .url has no usable icon
    /// reference (the caller then falls back to the generic shell icon).
    /// </summary>
    private static ImageSource? LoadUrlShortcutIcon(string urlPath, int pixelSize)
    {
        try
        {
            string? iconFile = null;
            int iconIndex = 0;

            foreach (string raw in File.ReadAllLines(urlPath))
            {
                string line = raw.Trim();
                if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    iconFile = line["IconFile=".Length..].Trim();
                else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(line["IconIndex=".Length..].Trim(), out int idx))
                    iconIndex = idx;
            }

            if (string.IsNullOrWhiteSpace(iconFile))
                return null;

            iconFile = Environment.ExpandEnvironmentVariables(iconFile);
            if (!File.Exists(iconFile))
                return null;

            // Extract straight from the referenced icon resource (.ico/.exe/.dll at the given index).
            // We deliberately avoid the IShellItemImageFactory path here: for a raw .ico it returns
            // the image in an orientation our DIB flip heuristic gets wrong (icons come out upside
            // down), whereas PrivateExtractIcons + CreateBitmapSourceFromHIcon is orientation-correct.
            return ExtractIconAtIndex(iconFile, iconIndex, pixelSize);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] .url icon resolve failed for '{urlPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Extracts a single icon at a given index/size from an .exe/.dll/.ico via the shell.</summary>
    private static unsafe ImageSource? ExtractIconAtIndex(string file, int index, int pixelSize)
    {
        uint extracted = PInvoke.PrivateExtractIcons(file, index, pixelSize, pixelSize, out var hicon, null, 1, 0);
        try
        {
            if (extracted == 0 || hicon is null || hicon.IsInvalid)
                return null;

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hicon.DangerousGetHandle(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Icon extract failed for '{file}' [{index}]: {ex.Message}");
            return null;
        }
        finally
        {
            hicon?.Dispose(); // the SafeHandle owns the HICON and DestroyIcon's it
        }
    }

    /// <summary>
    /// Copies a 32bpp DIB (as produced by GetImage, premultiplied BGRA) into a frozen
    /// WriteableBitmap, preserving the alpha channel.
    /// </summary>
    private static unsafe ImageSource? ConvertHBitmap(HBITMAP hbmp)
    {
        DIBSECTION ds = default;
        int written = PInvoke.GetObject((HGDIOBJ)(void*)hbmp, sizeof(DIBSECTION), &ds);
        if (written < sizeof(DIBSECTION) || ds.dsBm.bmBits is null)
            return null;

        int width = ds.dsBm.bmWidth;
        int height = Math.Abs(ds.dsBm.bmHeight);
        if (width <= 0 || height <= 0 || ds.dsBm.bmBitsPixel != 32)
            return null;

        // Read the rows via GetDIBits with an EXPLICITLY top-down target (negative biHeight): GDI
        // then delivers the rows in exactly the order WritePixels expects, regardless of how the
        // source DIB is stored. The old path inferred orientation from the source's biHeight sign,
        // which some shell extraction paths mislabel — PNG/ICO thumbnails and folder icons at
        // certain sizes arrived upside down. 32bpp BI_RGB keeps the alpha bytes intact.
        int stride = width * 4;
        byte[] buffer = new byte[stride * height];
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // negative = top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        HDC hdc = PInvoke.GetDC(HWND.Null);
        try
        {
            int lines;
            fixed (byte* pixels = buffer)
                lines = PInvoke.GetDIBits(hdc, hbmp, 0, (uint)height, pixels, &bmi, DIB_USAGE.DIB_RGB_COLORS);
            if (lines != height)
                return null;
        }
        finally
        {
            _ = PInvoke.ReleaseDC(HWND.Null, hdc);
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
