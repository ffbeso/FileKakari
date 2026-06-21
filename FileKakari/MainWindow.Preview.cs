using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileKakari;

public partial class MainWindow
{
    private static readonly TimeSpan PreviewLoadDelay = TimeSpan.FromMilliseconds(200);
    private readonly FilePreviewService _filePreviewService = new();
    private CancellationTokenSource? _previewCancellation;
    private int _previewGeneration;
    private int _previewMediaGeneration = -1;
    private Uri? _previewMediaUri;
    private bool _isPreviewMediaPlaying;
    private GridLength _previewPaneHeight = new(240);
    private GridLength _previewPaneWidth = new(320);

    private bool IsPreviewVisible => PreviewPane.Visibility == Visibility.Visible;

    private void PreviewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePreview();
        FocusActiveFileList();
    }

    private void PreviewToolbarButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        button.ToolTip = _text.Get("PreviewTitle");
        button.SetResourceReference(ForegroundProperty, "TextBrush");
    }

    private void TogglePreview()
    {
        SetPreviewVisible(!IsPreviewVisible);
    }

    private void PreviewCloseButton_Click(object sender, RoutedEventArgs e)
    {
        SetPreviewVisible(false);
        FocusActiveFileList();
    }

    private void PreviewPane_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var hasControl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var isTextPreview = PreviewTextBox.Visibility == Visibility.Visible;
        if (!hasControl && isTextPreview)
        {
            return;
        }

        MoveActivePreviewSelection(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private bool MoveActivePreviewSelection(int delta)
    {
        if (!IsPreviewVisible || delta == 0 || GetActivePreviewListView() is not { } listView || listView.Items.Count == 0)
        {
            return false;
        }

        var currentIndex = listView.SelectedIndex;
        if (currentIndex < 0)
        {
            currentIndex = delta > 0 ? -1 : listView.Items.Count;
        }

        var targetIndex = Math.Clamp(currentIndex + delta, 0, listView.Items.Count - 1);
        if (targetIndex == currentIndex)
        {
            return false;
        }

        var targetItem = listView.Items[targetIndex];
        listView.SelectedItems.Clear();
        listView.SelectedItem = targetItem;
        listView.ScrollIntoView(targetItem);
        listView.Focus();
        return true;
    }

    private bool MoveActivePreviewSelectionPage(int delta)
    {
        if (delta == 0 || GetActivePreviewListView() is not { } listView)
        {
            return false;
        }

        if (listView.SelectedIndex < 0)
        {
            return MoveActivePreviewSelection(delta > 0 ? 1 : -1);
        }

        var currentIndex = listView.SelectedIndex;
        var rowHeight = (listView.ItemContainerGenerator.ContainerFromIndex(currentIndex) as FrameworkElement)?.ActualHeight;
        var effectiveRowHeight = rowHeight is > 0 ? rowHeight.Value : 24d;
        var pageSize = Math.Max(1, (int)Math.Floor(listView.ActualHeight / effectiveRowHeight));
        return MoveActivePreviewSelection(delta * pageSize);
    }

    private ListView? GetActivePreviewListView()
    {
        return GetActiveFolderPane() is { } pane
            ? GetFolderPaneListView(pane)
            : null;
    }

    private void FocusActiveFileList()
    {
        if (GetActivePreviewListView() is { IsVisible: true, IsEnabled: true } listView)
        {
            listView.Focus();
        }
    }

    private bool HandlePreviewNavigationKey(Key key)
    {
        if (!IsPreviewVisible
            || Keyboard.FocusedElement is TextBox
            || Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        return key switch
        {
            Key.Up => MoveActivePreviewSelection(-1),
            Key.Down => MoveActivePreviewSelection(1),
            Key.PageUp => MoveActivePreviewSelectionPage(-1),
            Key.PageDown => MoveActivePreviewSelectionPage(1),
            _ => false
        };
    }

    private void SetPreviewVisible(bool isVisible)
    {
        if (!isVisible)
        {
            RememberPreviewPaneSize();

            CancelPreviewLoad();
            ClearPreviewContent();
            PreviewTitleText.Text = "";
            PreviewPane.Visibility = Visibility.Collapsed;
            PreviewGridSplitter.Visibility = Visibility.Collapsed;
            ApplyPreviewPanePlacement(isVisible: false);
            return;
        }

        PreviewPane.Visibility = Visibility.Visible;
        PreviewGridSplitter.Visibility = Visibility.Visible;
        ApplyPreviewPanePlacement(isVisible: true);
        RefreshPreviewForActiveSelection();
    }

    private void ApplyPreviewPanePlacement(bool? isVisible = null)
    {
        var visible = isVisible ?? IsPreviewVisible;
        var placement = AppSettings.NormalizePreviewPanePlacement(_settingsService.Settings.PreviewPanePlacement);

        Grid.SetRow(ItemsListHost, 0);
        Grid.SetColumn(ItemsListHost, 0);
        Grid.SetRowSpan(ItemsListHost, 1);
        Grid.SetColumnSpan(ItemsListHost, 1);
        Grid.SetRow(PreviewGridSplitter, placement == PreviewPanePlacement.Right ? 0 : 1);
        Grid.SetColumn(PreviewGridSplitter, placement == PreviewPanePlacement.Right ? 1 : 0);
        Grid.SetRow(PreviewPane, placement == PreviewPanePlacement.Right ? 0 : 2);
        Grid.SetColumn(PreviewPane, placement == PreviewPanePlacement.Right ? 2 : 0);

        if (placement == PreviewPanePlacement.Right)
        {
            PreviewPaneRow.Height = new GridLength(0);
            PreviewSplitterRow.Height = new GridLength(0);
            PreviewSplitterColumn.Width = visible ? new GridLength(5) : new GridLength(0);
            PreviewPaneColumn.Width = visible
                ? (_previewPaneWidth.Value > 0 ? _previewPaneWidth : new GridLength(320))
                : new GridLength(0);
            PreviewGridSplitter.ResizeDirection = GridResizeDirection.Columns;
            PreviewGridSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            PreviewGridSplitter.Height = double.NaN;
            PreviewGridSplitter.Width = 5;
            PreviewGridSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewGridSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            PreviewPane.BorderThickness = new Thickness(1, 0, 0, 0);
            return;
        }

        PreviewSplitterColumn.Width = new GridLength(0);
        PreviewPaneColumn.Width = new GridLength(0);
        PreviewSplitterRow.Height = visible ? new GridLength(5) : new GridLength(0);
        PreviewPaneRow.Height = visible
            ? (_previewPaneHeight.Value > 0 ? _previewPaneHeight : new GridLength(240))
            : new GridLength(0);
        PreviewGridSplitter.ResizeDirection = GridResizeDirection.Rows;
        PreviewGridSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        PreviewGridSplitter.Height = 5;
        PreviewGridSplitter.Width = double.NaN;
        PreviewGridSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        PreviewGridSplitter.VerticalAlignment = VerticalAlignment.Stretch;
        PreviewPane.BorderThickness = new Thickness(0, 1, 0, 0);
    }

    private void RememberPreviewPaneSize()
    {
        if (PreviewPaneRow.Height.Value > 0)
        {
            _previewPaneHeight = PreviewPaneRow.Height;
        }

        if (PreviewPaneColumn.Width.Value > 0)
        {
            _previewPaneWidth = PreviewPaneColumn.Width;
        }
    }

    private void RefreshPreviewForActiveSelection()
    {
        if (!IsPreviewVisible || InternalPageHost.Visibility == Visibility.Visible)
        {
            return;
        }

        SchedulePreview(GetSelectedEntries());
    }

    private void SchedulePreview(IReadOnlyList<FileEntry> selectedEntries)
    {
        if (!IsPreviewVisible)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _previewGeneration);
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        PreservePreviewMediaFrame();
        ReleasePreviewMediaForSelectionChange();

        if (selectedEntries.Count == 0)
        {
            PreviewLoadingBar.Visibility = Visibility.Collapsed;
            _ = ShowNoSelectionDelayedAsync(generation);
            return;
        }

        if (selectedEntries.Count != 1)
        {
            PreviewTitleText.Text = "";
            ReplacePreviewWithMessage(_text.Get("PreviewSingleFileOnly"));
            return;
        }

        var entry = selectedEntries[0];
        PreviewTitleText.Text = entry.Name;
        if (entry.IsDirectory)
        {
            ReplacePreviewWithMessage(_text.Get("PreviewFoldersUnsupported"));
            return;
        }

        _previewCancellation = new CancellationTokenSource();
        _ = LoadPreviewAsync(entry.FullPath, generation, _previewCancellation.Token);
    }

    private async Task ShowNoSelectionDelayedAsync(int generation)
    {
        await Task.Delay(PreviewLoadDelay);

        if (generation != _previewGeneration
            || !IsPreviewVisible
            || InternalPageHost.Visibility == Visibility.Visible)
        {
            return;
        }

        var selectedEntries = GetSelectedEntries();
        if (selectedEntries.Count > 0)
        {
            SchedulePreview(selectedEntries);
            return;
        }

        PreviewTitleText.Text = "";
        ReplacePreviewWithMessage(_text.Get("PreviewSelectFile"));
    }

    private async Task LoadPreviewAsync(string path, int generation, CancellationToken cancellationToken)
    {
        try
        {
            PreviewLoadingBar.Visibility = Visibility.Visible;
            await Task.Delay(PreviewLoadDelay, cancellationToken);

            var result = await _filePreviewService.LoadAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _previewGeneration)
            {
                return;
            }

            await ShowPreviewResultAsync(result, generation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (generation == _previewGeneration)
            {
                PreviewLoadingBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task ShowPreviewResultAsync(
        FilePreviewResult result,
        int generation,
        CancellationToken cancellationToken)
    {
        switch (result.Status)
        {
            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Text:
                ReplacePreviewWithText(result.Text ?? "");
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Image && result.ImageBytes is not null:
                try
                {
                    var bitmap = await Task.Run(
                        () => DecodePreviewImage(result.ImageBytes),
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _previewGeneration)
                    {
                        return;
                    }

                    ReplacePreviewWithImage(bitmap);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _previewGeneration)
                    {
                        return;
                    }

                    ReplacePreviewWithMessage(_text.Format("PreviewLoadFailed", ex.Message));
                }
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Video && result.FileInfo is not null:
                ReplacePreviewWithVideo(result.FileInfo.FullPath, generation);
                break;

            case FilePreviewStatus.Unsupported when result.FileInfo is not null:
                ReplacePreviewWithUnsupportedInfo(result.FileInfo);
                break;

            case FilePreviewStatus.Unsupported:
                ReplacePreviewWithMessage(_text.Get("PreviewUnsupported"));
                break;

            case FilePreviewStatus.TooLarge:
                ReplacePreviewWithMessage(_text.Format("PreviewTooLarge", FormatPreviewSize(result.SizeLimit ?? 0)));
                break;

            case FilePreviewStatus.Missing:
                ReplacePreviewWithMessage(_text.Get("PreviewMissing"));
                break;

            default:
                ReplacePreviewWithMessage(_text.Format("PreviewLoadFailed", result.ErrorMessage ?? _text.Get("PreviewUnknownError")));
                break;
        }
    }

    private void ReplacePreviewWithText(string text)
    {
        ClearPreviewContent();
        PreviewTextBox.Text = text;
        PreviewTextBox.Visibility = Visibility.Visible;
    }

    private void ReplacePreviewWithImage(BitmapImage bitmap)
    {
        ClearPreviewContent();
        PreviewImage.Source = bitmap;
        PreviewImageScrollViewer.Visibility = Visibility.Visible;
    }

    private void ReplacePreviewWithVideo(string path, int generation)
    {
        _previewMediaGeneration = generation;
        _previewMediaUri = new Uri(path, UriKind.Absolute);
        PreviewVideoHost.Visibility = Visibility.Visible;
        PreviewVideoHost.Opacity = 0;
        PreviewVideoHost.IsHitTestVisible = false;
        PreviewMediaPlayPauseButton.IsEnabled = false;
        PreviewMediaStopButton.IsEnabled = false;
        UpdatePreviewMediaPlayState(false);
        PreviewMediaElement.Source = _previewMediaUri;
    }

    private void ReplacePreviewWithMessage(string message)
    {
        ClearPreviewContent();
        ShowPreviewMessage(message);
    }

    private void ReplacePreviewWithUnsupportedInfo(FilePreviewInfo fileInfo)
    {
        ClearPreviewContent();
        PreviewInfoNameText.Text = fileInfo.FileName;
        PreviewInfoExtensionText.Text = string.IsNullOrWhiteSpace(fileInfo.Extension)
            ? _text.Get("PreviewInfoNoExtension")
            : fileInfo.Extension;
        PreviewInfoSizeText.Text = FormatPreviewFileSize(fileInfo.Size);
        PreviewInfoModifiedText.Text = fileInfo.LastWriteTime.ToString("g");
        PreviewInfoPathText.Text = fileInfo.FullPath;
        PreviewUnsupportedCard.Visibility = Visibility.Visible;
    }

    private static BitmapImage DecodePreviewImage(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void PreviewMediaElement_MediaOpened(object sender, RoutedEventArgs e)
    {
        var generation = _previewMediaGeneration;
        var mediaUri = _previewMediaUri;
        if (!IsCurrentPreviewVideo(generation, mediaUri))
        {
            return;
        }

        ShowOpenedPreviewMedia(_settingsService.Settings.AutoPlayVideoPreview);
    }

    private void PreviewMediaElement_MediaEnded(object sender, RoutedEventArgs e)
    {
        var generation = _previewMediaGeneration;
        var mediaUri = _previewMediaUri;
        if (!IsCurrentPreviewVideo(generation, mediaUri))
        {
            return;
        }

        PreviewMediaElement.Stop();
        PreviewMediaElement.Position = TimeSpan.Zero;
        UpdatePreviewMediaPlayState(false);
    }

    private void PreviewMediaElement_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var generation = _previewMediaGeneration;
        var mediaUri = _previewMediaUri;
        if (!IsCurrentPreviewVideo(generation, mediaUri))
        {
            return;
        }

        ReplacePreviewWithMessage(_text.Format(
            "PreviewLoadFailed",
            e.ErrorException?.Message ?? _text.Get("PreviewUnknownError")));
    }

    private void PreviewMediaPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewMediaPlaying)
        {
            PreviewMediaElement.Pause();
            UpdatePreviewMediaPlayState(false);
        }
        else
        {
            PreviewMediaElement.Play();
            UpdatePreviewMediaPlayState(true);
        }

        FocusActiveFileList();
    }

    private void PreviewMediaStopButton_Click(object sender, RoutedEventArgs e)
    {
        PreviewMediaElement.Stop();
        PreviewMediaElement.Position = TimeSpan.Zero;
        UpdatePreviewMediaPlayState(false);
        FocusActiveFileList();
    }

    private void ShowOpenedPreviewMedia(bool autoPlay)
    {
        PreviewMediaElement.IsMuted = true;
        if (autoPlay)
        {
            PreviewMediaElement.Play();
        }

        ClearNonVideoPreviewContent();
        PreviewVideoHost.Opacity = 1;
        PreviewVideoHost.IsHitTestVisible = true;
        PreviewMediaPlayPauseButton.IsEnabled = true;
        PreviewMediaStopButton.IsEnabled = true;
        UpdatePreviewMediaPlayState(autoPlay);
    }

    private bool IsCurrentPreviewVideo(int generation, Uri? mediaUri)
    {
        return generation == _previewGeneration
            && generation == _previewMediaGeneration
            && mediaUri is not null
            && Equals(mediaUri, _previewMediaUri)
            && Equals(mediaUri, PreviewMediaElement.Source)
            && PreviewVideoHost.Visibility == Visibility.Visible;
    }

    private void UpdatePreviewMediaPlayState(bool isPlaying)
    {
        _isPreviewMediaPlaying = isPlaying;
        PreviewMediaPlayPauseButton.Content = isPlaying ? "\uE769" : "\uE768";
        PreviewMediaPlayPauseButton.ToolTip = _text.Get(isPlaying ? "PreviewMediaPause" : "PreviewMediaPlay");
    }

    private void PreservePreviewMediaFrame()
    {
        if (PreviewVideoHost.Visibility != Visibility.Visible
            || PreviewVideoHost.Opacity < 1
            || PreviewMediaElement.ActualWidth <= 0
            || PreviewMediaElement.ActualHeight <= 0)
        {
            return;
        }

        var width = Math.Max(1, (int)Math.Ceiling(PreviewMediaElement.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(PreviewMediaElement.ActualHeight));
        var frame = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        frame.Render(PreviewMediaElement);
        frame.Freeze();
        PreviewImage.Source = frame;
        PreviewImageScrollViewer.Visibility = Visibility.Visible;
    }

    private void ReleasePreviewMediaForSelectionChange()
    {
        StopPreviewMedia(clearSource: true);
        PreviewVideoHost.Visibility = Visibility.Collapsed;
        PreviewVideoHost.Opacity = 0;
        PreviewVideoHost.IsHitTestVisible = false;
    }

    private void StopPreviewMedia(bool clearSource)
    {
        if (PreviewMediaElement.Source is not null)
        {
            PreviewMediaElement.Stop();
        }

        if (clearSource)
        {
            PreviewMediaElement.Source = null;
            _previewMediaGeneration = -1;
            _previewMediaUri = null;
        }

        PreviewMediaPlayPauseButton.IsEnabled = false;
        PreviewMediaStopButton.IsEnabled = false;
        UpdatePreviewMediaPlayState(false);
    }

    private void ClearNonVideoPreviewContent()
    {
        PreviewTextBox.Text = "";
        PreviewTextBox.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImageScrollViewer.Visibility = Visibility.Collapsed;
        PreviewUnsupportedCard.Visibility = Visibility.Collapsed;
        PreviewMessageText.Visibility = Visibility.Collapsed;
    }

    private void ClearPreviewContent()
    {
        PreviewTextBox.Text = "";
        PreviewTextBox.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImageScrollViewer.Visibility = Visibility.Collapsed;
        StopPreviewMedia(clearSource: true);
        PreviewVideoHost.Visibility = Visibility.Collapsed;
        PreviewVideoHost.Opacity = 0;
        PreviewVideoHost.IsHitTestVisible = false;
        PreviewUnsupportedCard.Visibility = Visibility.Collapsed;
        PreviewMessageText.Visibility = Visibility.Collapsed;
        PreviewLoadingBar.Visibility = Visibility.Collapsed;
    }

    private void ShowPreviewMessage(string message)
    {
        PreviewMessageText.Text = message;
        PreviewMessageText.Visibility = Visibility.Visible;
    }

    private void CancelPreviewLoad()
    {
        Interlocked.Increment(ref _previewGeneration);
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        StopPreviewMedia(clearSource: true);
    }

    private static string FormatPreviewSize(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / (1024 * 1024):N0} MB"
            : $"{bytes / 1024:N0} KB";
    }

    private static string FormatPreviewFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
    }
}
