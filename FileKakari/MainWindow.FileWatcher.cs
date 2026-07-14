using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow
{
    private readonly Dictionary<string, PendingWorkspacePaneWatchRefresh> _pendingWorkspacePaneWatchRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingFolderWatchMetadataPaths = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _delayedFolderWatchRefreshTimer;
    private bool _isExecutingDelayedFolderWatchRefresh;

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
            () => RequestFolderWatchMetadataRefresh(changedPaths),
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
            _fileWatcherRefreshCoordinator.RequestRefresh(activePath);
            _folderWatchTabTracker.MarkTabsPendingExternalChange(changedPath);
            _performanceLogger.Write($"folder-watch-refresh-suppressed path=\"{activePath}\" changedPath=\"{changedPath}\" remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)} pendingAction=enqueue refreshType=full");
            ScheduleDelayedFolderWatchRefresh(remaining, "normal-full-suppressed");
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
            EnqueueWorkspacePaneWatchRefreshes(panes, changedPath, remaining);
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
                _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason=loading paneId={pane.Id} stateId={pane.ActiveTabState?.Id ?? ""} path=\"{pane.CurrentPath}\" changedPath=\"{changedPath}\" refreshType=full");
                continue;
            }

            var beforeCount = pane.FileList.Items.Count;
            _performanceLogger.Write($"folder-pane-watch-refresh-start paneId={pane.Id} stateId={pane.ActiveTabState?.Id ?? ""} path=\"{pane.CurrentPath}\" changedPath=\"{changedPath}\" refreshType=full itemsBefore={beforeCount}");
            var preservedState = CaptureWorkspacePanePreservedState(pane);
            ClearWorkspacePaneItemsPreservingViewState(pane, preservedState);
            await LoadFolderPaneItemsAsync(pane, restoreTrigger: "pane-load-complete");
            pane.ActiveTabState?.ClearPendingExternalChange();
            _performanceLogger.Write($"folder-pane-watch-refresh-complete paneId={pane.Id} stateId={pane.ActiveTabState?.Id ?? ""} path=\"{pane.CurrentPath}\" changedPath=\"{changedPath}\" refreshType=full itemsBefore={beforeCount} itemsAfter={pane.FileList.Items.Count} uiApply=true");
        }
    }

    private IEnumerable<FolderPane> GetWorkspacePanesForChangedPath(string changedPath)
    {
        return _workspaceDisplayPanes

            .Where(pane => !string.IsNullOrWhiteSpace(pane.CurrentPath)
                && FolderWatchTabTracker.IsPathSameOrUnderFolder(pane.CurrentPath, changedPath));
    }

    private void RequestFolderWatchMetadataRefresh(IReadOnlyList<string> changedPaths)
    {
        if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var remaining))
        {
            EnqueueFolderWatchMetadataRefresh(changedPaths, remaining);
            return;
        }

        ApplyFolderWatchMetadataChanges(changedPaths);
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
        if (_fileWatcherRefreshCoordinator.IsRefreshPendingFor(activePath))
        {
            _performanceLogger.Write($"folder-watch-metadata-skipped reason=full-refresh-pending path=\"{activePath}\" changes={changedPaths.Count} refreshType=metadata uiApply=false");
            return;
        }

        var updated = 0;
        var missing = 0;
        var selectedChanged = false;
        var beforeCount = _items.Count;
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
            _performanceLogger.Write($"folder-watch-metadata-skipped path=\"{activePath}\" changes={changedPaths.Count} missing={missing} refreshType=metadata uiApply=false itemsBefore={beforeCount} itemsAfter={_items.Count}");
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
        _performanceLogger.Write($"folder-watch-metadata-complete path=\"{activePath}\" changes={changedPaths.Count} updated={updated} missing={missing} refreshType=metadata uiApply=true itemsBefore={beforeCount} itemsAfter={_items.Count}");
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
        var extended = _fileWatcherRefreshCoordinator.SuppressRefresh(out var previousUntil, out var suppressUntil);
        ActiveTab?.ClearPendingExternalChange();
        var remaining = suppressUntil - DateTimeOffset.UtcNow;
        _performanceLogger.Write(
            $"folder-watch-suppression-{(extended ? "extend" : "start")} reason={reason} " +
            $"durationMs={(int)_fileWatcherRefreshCoordinator.SuppressDuration.TotalMilliseconds} remainingMs={(int)Math.Ceiling(Math.Max(0, remaining.TotalMilliseconds))} " +
            $"previousUntil=\"{previousUntil:O}\" suppressUntil=\"{suppressUntil:O}\"");
        if (HasDelayedFolderWatchRefreshPending())
        {
            ScheduleDelayedFolderWatchRefresh(remaining, "suppression-extended");
        }
    }

    private async Task ProcessPendingFolderWatchRefreshAsync()
    {
        var activePath = ActiveNavigation?.CurrentPath;
        if (!_fileWatcherRefreshCoordinator.TryGetPendingRefreshPath(activePath, out var pendingPath))
        {
            if (_fileWatcherRefreshCoordinator.PendingPath is { } stalePath)
            {
                _performanceLogger.Write($"folder-watch-refresh-skipped reason=active-path-mismatch pendingPath=\"{stalePath}\" activePath=\"{activePath ?? ""}\" refreshType=full uiApply=false");
            }
            return;
        }

        if (!CanRefreshFromFolderWatch())
        {
            _performanceLogger.Write($"folder-watch-refresh-skipped reason=background-refresh-busy path=\"{pendingPath}\" refreshType=full loading={_isLoading} rename={IsRenameInteractionActive()} drag={_isFileDragInProgress} fileOperation={_isFileOperationInProgress} selecting={_selectionInteraction.IsSelecting} autoScroll={_scrollBehavior.IsAutoScrolling} uiApply=false");
            return;
        }

        if (!_fileWatcherRefreshCoordinator.TryBeginRefresh(pendingPath))
        {
            return;
        }

        try
        {
            var beforeCount = _items.Count;
            _performanceLogger.Write($"folder-watch-refresh-start path=\"{pendingPath}\" refreshType=full itemsBefore={beforeCount}");
            await NavigateToFolderAsync(pendingPath, NavigationKind.Refresh);
            _performanceLogger.Write($"folder-watch-refresh-complete path=\"{pendingPath}\" refreshType=full itemsBefore={beforeCount} itemsAfter={_items.Count} uiApply=true");
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

    private void EnqueueFolderWatchMetadataRefresh(IReadOnlyList<string> changedPaths, TimeSpan remaining)
    {
        if (changedPaths.Count == 0)
        {
            return;
        }

        var beforeCount = _pendingFolderWatchMetadataPaths.Count;
        foreach (var changedPath in changedPaths)
        {
            if (!string.IsNullOrWhiteSpace(changedPath))
            {
                _pendingFolderWatchMetadataPaths.Add(changedPath);
                _folderWatchTabTracker.MarkTabsPendingExternalChange(changedPath);
                MarkWorkspaceDisplayPanesExternalChange(changedPath);
            }
        }

        _performanceLogger.Write(
            $"folder-watch-metadata-suppressed changes={changedPaths.Count} pendingBefore={beforeCount} " +
            $"pendingAfter={_pendingFolderWatchMetadataPaths.Count} pendingAction={(beforeCount == 0 ? "enqueue" : "merge")} " +
            $"refreshType=metadata remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)}");
        ScheduleDelayedFolderWatchRefresh(remaining, "metadata-suppressed");
    }

    private void EnqueueWorkspacePaneWatchRefreshes(IReadOnlyList<FolderPane> panes, string changedPath, TimeSpan remaining)
    {
        foreach (var pane in panes)
        {
            if (pane.ActiveTabState is not { } state)
            {
                _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason=no-active-state paneId={pane.Id} path=\"{pane.CurrentPath}\" changedPath=\"{changedPath}\" refreshType=full uiApply=false");
                continue;
            }

            pane.FileList.MarkExternalChange();
            state.MarkPendingExternalChange();
            var pending = new PendingWorkspacePaneWatchRefresh(pane.Id, state.Id, state.CurrentPath, changedPath);
            var key = pending.Key;
            var action = _pendingWorkspacePaneWatchRefreshes.ContainsKey(key) ? "merge" : "enqueue";
            _pendingWorkspacePaneWatchRefreshes[key] = pending;
            _performanceLogger.Write(
                $"folder-pane-watch-refresh-suppressed paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" " +
                $"changedPath=\"{changedPath}\" pendingAction={action} refreshType=full remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)}");
        }

        ScheduleDelayedFolderWatchRefresh(remaining, "workspace-full-suppressed");
    }

    private void ScheduleDelayedFolderWatchRefresh(TimeSpan remaining, string reason)
    {
        var interval = remaining > TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25);
        _delayedFolderWatchRefreshTimer ??= CreateDelayedFolderWatchRefreshTimer();
        var action = _delayedFolderWatchRefreshTimer.IsEnabled ? "rescheduled" : "scheduled";
        _delayedFolderWatchRefreshTimer.Stop();
        _delayedFolderWatchRefreshTimer.Interval = interval;
        _delayedFolderWatchRefreshTimer.Start();
        _performanceLogger.Write(
            $"folder-watch-delayed-refresh-{action} reason={reason} remainingMs={(int)Math.Ceiling(interval.TotalMilliseconds)} " +
            $"pendingNormal={_fileWatcherRefreshCoordinator.HasPendingRefresh} pendingWorkspace={_pendingWorkspacePaneWatchRefreshes.Count} pendingMetadata={_pendingFolderWatchMetadataPaths.Count}");
    }

    private DispatcherTimer CreateDelayedFolderWatchRefreshTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        timer.Tick += async (_, _) => await ExecuteDelayedFolderWatchRefreshAsync();
        return timer;
    }

    private async Task ExecuteDelayedFolderWatchRefreshAsync()
    {
        if (_isExecutingDelayedFolderWatchRefresh)
        {
            return;
        }

        _delayedFolderWatchRefreshTimer?.Stop();
        if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var remaining))
        {
            ScheduleDelayedFolderWatchRefresh(remaining, "suppression-still-active");
            return;
        }

        _isExecutingDelayedFolderWatchRefresh = true;
        try
        {
            _performanceLogger.Write(
                $"folder-watch-suppression-end pendingNormal={_fileWatcherRefreshCoordinator.HasPendingRefresh} " +
                $"pendingWorkspace={_pendingWorkspacePaneWatchRefreshes.Count} pendingMetadata={_pendingFolderWatchMetadataPaths.Count}");
            _performanceLogger.Write("folder-watch-delayed-refresh-executed");

            await ProcessPendingFolderWatchRefreshAsync();
            await ProcessPendingWorkspacePaneWatchRefreshAsync();
            ApplyPendingFolderWatchMetadataRefreshes();
        }
        finally
        {
            _isExecutingDelayedFolderWatchRefresh = false;
        }

        if (HasDelayedFolderWatchRefreshPending())
        {
            ScheduleDelayedFolderWatchRefresh(TimeSpan.FromMilliseconds(500), "refresh-deferred");
        }
    }

    private async Task ProcessPendingWorkspacePaneWatchRefreshAsync()
    {
        foreach (var pending in _pendingWorkspacePaneWatchRefreshes.Values.ToList())
        {
            if (!_pendingWorkspacePaneWatchRefreshes.ContainsKey(pending.Key))
            {
                continue;
            }

            var pane = _workspaceDisplayPanes.FirstOrDefault(pane => string.Equals(pane.Id, pending.PaneId, StringComparison.OrdinalIgnoreCase));
            if (pane is null)
            {
                _pendingWorkspacePaneWatchRefreshes.Remove(pending.Key);
                _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason=pane-missing paneId={pending.PaneId} stateId={pending.StateId} path=\"{pending.Path}\" changedPath=\"{pending.ChangedPath}\" refreshType=full uiApply=false");
                continue;
            }

            var state = pane.ActiveTabState;
            if (state is null
                || !string.Equals(state.Id, pending.StateId, StringComparison.Ordinal)
                || !string.Equals(state.CurrentPath, pending.Path, StringComparison.OrdinalIgnoreCase))
            {
                _pendingWorkspacePaneWatchRefreshes.Remove(pending.Key);
                _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason=state-mismatch paneId={pending.PaneId} stateId={pending.StateId} currentStateId=\"{state?.Id ?? ""}\" path=\"{pending.Path}\" currentPath=\"{state?.CurrentPath ?? ""}\" changedPath=\"{pending.ChangedPath}\" refreshType=full uiApply=false");
                continue;
            }

            if (!CanRefreshWorkspacePaneFromFolderWatch(pane, out var skipReason))
            {
                _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason={skipReason} paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" changedPath=\"{pending.ChangedPath}\" refreshType=full uiApply=false");
                continue;
            }

            _pendingWorkspacePaneWatchRefreshes.Remove(pending.Key);
            var beforeCount = pane.FileList.Items.Count;
            _performanceLogger.Write($"folder-pane-watch-refresh-start paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" changedPath=\"{pending.ChangedPath}\" refreshType=full itemsBefore={beforeCount}");
            var preservedState = CaptureWorkspacePanePreservedState(pane);
            ClearWorkspacePaneItemsPreservingViewState(pane, preservedState);
            await LoadFolderPaneItemsAsync(pane, restoreTrigger: "pane-load-complete");
            pane.ActiveTabState?.ClearPendingExternalChange();
            _performanceLogger.Write($"folder-pane-watch-refresh-complete paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" changedPath=\"{pending.ChangedPath}\" refreshType=full itemsBefore={beforeCount} itemsAfter={pane.FileList.Items.Count} uiApply=true");
        }
    }

    private void ApplyPendingFolderWatchMetadataRefreshes()
    {
        if (_pendingFolderWatchMetadataPaths.Count == 0)
        {
            return;
        }

        if (ActiveNavigation?.CurrentPath is { } activePath
            && _fileWatcherRefreshCoordinator.IsRefreshPendingFor(activePath))
        {
            _performanceLogger.Write($"folder-watch-metadata-skipped reason=full-refresh-pending path=\"{activePath}\" changes={_pendingFolderWatchMetadataPaths.Count} refreshType=metadata uiApply=false");
            return;
        }

        var changedPaths = _pendingFolderWatchMetadataPaths.ToList();
        _pendingFolderWatchMetadataPaths.Clear();
        ApplyFolderWatchMetadataChanges(changedPaths);
    }

    private bool HasDelayedFolderWatchRefreshPending()
    {
        return _fileWatcherRefreshCoordinator.HasPendingRefresh
            || _pendingWorkspacePaneWatchRefreshes.Count > 0
            || _pendingFolderWatchMetadataPaths.Count > 0;
    }

    private bool CanRefreshWorkspacePaneFromFolderWatch(FolderPane pane, out string reason)
    {
        if (_isSwitchingWorkspacePane)
        {
            reason = "workspace-switching";
            return false;
        }

        if (pane.IsLoading)
        {
            reason = "loading";
            return false;
        }

        if (!InputSuppressionService.CanProcessBackgroundRefresh(GetInputBusyState()))
        {
            reason = "background-refresh-busy";
            return false;
        }

        if (pane.ActiveTab is not { } tab || pane.ActiveTabState is not { } state)
        {
            reason = "no-active-state";
            return false;
        }

        if (tab.IsDisconnected)
        {
            reason = "disconnected";
            return false;
        }

        if (SpecialLocationService.IsSpecialUri(state.CurrentPath))
        {
            reason = "special-location";
            return false;
        }

        if (!Directory.Exists(state.CurrentPath))
        {
            reason = "path-missing";
            return false;
        }

        reason = "";
        return true;
    }

    private sealed record PendingWorkspacePaneWatchRefresh(string PaneId, string StateId, string Path, string ChangedPath)
    {
        public string Key => $"{PaneId}\u001f{StateId}\u001f{Path}";
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
