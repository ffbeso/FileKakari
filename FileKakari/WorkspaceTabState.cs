namespace FileKakari;

public sealed class WorkspaceTabState
{
    private const int MaxNavigationViewStates = 64;
    private readonly Dictionary<string, NavigationViewState> _navigationViewStates = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceTabState(string currentPath, string? id = null, FileDisplayMode viewMode = FileDisplayMode.Details, string paneId = "primary")
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        PaneId = string.IsNullOrWhiteSpace(paneId) ? "primary" : paneId;
        CurrentPath = currentPath;
        BasePath = currentPath;
        ViewMode = AppSettings.NormalizeDisplayMode(viewMode);
    }

    public string Id { get; internal set; }

    public string PaneId { get; set; }

    public string BasePath { get; set; } = "";

    public string CurrentPath { get; set; }

    public string FilterText { get; set; } = "";

    public string SortColumn { get; set; } = "Name";

    public bool SortAscending { get; set; } = true;

    public FileDisplayMode ViewMode { get; set; }

    public string? CachedPath { get; set; }

    public IReadOnlyList<FileEntry>? CachedItems { get; set; }

    public DateTimeOffset? LastLoadedAt { get; set; }

    public bool HasPendingExternalChange { get; set; }

    public DateTimeOffset? LastExternalChangeAt { get; set; }

    public long? LastLoadElapsedMs { get; set; }

    public double VerticalOffset { get; set; }

    public IReadOnlyList<string> SelectedPaths { get; set; } = [];

    public void SaveNavigationViewState(
        string? path,
        string sortColumn,
        bool sortAscending,
        string? filterText,
        double verticalOffset,
        IReadOnlyList<string> selectedPaths)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(filterText))
        {
            return;
        }

        var key = CreateNavigationScrollKey(path, sortColumn, sortAscending);
        _navigationViewStates[key] = new NavigationViewState(
            Math.Max(0, verticalOffset),
            selectedPaths.ToList());
        if (_navigationViewStates.Count <= MaxNavigationViewStates)
        {
            return;
        }

        var firstKey = _navigationViewStates.Keys.FirstOrDefault();
        if (firstKey is not null)
        {
            _navigationViewStates.Remove(firstKey);
        }
    }

    public bool TryGetNavigationViewState(
        string? path,
        string sortColumn,
        bool sortAscending,
        string? filterText,
        out NavigationViewState viewState)
    {
        viewState = NavigationViewState.Empty;
        if (string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(filterText))
        {
            return false;
        }

        if (!_navigationViewStates.TryGetValue(
                CreateNavigationScrollKey(path, sortColumn, sortAscending),
                out var savedState))
        {
            return false;
        }

        viewState = savedState;
        return true;
    }
    public void CopyNavigationViewStatesFrom(WorkspaceTabState source)
    {
        _navigationViewStates.Clear();
        foreach (var kvp in source._navigationViewStates)
        {
            _navigationViewStates[kvp.Key] = new NavigationViewState(
                kvp.Value.VerticalOffset,
                kvp.Value.SelectedPaths?.ToList() ?? []
            );
        }
    }

    public void StoreItems(string path, IReadOnlyList<FileEntry> items)
    {
        CurrentPath = path;
        CachedPath = path;
        CachedItems = items;
        LastLoadedAt = DateTimeOffset.UtcNow;
    }

    public void ClearItems()
    {
        CachedPath = null;
        CachedItems = null;
        LastLoadedAt = null;
        SelectedPaths = [];
        VerticalOffset = 0;
        LastLoadElapsedMs = null;
    }

    public void MarkPendingExternalChange()
    {
        HasPendingExternalChange = true;
        LastExternalChangeAt = DateTimeOffset.UtcNow;
    }

    public void ClearPendingExternalChange()
    {
        HasPendingExternalChange = false;
        LastExternalChangeAt = null;
    }

    private static string CreateNavigationScrollKey(string path, string sortColumn, bool sortAscending)
    {
        return $"{path}|{sortColumn}|{sortAscending}";
    }
}

public sealed record NavigationViewState(double VerticalOffset, IReadOnlyList<string> SelectedPaths)
{
    public static NavigationViewState Empty { get; } = new(0, []);
}
