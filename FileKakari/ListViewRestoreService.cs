using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileKakari;

internal sealed class ListViewRestoreService
{
    private const int MaxRestoreSelectionCount = 512;

    public bool IsRestoring { get; private set; }

    public static bool CanCapture(
        FolderTab tab,
        string path,
        bool isLoading,
        bool isFileDragInProgress,
        bool isSelecting,
        bool isAutoScrolling,
        object? selectedTab,
        string? itemsOwnerStateId)
    {
        return !isLoading
            && !isFileDragInProgress
            && !isSelecting
            && !isAutoScrolling
            && !tab.IsDisconnected
            && !SpecialLocationService.IsSpecialUri(path)
            && ReferenceEquals(selectedTab, tab)
            && string.Equals(itemsOwnerStateId, tab.State.Id, StringComparison.Ordinal)
            && string.Equals(tab.State.CurrentPath, path, StringComparison.OrdinalIgnoreCase);
    }

    public static ListViewRestoreState? CreateFromTab(FolderTab tab, int selectionUserVersion, int scrollUserVersion)
    {
        if (tab.IsDisconnected || SpecialLocationService.IsSpecialUri(tab.Navigation.CurrentPath))
        {
            return null;
        }

        return new ListViewRestoreState(
            tab.State.Id,
            tab.State.CurrentPath,
            tab.State.SelectedPaths.ToList(),
            tab.State.SelectedPaths.FirstOrDefault(),
            null,
            tab.State.VerticalOffset,
            selectionUserVersion,
            scrollUserVersion);
    }

    public ListViewRestoreState? CaptureFromView(
        FolderTab tab,
        string path,
        bool isLoading,
        bool isFileDragInProgress,
        bool isSelecting,
        bool isAutoScrolling,
        object? selectedTab,
        string? itemsOwnerStateId,
        ListView itemsList,
        Func<ScrollViewer?> findScrollViewer,
        int selectionUserVersion,
        int scrollUserVersion)
    {
        if (!CanCapture(
            tab,
            path,
            isLoading,
            isFileDragInProgress,
            isSelecting,
            isAutoScrolling,
            selectedTab,
            itemsOwnerStateId))
        {
            return null;
        }

        var selectedPaths = itemsList.SelectedItems
            .OfType<FileEntry>()
            .Select(entry => entry.FullPath)
            .ToList();
        var focusedPath = GetFocusedEntryPath(itemsList);
        var topVisiblePath = GetTopVisibleEntryPath(itemsList, findScrollViewer());
        var verticalOffset = GetCurrentVerticalOffset(findScrollViewer);
        return new ListViewRestoreState(tab.State.Id, path, selectedPaths, focusedPath, topVisiblePath, verticalOffset, selectionUserVersion, scrollUserVersion);
    }

    public static bool CanRestore(
        ListViewRestoreState? state,
        FolderTab loadTab,
        int loadId,
        int currentLoadGeneration,
        object? selectedTab,
        bool isFileDragInProgress,
        bool isSelecting,
        bool isAutoScrolling)
    {
        return state is not null
            && loadId == currentLoadGeneration
            && ReferenceEquals(selectedTab, loadTab)
            && string.Equals(state.StateId, loadTab.State.Id, StringComparison.Ordinal)
            && string.Equals(state.Path, loadTab.State.CurrentPath, StringComparison.OrdinalIgnoreCase)
            && !SpecialLocationService.IsSpecialUri(loadTab.State.CurrentPath)
            && !loadTab.IsDisconnected
            && !isFileDragInProgress
            && !isSelecting
            && !isAutoScrolling;
    }

    public async Task<ListViewRestoreResult> RestoreAsync(
        ListViewRestoreState state,
        FolderTab loadTab,
        int loadId,
        ListView itemsList,
        IEnumerable<FileEntry> items,
        Func<ScrollViewer?> findScrollViewer,
        Func<int> getCurrentLoadGeneration,
        Func<object?> getSelectedTab,
        Func<int> getSelectionUserVersion,
        Func<int> getScrollUserVersion,
        Action updateSelectedItemStatus,
        Dispatcher dispatcher,
        CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var selectedCount = 0;
        var scrolledBy = "none";
        var restored = false;
        var selectionSkipped = false;
        var scrollSkipped = false;
        FileEntry? centerEntry = null;
        await dispatcher.InvokeAsync(() =>
        {
            if (loadId != getCurrentLoadGeneration()
                || !ReferenceEquals(getSelectedTab(), loadTab)
                || !string.Equals(state.StateId, loadTab.State.Id, StringComparison.Ordinal))
            {
                return;
            }

            IsRestoring = true;
            try
            {
                selectionSkipped = state.SelectionUserVersion != getSelectionUserVersion();
                scrollSkipped = state.ScrollUserVersion != getScrollUserVersion();
                var entriesByPath = items.ToDictionary(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase);
                if (!selectionSkipped)
                {
                    itemsList.SelectedItems.Clear();
                    foreach (var path in state.SelectedPaths.Take(MaxRestoreSelectionCount))
                    {
                        if (entriesByPath.TryGetValue(path, out var entry))
                        {
                            itemsList.SelectedItems.Add(entry);
                            selectedCount++;
                        }
                    }
                }

                if (!scrollSkipped)
                {
                    var selectedPath = state.FocusedPath ?? state.SelectedPaths.FirstOrDefault();
                    if (selectedPath is not null && entriesByPath.TryGetValue(selectedPath, out var selectedEntry))
                    {
                        itemsList.ScrollIntoView(selectedEntry);
                        centerEntry = selectedEntry;
                        if (!selectionSkipped)
                        {
                            itemsList.SelectedItem = selectedEntry;
                        }

                        scrolledBy = "centered-selection";
                    }
                    else if (state.TopVisiblePath is not null && entriesByPath.TryGetValue(state.TopVisiblePath, out var topEntry))
                    {
                        itemsList.ScrollIntoView(topEntry);
                        scrolledBy = "top-path";
                    }
                    else if (state.VerticalOffset > 0 && findScrollViewer() is { } scrollViewer)
                    {
                        scrollViewer.ScrollToVerticalOffset(state.VerticalOffset);
                        scrolledBy = "offset";
                    }
                }
            }
            finally
            {
                IsRestoring = false;
            }

            restored = selectedCount > 0 || !string.Equals(scrolledBy, "none", StringComparison.Ordinal);
            if (restored)
            {
                itemsList.Focus();
                updateSelectedItemStatus();
            }
        }, DispatcherPriority.Background).Task.WaitAsync(token);

        if (centerEntry is not null)
        {
            await CenterItemAsync(itemsList, centerEntry, findScrollViewer, dispatcher, token);
        }

        stopwatch.Stop();
        return new ListViewRestoreResult(
            restored,
            selectedCount,
            selectionSkipped,
            scrollSkipped,
            state.SelectedPaths.Count > MaxRestoreSelectionCount,
            scrolledBy,
            state.VerticalOffset,
            stopwatch.ElapsedMilliseconds);
    }

