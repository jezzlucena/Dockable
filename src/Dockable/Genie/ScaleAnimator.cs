using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Genie;

/// <summary>
/// A simple minimize/restore animation: the captured window image scales down toward its dock tile
/// (and reverses on restore). A lighter alternative to the genie warp. Uses the same pre-warmed,
/// reusable click-through overlay approach as <see cref="GenieAnimator"/>.
///
/// Coordinates are device-independent (DIP); rects are in virtual-screen DIPs.
/// </summary>
public sealed class ScaleAnimator : IMinimizeAnimator
{
    private const double DurationMs = 230;

    /// <summary>Speed multiplier; &gt;1 shortens the duration (faster), &lt;1 lengthens it (slower).</summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>Landed size at the dock (DIP); set from the actual tile width before each play.</summary>
    public double TargetTileWidth { get; set; } = 56;

    private Window? _overlay;
    private Image? _image;
    private ScaleTransform? _scale;
    private TranslateTransform? _translate;
    private EventHandler? _rendering;

    // Current animation state (overlay-local DIP coords).
    private Rect _src;
    private Point _target;
    private Point _monitorOrigin; // monitor top-left (DIP), to convert later AnimateTo targets
    private double _endScale;
    private bool _reverse;
    private Action? _onCompleted;
    private TimeSpan _startTime;

    public void Prewarm() => EnsureOverlay();

    public void Play(BitmapSource bitmap, Rect sourceDip, Point targetDip, Rect monitorDip, bool reverse, Action? onCompleted)
    {
        EnsureOverlay();
        FinishCurrent(); // only one animation at a time on the shared overlay — finalize any in-flight one
        SyncOverlay(monitorDip);

        _monitorOrigin = new Point(monitorDip.Left, monitorDip.Top);
        _src = new Rect(sourceDip.Left - monitorDip.Left, sourceDip.Top - monitorDip.Top, sourceDip.Width, sourceDip.Height);
        _target = new Point(targetDip.X - monitorDip.Left, targetDip.Y - monitorDip.Top);
        _reverse = reverse;
        _onCompleted = onCompleted;
        _startTime = TimeSpan.Zero;
        _endScale = Math.Clamp(TargetTileWidth / Math.Max(_src.Width, 1), 0.04, 0.25);

        _image!.Source = bitmap;
        _image.Width = _src.Width;
        _image.Height = _src.Height;
        Canvas.SetLeft(_image, _src.Left);
        Canvas.SetTop(_image, _src.Top);
        _scale!.CenterX = _src.Width / 2;
        _scale.CenterY = _src.Height / 2;

        ApplyFrame(reverse ? 1.0 : 0.0);
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
        FinishCurrent(); // finalize any in-flight animation so its window lands and frees up before this one
        SyncOverlay(monitorDip);

        _monitorOrigin = new Point(monitorDip.Left, monitorDip.Top);
        _src = new Rect(sourceDip.Left - monitorDip.Left, sourceDip.Top - monitorDip.Top, sourceDip.Width, sourceDip.Height);
        _target = new Point(_src.Left + _src.Width / 2, _src.Top + _src.Height / 2); // until AnimateTo

        _image!.Source = bitmap;
        _image.Width = _src.Width;
        _image.Height = _src.Height;
        Canvas.SetLeft(_image, _src.Left);
        Canvas.SetTop(_image, _src.Top);
        _scale!.CenterX = _src.Width / 2;  // scale about the image's own center
        _scale.CenterY = _src.Height / 2;

        ApplyFrame(0.0); // un-scaled: the window exactly where it was
        _overlay!.Visibility = Visibility.Visible;
    }

    public void AnimateTo(Point targetDip, bool reverse, Action? onCompleted)
    {
        EnsureOverlay();
        _target = new Point(targetDip.X - _monitorOrigin.X, targetDip.Y - _monitorOrigin.Y);
        _reverse = reverse;
        _onCompleted = onCompleted;
        _startTime = TimeSpan.Zero;
        _endScale = Math.Clamp(TargetTileWidth / Math.Max(_src.Width, 1), 0.04, 0.25);

        ApplyFrame(reverse ? 1.0 : 0.0);
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

        double duration = DurationMs / Math.Max(0.1, SpeedMultiplier);
        double progress = Math.Min(1.0, (now - _startTime).TotalMilliseconds / duration);
        double t = _reverse ? 1.0 - progress : progress;
        ApplyFrame(SmoothStep(t));

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

    // t: 0 = full window at source, 1 = shrunk onto the dock tile.
    private void ApplyFrame(double t)
    {
        double s = 1.0 + (_endScale - 1.0) * t;
        _scale!.ScaleX = s;
        _scale.ScaleY = s;

        // Scaling is centered, so move the image's center from the source center to the tile.
        double srcCenterX = _src.Left + _src.Width / 2;
        double srcCenterY = _src.Top + _src.Height / 2;
        _translate!.X = (_target.X - srcCenterX) * t;
        _translate.Y = (_target.Y - srcCenterY) * t;

        _image!.Opacity = 1.0 - 0.2 * t; // gentle fade as it lands
    }

    /// <summary>
    /// Ends any animation currently in flight by running its completion callback now (the previous
    /// window snaps to its tile and is freed) — the shared overlay can only show one at a time, so
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

        _scale = new ScaleTransform();
        _translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);

        _image = new Image { Stretch = Stretch.Fill, RenderTransform = group };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        var canvas = new Canvas();
        canvas.Children.Add(_image);

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
            Content = canvas,
        };

        _overlay.Left = 0;
        _overlay.Top = 0;
        _overlay.Width = SystemParameters.PrimaryScreenWidth;
        _overlay.Height = SystemParameters.PrimaryScreenHeight;
        _overlay.Show();          // create the HWND
        MakeClickThrough(_overlay);
        _overlay.Visibility = Visibility.Hidden; // idle until a minimize plays
    }

    private void SyncOverlay(Rect monitorDip)
    {
        _overlay!.Left = monitorDip.Left;
        _overlay.Top = monitorDip.Top;
        _overlay.Width = monitorDip.Width;
        _overlay.Height = monitorDip.Height;
    }

    private static void MakeClickThrough(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)ex);
    }

    private static double SmoothStep(double t) => t * t * (3 - 2 * t);
}
