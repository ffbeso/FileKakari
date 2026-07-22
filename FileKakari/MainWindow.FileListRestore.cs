using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow
{
    private IReadOnlyList<FileEntry> SelectItemsInPaneByPaths(
        FolderPane pane,
        IReadOnlyCollection<string> paths,
        bool focus = true,
        bool scrollIntoView = true)
    {
        var listView = GetFolderPaneListView(pane);
        if (listView is null)
        {
            return [];
        }

        using var previewSuppression = SuppressPreviewForProgrammaticSelection("restore");
        var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        listView.SelectedItems.Clear();

        var selectedEntries = new List<FileEntry>();
        FileEntry? firstSelected = null;
        var paneItems = GetPaneItems(pane);
        foreach (var entry in paneItems)
        {
            if (!pathSet.Contains(entry.FullPath))
            {
                continue;
            }

            listView.SelectedItems.Add(entry);
            selectedEntries.Add(entry);
            firstSelected ??= entry;
        }

        if (firstSelected is null)
        {
            if (IsWorkspaceDisplayPane(pane))
            {
                SyncPaneSelectionFromListView(pane, listView);
            }
            else
            {
                UpdateSelectedItemStatus();
            }
            return selectedEntries;
        }

        if (scrollIntoView)
        {
            listView.ScrollIntoView(firstSelected);
        }
        if (focus)
        {
            bool shouldFocus = true;
            if (Keyboard.FocusedElement is DependencyObject focusedElement)
            {
                var focusedPane = GetWorkspacePaneFromSender(focusedElement);
                if (focusedPane is not null && !string.Equals(focusedPane.Id, pane.Id, StringComparison.Ordinal))
                {
                    shouldFocus = false;
                }
            }
            if (shouldFocus)
            {
                listView.Focus();
            }
        }

        if (IsWorkspaceDisplayPane(pane))
        {
            SyncPaneSelectionFromListView(pane, listView);
        }
        else
        {
            UpdateSelectedItemStatus();
        }

        return selectedEntries;
    }

    private void RestorePaneSelectionWithoutFocus(FolderPane pane, IReadOnlyCollection<string> paths)
    {
        var listView = GetFolderPaneListView(pane);
        if (listView is null)
        {
            return;
        }

        using var previewSuppression = SuppressPreviewForProgrammaticSelection("restore");
        var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        listView.SelectedItems.Clear();

        var paneItems = GetPaneItems(pane);
        foreach (var entry in paneItems)
        {
            if (pathSet.Contains(entry.FullPath))
            {
                listView.SelectedItems.Add(entry);
            }
        }

        if (IsWorkspaceDisplayPane(pane))
        {
            SyncPaneSelectionFromListView(pane, listView);
        }
        else
        {
            UpdateSelectedItemStatus();
        }
    }

    private ListViewRestoreState? CaptureListViewRestoreState(FolderTab tab, string path)
    {
        var activeTab = ActiveTab;
        if (activeTab is null)
        {
            return null;
        }

        var state = _listViewRestore.CaptureFromView(
            tab,
            path,
            _isLoading,
            _isFileDragInProgress,
            _selectionInteraction.IsSelecting,
            _scrollBehavior.IsAutoScrolling,
            activeTab,
            _itemsOwnerStateId,
            ItemsList,
            FindItemsScrollViewer,
            _selectionUserVersion,
            _scrollUserVersion);
        if (state is null)
        {
            return null;
        }

        _performanceLogger.Write($"list-restore-capture stateId={tab.State.Id} path=\"{path}\" selected={state.SelectedPaths.Count} focused=\"{state.FocusedPath ?? ""}\" top=\"{state.TopVisiblePath ?? ""}\" offset={state.VerticalOffset:N1} selectionVersion={state.SelectionUserVersion} scrollVersion={state.ScrollUserVersion}");
        return state;
    }

    private async Task<bool> RestoreListViewStateAsync(
        ListViewRestoreState? state,
        FolderTab loadTab,
        int loadId,
        FileListRestorePolicy policy)
    {
        var activeTab = ActiveTab;
        if (activeTab is null || state is null || policy == FileListRestorePolicy.None)
        {
            return false;
        }

        bool isDragInProgress = policy == FileListRestorePolicy.RevealAndSelectPaths ? false : _isFileDragInProgress;
        if (!ListViewRestoreService.CanRestore(
            state,
            loadTab,
            loadId,
            _loadGeneration,
            activeTab,
            isDragInProgress,
            _selectionInteraction.IsSelecting,
            _scrollBehavior.IsAutoScrolling))
        {
            return false;
        }

        if (policy == FileListRestorePolicy.ExactRestore)
        {
            bool hasSelection = state.SelectedPaths.Count > 0;
            bool anySelectedExists = hasSelection && state.SelectedPaths.Any(path => _items.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase)));
            if (hasSelection && !anySelectedExists)
            {
                policy = FileListRestorePolicy.ScrollOnly;
            }
        }

        if (policy == FileListRestorePolicy.ScrollOnly)
        {
            state = state with { SelectedPaths = Array.Empty<string>(), FocusedPath = null };
        }
        else if (policy == FileListRestorePolicy.FocusPathFallback)
        {
            state = state with { VerticalOffset = 0, TopVisiblePath = null };
        }
        else if (policy == FileListRestorePolicy.RevealAndSelectPaths)
        {
            var firstExisting = state.SelectedPaths.FirstOrDefault(path => _items.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase)));
            state = state with {
                SelectionUserVersion = _selectionUserVersion,
                ScrollUserVersion = _scrollUserVersion,
                FocusedPath = firstExisting
            };
        }

        ListViewRestoreResult result;
        using (SuppressPreviewForProgrammaticSelection("restore"))
        {
            result = await _listViewRestore.RestoreAsync(
                state!,
                loadTab,
                loadId,
                ItemsList,
                _items,
                FindItemsScrollViewer,
                () => _loadGeneration,
                () => ActiveTab,
                () => _selectionUserVersion,
                () => _scrollUserVersion,
                UpdateSelectedItemStatus,
                Dispatcher,
                _loadCancellation?.Token ?? CancellationToken.None);
        }

        if (policy == FileListRestorePolicy.ExactRestore || policy == FileListRestorePolicy.ScrollOnly)
        {
            await RestoreListViewScrollOffsetAsync(state.VerticalOffset);
        }

        _performanceLogger.Write($"list-restore-apply id={loadId} stateId={loadTab.State.Id} path=\"{loadTab.State.CurrentPath}\" policy={policy} selected={result.SelectedCount}/{state!.SelectedPaths.Count} selectionSkipped={result.SelectionSkipped} scrollSkipped={result.ScrollSkipped} selectionCapped={result.SelectionCapped} scroll={result.ScrolledBy} offset={result.VerticalOffset:N1} elapsedMs={result.ElapsedMilliseconds}");
        return result.Restored;
    }

    private async Task RestoreListViewScrollOffsetAsync(double offset)
    {
        if (offset < 0)
        {
            return;
        }

        // First Stage: ContextIdle
        await Dispatcher.InvokeAsync(() =>
        {
            FindItemsScrollViewer()?.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.ContextIdle);

        // Second Stage: Render
        await Dispatcher.InvokeAsync(() =>
        {
            FindItemsScrollViewer()?.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Render);
    }

    private async Task RestoreWorkspacePaneScrollOffsetAsync(
        FolderPane pane,
        double offset,
        int workspaceSwitchId = 0,
        WorkspaceSession? session = null)
    {
        if (offset < 0)
        {
            return;
        }

        // First Stage: ContextIdle
        await Dispatcher.InvokeAsync(() =>
        {
            if (workspaceSwitchId > 0
                && session is not null
                && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
            {
                return;
            }

            if (GetFolderPaneListView(pane) is { } listView
                && FindVisualChild<ScrollViewer>(listView) is { } scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset(offset);
            }

            pane.ScrollOffset = offset;
            if (pane.ActiveTabState is { } state
                && string.Equals(pane.FileList.LoadedStateId, state.Id, StringComparison.Ordinal))
            {
                state.VerticalOffset = offset;
            }
        }, DispatcherPriority.ContextIdle);

        // Second Stage: Render (to override any virtualization adjustments or ScrollIntoView side-effects)
        await Dispatcher.InvokeAsync(() =>
        {
            if (workspaceSwitchId > 0
                && session is not null
                && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
            {
                return;
            }

            if (GetFolderPaneListView(pane) is { } listView
                && FindVisualChild<ScrollViewer>(listView) is { } scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset(offset);
            }
        }, DispatcherPriority.Render);
    }

    private async Task ReloadFolderPaneAsync(
        FolderPane pane,
        FileListRestorePolicy policy = FileListRestorePolicy.ExactRestore,
        IReadOnlyList<string>? targetPaths = null)
    {
        if (pane.ActiveTab is not { } tab)
        {
            return;
        }

        if (IsWorkspaceDisplayPane(pane))
        {
            if (policy == FileListRestorePolicy.RevealAndSelectPaths && targetPaths is not null)
            {
                tab.State.ClearItems();
                tab.State.SelectedPaths = targetPaths;
                tab.State.VerticalOffset = 0;
                await LoadFolderPaneItemsAsync(pane, FileListRestorePolicy.RevealAndSelectPaths, "pane-load-complete");
            }
            else
            {
                var preservedState = CaptureWorkspacePanePreservedState(pane);
                ClearWorkspacePaneItemsPreservingViewState(pane, preservedState);
                await LoadFolderPaneItemsAsync(pane, policy, "pane-load-complete");
            }
        }
        else
        {
            if (ReferenceEquals(tab, ActiveTab))
            {
                ListViewRestoreState? restoreState = null;
                if (policy == FileListRestorePolicy.RevealAndSelectPaths && targetPaths is not null)
                {
                    restoreState = new ListViewRestoreState(
                        tab.State.Id,
                        tab.Navigation.CurrentPath,
                        targetPaths,
                        targetPaths.FirstOrDefault(),
                        null,
                        0,
                        _selectionUserVersion,
                        _scrollUserVersion);
                }
                else
                {
                    restoreState = CaptureListViewRestoreState(tab, tab.Navigation.CurrentPath);
                }

                tab.ClearItems();
                await LoadFolderAsync(tab.Navigation.CurrentPath, restoreState, tab, policy);
            }
            else
            {
                tab.State.ClearItems();
            }
        }
    }

    private WorkspacePanePreservedState CaptureWorkspacePanePreservedState(FolderPane pane)
    {
        if (pane.ActiveTab is not { } tab)
        {
            return new WorkspacePanePreservedState([], 0);
        }

        SaveWorkspacePaneNavigationViewState(pane, tab);
        return new WorkspacePanePreservedState(tab.State.SelectedPaths, tab.State.VerticalOffset);
    }

    private static void ClearWorkspacePaneItemsPreservingViewState(
        FolderPane pane,
        WorkspacePanePreservedState preservedState)
    {
        pane.ActiveTabState?.ClearItems();
        if (pane.ActiveTabState is { } activeState)
        {
            activeState.SelectedPaths = preservedState.SelectedPaths;
            activeState.VerticalOffset = preservedState.VerticalOffset;
        }
    }

    private async Task<bool> ReloadFolderPanesShowingPathAsync(
        string path,
        FolderPane? preferredPane = null,
        FileListRestorePolicy policy = FileListRestorePolicy.ExactRestore,
        IReadOnlyList<string>? targetPaths = null)
    {
        var targetNormalized = NormalizePathForComparison(path);
        var panesToReload = new HashSet<FolderPane>();

        if (preferredPane is not null)
        {
            if (string.Equals(NormalizePathForComparison(preferredPane.ActiveTabState?.CurrentPath), targetNormalized, StringComparison.OrdinalIgnoreCase))
            {
                panesToReload.Add(preferredPane);
            }
        }

        foreach (var pane in GetDisplayedWorkspacePanes())
        {
            var matchFound = false;
            foreach (var tab in pane.Tabs)
            {
                if (string.Equals(NormalizePathForComparison(tab.Navigation.CurrentPath), targetNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    tab.State.ClearItems();
                    if (ReferenceEquals(tab, pane.ActiveTab))
                    {
                        matchFound = true;
                    }
                }
            }
            if (matchFound)
            {
                panesToReload.Add(pane);
            }
        }

        {
            var matchFound = false;
            foreach (var tab in _primaryPaneGroup.Tabs)
            {
                if (string.Equals(NormalizePathForComparison(tab.Navigation.CurrentPath), targetNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    tab.State.ClearItems();
                    if (ReferenceEquals(tab, _primaryPaneGroup.ActiveTab))
                    {
                        matchFound = true;
                    }
                }
            }
            if (matchFound)
            {
                panesToReload.Add(_primaryPaneGroup);
            }
        }

        var selectionsToRestore = new Dictionary<FolderPane, List<string>>();
        foreach (var pane in panesToReload)
        {
            if (!ReferenceEquals(pane, preferredPane))
            {
                selectionsToRestore[pane] = pane.SelectedPaths.ToList();
            }
        }

        bool preferredPaneReloadedWithPolicy = false;
        foreach (var pane in panesToReload)
        {
            if (ReferenceEquals(pane, preferredPane) && policy == FileListRestorePolicy.RevealAndSelectPaths)
            {
                if (pane.ActiveTab is not null && targetPaths is { Count: > 0 })
                {
                    await ReloadFolderPaneAsync(pane, policy, targetPaths);
                    preferredPaneReloadedWithPolicy = true;
                }
            }
            else
            {
                await ReloadFolderPaneAsync(pane);
            }
        }

        foreach (var kvp in selectionsToRestore)
        {
            RestorePaneSelectionWithoutFocus(kvp.Key, kvp.Value);
        }
        return preferredPaneReloadedWithPolicy;
    }

    private async Task RestoreWorkspacePaneStateAsync(
        FolderPane pane,
        FileListRestorePolicy policy,
        string trigger = "viewstate-restore",
        int workspaceSwitchId = 0)
    {
        if (policy == FileListRestorePolicy.None)
        {
            return;
        }

        var targetState = pane.ActiveTabState;
        if (targetState is null)
        {
            if (_performanceLogger.IsEnabled)
            {
                PerfLog.WriteVerbose($"workspace-restore-skip paneId={pane.Id} reason=targetState-null trigger={trigger} workspaceSwitchId={workspaceSwitchId} {GetWorkspaceRestoreFilterLog(pane, null)}");
            }
            return;
        }

        var session = FindSessionContainingPane(pane);
        if (workspaceSwitchId > 0
            && session is not null
            && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
        {
            WriteWorkspaceSwitchLog("workspace-switch-discard", workspaceSwitchId, session.Id, "stale-switch");
            return;
        }

        if (policy == FileListRestorePolicy.ExactRestore
            && IsWorkspaceSwitchRestoreInProgress
            && IsProgrammaticWorkspaceRestoreTrigger(trigger))
        {
            if (_performanceLogger.IsEnabled)
            {
                PerfLog.WriteVerbose($"workspace-restore-skip paneId={pane.Id} stateId={targetState.Id} reason=switch-restore-reentry trigger={trigger} workspaceSwitchId={workspaceSwitchId} sessionId={session?.Id ?? "not-found"} activeSessionId={_activeWorkspaceSession?.Id ?? "null"} selectedSessionId={GetSelectedWorkspaceSession()?.Id ?? "null"} {GetWorkspaceRestoreFilterLog(pane, targetState)}");
            }
            return;
        }

        if (policy == FileListRestorePolicy.ExactRestore
            && string.Equals(trigger, "active-pane-change", StringComparison.Ordinal)
            && !IsSameWorkspaceSession(_activeWorkspaceSession, GetSelectedWorkspaceSession()))
        {
            if (_performanceLogger.IsEnabled)
            {
                _performanceLogger.Write($"workspace-restore-skip paneId={pane.Id} stateId={targetState.Id} reason=active-selected-session-mismatch trigger={trigger} workspaceSwitchId={workspaceSwitchId} sessionId={session?.Id ?? "not-found"} activeSessionId={_activeWorkspaceSession?.Id ?? "null"} selectedSessionId={GetSelectedWorkspaceSession()?.Id ?? "null"} {GetWorkspaceRestoreFilterLog(pane, targetState)}");
            }
            return;
        }

        // Avoid unnatural restoration if path changed
        bool pathMatch = string.Equals(pane.FileList.CurrentPath, targetState.CurrentPath, StringComparison.OrdinalIgnoreCase);
        bool sortColMatch = string.Equals(pane.FileList.DisplaySortColumn, targetState.SortColumn, StringComparison.Ordinal);
        bool sortDirMatch = pane.FileList.DisplaySortAscending == targetState.SortAscending;
        bool filterMatch = string.Equals(pane.FileList.DisplayFilterText, targetState.FilterText, StringComparison.Ordinal);

        if (!pathMatch)
        {
            if (_performanceLogger.IsEnabled)
            {
                PerfLog.WriteVerbose($"workspace-restore-skip paneId={pane.Id} reason=guard-mismatch-path trigger={trigger} workspaceSwitchId={workspaceSwitchId} " +
                    $"pane:\"{pane.FileList.CurrentPath}\" vs state:\"{targetState.CurrentPath}\" {GetWorkspaceRestoreFilterLog(pane, targetState)}");
            }
            return;
        }

        if (!sortColMatch || !sortDirMatch || !filterMatch)
        {
            if (_performanceLogger.IsEnabled)
            {
                PerfLog.WriteVerbose($"workspace-restore-warning-mismatch paneId={pane.Id} " +
                    $"sortColMatch={sortColMatch}(pane:\"{pane.FileList.DisplaySortColumn}\" vs state:\"{targetState.SortColumn}\") " +
                    $"sortDirMatch={sortDirMatch}(pane:{pane.FileList.DisplaySortAscending} vs state:{targetState.SortAscending}) " +
                    $"filterMatch={filterMatch}(pane:\"{pane.FileList.DisplayFilterText}\" vs state:\"{targetState.FilterText}\")");
            }
        }

        var restoreKey = session is null
            ? null
            : $"{session.Id}|{pane.Id}|{targetState.Id}";
        if (policy == FileListRestorePolicy.ExactRestore
            && restoreKey is not null
            && !_activeWorkspaceRestoreKeys.Add(restoreKey))
        {
            if (_performanceLogger.IsEnabled)
            {
                PerfLog.WriteVerbose($"workspace-restore-skip paneId={pane.Id} stateId={targetState.Id} reason=duplicate-inflight trigger={trigger} workspaceSwitchId={workspaceSwitchId} sessionId={session?.Id ?? "not-found"} activeSessionId={_activeWorkspaceSession?.Id ?? "null"} selectedSessionId={GetSelectedWorkspaceSession()?.Id ?? "null"} {GetWorkspaceRestoreFilterLog(pane, targetState)}");
            }
            return;
        }

        var restoreId = System.Threading.Interlocked.Increment(ref _workspaceRestoreGeneration);
        var listView = GetFolderPaneListView(pane);

        try
        {
            if (_performanceLogger.IsEnabled)
            {
                var scrollViewer = listView != null ? FindVisualChild<ScrollViewer>(listView) : null;
                PerfLog.WriteVerbose($"workspace-restore-start restoreId={restoreId} workspaceSwitchId={workspaceSwitchId} " +
                    $"sessionId={session?.Id ?? "not-found"} paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" policy={policy} " +
                    $"caller={trigger} trigger={trigger} threadId={Environment.CurrentManagedThreadId} " +
                    $"activeSessionId={_activeWorkspaceSession?.Id ?? "null"} selectedSessionId={GetSelectedWorkspaceSession()?.Id ?? "null"} " +
                    $"isActiveSession={session?.IsActiveSession.ToString() ?? "null"} listViewHash={listView?.GetHashCode() ?? 0} paneHash={pane.GetHashCode()} " +
                    $"listViewExists={listView != null} scrollViewerExists={scrollViewer != null} itemsCount={listView?.Items.Count ?? -1} " +
                    $"offset={targetState.VerticalOffset} selectedCount={targetState.SelectedPaths.Count} {GetWorkspaceRestoreFilterLog(pane, targetState)}");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            policy = ResolveWorkspacePaneRestorePolicy(pane, targetState, policy);

            if (workspaceSwitchId > 0
                && session is not null
                && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", workspaceSwitchId, session.Id, "stale-switch");
                return;
            }

            if (policy == FileListRestorePolicy.ExactRestore)
            {
                await RestoreWorkspacePaneExactStateAsync(pane, targetState, workspaceSwitchId, session);
            }
            else if (policy == FileListRestorePolicy.ScrollOnly)
            {
                await RestoreWorkspacePaneScrollOnlyStateAsync(pane, targetState.VerticalOffset, workspaceSwitchId, session);
            }
            else if (policy == FileListRestorePolicy.FocusPathFallback)
            {
                await RestoreWorkspacePaneFocusedSelectionAsync(pane, targetState.SelectedPaths, workspaceSwitchId, session);
            }
            else if (policy == FileListRestorePolicy.RevealAndSelectPaths)
            {
                await RevealWorkspacePaneSelectionAsync(pane, targetState.SelectedPaths, workspaceSwitchId, session);
            }

            stopwatch.Stop();
            if (_performanceLogger.IsEnabled)
            {
                PerfLog.WriteVerbose($"workspace-restore-complete restoreId={restoreId} workspaceSwitchId={workspaceSwitchId} paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" policy={policy} trigger={trigger} elapsedMs={stopwatch.ElapsedMilliseconds} {GetWorkspaceRestoreFilterLog(pane, targetState)}");
            }
        }
        finally
        {
            if (restoreKey is not null)
            {
                _activeWorkspaceRestoreKeys.Remove(restoreKey);
            }
        }
    }

    private static string GetWorkspaceRestoreFilterLog(FolderPane pane, WorkspaceTabState? state)
    {
        return $"state.FilterText=\"{EscapeRestoreLogValue(state?.FilterText ?? "")}\" " +
            $"pane.FilterText=\"{EscapeRestoreLogValue(pane.FileList.DisplayFilterText)}\" " +
            $"activeTabState.FilterText=\"{EscapeRestoreLogValue(pane.ActiveTabState?.FilterText ?? "")}\"";
    }

    private static string EscapeRestoreLogValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static FileListRestorePolicy ResolveWorkspacePaneRestorePolicy(
        FolderPane pane,
        WorkspaceTabState targetState,
        FileListRestorePolicy policy)
    {
        if (policy != FileListRestorePolicy.ExactRestore || targetState.SelectedPaths.Count == 0)
        {
            return policy;
        }

        var anySelectedPathExists = targetState.SelectedPaths.Any(path =>
            pane.FileList.Items.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase)));
        return anySelectedPathExists ? policy : FileListRestorePolicy.ScrollOnly;
    }

    private async Task RestoreWorkspacePaneExactStateAsync(
        FolderPane pane,
        WorkspaceTabState targetState,
        int workspaceSwitchId = 0,
        WorkspaceSession? session = null)
    {
        if (targetState.SelectedPaths.Count > 0)
        {
            var selectedEntries = await Dispatcher.InvokeAsync(() =>
            {
                if (workspaceSwitchId > 0
                    && session is not null
                    && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
                {
                    return Array.Empty<FileEntry>();
                }

                return SelectItemsInPaneByPaths(
                    pane,
                    targetState.SelectedPaths,
                    focus: false,
                    scrollIntoView: targetState.VerticalOffset <= 0);
            }, DispatcherPriority.Send);

            if (_performanceLogger.IsEnabled)
            {
                PerfLog.WriteVerbose($"workspace-restore-selection-done paneId={pane.Id} count={selectedEntries.Count}");
            }
        }

        // Wait for container layout generation before restoring scroll offset.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (workspaceSwitchId > 0
            && session is not null
            && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
        {
            WriteWorkspaceSwitchLog("workspace-switch-discard", workspaceSwitchId, session.Id, "stale-switch");
            return;
        }

        await RestoreWorkspacePaneScrollOffsetAsync(pane, targetState.VerticalOffset, workspaceSwitchId, session);
    }

    private async Task RestoreWorkspacePaneScrollOnlyStateAsync(
        FolderPane pane,
        double verticalOffset,
        int workspaceSwitchId = 0,
        WorkspaceSession? session = null)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (workspaceSwitchId > 0
                && session is not null
                && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
            {
                return;
            }

            if (GetFolderPaneListView(pane) is { } listView)
            {
                using var previewSuppression = SuppressPreviewForProgrammaticSelection("restore");
                listView.SelectedItems.Clear();
            }
        }, DispatcherPriority.Send);

        // Wait for container layout generation before restoring scroll offset.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (workspaceSwitchId > 0
            && session is not null
            && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
        {
            WriteWorkspaceSwitchLog("workspace-switch-discard", workspaceSwitchId, session.Id, "stale-switch");
            return;
        }

        await RestoreWorkspacePaneScrollOffsetAsync(pane, verticalOffset, workspaceSwitchId, session);
    }

    private async Task RestoreWorkspacePaneFocusedSelectionAsync(
        FolderPane pane,
        IReadOnlyCollection<string> selectedPaths,
        int workspaceSwitchId = 0,
        WorkspaceSession? session = null)
    {
        var selectedEntries = await Dispatcher.InvokeAsync(() =>
        {
            if (workspaceSwitchId > 0
                && session is not null
                && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
            {
                return Array.Empty<FileEntry>();
            }

            return SelectItemsInPaneByPaths(
                pane,
                selectedPaths,
                focus: false,
                scrollIntoView: false);
        }, DispatcherPriority.Send);

        if (selectedEntries.FirstOrDefault() is not { } firstSelected
            || GetFolderPaneListView(pane) is not { } listView)
        {
            return;
        }

        // Wait for container layout generation before centering the selected item.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (workspaceSwitchId > 0
            && session is not null
            && !CanApplyWorkspaceSwitch(workspaceSwitchId, session))
        {
            WriteWorkspaceSwitchLog("workspace-switch-discard", workspaceSwitchId, session.Id, "stale-switch");
            return;
        }

        await ListViewRestoreService.CenterItemAsync(
            listView,
            firstSelected,
            () => FindVisualChild<ScrollViewer>(listView),
            Dispatcher);
    }

    private async Task RevealWorkspacePaneSelectionAsync(
        FolderPane pane,
        IReadOnlyCollection<string> selectedPaths,
        int workspaceSwitchId = 0,
        WorkspaceSession? session = null)
    {
        if (selectedPaths.Count == 0)
        {
            return;
        }

        await RestoreWorkspacePaneFocusedSelectionAsync(pane, selectedPaths, workspaceSwitchId, session);
    }

    private sealed record WorkspacePanePreservedState(
        IReadOnlyList<string> SelectedPaths,
        double VerticalOffset);

    private void SaveTabViewState(FolderTab tab)
    {
        if (_isLoading || !string.IsNullOrEmpty(_loadingStateId))
        {
            _performanceLogger.Write($"tab-cache-save-skip reason=loading stateId={tab.State.Id} activeStateId={_activeStateId ?? ""} loadingStateId={_loadingStateId ?? ""} path=\"{tab.Navigation.CurrentPath}\"");
            return;
        }

        if (!string.Equals(_itemsOwnerStateId, tab.State.Id, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var targetState = tab.State;
            var normalPane = GetNormalFolderPane();
            if (normalPane is not null)
            {
                SyncNormalPaneDisplayStateFromView(normalPane);
            }

            SaveCurrentFilterToState(targetState);
            targetState.SortColumn = NormalizeSortColumn(targetState.SortColumn);
            targetState.VerticalOffset = normalPane?.ScrollOffset ?? GetCurrentVerticalOffset();
            targetState.SelectedPaths = normalPane?.SelectedPaths.ToList()
                ?? ItemsList.SelectedItems
                    .OfType<FileEntry>()
                    .Select(entry => entry.FullPath)
                    .ToList();
            var cacheMemoryBefore = GetProcessWorkingSetBytes();
            tab.StoreItems(tab.Navigation.CurrentPath, _items.ToList());
            var cacheMemoryAfter = GetProcessWorkingSetBytes();
            _performanceLogger.Write($"tab-cache-save stateId={targetState.Id} path=\"{targetState.CurrentPath}\" items={targetState.CachedItems?.Count ?? 0} selected={targetState.SelectedPaths.Count} offset={targetState.VerticalOffset:N1} memoryDeltaMb={(cacheMemoryAfter - cacheMemoryBefore) / 1024d / 1024d:N1} memory={GetProcessMemoryStatus()}");
        }
        catch (Exception ex)
        {
            LogException("tab-cache-save", ex, tab.State);
            throw;
        }
    }

    private void SaveActiveTabViewState()
    {
        if (GetSelectedWorkspaceSession() is not null)
        {
            SaveWorkspacePanesViewState(_activeWorkspaceSession);
            var tab = ActiveTab;
            if (tab is null || _activeWorkspaceSession is null)
            {
                return;
            }

            SaveTabViewState(tab);
            if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
            {
                var rootOffset = (_activeWorkspaceSession.Workspace is not null && _activeWorkspaceSession.Workspace.HasRootPath) ? 1 : 0;
                _activeWorkspaceSession.SelectedTabIndex = Math.Clamp(
                    _activeWorkspacePaneGroup.SelectedTabIndex + rootOffset,
                    0,
                    Math.Max(0, _activeWorkspacePaneGroup.Tabs.Count));
            }
            else
            {
                var primaryPane = _activeWorkspaceSession.PaneGroups.FirstOrDefault(p => string.Equals(p.Id, "primary", StringComparison.OrdinalIgnoreCase)) ?? _activeWorkspaceSession.PaneGroups.FirstOrDefault();
                _activeWorkspaceSession.SelectedTabIndex = primaryPane is not null ? Math.Clamp(primaryPane.Tabs.IndexOf(tab), 0, Math.Max(0, primaryPane.Tabs.Count - 1)) : 0;
                _activeWorkspacePaneGroup.SelectedTabIndex = _activeWorkspaceSession.SelectedTabIndex;
            }
            _activeWorkspacePaneGroup.RefreshDisplay();
        }
    }

    private void SaveWorkspacePanesViewState()
    {
        SaveWorkspacePanesViewState(_activeWorkspaceSession);
    }

    private void SaveWorkspacePanesViewState(WorkspaceSession? targetSession)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible)
        {
            return;
        }

        if (targetSession is null)
        {
            return;
        }

        var targetPanes = targetSession.PaneGroups.ToList();

        if (_performanceLogger.IsEnabled)
        {
            var displayPaneIds = string.Join(",", _workspaceDisplayPanes.Select(p => p.Id));
            var targetPaneIds = string.Join(",", targetPanes.Select(p => p.Id));
            var activeSessionId = _activeWorkspaceSession?.Id ?? "null";
            var selectedSessionId = GetSelectedWorkspaceSession()?.Id ?? "null";

            _performanceLogger.Write($"workspace-save-viewstate-debug " +
                $"targetSessionId={targetSession.Id} " +
                $"activeSessionId={activeSessionId} " +
                $"selectedSessionId={selectedSessionId} " +
                $"displayPaneIds=[{displayPaneIds}] " +
                $"targetPanes=[{targetPaneIds}]");

            if (targetSession.LayoutRoot is null)
            {
                PerfLog.WriteVerbose($"workspace-save-viewstate-layoutroot-null " +
                    $"sessionId={targetSession.Id} " +
                    $"sessionName=\"{targetSession.Name}\" " +
                    $"_activeWorkspaceSessionId={activeSessionId}");
            }

            foreach (var pane in targetPanes)
            {
                var listView = GetFolderPaneListView(pane);
                var scrollViewer = listView != null ? FindVisualChild<ScrollViewer>(listView) : null;
                var offset = scrollViewer?.VerticalOffset ?? pane.ScrollOffset;
                var selectedCount = listView?.SelectedItems.Count ?? 0;
                var activeTabStateSelectedCount = pane.ActiveTab?.State.SelectedPaths.Count ?? 0;

                var selectedItem = listView?.SelectedItem;
                var itemType = selectedItem?.GetType().FullName ?? "null";
                var itemPath = "";
                if (selectedItem is FileEntry entry)
                {
                    itemPath = entry.FullPath;
                }
                else if (selectedItem != null)
                {
                    itemPath = selectedItem.ToString();
                }

                var paneSession = _workspaceSessions.FirstOrDefault(s => s.PaneGroups.Any(pg => ReferenceEquals(pg, pane)));
                var paneSessionId = paneSession?.Id ?? "null";

                PerfLog.WriteVerbose($"workspace-pane-save-debug paneId={pane.Id} " +
                    $"paneSessionId={paneSessionId} " +
                    $"activeSessionId={activeSessionId} " +
                    $"listViewExists={listView != null} " +
                    $"scrollViewerExists={scrollViewer != null} " +
                    $"offset={offset} " +
                    $"selectedCount={selectedCount} " +
                    $"activeTabStateSelectedCount={activeTabStateSelectedCount} " +
                    $"selectedItemType=\"{itemType}\" " +
                    $"selectedItemPath=\"{itemPath}\"");
            }
        }

        foreach (var pane in targetPanes)
        {
            if (pane.ActiveTab is { } tab)
            {
                SaveWorkspacePaneNavigationViewState(pane, tab);
            }
        }
    }

    private async Task RestoreWorkspaceTabAsync(WorkspaceSession workspaceSession)
    {
        var switchId = System.Threading.Interlocked.Increment(ref _workspaceSwitchGeneration);
        var previousCancellation = _workspaceSwitchCancellation;
        var currentCancellation = new CancellationTokenSource();
        _workspaceSwitchCancellation = currentCancellation;

        var requestedSessionId = workspaceSession.Id;
        var requestedSession = workspaceSession;
        var oldSession = _activeWorkspaceSession;
        var targetPanes = requestedSession.PaneGroups.ToList();
        var targetLayoutRoot = requestedSession.LayoutRoot;
        var targetDisplayLayoutRoot = requestedSession.DisplayLayoutRoot;

        WriteWorkspaceSwitchLog("workspace-switch-request", switchId, requestedSessionId, "selection-requested");

        if (previousCancellation is not null)
        {
            WriteWorkspaceSwitchLog("workspace-switch-cancel", switchId, requestedSessionId, "previous-switch-superseded");
            previousCancellation.Cancel();
        }

        try
        {
            _workspaceSwitchRestoreDepth++;
            var token = currentCancellation.Token;

            var result = _workspaceController.TrySelectSession(_activeWorkspaceSession, requestedSession);
            if (!result.Success)
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "selection-rejected");
                return;
            }

            token.ThrowIfCancellationRequested();

            UpdateCrashContextSnapshot("workspace-tab-restore");

            if (result.RequiresSaveActiveLocalState)
            {
                SaveWorkspacePanesViewState(oldSession);
                _workspaceLocalState.SaveActiveLocalState();
            }

            _activeWorkspaceSession = requestedSession;
            UpdateActiveWorkspaceSessionUi(requestedSession);
            RefreshWorkspaceDisplayPanes("workspace-switch-active-session-set", switchId, requestedSession);

            if (!CanApplyWorkspaceSwitch(switchId, requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "stale-switch");
                return;
            }

            if (result.ActiveSessionChanged)
            {
                CancelActiveLoadForWorkspaceSwitch(requestedSession, "workspace-restore");
            }

            ApplyWorkspaceSessionSelection(requestedSession, switchId);

            WriteWorkspacePaneDiagnostics("workspace-switch-diag", switchId, requestedSession);

            if (HasUnresolvedWorkspacePaneViews(requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-wait-ui", switchId, requestedSession.Id, "pane-view-unresolved");
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            }

            token.ThrowIfCancellationRequested();

            if (!CanApplyWorkspaceSwitch(switchId, requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSession.Id, "stale-switch");
                return;
            }

            WriteWorkspacePaneDiagnostics("workspace-switch-diag-post", switchId, requestedSession);

            if (HasUnresolvedWorkspacePaneViews(requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-container-unresolved", switchId, requestedSession.Id, "pane-view-still-unresolved");
            }

            PerfLog.WriteVerbose($"workspace-tab-restore sessionId={requestedSession.Id} root=\"{requestedSession.RootPath}\" panes={targetPanes.Count} selected={TabsControl.SelectedIndex} targetLayoutRoot=\"{DebugDumpLayout(targetLayoutRoot)}\" targetDisplayLayoutRoot=\"{DebugDumpLayout(targetDisplayLayoutRoot)}\"");

            await LoadWorkspaceDisplayPanesOnSwitchAsync(switchId, requestedSession, targetPanes, token);

            token.ThrowIfCancellationRequested();

            if (!CanApplyWorkspaceSwitch(switchId, requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "stale-switch");
                return;
            }

            try
            {
                if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
                {
                    foreach (var lv in FindVisualChildren<ListView>(WorkspaceSplitGrid))
                    {
                        WriteWorkspaceListViewDiagnostics("workspace-listview-final", lv);
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    WriteDiagLog($"event=workspace-listview-final-scan-error message=\"{ex.Message}\"");
                }
                catch { }
            }

            if (!CanApplyWorkspaceSwitch(switchId, requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "stale-switch");
                return;
            }

            ApplyWorkspacePostLoadState(requestedSession, switchId);

            if (!CanApplyWorkspaceSwitch(switchId, requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "stale-switch");
                return;
            }

            if (!EnsureWorkspaceSwitchDisplayState(switchId, requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "display-binding-mismatch");
                return;
            }

            var visibleHostDiagnostics = WriteWorkspaceVisibleHostDiagnostics("workspace-visible-host-final", switchId, requestedSession);
            var hitTestDiagnostics = WriteWorkspaceHitTestDiagnostics("workspace-hit-test-final", switchId, requestedSession);

            if (!CanApplyWorkspaceSwitch(switchId, requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "stale-switch");
                return;
            }

            if (!EnsureWorkspaceSwitchDisplayState(switchId, requestedSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "display-binding-mismatch");
                return;
            }

            WriteWorkspaceLayoutSyncDiagnostics("workspace-switch-apply:before", switchId, requestedSession);

            if (!IsVisibleWorkspaceHostMatch(requestedSession, hitTestDiagnostics))
            {
                WriteWorkspaceVisibleHostMismatchDiagnostics(
                    switchId,
                    requestedSessionId,
                    visibleHostDiagnostics,
                    hitTestDiagnostics);
            }

            WriteWorkspaceSwitchLog("workspace-switch-apply", switchId, requestedSessionId, "completed");
        }
        catch (OperationCanceledException)
        {
            WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, requestedSessionId, "cancelled");
        }
        finally
        {
            if (_workspaceSwitchRestoreDepth > 0)
            {
                _workspaceSwitchRestoreDepth--;
            }

            if (ReferenceEquals(_workspaceSwitchCancellation, currentCancellation))
            {
                _workspaceSwitchCancellation = null;
            }
            currentCancellation.Dispose();
        }
    }

    private void SaveAllActiveStates(bool saveLocalState = true)
    {
        if (saveLocalState)
        {
            _workspaceLocalState.SaveActiveLocalState();
        }

        SaveActiveTabViewState();
    }
}
