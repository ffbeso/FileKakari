using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace FileKakari;

// Each tile owns its cancellation and image; removing it cannot update a new selection.
public sealed class ImagePreviewTile : Border, IDisposable
{
    private readonly BuiltInImagePreviewProvider _provider = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private CancellationTokenSource? _resize;
    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _viewport = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Canvas _surface = new();
    private readonly TextBlock _zoomLabel = new() { MinWidth = 48, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly ImageViewportTransform _transform = new();
    private Point? _dragPoint;
    public event Action<ImagePreviewTile>? Activated;
    public double ZoomScale => _transform.Scale;
    public bool IsFit => _transform.IsFit;
    private readonly TextBlock _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private FilePreviewResult? _result;
    private bool _disposed;
    public string FilePath { get; }
    public FilePreviewResult? Result => _result;

    public ImagePreviewTile(string path, bool showName, CancellationToken token)
    {
        FilePath = path;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _lifetime.Token;
        Margin = new Thickness(2);
        ClipToBounds = true;
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        SetResourceReference(BackgroundProperty, "PanelBackgroundBrush");
        var layout = new DockPanel();
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Button AddButton(string text, string tooltip, Action action)
        {
            var button = new Button { Content = text, ToolTip = tooltip, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(1), Focusable = false, MinHeight = 22 };
            button.Click += (_, _) => { Activated?.Invoke(this); action(); };
            toolbar.Children.Add(button);
            return button;
        }
        AddButton("−", AppStrings.Get("ImageZoomOut"), () => ZoomAt(ZoomScale / 1.25, new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2)));
        _zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        toolbar.Children.Add(_zoomLabel);
        AddButton("+", AppStrings.Get("ImageZoomIn"), () => ZoomAt(ZoomScale * 1.25, new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2)));
        AddButton(AppStrings.Get("ImageZoomFit"), AppStrings.Get("ImageZoomFit"), Fit);
        DockPanel.SetDock(toolbar, Dock.Bottom);
        layout.Children.Add(toolbar);
        if (showName)
        {
            var title = new TextBlock { Text = System.IO.Path.GetFileName(path), ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 2, 4, 2) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            DockPanel.SetDock(title, Dock.Top);
            layout.Children.Add(title);
        }
        _message.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        _message.Text = AppStrings.Get("ImagePreviewLoading");
        _surface.Children.Add(_image);
        _viewport.Children.Add(_surface);
        _viewport.Children.Add(_message);
        layout.Children.Add(_viewport);
        Child = layout;
        _viewport.SizeChanged += (_, _) => RefreshSize();
        PreviewMouseLeftButtonDown += (_, _) => Activated?.Invoke(this);
        _viewport.MouseLeftButtonDown += (_, e) =>
        {
            if (_result is null) return;
            if (e.ClickCount == 2)
            {
                if (_transform.IsFit) ZoomAt(1, e.GetPosition(_viewport)); else Fit();
            }
            else if (_transform.ImageWidth * ZoomScale > _viewport.ActualWidth
                || _transform.ImageHeight * ZoomScale > _viewport.ActualHeight)
            {
                _dragPoint = e.GetPosition(_viewport);
                _viewport.CaptureMouse();
                _viewport.Cursor = Cursors.Hand;
            }
            e.Handled = true;
        };
        _viewport.MouseMove += (_, e) =>
        {
            if (_dragPoint is not { } previous || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(_viewport);
            _transform.Pan(point.X - previous.X, point.Y - previous.Y);
            _dragPoint = point;
            RenderTransformState();
            e.Handled = true;
        };
        _viewport.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragPoint is null) return;
            _viewport.ReleaseMouseCapture();
            e.Handled = true;
        };
        _viewport.LostMouseCapture += (_, _) => { _dragPoint = null; _viewport.Cursor = null; };
        Unloaded += (_, _) => Dispose();
    }

    public async Task LoadAsync(FilePreviewResult? existing = null)
    {
        try
        {
            var result = existing ?? await _provider.CreatePreviewAsync(CreateRequest(), _token);
            _token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (result.Status != FilePreviewStatus.Success || result.ImageSource is null)
            {
                _message.Text = AppStrings.Get("ImagePreviewFailed");
                return;
            }
            _result = result;
            _image.Source = result.ImageSource;
            _message.Visibility = Visibility.Collapsed;
            RefreshSize();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) _message.Text = AppStrings.Get("ImagePreviewFailed");
            PreviewDiagnostics.Error("PreviewImage", $"Tile load failed: {ex.Message}");
        }
    }

    private PreviewRequest CreateRequest()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new(FilePath) { TargetWidthDip = Math.Max(1, _viewport.ActualWidth), TargetHeightDip = Math.Max(1, _viewport.ActualHeight), DpiScaleX = dpi.DpiScaleX, DpiScaleY = dpi.DpiScaleY };
    }

    public void RefreshSize()
    {
        if (_disposed || _result is not { ImageSource: { } bitmap } result) return;
        var request = CreateRequest();
        _transform.SetGeometry(result.OriginalPixelWidth / request.DpiScaleX, result.OriginalPixelHeight / request.DpiScaleY,
            request.TargetWidthDip, request.TargetHeightDip);
        RenderTransformState();
        request = request with { TargetWidthDip = _transform.ImageWidth * ZoomScale, TargetHeightDip = _transform.ImageHeight * ZoomScale };
        _resize?.Cancel();
        _resize?.Dispose();
        _resize = null;
        var needed = ImagePreviewSizing.Fit(result.OriginalPixelWidth, result.OriginalPixelHeight,
            request.TargetWidthDip * request.DpiScaleX, request.TargetHeightDip * request.DpiScaleY);
        if (needed.Width <= bitmap.PixelWidth + 1 && needed.Height <= bitmap.PixelHeight + 1) return;
        _resize = CancellationTokenSource.CreateLinkedTokenSource(_token);
        _ = UpgradeAsync(request, _resize.Token);
    }

    public void SetActive(bool active) => SetResourceReference(BorderBrushProperty, active ? "ActivePaneAccentBrush" : "BorderBrush");

    public void ZoomAt(double scale, Point point)
    {
        if (_disposed || _result is null) return;
        _transform.ZoomAt(scale, point.X, point.Y);
        RefreshSize();
    }

    public void Fit()
    {
        if (_disposed) return;
        _transform.Fit();
        RefreshSize();
    }

    public void ZoomWheel(MouseWheelEventArgs e)
    {
        Activated?.Invoke(this);
        ZoomAt(ZoomScale * Math.Pow(1.25, e.Delta / 120.0), e.GetPosition(_viewport));
        e.Handled = true;
    }

    private void RenderTransformState()
    {
        _image.Width = _transform.ImageWidth * ZoomScale;
        _image.Height = _transform.ImageHeight * ZoomScale;
        Canvas.SetLeft(_image, _transform.X);
        Canvas.SetTop(_image, _transform.Y);
        _zoomLabel.Text = $"{ZoomScale * 100:0.#}%";
        _zoomLabel.ToolTip = AppStrings.Get("ImageZoomHint");
    }

    private async Task UpgradeAsync(PreviewRequest request, CancellationToken token)
    {
        try
        {
            await Task.Delay(160, token);
            var result = await _provider.CreatePreviewAsync(request, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || result.Status != FilePreviewStatus.Success || result.ImageSource is null) return;
            _result = result;
            _image.Source = result.ImageSource;
            RefreshSize();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewDiagnostics.Error("PreviewImage", $"Tile resize failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_viewport.IsMouseCaptured) _viewport.ReleaseMouseCapture();
        _lifetime.Cancel();
        _resize?.Cancel();
        _resize?.Dispose();
        _lifetime.Dispose();
        _result = null;
        _image.Source = null;
    }
}
