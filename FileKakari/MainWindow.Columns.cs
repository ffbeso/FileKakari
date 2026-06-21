using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FileKakari;

public partial class MainWindow
{
    private bool _isApplyingColumnWidths;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridViewColumn, EventHandler> _hookedColumns = new();

    private void InitializeColumns()
    {
        _columnsById = new Dictionary<string, GridViewColumn>
        {
            ["Name"] = NameColumn,
            ["Kind"] = KindColumn,
            ["Size"] = SizeColumn,
            ["ModifiedAt"] = ModifiedAtColumn,
            ["CreatedAt"] = CreatedAtColumn,
            ["AccessedAt"] = AccessedAtColumn,
            ["Extension"] = ExtensionColumn,
            ["Attributes"] = AttributesColumn,
            ["FullPath"] = FullPathColumn,
            ["ParentPath"] = ParentPathColumn,
            ["BaseName"] = BaseNameColumn
        };

        foreach (var (columnId, column) in _columnsById)
        {
            column.Header = CreateColumnHeader(columnId, column.Header?.ToString() ?? columnId);
            DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn))
                .AddValueChanged(column, (s, e) => OnMainColumnWidthChanged());
        }
    }

    private void OnMainColumnWidthChanged()
    {
        if (_isLoading || !IsLoaded || _isApplyingColumnWidths)
        {
            return;
        }

        SaveColumnWidths();
    }

    private TextBlock CreateColumnHeader(string columnId, string text)
    {
        return new TextBlock
        {
            Text = text,
            Tag = columnId
        };
    }

    private void SetColumnHeaderText(GridViewColumn column, string text)
    {
        if (column.Header is TextBlock header)
        {
            header.Text = text;
            return;
        }

        column.Header = text;
    }

    private void ApplyColumnSettings()
    {
        ApplyColumnSettings(ActiveTab);
    }

    private void ApplyColumnSettings(FolderTab? tab)
    {
        _isApplyingColumnWidths = true;
        try
        {
            var path = tab?.Navigation.CurrentPath;
            var resolvedPath = _columnLayout.GetAbsoluteFolderPath(path, _activeWorkspaceSession);
            PerfLog.Write($"column-apply path=\"{resolvedPath}\" gridHash={DetailsGridView.GetHashCode()}");

            var stopwatch = Stopwatch.StartNew();
            _columnLayout.Apply(path, _activeWorkspaceSession, OnWorkspaceColumnWidthsDirty);
            stopwatch.Stop();
            var displayMode = tab is not null
                ? tab.State.ViewMode
                : _settingsService.Settings.DisplayMode;
            _performanceLogger.Write($"columns-apply visible={DetailsGridView.Columns.Count} elapsedMs={stopwatch.ElapsedMilliseconds} isLoading={_isLoading} items={_items.Count} display={displayMode}");
        }
        finally
        {
            _isApplyingColumnWidths = false;
        }
    }

    private IReadOnlyList<string> GetVisibleColumnIds()
    {
        return _columnLayout.GetVisibleColumnIds();
    }

    private bool ShouldLoadExtraColumns(string sortColumn)
    {
        return _columnLayout.ShouldLoadExtraColumns(_devListPerfOptions.ExtraColumnsEnabled, sortColumn);
    }

    private void SaveColumnWidths()
    {
        var path = ActiveTab?.Navigation.CurrentPath;
        var resolvedPath = _columnLayout.GetAbsoluteFolderPath(path, _activeWorkspaceSession);
        PerfLog.Write($"column-save path=\"{resolvedPath}\" gridHash={DetailsGridView.GetHashCode()}");
        _columnLayout.SaveColumnWidths(path, _activeWorkspaceSession, OnWorkspaceColumnWidthsDirty);
    }

    private void OnWorkspaceColumnWidthsDirty()
    {
        _workspaceLocalState.MarkDirty("column-widths");
    }

    private void ApplyColumnWidthsToWorkspacePane(FolderPane pane)
    {
        var listView = FindListViewForPane(pane);
        if (listView is null)
        {
            PerfLog.Write($"column-apply-skip paneId=\"{pane.Id}\" reason=\"listview-not-found\"");
            return;
        }
        ApplyColumnWidthsToWorkspacePane(listView, pane);
    }

    private void ApplyColumnWidthsToWorkspacePane(ListView listView, FolderPane pane)
    {
        _isApplyingColumnWidths = true;
        try
        {
            if (listView.View is GridView gridView)
            {
                var tab = pane.ActiveTab;
                var path = tab?.Navigation.CurrentPath;
                var resolvedPath = _columnLayout.GetAbsoluteFolderPath(path, _activeWorkspaceSession);
                PerfLog.Write($"column-apply path=\"{resolvedPath}\" gridHash={gridView.GetHashCode()}");

                foreach (var column in gridView.Columns)
                {
                    if (column.Header is TextBlock textBlock && textBlock.Tag is string columnId)
                    {
                        var width = _columnLayout.GetColumnWidth(columnId, path, _activeWorkspaceSession, pane.Id);
                        if (width > 0)
                        {
                            column.Width = width;
                        }
                    }
                }
            }
        }
        finally
        {
            _isApplyingColumnWidths = false;
        }
    }

    private void HookWorkspacePaneColumnWidthChanges(FolderPane pane)
    {
        var listView = FindListViewForPane(pane);
        if (listView is not null)
        {
            HookWorkspacePaneColumnWidthChanges(listView, pane);
        }
    }

    private void HookWorkspacePaneColumnWidthChanges(ListView listView, FolderPane pane)
    {
        if (listView.View is GridView gridView)
        {
            foreach (var column in gridView.Columns)
            {
                if (_hookedColumns.TryGetValue(column, out _))
                {
                    continue;
                }

                EventHandler handler = (s, e) => OnWorkspacePaneColumnWidthChanged(pane);
                _hookedColumns.Add(column, handler);
                DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn))
                    .AddValueChanged(column, handler);
            }
        }
    }

    private void UnhookWorkspacePaneColumnWidthChanges(ListView listView)
    {
        if (listView.View is GridView gridView)
        {
            foreach (var column in gridView.Columns)
            {
                if (_hookedColumns.TryGetValue(column, out var handler))
                {
                    DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn))
                        .RemoveValueChanged(column, handler);
                    _hookedColumns.Remove(column);
                }
            }
        }
    }

    private void OnWorkspacePaneColumnWidthChanged(FolderPane pane)
    {
        if (_isLoading || !IsLoaded || _isApplyingColumnWidths)
        {
            return;
        }

        SaveWorkspacePaneColumnWidths(pane);
    }

    private void SaveWorkspacePaneColumnWidths(FolderPane pane)
    {
        if (pane.ActiveTab is { } tab)
        {
            SaveWorkspacePaneColumnWidthsForTab(pane, tab);
        }
    }

    private void SaveWorkspacePaneColumnWidthsForTab(FolderPane pane, FolderTab tab)
    {
        var listView = FindListViewForPane(pane);
        if (listView is null || listView.View is not GridView gridView)
        {
            return;
        }

        var widths = new Dictionary<string, double>();
        var hasValidWidth = false;

        foreach (var column in gridView.Columns)
        {
            if (column.Header is TextBlock textBlock && textBlock.Tag is string columnId)
            {
                if (!double.IsNaN(column.Width) && column.Width > 0)
                {
                    widths[columnId] = column.Width;
                    hasValidWidth = true;
                }
            }
        }

        if (!hasValidWidth)
        {
            return;
        }

        var path = tab.Navigation.CurrentPath;
        var resolvedPath = _columnLayout.GetAbsoluteFolderPath(path, _activeWorkspaceSession);
        PerfLog.Write($"column-save path=\"{resolvedPath}\" gridHash={gridView.GetHashCode()}");
        _columnLayout.SaveColumnWidthsForPath(path, _activeWorkspaceSession, pane.Id, widths, OnWorkspaceColumnWidthsDirty);
    }

    private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column?.Header is not TextBlock textBlock || textBlock.Tag is not string columnId)
        {
            return;
        }

        var targetState = ActiveTabState;
        var activeTab = ActiveTab;
        if (targetState is null || activeTab is null)
        {
            return;
        }

        // Capture current display order before updating sort properties
        var currentOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var item in ItemsView)
        {
            if (item is FileEntry entry)
            {
                currentOrder[entry.FullPath] = index++;
            }
        }

        if (targetState.SortColumn == columnId)
        {
            targetState.SortAscending = !targetState.SortAscending;
        }
        else
        {
            targetState.SortColumn = columnId;
            targetState.SortAscending = true;
        }

        ApplyTabSort(targetState, currentOrder);
        _workspaceLocalState.MarkDirty("sort");
    }

    private void ApplyTabSort(FolderTab tab)
    {
        ApplyTabSort(tab.State, null);
    }

    private void ApplyTabSort(WorkspaceTabState targetState, Dictionary<string, int>? currentOrder = null)
    {
        if (!_devListPerfOptions.SortEnabled)
        {
            _performanceLogger.Write($"sort-skip reason=dev-flag loadId={_diagnosticLoadId} isLoading={_isLoading} path=\"{targetState.CurrentPath}\" stateId={targetState.Id}");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _sortApplyCount++;
        if (ItemsView is ListCollectionView listView)
        {
            listView.CustomSort = new FileEntryComparer(targetState.SortColumn, targetState.SortAscending, _settingsService.Settings.SortFoldersFirst, currentOrder);
        }
        else
        {
            ItemsView.SortDescriptions.Clear();
            ItemsView.SortDescriptions.Add(new SortDescription(FileListSortHelper.GetSortPropertyName(targetState.SortColumn), targetState.SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));
        }

        UpdateNormalPaneColumnHeaders();

        stopwatch.Stop();
        _performanceLogger.Write($"sort-apply loadId={_diagnosticLoadId} stateId={targetState.Id} paneId={targetState.PaneId} path=\"{targetState.CurrentPath}\" count={_items.Count} elapsedMs={stopwatch.ElapsedMilliseconds} applyCount={_sortApplyCount} isLoading={_isLoading} column={targetState.SortColumn} ascending={targetState.SortAscending}");
    }

    private void ClearItemsSort()
    {
        if (!_devListPerfOptions.SortEnabled)
        {
            _performanceLogger.Write($"sort-clear-skip reason=dev-flag loadId={_diagnosticLoadId} isLoading={_isLoading} path=\"{ActiveNavigation?.CurrentPath ?? ""}\"");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _sortClearCount++;
        if (ItemsView is ListCollectionView listView)
        {
            listView.CustomSort = null;
        }
        else
        {
            ItemsView.SortDescriptions.Clear();
        }

        stopwatch.Stop();
        _performanceLogger.Write($"sort-clear loadId={_diagnosticLoadId} count={_items.Count} elapsedMs={stopwatch.ElapsedMilliseconds} clearCount={_sortClearCount} isLoading={_isLoading}");
    }



    private void WorkspacePaneGridViewColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header)
        {
            return;
        }

        if (header.Column is not GridViewColumn column)
        {
            return;
        }

        if (sender is not FrameworkElement element || element.DataContext is not FolderPane pane)
        {
            return;
        }

        var targetState = pane.ActiveTabState;
        if (targetState == null)
        {
            return;
        }

        // Capture current display order before updating sort properties
        var currentOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var item in pane.FileList.ItemsView)
        {
            if (item is FileEntry entry)
            {
                currentOrder[entry.FullPath] = index++;
            }
        }

        string? columnId = null;
        var headerObj = column.Header;
        if (headerObj is string headerText)
        {
            if (headerText == WorkspacePaneColumnNameText) columnId = "Name";
            else if (headerText == WorkspacePaneColumnSizeText) columnId = "Size";
            else if (headerText == WorkspacePaneColumnModifiedText) columnId = "ModifiedAt";
        }
        else if (headerObj is TextBlock textBlock && textBlock.Tag is string tag)
        {
            columnId = tag;
        }

        if (columnId == null)
        {
            return;
        }

        if (targetState.SortColumn == columnId)
        {
            targetState.SortAscending = !targetState.SortAscending;
        }
        else
        {
            targetState.SortColumn = columnId;
            targetState.SortAscending = true;
        }

        pane.FileList.ApplySort(
            targetState.SortColumn,
            targetState.SortAscending,
            _settingsService.Settings.SortFoldersFirst,
            currentOrder);

        _folderPaneController.UpdateStatus(pane);
        pane.RefreshDisplay();

        if (sender is ListView listView)
        {
            UpdateWorkspacePaneColumnHeaders(listView, pane);
        }

        _workspaceLocalState.MarkDirty("sort");
    }

    private static string NormalizeSortColumn(string? columnId)
    {
        return ColumnLayoutService.NormalizeSortColumn(columnId);
    }

    private void ApplyColumnSettingsToWorkspacePane(FolderPane pane)
    {
        var listView = FindListViewForPane(pane);
        if (listView is not null)
        {
            ApplyColumnSettingsToWorkspacePane(listView, pane);
        }
    }

    private void ApplyColumnSettingsToWorkspacePane(ListView listView, FolderPane pane)
    {
        if (listView.View is GridView gridView)
        {
            ApplyColumnSettingsToWorkspacePane(gridView, pane);
        }
    }

    private void ApplyColumnSettingsToWorkspacePane(GridView gridView, FolderPane pane)
    {
        _isApplyingColumnWidths = true;
        try
        {
            var tab = pane.ActiveTab;
            var path = tab?.Navigation.CurrentPath;
            var resolvedPath = _columnLayout.GetAbsoluteFolderPath(path, _activeWorkspaceSession);
            PerfLog.Write($"column-apply-workspace path=\"{resolvedPath}\" gridHash={gridView.GetHashCode()}");

            var visibleColumns = _columnLayout.GetVisibleColumnIds();
            gridView.Columns.Clear();

            foreach (var columnId in ColumnLayoutService.ColumnOrder)
            {
                if (!visibleColumns.Contains(columnId))
                {
                    continue;
                }

                var column = CreateWorkspacePaneColumn(columnId);
                var width = _columnLayout.GetColumnWidth(columnId, path, _activeWorkspaceSession, pane.Id);
                if (width > 0)
                {
                    column.Width = width;
                }
                else
                {
                    column.Width = _settingsService.Settings.ColumnWidths.TryGetValue(columnId, out var dw) ? dw : 100;
                }

                gridView.Columns.Add(column);
            }

            UpdateWorkspacePaneColumnHeaders(gridView, pane);
        }
        finally
        {
            _isApplyingColumnWidths = false;
        }
    }

    private sealed class ColumnMetadata
    {
        public required string ColumnId { get; init; }
        public required string HeaderResourceKey { get; init; }
        public required string BindingPath { get; init; }
    }

    private static readonly ColumnMetadata[] ColumnsDefinition =
    [
        new() { ColumnId = "Name", HeaderResourceKey = "ColumnName", BindingPath = "Name" },
        new() { ColumnId = "ModifiedAt", HeaderResourceKey = "ColumnModified", BindingPath = "ModifiedAtText" },
        new() { ColumnId = "Kind", HeaderResourceKey = "ColumnType", BindingPath = "Kind" },
        new() { ColumnId = "Size", HeaderResourceKey = "ColumnSize", BindingPath = "SizeText" },
        new() { ColumnId = "Extension", HeaderResourceKey = "ColumnExtension", BindingPath = "Extension" },
        new() { ColumnId = "CreatedAt", HeaderResourceKey = "ColumnCreated", BindingPath = "CreatedAtText" },
        new() { ColumnId = "AccessedAt", HeaderResourceKey = "ColumnAccessed", BindingPath = "AccessedAtText" },
        new() { ColumnId = "Attributes", HeaderResourceKey = "ColumnAttributes", BindingPath = "AttributesText" },
        new() { ColumnId = "FullPath", HeaderResourceKey = "ColumnFullPath", BindingPath = "FullPath" },
        new() { ColumnId = "ParentPath", HeaderResourceKey = "ColumnParentPath", BindingPath = "ParentPath" },
        new() { ColumnId = "BaseName", HeaderResourceKey = "ColumnBaseName", BindingPath = "BaseName" }
    ];

    private static string GetHeaderResourceKey(string columnId, bool isSpecial)
    {
        if (isSpecial)
        {
            return columnId switch
            {
                "Name" => "ColumnDriveName",
                "Kind" => "ColumnDriveType",
                "Size" => "ColumnTotalSize",
                "ModifiedAt" => "ColumnFreeAndUsage",
                _ => System.Linq.Enumerable.First(ColumnsDefinition, d => d.ColumnId == columnId).HeaderResourceKey
            };
        }
        return System.Linq.Enumerable.First(ColumnsDefinition, d => d.ColumnId == columnId).HeaderResourceKey;
    }

    private GridViewColumn CreateWorkspacePaneColumn(string columnId)
    {
        var column = new GridViewColumn();
        var normalizedId = ColumnLayoutService.NormalizeColumnId(columnId);

        var def = System.Linq.Enumerable.FirstOrDefault(ColumnsDefinition, d => d.ColumnId == normalizedId);
        var resourceKey = def?.HeaderResourceKey ?? "ColumnName";
        var bindingPath = def?.BindingPath ?? "Name";

        var baseText = _text.Get(resourceKey);

        column.Header = new TextBlock
        {
            Text = baseText,
            Tag = normalizedId
        };

        if (normalizedId == "Name")
        {
            column.CellTemplate = (DataTemplate)FindResource("ListDisplayTemplate");
        }
        else
        {
            column.DisplayMemberBinding = new Binding(bindingPath);
        }

        return column;
    }
}
