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
        Dispatcher dispatcher)
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
            // 2. NextPath (直後の表示対象)
            if (!string.IsNullOrEmpty(anchorState.NextPath))
            {
                targetEntry = visibleEntries.FirstOrDefault(e => string.Equals(e.FullPath, anchorState.NextPath, StringComparison.OrdinalIgnoreCase));
                if (targetEntry != null)
                {
                    resolveReason = "next-fallback";
                }
            }

            // 3. PreviousPath (直前の表示対象)
            if (targetEntry == null && !string.IsNullOrEmpty(anchorState.PreviousPath))
            {
                targetEntry = visibleEntries.FirstOrDefault(e => string.Equals(e.FullPath, anchorState.PreviousPath, StringComparison.OrdinalIgnoreCase));
                if (targetEntry != null)
                {
                    resolveReason = "prev-fallback";
                }
            }
        }

        // Anchor / Next / Previous 3件すべて不在の場合 ➔ 既存 VerticalOffset fallback
        if (targetEntry == null)
        {
            return false;
        }

        // Step 1: ScrollIntoView
        await dispatcher.InvokeAsync(() =>
        {
            listView.ScrollIntoView(targetEntry);
        }, DispatcherPriority.Send);

        // Step 2: Render 優先度でレイアウト確定を待ち、RelativeOffset 補正を実行
        await dispatcher.InvokeAsync(() =>
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(listView);
            if (scrollViewer == null) return;

            if (listView.ItemContainerGenerator.ContainerFromItem(targetEntry) is ListViewItem container)
            {
                try
                {
                    var transform = container.TransformToAncestor(scrollViewer);
                    var containerTop = transform.Transform(new Point(0, 0)).Y;
                    var diff = containerTop - anchorState.RelativeOffset;
                    scrollViewer.ScrollToVerticalOffset(Math.Clamp(scrollViewer.VerticalOffset + diff, 0, scrollViewer.ScrollableHeight));

                    double newRelativeOffset = 0;
                    if (listView.ItemContainerGenerator.ContainerFromItem(targetEntry) is ListViewItem newContainer)
                    {
                        newRelativeOffset = newContainer.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
                    }

                    PerfLog.WriteVerbose($"anchor-restore-log reason={resolveReason} AnchorPath=\"{anchorState.AnchorPath}\" TargetPath=\"{targetEntry.FullPath}\" AnchorRelOffset={anchorState.RelativeOffset:F1}px NewRelOffset={newRelativeOffset:F1}px VerticalOffset={scrollViewer.VerticalOffset:F1}px GroupCount={listView.Items.Groups?.Count ?? 0}");
                }
                catch (InvalidOperationException)
                {
                }
            }
        }, DispatcherPriority.Render);

        return true;
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
