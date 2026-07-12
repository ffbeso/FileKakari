using System;
using System.Windows.Media.Imaging;

namespace FileKakari;

public enum FilePreviewStatus
{
    Success,
    Unsupported,
    TooLarge,
    Missing,
    Failed
}

public sealed record FilePreviewResult(
    FilePreviewStatus Status,
    FilePreviewKind Kind,
    string? Text = null,
    BitmapSource? ImageSource = null,
    long? SizeLimit = null,
    string? ErrorMessage = null,
    FilePreviewInfo? FileInfo = null,
    Guid? Clsid = null,
    string? EncodingName = null);
public sealed record FilePreviewInfo(
    string FileName,
    string FullPath,
    string Extension,
    long Size,
    DateTime LastWriteTime);
