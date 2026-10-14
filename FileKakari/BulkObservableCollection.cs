using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace FileKakari;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void ReplaceAll(IEnumerable<T> items)
    {
        // Materialize before changing the collection: callers may pass this collection
        // or a lazy projection of it, and enumeration may fail.
        var replacement = items.ToList();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in replacement) Items.Add(item);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IEnumerable<T> items)
    {
        var added = false;
        _suppressNotifications = true;

        try
        {
            foreach (var item in items)
            {
                Items.Add(item);
                added = true;
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        if (added)
        {
            OnPropertyChanged(new(nameof(Count)));
            OnPropertyChanged(new("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
        {
            base.OnPropertyChanged(e);
        }
    }
}
