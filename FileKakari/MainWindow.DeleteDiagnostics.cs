using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace FileKakari;

public partial class MainWindow
{
    private long _deleteFlowGeneration;
    private long _lastDeleteFlowId;
    private DateTimeOffset _lastDeleteFlowStartedAt;
    private string _lastDeletePaneId = "";
    private IReadOnlyList<string> _lastDeleteTargetPaths = [];

    private long BeginDeleteFlowDiagnostics(FolderPane pane, IReadOnlyList<string> targetPaths)
    {
        var flowId = Interlocked.Increment(ref _deleteFlowGeneration);
        _lastDeleteFlowId = flowId;
        _lastDeleteFlowStartedAt = DateTimeOffset.UtcNow;
        _lastDeletePaneId = pane.Id;
        _lastDeleteTargetPaths = targetPaths.ToList();
        WriteDeleteFlowSnapshot(flowId, "delete-start", pane);
        return flowId;
    }

    private long GetDeleteFlowIdForChangedPath(string changedPath)
    {
        return _lastDeleteTargetPaths.Any(path =>
            string.Equals(path, changedPath, StringComparison.OrdinalIgnoreCase))
            ? _lastDeleteFlowId
            : 0;
    }

    private long GetDeleteFlowIdForPane(FolderPane pane)
    {
        return string.Equals(_lastDeletePaneId, pane.Id, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - _lastDeleteFlowStartedAt < TimeSpan.FromSeconds(10)
            ? _lastDeleteFlowId
            : 0;
    }

    private void WriteDeleteFlowSnapshot(long flowId, string stage, FolderPane pane, string? changedPath = null)
    {
        if (!_performanceLogger.IsEnabled)
        {
            return;
        }

        var isWorkspacePane = IsWorkspaceDisplayPane(pane);
        var view = isWorkspacePane ? pane.FileList.ItemsView : ItemsView;
        var sourceCount = isWorkspacePane ? pane.FileList.Items.Count : _items.Count;
        var listView = GetFolderPaneListView(pane);
        var scrollViewer = listView is null ? null : FindVisualChild<ScrollViewer>(listView);
        var currentPath = (view.CurrentItem as FileEntry)?.FullPath ?? "";
        var selectedPaths = listView?.SelectedItems.OfType<FileEntry>().Select(item => item.FullPath).ToList() ?? [];
        var realizedContainers = listView?.Items.OfType<FileEntry>()
            .Count(item => listView.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem) ?? 0;
        var collectionView = view as ListCollectionView;
        var anchorState = pane.FileList.PendingAnchorScrollState ?? pane.ActiveTabState?.AnchorState;

        _performanceLogger.Write(
            $"delete-flow flowId={flowId} stage={stage} paneId={pane.Id} stateId={pane.ActiveTabState?.Id ?? ""} " +
            $"path=\"{EscapeDeleteDiagnosticValue(pane.CurrentPath)}\" changedPath=\"{EscapeDeleteDiagnosticValue(changedPath ?? "")}\" " +
            $"groupMode={pane.ActiveTabState?.GroupMode.ToString() ?? "none"} sourceCount={sourceCount} viewCount={view.Cast<object>().Count()} " +
            $"groupDescriptions={collectionView?.GroupDescriptions.Count ?? -1} groups={collectionView?.Groups?.Count ?? 0} " +
            $"sortDescriptions={view.SortDescriptions.Count} hasCustomSort={collectionView?.CustomSort is not null} " +
            $"currentPosition={view.CurrentPosition} currentPath=\"{EscapeDeleteDiagnosticValue(currentPath)}\" " +
            $"selected={selectedPaths.Count} selectedFirst=\"{EscapeDeleteDiagnosticValue(selectedPaths.FirstOrDefault() ?? "")}\" " +
            $"savedSelected={pane.ActiveTabState?.SelectedPaths.Count ?? 0} savedOffset={pane.ActiveTabState?.VerticalOffset ?? -1:N1} " +
            $"anchor=\"{EscapeDeleteDiagnosticValue(anchorState?.AnchorPath ?? "")}\" pendingAnchor={pane.FileList.PendingAnchorScrollState is not null} " +
            $"offset={scrollViewer?.VerticalOffset ?? -1:N1} scrollableHeight={scrollViewer?.ScrollableHeight ?? -1:N1} viewportHeight={scrollViewer?.ViewportHeight ?? -1:N1} " +
            $"generatorStatus={listView?.ItemContainerGenerator.Status.ToString() ?? "none"} realized={realizedContainers} " +
            $"virtualization={GetDeleteDiagnosticVirtualizationMode(listView)} virtualizingWhenGrouping={GetDeleteDiagnosticVirtualizingWhenGrouping(listView)}");
    }

    private static string GetDeleteDiagnosticVirtualizationMode(ListView? listView) =>
        listView is null ? "none" : VirtualizingPanel.GetVirtualizationMode(listView).ToString();

    private static bool GetDeleteDiagnosticVirtualizingWhenGrouping(ListView? listView) =>
        listView is not null && VirtualizingPanel.GetIsVirtualizingWhenGrouping(listView);

    private static string EscapeDeleteDiagnosticValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
