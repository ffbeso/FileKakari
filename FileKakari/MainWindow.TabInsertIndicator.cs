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
        var layer = AdornerLayer.GetAdornerLayer(adornedElement);
        if (layer is null)
        {
            HideTabInsertIndicator();
            return;
        }

        var x = GetTabInsertIndicatorX(adornedElement, target);
        if (double.IsNaN(x))
        {
            HideTabInsertIndicator();
            return;
        }

        if (!ReferenceEquals(_tabInsertIndicatorLayer, layer)
            || !ReferenceEquals(_tabInsertIndicatorElement, adornedElement)
            || _tabInsertIndicatorAdorner is null)
        {
            HideTabInsertIndicator();
            var brush = TryFindResource("ActivePaneAccentBrush") as Brush
                ?? SystemColors.HighlightBrush;
            _tabInsertIndicatorAdorner = new TabInsertIndicatorAdorner(adornedElement, brush);
            _tabInsertIndicatorLayer = layer;
            _tabInsertIndicatorElement = adornedElement;
            layer.Add(_tabInsertIndicatorAdorner);
        }

        _tabInsertIndicatorAdorner.Update(x);
    }

    private static double GetTabInsertIndicatorX(FrameworkElement adornedElement, TabInsertDropTarget target)
    {
        if (target.TargetElement is null)
        {
            return 0;
        }

        var targetX = target.Zone == TabDropZone.Right
            ? target.TargetElement.ActualWidth
            : 0;

        try
        {
            return target.TargetElement
                .TransformToAncestor(adornedElement)
                .Transform(new Point(targetX, 0))
                .X;
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
        private double _x;

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

        public void Update(double x)
        {
            _x = x;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var height = Math.Max(0, AdornedElement.RenderSize.Height);
            if (height <= 0)
            {
                return;
            }

            var x = Math.Round(_x) + 0.5;
            drawingContext.DrawLine(_pen, new Point(x, 2), new Point(x, Math.Max(2, height - 2)));
        }
    }
}
