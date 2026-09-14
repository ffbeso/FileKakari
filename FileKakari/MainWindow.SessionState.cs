using System.IO;
using System.Windows;

namespace FileKakari;

public partial class MainWindow
{
    private WorkspaceSession? CreateInitialSession(SessionTabState tabState)
    {
        if (tabState.IsWorkspace)
        {
            if (tabState.IsUnsavedWorkspace || string.IsNullOrWhiteSpace(tabState.WorkspacePath))
            {
                // session.json から未保存Workspaceとして復元
                try
                {
                    // Fail-safe path check
                    if (!string.IsNullOrWhiteSpace(tabState.RootPath) &&
                        !SpecialLocationService.IsSpecialUri(tabState.RootPath) &&
                        !Directory.Exists(tabState.RootPath))
                    {
                        _performanceLogger.Write($"session-restore-unsaved-workspace-skip path=\"{tabState.RootPath}\" reason=directory-not-found");
                        return null;
                    }

                    if (_workspaceService.LoadFromSessionTabState(tabState) is { } workspace)
                    {
                        var restoredSession = _workspaceSessionFactory.Create(workspace);
                        restoredSession.ColumnWidths = ColumnLayoutService.NormalizeColumnWidths(tabState.LocalState?.ColumnWidths);
                        ApplyRestoredWorkspaceName(restoredSession, tabState);
                        return restoredSession;
                    }
                }
                catch (Exception ex)
                {
                    _performanceLogger.Write($"session-restore-unsaved-workspace-error msg=\"{ex.Message}\"");
                }
                return null;
            }
            else if (File.Exists(tabState.WorkspacePath))
            {
                // 保存済みWorkspaceは .workspace.json を正としてロード
                try
                {
                    if (_workspaceService.LoadFromFile(tabState.WorkspacePath, tabState.LocalState, tabState.Layout) is { } workspace)
                    {
                        // Fail-safe root path check
                        if (workspace.HasRootPath &&
                            !SpecialLocationService.IsSpecialUri(workspace.RootPath!) &&
                            !Directory.Exists(workspace.RootPath!))
                        {
                            _performanceLogger.Write($"session-restore-workspace-skip path=\"{workspace.RootPath}\" reason=directory-not-found");
                            return null;
                        }

                        var restoredSession = _workspaceSessionFactory.Create(workspace);
                        restoredSession.ColumnWidths = ColumnLayoutService.NormalizeColumnWidths(tabState.LocalState?.ColumnWidths);
                        ApplyRestoredWorkspaceName(restoredSession, tabState);
                        return restoredSession;
                    }
                }
                catch (Exception ex)
                {
                    _performanceLogger.Write($"session-restore-workspace-error path=\"{tabState.WorkspacePath}\" msg=\"{ex.Message}\"");
                }
                return null;
            }
            else
            {
                // 保存済みWorkspaceファイルが消えている場合のみ、session fallbackを検討
                // ただし名前を UnsavedWorkspace に固定しない
                try
                {
                    if (!string.IsNullOrWhiteSpace(tabState.RootPath) &&
                        !SpecialLocationService.IsSpecialUri(tabState.RootPath) &&
                        Directory.Exists(tabState.RootPath) &&
                        _workspaceService.LoadFromSessionTabState(tabState) is { } fallbackWorkspace)
                    {
                        _performanceLogger.Write($"session-restore-workspace-fallback path=\"{tabState.RootPath}\" reason=file-missing-reconstructed-as-unsaved");
                        var restoredSession = _workspaceSessionFactory.Create(fallbackWorkspace);
                        restoredSession.ColumnWidths = ColumnLayoutService.NormalizeColumnWidths(tabState.LocalState?.ColumnWidths);
                        ApplyRestoredWorkspaceName(restoredSession, tabState);
                        return restoredSession;
                    }
                }
                catch
                {
                    // Ignore fallback failure
                }

                _performanceLogger.Write($"session-restore-workspace-skip path=\"{tabState.WorkspacePath}\" reason=load-failed");
                return null;
            }
        }

        if (!SpecialLocationService.IsSpecialUri(tabState.Path) && !Directory.Exists(tabState.Path))
        {
            return null;
        }

        var state = new WorkspaceTabState(
            tabState.Path,
            Guid.NewGuid().ToString("N"),
            AppSettings.NormalizeDisplayMode(tabState.ViewMode))
        {
            SortColumn = NormalizeSortColumn(tabState.SortColumn),
            SortAscending = tabState.SortAscending,
            GroupMode = tabState.GroupMode,
            FilterText = tabState.FilterText
        };
        var tab = new FolderTab(tabState.Path, state: state);
        var session = CreateSinglePaneSession(tab);
        ApplyRestoredWorkspaceName(session, tabState);
        session.IsLocked = tabState.IsFolderLocked;
        return session;
    }

