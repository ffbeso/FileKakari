using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FileKakari;

public partial class MainWindow
{
    private const int FilterDebounceDelayMs = 300;

    private void SynchronizeSharedFilterBox(WorkspaceSession session)
    {
        if (FilterBox is null)
        {
            return;
        }

        var filterText = string.Empty;
        if (session.ActivePaneGroup?.ActiveTab?.State is { } state)
        {
            filterText = state.FilterText ?? string.Empty;
        }

        if (!string.Equals(FilterBox.Text, filterText, StringComparison.Ordinal))
        {
            _isSyncingPaneFilter = true;
            try
            {
                FilterBox.Text = filterText;
                if (NormalPaneFilterBox is not null)
                {
                    NormalPaneFilterBox.Text = filterText;
                }
            }
            finally
            {
                _isSyncingPaneFilter = false;
            }
        }
    }

    private bool FilterEntry(object item, string filter)
    {
        _filterPredicateCount++;
        if (item is not FileEntry entry)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(filter)
            || entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool UpdateItemsFilter(string filter)
    {
        filter ??= string.Empty;
        var shouldEnableFilter = !string.IsNullOrWhiteSpace(filter);
        if (!shouldEnableFilter)
        {
            if (!_itemsFilterEnabled)
            {
                return false;
            }

            ItemsView.Filter = null;
            _itemsFilterEnabled = false;
            return true;
        }

        // Keep the predicate bound to the tab state supplied by the caller.
        // FilterBox is only the editor for the active pane and must not decide
        // how a reload or watcher update filters its collection.
        ItemsView.Filter = item => FilterEntry(item, filter);
        _itemsFilterEnabled = true;
        return true;
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isSyncingPaneFilter
            && !string.Equals(NormalPaneFilterBox.Text, FilterBox.Text, StringComparison.Ordinal))
        {
            _isSyncingPaneFilter = true;
            try
            {
                NormalPaneFilterBox.Text = FilterBox.Text;
            }
            finally
            {
                _isSyncingPaneFilter = false;
            }
        }

        var filter = FilterBox.Text;
        if (ActiveTab is not { } activeTab)
        {
            return;
        }

        var oldFilter = activeTab.FilterText;
        activeTab.FilterText = filter;
        LogFilterTextChanged(GetNormalFolderPane(), activeTab.State, oldFilter, filter, nameof(FilterBox_TextChanged), "normal-filter-box");

        if (_isRestoringTabState)
        {
            ApplyNormalFilterNow(filter, "restore");
            _performanceLogger.Write($"filter-restoring-tab-skip-refresh textLength={filter.Length} items={_items.Count}");
            return;
        }

        ScheduleNormalFilterApply(activeTab.State, filter);
    }

    private bool ClearFilterIfNeeded(string reason = "explicit-clear", [CallerMemberName] string? caller = null)
    {
        var pane = GetActiveFolderPane();
        return ClearFilterIfNeeded(pane, reason, caller);
    }

    private bool ClearFilterIfNeeded(FolderPane? pane, string reason, string? caller)
    {
        if (pane is not null && IsWorkspaceDisplayPane(pane))
        {
            return ClearWorkspacePaneFilterIfNeeded(pane, reason, caller);
        }

        return ClearNormalPaneFilterIfNeeded(reason, caller);
    }

    private bool ClearNormalPaneFilterIfNeeded(string reason, string? caller)
    {
        var state = ActiveTabState;
        var currentFilter = state?.FilterText ?? FilterBox.Text;
        if (string.IsNullOrEmpty(currentFilter))
        {
            return false;
        }

        CancelPendingFilterApply();
        LogFilterClear(GetNormalFolderPane(), state, currentFilter, caller ?? "unknown", reason);
        if (state is not null)
        {
            state.FilterText = "";
        }

        _isSyncingPaneFilter = true;
        try
        {
            FilterBox.Text = "";
            if (NormalPaneFilterBox is not null)
            {
                NormalPaneFilterBox.Text = "";
            }
        }
        finally
        {
            _isSyncingPaneFilter = false;
        }
        ApplyNormalFilterNow("", reason);
        return true;
    }

    private bool ClearWorkspacePaneFilterIfNeeded(FolderPane pane, string reason, string? caller)
    {
        if (pane.ActiveTabState is not { } state
            || string.IsNullOrEmpty(state.FilterText))
        {
            return false;
        }

        CancelPendingFilterApply();
        var oldFilter = state.FilterText;
        LogFilterTextChanged(pane, state, oldFilter, "", caller ?? "unknown", reason);
        state.FilterText = "";
        SyncWorkspacePaneFilterTextBox(pane, "");
        ApplyWorkspacePaneFilterNow(pane, state, "", reason);
        _workspaceLocalState.MarkDirty("pane-filter");
        return true;
    }

