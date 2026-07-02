using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Dockable.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Genie;

/// <summary>
/// Plays a macOS-style "genie" warp: a captured window image mapped onto a fine grid mesh
/// that flows and pinches into the dock target over a short duration. Rendered with WPF 3D
/// (Viewport3D + animated MeshGeometry3D).
///
/// The overlay window is created once and reused (pre-warmed): building a fresh WPF window per
/// minimize costs tens-to-hundreds of ms, during which the (already-hidden) window leaves a
/// visible gap. The reusable overlay shows in ~1 frame, so the warp begins seamlessly. It spans
/// the whole virtual screen and is click-through, so it never interferes when idle.
///
/// Coordinates are device-independent (DIP); rects are in virtual-screen DIPs.
/// </summary>
public sealed class GenieAnimator : IMinimizeAnimator
{
    // Mesh resolution. Finer horizontally so the Suck black-hole spiral stays smooth; resolved from
    // PerformanceProfile at overlay-build time (coarser when reduced → less per-frame GPU buffer upload).
    private int Columns = 12;
    private int Rows = 48;

    /// <summary>Which curve the mesh warp uses; both share the engine, differing only in shaping.</summary>
    public enum GenieStyle { Suck, Genie }

    /// <summary>Per-style curve parameters.</summary>
    /// <param name="Stagger">How far the leading rows run ahead of the trailing ones (the flow).</param>
    /// <param name="TargetWidth">Neck/point width at full warp (DIP).</param>
    /// <param name="WidthBulge">Mid-neck width bulge as a fraction of the source width (negative pinches).</param>
    /// <param name="Duration">Animation length in ms.</param>
    /// <param name="ShapeEnd">Local-progress point by which the horizontal funnel/pinch is complete
    /// (&lt;1 front-loads the distortion). 1.0 = horizontal tracks the descent (the old unified curve).</param>
    /// <param name="DescendStart">Local-progress point at which the vertical descent begins (&gt;0
    /// back-loads the drop). 0 = descend from the start.</param>
    private readonly record struct StyleParams(
        double Stagger, double TargetWidth, double WidthBulge, double Duration, double ShapeEnd, double DescendStart);

    private static StyleParams ParamsFor(GenieStyle style) => style switch
    {
        // Smoke flowing into a bottle: the horizontal shrink is focused on the first ~2/3 (and only goes
        // down to the tile width, not a point), while the vertical glide runs the whole time — so the two
        // move together but the bottleneck shape is mostly formed before it lands. TargetWidth is unused
        // for Genie (the neck width comes from TargetTileWidth).
        GenieStyle.Genie => new StyleParams(Stagger: 0.5, TargetWidth: 6, WidthBulge: 0.35, Duration: 430,
            ShapeEnd: 0.66, DescendStart: 0.0),
        // Black hole: every point is dragged straight toward the target, nearest points first — so the
        // window stretches and collapses into the spot. Stagger = how strongly nearer points lead.
        _ => new StyleParams(Stagger: 0.95, TargetWidth: 2, WidthBulge: -0.08, Duration: 300,
            ShapeEnd: 1.0, DescendStart: 0.0),
    };

    /// <summary>Which curve to warp with; set before each play (defaults to the Suck funnel).</summary>
    public GenieStyle Style { get; set; } = GenieStyle.Suck;

    /// <summary>Speed multiplier; &gt;1 shortens the duration (faster), &lt;1 lengthens it (slower).</summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>Dock tile width (DIP). For the Genie style the window's width shrinks only down to this
    /// (so it lands looking like the thumbnail, not a thin neck). The Suck funnel ignores it (pinches to
    /// a point). Set before each play.</summary>
    public double TargetTileWidth { get; set; } = 56;

    private Window? _overlay;
    private MeshGeometry3D? _mesh;
    private Point3DCollection? _positions; // == _mesh.Positions; detached while bulk-mutated each frame
    private ImageBrush? _brush;
    private OrthographicCamera? _camera;
    private EventHandler? _rendering;

    // Per-play precomputed invariants (depend only on _src/_target/_leadFromTop, fixed across a play's
    // frames): source vertex coords, the black-hole fall-in lag, and the genie per-row lead/origin-Y.
    // Recomputing these (esp. the per-vertex sqrt distance) every frame was pure waste.
    private int _vertexCount;
    private double[]? _ox, _oy;          // source vertex positions (overlay-local DIP)
    private double[]? _lag;              // black-hole normalized distance-to-target (0 near … 1 far)
    private double[]? _leadRow;          // genie per-row flow lead (0 = leads, 1 = trails)
    private double[]? _origYRow;         // genie per-row source Y

