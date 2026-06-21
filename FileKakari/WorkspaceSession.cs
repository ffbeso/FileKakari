using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;

namespace FileKakari;

public sealed class WorkspaceSession : INotifyPropertyChanged
{
    private readonly Dictionary<string, WorkspaceTabState> _tabStatesByOwner = new(StringComparer.OrdinalIgnoreCase);
    private WorkspacePaneGroup? _activePaneGroup;
    private string _activePaneId = "primary";
    private string _name = "";
    private string _renameText = "";
    private bool _isRenaming;
    private int _selectedTabIndex;
    private WorkspaceLayoutNodeDefinition? _layoutRoot;
    private WorkspaceLayoutNodeDefinition? _displayLayoutRoot;
    private bool _isActiveSession;

    public WorkspaceSession(
        string rootPath,
        ObservableCollection<FolderTab> tabs,
        WorkspaceDefinition? workspace = null,
        FileDisplayMode rootViewMode = FileDisplayMode.Details)
    {
        Id = Guid.NewGuid().ToString("N");
        RootPath = rootPath;
        Workspace = workspace;
        _name = BuildInitialName(rootPath, workspace);
        _renameText = _name;
        Tabs = tabs;
        Tabs.CollectionChanged += Tabs_CollectionChanged;
        foreach (var tab in Tabs)
        {
            tab.PropertyChanged += Tab_PropertyChanged;
        }

        PaneGroups.CollectionChanged += PaneGroups_CollectionChanged;
        foreach (var pane in PaneGroups)
        {
            pane.Tabs.CollectionChanged += PaneTabs_CollectionChanged;
        }
        _cachedIsWorkspace = IsWorkspace;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string RootPath { get; }

    public WorkspaceDefinition? Workspace { get; }

    public ObservableCollection<FolderTab> Tabs { get; }

    public ObservableCollection<WorkspacePaneGroup> PaneGroups { get; } = [];

    public WorkspaceSplitOrientation PaneSplitOrientation { get; set; } = WorkspaceSplitOrientation.Horizontal;

    public WorkspaceLayoutNodeDefinition? LayoutRoot
    {
        get => _layoutRoot;
        set
        {
            if (ReferenceEquals(_layoutRoot, value))
            {
                return;
            }

            _layoutRoot = value;
            OnPropertyChanged(nameof(LayoutRoot));
        }
    }

    public WorkspaceLayoutNodeDefinition? DisplayLayoutRoot
    {
        get => _displayLayoutRoot;
        set
        {
            if (ReferenceEquals(_displayLayoutRoot, value))
            {
                return;
            }

            _displayLayoutRoot = value;
            OnPropertyChanged(nameof(DisplayLayoutRoot));
        }
    }

    public bool IsActiveSession
    {
        get => _isActiveSession;
        set
        {
            if (_isActiveSession == value)
            {
                return;
            }

            _isActiveSession = value;
            OnPropertyChanged(nameof(IsActiveSession));
        }
    }

    public bool IsWorkspace
    {
        get
        {
            if (Workspace is not null)
            {
                return true;
            }

            if (PaneGroups.Count > 1)
            {
                return true;
            }

            if (PaneGroups.Count == 1 && PaneGroups[0].Tabs.Count > 1)
            {
                return true;
            }

            return false;
        }
    }

    public bool IsSaved => IsWorkspace && !string.IsNullOrWhiteSpace(Workspace?.SharedPath);

    public Dictionary<string, double>? ColumnWidths { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            var normalized = NormalizeWorkspaceName(value, RootPath, Workspace);
            if (string.Equals(_name, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _name = normalized;
            _renameText = normalized;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(RenameText));
            OnPropertyChanged(nameof(Header));
        }
    }

    public string RenameText
    {
        get => _renameText;
        set
        {
            var normalized = value ?? "";
            if (string.Equals(_renameText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _renameText = normalized;
            OnPropertyChanged(nameof(RenameText));
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value)
            {
                return;
            }

            _isRenaming = value;
            OnPropertyChanged(nameof(IsRenaming));
        }
    }

    public string ActivePaneId
    {
        get => _activePaneId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "primary" : value.Trim();
            if (string.Equals(_activePaneId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _activePaneId = normalized;
            OnPropertyChanged(nameof(ActivePaneId));
        }
    }

    public string Header
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }

            if (!IsWorkspace && Tabs.FirstOrDefault() is { } tab)
            {
                return tab.Header;
            }

            if (SpecialLocationService.IsSpecialUri(RootPath))
            {
                return AppStrings.Get("LocationThisPc");
            }

            var name = Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? RootPath : name;
        }
    }

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked == value)
            {
                return;
            }

            _isLocked = value;
            RefreshHeader();
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(IsFolderLocked));
        }
    }

    public bool IsFolderLocked => IsLocked;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            var normalized = Math.Max(0, value);
            if (_selectedTabIndex == normalized)
            {
                return;
            }

            _selectedTabIndex = normalized;
            OnPropertyChanged(nameof(SelectedTabIndex));
            OnPropertyChanged(nameof(Header));
        }
    }

    public WorkspacePaneGroup? ActivePaneGroup
    {
        get => _activePaneGroup;
        set
        {
            if (ReferenceEquals(_activePaneGroup, value))
            {
                return;
            }

            _activePaneGroup = value;
            ActivePaneId = value?.Id ?? "primary";
            OnPropertyChanged(nameof(ActivePaneGroup));
            OnPropertyChanged(nameof(ActiveFolderPane));
        }
    }

    public FolderPane? ActiveFolderPane => ActivePaneGroup;

    public void RefreshHeader()
    {
        OnPropertyChanged(nameof(Header));
    }

    public WorkspaceTabState GetOrCreateTabState(
        string paneId,
        string path,
        FileDisplayMode viewMode,
        string sortColumn,
        bool sortAscending,
        string? id = null)
    {
        var pathKey = NormalizePathKey(path);
        var normalizedPaneId = NormalizeOwnerPart(paneId);
        var ownerKey = string.IsNullOrWhiteSpace(id)
            ? $"{Id}:{normalizedPaneId}:path:{pathKey}"
            : $"{Id}:{normalizedPaneId}:id:{id.Trim()}";
        if (_tabStatesByOwner.TryGetValue(ownerKey, out var state))
        {
            return state;
        }

        state = new WorkspaceTabState(path, id ?? ownerKey, viewMode, paneId)
        {
            SortColumn = string.IsNullOrWhiteSpace(sortColumn) ? "Name" : sortColumn.Trim(),
            SortAscending = sortAscending
        };
        state.PaneId = normalizedPaneId;
        _tabStatesByOwner[ownerKey] = state;
        return state;
    }

    public void UpdateTabStatePane(string tabId, string oldPaneId, string newPaneId)
    {
        var oldNormalized = NormalizeOwnerPart(oldPaneId);
        var newNormalized = NormalizeOwnerPart(newPaneId);
        if (string.Equals(oldNormalized, newNormalized, StringComparison.Ordinal))
        {
            return;
        }

        var oldKey = $"{Id}:{oldNormalized}:id:{tabId.Trim()}";
        var newKey = $"{Id}:{newNormalized}:id:{tabId.Trim()}";

        if (_tabStatesByOwner.TryGetValue(oldKey, out var state))
        {
            _tabStatesByOwner.Remove(oldKey);
            state.PaneId = newNormalized;
            _tabStatesByOwner[newKey] = state;
        }
        else
        {
            var matchingPair = _tabStatesByOwner.FirstOrDefault(kv =>
                string.Equals(kv.Value.Id, tabId, StringComparison.OrdinalIgnoreCase));
            if (matchingPair.Value is not null)
            {
                _tabStatesByOwner.Remove(matchingPair.Key);
                matchingPair.Value.PaneId = newNormalized;
                _tabStatesByOwner[newKey] = matchingPair.Value;
            }
        }
    }

    public void RegisterTabState(WorkspaceTabState state)
    {
        var normalizedPaneId = NormalizeOwnerPart(state.PaneId);
        var ownerKey = $"{Id}:{normalizedPaneId}:id:{state.Id.Trim()}";
        _tabStatesByOwner[ownerKey] = state;
    }

    public void UnregisterTabState(string tabId, string paneId)
    {
        var normalized = NormalizeOwnerPart(paneId);
        var key = $"{Id}:{normalized}:id:{tabId.Trim()}";
        _tabStatesByOwner.Remove(key);
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (FolderTab tab in e.OldItems)
            {
                tab.PropertyChanged -= Tab_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (FolderTab tab in e.NewItems)
            {
                tab.PropertyChanged += Tab_PropertyChanged;
            }
        }

        RefreshHeader();
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderTab.Header) or nameof(FolderTab.IsFolderLocked))
        {
            RefreshHeader();
        }
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string NormalizeOwnerPart(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "primary" : value.Trim();
    }

    private static string BuildInitialName(string rootPath, WorkspaceDefinition? workspace)
    {
        return NormalizeWorkspaceName(workspace?.Name, rootPath, workspace);
    }

    private static string NormalizeWorkspaceName(string? value, string rootPath, WorkspaceDefinition? workspace)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        if (SpecialLocationService.IsSpecialUri(rootPath))
        {
            return AppStrings.Get("LocationThisPc");
        }

        var name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (!string.IsNullOrWhiteSpace(workspace?.SourceDirectory))
        {
            return workspace.SourceDirectory;
        }

        return string.IsNullOrWhiteSpace(rootPath) ? "Workspace" : rootPath;
    }

    private bool _cachedIsWorkspace;

    private void PaneGroups_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (WorkspacePaneGroup oldPane in e.OldItems)
            {
                oldPane.Tabs.CollectionChanged -= PaneTabs_CollectionChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (WorkspacePaneGroup newPane in e.NewItems)
            {
                newPane.Tabs.CollectionChanged += PaneTabs_CollectionChanged;
            }
        }

        UpdateIsWorkspaceState();
    }

    private void PaneTabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateIsWorkspaceState();
    }

    private void UpdateIsWorkspaceState()
    {
        var current = IsWorkspace;
        if (_cachedIsWorkspace != current)
        {
            _cachedIsWorkspace = current;
            OnPropertyChanged(nameof(IsWorkspace));
            OnPropertyChanged(nameof(IsSaved));
            OnPropertyChanged(nameof(Header));
        }
    }
}
