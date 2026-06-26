using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dockable.Models;

namespace Dockable.Services;

/// <summary>
/// Loads and saves <see cref="DockSettings"/> as JSON under
/// %APPDATA%\Dockable\settings.json. Writes are atomic (temp file + replace)
/// to avoid corrupting the config if the process is killed mid-write.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string DirectoryPath { get; }
    public string FilePath { get; }

    public SettingsStore()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dockable");
        FilePath = Path.Combine(DirectoryPath, "settings.json");
    }

    public DockSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return DockSettings.CreateDefault();

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<DockSettings>(json, JsonOptions)
                   ?? DockSettings.CreateDefault();
        }
        catch
        {
            // A malformed config should never prevent startup; fall back to defaults.
            return DockSettings.CreateDefault();
        }
    }

    public void Save(DockSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        string json = JsonSerializer.Serialize(settings, JsonOptions);

        string tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(FilePath))
            File.Replace(tempPath, FilePath, null);
        else
            File.Move(tempPath, FilePath);
    }
}
