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
}
