using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileKakari;

public sealed class WebViewPreviewProvider : IFilePreviewProvider
{
    private static readonly HashSet<string> WebViewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".mp4",
        ".webm",
        ".svg",
        ".html",
        ".htm",
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

            var result = new FilePreviewResult(FilePreviewStatus.Success, FilePreviewKind.WebView, FileInfo: fileInfoResult);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.WebView, ErrorMessage: ex.Message));
        }
    }
}
