using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace FileKakari;

public sealed class BuiltInImagePreviewProvider : IFilePreviewProvider
{
    public const long MaxImageBytes = 32L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    public bool CanPreview(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return ImageExtensions.Contains(extension);
    }

    public async Task<FilePreviewResult> CreatePreviewAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(request.FilePath);
            if (!fileInfo.Exists)
            {
                return new FilePreviewResult(FilePreviewStatus.Missing, FilePreviewKind.Image);
            }

            var fileInfoResult = new FilePreviewInfo(
                fileInfo.Name,
                fileInfo.FullName,
                fileInfo.Extension,
                fileInfo.Length,
                fileInfo.LastWriteTime);

            if (fileInfo.Length > MaxImageBytes)
            {
                return new FilePreviewResult(FilePreviewStatus.TooLarge, FilePreviewKind.Image, SizeLimit: MaxImageBytes, FileInfo: fileInfoResult);
            }

            var scaleFactor = 1.25;
            var targetWidthPx = request.TargetWidthDip > 0
                ? (int)Math.Ceiling(request.TargetWidthDip * request.DpiScaleX * scaleFactor)
                : 1920;
            var targetHeightPx = request.TargetHeightDip > 0
                ? (int)Math.Ceiling(request.TargetHeightDip * request.DpiScaleY * scaleFactor)
                : 1080;

            targetWidthPx = Math.Max(1, targetWidthPx);
            targetHeightPx = Math.Max(1, targetHeightPx);

            cancellationToken.ThrowIfCancellationRequested();

            var (bitmap, metrics) = await Task.Run(() =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                cancellationToken.ThrowIfCancellationRequested();

                using var stream = new FileStream(
                    request.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    options: FileOptions.SequentialScan);

                var openMs = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Restart();

                cancellationToken.ThrowIfCancellationRequested();

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                bitmapImage.DecodePixelWidth = targetWidthPx;

                cancellationToken.ThrowIfCancellationRequested();

                bitmapImage.EndInit();

                var decodeMs = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Restart();

                cancellationToken.ThrowIfCancellationRequested();

                bitmapImage.Freeze();

                var freezeMs = stopwatch.Elapsed.TotalMilliseconds;

                return (bitmapImage, (openMs, decodeMs, freezeMs));
            }, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (PreviewDiagnostics.IsVerboseEnabled)
            {
                var totalMs = metrics.openMs + metrics.decodeMs + metrics.freezeMs;
                var decodedPixels = $"{bitmap.PixelWidth}x{bitmap.PixelHeight}";
                var requestedMaxPixels = $"{targetWidthPx}x{targetHeightPx}";
                PreviewDiagnostics.Verbose("PreviewImage", $"Load\r\npath=\"{request.FilePath}\"\r\nfileBytes={fileInfo.Length}\r\ndecodedPixels=\"{decodedPixels}\"\r\nrequestedMaxPixels=\"{requestedMaxPixels}\"\r\ndecodePixelWidth={targetWidthPx}\r\ndecodePixelHeight=0\r\nopenMs={metrics.openMs:F1}\r\ndecodeMs={metrics.decodeMs:F1}\r\nfreezeMs={metrics.freezeMs:F1}\r\ntotalMs={totalMs:F1}\r\ncancelled=false\r\ngeneration={request.Generation}");
            }

            return new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.Image, ImageSource: bitmap, FileInfo: fileInfoResult);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.Image, ErrorMessage: ex.Message);
        }
    }
}
