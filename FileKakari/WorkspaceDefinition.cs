namespace FileKakari;

public sealed class WorkspaceDefinition
{
    public string WorkspaceId { get; init; } = "";

    public string Name { get; init; } = "";

    public string SourceDirectory { get; init; } = "";

    public string? RootPath { get; init; }

    public bool HasRootPath => !string.IsNullOrWhiteSpace(RootPath);

    public string? SharedPath { get; init; }

    public bool IsSaved => !string.IsNullOrWhiteSpace(SharedPath);

    public string? LocalPath { get; init; }

    public int SelectedTabIndex { get; init; }

    public string ActivePaneId { get; init; } = "primary";

    public FileDisplayMode RootViewMode { get; init; } = FileDisplayMode.Details;

    public string RootSortColumn { get; init; } = "Name";

    public bool RootSortAscending { get; init; } = true;

    public WorkspaceLayoutNodeDefinition Layout { get; init; } = new WorkspacePaneGroupDefinition("primary", 0, []);

    public WorkspacePaneGroupDefinition PrimaryPaneGroup { get; init; } = new("primary", 0, []);


    public IReadOnlyList<WorkspacePaneGroupDefinition> PaneGroups { get; init; } = [];

    public IReadOnlyList<WorkspaceTabDefinition> Tabs { get; init; } = [];

    public bool IsWorkspaceLocked { get; init; }
}

public abstract record WorkspaceLayoutNodeDefinition(string Id);

public sealed record WorkspaceSplitNodeDefinition(
    string Id,
    WorkspaceSplitOrientation Orientation,
    double Ratio,
    WorkspaceLayoutNodeDefinition First,
    WorkspaceLayoutNodeDefinition Second) : WorkspaceLayoutNodeDefinition(Id);

public sealed record WorkspacePaneGroupDefinition(
    string Id,
    int SelectedTabIndex,
    IReadOnlyList<WorkspaceTabDefinition> Tabs) : WorkspaceLayoutNodeDefinition(Id)
{
    public string SelectedTabId { get; init; } = "";
}

public enum WorkspaceSplitOrientation
{
    Horizontal,
    Vertical
}

public sealed class WorkspaceTabDefinition
{
    public string Id { get; init; } = "";

    public string BasePath { get; init; } = "";

    public string CurrentPath { get; init; } = "";

    public string SortColumn { get; init; } = "Name";

    public bool SortAscending { get; init; } = true;

    public FileDisplayMode ViewMode { get; init; } = FileDisplayMode.Details;

    public string FilterText { get; init; } = "";

    public IReadOnlyList<string> SelectedPaths { get; init; } = [];

    public double ScrollOffset { get; init; }

    public bool IsFolderLocked { get; init; }
}
