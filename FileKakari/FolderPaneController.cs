using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;

namespace FileKakari;

sealed class FolderPaneController
{
    private readonly ObservableCollection<FolderPane> _displayPanes;
    private readonly FileService _fileService;
    private readonly SpecialLocationService _specialLocationService;
    private readonly DriveAvailabilityService _driveAvailabilityService;
    private readonly StatusSummaryService _statusSummary;
    private readonly LocalizationService _text;
    private readonly PerformanceLogger _performanceLogger;
    private readonly DevListPerfOptions _devListPerfOptions;
    private readonly Func<bool> _sortFoldersFirst;
    private readonly Func<string, bool> _shouldLoadExtraColumns;
    private int _statusVersion;

    internal FolderPaneController(
        ObservableCollection<FolderPane> displayPanes,
        FileService fileService,
        SpecialLocationService specialLocationService,
        DriveAvailabilityService driveAvailabilityService,
        StatusSummaryService statusSummary,
        LocalizationService text,
        PerformanceLogger performanceLogger,
        DevListPerfOptions devListPerfOptions,
        Func<bool> sortFoldersFirst,
        Func<string, bool> shouldLoadExtraColumns)
    {
        _displayPanes = displayPanes;
        _fileService = fileService;
        _specialLocationService = specialLocationService;
        _driveAvailabilityService = driveAvailabilityService;
        _statusSummary = statusSummary;
        _text = text;
        _performanceLogger = performanceLogger;
        _devListPerfOptions = devListPerfOptions;
        _sortFoldersFirst = sortFoldersFirst;
        _shouldLoadExtraColumns = shouldLoadExtraColumns;
    }

    internal void ClearDisplayPanes()
    {
        _displayPanes.Clear();
    }

    internal void RefreshDisplayPanes(WorkspaceSession session, bool isActivePaneActive)
    {
        var nextPanes = new List<FolderPane>();
        foreach (var paneGroup in session.PaneGroups)
        {
            paneGroup.IsActive = ReferenceEquals(paneGroup, session.ActivePaneGroup) && isActivePaneActive;
            nextPanes.Add(paneGroup);
        }

        ReplaceDisplayPanes(nextPanes);
    }

    internal async Task LoadDisplayPanesAsync(Dispatcher dispatcher)
    {
        var panes = _displayPanes.ToList();
        if (panes.Count == 0)
        {
            return;
        }

        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        _performanceLogger.Write($"folder-pane-display-load-start panes={panes.Count} detail=\"{string.Join("|", panes.Select(pane => $"{pane.Id}:{pane.ActiveTabState?.CurrentPath ?? ""}"))}\"");
        await Task.WhenAll(panes.Select(LoadPaneItemsAsync));
        await dispatcher.InvokeAsync(() =>
        {
            foreach (var pane in panes)
            {
                pane.RefreshDisplay();
            }
        }, DispatcherPriority.ContextIdle);
        _performanceLogger.Write($"folder-pane-display-load-complete panes={panes.Count} detail=\"{string.Join("|", panes.Select(pane => $"{pane.Id}:{pane.FileList.Items.Count}"))}\"");
    }

    internal Task LoadPaneItemsAsync(FolderPane pane)
    {
        return LoadPaneItemsAsync(pane, CancellationToken.None);
    }

