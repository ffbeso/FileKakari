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
            SortAscending = tabState.SortAscending
        };
        var tab = new FolderTab(tabState.Path, state: state);
        var session = _workspaceController.CreateSinglePaneSession(tab);
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

        var state = new SessionState
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
            FolderColumnWidths = _sessionFolderColumnWidths,
            ColumnWidths = _sessionColumnWidths
        };
        _sessionStateService.Save(state);
    }

    private SessionTabState? BuildSessionTabState(WorkspaceSession session)
    {
        var isSavedWorkspace = session.IsWorkspace &&
            (!string.IsNullOrWhiteSpace(session.Workspace?.SharedPath) ||
             !string.IsNullOrWhiteSpace(session.Workspace?.LocalPath));

        var isUnsavedWorkspacePromotion = !isSavedWorkspace && session.IsWorkspace;

        var isWorkspace = isSavedWorkspace || isUnsavedWorkspacePromotion;

        if (isWorkspace)
        {
            var workspacePath = session.Workspace?.SharedPath ?? "";
            var representativeTab = GetSessionRepresentativeTab(session);

            var targetLayoutTree = session.LayoutRoot ?? session.DisplayLayoutRoot;
            var cleanLayoutNode = WorkspaceService.StripRootPane(targetLayoutTree);
            var serializedLayout = WorkspaceService.BuildSessionLayoutState(targetLayoutTree);
            var layoutJson = serializedLayout is null ? "null" : System.Text.Json.JsonSerializer.Serialize(serializedLayout);
            _performanceLogger.Write($"session-save-layout-debug sessionId={session.Id} " +
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
                ViewMode = AppSettings.NormalizeDisplayMode(representativeTab.State.ViewMode),
                IsFolderLocked = session.IsLocked,
                LocalState = _workspaceService.BuildLocalState(session),
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
            ViewMode = AppSettings.NormalizeDisplayMode(tab.State.ViewMode),
            IsFolderLocked = session.IsLocked,
            Name = session.Name
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
