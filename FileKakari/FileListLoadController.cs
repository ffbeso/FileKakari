using System;
using System.Threading;

namespace FileKakari;

internal readonly record struct BatchBindResult(bool IsFirstBatch, long FirstDisplayMilliseconds);

internal sealed class FileListLoadController
{
    private CancellationTokenSource? _loadCancellation;
    private bool _isLoading;
    private string? _currentLoadPath;
    private string? _loadingStateId;
    private string? _activeStateId;
    private int _loadGeneration;
    private string? _lastLoadRequestPath;
    private int _lastLoadRequestId;
    private bool _firstBatchDisplayed;
    private long _totalBindMilliseconds;
    private long _maxBindMilliseconds;
    private long _firstDisplayMilliseconds;
    private int _bindBatchCount;
    private int _addRangeCallCount;
    private int _addRangeItemCount;
    private int _diagnosticLoadId;

    public bool IsLoading
    {
        get => _isLoading;
        set => _isLoading = value;
    }

    public string? CurrentLoadPath
    {
        get => _currentLoadPath;
        set => _currentLoadPath = value;
    }

    public string? LoadingStateId
    {
        get => _loadingStateId;
        set => _loadingStateId = value;
    }

    public string? ActiveStateId
    {
        get => _activeStateId;
        set => _activeStateId = value;
    }

    public int LoadGeneration
    {
        get => _loadGeneration;
        set => _loadGeneration = value;
    }

    public string? LastLoadRequestPath
    {
        get => _lastLoadRequestPath;
        set => _lastLoadRequestPath = value;
    }

    public int LastLoadRequestId
    {
        get => _lastLoadRequestId;
        set => _lastLoadRequestId = value;
    }

    public CancellationTokenSource? LoadCancellation
    {
        get => _loadCancellation;
        set => _loadCancellation = value;
    }

    public long TotalBindMilliseconds => _totalBindMilliseconds;
    public long MaxBindMilliseconds => _maxBindMilliseconds;
    public long FirstDisplayMilliseconds => _firstDisplayMilliseconds;
    public int BindBatchCount => _bindBatchCount;
    public int AddRangeCallCount => _addRangeCallCount;
    public int AddRangeItemCount => _addRangeItemCount;
    public int DiagnosticLoadId => _diagnosticLoadId;

    public void CancelCurrentLoad()
    {
        _loadCancellation?.Cancel();
    }

    public CancellationTokenSource ResetLoadCancellation()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        return _loadCancellation;
    }

    public int IncrementLoadGeneration()
    {
        return Interlocked.Increment(ref _loadGeneration);
    }

    public bool TryBeginLoadForState(WorkspaceTabState targetState, string path, Action<string> logWrite)
    {
        if (string.Equals(_loadingStateId, targetState.Id, StringComparison.Ordinal))
        {
            logWrite($"load-skip reason=same-state-loading stateId={targetState.Id} path=\"{path}\"");
            return false;
        }

        if (!string.IsNullOrEmpty(_loadingStateId))
        {
            logWrite($"load-cancel reason=state-switch previousStateId={_loadingStateId} nextStateId={targetState.Id} path=\"{path}\"");
            _loadCancellation?.Cancel();
        }

        _activeStateId = targetState.Id;
        _loadingStateId = targetState.Id;
        return true;
    }

    public bool IsLoadCurrentForState(int loadId, WorkspaceTabState targetState)
    {
        return loadId == _loadGeneration
            && string.Equals(_activeStateId, targetState.Id, StringComparison.Ordinal)
            && string.Equals(_loadingStateId, targetState.Id, StringComparison.Ordinal);
    }

    public bool CancelLoadForDifferentState(WorkspaceTabState targetState, string reason, Action<string> logWrite)
    {
        if (string.IsNullOrEmpty(_loadingStateId)
            || string.Equals(_loadingStateId, targetState.Id, StringComparison.Ordinal))
        {
            return false;
        }

        logWrite($"load-cancel reason={reason} previousStateId={_loadingStateId} nextStateId={targetState.Id}");
        _loadCancellation?.Cancel();
        _loadGeneration = Interlocked.Increment(ref _loadGeneration);
        _loadingStateId = null;
        _isLoading = false;
        _currentLoadPath = null;
        return true;
    }

    public void ResetBatchStats(int diagnosticLoadId)
    {
        _diagnosticLoadId = diagnosticLoadId;
        _firstBatchDisplayed = false;
        _totalBindMilliseconds = 0;
        _maxBindMilliseconds = 0;
        _firstDisplayMilliseconds = 0;
        _bindBatchCount = 0;
        _addRangeCallCount = 0;
        _addRangeItemCount = 0;
    }

    public BatchBindResult RecordBatchBound(long bindMs, int batchCount, long elapsedMs)
    {
        _bindBatchCount++;
        _addRangeCallCount++;
        _addRangeItemCount += batchCount;
        _totalBindMilliseconds += bindMs;
        _maxBindMilliseconds = Math.Max(_maxBindMilliseconds, bindMs);

        if (!_firstBatchDisplayed)
        {
            _firstBatchDisplayed = true;
            _firstDisplayMilliseconds = elapsedMs;
            return new BatchBindResult(true, _firstDisplayMilliseconds);
        }

        return new BatchBindResult(false, 0);
    }
}
