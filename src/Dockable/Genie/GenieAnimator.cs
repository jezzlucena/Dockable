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
    private const int Columns = 6;
    private const int Rows = 48;
    private const double Stagger = 0.7;
    private const double TargetWidth = 4;
    private const double DurationMs = 360;

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
    private Action? _onCompleted;
    private TimeSpan _startTime;

    /// <summary>Builds the reusable overlay ahead of time so the first genie is as fast as the rest.</summary>
    public void Prewarm() => EnsureOverlay();

    public void Play(BitmapSource bitmap, Rect sourceDip, Point targetDip, Rect monitorDip, bool reverse, Action? onCompleted)
    {
        EnsureOverlay();
        SyncOverlay(monitorDip);

        _brush!.ImageSource = bitmap;
        _monitorOrigin = new Point(monitorDip.Left, monitorDip.Top);
        _src = new Rect(sourceDip.Left - monitorDip.Left, sourceDip.Top - monitorDip.Top, sourceDip.Width, sourceDip.Height);
        _target = new Point(targetDip.X - monitorDip.Left, targetDip.Y - monitorDip.Top);
        _height = monitorDip.Height;
        _reverse = reverse;
        _onCompleted = onCompleted;
        _startTime = TimeSpan.Zero;

        UpdateMesh(_mesh!, _src, _target, _height, reverse ? 1.0 : 0.0);
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
        SyncOverlay(monitorDip); // size the reusable overlay to the relevant monitor

        _brush!.ImageSource = bitmap;
        _monitorOrigin = new Point(monitorDip.Left, monitorDip.Top);
        _src = new Rect(sourceDip.Left - monitorDip.Left, sourceDip.Top - monitorDip.Top, sourceDip.Width, sourceDip.Height);
        _height = monitorDip.Height;
        _target = new Point(_src.Left + _src.Width / 2, _src.Top + _src.Height / 2); // until AnimateTo
        _reverse = false;
        _onCompleted = null;

        UpdateMesh(_mesh!, _src, _target, _height, 0.0); // un-warped: the window exactly where it was
        _overlay!.Visibility = Visibility.Visible;
    }

    public void AnimateTo(Point targetDip, bool reverse, Action? onCompleted)
    {
        EnsureOverlay();
        _target = new Point(targetDip.X - _monitorOrigin.X, targetDip.Y - _monitorOrigin.Y);
        _reverse = reverse;
        _onCompleted = onCompleted;
        _startTime = TimeSpan.Zero;

        UpdateMesh(_mesh!, _src, _target, _height, reverse ? 1.0 : 0.0);
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

        double progress = Math.Min(1.0, (now - _startTime).TotalMilliseconds / DurationMs);
        double warp = _reverse ? 1.0 - progress : progress;
        UpdateMesh(_mesh!, _src, _target, _height, warp);

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

    /// <summary>
    /// Recomputes vertex positions for a given warp amount. Lower rows lead (a staggered flow),
    /// pinching their width and sliding toward the target, producing the genie neck/funnel.
    /// </summary>
    private static void UpdateMesh(MeshGeometry3D mesh, Rect src, Point target, double windowHeight, double warp)
    {
        var positions = mesh.Positions;
        double srcCenterX = src.Left + src.Width / 2;
        int rowStride = Columns + 1;

        for (int j = 0; j <= Rows; j++)
        {
            double v = (double)j / Rows;
            double lp = Clamp01(warp * (1 + Stagger) - (1 - v) * Stagger);
            double e = SmoothStep(lp);

            double rowCenterX = Lerp(srcCenterX, target.X, e);
            double rowWidth = Lerp(src.Width, TargetWidth, e);
            double origY = src.Top + v * src.Height;
            double y = Lerp(origY, target.Y, e);

            for (int i = 0; i <= Columns; i++)
            {
                double u = (double)i / Columns;
                double x = rowCenterX + (u - 0.5) * rowWidth;
                // 3D Y is up; screen Y is down — flip into the orthographic camera's space.
                positions[j * rowStride + i] = new Point3D(x, windowHeight - y, 0);
            }
        }
    }

    private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double SmoothStep(double t) => t * t * (3 - 2 * t);
}
