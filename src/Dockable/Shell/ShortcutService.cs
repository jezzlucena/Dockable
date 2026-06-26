using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dockable.Models;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

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

        object? shellItem = null;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            var hr = PInvoke.SHCreateItemFromParsingName(path, null!, iid, out shellItem);
            if (hr.Failed || shellItem is not IShellItemImageFactory factory)
                return null;

            var size = new Windows.Win32.Foundation.SIZE(pixelSize, pixelSize);

            // RESIZETOFIT + BIGGERSIZEOK lets the shell hand back the largest available
            // asset (up to 256px) and scale to fit, giving crisp results on HiDPI.
            const SIIGBF flags = SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK;

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
    /// Copies a 32bpp DIB (as produced by GetImage, premultiplied BGRA) into a frozen
    /// WriteableBitmap, preserving the alpha channel and handling row orientation.
    /// </summary>
    private static unsafe ImageSource? ConvertHBitmap(HBITMAP hbmp)
    {
        DIBSECTION ds = default;
        int written = PInvoke.GetObject((HGDIOBJ)(void*)hbmp, sizeof(DIBSECTION), &ds);
        if (written < sizeof(DIBSECTION) || ds.dsBm.bmBits is null)
            return null;

        int width = ds.dsBm.bmWidth;
        int height = ds.dsBm.bmHeight;
        int stride = ds.dsBm.bmWidthBytes;
        if (width <= 0 || height <= 0 || ds.dsBm.bmBitsPixel != 32)
            return null;

        int byteCount = stride * height;
        byte[] buffer = new byte[byteCount];
        Marshal.Copy((IntPtr)ds.dsBm.bmBits, buffer, 0, byteCount);

        // A negative biHeight means the DIB is top-down (the common case for GetImage).
        // A positive biHeight is bottom-up and must be flipped row-by-row.
        if (ds.dsBmih.biHeight > 0)
            FlipRowsInPlace(buffer, stride, height);

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void FlipRowsInPlace(byte[] buffer, int stride, int height)
    {
        byte[] tmp = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(buffer, top * stride, tmp, 0, stride);
            Buffer.BlockCopy(buffer, bottom * stride, buffer, top * stride, stride);
            Buffer.BlockCopy(tmp, 0, buffer, bottom * stride, stride);
        }
    }
}