    // Current animation state.
    private Rect _src;
    private Point _target;
    private Point _monitorOrigin; // monitor top-left (DIP), to convert later AnimateTo targets
    private double _height;
    private bool _reverse;
    // Which window edge leads the warp: the edge nearest the dock tile. Top-anchored docks lead from
    // the top (flow downward→up into the tile); bottom-anchored docks lead from the bottom.
    private bool _leadFromTop;
    private StyleParams _params = ParamsFor(GenieStyle.Suck); // resolved at the start of each play
    private Action? _onCompleted;
    private TimeSpan _startTime;
    private TimeSpan _lastFrame; // last painted frame's RenderingTime, for the frame-rate cap
    // Bumped whenever new content is shown on the shared overlay. A deferred restore hide (see
    // CompleteRestoreHold) checks it so it never hides a frame that a newer animation now owns.
    private int _playSeq;

    /// <summary>Builds the reusable overlay ahead of time so the first genie is as fast as the rest.</summary>
    public void Prewarm() => EnsureOverlay();

    public void Play(BitmapSource bitmap, Rect sourceDip, Point targetDip, Rect monitorDip, bool reverse, Action? onCompleted)
    {
        EnsureOverlay();
        FinishCurrent(); // only one warp at a time on the shared overlay — finalize any in-flight one first
        SyncOverlay(monitorDip);

        _brush!.ImageSource = bitmap;
        _monitorOrigin = new Point(monitorDip.Left, monitorDip.Top);
        _src = new Rect(sourceDip.Left - monitorDip.Left, sourceDip.Top - monitorDip.Top, sourceDip.Width, sourceDip.Height);
        _target = new Point(targetDip.X - monitorDip.Left, targetDip.Y - monitorDip.Top);
        _height = monitorDip.Height;
        _reverse = reverse;
        _leadFromTop = LeadsFromTop();
        _params = ParamsFor(Style);
        _onCompleted = onCompleted;
        _startTime = TimeSpan.Zero;
        _lastFrame = TimeSpan.Zero;

        PreparePlay();
        UpdateMesh(reverse ? 1.0 : 0.0);
        _playSeq++;
        _overlay!.Visibility = Visibility.Visible;

        if (MinimizeProfiler.Enabled)
            MinimizeProfiler.BeginSession($"{Style}/{(reverse ? "restore" : "min")}", _src.Width, _src.Height, TargetTileWidth);

        if (_rendering is null)
        {
            _rendering = OnRendering;
            CompositionTarget.Rendering += _rendering;
        }
    }

    public void ShowAtSource(BitmapSource bitmap, Rect sourceDip, Rect monitorDip)
    {
        EnsureOverlay();
        FinishCurrent(); // finalize any in-flight warp so its window lands and frees up before this one
        SyncOverlay(monitorDip); // size the reusable overlay to the relevant monitor

        _brush!.ImageSource = bitmap;
        _monitorOrigin = new Point(monitorDip.Left, monitorDip.Top);
        _src = new Rect(sourceDip.Left - monitorDip.Left, sourceDip.Top - monitorDip.Top, sourceDip.Width, sourceDip.Height);
        _height = monitorDip.Height;
        _target = new Point(_src.Left + _src.Width / 2, _src.Top + _src.Height / 2); // until AnimateTo
        _reverse = false;
        _params = ParamsFor(Style);
        _onCompleted = null;

        PreparePlay();
        UpdateMesh(0.0); // un-warped: the window exactly where it was
        _playSeq++;
        _overlay!.Visibility = Visibility.Visible;
    }

