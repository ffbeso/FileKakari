using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileKakari;

public sealed class FilePreviewController
{
    private readonly List<IFilePreviewProvider> _providers;

    public FilePreviewController(IEnumerable<IFilePreviewProvider> providers)
    {
        _providers = new List<IFilePreviewProvider>(providers);
    }

    public async Task<FilePreviewResult> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var request = new PreviewRequest(path);
        if (IsMarkdown(path))
        {
            var markdownResult = await TryLoadMarkdownPreviewAsync(request, cancellationToken).ConfigureAwait(false);
            if (markdownResult is not null)
            {
                return markdownResult;
            }
        }

        foreach (var provider in _providers)
        {
            if (provider.CanPreview(path))
            {
                var result = await provider.CreatePreviewAsync(request, cancellationToken);
                if (IsMarkdown(path))
                {
                    PerfLog.Write($"[MarkdownPreview] selected provider=\"{provider.GetType().Name}\"");
                }

                PerfLog.Write($"[FilePreviewController] Provider selected: {provider.GetType().Name} path=\"{path}\" kind={result.Kind} status={result.Status}");
                return result;
            }
        }

        // Fallback for unsupported formats (matches existing behavior of returning basic FileInfo)
        var fallbackResult = await CreateFallbackPreviewAsync(path);
        PerfLog.Write($"[FilePreviewController] Fallback selected path=\"{path}\" kind={fallbackResult.Kind} status={fallbackResult.Status}");
        return fallbackResult;
    }

    private async Task<FilePreviewResult?> TryLoadMarkdownPreviewAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        PerfLog.Write("[MarkdownPreview] ext=\".md\" checking shell preview handler");
        if (!ShellPreviewHandlerRegistry.TryGetPreviewHandlerClsid(request.FilePath, out var clsid))
        {
            PerfLog.Write("[MarkdownPreview] shell handler not found; fallback to built-in text");
            return null;
        }

        PerfLog.Write($"[MarkdownPreview] shell handler found clsid=\"{clsid:B}\"");
        foreach (var provider in _providers)
        {
            if (provider is not ShellPreviewHandlerProvider)
            {
                continue;
            }

            var result = await provider.CreatePreviewAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Status == FilePreviewStatus.Success && result.Kind == FilePreviewKind.Shell)
            {
                PerfLog.Write("[MarkdownPreview] selected provider=\"ShellPreviewHandlerProvider\"");
                PerfLog.Write($"[FilePreviewController] Provider selected: {provider.GetType().Name} path=\"{request.FilePath}\" kind={result.Kind} status={result.Status}");
                return result;
            }

            PerfLog.Write($"[MarkdownPreview] shell provider returned status={result.Status} kind={result.Kind}; fallback to built-in text");
            return null;
        }

        PerfLog.Write("[MarkdownPreview] shell provider unavailable; fallback to built-in text");
        return null;
    }

    private static bool IsMarkdown(string path)
    {
        return string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase);
    }

    private Task<FilePreviewResult> CreateFallbackPreviewAsync(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Missing, FilePreviewKind.Unsupported));
            }

            var fileInfoResult = new FilePreviewInfo(
                fileInfo.Name,
                fileInfo.FullName,
                fileInfo.Extension,
                fileInfo.Length,
                fileInfo.LastWriteTime);

            return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Unsupported, FilePreviewKind.Unsupported, FileInfo: fileInfoResult));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.Unsupported, ErrorMessage: ex.Message));
        }
    }
}
