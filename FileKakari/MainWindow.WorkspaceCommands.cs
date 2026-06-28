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
    private void WorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceButton.ContextMenu is not null)
        {
            WorkspaceButton.ContextMenu.PlacementTarget = WorkspaceButton;
            WorkspaceButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            WorkspaceButton.ContextMenu.IsOpen = true;
        }
    }

    private enum WorkspaceButtonState
    {
        Normal,    // 通常フォルダ (定義ファイル無し)
        Available, // 通常フォルダ (定義ファイル有り)
        Active     // Workspaceセッション表示中
    }

    private WorkspaceButtonState GetWorkspaceButtonState()
    {
        var session = GetSelectedWorkspaceButtonSession();
        if (session is null) return WorkspaceButtonState.Normal;

        if (session.IsWorkspace)
        {
            return WorkspaceButtonState.Active;
        }

        var workspacePath = FindOpenableWorkspaceFileForNormalSession(session);
        return workspacePath is not null ? WorkspaceButtonState.Available : WorkspaceButtonState.Normal;
    }

    private WorkspaceSession? GetSelectedWorkspaceButtonSession()
    {
        return GetSelectedWorkspaceSession() ?? _activeWorkspaceSession;
    }

    private static FolderTab? GetSelectedNormalWorkspaceTab(WorkspaceSession? session)
    {
        if (session is null || session.IsWorkspace)
        {
            return null;
        }

        return GetSessionActiveTab(session);
    }

    private static string? GetSelectedNormalWorkspacePath(WorkspaceSession? session)
    {
        var path = GetSelectedNormalWorkspaceTab(session)?.Navigation.CurrentPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private string? FindOpenableWorkspaceFileForNormalSession(WorkspaceSession? session)
    {
        if (GetSelectedNormalWorkspacePath(session) is not { } currentPath)
        {
            return null;
        }

        var workspacePath = _workspaceService.FindWorkspaceFile(currentPath);
        return workspacePath is not null && WorkspaceService.IsWorkspaceFile(workspacePath)
            ? workspacePath
            : null;
    }

    private WorkspaceSession? CreateWorkspaceSaveSessionFromSelectedNormalTab(WorkspaceSession session)
    {
        if (session is null) return null;

        var clonedPaneGroups = new System.Collections.Generic.List<WorkspacePaneGroup>();
        var totalValidTabs = 0;

        foreach (var srcPane in session.PaneGroups)
        {
            var clonedTabs = new System.Collections.ObjectModel.ObservableCollection<FolderTab>();
            var activeIndex = -1;

            for (int i = 0; i < srcPane.Tabs.Count; i++)
            {
                var sourceTab = srcPane.Tabs[i];
                var sourcePath = sourceTab.Navigation.CurrentPath;
                if (string.IsNullOrWhiteSpace(sourcePath)
                    || SpecialLocationService.IsSpecialUri(sourcePath)
                    || !Directory.Exists(sourcePath))
                {
                    continue;
                }

                var state = new WorkspaceTabState(
                    sourcePath,
                    sourceTab.State.Id,
                    AppSettings.NormalizeDisplayMode(sourceTab.State.ViewMode))
                {
                    SortColumn = NormalizeSortColumn(sourceTab.State.SortColumn),
                    SortAscending = sourceTab.State.SortAscending,
                    FilterText = sourceTab.State.FilterText,
                    SelectedPaths = sourceTab.State.SelectedPaths,
                    VerticalOffset = sourceTab.State.VerticalOffset
                };
                var saveTab = new FolderTab(
                    sourcePath,
                    viewMode: AppSettings.NormalizeDisplayMode(sourceTab.State.ViewMode),
                    state: state);
                saveTab.SetFolderLocked(sourceTab.IsFolderLocked);
                clonedTabs.Add(saveTab);

                if (ReferenceEquals(sourceTab, srcPane.ActiveTab))
                {
                    activeIndex = clonedTabs.Count - 1;
                }
            }

            if (clonedTabs.Count > 0)
            {
                if (activeIndex < 0)
                {
                    activeIndex = 0;
                }

                var paneRootPath = clonedTabs[activeIndex].Navigation.CurrentPath;
                var clonedPane = new WorkspacePaneGroup(srcPane.Id, clonedTabs, paneRootPath)
                {
                    SelectedTabIndex = activeIndex,
                    SelectedTabId = clonedTabs[activeIndex].Id
                };
                clonedPaneGroups.Add(clonedPane);
                totalValidTabs += clonedTabs.Count;
            }
        }

        if (clonedPaneGroups.Count == 0)
        {
            return null;
        }

        var firstTab = clonedPaneGroups[0].Tabs[clonedPaneGroups[0].SelectedTabIndex];
        var flatClonedTabs = new System.Collections.ObjectModel.ObservableCollection<FolderTab>();
        foreach (var gp in clonedPaneGroups)
        {
            foreach (var t in gp.Tabs)
            {
                flatClonedTabs.Add(t);
            }
        }

        var saveSession = new WorkspaceSession(
            firstTab.Navigation.CurrentPath,
            flatClonedTabs,
            workspace: null,
            firstTab.State.ViewMode)
        {
            SelectedTabIndex = 0,
            PaneSplitOrientation = session.PaneSplitOrientation
        };
        saveSession.Name = session.Name;

        foreach (var gp in clonedPaneGroups)
        {
            saveSession.PaneGroups.Add(gp);
        }

        var activeGroup = clonedPaneGroups.Find(g => string.Equals(g.Id, session.ActivePaneGroup?.Id, StringComparison.OrdinalIgnoreCase))
            ?? clonedPaneGroups[0];
        saveSession.ActivePaneGroup = activeGroup;
        var clonedPaneIds = clonedPaneGroups
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        saveSession.LayoutRoot = PruneLayoutToPaneIds(session.LayoutRoot, clonedPaneIds)
            ?? BuildLayoutRootFromPaneGroups(clonedPaneGroups, session.PaneSplitOrientation);

        return saveSession;
    }

    private void UpdateWorkspaceButtonState()
    {
        var state = GetWorkspaceButtonState();

        switch (state)
        {
            case WorkspaceButtonState.Active:
                WorkspaceButton.Opacity = 1.0;
                WorkspaceButton.ToolTip = _text.Get("WorkspaceActiveTooltip");
                break;
            case WorkspaceButtonState.Available:
                WorkspaceButton.Opacity = 1.0;
                WorkspaceButton.ToolTip = _text.Get("WorkspaceAvailableTooltip");
                break;
            case WorkspaceButtonState.Normal:
            default:
                WorkspaceButton.Opacity = 0.6; // 通常フォルダ（定義無し）は少し薄くする
                WorkspaceButton.ToolTip = _text.Get("WorkspaceNormalTooltip");
                break;
        }
    }

    private void WorkspaceButtonContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var state = GetWorkspaceButtonState();

        if (state == WorkspaceButtonState.Active)
        {
            var session = GetSelectedWorkspaceButtonSession();
            var hasShared = !string.IsNullOrWhiteSpace(session?.Workspace?.SharedPath);

            WorkspaceButtonHeader.Header = _text.Get("WorkspaceSaveButtonHeader");
            WorkspaceButtonHeader.Visibility = Visibility.Visible;
            WorkspaceButtonSeparator.Visibility = Visibility.Visible;

            OpenWorkspaceMenuItem.Visibility = Visibility.Collapsed;
            SaveNewWorkspaceMenuItem.Visibility = Visibility.Collapsed;

            OverwriteWorkspaceMenuItem.Header = _text.Get("WorkspaceSaveButtonOverwrite");
            OverwriteWorkspaceMenuItem.Visibility = Visibility.Visible;
            OverwriteWorkspaceMenuItem.IsEnabled = hasShared;

            SaveWorkspaceAsMenuItem.Header = _text.Get("WorkspaceSaveButtonSaveAs");
            SaveWorkspaceAsMenuItem.Visibility = Visibility.Visible;

            OpenWorkspaceJsonMenuItem.Header = _text.Get("WorkspaceSaveButtonOpenJson");
            OpenWorkspaceJsonMenuItem.Visibility = Visibility.Visible;
        }
        else if (state == WorkspaceButtonState.Available)
        {
            WorkspaceButtonHeader.Visibility = Visibility.Collapsed;
            WorkspaceButtonSeparator.Visibility = Visibility.Collapsed;

            OpenWorkspaceMenuItem.Header = _text.Get("WorkspaceMenuOpenLocal");
            OpenWorkspaceMenuItem.Visibility = Visibility.Visible;

            SaveNewWorkspaceMenuItem.Header = _text.Get("WorkspaceMenuSaveNew");
            SaveNewWorkspaceMenuItem.Visibility = Visibility.Visible;

            OverwriteWorkspaceMenuItem.Visibility = Visibility.Collapsed;
            SaveWorkspaceAsMenuItem.Visibility = Visibility.Collapsed;
            OpenWorkspaceJsonMenuItem.Visibility = Visibility.Collapsed;
        }
        else // WorkspaceButtonState.Normal
        {
            WorkspaceButtonHeader.Visibility = Visibility.Collapsed;
            WorkspaceButtonSeparator.Visibility = Visibility.Collapsed;

            OpenWorkspaceMenuItem.Visibility = Visibility.Collapsed;

            SaveNewWorkspaceMenuItem.Header = _text.Get("WorkspaceMenuSaveNew");
            SaveNewWorkspaceMenuItem.Visibility = Visibility.Visible;

            OverwriteWorkspaceMenuItem.Visibility = Visibility.Collapsed;
            SaveWorkspaceAsMenuItem.Visibility = Visibility.Collapsed;
            OpenWorkspaceJsonMenuItem.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<bool> SaveNewWorkspaceExplicitAsync(WorkspaceSession selectedSession)
    {
        if (selectedSession is null || selectedSession.IsWorkspace) return false;

        var saveSession = CreateWorkspaceSaveSessionFromSelectedNormalTab(selectedSession);
        if (saveSession is null)
        {
            var noValidTabsMsg = _text.Format("WorkspaceSaveFailed", "No valid folder tabs available (virtual locations like 'This PC' cannot be saved).");
            MessageBox.Show(this, noValidTabsMsg, _text.Get("WorkspaceSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var defaultName = saveSession.Name;
        var dlg = new WorkspaceSaveDialog(defaultName)
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true)
        {
            var success = _workspaceService.SaveWorkspace(saveSession, dlg.WorkspaceName);
            if (success)
            {
                var rootPath = saveSession.RootPath;
                var workspaceFilePath = Path.Combine(rootPath, ".workspace.json");

                var loadSuccess = await OpenWorkspaceFileExplicitAsync(workspaceFilePath);
                if (loadSuccess)
                {
                    SetNormalStatusText(_text.Get("WorkspaceSaveSuccess"));
                    return true;
                }
                else
                {
                    SetNormalStatusText(_text.Get("WorkspaceSaveSuccess"));
                    MessageBox.Show(
                        _text.Get("WorkspaceSaveSuccess") + "\n(Workspace activation failed, remaining in normal mode)",
                        _text.Get("WorkspaceSaveTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return true;
                }
            }
            else
            {
                var errorMsg = _text.Format("WorkspaceSaveFailed", "Write error or invalid path.");
                MessageBox.Show(errorMsg, _text.Get("WorkspaceSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        return false;
    }

    private async void OpenWorkspaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var session = GetSelectedWorkspaceButtonSession();
        if (session is null || session.IsWorkspace) return;

        var targetPath = FindOpenableWorkspaceFileForNormalSession(session);
        if (targetPath is not null)
        {
            await OpenWorkspaceFileExplicitAsync(targetPath);
        }
    }

    private async void SaveNewWorkspaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selectedSession = GetSelectedWorkspaceButtonSession();
        if (selectedSession is null) return;
        await SaveNewWorkspaceExplicitAsync(selectedSession);
    }

    private void OverwriteWorkspaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var session = GetSelectedWorkspaceButtonSession();
        if (session?.Workspace is not { } ws || string.IsNullOrWhiteSpace(ws.SharedPath)) return;

        if (!File.Exists(ws.SharedPath))
        {
            var errorMsg = "Workspace file does not exist. Please use 'Save As' to choose a location.";
            MessageBox.Show(errorMsg, _text.Get("WorkspaceSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            SaveWorkspaceAsMenuItem_Click(sender, e);
            return;
        }

        var success = _workspaceService.SaveWorkspace(session, session.Name, ws.SharedPath);
        if (success)
        {
            SetNormalStatusText(_text.Get("WorkspaceSaveSuccess"));
        }
        else
        {
            var errorMsg = _text.Format("WorkspaceSaveFailed", "Write error.");
            MessageBox.Show(errorMsg, _text.Get("WorkspaceSaveTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveWorkspaceAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var session = GetSelectedWorkspaceButtonSession();
        if (session?.Workspace is not { } ws) return;

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
            var chosenName = Path.GetFileNameWithoutExtension(sfd.FileName);
            if (chosenName.EndsWith(".workspace", StringComparison.OrdinalIgnoreCase))
            {
                chosenName = chosenName.Substring(0, chosenName.Length - ".workspace".Length);
            }
            if (string.IsNullOrWhiteSpace(chosenName))
            {
                chosenName = session.Name;
            }

            var success = _workspaceService.SaveWorkspace(session, chosenName, sfd.FileName);
            if (success)
            {
                var loadSuccess = await OpenWorkspaceFileExplicitAsync(sfd.FileName, forceReplaceCurrentSession: true);
                if (loadSuccess)
                {
                    SetNormalStatusText(_text.Get("WorkspaceSaveSuccess"));
                }
                else
                {
                    SetNormalStatusText(_text.Get("WorkspaceSaveSuccess"));
                    MessageBox.Show(
                        _text.Get("WorkspaceSaveSuccess") + "\n(Failed to switch to the new Workspace session)",
                        _text.Get("WorkspaceSaveTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            else
            {
                var errorMsg = _text.Format("WorkspaceSaveFailed", "Write error.");
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

        if (TryNormalizeSaveDialogDirectory(GetSelectedNormalWorkspacePath(GetSelectedWorkspaceButtonSession())) is { } normalDirectory)
        {
            return normalDirectory;
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

    private void OpenWorkspaceJsonMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var session = GetSelectedWorkspaceButtonSession();
        if (session?.Workspace is not { } ws || string.IsNullOrWhiteSpace(ws.SharedPath)) return;

        try
        {
            if (File.Exists(ws.SharedPath))
            {
                Process.Start(ExternalProcessStartInfo.CreateShellExecute(ws.SharedPath));
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
