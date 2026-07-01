using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Dockable.Genie;

/// <summary>
/// Captures the screen rectangle behind the dock bar for the Liquid Glass refraction on a dedicated
/// <b>background thread</b>, so the ~4 ms GDI screen read-back (a fixed GPU-sync cost, independent of
/// region size) never blocks the UI/render thread. It reuses one memory DC + DIB across frames, diffs
/// each grab against the previous, and — only when the pixels actually changed — hands the frame to the
/// UI thread (via <see cref="Dispatcher"/>) to upload into the <c>WriteableBitmap</c>.
///
/// The poll rate is <b>adaptive</b>: it runs fast while the backdrop is moving (e.g. a video plays
/// behind the dock) and backs off to a low idle rate once it's been static for a while, so a still
/// desktop doesn't burn a CPU core on read-backs that change nothing.
/// </summary>
public sealed unsafe class BackdropCapturer : IDisposable
{
    // CAPTUREBLT makes the blit include layered / composited windows (matches WindowCapture).
    private const ROP_CODE CaptureRop = ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT;
    private const int IdleAfterStaticFrames = 30; // ~250 ms of no change → drop to the idle poll rate

    private readonly Dispatcher _ui;
    private readonly Action<byte[], int, int> _upload; // uploads a changed frame (runs on the UI thread)
    private readonly GlassProfiler? _profiler;
    private readonly int _fastFps;
    private readonly int _idleFps;

    private Thread? _thread;
    private volatile bool _running;

    // Target rect (physical px), published by the UI thread as the bar moves/magnifies.
    private readonly object _rectLock = new();
    private int _rx, _ry, _rw, _rh;
    private bool _haveRect;

    // GDI resources — created and used ONLY on the capture thread.
    private HDC _memDc;
    private HBITMAP _dib;
    private HGDIOBJ _prevObj;
    private void* _bits;      // the DIB's pixel memory (top-down Bgr32)
    private int _w, _h;
    private byte[]? _prev;    // last frame, for diffing
    private bool _havePrev;

    // Transient all-black grabs happen when the WDA_EXCLUDEFROMCAPTURE dock resizes fast (magnification):
    // DWM flashes the excluded region black before it recomposites what's behind. Drop a short run of
    // them, but accept sustained black so a genuinely black backdrop still shows through.
    private const int MaxBlackSkips = 4;
    private int _blackSkips;
    private bool _blackReal;

    // Hand-off of a changed frame to the UI thread.
    private readonly object _outLock = new();
    private byte[]? _ready;
    private int _readyW, _readyH;
    private volatile bool _pending;

    public BackdropCapturer(Dispatcher ui, Action<byte[], int, int> upload, int fastFps, int idleFps,
        GlassProfiler? profiler)
    {
        _ui = ui;
        _upload = upload;
        _fastFps = Math.Max(1, fastFps);
        _idleFps = Math.Max(1, idleFps);
        _profiler = profiler;
    }

    /// <summary>Publishes the screen rect (physical px) to capture. Cheap; safe from any thread.</summary>
    public void SetRect(int x, int y, int w, int h)
    {
        lock (_rectLock)
        {
            _rx = x; _ry = y; _rw = w; _rh = h;
            _haveRect = true;
        }
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "GlassCapture",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        DisposeDib();
    }

    private void Loop()
    {
        // Pace with a high-resolution waitable timer (accurate to ~0.5 ms) rather than Thread.Sleep,
        // whose ~15.6 ms default granularity would otherwise cap the rate far below target unless some
        // other app happened to raise the system timer resolution. Falls back to Sleep if unavailable.
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        try
        {
            var sw = Stopwatch.StartNew();
            int staticFrames = 0;
            while (_running)
            {
                double t0 = sw.Elapsed.TotalMilliseconds;
                bool changed = CaptureOnce();
                staticFrames = changed ? 0 : staticFrames + 1;

                int fps = staticFrames > IdleAfterStaticFrames ? _idleFps : _fastFps;
                double waitMs = 1000.0 / fps - (sw.Elapsed.TotalMilliseconds - t0);
                if (waitMs > 0.3)
                    WaitFor(timer, waitMs);
            }
        }
        finally
        {
            if (timer != IntPtr.Zero)
                CloseHandle(timer);
        }
    }

