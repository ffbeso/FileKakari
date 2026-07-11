using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileKakari;

public sealed class ShellPreviewHandlerProvider : IFilePreviewProvider
{
    public bool CanPreview(string filePath)
    {
        return ShellPreviewHandlerRegistry.TryGetPreviewHandlerClsid(filePath, out _);
    }

    public Task<FilePreviewResult> CreatePreviewAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(request.FilePath);
            if (!fileInfo.Exists)
            {
                return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Missing, FilePreviewKind.Shell));
            }

            var fileInfoResult = new FilePreviewInfo(
                fileInfo.Name,
                fileInfo.FullName,
                fileInfo.Extension,
                fileInfo.Length,
                fileInfo.LastWriteTime);

            if (ShellPreviewHandlerRegistry.TryGetPreviewHandler(request.FilePath, out var registration))
            {
                PreviewDiagnostics.Info(
                    "PreviewShell",
                    $"Handler resolved path=\"{request.FilePath}\" ext=\"{fileInfoResult.Extension}\" clsid=\"{registration.Clsid:B}\" sourceKind=\"{registration.SourceKind}\" description=\"{registration.ClsidDescription ?? ""}\"");
                PreviewDiagnostics.Verbose(
                    "PreviewShell",
                    $"Handler registry details path=\"{request.FilePath}\" progId=\"{registration.ProgId ?? ""}\" perceivedType=\"{registration.PerceivedType ?? ""}\" contentType=\"{registration.ContentType ?? ""}\" source=\"{registration.Hive}\\{registration.RegistryPath}\"");
                return Task.FromResult(new FilePreviewResult(
                    FilePreviewStatus.Success,
                    FilePreviewKind.Shell,
                    FileInfo: fileInfoResult,
                    Clsid: registration.Clsid));
            }

            return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Unsupported, FilePreviewKind.Unsupported, FileInfo: fileInfoResult));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.Unsupported, ErrorMessage: ex.Message));
        }
    }
}
