using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dockable.Models;

namespace Dockable.Shell;

/// <summary>
/// Composes a pinned folder's "Stack" tile: the icons of its first <see cref="FolderContents.MaxItems"/>
/// items (per the folder's "Sort by" order) drawn as a bottom-anchored cascade, the top-of-stack
/// item in front at the bottom and each item behind it peeking out a few pixels higher — the macOS
/// Dock stack look.
/// </summary>
internal static class StackIcon
{
    /// <summary>Vertical offset (output px, of a 256px tile) between successive stack levels.
    /// Small on purpose: with 10 items the front icon still spans ~80% of the tile, so stacked
    /// items read nearly as large as regular dock icons.</summary>
    private const double Step = 6;

    /// <summary>Per-item icons are extracted smaller than app icons — up to 10 load per stack.</summary>
    private const int ItemIconPx = 128;

    /// <summary>
    /// Renders the stacked icon for <paramref name="folder"/>, or null when the folder is empty /
    /// unreadable (callers fall back to the plain folder icon). Must be awaited on the UI thread
    /// (the final composition uses a <see cref="RenderTargetBitmap"/>); the IO runs off it.
    /// </summary>
    public static async Task<ImageSource?> RenderAsync(string folder, FolderSortBy sortBy, int pixelSize)
    {
        var entries = await Task.Run(() => FolderContents.GetSorted(folder, sortBy));
        if (entries.Count == 0)
            return null;

        var icons = new List<ImageSource>();
        foreach (var entry in entries.Take(FolderContents.MaxItems))
        {
            var icon = await ShortcutService.LoadIconAsync(entry.FullName, ItemIconPx);
            if (icon is not null)
                icons.Add(icon);
        }
        if (icons.Count == 0)
            return null;

        // icons[0] is the top of the stack: drawn last (in front), anchored to the tile's bottom;
        // each item behind it sits Step px higher. All items share one size so the cascade is even.
        double scale = pixelSize / 256.0;
        double step = Step * scale;
        double size = pixelSize - step * (icons.Count - 1);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            for (int depth = icons.Count - 1; depth >= 0; depth--)
            {
                double y = pixelSize - size - step * depth;
                dc.DrawImage(icons[depth], new Rect((pixelSize - size) / 2, y, size, size));
            }
        }

        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
