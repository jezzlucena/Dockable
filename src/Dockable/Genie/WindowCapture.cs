using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;

namespace Dockable.Genie;

/// <summary>
/// Captures a window's on-screen pixels by BitBlt-ing the composited screen region it
/// occupies. Unlike <c>PrintWindow</c> (which returns a black client area for many
/// hardware-accelerated apps, leaving only the caption frame), this grabs exactly what
/// the user sees — including GPU-rendered content — as long as the window is visible and
/// not occluded, which holds for a window the user is actively minimizing.
/// </summary>
public static class WindowCapture
{
    // CAPTUREBLT makes the blit include layered / composited windows.
    private const ROP_CODE CaptureRop = ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT;

    public readonly record struct Result(BitmapSource Bitmap, Int32Rect ScreenRectPx);

    /// <summary>
    /// Captures an arbitrary screen rectangle (physical pixels) by BitBlt-ing the composited screen.
    /// With <c>WDA_EXCLUDEFROMCAPTURE</c> set on the dock, the dock is omitted from the grab, so this
    /// yields the true backdrop behind it (for the Liquid Glass refraction). Null on failure.
    /// </summary>
    public static unsafe BitmapSource? CaptureScreenRect(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000)
            return null;

        HDC screenDc = PInvoke.GetDC((HWND)IntPtr.Zero);
        HDC memDc = PInvoke.CreateCompatibleDC(screenDc);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            void* bits;
            HBITMAP dib = PInvoke.CreateDIBSection(memDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
            if (dib.IsNull || bits is null)
                return null;

            HGDIOBJ previous = PInvoke.SelectObject(memDc, (HGDIOBJ)(void*)dib);
            try
            {
                if (!PInvoke.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, CaptureRop))
                    return null;

                int stride = w * 4;
                var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, w, h), (IntPtr)bits, stride * h, stride);
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                PInvoke.SelectObject(memDc, previous);
                PInvoke.DeleteObject((HGDIOBJ)(void*)dib);
            }
        }
        finally
        {
            PInvoke.DeleteDC(memDc);
            PInvoke.ReleaseDC((HWND)IntPtr.Zero, screenDc);
        }
    }

    public static unsafe Result? Capture(IntPtr hwnd)
    {
        var window = (HWND)hwnd;

        // The DWM "extended frame bounds" exclude the drop shadow and the invisible resize border, so
        // we capture the window the user actually sees — not a margin of backdrop/shadow around it.
        // Fall back to GetWindowRect on older OSes / failure.
        RECT rect;
        var frame = default(RECT);
        var hr = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &frame, (uint)sizeof(RECT));
        if (hr.Succeeded && frame.right > frame.left && frame.bottom > frame.top)
            rect = frame;
        else if (!PInvoke.GetWindowRect(window, &rect))
            return null;

        int w = rect.right - rect.left;
        int h = rect.bottom - rect.top;
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000)
            return null;

        int radiusPx = CornerRadiusPx(window, w, h);

        HDC screenDc = PInvoke.GetDC((HWND)IntPtr.Zero);
        HDC memDc = PInvoke.CreateCompatibleDC(screenDc);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // negative => top-down rows
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            void* bits;
            HBITMAP dib = PInvoke.CreateDIBSection(memDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
            if (dib.IsNull || bits is null)
                return null;

            HGDIOBJ previous = PInvoke.SelectObject(memDc, (HGDIOBJ)(void*)dib);
            try
            {
                // Copy the composited screen pixels at the window's location.
                if (!PInvoke.BitBlt(memDc, 0, 0, w, h, screenDc, rect.left, rect.top, CaptureRop))
                    return null;

                var bitmap = BuildWindowBitmap(bits, w, h, radiusPx);
                return new Result(bitmap, new Int32Rect(rect.left, rect.top, w, h));
            }
            finally
            {
                PInvoke.SelectObject(memDc, previous);
                PInvoke.DeleteObject((HGDIOBJ)(void*)dib);
            }
        }
        finally
        {
            PInvoke.DeleteDC(memDc);
            PInvoke.ReleaseDC((HWND)IntPtr.Zero, screenDc);
        }
    }

    /// <summary>The window's rounded-corner radius in physical pixels (Win11 default ≈ 8 DIP, scaled
    /// by the window's DPI), clamped so it never exceeds half the capture.</summary>
    private static int CornerRadiusPx(HWND window, int w, int h)
    {
        uint dpi = PInvoke.GetDpiForWindow(window);
        if (dpi == 0)
            dpi = 96;
        int r = (int)Math.Round(8.0 * dpi / 96.0);
        return Math.Clamp(r, 0, Math.Min(w, h) / 2);
    }

    /// <summary>
    /// Builds the opaque window bitmap from the captured BGRX bits, then carves antialiased transparent
    /// rounded corners so the minimize warp follows the window's actual shape (not a square). BitBlt
    /// leaves the 4th byte unspecified, so alpha is set explicitly: 255 everywhere, tapering to 0 in the
    /// corner arcs.
    /// </summary>
    private static unsafe BitmapSource BuildWindowBitmap(void* bits, int w, int h, int radius)
    {
        int stride = w * 4;
        var buf = new byte[stride * h];
        Marshal.Copy((IntPtr)bits, buf, 0, buf.Length);

        for (int i = 3; i < buf.Length; i += 4)
            buf[i] = 255; // opaque window body

        if (radius > 0)
        {
            CarveCorner(buf, stride, radius, 0, 0, radius, radius);                 // top-left
            CarveCorner(buf, stride, radius, w - radius, 0, w - radius, radius);     // top-right
            CarveCorner(buf, stride, radius, 0, h - radius, radius, h - radius);     // bottom-left
            CarveCorner(buf, stride, radius, w - radius, h - radius, w - radius, h - radius); // bottom-right
        }

        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, w, h), buf, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Sets the alpha of a corner's r×r box to a quarter-disc centred at
    /// (<paramref name="cx"/>, <paramref name="cy"/>), with a 1px antialiased edge.</summary>
    private static void CarveCorner(byte[] buf, int stride, int r, int x0, int y0, double cx, double cy)
    {
        for (int y = y0; y < y0 + r; y++)
        for (int x = x0; x < x0 + r; x++)
        {
            double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
            double coverage = Math.Clamp(r - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0.0, 1.0);
            buf[y * stride + x * 4 + 3] = (byte)(coverage * 255.0 + 0.5);
        }
    }
}
