using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow
{
    private void ApplyDevListPerfOptions()
    {
        VirtualizingPanel.SetIsVirtualizing(ItemsList, true);
        VirtualizingPanel.SetVirtualizationMode(ItemsList, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(ItemsList, _devListPerfOptions.CanContentScroll);
        VirtualizingPanel.SetScrollUnit(ItemsList, _devListPerfOptions.ScrollUnit);
        ScrollViewer.SetPanningMode(ItemsList, _devListPerfOptions.PanningMode);

        if (_devListPerfOptions.DiagnosticRowStyleEnabled)
        {
            ItemsList.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "DiagnosticListViewItemStyle");
        }

        if (_devListPerfOptions.PreviewMouseWheelEnabled || _devListPerfOptions.ScrollTraceEnabled)
        {
            ItemsList.PreviewMouseWheel += ItemsList_PreviewMouseWheelForDiagnostics;
        }

        PerfLog.WriteVerbose($"dev-list-options {_devListPerfOptions.Describe()}");
    }

    private async void ItemsList_PreviewMouseWheelForDiagnostics(object sender, MouseWheelEventArgs e)
    {
        MarkUserScrollIntentDuringLoad("wheel");
        if (_selectionInteraction.IsSelecting)
        {
            e.Handled = true;
            return;
        }

        var scrollViewer = FindItemsScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        var traceId = Interlocked.Increment(ref _scrollTraceCount);
        var stopwatch = Stopwatch.StartNew();
        var beforeOffset = scrollViewer.VerticalOffset;
        var beforeRealizedChildren = GetRealizedChildCount();
        var handledByDiagnostics = false;
        var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        var direction = e.Delta > 0 ? -1 : 1;

        if (_devListPerfOptions.PreviewMouseWheelEnabled)
        {
            handledByDiagnostics = true;
            e.Handled = true;

            if (!_devListPerfOptions.CanContentScroll || _devListPerfOptions.ScrollUnit == ScrollUnit.Pixel)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + direction * _devListPerfOptions.MouseWheelPixels * notches);
            }
            else
            {
                var lines = _devListPerfOptions.MouseWheelLines * notches;
                for (var i = 0; i < lines; i++)
                {
                    if (direction < 0)
                    {
                        scrollViewer.LineUp();
                    }
                    else
                    {
                        scrollViewer.LineDown();
                    }
                }
            }
        }

        if (!_devListPerfOptions.ScrollTraceEnabled)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        stopwatch.Stop();

        _performanceLogger.Write(
            $"scroll-wheel id={traceId} delta={e.Delta} notches={notches} handled={handledByDiagnostics} beforeOffset={beforeOffset:N1} afterOffset={scrollViewer.VerticalOffset:N1} offsetDelta={scrollViewer.VerticalOffset - beforeOffset:N1} elapsedMs={stopwatch.ElapsedMilliseconds} beforeRealizedChildren={beforeRealizedChildren} afterRealizedChildren={GetRealizedChildCount()} viewportHeight={scrollViewer.ViewportHeight:N1} extentHeight={scrollViewer.ExtentHeight:N1} scrollableHeight={scrollViewer.ScrollableHeight:N1}");
    }

    private async Task RunAutoPerfChecksAsync(int loadId)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

        var scrollViewer = FindItemsScrollViewer();
        if (scrollViewer is not null)
        {
            var scrollStopwatch = Stopwatch.StartNew();
            scrollViewer.ScrollToEnd();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            scrollViewer.ScrollToHome();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            scrollStopwatch.Stop();
            _performanceLogger.Write($"scroll-check id={loadId} elapsedMs={scrollStopwatch.ElapsedMilliseconds} verticalOffset={scrollViewer.VerticalOffset:N0}");
        }
        else
        {
            _performanceLogger.Write($"scroll-check id={loadId} skipped=no-scrollviewer");
        }

        await MeasureFilterInputAsync("file_09999");
        await MeasureFilterInputAsync("");

        var sortStopwatch = Stopwatch.StartNew();
        if (ItemsView is ListCollectionView listView)
        {
            listView.CustomSort = new FileEntryComparer("Name", true, _settingsService.Settings.SortFoldersFirst);
        }
        else
        {
            ItemsView.SortDescriptions.Add(new SortDescription(nameof(FileEntry.Name), ListSortDirection.Ascending));
        }

        RefreshItemsView("auto-perf-sort-check");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (ItemsView is ListCollectionView restoredListView)
        {
            var restoreTab = ActiveTab;
            restoredListView.CustomSort = new FileEntryComparer(
                restoreTab?.State.SortColumn ?? "Name",
                restoreTab?.State.SortAscending ?? true,
                _settingsService.Settings.SortFoldersFirst);
        }
        else
        {
            ItemsView.SortDescriptions.Clear();
        }

        RefreshItemsView("auto-perf-sort-restore");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        sortStopwatch.Stop();
        _performanceLogger.Write($"sort-check id={loadId} count={_items.Count} elapsedMs={sortStopwatch.ElapsedMilliseconds}");

        if (string.Equals(Environment.GetEnvironmentVariable("FILEKAKARI_PERF_EXIT"), "1", StringComparison.Ordinal))
        {
            Close();
        }
    }

    private async Task MeasureFilterInputAsync(string filter)
    {
        var stopwatch = Stopwatch.StartNew();
        FilterBox.Text = filter;
        await Task.Delay(180);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        stopwatch.Stop();
        _performanceLogger.Write($"filter-input count={_items.Count} textLength={filter.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    private string GetVirtualizationStatus()
    {
        var panel = FindVisualChild<VirtualizingStackPanel>(ItemsList);
        var scrollViewer = FindItemsScrollViewer();
        var scrollViewerStatus = scrollViewer is null
            ? "scrollViewer=none"
            : $"scrollViewer={scrollViewer.GetType().Name},viewerCanContentScroll={scrollViewer.CanContentScroll},panningMode={scrollViewer.PanningMode},verticalOffset={scrollViewer.VerticalOffset:N1},scrollableHeight={scrollViewer.ScrollableHeight:N1}";

        return panel is null
            ? $"unknown,isVirtualizing={VirtualizingPanel.GetIsVirtualizing(ItemsList)},mode={VirtualizingPanel.GetVirtualizationMode(ItemsList)},canContentScroll={ScrollViewer.GetCanContentScroll(ItemsList)},scrollUnit={VirtualizingPanel.GetScrollUnit(ItemsList)},{scrollViewerStatus}"
            : $"panel={panel.GetType().Name},isVirtualizing={VirtualizingPanel.GetIsVirtualizing(ItemsList)},mode={VirtualizingPanel.GetVirtualizationMode(ItemsList)},canContentScroll={ScrollViewer.GetCanContentScroll(ItemsList)},scrollUnit={VirtualizingPanel.GetScrollUnit(ItemsList)},realizedChildren={panel.Children.Count},{scrollViewerStatus}";
    }

    private static string GetProcessMemoryStatus()
    {
        using var process = Process.GetCurrentProcess();
        return $"workingSetMb={process.WorkingSet64 / 1024d / 1024d:N1},privateMb={process.PrivateMemorySize64 / 1024d / 1024d:N1}";
    }

    private static long GetProcessWorkingSetBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    private void LogMemoryMetrics(string trigger)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / 1024d / 1024d;
            var privateMb = process.PrivateMemorySize64 / 1024d / 1024d;
            var gcMb = GC.GetTotalMemory(false) / 1024d / 1024d;
            var handles = process.HandleCount;

            PerfLog.WriteVerbose($"memory-metrics trigger={trigger} workingSetMb={workingSetMb:N1} privateMb={privateMb:N1} gcMb={gcMb:N1} handles={handles}");
        }
        catch
        {
            // Ignore any exceptions to protect application stability
        }
    }
}
