using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace FileKakari;

public partial class MainWindow
{
    public static readonly DependencyProperty WorkspaceDisplayLayoutRootProperty =
        DependencyProperty.Register(
            nameof(WorkspaceDisplayLayoutRoot),
            typeof(WorkspaceLayoutNodeDefinition),
            typeof(MainWindow));



    public WorkspaceLayoutNodeDefinition? WorkspaceDisplayLayoutRoot
    {
        get => (WorkspaceLayoutNodeDefinition?)GetValue(WorkspaceDisplayLayoutRootProperty);
        private set => SetValue(WorkspaceDisplayLayoutRootProperty, value);
    }

    private async Task<bool> OpenWorkspaceFileAsync(string workspaceFilePath, bool forceReplaceCurrentSession = false)
    {
        var workspace = _workspaceService.LoadFromFile(workspaceFilePath);
        if (workspace is null)
        {
            return false;
        }

        if (forceReplaceCurrentSession || ShouldReplaceCurrentNormalSessionWithWorkspace(workspace))
        {
            await ReplaceCurrentSessionWithWorkspaceAsync(workspace, "workspace-file-replaced");
            return true;
        }

        var existingSession = _workspaceSessions.FirstOrDefault(session =>
            string.Equals(session.Workspace?.SharedPath, workspace.SharedPath, StringComparison.OrdinalIgnoreCase));

        if (existingSession is not null)
        {
            SaveAllActiveStates();

            var selectResult = _workspaceController.TrySelectSession(_activeWorkspaceSession, existingSession);
            if (selectResult.Success)
            {
                _isSwitchingTabs = true;
                try
                {
                    _activeWorkspaceSession = existingSession;
                    UpdateActiveWorkspaceSessionUi(existingSession);
                    ApplyWorkspaceSessionToFolderTabs();
                    SelectWorkspaceSession(existingSession);
                }
                finally
                {
                    _isSwitchingTabs = false;
                }

                await RestoreWorkspaceTabAsync(existingSession);
            }
            return true;
        }


        SaveAllActiveStates();
        _loadCancellation?.Cancel();
        ClearLastClosedStates();

        var workspaceSession = _workspaceSessionFactory.Create(workspace);
        _workspacePaneGroups.Clear();
        foreach (var paneGroup in workspaceSession.PaneGroups)
        {
            _workspacePaneGroups.Add(paneGroup);
        }

        var activePaneGroup = workspaceSession.ActivePaneGroup ?? _workspacePaneGroups.FirstOrDefault();

        var addResult = _workspaceController.AddSession(_activeWorkspaceSession, workspaceSession);

        _isSwitchingTabs = true;
        try
        {
            _workspaceSessions.Add(workspaceSession);
            _activeWorkspaceSession = workspaceSession;
            UpdateActiveWorkspaceSessionUi(workspaceSession);
            SelectWorkspaceSession(workspaceSession);
            ApplyWorkspaceSessionToFolderTabs();
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _isSwitchingWorkspacePane = true;
        try
        {
            _workspacePaneUiController.ShowWorkspace(activePaneGroup);
        }
        finally
        {
            _isSwitchingWorkspacePane = false;
        }

        UpdatePathDisplay(workspace.RootPath ?? workspaceSession.RootPath);
        RefreshWorkspaceDisplayPanes();
        _performanceLogger.Write($"workspace-file-opened root=\"{workspace.RootPath ?? ""}\" source=\"{workspace.SourceDirectory}\" name=\"{workspace.Name}\" layout={workspace.Layout.GetType().Name} pane=\"{activePaneGroup?.Id ?? ""}\" panes={_workspacePaneGroups.Count} tabs={activePaneGroup?.Tabs.Count ?? 0} selected={TabsControl.SelectedIndex} shared=\"{workspace.SharedPath ?? ""}\" local=\"{workspace.LocalPath ?? ""}\"");
        await LoadWorkspaceDisplayPanesAsync("viewstate-restore");
        return true;
    }

    private bool ShouldReplaceCurrentNormalSessionWithWorkspace(WorkspaceDefinition workspace)
    {
        return false;
    }

    private async Task ReplaceCurrentSessionWithWorkspaceAsync(WorkspaceDefinition workspace, string logReason)
    {

        SaveAllActiveStates();
        _loadCancellation?.Cancel();
        ClearLastClosedStates();

        var workspaceSession = _workspaceSessionFactory.Create(workspace);
        var activePaneGroup = workspaceSession.ActivePaneGroup ?? workspaceSession.PaneGroups.FirstOrDefault();
        var selectedSession = GetSelectedWorkspaceSession() ?? _activeWorkspaceSession;
        var replaceIndex = selectedSession is not null ? _workspaceSessions.IndexOf(selectedSession) : -1;
        if (replaceIndex < 0)
        {
            replaceIndex = Math.Clamp(TabsControl.SelectedIndex, 0, Math.Max(0, _workspaceSessions.Count - 1));
        }

        _workspacePaneGroups.Clear();
        foreach (var paneGroup in workspaceSession.PaneGroups)
        {
            _workspacePaneGroups.Add(paneGroup);
        }

        _isSwitchingTabs = true;
        try
        {
            if (_workspaceSessions.Count == 0)
            {
                _workspaceSessions.Add(workspaceSession);
            }
            else
            {
                _workspaceSessions[replaceIndex] = workspaceSession;
            }

            _activeWorkspaceSession = workspaceSession;
            UpdateActiveWorkspaceSessionUi(workspaceSession);
            SelectWorkspaceSession(workspaceSession);
            ApplyWorkspaceSessionToFolderTabs();
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _isSwitchingWorkspacePane = true;
        try
        {
            _workspacePaneUiController.ShowWorkspace(activePaneGroup);
        }
        finally
        {
            _isSwitchingWorkspacePane = false;
        }

        UpdatePathDisplay(workspace.RootPath ?? workspaceSession.RootPath);
        RefreshWorkspaceDisplayPanes();
        _performanceLogger.Write($"{logReason} root=\"{workspace.RootPath ?? ""}\" source=\"{workspace.SourceDirectory}\" name=\"{workspace.Name}\" layout={workspace.Layout.GetType().Name} pane=\"{activePaneGroup?.Id ?? ""}\" panes={_workspacePaneGroups.Count} tabs={activePaneGroup?.Tabs.Count ?? 0} selected={TabsControl.SelectedIndex} shared=\"{workspace.SharedPath ?? ""}\" local=\"{workspace.LocalPath ?? ""}\"");
        await LoadWorkspaceDisplayPanesAsync("viewstate-restore");
    }

    private void CaptureActiveWorkspacePaneGroup()
    {
        if (!_activeWorkspaceSession.IsWorkspace)
        {
            _activeWorkspacePaneGroup.SelectedTabIndex = Math.Clamp(
                _activeWorkspacePaneGroup.SelectedTabIndex,
                0,
                Math.Max(0, _activeWorkspacePaneGroup.Tabs.Count - 1));
            return;
        }

        _activeWorkspaceSession.ActivePaneGroup?.RefreshDisplay();
    }

    private void ClearWorkspacePaneContext(bool force = false)
    {
        if (_activeWorkspacePaneGroup.Workspace is null && _workspacePaneGroups.Count == 0)
        {
            return;
        }

        if (!force && _activeWorkspaceSession is { IsLocked: true })
        {
            _performanceLogger.Write("ClearWorkspacePaneContext - Session replacement prevented due to locked workspace.");
            return;
        }

        _workspaceLocalState.SaveActiveLocalState();
        if (ActiveTab is not { } activeTab)
        {
            return;
        }

        activeTab.SetHeaderOverride(null);
        var session = _workspaceController.CreateSinglePaneSession(activeTab);
        var sessionIndex = Math.Clamp(TabsControl.SelectedIndex, 0, Math.Max(0, _workspaceSessions.Count - 1));

        _workspaceSessions[sessionIndex] = session;
        _activeWorkspaceSession = session;
        UpdateActiveWorkspaceSessionUi(session);
        ApplyWorkspaceSessionToFolderTabs();
        SelectWorkspaceSession(session);
        _primaryPaneGroup.SetWorkspace(null);
        _primaryPaneGroup.SelectedTabIndex = 0;
        _isSwitchingWorkspacePane = true;
        try
        {
            _workspacePaneUiController.ShowNormal(clearPaneGroups: true);
        }
        finally
        {
            _isSwitchingWorkspacePane = false;
        }
    }

    private void LoadWorkspacePaneGroup(WorkspacePaneGroup paneGroup)
    {
        var oldPaneId = _activeWorkspaceSession.ActivePaneId;
        _activeWorkspacePaneGroup = paneGroup;
        _lastInteractedWorkspaceDisplayPane = paneGroup;
        _activeWorkspaceSession.ActivePaneGroup = paneGroup;
        _activeWorkspaceSession.ActivePaneId = paneGroup.Id;
        WriteActivePaneRequestLog(
            "workspace-switch",
            oldPaneId,
            paneGroup.Id,
            _activeWorkspaceSession.Id,
            _activeWorkspaceSession.Id,
            GetSelectedWorkspaceSession()?.Id ?? "null",
            accepted: true,
            "accepted");
        EnsureWorkspacePaneHasFallbackTab(paneGroup);
        _workspacePaneUiController.SetActivePaneGroup(paneGroup);
        _activeWorkspaceSession.SelectedTabIndex = Math.Clamp(paneGroup.SelectedTabIndex, 0, Math.Max(0, paneGroup.Tabs.Count));
        ApplyWorkspaceSessionToFolderTabs();
        paneGroup.RefreshDisplay();
        RefreshWorkspaceDisplayPanes();
        UpdateWindowTitle();
        BeginPreviewAwaitingExplicitSelection("workspace-pane-activate");
    }

    private void EnsureWorkspacePaneHasFallbackTab(WorkspacePaneGroup paneGroup)
    {
        if (!_activeWorkspaceSession.IsWorkspace || paneGroup.Tabs.Count > 0)
        {
            return;
        }

        var path = _activeWorkspaceSession.RootPath;
        var tabId = $"tab_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        var state = _activeWorkspaceSession.GetOrCreateTabState(paneGroup.Id, path, FileDisplayMode.Details, "Name", true, id: tabId);
        var fallbackTab = new FolderTab(path, tabId, FileDisplayMode.Details, state);
        paneGroup.Tabs.Add(fallbackTab);
        paneGroup.SelectedTabIndex = 0;
        paneGroup.RefreshDisplay();
    }

    private void RefreshWorkspaceDisplayPanes()
    {
        RefreshWorkspaceDisplayPanes("RefreshWorkspaceDisplayPanes", 0, _activeWorkspaceSession);
    }

    private void RefreshWorkspaceDisplayPanes(string trigger, int switchId = 0, WorkspaceSession? sourceSession = null)
    {
        var displaySession = sourceSession ?? _activeWorkspaceSession;
        if (displaySession is null)
        {
            return;
        }

        WriteWorkspaceLayoutSyncDiagnostics($"{trigger}:before-refresh-display-panes", switchId, displaySession);
        _folderPaneController.RefreshDisplayPanes(
            displaySession,
            isActivePaneActive: true);
        EnsureWorkspaceLayoutRoot(displaySession);
        EnsureWorkspaceDisplayLayoutRoot(displaySession);
        WorkspaceDisplayLayoutRoot = displaySession.DisplayLayoutRoot;
        _workspacePaneUiController.ShowWorkspace(displaySession.ActivePaneGroup);
        SynchronizeWorkspaceSessionHostVisibility(displaySession);
        WriteWorkspaceLayoutSyncDiagnostics($"{trigger}:after-refresh-display-panes", switchId, displaySession);
    }

    private void UpdateWorkspacePaneActiveStates()
    {
        if (_activeWorkspaceSession is null)
        {
            return;
        }

        foreach (var session in _workspaceSessions)
        {
            var isActiveSession = IsSameWorkspaceSession(session, _activeWorkspaceSession);
            foreach (var paneGroup in session.PaneGroups)
            {
                paneGroup.IsActive = isActiveSession && ReferenceEquals(paneGroup, _activeWorkspaceSession.ActivePaneGroup);
            }
        }
    }


    private void WorkspaceSplitPanel_SplitRatioChanged(object? sender, WorkspaceSplitRatioChangedEventArgs e)
    {
        if (sender is not WorkspaceSplitPanel || _activeWorkspaceSession?.LayoutRoot is null)
        {
            return;
        }

        var session = _activeWorkspaceSession;
        var layoutRoot = UpdateWorkspaceSplitRatio(session.LayoutRoot, e.SplitId, e.Ratio, out var updated);
        if (!updated)
        {
            return;
        }

        session.LayoutRoot = layoutRoot;
        session.DisplayLayoutRoot = BuildDisplayLayoutRoot(session);
        WorkspaceDisplayLayoutRoot = session.DisplayLayoutRoot;
    }

    private void WorkspacePaneOperationsButton_Click(object sender, RoutedEventArgs e)
    {
        var pane = GetWorkspacePaneFromSender(sender);
        if (pane is null) return;

        if (sender is Button button && button.ContextMenu is not null)
        {
            button.Tag = pane;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Tag = pane;

            foreach (var item in button.ContextMenu.Items)
            {
                if (item is MenuItem menuItem)
                {
                    menuItem.IsEnabled = true;

                    var headerStr = menuItem.Header?.ToString() ?? "";
                    if (headerStr == "ペインを閉じる" || headerStr == _text.Get("WorkspacePaneMenuClose"))
                    {
                        menuItem.IsEnabled = _activeWorkspaceSession is not null && _activeWorkspaceSession.PaneGroups.Count > 1;
                    }
                }
            }

            UpdateSubTabPlacementMenuItemsChecked(button.ContextMenu, pane.SubTabPlacement);
            button.ContextMenu.IsOpen = true;
        }

        ScheduleWorkspacePaneActivation(pane);
    }

    private void SetSubTabPlacementFromSender(object sender, SubTabPlacement placement)
    {
        if (GetWorkspacePaneFromMenuItem(sender) is { } pane)
        {
            pane.SubTabPlacement = placement;
            _workspaceLocalState.MarkDirty("subtab-placement-change");
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (FindWorkspacePaneSubTabListBox(pane) is { } listBox)
                {
                    BringWorkspacePaneSelectedSubTabIntoView(listBox);
                }
            }, DispatcherPriority.ContextIdle);
        }
    }

