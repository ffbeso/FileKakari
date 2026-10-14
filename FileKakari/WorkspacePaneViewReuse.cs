using System.Windows.Controls;

namespace FileKakari;

public static class WorkspacePaneViewReuse
{
    // Check the live view, not just the cached items: a recreated ListView still needs restoration.
    public static bool CanPreserve(FolderPane pane, ListView? listView, double? verticalOffset, bool sortFoldersFirst)
    {
        var state = pane.ActiveTabState;
        var fileList = pane.FileList;
        if (state is null || !pane.IsActiveStateLoaded || pane.IsLoading
            || fileList.PendingAnchorScrollState is not null
            || listView is null || !ReferenceEquals(listView.DataContext, pane)
            || !ReferenceEquals(listView.ItemsSource, fileList.ItemsView)
            || verticalOffset is not { } offset || !double.IsFinite(offset)
            || !double.IsFinite(state.VerticalOffset) || Math.Abs(offset - state.VerticalOffset) > 0.5
            || fileList.DisplaySortColumn != state.SortColumn
            || fileList.DisplaySortAscending != state.SortAscending
            || fileList.DisplaySortFoldersFirst != sortFoldersFirst
            || fileList.DisplayFilterText != state.FilterText)
        {
            return false;
        }

        var groupMode = (fileList.ItemsView.GroupDescriptions.FirstOrDefault() as FileEntryGroupDescription)?.Mode ?? GroupMode.None;
        if (groupMode != state.GroupMode || listView.SelectedItems.Count != state.SelectedPaths.Count)
        {
            return false;
        }

        var selectedPaths = new HashSet<string>(state.SelectedPaths, StringComparer.OrdinalIgnoreCase);
        return listView.SelectedItems.OfType<FileEntry>().All(entry => selectedPaths.Contains(entry.FullPath));
    }
}
