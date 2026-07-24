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

        var offset = GetTabInsertIndicatorOffset(adornerTarget, target);
        if (double.IsNaN(offset))
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

        _tabInsertIndicatorAdorner.Update(offset, target.Orientation);
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

    private static double GetTabInsertIndicatorOffset(FrameworkElement adornedElement, TabInsertDropTarget target)
    {
        if (target.TargetElement is null)
        {
            return 0;
        }

        var orientation = target.Orientation;
        var boundaryOffset = target.Zone == TabDropZone.After
            ? (orientation == TabStripOrientation.Horizontal ? target.TargetElement.ActualWidth : target.TargetElement.ActualHeight)
            : 0;

        try
        {
            var point = orientation == TabStripOrientation.Horizontal
                ? new Point(boundaryOffset, 0)
                : new Point(0, boundaryOffset);
            var transformedPoint = target.TargetElement
                .TransformToAncestor(adornedElement)
                .Transform(point);
            return orientation == TabStripOrientation.Horizontal ? transformedPoint.X : transformedPoint.Y;
        }
        catch (InvalidOperationException)
        {
            return double.NaN;
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
        private double _offset;
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

        public void Update(double offset, TabStripOrientation orientation = TabStripOrientation.Horizontal)
        {
            _offset = offset;
            _orientation = orientation;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_orientation == TabStripOrientation.Horizontal)
            {
                var height = Math.Max(0, AdornedElement.RenderSize.Height);
                if (height <= 0)
                {
                    return;
                }

                var x = Math.Round(_offset) + 0.5;
                drawingContext.DrawLine(_pen, new Point(x, 2), new Point(x, Math.Max(2, height - 2)));
            }
            else
            {
                var width = Math.Max(0, AdornedElement.RenderSize.Width);
                if (width <= 0)
                {
                    return;
                }

                var y = Math.Round(_offset) + 0.5;
                drawingContext.DrawLine(_pen, new Point(2, y), new Point(Math.Max(2, width - 2), y));
            }
        }
    }
}
