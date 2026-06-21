using System.Windows;
using System.Windows.Controls;

namespace FileKakari;

public partial class MainWindow
{
    private void SyncNormalPaneSelectionFromView()
    {
        if (GetNormalFolderPane() is not { } pane)
        {
            return;
        }

        var selectedPaths = ItemsList.SelectedItems
            .OfType<FileEntry>()
            .Select(entry => entry.FullPath)
            .ToList();
        pane.SelectedPaths = selectedPaths;
        SyncNormalPaneStatusFromView(pane);
        if (pane.ActiveTabState is { } state)
        {
            state.SelectedPaths = selectedPaths;
        }
    }

    private void SyncNormalPaneStatusFromView()
    {
        if (GetNormalFolderPane() is { } pane)
        {
            SyncNormalPaneStatusFromView(pane);
        }
    }

    private void SetNormalStatusText(string text)
    {
        // Drawing compatibility: normal mode still renders through StatusText while
        // FolderPane remains the model-side source for subsequent status reads.
        StatusText.Text = text;
        SyncNormalPaneStatusFromView();
    }

    private void SetNormalStatusText(FolderPane pane, string text)
    {
        StatusText.Text = text;
        SyncNormalPaneStatusFromView(pane);
    }

    private void SyncNormalPaneStatusFromView(FolderPane pane)
    {
        // Drawing compatibility: StatusText is still the rendered normal-mode status bar.
        // Keep the normal FolderPane state current so later status rendering can read it.
        // Next integration point: Workspace panes still write pane.FileList.StatusText directly;
        // share message/summary calculation once normal rendering also reads FolderPane status.
        pane.FileList.StatusText = StatusText.Text;
    }

    private void WorkspacePaneFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible
            || InternalPageHost.Visibility == Visibility.Visible
            || sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            return;
        }

        SyncPaneSelectionFromListView(pane, listView);
        if (ReferenceEquals(pane, _activeWorkspaceSession.ActivePaneGroup))
        {
            SchedulePreview(listView.SelectedItems.OfType<FileEntry>().ToList());
        }
    }

    private void ClearSelectionForPane(FolderPane pane, ListView listView, bool updateStatus = true)
    {
        // Keep blank-click clearing scoped to the pane that received the input.
        listView.SelectedItems.Clear();
        if (updateStatus)
        {
            SyncPaneSelectionFromListView(pane, listView);
        }
    }

    private void SyncPaneSelectionFromListView(FolderPane pane, ListView listView)
    {
        if (!IsWorkspaceDisplayPane(pane))
        {
            SyncNormalPaneSelectionFromView();
            return;
        }

        UpdateWorkspacePaneSelectionFromListView(pane, listView);
    }

    private void UpdateWorkspacePaneSelectionFromListView(FolderPane pane, ListView listView)
    {
        if (_suppressWorkspaceSelectionSync)
        {
            return;
        }

        var selectedPaths = listView.SelectedItems
            .OfType<FileEntry>()
            .Select(entry => entry.FullPath)
            .ToList();
        pane.SelectedPaths = selectedPaths;
        if (pane.ActiveTabState is { } state)
        {
            state.SelectedPaths = selectedPaths;
        }

        UpdateWorkspacePaneStatusAsync(pane, listView.SelectedItems.OfType<FileEntry>().ToList());
    }

    private void UpdateWorkspacePaneStatus(FolderPane pane)
    {
        _folderPaneController.UpdateStatus(pane);
    }

    private void UpdateWorkspacePaneStatusAsync(FolderPane pane, IReadOnlyList<FileEntry> selectedEntries)
    {
        _folderPaneController.UpdateStatusAsync(pane, selectedEntries);
    }

    private void SetFileOperationStatus(FileOperationContext context, string text)
    {
        SetNormalStatusText(text);
        if (context.IsWorkspace)
        {
            context.Pane.FileList.StatusText = text;
        }
    }

    private void ShowDisconnectedStatus()
    {
        SetNormalStatusText(_text.Get("DriveDisconnectedStatus"));
    }

    private string GetStatusDiagnosticsStatus()
    {
        return _statusSummaryCoordinator.GetDiagnosticsStatus();
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _fileListInput.HandleSelectionChanged();
        if (!_listViewRestore.IsRestoring && !_suppressSelectionStatusUpdates)
        {
            SyncNormalPaneSelectionFromView();
        }
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible
            && InternalPageHost.Visibility != Visibility.Visible)
        {
            SchedulePreview(ItemsList.SelectedItems.OfType<FileEntry>().ToList());
        }
    }

    private void UpdateSelectedItemStatus()
    {
        var context = GetActiveNavigationContext();
        _statusSummaryCoordinator.UpdateSelectedItemStatus(
            context.Tab,
            _isLoading,
            GetSelectedEntries,
            _devListPerfOptions.StatusAggregationEnabled,
            _diagnosticLoadId,
            RefreshCurrentFolderSummary);
    }

    private void RefreshCurrentFolderSummary()
    {
        var context = GetActiveNavigationContext();
        var isSpecial = context.Tab is not null && SpecialLocationService.IsSpecialUri(context.Tab.Navigation.CurrentPath);
        _statusSummaryCoordinator.RefreshCurrentFolderSummary(
            _items,
            ItemsView,
            FilterBox?.Text,
            _devListPerfOptions.StatusAggregationEnabled,
            isSpecial,
            _diagnosticLoadId,
            _isLoading);
    }
}
