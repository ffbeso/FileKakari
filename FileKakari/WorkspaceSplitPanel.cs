using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileKakari;

public sealed class WorkspaceSplitRatioChangedEventArgs(string splitId, double ratio) : RoutedEventArgs
{
    public string SplitId { get; } = splitId;

    public double Ratio { get; } = ratio;
}

public sealed class WorkspaceSplitPanel : Panel
{
    private const double SplitterVisualThickness = 1;
    private const double SplitterHitThickness = 12;
    private const double MinimumPaneWidth = 160;
    private const double MinimumPaneHeight = 120;
    private const double MinimumRatio = 0.1;
    private const double MaximumRatio = 0.9;

    private readonly Dictionary<string, SplitLayout> _splitLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceSplitterElement> _splitterElements = new(StringComparer.Ordinal);
    private readonly List<WorkspaceSplitterElement> _splitterElementList = [];
    private string? _draggingSplitId;
    private double _draggingRatio;

    public static readonly DependencyProperty LayoutRootProperty =
        DependencyProperty.Register(
            nameof(LayoutRoot),
            typeof(WorkspaceLayoutNodeDefinition),
            typeof(WorkspaceSplitPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly RoutedEvent SplitRatioChangedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(SplitRatioChanged),
            RoutingStrategy.Bubble,
            typeof(EventHandler<WorkspaceSplitRatioChangedEventArgs>),
            typeof(WorkspaceSplitPanel));

    public WorkspaceSplitPanel()
    {
        Background = Brushes.Transparent;
    }

    public event EventHandler<WorkspaceSplitRatioChangedEventArgs> SplitRatioChanged
    {
        add => AddHandler(SplitRatioChangedEvent, value);
        remove => RemoveHandler(SplitRatioChangedEvent, value);
    }

    public WorkspaceLayoutNodeDefinition? LayoutRoot
    {
        get => (WorkspaceLayoutNodeDefinition?)GetValue(LayoutRootProperty);
        set => SetValue(LayoutRootProperty, value);
    }

    protected override int VisualChildrenCount => base.VisualChildrenCount + _splitterElementList.Count;

    protected override Visual GetVisualChild(int index)
    {
        var baseCount = base.VisualChildrenCount;
        if (index < baseCount)
        {
            return base.GetVisualChild(index);
        }
        return _splitterElementList[index - baseCount];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (childrenByPaneId, unreferencedChildren) = CollectChildren();
        var measured = new HashSet<UIElement>();
        var layoutPaneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPaneIds(LayoutRoot, layoutPaneIds);
        AddUnreferencedLayoutChildren(childrenByPaneId, layoutPaneIds, unreferencedChildren);

        if (LayoutRoot is null || layoutPaneIds.Count == 0)
        {
            MeasureFlat(InternalChildren.Cast<UIElement>().ToList(), availableSize, measured);
            UpdateSplitterElements();
            foreach (var splitter in _splitterElementList)
            {
                splitter.Measure(availableSize);
            }
            return availableSize;
        }

        var outerSize = MeasureUnreferencedChildren(unreferencedChildren, availableSize, measured);
        MeasureNode(LayoutRoot, outerSize, childrenByPaneId, measured);

        foreach (UIElement child in InternalChildren)
        {
            if (!measured.Contains(child))
            {
                child.Measure(new Size(0, 0));
            }
        }

        UpdateSplitterElements();
        foreach (var splitter in _splitterElementList)
        {
            splitter.Measure(availableSize);
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (childrenByPaneId, unreferencedChildren) = CollectChildren();
        var arranged = new HashSet<UIElement>();
        var layoutPaneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPaneIds(LayoutRoot, layoutPaneIds);
        AddUnreferencedLayoutChildren(childrenByPaneId, layoutPaneIds, unreferencedChildren);
        _splitLayouts.Clear();

        if (LayoutRoot is null || layoutPaneIds.Count == 0)
        {
            ArrangeFlat(InternalChildren.Cast<UIElement>().ToList(), new Rect(finalSize), arranged);
            foreach (var splitter in _splitterElementList)
            {
                splitter.Arrange(Rect.Empty);
            }
            return finalSize;
        }

        var outerRect = ArrangeUnreferencedChildren(unreferencedChildren, finalSize, arranged);
        ArrangeNode(LayoutRoot, outerRect, childrenByPaneId, arranged);

        foreach (UIElement child in InternalChildren)
        {
            if (!arranged.Contains(child))
            {
                child.Arrange(Rect.Empty);
            }
        }

        foreach (var splitter in _splitterElementList)
        {
            if (_splitLayouts.TryGetValue(splitter.SplitId, out var layout))
            {
                splitter.Arrange(layout.HitRect);
            }
            else
            {
                splitter.Arrange(Rect.Empty);
            }
        }

        InvalidateVisual();
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var brush = TryFindResource("BorderBrush") as Brush ?? SystemColors.ControlDarkBrush;
        foreach (var layout in _splitLayouts.Values)
        {
            drawingContext.DrawRectangle(brush, null, layout.VisualRect);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var position = e.GetPosition(this);
        var hit = _splitLayouts.Values.LastOrDefault(layout => layout.HitRect.Contains(position));
        if (hit is null)
        {
            return;
        }

        _draggingSplitId = hit.Split.Id;
        _draggingRatio = GetRatio(hit.Split);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (_draggingSplitId is not null && IsMouseCaptured && _splitLayouts.TryGetValue(_draggingSplitId, out var layout))
        {
            _draggingRatio = CalculateRatio(layout, position);
            InvalidateMeasure();
            e.Handled = true;
            return;
        }

        var hit = _splitLayouts.Values.LastOrDefault(candidate => candidate.HitRect.Contains(position));
        Cursor = hit?.Split.Orientation == WorkspaceSplitOrientation.Vertical
            ? Cursors.SizeNS
            : hit is not null ? Cursors.SizeWE : null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draggingSplitId is null)
        {
            return;
        }

        var splitId = _draggingSplitId;
        _draggingSplitId = null;
        ReleaseMouseCapture();
        RaiseEvent(new WorkspaceSplitRatioChangedEventArgs(splitId, _draggingRatio)
        {
            RoutedEvent = SplitRatioChangedEvent
        });
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _draggingSplitId = null;
    }

    private void MeasureNode(
        WorkspaceLayoutNodeDefinition node,
        Size size,
        IReadOnlyDictionary<string, UIElement> childrenByPaneId,
        HashSet<UIElement> measured)
    {
        switch (node)
        {
            case WorkspacePaneGroupDefinition pane when childrenByPaneId.TryGetValue(pane.Id, out var child):
                child.Measure(size);
                measured.Add(child);
                break;

            case WorkspaceSplitNodeDefinition split:
                var ratio = GetRatio(split);
                if (split.Orientation == WorkspaceSplitOrientation.Vertical)
                {
                    var firstHeight = size.Height * ratio;
                    MeasureNode(split.First, new Size(size.Width, firstHeight), childrenByPaneId, measured);
                    MeasureNode(split.Second, new Size(size.Width, size.Height - firstHeight), childrenByPaneId, measured);
                }
                else
                {
                    var firstWidth = size.Width * ratio;
                    MeasureNode(split.First, new Size(firstWidth, size.Height), childrenByPaneId, measured);
                    MeasureNode(split.Second, new Size(size.Width - firstWidth, size.Height), childrenByPaneId, measured);
                }
                break;
        }
    }

    private void ArrangeNode(
        WorkspaceLayoutNodeDefinition node,
        Rect rect,
        IReadOnlyDictionary<string, UIElement> childrenByPaneId,
        HashSet<UIElement> arranged)
    {
        switch (node)
        {
            case WorkspacePaneGroupDefinition pane when childrenByPaneId.TryGetValue(pane.Id, out var child):
                ArrangeChild(child, rect, arranged);
                break;

            case WorkspaceSplitNodeDefinition split:
                var ratio = GetRatio(split);
                if (split.Orientation == WorkspaceSplitOrientation.Vertical)
                {
                    var firstHeight = rect.Height * ratio;
                    var boundary = rect.Y + firstHeight;
                    var visualRect = new Rect(rect.X, boundary - SplitterVisualThickness, rect.Width, SplitterVisualThickness);
                    var hitRect = new Rect(rect.X, boundary - SplitterHitThickness / 2, rect.Width, SplitterHitThickness);
                    _splitLayouts[split.Id] = new SplitLayout(split, rect, visualRect, hitRect);
                    ArrangeNode(split.First, new Rect(rect.X, rect.Y, rect.Width, firstHeight), childrenByPaneId, arranged);
                    ArrangeNode(split.Second, new Rect(rect.X, boundary, rect.Width, rect.Height - firstHeight), childrenByPaneId, arranged);
                }
                else
                {
                    var firstWidth = rect.Width * ratio;
                    var boundary = rect.X + firstWidth;
                    var visualRect = new Rect(boundary - SplitterVisualThickness, rect.Y, SplitterVisualThickness, rect.Height);
                    var hitRect = new Rect(boundary - SplitterHitThickness / 2, rect.Y, SplitterHitThickness, rect.Height);
                    _splitLayouts[split.Id] = new SplitLayout(split, rect, visualRect, hitRect);
                    ArrangeNode(split.First, new Rect(rect.X, rect.Y, firstWidth, rect.Height), childrenByPaneId, arranged);
                    ArrangeNode(split.Second, new Rect(boundary, rect.Y, rect.Width - firstWidth, rect.Height), childrenByPaneId, arranged);
                }
                break;
        }
    }

    private double GetRatio(WorkspaceSplitNodeDefinition split)
    {
        if (string.Equals(_draggingSplitId, split.Id, StringComparison.Ordinal))
        {
            return _draggingRatio;
        }

        return double.IsFinite(split.Ratio)
            ? Math.Clamp(split.Ratio, MinimumRatio, MaximumRatio)
            : 0.5;
    }

    private static double CalculateRatio(SplitLayout layout, Point position)
    {
        var vertical = layout.Split.Orientation == WorkspaceSplitOrientation.Vertical;
        var totalLength = vertical ? layout.Bounds.Height : layout.Bounds.Width;
        if (totalLength <= 0)
        {
            return 0.5;
        }

        var offset = vertical
            ? position.Y - layout.Bounds.Y
            : position.X - layout.Bounds.X;
        var minimumLength = vertical ? MinimumPaneHeight : MinimumPaneWidth;
        var minimum = Math.Max(MinimumRatio, Math.Min(0.5, minimumLength / totalLength));
        var maximum = Math.Min(MaximumRatio, 1 - minimum);
        return Math.Clamp(offset / totalLength, minimum, maximum);
    }

    private (Dictionary<string, UIElement> ChildrenByPaneId, List<UIElement> UnreferencedChildren) CollectChildren()
    {
        var childrenByPaneId = new Dictionary<string, UIElement>(StringComparer.OrdinalIgnoreCase);
        var unreferencedChildren = new List<UIElement>();
        foreach (UIElement child in InternalChildren)
        {
            if (GetPaneId(child) is { } paneId && !childrenByPaneId.ContainsKey(paneId))
            {
                childrenByPaneId[paneId] = child;
            }
            else
            {
                unreferencedChildren.Add(child);
            }
        }

        return (childrenByPaneId, unreferencedChildren);
    }

    private static void AddUnreferencedLayoutChildren(
        IReadOnlyDictionary<string, UIElement> childrenByPaneId,
        ISet<string> layoutPaneIds,
        ICollection<UIElement> unreferencedChildren)
    {
        foreach (var pair in childrenByPaneId)
        {
            if (!layoutPaneIds.Contains(pair.Key))
            {
                unreferencedChildren.Add(pair.Value);
            }
        }
    }

    private static Size MeasureUnreferencedChildren(
        IReadOnlyList<UIElement> children,
        Size availableSize,
        ISet<UIElement> measured)
    {
        if (children.Count == 0)
        {
            return availableSize;
        }

        var slots = children.Count + 1;
        var prefixWidth = availableSize.Width / slots;
        foreach (var child in children)
        {
            child.Measure(new Size(prefixWidth, availableSize.Height));
            measured.Add(child);
        }

        return new Size(availableSize.Width - children.Count * prefixWidth, availableSize.Height);
    }

    private static Rect ArrangeUnreferencedChildren(
        IReadOnlyList<UIElement> children,
        Size finalSize,
        ISet<UIElement> arranged)
    {
        if (children.Count == 0)
        {
            return new Rect(finalSize);
        }

        var slots = children.Count + 1;
        var prefixWidth = finalSize.Width / slots;
        for (var i = 0; i < children.Count; i++)
        {
            ArrangeChild(children[i], new Rect(i * prefixWidth, 0, prefixWidth, finalSize.Height), arranged);
        }

        return new Rect(children.Count * prefixWidth, 0, finalSize.Width - children.Count * prefixWidth, finalSize.Height);
    }

    private static void MeasureFlat(IReadOnlyList<UIElement> children, Size size, ISet<UIElement> measured)
    {
        if (children.Count == 0)
        {
            return;
        }

        var width = size.Width / children.Count;
        foreach (var child in children)
        {
            child.Measure(new Size(width, size.Height));
            measured.Add(child);
        }
    }

    private static void ArrangeFlat(IReadOnlyList<UIElement> children, Rect rect, ISet<UIElement> arranged)
    {
        if (children.Count == 0)
        {
            return;
        }

        var width = rect.Width / children.Count;
        for (var i = 0; i < children.Count; i++)
        {
            ArrangeChild(children[i], new Rect(rect.X + i * width, rect.Y, width, rect.Height), arranged);
        }
    }

    private static void ArrangeChild(UIElement child, Rect rect, ISet<UIElement> arranged)
    {
        child.Arrange(rect);
        arranged.Add(child);
    }

    private static void CollectPaneIds(WorkspaceLayoutNodeDefinition? node, ISet<string> paneIds)
    {
        switch (node)
        {
            case WorkspacePaneGroupDefinition pane:
                paneIds.Add(pane.Id);
                break;
            case WorkspaceSplitNodeDefinition split:
                CollectPaneIds(split.First, paneIds);
                CollectPaneIds(split.Second, paneIds);
                break;
        }
    }

    private static string? GetPaneId(UIElement child)
    {
        if (child is ContentPresenter { Content: FolderPane contentPane })
        {
            return contentPane.Id;
        }

        return child is FrameworkElement { DataContext: FolderPane dataContextPane }
            ? dataContextPane.Id
            : null;
    }

    private void CollectSplitNodes(WorkspaceLayoutNodeDefinition? node, List<WorkspaceSplitNodeDefinition> splits)
    {
        if (node is WorkspaceSplitNodeDefinition split)
        {
            splits.Add(split);
            CollectSplitNodes(split.First, splits);
            CollectSplitNodes(split.Second, splits);
        }
    }

    private void UpdateSplitterElements()
    {
        var activeSplits = new List<WorkspaceSplitNodeDefinition>();
        CollectSplitNodes(LayoutRoot, activeSplits);

        var activeSplitIds = activeSplits.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // Remove no longer active splitters
        for (int i = _splitterElementList.Count - 1; i >= 0; i--)
        {
            var splitter = _splitterElementList[i];
            if (!activeSplitIds.Contains(splitter.SplitId))
            {
                RemoveVisualChild(splitter);
                _splitterElements.Remove(splitter.SplitId);
                _splitterElementList.RemoveAt(i);
            }
        }

        // Add new splitters
        foreach (var split in activeSplits)
        {
            if (!_splitterElements.TryGetValue(split.Id, out var splitter))
            {
                splitter = new WorkspaceSplitterElement(split.Id);
                AddVisualChild(splitter);
                _splitterElements[split.Id] = splitter;
                _splitterElementList.Add(splitter);
            }

            splitter.Cursor = split.Orientation == WorkspaceSplitOrientation.Vertical
                ? Cursors.SizeNS
                : Cursors.SizeWE;
        }
    }

    private sealed class WorkspaceSplitterElement : Border
    {
        public string SplitId { get; }

        public WorkspaceSplitterElement(string splitId)
        {
            SplitId = splitId;
            Background = Brushes.Transparent;
        }
    }

    private sealed record SplitLayout(
        WorkspaceSplitNodeDefinition Split,
        Rect Bounds,
        Rect VisualRect,
        Rect HitRect);
}
