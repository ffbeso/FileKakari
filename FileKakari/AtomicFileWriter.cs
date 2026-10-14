using System.IO;
using System.Text;

namespace FileKakari;

public static class AtomicFileWriter
{
    public static void WriteAllText(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                    writer.Write(content);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replacement keeps the previous file intact on write/replace failure.
            if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
            else File.Move(temporaryPath, fullPath);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
