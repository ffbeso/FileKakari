using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace FileKakari;

// Coalesce application follow-up work, not WPF's selection notifications.
// Scopes must remain synchronous so the final state is committed before navigation.
public static class SelectionUpdateBatch
{
    private static readonly ConditionalWeakTable<ListView, BatchState> States = new();

    public static IDisposable Begin(ListView listView)
    {
        listView.Dispatcher.VerifyAccess();
        var state = States.GetValue(listView, static _ => new BatchState());
        state.Depth++;
        return new Scope(state);
    }

    public static void RunOrDefer(ListView listView, Action update)
    {
        listView.Dispatcher.VerifyAccess();
        if (States.TryGetValue(listView, out var state) && state.Depth > 0)
        {
            state.Pending = update;
            return;
        }
        update();
    }

    private sealed class BatchState
    {
        public int Depth;
        public Action? Pending;
    }

    private sealed class Scope(BatchState state) : IDisposable
    {
        private BatchState? _state = state;

        public void Dispose()
        {
            var current = _state;
            if (current is null) return;
            _state = null;
            if (--current.Depth != 0) return;
            var update = current.Pending;
            current.Pending = null;
            update?.Invoke();
        }
    }
}
