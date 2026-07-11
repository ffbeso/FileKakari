using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileKakari;

public sealed class WebViewPreviewProvider : IFilePreviewProvider
{
    private const long MaxHtmlPreviewBytes = 10L * 1024 * 1024; // 10 MiB
    private const long MaxSvgPreviewBytes = 5L * 1024 * 1024;   // 5 MiB

    private static readonly HashSet<string> WebViewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".mp4",
        ".webm",
        ".svg",
        ".html",
        ".htm",
        ".mht",
        ".mhtml",
        ".mp3",
        ".wav",
        ".m4a"
    };

    public bool CanPreview(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath);
        return WebViewExtensions.Contains(ext);
    }

    public Task<FilePreviewResult> CreatePreviewAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(request.FilePath);
            if (!fileInfo.Exists)
            {
                return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Missing, FilePreviewKind.WebView));
            }

            var fileInfoResult = new FilePreviewInfo(
                fileInfo.Name,
                fileInfo.FullName,
                fileInfo.Extension,
                fileInfo.Length,
                fileInfo.LastWriteTime);

            var ext = fileInfoResult.Extension;
            if (string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase))
            {
                if (fileInfo.Length > MaxHtmlPreviewBytes)
                {
                    PerfLog.Write($"[WebViewPreviewProvider] Rejected: html size exceeds limit path=\"{request.FilePath}\" size={fileInfo.Length} limit={MaxHtmlPreviewBytes}");
                    return Task.FromResult(new FilePreviewResult(FilePreviewStatus.TooLarge, FilePreviewKind.WebView, SizeLimit: MaxHtmlPreviewBytes, FileInfo: fileInfoResult));
                }
                else
                {
                    PerfLog.Write($"[WebViewPreviewProvider] Accepted: html size within limit path=\"{request.FilePath}\" size={fileInfo.Length} limit={MaxHtmlPreviewBytes}");
                }
            }
            else if (string.Equals(ext, ".mht", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ext, ".mhtml", StringComparison.OrdinalIgnoreCase))
            {
                if (fileInfo.Length > MaxHtmlPreviewBytes)
                {
                    PerfLog.Write($"[WebViewPreviewProvider] Rejected: mhtml size exceeds limit path=\"{request.FilePath}\" size={fileInfo.Length} limit={MaxHtmlPreviewBytes}");
                    return Task.FromResult(new FilePreviewResult(FilePreviewStatus.TooLarge, FilePreviewKind.WebView, SizeLimit: MaxHtmlPreviewBytes, FileInfo: fileInfoResult));
                }
                else
                {
                    PerfLog.Write($"[WebViewPreviewProvider] Accepted: mhtml size within limit path=\"{request.FilePath}\" size={fileInfo.Length} limit={MaxHtmlPreviewBytes}");
                }
            }
            else if (string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase))
            {
                if (fileInfo.Length > MaxSvgPreviewBytes)
                {
                    PerfLog.Write($"[WebViewPreviewProvider] Rejected: svg size exceeds limit path=\"{request.FilePath}\" size={fileInfo.Length} limit={MaxSvgPreviewBytes}");
                    return Task.FromResult(new FilePreviewResult(FilePreviewStatus.TooLarge, FilePreviewKind.WebView, SizeLimit: MaxSvgPreviewBytes, FileInfo: fileInfoResult));
                }
                else
                {
                    PerfLog.Write($"[WebViewPreviewProvider] Accepted: svg size within limit path=\"{request.FilePath}\" size={fileInfo.Length} limit={MaxSvgPreviewBytes}");
                }
            }

            var result = new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.WebView, FileInfo: fileInfoResult);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.WebView, ErrorMessage: ex.Message));
        }
    }
}
