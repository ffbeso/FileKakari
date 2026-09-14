using System.IO;

namespace FileKakari;

public sealed record SessionStateRestorePlan(
    IReadOnlyList<SessionTabState> Tabs,
    int SelectedTabIndex);

public static class SessionStateRestorePlanner
{
    public static SessionStateRestorePlan Prepare(
        SessionState? state,
        string fallbackPath,
        FileDisplayMode fallbackViewMode)
    {
        var source = state ?? new SessionState();
        var sourceTabs = source.Tabs ?? [];
        var tabs = new List<SessionTabState>();
        var selectedValidIndex = 0;

        for (var index = 0; index < sourceTabs.Count; index++)
        {
            var tab = sourceTabs[index];
            if (tab is null || !IsRestorable(tab))
            {
                continue;
            }

            if (index < source.SelectedTabIndex)
            {
                selectedValidIndex++;
            }

            tabs.Add(CloneForRestore(tab));
        }

        if (tabs.Count == 0)
        {
            tabs.Add(new SessionTabState
            {
                Path = fallbackPath,
                ViewMode = AppSettings.NormalizeDisplayMode(fallbackViewMode)
            });
            selectedValidIndex = 0;
        }

        return new SessionStateRestorePlan(
            tabs,
            Math.Clamp(selectedValidIndex, 0, tabs.Count - 1));
    }

    private static bool IsRestorable(SessionTabState tab)
    {
        if (tab.IsWorkspace)
        {
            var isUnsaved = tab.IsUnsavedWorkspace || string.IsNullOrWhiteSpace(tab.WorkspacePath);
            var rootPath = string.IsNullOrWhiteSpace(tab.RootPath) ? tab.Path : tab.RootPath;
            if (isUnsaved)
            {
                return !string.IsNullOrWhiteSpace(rootPath)
                    && (SpecialLocationService.IsSpecialUri(rootPath) || Directory.Exists(rootPath));
            }

            if (File.Exists(tab.WorkspacePath))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(rootPath)
                && (SpecialLocationService.IsSpecialUri(rootPath) || Directory.Exists(rootPath));
        }

        return !string.IsNullOrWhiteSpace(tab.Path)
            && (SpecialLocationService.IsSpecialUri(tab.Path) || Directory.Exists(tab.Path));
    }

    private static SessionTabState CloneForRestore(SessionTabState source)
    {
        var resolvedTabId = source.TabId;
        if (string.IsNullOrWhiteSpace(resolvedTabId)
            || string.Equals(resolvedTabId, "root", StringComparison.OrdinalIgnoreCase))
        {
            resolvedTabId = Guid.NewGuid().ToString("N");
        }

        return new SessionTabState
        {
            TabId = resolvedTabId,
            Path = source.Path,
            IsWorkspace = source.IsWorkspace,
            WorkspacePath = source.WorkspacePath,
            RootPath = string.IsNullOrWhiteSpace(source.RootPath) ? source.Path : source.RootPath,
            SortColumn = source.SortColumn,
            SortAscending = source.SortAscending,
            GroupMode = source.GroupMode,
            ViewMode = AppSettings.NormalizeDisplayMode(source.ViewMode),
            IsFolderLocked = source.IsFolderLocked,
            FilterText = source.FilterText ?? "",
            SelectedPaths = source.SelectedPaths?.ToList() ?? [],
            ScrollOffset = source.ScrollOffset,
            LocalState = source.LocalState,
            IsUnsavedWorkspace = source.IsUnsavedWorkspace,
            WorkspaceId = source.WorkspaceId,
            Name = source.Name,
            ActivePaneId = source.ActivePaneId,
            Layout = source.Layout
        };
    }
}
