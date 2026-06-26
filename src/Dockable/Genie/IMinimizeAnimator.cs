using System.Windows;
using System.Windows.Media.Imaging;

namespace Dockable.Genie;

/// <summary>
/// Plays a window minimize/restore animation from a captured window image into a dock tile.
/// Implemented by <see cref="GenieAnimator"/> (3D warp) and <see cref="ScaleAnimator"/> (scale-down).
/// All coordinates are device-independent (DIP); rects are virtual-screen DIPs.
/// </summary>
public interface IMinimizeAnimator
{
    /// <summary>Builds the reusable overlay ahead of time so the first play is instant.</summary>
    void Prewarm();

    /// <param name="reverse">false = minimize (source → tile); true = restore (tile → source).</param>
    void Play(BitmapSource bitmap, Rect sourceDip, Point targetDip, Rect monitorDip, bool reverse, Action? onCompleted);

    /// <summary>
    /// Immediately shows the captured window at its on-screen spot (the un-animated first frame),
    /// so it covers the gap the instant the real window is minimized. Call <see cref="AnimateTo"/>
    /// once the dock tile's position is known to start the warp.
    /// </summary>
    void ShowAtSource(BitmapSource bitmap, Rect sourceDip, Rect monitorDip);

    /// <summary>Starts the minimize animation from the currently-shown source frame into the tile.</summary>
    void AnimateTo(Point targetDip, bool reverse, Action? onCompleted);
}
