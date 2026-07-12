using System.IO;
using System.Security.Cryptography;

namespace Dockable.Services;

/// <summary>
/// The %APPDATA%\Dockable\icons cache backing custom pin icons: a user-chosen image
/// (.png/.svg) is imported (copied) here when set, so the dock keeps rendering it after the
/// original file moves or disappears. Files are content-addressed (hash + original extension),
/// so re-importing the same image is a no-op and two pins can share one cached file.
/// </summary>
public static class PinIconCache
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dockable", "icons");

    /// <summary>Copies <paramref name="sourcePath"/> into the cache and returns the cached file
    /// name (the value persisted in settings), or null when the image can't be read.</summary>
    public static string? Import(string sourcePath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(sourcePath);
            string name = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant()
                          + Path.GetExtension(sourcePath).ToLowerInvariant();
            Directory.CreateDirectory(DirectoryPath);
            string target = Path.Combine(DirectoryPath, name);
            if (!File.Exists(target))
                File.WriteAllBytes(target, bytes);
            return name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Full path of a cached icon, or null when the cache no longer holds the file.</summary>
    public static string? Resolve(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        string path = Path.Combine(DirectoryPath, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Deletes a cached icon no pin references anymore (best-effort cleanup).</summary>
    public static void Delete(string fileName)
    {
        try { File.Delete(Path.Combine(DirectoryPath, fileName)); }
        catch { /* cache cleanup is best-effort */ }
    }
}
