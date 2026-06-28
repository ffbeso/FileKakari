using System.IO;

namespace FileKakari;

internal sealed class FolderWatchTabTracker
{
    private readonly Func<IEnumerable<FolderTab>> _allTabsProvider;
    private readonly FolderWatchService _folderWatchService;
    private readonly PerformanceLogger _performanceLogger;

    public FolderWatchTabTracker(
        Func<IEnumerable<FolderTab>> allTabsProvider,
        FolderWatchService folderWatchService,
        PerformanceLogger performanceLogger)
    {
        _allTabsProvider = allTabsProvider;
        _folderWatchService = folderWatchService;
        _performanceLogger = performanceLogger;
    }

    public void UpdateWatchedFolders(IEnumerable<string> watchPaths, Action<string> clearPendingRefresh)
    {
        var before = _folderWatchService.CurrentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = watchPaths
            .Where(path => !string.IsNullOrWhiteSpace(path)
                && !SpecialLocationService.IsSpecialUri(path)
                && FolderWatchService.CanWatchFolder(path))
            .Select(NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _folderWatchService.SetWatchedFolders(paths);

        var after = _folderWatchService.CurrentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in after.Except(before, StringComparer.OrdinalIgnoreCase))
        {
            PerfLog.WriteVerbose($"folder-watch-start path=\"{path}\" refs={CountTabsForWatchPath(path)}");
        }

        foreach (var path in before.Except(after, StringComparer.OrdinalIgnoreCase))
        {
            PerfLog.WriteVerbose($"folder-watch-stop path=\"{path}\"");
            clearPendingRefresh(path);
        }
    }

    public void MarkTabsPendingExternalChange(string changedPath)
    {
        foreach (var tab in _allTabsProvider())
        {
            if (IsPathSameOrUnderFolder(tab.Navigation.CurrentPath, changedPath))
            {
                tab.MarkPendingExternalChange();
            }
        }
    }

    private int CountTabsForWatchPath(string path)
    {
        return _allTabsProvider().Count(tab =>
            !tab.IsDisconnected
            && !SpecialLocationService.IsSpecialUri(tab.Navigation.CurrentPath)
            && IsPathSameOrUnderFolder(path, tab.Navigation.CurrentPath)
            && IsPathSameOrUnderFolder(tab.Navigation.CurrentPath, path));
    }

    public static bool IsPathSameOrUnderFolder(string folderPath, string candidatePath)
    {
        if (SpecialLocationService.IsSpecialUri(folderPath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var folder = NormalizeFolderPath(folderPath);
            var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(folder, candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeFolderPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
