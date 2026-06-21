using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

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

        activeTab.FilterText = filter;

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

    private void ClearFilterIfNeeded()
    {
        if (string.IsNullOrEmpty(FilterBox.Text))
        {
            return;
        }

        FilterBox.Text = "";
    }

    private void SaveCurrentFilterToState(WorkspaceTabState targetState)
    {
        targetState.FilterText = FilterBox.Text;
    }

    private void RestoreFilterFromState(WorkspaceTabState targetState)
    {
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = null;

        _isRestoringTabState = true;
        try
        {
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

        var filterChanged = !string.Equals(state.FilterText, textBox.Text, StringComparison.Ordinal);
        state.FilterText = textBox.Text;
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
}
