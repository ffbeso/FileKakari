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

    public void Apply(string? currentPath, WorkspaceSession? activeSession, System.Action? onWorkspaceDirty = null)
    {
        SaveColumnWidths(_lastPath, _lastSession, onWorkspaceDirty);

        _lastPath = currentPath;
        _lastSession = activeSession;

        var visibleColumns = GetVisibleColumnIds().ToHashSet(StringComparer.Ordinal);
        _gridView.Columns.Clear();
        foreach (var columnId in ColumnOrder)
        {
            if (!visibleColumns.Contains(columnId) || !_columnsById.TryGetValue(columnId, out var column))
            {
                continue;
            }

            var width = GetColumnWidth(columnId, currentPath, activeSession, null);
            if (width > 0)
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
            if (!double.IsNaN(column.Width) && column.Width > 0)
            {
                widths[NormalizeColumnId(columnId)] = column.Width;
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
        System.Action? onWorkspaceDirty = null)
    {
        var resolvedPath = GetAbsoluteFolderPath(currentPath, activeSession);

        var normalizedWidths = new System.Collections.Generic.Dictionary<string, double>();
        foreach (var (k, v) in widths)
        {
            normalizedWidths[NormalizeColumnId(k)] = v;
        }

        if (!string.IsNullOrWhiteSpace(resolvedPath) && !SpecialLocationService.IsSpecialUri(resolvedPath))
        {
            string lookupKey;
            if (activeSession?.IsWorkspace == true && !string.IsNullOrWhiteSpace(paneId))
            {
                var workspaceId = !string.IsNullOrWhiteSpace(activeSession.Workspace?.WorkspaceId)
                    ? activeSession.Workspace.WorkspaceId
                    : activeSession.Id;
                lookupKey = $"{workspaceId}|{paneId}|{resolvedPath}";
            }
            else
            {
                lookupKey = resolvedPath;
            }

            PerfLog.WriteVerbose($"column-layout-save path=\"{lookupKey}\" widths={string.Join(",", normalizedWidths.Select(kv => $"{kv.Key}:{kv.Value:N0}"))}");
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

    internal double GetColumnWidth(string columnId, string? currentPath, WorkspaceSession? activeSession, string? paneId)
    {
        columnId = NormalizeColumnId(columnId);
        var resolvedPath = GetAbsoluteFolderPath(currentPath, activeSession);

        // Priority 1: workspaceId|paneId|absolutePath
        if (activeSession?.IsWorkspace == true && !string.IsNullOrWhiteSpace(paneId))
        {
            var workspaceId = !string.IsNullOrWhiteSpace(activeSession.Workspace?.WorkspaceId)
                ? activeSession.Workspace.WorkspaceId
                : activeSession.Id;
            var compositeKey = $"{workspaceId}|{paneId}|{resolvedPath}";

            if (!string.IsNullOrWhiteSpace(compositeKey) && !SpecialLocationService.IsSpecialUri(compositeKey))
            {
                if (_sessionFolderColumnWidths.TryGetValue(compositeKey, out var state))
                {
                    state.LastAccessUtc = System.DateTime.UtcNow;
                    if (state.Widths.TryGetValue(columnId, out var w))
                    {
                        PerfLog.WriteVerbose($"column-load path=\"{compositeKey}\" column=\"{columnId}\" width={w:N0}");
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
                    PerfLog.WriteVerbose($"column-load path=\"{resolvedPath}\" column=\"{columnId}\" width={w:N0} (path-fallback)");
                    return w;
                }
            }
        }

        // Priority 3: global ColumnWidths
        if (_sessionColumnWidths.TryGetValue(columnId, out var globalWidth))
        {
            PerfLog.WriteVerbose($"column-load path=\"{resolvedPath}\" column=\"{columnId}\" width={globalWidth:N0} (global)");
            return globalWidth;
        }

        // Priority 4: default
        return -1;
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