    private static void ApplyRestoredWorkspaceName(WorkspaceSession session, SessionTabState tabState)
    {
        if (!string.IsNullOrWhiteSpace(tabState.Name))
        {
            session.Name = tabState.Name;
        }
    }

    private void ScheduleSessionSave(string reason)
    {
        _workspaceLocalState.QueueCapture(markDirty: true, reason: reason);
    }

    private void SaveSessionState()
    {
        _sessionStateService.Save(CaptureCurrentSessionState());
    }

    private SessionState CaptureCurrentSessionState()
    {
        if (GetSelectedWorkspaceSession() is not null)
        {
            SaveActiveTabViewState();
        }

        double left = Left;
        double top = Top;
        double width = Width;
        double height = Height;
        if (WindowState == WindowState.Maximized)
        {
            var bounds = RestoreBounds;
            left = bounds.Left;
            top = bounds.Top;
            width = bounds.Width;
            height = bounds.Height;
        }

        var selectedWorkspaceIndex = _activeWorkspaceSession is not null
            ? _workspaceSessions.IndexOf(_activeWorkspaceSession)
            : -1;
        if (selectedWorkspaceIndex < 0)
        {
            selectedWorkspaceIndex = 0;
        }

        return new SessionState
        {
            SelectedTabIndex = Math.Clamp(selectedWorkspaceIndex, 0, Math.Max(0, _workspaceSessions.Count - 1)),
            Tabs = _workspaceSessions
                .Select(BuildSessionTabState)
                .OfType<SessionTabState>()
                .ToList(),
            WindowLeft = left,
            WindowTop = top,
            WindowWidth = width,
            WindowHeight = height,
            WindowState = WindowState.ToString(),
            FolderColumnWidths = CloneFolderColumnWidths(_sessionFolderColumnWidths),
            ColumnWidths = new Dictionary<string, double>(_sessionColumnWidths, StringComparer.OrdinalIgnoreCase)
        };
    }

    private SessionTabState? BuildSessionTabState(WorkspaceSession session)
    {
        var isSavedWorkspace = session.IsWorkspace &&
            (!string.IsNullOrWhiteSpace(session.WorkspaceFilePath) ||
             !string.IsNullOrWhiteSpace(session.Workspace?.LocalPath));

        var isUnsavedWorkspacePromotion = !isSavedWorkspace && session.IsWorkspace;

        var isWorkspace = isSavedWorkspace || isUnsavedWorkspacePromotion;

        if (isWorkspace)
        {
            var workspacePath = session.WorkspaceFilePath;
            var representativeTab = GetSessionRepresentativeTab(session);

            var targetLayoutTree = session.LayoutRoot ?? session.DisplayLayoutRoot;
            var cleanLayoutNode = WorkspaceService.StripRootPane(targetLayoutTree);
            var serializedLayout = WorkspaceService.BuildSessionLayoutState(targetLayoutTree);
            var layoutJson = serializedLayout is null ? "null" : System.Text.Json.JsonSerializer.Serialize(serializedLayout);
            PerfLog.WriteVerbose($"session-save-layout-debug sessionId={session.Id} " +
                $"layoutRoot=\"{DebugDumpLayout(session.LayoutRoot)}\" " +
                $"displayLayoutRoot=\"{DebugDumpLayout(session.DisplayLayoutRoot)}\" " +
                $"cleanLayoutNode=\"{DebugDumpLayout(cleanLayoutNode)}\" " +
                $"serializedLayout=\"{layoutJson}\"");

            return new SessionTabState
            {
                TabId = session.Id,
                Path = session.RootPath,
                IsWorkspace = true,
                WorkspacePath = workspacePath,
                RootPath = session.RootPath,
                SortColumn = representativeTab.State.SortColumn,
                SortAscending = representativeTab.State.SortAscending,
                GroupMode = representativeTab.State.GroupMode,
                ViewMode = AppSettings.NormalizeDisplayMode(representativeTab.State.ViewMode),
                IsFolderLocked = session.IsLocked,
                LocalState = CloneWorkspaceLocalState(_workspaceService.BuildLocalState(session)),
                IsUnsavedWorkspace = isUnsavedWorkspacePromotion,
                WorkspaceId = session.Workspace?.WorkspaceId ?? session.Id,
                Name = session.Name,
                ActivePaneId = session.ActivePaneId,
                Layout = serializedLayout
            };
        }

        if (GetSessionActiveTab(session) is not { } tab)
        {
            return null;
        }

        return new SessionTabState
        {
            TabId = session.Id,
            Path = tab.Navigation.CurrentPath,
            RootPath = session.RootPath,
            SortColumn = tab.State.SortColumn,
            SortAscending = tab.State.SortAscending,
            GroupMode = tab.State.GroupMode,
            ViewMode = AppSettings.NormalizeDisplayMode(tab.State.ViewMode),
            IsFolderLocked = session.IsLocked,
            FilterText = tab.State.FilterText,
            Name = session.Name
        };
    }

