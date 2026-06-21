namespace FileKakari;

public sealed class PerformanceLogger
{
    public bool IsEnabled => PerfLog.ConfiguredLogPath != null;

    public void Write(string message)
    {
        PerfLog.Write(message);
    }
}
