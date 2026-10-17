namespace FileKakari;

public sealed partial class BuiltInImagePreviewProvider
{
    // Cache hits bypass DecodeGate; only short LRU operations hold this lock.
    private static readonly object CacheLock = new();
    private const long CacheBudgetBytes = 64L * 1024 * 1024;
    private static readonly LinkedList<(FilePreviewResult Result, long Bytes)> ImageCache = new();
    private static long _cachedBytes;

    private static FilePreviewResult? FindCachedImage(FilePreviewInfo info, int width, int height)
    {
        lock (CacheLock) return FindCachedImageCore(info, width, height);
    }

    private static FilePreviewResult? FindCachedImageCore(FilePreviewInfo info, int width, int height)
    {
        for (var node = ImageCache.First; node is not null; node = node.Next)
        {
            var result = node.Value.Result;
            var previous = result.FileInfo!;
            if (!string.Equals(previous.FullPath, info.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
            var needed = ImagePreviewSizing.Fit(result.OriginalPixelWidth, result.OriginalPixelHeight, width, height);
            if (previous.Size == info.Size && previous.LastWriteTime == info.LastWriteTime)
            {
                // Keep the smaller bitmap until an upgrade succeeds. A cancelled
                // resize must not evict the image needed by a later smaller view.
                if (result.ImageSource!.PixelWidth < needed.Width || result.ImageSource.PixelHeight < needed.Height)
                    return null;
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
        lock (CacheLock) RememberImageCore(result);
    }

    private static void RememberImageCore(FilePreviewResult result)
    {
        var bitmap = result.ImageSource!;
        var bytes = (long)bitmap.PixelHeight * (((long)bitmap.PixelWidth * Math.Max(32, bitmap.Format.BitsPerPixel) + 7) / 8);
        if (bytes > CacheBudgetBytes) return;
        for (var node = ImageCache.First; node is not null; node = node.Next)
        {
            if (!string.Equals(node.Value.Result.FileInfo!.FullPath, result.FileInfo!.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
            _cachedBytes -= node.Value.Bytes;
            ImageCache.Remove(node);
            break;
        }
        while (ImageCache.Last is { } last && (_cachedBytes + bytes > CacheBudgetBytes || ImageCache.Count >= 64))
        {
            _cachedBytes -= last.Value.Bytes;
            ImageCache.RemoveLast();
        }
        ImageCache.AddFirst((result, bytes));
        _cachedBytes += bytes;
    }
}