    public void AnimateTo(Point targetDip, bool reverse, Action? onCompleted)
    {
        EnsureOverlay();
        _target = new Point(targetDip.X - _monitorOrigin.X, targetDip.Y - _monitorOrigin.Y);
        // (No FinishCurrent here: AnimateTo follows our own ShowAtSource, which already finalized any
        // prior warp; ShowAtSource doesn't start the render loop, so nothing of ours is in flight.)
        _reverse = reverse;
        _leadFromTop = LeadsFromTop();
        _params = ParamsFor(Style);
        _onCompleted = onCompleted;
        _startTime = TimeSpan.Zero;
        _lastFrame = TimeSpan.Zero;

        PreparePlay();
        UpdateMesh(reverse ? 1.0 : 0.0);
        _playSeq++;
        _overlay!.Visibility = Visibility.Visible;

        if (MinimizeProfiler.Enabled)
            MinimizeProfiler.BeginSession($"{Style}/{(reverse ? "restore" : "min")}", _src.Width, _src.Height, TargetTileWidth);

        if (_rendering is null)
        {
            _rendering = OnRendering;
            CompositionTarget.Rendering += _rendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_startTime == TimeSpan.Zero)
            _startTime = now;

        double duration = _params.Duration / Math.Max(0.1, SpeedMultiplier);
        double progress = Math.Min(1.0, (now - _startTime).TotalMilliseconds / duration);
        double warp = _reverse ? 1.0 - progress : progress;

        // Frame-rate cap: skip this frame's mesh rebuild if we painted too recently — but never skip the
        // final frame, so the warp always lands and its completion callback runs.
        if (progress < 1.0 && _lastFrame != TimeSpan.Zero
            && (now - _lastFrame).TotalMilliseconds < PerformanceProfile.MinFrameIntervalMs)
            return;
        _lastFrame = now;

        long ts = MinimizeProfiler.Enabled ? Stopwatch.GetTimestamp() : 0;
        UpdateMesh(warp);
        if (MinimizeProfiler.Enabled)
            MinimizeProfiler.Frame(now, Stopwatch.GetElapsedTime(ts).TotalMilliseconds);

        if (progress >= 1.0)
        {
            CompositionTarget.Rendering -= _rendering!;
            _rendering = null;
            MinimizeProfiler.EndSession();
            var done = _onCompleted;
            _onCompleted = null;
            if (_reverse)
            {
                // Restore: keep the final captured frame on the (topmost) overlay and let `done` bring the
                // real window up beneath it, then hide the capture only once the window has painted — so
                // there's no blank blink between the capture vanishing and the window appearing.
                CompleteRestoreHold(done);
            }
            else
            {
                _overlay!.Visibility = Visibility.Hidden;
                done?.Invoke();
            }
        }
    }

