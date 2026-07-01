using System.Linq;
using System.Windows.Controls;

namespace FileKakari;

internal sealed class ColumnLayoutService
{
    public static readonly string[] ColumnOrder = ["Name", "ModifiedAt", "Kind", "Size", "Extension", "CreatedAt", "AccessedAt", "Attributes", "FullPath", "ParentPath", "BaseName"];

    private AppSettings _settings;
    private readonly GridView _gridView;
    private readonly IReadOnlyDictionary<string, GridViewColumn> _columnsById;
    private readonly System.Collections.Generic.Dictionary<string, FolderColumnWidthsState> _sessionFolderColumnWidths;
    private readonly System.Collections.Generic.Dictionary<string, double> _sessionColumnWidths;

    private string? _lastPath;
    private WorkspaceSession? _lastSession;

    public ColumnLayoutService(
        AppSettings settings,
        GridView gridView,
        IReadOnlyDictionary<string, GridViewColumn> columnsById,
        System.Collections.Generic.Dictionary<string, FolderColumnWidthsState> sessionFolderColumnWidths,
        System.Collections.Generic.Dictionary<string, double> sessionColumnWidths)
    {
        _settings = settings;
        _gridView = gridView;
        _columnsById = columnsById;
        _sessionFolderColumnWidths = sessionFolderColumnWidths;
        _sessionColumnWidths = sessionColumnWidths;
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
    }

    public void Apply(string? currentPath, WorkspaceSession? activeSession, double parentWidth, System.Action? onWorkspaceDirty = null)
    {
        SaveColumnWidths(_lastPath, _lastSession, onWorkspaceDirty);

        _lastPath = currentPath;
        _lastSession = activeSession;

        var visibleColumns = GetVisibleColumnIds().ToHashSet(StringComparer.Ordinal);

        // Calculate other columns width
        double otherColumnsWidth = 0;
        foreach (var colId in visibleColumns)
        {
            if (colId == "Name") continue;
            var w = GetColumnWidth(colId, currentPath, activeSession, null);
            if (w <= 0)
            {
                w = _settings.ColumnWidths.TryGetValue(colId, out var dw) ? dw : 100;
            }
            otherColumnsWidth += w;
        }

        _gridView.Columns.Clear();
        foreach (var columnId in ColumnOrder)
        {
            if (!visibleColumns.Contains(columnId) || !_columnsById.TryGetValue(columnId, out var column))
            {
                continue;
            }

            var width = GetColumnWidth(columnId, currentPath, activeSession, null);
            if (width <= 0)
            {
                if (columnId == "Name")
                {
                    if (parentWidth > 150)
                    {
                        width = System.Math.Max(360, parentWidth - otherColumnsWidth - 25);
                    }
                    else
                    {
                        width = _settings.ColumnWidths.TryGetValue(columnId, out var dw) ? dw : 400;
                    }
                }
                else
                {
                    width = _settings.ColumnWidths.TryGetValue(columnId, out var dw) ? dw : 100;
                }
            }

            if (width > 0 && (double.IsNaN(column.Width) || System.Math.Abs(column.Width - width) > 0.1))
            {
                column.Width = width;
            }

            _gridView.Columns.Add(column);
        }
    }

