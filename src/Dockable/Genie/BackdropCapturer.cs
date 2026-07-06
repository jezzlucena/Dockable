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
///
/// <b>Capture-friendly mode</b> (<see cref="EnterCaptureFriendly"/>): while the user is screen-capturing
/// (Snipping Tool), the dock's WDA_EXCLUDEFROMCAPTURE is lifted so the dock shows up in their capture —
/// which would also make our own CAPTUREBLT grab see the dock and feed the glass its own rendering
/// (runaway feedback). On entry the capture thread probes whether a plain SRCCOPY blit (no CAPTUREBLT)
/// still omits layered windows (the dock is one) on this Windows build: if it does, capture keeps
/// running <b>live</b> with that ROP; if not (or the backdrop is animating so the probe can't tell),
/// it <b>freezes</b> on the last uploaded frame until <see cref="ExitCaptureFriendly"/>.
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
    // Interest rect (physical px): the sub-region actually shown (the live bar slice of the max-extent
    // grab). The black-glitch test runs on it alone, so a flash covering just the visible slice is
    // caught even when the margins still hold real content.
    private int _ix, _iy, _iw, _ih;
    private bool _haveInterest;

    // GDI resources — created and used ONLY on the capture thread.
    private HDC _memDc;
    private HBITMAP _dib;
    private HGDIOBJ _prevObj;
    private void* _bits;      // the DIB's pixel memory (top-down Bgr32)
    private int _w, _h;
    private byte[]? _prev;    // last frame, for diffing
    private bool _havePrev;

    // Transient all-black grabs happen when the WDA_EXCLUDEFROMCAPTURE dock's window region changes
    // (the idle SetWindowRgn clip is cleared on hover and re-applied when the loop settles): DWM flashes
    // the excluded region black before it recomposites what's behind. Drop a short run of them, but
    // accept sustained black so a genuinely black backdrop still shows through.
    private const int MaxBlackSkips = 4;
    private int _blackSkips;
    private bool _blackReal;

    // Hand-off of a changed frame to the UI thread.
    private readonly object _outLock = new();
    private byte[]? _ready;
    private int _readyW, _readyH;
    private volatile bool _pending;

    // --- Capture-friendly mode (see the class doc) ---
    private enum FriendlyState { Off, Probe, Live, Frozen }

    private volatile int _friendly = (int)FriendlyState.Off; // FriendlyState (volatile enums aren't a thing)
    private long _friendlyEnterMs;  // TickCount64 at entry — DWM needs a beat to recomposite the un-excluded dock
    private int _probeTries;
    private byte[]? _probeA;        // probe scratch (capture-thread only)
    private byte[]? _probeB;

    /// <summary>Enters capture-friendly mode (the dock's capture exclusion was just lifted). Cheap;
    /// safe from any thread. No-op if already in it.</summary>
    public void EnterCaptureFriendly()
    {
        if ((FriendlyState)_friendly != FriendlyState.Off)
            return;
        _probeTries = 0;
        _friendlyEnterMs = Environment.TickCount64;
        _friendly = (int)FriendlyState.Probe;
    }

    /// <summary>Back to normal CAPTUREBLT capture (the dock's capture exclusion is being restored).</summary>
    public void ExitCaptureFriendly() => _friendly = (int)FriendlyState.Off;

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

    /// <summary>Publishes the visible sub-rect (physical px, same space as <see cref="SetRect"/>) that
    /// the black-glitch test should judge. Cheap; safe from any thread.</summary>
    public void SetInterestRect(int x, int y, int w, int h)
    {
        lock (_rectLock)
        {
            _ix = x; _iy = y; _iw = w; _ih = h;
            _haveInterest = true;
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
                double waitMs;
                var friendly = (FriendlyState)_friendly;
                if (friendly == FriendlyState.Frozen)
                {
                    // Parked on the last uploaded frame; just watch for the mode to end.
                    waitMs = 250;
                }
                else if (friendly == FriendlyState.Probe)
                {
                    ProbeOnce();
                    waitMs = 30 - (sw.Elapsed.TotalMilliseconds - t0);
                }
                else
                {
                    bool changed = CaptureOnce(plainRop: friendly == FriendlyState.Live);
                    staticFrames = changed ? 0 : staticFrames + 1;

                    int fps = staticFrames > IdleAfterStaticFrames ? _idleFps : _fastFps;
                    waitMs = 1000.0 / fps - (sw.Elapsed.TotalMilliseconds - t0);
                }
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
    /// <param name="plainRop">Capture-friendly live mode: blit without CAPTUREBLT so the (layered,
    /// no-longer-affinity-excluded) dock stays out of its own backdrop.</param>
    private bool CaptureOnce(bool plainRop)
    {
        int x, y, w, h;
        int ix, iy, iw, ih;
        lock (_rectLock)
        {
            if (!_haveRect)
                return false;
            x = _rx; y = _ry; w = _rw; h = _rh;
            // Interest rect in capture-local coords, clamped; empty/absent → judge the whole frame.
            if (_haveInterest)
            {
                ix = Math.Clamp(_ix - x, 0, w); iy = Math.Clamp(_iy - y, 0, h);
                iw = Math.Clamp(_iw, 0, w - ix); ih = Math.Clamp(_ih, 0, h - iy);
            }
            else
            {
                ix = 0; iy = 0; iw = w; ih = h;
            }
            if (iw == 0 || ih == 0)
            {
                ix = 0; iy = 0; iw = w; ih = h;
            }
        }
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000)
            return false;
        if ((w != _w || h != _h || _dib.IsNull) && !Recreate(w, h))
            return false;

        _profiler?.TickStart(); // measured region: the BitBlt read-back + diff (always paired with TickEnd)
        if (!Blit(x, y, w, h, plainRop ? ROP_CODE.SRCCOPY : CaptureRop))
        {
            _profiler?.TickEnd(false, w, h);
            return false;
        }

        int len = w * 4 * h;
        var cur = new ReadOnlySpan<byte>(_bits, len);

        // Drop transient all-black grabs (the exclude-from-capture hole flashing after a region change),
        // but stop dropping once black persists — that's a real black backdrop, not a glitch.
        if (LooksBlack(cur, w, ix, iy, iw, ih))
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

    /// <summary>One screen-DC blit into the reusable DIB.</summary>
    private bool Blit(int x, int y, int w, int h, ROP_CODE rop)
    {
        HDC screenDc = PInvoke.GetDC((HWND)IntPtr.Zero);
        if (screenDc.IsNull)
            return false;
        try
        {
            return PInvoke.BitBlt(_memDc, 0, 0, w, h, screenDc, x, y, rop);
        }
        finally
        {
            PInvoke.ReleaseDC((HWND)IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Capture-friendly probe: with the dock's exclusion lifted, three back-to-back grabs — CAPTUREBLT
    /// (dock included), plain SRCCOPY, CAPTUREBLT again. If the two CAPTUREBLT grabs match (backdrop
    /// held still through the probe) and the SRCCOPY one differs strongly (the layered dock is absent
    /// from it), a plain blit is a safe live path → <see cref="FriendlyState.Live"/>. Otherwise retry
    /// briefly, then park on the last frame (<see cref="FriendlyState.Frozen"/>) — the safe verdict.
    /// </summary>
    private void ProbeOnce()
    {
        if (Environment.TickCount64 - _friendlyEnterMs < 150)
            return; // DWM may still be recompositing the un-excluded dock (transient black/torn grabs)

        int x, y, w, h;
        int ix, iy, iw, ih;
        lock (_rectLock)
        {
            if (!_haveRect)
                return;
            x = _rx; y = _ry; w = _rw; h = _rh;
            if (_haveInterest)
            {
                ix = Math.Clamp(_ix - x, 0, w); iy = Math.Clamp(_iy - y, 0, h);
                iw = Math.Clamp(_iw, 0, w - ix); ih = Math.Clamp(_ih, 0, h - iy);
            }
            else
            {
                ix = 0; iy = 0; iw = w; ih = h;
            }
            if (iw == 0 || ih == 0)
            {
                ix = 0; iy = 0; iw = w; ih = h;
            }
        }
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000 || ((w != _w || h != _h || _dib.IsNull) && !Recreate(w, h)))
        {
            RetryOrFreeze();
            return;
        }

        int len = w * 4 * h;
        if (_probeA is null || _probeA.Length < len)
            _probeA = new byte[len];
        if (_probeB is null || _probeB.Length < len)
            _probeB = new byte[len];

        var cur = new ReadOnlySpan<byte>(_bits, len);
        if (!Blit(x, y, w, h, CaptureRop)) { RetryOrFreeze(); return; }
        cur.CopyTo(_probeA);
        if (!Blit(x, y, w, h, ROP_CODE.SRCCOPY)) { RetryOrFreeze(); return; }
        cur.CopyTo(_probeB);
        if (!Blit(x, y, w, h, CaptureRop)) { RetryOrFreeze(); return; }

        // Transitional black frames (DWM recompositing) → try again shortly.
        if (LooksBlack(_probeA, w, ix, iy, iw, ih) || LooksBlack(_probeB, w, ix, iy, iw, ih)
            || LooksBlack(cur, w, ix, iy, iw, ih))
        {
            RetryOrFreeze();
            return;
        }

        if (DiffPercent(_probeA, cur, w, ix, iy, iw, ih) > 2.0)
        {
            RetryOrFreeze(); // backdrop moved mid-probe (video playing) — the verdict would be unreliable
            return;
        }

        if (DiffPercent(_probeA, _probeB, w, ix, iy, iw, ih) >= 12.0)
        {
            // SRCCOPY omits the layered dock on this build → keep capturing LIVE with it.
            _havePrev = false; // the retained prev frame predates the mode; force the next frame through
            _friendly = (int)FriendlyState.Live;
        }
        else
        {
            // The plain blit sees the dock too (or the bar is indistinguishable from its backdrop) —
            // live capture would feed back. Park on the last good frame.
            _friendly = (int)FriendlyState.Frozen;
        }
    }

    private void RetryOrFreeze()
    {
        if (++_probeTries >= 20) // ~600 ms of trying (e.g. a video behind the bar never holds still)
            _friendly = (int)FriendlyState.Frozen;
    }

    /// <summary>Percentage of sampled interest-region pixels whose colour differs noticeably between
    /// two frames. Same sampling lattice as <see cref="LooksBlack"/>.</summary>
    private static double DiffPercent(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int frameW,
        int ix, int iy, int iw, int ih)
    {
        int samples = 0, moved = 0;
        int stride = frameW * 4;
        for (int y = iy; y < iy + ih; y += 4)       // every 4th row
        {
            int row = y * stride;
            for (int x = ix; x < ix + iw; x += 32)  // every 32nd pixel
            {
                int i = row + x * 4;
                samples++;
                if (Math.Abs(a[i] - b[i]) > 12 || Math.Abs(a[i + 1] - b[i + 1]) > 12
                    || Math.Abs(a[i + 2] - b[i + 2]) > 12)
                    moved++;
            }
        }
        return samples == 0 ? 0 : moved * 100.0 / samples;
    }

    /// <summary>True when most of the visible (interest) region is exact RGB 0,0,0 — the
    /// exclude-from-capture hole flashing black while DWM recomposites after the dock's window region
    /// changes. Judged on the interest region only: the max-extent grab's margins may hold real content
    /// while the bar slice — the part the user sees — is blacked out. It's a large FRACTION rather than
    /// all-or-nothing because the hole and the slice never align exactly; real content (even dark)
    /// virtually never has a majority of pixels at exactly zero.</summary>
    private static bool LooksBlack(ReadOnlySpan<byte> px, int frameW, int ix, int iy, int iw, int ih)
    {
        int samples = 0, black = 0;
        int stride = frameW * 4;
        for (int y = iy; y < iy + ih; y += 4)       // every 4th row
        {
            int row = y * stride;
            for (int x = ix; x < ix + iw; x += 32)  // every 32nd pixel; test B|G|R, skip the unused X byte
            {
                int i = row + x * 4;
                samples++;
                if ((px[i] | px[i + 1] | px[i + 2]) == 0)
                    black++;
            }
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
