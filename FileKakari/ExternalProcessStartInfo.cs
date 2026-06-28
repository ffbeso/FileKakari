using System.Diagnostics;
using System.IO;

namespace FileKakari;

internal static class ExternalProcessStartInfo
{
    public static ProcessStartInfo CreateShellExecute(string targetPath, string? fallbackWorkingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(targetPath)
        {
            UseShellExecute = true
        };

        ApplyWorkingDirectory(
            startInfo,
            ResolveWorkingDirectoryForTargetPath(targetPath),
            fallbackWorkingDirectory);

        return startInfo;
    }

    public static string ResolveWorkingDirectoryForTargetPath(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return "";
        }

        try
        {
            if (Directory.Exists(targetPath))
            {
                return targetPath;
            }

            if (File.Exists(targetPath))
            {
                return Path.GetDirectoryName(targetPath) ?? "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }

        return "";
    }

    public static string ResolveWorkingDirectoryForEntry(FileEntry? entry)
    {
        if (entry is null)
        {
            return "";
        }

        return entry.IsDirectory
            ? ResolveExistingDirectory(entry.FullPath)
            : ResolveExistingDirectory(entry.ParentPath);
    }

    public static string ResolveExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || SpecialLocationService.IsSpecialUri(path))
        {
            return "";
        }

        try
        {
            return Directory.Exists(path) ? path : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    public static void ApplyWorkingDirectory(ProcessStartInfo startInfo, params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var workingDirectory = ResolveExistingDirectory(candidate);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
                return;
            }
        }
    }
}
