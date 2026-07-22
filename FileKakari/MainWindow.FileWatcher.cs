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
            async () => await RequestFolderWatchMetadataRefreshAsync(changedPaths),
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
        var processed = await ProcessPendingFolderWatchRefreshAsync();
        if (processed && HasDelayedFolderWatchRefreshPending())
        {
            ScheduleDelayedFolderWatchRefresh(TimeSpan.FromMilliseconds(50), "normal-refresh-remaining");
        }
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

        if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var remaining))
        {
            EnqueueWorkspacePaneWatchRefreshes(panes, changedPath, remaining);
            return;
        }

        foreach (var pane in panes)
        {
            EnqueueWorkspacePaneWatchRefresh(pane, changedPath, TimeSpan.Zero);
        }
        var processed = await ProcessPendingWorkspacePaneWatchRefreshAsync();
        if (processed && HasDelayedFolderWatchRefreshPending())
        {
            ScheduleDelayedFolderWatchRefresh(TimeSpan.FromMilliseconds(50), "workspace-refresh-remaining");
        }
    }

    private void EnqueueWorkspacePaneWatchRefreshes(IReadOnlyList<FolderPane> panes, string changedPath, TimeSpan remaining)
    {
        foreach (var pane in panes)
        {
            EnqueueWorkspacePaneWatchRefresh(pane, changedPath, remaining);
        }
    }

    private void EnqueueWorkspacePaneWatchRefresh(FolderPane pane, string changedPath, TimeSpan remaining)
    {
        var ownerSession = FindSessionContainingPane(pane);
        if (ownerSession is null)
        {
            _performanceLogger.Write(
                $"folder-pane-watch-refresh-skipped reason=owner-session-unresolved paneId={pane.Id} skipReason=owner-session-unresolved");
            return;
        }

        if (pane.ActiveTabState is not { } state)
        {
            _performanceLogger.Write($"folder-pane-watch-refresh-skipped reason=no-active-state paneId={pane.Id} ownerSessionId={ownerSession.Id} refreshType=full uiApply=false");
            return;
        }

        pane.FileList.MarkExternalChange();
        state.MarkPendingExternalChange();

        var key = PendingWorkspacePaneWatchRefresh.GetKey(ownerSession.Id, pane.Id, state.Id, state.CurrentPath);
        var exists = _pendingWorkspacePaneWatchRefreshes.TryGetValue(key, out var existing);
        var action = exists ? "merge" : "enqueue";

        _pendingWorkspacePaneWatchRefreshes[key] = new PendingWorkspacePaneWatchRefresh(ownerSession.Id, pane.Id, state.Id, state.CurrentPath, changedPath, IsFullRefresh: true);

        var suppressionState = remaining > TimeSpan.Zero ? "suppressed" : "ready";
        _performanceLogger.Write(
            $"folder-pane-watch-refresh-suppressed paneId={pane.Id} stateId={state.Id} path=\"{state.CurrentPath}\" " +
            $"changedPath=\"{changedPath}\" pendingAction={action} refreshType=full remainingMs={(int)Math.Ceiling(remaining.TotalMilliseconds)} " +
            $"suppressionState={suppressionState} pendingKey=\"{key}\" ownerSessionId={ownerSession.Id} refreshTargetPaneId={pane.Id}");

        if (remaining > TimeSpan.Zero)
        {
            ScheduleDelayedFolderWatchRefresh(remaining, "workspace-full-suppressed");
        }
    }

    private IEnumerable<FolderPane> GetWorkspacePanesForChangedPath(string changedPath)
    {
        return GetDisplayedWorkspacePanes()
            .Where(pane => !string.IsNullOrWhiteSpace(pane.CurrentPath)
                && FolderWatchTabTracker.IsPathSameOrUnderFolder(pane.CurrentPath, changedPath));
    }

    private async Task RequestFolderWatchMetadataRefreshAsync(IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Count == 0)
        {
            return;
        }

        if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            var panesToRefresh = new HashSet<FolderPane>();
            foreach (var changedPath in changedPaths)
            {
                foreach (var pane in GetDisplayedWorkspacePanes().Where(pane => IsDirectChildPath(pane.CurrentPath, changedPath)))
                {
                    panesToRefresh.Add(pane);
                }
            }

            if (panesToRefresh.Count == 0)
            {
                return;
            }

            if (_fileWatcherRefreshCoordinator.IsSuppressed(_isFileOperationInProgress, out var workspaceRemaining))
            {
                foreach (var pane in panesToRefresh)
                {
                    var changedPath = changedPaths.First(path => IsDirectChildPath(pane.CurrentPath, path));
                    EnqueueWorkspacePaneWatchRefresh(pane, changedPath, workspaceRemaining);
                }
                return;
            }

            foreach (var pane in panesToRefresh)
            {
                var changedPath = changedPaths.First(path => IsDirectChildPath(pane.CurrentPath, path));
                EnqueueWorkspacePaneWatchRefresh(pane, changedPath, TimeSpan.Zero);
            }

            var processed = await ProcessPendingWorkspacePaneWatchRefreshAsync();
            if (processed && HasDelayedFolderWatchRefreshPending())
            {
                ScheduleDelayedFolderWatchRefresh(TimeSpan.FromMilliseconds(50), "workspace-metadata-refresh-remaining");
            }
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
            var addedCount = 0;

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

        if (GetNormalFolderPane() is { } normalPane)
        {
            RefreshPaneItemsPreservingFilter(normalPane, "watcher-metadata");
        }
        else
        {
            RefreshCurrentFolderSummary();
            if (selectedChanged)
            {
                UpdateSelectedItemStatus();
            }
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

    private WorkspaceSession? GetWorkspaceSessionForPath(string path)
    {
        return GetDisplayedWorkspaceSessionsInTabOrder().FirstOrDefault(s =>
            s.PaneGroups.Any(p => string.Equals(p.ActiveTab?.Navigation.CurrentPath, path, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdateFolderWatch(bool force = false)
    {
        if (_isSwitchingWorkspacePane && !force)
        {
            return;
        }

        var watchPaths = new List<string>();
        if (GetSelectedInternalPage() is null)
        {
            var displayedSessions = GetDisplayedWorkspaceSessionsInTabOrder();
            foreach (var session in displayedSessions)
            {
                foreach (var pane in session.PaneGroups)
                {
                    if (pane.ActiveTab?.Navigation.CurrentPath is { } path && !string.IsNullOrWhiteSpace(path))
                    {
                        watchPaths.Add(path);
                    }
                }
            }
        }

        var currentWatched = _folderWatchService.CurrentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newWatched = watchPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _folderWatchTabTracker.UpdateWatchedFolders(newWatched, ClearPendingFolderWatchRefresh);

        foreach (var path in newWatched)
        {
            if (!currentWatched.Contains(path))
            {
                var owner = GetWorkspaceSessionForPath(path);
                var activePane = owner?.ActivePaneGroup ?? owner?.PaneGroups.FirstOrDefault();
                _performanceLogger.Write(
                    $"watcher-added watcherOwnerSessionId={owner?.Id ?? "unknown"} watcherOwnerPaneId={activePane?.Id ?? "unknown"} watchedPath=\"{path}\" watcherAdded=true");
            }
        }
        foreach (var path in currentWatched)
        {
            if (!newWatched.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _performanceLogger.Write(
                    $"watcher-removed watchedPath=\"{path}\" watcherRemoved=true");
            }
        }
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

    private void DiscardPendingFolderWatchRefreshesForRemovedSessions(
        IEnumerable<string> removedSessionIds,
        string source)
    {
        var removedIds = removedSessionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removedIds.Count == 0)
        {
            return;
        }

        var normalKeys = _pendingNormalRefreshes
            .Where(pair => removedIds.Contains(pair.Value.SessionId))
            .Select(pair => pair.Key)
            .ToList();
        var workspaceKeys = _pendingWorkspacePaneWatchRefreshes
            .Where(pair => removedIds.Contains(pair.Value.SessionId))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in normalKeys)
        {
            _pendingNormalRefreshes.Remove(key);
        }
        foreach (var key in workspaceKeys)
        {
            _pendingWorkspacePaneWatchRefreshes.Remove(key);
        }

        // Metadata pending entries do not currently retain a SessionId.  They
        // belong only to the pre-split normal-pane route, so clearing them on a
        // session-set mutation avoids applying a closed owner's changes to the
        // next active session.
        _pendingFolderWatchMetadataPaths.Clear();

        _performanceLogger.Write(
            $"workspace-session-pending-discarded source={source} sessionRemoved=[{string.Join(",", removedIds)}] " +
            $"normalRemoved={normalKeys.Count} workspaceRemoved={workspaceKeys.Count}");
    }

    private void DiscardPendingFolderWatchRefreshesForFailedPane(
        WorkspaceSession session,
        FolderPane pane,
        WorkspaceTabState state)
    {
        var workspaceKeys = _pendingWorkspacePaneWatchRefreshes.Values
            .Where(pending => string.Equals(pending.SessionId, session.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pending.PaneId, pane.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pending.StateId, state.Id, StringComparison.Ordinal))
            .Select(pending => pending.Key)
            .ToList();
        foreach (var key in workspaceKeys)
        {
            _pendingWorkspacePaneWatchRefreshes.Remove(key);
        }

        var normalKeys = _pendingNormalRefreshes.Values
            .Where(pending => string.Equals(pending.SessionId, session.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pending.StateId, state.Id, StringComparison.Ordinal))
            .Select(pending => pending.Key)
            .ToList();
        foreach (var key in normalKeys)
        {
            _pendingNormalRefreshes.Remove(key);
        }

        var metadataPendingCleared = false;
        if (ReferenceEquals(pane, GetNormalFolderPane()) && _pendingFolderWatchMetadataPaths.Count > 0)
        {
            _pendingFolderWatchMetadataPaths.Clear();
            metadataPendingCleared = true;
        }

        if (workspaceKeys.Count > 0 || normalKeys.Count > 0 || metadataPendingCleared)
        {
            _performanceLogger.Write($"folder-watch-pending-discarded reason=folder-load-failure sessionId={session.Id} paneId={pane.Id} stateId={state.Id} workspacePending={workspaceKeys.Count} normalPending={normalKeys.Count} metadataPendingCleared={metadataPendingCleared}");
        }
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

            var pane = session.PaneGroups.FirstOrDefault(p => string.Equals(p.Id, pending.PaneId, StringComparison.OrdinalIgnoreCase));
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

            if (!IsWorkspaceDisplayPane(pane))
            {
                _pendingWorkspacePaneWatchRefreshes.Remove(pending.Key);
                pane.FileList.MarkExternalChange();
                state.MarkPendingExternalChange();
                _performanceLogger.Write($"folder-pane-watch-refresh-deferred-nondisplayed sessionId={pending.SessionId} paneId={pending.PaneId} stateId={pending.StateId} refreshType=full skipReason=session-not-displayed");
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
                RefreshPaneItemsPreservingFilter(pane, "watcher-full");
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
