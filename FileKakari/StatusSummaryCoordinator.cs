using System.ComponentModel;
using System.Diagnostics;

namespace FileKakari;

internal sealed class StatusSummaryCoordinator : IDisposable
{
    private readonly StatusSummaryService _statusSummary;
    private readonly PerformanceLogger _performanceLogger;
    private readonly Action<string> _setStatusText;
    private readonly Action _showDisconnectedStatus;
    private readonly Action<string> _updateCrashContextSnapshot;
    private CancellationTokenSource? _statusCancellation;
    private int _statusUpdateCount;
    private int _statusSkippedDuringLoadCount;
    private int _statusSizeCalculationCount;
    private long _statusSummaryMilliseconds;
    private long _statusSizeCalculationMilliseconds;
    private string _currentFolderSummary = "";

    public StatusSummaryCoordinator(
        StatusSummaryService statusSummary,
        PerformanceLogger performanceLogger,
        Action<string> setStatusText,
        Action showDisconnectedStatus,
        Action<string> updateCrashContextSnapshot)
    {
        _statusSummary = statusSummary;
        _performanceLogger = performanceLogger;
        _setStatusText = setStatusText;
        _showDisconnectedStatus = showDisconnectedStatus;
        _updateCrashContextSnapshot = updateCrashContextSnapshot;
    }

    public string? StatusMessagePrefix { get; set; }

    public void Dispose()
    {
        _statusCancellation?.Cancel();
        _statusCancellation?.Dispose();
        _statusCancellation = null;
    }

    public void ResetDiagnostics()
    {
        _statusUpdateCount = 0;
        _statusSkippedDuringLoadCount = 0;
        _statusSizeCalculationCount = 0;
        _statusSummaryMilliseconds = 0;
        _statusSizeCalculationMilliseconds = 0;
    }

    public string GetDiagnosticsStatus()
    {
        return $"updates={_statusUpdateCount},skippedDuringLoad={_statusSkippedDuringLoadCount},summaryMs={_statusSummaryMilliseconds},sizeCalcCount={_statusSizeCalculationCount},sizeCalcMs={_statusSizeCalculationMilliseconds}";
    }

    public void RecordLoadingProgressSummary(long elapsedMilliseconds)
    {
        _statusUpdateCount++;
        _statusSummaryMilliseconds += elapsedMilliseconds;
    }

    public async void UpdateSelectedItemStatus(
        FolderTab? activeTab,
        bool isLoading,
        Func<IReadOnlyList<FileEntry>> getSelectedEntries,
        bool statusAggregationEnabled,
        int diagnosticLoadId,
        Action refreshCurrentFolderSummary)
    {
        _updateCrashContextSnapshot("status-update");
        if (activeTab is null)
        {
            return;
        }

        if (activeTab.IsDisconnected)
        {
            _showDisconnectedStatus();
            return;
        }

        if (isLoading)
        {
            _statusSkippedDuringLoadCount++;
            return;
        }

        var statusStopwatch = Stopwatch.StartNew();
        _statusUpdateCount++;
        _statusCancellation?.Cancel();
        _statusCancellation?.Dispose();
        _statusCancellation = new CancellationTokenSource();
        var token = _statusCancellation.Token;
        var selectedEntries = getSelectedEntries();
        var summary = BuildStatusSummary(selectedEntries, selectedSizeText: null, refreshCurrentFolderSummary);
        _setStatusText(summary);
        statusStopwatch.Stop();
        _statusSummaryMilliseconds += statusStopwatch.ElapsedMilliseconds;
        _performanceLogger.Write($"status-update id={diagnosticLoadId} count={_statusUpdateCount} selected={selectedEntries.Count} summaryMs={statusStopwatch.ElapsedMilliseconds} aggregation={statusAggregationEnabled} isLoading={isLoading}");
        if (selectedEntries.Count == 0)
        {
            return;
        }

        if (!statusAggregationEnabled)
        {
            _performanceLogger.Write($"status-size-skip id={diagnosticLoadId} reason=dev-flag selected={selectedEntries.Count}");
            return;
        }

        try
        {
            var sizeStopwatch = Stopwatch.StartNew();
            var selectedSize = await Task.Run(() => StatusSummaryService.SumKnownFileSizes(selectedEntries, token), token);
            sizeStopwatch.Stop();
            _statusSizeCalculationCount++;
            _statusSizeCalculationMilliseconds += sizeStopwatch.ElapsedMilliseconds;
            _performanceLogger.Write($"status-size-complete id={diagnosticLoadId} selected={selectedEntries.Count} elapsedMs={sizeStopwatch.ElapsedMilliseconds} totalMs={_statusSizeCalculationMilliseconds}");
            if (token.IsCancellationRequested)
            {
                return;
            }

            _setStatusText(BuildStatusSummary(selectedEntries, StatusSummaryService.FormatSize(selectedSize), refreshCurrentFolderSummary));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void RefreshCurrentFolderSummary(
        IReadOnlyCollection<FileEntry> items,
        ICollectionView itemsView,
        string? filter,
        bool statusAggregationEnabled,
        bool isSpecialLocation,
        int diagnosticLoadId,
        bool isLoading)
    {
        var stopwatch = Stopwatch.StartNew();
        _currentFolderSummary = _statusSummary.BuildCurrentViewSummary(
            items,
            itemsView,
            filter,
            statusAggregationEnabled,
            isSpecialLocation);
        stopwatch.Stop();
        _statusSummaryMilliseconds += stopwatch.ElapsedMilliseconds;
        _performanceLogger.Write($"status-summary-refresh id={diagnosticLoadId} items={items.Count} elapsedMs={stopwatch.ElapsedMilliseconds} aggregation={statusAggregationEnabled} isLoading={isLoading}");
    }

    private string BuildStatusSummary(
        IReadOnlyList<FileEntry> selectedEntries,
        string? selectedSizeText,
        Action refreshCurrentFolderSummary)
    {
        if (string.IsNullOrEmpty(_currentFolderSummary))
        {
            refreshCurrentFolderSummary();
        }

        return _statusSummary.BuildStatusSummary(_currentFolderSummary, StatusMessagePrefix, selectedEntries, selectedSizeText);
    }
}
