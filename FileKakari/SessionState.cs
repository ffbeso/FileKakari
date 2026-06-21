using System.Collections.Generic;

namespace FileKakari;

public sealed class SessionState
{
    public int Version { get; set; } = 1;

    public int SelectedTabIndex { get; set; }

    public List<SessionTabState> Tabs { get; set; } = [];

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public string? WindowState { get; set; }

    public Dictionary<string, FolderColumnWidthsState> FolderColumnWidths { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, double> ColumnWidths { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
}

public sealed class SessionTabState
{
    public string TabId { get; set; } = "";

    public string Path { get; set; } = "";

    public bool IsWorkspace { get; set; }

    public string WorkspacePath { get; set; } = "";

    public string RootPath { get; set; } = "";

    public string SortColumn { get; set; } = "Name";

    public bool SortAscending { get; set; } = true;

    public FileDisplayMode ViewMode { get; set; } = FileDisplayMode.Details;

    public bool IsFolderLocked { get; set; }

    public WorkspaceService.WorkspaceLocalStateDocument? LocalState { get; set; }

    public bool IsUnsavedWorkspace { get; set; }

    public string? WorkspaceId { get; set; }

    public string? Name { get; set; }

    public string? ActivePaneId { get; set; }

    public SessionLayoutNodeState? Layout { get; set; }
}

public sealed class SessionLayoutNodeState
{
    public string? Type { get; set; } // "pane", "horizontal", "vertical"

    public string? PaneId { get; set; }

    public double? Ratio { get; set; }

    public List<SessionLayoutNodeState> Children { get; set; } = [];
}
