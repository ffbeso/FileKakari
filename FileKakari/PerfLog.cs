using System.Diagnostics;
using System.IO;

namespace FileKakari;

public static class PerfLog
{
    private static readonly object Gate = new();
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("FILEKAKARI_PERF_LOG");

    public static string? ConfiguredLogPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LogPath))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(LogPath);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
    }

    public static void Write(string message)
    {
        var line = $"[FileKakariPerf] {DateTime.Now:HH:mm:ss.fff} {message}";
        Debug.WriteLine(line);

        if (string.IsNullOrWhiteSpace(LogPath))
        {
            return;
        }

        lock (Gate)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
