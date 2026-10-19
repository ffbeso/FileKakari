using System.IO;

namespace FileKakari;

public sealed class TimingLogBuffer
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private long _dropped;

    public TimingLogBuffer(int capacity = 4096)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Record(string message)
    {
        lock (_gate)
        {
            if (_lines.Count == _capacity)
            {
                _lines.Dequeue();
                _dropped++;
            }
            _lines.Enqueue($"{DateTimeOffset.Now:O} {message}");
        }
    }

    public void SaveSnapshot(string path)
    {
        string[] lines;
        lock (_gate)
        {
            lines = new[] { $"timing-buffer pid={Environment.ProcessId} retained={_lines.Count} dropped={_dropped}" }
                .Concat(_lines).ToArray();
        }
        // Called at normal exit, never on the interactive recording path.
        File.WriteAllLines(path, lines);
    }
}
