using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileKakari;

internal static class FileListRangeSelectionHelper
{
    public static Rect CreateSelectionRect(Point startPoint, Point currentPoint)
    {
        var left = Math.Min(startPoint.X, currentPoint.X);
        var top = Math.Min(startPoint.Y, currentPoint.Y);
        var right = Math.Max(startPoint.X, currentPoint.X);
        var bottom = Math.Max(startPoint.Y, currentPoint.Y);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public static void SelectItemsInRange(
        ListView listView,
        Rect selectionRect,
        bool additive,
        IReadOnlySet<FileEntry> baseSelection)
    {
        var targetSelection = new HashSet<FileEntry>(additive
            ? baseSelection
            : Enumerable.Empty<FileEntry>());
        // Walk realized containers, including grouped rows, instead of looking up
        // a container for every file on every mouse move.
        foreach (var item in EnumerateRealizedRows(listView))
        {
            if (item.Content is not FileEntry entry
                || ItemsControl.ItemsControlFromItemContainer(item) != listView
                || !item.IsVisible)
            {
                continue;
            }

            var itemTopLeft = item.TranslatePoint(new Point(0, 0), listView);
            var itemRect = new Rect(itemTopLeft, new Size(item.ActualWidth, item.ActualHeight));
            if (selectionRect.IntersectsWith(itemRect))
            {
                targetSelection.Add(entry);
            }
        }

        ApplySelection(listView, targetSelection);
    }

    public static void ApplySelection(ListView listView, IReadOnlySet<FileEntry> targetSelection)
    {
        var selectedItems = listView.SelectedItems;
        var retained = new HashSet<FileEntry>();
        for (var i = selectedItems.Count - 1; i >= 0; i--)
        {
            if (selectedItems[i] is not FileEntry entry)
            {
                continue;
            }

            if (!targetSelection.Contains(entry))
            {
                selectedItems.RemoveAt(i);
            }
            else
            {
                retained.Add(entry);
            }
        }

        foreach (var entry in targetSelection)
        {
            if (retained.Add(entry))
            {
                selectedItems.Add(entry);
            }
        }
    }

    private static IEnumerable<ListViewItem> EnumerateRealizedRows(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListViewItem row)
            {
                yield return row;
                continue;
            }

            foreach (var descendant in EnumerateRealizedRows(child))
            {
                yield return descendant;
            }
        }
    }
}
