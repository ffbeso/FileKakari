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
        PreviewDiagnostics.Info(
            "PreviewShell",
            $"ShellPreviewHandlerProvider.CreatePreviewAsync start requestId=\"{request.RequestId}\" source=\"{request.Source}\" path=\"{request.FilePath}\" generation={request.Generation}");
        try
        {
            var fileInfo = new FileInfo(request.FilePath);
            if (!fileInfo.Exists)
            {
                PreviewDiagnostics.Info("PreviewShell", $"ShellPreviewHandlerProvider.CreatePreviewAsync end requestId=\"{request.RequestId}\" path=\"{request.FilePath}\" status=Missing kind=Shell");
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
                    $"Handler resolved requestId=\"{request.RequestId}\" path=\"{request.FilePath}\" ext=\"{fileInfoResult.Extension}\" clsid=\"{registration.Clsid:B}\" sourceKind=\"{registration.SourceKind}\" description=\"{registration.ClsidDescription ?? ""}\"");
                PreviewDiagnostics.Verbose(
                    "PreviewShell",
                    $"Handler registry details requestId=\"{request.RequestId}\" path=\"{request.FilePath}\" progId=\"{registration.ProgId ?? ""}\" perceivedType=\"{registration.PerceivedType ?? ""}\" contentType=\"{registration.ContentType ?? ""}\" source=\"{registration.Hive}\\{registration.RegistryPath}\"");
                PreviewDiagnostics.Info("PreviewShell", $"ShellPreviewHandlerProvider.CreatePreviewAsync end requestId=\"{request.RequestId}\" path=\"{request.FilePath}\" status=Success kind=Shell clsid=\"{registration.Clsid:B}\"");
                return Task.FromResult(new FilePreviewResult(
                    FilePreviewStatus.Success,
                    FilePreviewKind.Shell,
                    FileInfo: fileInfoResult,
                    Clsid: registration.Clsid));
            }

            PreviewDiagnostics.Info("PreviewShell", $"ShellPreviewHandlerProvider.CreatePreviewAsync end requestId=\"{request.RequestId}\" path=\"{request.FilePath}\" status=Unsupported kind=Unsupported");
            return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Unsupported, FilePreviewKind.Unsupported, FileInfo: fileInfoResult));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            PreviewDiagnostics.Error("PreviewShell", $"ShellPreviewHandlerProvider.CreatePreviewAsync end requestId=\"{request.RequestId}\" path=\"{request.FilePath}\" status=Failed kind=Unsupported reason=\"{ex.Message}\"");
            return Task.FromResult(new FilePreviewResult(FilePreviewStatus.Failed, FilePreviewKind.Unsupported, ErrorMessage: ex.Message));
        }
    }
}
