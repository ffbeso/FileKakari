using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    public async Task<FilePreviewResult> LoadAsync(
        string path,
        double targetWidthDip,
        double targetHeightDip,
        double dpiScaleX,
        double dpiScaleY,
        int generation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var extension = Path.GetExtension(path);
        PreviewDiagnostics.Info("Preview", $"Preview request path=\"{path}\" ext=\"{extension}\"");

        var routing = await CreateRoutingContextAsync(
            path,
            extension,
            targetWidthDip,
            targetHeightDip,
            dpiScaleX,
            dpiScaleY,
            generation,
            cancellationToken).ConfigureAwait(false);

        if (!routing.ForceBuiltInText && ShouldTryShellFirst(path))
        {
            var shellResult = await TryLoadShellPreviewAsync(routing.Request, cancellationToken, stopwatch).ConfigureAwait(false);
            if (shellResult is not null)
            {
                return shellResult;
            }
        }

        var providerResult = await TryLoadFromProviderOrderAsync(
            path,
            extension,
            routing.Request,
            routing.ForceBuiltInText,
            cancellationToken,
            stopwatch).ConfigureAwait(false);
        if (providerResult is not null)
        {
            return providerResult;
        }

        return await CreateUnsupportedFallbackAsync(path, extension, stopwatch).ConfigureAwait(false);
    }

    private async Task<PreviewRoutingContext> CreateRoutingContextAsync(
        string path,
        string extension,
        double targetWidthDip,
        double targetHeightDip,
        double dpiScaleX,
        double dpiScaleY,
        int generation,
        CancellationToken cancellationToken)
    {
        var request = new PreviewRequest(path)
        {
            TargetWidthDip = targetWidthDip,
            TargetHeightDip = targetHeightDip,
            DpiScaleX = dpiScaleX,
            DpiScaleY = dpiScaleY,
            Generation = generation
        };
        var forceBuiltInText = false;
        if (!IsCmdBatExtension(extension))
        {
            return new PreviewRoutingContext(request, forceBuiltInText);
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return new PreviewRoutingContext(request, forceBuiltInText);
            }

            var readLen = (int)Math.Min(fileInfo.Length, 8192);
            var preloaded = await ReadEncodingProbeBytesAsync(path, readLen, fileInfo.Length, cancellationToken).ConfigureAwait(false);
            var encodingName = BuiltInTextPreviewProvider.DetectEncodingName(preloaded, path, extension);
            request = new PreviewRequest(path)
            {
                PreloadedContent = readLen == fileInfo.Length ? preloaded : null,
                PreloadedEncoding = encodingName,
                TargetWidthDip = targetWidthDip,
                TargetHeightDip = targetHeightDip,
                DpiScaleX = dpiScaleX,
                DpiScaleY = dpiScaleY,
                Generation = generation
            };

            forceBuiltInText = ShouldForceBuiltInTextForCmdBat(encodingName);
            LogCmdBatRouting(path, extension, encodingName, forceBuiltInText);
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewRouting", $"BAT/CMD encoding detection failed path=\"{path}\" ext=\"{extension}\" reason=\"{ex.Message}\"");
        }

        return new PreviewRoutingContext(request, forceBuiltInText);
    }

    private static async Task<byte[]> ReadEncodingProbeBytesAsync(
        string path,
        int readLen,
        long fileLength,
        CancellationToken cancellationToken)
    {
        var preloaded = new byte[readLen];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        if (readLen == fileLength)
        {
            await fs.ReadExactlyAsync(preloaded, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await fs.ReadExactlyAsync(preloaded, 0, readLen, cancellationToken).ConfigureAwait(false);
        }

        return preloaded;
    }

    private async Task<FilePreviewResult?> TryLoadFromProviderOrderAsync(
        string path,
        string extension,
        PreviewRequest request,
        bool forceBuiltInText,
        CancellationToken cancellationToken,
        Stopwatch stopwatch)
    {
        foreach (var provider in _providers)
        {
            if (forceBuiltInText && provider is not BuiltInTextPreviewProvider)
            {
                continue;
            }

            if (provider.CanPreview(path))
            {
                var result = await provider.CreatePreviewAsync(request, cancellationToken);
                if (IsMarkdown(path))
                {
                    PreviewDiagnostics.Info("PreviewRouting", $"Markdown provider selected provider=\"{provider.GetType().Name}\"");
                }

                stopwatch.Stop();
                PreviewDiagnostics.Info("Preview", $"Provider selected path=\"{path}\" ext=\"{extension}\" provider=\"{provider.GetType().Name}\" kind={result.Kind} status={result.Status} fallback=false elapsedMs={stopwatch.ElapsedMilliseconds}");
                return result;
            }
        }

        return null;
    }

    private async Task<FilePreviewResult> CreateUnsupportedFallbackAsync(
        string path,
        string extension,
        Stopwatch stopwatch)
    {
        // Fallback for unsupported formats (matches existing behavior of returning basic FileInfo)
        var fallbackResult = await CreateFallbackPreviewAsync(path);
        stopwatch.Stop();
        PreviewDiagnostics.Info("Preview", $"Unsupported path=\"{path}\" ext=\"{extension}\" kind={fallbackResult.Kind} status={fallbackResult.Status} fallback=true elapsedMs={stopwatch.ElapsedMilliseconds}");
        return fallbackResult;
    }

    private static void LogCmdBatRouting(
        string path,
        string extension,
        string encodingName,
        bool forceBuiltInText)
    {
        if (forceBuiltInText)
        {
            PreviewDiagnostics.Info("PreviewRouting", $"BAT/CMD routing path=\"{path}\" ext=\"{extension}\" encoding=\"{encodingName}\" provider=\"BuiltInTextPreviewProvider\" reason=\"unsupported-by-monaco\"");
            return;
        }

        PreviewDiagnostics.Info("PreviewRouting", $"BAT/CMD routing path=\"{path}\" ext=\"{extension}\" encoding=\"{encodingName}\" provider=\"ShellPreviewHandlerProvider\"");
    }

    private void LogProviderCandidates(string path, string extension)
    {
        foreach (var provider in _providers)
        {
            var canPreview = provider.CanPreview(path);
            PreviewDiagnostics.Verbose("Preview", $"Candidate provider path=\"{path}\" ext=\"{extension}\" provider=\"{provider.GetType().Name}\" canPreview={canPreview}");
        }
    }

    private async Task<FilePreviewResult?> TryLoadShellPreviewAsync(
        PreviewRequest request,
        CancellationToken cancellationToken,
        Stopwatch stopwatch)
    {
        var ext = Path.GetExtension(request.FilePath);
        if (IsMarkdown(request.FilePath))
        {
            PreviewDiagnostics.Info("PreviewRouting", "Markdown checking shell preview handler ext=\".md\"");
        }

        PreviewDiagnostics.Info("PreviewRouting", $"Shell-first lookup path=\"{request.FilePath}\" ext=\"{ext}\"");
        if (!ShellPreviewHandlerRegistry.TryGetPreviewHandler(request.FilePath, out var registration))
        {
            if (IsMarkdown(request.FilePath))
            {
                PreviewDiagnostics.Info("PreviewRouting", "Markdown shell handler not found; fallback=\"BuiltInTextPreviewProvider\"");
            }
            PreviewDiagnostics.Info("PreviewRouting", $"Shell-first handler not found path=\"{request.FilePath}\" ext=\"{ext}\"");
            return null;
        }

        if (IsMarkdown(request.FilePath))
        {
            PreviewDiagnostics.Info("PreviewRouting", $"Markdown shell handler found clsid=\"{registration.Clsid:B}\"");
        }

        PreviewDiagnostics.Info(
            "PreviewRouting",
            $"Shell-first handler found path=\"{request.FilePath}\" ext=\"{ext}\" clsid=\"{registration.Clsid:B}\" description=\"{registration.ClsidDescription ?? ""}\" sourceKind=\"{registration.SourceKind}\"");
        foreach (var provider in _providers)
        {
            if (provider is not ShellPreviewHandlerProvider)
            {
                continue;
            }

            var result = await provider.CreatePreviewAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Status == FilePreviewStatus.Success && result.Kind == FilePreviewKind.Shell)
            {
                if (IsMarkdown(request.FilePath))
                {
                    PreviewDiagnostics.Info("PreviewRouting", "Markdown selected provider=\"ShellPreviewHandlerProvider\"");
                }
                stopwatch.Stop();
                PreviewDiagnostics.Info("Preview", $"Provider selected path=\"{request.FilePath}\" ext=\"{ext}\" provider=\"{provider.GetType().Name}\" kind={result.Kind} status={result.Status} fallback=false elapsedMs={stopwatch.ElapsedMilliseconds}");
                return result;
            }

            if (IsMarkdown(request.FilePath))
            {
                PreviewDiagnostics.Info("PreviewRouting", $"Markdown shell provider returned status={result.Status} kind={result.Kind}; fallback=\"BuiltInTextPreviewProvider\"");
            }
            PreviewDiagnostics.Info("PreviewRouting", $"Shell-first provider returned status={result.Status} kind={result.Kind}; fallback=\"normal-provider-order\" path=\"{request.FilePath}\"");
            return null;
        }

        if (IsMarkdown(request.FilePath))
        {
            PreviewDiagnostics.Info("PreviewRouting", "Markdown shell provider unavailable; fallback=\"BuiltInTextPreviewProvider\"");
        }
        PreviewDiagnostics.Info("PreviewRouting", $"Shell-first provider unavailable; fallback=\"normal-provider-order\" path=\"{request.FilePath}\"");
        return null;
    }

    private bool ShouldTryShellFirst(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension) || IsBuiltInTextPreferred(extension))
        {
            return false;
        }

        foreach (var provider in _providers)
        {
            if (provider is BuiltInImagePreviewProvider or BuiltInVideoPreviewProvider or WebViewPreviewProvider
                && provider.CanPreview(path))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBuiltInTextPreferred(string extension)
    {
        return string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tsv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCmdBatExtension(string extension)
    {
        return string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldForceBuiltInTextForCmdBat(string encodingName)
    {
        return string.Equals(encodingName, "cp932", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encodingName, "utf-8-loose", StringComparison.OrdinalIgnoreCase);
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

    private sealed record PreviewRoutingContext(PreviewRequest Request, bool ForceBuiltInText);
}