    public IReadOnlyList<string> GetVisibleColumnIds()
    {
        var visibleColumns = _settings.VisibleColumns
            .Where(columnId => _columnsById.ContainsKey(columnId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return visibleColumns.Count == 0
            ? AppSettings.DefaultVisibleColumns
            : visibleColumns;
    }

    public bool ShouldLoadExtraColumns(bool extraColumnsEnabled, string sortColumn)
    {
        if (!extraColumnsEnabled)
        {
            return false;
        }

        return RequiresExtraColumnMetadata(sortColumn)
            || GetVisibleColumnIds().Any(RequiresExtraColumnMetadata);
    }

    internal string? GetAbsoluteFolderPath(string? path, WorkspaceSession? activeSession)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (SpecialLocationService.IsSpecialUri(path))
        {
            var resolvedSpecial = path.TrimEnd('/', '\\');
            PerfLog.WriteVerbose($"column-layout-resolve path=\"{path}\" resolved=\"{resolvedSpecial}\"");
            return resolvedSpecial;
        }

        try
        {
            var trimmed = path.Trim();
            string fullPath;
            if (activeSession != null && !string.IsNullOrWhiteSpace(activeSession.RootPath) && !System.IO.Path.IsPathFullyQualified(trimmed))
            {
                fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(activeSession.RootPath, trimmed));
            }
            else
            {
                fullPath = System.IO.Path.GetFullPath(trimmed);
            }

            var root = System.IO.Path.GetPathRoot(fullPath);
            string resolved;
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                if (!fullPath.EndsWith("\\") && !fullPath.EndsWith("/"))
                {
                    fullPath += "\\";
                }
                resolved = fullPath;
            }
            else
            {
                resolved = fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            }

            PerfLog.WriteVerbose($"column-layout-resolve path=\"{path}\" resolved=\"{resolved}\"");
            return resolved;
        }
        catch
        {
            var fallback = path.TrimEnd('/', '\\');
            PerfLog.WriteVerbose($"column-layout-resolve path=\"{path}\" resolved=\"{fallback}\" (fallback)");
            return fallback;
        }
    }

    public void SaveColumnWidths(string? currentPath, WorkspaceSession? activeSession, System.Action? onWorkspaceDirty = null)
    {
        if (_columnsById.Count == 0)
        {
            return;
        }

        var widths = new System.Collections.Generic.Dictionary<string, double>();
        var hasValidWidth = false;

        foreach (var (columnId, column) in _columnsById)
        {
            var width = GetPersistableWidth(column);
            if (width > 0)
            {
                widths[NormalizeColumnId(columnId)] = width;
                hasValidWidth = true;
            }
        }

        if (!hasValidWidth)
        {
            return;
        }

        SaveColumnWidthsForPath(currentPath, activeSession, null, widths, onWorkspaceDirty);
    }

    public void SaveColumnWidthsForPath(
        string? currentPath,
        WorkspaceSession? activeSession,
        string? paneId,
        System.Collections.Generic.Dictionary<string, double> widths,
        System.Action? onWorkspaceDirty = null,
        string? tabStateId = null)
    {
        var resolvedPath = GetAbsoluteFolderPath(currentPath, activeSession);

        var normalizedWidths = new System.Collections.Generic.Dictionary<string, double>();
        foreach (var (k, v) in widths)
        {
            if (!double.IsNaN(v) && !double.IsInfinity(v) && v > 0)
            {
                normalizedWidths[NormalizeColumnId(k)] = v;
            }
        }

        if (normalizedWidths.Count == 0)
        {
            PerfLog.WriteVerbose($"column-layout-save-skip reason=\"no-valid-width\" sessionId=\"{activeSession?.Id ?? "null"}\" paneId=\"{paneId ?? "null"}\" tabStateId=\"{tabStateId ?? "null"}\" path=\"{currentPath ?? ""}\" resolvedPath=\"{resolvedPath ?? ""}\"");
            return;
        }

        if (!string.IsNullOrWhiteSpace(resolvedPath) && !SpecialLocationService.IsSpecialUri(resolvedPath))
        {
            var lookupKey = BuildColumnWidthLookupKey(activeSession, paneId, tabStateId, resolvedPath);

            PerfLog.WriteVerbose(
                $"column-layout-save source=\"tab-state\" sessionId=\"{activeSession?.Id ?? "null"}\" paneId=\"{paneId ?? "null"}\" tabStateId=\"{tabStateId ?? "null"}\" " +
                $"path=\"{currentPath ?? ""}\" resolvedPath=\"{resolvedPath}\" generatedKey=\"{lookupKey}\" " +
                $"widths={string.Join(",", normalizedWidths.Select(kv => $"{kv.Key}:{kv.Value:N0}"))}");
            _sessionFolderColumnWidths[lookupKey] = new FolderColumnWidthsState
            {
                LastAccessUtc = System.DateTime.UtcNow,
                Widths = normalizedWidths
            };

            if (_sessionFolderColumnWidths.Count > 500)
            {
                var keysToRemove = _sessionFolderColumnWidths
                    .OrderBy(kvp => kvp.Value.LastAccessUtc)
                    .Take(100)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in keysToRemove)
                {
                    _sessionFolderColumnWidths.Remove(key);
                }
            }
        }

        foreach (var (columnId, w) in normalizedWidths)
        {
            _sessionColumnWidths[columnId] = w;
        }

        if (activeSession?.IsWorkspace == true)
        {
            activeSession.ColumnWidths = normalizedWidths;
        }

        onWorkspaceDirty?.Invoke();
    }

    internal double GetColumnWidth(string columnId, string? currentPath, WorkspaceSession? activeSession, string? paneId, string? tabStateId = null)
    {
        columnId = NormalizeColumnId(columnId);
        var resolvedPath = GetAbsoluteFolderPath(currentPath, activeSession);

        // Priority 1: workspaceId|paneId|tabStateId|absolutePath
        if (activeSession?.IsWorkspace == true && !string.IsNullOrWhiteSpace(paneId))
        {
            if (!string.IsNullOrWhiteSpace(resolvedPath) && !SpecialLocationService.IsSpecialUri(resolvedPath))
            {
                var tabKey = BuildColumnWidthLookupKey(activeSession, paneId, tabStateId, resolvedPath);
                if (!string.IsNullOrWhiteSpace(tabStateId)
                    && _sessionFolderColumnWidths.TryGetValue(tabKey, out var state))
                {
                    state.LastAccessUtc = System.DateTime.UtcNow;
                    if (state.Widths.TryGetValue(columnId, out var w))
                    {
                        PerfLog.WriteVerbose(
                            $"column-load hit=true source=\"tab-state\" sessionId=\"{activeSession.Id}\" paneId=\"{paneId}\" tabStateId=\"{tabStateId}\" " +
                            $"path=\"{currentPath ?? ""}\" resolvedPath=\"{resolvedPath}\" generatedKey=\"{tabKey}\" " +
                            $"column=\"{columnId}\" width={w:N0}");
                        return w;
                    }
                }

                PerfLog.WriteVerbose(
                    $"column-load hit=false source=\"tab-state\" sessionId=\"{activeSession.Id}\" paneId=\"{paneId}\" tabStateId=\"{tabStateId ?? "null"}\" " +
                    $"path=\"{currentPath ?? ""}\" resolvedPath=\"{resolvedPath}\" generatedKey=\"{tabKey}\" column=\"{columnId}\"");

                var paneKey = BuildLegacyPaneColumnWidthLookupKey(activeSession, paneId, resolvedPath);
                if (_sessionFolderColumnWidths.TryGetValue(paneKey, out state))
                {
                    state.LastAccessUtc = System.DateTime.UtcNow;
                    if (state.Widths.TryGetValue(columnId, out var w))
                    {
                        PerfLog.WriteVerbose(
                            $"column-load hit=true source=\"legacy-pane\" sessionId=\"{activeSession.Id}\" paneId=\"{paneId}\" tabStateId=\"{tabStateId ?? "null"}\" " +
                            $"path=\"{currentPath ?? ""}\" resolvedPath=\"{resolvedPath}\" generatedKey=\"{paneKey}\" " +
                            $"column=\"{columnId}\" width={w:N0}");
                        return w;
                    }
                }
            }
        }

        // Priority 2: absolutePath
        if (!string.IsNullOrWhiteSpace(resolvedPath) && !SpecialLocationService.IsSpecialUri(resolvedPath))
        {
            if (_sessionFolderColumnWidths.TryGetValue(resolvedPath, out var state))
            {
                state.LastAccessUtc = System.DateTime.UtcNow;
                if (state.Widths.TryGetValue(columnId, out var w))
                {
                    PerfLog.WriteVerbose(
                        $"column-load hit=true source=\"path\" sessionId=\"{activeSession?.Id ?? "null"}\" paneId=\"{paneId ?? "null"}\" tabStateId=\"{tabStateId ?? "null"}\" " +
                        $"path=\"{currentPath ?? ""}\" resolvedPath=\"{resolvedPath}\" generatedKey=\"{resolvedPath}\" " +
                        $"column=\"{columnId}\" width={w:N0}");
                    return w;
                }
            }
        }

        // Priority 3: global ColumnWidths
        if (_sessionColumnWidths.TryGetValue(columnId, out var globalWidth))
        {
            PerfLog.WriteVerbose(
                $"column-load hit=true source=\"global\" sessionId=\"{activeSession?.Id ?? "null"}\" paneId=\"{paneId ?? "null"}\" tabStateId=\"{tabStateId ?? "null"}\" " +
                $"path=\"{currentPath ?? ""}\" resolvedPath=\"{resolvedPath ?? ""}\" generatedKey=\"global\" " +
                $"column=\"{columnId}\" width={globalWidth:N0}");
            return globalWidth;
        }

        // Priority 4: default
        return -1;
    }

    internal static string BuildColumnWidthLookupKey(WorkspaceSession? activeSession, string? paneId, string? tabStateId, string? resolvedPath)
    {
        if (activeSession?.IsWorkspace == true && !string.IsNullOrWhiteSpace(paneId))
        {
            var workspaceId = !string.IsNullOrWhiteSpace(activeSession.Workspace?.WorkspaceId)
                ? activeSession.Workspace.WorkspaceId
                : activeSession.Id;
            if (!string.IsNullOrWhiteSpace(tabStateId))
            {
                return $"{workspaceId}|{paneId}|{tabStateId}|{resolvedPath}";
            }

            return $"{workspaceId}|{paneId}|{resolvedPath}";
        }

        return resolvedPath ?? "";
    }

    internal static string BuildLegacyPaneColumnWidthLookupKey(WorkspaceSession? activeSession, string? paneId, string? resolvedPath)
    {
        if (activeSession?.IsWorkspace == true && !string.IsNullOrWhiteSpace(paneId))
        {
            var workspaceId = !string.IsNullOrWhiteSpace(activeSession.Workspace?.WorkspaceId)
                ? activeSession.Workspace.WorkspaceId
                : activeSession.Id;
            return $"{workspaceId}|{paneId}|{resolvedPath}";
        }

        return resolvedPath ?? "";
    }

    private static double GetPersistableWidth(GridViewColumn column)
    {
        if (!double.IsNaN(column.Width) && !double.IsInfinity(column.Width) && column.Width > 0)
        {
            return column.Width;
        }

        return !double.IsNaN(column.ActualWidth) && !double.IsInfinity(column.ActualWidth) && column.ActualWidth > 0
            ? column.ActualWidth
            : -1;
    }

    public static string NormalizeColumnId(string columnId)
    {
        return columnId switch
        {
            "Type" => "Kind",
            "Modified" => "ModifiedAt",
            "Created" => "CreatedAt",
            _ => columnId
        };
    }

    public static System.Collections.Generic.Dictionary<string, double>? NormalizeColumnWidths(System.Collections.Generic.Dictionary<string, double>? widths)
    {
        if (widths == null) return null;
        var normalized = new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in widths)
        {
            normalized[NormalizeColumnId(k)] = v;
        }
        return normalized;
    }

    public static System.Collections.Generic.Dictionary<string, FolderColumnWidthsState>? NormalizeFolderColumnWidths(System.Collections.Generic.Dictionary<string, FolderColumnWidthsState>? folderColumnWidths)
    {
        if (folderColumnWidths == null) return null;
        var normalized = new System.Collections.Generic.Dictionary<string, FolderColumnWidthsState>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (k, state) in folderColumnWidths)
        {
            if (state.Widths != null)
            {
                state.Widths = NormalizeColumnWidths(state.Widths)!;
            }
            normalized[k] = state;
        }
        return normalized;
    }

    public static string NormalizeSortColumn(string? columnId)
    {
        if (columnId == null) return "Name";
        var normalized = NormalizeColumnId(columnId);
        return ColumnOrder.Contains(normalized, StringComparer.Ordinal) ? normalized : "Name";
    }

    public static bool RequiresExtraColumnMetadata(string columnId)
    {
        var normalized = NormalizeColumnId(columnId);
        return normalized is "CreatedAt" or "AccessedAt" or "Attributes";
    }
}
