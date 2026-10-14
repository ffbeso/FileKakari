namespace FileKakari;

public static class ImagePreviewSizing
{
    public const long MaxDecodedPixels = 8 * 1024 * 1024;

    public static (int Width, int Height) Fit(int width, int height, double targetWidth, double targetHeight)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        targetWidth = double.IsFinite(targetWidth) ? Math.Max(1, targetWidth) : 1;
        targetHeight = double.IsFinite(targetHeight) ? Math.Max(1, targetHeight) : 1;
        var scale = Math.Min(1, Math.Min(targetWidth / width, targetHeight / height));
        scale = Math.Min(scale, Math.Sqrt((double)MaxDecodedPixels / ((double)width * height)));
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }
}
