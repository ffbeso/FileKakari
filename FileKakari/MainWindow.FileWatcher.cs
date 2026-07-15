using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow
{
    private readonly Dictionary<string, PendingNormalRefresh> _pendingNormalRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingWorkspacePaneWatchRefresh> _pendingWorkspacePaneWatchRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingFolderWatchMetadataPaths = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _delayedFolderWatchRefreshTimer;
    private bool _isExecutingDelayedFolderWatchRefresh;



    private void FolderWatchService_ChangeObserved(string changedPath)
    {
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                _folderWatchTabTracker.MarkTabsPendingExternalChange(changedPath);
                MarkWorkspaceDisplayPanesExternalChange(changedPath);
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

        if (ActiveNavigation is not { } navigation || ActiveTab is not { } activeTab)
        {
            return;
        }

        var activePath = navigation.CurrentPath;
        if (!FolderWatchTabTracker.IsPathSameOrUnderFolder(activePath, changedPath))
        {
            return;
        }

        var sessionId = ActiveSession?.Id ?? "unknown";
        var stateId = activeTab.State.Id;

        if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var remaining))
        {
            EnqueueNormalRefresh(sessionId, stateId, activePath, changedPath, remaining);
            return;
        }

        EnqueueNormalRefresh(sessionId, stateId, activePath, changedPath, TimeSpan.Zero);
        await ProcessPendingFolderWatchRefreshAsync();
    }

    private void EnqueueNormalRefresh(string sessionId, string stateId, string path, string changedPath, TimeSpan remaining)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var key = PendingNormalRefresh.GetKey(sessionId, stateId, path);
        var exists = _pendingNormalRefreshes.TryGetValue(key, out var existing);
        var action = exists ? "merge" : "enqueue";

        _pendingNormalRefreshes[key] = new PendingNormalRefresh(sessionId, stateId, path, changedPath, IsFullRefresh: true);

        _performanceLogger.Write(
            $"folder-watch-refresh-suppressed path=\"{path}\" changedPath=\"{changedPath}\" " +
            $"pendingAction={action} refreshType=full remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)}");

        if (remaining > TimeSpan.Zero)
        {
            ScheduleDelayedFolderWatchRefresh(remaining, "normal-full-suppressed");
        }
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

        var sessionId = ActiveSession?.Id ?? "unknown";

        if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var remaining))
        {
            EnqueueWorkspacePaneWatchRefreshes(sessionId, panes, changedPath, remaining);
            return;
        }

        foreach (var pane in panes)
        {
            EnqueueWorkspacePaneWatchRefresh(sessionId, pane, changedPath, TimeSpan.Zero);
        }
        await ProcessPendingWorkspacePaneWatchRefreshAsync();
    }

    private void EnqueueWorkspacePaneWatchRefreshes(string sessionId, IReadOnlyList<FolderPane> panes, string changedPath, TimeSpan remaining)
    {
        foreach (var pane in panes)
        {
            EnqueueWorkspacePaneWatchRefresh(sessionId, pane, changedPath, remaining);
        }
    }

    private void EnqueueWorkspacePaneWatchRefresh(string sessionId, FolderPane pane, string changedPath, TimeSpan remaining)
    {
        if (pane.ActiveTabState is not { } state)
        {
            _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason=no-active-state paneId={pane.Id} path=\"{pane.CurrentPath}\" changedPath=\"{changedPath}\" refreshType=full uiApply=false");
            return;
        }

        pane.FileList.MarkExternalChange();
        state.MarkPendingExternalChange();

        var key = PendingWorkspacePaneWatchRefresh.GetKey(sessionId, pane.Id, state.Id, state.CurrentPath);
        var exists = _pendingWorkspacePaneWatchRefreshes.TryGetValue(key, out var existing);
        var action = exists ? "merge" : "enqueue";

        _pendingWorkspacePaneWatchRefreshes[key] = new PendingWorkspacePaneWatchRefresh(sessionId, pane.Id, state.Id, state.CurrentPath, changedPath, IsFullRefresh: true);

        _performanceLogger.Write(
            $"folder-pane-watch-refresh-suppressed paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" " +
            $"changedPath=\"{changedPath}\" pendingAction={action} refreshType=full remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)}");

        if (remaining > TimeSpan.Zero)
        {
            ScheduleDelayedFolderWatchRefresh(remaining, "workspace-full-suppressed");
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
        if (changedPaths.Count == 0)
        {
            return;
        }

        if (ActiveNavigation is not { } navigation || ActiveTab is not { } activeTab)
        {
            return;
        }

        var activePath = navigation.CurrentPath;
        var sessionId = ActiveSession?.Id ?? "unknown";
        var stateId = activeTab.State.Id;

        if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var remaining))
        {
            var beforeCount = _pendingFolderWatchMetadataPaths.Count;

            var key = PendingNormalRefresh.GetKey(sessionId, stateId, activePath);
            var hasFullPending = _pendingNormalRefreshes.ContainsKey(key);

            foreach (var changedPath in changedPaths)
            {
                if (string.IsNullOrWhiteSpace(changedPath) || !IsDirectChildPath(activePath, changedPath))
                {
                    continue;
                }

                _folderWatchTabTracker.MarkTabsPendingExternalChange(changedPath);
                MarkWorkspaceDisplayPanesExternalChange(changedPath);

                if (hasFullPending)
                {
                    continue;
                }

                var hasExistingMetadataForPath = _pendingFolderWatchMetadataPaths.Any(p => IsDirectChildPath(activePath, p));
                if (hasExistingMetadataForPath)
                {
                    EnqueueNormalRefresh(sessionId, stateId, activePath, changedPath, remaining);
                    hasFullPending = true;
                    var childPaths = _pendingFolderWatchMetadataPaths.Where(p => IsDirectChildPath(activePath, p)).ToList();
                    foreach (var cp in childPaths)
                    {
                        _pendingFolderWatchMetadataPaths.Remove(cp);
                    }
                }
                else
                {
                    if (_pendingFolderWatchMetadataPaths.Add(changedPath))
                    {
                        addedCount++;
                    }
                }
            }

            if (addedCount > 0)
            {
                var action = beforeCount == 0 ? "enqueue" : "merge";
                _performanceLogger.Write(
                    $"folder-watch-metadata-suppressed changes={changedPaths.Count} pendingBefore={beforeCount} " +
                    $"pendingAfter={_pendingFolderWatchMetadataPaths.Count} pendingAction={action} " +
                    $"refreshType=metadata remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)}");
                ScheduleDelayedFolderWatchRefresh(remaining, "metadata-suppressed");
            }
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
        if (_pendingNormalRefreshes.Values.Any(p => string.Equals(p.Path, activePath, StringComparison.OrdinalIgnoreCase)))
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

    private async Task<bool> ProcessPendingFolderWatchRefreshAsync()
    {
        var activeSession = ActiveSession;
        var activeTab = ActiveTab;

        foreach (var pending in _pendingNormalRefreshes.Values.ToList())
        {
            var session = _workspaceSessions.FirstOrDefault(s => string.Equals(s.Id, pending.SessionId, StringComparison.Ordinal));
            if (session is null)
            {
                _pendingNormalRefreshes.Remove(pending.Key);
                _performanceLogger.Write($"folder-watch-refresh-skipped reason=session-missing sessionId={pending.SessionId} path=\"{pending.Path}\" refreshType=full uiApply=false");
                continue;
            }

            var tab = session.Tabs.FirstOrDefault(t => string.Equals(t.State.Id, pending.StateId, StringComparison.Ordinal));
            if (tab is null || !string.Equals(tab.Navigation.CurrentPath, pending.Path, StringComparison.OrdinalIgnoreCase))
            {
                _pendingNormalRefreshes.Remove(pending.Key);
                _performanceLogger.Write($"folder-watch-refresh-skipped reason=state-mismatch sessionId={pending.SessionId} stateId={pending.StateId} path=\"{pending.Path}\" refreshType=full uiApply=false");
                continue;
            }

            var isActive = activeSession is not null
                && string.Equals(activeSession.Id, pending.SessionId, StringComparison.Ordinal)
                && activeTab is not null
                && string.Equals(activeTab.State.Id, pending.StateId, StringComparison.Ordinal);

            if (!isActive)
            {
                _pendingNormalRefreshes.Remove(pending.Key);
                tab.MarkPendingExternalChange();
                _performanceLogger.Write($"folder-watch-refresh-deferred-nonactive sessionId={pending.SessionId} stateId={pending.StateId} path=\"{pending.Path}\" refreshType=full");
                continue;
            }

            if (!CanRefreshFromFolderWatch(tab))
            {
                _performanceLogger.Write($"folder-watch-refresh-skipped reason=background-refresh-busy path=\"{pending.Path}\" refreshType=full uiApply=false");
                return false;
            }

            if (!_fileWatcherRefreshCoordinator.TryBeginRefresh())
            {
                _performanceLogger.Write($"folder-watch-refresh-skipped reason=lock-failed path=\"{pending.Path}\" refreshType=full uiApply=false");
                return false;
            }

            var capturedPending = pending;
            var success = false;
            try
            {
                var beforeCount = _items.Count;
                _performanceLogger.Write($"folder-watch-refresh-start path=\"{pending.Path}\" refreshType=full itemsBefore={beforeCount}");
                await NavigateToFolderAsync(pending.Path, NavigationKind.Refresh);
                _performanceLogger.Write($"folder-watch-refresh-complete path=\"{pending.Path}\" refreshType=full itemsBefore={beforeCount} itemsAfter={_items.Count} uiApply=true");
                success = true;
            }
            catch (Exception ex)
            {
                _performanceLogger.Write($"folder-watch-refresh-failed path=\"{pending.Path}\" error=\"{ex.Message}\"");
            }
            finally
            {
                _fileWatcherRefreshCoordinator.CompleteRefresh();
            }

            if (success)
            {
                if (_pendingNormalRefreshes.TryGetValue(pending.Key, out var currentPending) && ReferenceEquals(currentPending, capturedPending))
                {
                    _pendingNormalRefreshes.Remove(pending.Key);
                }
            }
            return true;
        }
        return false;
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
        var sessionId = ActiveSession?.Id ?? "unknown";
        var stateId = ActiveTab?.State.Id ?? "unknown";
        var key = PendingNormalRefresh.GetKey(sessionId, stateId, path);
        _pendingNormalRefreshes.Remove(key);
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
            $"pendingNormal={_pendingNormalRefreshes.Count} pendingWorkspace={_pendingWorkspacePaneWatchRefreshes.Count} pendingMetadata={_pendingFolderWatchMetadataPaths.Count}");
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
        var executedAny = false;
        try
        {
            _performanceLogger.Write(
                $"folder-watch-suppression-end pendingNormal={_pendingNormalRefreshes.Count} " +
                $"pendingWorkspace={_pendingWorkspacePaneWatchRefreshes.Count} pendingMetadata={_pendingFolderWatchMetadataPaths.Count}");
            _performanceLogger.Write("folder-watch-delayed-refresh-executed");

            executedAny = await ProcessPendingFolderWatchRefreshAsync();

            if (!executedAny)
            {
                executedAny = await ProcessPendingWorkspacePaneWatchRefreshAsync();
            }

            if (!executedAny)
            {
                ApplyPendingFolderWatchMetadataRefreshes();
            }
        }
        finally
        {
            _isExecutingDelayedFolderWatchRefresh = false;
        }

        if (HasDelayedFolderWatchRefreshPending())
        {
            ScheduleDelayedFolderWatchRefresh(TimeSpan.FromMilliseconds(50), "refresh-deferred");
        }
    }

    private async Task<bool> ProcessPendingWorkspacePaneWatchRefreshAsync()
    {
        var activeSession = ActiveSession;

        foreach (var pending in _pendingWorkspacePaneWatchRefreshes.Values.ToList())
        {
            if (!_pendingWorkspacePaneWatchRefreshes.ContainsKey(pending.Key))
            {
                continue;
            }

            var session = _workspaceSessions.FirstOrDefault(s => string.Equals(s.Id, pending.SessionId, StringComparison.Ordinal));
            if (session is null)
            {
                _pendingWorkspacePaneWatchRefreshes.Remove(pending.Key);
                _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason=session-missing paneId={pending.PaneId} stateId={pending.StateId} path=\"{pending.Path}\" changedPath=\"{pending.ChangedPath}\" refreshType=full uiApply=false");
                continue;
            }

            var pane = _workspaceDisplayPanes.FirstOrDefault(p => string.Equals(p.Id, pending.PaneId, StringComparison.OrdinalIgnoreCase));
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

            var isActive = activeSession is not null && string.Equals(activeSession.Id, pending.SessionId, StringComparison.Ordinal);

            if (!isActive)
            {
                _pendingWorkspacePaneWatchRefreshes.Remove(pending.Key);
                pane.FileList.MarkExternalChange();
                state.MarkPendingExternalChange();
                _performanceLogger.Write($"folder-pane-watch-refresh-deferred-nonactive sessionId={pending.SessionId} paneId={pending.PaneId} stateId={pending.StateId} path=\"{pending.Path}\" refreshType=full");
                continue;
            }

            if (!CanRefreshWorkspacePaneFromFolderWatch(pane, out var skipReason))
            {
                _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason={skipReason} paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" changedPath=\"{pending.ChangedPath}\" refreshType=full uiApply=false");
                return false;
            }

            if (!_fileWatcherRefreshCoordinator.TryBeginRefresh())
            {
                _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason=lock-failed paneId={pane.Id} path=\"{pending.Path}\" refreshType=full uiApply=false");
                return false;
            }

            var capturedPending = pending;
            var success = false;
            try
            {
                var beforeCount = pane.FileList.Items.Count;
                _performanceLogger.Write($"folder-pane-watch-refresh-start paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" changedPath=\"{pending.ChangedPath}\" refreshType=full itemsBefore={beforeCount}");
                var preservedState = CaptureWorkspacePanePreservedState(pane);
                ClearWorkspacePaneItemsPreservingViewState(pane, preservedState);
                await LoadFolderPaneItemsAsync(pane, restoreTrigger: "pane-load-complete");
                pane.ActiveTabState?.ClearPendingExternalChange();
                _performanceLogger.Write($"folder-pane-watch-refresh-complete paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" changedPath=\"{pending.ChangedPath}\" refreshType=full itemsBefore={beforeCount} itemsAfter={pane.FileList.Items.Count} uiApply=true");
                success = true;
            }
            catch (Exception ex)
            {
                _performanceLogger.Write($"folder-pane-watch-refresh-failed paneId={pane.Id} error=\"{ex.Message}\"");
            }
            finally
            {
                _fileWatcherRefreshCoordinator.CompleteRefresh();
            }

            if (success)
            {
                if (_pendingWorkspacePaneWatchRefreshes.TryGetValue(pending.Key, out var currentPending) && ReferenceEquals(currentPending, capturedPending))
                {
                    _pendingWorkspacePaneWatchRefreshes.Remove(pending.Key);
                }
            }
            return true;
        }
        return false;
    }

    private void ApplyPendingFolderWatchMetadataRefreshes()
    {
        if (_pendingFolderWatchMetadataPaths.Count == 0)
        {
            return;
        }

        if (ActiveNavigation?.CurrentPath is { } activePath && ActiveTab is { } activeTab)
        {
            var sessionId = ActiveSession?.Id ?? "unknown";
            var stateId = activeTab.State.Id;
            var key = PendingNormalRefresh.GetKey(sessionId, stateId, activePath);

            if (_pendingNormalRefreshes.ContainsKey(key))
            {
                _performanceLogger.Write($"folder-watch-metadata-skipped reason=full-refresh-pending path=\"{activePath}\" changes={_pendingFolderWatchMetadataPaths.Count} refreshType=metadata uiApply=false");
                var childPaths = _pendingFolderWatchMetadataPaths.Where(p => IsDirectChildPath(activePath, p)).ToList();
                foreach (var cp in childPaths)
                {
                    _pendingFolderWatchMetadataPaths.Remove(cp);
                }
                return;
            }

            if (!CanRefreshFromFolderWatch())
            {
                _performanceLogger.Write($"folder-watch-metadata-skipped reason=background-refresh-busy path=\"{activePath}\" changes={_pendingFolderWatchMetadataPaths.Count} refreshType=metadata uiApply=false");
                return;
            }
        }

        var activeFolderPath = ActiveNavigation?.CurrentPath;
        if (activeFolderPath is not null)
        {
            var changedPaths = _pendingFolderWatchMetadataPaths.Where(p => IsDirectChildPath(activeFolderPath, p)).ToList();
            foreach (var cp in changedPaths)
            {
                _pendingFolderWatchMetadataPaths.Remove(cp);
            }
            ApplyFolderWatchMetadataChanges(changedPaths);
        }
    }

    private bool HasDelayedFolderWatchRefreshPending()
    {
        return _pendingNormalRefreshes.Count > 0
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

internal sealed record PendingNormalRefresh(string SessionId, string StateId, string Path, string ChangedPath, bool IsFullRefresh)
{
    public string Key => GetKey(this.SessionId, this.StateId, this.Path);

    public static string GetKey(string sessionId, string stateId, string path)
    {
        var normalized = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return $"{sessionId}\u001f{stateId}\u001f{normalized.ToLowerInvariant()}";
    }
}

internal sealed record PendingWorkspacePaneWatchRefresh(string SessionId, string PaneId, string StateId, string Path, string ChangedPath, bool IsFullRefresh)
{
    public string Key => GetKey(this.SessionId, this.PaneId, this.StateId, this.Path);

    public static string GetKey(string sessionId, string paneId, string stateId, string path)
    {
        var normalized = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return $"{sessionId}\u001f{paneId}\u001f{stateId}\u001f{normalized.ToLowerInvariant()}";
    }
}
