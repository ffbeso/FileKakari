namespace FileKakari;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal sealed class ListViewRangeSelectionSession : IDisposable
{
    private readonly ListView _listView;
    private readonly Point _startPoint;
    private readonly bool _additive;
    private readonly HashSet<FileEntry> _baseSelection;
    private bool _moved;
    private readonly ScrollViewer? _scroll;
    private readonly double _initialOffset;
    private readonly Action<Rect>? _draw;
    private readonly Dictionary<FileEntry, Rect> _visitedRows = new();
    private readonly DispatcherTimer _timer;
    private readonly object _owner;
    private readonly long? _generation;
    private readonly int _itemCount;
    private bool _disposed;

    public ListViewRangeSelectionSession(ListView listView, Point startPoint, bool additive, Action<Rect>? draw = null)
    {
        _listView = listView;
        _startPoint = startPoint;
        _additive = additive;
        _baseSelection = new HashSet<FileEntry>(listView.SelectedItems.OfType<FileEntry>());
        _scroll = FindVisual<ScrollViewer>(listView);
        _initialOffset = _scroll?.VerticalOffset ?? 0;
        _draw = draw;
        _owner = listView.DataContext;
        _generation = (_owner as FolderPane)?.FileList.LoadGeneration;
        _itemCount = listView.Items.Count;
        _timer = new DispatcherTimer(DispatcherPriority.Background, listView.Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += OnTick;
        listView.Unloaded += OnUnloaded;
        listView.RequestBringIntoView += OnRequestBringIntoView;
    }

    public ListView ListView => _listView;
    public Point StartPoint => _startPoint;
    public bool Additive => _additive;
    public bool Moved => _moved;
    public IReadOnlySet<FileEntry> BaseSelection => _baseSelection;

    public bool CheckMove(Point currentPoint)
    {
        if (_moved)
        {
            return true;
        }

        if (Math.Abs(currentPoint.X - _startPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(currentPoint.Y - _startPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            _moved = true;
        }

        return _moved;
    }

    public void ApplySelection(Point currentPoint)
    {
        if (!IsCurrent()) { Dispose(); return; }
        var viewport = GetViewport();
        var offset = _scroll?.VerticalOffset ?? 0;
        currentPoint = new Point(Math.Clamp(currentPoint.X, viewport.Left, viewport.Right), Math.Clamp(currentPoint.Y, viewport.Top, viewport.Bottom));
        var contentRect = FileListRangeSelectionHelper.CreateSelectionRect(
            new Point(_startPoint.X, _startPoint.Y + _initialOffset), new Point(currentPoint.X, currentPoint.Y + offset));
        foreach (var row in FileListRangeSelectionHelper.EnumerateRealizedRows(_listView))
        {
            if (row.Content is not FileEntry entry || !row.IsVisible
                || ItemsControl.ItemsControlFromItemContainer(row) != _listView) continue;
            var point = row.TranslatePoint(new Point(), _listView);
            _visitedRows[entry] = new Rect(new Point(point.X, point.Y + offset), new Size(row.ActualWidth, row.ActualHeight));
        }
        var target = _additive ? new HashSet<FileEntry>(_baseSelection) : new HashSet<FileEntry>();
        foreach (var pair in _visitedRows)
            if (contentRect.IntersectsWith(pair.Value)) target.Add(pair.Key);
        FileListRangeSelectionHelper.ApplySelection(_listView, target);
        var displayRect = contentRect;
        displayRect.Offset(0, -offset);
        displayRect.Intersect(viewport);
        if (!displayRect.IsEmpty) _draw?.Invoke(displayRect);
        if (_moved && !_timer.IsEnabled) _timer.Start();
    }

    // One small pixel step keeps intermediate rows realized before recycling.
    internal void AdvanceAtEdge(Point pointer)
    {
        if (!IsCurrent() || _scroll is null) { Dispose(); return; }
        var viewport = GetViewport();
        var direction = pointer.Y < viewport.Top + 24 ? -1 : pointer.Y > viewport.Bottom - 24 ? 1 : 0;
        if (direction == 0) return;
        ApplySelection(pointer);
        _scroll.ScrollToVerticalOffset(Math.Clamp(_scroll.VerticalOffset + direction * 16, 0, _scroll.ScrollableHeight));
        _listView.UpdateLayout();
        ApplySelection(pointer);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed || !_listView.IsVisible || !IsCurrent()) { Dispose(); return; }
        AdvanceAtEdge(Mouse.GetPosition(_listView));
    }

    private bool IsCurrent() => !_disposed && ReferenceEquals(_owner, _listView.DataContext)
        && _itemCount == _listView.Items.Count
        && _generation == (_listView.DataContext as FolderPane)?.FileList.LoadGeneration;

    private Rect GetViewport()
    {
        // GridView's header contains a nested ScrollViewer. A recursive search
        // can return its presenter instead of the file rows' viewport.
        if (_scroll?.Template?.FindName("PART_ScrollContentPresenter", _scroll) is ScrollContentPresenter presenter)
            return new Rect(presenter.TranslatePoint(new Point(), _listView), new Size(presenter.ActualWidth, presenter.ActualHeight));
        return new Rect(0, 0, _listView.ActualWidth, _listView.ActualHeight);
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();
    private void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_moved) e.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _listView.Unloaded -= OnUnloaded;
        _listView.RequestBringIntoView -= OnRequestBringIntoView;
        _visitedRows.Clear();
    }
}
