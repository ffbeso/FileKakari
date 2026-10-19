using System.Diagnostics;
using System.Windows.Media;

namespace FileKakari;

// Observes existing dispatcher/render work; never inserts a layout or a wait.
internal sealed class PaneLoadTiming : IDisposable
{
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private readonly string _context;
    private readonly List<string> _samples = new();
    private bool _waitingForRender;
    private bool _completed;

    private PaneLoadTiming(FolderPane pane, string trigger)
    {
        _context = $"pane={pane.Id} state={pane.ActiveTabState?.Id} trigger={trigger}";
    }

    public static PaneLoadTiming? Start(FolderPane pane, string trigger) =>
        PerfLog.IsTimingEnabled ? new PaneLoadTiming(pane, trigger) : null;

    public void Mark(string stage) => _samples.Add($"{stage}={_timer.Elapsed.TotalMilliseconds:F2}");

    public void ObserveNextRender()
    {
        if (_waitingForRender) return;
        _waitingForRender = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        Mark("renderOpportunity");
        StopObserving();
    }

    private void StopObserving()
    {
        if (!_waitingForRender) return;
        CompositionTarget.Rendering -= OnRendering;
        _waitingForRender = false;
    }

    public void Complete() => _completed = true;

    public void Dispose()
    {
        StopObserving();
        Mark("end");
        PerfLog.WriteTiming($"pane-load-timing {_context} completed={_completed} elapsedMs=[{string.Join(' ', _samples)}]");
    }
}
