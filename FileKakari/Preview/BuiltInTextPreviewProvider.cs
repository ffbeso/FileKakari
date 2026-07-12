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
        ".txt", ".md", ".json", ".xml", ".xaml", ".cs", ".log", ".csv", ".tsv", ".cmd", ".bat", ".ini", ".ps1"
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

            byte[] content;
            if (request.PreloadedContent != null)
            {
                content = request.PreloadedContent;
            }
            else
            {
                await using var stream = new FileStream(
                    request.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 81920,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                content = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
            }

            if (content.Length == 0)
            {
                return new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.Text, Text: "", FileInfo: fileInfoResult);
            }

            TextDecodeResult? decodeResult = null;
            if (request.PreloadedEncoding != null)
            {
                Encoding targetEncoding;
                if (string.Equals(request.PreloadedEncoding, "utf-8-bom", StringComparison.OrdinalIgnoreCase))
                {
                    targetEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                }
                else if (string.Equals(request.PreloadedEncoding, "utf-16le-bom", StringComparison.OrdinalIgnoreCase))
                {
                    targetEncoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                }
                else if (string.Equals(request.PreloadedEncoding, "utf-16be-bom", StringComparison.OrdinalIgnoreCase))
                {
                    targetEncoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                }
                else if (string.Equals(request.PreloadedEncoding, "utf-32le-bom", StringComparison.OrdinalIgnoreCase))
                {
                    targetEncoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
                }
                else if (string.Equals(request.PreloadedEncoding, "utf-32be-bom", StringComparison.OrdinalIgnoreCase))
                {
                    targetEncoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
                }
                else if (string.Equals(request.PreloadedEncoding, "utf-8", StringComparison.OrdinalIgnoreCase))
                {
                    targetEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                }
                else if (string.Equals(request.PreloadedEncoding, "cp932", StringComparison.OrdinalIgnoreCase))
                {
                    targetEncoding = Encoding.GetEncoding(932);
                }
                else
                {
                    targetEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                }

                try
                {
                    var text = targetEncoding.GetString(content);
                    var (replacementCount, nulCount) = CountDecodeIndicators(text);
                    decodeResult = new TextDecodeResult(text, request.PreloadedEncoding, replacementCount, nulCount);
                }
                catch
                {
                }
            }

            if (decodeResult == null)
            {
                decodeResult = DecodeTextContent(content, request.FilePath, fileInfo.Extension);
            }

            if (decodeResult is null)
            {
                var loc = new LocalizationService();
                var errorTitle = loc.Get("PreviewBinaryFileError");
                if (string.IsNullOrEmpty(errorTitle))
                {
                    errorTitle = "This file cannot be previewed as text. It may contain binary data.";
                }
                return new FilePreviewResult(FilePreviewStatus.Unsupported, FilePreviewKind.Text, ErrorMessage: errorTitle, FileInfo: fileInfoResult);
            }

            return new FilePreviewResult(
                FilePreviewStatus.Success,
                FilePreviewKind.Text,
                Text: decodeResult.Text,
                FileInfo: fileInfoResult,
                EncodingName: decodeResult.EncodingName);
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

    private static TextDecodeResult? DecodeTextContent(byte[] content, string path, string extension)
    {
        PreviewDiagnostics.Verbose("PreviewText", $"Encoding probe path=\"{path}\" ext=\"{extension}\" bytes={content.Length}");

        if (TryGetBomEncoding(content, out var bomEncoding, out var bomEncodingName))
        {
            PreviewDiagnostics.Verbose("PreviewText", $"BOM detected path=\"{path}\" ext=\"{extension}\" encoding=\"{bomEncodingName}\"");
            return DecodeCandidate(content, bomEncoding, bomEncodingName, path, extension, allowNulText: true, fallback: false);
        }

        PreviewDiagnostics.Verbose("PreviewText", $"BOM detected path=\"{path}\" ext=\"{extension}\" encoding=\"none\"");

        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            var utf8Result = DecodeCandidate(content, utf8Strict, "utf-8", path, extension, allowNulText: false, fallback: false);
            if (utf8Result is not null)
            {
                PreviewDiagnostics.Verbose("PreviewText", $"Strict UTF-8 result path=\"{path}\" ext=\"{extension}\" status=\"success\"");
                return utf8Result;
            }

            PreviewDiagnostics.Verbose("PreviewText", $"Strict UTF-8 result path=\"{path}\" ext=\"{extension}\" status=\"rejected-nul-heavy\"");
        }
        catch (DecoderFallbackException ex)
        {
            PreviewDiagnostics.Verbose("PreviewText", $"Strict UTF-8 result path=\"{path}\" ext=\"{extension}\" status=\"failed\" reason=\"{ex.Message}\"");
        }

        if (TryGetBomlessUtf16Encoding(content, out var utf16Encoding, out var utf16EncodingName, out var oddNuls, out var evenNuls, out var totalPairs))
        {
            PreviewDiagnostics.Verbose("PreviewText", $"UTF-16 heuristic path=\"{path}\" ext=\"{extension}\" status=\"matched\" encoding=\"{utf16EncodingName}\" oddNuls={oddNuls} evenNuls={evenNuls} pairs={totalPairs}");
            var utf16Result = DecodeCandidate(content, utf16Encoding, utf16EncodingName, path, extension, allowNulText: true, fallback: false);
            if (utf16Result is not null)
            {
                return utf16Result;
            }
        }
        else
        {
            PreviewDiagnostics.Verbose("PreviewText", $"UTF-16 heuristic path=\"{path}\" ext=\"{extension}\" status=\"not-matched\" oddNuls={oddNuls} evenNuls={evenNuls} pairs={totalPairs}");
        }

        try
        {
            var cp932 = Encoding.GetEncoding(932);
            var cp932Result = DecodeCandidate(content, cp932, "cp932", path, extension, allowNulText: false, fallback: false);
            if (cp932Result is not null)
            {
                return cp932Result;
            }

            PreviewDiagnostics.Verbose("PreviewText", $"CP932 result path=\"{path}\" ext=\"{extension}\" status=\"rejected-nul-heavy\"");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            PreviewDiagnostics.Error("PreviewText", $"CP932 result path=\"{path}\" ext=\"{extension}\" status=\"failed\" reason=\"{ex.Message}\"");
        }

        var utf8Loose = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var looseResult = DecodeCandidate(content, utf8Loose, "utf-8-loose", path, extension, allowNulText: false, fallback: true);
        if (looseResult is not null)
        {
            return looseResult;
        }

        PreviewDiagnostics.Info("PreviewText", $"Unsupported path=\"{path}\" ext=\"{extension}\" reason=\"binary-or-nul-heavy\"");
        return null;
    }

    private static TextDecodeResult? DecodeCandidate(
        byte[] content,
        Encoding encoding,
        string encodingName,
        string path,
        string extension,
        bool allowNulText,
        bool fallback)
    {
        var text = encoding.GetString(content);
        var (replacementChars, nulChars) = CountDecodeIndicators(text);
        if (!allowNulText && IsNulHeavy(text.Length, nulChars))
        {
            PreviewDiagnostics.Verbose("PreviewText", $"Encoding candidate rejected path=\"{path}\" ext=\"{extension}\" encoding=\"{encodingName}\" reason=\"nul-heavy\" replacementChars={replacementChars} nulChars={nulChars}");
            return null;
        }

        var logKind = fallback ? "Encoding fallback" : "Encoding detected";
        PreviewDiagnostics.Info("PreviewText", $"{logKind} path=\"{path}\" ext=\"{extension}\" encoding=\"{encodingName}\" replacementChars={replacementChars} nulChars={nulChars}");
        return new TextDecodeResult(text, encodingName, replacementChars, nulChars);
    }

    private static bool TryGetBomEncoding(byte[] content, out Encoding encoding, out string encodingName)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            encodingName = "utf-8-bom";
            return true;
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            if (content.Length >= 4 && content[2] == 0x00 && content[3] == 0x00)
            {
                encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
                encodingName = "utf-32le-bom";
                return true;
            }

            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            encodingName = "utf-16le-bom";
            return true;
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
            encodingName = "utf-16be-bom";
            return true;
        }

        if (content.Length >= 4 && content[0] == 0x00 && content[1] == 0x00 && content[2] == 0xFE && content[3] == 0xFF)
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
            encodingName = "utf-32be-bom";
            return true;
        }

        encoding = Encoding.UTF8;
        encodingName = "";
        return false;
    }

    private static bool TryGetBomlessUtf16Encoding(
        byte[] content,
        out Encoding encoding,
        out string encodingName,
        out int oddNuls,
        out int evenNuls,
        out int totalPairs)
    {
        oddNuls = 0;
        evenNuls = 0;
        var scanLen = Math.Min(content.Length, 1024);
        scanLen = (scanLen / 2) * 2;
        totalPairs = scanLen / 2;

        for (var i = 0; i < totalPairs; i++)
        {
            if (content[i * 2] == 0)
            {
                evenNuls++;
            }

            if (content[i * 2 + 1] == 0)
            {
                oddNuls++;
            }
        }

        if (totalPairs >= 2 && oddNuls > 0 && evenNuls == 0 && oddNuls >= totalPairs * 0.7)
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            encodingName = "utf-16le-heuristic";
            return true;
        }

        if (totalPairs >= 2 && evenNuls > 0 && oddNuls == 0 && evenNuls >= totalPairs * 0.7)
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
            encodingName = "utf-16be-heuristic";
            return true;
        }

        encoding = Encoding.UTF8;
        encodingName = "";
        return false;
    }

    private static bool IsNulHeavy(int textLength, int nulChars)
    {
        return nulChars > 0 && (nulChars > 16 || nulChars >= textLength * 0.1);
    }

    private static (int ReplacementCount, int NulCount) CountDecodeIndicators(string text)
    {
        var replacementCount = 0;
        var nulCount = 0;

        foreach (var ch in text)
        {
            if (ch == '\uFFFD')
            {
                replacementCount++;
            }
            else if (ch == '\0')
            {
                nulCount++;
            }
        }

        return (replacementCount, nulCount);
    }

    private sealed record TextDecodeResult(string Text, string EncodingName, int ReplacementChars, int NulChars);

    public static string DetectEncodingName(byte[] content, string path, string extension)
    {
        if (TryGetBomEncoding(content, out _, out var bomEncodingName))
        {
            return bomEncodingName;
        }

        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            var text = utf8Strict.GetString(content);
            var (replacementChars, nulChars) = CountDecodeIndicators(text);
            if (replacementChars == 0 && !IsNulHeavy(text.Length, nulChars))
            {
                return "utf-8";
            }
        }
        catch (DecoderFallbackException)
        {
        }

        if (TryGetBomlessUtf16Encoding(content, out _, out var utf16EncodingName, out _, out _, out _))
        {
            return utf16EncodingName;
        }

        try
        {
            var cp932 = Encoding.GetEncoding(932);
            var text = cp932.GetString(content);
            var (replacementChars, nulChars) = CountDecodeIndicators(text);
            if (replacementChars == 0 && !IsNulHeavy(text.Length, nulChars))
            {
                return "cp932";
            }
        }
        catch (Exception)
        {
        }

        return "utf-8-loose";
    }
}
