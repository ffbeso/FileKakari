using System.Windows.Controls;

namespace FileKakari;

internal static class FileListSelectionHelper
{
    public static void ApplySingleSelection(ListView listView, FileEntry entry)
    {
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

    public static void PerformShiftSelection(ListView listView, FileEntry? anchor, FileEntry target, bool additive)
    {
        if (listView.Items.Count == 0)
        {
            return;
        }

        var anchorEntry = anchor ?? listView.SelectedItem as FileEntry;
        if (anchorEntry is null)
        {
            AddTargetFallback(listView, target, additive);
            return;
        }

        var anchorIndex = listView.Items.IndexOf(anchorEntry);
        var targetIndex = listView.Items.IndexOf(target);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            AddTargetFallback(listView, target, additive);
            return;
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);

        if (!additive)
        {
            listView.SelectedItems.Clear();
        }

        for (var i = start; i <= end; i++)
        {
            if (listView.Items[i] is FileEntry item
                && !listView.SelectedItems.Contains(item))
            {
                listView.SelectedItems.Add(item);
            }
        }
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
        if (!additive)
        {
            listView.SelectedItems.Clear();
        }

        if (!listView.SelectedItems.Contains(target))
        {
            listView.SelectedItems.Add(target);
        }
    }
}
