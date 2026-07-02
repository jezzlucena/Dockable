using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dockable;

/// <summary>
/// The application's own icon, loaded once from the embedded resources for use as window icons and the
/// tray icon. <see cref="Large"/> (256px) suits window/title-bar icons; <see cref="Tray"/> (32px) is
/// sized for the notification area.
/// </summary>
internal static class AppIcon
{
    /// <summary>256px app icon (window / title-bar / Alt-Tab).</summary>
    public static BitmapImage Large { get; } = Load("Assets/Dockable.png");

    /// <summary>32px app icon, sized for the system tray.</summary>
    public static BitmapImage Tray { get; } = Load("Assets/Dockable-32.png");

    /// <summary>The macOS-style Settings icon used by the built-in Dock Preferences tile.</summary>
    public static ImageSource Preferences { get; } = Load("Assets/settings.png");

    private static BitmapImage Load(string relativePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri($"pack://application:,,,/{relativePath}");
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze(); // cross-thread / reusable
        return bitmap;
    }
}
