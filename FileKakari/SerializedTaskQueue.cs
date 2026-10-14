namespace FileKakari;

/// <summary>Runs snapshots off the caller thread, in request order, even after a failed save.</summary>
public sealed class SerializedTaskQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public Task<T> Enqueue<T>(Func<T> work)
    {
        lock (_gate)
        {
            var previous = _tail;
            var next = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch { /* A failed write must not prevent the next snapshot from being saved. */ }
                return work();
            });
            _tail = next;
            return next;
        }
    }

    public Task Completion { get { lock (_gate) return _tail; } }
}
