using System.Windows.Controls;

namespace FileKakari;

internal static class FileListSelectionHelper
{
    public static void ApplySingleSelection(ListView listView, FileEntry entry)
    {
        using var batch = SelectionUpdateBatch.Begin(listView);
        listView.SelectedItems.Clear();
        listView.SelectedItems.Add(entry);
        listView.SelectedItem = entry;
    }

    public static void ApplyControlSelection(ListView listView, FileEntry entry)
    {
        if (listView.SelectedItems.Contains(entry))
        {
            listView.SelectedItems.Remove(entry);
        }
        else
        {
            listView.SelectedItems.Add(entry);
        }
    }

    public static FileEntry? PerformShiftSelection(ListView listView, FileEntry? anchor, FileEntry target, bool additive)
    {
        if (listView.Items.Count == 0)
        {
            return anchor;
        }

        var targetIndex = listView.Items.IndexOf(target);
        if (targetIndex < 0)
        {
            AddTargetFallback(listView, target, additive);
            return target;
        }

        var anchorEntry = anchor;
        var anchorIndex = anchorEntry is not null ? listView.Items.IndexOf(anchorEntry) : -1;
        if (anchorIndex < 0)
        {
            anchorEntry = listView.SelectedItem as FileEntry
                ?? listView.SelectedItems.OfType<FileEntry>().FirstOrDefault();
            anchorIndex = anchorEntry is not null ? listView.Items.IndexOf(anchorEntry) : -1;
        }

        if (anchorIndex < 0 || anchorEntry is null)
        {
            AddTargetFallback(listView, target, additive);
            return target;
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);

        var targetSelection = additive
            ? new HashSet<FileEntry>(listView.SelectedItems.OfType<FileEntry>())
            : new HashSet<FileEntry>();

        for (var i = start; i <= end; i++)
        {
            if (listView.Items[i] is FileEntry item)
            {
                targetSelection.Add(item);
            }
        }

        FileListRangeSelectionHelper.ApplySelection(listView, targetSelection);
        return anchorEntry;
    }

    public static bool PrepareRightClickSelection(ListView listView, FileEntry? clickedEntry)
    {
        if (clickedEntry is null)
        {
            return false;
        }

        if (!listView.SelectedItems.Contains(clickedEntry))
        {
            ApplySingleSelection(listView, clickedEntry);
            return true;
        }

        return false;
    }

    private static void AddTargetFallback(ListView listView, FileEntry target, bool additive)
    {
        using var batch = SelectionUpdateBatch.Begin(listView);
        if (!additive)
        {
            listView.SelectedItems.Clear();
        }

        if (!listView.SelectedItems.Contains(target))
        {
            listView.SelectedItems.Add(target);
        }

        if (!additive || listView.SelectedItem is null)
        {
            listView.SelectedItem = target;
        }
    }
}
