using System.IO;

namespace FileKakari;

public enum NamedSessionNameError
{
    None,
    Empty,
    InvalidCharacters,
    ReservedName,
    TooLong
}

public sealed record NamedSessionNameValidation(
    string Name,
    NamedSessionNameError Error)
{
    public bool IsValid => Error == NamedSessionNameError.None;
}

public sealed record NamedSessionInfo(
    string Name,
    string Path,
    DateTime LastWriteTime);

public sealed class NamedSessionStore
{
    private const int MaxNameLength = 120;

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string DirectoryPath { get; }

    public NamedSessionStore(string? directoryPath = null)
    {
        DirectoryPath = Path.GetFullPath(directoryPath ?? AppPaths.SessionsDirectory);
    }

    public static NamedSessionNameValidation ValidateName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^5].Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return new NamedSessionNameValidation(name, NamedSessionNameError.Empty);
        }

        if (name.Length > MaxNameLength)
        {
            return new NamedSessionNameValidation(name, NamedSessionNameError.TooLong);
        }

        if (name is "." or ".."
            || name.EndsWith(".", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return new NamedSessionNameValidation(name, NamedSessionNameError.InvalidCharacters);
        }

        var deviceName = name.Split('.')[0];
        if (ReservedFileNames.Contains(deviceName))
        {
            return new NamedSessionNameValidation(name, NamedSessionNameError.ReservedName);
        }

        return new NamedSessionNameValidation(name, NamedSessionNameError.None);
    }

    public string GetPath(string name)
    {
        var validation = ValidateName(name);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Invalid named session name: {validation.Error}", nameof(name));
        }

        return Path.Combine(DirectoryPath, validation.Name + ".json");
    }

    public IReadOnlyList<NamedSessionInfo> GetSessions()
    {
        Directory.CreateDirectory(DirectoryPath);
        return Directory
            .EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(file => new NamedSessionInfo(
                Path.GetFileNameWithoutExtension(file.Name),
                file.FullName,
                file.LastWriteTime))
            .ToList();
    }

    public NamedSessionInfo? FindByName(string name)
    {
        var validation = ValidateName(name);
        if (!validation.IsValid)
        {
            return null;
        }

        return GetSessions().FirstOrDefault(session =>
            string.Equals(session.Name, validation.Name, StringComparison.OrdinalIgnoreCase));
    }
}
