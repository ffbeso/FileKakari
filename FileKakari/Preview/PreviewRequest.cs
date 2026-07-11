namespace FileKakari;

public sealed record PreviewRequest(string FilePath)
{
    public byte[]? PreloadedContent { get; init; }
    public string? PreloadedEncoding { get; init; }
}
