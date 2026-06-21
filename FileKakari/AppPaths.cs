using System.IO;

namespace FileKakari;

public static class AppPaths
{
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileKakari");

    public static string LocalDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileKakari");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static string SessionPath => Path.Combine(LocalDirectory, "session.json");

    public static string CommandsPath => Path.Combine(LocalDirectory, "commands.json");

    public static string CommandsDirectory => Path.Combine(LocalDirectory, "Commands");

    public static string LayoutPath => Path.Combine(SettingsDirectory, "layout.json");

    public static string LogsDirectory => Path.Combine(SettingsDirectory, "logs");

    public static string CacheDirectory => Path.Combine(SettingsDirectory, "cache");

    public static string ThemesDirectory => Path.Combine(SettingsDirectory, "Themes");

    public static void EnsureSettingsDirectory()
    {
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(LocalDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ThemesDirectory);
        Directory.CreateDirectory(CommandsDirectory);
    }
}
