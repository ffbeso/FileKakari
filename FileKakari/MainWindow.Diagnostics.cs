using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
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

    public void PrototypeApplyGroupMode(ListView listView, GroupMode mode)
    {
        if (listView.ItemsSource is not System.Collections.IEnumerable itemsSource)
        {
            return;
        }

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(itemsSource) as ListCollectionView;
        if (view is null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var memBefore = GC.GetTotalMemory(false);
        var beforeRealized = GetRealizedChildCount(listView);

        view.GroupDescriptions.Clear();
        if (mode != GroupMode.None)
        {
            view.GroupDescriptions.Add(new FileEntryGroupDescription(mode));
        }

        stopwatch.Stop();
        var memAfter = GC.GetTotalMemory(false);
        var afterRealized = GetRealizedChildCount(listView);

        var isGroupVirt = VirtualizingPanel.GetIsVirtualizingWhenGrouping(listView);
        var isVirt = VirtualizingPanel.GetIsVirtualizing(listView);
        var virtMode = VirtualizingPanel.GetVirtualizationMode(listView);
        var scrollUnit = VirtualizingPanel.GetScrollUnit(listView);
        using var process = Process.GetCurrentProcess();

        PerfLog.Write($"group-prototype mode={mode} count={view.Count} elapsedMs={stopwatch.ElapsedMilliseconds} isVirt={isVirt} isGroupVirt={isGroupVirt} virtMode={virtMode} scrollUnit={scrollUnit} beforeRealized={beforeRealized} afterRealized={afterRealized} memDeltaKb={(memAfter - memBefore) / 1024} workingSetMb={process.WorkingSet64 / 1024d / 1024d:N1} privateMb={process.PrivateMemorySize64 / 1024d / 1024d:N1}");
    }

    private int GetRealizedChildCount(ListView listView)
    {
        var panel = FindVisualChild<VirtualizingStackPanel>(listView);
        return panel?.Children.Count ?? 0;
    }

    private string GetVirtualizationStatus()
    {
        var panel = FindVisualChild<VirtualizingStackPanel>(ItemsList);
        var scrollViewer = FindItemsScrollViewer();
        var scrollViewerStatus = scrollViewer is null
            ? "scrollViewer=none"
            : $"scrollViewer={scrollViewer.GetType().Name},viewerCanContentScroll={scrollViewer.CanContentScroll},panningMode={scrollViewer.PanningMode},verticalOffset={scrollViewer.VerticalOffset:N1},scrollableHeight={scrollViewer.ScrollableHeight:N1}";

        return panel is null
            ? $"unknown,isVirt={VirtualizingPanel.GetIsVirtualizing(ItemsList)},isGroupVirt={VirtualizingPanel.GetIsVirtualizingWhenGrouping(ItemsList)},mode={VirtualizingPanel.GetVirtualizationMode(ItemsList)},canContentScroll={ScrollViewer.GetCanContentScroll(ItemsList)},scrollUnit={VirtualizingPanel.GetScrollUnit(ItemsList)},{scrollViewerStatus}"
            : $"panel={panel.GetType().Name},isVirt={VirtualizingPanel.GetIsVirtualizing(ItemsList)},isGroupVirt={VirtualizingPanel.GetIsVirtualizingWhenGrouping(ItemsList)},mode={VirtualizingPanel.GetVirtualizationMode(ItemsList)},canContentScroll={ScrollViewer.GetCanContentScroll(ItemsList)},scrollUnit={VirtualizingPanel.GetScrollUnit(ItemsList)},realizedChildren={panel.Children.Count},{scrollViewerStatus}";
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

    public void LogGroupDiagnostics(ListView listView, string trigger)
    {
        try
        {
            if (listView.ItemsSource is not System.Collections.IEnumerable itemsSource)
            {
                PerfLog.Write($"[GroupDiag] trigger={trigger} itemsSource=null");
                return;
            }

            var view = CollectionViewSource.GetDefaultView(itemsSource) as ListCollectionView;
            if (view is null)
            {
                PerfLog.Write($"[GroupDiag] trigger={trigger} view=null");
                return;
            }

            var groupCount = view.Groups?.Count ?? 0;
            var groupDescCount = view.GroupDescriptions.Count;
            var hasCustomSort = view.CustomSort != null;
            var hasFilter = view.Filter != null;

            PerfLog.Write($"[GroupDiag] trigger={trigger} itemsSourceCount={listView.Items.Count} viewCount={view.Count} groupsCount={groupCount} groupDescCount={groupDescCount} hasCustomSort={hasCustomSort} hasFilter={hasFilter}");

            if (view.Groups != null)
            {
                int groupIdx = 0;
                foreach (var g in view.Groups.OfType<CollectionViewGroup>())
                {
                    PerfLog.Write($"[GroupDiag-Group] idx={groupIdx} groupName=\"{g.Name}\" itemsCount={g.Items.Count}");
                    for (int i = 0; i < Math.Min(3, g.Items.Count); i++)
                    {
                        var item = g.Items[i];
                        if (item is FileEntry fe)
                        {
                            PerfLog.Write($"[GroupDiag-GroupItem] group=\"{g.Name}\" idx={i} type={item.GetType().Name} name=\"{fe.Name}\" path=\"{fe.FullPath}\"");
                        }
                        else
                        {
                            PerfLog.Write($"[GroupDiag-GroupItem] group=\"{g.Name}\" idx={i} type={item?.GetType().FullName ?? "null"} content=\"{item}\"");
                        }
                    }
                    groupIdx++;
                }
            }

            // Realized ListViewItem container check
            var panel = FindVisualChild<VirtualizingStackPanel>(listView);
            var gridView = listView.View as GridView;
            int realizedCount = 0;
            int blankContainerCount = 0;

            if (panel != null)
            {
                for (int i = 0; i < panel.Children.Count; i++)
                {
                    var child = panel.Children[i];
                    if (child is ListViewItem lvi)
                    {
                        realizedCount++;
                        var dc = lvi.DataContext;
                        var dcType = dc?.GetType().FullName ?? "null";
                        var isFileEntry = dc is FileEntry;
                        var name = (dc as FileEntry)?.Name ?? "";
                        var path = (dc as FileEntry)?.FullPath ?? "";
                        var isSelected = lvi.IsSelected;
                        var vis = lvi.Visibility;
                        var content = lvi.Content?.GetType().FullName ?? "null";

                        if (isFileEntry && string.IsNullOrEmpty(name))
                        {
                            blankContainerCount++;
                        }

                        PerfLog.Write($"[GroupDiag-Container] lviIdx={i} dcType={dcType} isFileEntry={isFileEntry} name=\"{name}\" path=\"{path}\" content={content} isSelected={isSelected} vis={vis}");
                    }
                }
            }

            // GridViewColumns check
            if (gridView != null)
            {
                int colIdx = 0;
                foreach (var col in gridView.Columns)
                {
                    var binding = (col.DisplayMemberBinding as Binding)?.Path?.Path ?? "none";
                    var header = col.Header?.ToString() ?? "none";
                    PerfLog.Write($"[GroupDiag-Column] colIdx={colIdx} header=\"{header}\" binding=\"{binding}\" template={(col.CellTemplate != null)}");
                    colIdx++;
                }
            }
        }
        catch (Exception ex)
        {
            PerfLog.Write($"[GroupDiag-Error] trigger={trigger} ex={ex.Message}");
        }
    }

    public void LogSortGroupStatus(string trigger, WorkspaceTabState targetState)
    {
        try
        {
            if (ItemsView is not ListCollectionView view)
            {
                return;
            }

            var groupNames = view.Groups == null
                ? "none"
                : string.Join(" | ", view.Groups.OfType<CollectionViewGroup>().Select(g => g.Name));

            PerfLog.Write($"[SortGroupStatus] trigger={trigger} groupMode={targetState.GroupMode} sortCol={targetState.SortColumn} asc={targetState.SortAscending} groupCount={view.Groups?.Count ?? 0} groupOrder=\"{groupNames}\"");
        }
        catch (Exception ex)
        {
            PerfLog.Write($"[SortGroupStatus-Error] trigger={trigger} ex={ex.Message}");
        }
    }

    public void LogVisualTreeDetails(ListView listView, string trigger)
    {
        try
        {
            if (listView.ItemsSource is not System.Collections.IEnumerable itemsSource)
            {
                return;
            }

            var panel = FindVisualChild<VirtualizingStackPanel>(listView);
            if (panel == null)
            {
                PerfLog.Write($"[VisualDiag] trigger={trigger} panel=null");
                return;
            }

            PerfLog.Write($"[VisualDiag] trigger={trigger} childrenCount={panel.Children.Count}");

            for (int i = 0; i < panel.Children.Count; i++)
            {
                var child = panel.Children[i];
                if (child is ListViewItem lvi)
                {
                    var dc = lvi.DataContext;
                    var dcType = dc?.GetType().FullName ?? "null";
                    var name = (dc as FileEntry)?.Name ?? "";
                    var path = (dc as FileEntry)?.FullPath ?? "";

                    PerfLog.Write($"[VisualDiag-LVI] idx={i} dcType={dcType} name=\"{name}\" width={lvi.ActualWidth:N1} height={lvi.ActualHeight:N1} vis={lvi.Visibility}");

                    // Explore Visual Children inside ListViewItem
                    InspectVisualElement(lvi, i, 0);
                }
            }
        }
        catch (Exception ex)
        {
            PerfLog.Write($"[VisualDiag-Error] trigger={trigger} ex={ex.Message}");
        }
    }

    private void InspectVisualElement(DependencyObject parent, int lviIdx, int depth)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            var typeName = child.GetType().FullName ?? "null";

            if (child is FrameworkElement fe)
            {
                var dcType = fe.DataContext?.GetType().FullName ?? "null";
                string extraInfo = "";

                if (child is TextBlock tb)
                {
                    extraInfo = $"text=\"{tb.Text}\"";
                }
                else if (child is ContentPresenter cp)
                {
                    extraInfo = $"content={cp.Content?.GetType().FullName ?? "null"}";
                }

                if (child is TextBlock || child is ContentPresenter || typeName.Contains("GridView") || depth <= 3)
                {
                    PerfLog.Write($"[VisualDiag-Child] lvi={lviIdx} depth={depth} type={child.GetType().Name} name=\"{fe.Name}\" dcType={dcType} w={fe.ActualWidth:N1} h={fe.ActualHeight:N1} vis={fe.Visibility} {extraInfo}");
                }
            }

            if (depth < 6)
            {
                InspectVisualElement(child, lviIdx, depth + 1);
            }
        }
    }

    public static void VerifyFileNameNormalizerCases()
    {
        var cases = new (string Input, bool IsDir, string ExpectedGroup)[]
        {
            ("水曜日のダウンタウン 2026-08-01.ts", false, "水曜日のダウンタウン"),
            ("水曜日のダウンタウン 2026-08-08.ts", false, "水曜日のダウンタウン"),
            ("相棒 season24 第12話.ts", false, "相棒 season24"),
            ("相棒 season24 第13話.ts", false, "相棒 season24"),
            ("NEWS23 20260808.ts", false, "NEWS23"),
            ("NHKニュース7 2026-08-08.ts", false, "NHKニュース7"),
            ("season24 第12話.ts", false, "season24"),
            ("100分de名著 202608.ts", false, "100分de名著"),
            ("20260808.mp4", false, "20260808"),
            ("[字].mp4", false, "[字]"),
            ("鬼滅の刃 「刀鍛冶の里」.mp4", false, "鬼滅の刃 「刀鍛冶の里」"),
            ("鬼滅の刃 「遊郭編」.mp4", false, "鬼滅の刃 「遊郭編」"),
            ("保存フォルダ", true, "フォルダ"),
        };

        foreach (var c in cases)
        {
            var result = FileNameNormalizer.Normalize(c.Input, c.IsDir);
            PerfLog.Write($"[NormalizerVerify] input=\"{c.Input}\" result=\"{result}\" expected=\"{c.ExpectedGroup}\" pass={(result == c.ExpectedGroup)}");
        }
    }
}