    private void ApplySessionWindowState(SessionState state)
    {
        if (!state.WindowWidth.HasValue || !state.WindowHeight.HasValue)
        {
            return;
        }

        Left = state.WindowLeft ?? 100;
        Top = state.WindowTop ?? 100;
        Width = state.WindowWidth.Value;
        Height = state.WindowHeight.Value;

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;
        if (Left < virtualLeft || Left > virtualLeft + virtualWidth - 50
            || Top < virtualTop || Top > virtualTop + virtualHeight - 50)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (Enum.TryParse<WindowState>(state.WindowState, out var windowState))
        {
            WindowState = windowState;
        }
    }

    internal async Task<bool> ApplySessionStateAsync(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fallbackPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var restorePlan = SessionStateRestorePlanner.Prepare(
            state,
            fallbackPath,
            _settingsService.Settings.DisplayMode);
        var sessions = new List<WorkspaceSession>();
        var selectedSessionIndex = 0;
        for (var index = 0; index < restorePlan.Tabs.Count; index++)
        {
            if (CreateInitialSession(restorePlan.Tabs[index]) is not { } session)
            {
                continue;
            }

            if (index < restorePlan.SelectedTabIndex)
            {
                selectedSessionIndex++;
            }
            sessions.Add(session);
        }

        if (sessions.Count == 0)
        {
            var fallbackTab = new FolderTab(
                fallbackPath,
                viewMode: _settingsService.Settings.DisplayMode);
            sessions.Add(CreateSinglePaneSession(fallbackTab));
            selectedSessionIndex = 0;
        }

        selectedSessionIndex = Math.Clamp(selectedSessionIndex, 0, sessions.Count - 1);
        var targetSession = sessions[selectedSessionIndex];
        var previousState = CaptureCurrentSessionState();
        var previousSessions = _workspaceSessions.ToList();
        var previousActiveSession = _activeWorkspaceSession;

        try
        {
            ApplyPreparedSessionState(state, sessions, targetSession, "session-state-apply");
            await RestoreWorkspaceTabAsync(targetSession);
            return true;
        }
        catch (Exception ex)
        {
            _performanceLogger.Write($"session-state-apply-failed type={ex.GetType().FullName} message=\"{ex.Message}\"");
            if (previousActiveSession is not null && previousSessions.Count > 0)
            {
                try
                {
                    ApplyPreparedSessionState(
                        previousState,
                        previousSessions,
                        previousActiveSession,
                        "session-state-apply-rollback");
                    await RestoreWorkspaceTabAsync(previousActiveSession);
                }
                catch (Exception rollbackException)
                {
                    _performanceLogger.Write(
                        $"session-state-apply-rollback-failed type={rollbackException.GetType().FullName} " +
                        $"message=\"{rollbackException.Message}\"");
                }
            }
            return false;
        }
    }

