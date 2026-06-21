using System.IO;

namespace FileKakari;

public sealed class DriveAvailabilityService
{
    public Task<DriveAvailabilityResult> CheckAsync(string path)
    {
        return Task.Run(() => Check(path));
    }

    public Task<bool> DirectoryExistsAsync(string path)
    {
        return Task.Run(() => Directory.Exists(path));
    }

    private static DriveAvailabilityResult Check(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                var exists = Directory.Exists(path);
                return new DriveAvailabilityResult(exists, null, exists, exists, null);
            }

            if (IsUncRoot(root))
            {
                var exists = Directory.Exists(root);
                return new DriveAvailabilityResult(exists, root, exists, exists, null);
            }

            var normalizedRoot = EnsureTrailingSeparator(root);
            var rootExists = DriveInfo.GetDrives()
                .Any(drive => string.Equals(EnsureTrailingSeparator(drive.Name), normalizedRoot, StringComparison.OrdinalIgnoreCase));
            if (!rootExists)
            {
                return new DriveAvailabilityResult(false, normalizedRoot, false, false, null);
            }

            var driveInfo = new DriveInfo(normalizedRoot);
            var isReady = driveInfo.IsReady;
            return new DriveAvailabilityResult(isReady, normalizedRoot, true, isReady, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DriveAvailabilityResult(false, Path.GetPathRoot(path), false, false, ex.Message);
        }
    }

    private static bool IsUncRoot(string root)
    {
        return root.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}

public readonly record struct DriveAvailabilityResult(
    bool IsAvailable,
    string? RootPath,
    bool RootExists,
    bool IsReady,
    string? Error);
