using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace FileKakari;

public sealed partial class BuiltInImagePreviewProvider : IFilePreviewProvider
{
    // Shared by selection loads and resize loads. Cancellation removes obsolete waiters
    // before they open a file; a codec already running is allowed to finish safely.
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
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
        // Metadata access stays off the UI thread. Frozen cached images do not
        // need to wait for a different image's non-cancellable codec operation.
        var cached = await Task.Run(() => TryGetCachedPreview(request, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (cached is not null) return cached;
        await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CreatePreview(request, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static (int Width, int Height) GetTargetSize(PreviewRequest request) => (
        request.TargetWidthDip > 0 ? Math.Max(1, (int)Math.Ceiling(request.TargetWidthDip * request.DpiScaleX)) : 1920,
        request.TargetHeightDip > 0 ? Math.Max(1, (int)Math.Ceiling(request.TargetHeightDip * request.DpiScaleY)) : 1080);

    private static FilePreviewResult? TryGetCachedPreview(PreviewRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var file = new FileInfo(request.FilePath);
            lock (CacheLock)
            {
                // Cold images take the normal path without an extra metadata read.
                if (!ImageCache.Any(item => string.Equals(item.Result.FileInfo!.FullPath, file.FullName, StringComparison.OrdinalIgnoreCase)))
                    return null;
            }
            if (!file.Exists || file.Length > MaxImageBytes) return null;
            var info = new FilePreviewInfo(file.Name, file.FullName, file.Extension, file.Length, file.LastWriteTime);
            var target = GetTargetSize(request);
            token.ThrowIfCancellationRequested();
            return FindCachedImage(info, target.Width, target.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The normal load path reports missing/failed status consistently.
            return null;
        }
    }

    private FilePreviewResult CreatePreview(PreviewRequest request, CancellationToken cancellationToken)
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

            var (targetWidthPx, targetHeightPx) = GetTargetSize(request);

            cancellationToken.ThrowIfCancellationRequested();

            if (FindCachedImage(fileInfoResult, targetWidthPx, targetHeightPx) is { } cached)
                return cached;

            (BitmapImage, (double openMs, double decodeMs, double freezeMs, int originalWidth, int originalHeight)) Decode()
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
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                var fitted = ImagePreviewSizing.Fit(frame.PixelWidth, frame.PixelHeight, targetWidthPx, targetHeightPx);
                stream.Position = 0;
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                // Set only the limiting dimension so the codec preserves the aspect ratio.
                if ((double)fitted.Width / frame.PixelWidth <= (double)fitted.Height / frame.PixelHeight)
                    bitmapImage.DecodePixelWidth = fitted.Width;
                else
                    bitmapImage.DecodePixelHeight = fitted.Height;

                cancellationToken.ThrowIfCancellationRequested();

                bitmapImage.EndInit();

                var decodeMs = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Restart();

                cancellationToken.ThrowIfCancellationRequested();

                bitmapImage.Freeze();

                var freezeMs = stopwatch.Elapsed.TotalMilliseconds;

                return (bitmapImage, (openMs, decodeMs, freezeMs, originalWidth: frame.PixelWidth, originalHeight: frame.PixelHeight));
            }
            var (bitmap, metrics) = Decode();

            cancellationToken.ThrowIfCancellationRequested();

            if (PreviewDiagnostics.IsVerboseEnabled)
            {
                var totalMs = metrics.openMs + metrics.decodeMs + metrics.freezeMs;
                var decodedPixels = $"{bitmap.PixelWidth}x{bitmap.PixelHeight}";
                var requestedMaxPixels = $"{targetWidthPx}x{targetHeightPx}";
                PreviewDiagnostics.Verbose("PreviewImage", $"Load\r\npath=\"{request.FilePath}\"\r\nfileBytes={fileInfo.Length}\r\ndecodedPixels=\"{decodedPixels}\"\r\nrequestedMaxPixels=\"{requestedMaxPixels}\"\r\ndecodePixelWidth={bitmap.DecodePixelWidth}\r\ndecodePixelHeight={bitmap.DecodePixelHeight}\r\nopenMs={metrics.openMs:F1}\r\ndecodeMs={metrics.decodeMs:F1}\r\nfreezeMs={metrics.freezeMs:F1}\r\ntotalMs={totalMs:F1}\r\ncancelled=false\r\ngeneration={request.Generation}");
            }

            var result = new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.Image, ImageSource: bitmap, FileInfo: fileInfoResult,
                OriginalPixelWidth: metrics.originalWidth, OriginalPixelHeight: metrics.originalHeight);
            fileInfo.Refresh();
            if (fileInfo.Exists && fileInfo.Length == fileInfoResult.Size && fileInfo.LastWriteTime == fileInfoResult.LastWriteTime)
                RememberImage(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            return new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.Image, ErrorMessage: ex.Message);
        }
    }
}
