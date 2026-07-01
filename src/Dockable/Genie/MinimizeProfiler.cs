using System.Diagnostics;
using System.IO;

namespace Dockable.Genie;

/// <summary>
/// Lightweight per-animation profiler for the minimize/restore effects, enabled by the
/// <c>DOCKABLE_MINIMIZE_PROFILE</c> environment variable. It appends one summary line per animation
/// session (one warp: a minimize or a restore) to <c>%APPDATA%\Dockable\minimize_profile.log</c>:
/// <list type="bullet">
///   <item><c>frames</c>/<c>wall</c>/<c>fps</c> — how many frames the warp took, over how long, and the
///     achieved on-screen frame rate (this is the number the 120/240 Hz target is measured against).</item>
///   <item><c>upd_avg</c>/<c>upd_max</c> — per-frame CPU cost of the mesh/transform update (the work we
///     control). If this is small but fps is low, the bottleneck is GPU compositing, not our math.</item>
///   <item><c>gap_min</c>/<c>gap_avg</c>/<c>gap_max</c> — interval between consecutive rendered frames.</item>
///   <item><c>miss120</c>/<c>miss60</c> — how many frame intervals blew the 120 Hz / 60 Hz budget.</item>
/// </list>
/// Capture cost (BitBlt + bitmap build) is logged separately as it happens. All work is gated behind
/// <see cref="Enabled"/>, so an unset env var costs nothing on the hot path.
/// </summary>
public static class MinimizeProfiler
{
    /// <summary>True when DOCKABLE_MINIMIZE_PROFILE is set (to any non-empty value).</summary>
    public static bool Enabled { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKABLE_MINIMIZE_PROFILE"));

    private const double Budget120Ms = 1000.0 / 120.0; // 8.33 ms
    private const double Budget60Ms = 1000.0 / 60.0;   // 16.67 ms

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _init, _failed;

    // Current session state. Only ever touched on the UI thread, and only one warp animates at a time
    // (the animators share a single overlay), so no per-session locking is needed.
    private static bool _inSession;
    private static string _label = "";
    private static double _srcW, _srcH, _tileW;
    private static int _frames;
    private static double _updSumMs, _updMaxMs;
    private static double _gapSumMs, _gapMinMs, _gapMaxMs;
    private static int _miss120, _miss60;
    private static TimeSpan _startNow, _lastNow;

    /// <summary>Logs a one-shot cost (e.g. a window capture) outside the per-frame accounting.</summary>
    public static void Mark(string label, double ms, int w, int h)
    {
        if (!Enabled)
            return;
        Write($"{label,-16}  {ms,7:F3}ms  {w}x{h}");
    }

    /// <summary>Begins accounting for one warp (a single minimize or restore animation).</summary>
    public static void BeginSession(string label, double srcW, double srcH, double tileW)
    {
        if (!Enabled)
            return;
        _inSession = true;
        _label = label;
        _srcW = srcW;
        _srcH = srcH;
        _tileW = tileW;
        _frames = 0;
        _updSumMs = _updMaxMs = 0;
        _gapSumMs = _gapMaxMs = 0;
        _gapMinMs = double.MaxValue;
        _miss120 = _miss60 = 0;
        _startNow = _lastNow = TimeSpan.Zero;
    }

    /// <summary>Records one rendered frame: its wall-clock timestamp (from <c>RenderingEventArgs</c>) and
    /// the CPU time spent this frame updating the mesh/transform.</summary>
    public static void Frame(TimeSpan now, double updateMs)
    {
        if (!Enabled || !_inSession)
            return;
        if (_frames == 0)
        {
            _startNow = now;
            _lastNow = now;
        }
        else
        {
            double gap = (now - _lastNow).TotalMilliseconds;
            _lastNow = now;
            _gapSumMs += gap;
            if (gap < _gapMinMs) _gapMinMs = gap;
            if (gap > _gapMaxMs) _gapMaxMs = gap;
            if (gap > Budget120Ms) _miss120++;
            if (gap > Budget60Ms) _miss60++;
        }
        _frames++;
        _updSumMs += updateMs;
        if (updateMs > _updMaxMs) _updMaxMs = updateMs;
    }

    /// <summary>Ends the current session and writes its summary line (no-op if none is open).</summary>
    public static void EndSession()
    {
        if (!Enabled || !_inSession)
            return;
        _inSession = false;
        int intervals = Math.Max(0, _frames - 1);
        double wall = (_lastNow - _startNow).TotalMilliseconds;
        double fps = wall > 0 ? intervals / (wall / 1000.0) : 0;
        double updAvg = _frames > 0 ? _updSumMs / _frames : 0;
        double gapAvg = intervals > 0 ? _gapSumMs / intervals : 0;
        double gapMin = intervals > 0 ? _gapMinMs : 0;
        Write($"{_label,-16}  frames={_frames,3}  wall={wall,7:F1}ms  fps={fps,6:F1}  " +
              $"upd_avg={updAvg:F3}ms  upd_max={_updMaxMs:F3}ms  " +
              $"gap_min={gapMin,5:F1}  gap_avg={gapAvg,5:F1}  gap_max={_gapMaxMs,5:F1}ms  " +
              $"miss120={_miss120,2}  miss60={_miss60,2}  src={_srcW:F0}x{_srcH:F0}  tile={_tileW:F0}");
    }

    private static void Write(string msg)
    {
        lock (Gate)
        {
            if (_failed)
                return;
            try
            {
                if (!_init)
                {
                    _init = true;
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockable");
                    Directory.CreateDirectory(dir);
                    _writer = new StreamWriter(
                        new FileStream(Path.Combine(dir, "minimize_profile.log"),
                            FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                    _writer.WriteLine($"=== session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                }
                _writer!.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {msg}");
            }
            catch
            {
                _failed = true; // logging is best-effort; never let it break the dock
            }
        }
    }
}
