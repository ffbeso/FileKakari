using System.Threading;
using System.Threading.Tasks;

namespace FileKakari;

public interface IFilePreviewProvider
{
    bool CanPreview(string filePath);
    Task<FilePreviewResult> CreatePreviewAsync(PreviewRequest request, CancellationToken cancellationToken);
}
