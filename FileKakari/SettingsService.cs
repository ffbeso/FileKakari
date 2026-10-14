using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKakari;

public sealed class SettingsService
{
    private readonly SerializedTaskQueue _saveQueue = new();
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
        var requestedSettings = settings.Clone();
        var clone = requestedSettings.Clone();
        clone.FolderColumnWidths.Clear();
        clone.ColumnWidths.Clear();
        clone.EnsureDefaults();
        await _saveQueue.Enqueue(() =>
        {
            AppPaths.EnsureSettingsDirectory();
            AtomicFileWriter.WriteAllText(SettingsPath, JsonSerializer.Serialize(clone, JsonOptions));
            return true;
        });
        PerfLog.WriteVerbose($"json-save-complete target=\"settings\" path=\"{SettingsPath}\"");
        Settings = requestedSettings;
    }

    public async Task SaveCurrentAsync()
    {
        var clone = Settings.Clone();
        clone.FolderColumnWidths.Clear();
        clone.ColumnWidths.Clear();
        clone.EnsureDefaults();
        await _saveQueue.Enqueue(() =>
        {
            AppPaths.EnsureSettingsDirectory();
            AtomicFileWriter.WriteAllText(SettingsPath, JsonSerializer.Serialize(clone, JsonOptions));
            return true;
        });
        PerfLog.WriteVerbose($"json-save-complete target=\"settings\" path=\"{SettingsPath}\"");
    }
}
