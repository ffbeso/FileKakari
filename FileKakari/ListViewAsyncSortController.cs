using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileKakari;

internal sealed class ListViewAsyncSortController
{
    private CancellationTokenSource? _pending;
    private long _request;

    public void Cancel()
    {
        _request++;
        _pending?.Cancel();
    }

    public async Task<bool> SortAsync(ListView list, BulkObservableCollection<FileEntry> items,
        ListCollectionView view, string column, bool ascending, bool foldersFirst,
        Func<bool> isCurrent, Action<Action> publish, INotifyPropertyChanged owner)
    {
        Cancel();
        var request = _request;
        // A filter/metadata update invalidates the snapshot, but not the user's sort request.
        // Retry against the new data while this request still owns the same tab and settings.
        while (request == _request && isCurrent())
        {
            using var cancellation = new CancellationTokenSource();
            _pending = cancellation;
            var token = cancellation.Token;
            var observed = new List<FileEntry>();
            void Changed(object? sender, NotifyCollectionChangedEventArgs e) => cancellation.Cancel();
            void MetadataChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName is nameof(FileEntry.Name) or nameof(FileEntry.FullPath) or nameof(FileEntry.Kind)
                    or nameof(FileEntry.Size) or nameof(FileEntry.ModifiedAt) or nameof(FileEntry.CreatedAt)
                    or nameof(FileEntry.AccessedAt) or nameof(FileEntry.AttributesText)) cancellation.Cancel();
            }
            void OwnerChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (!isCurrent()) cancellation.Cancel();
            }
            void Hidden(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (!list.IsVisible) Cancel();
            }
            void Unloaded(object sender, RoutedEventArgs e) => Cancel();
            void Detach()
            {
                ((INotifyCollectionChanged)view).CollectionChanged -= Changed;
                items.CollectionChanged -= Changed;
                owner.PropertyChanged -= OwnerChanged;
                list.IsVisibleChanged -= Hidden;
                list.Unloaded -= Unloaded;
                foreach (var entry in observed) entry.PropertyChanged -= MetadataChanged;
                observed.Clear();
            }
            void Check()
            {
                token.ThrowIfCancellationRequested();
                if (request != _request || !isCurrent()) throw new OperationCanceledException(token);
            }
            ((INotifyCollectionChanged)view).CollectionChanged += Changed;
            items.CollectionChanged += Changed;
            owner.PropertyChanged += OwnerChanged;
            list.IsVisibleChanged += Hidden;
            list.Unloaded += Unloaded;
            try
            {
                var timer = Stopwatch.StartNew();
                var previous = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < view.Count; i++)
                {
                    if (view.GetItemAt(i) is FileEntry entry) previous[entry.FullPath] = i;
                    if ((i & 511) == 511)
                    {
                        await list.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Check();
                    }
                }
                var entries = new FileEntry[items.Count];
                var keys = new FileSortKey[items.Count];
                for (var i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    entries[i] = entry;
                    keys[i] = FileSortSnapshot.Capture(entry, i, column, previous.TryGetValue(entry.FullPath, out var index) ? index : null);
                    entry.PropertyChanged += MetadataChanged;
                    observed.Add(entry);
                    if ((i & 511) == 511)
                    {
                        await list.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Check();
                    }
                }
                Check();
                PerfLog.WriteVerbose($"column-sort-stage stage=snapshot count={items.Count} elapsedMs={timer.Elapsed.TotalMilliseconds:F2}");
                timer.Restart();
                var order = await FileSortSnapshot.SortAsync(keys, column, ascending, foldersFirst, token);
                Check();
                PerfLog.WriteVerbose($"column-sort-stage stage=worker count={items.Count} elapsedMs={timer.Elapsed.TotalMilliseconds:F2}");
                // Do not replace rows in the middle of rectangle selection or file dragging.
                while (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    await Task.Delay(30, token);
                    Check();
                }
                var sorted = order.Select(i => entries[i]).ToArray();
                // Capture immediately before replacing the view, including input during the worker sort.
                var selected = list.SelectedItems.OfType<FileEntry>().ToHashSet();
                var anchor = CaptureViewport(list, selected);
                Detach(); // Our own single Reset must not cancel the request.
                timer.Restart();
                publish(() =>
                {
                    using var selectionBatch = SelectionUpdateBatch.Begin(list);
                    using (view.DeferRefresh())
                    {
                        view.CustomSort = null;
                        view.SortDescriptions.Clear();
                        items.ReplaceAll(sorted);
                    }
                    FileListRangeSelectionHelper.ApplySelection(list, selected);
                    RestoreViewport(list, anchor);
                });
                PerfLog.WriteVerbose($"column-sort-stage stage=publish count={items.Count} elapsedMs={timer.Elapsed.TotalMilliseconds:F2}");
                return true;
            }
            catch (OperationCanceledException)
            {
                PerfLog.WriteVerbose($"column-sort-discard request={request} latest={_request}");
            }
            finally
            {
                Detach();
                if (ReferenceEquals(_pending, cancellation)) _pending = null;
            }
            await list.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        return false;
    }

    private sealed record Viewport(FileEntry? Entry, double RelativeTop, double Offset);

    private static Viewport CaptureViewport(ListView list, HashSet<FileEntry> selected)
    {
        var scroll = FindScroll(list);
        if (scroll is null) return new(null, 0, 0);
        var presenter = scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter;
        Visual relativeTo = presenter ?? (Visual)list;
        var height = presenter?.ActualHeight ?? list.ActualHeight;
        var visible = FileListRangeSelectionHelper.EnumerateRealizedRows(list)
            .Where(row => row.IsVisible && row.Content is FileEntry && ItemsControl.ItemsControlFromItemContainer(row) == list)
            .Select(row => (Entry: (FileEntry)row.Content, Top: row.TransformToAncestor(relativeTo).Transform(new Point()).Y, Height: row.ActualHeight))
            .Where(row => row.Top < height && row.Top + row.Height > 0)
            .OrderBy(row => row.Top).ToArray();
        var anchor = visible.FirstOrDefault(row => selected.Contains(row.Entry));
        if (anchor.Entry is null) anchor = visible.FirstOrDefault();
        return new(anchor.Entry, anchor.Top, scroll.VerticalOffset);
    }

    private static void RestoreViewport(ListView list, Viewport anchor)
    {
        var scroll = FindScroll(list);
        if (scroll is null) return;
        if (anchor.Entry is not null)
        {
            list.ScrollIntoView(anchor.Entry);
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(anchor.Entry) is ListViewItem row)
            {
                var presenter = scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter;
                var top = row.TransformToAncestor(presenter ?? (Visual)list).Transform(new Point()).Y;
                scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + top - anchor.RelativeTop));
                list.UpdateLayout();
                return;
            }
        }
        scroll.ScrollToVerticalOffset(anchor.Offset);
        list.UpdateLayout();
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}
