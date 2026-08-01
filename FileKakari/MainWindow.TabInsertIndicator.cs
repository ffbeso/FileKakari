using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FileKakari;

public partial class MainWindow
{
    private TabInsertIndicatorAdorner? _tabInsertIndicatorAdorner;
    private AdornerLayer? _tabInsertIndicatorLayer;
    private FrameworkElement? _tabInsertIndicatorElement;

    private void ShowTabInsertIndicator(FrameworkElement adornedElement, TabInsertDropTarget target)
    {
        if (!target.IsInsert)
        {
            HideTabInsertIndicator();
            return;
        }

        adornedElement.UpdateLayout();
        var adornerTarget = GetTabInsertIndicatorAdornerTarget(adornedElement);
        var layer = AdornerLayer.GetAdornerLayer(adornerTarget);
        if (layer is null)
        {
            HideTabInsertIndicator();
            return;
        }

        if (!TryCalculateTabInsertIndicatorGeometry(adornedElement, adornerTarget, target, out var insertPoint, out var tabBarRect))
        {
            HideTabInsertIndicator();
            return;
        }

        if (!ReferenceEquals(_tabInsertIndicatorLayer, layer)
            || !ReferenceEquals(_tabInsertIndicatorElement, adornerTarget)
            || _tabInsertIndicatorAdorner is null)
        {
            HideTabInsertIndicator();
            var brush = TryFindResource("ActivePaneAccentBrush") as Brush
                ?? SystemColors.HighlightBrush;
            _tabInsertIndicatorAdorner = new TabInsertIndicatorAdorner(adornerTarget, brush);
            _tabInsertIndicatorLayer = layer;
            _tabInsertIndicatorElement = adornerTarget;
            layer.Add(_tabInsertIndicatorAdorner);
        }

        _tabInsertIndicatorAdorner.Update(insertPoint, tabBarRect, target.Orientation);
    }

    private static FrameworkElement GetTabInsertIndicatorAdornerTarget(FrameworkElement preferredTarget)
    {
        for (DependencyObject? current = preferredTarget; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element && AdornerLayer.GetAdornerLayer(element) is not null)
            {
                return element;
            }
        }

        return preferredTarget;
    }

    private static bool TryCalculateTabInsertIndicatorGeometry(
        FrameworkElement adornedElement,
        FrameworkElement adornerTarget,
        TabInsertDropTarget target,
        out Point insertPoint,
        out Rect tabBarRect)
    {
        insertPoint = default;
        tabBarRect = default;

        try
        {
            var localBarRect = new Rect(0, 0, adornedElement.ActualWidth, adornedElement.ActualHeight);
            var transformBar = adornedElement.TransformToAncestor(adornerTarget);
            tabBarRect = transformBar.TransformBounds(localBarRect);

            if (target.TargetElement is not null)
            {
                var orientation = target.Orientation;
                var boundaryOffset = target.Zone == TabDropZone.After
                    ? (orientation == TabStripOrientation.Horizontal ? target.TargetElement.ActualWidth : target.TargetElement.ActualHeight)
                    : 0;
                var localInsertPoint = orientation == TabStripOrientation.Horizontal
                    ? new Point(boundaryOffset, 0)
                    : new Point(0, boundaryOffset);

                var transformTarget = target.TargetElement.TransformToAncestor(adornerTarget);
                insertPoint = transformTarget.Transform(localInsertPoint);
            }
            else
            {
                insertPoint = target.Orientation == TabStripOrientation.Horizontal
                    ? new Point(tabBarRect.Right, tabBarRect.Top)
                    : new Point(tabBarRect.Left, tabBarRect.Bottom);
            }

            return !double.IsNaN(insertPoint.X)
                && !double.IsNaN(insertPoint.Y)
                && !double.IsNaN(tabBarRect.Width)
                && !double.IsNaN(tabBarRect.Height)
                && tabBarRect.Width > 0
                && tabBarRect.Height > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void HideTabInsertIndicator()
    {
        if (_tabInsertIndicatorAdorner is not null && _tabInsertIndicatorLayer is not null)
        {
            _tabInsertIndicatorLayer.Remove(_tabInsertIndicatorAdorner);
        }

        _tabInsertIndicatorAdorner = null;
        _tabInsertIndicatorLayer = null;
        _tabInsertIndicatorElement = null;
    }

    private sealed class TabInsertIndicatorAdorner : Adorner
    {
        private readonly Pen _pen;
        private Point _insertPoint;
        private Rect _tabBarRect;
        private TabStripOrientation _orientation = TabStripOrientation.Horizontal;

        public TabInsertIndicatorAdorner(UIElement adornedElement, Brush brush)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            _pen = new Pen(brush, 2);
            if (_pen.CanFreeze)
            {
                _pen.Freeze();
            }
        }

        public void Update(Point insertPoint, Rect tabBarRect, TabStripOrientation orientation)
        {
            _insertPoint = insertPoint;
            _tabBarRect = tabBarRect;
            _orientation = orientation;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_tabBarRect.Width <= 0 || _tabBarRect.Height <= 0)
            {
                return;
            }

            if (_orientation == TabStripOrientation.Horizontal)
            {
                var x = Math.Clamp(Math.Round(_insertPoint.X) + 0.5, _tabBarRect.Left + 0.5, _tabBarRect.Right - 0.5);
                var y1 = _tabBarRect.Top + 2;
                var y2 = Math.Max(y1, _tabBarRect.Bottom - 2);
                drawingContext.DrawLine(_pen, new Point(x, y1), new Point(x, y2));
            }
            else
            {
                var y = Math.Clamp(Math.Round(_insertPoint.Y) + 0.5, _tabBarRect.Top + 0.5, _tabBarRect.Bottom - 0.5);
                var x1 = _tabBarRect.Left + 2;
                var x2 = Math.Max(x1, _tabBarRect.Right - 2);
                drawingContext.DrawLine(_pen, new Point(x1, y), new Point(x2, y));
            }
        }
    }

}
