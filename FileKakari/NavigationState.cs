using System.IO;

namespace FileKakari;

public sealed class NavigationState
{
    private readonly Stack<string> _backHistory = [];
    private readonly Stack<string> _forwardHistory = [];

    public NavigationState(string currentPath)
    {
        CurrentPath = currentPath;
    }

    public string CurrentPath { get; private set; }

    public bool CanGoBack => _backHistory.Count > 0;

    public bool CanGoForward => _forwardHistory.Count > 0;

    public bool CanGoUp => !SpecialLocationService.IsSpecialUri(CurrentPath)
        && (Directory.GetParent(CurrentPath) is not null || IsFileSystemRoot(CurrentPath));

    public string? PeekBack()
    {
        return _backHistory.Count == 0 ? null : _backHistory.Peek();
    }

    public string? PeekForward()
    {
        return _forwardHistory.Count == 0 ? null : _forwardHistory.Peek();
    }

    public void Commit(string path, NavigationKind navigationKind)
    {
        var previousPath = CurrentPath;

        switch (navigationKind)
        {
            case NavigationKind.New:
            case NavigationKind.Up:
                _backHistory.Push(previousPath);
                _forwardHistory.Clear();
                break;
            case NavigationKind.Back:
                _forwardHistory.Push(previousPath);
                _backHistory.Pop();
                break;
            case NavigationKind.Forward:
                _backHistory.Push(previousPath);
                _forwardHistory.Pop();
                break;
            case NavigationKind.Refresh:
                break;
        }

        CurrentPath = path;
    }

    public void SetCurrentPath(string path)
    {
        CurrentPath = path;
    }

    public NavigationState Clone()
    {
        var clone = new NavigationState(CurrentPath);
        clone.CopyHistoryFrom(this);
        return clone;
    }

    public void CopyFrom(NavigationState source)
    {
        CurrentPath = source.CurrentPath;
        CopyHistoryFrom(source);
    }

    private void CopyHistoryFrom(NavigationState source)
    {
        _backHistory.Clear();
        foreach (var path in source._backHistory.Reverse())
        {
            _backHistory.Push(path);
        }

        _forwardHistory.Clear();
        foreach (var path in source._forwardHistory.Reverse())
        {
            _forwardHistory.Push(path);
        }
    }

    public static string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (SpecialLocationService.IsSpecialUri(path.Trim()))
        {
            return path.Trim();
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch
        {
            return null;
        }
    }

    public static bool IsExistingDirectory(string path)
    {
        if (SpecialLocationService.IsSpecialUri(path))
        {
            return true;
        }

        var normalizedPath = NormalizePath(path);
        return normalizedPath is not null && Directory.Exists(normalizedPath);
    }

    public static bool IsFileSystemRoot(string path)
    {
        if (SpecialLocationService.IsSpecialUri(path))
        {
            return false;
        }

        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var root = Directory.GetDirectoryRoot(normalizedPath);
            return !string.IsNullOrEmpty(root)
                && string.Equals(
                    EnsureTrailingSeparator(normalizedPath),
                    EnsureTrailingSeparator(root),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}

public enum NavigationKind
{
    New,
    Up,
    Back,
    Forward,
    Refresh
}

public enum FileListRestorePolicy
{
    None,
    ExactRestore,
    FocusPathFallback,
    ScrollOnly,
    RevealAndSelectPaths
}