    internal async Task LoadPaneItemsAsync(FolderPane pane, CancellationToken cancellationToken)
    {
        if (pane.IsLoading)
        {
            return;
        }

        var targetState = pane.ActiveTabState;
        if (targetState is null)
        {
            pane.FileList.StatusText = "";
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (SpecialLocationService.IsSpecialUri(targetState.CurrentPath))
        {
            await LoadSpecialLocationItemsAsync(pane, targetState, cancellationToken);
            return;
        }

        // Measure folder listing duration (ms) for performance logging
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (CanRestorePaneCache(pane, targetState)
            && targetState.CachedItems is { } cachedItems)
        {
            var cachedSortFoldersFirst = _sortFoldersFirst();
            if (CanReuseDisplayedItems(pane, targetState, cachedItems, cachedSortFoldersFirst))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateStatus(pane);
                pane.RefreshDisplay();
                _performanceLogger.Write(
                    $"folder-pane-display-reuse paneId={pane.Id} stateId={targetState.Id} " +
                    $"path=\"{targetState.CurrentPath}\" items={pane.FileList.Items.Count}");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            pane.FileList.ApplySort(targetState.SortColumn, targetState.SortAscending, cachedSortFoldersFirst, null);
            pane.FileList.ReplaceItems(targetState.CurrentPath, cachedItems, targetState.LastLoadedAt);
            ApplyFilter(pane, targetState.FilterText);
            stopwatch.Stop();
            if (targetState.LastLoadElapsedMs is null)
            {
                targetState.LastLoadElapsedMs = stopwatch.ElapsedMilliseconds;
            }
            UpdateStatus(pane);
            pane.RefreshDisplay();
            _performanceLogger.Write($"folder-pane-cache-restore paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" items={pane.FileList.Items.Count} lastLoaded=\"{FormatTimestamp(pane.FileList.LastLoadedAt)}\" lastExternalChange=\"{FormatTimestamp(pane.FileList.LastExternalChangeAt)}\"");
            return;
        }

        if (!await _driveAvailabilityService.DirectoryExistsAsync(targetState.CurrentPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            pane.FileList.StatusText = _text.Format("PathNotFound", targetState.CurrentPath);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        pane.IsLoading = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pane.FileList.StatusText = _text.Get("LoadingZero");
            var sortColumn = _devListPerfOptions.SortEnabled ? targetState.SortColumn : "__none";
            var sortAscending = targetState.SortAscending;
            var sortFoldersFirst = _sortFoldersFirst();
            var extraColumnsEnabled = _shouldLoadExtraColumns(sortColumn);
            _performanceLogger.Write($"folder-pane-load-start paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" sort={sortColumn}:{sortAscending} shellIcons={_devListPerfOptions.ShellIconsEnabled}");

            var iconLoadMilliseconds = 0L;
            var iconLoadCount = 0;
            var items = await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entries = _fileService.Enumerate(
                    targetState.CurrentPath,
                    sortColumn,
                    sortAscending,
                    sortFoldersFirst,
                    extraColumnsEnabled,
                    cancellationToken).ToList();

                if (_devListPerfOptions.ShellIconsEnabled)
                {
                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var iconStopwatch = Stopwatch.StartNew();
                        await entry.LoadIconAsync();
                        iconStopwatch.Stop();
                        iconLoadMilliseconds += iconStopwatch.ElapsedMilliseconds;
                        iconLoadCount++;
                    }
                }

                return entries;
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            targetState.StoreItems(targetState.CurrentPath, items);
            targetState.ClearPendingExternalChange();
            pane.FileList.ApplySort(targetState.SortColumn, targetState.SortAscending, sortFoldersFirst, null);
            pane.FileList.ReplaceItems(targetState.CurrentPath, items, targetState.LastLoadedAt);
            ApplyFilter(pane, targetState.FilterText);
            stopwatch.Stop();
            targetState.LastLoadElapsedMs = stopwatch.ElapsedMilliseconds;
            UpdateStatus(pane);
            pane.RefreshDisplay();
            _performanceLogger.Write($"folder-pane-load-complete paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" items={pane.FileList.Items.Count} iconCount={iconLoadCount} iconTotalMs={iconLoadMilliseconds} lastLoaded=\"{FormatTimestamp(pane.FileList.LastLoadedAt)}\"");
        }
        catch (OperationCanceledException ex)
        {
            _performanceLogger.Write($"folder-pane-load-cancelled paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" message=\"{ex.Message}\"");
            throw;
        }
        catch (Exception ex)
        {
            pane.FileList.StatusText = ex.Message;
            _performanceLogger.Write($"folder-pane-load-failed paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" type={ex.GetType().FullName} message=\"{ex.Message}\"");
        }
        finally
        {
            pane.IsLoading = false;
        }
    }

    internal void ApplyFilter(FolderPane pane, string filter)
    {
        pane.FileList.SetDisplayFilterText(filter);
        if (string.IsNullOrWhiteSpace(filter))
        {
            pane.FileList.ItemsView.Filter = null;
        }
        else
        {
            pane.FileList.ItemsView.Filter = item =>
                item is FileEntry entry
                && entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        }

        pane.FileList.ItemsView.Refresh();
    }

    internal void UpdateStatus(FolderPane pane)
    {
        var currentSummary = BuildCurrentViewSummary(pane);
        pane.FileList.StatusText = _statusSummary.BuildStatusSummary(
            currentSummary,
            pane.FileList.StatusMessagePrefix,
            [],
            selectedSizeText: null, elapsedMs: pane.ActiveTabState?.LastLoadElapsedMs);
    }

    internal async void UpdateStatusAsync(FolderPane pane, IReadOnlyList<FileEntry> selectedEntries)
    {
        var version = Interlocked.Increment(ref _statusVersion);
        var currentSummary = BuildCurrentViewSummary(pane);
        pane.FileList.StatusText = _statusSummary.BuildStatusSummary(
            currentSummary,
            pane.FileList.StatusMessagePrefix,
            selectedEntries,
            selectedSizeText: null, elapsedMs: pane.ActiveTabState?.LastLoadElapsedMs);

        if (selectedEntries.Count == 0 || !_devListPerfOptions.StatusAggregationEnabled)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource();
            var selectedSize = await Task.Run(() => StatusSummaryService.SumKnownFileSizes(selectedEntries, cts.Token), cts.Token);
            if (version != _statusVersion)
            {
                return;
            }

            pane.FileList.StatusText = _statusSummary.BuildStatusSummary(
                currentSummary,
                pane.FileList.StatusMessagePrefix,
                selectedEntries,
                StatusSummaryService.FormatSize(selectedSize), elapsedMs: pane.ActiveTabState?.LastLoadElapsedMs);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReplaceDisplayPanes(IEnumerable<FolderPane> panes)
    {
        var nextPanes = panes.ToList();
        for (var i = _displayPanes.Count - 1; i >= 0; i--)
        {
            if (!nextPanes.Contains(_displayPanes[i]))
            {
                _displayPanes.RemoveAt(i);
            }
        }

        for (var i = 0; i < nextPanes.Count; i++)
        {
            var pane = nextPanes[i];
            if (i < _displayPanes.Count && ReferenceEquals(_displayPanes[i], pane))
            {
                continue;
            }

            if (_displayPanes.Contains(pane))
            {
                _displayPanes.Move(_displayPanes.IndexOf(pane), i);
            }
            else
            {
                _displayPanes.Insert(i, pane);
            }
        }
    }

    private string BuildCurrentViewSummary(FolderPane pane)
    {
        return _statusSummary.BuildCurrentViewSummary(
            pane.FileList.Items,
            pane.FileList.ItemsView,
            pane.ActiveTabState?.FilterText,
            _devListPerfOptions.StatusAggregationEnabled,
            isSpecialLocation: pane.ActiveTabState is { } state && SpecialLocationService.IsSpecialUri(state.CurrentPath));
    }

    private async Task LoadSpecialLocationItemsAsync(FolderPane pane, WorkspaceTabState targetState, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (CanRestorePaneCache(pane, targetState)
            && targetState.CachedItems is { } cachedItems)
        {
            var sortFoldersFirst = _sortFoldersFirst();
            if (CanReuseDisplayedItems(pane, targetState, cachedItems, sortFoldersFirst))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateStatus(pane);
                pane.RefreshDisplay();
                _performanceLogger.Write(
                    $"folder-pane-display-reuse paneId={pane.Id} stateId={targetState.Id} " +
                    $"path=\"{targetState.CurrentPath}\" items={pane.FileList.Items.Count} special=true");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            pane.FileList.ApplySort(targetState.SortColumn, targetState.SortAscending, sortFoldersFirst, null);
            pane.FileList.ReplaceItems(targetState.CurrentPath, cachedItems, targetState.LastLoadedAt);
            ApplyFilter(pane, targetState.FilterText);
            stopwatch.Stop();
            if (targetState.LastLoadElapsedMs is null)
            {
                targetState.LastLoadElapsedMs = stopwatch.ElapsedMilliseconds;
            }
            UpdateStatus(pane);
            pane.RefreshDisplay();
            _performanceLogger.Write($"folder-pane-cache-restore paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" items={pane.FileList.Items.Count} lastLoaded=\"{FormatTimestamp(pane.FileList.LastLoadedAt)}\" special=true");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        pane.IsLoading = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pane.FileList.StatusText = _text.Get("LoadingZero");
            _performanceLogger.Write($"folder-pane-load-start paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" view=this-pc");

            var items = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _specialLocationService.EnumerateThisPc().ToList();
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            targetState.StoreItems(targetState.CurrentPath, items);
            targetState.ClearPendingExternalChange();
            pane.FileList.ApplySort(targetState.SortColumn, targetState.SortAscending, _sortFoldersFirst(), null);
            pane.FileList.ReplaceItems(targetState.CurrentPath, items, targetState.LastLoadedAt);
            ApplyFilter(pane, targetState.FilterText);
            stopwatch.Stop();
            targetState.LastLoadElapsedMs = stopwatch.ElapsedMilliseconds;
            UpdateStatus(pane);
            pane.RefreshDisplay();
            _performanceLogger.Write($"folder-pane-load-complete paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" items={pane.FileList.Items.Count} view=this-pc lastLoaded=\"{FormatTimestamp(pane.FileList.LastLoadedAt)}\"");
        }
        catch (OperationCanceledException ex)
        {
            _performanceLogger.Write($"folder-pane-load-cancelled paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" message=\"{ex.Message}\" view=this-pc");
            throw;
        }
        catch (Exception ex)
        {
            pane.FileList.StatusText = ex.Message;
            _performanceLogger.Write($"folder-pane-load-failed paneId={pane.Id} stateId={targetState.Id} path=\"{targetState.CurrentPath}\" type={ex.GetType().FullName} message=\"{ex.Message}\" view=this-pc");
        }
        finally
        {
            pane.IsLoading = false;
        }
    }

    private static bool CanRestorePaneCache(FolderPane pane, WorkspaceTabState targetState)
    {
        return targetState.CachedItems is not null
            && !targetState.HasPendingExternalChange
            && string.Equals(targetState.CachedPath, targetState.CurrentPath, StringComparison.OrdinalIgnoreCase)
            && (pane.FileList.LastExternalChangeAt is null
                || (targetState.LastLoadedAt is not null
                    && targetState.LastLoadedAt >= pane.FileList.LastExternalChangeAt));
    }

    private static bool CanReuseDisplayedItems(
        FolderPane pane,
        WorkspaceTabState targetState,
        IReadOnlyList<FileEntry> cachedItems,
        bool sortFoldersFirst)
    {
        return string.Equals(pane.FileList.CurrentPath, targetState.CurrentPath, StringComparison.OrdinalIgnoreCase)
            && pane.FileList.LastLoadedAt == targetState.LastLoadedAt
            && pane.FileList.Items.Count == cachedItems.Count
            && string.Equals(pane.FileList.DisplaySortColumn, targetState.SortColumn, StringComparison.Ordinal)
            && pane.FileList.DisplaySortAscending == targetState.SortAscending
            && pane.FileList.DisplaySortFoldersFirst == sortFoldersFirst
            && string.Equals(pane.FileList.DisplayFilterText, targetState.FilterText, StringComparison.Ordinal);
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.ToString("O") ?? "";
    }
}
