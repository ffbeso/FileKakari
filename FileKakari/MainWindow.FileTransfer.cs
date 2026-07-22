using System.IO;
using System.Windows;

namespace FileKakari;

public partial class MainWindow
{
    private async Task DropFileItemsAsync(
        IReadOnlyList<FileDragItem> dragItems,
        string targetDirectory,
        PendingFileOperationKind operationKind,
        bool confirmNonSelfCopy = false)
    {
        var operationItems = dragItems
            .Select(item => new FileTransferItem(item.SourcePath, item.Name, item.IsDirectory))
            .ToList();
        await ExecuteFileTransferAsync(operationItems, targetDirectory, operationKind, confirmNonSelfCopy: confirmNonSelfCopy, refreshPane: GetNormalFolderPane());
    }

    private async Task<List<string>> ExecuteFileTransferAsync(
        IReadOnlyList<FileTransferItem> items,
        string targetDirectory,
        PendingFileOperationKind operationKind,
        bool refreshActiveFolder = true,
        FolderTab? refreshTab = null,
        FileOperationContext? statusContext = null,
        bool confirmNonSelfCopy = false,
        FolderPane? refreshPane = null)
    {
        if (SpecialLocationService.IsSpecialUri(targetDirectory))
        {
            var errorMsg = "Copy/Move to special URI is not supported.";
            if (statusContext is not null)
            {
                SetFileOperationStatus(statusContext, _text.Format("PasteFailedPrefix", errorMsg));
            }
            else
            {
                SetNormalStatusText(_text.Format("PasteFailedPrefix", errorMsg));
            }
            MessageBox.Show(this, errorMsg, _text.Get("PasteFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return new List<string>();
        }

        if (!await EnsurePathReadyForOperationAsync(targetDirectory, "transfer-target"))
        {
            return new List<string>();
        }

        _isFileOperationInProgress = true;

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var sourcePaths = items.Select(item => item.SourcePath).ToList();

            int errorCode = 0;
            bool userAborted = false;
            bool isSelfCopy = operationKind == PendingFileOperationKind.Copy
                && items.Count > 0
                && items.All(item => {
                    var itemDir = Path.GetDirectoryName(item.SourcePath);
                    return !string.IsNullOrEmpty(itemDir)
                        && string.Equals(
                            NormalizePathForComparison(itemDir),
                            NormalizePathForComparison(targetDirectory),
                            StringComparison.OrdinalIgnoreCase);
                });

            if (operationKind == PendingFileOperationKind.Copy
                && confirmNonSelfCopy
                && !isSelfCopy
                && !ConfirmDragCopy(items.Count, targetDirectory))
            {
                return new List<string>();
            }

            var targetExistBefore = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var resolvedTargetPaths = new List<string>();

            if (operationKind == PendingFileOperationKind.Copy)
            {
                if (!isSelfCopy)
                {
                    var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items)
                    {
                        var targetPath = FileSystemOperations.GetAvailableCopyPath(item.SourcePath, targetDirectory, item.IsDirectory, reservedPaths);
                        reservedPaths.Add(targetPath);
                        resolvedTargetPaths.Add(targetPath);
                        targetExistBefore[targetPath] = File.Exists(targetPath) || Directory.Exists(targetPath);
                    }
                }
            }
            else // Move
            {
                foreach (var item in items)
                {
                    var expectedTarget = Path.Combine(targetDirectory, item.Name);
                    targetExistBefore[expectedTarget] = File.Exists(expectedTarget) || Directory.Exists(expectedTarget);
                    resolvedTargetPaths.Add(expectedTarget);
                }
            }

            List<string> executedTargets;

            if (isSelfCopy)
            {
                var (selfExecuted, selfError) = await _fileOperationService.CopySelfMultipleAsync(sourcePaths, targetDirectory);
                executedTargets = selfExecuted;
                if (selfError is not null)
                {
                    if (executedTargets.Count == 0)
                    {
                        var failedMessage = _text.Format("PasteFailedPrefix", selfError.Message);
                        if (statusContext is not null)
                        {
                            SetFileOperationStatus(statusContext, failedMessage);
                        }
                        else
                        {
                            SetNormalStatusText(failedMessage);
                        }
                        MessageBox.Show(this, selfError.Message, _text.Get("PasteFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return new List<string>();
                    }
                    else
                    {
                        var partialMessage = _text.Format("PasteFailedPrefix", selfError.Message);
                        if (statusContext is not null)
                        {
                            SetFileOperationStatus(statusContext, partialMessage);
                        }
                        else
                        {
                            SetNormalStatusText(partialMessage);
                        }
                        MessageBox.Show(this, selfError.Message, _text.Get("PasteFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            else
            {
                if (operationKind == PendingFileOperationKind.Copy)
                {
                    (errorCode, userAborted) = await _fileOperationService.CopyMultipleToPathsAsync(hwnd, sourcePaths, resolvedTargetPaths);
                    executedTargets = resolvedTargetPaths;
                }
                else
                {
                    (errorCode, userAborted) = await _fileOperationService.MoveMultipleAsync(hwnd, sourcePaths, targetDirectory);
                    executedTargets = resolvedTargetPaths;
                }

                if (errorCode != 0)
                {
                    var failedMessage = _text.Format("PasteFailedPrefix", $"Shell operation failed (Error: {errorCode})");
                    if (statusContext is not null)
                    {
                        SetFileOperationStatus(statusContext, failedMessage);
                    }
                    else
                    {
                        SetNormalStatusText(failedMessage);
                    }
                    return new List<string>();
                }
            }

            if (errorCode == 0 && !userAborted)
            {
                var undoItems = new List<UndoFileOperationItem>();
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    string targetPath;
                    bool existsBefore = false;

                    if (isSelfCopy)
                    {
                        if (i < executedTargets.Count)
                        {
                            targetPath = executedTargets[i];
                            existsBefore = false;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (i < executedTargets.Count)
                        {
                            targetPath = executedTargets[i];
                            targetExistBefore.TryGetValue(targetPath, out existsBefore);
                        }
                        else
                        {
                            continue;
                        }
                    }

                    bool isDirectory = item.IsDirectory;
                    bool success = false;

                    if (operationKind == PendingFileOperationKind.Copy)
                    {
                        if (!existsBefore)
                        {
                            if (isDirectory)
                            {
                                success = Directory.Exists(targetPath);
                            }
                            else
                            {
                                if (File.Exists(targetPath))
                                {
                                    var targetInfo = new FileInfo(targetPath);
                                    var sourceInfo = new FileInfo(item.SourcePath);
                                    success = targetInfo.Length == sourceInfo.Length
                                        && Math.Abs((targetInfo.LastWriteTimeUtc - sourceInfo.LastWriteTimeUtc).TotalSeconds) < 2;
                                }
                            }
                        }
                    }
                    else // Move
                    {
                        if (!existsBefore)
                        {
                            if (isDirectory)
                            {
                                success = Directory.Exists(targetPath) && !Directory.Exists(item.SourcePath);
                            }
                            else
                            {
                                success = File.Exists(targetPath) && !File.Exists(item.SourcePath);
                            }
                        }
                    }

                    if (success)
                    {
                        undoItems.Add(new UndoFileOperationItem(
                            item.SourcePath,
                            targetPath,
                            isDirectory,
                            null,
                            false));
                    }
                }

                if (undoItems.Count > 0)
                {
                    _undoService.RecordFileOperation(
                        operationKind == PendingFileOperationKind.Copy ? UndoOperationKind.Copy : UndoOperationKind.Move,
                        undoItems);
                }
            }

            var operationStatus = operationKind == PendingFileOperationKind.Copy
                ? _text.Format("CopiedMultiple", executedTargets.Count)
                : _text.Format("MovedMultiple", executedTargets.Count);

            if (refreshActiveFolder)
            {
                var preferredPane = refreshPane ?? (refreshTab is not null ? FindPaneForTab(refreshTab) : (ActiveTab is not null ? FindPaneForTab(ActiveTab) : null));
                var reloadPlan = FileTransferResultService.CreateReloadPlan(
                    targetDirectory,
                    executedTargets,
                    sourcePaths,
                    includeSourceDirectories: operationKind == PendingFileOperationKind.Move);
                var revealTargets = reloadPlan.RevealTargetPaths;

                if (operationKind == PendingFileOperationKind.Move && !userAborted)
                {
                    SynchronizeMovedSourcePaneItems(sourcePaths, reloadPlan.TargetDirectory);
                }

                _performanceLogger.Write(
                    $"file-transfer-refresh-plan operationKind={operationKind} sourceRefreshRequested={operationKind == PendingFileOperationKind.Move} " +
                    $"targetRefreshRequested=true reloadPathCount={reloadPlan.ReloadPaths.Count}");

                bool revealAndSelectExecuted = false;
                foreach (var path in reloadPlan.ReloadPaths)
                {
                    _folderWatchTabTracker.MarkTabsPendingExternalChange(path);
                    if (string.Equals(NormalizePathForComparison(path), NormalizePathForComparison(reloadPlan.TargetDirectory), StringComparison.OrdinalIgnoreCase)
                        && preferredPane is not null
                        && revealTargets.Count > 0
                        && !userAborted)
                    {
                        var didReveal = await ReloadFolderPanesShowingPathAsync(path, preferredPane, FileListRestorePolicy.RevealAndSelectPaths, revealTargets);
                        if (didReveal)
                        {
                            revealAndSelectExecuted = true;
                        }
                    }
                    else
                    {
                        await ReloadFolderPanesShowingPathAsync(path, preferredPane);
                    }
                }

                // Restore/focus selection only if the operation completed fully without user abort.
                if (!userAborted && preferredPane is not null)
                {
                    _statusSummaryCoordinator.StatusMessagePrefix = operationStatus;
                    if (!revealAndSelectExecuted)
                    {
                        SelectItemsInPaneByPaths(preferredPane, revealTargets);
                    }

                    if (ReferenceEquals(preferredPane, GetNormalFolderPane()))
                    {
                        SyncNormalPaneDisplayStateFromView(preferredPane);
                    }

                    _performanceLogger.Write($"file-transfer-reveal target=\"{reloadPlan.TargetDirectory}\" requested={executedTargets.Count} distinct={revealTargets.Count} paneId=\"{preferredPane.Id}\" policyApplied={revealAndSelectExecuted}");

                    if (ReferenceEquals(preferredPane, _primaryPaneGroup))
                    {
                        RefreshCurrentFolderSummary();
                        UpdateNavigationButtons();
                    }
                    else
                    {
                        UpdateWorkspacePaneStatus(preferredPane);
                    }
                }
            }
            else
            {
                if (!userAborted)
                {
                    if (statusContext is not null)
                    {
                        SetFileOperationStatus(statusContext, operationStatus);
                    }
                    else
                    {
                        StatusText.Text = operationStatus;
                    }
                }
            }

            return userAborted ? new List<string>() : executedTargets;
        }
        catch (Exception ex)
        {
            if (statusContext is not null)
            {
                SetFileOperationStatus(statusContext, _text.Format("PasteFailedPrefix", ex.Message));
            }
            else
            {
                SetNormalStatusText(_text.Format("PasteFailedPrefix", ex.Message));
            }
            MessageBox.Show(this, ex.Message, _text.Get("PasteFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return new List<string>();
        }
        finally
        {
            SuppressFolderWatchRefreshForSelfOperation("file-transfer");
            _isFileOperationInProgress = false;
            await ProcessPendingFolderWatchRefreshAsync();
            await ProcessPendingDriveListRefreshAsync();
        }
    }

    private void SynchronizeMovedSourcePaneItems(IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        var sourcePathSet = sourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourcePathSet.Count == 0)
        {
            return;
        }

        var targetPath = NormalizePathForComparison(targetDirectory);
        var panes = GetDisplayedWorkspacePanes()
            .Append(_primaryPaneGroup)
            .Distinct()
            .ToList();

        foreach (var pane in panes)
        {
            if (pane.ActiveTabState is not { } state
                || string.Equals(NormalizePathForComparison(state.CurrentPath), targetPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var paneItems = GetPaneItems(pane);
            var itemsToRemove = paneItems
                .Where(entry => sourcePathSet.Contains(entry.FullPath)
                    && string.Equals(
                        NormalizePathForComparison(Path.GetDirectoryName(entry.FullPath)),
                        NormalizePathForComparison(state.CurrentPath),
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (itemsToRemove.Count == 0)
            {
                continue;
            }

            var itemCountBefore = paneItems.Count;
            var visibleCountBefore = IsWorkspaceDisplayPane(pane)
                ? CountVisibleItems(pane.FileList.ItemsView)
                : CountVisibleItems(ItemsView);
            foreach (var entry in itemsToRemove)
            {
                paneItems.Remove(entry);
            }

            var remainingSelectedPaths = state.SelectedPaths
                .Where(path => paneItems.Any(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            state.SelectedPaths = remainingSelectedPaths;
            if (IsWorkspaceDisplayPane(pane))
            {
                pane.SelectedPaths = remainingSelectedPaths;
            }

            state.StoreItems(state.CurrentPath, paneItems.ToList());
            RefreshPaneItemsPreservingFilter(pane, "file-transfer-source-move");
            RestorePaneSelectionWithoutFocus(pane, remainingSelectedPaths);

            var session = FindSessionContainingPane(pane);
            var visibleCountAfter = IsWorkspaceDisplayPane(pane)
                ? CountVisibleItems(pane.FileList.ItemsView)
                : CountVisibleItems(ItemsView);
            _performanceLogger.Write(
                $"file-transfer-source-synchronized sessionId={session?.Id ?? "unknown"} paneId={pane.Id} " +
                $"path=\"{Path.GetFileName(state.CurrentPath)}\" filterTextLength={(state.FilterText ?? string.Empty).Length} filterPreserved=true " +
                $"itemCountBefore={itemCountBefore} itemCountAfter={paneItems.Count} " +
                $"visibleCountBefore={visibleCountBefore} visibleCountAfter={visibleCountAfter} " +
                $"collectionViewRefreshed=true sourceRefreshRequested=true targetRefreshRequested=true removedCount={itemsToRemove.Count}");
        }
    }

    private bool ConfirmDragCopy(int itemCount, string targetDirectory)
    {
        var result = MessageBox.Show(
            this,
            _text.Format("DragCopyConfirmMessage", itemCount, targetDirectory),
            _text.Get("DragCopyConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}
