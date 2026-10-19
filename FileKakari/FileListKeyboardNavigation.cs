using System.Windows.Controls;

namespace FileKakari;

public static class FileListKeyboardNavigation
{
    // The result means the key was handled, not that the selection changed.
    public static bool MoveSelection(ListView listView, int delta)
    {
        if (delta == 0) return false;
        if (listView.Items.Count == 0) return true;

        var currentIndex = listView.SelectedIndex;
        if (currentIndex < 0)
            currentIndex = delta > 0 ? -1 : listView.Items.Count;

        var targetIndex = Math.Clamp(currentIndex + delta, 0, listView.Items.Count - 1);
        // Do not let boundary keys escape to directional focus navigation
        // (in particular, the preview splitter).
        if (targetIndex == currentIndex) return true;

        var targetItem = listView.Items[targetIndex];
        listView.SelectedItems.Clear();
        listView.SelectedItem = targetItem;
        listView.ScrollIntoView(targetItem);
        listView.Focus();
        return true;
    }
}