    private void ApplyPreparedSessionState(
        SessionState state,
        IReadOnlyList<WorkspaceSession> sessions,
        WorkspaceSession activeSession,
        string reason)
    {
        _loadCancellation?.Cancel();
        ClearLastClosedStates();

        _isSwitchingTabs = true;
        try
        {
            _workspaceSessions.Clear();
            foreach (var session in sessions)
            {
                _workspaceSessions.Add(session);
            }

            ResetToSingleWorkspaceDisplay(activeSession, reason);
            UpdateActiveWorkspaceSessionUi(activeSession);
            _workspaceTabSync.ApplyToDisplay(activeSession);
            SelectWorkspaceSession(activeSession);
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        ReplaceDictionaryContents(
            _sessionFolderColumnWidths,
            ColumnLayoutService.NormalizeFolderColumnWidths(CloneFolderColumnWidths(state.FolderColumnWidths)));
        ReplaceDictionaryContents(
            _sessionColumnWidths,
            ColumnLayoutService.NormalizeColumnWidths(state.ColumnWidths));
        ApplySessionWindowState(state);
        ApplyDisplayMode();
        ApplyColumnSettings();
    }

    private static void ReplaceDictionaryContents<TKey, TValue>(
        Dictionary<TKey, TValue> target,
        Dictionary<TKey, TValue>? source)
        where TKey : notnull
    {
        target.Clear();
        if (source is null)
        {
            return;
        }

        foreach (var pair in source)
        {
            target[pair.Key] = pair.Value;
        }
    }

    private static Dictionary<string, FolderColumnWidthsState> CloneFolderColumnWidths(
        IReadOnlyDictionary<string, FolderColumnWidthsState>? source)
    {
        var clone = new Dictionary<string, FolderColumnWidthsState>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return clone;
        }

        foreach (var pair in source)
        {
            clone[pair.Key] = new FolderColumnWidthsState
            {
                LastAccessUtc = pair.Value.LastAccessUtc,
                Widths = new Dictionary<string, double>(pair.Value.Widths, StringComparer.OrdinalIgnoreCase)
            };
        }
        return clone;
    }

    private static WorkspaceService.WorkspaceLocalStateDocument CloneWorkspaceLocalState(
        WorkspaceService.WorkspaceLocalStateDocument source)
    {
        return new WorkspaceService.WorkspaceLocalStateDocument
        {
            IsWorkspaceLocked = source.IsWorkspaceLocked,
            ActivePaneId = source.ActivePaneId,
            PaneStates = source.PaneStates.Select(pane => new WorkspaceService.LocalPaneStateDocument
            {
                PaneId = pane.PaneId,
                SelectedTabId = pane.SelectedTabId,
                SubTabPlacement = pane.SubTabPlacement,
                SubTabBarWidth = pane.SubTabBarWidth,
                Tabs = pane.Tabs.Select(CloneWorkspaceLocalTabState).ToList()
            }).ToList(),
            TabStates = source.TabStates.Select(CloneWorkspaceLocalTabState).ToList(),
            ColumnWidths = source.ColumnWidths is null
                ? null
                : new Dictionary<string, double>(source.ColumnWidths, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static WorkspaceService.WorkspaceLocalTabStateDocument CloneWorkspaceLocalTabState(
        WorkspaceService.WorkspaceLocalTabStateDocument source)
    {
        return new WorkspaceService.WorkspaceLocalTabStateDocument
        {
            TabId = source.TabId,
            PaneId = source.PaneId,
            CurrentPath = source.CurrentPath,
            Path = source.Path,
            ViewMode = source.ViewMode,
            SortColumn = source.SortColumn,
            SortAscending = source.SortAscending,
            GroupMode = source.GroupMode,
            FilterText = source.FilterText,
            SelectedPaths = source.SelectedPaths?.ToList(),
            ScrollOffset = source.ScrollOffset,
            IsFolderLocked = source.IsFolderLocked
        };
    }

    private static string DebugDumpLayout(WorkspaceLayoutNodeDefinition? node)
    {
        if (node is null) return "null";
        if (node is WorkspacePaneGroupDefinition pane) return $"pane({pane.Id})";
        if (node is WorkspaceSplitNodeDefinition split)
        {
            return $"{split.Orientation.ToString().ToLower()}({DebugDumpLayout(split.First)},{DebugDumpLayout(split.Second)})";
        }
        return "unknown";
    }
}
