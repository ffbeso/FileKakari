using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileKakari;

public sealed class ScrollBehaviorService
{
    private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(16);
    private const double AutoScrollDeadZone = 8;
    private const double AutoScrollMaxPixelsPerTick = 48;
    private const double AutoScrollScale = 0.22;

    private readonly ListView _itemsList;
    private readonly Canvas _markerOverlay;
    private readonly FrameworkElement _marker;
    private readonly Func<bool> _isRangeSelecting;
    private readonly Func<bool> _isLoading;
    private readonly Func<ScrollViewer?> _getScrollViewer;
    private readonly DispatcherTimer _autoScrollTimer = new() { Interval = AutoScrollInterval };
    private Point _autoScrollStartPoint;
    private Point _autoScrollCurrentPoint;
    private bool _isDragMode;
    private bool _dragMoved;

    public ScrollBehaviorService(
        ListView itemsList,
        Canvas markerOverlay,
        FrameworkElement marker,
        Func<bool> isRangeSelecting,
        Func<bool> isLoading,
        Func<ScrollViewer?> getScrollViewer)
    {
        _itemsList = itemsList;
        _markerOverlay = markerOverlay;
        _marker = marker;
        _isRangeSelecting = isRangeSelecting;
        _isLoading = isLoading;
        _getScrollViewer = getScrollViewer;
        _autoScrollTimer.Tick += AutoScrollTimer_Tick;
    }

    public bool IsAutoScrolling { get; private set; }

#if DEBUG
    public int DiagnosticTickCount { get; private set; }

    public void ResetDiagnostics()
    {
        DiagnosticTickCount = 0;
    }
#endif

    public void StartAutoScroll(Point startPoint, bool dragMode)
    {
        if (_isLoading())
        {
            return;
        }

        _autoScrollStartPoint = startPoint;
        _autoScrollCurrentPoint = startPoint;
        _isDragMode = dragMode;
        _dragMoved = false;
        IsAutoScrolling = true;
        ShowMarker(startPoint);
        _itemsList.CaptureMouse();
        _autoScrollTimer.Start();
    }

    public void UpdateAutoScrollPoint(Point point)
    {
        _autoScrollCurrentPoint = point;
        if (_isDragMode
            && (Math.Abs(point.X - _autoScrollStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(point.Y - _autoScrollStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance))
        {
            _dragMoved = true;
        }
    }

    public bool CompleteMiddleButtonPress(Point point)
    {
        if (!IsAutoScrolling || !_isDragMode)
        {
            return false;
        }

        UpdateAutoScrollPoint(point);
        if (_dragMoved)
        {
            StopAutoScroll();
        }
        else
        {
            _isDragMode = false;
        }

        return true;
    }

    public void StopAutoScroll()
    {
        if (!IsAutoScrolling)
        {
            return;
        }

        IsAutoScrolling = false;
        _isDragMode = false;
        _dragMoved = false;
        _autoScrollTimer.Stop();
        HideMarker();
        if (_itemsList.IsMouseCaptured)
        {
            _itemsList.ReleaseMouseCapture();
        }
    }

    private void AutoScrollTimer_Tick(object? sender, EventArgs e)
    {
#if DEBUG
        DiagnosticTickCount++;
#endif
        if (!IsAutoScrolling)
        {
            return;
        }

        if (_isLoading() || _isRangeSelecting())
        {
            StopAutoScroll();
            return;
        }

        var scrollViewer = _getScrollViewer();
        if (scrollViewer is null)
        {
            StopAutoScroll();
            return;
        }

        var horizontalDelta = GetScrollDelta(_autoScrollCurrentPoint.X - _autoScrollStartPoint.X);
        var verticalDelta = GetScrollDelta(_autoScrollCurrentPoint.Y - _autoScrollStartPoint.Y);
        if (horizontalDelta == 0 && verticalDelta == 0)
        {
            return;
        }

        if (horizontalDelta != 0)
        {
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + horizontalDelta);
        }

        if (verticalDelta != 0)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + verticalDelta);
        }
    }

    private static double GetScrollDelta(double distance)
    {
        var magnitude = Math.Abs(distance);
        if (magnitude <= AutoScrollDeadZone)
        {
            return 0;
        }

        return Math.Sign(distance) * Math.Min(AutoScrollMaxPixelsPerTick, (magnitude - AutoScrollDeadZone) * AutoScrollScale);
    }

    private void ShowMarker(Point point)
    {
        _markerOverlay.Width = _itemsList.ActualWidth;
        _markerOverlay.Height = _itemsList.ActualHeight;
        _markerOverlay.Visibility = Visibility.Visible;
        Canvas.SetLeft(_marker, point.X - _marker.Width / 2);
        Canvas.SetTop(_marker, point.Y - _marker.Height / 2);
    }

    private void HideMarker()
    {
        _markerOverlay.Visibility = Visibility.Collapsed;
    }
}
