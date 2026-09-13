using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileKakari;

public sealed record AnchorScrollState(
    string AnchorPath,
    double RelativeOffset,
    string? NextPath,
    string? PreviousPath,
    double FallbackVerticalOffset
);

public sealed record DeleteScrollRestoreState(
    long FlowId,
    string PaneId,
    string StateId,
    string Path,
    AnchorScrollState AnchorState,
    DateTimeOffset CapturedAt
);

internal static class AnchorScrollService
{
    public static AnchorScrollState? CaptureAnchorState(ListView? listView, GroupMode mode, double currentVerticalOffset)
    {
        if (listView == null || mode == GroupMode.None || listView.Items.Count == 0)
        {
            return null;
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(listView);
        if (scrollViewer == null)
        {
            return null;
        }

        FileEntry? anchorEntry = null;
        double anchorRelativeOffset = 0;
        int anchorIndex = -1;

        // GroupHeader ではなく必ず実 FileEntry の ListViewItem だけを探索
        for (int i = 0; i < listView.Items.Count; i++)
        {
            if (listView.Items[i] is FileEntry entry &&
                listView.ItemContainerGenerator.ContainerFromItem(entry) is ListViewItem container)
            {
                try
                {
                    var transform = container.TransformToAncestor(scrollViewer);
                    var containerTop = transform.Transform(new Point(0, 0)).Y;
                    var containerBottom = containerTop + container.ActualHeight;

                    if (containerBottom > 0)
                    {
                        anchorEntry = entry;
                        anchorRelativeOffset = containerTop;
                        anchorIndex = i;
                        break;
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        if (anchorEntry == null)
        {
            return null;
        }

        string? nextPath = null;
        if (anchorIndex + 1 < listView.Items.Count && listView.Items[anchorIndex + 1] is FileEntry nextEntry)
        {
            nextPath = nextEntry.FullPath;
        }

        string? previousPath = null;
        if (anchorIndex - 1 >= 0 && listView.Items[anchorIndex - 1] is FileEntry prevEntry)
        {
            previousPath = prevEntry.FullPath;
        }

        return new AnchorScrollState(
            anchorEntry.FullPath,
            anchorRelativeOffset,
            nextPath,
            previousPath,
            currentVerticalOffset
        );
    }

    public static async Task<bool> RestoreAnchorScrollAsync(
        ListView? listView,
        AnchorScrollState? anchorState,
        GroupMode mode,
        Dispatcher dispatcher,
        long deleteFlowId = 0)
    {
        if (listView == null || anchorState == null || mode == GroupMode.None || listView.Items.Count == 0)
        {
            return false;
        }

        // Filter / Group / Sort 適用後の表示対象アイテム (listView.Items) のみからターゲットを探索
        var visibleEntries = listView.Items.OfType<FileEntry>().ToList();
        if (visibleEntries.Count == 0)
        {
            return false;
        }

        FileEntry? targetEntry = null;
        string resolveReason = "";

        // 1. AnchorPath (確信)
        targetEntry = visibleEntries.FirstOrDefault(e => string.Equals(e.FullPath, anchorState.AnchorPath, StringComparison.OrdinalIgnoreCase));
        if (targetEntry != null)
        {
            resolveReason = "exact-anchor";
        }
        else
        {
            // 2. PreviousPath (削除前の表示順で直前の表示対象)
            if (!string.IsNullOrEmpty(anchorState.PreviousPath))
            {
                targetEntry = visibleEntries.FirstOrDefault(e => string.Equals(e.FullPath, anchorState.PreviousPath, StringComparison.OrdinalIgnoreCase));
                if (targetEntry != null)
                {
                    resolveReason = "previous-fallback";
                }
            }

            // 3. NextPath (削除前の表示順で直後の表示対象)
            if (targetEntry == null && !string.IsNullOrEmpty(anchorState.NextPath))
            {
                targetEntry = visibleEntries.FirstOrDefault(e => string.Equals(e.FullPath, anchorState.NextPath, StringComparison.OrdinalIgnoreCase));
                if (targetEntry != null)
                {
                    resolveReason = "next-fallback";
                }
            }
        }

        // Anchor / Next / Previous 3件すべて不在の場合 ➔ 既存 VerticalOffset fallback
        if (targetEntry == null)
        {
            return false;
        }

        await WaitForStableExtentAsync(listView, dispatcher, deleteFlowId, "before-scroll-into-view");

        // Step 1: ScrollIntoView
        await dispatcher.InvokeAsync(() =>
        {
            listView.ScrollIntoView(targetEntry);
        }, DispatcherPriority.Send);

        // ScrollIntoView と相対Y補正のどちらでも仮想化コンテナの再配置が起こり得る。
        // Extent・VerticalOffset・相対Yが同時に安定するまで、低優先度のレイアウト周期ごとに再補正する。
        return await RestoreAnchorRelativePositionUntilStableAsync(
            listView,
            targetEntry,
            anchorState,
            dispatcher,
            deleteFlowId,
            resolveReason);
    }

    private static async Task WaitForStableExtentAsync(
        ListView listView,
        Dispatcher dispatcher,
        long deleteFlowId,
        string stage)
    {
        const int requiredStableTransitions = 2;
        const int maxLayoutPasses = 10;
        const double tolerance = 0.5;

        double? previousScrollableHeight = null;
        var stableTransitions = 0;
        var samples = new List<double>(maxLayoutPasses);

        for (var pass = 0; pass < maxLayoutPasses; pass++)
        {
            var sample = await dispatcher.InvokeAsync(() =>
            {
                listView.UpdateLayout();
                return FindVisualChild<ScrollViewer>(listView)?.ScrollableHeight ?? -1;
            }, DispatcherPriority.ContextIdle);

            samples.Add(sample);
            if (sample >= 0
                && previousScrollableHeight is { } previous
                && Math.Abs(sample - previous) <= tolerance)
            {
                stableTransitions++;
                if (stableTransitions >= requiredStableTransitions)
                {
                    break;
                }
            }
            else
            {
                stableTransitions = 0;
            }

            previousScrollableHeight = sample;
        }

        PerfLog.WriteVerbose(
            $"anchor-extent-stability flowId={deleteFlowId} stage={stage} stable={stableTransitions >= requiredStableTransitions} " +
            $"stableTransitions={stableTransitions} samples=[{string.Join(',', samples.Select(value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]");
    }

    private static async Task<bool> RestoreAnchorRelativePositionUntilStableAsync(
        ListView listView,
        FileEntry targetEntry,
        AnchorScrollState anchorState,
        Dispatcher dispatcher,
        long deleteFlowId,
        string resolveReason)
    {
        const int requiredStableTransitions = 2;
        const int maxLayoutPasses = 12;
        const double metricTolerance = 0.5;
        const double positionTolerance = 0.75;

        double? previousScrollableHeight = null;
        double? previousVerticalOffset = null;
        var stableTransitions = 0;
        var lastPositionMatched = false;
        var samples = new List<string>(maxLayoutPasses);

        for (var pass = 0; pass < maxLayoutPasses; pass++)
        {
            var sample = await dispatcher.InvokeAsync(() =>
            {
                listView.UpdateLayout();
                var scrollViewer = FindVisualChild<ScrollViewer>(listView);
                if (scrollViewer == null)
                {
                    return (Valid: false, ScrollableHeight: -1d, VerticalOffset: -1d, RelativeY: double.NaN);
                }

                var container = listView.ItemContainerGenerator.ContainerFromItem(targetEntry) as ListViewItem;
                if (container == null)
                {
                    listView.ScrollIntoView(targetEntry);
                    listView.UpdateLayout();
                    container = listView.ItemContainerGenerator.ContainerFromItem(targetEntry) as ListViewItem;
                }

                if (container == null)
                {
                    return (Valid: false, scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset, RelativeY: double.NaN);
                }

                try
                {
                    var relativeY = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
                    var diff = relativeY - anchorState.RelativeOffset;
                    if (Math.Abs(diff) > positionTolerance)
                    {
                        scrollViewer.ScrollToVerticalOffset(Math.Clamp(scrollViewer.VerticalOffset + diff, 0, scrollViewer.ScrollableHeight));
                        listView.UpdateLayout();
                    }

                    var adjustedRelativeY = listView.ItemContainerGenerator.ContainerFromItem(targetEntry) is ListViewItem adjustedContainer
                        ? adjustedContainer.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y
                        : double.NaN;
                    return (Valid: !double.IsNaN(adjustedRelativeY), scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset, RelativeY: adjustedRelativeY);
                }
                catch (InvalidOperationException)
                {
                    return (Valid: false, scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset, RelativeY: double.NaN);
                }
            }, DispatcherPriority.ContextIdle);

            samples.Add(
                $"{sample.ScrollableHeight.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}/" +
                $"{sample.VerticalOffset.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}/" +
                $"{sample.RelativeY.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");

            var positionMatched = sample.Valid
                && Math.Abs(sample.RelativeY - anchorState.RelativeOffset) <= positionTolerance;
            var metricsStable = previousScrollableHeight is { } previousHeight
                && previousVerticalOffset is { } previousOffset
                && Math.Abs(sample.ScrollableHeight - previousHeight) <= metricTolerance
                && Math.Abs(sample.VerticalOffset - previousOffset) <= metricTolerance;
            lastPositionMatched = positionMatched;

            if (positionMatched && metricsStable)
            {
                stableTransitions++;
                if (stableTransitions >= requiredStableTransitions)
                {
                    break;
                }
            }
            else
            {
                stableTransitions = 0;
            }

            previousScrollableHeight = sample.ScrollableHeight;
            previousVerticalOffset = sample.VerticalOffset;
        }

        PerfLog.WriteVerbose(
            $"anchor-restore-log flowId={deleteFlowId} stage=converged reason={resolveReason} stable={stableTransitions >= requiredStableTransitions} " +
            $"AnchorPath=\"{anchorState.AnchorPath}\" TargetPath=\"{targetEntry.FullPath}\" AnchorRelOffset={anchorState.RelativeOffset:F1}px " +
            $"samples=[{string.Join(',', samples)}] GroupCount={listView.Items.Groups?.Count ?? 0}");

        return lastPositionMatched;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var childOfChild = FindVisualChild<T>(child);
            if (childOfChild != null)
            {
                return childOfChild;
            }
        }
        return null;
    }
}