    public static async Task<bool> CenterItemAsync(
        ListView itemsList,
        object item,
        Func<ScrollViewer?> findScrollViewer,
        Dispatcher dispatcher,
        CancellationToken token = default)
    {
        itemsList.ScrollIntoView(item);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var centered = await dispatcher.InvokeAsync(() =>
            {
                if (findScrollViewer() is not { } scrollViewer)
                {
                    return false;
                }

                if (scrollViewer.CanContentScroll
                    && VirtualizingPanel.GetScrollUnit(itemsList) == ScrollUnit.Item)
                {
                    var itemIndex = itemsList.Items.IndexOf(item);
                    if (itemIndex < 0)
                    {
                        return false;
                    }

                    var targetOffset = itemIndex - Math.Max(0, scrollViewer.ViewportHeight - 1) / 2;
                    scrollViewer.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0, scrollViewer.ScrollableHeight));
                    return true;
                }

                if (itemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container)
                {
                    itemsList.ScrollIntoView(item);
                    return false;
                }

                try
                {
                    var itemTop = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
                    var itemCenter = itemTop + container.ActualHeight / 2;
                    var targetOffset = scrollViewer.VerticalOffset + itemCenter - scrollViewer.ViewportHeight / 2;
                    scrollViewer.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0, scrollViewer.ScrollableHeight));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, attempt == 0 ? DispatcherPriority.Render : DispatcherPriority.ContextIdle).Task.WaitAsync(token);

            if (centered)
            {
                return true;
            }
        }

        return false;
    }

    public void RestoreSelection(
        ListView itemsList,
        IEnumerable<FileEntry> items,
        IReadOnlyList<string> paths,
        Action updateSelectedItemStatus)
    {
        IsRestoring = true;
        try
        {
            if (paths.Count == 0)
            {
                itemsList.SelectedItems.Clear();
            }
            else
            {
                var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                itemsList.SelectedItems.Clear();
                foreach (var entry in items)
                {
                    if (pathSet.Contains(entry.FullPath))
                    {
                        itemsList.SelectedItems.Add(entry);
                    }
                }
            }
        }
        finally
        {
            IsRestoring = false;
        }

        updateSelectedItemStatus();
    }

    public static double GetCurrentVerticalOffset(Func<ScrollViewer?> findScrollViewer)
    {
        return findScrollViewer()?.VerticalOffset ?? 0;
    }

    private static string? GetFocusedEntryPath(ListView itemsList)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused is not null)
        {
            if (focused is FrameworkElement { DataContext: FileEntry entry })
            {
                return entry.FullPath;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return itemsList.SelectedItem is FileEntry selectedEntry
            ? selectedEntry.FullPath
            : null;
    }

    private static string? GetTopVisibleEntryPath(ListView itemsList, ScrollViewer? scrollViewer)
    {
        var panel = FindVisualChild<VirtualizingStackPanel>(itemsList);
        if (scrollViewer is null || panel is null || panel.Children.Count == 0)
        {
            return null;
        }

        FileEntry? topEntry = null;
        var topY = double.MaxValue;
        foreach (var child in panel.Children.OfType<ListViewItem>())
        {
            if (child.DataContext is not FileEntry entry)
            {
                continue;
            }

            try
            {
                var y = child.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
                if (y >= -1 && y < topY)
                {
                    topY = y;
                    topEntry = entry;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        return topEntry?.FullPath;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var result = FindVisualChild<T>(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}

internal sealed record ListViewRestoreState(
    string StateId,
    string Path,
    IReadOnlyList<string> SelectedPaths,
    string? FocusedPath,
    string? TopVisiblePath,
    double VerticalOffset,
    int SelectionUserVersion,
    int ScrollUserVersion);

internal sealed record ListViewRestoreResult(
    bool Restored,
    int SelectedCount,
    bool SelectionSkipped,
    bool ScrollSkipped,
    bool SelectionCapped,
    string ScrolledBy,
    double VerticalOffset,
    long ElapsedMilliseconds);
