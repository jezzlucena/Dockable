using System.Diagnostics;
using System.IO;

namespace Dockable.Genie;

/// <summary>
/// Lightweight profiler for the Liquid Glass capture/render pipeline, enabled by the
/// <c>DOCKABLE_GLASS_PROFILE</c> environment variable. Appends one summary line per ~1 s window to
/// <c>%APPDATA%\Dockable\glass_profile.log</c>:
/// <list type="bullet">
///   <item><c>tick_fps</c> — how fast the capture timer actually fires (vs <c>target_fps</c>).</item>
///   <item><c>upload_fps</c> — how often the backdrop actually changed, i.e. how often the bitmap was
///     re-uploaded and the shader re-ran. This is the effective on-screen blur frame rate.</item>
///   <item><c>upload_ratio</c> — share of ticks that changed (the rest cost only a BitBlt + diff).</item>
///   <item><c>cap_avg</c>/<c>cap_max</c> — per-tick capture cost (BitBlt + diff + any upload).</item>
///   <item><c>gap_max</c> — largest gap between consecutive ticks (timer jitter / stalls).</item>
/// </list>
/// All work is gated behind <see cref="Enabled"/>, so when the env var is unset the profiler is never
/// constructed and the hot path pays nothing.
/// </summary>
public sealed class GlassProfiler : IDisposable
{
    /// <summary>True when DOCKABLE_GLASS_PROFILE is set (to any non-empty value).</summary>
    public static bool Enabled { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKABLE_GLASS_PROFILE"));

    private const double WindowMs = 1000.0; // emit a summary line roughly once a second

    private readonly string _path;
    private StreamWriter? _writer;
    private bool _failed;

    private readonly Stopwatch _window = Stopwatch.StartNew(); // wall clock for the current window
    private readonly Stopwatch _tick = new();                  // per-tick capture timer
    private double _targetFps;

    private long _ticks, _uploads;
    private double _capSumMs, _capMaxMs;
    private double _lastTickMs, _gapMaxMs;
    private int _w, _h;

    public GlassProfiler()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockable");
        _path = Path.Combine(dir, "glass_profile.log");
        try
        {
            Directory.CreateDirectory(dir);
            _writer = new StreamWriter(
                new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
        catch
        {
            _failed = true; // logging is best-effort; never let it break the dock
        }
    }

    /// <summary>Records pipeline context once the timer rate + GPU render tier are known.</summary>
    public void Begin(double targetFps, int refreshHz, int renderTier)
    {
        _targetFps = targetFps;
        WriteLine($"=== session start  target_fps={targetFps:F0}  refresh={refreshHz}Hz  render_tier={renderTier} ===");
    }

    /// <summary>Call at the very start of a capture tick.</summary>
    public void TickStart() => _tick.Restart();

    /// <summary>Call at the end of a capture tick with the outcome (whether the bitmap was uploaded).</summary>
    public void TickEnd(bool uploaded, int w, int h)
    {
        double cap = _tick.Elapsed.TotalMilliseconds;
        _ticks++;
        if (uploaded) _uploads++;
        _capSumMs += cap;
        if (cap > _capMaxMs) _capMaxMs = cap;

        double nowMs = _window.Elapsed.TotalMilliseconds;
        if (_ticks > 1)
        {
            double gap = nowMs - _lastTickMs;
            if (gap > _gapMaxMs) _gapMaxMs = gap;
        }
        _lastTickMs = nowMs;
        _w = w;
        _h = h;

        if (nowMs >= WindowMs)
            FlushWindow(nowMs);
    }

    private void FlushWindow(double elapsedMs)
    {
        double secs = elapsedMs / 1000.0;
        double tickFps = secs > 0 ? _ticks / secs : 0;
        double uploadFps = secs > 0 ? _uploads / secs : 0;
        double ratio = _ticks > 0 ? 100.0 * _uploads / _ticks : 0;
        double capAvg = _ticks > 0 ? _capSumMs / _ticks : 0;

        WriteLine(
            $"tick_fps={tickFps,6:F1}  upload_fps={uploadFps,6:F1}  upload_ratio={ratio,3:F0}%  " +
            $"cap_avg={capAvg:F3}ms  cap_max={_capMaxMs:F3}ms  gap_max={_gapMaxMs:F1}ms  " +
            $"size={_w}x{_h}  target_fps={_targetFps:F0}");

        _ticks = _uploads = 0;
        _capSumMs = _capMaxMs = 0;
        _gapMaxMs = 0;
        _lastTickMs = 0;
        _window.Restart();
    }

    private void WriteLine(string msg)
    {
        if (_failed || _writer is null)
            return;
        try
        {
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}");
        }
        catch
        {
            _failed = true;
        }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); }
        catch { /* ignore */ }
        _writer = null;
    }
}
