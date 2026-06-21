using System.IO;

namespace FileKakari;

internal static class WorkspacePathService
{
    public static bool IsWorkspaceRootPath(WorkspaceSession? session, string path)
    {
        return session?.IsWorkspace == true
            && session.Workspace?.HasRootPath != false
            && IsSamePath(path, session.RootPath);
    }

    public static string? ResolveWorkspacePath(string? path, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return workspaceRoot;
        }

        if (SpecialLocationService.IsSpecialUri(path.Trim()))
        {
            return SpecialLocationService.ThisPcUri;
        }

        try
        {
            var resolved = Path.IsPathFullyQualified(path)
                ? path
                : Path.GetFullPath(Path.Combine(workspaceRoot, path));
            return Directory.Exists(resolved) ? resolved : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveWorkspaceCurrentPath(string? path, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return workspaceRoot;
        }

        if (SpecialLocationService.IsSpecialUri(path.Trim()))
        {
            return SpecialLocationService.ThisPcUri;
        }

        try
        {
            var trimmed = path.Trim();
            return Path.IsPathFullyQualified(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(workspaceRoot, trimmed));
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveOptionalWorkspaceRootPath(string? path, string sourceDirectory)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : ResolveWorkspacePath(path, sourceDirectory);
    }



    public static string ToWorkspaceRelativePath(string path, string? workspaceRoot)
    {
        if (SpecialLocationService.IsSpecialUri(path))
        {
            return SpecialLocationService.ThisPcUri;
        }

        if (workspaceRoot == null)
        {
            return path;
        }

        if (IsSamePath(path, workspaceRoot))
        {
            return ".";
        }

        var relative = Path.GetRelativePath(workspaceRoot, path);
        return IsParentRelativePath(relative)
            ? path
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static string ToWorkspaceLocalCurrentPath(string path, string? workspaceRoot)
    {
        if (SpecialLocationService.IsSpecialUri(path))
        {
            return SpecialLocationService.ThisPcUri;
        }

        if (workspaceRoot == null)
        {
            return Path.GetFullPath(path);
        }

        if (IsSamePath(path, workspaceRoot))
        {
            return ".";
        }

        var relative = Path.GetRelativePath(workspaceRoot, path);
        return IsParentRelativePath(relative)
            ? Path.GetFullPath(path)
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static bool IsSamePath(string path, string otherPath)
    {
        return string.Equals(NormalizePathKey(path), NormalizePathKey(otherPath), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePathKey(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsParentRelativePath(string path)
    {
        return string.Equals(path, "..", StringComparison.Ordinal)
            || path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || path.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
