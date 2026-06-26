using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
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

    public static unsafe Result? Capture(IntPtr hwnd)
    {
        var window = (HWND)hwnd;
        RECT rect;
        if (!PInvoke.GetWindowRect(window, &rect))
            return null;

        int w = rect.right - rect.left;
        int h = rect.bottom - rect.top;
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000)
            return null;

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

                int stride = w * 4;
                // Bgr32 treats the data as opaque (screen has no meaningful per-pixel alpha here).
                var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, w, h), (IntPtr)bits, stride * h, stride);
                bitmap.Freeze();

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
}
