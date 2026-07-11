using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileKakari;

public sealed class BuiltInTextPreviewProvider : IFilePreviewProvider
{
    public const long MaxTextBytes = 2L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".xaml", ".cs", ".log", ".csv"
    };

    public bool CanPreview(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return TextExtensions.Contains(extension);
    }

    public async Task<FilePreviewResult> CreatePreviewAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(request.FilePath);
            if (!fileInfo.Exists)
            {
                return new FilePreviewResult(FilePreviewStatus.Missing, FilePreviewKind.Text);
            }

            var fileInfoResult = new FilePreviewInfo(
                fileInfo.Name,
                fileInfo.FullName,
                fileInfo.Extension,
                fileInfo.Length,
                fileInfo.LastWriteTime);

            if (fileInfo.Length > MaxTextBytes)
            {
                return new FilePreviewResult(FilePreviewStatus.TooLarge, FilePreviewKind.Text, SizeLimit: MaxTextBytes, FileInfo: fileInfoResult);
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

            using var contentStream = new MemoryStream(content, writable: false);
            using var reader = new StreamReader(contentStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.Text, Text: text, FileInfo: fileInfoResult);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.Text, ErrorMessage: ex.Message);
        }
    }
}
