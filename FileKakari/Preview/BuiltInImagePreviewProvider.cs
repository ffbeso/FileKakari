using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileKakari;

public sealed class BuiltInImagePreviewProvider : IFilePreviewProvider
{
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
        try
        {
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

            await using var stream = new FileStream(
                request.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var content = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);

            return new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.Image, ImageBytes: content, FileInfo: fileInfoResult);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.Image, ErrorMessage: ex.Message);
        }
    }
}
