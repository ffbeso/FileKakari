using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow
{
    private readonly List<ImagePreviewTile> _imageTiles = new();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (PreviewContentViewport is not null) QueueImagePreviewResize();
    }

    private void QueueImagePreviewResize()
    {
        foreach (var tile in _imageTiles) tile.RefreshSize();
    }

    private void ClearImageTiles()
    {
        foreach (var tile in _imageTiles) tile.Dispose();
        _imageTiles.Clear();
        PreviewImagesGrid.Children.Clear();
    }

    private async Task ShowImageTilesAsync(IReadOnlyList<string> paths, int total, int generation,
        CancellationToken token, FilePreviewResult? existing = null)
    {
        try
        {
            if (existing is null) await Task.Delay(30, token);
            token.ThrowIfCancellationRequested();
            if (generation != _previewGeneration || !IsPreviewPaneActuallyVisible) return;
            ClearPreviewContent();
            PreviewImagesGrid.RowDefinitions.Clear();
            PreviewImagesGrid.ColumnDefinitions.Clear();
            var columns = paths.Count == 1 ? 1 : 2;
            var rows = paths.Count <= 2 ? 1 : 2;
            for (var i = 0; i < columns; i++) PreviewImagesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < rows; i++) PreviewImagesGrid.RowDefinitions.Add(new RowDefinition());
            var tiles = new List<ImagePreviewTile>();
            for (var i = 0; i < paths.Count; i++)
            {
                var tile = new ImagePreviewTile(paths[i], paths.Count > 1, token);
                tile.Activated += selected =>
                {
                    foreach (var current in _imageTiles) current.SetActive(ReferenceEquals(current, selected));
                };
                tile.SetActive(i == 0);
                Grid.SetColumn(tile, i % columns);
                Grid.SetRow(tile, i / columns);
                PreviewImagesGrid.Children.Add(tile);
                _imageTiles.Add(tile);
                tiles.Add(tile);
            }
            PreviewImageScrollViewer.Visibility = Visibility.Visible;
            if (total > 1) PreviewTitleText.Text = total > 4
                ? _text.Format("ImagePreviewMore", total - 4) : _text.Format("ImagePreviewCount", total);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            token.ThrowIfCancellationRequested();
            if (generation != _previewGeneration) return;
            await Task.WhenAll(tiles.Select(tile => tile.LoadAsync(existing)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewDiagnostics.Error("PreviewImage", $"Image tiles failed: {ex.Message}"); }
    }
}
