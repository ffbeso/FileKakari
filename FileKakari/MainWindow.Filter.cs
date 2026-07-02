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
    private bool FilterEntry(object item)
    {
        _filterPredicateCount++;
        if (item is not FileEntry entry)
        {
            return false;
        }

        var filter = FilterBox?.Text;
        return string.IsNullOrWhiteSpace(filter)
            || entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool UpdateItemsFilter(string filter)
    {
        var shouldEnableFilter = !string.IsNullOrWhiteSpace(filter);
        if (_itemsFilterEnabled == shouldEnableFilter)
        {
            return false;
        }

        ItemsView.Filter = shouldEnableFilter ? FilterEntry : null;
        _itemsFilterEnabled = shouldEnableFilter;
        return true;
    }

    private async void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
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

        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = new CancellationTokenSource();
        var token = _filterCancellation.Token;
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
            UpdateItemsFilter(filter);
            _performanceLogger.Write($"filter-restoring-tab-skip-refresh textLength={filter.Length} items={_items.Count}");
            return;
        }

        if (_items.Count >= 1000)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(filter)
                ? _text.Format("ItemsCount", _items.Count)
                : _text.Format("Filtering", _items.Count);
        }

        try
        {
            await Task.Delay(120, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var filterChangedView = UpdateItemsFilter(filter);
        if (filterChangedView)
        {
            stopwatch.Stop();
            if (_items.Count >= 1000)
            {
                _performanceLogger.Write($"filter-refresh count={_items.Count} textLength={filter.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }

            _statusSummaryCoordinator.StatusMessagePrefix = null;
            RefreshCurrentFolderSummary();
            UpdateSelectedItemStatus();
            return;
        }

        RefreshItemsView("filter-text-changed");
        stopwatch.Stop();
        if (_items.Count >= 1000)
        {
            _performanceLogger.Write($"filter-refresh count={_items.Count} textLength={filter.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }

        _statusSummaryCoordinator.StatusMessagePrefix = null;
        RefreshCurrentFolderSummary();
        UpdateSelectedItemStatus();
    }

    private void ClearFilterIfNeeded(string reason = "explicit-clear", [CallerMemberName] string? caller = null)
    {
        if (string.IsNullOrEmpty(FilterBox.Text))
        {
            return;
        }

        LogFilterClear(GetNormalFolderPane(), ActiveTabState, FilterBox.Text, caller ?? "unknown", reason);
        FilterBox.Text = "";
    }

    private void SaveCurrentFilterToState(WorkspaceTabState targetState, [CallerMemberName] string? caller = null)
    {
        LogFilterTextChanged(GetNormalFolderPane(), targetState, targetState.FilterText, FilterBox.Text, caller ?? "unknown", "save-current-filter-to-state");
        targetState.FilterText = FilterBox.Text;
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

    private void WorkspacePaneFilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox
            || GetWorkspacePaneFromSender(sender) is not { } pane
            || pane.ActiveTabState is not { } state)
        {
            return;
        }

        var oldFilter = state.FilterText;
        var filterChanged = !string.Equals(oldFilter, textBox.Text, StringComparison.Ordinal);
        state.FilterText = textBox.Text;
        LogFilterTextChanged(pane, state, oldFilter, state.FilterText, nameof(WorkspacePaneFilterBox_TextChanged), "workspace-pane-filter-box");
        _folderPaneController.ApplyFilter(pane, state.FilterText);
        if (filterChanged)
        {
            _workspaceLocalState.MarkDirty("pane-filter");
        }
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
