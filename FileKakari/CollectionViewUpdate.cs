using System.ComponentModel;
using System.Windows.Data;

namespace FileKakari;

public static class CollectionViewUpdate
{
    public static void SetFilterAndRefresh(ICollectionView view, Predicate<object>? filter)
    {
        // Assigning a different filter already refreshes the WPF view. A caller
        // reusing its predicate still needs to reflect changed item metadata.
        if (ReferenceEquals(view.Filter, filter)) view.Refresh();
        else view.Filter = filter;
    }

    public static int Count(ICollectionView view)
    {
        if (view is ListCollectionView list) return list.Count;
        var count = 0;
        foreach (var _ in view) count++;
        return count;
    }
}
