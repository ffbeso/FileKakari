using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKakari;

public sealed class WorkspaceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] CandidateNames =
    [
        ".workspace.json",
        ".kakari-workspace.json"
    ];

    public WorkspaceDefinition? LoadForDirectory(string directoryPath, WorkspaceLocalStateDocument? sessionLocalState = null, SessionLayoutNodeState? sessionLayout = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)
            || SpecialLocationService.IsSpecialUri(directoryPath)
            || !Directory.Exists(directoryPath))
        {
            return null;
        }

        foreach (var name in CandidateNames)
        {
            var sharedPath = Path.Combine(directoryPath, name);
            if (!File.Exists(sharedPath))
            {
                continue;
            }

            try
            {
                var definitionJson = File.ReadAllText(sharedPath);
                var document = JsonSerializer.Deserialize<WorkspaceDocument>(definitionJson, JsonOptions);
                if (document != null)
                {
                    SelfHealDocument(document, directoryPath, out var _, out var _);
                }
                return BuildDefinition(document, sessionLocalState, directoryPath, directoryPath, sharedPath, null, sessionLayout);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public WorkspaceDefinition? LoadFromFile(string workspaceFilePath, WorkspaceLocalStateDocument? sessionLocalState = null, SessionLayoutNodeState? sessionLayout = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceFilePath)
            || !IsWorkspaceFile(workspaceFilePath)
            || !File.Exists(workspaceFilePath))
        {
            return null;
        }

        try
        {
            var sharedPath = Path.GetFullPath(workspaceFilePath);
            var sourceDirectory = Path.GetDirectoryName(sharedPath);
            if (string.IsNullOrWhiteSpace(sourceDirectory))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<WorkspaceDocument>(File.ReadAllText(sharedPath), JsonOptions);
            var defaultRootPath = IsAutoWorkspaceFileName(sharedPath)
                ? sourceDirectory
                : null;
            var rootPath = WorkspacePathService.ResolveOptionalWorkspaceRootPath(document?.RootPath, sourceDirectory)
                ?? defaultRootPath;

            if (document != null)
            {
                SelfHealDocument(document, rootPath ?? sourceDirectory, out var _, out var _);
            }

            return BuildDefinition(document, sessionLocalState, sourceDirectory, rootPath, sharedPath, null, sessionLayout);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsWorkspaceFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(fileName)
            && (fileName.EndsWith(".workspace.json", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(".kakari-workspace.json", StringComparison.OrdinalIgnoreCase));
    }

    public WorkspaceLocalStateDocument BuildLocalState(WorkspaceSession session)
    {
        var activePaneGroup = session.ActivePaneGroup;
        var tabStates = new List<WorkspaceLocalTabStateDocument>();
        var paneStates = new List<LocalPaneStateDocument>();
        string? workspaceRoot = null;
        if (session.Workspace != null)
        {
            if (IsAutoWorkspaceFileName(session.Workspace.SharedPath ?? ""))
            {
                workspaceRoot = session.RootPath;
            }
            else if (session.Workspace.HasRootPath)
            {
                workspaceRoot = session.Workspace.RootPath;
            }
        }

        var savedOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var paneGroup in session.PaneGroups)
        {
            AddLocalPaneState(paneGroup, workspaceRoot, paneStates, tabStates, savedOwners);
        }

        return new WorkspaceLocalStateDocument
        {
            IsWorkspaceLocked = session.IsLocked,
            ActivePaneId = activePaneGroup?.Id ?? session.ActivePaneId,
            PaneStates = paneStates,
            TabStates = tabStates,
            ColumnWidths = session.ColumnWidths
        };
    }

    public string? FindWorkspaceFile(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)
            || SpecialLocationService.IsSpecialUri(directoryPath)
            || !Directory.Exists(directoryPath))
        {
            return null;
        }

        foreach (var name in CandidateNames)
        {
            var sharedPath = Path.Combine(directoryPath, name);
            if (File.Exists(sharedPath))
            {
                return sharedPath;
            }
        }
        return null;
    }

    public bool SaveWorkspace(WorkspaceSession session, string workspaceName, string? targetFilePath = null)
    {
        var rootDir = session.RootPath;
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
        {
            return false;
        }

        var filePath = targetFilePath ?? Path.Combine(rootDir, ".workspace.json");
        try
        {
            var workspaceId = session.Workspace?.WorkspaceId;
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                workspaceId = Guid.NewGuid().ToString("N");
            }

            // Ensure all tab IDs are unique, creating new UUIDs only for empty or duplicate IDs.
            EnsureUniqueTabIds(session);

            string? savedRootPath = null;
            string? workspaceRootForRelativization = null;

            if (IsAutoWorkspaceFileName(filePath))
            {
                savedRootPath = null;
                workspaceRootForRelativization = Path.GetDirectoryName(filePath);
            }
            else
            {
                if (session.Workspace != null && session.Workspace.HasRootPath)
                {
                    savedRootPath = session.Workspace.RootPath;
                    workspaceRootForRelativization = session.Workspace.RootPath;
                }
                else
                {
                    savedRootPath = null;
                    workspaceRootForRelativization = null;
                }
            }

            WorkspaceLayoutDocument layoutDoc;
            if (session.PaneGroups.Count > 0 && session.LayoutRoot is not null)
            {
                layoutDoc = BuildLayoutDocumentFromDefinition(session.LayoutRoot, session, workspaceRootForRelativization);
            }
            else if (session.PaneGroups.Count > 0)
            {
                layoutDoc = BuildLayoutFromPaneGroups(
                    session.PaneGroups.ToList(),
                    0,
                    session.PaneGroups.Count,
                    workspaceRootForRelativization,
                    session.PaneSplitOrientation);
            }
            else
            {
                // Fallback to primary tabs
                var tabDocs = new List<WorkspaceTabDocument>();
                var primaryTabs = session.Tabs;
                foreach (var tab in primaryTabs)
                {
                    tabDocs.Add(BuildWorkspaceTabDocument(tab, workspaceRootForRelativization));
                }

                layoutDoc = new WorkspaceLayoutDocument
                {
                    Id = "primary",
                    Type = "paneGroup",
                    SelectedTabId = session.ActivePaneGroup?.ActiveTab?.State.Id ?? "",
                    Tabs = tabDocs
                };
            }

            var doc = new WorkspaceDocument
            {
                WorkspaceId = workspaceId,
                Name = workspaceName.Trim(),
                RootPath = savedRootPath,
                ViewMode = GetRepresentativeTab(session).State.ViewMode,
                Layout = layoutDoc,
                Tabs = []
            };

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(doc, JsonOptions));

            return true;
        }
        catch (Exception ex)
        {
            PerfLog.Write($"[Error] Failed to save workspace.json at '{filePath}': {ex.Message}");
            return false;
        }
    }

    private void EnsureUniqueTabIds(WorkspaceSession session)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var paneGroup in session.PaneGroups)
        {
            foreach (var tab in paneGroup.Tabs)
            {
                if (tab.State is not { } state) continue;

                if (string.IsNullOrWhiteSpace(state.Id) || !seenIds.Add(state.Id))
                {
                    state.Id = Guid.NewGuid().ToString("N");
                    seenIds.Add(state.Id);
                }
            }
        }
    }

    private static FolderTab GetRepresentativeTab(WorkspaceSession session)
    {
        var primaryPane = session.PaneGroups.FirstOrDefault(pane =>
                string.Equals(pane.Id, "primary", StringComparison.OrdinalIgnoreCase))
            ?? session.PaneGroups.FirstOrDefault();

        return session.ActivePaneGroup?.ActiveTab
            ?? primaryPane?.ActiveTab
            ?? primaryPane?.Tabs.FirstOrDefault()
            ?? new FolderTab(
                session.RootPath,
                viewMode: AppSettings.NormalizeDisplayMode(session.Workspace?.RootViewMode ?? FileDisplayMode.Details));
    }

    private WorkspaceTabDocument BuildWorkspaceTabDocument(FolderTab tab, string? workspaceRoot)
    {
        var basePathSource = string.IsNullOrWhiteSpace(tab.State.BasePath)
            ? tab.Navigation.CurrentPath
            : tab.State.BasePath;
        var basePath = workspaceRoot != null
            ? WorkspacePathService.ToWorkspaceRelativePath(basePathSource, workspaceRoot)
            : basePathSource;
        return new WorkspaceTabDocument
        {
            Id = tab.State.Id,
            BasePath = basePath,
            SortColumn = tab.State.SortColumn,
            SortAscending = tab.State.SortAscending,
            ViewMode = tab.State.ViewMode,
            IsFolderLocked = tab.IsFolderLocked
        };
    }

    private WorkspaceLayoutDocument BuildLayoutFromPaneGroups(
        IReadOnlyList<WorkspacePaneGroup> paneGroups,
        int startIndex,
        int count,
        string? workspaceRoot,
        WorkspaceSplitOrientation orientation)
    {
        if (count == 1)
        {
            var pane = paneGroups[startIndex];
            var tabDocs = new List<WorkspaceTabDocument>();
            foreach (var tab in pane.Tabs)
            {
                tabDocs.Add(BuildWorkspaceTabDocument(tab, workspaceRoot));
            }
            return new WorkspaceLayoutDocument
            {
                Id = pane.Id,
                Type = "paneGroup",
                SelectedTabId = pane.ActiveTab?.State.Id ?? "",
                SelectedTabIndex = pane.SelectedTabIndex,
                Tabs = tabDocs
            };
        }
        else
        {
            var first = BuildLayoutFromPaneGroups(paneGroups, startIndex, 1, workspaceRoot, orientation);
            var second = BuildLayoutFromPaneGroups(paneGroups, startIndex + 1, count - 1, workspaceRoot, orientation);
            return new WorkspaceLayoutDocument
            {
                Id = $"split_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Type = "split",
                Orientation = orientation,
                Ratio = 1.0 / count,
                First = first,
                Second = second
            };
        }
    }

    private WorkspaceLayoutDocument BuildLayoutDocumentFromDefinition(
        WorkspaceLayoutNodeDefinition node,
        WorkspaceSession session,
        string? workspaceRoot)
    {
        if (node is WorkspaceSplitNodeDefinition split)
        {
            return new WorkspaceLayoutDocument
            {
                Id = split.Id,
                Type = "split",
                Orientation = split.Orientation,
                Ratio = split.Ratio,
                First = BuildLayoutDocumentFromDefinition(split.First, session, workspaceRoot),
                Second = BuildLayoutDocumentFromDefinition(split.Second, session, workspaceRoot)
            };
        }
        else if (node is WorkspacePaneGroupDefinition paneGroup)
        {
            var currentPane = session.PaneGroups.FirstOrDefault(p => string.Equals(p.Id, paneGroup.Id, StringComparison.OrdinalIgnoreCase));
            var tabDocs = new List<WorkspaceTabDocument>();
            var selectedTabId = paneGroup.SelectedTabId;

            if (currentPane is not null)
            {
                foreach (var tab in currentPane.Tabs)
                {
                    tabDocs.Add(BuildWorkspaceTabDocument(tab, workspaceRoot));
                }
                selectedTabId = currentPane.ActiveTab?.State.Id ?? "";
            }
            else
            {
                foreach (var tabDef in paneGroup.Tabs)
                {
                    tabDocs.Add(new WorkspaceTabDocument
                    {
                        Id = tabDef.Id,
                        BasePath = tabDef.BasePath,
                        SortColumn = tabDef.SortColumn,
                        SortAscending = tabDef.SortAscending,
                        ViewMode = tabDef.ViewMode,
                        IsFolderLocked = tabDef.IsFolderLocked
                    });
                }
            }

            return new WorkspaceLayoutDocument
            {
                Id = paneGroup.Id,
                Type = "paneGroup",
                SelectedTabId = selectedTabId,
                Tabs = tabDocs
            };
        }

        return new WorkspaceLayoutDocument { Type = "paneGroup" };
    }



    private static WorkspaceDefinition? BuildDefinition(
        WorkspaceDocument? document,
        WorkspaceLocalStateDocument? localState,
        string sourceDirectory,
        string? rootPath,
        string? sharedPath,
        string? localPath,
        SessionLayoutNodeState? sessionLayout = null)
    {
        if (document is null)
        {
            return null;
        }

        var pathBase = rootPath ?? sourceDirectory;
        var fallbackPaneGroup = BuildPaneGroupDefinition(
            "primary",
            document.SelectedTabIndex,
            document.Tabs ?? [],
            pathBase);

        WorkspaceLayoutNodeDefinition? layout = null;
        if (sessionLayout is not null)
        {
            layout = BuildLayoutDefinitionFromState(sessionLayout, localState, pathBase);
        }

        if (layout is null)
        {
            layout = BuildLayoutDefinition(document.Layout, pathBase)
                ?? fallbackPaneGroup;
        }

        if (localState is not null)
        {
            layout = ApplyLocalState(layout, localState, pathBase);
            fallbackPaneGroup = (WorkspacePaneGroupDefinition)(ApplyLocalState(fallbackPaneGroup, localState, pathBase));
        }

        var primaryPaneGroup = FindFirstPaneGroup(layout) ?? fallbackPaneGroup;
        var paneGroups = CollectPaneGroups(layout).ToList();
        if (paneGroups.Count == 0)
        {
            paneGroups.Add(primaryPaneGroup);
        }

        return new WorkspaceDefinition
        {
            WorkspaceId = document.WorkspaceId ?? "",
            Name = document.Name?.Trim() ?? "",
            SourceDirectory = sourceDirectory,
            RootPath = rootPath,
            SharedPath = sharedPath,
            LocalPath = localPath,
            SelectedTabIndex = Math.Max(0, document.SelectedTabIndex),
            IsWorkspaceLocked = localState?.IsWorkspaceLocked ?? false,
            ActivePaneId = string.IsNullOrWhiteSpace(localState?.ActivePaneId) || string.Equals(localState.ActivePaneId, "root", StringComparison.OrdinalIgnoreCase)
                ? primaryPaneGroup.Id
                : localState.ActivePaneId.Trim(),
            RootViewMode = GetRootViewMode(localState, pathBase, document.ViewMode),
            RootSortColumn = GetRootSortColumn(localState, pathBase),
            RootSortAscending = GetRootSortAscending(localState, pathBase),
            Layout = layout,
            PrimaryPaneGroup = primaryPaneGroup,
            PaneGroups = paneGroups,
            Tabs = primaryPaneGroup.Tabs
        };
    }

    private static WorkspaceLayoutNodeDefinition? BuildLayoutDefinition(
        WorkspaceLayoutDocument? layout,
        string workspaceRoot)
    {
        if (layout is null)
        {
            return null;
        }

        if (string.Equals(layout.Type, "split", StringComparison.OrdinalIgnoreCase))
        {
            var children = new List<WorkspaceLayoutDocument>();
            if (layout.First is not null)
            {
                children.Add(layout.First);
            }

            if (layout.Second is not null)
            {
                children.Add(layout.Second);
            }

            children.AddRange(layout.Children ?? []);
            if (children.Count < 2)
            {
                return null;
            }

            var first = BuildLayoutDefinition(children[0], workspaceRoot);
            var second = BuildLayoutDefinition(children[1], workspaceRoot);
            if (first is null || second is null)
            {
                return null;
            }

            return new WorkspaceSplitNodeDefinition(
                NormalizeNodeId(layout.Id, "split"),
                NormalizeOrientation(layout.Orientation),
                NormalizeRatio(layout.Ratio),
                first,
                second);
        }

        if (layout.Type is null
            || string.Equals(layout.Type, "paneGroup", StringComparison.OrdinalIgnoreCase))
        {
            var paneGroupDef = BuildPaneGroupDefinition(
                NormalizeNodeId(layout.Id, "pane"),
                layout.SelectedTabIndex,
                layout.Tabs ?? [],
                workspaceRoot);
            return paneGroupDef with { SelectedTabId = layout.SelectedTabId ?? "" };
        }

        return null;
    }

    private static WorkspaceLayoutNodeDefinition ApplyLocalState(
        WorkspaceLayoutNodeDefinition node,
        WorkspaceLocalStateDocument localState,
        string workspaceRoot)
    {
        return node switch
        {
            WorkspacePaneGroupDefinition paneGroup => ApplyLocalPaneState(paneGroup, localState, workspaceRoot),
            WorkspaceSplitNodeDefinition split => split with
            {
                First = ApplyLocalState(split.First, localState, workspaceRoot),
                Second = ApplyLocalState(split.Second, localState, workspaceRoot)
            },
            _ => node
        };
    }

    private static WorkspacePaneGroupDefinition ApplyLocalPaneState(
        WorkspacePaneGroupDefinition paneGroup,
        WorkspaceLocalStateDocument localState,
        string workspaceRoot)
    {
        var sharedTabs = paneGroup.Tabs
            .Select(tab => ApplyLocalTabState(tab, localState, workspaceRoot, paneGroup.Id))
            .ToList();

        var paneState = localState.PaneStates?.FirstOrDefault(p => string.Equals(p.PaneId, paneGroup.Id, StringComparison.OrdinalIgnoreCase));
        var updatedTabs = BuildLocalPaneTabs(paneGroup, paneState, localState, workspaceRoot, sharedTabs);
        if (paneState is not null)
        {
            var targetSelectedTabId = paneState.SelectedTabId;
            var tabIndex = -1;
            if (!string.IsNullOrWhiteSpace(targetSelectedTabId))
            {
                tabIndex = updatedTabs.FindIndex(t => string.Equals(t.Id, targetSelectedTabId, StringComparison.OrdinalIgnoreCase));
            }
            if (tabIndex < 0 && updatedTabs.Count > 0)
            {
                tabIndex = 0;
                targetSelectedTabId = updatedTabs[0].Id;
            }

            return paneGroup with
            {
                Tabs = updatedTabs,
                SelectedTabId = targetSelectedTabId ?? "",
                SelectedTabIndex = Math.Clamp(tabIndex, 0, Math.Max(0, updatedTabs.Count - 1))
            };
        }

        return paneGroup with { Tabs = updatedTabs };
    }

    private static List<WorkspaceTabDefinition> BuildLocalPaneTabs(
        WorkspacePaneGroupDefinition paneGroup,
        LocalPaneStateDocument? paneState,
        WorkspaceLocalStateDocument localState,
        string workspaceRoot,
        List<WorkspaceTabDefinition> sharedTabs)
    {
        var localTabs = paneState?.Tabs is { Count: > 0 }
            ? paneState.Tabs
            : localState.TabStates?
                .Where(tab => string.Equals(tab.PaneId, paneGroup.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (localTabs is not { Count: > 0 })
        {
            return sharedTabs;
        }

        var sharedById = sharedTabs
            .Where(tab => !string.IsNullOrWhiteSpace(tab.Id))
            .ToDictionary(tab => tab.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<WorkspaceTabDefinition>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var localTab in localTabs)
        {
            WorkspaceTabDefinition? tab = null;
            if (!string.IsNullOrWhiteSpace(localTab.TabId)
                && sharedById.TryGetValue(localTab.TabId, out var sharedTab))
            {
                tab = ApplyLocalTabState(sharedTab, localTab, workspaceRoot);
            }
            else
            {
                tab = BuildLocalOnlyTabDefinition(localTab, workspaceRoot);
            }

            if (tab is null || !seenIds.Add(tab.Id))
            {
                continue;
            }

            result.Add(tab);
        }

        // When pane-local tabs exist, they are the runtime tab list for that pane.
        // Do not append workspace.json tabs here; that reintroduces the startup/shutdown duplication loop.
        return result.Count == 0 ? sharedTabs : result;
    }

    private static WorkspacePaneGroupDefinition BuildPaneGroupDefinition(
        string id,
        int selectedTabIndex,
        IReadOnlyList<WorkspaceTabDocument> sourceTabs,
        string workspaceRoot)
    {
        var tabs = sourceTabs
            .Select(tab => BuildTabDefinition(tab, workspaceRoot, id))
            .Where(tab => tab is not null)
            .Select(tab => tab!)
            .ToList();

        if (tabs.Count == 0)
        {
            selectedTabIndex = 0;
        }

        return new WorkspacePaneGroupDefinition(
            id,
            Math.Clamp(selectedTabIndex, 0, Math.Max(0, tabs.Count - 1)),
            tabs);
    }

    private static WorkspacePaneGroupDefinition? FindFirstPaneGroup(WorkspaceLayoutNodeDefinition node)
    {
        return node switch
        {
            WorkspacePaneGroupDefinition paneGroup => paneGroup,
            WorkspaceSplitNodeDefinition split => FindFirstPaneGroup(split.First) ?? FindFirstPaneGroup(split.Second),
            _ => null
        };
    }

    private static IEnumerable<WorkspacePaneGroupDefinition> CollectPaneGroups(WorkspaceLayoutNodeDefinition node)
    {
        switch (node)
        {
            case WorkspacePaneGroupDefinition paneGroup:
                yield return paneGroup;
                break;
            case WorkspaceSplitNodeDefinition split:
                foreach (var paneGroup in CollectPaneGroups(split.First))
                {
                    yield return paneGroup;
                }

                foreach (var paneGroup in CollectPaneGroups(split.Second))
                {
                    yield return paneGroup;
                }

                break;
        }
    }

    private static WorkspaceTabDefinition? BuildTabDefinition(WorkspaceTabDocument tab, string workspaceRoot, string paneId)
    {
        var basePath = !string.IsNullOrWhiteSpace(tab.BasePath) ? tab.BasePath : tab.Path;
        var path = WorkspacePathService.ResolveWorkspacePath(basePath, workspaceRoot);
        if (path is null)
        {
            return null;
        }

        return new WorkspaceTabDefinition
        {
            Id = tab.Id ?? Guid.NewGuid().ToString("N"),
            BasePath = path,
            CurrentPath = path,
            SortColumn = string.IsNullOrWhiteSpace(tab.SortColumn) ? "Name" : tab.SortColumn.Trim(),
            SortAscending = tab.SortAscending,
            ViewMode = AppSettings.NormalizeDisplayMode(tab.ViewMode),
            IsFolderLocked = tab.IsFolderLocked || tab.Fixed
        };
    }

    private static WorkspaceTabDefinition ApplyLocalTabState(
        WorkspaceTabDefinition tab,
        WorkspaceLocalStateDocument localState,
        string workspaceRoot,
        string paneId)
    {
        var state = FindLocalTabState(localState, tab.Id, tab.BasePath, workspaceRoot, paneId);
        if (state is null)
        {
            return tab;
        }

        var currentPath = WorkspacePathService.ResolveWorkspaceCurrentPath(
                !string.IsNullOrWhiteSpace(state.CurrentPath) ? state.CurrentPath : state.Path,
                workspaceRoot)
            ?? tab.CurrentPath
            ?? tab.BasePath;

        return ApplyLocalTabState(tab, state, workspaceRoot, currentPath);
    }

    private static WorkspaceTabDefinition ApplyLocalTabState(
        WorkspaceTabDefinition tab,
        WorkspaceLocalTabStateDocument state,
        string workspaceRoot)
    {
        var currentPath = WorkspacePathService.ResolveWorkspaceCurrentPath(
                !string.IsNullOrWhiteSpace(state.CurrentPath) ? state.CurrentPath : state.Path,
                workspaceRoot)
            ?? tab.CurrentPath
            ?? tab.BasePath;

        return ApplyLocalTabState(tab, state, workspaceRoot, currentPath);
    }

    private static WorkspaceTabDefinition ApplyLocalTabState(
        WorkspaceTabDefinition tab,
        WorkspaceLocalTabStateDocument state,
        string workspaceRoot,
        string currentPath)
    {
        return new WorkspaceTabDefinition
        {
            Id = tab.Id,
            BasePath = tab.BasePath,
            CurrentPath = currentPath,
            SortColumn = string.IsNullOrWhiteSpace(state.SortColumn) ? tab.SortColumn : state.SortColumn.Trim(),
            SortAscending = state.SortAscending ?? tab.SortAscending,
            ViewMode = state.ViewMode is null ? tab.ViewMode : AppSettings.NormalizeDisplayMode(state.ViewMode.Value),
            FilterText = state.FilterText ?? tab.FilterText,
            SelectedPaths = state.SelectedPaths ?? tab.SelectedPaths,
            ScrollOffset = state.ScrollOffset ?? tab.ScrollOffset,
            IsFolderLocked = state.IsFolderLocked ?? tab.IsFolderLocked
        };
    }

    private static WorkspaceTabDefinition? BuildLocalOnlyTabDefinition(
        WorkspaceLocalTabStateDocument state,
        string workspaceRoot)
    {
        var currentPath = WorkspacePathService.ResolveWorkspaceCurrentPath(
            !string.IsNullOrWhiteSpace(state.CurrentPath) ? state.CurrentPath : state.Path,
            workspaceRoot);
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return null;
        }

        return new WorkspaceTabDefinition
        {
            Id = string.IsNullOrWhiteSpace(state.TabId) ? Guid.NewGuid().ToString("N") : state.TabId,
            BasePath = currentPath,
            CurrentPath = currentPath,
            SortColumn = string.IsNullOrWhiteSpace(state.SortColumn) ? "Name" : state.SortColumn.Trim(),
            SortAscending = state.SortAscending ?? true,
            ViewMode = state.ViewMode is null ? FileDisplayMode.Details : AppSettings.NormalizeDisplayMode(state.ViewMode.Value),
            FilterText = state.FilterText ?? "",
            SelectedPaths = state.SelectedPaths ?? [],
            ScrollOffset = state.ScrollOffset ?? 0,
            IsFolderLocked = state.IsFolderLocked ?? false
        };
    }

    private static FileDisplayMode GetRootViewMode(
        WorkspaceLocalStateDocument? localState,
        string workspaceRoot,
        FileDisplayMode defaultViewMode)
    {
        var state = localState is null ? null : FindLocalTabState(localState, "root", workspaceRoot, workspaceRoot, "root");
        return state?.ViewMode is null
            ? AppSettings.NormalizeDisplayMode(defaultViewMode)
            : AppSettings.NormalizeDisplayMode(state.ViewMode.Value);
    }

    private static string GetRootSortColumn(WorkspaceLocalStateDocument? localState, string workspaceRoot)
    {
        var state = localState is null ? null : FindLocalTabState(localState, "root", workspaceRoot, workspaceRoot, "root");
        return string.IsNullOrWhiteSpace(state?.SortColumn) ? "Name" : state.SortColumn.Trim();
    }

    private static bool GetRootSortAscending(WorkspaceLocalStateDocument? localState, string workspaceRoot)
    {
        var state = localState is null ? null : FindLocalTabState(localState, "root", workspaceRoot, workspaceRoot, "root");
        return state?.SortAscending ?? true;
    }

    private static WorkspaceLocalTabStateDocument? FindLocalTabState(
        WorkspaceLocalStateDocument localState,
        string tabId,
        string path,
        string workspaceRoot,
        string paneId)
    {
        if (localState.TabStates is not null)
        {
            foreach (var candidate in localState.TabStates)
            {
                if (string.Equals(candidate.TabId, tabId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            WorkspaceLocalTabStateDocument? fallback = null;
            foreach (var candidate in localState.TabStates)
            {
                if (string.IsNullOrWhiteSpace(candidate.TabId))
                {
                    var candPath = !string.IsNullOrWhiteSpace(candidate.CurrentPath) ? candidate.CurrentPath : candidate.Path;
                    var statePath = WorkspacePathService.ResolveWorkspaceCurrentPath(candPath, workspaceRoot);
                    if (statePath is not null && WorkspacePathService.IsSamePath(statePath, path))
                    {
                        if (string.Equals(candidate.PaneId, paneId, StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate;
                        }
                        fallback ??= candidate;
                    }
                }
            }
            if (fallback is not null)
            {
                return fallback;
            }
        }
        return null;
    }

    private static bool IsAutoWorkspaceFileName(string path)
    {
        var fileName = Path.GetFileName(path) ?? "";
        return fileName.Equals(".workspace.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".kakari-workspace.json", StringComparison.OrdinalIgnoreCase);
    }



    private static WorkspaceLocalTabStateDocument BuildLocalTabState(FolderTab tab, string? workspaceRoot, string paneId)
    {
        return new WorkspaceLocalTabStateDocument
        {
            TabId = tab.State.Id,
            PaneId = paneId,
            CurrentPath = WorkspacePathService.ToWorkspaceLocalCurrentPath(tab.Navigation.CurrentPath, workspaceRoot),
            ViewMode = AppSettings.NormalizeDisplayMode(tab.State.ViewMode),
            SortColumn = tab.State.SortColumn,
            SortAscending = tab.State.SortAscending,
            FilterText = tab.State.FilterText,
            SelectedPaths = tab.State.SelectedPaths,
            ScrollOffset = tab.State.VerticalOffset,
            IsFolderLocked = tab.IsFolderLocked
        };
    }

    private static void AddLocalPaneState(
        FolderPane pane,
        string? workspaceRoot,
        List<LocalPaneStateDocument> paneStates,
        List<WorkspaceLocalTabStateDocument> tabStates,
        HashSet<string> savedOwners)
    {
        var paneTabs = new List<WorkspaceLocalTabStateDocument>();
        var seenTabIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in pane.Tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.State.Id) || !seenTabIds.Add(tab.State.Id))
            {
                continue;
            }

            var tabState = BuildLocalTabState(tab, workspaceRoot, pane.Id);
            paneTabs.Add(tabState);
            if (savedOwners.Add(pane.Id + ":" + tab.State.Id))
            {
                tabStates.Add(tabState);
            }
        }

        if (paneTabs.Count == 0)
        {
            return;
        }

        var selectedTabId = pane.ActiveTab?.State.Id;
        if (string.IsNullOrWhiteSpace(selectedTabId)
            || !paneTabs.Any(tab => string.Equals(tab.TabId, selectedTabId, StringComparison.OrdinalIgnoreCase)))
        {
            selectedTabId = paneTabs[0].TabId ?? "";
        }

        paneStates.Add(new LocalPaneStateDocument
        {
            PaneId = pane.Id,
            SelectedTabId = selectedTabId,
            Tabs = paneTabs
        });
    }

    private static void SelfHealDocument(WorkspaceDocument document, string workspaceRoot, out bool isDirty, out string repairSummary)
    {
        isDirty = false;
        var seenPaneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTabIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repairedMissingTabIds = 0;
        var repairedDuplicateTabIds = 0;
        var repairedDuplicatePaneIds = 0;
        var repairedEmptyPanes = 0;
        var repairedMalformedSplits = 0;
        var repairedWorkspaceId = false;

        if (string.IsNullOrWhiteSpace(document.WorkspaceId))
        {
            document.WorkspaceId = Guid.NewGuid().ToString("N");
            repairedWorkspaceId = true;
            isDirty = true;
            PerfLog.Write($"[Warning] Workspace missing explicit workspaceId. Assigned UUID='{document.WorkspaceId}'");
        }

        if (document.Layout == null)
        {
            document.Layout = new WorkspaceLayoutDocument
            {
                Id = "primary",
                Type = "paneGroup",
                Tabs = document.Tabs ?? new List<WorkspaceTabDocument>(),
                SelectedTabIndex = document.SelectedTabIndex
            };
            document.Tabs = new List<WorkspaceTabDocument>();
            isDirty = true;
        }

        SelfHealLayoutNode(
            document.Layout,
            workspaceRoot,
            seenPaneIds,
            seenTabIds,
            ref repairedMissingTabIds,
            ref repairedDuplicateTabIds,
            ref repairedDuplicatePaneIds,
            ref repairedEmptyPanes,
            ref repairedMalformedSplits,
            ref isDirty);

        var summaries = new List<string>();
        if (repairedWorkspaceId) summaries.Add("repaired missing workspaceId");
        if (repairedMissingTabIds > 0) summaries.Add($"repaired {repairedMissingTabIds} missing tab ID{(repairedMissingTabIds > 1 ? "s" : "")}");
        if (repairedDuplicateTabIds > 0) summaries.Add($"repaired {repairedDuplicateTabIds} duplicate tab ID{(repairedDuplicateTabIds > 1 ? "s" : "")}");
        if (repairedDuplicatePaneIds > 0) summaries.Add($"repaired {repairedDuplicatePaneIds} duplicate pane ID{(repairedDuplicatePaneIds > 1 ? "s" : "")}");
        if (repairedEmptyPanes > 0) summaries.Add($"repaired {repairedEmptyPanes} empty pane{(repairedEmptyPanes > 1 ? "s" : "")}");
        if (repairedMalformedSplits > 0) summaries.Add($"repaired {repairedMalformedSplits} malformed split{(repairedMalformedSplits > 1 ? "s" : "")}");

        repairSummary = summaries.Count > 0 ? string.Join(", ", summaries) : "none";
    }

    private static void SelfHealLayoutNode(
        WorkspaceLayoutDocument node,
        string workspaceRoot,
        HashSet<string> seenPaneIds,
        HashSet<string> seenTabIds,
        ref int repairedMissingTabIds,
        ref int repairedDuplicateTabIds,
        ref int repairedDuplicatePaneIds,
        ref int repairedEmptyPanes,
        ref int repairedMalformedSplits,
        ref bool isDirty)
    {
        if (node == null) return;

        if (string.Equals(node.Type, "split", StringComparison.OrdinalIgnoreCase))
        {
            if (node.First == null || node.Second == null)
            {
                repairedMalformedSplits++;
                isDirty = true;
                var survivor = node.First ?? node.Second;
                if (survivor != null)
                {
                    node.Type = survivor.Type;
                    node.Id = survivor.Id;
                    node.Orientation = survivor.Orientation;
                    node.Ratio = survivor.Ratio;
                    node.SelectedTabIndex = survivor.SelectedTabIndex;
                    node.SelectedTabId = survivor.SelectedTabId;
                    node.First = survivor.First;
                    node.Second = survivor.Second;
                    node.Children = survivor.Children;
                    node.Tabs = survivor.Tabs;
                    SelfHealLayoutNode(node, workspaceRoot, seenPaneIds, seenTabIds, ref repairedMissingTabIds, ref repairedDuplicateTabIds, ref repairedDuplicatePaneIds, ref repairedEmptyPanes, ref repairedMalformedSplits, ref isDirty);
                }
                else
                {
                    node.Type = "paneGroup";
                    node.Id = Guid.NewGuid().ToString("N");
                    node.Tabs = new List<WorkspaceTabDocument>();
                }
            }
            else
            {
                if (node.Ratio < 0.1 || node.Ratio > 0.9)
                {
                    node.Ratio = 0.5;
                    isDirty = true;
                }
                SelfHealLayoutNode(node.First, workspaceRoot, seenPaneIds, seenTabIds, ref repairedMissingTabIds, ref repairedDuplicateTabIds, ref repairedDuplicatePaneIds, ref repairedEmptyPanes, ref repairedMalformedSplits, ref isDirty);
                SelfHealLayoutNode(node.Second, workspaceRoot, seenPaneIds, seenTabIds, ref repairedMissingTabIds, ref repairedDuplicateTabIds, ref repairedDuplicatePaneIds, ref repairedEmptyPanes, ref repairedMalformedSplits, ref isDirty);
            }
        }
        else if (node.Type == null || string.Equals(node.Type, "paneGroup", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                node.Id = Guid.NewGuid().ToString("N");
                isDirty = true;
            }
            else if (!seenPaneIds.Add(node.Id.Trim().ToLowerInvariant()))
            {
                var oldId = node.Id;
                var newId = Guid.NewGuid().ToString("N");
                node.Id = newId;
                repairedDuplicatePaneIds++;
                isDirty = true;
                seenPaneIds.Add(newId.ToLowerInvariant());
                PerfLog.Write($"[Warning] Duplicate paneGroup.id '{oldId}' detected. Conflicting pane re-assigned ID='{newId}'");
            }

            if (node.Tabs == null || node.Tabs.Count == 0)
            {
                node.Tabs = new List<WorkspaceTabDocument>
                {
                    new WorkspaceTabDocument
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        BasePath = workspaceRoot
                    }
                };
                repairedEmptyPanes++;
                isDirty = true;
            }

            foreach (var tab in node.Tabs)
            {
                if (string.IsNullOrWhiteSpace(tab.Id))
                {
                    var newTabId = Guid.NewGuid().ToString("N");
                    tab.Id = newTabId;
                    repairedMissingTabIds++;
                    isDirty = true;
                    seenTabIds.Add(newTabId.ToLowerInvariant());
                    PerfLog.Write($"[Warning] Tab missing explicit ID. Assigned UUID='{newTabId}'");
                }
                else if (!seenTabIds.Add(tab.Id.Trim().ToLowerInvariant()))
                {
                    var oldTabId = tab.Id;
                    var newTabId = Guid.NewGuid().ToString("N");
                    tab.Id = newTabId;
                    repairedDuplicateTabIds++;
                    isDirty = true;
                    seenTabIds.Add(newTabId.ToLowerInvariant());
                    PerfLog.Write($"[Warning] Duplicate tab.id '{oldTabId}' detected. Conflicting tab re-assigned UUID='{newTabId}'");
                }

                if (string.IsNullOrWhiteSpace(tab.BasePath) && !string.IsNullOrWhiteSpace(tab.Path))
                {
                    tab.BasePath = tab.Path;
                    isDirty = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(node.SelectedTabId))
            {
                var hasSelected = node.Tabs.Any(t => string.Equals(t.Id, node.SelectedTabId, StringComparison.OrdinalIgnoreCase));
                if (!hasSelected)
                {
                    node.SelectedTabId = node.Tabs[0].Id ?? "";
                    isDirty = true;
                    PerfLog.Write($"[Warning] selectedTabId '{node.SelectedTabId}' not found in pane '{node.Id}'. Fallback to index 0.");
                }
            }
            else
            {
                node.SelectedTabId = node.Tabs[0].Id ?? "";
                isDirty = true;
            }
        }
    }





    private sealed class WorkspaceDocument
    {
        public string? WorkspaceId { get; set; }

        public string? Name { get; set; }

        public string? RootPath { get; set; }

        public FileDisplayMode ViewMode { get; set; } = FileDisplayMode.Details;

        public int SelectedTabIndex { get; set; }

        public WorkspaceLayoutDocument? Layout { get; set; }

        public List<WorkspaceTabDocument> Tabs { get; set; } = [];
    }

    private sealed class WorkspaceLayoutDocument
    {
        public string? Id { get; set; }

        public string? Type { get; set; }

        public WorkspaceSplitOrientation Orientation { get; set; } = WorkspaceSplitOrientation.Horizontal;

        public double Ratio { get; set; } = 0.5;

        public int SelectedTabIndex { get; set; }

        public string? SelectedTabId { get; set; }

        public WorkspaceLayoutDocument? First { get; set; }

        public WorkspaceLayoutDocument? Second { get; set; }

        public List<WorkspaceLayoutDocument> Children { get; set; } = [];

        public List<WorkspaceTabDocument> Tabs { get; set; } = [];
    }

    private sealed class WorkspaceTabDocument
    {
        public string? Id { get; set; }

        public string? BasePath { get; set; }

        public string? Path { get; set; }

        public string? SortColumn { get; set; }

        public bool SortAscending { get; set; } = true;

        public FileDisplayMode ViewMode { get; set; } = FileDisplayMode.Details;

        public bool IsFolderLocked { get; set; }

        public bool Fixed { get; set; }
    }

    public sealed class WorkspaceLocalStateDocument
    {
        [JsonPropertyName("isWorkspaceLocked")]
        public bool IsWorkspaceLocked { get; set; }

        [JsonPropertyName("activePaneId")]
        public string? ActivePaneId { get; set; }

        [JsonPropertyName("paneStates")]
        public List<LocalPaneStateDocument> PaneStates { get; set; } = [];

        [JsonPropertyName("tabStates")]
        public List<WorkspaceLocalTabStateDocument> TabStates { get; set; } = [];

        [JsonPropertyName("columnWidths")]
        public Dictionary<string, double>? ColumnWidths { get; set; }
    }

    public sealed class LocalPaneStateDocument
    {
        [JsonPropertyName("paneId")]
        public string PaneId { get; set; } = "";

        [JsonPropertyName("selectedTabId")]
        public string SelectedTabId { get; set; } = "";

        [JsonPropertyName("tabs")]
        public List<WorkspaceLocalTabStateDocument> Tabs { get; set; } = [];
    }

    public sealed class WorkspaceLocalTabStateDocument
    {
        [JsonPropertyName("tabId")]
        public string? TabId { get; set; }

        [JsonPropertyName("paneId")]
        public string? PaneId { get; set; }

        [JsonPropertyName("currentPath")]
        public string? CurrentPath { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("viewMode")]
        public FileDisplayMode? ViewMode { get; set; }

        [JsonPropertyName("sortColumn")]
        public string? SortColumn { get; set; }

        [JsonPropertyName("sortAscending")]
        public bool? SortAscending { get; set; }

        [JsonPropertyName("filterText")]
        public string? FilterText { get; set; }

        [JsonPropertyName("selectedPaths")]
        public IReadOnlyList<string>? SelectedPaths { get; set; }

        [JsonPropertyName("scrollOffset")]
        public double? ScrollOffset { get; set; }

        [JsonPropertyName("isFolderLocked")]
        public bool? IsFolderLocked { get; set; }
    }



    private static string NormalizeNodeId(string? id, string fallbackPrefix)
    {
        return string.IsNullOrWhiteSpace(id)
            ? fallbackPrefix
            : id.Trim();
    }

    private static WorkspaceSplitOrientation NormalizeOrientation(WorkspaceSplitOrientation orientation)
    {
        return Enum.IsDefined(orientation)
            ? orientation
            : WorkspaceSplitOrientation.Horizontal;
    }

    private static double NormalizeRatio(double ratio)
    {
        return double.IsFinite(ratio) && ratio is >= 0.1 and <= 0.9
            ? ratio
            : 0.5;
    }

    public WorkspaceDefinition? LoadFromSessionTabState(SessionTabState tabState)
    {
        if (tabState is null || string.IsNullOrWhiteSpace(tabState.RootPath))
        {
            return null;
        }

        var pathBase = tabState.RootPath;
        var workspaceId = tabState.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            workspaceId = Guid.NewGuid().ToString("N");
        }

        var name = tabState.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Unsaved Workspace";
        }

        var layout = BuildLayoutDefinitionFromState(tabState.Layout, tabState.LocalState, pathBase);
        if (layout is null)
        {
            var defaultTab = new WorkspaceTabDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                BasePath = pathBase,
                CurrentPath = pathBase
            };
            layout = new WorkspacePaneGroupDefinition("primary", 0, [defaultTab]);
        }

        if (tabState.LocalState is not null)
        {
            layout = ApplyLocalState(layout, tabState.LocalState, pathBase);
        }

        var primaryPaneGroup = FindFirstPaneGroup(layout) ?? new WorkspacePaneGroupDefinition("primary", 0, []);
        var paneGroups = CollectPaneGroups(layout).ToList();
        if (paneGroups.Count == 0)
        {
            paneGroups.Add(primaryPaneGroup);
        }

        return new WorkspaceDefinition
        {
            WorkspaceId = workspaceId,
            Name = name,
            SourceDirectory = pathBase,
            RootPath = pathBase,
            SharedPath = null,
            LocalPath = null,
            SelectedTabIndex = 0,
            IsWorkspaceLocked = tabState.LocalState?.IsWorkspaceLocked ?? false,
            ActivePaneId = string.IsNullOrWhiteSpace(tabState.LocalState?.ActivePaneId) || string.Equals(tabState.LocalState.ActivePaneId, "root", StringComparison.OrdinalIgnoreCase)
                ? primaryPaneGroup.Id
                : tabState.LocalState.ActivePaneId.Trim(),
            RootViewMode = tabState.ViewMode,
            RootSortColumn = tabState.SortColumn ?? "Name",
            RootSortAscending = tabState.SortAscending,
            Layout = layout,
            PrimaryPaneGroup = primaryPaneGroup,
            PaneGroups = paneGroups,
            Tabs = primaryPaneGroup.Tabs
        };
    }

    public static SessionLayoutNodeState? BuildSessionLayoutState(WorkspaceLayoutNodeDefinition? node)
    {
        if (node is null)
        {
            return null;
        }

        var cleanedNode = StripRootPane(node);
        return BuildSessionLayoutStateInternal(cleanedNode);
    }

    public static WorkspaceLayoutNodeDefinition? StripRootPane(WorkspaceLayoutNodeDefinition? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is WorkspacePaneGroupDefinition pane)
        {
            if (string.Equals(pane.Id, "root", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return pane;
        }

        if (node is WorkspaceSplitNodeDefinition split)
        {
            var first = StripRootPane(split.First);
            var second = StripRootPane(split.Second);

            if (first is null && second is null)
            {
                return null;
            }
            if (first is null)
            {
                return second;
            }
            if (second is null)
            {
                return first;
            }

            return split with { First = first, Second = second };
        }

        return node;
    }

    private static SessionLayoutNodeState? BuildSessionLayoutStateInternal(WorkspaceLayoutNodeDefinition? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is WorkspaceSplitNodeDefinition split)
        {
            var first = BuildSessionLayoutStateInternal(split.First);
            var second = BuildSessionLayoutStateInternal(split.Second);
            var children = new List<SessionLayoutNodeState>();
            if (first is not null) children.Add(first);
            if (second is not null) children.Add(second);

            return new SessionLayoutNodeState
            {
                Type = split.Orientation == WorkspaceSplitOrientation.Vertical ? "vertical" : "horizontal",
                Ratio = split.Ratio,
                Children = children
            };
        }
        else if (node is WorkspacePaneGroupDefinition pane)
        {
            return new SessionLayoutNodeState
            {
                Type = "pane",
                PaneId = pane.Id
            };
        }

        return null;
    }

    public static WorkspaceLayoutNodeDefinition? BuildLayoutDefinitionFromState(
        SessionLayoutNodeState? state,
        WorkspaceLocalStateDocument? localState,
        string workspaceRoot)
    {
        if (state is null)
        {
            return null;
        }

        var type = state.Type?.ToLowerInvariant();
        if (type == "horizontal" || type == "vertical")
        {
            if (state.Children is null || state.Children.Count == 0)
            {
                return null;
            }

            if (state.Children.Count == 1)
            {
                return BuildLayoutDefinitionFromState(state.Children[0], localState, workspaceRoot);
            }

            var first = BuildLayoutDefinitionFromState(state.Children[0], localState, workspaceRoot);
            var second = BuildLayoutDefinitionFromState(state.Children[1], localState, workspaceRoot);
            if (first is null && second is null)
            {
                return null;
            }
            if (first is null)
            {
                return second;
            }
            if (second is null)
            {
                return first;
            }

            var orientation = type == "vertical" ? WorkspaceSplitOrientation.Vertical : WorkspaceSplitOrientation.Horizontal;
            return new WorkspaceSplitNodeDefinition(
                $"split_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                orientation,
                state.Ratio ?? 0.5,
                first,
                second);
        }
        else if (type == "pane")
        {
            var paneId = state.PaneId ?? "primary";
            if (string.Equals(paneId, "root", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var defaultTab = new WorkspaceTabDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                BasePath = workspaceRoot,
                CurrentPath = workspaceRoot
            };
            return new WorkspacePaneGroupDefinition(paneId, 0, [defaultTab]);
        }

        return null;
    }
}
