namespace FileKakari;

internal sealed class FileWatcherRefreshCoordinator
{
    private static readonly TimeSpan SelfOperationSuppressDuration = TimeSpan.FromMilliseconds(1500);
    private bool _refreshPending;
    private bool _refreshRunning;
    private string? _pendingPath;
    private DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;

    public TimeSpan SuppressDuration => SelfOperationSuppressDuration;

    public void RequestRefresh(string path)
    {
        _refreshPending = true;
        _pendingPath = path;
    }

    public void SuppressRefresh()
    {
        _suppressUntil = DateTimeOffset.UtcNow + SelfOperationSuppressDuration;
        ClearPendingRefresh();
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

    public bool TryGetPendingRefreshPath(string? activePath, out string pendingPath)
    {
        if (!_refreshPending || _refreshRunning)
        {
            pendingPath = string.Empty;
            return false;
        }

        if (_pendingPath is null
            || activePath is null
            || !string.Equals(_pendingPath, activePath, StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingRefresh();
            pendingPath = string.Empty;
            return false;
        }

        pendingPath = _pendingPath;
        return true;
    }

    public bool TryBeginRefresh(string path)
    {
        if (!_refreshPending
            || _refreshRunning
            || _pendingPath is null
            || !string.Equals(_pendingPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _refreshRunning = true;
        ClearPendingRefresh();
        return true;
    }

    public void CompleteRefresh()
    {
        _refreshRunning = false;
    }

    public void ClearPendingRefresh(string? path = null)
    {
        if (path is not null
            && _pendingPath is not null
            && !string.Equals(_pendingPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _refreshPending = false;
        _pendingPath = null;
    }
}
