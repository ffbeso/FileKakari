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
        ".txt", ".md", ".json", ".xml", ".xaml", ".cs", ".log", ".csv", ".tsv"
    };

    private static readonly HashSet<string> CsvExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv"
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static BuiltInTextPreviewProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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

            var extension = fileInfo.Extension;
            var text = CsvExtensions.Contains(extension)
                ? ReadCsvText(content, request.FilePath, extension)
                : await ReadUtf8TextAsync(content, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string> ReadUtf8TextAsync(byte[] content, CancellationToken cancellationToken)
    {
        using var contentStream = new MemoryStream(content, writable: false);
        using var reader = new StreamReader(contentStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReadCsvText(byte[] content, string path, string extension)
    {
        var encoding = DetectCsvEncoding(content, path, extension);
        using var contentStream = new MemoryStream(content, writable: false);
        using var reader = new StreamReader(contentStream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Encoding DetectCsvEncoding(byte[] content, string path, string extension)
    {
        if (content.Length >= 3
            && content[0] == 0xEF
            && content[1] == 0xBB
            && content[2] == 0xBF)
        {
            LogCsvEncoding(path, extension, "utf-8-bom");
            return Encoding.UTF8;
        }

        if (content.Length >= 2)
        {
            if (content[0] == 0xFF && content[1] == 0xFE)
            {
                LogCsvEncoding(path, extension, "utf-16-le-bom");
                return Encoding.Unicode;
            }

            if (content[0] == 0xFE && content[1] == 0xFF)
            {
                LogCsvEncoding(path, extension, "utf-16-be-bom");
                return Encoding.BigEndianUnicode;
            }
        }

        try
        {
            _ = StrictUtf8.GetString(content);
            LogCsvEncoding(path, extension, "utf-8");
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException ex)
        {
            try
            {
                var cp932 = Encoding.GetEncoding(932);
                LogCsvEncoding(path, extension, "cp932");
                return cp932;
            }
            catch (Exception fallbackEx) when (fallbackEx is ArgumentException or NotSupportedException)
            {
                PerfLog.Write($"[BuiltInTextPreviewProvider] CSV encoding fallback path=\"{path}\" ext=\"{extension}\" reason=\"cp932-unavailable after utf8-invalid: {ex.Message}; {fallbackEx.Message}\"");
                return Encoding.UTF8;
            }
        }
    }

    private static void LogCsvEncoding(string path, string extension, string encoding)
    {
        PerfLog.Write($"[BuiltInTextPreviewProvider] CSV encoding detected path=\"{path}\" ext=\"{extension}\" encoding=\"{encoding}\"");
    }
}
