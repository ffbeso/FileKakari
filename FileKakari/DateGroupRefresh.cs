using System.Windows.Controls;
using System.Windows.Data;

namespace FileKakari;

internal static class DateGroupRefresh
{
    // Individual WPF notifications are cheap for a download batch. For large
    // changes use the existing single-reset load instead.
    private const int MaxRowChanges = 64;

    public static bool TryApply(ListView list, DateTimeOffset? lastLoadedAt, IReadOnlyList<FileEntry> snapshot)
    {
        if (lastLoadedAt?.LocalDateTime.Date != DateTime.Today
            || list.ItemsSource is not ListCollectionView { CustomSort: FileEntryComparer activeSort, SourceCollection: BulkObservableCollection<FileEntry> items } view
            || view.GroupDescriptions.Count != 1
            || view.GroupDescriptions[0] is not FileEntryGroupDescription { Mode: GroupMode.Date }
            || MainWindow.FindVisualChild<ScrollViewer>(list) is not { } scroll)
            return false;

        var pending = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        foreach (var entry in snapshot)
            if (!pending.TryAdd(entry.FullPath, entry)) return false;

        var removals = new List<FileEntry>();
        var additions = new List<FileEntry>();
        var changes = new List<(FileEntry Row, FileEntry Updated)>();
        var survivors = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        var columnSort = new FileEntryComparer(activeSort.ColumnId, activeSort.Ascending, activeSort.FoldersFirst);
        foreach (var row in items)
        {
            if (!pending.Remove(row.FullPath, out var updated))
            {
                removals.Add(row);
                continue;
            }
            var group = FileGroupHelper.GetGroupKey(row, GroupMode.Date);
            if (row.IsDirectory != updated.IsDirectory || columnSort.Compare(row, updated) != 0
                || group != FileGroupHelper.GetGroupKey(updated, GroupMode.Date))
            {
                // Selection on a moved row needs the normal selection restoration path.
                if (list.SelectedItems.Contains(row)) return false;
                removals.Add(row);
                additions.Add(updated);
                continue;
            }

            survivors.TryAdd(group, row);
            if (row.ModifiedAt != updated.ModifiedAt || row.CreatedAt != updated.CreatedAt
                || row.AccessedAt != updated.AccessedAt || row.Size != updated.Size
                || row.AttributesText != updated.AttributesText)
                changes.Add((row, updated));
        }
        additions.AddRange(pending.Values);
        if (removals.Count + additions.Count + changes.Count > MaxRowChanges) return false;

        var offset = scroll.VerticalOffset;
        GroupRepresentativeIndex? reps = null;
        if (activeSort.UsesGroupRepresentatives && (removals.Count > 0 || additions.Count > 0))
        {
            reps = GroupRepresentativeIndex.Build(snapshot, GroupMode.Date, activeSort.ColumnId,
                activeSort.Ascending, activeSort.FoldersFirst);
            var newSort = new FileEntryComparer(activeSort.ColumnId, activeSort.Ascending,
                activeSort.FoldersFirst, groupMode: GroupMode.Date, representativeIndex: reps);
            // WPF can insert a group but cannot reorder surviving groups in place.
            var oldOrder = survivors.Values.OrderBy(row => row, Comparer<FileEntry>.Create(activeSort.Compare));
            if (!oldOrder.SequenceEqual(survivors.Values.OrderBy(row => row, Comparer<FileEntry>.Create(newSort.Compare))))
                return false;
        }

        foreach (var row in removals) items.Remove(row);
        if (reps is not null) activeSort.UpdateRepresentatives(reps);
        foreach (var (row, updated) in changes)
        {
            row.ModifiedAt = updated.ModifiedAt;
            row.CreatedAt = updated.CreatedAt;
            row.AccessedAt = updated.AccessedAt;
            row.Size = updated.Size;
            row.AttributesText = updated.AttributesText;
        }
        foreach (var row in additions) items.Add(row);

        if (removals.Count + additions.Count > 0)
        {
            list.UpdateLayout();
            var target = Math.Min(offset, scroll.ScrollableHeight);
            if (Math.Abs(scroll.VerticalOffset - target) > 0.5)
            {
                scroll.ScrollToVerticalOffset(target);
                list.UpdateLayout();
            }
        }
        return true;
    }
}
