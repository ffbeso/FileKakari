using System.Diagnostics;
using System.IO;

namespace FileKakari;

public static class PerfLog
{
    private static readonly object Gate = new();
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("FILEKAKARI_PERF_LOG");
    private static readonly string? TimingPath = Environment.GetEnvironmentVariable("FILEKAKARI_TIMING_LOG");
    private static readonly TimingLogBuffer? TimingBuffer = string.IsNullOrWhiteSpace(TimingPath) ? null : new();

    public static bool IsTimingEnabled => TimingBuffer is not null || IsVerboseEnabled;

    public static void WriteTiming(string message)
    {
        if (TimingBuffer is not null) TimingBuffer.Record(message);
        else WriteVerbose(message);
    }

    public static void FlushTiming()
    {
        if (TimingBuffer is null || string.IsNullOrWhiteSpace(TimingPath)) return;
        try { TimingBuffer.SaveSnapshot(TimingPath); }
        catch (Exception ex) { Debug.WriteLine($"Timing log save failed: {ex.Message}"); }
    }

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

    public static void WriteVerbose(string message)
    {
        if (!IsVerboseEnabled)
        {
            return;
        }

        Write(message);
    }

    public static bool IsVerboseEnabled =>
        Environment.GetEnvironmentVariable("FILEKAKARI_WORKSPACE_VERBOSE_LOG") == "1"
        || Environment.GetEnvironmentVariable("FILEKAKARI_DIAG_LOG") == "1";
}