    private void SyncWorkspacePaneFilterTextBox(FolderPane pane, string filter)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible)
        {
            return;
        }

        foreach (var textBox in FindVisualChildren<TextBox>(WorkspaceSplitGrid))
        {
            if (Equals(textBox.Tag, "PaneFilterBox")
                && ReferenceEquals(GetWorkspacePaneFromSender(textBox), pane)
                && !string.Equals(textBox.Text, filter, StringComparison.Ordinal))
            {
                textBox.Text = filter;
                return;
            }
        }
    }

    private void SaveCurrentFilterToState(WorkspaceTabState targetState, [CallerMemberName] string? caller = null)
    {
        // TextChanged writes the user's edit to the active tab immediately.
        // Do not overwrite a pane-owned value from the shared editor during a
        // reload, watcher refresh, or tab switch.
        var filter = targetState.FilterText ?? string.Empty;
        if (ReferenceEquals(targetState, ActiveTabState)
            && !string.Equals(FilterBox.Text, filter, StringComparison.Ordinal))
        {
            _isSyncingPaneFilter = true;
            try
            {
                FilterBox.Text = filter;
                if (NormalPaneFilterBox is not null)
                {
                    NormalPaneFilterBox.Text = filter;
                }
            }
            finally
            {
                _isSyncingPaneFilter = false;
            }
        }
    }

    private void RestoreFilterFromState(WorkspaceTabState targetState, [CallerMemberName] string? caller = null)
    {
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = null;

        _isRestoringTabState = true;
        try
        {
            LogFilterTextChanged(GetNormalFolderPane(), targetState, FilterBox.Text, targetState.FilterText, caller ?? "unknown", "restore-filter-from-state");
            FilterBox.Text = targetState.FilterText;
        }
        finally
        {
            _isRestoringTabState = false;
        }
    }

    private void NormalPaneFilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isSyncingPaneFilter)
        {
            return;
        }

        _isSyncingPaneFilter = true;
        try
        {
            FilterBox.Text = NormalPaneFilterBox.Text;
        }
        finally
        {
            _isSyncingPaneFilter = false;
        }
    }

    private void ScheduleNormalFilterApply(WorkspaceTabState state, string filter)
    {
        var generation = BeginFilterApplySchedule(GetNormalFolderPane(), state, filter);
        var token = _filterCancellation!.Token;
        _ = ApplyNormalFilterAfterDelayAsync(state.Id, filter, generation, token);
    }

    private void ScheduleWorkspacePaneFilterApply(FolderPane pane, WorkspaceTabState state, string filter)
    {
        var generation = BeginFilterApplySchedule(pane, state, filter);
        var token = _filterCancellation!.Token;
        _ = ApplyWorkspacePaneFilterAfterDelayAsync(pane, state.Id, filter, generation, token);
    }

    private int BeginFilterApplySchedule(FolderPane? pane, WorkspaceTabState state, string filter)
    {
        CancelPendingFilterApply();
        var generation = ++_filterApplyGeneration;
        _filterCancellation = new CancellationTokenSource();
        _performanceLogger.Write(
            $"filter-schedule paneId=\"{pane?.Id ?? "normal"}\" tabStateId=\"{state.Id}\" " +
            $"filterText=\"{EscapeLogValue(filter)}\" delayMs={FilterDebounceDelayMs}");
        return generation;
    }

    private async Task ApplyNormalFilterAfterDelayAsync(
        string stateId,
        string filter,
        int generation,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(FilterDebounceDelayMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested
            || generation != _filterApplyGeneration
            || WorkspaceSplitGrid.Visibility == Visibility.Visible
            || ActiveTabState?.Id != stateId
            || !string.Equals(FilterBox.Text, filter, StringComparison.Ordinal))
        {
            return;
        }

        ApplyNormalFilterNow(filter, "debounce");
    }

    private async Task ApplyWorkspacePaneFilterAfterDelayAsync(
        FolderPane pane,
        string stateId,
        string filter,
        int generation,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(FilterDebounceDelayMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested
            || generation != _filterApplyGeneration
            || !ReferenceEquals(GetActiveFolderPane(), pane)
            || pane.ActiveTabState?.Id != stateId
            || !string.Equals(pane.ActiveTabState.FilterText, filter, StringComparison.Ordinal))
        {
            return;
        }

        ApplyWorkspacePaneFilterNow(pane, pane.ActiveTabState, filter, "debounce");
    }

    private void ApplyActiveFilterImmediately(string reason)
    {
        CancelPendingFilterApply();
        var pane = GetActiveFolderPane();
        if (pane is not null
            && IsWorkspaceDisplayPane(pane)
            && pane.ActiveTabState is { } state)
        {
            ApplyWorkspacePaneFilterNow(pane, state, state.FilterText, reason);
            return;
        }

        ApplyNormalFilterNow(ActiveTabState?.FilterText ?? FilterBox.Text, reason);
    }

    private void ApplyNormalFilterNow(string filter, string reason)
    {
        var state = ActiveTabState;
        var stopwatch = Stopwatch.StartNew();
        UpdateItemsFilter(filter);
        RefreshItemsView($"filter-{reason}");
        stopwatch.Stop();
        _statusSummaryCoordinator.StatusMessagePrefix = null;
        RefreshCurrentFolderSummary();
        UpdateSelectedItemStatus();
        LogFilterApply(GetNormalFolderPane(), state, filter, _items.Count, CountVisibleItems(ItemsView), stopwatch.ElapsedMilliseconds, reason);
    }

    private void RefreshPaneItemsPreservingFilter(
        FolderPane pane,
        string source,
        bool preserveSelection = true,
        bool preserveScroll = true)
    {
        if (pane.ActiveTabState is not { } state)
        {
            _performanceLogger.Write($"pane-filter-refresh source={source} paneId={pane.Id} filterPreserved=false collectionViewRefreshed=false skipReason=no-active-state");
            return;
        }

        var session = FindSessionContainingPane(pane);
        var filter = state.FilterText ?? string.Empty;
        var items = GetPaneItems(pane);
        var itemCountBefore = items.Count;
        var visibleCountBefore = IsWorkspaceDisplayPane(pane)
            ? CountVisibleItems(pane.FileList.ItemsView)
            : CountVisibleItems(ItemsView);

        if (IsWorkspaceDisplayPane(pane))
        {
            _folderPaneController.ApplyFilter(pane, filter);
            _folderPaneController.UpdateStatus(pane);
        }
        else
        {
            if (ReferenceEquals(pane, GetNormalFolderPane())
                && !string.Equals(FilterBox.Text, filter, StringComparison.Ordinal))
            {
                _isSyncingPaneFilter = true;
                try
                {
                    FilterBox.Text = filter;
                    if (NormalPaneFilterBox is not null)
                    {
                        NormalPaneFilterBox.Text = filter;
                    }
                }
                finally
                {
                    _isSyncingPaneFilter = false;
                }
            }

            UpdateItemsFilter(filter);
            RefreshItemsView($"pane-filter-refresh-{source}");
            if (ReferenceEquals(pane, GetNormalFolderPane()))
            {
                RefreshCurrentFolderSummary();
                UpdateSelectedItemStatus();
            }
        }

        var visibleCountAfter = IsWorkspaceDisplayPane(pane)
            ? CountVisibleItems(pane.FileList.ItemsView)
            : CountVisibleItems(ItemsView);
        _performanceLogger.Write(
            $"pane-filter-refresh source={source} sessionId={session?.Id ?? "unknown"} paneId={pane.Id} " +
            $"path=\"{System.IO.Path.GetFileName(state.CurrentPath)}\" filterTextLength={filter.Length} filterPreserved=true " +
            $"itemCountBefore={itemCountBefore} itemCountAfter={items.Count} " +
            $"visibleCountBefore={visibleCountBefore} visibleCountAfter={visibleCountAfter} " +
            $"collectionViewRefreshed=true preserveSelection={preserveSelection} preserveScroll={preserveScroll}");
    }

    private void ApplyWorkspacePaneFilterNow(FolderPane pane, WorkspaceTabState state, string filter, string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        _folderPaneController.ApplyFilter(pane, filter);
        stopwatch.Stop();
        _folderPaneController.UpdateStatus(pane);
        LogFilterApply(pane, state, filter, pane.FileList.Items.Count, CountVisibleItems(pane.FileList.ItemsView), stopwatch.ElapsedMilliseconds, reason);
    }

    private bool IsFocusedFilterTextBox()
    {
        return Keyboard.FocusedElement is DependencyObject focused
            && (ReferenceEquals(focused, FilterBox)
                || ReferenceEquals(focused, NormalPaneFilterBox)
                || FindVisualParent<TextBox>(focused) is { } textBox
                    && Equals(textBox.Tag, "PaneFilterBox"));
    }

    private void CancelPendingFilterApply()
    {
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = null;
        _filterApplyGeneration++;
    }

    private void LogFilterApply(
        FolderPane? pane,
        WorkspaceTabState? state,
        string filter,
        int totalCount,
        int visibleCount,
        long elapsedMs,
        string reason)
    {
        _performanceLogger.Write(
            $"filter-apply paneId=\"{pane?.Id ?? "normal"}\" tabStateId=\"{state?.Id ?? ""}\" " +
            $"filterText=\"{EscapeLogValue(filter)}\" totalCount={totalCount} visibleCount={visibleCount} " +
            $"elapsedMs={elapsedMs} reason={reason}");
    }

    private static int CountVisibleItems(System.ComponentModel.ICollectionView itemsView)
    {
        var count = 0;
        foreach (var _ in itemsView)
        {
            count++;
        }

        return count;
    }

    private void FocusPaneFilterBox()
    {
        if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            FocusWorkspacePaneTextBox("PaneFilterBox");
            return;
        }

        FocusAndSelectTextBox(NormalPaneFilterBox);
    }

    private void LogFilterTextChanged(
        FolderPane? pane,
        WorkspaceTabState? state,
        string? oldFilter,
        string? newFilter,
        string caller,
        string reason)
    {
        oldFilter ??= "";
        newFilter ??= "";
        if (string.Equals(oldFilter, newFilter, StringComparison.Ordinal))
        {
            return;
        }

        _performanceLogger.Write(
            $"filter-text-changed paneId=\"{pane?.Id ?? "normal"}\" tabStateId=\"{state?.Id ?? ""}\" " +
            $"oldFilter=\"{EscapeLogValue(oldFilter)}\" newFilter=\"{EscapeLogValue(newFilter)}\" " +
            $"caller=\"{caller}\" reason=\"{reason}\"");

        if (oldFilter.Length > 0 && newFilter.Length == 0)
        {
            LogFilterClear(pane, state, oldFilter, caller, reason);
        }
    }

    private void LogFilterClear(
        FolderPane? pane,
        WorkspaceTabState? state,
        string? oldFilter,
        string caller,
        string reason)
    {
        _performanceLogger.Write(
            $"filter-clear paneId=\"{pane?.Id ?? "normal"}\" tabStateId=\"{state?.Id ?? ""}\" " +
            $"oldFilter=\"{EscapeLogValue(oldFilter ?? "")}\" caller=\"{caller}\" reason=\"{reason}\" " +
            $"stack=\"{EscapeLogValue(GetLogStackSummary())}\"");
    }

    private void LogListViewClick(
        ListView listView,
        FolderPane? pane,
        MouseButtonEventArgs e,
        DependencyObject? source)
    {
        var entry = FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
        var isEmptyArea = source is not null
            && IsInsideListView(listView, source)
            && !IsInsideScrollBar(source)
            && FindVisualParent<GridViewColumnHeader>(source) is null
            && entry is null;
        var state = pane?.ActiveTabState ?? ActiveTabState;
        _performanceLogger.Write(
            $"listview-click paneId=\"{pane?.Id ?? "normal"}\" tabStateId=\"{state?.Id ?? ""}\" " +
            $"clickCount={e.ClickCount} button=\"{e.ChangedButton}\" hitType=\"{GetListViewHitType(source, entry)}\" " +
            $"hitItemPath=\"{EscapeLogValue(entry?.FullPath ?? "")}\" isEmptyArea={isEmptyArea} " +
            $"isFilterTextBoxFocused={IsFilterTextBoxFocused(pane)} " +
            $"keyboardFocusedElement=\"{EscapeLogValue(GetKeyboardFocusedElementDescription())}\"");
    }

    private static string GetListViewHitType(DependencyObject? source, FileEntry? entry)
    {
        if (source is null) return "null";
        if (FindVisualParent<ScrollBar>(source) is not null) return "scrollbar";
        if (FindVisualParent<GridViewColumnHeader>(source) is not null) return "header";
        if (entry is not null) return "item";
        return "empty";
    }

    private bool IsFilterTextBoxFocused(FolderPane? pane)
    {
        if (Keyboard.FocusedElement is not DependencyObject focused)
        {
            return false;
        }

        if (ReferenceEquals(focused, FilterBox) || ReferenceEquals(focused, NormalPaneFilterBox))
        {
            return true;
        }

        var focusedTextBox = FindVisualParent<TextBox>(focused);
        return focusedTextBox is not null
            && Equals(focusedTextBox.Tag, "PaneFilterBox")
            && (pane is null || ReferenceEquals(GetWorkspacePaneFromSender(focusedTextBox), pane));
    }

    private static string GetKeyboardFocusedElementDescription()
    {
        if (Keyboard.FocusedElement is not object focused)
        {
            return "null";
        }

        return focused is FrameworkElement element
            ? $"{focused.GetType().Name}:{element.Name}"
            : focused.GetType().Name;
    }

    private static bool IsInsideListView(ListView listView, DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, listView))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static string GetLogStackSummary()
    {
        var frames = new StackTrace(skipFrames: 2, fNeedFileInfo: false)
            .GetFrames();
        if (frames is null)
        {
            return "";
        }

        return string.Join(">", frames
            .Select(frame => frame.GetMethod()?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(6));
    }

    private static string EscapeLogValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
