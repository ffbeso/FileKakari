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

        var result = await _listViewRestore.RestoreAsync(
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

    private async Task RestoreWorkspacePaneScrollOffsetAsync(FolderPane pane, double offset)
    {
        if (offset < 0)
        {
            return;
        }

        // First Stage: ContextIdle
        await Dispatcher.InvokeAsync(() =>
        {
            if (GetFolderPaneListView(pane) is { } listView
                && FindVisualChild<ScrollViewer>(listView) is { } scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset(offset);
            }

            pane.ScrollOffset = offset;
            if (pane.ActiveTabState is { } state)
            {
                state.VerticalOffset = offset;
            }
        }, DispatcherPriority.ContextIdle);

        // Second Stage: Render (to override any virtualization adjustments or ScrollIntoView side-effects)
        await Dispatcher.InvokeAsync(() =>
        {
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
                await LoadFolderPaneItemsAsync(pane, FileListRestorePolicy.RevealAndSelectPaths);
            }
            else
            {
                var preservedState = CaptureWorkspacePanePreservedState(pane);
                ClearWorkspacePaneItemsPreservingViewState(pane, preservedState);
                await LoadFolderPaneItemsAsync(pane, policy);
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

        foreach (var pane in _workspaceDisplayPanes)
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

    private async Task RestoreWorkspacePaneStateAsync(FolderPane pane, FileListRestorePolicy policy)
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
                _performanceLogger.Write($"workspace-restore-skip paneId={pane.Id} reason=targetState-null");
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
                _performanceLogger.Write($"workspace-restore-skip paneId={pane.Id} reason=guard-mismatch-path " +
                    $"pane:\"{pane.FileList.CurrentPath}\" vs state:\"{targetState.CurrentPath}\"");
            }
            return;
        }

        if (!sortColMatch || !sortDirMatch || !filterMatch)
        {
            if (_performanceLogger.IsEnabled)
            {
                _performanceLogger.Write($"workspace-restore-warning-mismatch paneId={pane.Id} " +
                    $"sortColMatch={sortColMatch}(pane:\"{pane.FileList.DisplaySortColumn}\" vs state:\"{targetState.SortColumn}\") " +
                    $"sortDirMatch={sortDirMatch}(pane:{pane.FileList.DisplaySortAscending} vs state:{targetState.SortAscending}) " +
                    $"filterMatch={filterMatch}(pane:\"{pane.FileList.DisplayFilterText}\" vs state:\"{targetState.FilterText}\")");
            }
        }

        if (_performanceLogger.IsEnabled)
        {
            var listView = GetFolderPaneListView(pane);
            var scrollViewer = listView != null ? FindVisualChild<ScrollViewer>(listView) : null;
            _performanceLogger.Write($"workspace-restore-start paneId={pane.Id} path=\"{targetState.CurrentPath}\" policy={policy} " +
                $"listViewExists={listView != null} scrollViewerExists={scrollViewer != null} itemsCount={listView?.Items.Count ?? -1} " +
                $"offset={targetState.VerticalOffset} selectedCount={targetState.SelectedPaths.Count}");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        policy = ResolveWorkspacePaneRestorePolicy(pane, targetState, policy);

        if (policy == FileListRestorePolicy.ExactRestore)
        {
            await RestoreWorkspacePaneExactStateAsync(pane, targetState);
        }
        else if (policy == FileListRestorePolicy.ScrollOnly)
        {
            await RestoreWorkspacePaneScrollOnlyStateAsync(pane, targetState.VerticalOffset);
        }
        else if (policy == FileListRestorePolicy.FocusPathFallback)
        {
            await RestoreWorkspacePaneFocusedSelectionAsync(pane, targetState.SelectedPaths);
        }
        else if (policy == FileListRestorePolicy.RevealAndSelectPaths)
        {
            await RevealWorkspacePaneSelectionAsync(pane, targetState.SelectedPaths);
        }

        stopwatch.Stop();
        if (_performanceLogger.IsEnabled)
        {
            _performanceLogger.Write($"workspace-restore-complete paneId={pane.Id} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
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

    private async Task RestoreWorkspacePaneExactStateAsync(FolderPane pane, WorkspaceTabState targetState)
    {
        if (targetState.SelectedPaths.Count > 0)
        {
            var selectedEntries = await Dispatcher.InvokeAsync(() =>
            {
                return SelectItemsInPaneByPaths(
                    pane,
                    targetState.SelectedPaths,
                    focus: false,
                    scrollIntoView: targetState.VerticalOffset <= 0);
            }, DispatcherPriority.Send);

            if (_performanceLogger.IsEnabled)
            {
                _performanceLogger.Write($"workspace-restore-selection-done paneId={pane.Id} count={selectedEntries.Count}");
            }
        }

        // Wait for container layout generation before restoring scroll offset.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        await RestoreWorkspacePaneScrollOffsetAsync(pane, targetState.VerticalOffset);
    }

    private async Task RestoreWorkspacePaneScrollOnlyStateAsync(FolderPane pane, double verticalOffset)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (GetFolderPaneListView(pane) is { } listView)
            {
                listView.SelectedItems.Clear();
            }
        }, DispatcherPriority.Send);

        // Wait for container layout generation before restoring scroll offset.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        await RestoreWorkspacePaneScrollOffsetAsync(pane, verticalOffset);
    }

    private async Task RestoreWorkspacePaneFocusedSelectionAsync(
        FolderPane pane,
        IReadOnlyCollection<string> selectedPaths)
    {
        var selectedEntries = await Dispatcher.InvokeAsync(() =>
        {
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
        await ListViewRestoreService.CenterItemAsync(
            listView,
            firstSelected,
            () => FindVisualChild<ScrollViewer>(listView),
            Dispatcher);
    }

    private async Task RevealWorkspacePaneSelectionAsync(
        FolderPane pane,
        IReadOnlyCollection<string> selectedPaths)
    {
        if (selectedPaths.Count == 0)
        {
            return;
        }

        await RestoreWorkspacePaneFocusedSelectionAsync(pane, selectedPaths);
    }

    private sealed record WorkspacePanePreservedState(
        IReadOnlyList<string> SelectedPaths,
        double VerticalOffset);
}
