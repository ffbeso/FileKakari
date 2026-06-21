namespace FileKakari;

sealed class WorkspaceSessionFactory
{
    private readonly Func<string, string> _normalizeSortColumn;

    internal WorkspaceSessionFactory(Func<string, string> normalizeSortColumn)
    {
        _normalizeSortColumn = normalizeSortColumn;
    }

    internal WorkspaceSession Create(WorkspaceDefinition workspace)
    {
        var rootPath = workspace.RootPath
            ?? workspace.Tabs.FirstOrDefault()?.BasePath
            ?? workspace.SourceDirectory;

        var session = new WorkspaceSession(
            rootPath,
            [],
            workspace,
            workspace.RootViewMode)
        {
            SelectedTabIndex = workspace.SelectedTabIndex,
            IsLocked = workspace.IsWorkspaceLocked,
            ColumnWidths = null,
            PaneSplitOrientation = ResolvePaneSplitOrientation(workspace.Layout),
            LayoutRoot = workspace.Layout
        };

        foreach (var paneGroupDefinition in workspace.PaneGroups)
        {
            session.PaneGroups.Add(CreatePaneGroup(paneGroupDefinition, workspace, session));
        }

        if (session.PaneGroups.Count == 0)
        {
            session.PaneGroups.Add(CreatePaneGroup(workspace.PrimaryPaneGroup, workspace, session));
        }

        var activePaneGroup = session.PaneGroups.FirstOrDefault(group =>
                string.Equals(group.Id, workspace.ActivePaneId, StringComparison.OrdinalIgnoreCase))
            ?? session.PaneGroups[0];
        session.SelectedTabIndex = activePaneGroup.SelectedTabIndex;
        session.ActivePaneGroup = activePaneGroup;

        return session;
    }

    private static WorkspaceSplitOrientation ResolvePaneSplitOrientation(WorkspaceLayoutNodeDefinition layout)
    {
        return layout is WorkspaceSplitNodeDefinition split
            ? split.Orientation
            : WorkspaceSplitOrientation.Horizontal;
    }

    private WorkspacePaneGroup CreatePaneGroup(
        WorkspacePaneGroupDefinition definition,
        WorkspaceDefinition workspace,
        WorkspaceSession session)
    {
        var paneRootPath = definition.Tabs.FirstOrDefault()?.BasePath
            ?? workspace.RootPath
            ?? workspace.SourceDirectory;
        var paneGroup = new WorkspacePaneGroup(definition.Id, [], paneRootPath);
        paneGroup.SetWorkspace(workspace);

        foreach (var tabDefinition in definition.Tabs)
        {
            paneGroup.Tabs.Add(CreateTab(tabDefinition, session, paneGroup.Id));
        }

        paneGroup.SelectedTabIndex = ResolveSelectedTabIndex(
            paneGroup.Tabs,
            definition.SelectedTabId,
            definition.SelectedTabIndex);

        return paneGroup;
    }

    private FolderTab CreateTab(
        WorkspaceTabDefinition tabDefinition,
        WorkspaceSession session,
        string paneId)
    {
        var state = session.GetOrCreateTabState(
            paneId,
            tabDefinition.BasePath,
            AppSettings.NormalizeDisplayMode(tabDefinition.ViewMode),
            _normalizeSortColumn(tabDefinition.SortColumn),
            tabDefinition.SortAscending,
            id: tabDefinition.Id);
        var currentPath = string.IsNullOrWhiteSpace(tabDefinition.CurrentPath)
            ? tabDefinition.BasePath
            : tabDefinition.CurrentPath;
        ApplyTabDefinitionToState(state, tabDefinition, currentPath);
        var tab = new FolderTab(
            currentPath,
            viewMode: AppSettings.NormalizeDisplayMode(tabDefinition.ViewMode),
            state: state);
        tab.SetFolderLocked(tabDefinition.IsFolderLocked);
        return tab;
    }

    private static int ResolveSelectedTabIndex(
        IReadOnlyList<FolderTab> tabs,
        string selectedTabId,
        int fallbackIndex)
    {
        var tabIndex = -1;
        if (!string.IsNullOrWhiteSpace(selectedTabId))
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                if (string.Equals(tabs[i].State.Id, selectedTabId, StringComparison.OrdinalIgnoreCase))
                {
                    tabIndex = i;
                    break;
                }
            }
        }

        if (tabIndex < 0)
        {
            tabIndex = fallbackIndex;
        }

        return Math.Clamp(tabIndex, 0, Math.Max(0, tabs.Count - 1));
    }

    private static void ApplyTabDefinitionToState(
        WorkspaceTabState state,
        WorkspaceTabDefinition tabDefinition,
        string? currentPath = null)
    {
        state.BasePath = tabDefinition.BasePath;
        state.CurrentPath = string.IsNullOrWhiteSpace(currentPath)
            ? string.IsNullOrWhiteSpace(tabDefinition.CurrentPath) ? tabDefinition.BasePath : tabDefinition.CurrentPath
            : currentPath;
        state.FilterText = tabDefinition.FilterText;
        state.SelectedPaths = tabDefinition.SelectedPaths;
        state.VerticalOffset = tabDefinition.ScrollOffset;
        state.SortColumn = string.IsNullOrWhiteSpace(tabDefinition.SortColumn) ? "Name" : tabDefinition.SortColumn.Trim();
        state.SortAscending = tabDefinition.SortAscending;
        state.ViewMode = AppSettings.NormalizeDisplayMode(tabDefinition.ViewMode);
    }
}
