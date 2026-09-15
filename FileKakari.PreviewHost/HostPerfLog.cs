using System;
using System.Diagnostics;
using System.IO;

namespace FileKakari.PreviewHost;

public static class HostPerfLog
{
    private static readonly object Gate = new();
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("FILEKAKARI_PERF_LOG");

    public static void Write(string message)
    {
        var line = $"[FileKakariPerf] {DateTime.Now:HH:mm:ss.fff} [PreviewHost] {message}";
        Debug.WriteLine(line);

        if (string.IsNullOrWhiteSpace(LogPath))
        {
            return;
        }

        lock (Gate)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                    writer.WriteLine(line);
                    break;
                }
                catch (IOException)
                {
                    if (attempt == 9)
                    {
                        break;
                    }
                    System.Threading.Thread.Sleep(5);
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
