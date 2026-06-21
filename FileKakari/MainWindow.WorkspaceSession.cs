using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

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

    private async Task<bool> ApplyWorkspaceForFolderAsync(string folderPath)
    {
        var workspace = _workspaceService.LoadForDirectory(folderPath);
        if (workspace is null)
        {
            return false;
        }

        if (_activeWorkspaceSession?.Workspace is { } currentWorkspace
            && string.Equals(currentWorkspace.SourceDirectory, workspace.SourceDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _workspaceLocalState.SaveActiveLocalState();
        SaveActiveTabViewState();
        _loadCancellation?.Cancel();
        ClearLastClosedStates();
        _primaryPaneGroup.SetWorkspace(workspace);
        _workspacePaneGroups.Clear();
        var workspaceSession = _workspaceSessionFactory.Create(workspace);
        foreach (var paneGroup in workspaceSession.PaneGroups)
        {
            _workspacePaneGroups.Add(paneGroup);
        }

        var activePaneGroup = workspaceSession.ActivePaneGroup ?? _workspacePaneGroups[0];
        var replaceIndex = Math.Clamp(TabsControl.SelectedIndex, 0, Math.Max(0, _workspaceSessions.Count - 1));

        _isSwitchingTabs = true;
        try
        {
            if (_activeWorkspaceSession?.IsLocked == true)
            {
                _workspaceSessions.Add(workspaceSession);
            }
            else if (_workspaceSessions.Count == 0)
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

        UpdatePathDisplay(workspace.SourceDirectory);
        RefreshWorkspaceDisplayPanes();
        _performanceLogger.Write($"workspace-applied root=\"{workspace.SourceDirectory}\" name=\"{workspace.Name}\" layout={workspace.Layout.GetType().Name} pane=\"{activePaneGroup.Id}\" panes={_workspacePaneGroups.Count} tabs={activePaneGroup.Tabs.Count} selected={TabsControl.SelectedIndex} shared=\"{workspace.SharedPath ?? ""}\" local=\"{workspace.LocalPath ?? ""}\"");
        await LoadWorkspaceDisplayPanesAsync();
        UpdateWorkspaceButtonState();
        return true;
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
            _workspaceLocalState.SaveActiveLocalState();
            SaveActiveTabViewState();

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

        _workspaceLocalState.SaveActiveLocalState();
        SaveActiveTabViewState();
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
        await LoadWorkspaceDisplayPanesAsync();
        UpdateWorkspaceButtonState();
        return true;
    }

    private bool ShouldReplaceCurrentNormalSessionWithWorkspace(WorkspaceDefinition workspace)
    {
        var session = GetSelectedWorkspaceButtonSession();
        if (session is null || session.IsWorkspace || session.IsLocked)
        {
            return false;
        }

        var sharedPath = workspace.SharedPath;
        if (string.IsNullOrWhiteSpace(sharedPath)
            || string.IsNullOrWhiteSpace(session.RootPath)
            || SpecialLocationService.IsSpecialUri(session.RootPath))
        {
            return false;
        }

        try
        {
            var workspaceDirectory = Path.GetDirectoryName(Path.GetFullPath(sharedPath));
            if (string.IsNullOrWhiteSpace(workspaceDirectory)
                || !string.Equals(
                    WorkspacePathService.NormalizePathKey(workspaceDirectory),
                    WorkspacePathService.NormalizePathKey(Path.GetFullPath(session.RootPath)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(
                Path.GetFileName(sharedPath),
                ".workspace.json",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task ReplaceCurrentSessionWithWorkspaceAsync(WorkspaceDefinition workspace, string logReason)
    {
        _workspaceLocalState.SaveActiveLocalState();
        SaveActiveTabViewState();
        _loadCancellation?.Cancel();
        ClearLastClosedStates();

        var workspaceSession = _workspaceSessionFactory.Create(workspace);
        var activePaneGroup = workspaceSession.ActivePaneGroup ?? workspaceSession.PaneGroups.FirstOrDefault();
        var selectedSession = GetSelectedWorkspaceButtonSession();
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
        await LoadWorkspaceDisplayPanesAsync();
        UpdateWorkspaceButtonState();
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
        _activeWorkspacePaneGroup = paneGroup;
        _lastInteractedWorkspaceDisplayPane = paneGroup;
        _activeWorkspaceSession.ActivePaneGroup = paneGroup;
        _activeWorkspaceSession.ActivePaneId = paneGroup.Id;
        EnsureWorkspacePaneHasFallbackTab(paneGroup);
        _workspacePaneUiController.SetActivePaneGroup(paneGroup);
        _activeWorkspaceSession.SelectedTabIndex = Math.Clamp(paneGroup.SelectedTabIndex, 0, Math.Max(0, paneGroup.Tabs.Count));
        ApplyWorkspaceSessionToFolderTabs();
        paneGroup.RefreshDisplay();
        RefreshWorkspaceDisplayPanes();
        UpdateWindowTitle();
        RefreshPreviewForActiveSelection();
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
        _folderPaneController.RefreshDisplayPanes(
            _activeWorkspaceSession,
            isActivePaneActive: true);
        EnsureWorkspaceLayoutRoot(_activeWorkspaceSession);
        EnsureWorkspaceDisplayLayoutRoot(_activeWorkspaceSession);
        WorkspaceDisplayLayoutRoot = _activeWorkspaceSession.DisplayLayoutRoot;
        _workspacePaneUiController.ShowWorkspace(_activeWorkspaceSession.ActivePaneGroup);
    }

    private void UpdateWorkspacePaneActiveStates()
    {
        if (_activeWorkspaceSession is null)
        {
            return;
        }

        foreach (var paneGroup in _activeWorkspaceSession.PaneGroups)
        {
            paneGroup.IsActive = ReferenceEquals(paneGroup, _activeWorkspaceSession.ActivePaneGroup);
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

    private async Task LoadWorkspaceDisplayPanesAsync()
    {
        await _folderPaneController.LoadDisplayPanesAsync(Dispatcher);
        UpdateFolderWatchForWorkspacePanes();

        if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            foreach (var pane in _workspaceDisplayPanes)
            {
                await RestoreWorkspacePaneStateAsync(pane, FileListRestorePolicy.ExactRestore);
            }
        }
    }

    private async Task LoadWorkspaceDisplayPanesOnSwitchAsync()
    {
        var panes = _workspaceDisplayPanes.ToList();
        var loadTasks = new List<Task>();
        foreach (var pane in panes)
        {
            var targetState = pane.ActiveTabState;
            if (targetState is null) continue;

            if (targetState.HasPendingExternalChange || pane.FileList.Items.Count == 0)
            {
                loadTasks.Add(LoadFolderPaneItemsAsync(pane, FileListRestorePolicy.ExactRestore));
            }
        }
        if (loadTasks.Count > 0)
        {
            await Task.WhenAll(loadTasks);
        }
        UpdateFolderWatchForWorkspacePanes();
    }

    private async Task LoadFolderPaneItemsAsync(FolderPane pane, FileListRestorePolicy policy = FileListRestorePolicy.ExactRestore)
    {
        _suppressWorkspaceSelectionSync = true;
        _suppressWorkspaceScrollSync = true;
        try
        {
            await _folderPaneController.LoadPaneItemsAsync(pane);
            if (policy != FileListRestorePolicy.None && WorkspaceSplitGrid.Visibility == Visibility.Visible)
            {
                await RestoreWorkspacePaneStateAsync(pane, policy);
            }
        }
        finally
        {
            _suppressWorkspaceSelectionSync = false;
            _suppressWorkspaceScrollSync = false;

            RestoreWorkspacePaneListViewOpacityIfNeeded(pane);
        }

        if (policy != FileListRestorePolicy.None)
        {
            SynchronizeWorkspacePaneSelectionAfterLoad(pane);
        }
    }

    private void SynchronizeWorkspacePaneSelectionAfterLoad(FolderPane pane)
    {
        var currentSelection = GetFolderPaneListView(pane)?.SelectedItems
            .OfType<FileEntry>()
            .Select(entry => entry.FullPath)
            .ToList() ?? [];

        pane.SelectedPaths = currentSelection;
        if (pane.ActiveTabState is { } state)
        {
            state.SelectedPaths = currentSelection;
        }
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
        if (session.DisplayLayoutRoot is not null)
        {
            return;
        }

        session.DisplayLayoutRoot = BuildDisplayLayoutRoot(session);
    }

    private WorkspaceLayoutNodeDefinition? BuildDisplayLayoutRoot(WorkspaceSession session)
    {
        return session.LayoutRoot;
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
            SelectedTabId = paneGroup.SelectedTabId ?? ""
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
            s.IsActiveSession = ReferenceEquals(s, session);
        }

        if (session.ActivePaneGroup is { } activePaneGroup)
        {
            _activeWorkspacePaneGroup = activePaneGroup;
        }

        _performanceLogger.Write($"workspace-session-active id={session.Id} workspace={session.IsWorkspace} root=\"{session.RootPath}\" header=\"{session.Header}\" activePaneId=\"{session.ActivePaneId}\" panes={session.PaneGroups.Count} tabs={session.Tabs.Count}");
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
            || ReferenceEquals(paneGroup, _activeWorkspacePaneGroup))
        {
            return;
        }

        _isSwitchingWorkspacePane = true;
        try
        {
            ClearLastClosedStates();
            LoadWorkspacePaneGroup(paneGroup);
            _performanceLogger.Write($"workspace-pane-switch paneId=\"{paneGroup.Id}\" activePaneId=\"{_activeWorkspaceSession.ActivePaneId}\" tabs={paneGroup.Tabs.Count} selected={TabsControl.SelectedIndex}");
            await LoadWorkspaceDisplayPanesAsync();
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
                await LoadFolderPaneItemsAsync(sourcePane);
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
            await LoadFolderPaneItemsAsync(targetPane);
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
}
