using System;

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
    byte[]? ImageBytes = null,
    long? SizeLimit = null,
    string? ErrorMessage = null,
    FilePreviewInfo? FileInfo = null,
    Guid? Clsid = null);

public sealed record FilePreviewInfo(
    string FileName,
    string FullPath,
    string Extension,
    long Size,
    DateTime LastWriteTime);
