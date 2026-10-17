using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Data;

namespace FileKakari;

public partial class MainWindow
{
    private readonly ConditionalWeakTable<ListView, ListViewAsyncSortController> _columnSorts = new();
    private bool _normalListHasMaterializedSort;

    private async Task<bool> TrySortColumnAsync(FolderPane pane, WorkspaceTabState state, ListView list)
    {
        var sorter = _columnSorts.GetValue(list, _ => new());
        sorter.Cancel();
        var normal = ReferenceEquals(list, ItemsList);
        var items = normal ? _items : pane.FileList.Items;
        var view = (normal ? ItemsView : pane.FileList.ItemsView) as ListCollectionView;
        // Small lists and grouped views keep their existing synchronous path.
        if (items.Count < 2048 || state.GroupMode != GroupMode.None || view is null
            || view.GroupDescriptions.Count != 0 || pane.IsLoading || (normal && _isLoading)
            || pane.FileList.ViewStateSyncSuppression.IsActive || !_devListPerfOptions.SortEnabled)
            return false;

        var column = state.SortColumn;
        var ascending = state.SortAscending;
        var foldersFirst = _settingsService.Settings.SortFoldersFirst;
        var path = state.CurrentPath;
        var generation = pane.FileList.LoadGeneration;
        bool IsCurrent() => ReferenceEquals(pane.ActiveTabState, state)
            && (!normal || (ReferenceEquals(GetNormalFolderPane(), pane) && ReferenceEquals(ActiveTabState, state)))
            && ReferenceEquals(GetFolderPaneListView(pane), list) && list.IsVisible
            && string.Equals(state.CurrentPath, path, StringComparison.OrdinalIgnoreCase)
            && pane.FileList.LoadGeneration == generation && !pane.IsLoading && !(normal && _isLoading)
            && !pane.FileList.ViewStateSyncSuppression.IsActive
            && state.SortColumn == column && state.SortAscending == ascending
            && state.GroupMode == GroupMode.None && view.GroupDescriptions.Count == 0
            && _settingsService.Settings.SortFoldersFirst == foldersFirst;

        _workspaceLocalState.MarkDirty("sort");
        try
        {
            var applied = await sorter.SortAsync(list, items, view, column, ascending, foldersFirst,
                IsCurrent, update =>
                {
                    using var preview = SuppressPreviewForProgrammaticSelection("column-sort");
                    using var capture = pane.FileList.ViewStateSyncSuppression.Enter();
                    update();
                }, pane.FileList);
            if (applied)
            {
                if (normal)
                {
                    _normalListHasMaterializedSort = true;
                    SyncNormalPaneSelectionFromView();
                    UpdateNormalPaneColumnHeaders();
                }
                else
                {
                    pane.FileList.RecordMaterializedSort(column, ascending, foldersFirst);
                    SyncPaneSelectionFromListView(pane, list);
                    _folderPaneController.UpdateStatus(pane);
                    UpdateWorkspacePaneColumnHeaders(list, pane);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            LogException("column-sort", ex, state);
            // Preserve the existing synchronous fallback only while the same request still owns the view.
            return !IsCurrent();
        }
    }
}
