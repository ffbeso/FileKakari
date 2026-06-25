using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;

namespace FileKakari;

public sealed class FileListState : INotifyPropertyChanged
{
    private bool _isLoading;
    private string _currentPath = "";
    private double _scrollOffset;
    private IReadOnlyList<string> _selectedPaths = [];
    private DateTimeOffset? _lastLoadedAt;
    private DateTimeOffset? _lastExternalChangeAt;
    private string? _loadedPath;
    private string? _loadedStateId;
    private string _statusText = "";
    private string? _statusMessagePrefix;
    private string _displaySortColumn = "";
    private bool _displaySortAscending;
    private bool _displaySortFoldersFirst;
    private string _displayFilterText = "";

    public FileListState()
    {
        ItemsView = CollectionViewSource.GetDefaultView(Items);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplaySortColumn => _displaySortColumn;
    public bool DisplaySortAscending => _displaySortAscending;
    public bool DisplaySortFoldersFirst => _displaySortFoldersFirst;
    public string DisplayFilterText => _displayFilterText;

    public BulkObservableCollection<FileEntry> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    public IReadOnlyList<string> SelectedPaths
    {
        get => _selectedPaths;
        set
        {
            _selectedPaths = value;
            OnPropertyChanged(nameof(SelectedPaths));
        }
    }

    public double ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            if (Math.Abs(_scrollOffset - value) < 0.1)
            {
                return;
            }

            _scrollOffset = value;
            OnPropertyChanged(nameof(ScrollOffset));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "" : value;
            if (string.Equals(_currentPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _currentPath = normalized;
            OnPropertyChanged(nameof(CurrentPath));
        }
    }

    public DateTimeOffset? LastLoadedAt
    {
        get => _lastLoadedAt;
        set
        {
            if (_lastLoadedAt == value)
            {
                return;
            }

            _lastLoadedAt = value;
            OnPropertyChanged(nameof(LastLoadedAt));
        }
    }

    public string? LoadedPath
    {
        get => _loadedPath;
        private set
        {
            if (string.Equals(_loadedPath, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _loadedPath = value;
            OnPropertyChanged(nameof(LoadedPath));
        }
    }

    public string? LoadedStateId
    {
        get => _loadedStateId;
        private set
        {
            if (string.Equals(_loadedStateId, value, StringComparison.Ordinal))
            {
                return;
            }

            _loadedStateId = value;
            OnPropertyChanged(nameof(LoadedStateId));
        }
    }

    public DateTimeOffset? LastExternalChangeAt
    {
        get => _lastExternalChangeAt;
        set
        {
            if (_lastExternalChangeAt == value)
            {
                return;
            }

            _lastExternalChangeAt = value;
            OnPropertyChanged(nameof(LastExternalChangeAt));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            var normalized = value ?? "";
            if (string.Equals(_statusText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = normalized;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string? StatusMessagePrefix
    {
        get => _statusMessagePrefix;
        set
        {
            if (string.Equals(_statusMessagePrefix, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusMessagePrefix = value;
            OnPropertyChanged(nameof(StatusMessagePrefix));
        }
    }

    public bool IsLoadedFor(string stateId, string path)
    {
        return LastLoadedAt is not null
            && string.Equals(LoadedStateId, stateId, StringComparison.Ordinal)
            && string.Equals(LoadedPath, path, StringComparison.OrdinalIgnoreCase);
    }

    public void ReplaceItems(string path, IReadOnlyList<FileEntry> items, DateTimeOffset? loadedAt = null, string? loadedStateId = null)
    {
        Items.Clear();
        Items.AddRange(items);
        ItemsView.Refresh();
        CurrentPath = path;
        LastLoadedAt = loadedAt ?? DateTimeOffset.UtcNow;
        LoadedPath = path;
        LoadedStateId = loadedStateId;
    }

    public void MarkExternalChange(DateTimeOffset? changedAt = null)
    {
        LastExternalChangeAt = changedAt ?? DateTimeOffset.UtcNow;
    }

    public void ApplySort(string sortColumn, bool sortAscending, bool sortFoldersFirst, Dictionary<string, int>? currentOrder = null)
    {
        _displaySortColumn = sortColumn;
        _displaySortAscending = sortAscending;
        _displaySortFoldersFirst = sortFoldersFirst;
        if (ItemsView is ListCollectionView listView)
        {
            listView.CustomSort = new FileEntryComparer(sortColumn, sortAscending, sortFoldersFirst, currentOrder);
        }
        else
        {
            ItemsView.SortDescriptions.Clear();
            ItemsView.SortDescriptions.Add(new SortDescription(
                FileListSortHelper.GetSortPropertyName(sortColumn),
                sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));
        }
    }

    public void ClearSort()
    {
        _displaySortColumn = "";
        _displaySortAscending = true;
        if (ItemsView is ListCollectionView listView)
        {
            listView.CustomSort = null;
        }
        else
        {
            ItemsView.SortDescriptions.Clear();
        }
    }

    public void SetDisplayFilterText(string filterText)
    {
        _displayFilterText = filterText ?? "";
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record PathBreadcrumbSegment(string Text, string TargetPath, bool IsCurrent);

public class FolderPane : INotifyPropertyChanged
{
    private bool _isActive;
    private int _activeTabIndex;

    public FolderPane(string paneId, ObservableCollection<FolderTab> tabs, string? rootPath = null)
    {
        PaneId = string.IsNullOrWhiteSpace(paneId) ? "primary" : paneId;
        DisplayName = PaneId;
        RootPath = string.IsNullOrWhiteSpace(rootPath) ? "" : rootPath;
        Tabs = tabs;
        SyncFileListPath();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PaneId { get; }

    public string Id => PaneId;

    public string DisplayName { get; }

    public string RootPath { get; private set; }

    public ObservableCollection<FolderTab> Tabs { get; }

    public FileListState FileList { get; } = new();

    public ObservableCollection<PathBreadcrumbSegment> BreadcrumbSegments { get; } = [];

    public BulkObservableCollection<FileEntry> Items => FileList.Items;

    public ICollectionView ItemsView => FileList.ItemsView;

    public IReadOnlyList<string> SelectedPaths
    {
        get => FileList.SelectedPaths;
        set => FileList.SelectedPaths = value;
    }

    public double ScrollOffset
    {
        get => FileList.ScrollOffset;
        set => FileList.ScrollOffset = value;
    }

    private string? _selectedTabId;
    public string? SelectedTabId
    {
        get => _selectedTabId ?? ActiveTab?.Id;
        set
        {
            if (string.Equals(_selectedTabId, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedTabId = value;
            if (value is not null)
            {
                var targetIndex = -1;
                for (int i = 0; i < Tabs.Count; i++)
                {
                    if (Tabs[i].Id == value)
                    {
                        targetIndex = i;
                        break;
                    }
                }
                if (targetIndex >= 0)
                {
                    ActiveTabIndex = targetIndex;
                }
            }
            OnPropertyChanged(nameof(SelectedTabId));
            OnPropertyChanged(nameof(ActiveTab));
            OnPropertyChanged(nameof(ActiveTabState));
        }
    }

    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            var normalized = Math.Max(0, value);
            if (_activeTabIndex == normalized)
            {
                return;
            }

            _activeTabIndex = normalized;
            _selectedTabId = ActiveTab?.Id;
            SyncFileListPath();
            OnPropertyChanged(nameof(ActiveTabIndex));
            OnPropertyChanged(nameof(SelectedTabIndex));
            OnPropertyChanged(nameof(SelectedTabId));
            OnPropertyChanged(nameof(ActiveTab));
            OnPropertyChanged(nameof(ActiveTabState));
            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(CurrentPathSummary));
        }
    }

    public int SelectedTabIndex
    {
        get => ActiveTabIndex;
        set => ActiveTabIndex = value;
    }

    public string Appearance { get; set; } = "";

    public bool IsMinimal { get; set; }

    public bool IsFixed { get; set; }

    public WorkspaceTabState? ActiveTabState =>
        Tabs.Count == 0
            ? null
            : Tabs[Math.Clamp(ActiveTabIndex, 0, Tabs.Count - 1)].State;

    public FolderTab? ActiveTab =>
        Tabs.Count == 0
            ? null
            : Tabs[Math.Clamp(ActiveTabIndex, 0, Tabs.Count - 1)];

    public string CurrentPath => ActiveTabState?.CurrentPath ?? FileList.CurrentPath;

    public bool CanGoBack => ActiveTab?.Navigation.CanGoBack ?? false;

    public bool CanGoForward => ActiveTab?.Navigation.CanGoForward ?? false;

    public bool CanGoUp => ActiveTab?.Navigation.CanGoUp ?? false;

    public DateTimeOffset? LastLoadedAt => FileList.LastLoadedAt;

    public DateTimeOffset? LastExternalChangeAt => FileList.LastExternalChangeAt;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public bool IsLoading
    {
        get => FileList.IsLoading;
        set => FileList.IsLoading = value;
    }

    public bool IsActiveStateLoaded =>
        ActiveTabState is { } state
        && !state.HasPendingExternalChange
        && FileList.IsLoadedFor(state.Id, state.CurrentPath);

    public string CurrentPathSummary
    {
        get
        {
            if (Tabs.Count == 0)
            {
                return "";
            }

            var index = Math.Clamp(SelectedTabIndex, 0, Tabs.Count - 1);
            var path = Tabs[index].Navigation.CurrentPath;
            if (SpecialLocationService.IsSpecialUri(path))
            {
                return AppStrings.Get("LocationThisPc");
            }

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
    }

    public WorkspaceDefinition? Workspace { get; private set; }

    public void SetWorkspace(WorkspaceDefinition? workspace)
    {
        Workspace = workspace;
        if (workspace?.RootPath is { Length: > 0 } rootPath)
        {
            RootPath = rootPath;
        }
    }

    public void ResolveTabHeaders()
    {
        if (Tabs.Count == 0) return;

        var leafToTabs = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<FolderTab>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Tabs)
        {
            tab.SetHeaderOverride(null);
            var leaf = tab.Header;
            if (!leafToTabs.TryGetValue(leaf, out var list))
            {
                list = new System.Collections.Generic.List<FolderTab>();
                leafToTabs[leaf] = list;
            }
            list.Add(tab);
        }

        foreach (var kvp in leafToTabs)
        {
            var leaf = kvp.Key;
            var tabList = kvp.Value;

            if (tabList.Count > 1)
            {
                foreach (var tab in tabList)
                {
                    var path = tab.Navigation.CurrentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var parentPath = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(parentPath))
                    {
                        var parentName = Path.GetFileName(parentPath);
                        if (string.IsNullOrWhiteSpace(parentName))
                        {
                            parentName = parentPath;
                        }
                        tab.SetHeaderOverride($"{leaf} ({parentName})");
                    }
                    else
                    {
                        tab.SetHeaderOverride(leaf);
                    }
                }
            }
            else
            {
                tabList[0].SetHeaderOverride(null);
            }
        }
    }

    public void AddTab(FolderTab tab)
    {
        Tabs.Add(tab);
        SelectedTabId = tab.Id;
        ResolveTabHeaders();
    }

    public void RemoveTab(FolderTab tab, string fallbackPath)
    {
        var removedIndex = Tabs.IndexOf(tab);
        var selectedTabId = SelectedTabId;
        var wasSelected = string.Equals(selectedTabId, tab.Id, StringComparison.Ordinal);

        Tabs.Remove(tab);
        if (Tabs.Count == 0)
        {
            var fallbackTab = new FolderTab(fallbackPath);
            Tabs.Add(fallbackTab);
            SelectedTabId = fallbackTab.Id;
            ResolveTabHeaders();
            return;
        }

        if (wasSelected)
        {
            var fallbackIndex = Math.Clamp(removedIndex, 0, Tabs.Count - 1);
            _selectedTabId = null;
            SelectedTabId = Tabs[fallbackIndex].Id;
        }
        else if (!string.IsNullOrWhiteSpace(selectedTabId)
            && Tabs.Any(t => string.Equals(t.Id, selectedTabId, StringComparison.Ordinal)))
        {
            _selectedTabId = null;
            SelectedTabId = selectedTabId;
        }
        ResolveTabHeaders();
        RefreshDisplay();
    }

    public bool MoveTab(FolderTab tab, int targetIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        if (oldIndex < 0 || Tabs.Count <= 1)
        {
            return false;
        }

        var newIndex = Math.Clamp(targetIndex, 0, Tabs.Count - 1);
        if (oldIndex == newIndex)
        {
            return false;
        }

        var selectedTabId = SelectedTabId;
        Tabs.Move(oldIndex, newIndex);
        if (!string.IsNullOrWhiteSpace(selectedTabId))
        {
            _selectedTabId = null;
            SelectedTabId = selectedTabId;
        }
        else
        {
            ActiveTabIndex = Math.Clamp(ActiveTabIndex, 0, Tabs.Count - 1);
        }

        ResolveTabHeaders();
        RefreshDisplay();
        return true;
    }

    public void ReplaceTabsFrom(IEnumerable<FolderTab> tabs)
    {
        Tabs.Clear();
        foreach (var tab in tabs)
        {
            Tabs.Add(tab);
        }
        ResolveTabHeaders();

        SyncFileListPath();
        OnPropertyChanged(nameof(CurrentPathSummary));
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(ActiveTabState));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
    }

    public void RefreshDisplay()
    {
        SyncFileListPath();
        OnPropertyChanged(nameof(CurrentPathSummary));
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(ActiveTabState));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(LastLoadedAt));
        OnPropertyChanged(nameof(LastExternalChangeAt));
        OnPropertyChanged(nameof(FileList));
        OnPropertyChanged(nameof(Items));
    }

    private void SyncFileListPath()
    {
        FileList.CurrentPath = ActiveTabState?.CurrentPath ?? RootPath;
        ReplaceBreadcrumbSegments(BreadcrumbSegments, FileList.CurrentPath);
    }

    public static void ReplaceBreadcrumbSegments(ObservableCollection<PathBreadcrumbSegment> target, string path)
    {
        target.Clear();
        foreach (var segment in CreateBreadcrumbSegments(path))
        {
            target.Add(segment);
        }
    }

    public static IReadOnlyList<PathBreadcrumbSegment> CreateBreadcrumbSegments(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        if (SpecialLocationService.IsSpecialUri(path))
        {
            return [new PathBreadcrumbSegment(AppStrings.Get("LocationThisPc"), SpecialLocationService.ThisPcUri, true)];
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return [new PathBreadcrumbSegment(fullPath, fullPath, true)];
            }

            var normalizedRoot = EnsureDirectorySeparator(root);
            var result = new List<PathBreadcrumbSegment>
            {
                new(AppStrings.Get("LocationThisPc"), SpecialLocationService.ThisPcUri, false),
                new(FormatRoot(root), normalizedRoot, string.Equals(EnsureDirectorySeparator(fullPath), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            };
            var relative = Path.GetRelativePath(normalizedRoot, fullPath);
            if (!string.IsNullOrWhiteSpace(relative) && relative != ".")
            {
                var current = normalizedRoot;
                foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    result.Add(new PathBreadcrumbSegment(
                        segment,
                        current,
                        string.Equals(Path.GetFullPath(current), fullPath, StringComparison.OrdinalIgnoreCase)));
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [new PathBreadcrumbSegment(path, path, true)];
        }
    }

    private static string FormatRoot(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(trimmed) ? root : trimmed;
    }

    private static string EnsureDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class WorkspacePaneGroup : FolderPane
{
    public WorkspacePaneGroup(string id, ObservableCollection<FolderTab> tabs, string? rootPath = null)
        : base(id, tabs, rootPath)
    {
    }
}
