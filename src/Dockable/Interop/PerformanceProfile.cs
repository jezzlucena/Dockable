using System.Windows.Media;
using Dockable.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Dockable.Interop;

/// <summary>
/// Central place that decides how much visual fidelity to trade for smoothness, based on the machine's
/// WPF render tier and the user's <see cref="PerformanceMode"/>. Every animation hot path reads its
/// tuning knobs (frame-rate cap, glass capture rate, genie mesh resolution, shadow downgrade)
/// from here so the policy lives in one spot.
///
/// <para><see cref="Tier"/> and <see cref="RefreshHz"/> are read once at startup (they don't change
/// during a session in any way we care about). <see cref="Configure"/> is called whenever the user
/// changes the Performance setting so the derived knobs re-resolve live.</para>
/// </summary>
public static class PerformanceProfile
{
    /// <summary>WPF render tier: 0 = no HW acceleration, 1 = partial, 2 = full GPU.</summary>
    public static int Tier { get; }

    /// <summary>Primary display vertical refresh in Hz (60 if unknown).</summary>
    public static int RefreshHz { get; }

    private static PerformanceMode _mode = PerformanceMode.Auto;

    static PerformanceProfile()
    {
        // RenderCapability.Tier packs the tier in the high word (0/1/2 = none/partial/full GPU).
        Tier = RenderCapability.Tier >> 16;
        RefreshHz = ReadRefreshHz();
    }

    /// <summary>Sets the active performance mode (from settings). Cheap; safe to call on any change.</summary>
    public static void Configure(PerformanceMode mode) => _mode = mode;

    /// <summary>
    /// Whether effects should run in their reduced form. Performance forces it on and Quality forces it
    /// off; Auto reduces only on a less-than-full-hardware GPU (tier &lt; 2).
    /// </summary>
    public static bool Reduced => _mode switch
    {
        PerformanceMode.Performance => true,
        PerformanceMode.Quality => false,
        _ => Tier < 2,
    };

    /// <summary>Target frame rate for the magnification + minimize render loops: half-refresh in
    /// reduced/Performance mode (a consistent 30 beats a struggling 45+), full 60 otherwise.</summary>
    public static int TargetFps => Reduced ? 30 : 60;

    /// <summary>
    /// Minimum wall-clock gap (ms) a render loop must wait before doing its per-frame work again — the
    /// frame-rate cap. Shaved a few ms below the exact interval so a display refreshing at (or near) the
    /// target isn't accidentally halved by tiny vsync jitter; a faster panel (120/144 Hz) is throttled
    /// down toward the target.
    /// </summary>
    public static double MinFrameIntervalMs => System.Math.Max(1.0, 1000.0 / TargetFps - 4.0);

    /// <summary>Fast (active-backdrop) capture rate for the Liquid Glass backdrop capturer. Capped hard
    /// at 30 fps — the glass shows a blurred, refracted backdrop, so a half-refresh rate is barely
    /// perceptible there while halving (or better) the GDI read-back + diff + upload cost — and dropped
    /// to 5 fps in reduced mode so weak machines pay almost nothing for a moving backdrop.</summary>
    public static int GlassCaptureFps => Reduced ? 5 : 30;

    /// <summary>Genie mesh resolution (columns, rows). Coarser when reduced → roughly half the per-frame
    /// vertex buffer WPF re-uploads to the GPU each warp frame.</summary>
    public static (int Columns, int Rows) GenieMesh => Reduced ? (8, 24) : (12, 48);

    /// <summary>When true, icons cast a single (not dual) drop shadow to cut per-frame blur re-rasters.</summary>
    public static bool ReduceIconShadows => Reduced;

    /// <summary>The primary display's vertical refresh in Hz (60 if the device can't report it).</summary>
    private static int ReadRefreshHz()
    {
        HDC dc = PInvoke.GetDC((HWND)nint.Zero);
        if (dc.IsNull)
            return 60;
        try
        {
            int hz = PInvoke.GetDeviceCaps(dc, GET_DEVICE_CAPS_INDEX.VREFRESH);
            return hz > 1 ? hz : 60; // VREFRESH reports 0/1 for the hardware default
        }
        finally
        {
            PInvoke.ReleaseDC((HWND)nint.Zero, dc);
        }
    }
}
