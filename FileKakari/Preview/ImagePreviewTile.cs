using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileKakari;

// Each tile owns its cancellation and image; removing it cannot update a new selection.
public sealed class ImagePreviewTile : Border, IDisposable
{
    private readonly BuiltInImagePreviewProvider _provider = new();
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource? _resize;
    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _viewport = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly TextBlock _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private FilePreviewResult? _result;
    private bool _disposed;
    public string FilePath { get; }
    public FilePreviewResult? Result => _result;

    public ImagePreviewTile(string path, bool showName, CancellationToken token)
    {
        FilePath = path;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Margin = new Thickness(2);
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        SetResourceReference(BackgroundProperty, "PanelBackgroundBrush");
        var layout = new DockPanel();
        if (showName)
        {
            var title = new TextBlock { Text = System.IO.Path.GetFileName(path), ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 2, 4, 2) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            DockPanel.SetDock(title, Dock.Top);
            layout.Children.Add(title);
        }
        _message.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        _message.Text = AppStrings.Get("ImagePreviewLoading");
        _viewport.Children.Add(_image);
        _viewport.Children.Add(_message);
        layout.Children.Add(_viewport);
        Child = layout;
        _viewport.SizeChanged += (_, _) => RefreshSize();
        Unloaded += (_, _) => Dispose();
    }

    public async Task LoadAsync(FilePreviewResult? existing = null)
    {
        try
        {
            var result = existing ?? await _provider.CreatePreviewAsync(CreateRequest(), _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
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
        var scale = Math.Min(1, Math.Min(request.TargetWidthDip * request.DpiScaleX / result.OriginalPixelWidth,
            request.TargetHeightDip * request.DpiScaleY / result.OriginalPixelHeight));
        _image.Width = result.OriginalPixelWidth * scale / request.DpiScaleX;
        _image.Height = result.OriginalPixelHeight * scale / request.DpiScaleY;
        _resize?.Cancel();
        _resize?.Dispose();
        _resize = null;
        var needed = ImagePreviewSizing.Fit(result.OriginalPixelWidth, result.OriginalPixelHeight,
            request.TargetWidthDip * request.DpiScaleX, request.TargetHeightDip * request.DpiScaleY);
        if (needed.Width <= bitmap.PixelWidth + 1 && needed.Height <= bitmap.PixelHeight + 1) return;
        _resize = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = UpgradeAsync(request, _resize.Token);
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewDiagnostics.Error("PreviewImage", $"Tile resize failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _resize?.Cancel();
        _resize?.Dispose();
        _lifetime.Dispose();
        _result = null;
        _image.Source = null;
    }
}