    private static void WaitFor(IntPtr timer, double waitMs)
    {
        if (timer != IntPtr.Zero)
        {
            long due = -(long)Math.Round(waitMs * 10_000.0); // relative, 100-ns units
            if (SetWaitableTimer(timer, in due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(timer, INFINITE);
                return;
            }
        }
        Thread.Sleep((int)Math.Round(waitMs)); // fallback (coarse)
    }

    // High-resolution waitable timer (Windows 10 1803+). Hand-written P/Invoke — CsWin32 emits these
    // with SafeHandles, but a raw handle used only on the capture thread is simpler here.
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, in long dueTime, int period,
        IntPtr completion, IntPtr completionArg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>One BitBlt + diff. Returns true if the frame changed (and was queued for upload).</summary>
    private bool CaptureOnce()
    {
        int x, y, w, h;
        lock (_rectLock)
        {
            if (!_haveRect)
                return false;
            x = _rx; y = _ry; w = _rw; h = _rh;
        }
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000)
            return false;
        if ((w != _w || h != _h || _dib.IsNull) && !Recreate(w, h))
            return false;

        HDC screenDc = PInvoke.GetDC((HWND)IntPtr.Zero);
        if (screenDc.IsNull)
            return false;
        _profiler?.TickStart(); // measured region: the BitBlt read-back + diff (always paired with TickEnd)
        bool blitOk;
        try
        {
            blitOk = PInvoke.BitBlt(_memDc, 0, 0, w, h, screenDc, x, y, CaptureRop);
        }
        finally
        {
            PInvoke.ReleaseDC((HWND)IntPtr.Zero, screenDc);
        }
        if (!blitOk)
        {
            _profiler?.TickEnd(false, w, h);
            return false;
        }

        int len = w * 4 * h;
        var cur = new ReadOnlySpan<byte>(_bits, len);

        // Drop transient all-black grabs (the exclude-from-capture hole flashing during a fast resize),
        // but stop dropping once black persists — that's a real black backdrop, not a glitch.
        if (LooksBlack(cur, len))
        {
            if (!_blackReal && ++_blackSkips < MaxBlackSkips)
            {
                _profiler?.TickEnd(false, w, h);
                return false; // keep the last good frame on screen
            }
            _blackReal = true;
        }
        else
        {
            _blackSkips = 0;
            _blackReal = false;
        }

        bool changed = !_havePrev || !cur.SequenceEqual(_prev);
        if (changed)
        {
            cur.CopyTo(_prev);
            _havePrev = true;
            lock (_outLock)
            {
                if (_ready is null || _ready.Length < len)
                    _ready = new byte[len];
                cur.CopyTo(_ready);
                _readyW = w;
                _readyH = h;
            }
            // Coalesce: only one upload in flight; it always grabs the latest _ready.
            if (!_pending)
            {
                _pending = true;
                _ui.BeginInvoke(DispatcherPriority.Render, UploadPending);
            }
        }
        _profiler?.TickEnd(changed, w, h);
        return changed;
    }

    private void UploadPending()
    {
        // Hold _outLock across the upload so the capture thread can't overwrite _ready mid-copy. The
        // WritePixels behind _upload is sub-millisecond, so the capture thread barely ever waits.
        lock (_outLock)
        {
            _pending = false;
            if (_ready is not null)
                _upload(_ready, _readyW, _readyH);
        }
    }

    /// <summary>True when most of the grab is exact RGB 0,0,0 — the exclude-from-capture hole flashing
    /// black during a fast resize. It's a large FRACTION rather than all-or-nothing because when the
    /// published rect and the on-screen dock are briefly out of sync the hole covers only part of the
    /// grab. Real content (even dark) virtually never has a majority of pixels at exactly zero.</summary>
    private static bool LooksBlack(ReadOnlySpan<byte> px, int len)
    {
        int samples = 0, black = 0;
        for (int i = 0; i + 2 < len; i += 128) // every 32nd pixel; test B|G|R, skip the unused X byte
        {
            samples++;
            if ((px[i] | px[i + 1] | px[i + 2]) == 0)
                black++;
        }
        return samples > 0 && black * 100 >= samples * 40; // ≥40% pure-black → treat as a glitch
    }

    private bool Recreate(int w, int h)
    {
        DisposeDib();

        HDC screenDc = PInvoke.GetDC((HWND)IntPtr.Zero);
        if (screenDc.IsNull)
            return false;
        _memDc = PInvoke.CreateCompatibleDC(screenDc);
        PInvoke.ReleaseDC((HWND)IntPtr.Zero, screenDc);
        if (_memDc.IsNull)
            return false;

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h; // top-down rows
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        void* bits;
        _dib = PInvoke.CreateDIBSection(_memDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        if (_dib.IsNull || bits is null)
        {
            DisposeDib();
            return false;
        }
        _bits = bits;
        _prevObj = PInvoke.SelectObject(_memDc, (HGDIOBJ)(void*)_dib);
        _w = w;
        _h = h;
        _prev = new byte[w * 4 * h];
        _havePrev = false;
        return true;
    }

    private void DisposeDib()
    {
        if (!_memDc.IsNull)
        {
            if (!_prevObj.IsNull)
                PInvoke.SelectObject(_memDc, _prevObj);
            if (!_dib.IsNull)
                PInvoke.DeleteObject((HGDIOBJ)(void*)_dib);
            PInvoke.DeleteDC(_memDc);
        }
        _memDc = default;
        _dib = default;
        _prevObj = default;
        _bits = null;
        _w = _h = 0;
        _havePrev = false;
    }

    public void Dispose() => Stop();
}
