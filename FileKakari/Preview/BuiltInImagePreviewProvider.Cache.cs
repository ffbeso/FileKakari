namespace FileKakari;

public sealed partial class BuiltInImagePreviewProvider
{
    // Access is serialized by DecodeGate. No disk cache or background monitoring.
    private const long CacheBudgetBytes = 64L * 1024 * 1024;
    private static readonly LinkedList<(FilePreviewResult Result, long Bytes)> ImageCache = new();
    private static long _cachedBytes;

    private static FilePreviewResult? FindCachedImage(FilePreviewInfo info, int width, int height)
    {
        for (var node = ImageCache.First; node is not null; node = node.Next)
        {
            var result = node.Value.Result;
            var previous = result.FileInfo!;
            if (!string.Equals(previous.FullPath, info.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
            var needed = ImagePreviewSizing.Fit(result.OriginalPixelWidth, result.OriginalPixelHeight, width, height);
            if (previous.Size == info.Size && previous.LastWriteTime == info.LastWriteTime
                && result.ImageSource!.PixelWidth >= needed.Width && result.ImageSource.PixelHeight >= needed.Height)
            {
                ImageCache.Remove(node);
                ImageCache.AddFirst(node);
                PreviewDiagnostics.Verbose("PreviewImage", "Memory cache hit");
                return result;
            }
            _cachedBytes -= node.Value.Bytes;
            ImageCache.Remove(node);
            break;
        }
        return null;
    }

    private static void RememberImage(FilePreviewResult result)
    {
        var bitmap = result.ImageSource!;
        var bytes = (long)bitmap.PixelHeight * (((long)bitmap.PixelWidth * Math.Max(32, bitmap.Format.BitsPerPixel) + 7) / 8);
        if (bytes > CacheBudgetBytes) return;
        while (ImageCache.Last is { } last && (_cachedBytes + bytes > CacheBudgetBytes || ImageCache.Count >= 64))
        {
            _cachedBytes -= last.Value.Bytes;
            ImageCache.RemoveLast();
        }
        ImageCache.AddFirst((result, bytes));
        _cachedBytes += bytes;
    }
}
