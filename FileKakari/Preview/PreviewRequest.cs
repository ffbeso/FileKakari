namespace FileKakari;

public sealed record PreviewRequest(string FilePath)
{
    public string RequestId { get; init; } = "";
    public string Source { get; init; } = "";
    public byte[]? PreloadedContent { get; init; }
    public string? PreloadedEncoding { get; init; }
    public double TargetWidthDip { get; init; }
    public double TargetHeightDip { get; init; }
    public double DpiScaleX { get; init; } = 1.0;
    public double DpiScaleY { get; init; } = 1.0;
    public int Generation { get; init; }
}
