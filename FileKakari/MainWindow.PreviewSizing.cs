using System.Windows;
using System.Windows.Media;

namespace FileKakari;

public partial class MainWindow
{
    private FilePreviewResult? _displayedImagePreview;
    private int _displayedImageGeneration;
    private CancellationTokenSource? _imageResizeCancellation;
    private readonly BuiltInImagePreviewProvider _resizeImageProvider = new();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (PreviewContentViewport is not null) QueueImagePreviewResize();
    }

    private void QueueImagePreviewResize()
    {
        _imageResizeCancellation?.Cancel();
        _imageResizeCancellation?.Dispose();
        _imageResizeCancellation = null;
        if (!IsPreviewPaneActuallyVisible || _displayedImagePreview is not { ImageSource: { } bitmap, FileInfo: { } info } result
            || _displayedImageGeneration != _previewGeneration) return;

        var dpi = VisualTreeHelper.GetDpi(PreviewContentViewport);
        var width = Math.Max(1, PreviewContentViewport.ActualWidth - 16);
        var height = Math.Max(1, PreviewContentViewport.ActualHeight - 16);
        var originalWidth = result.OriginalPixelWidth;
        var originalHeight = result.OriginalPixelHeight;
        if (originalWidth <= 0 || originalHeight <= 0) return;
        // Original physical pixels, rather than embedded image DPI, define the no-upscale limit.
        var displayScale = Math.Min(1, Math.Min(width * dpi.DpiScaleX / originalWidth, height * dpi.DpiScaleY / originalHeight));
        PreviewImage.Width = originalWidth * displayScale / dpi.DpiScaleX;
        PreviewImage.Height = originalHeight * displayScale / dpi.DpiScaleY;
        var needed = ImagePreviewSizing.Fit(originalWidth, originalHeight, width * dpi.DpiScaleX, height * dpi.DpiScaleY);
        if (needed.Width <= bitmap.PixelWidth + 1 && needed.Height <= bitmap.PixelHeight + 1) return;

        _imageResizeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_previewCancellation?.Token ?? CancellationToken.None);
        _ = RefreshImageSizeAsync(new PreviewRequest(info.FullPath)
        {
            TargetWidthDip = width, TargetHeightDip = height,
            DpiScaleX = dpi.DpiScaleX, DpiScaleY = dpi.DpiScaleY, Generation = _previewGeneration
        }, _imageResizeCancellation.Token);
    }

    private async Task RefreshImageSizeAsync(PreviewRequest request, CancellationToken token)
    {
        try
        {
            await Task.Delay(160, token);
            var result = await _resizeImageProvider.CreatePreviewAsync(request, token);
            token.ThrowIfCancellationRequested();
            if (request.Generation != _previewGeneration || !IsPreviewPaneActuallyVisible
                || result.Status != FilePreviewStatus.Success || result.ImageSource is null) return;
            _displayedImagePreview = result;
            PreviewImage.Source = result.ImageSource;
            QueueImagePreviewResize();
        }
        catch (OperationCanceledException) { }
    }
}
