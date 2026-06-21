using System.Windows;
using System.Windows.Controls;

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
        foreach (var entry in listView.Items.OfType<FileEntry>())
        {
            if (listView.ItemContainerGenerator.ContainerFromItem(entry) is not ListViewItem item)
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
        for (var i = selectedItems.Count - 1; i >= 0; i--)
        {
            if (selectedItems[i] is FileEntry entry && !targetSelection.Contains(entry))
            {
                selectedItems.RemoveAt(i);
            }
        }

        foreach (var entry in targetSelection)
        {
            if (!selectedItems.Contains(entry))
            {
                selectedItems.Add(entry);
            }
        }
    }
}
