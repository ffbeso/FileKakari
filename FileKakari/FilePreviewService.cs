using System.IO;
using System.Text;

namespace FileKakari;

public sealed class FilePreviewService
{
    public const long MaxTextBytes = 2L * 1024 * 1024;
    public const long MaxImageBytes = 32L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".xaml", ".cs", ".log", ".csv", ".html", ".htm"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4"
    };

    public async Task<FilePreviewResult> LoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var extension = Path.GetExtension(path);
            var kind = TextExtensions.Contains(extension)
                ? FilePreviewKind.Text
                : ImageExtensions.Contains(extension)
                    ? FilePreviewKind.Image
                    : VideoExtensions.Contains(extension)
                        ? FilePreviewKind.Video
                        : FilePreviewKind.Unsupported;

            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return new FilePreviewResult(FilePreviewStatus.Missing, kind);
            }

            var fileInfoResult = new FilePreviewInfo(
                fileInfo.Name,
                fileInfo.FullName,
                fileInfo.Extension,
                fileInfo.Length,
                fileInfo.LastWriteTime);

            if (kind == FilePreviewKind.Unsupported)
            {
                return new FilePreviewResult(FilePreviewStatus.Unsupported, kind, FileInfo: fileInfoResult);
            }

            if (kind == FilePreviewKind.Video)
            {
                return new FilePreviewResult(FilePreviewStatus.Success, kind, FileInfo: fileInfoResult);
            }

            var sizeLimit = kind == FilePreviewKind.Text ? MaxTextBytes : MaxImageBytes;
            if (fileInfo.Length > sizeLimit)
            {
                return new FilePreviewResult(FilePreviewStatus.TooLarge, kind, SizeLimit: sizeLimit);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var content = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);

            if (kind == FilePreviewKind.Image)
            {
                return new FilePreviewResult(FilePreviewStatus.Success, kind, ImageBytes: content);
            }

            using var contentStream = new MemoryStream(content, writable: false);
            using var reader = new StreamReader(contentStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return new FilePreviewResult(FilePreviewStatus.Success, kind, Text: text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.Unsupported, ErrorMessage: ex.Message);
        }
    }
}

public enum FilePreviewKind
{
    Unsupported,
    Text,
    Image,
    Video
}

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
    FilePreviewInfo? FileInfo = null);

public sealed record FilePreviewInfo(
    string FileName,
    string FullPath,
    string Extension,
    long Size,
    DateTime LastWriteTime);
