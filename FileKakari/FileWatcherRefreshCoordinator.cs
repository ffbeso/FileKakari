namespace FileKakari;

internal sealed class FileWatcherRefreshCoordinator
{
    private static readonly TimeSpan SelfOperationSuppressDuration = TimeSpan.FromMilliseconds(1500);
    private bool _refreshRunning;
    private DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;

    public TimeSpan SuppressDuration => SelfOperationSuppressDuration;

    public DateTimeOffset SuppressUntil => _suppressUntil;

    public bool IsRefreshRunning => _refreshRunning;

    public bool SuppressRefresh(out DateTimeOffset previousSuppressUntil, out DateTimeOffset suppressUntil)
    {
        previousSuppressUntil = _suppressUntil;
        var nextSuppressUntil = DateTimeOffset.UtcNow + SelfOperationSuppressDuration;
        if (nextSuppressUntil > _suppressUntil)
        {
            _suppressUntil = nextSuppressUntil;
        }

        suppressUntil = _suppressUntil;
        return previousSuppressUntil > DateTimeOffset.UtcNow;
    }

    public bool IsSuppressed(bool isFileOperationInProgress, out TimeSpan remaining)
    {
        if (isFileOperationInProgress)
        {
            remaining = SelfOperationSuppressDuration;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _suppressUntil)
        {
            remaining = _suppressUntil - now;
            return true;
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    public bool TryBeginRefresh()
    {
        if (_refreshRunning)
        {
            return false;
        }

        _refreshRunning = true;
        return true;
    }

    public void CompleteRefresh()
    {
        _refreshRunning = false;
    }
}