    /// <summary>
    /// Ends a restore by holding the final capture on the overlay while the real window is restored
    /// underneath, then hiding the capture after the window has had a couple of frames to paint. The hide
    /// is abandoned if a newer play has since taken over the shared overlay (tracked by <see cref="_playSeq"/>).
    /// </summary>
    private void CompleteRestoreHold(Action? done)
    {
        done?.Invoke(); // bring the real window up beneath the still-visible capture

        int seq = _playSeq;
        int ticks = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (_playSeq != seq) // a newer animation now owns the overlay — this hide is stale
            {
                CompositionTarget.Rendering -= handler!;
                return;
            }
            if (++ticks < 2) // let the restored window paint for a frame or two first
                return;
            CompositionTarget.Rendering -= handler!;
            if (_playSeq == seq)
                _overlay!.Visibility = Visibility.Hidden;
        };
        CompositionTarget.Rendering += handler;
    }

    /// <summary>
    /// Ends any animation currently in flight by running its completion callback now (the previous
    /// window snaps to its tile and is freed) — the shared overlay can only show one warp at a time, so
    /// starting a new one must not silently drop the old one's onCompleted (which would leave it stuck).
    /// </summary>
    private void FinishCurrent()
    {
        if (_rendering is null)
            return;
        CompositionTarget.Rendering -= _rendering;
        _rendering = null;
        MinimizeProfiler.EndSession();
        var done = _onCompleted;
        _onCompleted = null;
        done?.Invoke();
    }

    /// <summary>Re-resolves the mesh resolution from <see cref="PerformanceProfile"/> after a mode change.
    /// The overlay is idle (hidden) between warps, so tearing it down here lets the next play rebuild it
    /// at the new resolution. A no-op when the resolution is unchanged.</summary>
    public void RefreshQuality()
    {
        var (cols, rows) = PerformanceProfile.GenieMesh;
        if (_overlay is null || (cols == Columns && rows == Rows))
            return;
        FinishCurrent(); // finalize any in-flight warp (should be none while idle) so nothing is stranded
        _overlay.Close();
        _overlay = null; // next EnsureOverlay rebuilds the mesh + arrays at the new resolution
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null)
            return;

        (Columns, Rows) = PerformanceProfile.GenieMesh;
        _mesh = BuildMesh();
        _positions = _mesh.Positions; // held so we can detach/reattach it cheaply each frame

        _vertexCount = (Columns + 1) * (Rows + 1);
        _ox = new double[_vertexCount];
        _oy = new double[_vertexCount];
        _lag = new double[_vertexCount];
        _leadRow = new double[Rows + 1];
        _origYRow = new double[Rows + 1];

        _brush = new ImageBrush { Stretch = Stretch.Fill };
        var model = new GeometryModel3D(_mesh, new DiffuseMaterial(_brush));

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Colors.White)); // unshaded: show the texture as-is
        group.Children.Add(model);

        _camera = new OrthographicCamera(new Point3D(0, 0, 100), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 100);
        var viewport = new Viewport3D { Camera = _camera };
        viewport.Children.Add(new ModelVisual3D { Content = group });

        _overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = viewport,
        };

        // Default to the primary screen; resized to the active monitor on each Play.
        _overlay.Left = 0;
        _overlay.Top = 0;
        _overlay.Width = SystemParameters.PrimaryScreenWidth;
        _overlay.Height = SystemParameters.PrimaryScreenHeight;
        _overlay.Show();          // create the HWND
        MakeClickThrough(_overlay);
        _overlay.Visibility = Visibility.Hidden; // idle until a genie plays
    }

    /// <summary>Sizes the overlay to the given monitor and matches the camera to it.</summary>
    private void SyncOverlay(Rect monitorDip)
    {
        _overlay!.Left = monitorDip.Left;
        _overlay.Top = monitorDip.Top;
        _overlay.Width = monitorDip.Width;
        _overlay.Height = monitorDip.Height;

        _camera!.Width = monitorDip.Width;
        _camera.Position = new Point3D(monitorDip.Width / 2, monitorDip.Height / 2, 100);
    }

    private static void MakeClickThrough(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)ex);
    }

    private MeshGeometry3D BuildMesh()
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection((Columns + 1) * (Rows + 1)),
            TextureCoordinates = new PointCollection((Columns + 1) * (Rows + 1)),
            TriangleIndices = new Int32Collection(Columns * Rows * 6),
        };

        for (int j = 0; j <= Rows; j++)
        {
            double v = (double)j / Rows;
            for (int i = 0; i <= Columns; i++)
            {
                double u = (double)i / Columns;
                mesh.Positions.Add(new Point3D(0, 0, 0)); // filled in by UpdateMesh
                mesh.TextureCoordinates.Add(new Point(u, v));
            }
        }

        int rowStride = Columns + 1;
        for (int j = 0; j < Rows; j++)
        {
            for (int i = 0; i < Columns; i++)
            {
                int topLeft = j * rowStride + i;
                int topRight = topLeft + 1;
                int bottomLeft = topLeft + rowStride;
                int bottomRight = bottomLeft + 1;

                mesh.TriangleIndices.Add(topLeft);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(topRight);

                mesh.TriangleIndices.Add(topRight);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(bottomRight);
            }
        }

        return mesh;
    }

    /// <summary>
    /// Precomputes everything that stays fixed for the whole play (source vertex coords, the black-hole
    /// fall-in lag with its per-vertex distance, and the genie per-row lead/origin) so the per-frame
    /// <see cref="UpdateMesh"/> only does the cheap warp interpolation — no <c>sqrt</c> per vertex per
    /// frame. Call after <c>_src</c>/<c>_target</c>/<c>_leadFromTop</c> are set for this play.
    /// </summary>
    private void PreparePlay()
    {
        var src = _src;
        double tx = _target.X, ty = _target.Y;
        int rowStride = Columns + 1;

        for (int j = 0; j <= Rows; j++)
        {
            double v = (double)j / Rows;
            double rowY = src.Top + v * src.Height;
            _origYRow![j] = rowY;
            // The edge nearest the dock leads the flow: top rows (v=0) for a top-anchored dock, bottom
            // rows (v=1) otherwise.
            _leadRow![j] = _leadFromTop ? v : 1 - v;
            for (int i = 0; i <= Columns; i++)
            {
                int idx = j * rowStride + i;
                _ox![idx] = src.Left + (double)i / Columns * src.Width;
                _oy![idx] = rowY;
            }
        }

        // Black-hole fall-in lag: normalized distance to the target (0 at the target … 1 at the farthest
        // corner), so the vertices nearest the target arrive first. Constant across the play's frames.
        double maxDist = 1e-3;
        maxDist = Math.Max(maxDist, Distance(src.Left - tx, src.Top - ty));
        maxDist = Math.Max(maxDist, Distance(src.Right - tx, src.Top - ty));
        maxDist = Math.Max(maxDist, Distance(src.Left - tx, src.Bottom - ty));
        maxDist = Math.Max(maxDist, Distance(src.Right - tx, src.Bottom - ty));
        double invMax = 1.0 / maxDist;
        for (int idx = 0; idx < _vertexCount; idx++)
            _lag![idx] = Clamp01(Distance(_ox![idx] - tx, _oy![idx] - ty) * invMax);
    }

    /// <summary>Recomputes vertex positions for a given warp amount, per the active style.</summary>
    private void UpdateMesh(double warp)
    {
        if (Style == GenieStyle.Suck)
            UpdateMeshBlackHole(warp);
        else
            UpdateMeshGenie(warp);
    }

    /// <summary>
    /// Black-hole collapse: every vertex is pulled straight toward the target, with the vertices nearest
    /// the target arriving first (so the window stretches and collapses into the point — gravity, not a
    /// uniform funnel). At full warp everything reaches the target (the thumbnail's spot).
    /// </summary>
    private void UpdateMeshBlackHole(double warp)
    {
        var mesh = _mesh!;
        var positions = _positions!;
        double tx = _target.X, ty = _target.Y, h = _height;
        double stagger = _params.Stagger;
        double baseProgress = warp * (1 + stagger);

        // Detach the collection so the 637 element writes don't each propagate a change notification up
        // to the mesh; reattach once at the end for a single re-realization of the vertex buffer.
        mesh.Positions = null;
        for (int idx = 0; idx < _vertexCount; idx++)
        {
            double e = SmoothStep(Clamp01(baseProgress - _lag![idx] * stagger));
            double x = Lerp(_ox![idx], tx, e); // straight pull toward the thumbnail's spot
            double y = Lerp(_oy![idx], ty, e);
            positions[idx] = new Point3D(x, h - y, 0);
        }
        mesh.Positions = positions;
    }

    /// <summary>
    /// Genie funnel: rows lead in a staggered flow, pinching their width to the tile and sliding toward
    /// the target — the smoke-into-a-bottle neck.
    /// </summary>
    private void UpdateMeshGenie(double warp)
    {
        var mesh = _mesh!;
        var positions = _positions!;
        var src = _src;
        var target = _target;
        var p = _params;
        double srcCenterX = src.Left + src.Width / 2;
        int rowStride = Columns + 1;
        double neckWidth = TargetTileWidth; // shrink only to the tile width (lands as the thumbnail)
        double h = _height;
        double baseProgress = warp * (1 + p.Stagger);
        double invShapeEnd = 1.0 / p.ShapeEnd;
        double invDescendSpan = 1.0 / (1 - p.DescendStart);

        mesh.Positions = null; // detach for cheap bulk mutation (see UpdateMeshBlackHole)
        for (int j = 0; j <= Rows; j++)
        {
            double lp = Clamp01(baseProgress - _leadRow![j] * p.Stagger);
            // Decouple the horizontal shaping from the vertical descent: the funnel/pinch front-loads
            // (done by ShapeEnd) so the neck forms early, then the drop happens (starting at DescendStart).
            double eShape = SmoothStep(Clamp01(lp * invShapeEnd));
            double eDescend = SmoothStep(Clamp01((lp - p.DescendStart) * invDescendSpan));

            double rowCenterX = Lerp(srcCenterX, target.X, eShape);
            // Width tapers from the body to the neck; the bulge term bellies the mid-neck out (smoke
            // into a bottle) for the Genie style, or pinches it (negative) for the rigid Suck funnel.
            double baseWidth = Lerp(src.Width, neckWidth, eShape);
            double bulge = p.WidthBulge * src.Width * Math.Sin(Math.PI * eShape);
            double rowWidth = Math.Max(neckWidth, baseWidth + bulge);
            // 3D Y is up; screen Y is down — flip into the orthographic camera's space.
            double yUp = h - Lerp(_origYRow![j], target.Y, eDescend);

            int rowBase = j * rowStride;
            for (int i = 0; i <= Columns; i++)
            {
                double x = rowCenterX + ((double)i / Columns - 0.5) * rowWidth;
                positions[rowBase + i] = new Point3D(x, yUp, 0);
            }
        }
        mesh.Positions = positions;
    }

    /// <summary>The tile is above the window's center → the dock is on the top edge, so lead from the top.</summary>
    private bool LeadsFromTop() => _target.Y < _src.Top + _src.Height / 2;

    private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double SmoothStep(double t) => t * t * (3 - 2 * t);
    private static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
}