    private void WorkspacePaneSubTabPlacementTop_Click(object sender, RoutedEventArgs e) => SetSubTabPlacementFromSender(sender, SubTabPlacement.Top);
    private void WorkspacePaneSubTabPlacementLeft_Click(object sender, RoutedEventArgs e) => SetSubTabPlacementFromSender(sender, SubTabPlacement.Left);
    private void WorkspacePaneSubTabPlacementRight_Click(object sender, RoutedEventArgs e) => SetSubTabPlacementFromSender(sender, SubTabPlacement.Right);
    private void WorkspacePaneSubTabPlacementBottom_Click(object sender, RoutedEventArgs e) => SetSubTabPlacementFromSender(sender, SubTabPlacement.Bottom);

    private void WorkspacePaneSubTabBarContextMenu_Opening(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            var pane = GetWorkspacePaneFromSender(menu.PlacementTarget ?? menu);
            if (pane is not null)
            {
                UpdateSubTabPlacementMenuItemsChecked(menu, pane.SubTabPlacement);
            }
        }
    }

    private static void UpdateSubTabPlacementMenuItemsChecked(ContextMenu menu, SubTabPlacement currentPlacement)
    {
        foreach (var item in menu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Header?.ToString() == "サブタブの配置")
            {
                foreach (var subItem in menuItem.Items)
                {
                    if (subItem is MenuItem subMenu)
                    {
                        var header = subMenu.Header?.ToString() ?? "";
                        subMenu.IsChecked = header switch
                        {
                            string h when h.Contains("Top") || h.Contains("上") => currentPlacement == SubTabPlacement.Top,
                            string h when h.Contains("Left") || h.Contains("左") => currentPlacement == SubTabPlacement.Left,
                            string h when h.Contains("Right") || h.Contains("右") => currentPlacement == SubTabPlacement.Right,
                            string h when h.Contains("Bottom") || h.Contains("下") => currentPlacement == SubTabPlacement.Bottom,
                            _ => false
                        };
                    }
                }
            }
        }
    }

    private void WorkspacePaneSplitRightMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetWorkspacePaneFromMenuItem(sender) is { } pane)
        {
            SplitWorkspacePane(pane, WorkspaceSplitOrientation.Horizontal);
        }
    }

    private void WorkspacePaneSplitDownMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetWorkspacePaneFromMenuItem(sender) is { } pane)
        {
            SplitWorkspacePane(pane, WorkspaceSplitOrientation.Vertical);
        }
    }

    private void WorkspacePaneCloseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetWorkspacePaneFromMenuItem(sender) is { } pane)
        {
            CloseWorkspacePane(pane);
        }
    }

    private async void SplitWorkspacePane(FolderPane pane, WorkspaceSplitOrientation orientation)
    {
        if (_activeWorkspaceSession is not { } session) return;

        var activeTab = pane.ActiveTab;
        if (activeTab is null) return;
        var path = activeTab.Navigation.CurrentPath;
        var paneId = $"pane_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        var tabId = $"tab_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        var state = session.GetOrCreateTabState(paneId, path, activeTab.State.ViewMode, activeTab.State.SortColumn, activeTab.State.SortAscending, id: tabId);
        state.FilterText = activeTab.State.FilterText;
        state.SelectedPaths = activeTab.State.SelectedPaths;
        state.VerticalOffset = activeTab.State.VerticalOffset;
        var clonedTab = new FolderTab(path, tabId, activeTab.State.ViewMode, state);
        clonedTab.SetFolderLocked(activeTab.IsFolderLocked);
        var newPaneTabs = new ObservableCollection<FolderTab> { clonedTab };
        var defaultPlacement = AppSettings.NormalizeSubTabPlacement(_settingsService.Settings.SubTabPlacement);
        var newPaneGroup = new WorkspacePaneGroup(paneId, newPaneTabs, path, defaultPlacement) { SelectedTabIndex = 0, SelectedTabId = tabId };
        if (session.Workspace is not null) newPaneGroup.SetWorkspace(session.Workspace);

        EnsureWorkspaceLayoutRoot(session);

        var layoutReplaced = false;
        var nextLayout = session.LayoutRoot is { } layoutRoot
            ? ReplacePaneInLayout(layoutRoot, pane.Id, newPaneGroup, orientation, out layoutReplaced)
            : null;

        var insertIndex = pane is WorkspacePaneGroup wp ? session.PaneGroups.IndexOf(wp) : -1;
        if (insertIndex >= 0) session.PaneGroups.Insert(insertIndex + 1, newPaneGroup);
        else session.PaneGroups.Add(newPaneGroup);

        session.LayoutRoot = nextLayout is not null && layoutReplaced
            ? nextLayout
            : BuildLayoutRootFromPaneGroups(session.PaneGroups, orientation);

        // DisplayLayoutRoot は LayoutRoot から再構築する
        session.DisplayLayoutRoot = BuildDisplayLayoutRoot(session);

        session.PaneSplitOrientation = orientation;
        session.ActivePaneGroup = newPaneGroup;
        RefreshWorkspaceDisplayPanes();
        await LoadFolderPaneItemsAsync(newPaneGroup, restoreTrigger: "active-pane-change");
        ScheduleSessionSave("split-pane");
        UpdateWindowTitle();
    }

    private void WorkspacePaneFileList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
        {
            try
            {
                WriteWorkspaceListViewDiagnostics("workspace-listview-loaded", listView);
            }
            catch (Exception ex)
            {
                try
                {
                    WriteDiagLog($"event=workspace-listview-diag-error message=\"{ex.Message}\"");
                }
                catch { }
            }

            if (listView.DataContext is FolderPane pane)
            {
                if (!IsPaneOwnedByActiveWorkspaceSession(pane))
                {
                    WriteDiagLog($"event=workspace-listview-loaded-skip reason=inactive-session-pane paneId={pane.Id} paneHash={pane.GetHashCode()} activeSessionId={_activeWorkspaceSession?.Id ?? "null"}");
                    return;
                }

                ApplyDisplayModeToPane(listView, pane);
                ApplyColumnSettingsToWorkspacePane(listView, pane);
                HookWorkspacePaneColumnWidthChanges(listView, pane);
            }
        }
    }

    private void WorkspacePaneFileList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
        {
            UnhookWorkspacePaneColumnWidthChanges(listView);
        }
    }

    private void WorkspacePaneGridViewColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header)
        {
            return;
        }

        if (header.Column is not GridViewColumn column)
        {
            return;
        }

        if (sender is not FrameworkElement element || element.DataContext is not FolderPane pane)
        {
            return;
        }

        var targetState = pane.ActiveTabState;
        if (targetState == null)
        {
            return;
        }

        // Capture current display order before updating sort properties
        var currentOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var item in pane.FileList.ItemsView)
        {
            if (item is FileEntry entry)
            {
                currentOrder[entry.FullPath] = index++;
            }
        }

        string? columnId = null;
        var headerObj = column.Header;
        if (headerObj is string headerText)
        {
            if (headerText == WorkspacePaneColumnNameText) columnId = "Name";
            else if (headerText == WorkspacePaneColumnSizeText) columnId = "Size";
            else if (headerText == WorkspacePaneColumnModifiedText) columnId = "ModifiedAt";
        }
        else if (headerObj is TextBlock textBlock && textBlock.Tag is string tag)
        {
            columnId = tag;
        }

        if (columnId == null)
        {
            return;
        }

        if (targetState.SortColumn == columnId)
        {
            targetState.SortAscending = !targetState.SortAscending;
        }
        else
        {
            targetState.SortColumn = columnId;
            targetState.SortAscending = true;
        }

        using (SuppressPreviewForProgrammaticSelection("reload"))
        {
            pane.FileList.ApplySort(
                targetState.SortColumn,
                targetState.SortAscending,
                _settingsService.Settings.SortFoldersFirst,
                currentOrder);
        }

        _folderPaneController.UpdateStatus(pane);
        pane.RefreshDisplay();

        if (sender is ListView listView)
        {
            UpdateWorkspacePaneColumnHeaders(listView, pane);
        }

        _workspaceLocalState.MarkDirty("sort");
    }

    private void UpdateWorkspacePaneColumnHeadersForPane(FolderPane pane)
    {
        var listView = FindListViewForPane(pane);
        if (listView is not null)
        {
            UpdateWorkspacePaneColumnHeaders(listView, pane);
        }
    }

    private void UpdateWorkspacePaneColumnHeaders(ListView listView, FolderPane pane)
    {
        if (listView.View is GridView gridView)
        {
            UpdateWorkspacePaneColumnHeaders(gridView, pane);
        }
    }

    private void UpdateWorkspacePaneColumnHeaders(GridView gridView, FolderPane pane)
    {
        var targetState = pane.ActiveTabState;
        var path = pane.ActiveTab?.Navigation.CurrentPath ?? "";
        bool isSpecial = SpecialLocationService.IsSpecialUri(path);

        foreach (var column in gridView.Columns)
        {
            if (column.Header is TextBlock textBlock && textBlock.Tag is string columnId)
            {
                var normalizedId = ColumnLayoutService.NormalizeColumnId(columnId);
                var resourceKey = GetHeaderResourceKey(normalizedId, isSpecial);
                var baseText = _text.Get(resourceKey);

                if (targetState is not null && string.Equals(targetState.SortColumn, normalizedId, StringComparison.OrdinalIgnoreCase))
                {
                    textBlock.Text = baseText + (targetState.SortAscending ? " ↑" : " ↓");
                }
                else
                {
                    textBlock.Text = baseText;
                }
            }
        }
    }

    private void WorkspacePaneFilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox
            || GetWorkspacePaneFromSender(sender) is not { } pane
            || pane.ActiveTabState is not { } state)
        {
            return;
        }

        var oldFilter = state.FilterText;
        var filterChanged = !string.Equals(oldFilter, textBox.Text, StringComparison.Ordinal);
        state.FilterText = textBox.Text;
        LogFilterTextChanged(pane, state, oldFilter, state.FilterText, nameof(WorkspacePaneFilterBox_TextChanged), "workspace-pane-filter-box");
        if (filterChanged)
        {
            ScheduleWorkspacePaneFilterApply(pane, state, state.FilterText);
            _workspaceLocalState.MarkDirty("pane-filter");
        }
    }

    private async void WorkspacePaneFileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CancelPendingRenameClick();
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible
            || sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        LogListViewClick(listView, pane, e, source);
        if (e.ChangedButton != MouseButton.Left
            || IsInsideScrollBar(source)
            || FindVisualParent<GridViewColumnHeader>(source) is not null)
        {
            return;
        }

        var entry = FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
        if (entry is null)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        if (entry.IsDirectory)
        {
            await NavigateWorkspacePaneToFolderAsync(pane, entry.FullPath, NavigationKind.New);
            return;
        }

        await OpenWorkspacePaneFileAsync(entry);
    }

    private async Task OpenWorkspacePaneSelectionAsync(FolderPane pane, FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            await NavigateWorkspacePaneToFolderAsync(pane, entry.FullPath, NavigationKind.New);
            return;
        }

        await OpenWorkspacePaneFileAsync(entry);
    }

    private async Task OpenWorkspacePaneFileAsync(FileEntry entry)
    {
        if (!File.Exists(entry.FullPath))
        {
            StatusText.Text = _text.Get("OpenFailedMissing");
            return;
        }

        if (WorkspaceService.IsWorkspaceFile(entry.FullPath)
            && await OpenWorkspaceFileAsync(entry.FullPath))
        {
            return;
        }

        try
        {
            Process.Start(ExternalProcessStartInfo.CreateShellExecute(entry.FullPath, entry.ParentPath));
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            _performanceLogger.Write($"folder-pane-file-open-failed path=\"{entry.FullPath}\" error=\"{ex.Message}\"");
            MessageBox.Show(this, ex.Message, _text.Get("OpenFileFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void WorkspacePaneFileList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            return;
        }

        var activePaneChanged = !ReferenceEquals(pane, _activeWorkspaceSession?.ActivePaneGroup);
        var isAccepted = TryRequestActivePane(pane, "listview", listView);
        if (!isAccepted)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            PrepareWorkspacePaneFileListLeftMouseDown(listView, pane, e);
        }

        ScheduleWorkspacePaneActivation(pane, refreshPreviewIfAlreadyActivated: activePaneChanged);
    }

    private async void WorkspacePaneFileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _workspacePendingRangeSelectionClickEntry is { } pendingEntry)
        {
            var entry = pendingEntry;
            var pendingListView = _workspacePendingRangeSelectionListView;
            var pendingPane = _workspacePendingRangeSelectionPane;
            ClearFileDragStart();

            if (pendingListView is not null && pendingPane is not null && IsPaneOwnedByActiveWorkspaceSession(pendingPane))
            {
                var modifiers = Keyboard.Modifiers;
                var hasControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                var hasShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                if (hasShift)
                {
                    FileListSelectionHelper.PerformShiftSelection(pendingListView, _workspaceSelectionAnchorEntry, entry, hasControl);
                }
                else
                {
                    if (hasControl)
                    {
                        FileListSelectionHelper.ApplyControlSelection(pendingListView, entry);
                        _workspaceSelectionAnchorEntry = entry;
                    }
                    else
                    {
                        FileListSelectionHelper.ApplySingleSelection(pendingListView, entry);
                        _workspaceSelectionAnchorEntry = entry;
                    }
                }

                pendingListView.Focus();
                SyncPaneSelectionFromListView(pendingPane, pendingListView);
                CompletePreviewExplicitMouseSelectionCandidate(
                    pendingListView,
                    pendingPane,
                    e,
                    "workspace-pane-mouse-up-range-selection");
            }
            e.Handled = true;
            return;
        }

        if (sender is ListView upListView
            && upListView.DataContext is FolderPane upPane
            && _renameInteraction.PendingClickEntry is { } renameEntry)
        {
            CancelPreviewExplicitMouseSelectionCandidate("rename-pending");
            if (!IsPaneOwnedByActiveWorkspaceSession(upPane))
            {
                ClearPendingRenameClick();
                ClearFileDragStart();
                return;
            }

            var currentPoint = e.GetPosition(upListView);
            var startPoint = _renameInteraction.PendingClickPoint;
            ClearPendingRenameClick();
            ClearFileDragStart();

            if (startPoint is not null
                && Math.Abs(currentPoint.X - startPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(currentPoint.Y - startPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance
                && upListView.SelectedItems.Count == 1
                && upListView.SelectedItems.Contains(renameEntry)
                && e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                await BeginRenameAfterClickDelayAsync(renameEntry, _renameInteraction.AdvanceGeneration(), upPane);
                return;
            }
        }

        if (CommitPendingSelection(sender, e))
        {
            if (sender is ListView selectionListView
                && selectionListView.DataContext is FolderPane selectionPane)
            {
                CompletePreviewExplicitMouseSelectionCandidate(
                    selectionListView,
                    selectionPane,
                    e,
                    "workspace-pane-mouse-up-pending-selection");
            }
            e.Handled = true;
            return;
        }

        if (TryApplyWorkspacePanePendingSingleSelectionClick(sender, e))
        {
            if (sender is ListView selectionListView
                && selectionListView.DataContext is FolderPane selectionPane)
            {
                CompletePreviewExplicitMouseSelectionCandidate(
                    selectionListView,
                    selectionPane,
                    e,
                    "workspace-pane-mouse-up-pending-single-selection");
            }
            e.Handled = true;
            return;
        }

        if (sender is ListView listView
            && listView.DataContext is FolderPane pane)
        {
            CompletePreviewExplicitMouseSelectionCandidate(
                listView,
                pane,
                e,
                "workspace-pane-mouse-up");
        }
    }

    private void WorkspacePaneFileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible
            || sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            return;
        }

        if (IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var displayMode = AppSettings.NormalizeDisplayMode(pane.ActiveTabState?.ViewMode ?? _settingsService.Settings.DisplayMode);
        var clickedEntry = FileListHitTestService.GetFileEntryFromDisplayedContentHitTarget(source, displayMode);
        PreparePaneRightClickSelection(pane, listView, clickedEntry);
        e.Handled = true;
        ShowWorkspacePaneContextMenu(pane, listView, clickedEntry);
    }

    private void DrawWorkspacePaneRangeSelection(ListView listView, Rect selectionRect)
    {
        EnsureWorkspaceRangeSelectionAdorner(listView);
        _workspaceRangeSelectionAdorner?.Update(selectionRect);
    }

    private void ClearWorkspacePaneRangeSelection()
    {
        if (_workspaceRangeSelectionSession?.ListView?.IsMouseCaptured == true)
        {
            _workspaceRangeSelectionSession.ListView.ReleaseMouseCapture();
        }

        _workspaceRangeSelectionPane = null;
        _workspaceRangeSelectionSession = null;
        ClearWorkspaceRangeSelectionAdorner();
    }

    private void WorkspacePaneFileList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            StopFileListDragAutoScroll();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var resolvedListView = GetFolderPaneListView(pane);
        var scrollViewer = ReferenceEquals(resolvedListView, listView)
            ? FindVisualChild<ScrollViewer>(listView)
            : null;
        UpdateFileListDragAutoScroll(pane, listView, scrollViewer, e);

        if (GetMainTabDemotionTarget(e, FindSessionContainingPane(pane), pane) is not null)
        {
            e.Effects = DragDropEffects.Move;
            ClearFileDropHighlight();
            e.Handled = true;
            return;
        }

        var targetDirectory = GetWorkspacePaneFileDropTargetDirectory(pane, e);
        if (targetDirectory is null)
        {
            e.Effects = DragDropEffects.None;
            ClearFileDropHighlight();
            e.Handled = true;
            return;
        }

        var dragItems = GetFileOperationDragItems(e);
        var operationKind = GetFileDropOperationKind(e, targetDirectory);
        if (dragItems is null || !CanDropFileItems(dragItems, targetDirectory, operationKind))
        {
            e.Effects = DragDropEffects.None;
            ClearFileDropHighlight();
            e.Handled = true;
            return;
        }

        e.Effects = operationKind == PendingFileOperationKind.Copy
            ? DragDropEffects.Copy
            : DragDropEffects.Move;
        HighlightWorkspacePaneFileDropTarget(listView, e);
        e.Handled = true;
    }

    private void WorkspacePaneFileList_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListView listView)
        {
            StopFileListDragAutoScrollIfPointerOutside(listView, e);
        }

        ClearFileDropHighlight();
    }

    private async void WorkspacePaneFileList_Drop(object sender, DragEventArgs e)
    {
        StopFileListDragAutoScroll();
        ClearFileDropHighlight();
        if (sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (GetMainTabDemotionTarget(e, FindSessionContainingPane(pane), pane) is { } demotionTarget)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            await DemoteMainTabToSubTabAsync(
                demotionTarget.DraggedSession,
                demotionTarget.TargetPane,
                demotionTarget.TargetPane.Tabs.Count);
            return;
        }

        if (GetWorkspacePaneFileDropTargetDirectory(pane, e) is not { } targetDirectory)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dragItems = GetFileOperationDragItems(e);
        var operationKind = GetFileDropOperationKind(e, targetDirectory);
        if (dragItems is null || !CanDropFileItems(dragItems, targetDirectory, operationKind))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = operationKind == PendingFileOperationKind.Copy
            ? DragDropEffects.Copy
            : DragDropEffects.Move;
        e.Handled = true;

        var targetSession = FindSessionContainingPane(pane);
        var sourceSession = _fileDragSourceSession ?? ActiveSession;
        _performanceLogger.Write(
            $"drag-drop-executed " +
            $"dragSourceSessionId={sourceSession?.Id ?? "unknown"} " +
            $"dragTargetSessionId={targetSession?.Id ?? "unknown"} " +
            $"dropTargetPath=\"{targetDirectory}\" " +
            $"refreshAfterDrop=true");

        var transferItems = dragItems
            .Select(item => new FileTransferItem(item.SourcePath, item.Name, item.IsDirectory))
            .ToList();
        await ExecuteFileTransferAsync(
            transferItems,
            targetDirectory,
            operationKind,
            refreshActiveFolder: true,
            refreshTab: pane.ActiveTab,
            confirmNonSelfCopy: IsExplicitCopyDrop(e),
            refreshPane: pane);
    }

    private void HighlightWorkspacePaneFileDropTarget(ListView listView, DragEventArgs e)
    {
        if (GetFileDropTargetEntry(e) is { } targetEntry)
        {
            HighlightFileDropTarget(listView, targetEntry);
            return;
        }

        ClearFileDropHighlight();
    }

    private async void WorkspacePaneSubTabBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeWorkspaceSession is not null
            && e.ChangedButton == MouseButton.Left)
        {
            await ActivateWorkspacePaneFromSenderAsync(sender);
        }

        if (e.ChangedButton == MouseButton.Middle
            && sender is ListBox closeListBox
            && closeListBox.DataContext is FolderPane closePane
            && FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } closeItem
            && closeItem.DataContext is FolderTab closeTab)
        {
            await CloseWorkspacePaneSubTabAsync(closePane, closeTab, closeListBox);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left
            && e.ClickCount == 1
            && sender is ListBox dragListBox
            && dragListBox.DataContext is FolderPane dragPane
            && FindVisualParent<Button>(e.OriginalSource as DependencyObject) is null
            && FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } dragItem
            && dragItem.DataContext is FolderTab dragTab)
        {
            _subTabDragStartPoint = e.GetPosition(dragListBox);
            _draggedSubTabPane = dragPane;
            _draggedSubTab = dragTab;
        }

        if (e.ChangedButton != MouseButton.Left
            || e.ClickCount != 2
            || sender is not ListBox listBox
            || listBox.DataContext is not FolderPane pane)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var listItem = FindVisualParent<ListBoxItem>(source);
        if (listItem is not null)
        {
            // Close button click should not trigger lock toggling
            if (FindVisualParent<Button>(source) is null && listItem.DataContext is FolderTab tab)
            {
                e.Handled = true;
                ToggleWorkspacePaneSubTabLock(pane, tab);
            }
            return;
        }

        e.Handled = true;
        await CreateWorkspacePaneSubTabAsync(pane);
    }

    private async void WorkspacePaneSubTabBarBackground_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsWorkspacePaneSubTabBarBackgroundInput(e.OriginalSource as DependencyObject)
            || !TryGetWorkspacePaneSubTabBarTarget(sender, out _, out var pane)
            || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (_activeWorkspaceSession is not null)
        {
            await ActivateWorkspacePaneFromSenderAsync(sender);
        }

        if (e.ClickCount != 2)
        {
            return;
        }

        e.Handled = true;
        await CreateWorkspacePaneSubTabAsync(pane);
    }

    private void WorkspacePaneSubTabBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_subTabDragStartPoint is null
            || _draggedSubTabPane is null
            || _draggedSubTab is null
            || sender is not ListBox listBox
            || !ReferenceEquals(listBox.DataContext, _draggedSubTabPane)
            || e.LeftButton != MouseButtonState.Pressed)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ClearSubTabDragState();
            }
            return;
        }

        var position = e.GetPosition(listBox);
        if (Math.Abs(position.X - _subTabDragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _subTabDragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        try
        {
            var data = new DataObject(SubTabDragFormat, _draggedSubTab);
            e.Handled = true;
            DragDrop.DoDragDrop(listBox, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            ClearSubTabDragState();
            ClearWorkspacePaneSubTabHover();
            ClearMainTabHover();
            HideTabInsertIndicator();
        }
    }

    private void WorkspacePaneSubTabBar_DragOver(object sender, DragEventArgs e)
    {
        if (!TryGetWorkspacePaneSubTabBarTarget(sender, out var listBox, out var pane))
        {
            StopWorkspacePaneSubTabAutoScroll();
            e.Effects = DragDropEffects.None;
            return;
        }

        if (CanDropSubTab(pane, e))
        {
            ClearWorkspacePaneSubTabHover();
            ShowTabInsertIndicator(listBox, GetWorkspacePaneSubTabDragInsertDropTarget(listBox, pane, e));
            UpdateWorkspacePaneSubTabAutoScroll(pane, listBox, e, SubTabAutoScrollDragKind.SubTab);

            if (e.Data.GetDataPresent(TabDragFormat))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                var isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
                e.Effects = isCopy ? DragDropEffects.Copy : DragDropEffects.Move;
            }
            e.Handled = true;
            return;
        }

        if (GetWorkspacePaneSubTabFolderDropPath(e) is { } folderPath)
        {
            var insertTarget = GetWorkspacePaneSubTabInsertDropTarget(listBox, pane, e);
            if (insertTarget.IsInsert)
            {
                e.Effects = DragDropEffects.Link;
                ClearWorkspacePaneSubTabHover();
                ShowTabInsertIndicator(listBox, insertTarget);
                UpdateWorkspacePaneSubTabAutoScroll(pane, listBox, e, SubTabAutoScrollDragKind.Folder);
            }
            else
            {
                StopWorkspacePaneSubTabAutoScroll();
                e.Effects = DragDropEffects.None;
                HideTabInsertIndicator();
                QueueWorkspacePaneSubTabHover(sender, e);
            }
            e.Handled = true;
            return;
        }

        if (GetWorkspacePaneSubTabFileDropTarget(e) is { } fileDropTarget)
        {
            StopWorkspacePaneSubTabAutoScroll();
            HideTabInsertIndicator();
            QueueWorkspacePaneSubTabHover(sender, e);
            var dragItems = GetFileOperationDragItems(e);
            var operationKind = GetFileDropOperationKind(e, fileDropTarget.Navigation.CurrentPath);
            e.Effects = dragItems is not null
                && CanDropFileItems(dragItems, fileDropTarget.Navigation.CurrentPath, operationKind)
                    ? operationKind == PendingFileOperationKind.Copy
                        ? DragDropEffects.Copy
                        : DragDropEffects.Move
                    : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        StopWorkspacePaneSubTabAutoScroll();
        HideTabInsertIndicator();
        QueueWorkspacePaneSubTabHover(sender, e);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void WorkspacePaneSubTabBar_DragLeave(object sender, DragEventArgs e)
    {
        StopWorkspacePaneSubTabAutoScroll();
        ClearWorkspacePaneSubTabHover();
        HideTabInsertIndicator();
    }

    private void QueueWorkspacePaneSubTabHover(object sender, DragEventArgs e)
    {
        if (sender is not ListBox { DataContext: FolderPane pane }
            || FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not FolderTab tab
            || !IsWorkspacePaneSubTabHoverDrag(e)
            || ReferenceEquals(pane.ActiveTab, tab))
        {
            ClearWorkspacePaneSubTabHover();
            return;
        }

        if (ReferenceEquals(_subTabHoverPane, pane)
            && ReferenceEquals(_subTabHoverTarget, tab)
            && _subTabHoverTimer.IsEnabled)
        {
            return;
        }

        _subTabHoverPane = pane;
        _subTabHoverTarget = tab;
        _subTabHoverTimer.Stop();
        _subTabHoverTimer.Start();
    }

    private void SubTabHoverTimer_Tick(object? sender, EventArgs e)
    {
        _subTabHoverTimer.Stop();
        var pane = _subTabHoverPane;
        var tab = _subTabHoverTarget;
        _subTabHoverPane = null;
        _subTabHoverTarget = null;

        if (pane is null
            || tab is null
            || !IsWorkspaceDisplayPane(pane)
            || !pane.Tabs.Contains(tab)
            || ReferenceEquals(pane.ActiveTab, tab))
        {
            return;
        }

        pane.SelectedTabId = tab.Id;
    }

    private void ClearWorkspacePaneSubTabHover()
    {
        _subTabHoverPane = null;
        _subTabHoverTarget = null;
        _subTabHoverTimer.Stop();
    }

    private void ClearSubTabDragState()
    {
        StopWorkspacePaneSubTabAutoScroll();
        _subTabDragStartPoint = null;
        _draggedSubTabPane = null;
        _draggedSubTab = null;
        HideTabInsertIndicator();
    }

    private async Task CreateWorkspacePaneSubTabAtAsync(
        FolderPane pane,
        string path,
        int insertIndex)
    {
        if (_activeWorkspaceSession is null)
        {
            return;
        }

        var activeTab = pane.ActiveTab ?? pane.Tabs.FirstOrDefault();
        if (activeTab is null)
        {
            return;
        }
        var paneCurrentPath = activeTab.Navigation.CurrentPath ?? _activeWorkspaceSession.RootPath;
        var paneNewTabPath = _tabOperations.ResolveNewTabPath(path, paneCurrentPath);

        var newTab = _tabOperations.CreateNewTab(paneNewTabPath, activeTab);
        SeedNewTabCache(newTab, activeTab);

        var clampedIndex = Math.Clamp(insertIndex, 0, pane.Tabs.Count);
        pane.Tabs.Insert(clampedIndex, newTab);
        pane.SelectedTabId = newTab.Id;
        pane.ResolveTabHeaders();
        pane.RefreshDisplay();
        RestoreWorkspacePaneSubTabSelection(FindWorkspacePaneSubTabListBox(pane), pane, newTab);

        await _navigationController.NavigateWorkspacePaneToFolderAsync(pane, newTab.Navigation.CurrentPath, NavigationKind.New);
        _workspaceLocalState.QueueCapture(markDirty: true, reason: "new-subtab");
    }

    private static WorkspaceLayoutNodeDefinition UpdateWorkspaceSplitRatio(
        WorkspaceLayoutNodeDefinition node,
        string splitId,
        double ratio,
        out bool updated)
    {
        if (node is not WorkspaceSplitNodeDefinition split)
        {
            updated = false;
            return node;
        }

        if (string.Equals(split.Id, splitId, StringComparison.Ordinal))
        {
            updated = true;
            return split with { Ratio = Math.Clamp(ratio, 0.1, 0.9) };
        }

        var first = UpdateWorkspaceSplitRatio(split.First, splitId, ratio, out var firstUpdated);
        if (firstUpdated)
        {
            updated = true;
            return split with { First = first };
        }

        var second = UpdateWorkspaceSplitRatio(split.Second, splitId, ratio, out var secondUpdated);
        updated = secondUpdated;
        return secondUpdated ? split with { Second = second } : split;
    }

    private async Task LoadWorkspaceDisplayPanesAsync(string restoreTrigger = "viewstate-restore")
    {
        var shouldMarkRestoreInProgress = string.Equals(restoreTrigger, "viewstate-restore", StringComparison.Ordinal)
            && !IsWorkspaceSwitchRestoreInProgress;
        if (shouldMarkRestoreInProgress)
        {
            _workspaceSwitchRestoreDepth++;
        }

        try
        {
            await _folderPaneController.LoadDisplayPanesAsync(Dispatcher);
            UpdateFolderWatchForWorkspacePanes();

            if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
            {
                foreach (var pane in _workspaceDisplayPanes)
                {
                    await RestoreWorkspacePaneStateAsync(pane, FileListRestorePolicy.ExactRestore, restoreTrigger);
                }
            }
        }
        finally
        {
            if (shouldMarkRestoreInProgress && _workspaceSwitchRestoreDepth > 0)
            {
                _workspaceSwitchRestoreDepth--;
            }
        }
    }

    private async Task LoadWorkspaceDisplayPanesOnSwitchAsync(
        int switchId,
        WorkspaceSession workspaceSession,
        IReadOnlyList<WorkspacePaneGroup> targetPanes,
        CancellationToken cancellationToken)
    {
        if (!CanApplyWorkspaceSwitch(switchId, workspaceSession))
        {
            WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, workspaceSession.Id, "stale-switch");
            return;
        }

        var loadTasks = new List<Task>();
        foreach (var pane in targetPanes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CanApplyWorkspaceSwitch(switchId, workspaceSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, workspaceSession.Id, "stale-switch");
                return;
            }

            var targetState = pane.ActiveTabState;
            if (targetState is null) continue;

            if (!pane.IsActiveStateLoaded)
            {
                WriteDiagLog($"event=pane-load-schedule switchId={switchId} workspaceSessionId={workspaceSession.Id} paneId={pane.Id} paneHash={pane.GetHashCode()} path=\"{targetState.CurrentPath}\" stateId={targetState.Id} itemsCountBefore={pane.FileList.Items.Count} pendingExternalChange={targetState.HasPendingExternalChange} loadedPath=\"{pane.FileList.LoadedPath ?? ""}\" loadedStateId=\"{pane.FileList.LoadedStateId ?? ""}\" firstItem=\"{GetPaneFirstItemPath(pane)}\"");
                loadTasks.Add(LoadFolderPaneItemsAsync(pane, cancellationToken, FileListRestorePolicy.ExactRestore, "workspace-switch", switchId));
            }
            else
            {
                WriteDiagLog($"event=pane-load-skip switchId={switchId} workspaceSessionId={workspaceSession.Id} paneId={pane.Id} paneHash={pane.GetHashCode()} path=\"{targetState.CurrentPath}\" stateId={targetState.Id} itemsCount={pane.FileList.Items.Count} loadedPath=\"{pane.FileList.LoadedPath ?? ""}\" loadedStateId=\"{pane.FileList.LoadedStateId ?? ""}\" firstItem=\"{GetPaneFirstItemPath(pane)}\"");
            }
        }
        if (loadTasks.Count > 0)
        {
            await Task.WhenAll(loadTasks);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!CanApplyWorkspaceSwitch(switchId, workspaceSession))
        {
            WriteWorkspaceSwitchLog("workspace-switch-discard", switchId, workspaceSession.Id, "stale-switch");
            return;
        }

        UpdateFolderWatchForWorkspacePanes();
    }

    private static string GetPaneFirstItemPath(FolderPane pane)
    {
        return pane.FileList.Items.FirstOrDefault()?.FullPath ?? "";
    }

    private Task LoadFolderPaneItemsAsync(
        FolderPane pane,
        FileListRestorePolicy policy = FileListRestorePolicy.ExactRestore,
        string restoreTrigger = "pane-load-complete")
    {
        return LoadFolderPaneItemsAsync(pane, CancellationToken.None, policy, restoreTrigger, 0);
    }

    internal async Task ClearWorkspacePaneAfterFolderLoadFailureAsync(
        FolderPane pane,
        string requestedPath,
        string source,
        Exception? exception = null)
    {
        var state = pane.ActiveTabState;
        var session = FindSessionContainingPane(pane);
        if (state is null || session is null || !_workspaceSessions.Contains(session))
        {
            _performanceLogger.Write($"folder-pane-load-failure-skip source={source} paneId={pane.Id} requestedPath=\"{requestedPath}\" skipReason=owner-missing");
            return;
        }

        var generation = pane.FileList.BeginLoad(state.Id, requestedPath);
        if (!ReferenceEquals(pane.ActiveTabState, state) || !pane.FileList.IsLoadCurrent(generation, state.Id, requestedPath))
        {
            _performanceLogger.Write($"folder-pane-load-failure-skip source={source} sessionId={session.Id} paneId={pane.Id} stateId={state.Id} requestedPath=\"{requestedPath}\" generation={generation} skipReason=owner-or-generation-mismatch");
            return;
        }

        pane.FileList.ClearItemsAfterLoadFailure(requestedPath);
        pane.FileList.StatusText = exception?.Message ?? _text.Format("PathNotFound", requestedPath);
        pane.FileList.IsLoading = false;
        state.ClearItems();
        state.ClearPendingExternalChange();
        DiscardPendingFolderWatchRefreshesForFailedPane(session, pane, state);
        GetFolderPaneListView(pane)?.SelectedItems.Clear();
        await RestoreWorkspacePaneScrollOffsetAsync(pane, 0);
        pane.RefreshDisplay();

        var previewCleared = false;
        if (IsCurrentActiveSessionAndPane(session, out _, pane))
        {
            await CancelAndClearPreviewAsync("workspace-pane-navigation-failure");
            previewCleared = true;
        }

        _performanceLogger.Write($"folder-pane-load-failure-cleared source={source} sessionId={session.Id} paneId={pane.Id} stateId={state.Id} requestedPath=\"{requestedPath}\" generation={generation} exceptionType={exception?.GetType().FullName ?? "none"} ownerValid=true generationValid=true listCleared=true previewCleared={previewCleared}");
    }

    private async Task LoadFolderPaneItemsAsync(
        FolderPane pane,
        CancellationToken cancellationToken,
        FileListRestorePolicy policy,
        string restoreTrigger = "pane-load-complete",
        int workspaceSwitchId = 0)
    {
        using var previewSuppression = SuppressPreviewForProgrammaticSelection(restoreTrigger);
        if (restoreTrigger is "workspace-switch" or "subtab-selection-changed" or "active-pane-change" or "viewstate-restore"
            && FindSessionContainingPane(pane) is { } previewSession
            && IsCurrentActiveSessionAndPane(previewSession, out _, pane))
        {
            BeginPreviewAwaitingExplicitSelection(restoreTrigger);
        }

        _suppressWorkspaceSelectionSync = true;
        _suppressWorkspaceScrollSync = true;
        try
        {
            ApplyColumnWidthsToWorkspacePane(pane);
            await _folderPaneController.LoadPaneItemsAsync(pane, cancellationToken);

            var failedState = pane.ActiveTabState;
            if (failedState is not null
                && pane.FileList.HasCurrentLoadFailure(failedState.Id, failedState.CurrentPath)
                && FindSessionContainingPane(pane) is { } failureSession
                && IsCurrentActiveSessionAndPane(failureSession, out _, pane))
            {
                await CancelAndClearPreviewAsync("workspace-pane-folder-load-failure");
                _performanceLogger.Write(
                    $"folder-pane-load-failure-preview-cleared sessionId={failureSession.Id} " +
                    $"paneId={pane.Id} stateId={failedState.Id} requestedPath=\"{failedState.CurrentPath}\"");
            }
            
            cancellationToken.ThrowIfCancellationRequested();

            if (workspaceSwitchId > 0
                && FindSessionContainingPane(pane) is { } switchSession
                && !CanApplyWorkspaceSwitch(workspaceSwitchId, switchSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", workspaceSwitchId, switchSession.Id, "stale-switch");
                return;
            }

            if (policy != FileListRestorePolicy.None && WorkspaceSplitGrid.Visibility == Visibility.Visible)
            {
                await RestoreWorkspacePaneStateAsync(pane, policy, restoreTrigger, workspaceSwitchId);
            }
        }
        finally
        {
            _suppressWorkspaceSelectionSync = false;
            _suppressWorkspaceScrollSync = false;

            if (workspaceSwitchId == 0
                || FindSessionContainingPane(pane) is not { } switchSession
                || CanApplyWorkspaceSwitch(workspaceSwitchId, switchSession))
            {
                RestoreWorkspacePaneListViewOpacityIfNeeded(pane);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (policy != FileListRestorePolicy.None)
        {
            if (workspaceSwitchId > 0
                && FindSessionContainingPane(pane) is { } switchSession
                && !CanApplyWorkspaceSwitch(workspaceSwitchId, switchSession))
            {
                WriteWorkspaceSwitchLog("workspace-switch-discard", workspaceSwitchId, switchSession.Id, "stale-switch");
                return;
            }

            SynchronizeWorkspacePaneSelectionAfterLoad(pane);
        }
    }

    private void SynchronizeWorkspacePaneSelectionAfterLoad(FolderPane pane)
    {
        if (!IsWorkspaceDisplayPane(pane))
        {
            return;
        }

        var currentSelection = GetFolderPaneListView(pane)?.SelectedItems
            .OfType<FileEntry>()
            .Select(entry => entry.FullPath)
            .ToList() ?? [];

        if (TryGetDisplayedWorkspacePaneTab(pane, out var displayedTab))
        {
            if (ReferenceEquals(displayedTab, pane.ActiveTab))
            {
                pane.SelectedPaths = currentSelection;
            }

            displayedTab.State.SelectedPaths = currentSelection;
            return;
        }

        PerfLog.WriteVerbose($"workspace-selection-sync-after-load-skip reason=displayed-tab-unresolved paneId={pane.Id} loadedStateId=\"{pane.FileList.LoadedStateId ?? ""}\" activeStateId=\"{pane.ActiveTabState?.Id ?? ""}\"");
    }

    private void RestoreWorkspacePaneListViewOpacityIfNeeded(FolderPane pane)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible || !IsWorkspaceDisplayPane(pane))
        {
            return;
        }

        var listView = GetFolderPaneListView(pane);
        if (listView is null)
        {
            return;
        }

        if (listView.Opacity < 1.0)
        {
            listView.BeginAnimation(UIElement.OpacityProperty, null);
            listView.Opacity = 1.0;
        }
    }

    private void EnsureWorkspaceLayoutRoot(WorkspaceSession session)
    {
        if (session.LayoutRoot is not null)
        {
            return;
        }

        session.LayoutRoot = BuildLayoutRootFromPaneGroups(session.PaneGroups, session.PaneSplitOrientation);
    }

    private void EnsureWorkspaceDisplayLayoutRoot(WorkspaceSession session)
    {
        if (session.DisplayLayoutRoot is null)
        {
            session.DisplayLayoutRoot = BuildDisplayLayoutRoot(session);
        }
    }

    private WorkspaceLayoutNodeDefinition? BuildDisplayLayoutRoot(WorkspaceSession session)
    {
        return WorkspaceSessionFactory.CreateDisplayLayoutRoot(session.LayoutRoot);
    }

    private static WorkspaceLayoutNodeDefinition? BuildLayoutRootFromPaneGroups(
        IReadOnlyList<WorkspacePaneGroup> paneGroups,
        WorkspaceSplitOrientation orientation)
    {
        if (paneGroups.Count == 0)
        {
            return null;
        }

        return BuildLayoutRootFromPaneGroups(paneGroups, 0, paneGroups.Count, orientation);
    }

    private static WorkspaceLayoutNodeDefinition BuildLayoutRootFromPaneGroups(
        IReadOnlyList<WorkspacePaneGroup> paneGroups,
        int startIndex,
        int count,
        WorkspaceSplitOrientation orientation)
    {
        if (count == 1)
        {
            return CreateLayoutPaneNode(paneGroups[startIndex]);
        }

        return new WorkspaceSplitNodeDefinition(
            $"split_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
            orientation,
            1.0 / count,
            BuildLayoutRootFromPaneGroups(paneGroups, startIndex, 1, orientation),
            BuildLayoutRootFromPaneGroups(paneGroups, startIndex + 1, count - 1, orientation));
    }

    private static WorkspacePaneGroupDefinition CreateLayoutPaneNode(WorkspacePaneGroup paneGroup)
    {
        return new WorkspacePaneGroupDefinition(paneGroup.Id, paneGroup.SelectedTabIndex, [])
        {
            SelectedTabId = paneGroup.SelectedTabId ?? "",
            SubTabPlacement = paneGroup.SubTabPlacement
        };
    }

    private static WorkspaceLayoutNodeDefinition ReplacePaneInLayout(
        WorkspaceLayoutNodeDefinition node,
        string targetPaneId,
        WorkspacePaneGroup newPaneGroup,
        WorkspaceSplitOrientation orientation,
        out bool replaced)
    {
        switch (node)
        {
            case WorkspacePaneGroupDefinition pane when string.Equals(pane.Id, targetPaneId, StringComparison.OrdinalIgnoreCase):
                replaced = true;
                return new WorkspaceSplitNodeDefinition(
                    $"split_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    orientation,
                    0.5,
                    pane,
                    CreateLayoutPaneNode(newPaneGroup));

            case WorkspaceSplitNodeDefinition split:
                var first = ReplacePaneInLayout(split.First, targetPaneId, newPaneGroup, orientation, out var firstReplaced);
                var secondReplaced = false;
                var second = firstReplaced
                    ? split.Second
                    : ReplacePaneInLayout(split.Second, targetPaneId, newPaneGroup, orientation, out secondReplaced);
                replaced = firstReplaced || secondReplaced;
                return replaced
                    ? split with { First = first, Second = second }
                    : split;

            default:
                replaced = false;
                return node;
        }
    }

    private static WorkspaceLayoutNodeDefinition? RemovePaneFromLayout(
        WorkspaceLayoutNodeDefinition? node,
        string targetPaneId,
        out bool removed)
    {
        switch (node)
        {
            case WorkspacePaneGroupDefinition pane:
                removed = string.Equals(pane.Id, targetPaneId, StringComparison.OrdinalIgnoreCase);
                return removed ? null : pane;

            case WorkspaceSplitNodeDefinition split:
                var first = RemovePaneFromLayout(split.First, targetPaneId, out var firstRemoved);
                var second = RemovePaneFromLayout(split.Second, targetPaneId, out var secondRemoved);
                removed = firstRemoved || secondRemoved;
                if (!removed)
                {
                    return split;
                }

                return (first, second) switch
                {
                    (null, null) => null,
                    (null, not null) => second,
                    (not null, null) => first,
                    _ => split with { First = first, Second = second }
                };

            default:
                removed = false;
                return node;
        }
    }

    private static WorkspaceLayoutNodeDefinition? PruneLayoutToPaneIds(
        WorkspaceLayoutNodeDefinition? node,
        ISet<string> paneIds)
    {
        switch (node)
        {
            case WorkspacePaneGroupDefinition pane:
                return paneIds.Contains(pane.Id) ? pane : null;

            case WorkspaceSplitNodeDefinition split:
                var first = PruneLayoutToPaneIds(split.First, paneIds);
                var second = PruneLayoutToPaneIds(split.Second, paneIds);
                return (first, second) switch
                {
                    (null, null) => null,
                    (null, not null) => second,
                    (not null, null) => first,
                    _ => split with { First = first, Second = second }
                };

            default:
                return null;
        }
    }

    private void UpdateFolderWatchForWorkspacePanes()
    {
        UpdateFolderWatch();
    }

    private void UpdateActiveWorkspaceSessionUi(WorkspaceSession session)
    {
        foreach (var s in _workspaceSessions)
        {
            s.IsActiveSession = IsSameWorkspaceSession(s, session);
        }

        SynchronizeWorkspaceSessionHostVisibility(session);

        if (session.ActivePaneGroup is { } activePaneGroup)
        {
            _activeWorkspacePaneGroup = activePaneGroup;
        }

        PerfLog.WriteVerbose($"workspace-session-active id={session.Id} workspace={session.IsWorkspace} root=\"{session.RootPath}\" header=\"{session.Header}\" activePaneId=\"{session.ActivePaneId}\" panes={session.PaneGroups.Count} tabs={session.Tabs.Count}");
    }

    private void ApplyWorkspaceSessionToFolderTabs()
    {
        if (_activeWorkspaceSession is null)
        {
            return;
        }

        _workspaceTabSync.ApplyToDisplay(_activeWorkspaceSession);
    }

    private async Task SwitchWorkspacePaneGroupAsync(WorkspacePaneGroup paneGroup)
    {
        if (_workspaceRangeSelectionPane is not null && !ReferenceEquals(_workspaceRangeSelectionPane, paneGroup))
        {
            ClearWorkspacePaneRangeSelection();
        }

        if (_activeWorkspaceSession?.IsWorkspace != true
            || paneGroup.Workspace is null
            || !IsPaneOwnedByActiveWorkspaceSession(paneGroup))
        {
            return;
        }

        _isSwitchingWorkspacePane = true;
        try
        {
            ClearLastClosedStates();
            LoadWorkspacePaneGroup(paneGroup);
            PerfLog.WriteVerbose($"workspace-pane-switch paneId=\"{paneGroup.Id}\" activePaneId=\"{_activeWorkspaceSession.ActivePaneId}\" tabs={paneGroup.Tabs.Count} selected={TabsControl.SelectedIndex}");
            await LoadWorkspaceDisplayPanesAsync("active-pane-change");
            UpdateFolderWatchForWorkspacePanes();
            ScheduleSessionSave("active-pane");
            UpdateWindowTitle();
        }
        finally
        {
            _isSwitchingWorkspacePane = false;
        }
    }

    private async Task<bool> MoveSubTabToPane(
        WorkspaceSession sourceSession,
        WorkspacePaneGroup sourcePane,
        FolderTab tab,
        WorkspaceSession targetSession,
        WorkspacePaneGroup targetPane,
        int targetIndex,
        ListBox? targetListBox)
    {
        if (!sourceSession.PaneGroups.Any(pane => ReferenceEquals(pane, sourcePane))
            || !targetSession.PaneGroups.Any(pane => ReferenceEquals(pane, targetPane))
            || !sourcePane.Tabs.Contains(tab)
            || targetSession.PaneGroups
                .SelectMany(pane => pane.Tabs)
                .Any(candidate => !ReferenceEquals(candidate, tab)
                    && string.Equals(candidate.Id, tab.Id, StringComparison.Ordinal)))
        {
            return false;
        }

        if (ReferenceEquals(sourcePane, targetPane))
        {
            if (!sourcePane.MoveTab(tab, targetIndex))
            {
                return false;
            }

            targetSession.ActivePaneId = targetPane.Id;
            targetSession.ActivePaneGroup = targetPane;
            RestoreWorkspacePaneSubTabSelection(targetListBox, targetPane, tab);
            ScheduleSessionSave("move-subtab");
            return true;
        }

        if (ReferenceEquals(sourceSession, _activeWorkspaceSession))
        {
            SaveWorkspacePanesViewState();
        }

        var wasActiveInSource = string.Equals(sourcePane.SelectedTabId, tab.Id, StringComparison.Ordinal);
        var removedIndex = sourcePane.Tabs.IndexOf(tab);
        var sourceSelectedTabId = sourcePane.SelectedTabId;
        var targetSelectedTabId = targetPane.SelectedTabId;
        var oldPaneId = tab.State.PaneId;
        var isCrossSession = !ReferenceEquals(sourceSession, targetSession);

        if (isCrossSession)
        {
            sourceSession.UnregisterTabState(tab.Id, sourcePane.Id);
            tab.State.PaneId = targetPane.Id;
            targetSession.RegisterTabState(tab.State);
        }
        else
        {
            sourceSession.UpdateTabStatePane(tab.Id, sourcePane.Id, targetPane.Id);
        }

        var clampedIndex = Math.Clamp(targetIndex, 0, targetPane.Tabs.Count);
        try
        {
            targetPane.Tabs.Insert(clampedIndex, tab);
            if (!sourcePane.Tabs.Remove(tab))
            {
                throw new InvalidOperationException("The source pane no longer contains the dragged subtab.");
            }
        }
        catch (Exception ex)
        {
            targetPane.Tabs.Remove(tab);
            targetPane.SelectedTabId = targetSelectedTabId;
            if (!sourcePane.Tabs.Contains(tab))
            {
                sourcePane.Tabs.Insert(Math.Clamp(removedIndex, 0, sourcePane.Tabs.Count), tab);
                sourcePane.SelectedTabId = sourceSelectedTabId;
            }
            if (isCrossSession)
            {
                targetSession.UnregisterTabState(tab.Id, targetPane.Id);
                tab.State.PaneId = oldPaneId;
                sourceSession.RegisterTabState(tab.State);
            }
            else
            {
                sourceSession.UpdateTabStatePane(tab.Id, targetPane.Id, sourcePane.Id);
            }
            _performanceLogger.Write($"workspace-subtab-move-failed sourceSessionId=\"{sourceSession.Id}\" targetSessionId=\"{targetSession.Id}\" tabId=\"{tab.Id}\" error=\"{ex.Message}\"");
            return false;
        }

        await CompleteSourcePaneAfterSubTabTransferAsync(
            sourceSession,
            sourcePane,
            wasActiveInSource,
            removedIndex,
            sourceSelectedTabId);

        targetPane.SelectedTabId = tab.Id;
        targetPane.ResolveTabHeaders();
        targetPane.RefreshDisplay();
        targetSession.ActivePaneId = targetPane.Id;
        targetSession.ActivePaneGroup = targetPane;
        RestoreWorkspacePaneSubTabSelection(targetListBox, targetPane, tab);

        await ActivateTransferredSubTabAsync(targetSession, targetPane);
        RestoreWorkspacePaneSubTabSelection(
            FindWorkspacePaneSubTabListBox(targetPane) ?? targetListBox,
            targetPane,
            tab);
        ScheduleSessionSave("move-subtab");
        return true;
    }

    private async Task CompleteSourcePaneAfterSubTabTransferAsync(
        WorkspaceSession sourceSession,
        WorkspacePaneGroup sourcePane,
        bool wasActiveInSource,
        int removedIndex,
        string? previousSelectedTabId)
    {
        if (sourcePane.Tabs.Count == 0)
        {
            if (sourceSession.PaneGroups.Count > 1)
            {
                CloseWorkspacePane(sourceSession, sourcePane);
                return;
            }

            var fallbackPath = sourceSession.RootPath;
            var fallbackTabId = $"tab_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            var fallbackState = sourceSession.GetOrCreateTabState(
                sourcePane.Id,
                fallbackPath,
                FileDisplayMode.Details,
                "Name",
                true,
                id: fallbackTabId);
            var fallbackTab = new FolderTab(fallbackPath, fallbackTabId, FileDisplayMode.Details, fallbackState);
            sourcePane.Tabs.Add(fallbackTab);
            sourcePane.SelectedTabId = fallbackTab.Id;
        }
        else if (wasActiveInSource)
        {
            var fallbackIndex = Math.Clamp(removedIndex, 0, sourcePane.Tabs.Count - 1);
            sourcePane.SelectedTabId = sourcePane.Tabs[fallbackIndex].Id;
        }
        else if (!string.IsNullOrWhiteSpace(previousSelectedTabId)
            && sourcePane.Tabs.Any(candidate => string.Equals(candidate.Id, previousSelectedTabId, StringComparison.Ordinal)))
        {
            sourcePane.SelectedTabId = previousSelectedTabId;
        }

        sourcePane.ResolveTabHeaders();
        sourcePane.RefreshDisplay();

        if (ReferenceEquals(sourceSession, _activeWorkspaceSession))
        {
            var selectedTab = sourcePane.ActiveTab;
            RestoreWorkspacePaneSubTabSelection(
                FindWorkspacePaneSubTabListBox(sourcePane),
                sourcePane,
                selectedTab?.Id);
            if (wasActiveInSource && selectedTab is not null)
            {
                await LoadFolderPaneItemsAsync(sourcePane, restoreTrigger: "subtab-selection-changed");
            }
        }
    }

    private async Task<bool> CopySubTabToPane(
        WorkspaceSession sourceSession,
        WorkspacePaneGroup sourcePane,
        FolderTab sourceTab,
        WorkspaceSession targetSession,
        WorkspacePaneGroup targetPane,
        int targetIndex,
        ListBox? targetListBox)
    {
        if (!sourceSession.PaneGroups.Any(pane => ReferenceEquals(pane, sourcePane))
            || !targetSession.PaneGroups.Any(pane => ReferenceEquals(pane, targetPane))
            || !sourcePane.Tabs.Contains(sourceTab))
        {
            return false;
        }

        // Create duplicate FolderTab with new TabId & new targetPaneId.
        var newTabId = $"tab_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        var state = targetSession.GetOrCreateTabState(
            targetPane.Id,
            sourceTab.Navigation.CurrentPath,
            sourceTab.State.ViewMode,
            sourceTab.State.SortColumn,
            sourceTab.State.SortAscending,
            id: newTabId);

        // Ensure PaneId is set to targetPaneId
        state.PaneId = targetPane.Id;

        // Copy properties and ensure state/lists are NOT shared
        state.FilterText = sourceTab.State.FilterText;
        state.VerticalOffset = sourceTab.State.VerticalOffset;
        state.SelectedPaths = sourceTab.State.SelectedPaths.ToList();
        state.BasePath = sourceTab.State.BasePath;
        state.CachedPath = sourceTab.State.CachedPath;
        state.CachedItems = sourceTab.State.CachedItems;
        state.LastLoadedAt = sourceTab.State.LastLoadedAt;
        state.HasPendingExternalChange = sourceTab.State.HasPendingExternalChange;
        state.LastExternalChangeAt = sourceTab.State.LastExternalChangeAt;
        state.LastLoadElapsedMs = sourceTab.State.LastLoadElapsedMs;
        state.CopyNavigationViewStatesFrom(sourceTab.State);

        var newTab = new FolderTab(
            sourceTab.Navigation.CurrentPath,
            newTabId,
            sourceTab.State.ViewMode,
            state);

        newTab.Navigation.CopyFrom(sourceTab.Navigation);
        newTab.SetFolderLocked(sourceTab.IsFolderLocked);

        var clampedIndex = Math.Clamp(targetIndex, 0, targetPane.Tabs.Count);
        targetPane.Tabs.Insert(clampedIndex, newTab);
        targetPane.SelectedTabId = newTab.Id;
        targetPane.ResolveTabHeaders();
        targetPane.RefreshDisplay();

        targetSession.ActivePaneId = targetPane.Id;
        targetSession.ActivePaneGroup = targetPane;
        RestoreWorkspacePaneSubTabSelection(targetListBox, targetPane, newTab);

        await ActivateTransferredSubTabAsync(targetSession, targetPane);
        RestoreWorkspacePaneSubTabSelection(
            FindWorkspacePaneSubTabListBox(targetPane) ?? targetListBox,
            targetPane,
            newTab);
        ScheduleSessionSave("copy-subtab");
        return true;
    }

    private async Task ActivateTransferredSubTabAsync(WorkspaceSession targetSession, WorkspacePaneGroup targetPane)
    {
        if (!ReferenceEquals(targetSession, _activeWorkspaceSession))
        {
            var selectResult = _workspaceController.TrySelectSession(_activeWorkspaceSession, targetSession);
            if (!selectResult.Success)
            {
                return;
            }

            if (selectResult.ActiveSessionChanged)
            {
                CancelActiveLoadForWorkspaceSwitch(targetSession, "workspace-subtab-drop");
            }

            _isSwitchingTabs = true;
            try
            {
                _activeWorkspaceSession = targetSession;
                UpdateActiveWorkspaceSessionUi(targetSession);
                ApplyWorkspaceSessionToFolderTabs();
                RefreshWorkspaceDisplayPanes();
                SelectWorkspaceSession(targetSession);
            }
            finally
            {
                _isSwitchingTabs = false;
            }

            _workspaceLocalState.Capture(markDirty: true, reason: "selected-tab");
            await RestoreActiveTabAsync();
            return;
        }

        if (ReferenceEquals(targetPane, _activeWorkspacePaneGroup))
        {
            targetPane.RefreshDisplay();
            await LoadFolderPaneItemsAsync(targetPane, restoreTrigger: "subtab-selection-changed");
        }
        else
        {
            await SwitchWorkspacePaneGroupAsync(targetPane);
        }
    }

    private ListBox? FindWorkspacePaneSubTabListBox(FolderPane pane)
    {
        return FindVisualChildren<ListBox>(WorkspaceSessionsHost)
            .FirstOrDefault(listBox =>
                listBox is not ListView
                && ReferenceEquals(listBox.DataContext, pane)
                && ReferenceEquals(listBox.ItemsSource, pane.Tabs));
    }

    private async void WorkspacePaneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element
            || element.DataContext is not WorkspacePaneGroup paneGroup)
        {
            return;
        }

        var wasActive = ReferenceEquals(paneGroup, _activeWorkspaceSession?.ActivePaneGroup);
        if (!TryRequestActivePane(paneGroup, "click"))
        {
            return;
        }

        if (!wasActive)
        {
            await SwitchWorkspacePaneGroupAsync(paneGroup);
        }
    }

    private void WorkspacePaneFileList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListView listView && listView.DataContext is FolderPane pane)
        {
            TryRequestActivePane(pane, "focus", listView);
        }
    }

    private async Task ActivateWorkspacePaneFromSenderAsync(object sender, bool allowRefresh = true)
    {
        if (_activeWorkspaceSession is null)
        {
            return;
        }

        if (GetWorkspacePaneFromSender(sender) is not { } pane)
        {
            return;
        }

        var wasActive = ReferenceEquals(pane, _activeWorkspaceSession.ActivePaneGroup);
        if (!TryRequestActivePane(pane, "subtab", sender as DependencyObject))
        {
            return;
        }

        _lastInteractedWorkspaceDisplayPane = pane;
        if (pane is not WorkspacePaneGroup paneGroup)
        {
            if (allowRefresh)
            {
                RefreshWorkspaceDisplayPanes();
            }
            UpdateWindowTitle();
            return;
        }

        if (wasActive)
        {
            UpdateWindowTitle();
            return;
        }

        await SwitchWorkspacePaneGroupAsync(paneGroup);
    }

    private async Task SwitchPaneFocusByOffsetAsync(int offset)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible)
        {
            return;
        }

        var panes = _workspaceDisplayPanes;
        if (panes is null || panes.Count <= 1)
        {
            return;
        }

        var activePane = GetActiveFolderPane();
        if (activePane is null)
        {
            return;
        }
        var currentIndex = panes.IndexOf(activePane);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = (currentIndex + offset + panes.Count) % panes.Count;
        var targetPane = panes[nextIndex];

        if (IsDiagLogEnabled) WriteDiagLog($"SwitchPaneFocusByOffsetAsync index={currentIndex}->{nextIndex} paneId={targetPane.Id}");

        // 既存のペインアクティブ化処理を通す
        await ActivateWorkspacePaneFromSenderAsync(targetPane);

        // アクティブ表示更新
        UpdateWorkspacePaneActiveStates();

        // リストビューにフォーカスを当て、選択項目へフォーカスを復元する
        FocusActiveFileList();
        FocusSelectedListViewItemOfActivePane();
    }

    private async void WorkspacePaneBackButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await NavigateHistoryAsync(NavigationDirection.Back, pane);
        }
    }

    private async void WorkspacePaneForwardButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await NavigateHistoryAsync(NavigationDirection.Forward, pane);
        }
    }

    private async void WorkspacePaneUpButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is not { } pane)
        {
            return;
        }

        await _navigationController.OpenWorkspacePaneParentAsync(pane);
    }

    private async void WorkspacePaneRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        var pane = GetWorkspacePaneFromSender(sender);
        var context = GetActiveNavigationContext(pane);
        if (context.Pane is not null && context.Tab is { } tab)
        {
            await ReloadFolderPanesShowingPathAsync(tab.Navigation.CurrentPath, context.Pane);
        }
    }

    private void WorkspacePanePlacesButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (sender is not FrameworkElement placementTarget
            || GetWorkspacePaneFromSender(sender) is not { } pane)
        {
            return;
        }

        ShowPlacesMenu(placementTarget, path => NavigateWorkspacePaneToFolderAsync(pane, path, NavigationKind.New));
    }

    private void WorkspacePaneViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedInternalPage() is not null) return;
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (sender is not FrameworkElement placementTarget
            || GetWorkspacePaneFromSender(sender) is not { } pane
            || pane.ActiveTabState is not { } state)
        {
            return;
        }

        _viewModeController.ShowWorkspaceMenu(
            placementTarget,
            pane,
            state,
            reason => _workspaceLocalState.MarkDirty(reason),
            ApplyDisplayModeToPane);
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

    private void WorkspacePanePathBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null
            || IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (FindVisualChild<TextBox>((DependencyObject)sender) is { } textBox
            && GetWorkspacePaneFromSender(sender) is { } pane)
        {
            e.Handled = true;
            ShowPanePathTextBox(textBox, pane.CurrentPath);
        }
    }

    private async void WorkspacePaneBreadcrumbButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (sender is Button { Tag: string targetPath }
            && GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await NavigateWorkspacePaneToFolderAsync(pane, targetPath, NavigationKind.New);
        }
    }

    private async void WorkspacePanePathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox
            || GetWorkspacePaneFromSender(sender) is not { } pane)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            textBox.Text = pane.CurrentPath;
            textBox.Visibility = Visibility.Collapsed;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await NavigateWorkspacePaneToFolderAsync(pane, textBox.Text, NavigationKind.New);
        textBox.Visibility = Visibility.Collapsed;
    }

    private void WorkspacePanePathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Visibility = Visibility.Collapsed;
        }
    }

    private void WorkspacePaneTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
    }

    private async Task SwitchSubTabByOffsetAsync(int offset)
    {
        var pane = GetActiveFolderPane();
        if (pane is null || pane.Tabs.Count <= 1)
        {
            return;
        }

        var newIndex = (pane.ActiveTabIndex + offset + pane.Tabs.Count) % pane.Tabs.Count;
        if (IsDiagLogEnabled) WriteDiagLog($"SwitchSubTabByOffsetAsync index={pane.ActiveTabIndex}->{newIndex}");

        var previousTab = pane.ActiveTab;
        var targetTab = pane.Tabs[newIndex];

        _isSwitchingSubTabByKey = true;
        try
        {
            if (previousTab is not null)
            {
                SaveWorkspacePaneColumnWidthsForTab(pane, previousTab);
                SaveWorkspacePaneNavigationViewState(pane, previousTab);
            }

            pane.SelectedTabId = targetTab.Id;

            var listBox = FindSubTabBarListBoxForPane(pane);
            if (listBox is not null)
            {
                if (!ReferenceEquals(listBox.SelectedItem, targetTab))
                {
                    listBox.SelectedItem = targetTab;
                }
                BringWorkspacePaneSelectedSubTabIntoView(listBox);
            }

            if (_activeWorkspaceSession is not null)
            {
                await ActivateWorkspacePaneFromSenderAsync(listBox ?? (object)pane);
            }

            targetTab.State.CurrentPath = targetTab.Navigation.CurrentPath;
            ApplyDisplayModeToPane(pane);
            pane.RefreshDisplay();

            await LoadFolderPaneItemsAsync(pane, restoreTrigger: "subtab-selection-changed");

            UpdateFolderWatchForWorkspacePanes();
            ApplyColumnSettingsToWorkspacePane(pane);
            UpdateWindowTitle();
            ScheduleSessionSave("subtab-selection-changed-offset");
        }
        finally
        {
            _isSwitchingSubTabByKey = false;
        }

        FocusActiveFileList();
        FocusSelectedListViewItemOfActivePane();
    }

    private void RestoreWorkspacePaneSubTabSelection(ListBox? listBox, FolderPane pane, FolderTab selectedTab)
    {
        RestoreWorkspacePaneSubTabSelection(listBox, pane, selectedTab.Id, selectedTab);
    }

    private void RestoreWorkspacePaneSubTabSelection(ListBox? listBox, FolderPane pane, string? selectedTabId = null)
    {
        RestoreWorkspacePaneSubTabSelection(listBox, pane, selectedTabId, selectedTab: null);
    }

    private void RestoreWorkspacePaneSubTabSelection(ListBox? listBox, FolderPane pane, string? selectedTabId, FolderTab? selectedTab)
    {
        if (listBox is null)
        {
            return;
        }

        ApplyWorkspacePaneSubTabSelection(listBox, pane, selectedTabId, selectedTab);
        var scheduledSwitchGeneration = _workspaceSwitchGeneration;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (scheduledSwitchGeneration != _workspaceSwitchGeneration)
            {
                PerfLog.WriteVerbose($"workspace-subtab-selection-restore-skip reason=switch-generation-mismatch scheduledGeneration={scheduledSwitchGeneration} currentGeneration={_workspaceSwitchGeneration} paneId={pane.Id}");
                return;
            }

            ApplyWorkspacePaneSubTabSelection(listBox, pane, selectedTabId, selectedTab);
        }, DispatcherPriority.ContextIdle);
    }

    private void ApplyWorkspacePaneSubTabSelection(ListBox listBox, FolderPane pane, string? selectedTabId, FolderTab? selectedTab)
    {
        if (listBox.DataContext is not FolderPane currentPane
            || !ReferenceEquals(currentPane, pane))
        {
            return;
        }

        var targetTab = selectedTab is not null && pane.Tabs.Contains(selectedTab)
            ? selectedTab
            : pane.Tabs.FirstOrDefault(tab => string.Equals(tab.Id, selectedTabId ?? pane.SelectedTabId, StringComparison.Ordinal));
        if (targetTab is null)
        {
            return;
        }

        if (!string.Equals(pane.SelectedTabId, targetTab.Id, StringComparison.Ordinal))
        {
            pane.SelectedTabId = targetTab.Id;
        }

        if (!ReferenceEquals(listBox.SelectedItem, targetTab))
        {
            listBox.SelectedItem = targetTab;
        }

        if (!string.Equals(listBox.SelectedValue as string, targetTab.Id, StringComparison.Ordinal))
        {
            listBox.SelectedValue = targetTab.Id;
        }

        BringWorkspacePaneSelectedSubTabIntoView(listBox);
    }

    private async void WorkspacePaneSubTabBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSwitchingSubTabByKey)
        {
            return;
        }

        if (sender is ListBox listBox
            && listBox.DataContext is FolderPane pane)
        {
            if (!IsPaneOwnedByActiveWorkspaceSession(pane))
            {
                PerfLog.WriteVerbose($"workspace-subtab-selection-skip reason=inactive-session-pane paneId={pane.Id} activeSessionId={_activeWorkspaceSession?.Id ?? "null"} selectedSessionId={GetSelectedWorkspaceSession()?.Id ?? "null"}");
                return;
            }

            if (e.RemovedItems.OfType<FolderTab>().FirstOrDefault() is { } previousTab)
            {
                SaveWorkspacePaneColumnWidthsForTab(pane, previousTab);
                SaveWorkspacePaneNavigationViewState(pane, previousTab);
            }

            if (pane.ActiveTab is { } activeTab)
            {
                BeginPreviewAwaitingExplicitSelection("workspace-subtab-switch");
                if (_activeWorkspaceSession is not null)
                {
                    _ = ActivateWorkspacePaneFromSenderAsync(listBox);
                }
                BringWorkspacePaneSelectedSubTabIntoView(listBox);
                activeTab.State.CurrentPath = activeTab.Navigation.CurrentPath;
                ApplyDisplayModeToPane(pane);
                pane.RefreshDisplay();
                await LoadFolderPaneItemsAsync(pane, restoreTrigger: "subtab-selection-changed");
                UpdateFolderWatchForWorkspacePanes();
                ApplyColumnSettingsToWorkspacePane(pane);

                UpdateWindowTitle();
                ScheduleSessionSave("subtab-selection-changed");
            }
        }
    }

    private void BringWorkspacePaneSelectedSubTabIntoView(ListBox listBox)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (listBox.SelectedItem is not { } selectedItem)
            {
                return;
            }

            listBox.UpdateLayout();
            if (listBox.ItemContainerGenerator.ContainerFromItem(selectedItem) is FrameworkElement item)
            {
                item.BringIntoView();
            }
        }, DispatcherPriority.ContextIdle);
    }

    private async void WorkspacePaneNewSubTabButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane && pane.ActiveTab is { } activeTab)
        {
            var newTab = _tabOperations.CreateNewTab(activeTab.Navigation.CurrentPath, activeTab);
            pane.AddTab(newTab);

            await _navigationController.NavigateWorkspacePaneToFolderAsync(pane, newTab.Navigation.CurrentPath, NavigationKind.New);
            ScheduleSessionSave("new-subtab");
        }
    }

    private async void WorkspacePaneSubTabClose_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (sender is FrameworkElement element
            && element.DataContext is FolderTab targetTab
            && FindVisualParent<ListBox>(element) is ListBox listBox
            && listBox.DataContext is FolderPane pane)
        {
            var activeTabBeforeClose = ReferenceEquals(_workspaceSubTabClosePaneBeforeClick, pane)
                ? _workspaceSubTabCloseActiveTabBeforeClick
                : null;
            _workspaceSubTabClosePaneBeforeClick = null;
            _workspaceSubTabCloseActiveTabBeforeClick = null;
            await CloseWorkspacePaneSubTabAsync(pane, targetTab, listBox, activeTabBeforeClose);
            e.Handled = true;
        }
        else
        {
            _workspaceSubTabClosePaneBeforeClick = null;
            _workspaceSubTabCloseActiveTabBeforeClick = null;
        }
    }

    private void WorkspacePaneSubTabClose_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element
            && FindVisualParent<ListBox>(element) is ListBox { DataContext: FolderPane pane })
        {
            _workspaceSubTabClosePaneBeforeClick = pane;
            _workspaceSubTabCloseActiveTabBeforeClick = pane.ActiveTab;
        }
    }

    private async Task CloseActiveSubTabAsync()
    {
        var pane = GetActiveFolderPane();
        if (pane is null || pane.ActiveTab is null)
        {
            return;
        }

        if (IsDiagLogEnabled) WriteDiagLog($"CloseActiveSubTabAsync path={pane.ActiveTab.Navigation.CurrentPath}");
        var listBox = FindSubTabBarListBoxForPane(pane);
        await CloseWorkspacePaneSubTabAsync(pane, pane.ActiveTab, listBox);
    }

    private async Task RestoreLastClosedSubTabAsync(FolderPane? preferredPane = null)
    {
        if (_lastClosedSubTab is not { } closedSubTab)
        {
            _performanceLogger.Write("restore-closed-subtab-skip reason=empty");
            return;
        }

        var pane = FindWorkspacePaneById(closedSubTab.PaneId)
            ?? GetActiveFolderPane()
            ?? preferredPane;
        if (pane is null)
        {
            _performanceLogger.Write("restore-closed-subtab-skip reason=no-pane");
            return;
        }

        _lastClosedSubTab = null;
        if (_lastClosedKind == LastClosedKind.SubTab)
        {
            _lastClosedKind = LastClosedKind.None;
        }
        var tab = _tabOperations.RestoreClosedTabState(closedSubTab.TabState);
        var insertIndex = Math.Clamp(closedSubTab.TabState.Index, 0, pane.Tabs.Count);
        pane.Tabs.Insert(insertIndex, tab);
        pane.SelectedTabId = tab.Id;
        pane.ResolveTabHeaders();
        pane.RefreshDisplay();
        await _navigationController.NavigateWorkspacePaneToFolderAsync(pane, tab.Navigation.CurrentPath, NavigationKind.New);
        _workspaceLocalState.QueueCapture(markDirty: true, reason: "restore-subtab");
        UpdateFolderWatch();
    }

    private async Task CloseWorkspacePaneSubTabAsync(
        FolderPane pane,
        FolderTab tab,
        ListBox? listBox = null,
        FolderTab? activeTabBeforeClose = null)
    {
        SaveWorkspacePanesViewState();
        var session = _workspaceSessions.FirstOrDefault(s => s.PaneGroups.Any(pg => ReferenceEquals(pg, pane)));
        if (session is null)
        {
            return;
        }

        // 1. ロック済みサブタブの場合: 当然閉じない
        if (tab.IsFolderLocked)
        {
            _performanceLogger.Write($"close-subtab-blocked reason=subtab-locked path=\"{tab.Navigation.CurrentPath}\"");
            return;
        }

        var totalTabsInSession = session.PaneGroups.Sum(pg => pg.Tabs.Count);

        // 2. 最後のサブタブの場合
        if (totalTabsInSession == 1)
        {
            // ロック済みメインタブの場合: 最後のサブタブを閉じようとしても拒否
            if (session.IsLocked)
            {
                _performanceLogger.Write($"close-subtab-blocked reason=maintab-locked path=\"{tab.Navigation.CurrentPath}\"");
                return;
            }

            if (_workspaceSessions.Count > 1)
            {
                await CloseSessionAsync(session);
            }
            return;
        }

        // 3. 複数ペインWorkspaceで対象ペインの最後のサブタブを閉じる場合
        if (pane.Tabs.Count == 1)
        {
            SaveTabViewState(tab);
            _lastClosedSubTab = new ClosedSubTabState(
                pane.Id,
                _tabOperations.CaptureClosedTabState(tab, 0, 1) with { Index = 0 });
            _lastClosedKind = LastClosedKind.SubTab;

            CloseWorkspacePane(pane);
            return;
        }

        // 4. それ以外の場合
        var oldIndex = pane.Tabs.IndexOf(tab);
        if (oldIndex < 0)
        {
            return;
        }

        var previousActiveTab = activeTabBeforeClose ?? pane.ActiveTab;
        var wasActiveTab = ReferenceEquals(previousActiveTab, tab);
        var previousSelectedTabId = previousActiveTab?.Id ?? pane.SelectedTabId;
        SaveTabViewState(tab);
        _lastClosedSubTab = new ClosedSubTabState(
            pane.Id,
            _tabOperations.CaptureClosedTabState(tab, oldIndex, pane.Tabs.Count) with { Index = oldIndex });
        _lastClosedKind = LastClosedKind.SubTab;

        pane.RemoveTab(tab, session.RootPath);
        if (wasActiveTab)
        {
            await ActivateWorkspacePaneAfterSubTabCloseAsync(pane, oldIndex, listBox);
        }
        else
        {
            await RestoreWorkspacePaneActiveSubTabAfterNonActiveCloseAsync(pane, previousActiveTab, previousSelectedTabId, listBox);
        }

        _workspaceLocalState.QueueCapture(markDirty: true, reason: "remove-subtab");
        UpdateFolderWatch();
        UpdateWindowTitle();
    }

    private async Task ActivateWorkspacePaneAfterSubTabCloseAsync(FolderPane pane, int oldIndex, ListBox? listBox)
    {
        if (_activeWorkspaceSession is not { } session || pane.Tabs.Count == 0)
        {
            return;
        }

        var newIndex = Math.Clamp(oldIndex, 0, pane.Tabs.Count - 1);
        var nextTab = pane.Tabs[newIndex];
        pane.SelectedTabId = nextTab.Id;
        if (pane is WorkspacePaneGroup paneGroup)
        {
            _lastInteractedWorkspaceDisplayPane = paneGroup;
            _activeWorkspacePaneGroup = paneGroup;
            session.ActivePaneGroup = paneGroup;
            session.ActivePaneId = paneGroup.Id;
            var rootOffset = session.Workspace?.HasRootPath == true ? 1 : 0;
            session.SelectedTabIndex = Math.Clamp(
                paneGroup.SelectedTabIndex + rootOffset,
                0,
                Math.Max(0, paneGroup.Tabs.Count));
        }

        RestoreWorkspacePaneSubTabSelection(listBox, pane, nextTab);
        ApplyWorkspaceSessionToFolderTabs();
        ApplyDisplayModeToPane(pane);
        pane.RefreshDisplay();
        await LoadFolderPaneItemsAsync(pane, restoreTrigger: "subtab-selection-changed");
        UpdateWorkspacePaneActiveStates();
        UpdateFolderWatchForWorkspacePanes();
        ApplyColumnSettingsToWorkspacePane(pane);
        RefreshPreviewForActiveSelection("workspace-subtab-close-active");
        ScheduleSessionSave("subtab-close-active");
    }

    private async Task RestoreWorkspacePaneActiveSubTabAfterNonActiveCloseAsync(
        FolderPane pane,
        FolderTab? previousActiveTab,
        string? previousSelectedTabId,
        ListBox? listBox)
    {
        var targetTab = previousActiveTab is not null && pane.Tabs.Contains(previousActiveTab)
            ? previousActiveTab
            : pane.Tabs.FirstOrDefault(tab => string.Equals(tab.Id, previousSelectedTabId, StringComparison.Ordinal));

        if (targetTab is null)
        {
            RestoreWorkspacePaneSubTabSelection(listBox, pane, previousSelectedTabId);
            return;
        }

        pane.SelectedTabId = targetTab.Id;
        RestoreWorkspacePaneSubTabSelection(listBox, pane, targetTab);
        ApplyWorkspaceSessionToFolderTabs();
        pane.RefreshDisplay();
        await LoadFolderPaneItemsAsync(pane, restoreTrigger: "subtab-selection-changed");
        UpdateFolderWatchForWorkspacePanes();
        ApplyColumnSettingsToWorkspacePane(pane);
        RefreshPreviewForActiveSelection("workspace-subtab-close-inactive");
        ScheduleSessionSave("subtab-close-inactive");
    }
}
