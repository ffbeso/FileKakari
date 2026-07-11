using System.IO;
using System.Text;

namespace FileKakari;

public static class PreviewTemporaryFileManager
{
    private const string MediaPreviewFilePrefix = "FileKakari_media_preview";

    public static string CreateMediaPreviewHtmlPath(int generation)
    {
        var tempFileName = $"{MediaPreviewFilePrefix}_{Environment.ProcessId}_{generation}_{Guid.NewGuid():N}.html";
        return Path.Combine(Path.GetTempPath(), tempFileName);
    }

    public static Task WriteMediaPreviewHtmlAsync(string path, string html)
    {
        return File.WriteAllTextAsync(path, html, Encoding.UTF8);
    }

    public static bool IsMediaPreviewHtmlUri(string uri)
    {
        return uri.Contains(MediaPreviewFilePrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static void DeleteMediaPreviewHtml(string path, int generation, string reason)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
        PreviewDiagnostics.Verbose(
            "PreviewMedia",
            $"Temporary media HTML deleted path=\"{path}\" generation={generation} reason=\"{reason}\"");
    }

    public static void CleanupCurrentProcessMediaPreviewFiles()
    {
        var tempDir = Path.GetTempPath();
        var pattern = $"{MediaPreviewFilePrefix}_{Environment.ProcessId}_*.html";
        foreach (var filePath in Directory.EnumerateFiles(tempDir, pattern))
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                PreviewDiagnostics.Error(
                    "PreviewMedia",
                    $"Temporary media HTML cleanup failed path=\"{filePath}\" reason=\"{ex.Message}\"");
            }
        }
    }
}
