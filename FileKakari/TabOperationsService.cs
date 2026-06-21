using System.Collections.ObjectModel;
using System.IO;

namespace FileKakari;

internal sealed class TabOperationsService
{
    private const int ClosedTabCachedItemsLimit = 3000;

    private readonly Func<IEnumerable<FolderTab>> _allTabsProvider;
    private readonly Func<FileDisplayMode> _getDefaultViewMode;
    private readonly Func<string?, string> _normalizeSortColumn;

    public TabOperationsService(
        Func<IEnumerable<FolderTab>> allTabsProvider,
        Func<FileDisplayMode> getDefaultViewMode,
        Func<string?, string> normalizeSortColumn)
    {
        _allTabsProvider = allTabsProvider;
        _getDefaultViewMode = getDefaultViewMode;
        _normalizeSortColumn = normalizeSortColumn;
    }

    public string ResolveNewTabPath(string? requestedPath, string activePath)
    {
        if (SpecialLocationService.IsSpecialUri(requestedPath ?? ""))
        {
            return requestedPath!;
        }

        if (Directory.Exists(requestedPath))
        {
            return requestedPath!;
        }

        if (!SpecialLocationService.IsSpecialUri(activePath) && Directory.Exists(activePath))
        {
            return activePath;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public FolderTab CreateNewTab(string path, FolderTab? sourceTab)
    {
        var tab = new FolderTab(path)
        {
            ViewMode = AppSettings.NormalizeDisplayMode(sourceTab?.State.ViewMode ?? _getDefaultViewMode())
        };
        CopyTabViewStateForDuplicate(tab, sourceTab);
        return tab;
    }

    public void CopyTabViewStateForDuplicate(FolderTab tab, FolderTab? sourceTab)
    {
        if (sourceTab is null
            || !string.Equals(sourceTab.Navigation.CurrentPath, tab.Navigation.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        tab.State.FilterText = sourceTab.State.FilterText;
        tab.State.SortColumn = _normalizeSortColumn(sourceTab.State.SortColumn);
        tab.State.SortAscending = sourceTab.State.SortAscending;
        tab.State.ViewMode = AppSettings.NormalizeDisplayMode(sourceTab.State.ViewMode);
        tab.State.VerticalOffset = sourceTab.State.VerticalOffset;
        tab.State.SelectedPaths = sourceTab.State.SelectedPaths.ToList();
    }

    public TabCacheSeedResult SeedNewTabCache(
        FolderTab tab,
        FolderTab? preferredSource,
        FolderTab? activeTab,
        string? itemsOwnerStateId,
        IReadOnlyList<FileEntry> activeItems)
    {
        var path = tab.Navigation.CurrentPath;
        var source = preferredSource is not null
            && !ReferenceEquals(preferredSource, tab)
            && preferredSource.HasCachedItemsForCurrentPath
            && string.Equals(preferredSource.CachedPath, path, StringComparison.OrdinalIgnoreCase)
            ? preferredSource
            : _allTabsProvider().FirstOrDefault(candidate =>
                !ReferenceEquals(candidate, tab)
                && candidate.HasCachedItemsForCurrentPath
                && string.Equals(candidate.CachedPath, path, StringComparison.OrdinalIgnoreCase));

        if (source?.CachedItems is not null)
        {
            tab.StoreItems(path, source.CachedItems);
            return new TabCacheSeedResult(TabCacheSeedKind.Shared, source, source.CachedItems.Count);
        }

        if (activeTab is not null
            && string.Equals(path, activeTab.Navigation.CurrentPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(itemsOwnerStateId, activeTab.State.Id, StringComparison.Ordinal)
            && activeItems.Count > 0)
        {
            var items = activeItems.ToList();
            tab.StoreItems(path, items);
            return new TabCacheSeedResult(TabCacheSeedKind.Cloned, activeTab, items.Count);
        }

        return TabCacheSeedResult.None;
    }

    public ClosedTabState CaptureClosedTabState(FolderTab tab, int index, int maxCount)
    {
        IReadOnlyList<FileEntry>? cachedItems = tab.CachedItems;
        if (cachedItems is not null && cachedItems.Count > ClosedTabCachedItemsLimit)
        {
            cachedItems = null;
        }

        return new ClosedTabState(
            tab.State.Id,
            tab.Navigation.Clone(),
            tab.State.FilterText,
            _normalizeSortColumn(tab.State.SortColumn),
            tab.State.SortAscending,
            AppSettings.NormalizeDisplayMode(tab.State.ViewMode),
            tab.State.VerticalOffset,
            tab.State.SelectedPaths.ToList(),
            tab.IsFolderLocked,
            tab.CachedPath,
            cachedItems,
            Math.Clamp(index, 0, maxCount));
    }

    public FolderTab RestoreClosedTabState(ClosedTabState state)
    {
        var tabState = new WorkspaceTabState(state.Navigation.CurrentPath, state.StateId, state.ViewMode)
        {
            FilterText = state.FilterText,
            SortColumn = _normalizeSortColumn(state.SortColumn),
            SortAscending = state.SortAscending,
            VerticalOffset = state.VerticalOffset,
            SelectedPaths = state.SelectedPaths.ToList()
        };
        var tab = new FolderTab(state.Navigation.CurrentPath, state: tabState);
        tab.Navigation.CopyFrom(state.Navigation);
        tab.SetFolderLocked(state.IsFolderLocked);

        if (state.CachedPath is not null
            && state.CachedItems is not null
            && (SpecialLocationService.IsSpecialUri(state.CachedPath) || Directory.Exists(state.CachedPath)))
        {
            tab.StoreItems(state.CachedPath, state.CachedItems);
        }

        return tab;
    }

    public int CalculateLockedGroupTargetIndex(IReadOnlyList<FolderTab> tabs, FolderTab tab)
    {
        return tabs.Count(candidate => !ReferenceEquals(candidate, tab) && candidate.IsFolderLocked);
    }

    public int CalculateReorderTargetIndex(IReadOnlyList<FolderTab> tabs, FolderTab draggedTab, FolderTab targetTab, bool insertAfterTarget)
    {
        var sourceIndex = -1;
        var targetIndex = -1;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (ReferenceEquals(tabs[i], draggedTab)) sourceIndex = i;
            if (ReferenceEquals(tabs[i], targetTab)) targetIndex = i;
        }

        if (sourceIndex < 0 || targetIndex < 0 || ReferenceEquals(draggedTab, targetTab))
        {
            return -1;
        }

        if (insertAfterTarget)
        {
            targetIndex++;
        }

        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, tabs.Count - 1);
        return sourceIndex == targetIndex ? -1 : targetIndex;
    }
}

internal sealed record ClosedTabState(
    string StateId,
    NavigationState Navigation,
    string FilterText,
    string SortColumn,
    bool SortAscending,
    FileDisplayMode ViewMode,
    double VerticalOffset,
    IReadOnlyList<string> SelectedPaths,
    bool IsFolderLocked,
    string? CachedPath,
    IReadOnlyList<FileEntry>? CachedItems,
    int Index);

internal sealed record TabCacheSeedResult(TabCacheSeedKind Kind, FolderTab? SourceTab, int ItemCount)
{
    public static TabCacheSeedResult None { get; } = new(TabCacheSeedKind.None, null, 0);
}

internal enum TabCacheSeedKind
{
    None,
    Shared,
    Cloned
}
