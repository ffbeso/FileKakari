using System.Diagnostics;
using System.Windows.Threading;

namespace FileKakari;

public static class StagedFileListPublisher
{
    // Called on the UI dispatcher. Single-item notifications preserve realized rows
    // and selection; ListCollectionView does not accept range-add notifications.
    public static async Task<bool> PublishAsync(
        FileListState state, IReadOnlyList<FileEntry> items, string path, string stateId,
        Func<bool> isCurrent, Action configureView, Action<int, double, int> reportBatch,
        CancellationToken cancellationToken)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.VerifyAccess();
        var sizer = new AdaptiveBatchSizer();
        var offset = 0;
        var started = false;
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isCurrent()) return false;
                var timer = Stopwatch.StartNew();
                if (!started)
                {
                    state.IsPublishingItems = true;
                    using (state.ItemsView.DeferRefresh())
                    {
                        state.BeginStagedItems(path, stateId);
                        configureView();
                    }
                    started = true;
                }
                var count = Math.Min(sizer.NextSize, items.Count - offset);
                for (var index = 0; index < count; index++) state.AppendStagedItem(items[offset++]);
                timer.Stop();
                sizer.Observe(count, timer.Elapsed.TotalMilliseconds);
                reportBatch(offset, timer.Elapsed.TotalMilliseconds, sizer.NextSize);
                if (offset < items.Count)
                    await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background, cancellationToken);
            } while (offset < items.Count);
            cancellationToken.ThrowIfCancellationRequested();
            return isCurrent();
        }
        finally
        {
            if (isCurrent()) state.IsPublishingItems = false;
        }
    }
}
