using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Dockable.Shell;

/// <summary>
/// Renders .svg/.svgz files to real icons — the Windows shell has no native SVG thumbnailer, so
/// <c>IShellItemImageFactory</c> hands back the generic association icon for them. Plugged into
/// <see cref="ShortcutService"/>'s single icon funnel, so every SVG icon app-wide (a pinned file
/// tile, a stack member, a fan row) shows the actual artwork.
/// </summary>
internal static class SvgIcon
{
    /// <summary>
    /// Rasterizes an SVG to a square, centered, aspect-preserving bitmap of the requested size.
    /// Runs on whatever thread the icon loader uses (a worker): the visual and RenderTargetBitmap
    /// are created and rendered on that same thread, then frozen for cross-thread use. Returns
    /// null when parsing or rendering fails (callers fall back to the shell icon).
    /// </summary>
    public static ImageSource? Render(string path, int pixelSize)
    {
        try
        {
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = false,
                TextAsGeometry = true, // bake text to paths: no font lookups in the frozen drawing
            };
            var reader = new FileSvgReader(settings);
            DrawingGroup? drawing = reader.Read(path);
            if (drawing is null)
                return null;
            drawing.Freeze();

            Rect bounds = drawing.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            var image = new DrawingImage(drawing);
            image.Freeze();

            // Uniform-fit into the square canvas, centered — the same shape shell icons come in,
            // so stacks/fans/tiles treat SVGs identically to every other icon.
            double scale = pixelSize / Math.Max(bounds.Width, bounds.Height);
            double w = bounds.Width * scale;
            double h = bounds.Height * scale;
            var fit = new Rect((pixelSize - w) / 2, (pixelSize - h) / 2, w, h);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawImage(image, fit);

            var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Dockable] SVG render failed for '{path}': {ex.Message}");
            return null;
        }
    }
}
