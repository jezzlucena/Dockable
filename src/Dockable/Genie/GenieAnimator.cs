using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
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
    private const int Columns = 12; // finer horizontally so the Suck black-hole spiral stays smooth
    private const int Rows = 48;

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
    private ImageBrush? _brush;
    private OrthographicCamera? _camera;
    private EventHandler? _rendering;

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

        UpdateMesh(reverse ? 1.0 : 0.0);
        _overlay!.Visibility = Visibility.Visible;

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

        UpdateMesh(0.0); // un-warped: the window exactly where it was
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

        UpdateMesh(reverse ? 1.0 : 0.0);
        _overlay!.Visibility = Visibility.Visible;

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
        UpdateMesh(warp);

        if (progress >= 1.0)
        {
            CompositionTarget.Rendering -= _rendering!;
            _rendering = null;
            _overlay!.Visibility = Visibility.Hidden;
            var done = _onCompleted;
            _onCompleted = null;
            done?.Invoke();
        }
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
        var done = _onCompleted;
        _onCompleted = null;
        done?.Invoke();
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null)
            return;

        _mesh = BuildMesh();
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

    private static MeshGeometry3D BuildMesh()
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
        var src = _src;
        var p = _params;
        var positions = mesh.Positions;
        int rowStride = Columns + 1;
        double tx = _target.X, ty = _target.Y;

        // Normalize the per-vertex "fall-in" stagger by the farthest source corner from the target.
        double maxDist = 1e-3;
        maxDist = Math.Max(maxDist, Distance(src.Left - tx, src.Top - ty));
        maxDist = Math.Max(maxDist, Distance(src.Right - tx, src.Top - ty));
        maxDist = Math.Max(maxDist, Distance(src.Left - tx, src.Bottom - ty));
        maxDist = Math.Max(maxDist, Distance(src.Right - tx, src.Bottom - ty));

        for (int j = 0; j <= Rows; j++)
        {
            double v = (double)j / Rows;
            for (int i = 0; i <= Columns; i++)
            {
                double u = (double)i / Columns;
                double ox = src.Left + u * src.Width;
                double oy = src.Top + v * src.Height;

                // Normalized distance to the target (0 at the target … 1 at the farthest corner). It's
                // subtracted from the progress, so the farther a vertex is the more it lags — i.e. the
                // vertices nearest the target arrive first.
                double lag = Clamp01(Distance(ox - tx, oy - ty) / maxDist);
                double e = SmoothStep(Clamp01(warp * (1 + p.Stagger) - lag * p.Stagger));

                double x = Lerp(ox, tx, e); // straight pull toward the thumbnail's spot
                double y = Lerp(oy, ty, e);
                positions[j * rowStride + i] = new Point3D(x, _height - y, 0);
            }
        }
    }

    /// <summary>
    /// Genie funnel: rows lead in a staggered flow, pinching their width to the tile and sliding toward
    /// the target — the smoke-into-a-bottle neck.
    /// </summary>
    private void UpdateMeshGenie(double warp)
    {
        var mesh = _mesh!;
        var src = _src;
        var target = _target;
        var p = _params;
        var positions = mesh.Positions;
        double srcCenterX = src.Left + src.Width / 2;
        int rowStride = Columns + 1;
        double neckWidth = TargetTileWidth; // shrink only to the tile width (lands as the thumbnail)

        for (int j = 0; j <= Rows; j++)
        {
            double v = (double)j / Rows;
            // The edge nearest the dock leads the flow: the top rows (v=0) for a top-anchored dock,
            // the bottom rows (v=1) otherwise.
            double lead = _leadFromTop ? v : 1 - v;
            double lp = Clamp01(warp * (1 + p.Stagger) - lead * p.Stagger);
            // Decouple the horizontal shaping from the vertical descent: the funnel/pinch front-loads
            // (done by ShapeEnd) so the neck forms early, then the drop happens (starting at DescendStart).
            double eShape = SmoothStep(Clamp01(lp / p.ShapeEnd));
            double eDescend = SmoothStep(Clamp01((lp - p.DescendStart) / (1 - p.DescendStart)));

            double rowCenterX = Lerp(srcCenterX, target.X, eShape);
            // Width tapers from the body to the neck; the bulge term bellies the mid-neck out (smoke
            // into a bottle) for the Genie style, or pinches it (negative) for the rigid Suck funnel.
            double baseWidth = Lerp(src.Width, neckWidth, eShape);
            double bulge = p.WidthBulge * src.Width * Math.Sin(Math.PI * eShape);
            double rowWidth = Math.Max(neckWidth, baseWidth + bulge);
            double origY = src.Top + v * src.Height;
            double y = Lerp(origY, target.Y, eDescend);

            for (int i = 0; i <= Columns; i++)
            {
                double u = (double)i / Columns;
                double x = rowCenterX + (u - 0.5) * rowWidth;
                // 3D Y is up; screen Y is down — flip into the orthographic camera's space.
                positions[j * rowStride + i] = new Point3D(x, _height - y, 0);
            }
        }
    }

    /// <summary>The tile is above the window's center → the dock is on the top edge, so lead from the top.</summary>
    private bool LeadsFromTop() => _target.Y < _src.Top + _src.Height / 2;

    private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double SmoothStep(double t) => t * t * (3 - 2 * t);
    private static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
}
