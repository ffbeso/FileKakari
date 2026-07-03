using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow
{
    private void SaveWorkspaceMenuItem_Click(WorkspaceSession? session)
    {
        if (session is null || !session.IsWorkspace)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(session.WorkspaceFilePath))
        {
            SaveWorkspaceAsMenuItem_Click(session);
            return;
        }

        var success = _workspaceService.SaveWorkspace(session, session.Name, session.WorkspaceFilePath);
        if (success)
        {
            SaveSessionState();
            SetNormalStatusText(_text.Get("WorkspaceSaveSuccess"));
        }
        else
        {
            var errorMsg = _text.Format("WorkspaceSaveFailed", "Write error.");
            MessageBox.Show(errorMsg, _text.Get("WorkspaceSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveWorkspaceAsMenuItem_Click(WorkspaceSession? session)
    {
        if (session is null || !session.IsWorkspace) return;

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Workspace files (*.workspace.json)|*.workspace.json",
            DefaultExt = ".workspace.json",
            FileName = BuildWorkspaceFileNameCandidate(session.Name),
            Title = _text.Get("WorkspaceMenuSaveAs"),
            InitialDirectory = GetWorkspaceSaveDialogInitialDirectory(session)
        };

        if (sfd.ShowDialog(this) == true)
        {
            var requestedSavePath = sfd.FileName;
            var oldWorkspaceFilePath = session.WorkspaceFilePath;
            var expectedWorkspaceId = session.Workspace?.WorkspaceId;
            _performanceLogger.Write(
                $"workspace-save-as-start sessionId=\"{session.Id}\" " +
                $"workspaceId=\"{expectedWorkspaceId ?? ""}\" oldWorkspaceFilePath=\"{oldWorkspaceFilePath}\" " +
                $"requestedSavePath=\"{requestedSavePath}\" rootPath=\"{session.RootPath}\" " +
                $"isDirty={_workspaceLocalState.IsDirty}");

            var chosenName = Path.GetFileNameWithoutExtension(sfd.FileName);
            if (chosenName.EndsWith(".workspace", StringComparison.OrdinalIgnoreCase))
            {
                chosenName = chosenName.Substring(0, chosenName.Length - ".workspace".Length);
            }
            if (string.IsNullOrWhiteSpace(chosenName))
            {
                chosenName = session.Name;
            }

            try
            {
                var success = _workspaceService.SaveWorkspace(session, chosenName, requestedSavePath);
                if (success)
                {
                    var savedWorkspace = _workspaceService.LoadFromFile(requestedSavePath);
                    var jsonSaveComplete = savedWorkspace is not null
                        && (string.IsNullOrWhiteSpace(expectedWorkspaceId)
                            || string.Equals(savedWorkspace.WorkspaceId, expectedWorkspaceId, StringComparison.Ordinal));
                    var fileExists = File.Exists(requestedSavePath);
                    if (jsonSaveComplete && savedWorkspace is not null)
                    {
                        session.ApplySavedWorkspace(savedWorkspace);
                        if (ReferenceEquals(session, _activeWorkspaceSession))
                        {
                            UpdateActiveWorkspaceSessionUi(session);
                            RefreshWorkspaceDisplayPanes();
                        }

                        SelectWorkspaceSession(session);
                        UpdateWindowTitle();
                        SaveSessionState();
                        _performanceLogger.Write(
                            $"workspace-save-as-complete sessionId=\"{session.Id}\" " +
                            $"workspaceId=\"{session.Workspace?.WorkspaceId ?? ""}\" " +
                            $"newWorkspaceFilePath=\"{session.WorkspaceFilePath}\" " +
                            $"fileExists={fileExists} jsonSaveComplete={jsonSaveComplete} " +
                            $"isDirty={_workspaceLocalState.IsDirty}");
                        SetNormalStatusText(_text.Get("WorkspaceSaveSuccess"));
                    }
                    else
                    {
                        _performanceLogger.Write(
                            $"workspace-save-as-failed reason=\"saved-json-load-or-workspace-id-mismatch\" " +
                            $"exception=\"\" fileExists={fileExists} jsonSaveComplete={jsonSaveComplete} " +
                            $"requestedSavePath=\"{requestedSavePath}\" expectedWorkspaceId=\"{expectedWorkspaceId ?? ""}\" " +
                            $"actualWorkspaceId=\"{savedWorkspace?.WorkspaceId ?? ""}\"");
                        var errorMsg = _text.Format("WorkspaceSaveFailed", "Saved file could not be reloaded or workspaceId changed.");
                        MessageBox.Show(errorMsg, _text.Get("WorkspaceSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    _performanceLogger.Write(
                        $"workspace-save-as-failed reason=\"workspace-service-save-failed\" exception=\"\" " +
                        $"requestedSavePath=\"{requestedSavePath}\"");
                    var errorMsg = _text.Format("WorkspaceSaveFailed", "Write error.");
                    MessageBox.Show(errorMsg, _text.Get("WorkspaceSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _performanceLogger.Write(
                    $"workspace-save-as-failed reason=\"exception\" exception=\"{ex.GetType().FullName}: {ex.Message}\" " +
                    $"requestedSavePath=\"{requestedSavePath}\"");
                var errorMsg = _text.Format("WorkspaceSaveFailed", ex.Message);
                MessageBox.Show(errorMsg, _text.Get("WorkspaceSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static string BuildWorkspaceFileNameCandidate(string workspaceName)
    {
        var name = string.IsNullOrWhiteSpace(workspaceName) ? "Workspace" : workspaceName.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "Workspace" : name;
    }

    private string GetWorkspaceSaveDialogInitialDirectory(WorkspaceSession session)
    {
        if (session.IsWorkspace)
        {
            var activePane = GetWorkspaceSaveDialogActivePane(session);
            if (TryGetWorkspacePaneActiveDirectory(activePane) is { } activePaneDirectory)
            {
                return activePaneDirectory;
            }

            foreach (var pane in session.PaneGroups)
            {
                if (TryGetWorkspacePaneActiveDirectory(pane) is { } paneDirectory)
                {
                    return paneDirectory;
                }
            }
        }

        if (TryNormalizeSaveDialogDirectory(session.RootPath) is { } rootDirectory)
        {
            return rootDirectory;
        }

        if (TryNormalizeSaveDialogDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) is { } documentsDirectory)
        {
            return documentsDirectory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private FolderPane? GetWorkspaceSaveDialogActivePane(WorkspaceSession session)
    {
        var activePane = GetActiveFolderPane();
        if (activePane is not null
            && session.PaneGroups.Any(pane => ReferenceEquals(pane, activePane)))
        {
            return activePane;
        }

        if (session.ActivePaneGroup is not null
            && session.PaneGroups.Any(pane => ReferenceEquals(pane, session.ActivePaneGroup)))
        {
            return session.ActivePaneGroup;
        }

        return session.PaneGroups.FirstOrDefault(pane => string.Equals(pane.Id, session.ActivePaneId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetWorkspacePaneActiveDirectory(FolderPane? pane)
    {
        return TryNormalizeSaveDialogDirectory(pane?.ActiveTab?.Navigation.CurrentPath);
    }

    private static string? TryNormalizeSaveDialogDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || SpecialLocationService.IsSpecialUri(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return null;
            }

            using var enumerator = Directory.EnumerateFileSystemEntries(fullPath).GetEnumerator();
            _ = enumerator.MoveNext();
            return fullPath;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    private void OpenWorkspaceJsonMenuItem_Click(WorkspaceSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.WorkspaceFilePath)) return;

        try
        {
            if (File.Exists(session.WorkspaceFilePath))
            {
                Process.Start(ExternalProcessStartInfo.CreateShellExecute(session.WorkspaceFilePath));
            }
            else
            {
                MessageBox.Show("Workspace file does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open JSON file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenWorkspaceFolderMenuItem_Click(WorkspaceSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.WorkspaceFilePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(session.WorkspaceFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        Process.Start(ExternalProcessStartInfo.CreateShellExecute(directory));
    }

    private async Task<bool> OpenWorkspaceFileExplicitAsync(string workspaceFilePath, bool forceReplaceCurrentSession = false)
    {
        try
        {
            return await OpenWorkspaceFileAsync(workspaceFilePath, forceReplaceCurrentSession);
        }
        catch (Exception ex)
        {
            LogException("explicit-workspace-load", ex);
            return false;
        }
    }

    private void BeginWorkspaceRename(WorkspaceSession? session)
    {
        if (session is null || session.IsRenaming)
        {
            return;
        }

        session.RenameText = session.Name;
        session.IsRenaming = true;
        SelectWorkspaceSession(session);

        _ = Dispatcher.InvokeAsync(() =>
        {
            FocusWorkspaceRenameTextBox(session);
        }, DispatcherPriority.ContextIdle);
    }

    private void CommitWorkspaceRename(WorkspaceSession session)
    {
        var newName = session.RenameText.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            session.RenameText = session.Name;
            session.IsRenaming = false;
            SetNormalStatusText(_text.Get("RenameFailedEmpty"));
            return;
        }

        if (string.Equals(session.Name, newName, StringComparison.Ordinal))
        {
            session.IsRenaming = false;
            return;
        }

        session.Name = newName;
        session.IsRenaming = false;
        _workspaceLocalState.Capture(markDirty: true, reason: "workspace-name");
        SaveSessionState();
    }

    private static void CancelWorkspaceRename(WorkspaceSession session)
    {
        session.RenameText = session.Name;
        session.IsRenaming = false;
    }

    private void FocusWorkspaceRenameTextBox(WorkspaceSession session)
    {
        if (GetTabItem(session) is not { } tabItem)
        {
            return;
        }

        foreach (var textBox in FindVisualChildren<TextBox>(tabItem))
        {
            if (ReferenceEquals(GetWorkspaceSession(textBox.DataContext), session)
                && textBox.Visibility == Visibility.Visible)
            {
                textBox.Focus();
                Keyboard.Focus(textBox);
                textBox.SelectAll();
                return;
            }
        }
    }

    private bool IsKeyboardFocusInsideMainTabs()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused)
        {
            return false;
        }

        return ReferenceEquals(focused, TabsControl)
            || IsDescendantOf(focused, TabsControl)
            || FindVisualParent<TabItem>(focused) is not null;
    }

    private void WorkspaceTabRenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox
            && GetWorkspaceSession(textBox.DataContext) is { IsRenaming: true } session)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (session.IsRenaming)
                {
                    textBox.Focus();
                    Keyboard.Focus(textBox);
                    textBox.SelectAll();
                }
            }, DispatcherPriority.ContextIdle);
        }
    }

    private void WorkspaceTabRenameTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || GetWorkspaceSession(textBox.DataContext) is not { } session)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitWorkspaceRename(session);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelWorkspaceRename(session);
        }
    }

    private void WorkspaceTabRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox
            && GetWorkspaceSession(textBox.DataContext) is { IsRenaming: true } session)
        {
            CommitWorkspaceRename(session);
        }
    }

    private void CommitWorkspaceRenameOnExternalMouseDown(DependencyObject? source)
    {
        if (Keyboard.FocusedElement is not TextBox textBox
            || GetWorkspaceSession(textBox.DataContext) is not { IsRenaming: true } session
            || ReferenceEquals(textBox, source)
            || FindVisualParent<TextBox>(source) is { } clickedTextBox
                && ReferenceEquals(clickedTextBox, textBox))
        {
            return;
        }

        session.RenameText = textBox.Text;
        CommitWorkspaceRename(session);
    }

    private void ToggleWorkspaceLock(WorkspaceSession session)
    {
        if (session is null)
        {
            return;
        }

        session.IsLocked = !session.IsLocked;
        _workspaceLocalState.Capture(markDirty: true, reason: "workspace-lock");
    }

    private void OpenTabInExplorer(FolderTab tab)
    {
        if (SpecialLocationService.IsSpecialUri(tab.Navigation.CurrentPath)
            || !Directory.Exists(tab.Navigation.CurrentPath))
        {
            _performanceLogger.Write($"tab-explorer-open-skip path=\"{tab.Navigation.CurrentPath}\"");
            return;
        }

        var startInfo = new ProcessStartInfo("explorer.exe", tab.Navigation.CurrentPath)
        {
            UseShellExecute = true
        };
        ExternalProcessStartInfo.ApplyWorkingDirectory(startInfo, tab.Navigation.CurrentPath);
        Process.Start(startInfo);
    }

    private TabItem? GetTabItem(WorkspaceSession session)
    {
        return TabsControl.ItemContainerGenerator.ContainerFromItem(GetMainTabItem(session)) as TabItem;
    }

    private void ClearPendingWorkspaceRenameClick()
    {
        _pendingWorkspaceRenameSession = null;
        _pendingWorkspaceRenamePoint = null;
    }

    private void CancelScheduledWorkspaceRenameClick()
    {
        _workspaceRenameClickGeneration++;
    }

    private async void ScheduleWorkspaceRenameFromClick(WorkspaceSession session)
    {
        var generation = ++_workspaceRenameClickGeneration;
        await Task.Delay(WorkspaceRenameClickDelay);
        if (generation != _workspaceRenameClickGeneration
            || !ReferenceEquals(GetSelectedWorkspaceSession(), session)
            || session.IsRenaming
            || _draggedTab is not null
            || TabsControl.IsMouseCaptured
            || Keyboard.FocusedElement is TextBox focusedTextBox
                && GetWorkspaceSession(focusedTextBox.DataContext) is not null)
        {
            return;
        }

        BeginWorkspaceRename(session);
    }

    private bool HasExceededWorkspaceRenamePendingDistance(Point currentPoint)
    {
        return _pendingWorkspaceRenamePoint is { } startPoint
            && (Math.Abs(currentPoint.X - startPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(currentPoint.Y - startPoint.Y) >= SystemParameters.MinimumVerticalDragDistance);
    }
}
