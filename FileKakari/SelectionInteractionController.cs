using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileKakari;

public sealed class SelectionInteractionController
{
    private readonly ListView _itemsList;
    private readonly Canvas _overlay;
    private readonly FrameworkElement _selectionRectangle;
    private readonly Func<bool> _isLoading;
    private readonly Action _clearFileDragStart;
    private readonly Action _updateSelectedItemStatus;
    private ListViewRangeSelectionSession? _session;
    private bool _isRestoringScroll;
    private Rect? _pendingRect;
    private ScrollViewer? _scrollViewer;
    private double _horizontalOffset;
    private double _verticalOffset;

    public SelectionInteractionController(
        ListView itemsList,
        Canvas overlay,
        FrameworkElement selectionRectangle,
        Func<bool> isLoading,
        Action clearFileDragStart,
        Action updateSelectedItemStatus)
    {
        _itemsList = itemsList;
        _overlay = overlay;
        _selectionRectangle = selectionRectangle;
        _isLoading = isLoading;
        _clearFileDragStart = clearFileDragStart;
        _updateSelectedItemStatus = updateSelectedItemStatus;
    }

    public bool IsSelecting { get; private set; }

    public void Start(Point startPoint, bool additive)
    {
        if (_isLoading())
        {
            return;
        }

        _clearFileDragStart();
        IsSelecting = true;
        _session = new ListViewRangeSelectionSession(_itemsList, startPoint, additive);

        _scrollViewer = FindVisualChild<ScrollViewer>(_itemsList);
        if (_scrollViewer is not null)
        {
            _horizontalOffset = _scrollViewer.HorizontalOffset;
            _verticalOffset = _scrollViewer.VerticalOffset;
        }

        _overlay.Width = _itemsList.ActualWidth;
        _overlay.Height = _itemsList.ActualHeight;
        _overlay.Visibility = Visibility.Visible;
        _overlay.IsHitTestVisible = true;
        _selectionRectangle.Width = 0;
        _selectionRectangle.Height = 0;
        Canvas.SetLeft(_selectionRectangle, startPoint.X);
        Canvas.SetTop(_selectionRectangle, startPoint.Y);
        _itemsList.Focus();
        _overlay.CaptureMouse();
    }

    public bool HandleMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsSelecting)
        {
            return false;
        }

        e.Handled = true;
        return true;
    }

    public bool HandleRequestBringIntoView(RequestBringIntoViewEventArgs e)
    {
        if (!IsSelecting)
        {
            return false;
        }

        e.Handled = true;
        return true;
    }

    public void HandleScrollChanged(ScrollChangedEventArgs e)
    {
        if (!IsSelecting || _isRestoringScroll)
        {
            return;
        }

        var scrollViewer = e.OriginalSource as ScrollViewer ?? _scrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        if (Math.Abs(scrollViewer.VerticalOffset - _verticalOffset) < 0.1
            && Math.Abs(scrollViewer.HorizontalOffset - _horizontalOffset) < 0.1)
        {
            return;
        }

        _isRestoringScroll = true;
        try
        {
            scrollViewer.ScrollToHorizontalOffset(_horizontalOffset);
            scrollViewer.ScrollToVerticalOffset(_verticalOffset);
        }
        finally
        {
            _isRestoringScroll = false;
        }
    }

    public void HandleLostMouseCapture()
    {
        if (!IsSelecting)
        {
            return;
        }

        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            Finish();
            return;
        }

        EnsureMouseCapture();
    }

    public bool HandlePreviewMouseMove(Point point)
    {
        if (!IsSelecting)
        {
            return false;
        }

        Update(point);
        return true;
    }

    public bool HandlePreviewMouseUp(MouseButton changedButton)
    {
        if (!IsSelecting || changedButton != MouseButton.Left)
        {
            return false;
        }

        Finish();
        return true;
    }

    public void Cancel()
    {
        Clear();
    }

    private void Update(Point currentPoint)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed || _session is null)
        {
            Finish();
            return;
        }

        currentPoint = ClampToItemsList(currentPoint);
        if (!_session.CheckMove(currentPoint))
        {
            return;
        }

        var selectionRect = FileListRangeSelectionHelper.CreateSelectionRect(_session.StartPoint, currentPoint);
        _pendingRect = selectionRect;
        DrawSelectionRect(selectionRect);
        _session.ApplySelection(currentPoint);
    }

    private void EnsureMouseCapture()
    {
        _itemsList.Dispatcher.InvokeAsync(() =>
        {
            if (IsSelecting
                && Mouse.LeftButton == MouseButtonState.Pressed
                && !_overlay.IsMouseCaptured)
            {
                _overlay.CaptureMouse();
            }
        }, DispatcherPriority.Input);
    }

    private Point ClampToItemsList(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, Math.Max(0, _itemsList.ActualWidth)),
            Math.Clamp(point.Y, 0, Math.Max(0, _itemsList.ActualHeight)));
    }

    private void Finish()
    {
        if (_session is not null)
        {
            if (_session.Moved && _pendingRect is { } selectionRect)
            {
                FileListRangeSelectionHelper.SelectItemsInRange(_itemsList, selectionRect, _session.Additive, _session.BaseSelection);
            }

            if (!_session.Moved && !_session.Additive)
            {
                _itemsList.SelectedItems.Clear();
            }
        }

        Clear();
        _updateSelectedItemStatus();
    }

    private void Clear()
    {
        IsSelecting = false;
        _session = null;
        _pendingRect = null;
        _scrollViewer = null;
        _horizontalOffset = 0;
        _verticalOffset = 0;
        _isRestoringScroll = false;
        _overlay.Visibility = Visibility.Collapsed;
        _overlay.IsHitTestVisible = false;
        if (_overlay.IsMouseCaptured)
        {
            _overlay.ReleaseMouseCapture();
        }

        if (_itemsList.IsMouseCaptured)
        {
            _itemsList.ReleaseMouseCapture();
        }
    }

    private void DrawSelectionRect(Rect rect)
    {
        _overlay.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selectionRectangle, rect.Left);
        Canvas.SetTop(_selectionRectangle, rect.Top);
        _selectionRectangle.Width = rect.Width;
        _selectionRectangle.Height = rect.Height;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
