using System;

namespace FileKakari;

public static class PreviewDiagnostics
{
    public static bool IsVerboseEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("FILEKAKARI_PREVIEW_VERBOSE_LOG"), "1", StringComparison.Ordinal);

    public static void Info(string category, string message)
    {
        PerfLog.Write($"[{category}] {message}");
    }

    public static void Error(string category, string message)
    {
        PerfLog.Write($"[{category}] {message}");
    }

    public static void Verbose(string category, string message)
    {
        if (!IsVerboseEnabled)
        {
            return;
        }

        PerfLog.Write($"[{category}] {message}");
    }

    public static void LogTiming(
        string step,
        string requestId,
        int generation,
        string filePath,
        long elapsedMs = -1)
    {
        var threadId = Environment.CurrentManagedThreadId;
        var isUi = System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? false;
        var fileName = string.IsNullOrEmpty(filePath) ? "" : System.IO.Path.GetFileName(filePath);
        var ext = string.IsNullOrEmpty(filePath) ? "" : System.IO.Path.GetExtension(filePath);
        var slowTag = elapsedMs >= 100 ? " [SLOW >= 100ms]" : "";
        var elapsedText = elapsedMs >= 0 ? $" elapsedMs={elapsedMs}{slowTag}" : "";

        PerfLog.Write($"[PreviewTiming] step=\"{step}\" requestId=\"{requestId}\" gen={generation} ext=\"{ext}\" fileName=\"{fileName}\" threadId={threadId} isUiThread={isUi}{elapsedText}");
    }
}
