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

            if (content.Length == 0)
            {
                return new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.Text, Text: "", FileInfo: fileInfoResult);
            }

            // 1. BOM Detection
            Encoding? encoding = null;
            string encodingName = "utf-8";

            if (content.Length >= 4 && content[0] == 0x00 && content[1] == 0x00 && content[2] == 0xFE && content[3] == 0xFF)
            {
                encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
                encodingName = "utf-32be";
            }
            else if (content.Length >= 4 && content[0] == 0xFF && content[1] == 0xFE && content[2] == 0x00 && content[3] == 0x00)
            {
                encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
                encodingName = "utf-32le";
            }
            else if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                encodingName = "utf-8-bom";
            }
            else if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                encodingName = "utf-16le-bom";
            }
            else if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
            {
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                encodingName = "utf-16be-bom";
            }

            // 2. BOM-less UTF-16 heuristics
            if (encoding == null)
            {
                int scanLen = Math.Min(content.Length, 1024);
                scanLen = (scanLen / 2) * 2; // Make it even
                if (scanLen >= 4)
                {
                    int oddNuls = 0;
                    int evenNuls = 0;
                    int totalChars = scanLen / 2;

                    for (int i = 0; i < totalChars; i++)
                    {
                        byte bEven = content[i * 2];
                        byte bOdd = content[i * 2 + 1];
                        if (bEven == 0) evenNuls++;
                        if (bOdd == 0) oddNuls++;
                    }

                    if (oddNuls > 0 && evenNuls == 0 && oddNuls >= totalChars * 0.7)
                    {
                        encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
                        encodingName = "utf-16le-heuristic";
                    }
                    else if (evenNuls > 0 && oddNuls == 0 && evenNuls >= totalChars * 0.7)
                    {
                        encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
                        encodingName = "utf-16be-heuristic";
                    }
                }
            }

            // 3. Binary detection (NUL check) for non-UTF16/32
            if (encoding == null)
            {
                int scanLen = Math.Min(content.Length, 1024);
                bool hasNul = false;
                for (int i = 0; i < scanLen; i++)
                {
                    if (content[i] == 0)
                    {
                        hasNul = true;
                        break;
                    }
                }

                if (hasNul)
                {
                    PerfLog.Write($"[BuiltInTextPreviewProvider] Text preview rejected path=\"{request.FilePath}\" reason=\"binary-or-nul-heavy\"");
                    var loc = new LocalizationService();
                    var errorTitle = loc.Get("PreviewBinaryFileError");
                    if (string.IsNullOrEmpty(errorTitle))
                    {
                        errorTitle = "This file cannot be previewed as text. It may contain binary data.";
                    }
                    return new FilePreviewResult(FilePreviewStatus.Unsupported, FilePreviewKind.Text, ErrorMessage: errorTitle, FileInfo: fileInfoResult);
                }
            }

            // 4. Strict decoding or CP932 resolution
            string textResult;
            if (encoding != null)
            {
                PerfLog.Write($"[BuiltInTextPreviewProvider] Encoding detected path=\"{request.FilePath}\" ext=\"{fileInfo.Extension}\" encoding=\"{encodingName}\"");
                textResult = encoding.GetString(content);
            }
            else
            {
                // Strict UTF-8 decoding attempt
                bool isCsvOrTsv = string.Equals(fileInfo.Extension, ".csv", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(fileInfo.Extension, ".tsv", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                    textResult = utf8Strict.GetString(content);
                    PerfLog.Write($"[BuiltInTextPreviewProvider] Encoding detected path=\"{request.FilePath}\" ext=\"{fileInfo.Extension}\" encoding=\"utf-8\"");
                }
                catch (ArgumentException)
                {
                    // Failed strict UTF-8 decoding
                    if (isCsvOrTsv)
                    {
                        try
                        {
                            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                            var cp932 = System.Text.Encoding.GetEncoding(932);
                            textResult = cp932.GetString(content);
                            PerfLog.Write($"[BuiltInTextPreviewProvider] Encoding detected path=\"{request.FilePath}\" ext=\"{fileInfo.Extension}\" encoding=\"cp932\"");
                        }
                        catch (Exception ex)
                        {
                            PerfLog.Write($"[BuiltInTextPreviewProvider] CP932 decoding failed path=\"{request.FilePath}\": {ex.Message}");
                            var utf8Loose = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                            textResult = utf8Loose.GetString(content);
                            PerfLog.Write($"[BuiltInTextPreviewProvider] Encoding fallback path=\"{request.FilePath}\" ext=\"{fileInfo.Extension}\" encoding=\"utf-8-loose\"");
                        }
                    }
                    else
                    {
                        // Loose UTF-8 fallback for general text files
                        var utf8Loose = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                        textResult = utf8Loose.GetString(content);
                        PerfLog.Write($"[BuiltInTextPreviewProvider] Encoding fallback path=\"{request.FilePath}\" ext=\"{fileInfo.Extension}\" encoding=\"utf-8-loose\"");
                    }
                }
            }

            return new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.Text, Text: textResult, FileInfo: fileInfoResult);
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
