using System.Windows.Media;

namespace FileKakari;

/// <summary>Bounded LRU icon storage with independently cancellable shared requests.</summary>
public sealed class ShellIconCache(int capacity = 512)
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Icon)>> _cached = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, ImageSource Icon)> _recent = new();
    private readonly Dictionary<string, Request> _pending = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ImageSource?> GetAsync(string key, Func<CancellationToken, Task<ImageSource?>> load, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Request request;
        bool start;
        lock (_gate)
        {
            if (_cached.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Icon;
            }
            start = !_pending.TryGetValue(key, out request!);
            if (start) _pending.Add(key, request = new Request());
            request.Waiters++;
        }

        if (start) _ = LoadAsync(key, request, load);
        try
        {
            return await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (--request.Waiters == 0)
                {
                    if (request.Finished) request.Cancellation.Dispose();
                    else
                    {
                        RemovePending(key, request);
                        // Cancel queued work only when nobody still needs it.
                        request.Cancellation.Cancel();
                    }
                }
            }
        }
    }

    private async Task LoadAsync(string key, Request request, Func<CancellationToken, Task<ImageSource?>> load)
    {
        try
        {
            var icon = await load(request.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (icon is not null && !request.Cancellation.IsCancellationRequested)
                {
                    var node = _recent.AddFirst((key, icon));
                    _cached.Add(key, node);
                    if (_cached.Count > _capacity)
                    {
                        var oldest = _recent.Last!;
                        _cached.Remove(oldest.Value.Key);
                        _recent.RemoveLast();
                    }
                }
                RemovePending(key, request);
            }
            request.Completion.TrySetResult(icon);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) RemovePending(key, request);
            request.Completion.TrySetCanceled();
        }
        catch (Exception error)
        {
            lock (_gate) RemovePending(key, request);
            request.Completion.TrySetException(error);
            // A native request can fail after every caller has left.
            _ = request.Completion.Task.Exception;
        }
        finally
        {
            lock (_gate)
            {
                RemovePending(key, request);
                request.Finished = true;
                if (request.Waiters == 0) request.Cancellation.Dispose();
            }
        }
    }

    private void RemovePending(string key, Request request)
    {
        if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, request))
            _pending.Remove(key);
    }

    private sealed class Request
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<ImageSource?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters;
        public bool Finished;
    }
}
