using System.Collections.ObjectModel;
using System.Windows;

namespace FileKakari;

sealed class WorkspacePaneUiController
{
    private readonly ObservableCollection<WorkspacePaneGroup> _paneGroups;
    private readonly FolderPaneController _folderPaneController;
    private readonly UIElement _paneRail;
    private readonly UIElement _splitGrid;
    private readonly UIElement _itemsListHost;

    internal WorkspacePaneUiController(
        ObservableCollection<WorkspacePaneGroup> paneGroups,
        FolderPaneController folderPaneController,
        UIElement paneRail,
        UIElement splitGrid,
        UIElement itemsListHost)
    {
        _paneGroups = paneGroups;
        _folderPaneController = folderPaneController;
        _paneRail = paneRail;
        _splitGrid = splitGrid;
        _itemsListHost = itemsListHost;
    }

    internal void ShowWorkspace(WorkspacePaneGroup? activePaneGroup)
    {
        // Keep the future pane-management rail wired, but hide it during the current two-pane validation phase.
        _paneRail.Visibility = Visibility.Collapsed;
        _splitGrid.Visibility = Visibility.Visible;
        _itemsListHost.Visibility = Visibility.Collapsed;
        SetActivePaneGroup(activePaneGroup);
    }

    internal void ShowNormal(bool clearPaneGroups)
    {
        _paneRail.Visibility = Visibility.Collapsed;
        _splitGrid.Visibility = Visibility.Collapsed;
        _itemsListHost.Visibility = Visibility.Visible;
        if (clearPaneGroups)
        {
            _paneGroups.Clear();
        }

        _folderPaneController.ClearDisplayPanes();
        SetActivePaneGroup(null);
    }

    internal void SetActivePaneGroup(WorkspacePaneGroup? activePaneGroup)
    {
        foreach (var paneGroup in _paneGroups)
        {
            paneGroup.IsActive = ReferenceEquals(paneGroup, activePaneGroup);
        }
    }
}
