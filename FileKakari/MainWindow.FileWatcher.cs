using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow
{
    private void FolderWatchService_ChangeObserved(string changedPath)
    {
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                if (!_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out _))
                {
                    _folderWatchTabTracker.MarkTabsPendingExternalChange(changedPath);
                    MarkWorkspaceDisplayPanesExternalChange(changedPath);
                }
            },
            DispatcherPriority.Background);
    }

    private void FolderWatchService_Changed(string changedPath)
    {
        _ = Dispatcher.InvokeAsync(
            async () => await RequestFolderWatchRefreshAsync(changedPath),
            DispatcherPriority.Background);
    }

    private void FolderWatchService_FileMetadataChanged(IReadOnlyList<string> changedPaths)
    {
        _ = Dispatcher.InvokeAsync(
            () => ApplyFolderWatchMetadataChanges(changedPaths),
            DispatcherPriority.Background);
    }

    private void FolderWatchService_WatchError(string path, Exception? exception)
    {
        _performanceLogger.Write($"folder-watch-error path=\"{path}\" error=\"{exception?.Message ?? ""}\"");
    }

    private async Task RequestFolderWatchRefreshAsync(string changedPath)
    {
        if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            await RequestWorkspacePaneWatchRefreshAsync(changedPath);
            return;
        }

        if (ActiveNavigation is not { } navigation)
        {
            return;
        }

        var activePath = navigation.CurrentPath;
        if (!FolderWatchTabTracker.IsPathSameOrUnderFolder(activePath, changedPath))
        {
            return;
        }

        if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var remaining))
        {
            _performanceLogger.Write($"folder-watch-refresh-suppressed path=\"{activePath}\" changedPath=\"{changedPath}\" remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)}");
            return;
        }

        _fileWatcherRefreshCoordinator.RequestRefresh(activePath);
        _folderWatchTabTracker.MarkTabsPendingExternalChange(changedPath);
        await ProcessPendingFolderWatchRefreshAsync();
    }

    private void MarkWorkspaceDisplayPanesExternalChange(string changedPath)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible)
        {
            return;
        }

        foreach (var pane in GetWorkspacePanesForChangedPath(changedPath))
        {
            pane.FileList.MarkExternalChange();
            pane.ActiveTabState?.MarkPendingExternalChange();
        }
    }

    private async Task RequestWorkspacePaneWatchRefreshAsync(string changedPath)
    {
        var panes = GetWorkspacePanesForChangedPath(changedPath).ToList();
        if (panes.Count == 0)
        {
            return;
        }

        if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var remaining))
        {
            _performanceLogger.Write($"folder-pane-watch-refresh-suppressed changedPath=\"{changedPath}\" panes={panes.Count} remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)}");
            return;
        }

        foreach (var pane in panes)
        {
            pane.FileList.MarkExternalChange();
            if (pane.ActiveTabState is { } state)
            {
                state.MarkPendingExternalChange();
            }

            if (pane.IsLoading)
            {
                _performanceLogger.Write($"folder-pane-watch-refresh-skip reason=loading paneId={pane.Id} path=\"{pane.CurrentPath}\" changedPath=\"{changedPath}\"");
                continue;
            }

            var preservedState = CaptureWorkspacePanePreservedState(pane);
            ClearWorkspacePaneItemsPreservingViewState(pane, preservedState);
            await LoadFolderPaneItemsAsync(pane, restoreTrigger: "pane-load-complete");
            pane.ActiveTabState?.ClearPendingExternalChange();
            _performanceLogger.Write($"folder-pane-watch-refresh paneId={pane.Id} path=\"{pane.CurrentPath}\" changedPath=\"{changedPath}\" items={pane.FileList.Items.Count}");
        }
    }

    private IEnumerable<FolderPane> GetWorkspacePanesForChangedPath(string changedPath)
    {
        return _workspaceDisplayPanes

            .Where(pane => !string.IsNullOrWhiteSpace(pane.CurrentPath)
                && FolderWatchTabTracker.IsPathSameOrUnderFolder(pane.CurrentPath, changedPath));
    }

    private void ApplyFolderWatchMetadataChanges(IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Count == 0
            || _isLoading
            || ActiveNavigation is not { } navigation
            || ActiveTab is not { } activeTab
            || SpecialLocationService.IsSpecialUri(navigation.CurrentPath)
            || activeTab.IsDisconnected
            || !string.Equals(_itemsOwnerStateId, activeTab.State.Id, StringComparison.Ordinal))
        {
            return;
        }

        var activePath = navigation.CurrentPath;
        var updated = 0;
        var missing = 0;
        var selectedChanged = false;
        foreach (var changedPath in changedPaths)
        {
            if (!IsDirectChildPath(activePath, changedPath))
            {
                missing++;
                continue;
            }

            var entry = FindItemByPath(changedPath);
            if (entry is null)
            {
                missing++;
                continue;
            }

            if (!entry.RefreshMetadataFromPath(changedPath))
            {
                missing++;
                continue;
            }

            updated++;
            selectedChanged |= ItemsList.SelectedItems.Contains(entry);
        }

        if (updated == 0)
        {
            _performanceLogger.Write($"folder-watch-metadata-skip path=\"{activePath}\" changes={changedPaths.Count} missing={missing}");
            return;
        }

        RefreshCurrentFolderSummary();
        if (selectedChanged)
        {
            UpdateSelectedItemStatus();
        }

        activeTab.StoreItems(navigation.CurrentPath, _items.ToList());
        activeTab.ClearPendingExternalChange();
        ClearPendingFolderWatchRefresh(activePath);
        _performanceLogger.Write($"folder-watch-metadata-update path=\"{activePath}\" changes={changedPaths.Count} updated={updated} missing={missing} items={_items.Count}");
    }

    private FileEntry? FindItemByPath(string path)
    {
        return _items.FirstOrDefault(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDirectChildPath(string folderPath, string childPath)
    {
        try
        {
            var folder = Path.GetFullPath(folderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var child = Path.GetFullPath(childPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!child.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !child.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = Path.GetRelativePath(folder, child);
            return !string.IsNullOrWhiteSpace(relative)
                && relative != "."
                && relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void SuppressFolderWatchRefreshForSelfOperation(string reason)
    {
        _fileWatcherRefreshCoordinator.SuppressRefresh();
        ActiveTab?.ClearPendingExternalChange();
        _performanceLogger.Write($"folder-watch-refresh-suppress reason={reason} durationMs={(int)_fileWatcherRefreshCoordinator.SuppressDuration.TotalMilliseconds}");
    }

    private async Task ProcessPendingFolderWatchRefreshAsync()
    {
        var activePath = ActiveNavigation?.CurrentPath;
        if (!_fileWatcherRefreshCoordinator.TryGetPendingRefreshPath(activePath, out var pendingPath))
        {
            return;
        }

        if (!CanRefreshFromFolderWatch())
        {
            _performanceLogger.Write($"folder-watch-refresh-deferred path=\"{pendingPath}\" loading={_isLoading} rename={IsRenameInteractionActive()} drag={_isFileDragInProgress} fileOperation={_isFileOperationInProgress} selecting={_selectionInteraction.IsSelecting} autoScroll={_scrollBehavior.IsAutoScrolling}");
            return;
        }

        if (!_fileWatcherRefreshCoordinator.TryBeginRefresh(pendingPath))
        {
            return;
        }

        try
        {
            _performanceLogger.Write($"folder-watch-refresh path=\"{pendingPath}\"");
            await NavigateToFolderAsync(pendingPath, NavigationKind.Refresh);
        }
        finally
        {
            _fileWatcherRefreshCoordinator.CompleteRefresh();
        }
    }

    private bool CanRefreshFromFolderWatch()
    {
        return ActiveTab is { } tab && CanRefreshFromFolderWatch(tab);
    }

    private bool CanRefreshFromFolderWatch(FolderTab tab)
    {
        return InputSuppressionService.CanProcessBackgroundRefresh(GetInputBusyState())
            && !tab.IsDisconnected
            && !SpecialLocationService.IsSpecialUri(tab.Navigation.CurrentPath)
            && Directory.Exists(tab.Navigation.CurrentPath);
    }

    private void UpdateFolderWatch(bool force = false)
    {
        if (_isSwitchingWorkspacePane && !force)
        {
            return;
        }

        var watchPaths = new List<string>();
        if (GetSelectedInternalPage() is null && ActiveSession is not null)
        {
            if (WorkspaceSplitGrid.Visibility == Visibility.Visible && _workspaceDisplayPanes.Count > 0)
            {
                foreach (var pane in _workspaceDisplayPanes)
                {
                    if (pane.ActiveTab?.Navigation.CurrentPath is { } path)
                    {
                        watchPaths.Add(path);
                    }
                }
            }
            else
            {
                var activePane = ActiveSession.ActivePaneGroup ?? ActiveSession.PaneGroups.FirstOrDefault();
                if (activePane?.ActiveTab?.Navigation.CurrentPath is { } path)
                {
                    watchPaths.Add(path);
                }
            }
        }

        _folderWatchTabTracker.UpdateWatchedFolders(watchPaths, ClearPendingFolderWatchRefresh);
    }

    private void UpdateFolderWatchForOpenTabs(bool force = false)
    {
        UpdateFolderWatch(force);
    }

    private void ClearPendingFolderWatchRefresh(string path)
    {
        _fileWatcherRefreshCoordinator.ClearPendingRefresh(path);
    }

    private async Task<bool> ShouldRefreshTabOnSwitchAsync(FolderTab tab)
    {
        var path = tab.Navigation.CurrentPath;
        if (!tab.HasPendingExternalChange
            || tab.IsDisconnected
            || SpecialLocationService.IsSpecialUri(path)
            || !CanRefreshFromFolderWatch(tab)
            || !FolderWatchService.CanWatchFolder(path))
        {
            return false;
        }

        var availability = await _driveAvailabilityService.CheckAsync(path);
        if (!availability.IsAvailable)
        {
            return false;
        }

        return true;
    }
}
