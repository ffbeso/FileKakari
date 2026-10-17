namespace FileKakari;

// Coordinates are DIPs. Image dimensions are physical pixels divided by monitor DPI.
public sealed class ImageViewportTransform
{
    public double Scale { get; private set; } = 1;
    public double X { get; private set; }
    public double Y { get; private set; }
    public double ImageWidth { get; private set; } = 1;
    public double ImageHeight { get; private set; } = 1;
    public double ViewWidth { get; private set; } = 1;
    public double ViewHeight { get; private set; } = 1;
    public bool IsFit { get; private set; } = true;
    public double FitScale => Math.Min(1, Math.Min(ViewWidth / ImageWidth, ViewHeight / ImageHeight));

    public void SetGeometry(double imageWidth, double imageHeight, double viewWidth, double viewHeight)
    {
        var centerX = (ViewWidth / 2 - X) / (ImageWidth * Scale);
        var centerY = (ViewHeight / 2 - Y) / (ImageHeight * Scale);
        ImageWidth = Math.Max(1, imageWidth); ImageHeight = Math.Max(1, imageHeight);
        ViewWidth = Math.Max(1, viewWidth); ViewHeight = Math.Max(1, viewHeight);
        if (IsFit) Fit();
        else
        {
            X = ViewWidth / 2 - centerX * ImageWidth * Scale;
            Y = ViewHeight / 2 - centerY * ImageHeight * Scale;
            Clamp();
        }
    }

    public void Fit()
    {
        IsFit = true;
        Scale = FitScale;
        X = (ViewWidth - ImageWidth * Scale) / 2;
        Y = (ViewHeight - ImageHeight * Scale) / 2;
    }

    public void ZoomAt(double scale, double x, double y)
    {
        scale = Math.Clamp(scale, Math.Min(.01, FitScale), 8);
        var ratio = scale / Scale;
        X = x - (x - X) * ratio;
        Y = y - (y - Y) * ratio;
        Scale = scale;
        IsFit = false;
        Clamp();
    }

    public void Pan(double dx, double dy)
    {
        X += dx; Y += dy;
        Clamp();
    }

    private void Clamp()
    {
        var width = ImageWidth * Scale;
        var height = ImageHeight * Scale;
        X = width <= ViewWidth ? (ViewWidth - width) / 2 : Math.Clamp(X, ViewWidth - width, 0);
        Y = height <= ViewHeight ? (ViewHeight - height) / 2 : Math.Clamp(Y, ViewHeight - height, 0);
    }
}
