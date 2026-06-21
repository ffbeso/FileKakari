using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow
{
    private async void BeginRenameSelected(FolderPane? targetPane = null)
    {
        await BeginRenameSelectedAsync(targetPane);
    }

    private async Task BeginRenameSelectedAsync(FolderPane? targetPane = null)
    {
        var context = GetActiveFileOperationContext(targetPane);
        if (context is null)
        {
            return;
        }

        if (!await EnsureFileOperationPathReadyAsync(context, "rename"))
        {
            return;
        }

        if (SpecialLocationService.IsSpecialUri(context.CurrentPath))
        {
            SetFileOperationStatus(context, _text.Get("OperationUnavailableInSpecialLocation"));
            return;
        }

        var selectedEntries = context.SelectedEntries;
        if (selectedEntries.Count == 0)
        {
            return;
        }

        if (selectedEntries.Count != 1)
        {
            SetFileOperationStatus(context, _text.Get("RenameSingleSelectionOnly"));
            return;
        }

        var entry = selectedEntries[0];
        if (!IsRenameable(entry))
        {
            SetFileOperationStatus(context, _text.Get("OperationUnavailableInSpecialLocation"));
            return;
        }

        await BeginRenameEntryAsync(entry, context.Pane);
    }

    private async Task BeginRenameEntryAsync(FileEntry entry, FolderPane? targetPane = null)
    {
        var pane = targetPane ?? FindPaneContainingEntry(entry) ?? GetActiveFolderPane();
        if (pane is null || pane.ActiveTab is null)
        {
            return;
        }

        var tab = pane.ActiveTab;
        var currentPath = tab.Navigation.CurrentPath;

        if (tab.IsDisconnected)
        {
            ShowDisconnectedStatus();
            if (IsWorkspaceDisplayPane(pane))
            {
                pane.FileList.StatusText = StatusText.Text;
            }
            return;
        }

        var isWorkspace = IsWorkspaceDisplayPane(pane);
        var operationName = isWorkspace ? "workspace-rename" : "rename";

        if (!await EnsurePathReadyForOperationAsync(currentPath, operationName))
        {
            if (isWorkspace)
            {
                pane.FileList.StatusText = StatusText.Text;
            }
            return;
        }

        if (SpecialLocationService.IsSpecialUri(currentPath) || !IsRenameable(entry))
        {
            var msg = _text.Get("OperationUnavailableInSpecialLocation");
            SetNormalStatusText(msg);
            if (isWorkspace)
            {
                pane.FileList.StatusText = msg;
            }
            return;
        }

        var paneItems = GetPaneItems(pane);
        foreach (var item in paneItems)
        {
            if (!ReferenceEquals(item, entry) && item.IsRenaming)
            {
                CancelRename(item, showStatus: false);
            }
        }

        entry.BeginRename();
        _activeRenameEntry = entry;
        _activeRenamePane = pane;

        var listView = GetFolderPaneListView(pane);
        if (listView is not null)
        {
            listView.SelectedItem = entry;
            listView.ScrollIntoView(entry);
            await _renameFocus.FocusRenameTextBoxAsync(listView, entry);
        }
    }

    private async Task BeginRenameAfterClickDelayAsync(FileEntry entry, int generation, FolderPane? targetPane = null)
    {
        await _renameInteraction.WaitForClickDelayAsync();

        var pane = targetPane ?? FindPaneContainingEntry(entry) ?? GetActiveFolderPane();
        if (pane is null)
        {
            return;
        }

        var listView = GetFolderPaneListView(pane);
        if (listView is null)
        {
            return;
        }

        if (!_renameInteraction.IsCurrentGeneration(generation)
            || _selectionInteraction.IsSelecting
            || _isFileDragInProgress
            || entry.IsRenaming
            || listView.SelectedItems.Count != 1
            || !listView.SelectedItems.Contains(entry))
        {
            return;
        }

        await BeginRenameEntryAsync(entry, pane);
    }

    private async Task<bool> CommitRenameAsync(FileEntry entry)
    {
        if (!_renameInteraction.TryBeginCommit(entry))
        {
            return true;
        }

        var renamed = false;
        try
        {
            var newName = entry.RenameText.Trim();
            var pane = _activeRenamePane ?? FindPaneContainingEntry(entry) ?? GetActiveFolderPane();
            var validationError = ValidateRename(entry, newName);
            if (validationError is not null)
            {
                if (validationError.Length == 0)
                {
                    CancelRename(entry, showStatus: false);
                    if (_activeRenamePane is { } p && GetFolderPaneListView(p) is { } lv)
                    {
                        lv.Focus();
                    }
                    else
                    {
                        ItemsList.Focus();
                    }
                    SetNormalStatusText(_text.Get("RenameCanceled"));
                    return true;
                }

                ReportRenameFailure(pane, validationError);
                return false;
            }

            if (pane is null || pane.ActiveTab is null)
            {
                ReportRenameFailure(null, _text.Format("RenameFailedPrefix", _text.Get("OperationUnavailableInSpecialLocation")));
                return false;
            }

            var tab = pane.ActiveTab;
            var isWorkspace = IsWorkspaceDisplayPane(pane);
            var operationName = isWorkspace ? "workspace-rename-commit" : "rename-commit";

            if (tab.IsDisconnected)
            {
                ShowDisconnectedStatus();
                if (isWorkspace)
                {
                    pane.FileList.StatusText = StatusText.Text;
                }
                PlayRenameFailureSound();
                return false;
            }

            if (!await EnsurePathReadyForOperationAsync(tab.Navigation.CurrentPath, operationName))
            {
                if (isWorkspace)
                {
                    pane.FileList.StatusText = StatusText.Text;
                }
                PlayRenameFailureSound();
                return false;
            }

            var targetPath = Path.Combine(tab.Navigation.CurrentPath, newName);
            var originalPath = entry.FullPath;
            try
            {
                await _fileOperationService.RenameAsync(entry.FullPath, targetPath, entry.IsDirectory);
                renamed = true;

                entry.UpdateFromPath(targetPath);
                entry.IsRenaming = false;
                ClearActiveRenameEntry(entry);
                _undoService.RecordRename(originalPath, targetPath, entry.IsDirectory);

                if (isWorkspace)
                {
                    await ReloadFolderPanesShowingPathAsync(tab.Navigation.CurrentPath, pane);
                    SelectItemInPaneByPath(pane, targetPath);
                }
                else
                {
                    RefreshItemsView("rename-commit");
                    var listView = GetFolderPaneListView(pane);
                    if (listView is not null)
                    {
                        listView.SelectedItem = entry;
                        listView.ScrollIntoView(entry);
                    }
                }

                var msg = _text.Format("RenamedTo", entry.Name);
                SetNormalStatusText(msg);
                if (isWorkspace)
                {
                    pane.FileList.StatusText = msg;
                }

                UpdateSelectedItemStatus();
                return true;
            }
            catch (Exception ex)
            {
                var failedMsg = _text.Format("RenameFailedPrefix", ex.Message);
                ReportRenameFailure(pane, failedMsg);
                MessageBox.Show(this, ex.Message, _text.Get("RenameFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        finally
        {
            if (renamed)
            {
                SuppressFolderWatchRefreshForSelfOperation("rename");
            }

            _renameInteraction.EndCommit();
            await ProcessPendingFolderWatchRefreshAsync();
            await ProcessPendingDriveListRefreshAsync();
        }
    }

    private string? ValidateRename(FileEntry entry, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return _text.Get("RenameFailedEmpty");
        }

        if (string.Equals(entry.Name, newName, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !string.Equals(Path.GetFileName(newName), newName, StringComparison.Ordinal))
        {
            return _text.Get("RenameFailedInvalid");
        }

        var targetPath = Path.Combine(Path.GetDirectoryName(entry.FullPath) ?? "", newName);
        var fullSourcePath = Path.GetFullPath(entry.FullPath);
        var fullTargetPath = Path.GetFullPath(targetPath);
        if (!string.Equals(fullSourcePath, fullTargetPath, StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(fullTargetPath) || File.Exists(fullTargetPath)))
        {
            return _text.Get("RenameFailedExists");
        }

        return null;
    }

    private void ReportRenameFailure(FolderPane? pane, string message)
    {
        PlayRenameFailureSound();
        SetNormalStatusText(message);
        if (pane is not null && IsWorkspaceDisplayPane(pane))
        {
            pane.FileList.StatusText = message;
        }
    }

    private static void PlayRenameFailureSound()
    {
        try
        {
            SystemSounds.Exclamation.Play();
        }
        catch
        {
            // Audio feedback is best-effort; status text is the primary feedback.
        }
    }

    private async Task DeleteSelectedAsync(FolderPane? targetPane = null)
    {
        if (GetActiveFileOperationContext(targetPane) is not { } context)
        {
            return;
        }

        if (!await EnsureFileOperationPathReadyAsync(context, "delete"))
        {
            return;
        }

        if (SpecialLocationService.IsSpecialUri(context.CurrentPath))
        {
            SetFileOperationStatus(context, _text.Get("OperationUnavailableInSpecialLocation"));
            return;
        }

        var selectedEntries = context.SelectedEntries;
        if (selectedEntries.Count == 0)
        {
            return;
        }

        var confirmMessage = selectedEntries.Count == 1
            ? _text.Format("DeleteConfirmMessage", selectedEntries[0].Name)
            : _text.Format("DeleteConfirmMultipleMessage", selectedEntries.Count);
        var result = MessageBox.Show(
            this,
            confirmMessage,
            _text.Get("DeleteConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.None,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _isFileOperationInProgress = true;
        try
        {
            var deletedCount = selectedEntries.Count;
            var paneItems = GetPaneItems(context.Pane);
            foreach (var entry in selectedEntries)
            {
                await _fileOperationService.DeleteAsync(entry.FullPath, entry.IsDirectory);
                paneItems.Remove(entry);
            }

            _undoService.Clear();

            if (context.IsWorkspace)
            {
                context.Pane.FileList.ItemsView.Refresh();
                context.Pane.SelectedPaths = [];
                if (context.Pane.ActiveTabState is { } state)
                {
                    state.SelectedPaths = [];
                }

                GetFolderPaneListView(context.Pane)?.SelectedItems.Clear();
                context.Pane.FileList.StatusMessagePrefix = _text.Format("DeletedMultiple", deletedCount);
                await ReloadFolderPanesShowingPathAsync(context.CurrentPath, context.Pane);
            }
            else
            {
                RefreshItemsView("undo-clear");
                _statusSummaryCoordinator.StatusMessagePrefix = _text.Format("DeletedMultiple", deletedCount);
                RefreshCurrentFolderSummary();
                UpdateSelectedItemStatus();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, _text.Get("DeleteFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SuppressFolderWatchRefreshForSelfOperation("delete");
            _isFileOperationInProgress = false;
            await ProcessPendingFolderWatchRefreshAsync();
            await ProcessPendingDriveListRefreshAsync();
        }
    }

    private FileOperationContext? GetActiveFileOperationContext(FolderPane? targetPane = null)
    {
        var context = GetActiveNavigationContext(targetPane);
        if (context.Pane is not { } pane || context.Tab is not { } tab)
        {
            return null;
        }

        return new FileOperationContext(
            context.IsWorkspace,
            pane,
            tab,
            tab.Navigation.CurrentPath,
            GetSelectedEntries(pane));
    }

    private async Task<bool> EnsureFileOperationPathReadyAsync(FileOperationContext context, string operationName)
    {
        if (!SpecialLocationService.IsSpecialUri(context.CurrentPath) && context.Tab.IsDisconnected)
        {
            ShowDisconnectedStatus();
            if (context.IsWorkspace)
            {
                context.Pane.FileList.StatusText = StatusText.Text;
            }

            _performanceLogger.Write($"operation-blocked-disconnected operation={operationName} path=\"{context.CurrentPath}\" reason=tab-state");
            return false;
        }

        if (context.IsWorkspace)
        {
            var ready = await EnsurePathReadyForOperationAsync(context.CurrentPath, $"workspace-{operationName}");
            if (!ready)
            {
                context.Pane.FileList.StatusText = StatusText.Text;
            }

            return ready;
        }

        return await EnsurePathReadyForOperationAsync(context.CurrentPath, operationName);
    }

    private async Task SetPendingFileOperationAsync(PendingFileOperationKind operationKind, FolderPane? targetPane = null)
    {
        var operationName = operationKind == PendingFileOperationKind.Copy ? "copy" : "move";
        if (GetActiveFileOperationContext(targetPane) is not { } context)
        {
            return;
        }

        if (!await EnsureFileOperationPathReadyAsync(context, operationName))
        {
            return;
        }

        if (SpecialLocationService.IsSpecialUri(context.CurrentPath))
        {
            SetFileOperationStatus(context, _text.Get("OperationUnavailableInSpecialLocation"));
            return;
        }

        var selectedEntries = context.SelectedEntries;
        if (selectedEntries.Count == 0)
        {
            SetFileOperationStatus(context, operationKind == PendingFileOperationKind.Copy
                ? _text.Get("CopyFailedNoSelection")
                : _text.Get("CutFailedNoSelection"));
            return;
        }

        foreach (var entry in selectedEntries)
        {
            if (!File.Exists(entry.FullPath) && !Directory.Exists(entry.FullPath))
            {
                SetFileOperationStatus(context, _text.Format("OperationFailedSourceMissing", GetOperationName(operationKind)));
                return;
            }
        }

        var operationItems = selectedEntries
            .Select(entry => new PendingFileOperationItem(entry.FullPath, entry.Name, entry.IsDirectory))
            .ToList();
        _pendingFileOperation = new PendingFileOperation(operationItems, operationKind);
        SetFileOperationStatus(context, operationKind == PendingFileOperationKind.Copy
            ? _text.Format("CopyReady", FormatOperationTargetSummary(operationItems))
            : _text.Format("MoveReady", FormatOperationTargetSummary(operationItems)));
    }

    private async Task PastePendingFileOperationAsync(FolderPane? targetPane = null)
    {
        if (GetActiveFileOperationContext(targetPane) is not { } context)
        {
            return;
        }

        if (!await EnsureFileOperationPathReadyAsync(context, "paste"))
        {
            return;
        }

        if (SpecialLocationService.IsSpecialUri(context.CurrentPath))
        {
            SetFileOperationStatus(context, _text.Get("OperationUnavailableInSpecialLocation"));
            return;
        }

        if (_pendingFileOperation is null)
        {
            SetFileOperationStatus(context, _text.Get("PasteFailedNoPending"));
            return;
        }

        var operation = _pendingFileOperation;
        foreach (var item in operation.Items)
        {
            if (!File.Exists(item.SourcePath) && !Directory.Exists(item.SourcePath))
            {
                SetFileOperationStatus(context, _text.Get("PasteFailedSourceMissing"));
                _pendingFileOperation = null;
                return;
            }
        }

        var operationItems = operation.Items
            .Select(item => new FileTransferItem(item.SourcePath, item.Name, item.IsDirectory))
            .ToList();
        var completedPaths = await ExecuteFileTransferAsync(
            operationItems,
            context.CurrentPath,
            operation.Kind,
            refreshActiveFolder: true,
            refreshTab: context.Tab,
            statusContext: context,
            refreshPane: context.Pane);
        var completed = completedPaths.Count > 0;
        if (completed && operation.Kind == PendingFileOperationKind.Move)
        {
            _pendingFileOperation = null;
        }
    }

    private async Task CreateNewItemAsync(NewItemKind kind, FolderPane? targetPane = null)
    {
        if (GetActiveFileOperationContext(targetPane) is not { } context)
        {
            return;
        }

        var operationName = kind == NewItemKind.Folder ? "create-folder" : "create-file";
        if (!await EnsureFileOperationPathReadyAsync(context, operationName))
        {
            return;
        }

        if (SpecialLocationService.IsSpecialUri(context.CurrentPath))
        {
            SetFileOperationStatus(context, _text.Get("OperationUnavailableInSpecialLocation"));
            return;
        }

        _isFileOperationInProgress = true;
        try
        {
            var isDirectory = kind == NewItemKind.Folder;
            var baseName = isDirectory
                ? _text.Get("NewFolderName")
                : _text.Get("NewTextDocumentName");
            var extension = isDirectory ? "" : ".txt";
            var targetPath = _fileOperationService.GetAvailableNewItemPath(context.CurrentPath, baseName, extension, isDirectory);

            if (isDirectory)
            {
                await _fileOperationService.CreateDirectoryAsync(targetPath);
            }
            else
            {
                await _fileOperationService.CreateTextFileAsync(targetPath);
            }

            _undoService.RecordFileOperation(
                UndoOperationKind.Create,
                [new UndoFileOperationItem("", targetPath, isDirectory)]);

            if (context.IsWorkspace)
            {
                context.Pane.FileList.StatusMessagePrefix = _text.Format("CreatedItem", Path.GetFileName(targetPath));
                await ReloadFolderPanesShowingPathAsync(context.CurrentPath, context.Pane);
                SetNormalStatusText(_text.Format("CreatedItem", Path.GetFileName(targetPath)));
                SelectItemInPaneByPath(context.Pane, targetPath);
                BeginRenameSelected(context.Pane);
                _performanceLogger.Write($"folder-pane-create paneId={context.Pane.Id} path=\"{targetPath}\" directory={isDirectory}");
            }
            else
            {
                await LoadFolderAsync(context.CurrentPath, targetTab: context.Tab);
                SelectItemByPath(targetPath);
                BeginRenameSelected();
                SetNormalStatusText(_text.Format("CreatedItem", Path.GetFileName(targetPath)));
            }
        }
        catch (Exception ex)
        {
            var failedMsg = _text.Format("CreateFailedPrefix", ex.Message);
            SetFileOperationStatus(context, failedMsg);
            var title = kind == NewItemKind.Folder
                ? _text.Get("CreateFolderFailedTitle")
                : _text.Get("CreateFileFailedTitle");
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SuppressFolderWatchRefreshForSelfOperation("create");
            _isFileOperationInProgress = false;
            await ProcessPendingFolderWatchRefreshAsync();
            await ProcessPendingDriveListRefreshAsync();
        }
    }

    private async Task UndoLastActionAsync(FolderPane? targetPane = null)
    {
        if (GetActiveFileOperationContext(targetPane) is not { } context)
        {
            return;
        }

        if (!await EnsureFileOperationPathReadyAsync(context, "undo"))
        {
            return;
        }

        var lastAction = _undoService.LastAction;
        if (lastAction is null)
        {
            SetFileOperationStatus(context, _text.Get("UndoNothing"));
            return;
        }

        var affectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (lastAction is RenameUndoAction rename)
            {
                var dir1 = Path.GetDirectoryName(rename.OriginalPath);
                if (!string.IsNullOrEmpty(dir1)) affectedDirs.Add(dir1);
                var dir2 = Path.GetDirectoryName(rename.CurrentPath);
                if (!string.IsNullOrEmpty(dir2)) affectedDirs.Add(dir2);
            }
            else if (lastAction is FileOperationUndoAction fileOp)
            {
                foreach (var item in fileOp.Items)
                {
                    var dir1 = Path.GetDirectoryName(item.OriginalPath);
                    if (!string.IsNullOrEmpty(dir1)) affectedDirs.Add(dir1);
                    var dir2 = Path.GetDirectoryName(item.CurrentPath);
                    if (!string.IsNullOrEmpty(dir2)) affectedDirs.Add(dir2);
                    if (!string.IsNullOrEmpty(item.ReplacedPath))
                    {
                        var dir3 = Path.GetDirectoryName(item.ReplacedPath);
                        if (!string.IsNullOrEmpty(dir3)) affectedDirs.Add(dir3);
                    }
                }
            }
        }
        catch
        {
            // fallback in case of path parsing error
        }

        _isFileOperationInProgress = true;
        try
        {
            var pathsToSelect = await _undoService.UndoLastAsync();

            // Reload all visible panes displaying any affected directories
            foreach (var dir in affectedDirs)
            {
                await ReloadFolderPanesShowingPathAsync(dir, context.Pane);
            }

            // Restore selection for the affected paths in all matching visible panes
            var uniquePanes = _workspaceDisplayPanes.Concat([_primaryPaneGroup]).Distinct().ToList();
            foreach (var pane in uniquePanes)
            {
                if (pane.ActiveTab is { } tab)
                {
                    var paneDir = tab.Navigation.CurrentPath;
                    var pathsForPane = pathsToSelect.Where(p => IsPathInDirectory(p, paneDir)).ToList();
                    if (pathsForPane.Count > 0)
                    {
                        var isTargetPane = ReferenceEquals(pane, context.Pane);
                        var selectedEntries = SelectItemsInPaneByPaths(pane, pathsForPane, focus: isTargetPane);
                        if (IsWorkspaceDisplayPane(pane))
                        {
                            UpdateWorkspacePaneStatusAsync(pane, selectedEntries);
                        }
                    }
                }
            }

            if (context.IsWorkspace)
            {
                context.Pane.FileList.StatusMessagePrefix = _text.Get("UndoComplete");
                _performanceLogger.Write($"folder-pane-undo paneId={context.Pane.Id} path=\"{context.CurrentPath}\" selected={pathsToSelect.Count}");
            }
            else
            {
                _statusSummaryCoordinator.StatusMessagePrefix = _text.Get("UndoComplete");
                RefreshCurrentFolderSummary();
                UpdateSelectedItemStatus();
            }
        }
        catch (Exception ex)
        {
            var failedMsg = _text.Format("UndoFailedPrefix", ex.Message);
            SetFileOperationStatus(context, failedMsg);
            MessageBox.Show(this, ex.Message, _text.Get("UndoFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SuppressFolderWatchRefreshForSelfOperation("undo");
            _isFileOperationInProgress = false;
            await ProcessPendingFolderWatchRefreshAsync();
            await ProcessPendingDriveListRefreshAsync();
        }
    }

    private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateNewItemAsync(NewItemKind.Folder);
    }

    private async void NewFileButton_Click(object sender, RoutedEventArgs e)
    {
        await CreateNewItemAsync(NewItemKind.TextFile);
    }

    private async void WorkspacePaneNewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await CreateNewItemAsync(NewItemKind.Folder, pane);
        }
    }

    private async void WorkspacePaneNewFileButton_Click(object sender, RoutedEventArgs e)
    {
        await ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await CreateNewItemAsync(NewItemKind.TextFile, pane);
        }
    }

    private async void WorkspacePaneDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await DeleteSelectedAsync(pane);
        }
    }

    private void RenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not FileEntry entry || !entry.IsRenaming)
        {
            return;
        }

        _activeRenameTextBox = textBox;
        _renameFocus.FocusAndSelectRenameText(textBox, entry);
    }

    private void RenameTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not FileEntry entry || !entry.IsRenaming)
        {
            return;
        }

        PrepareRenameTextBoxMouseInput(textBox);
        if (!textBox.IsKeyboardFocusWithin)
        {
            textBox.Focus();
            Keyboard.Focus(textBox);
            e.Handled = true;
        }
    }

    private void RenameTextBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox textBox && textBox.DataContext is FileEntry { IsRenaming: true })
        {
            PrepareRenameTextBoxMouseInput(textBox);
        }
    }

    private void RenameTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.DataContext is FileEntry { IsRenaming: true })
        {
            _activeRenameTextBox = textBox;
        }
    }

    private async void RenameTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not FileEntry entry)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _activeRenameTextBoxMouseDown = false;
            entry.RenameText = textBox.Text;
            var committed = await CommitRenameAsync(entry);
            if (!committed)
            {
                textBox.Focus();
                Keyboard.Focus(textBox);
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _activeRenameTextBoxMouseDown = false;
            CancelRename(entry);
            if (FindPaneContainingEntry(entry) is { } pane && GetFolderPaneListView(pane) is { } listView)
            {
                listView.Focus();
            }
            else
            {
                ItemsList.Focus();
            }
        }
    }

    private async void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not FileEntry entry || !entry.IsRenaming)
        {
            return;
        }

        if (_activeRenameTextBoxMouseDown && ReferenceEquals(textBox, _activeRenameTextBox))
        {
            _activeRenameTextBoxMouseDown = false;
            await Dispatcher.InvokeAsync(() =>
            {
                if (entry.IsRenaming)
                {
                    textBox.Focus();
                    Keyboard.Focus(textBox);
                }
            }, DispatcherPriority.ContextIdle);
            return;
        }

        entry.RenameText = textBox.Text;
        var committed = await CommitRenameAsync(entry);
        if (!committed && entry.IsRenaming)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                textBox.Focus();
                Keyboard.Focus(textBox);
            }, DispatcherPriority.ContextIdle);
        }
    }

    private void CancelRename(FileEntry entry, bool showStatus = true)
    {
        if (!entry.IsRenaming)
        {
            return;
        }

        _renameInteraction.BeginCancel();
        try
        {
            entry.CancelRename();
            ClearActiveRenameEntry(entry);
        }
        finally
        {
            _renameInteraction.EndCancel();
        }

        if (showStatus)
        {
            SetNormalStatusText(_text.Get("RenameCanceled"));
        }
    }

    private string FormatOperationTargetSummary(IReadOnlyCollection<PendingFileOperationItem> items)
    {
        return items.Count == 1
            ? items.First().Name
            : _text.Format("MultipleItems", items.Count);
    }

    private enum NewItemKind
    {
        Folder,
        TextFile
    }

    private sealed record FileOperationContext(
        bool IsWorkspace,
        FolderPane Pane,
        FolderTab Tab,
        string CurrentPath,
        IReadOnlyList<FileEntry> SelectedEntries);

    private sealed record PendingFileOperation(IReadOnlyList<PendingFileOperationItem> Items, PendingFileOperationKind Kind);

    private sealed record PendingFileOperationItem(string SourcePath, string Name, bool IsDirectory);

    private enum PendingFileOperationKind
    {
        Copy,
        Move
    }

    private string GetOperationName(PendingFileOperationKind operationKind)
    {
        return operationKind == PendingFileOperationKind.Copy
            ? _text.Get("OperationCopy")
            : _text.Get("OperationMove");
    }
}
