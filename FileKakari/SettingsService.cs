using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKakari;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings Settings { get; private set; } = new();

    public string SettingsPath { get; }

    public SettingsService()
    {
        SettingsPath = AppPaths.SettingsPath;
    }

    public void Load()
    {
        if (!File.Exists(SettingsPath))
        {
            Settings = new AppSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            Settings.EnsureDefaults();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var clone = settings.Clone();
        clone.FolderColumnWidths.Clear();
        clone.ColumnWidths.Clear();
        clone.EnsureDefaults();
        AppPaths.EnsureSettingsDirectory();

        var json = JsonSerializer.Serialize(clone, JsonOptions);
        await File.WriteAllTextAsync(SettingsPath, json);
        Settings = settings.Clone();
    }

    public void SaveCurrent()
    {
        var clone = Settings.Clone();
        clone.FolderColumnWidths.Clear();
        clone.ColumnWidths.Clear();
        clone.EnsureDefaults();
        AppPaths.EnsureSettingsDirectory();

        var json = JsonSerializer.Serialize(clone, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
