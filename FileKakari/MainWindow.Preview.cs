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
    private readonly FilePreviewController _filePreviewController = new(new IFilePreviewProvider[]
    {
        new BuiltInTextPreviewProvider(),
        new BuiltInImagePreviewProvider(),
        new WebViewPreviewProvider(),
        new BuiltInVideoPreviewProvider(),
        new ShellPreviewHandlerProvider()
    });
    private static readonly System.Collections.Generic.HashSet<string> OfficeExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm",
        ".xls", ".xlsx", ".xlsm", ".xlt", ".xltx", ".xltm",
        ".ppt", ".pptx", ".pptm", ".pot", ".potx", ".potm", ".pps", ".ppsx", ".ppsm"
    };
    private CancellationTokenSource? _previewCancellation;
    private int _previewGeneration;
    private int _previewMediaGeneration = -1;
    private Uri? _previewMediaUri;
    private bool _isPreviewMediaPlaying;
    private bool _isPreviewMediaVideo;
    private string? _currentTempMediaHtmlPath;
    private int _currentTempMediaHtmlGeneration = -1;
    private int _currentWebViewMediaGeneration = -1;
    private string _currentWebViewMediaType = "";
    private bool _currentWebViewMediaAutoPlayVideoSetting;
    private bool _currentWebViewMediaMuteVideoSetting;
    private bool _currentWebViewMediaAutoPlayAudioSetting;
    private bool _currentWebViewMediaEffectiveAutoPlay;
    private bool _currentWebViewMediaEffectiveMuted;
    private GridLength _previewPaneHeight = new(240);
    private GridLength _previewPaneWidth = new(320);
    private bool _isWebViewInitialized;
    private string? _currentWebViewUri;
    private int _webViewNavigationGeneration;
    private FilePreviewInfo? _currentWebViewFileInfo;
    private bool _hasRetriedCurrentMhtml;
    private bool _isPreviewTemporarilyHiddenForSettings;
    private bool _previewWasVisibleBeforeSettings;
    private bool _isPreviewMaximized;
    private GridLength _previousPreviewRowHeight;
    private GridLength _previousPreviewColumnWidth;
    private const double DefaultPreviewPaneWidth = 320;
    private const double DefaultPreviewPaneHeight = 240;
    private const double MinPreviewPaneSize = 120;
    private const double MinFileListPaneSize = 180;
    private const double PreviewSplitterSize = 5;
    private static readonly Guid WindowsTxtPreviewerClsid = new("1531D583-8375-4D3F-B5FB-D23BBD169F22");

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
        _settingsService.Settings.IsPreviewPaneVisible = isVisible;
        if (!isVisible)
        {
            if (_isPreviewMaximized)
            {
                SetPreviewMaximized(false);
            }
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

    private void InitializePreviewPaneVisibilityFromSettings()
    {
        if (_settingsService.Settings.IsPreviewPaneVisible != true)
        {
            ApplyPreviewPanePlacement(isVisible: false);
            return;
        }

        PreviewPane.Visibility = Visibility.Visible;
        PreviewGridSplitter.Visibility = Visibility.Visible;
        ApplyPreviewPanePlacement(isVisible: true);
    }

    private void HidePreviewPaneForSettingsPage()
    {
        if (_isPreviewTemporarilyHiddenForSettings)
        {
            return;
        }

        _previewWasVisibleBeforeSettings = IsPreviewVisible;
        _isPreviewTemporarilyHiddenForSettings = true;
        if (!_previewWasVisibleBeforeSettings)
        {
            PerfLog.Write("[MainWindow.Preview] Settings page opened with preview pane already hidden");
            return;
        }

        RememberPreviewPaneSize();
        PreviewPane.Visibility = Visibility.Collapsed;
        PreviewGridSplitter.Visibility = Visibility.Collapsed;
        if (!_isPreviewMaximized)
        {
            ApplyPreviewPanePlacement(isVisible: false);
        }

        PreviewShellHostContainer.UpdateLayout();
        PreviewWebView.UpdateLayout();
        PerfLog.Write("[MainWindow.Preview] Preview pane temporarily hidden for settings page");
    }

    private void RestorePreviewPaneAfterSettingsPage()
    {
        if (!_isPreviewTemporarilyHiddenForSettings)
        {
            return;
        }

        var shouldRestore = _previewWasVisibleBeforeSettings;
        _previewWasVisibleBeforeSettings = false;
        _isPreviewTemporarilyHiddenForSettings = false;
        if (!shouldRestore)
        {
            PerfLog.Write("[MainWindow.Preview] Settings page closed with no preview pane restore needed");
            return;
        }

        PreviewPane.Visibility = Visibility.Visible;
        PreviewGridSplitter.Visibility = _isPreviewMaximized
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (!_isPreviewMaximized)
        {
            ApplyPreviewPanePlacement(isVisible: true);
        }

        PreviewShellHostContainer.UpdateLayout();
        PreviewWebView.UpdateLayout();
        PerfLog.Write("[MainWindow.Preview] Preview pane restored after settings page");
    }

    private void ApplyPreviewPanePlacement(bool? isVisible = null)
    {
        var visible = isVisible ?? IsPreviewVisible;
        if (_isPreviewMaximized && visible)
        {
            return;
        }
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
                ? GetPreviewPaneWidthLength()
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
            ? GetPreviewPaneHeightLength()
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
        if (_isPreviewMaximized || !IsPreviewVisible)
        {
            return;
        }

        var placement = AppSettings.NormalizePreviewPanePlacement(_settingsService.Settings.PreviewPanePlacement);
        if (placement == PreviewPanePlacement.Right)
        {
            RememberPreviewPaneWidth(PreviewPaneColumn.ActualWidth);
            return;
        }

        RememberPreviewPaneHeight(PreviewPaneRow.ActualHeight);
    }

    private void InitializePreviewPaneSizeFromSettings()
    {
        _previewPaneWidth = new GridLength(GetValidPreviewPaneSize(
            _settingsService.Settings.PreviewPaneWidth,
            DefaultPreviewPaneWidth));
        _previewPaneHeight = new GridLength(GetValidPreviewPaneSize(
            _settingsService.Settings.PreviewPaneHeight,
            DefaultPreviewPaneHeight));
        SyncPreviewPaneSizeToSettings(_settingsService.Settings);
    }

    private GridLength GetPreviewPaneWidthLength()
    {
        var width = ClampPreviewPaneSize(
            GetValidPreviewPaneSize(_previewPaneWidth.Value, DefaultPreviewPaneWidth),
            PreviewPanePlacement.Right);
        RememberPreviewPaneWidth(width);
        return new GridLength(width);
    }

    private GridLength GetPreviewPaneHeightLength()
    {
        var height = ClampPreviewPaneSize(
            GetValidPreviewPaneSize(_previewPaneHeight.Value, DefaultPreviewPaneHeight),
            PreviewPanePlacement.Bottom);
        RememberPreviewPaneHeight(height);
        return new GridLength(height);
    }

    private void RememberPreviewPaneWidth(double width)
    {
        var value = GetValidPreviewPaneSize(width, DefaultPreviewPaneWidth);
        value = ClampPreviewPaneSize(value, PreviewPanePlacement.Right);
        if (value <= 0)
        {
            return;
        }

        _previewPaneWidth = new GridLength(value);
        _settingsService.Settings.PreviewPaneWidth = value;
    }

    private void RememberPreviewPaneHeight(double height)
    {
        var value = GetValidPreviewPaneSize(height, DefaultPreviewPaneHeight);
        value = ClampPreviewPaneSize(value, PreviewPanePlacement.Bottom);
        if (value <= 0)
        {
            return;
        }

        _previewPaneHeight = new GridLength(value);
        _settingsService.Settings.PreviewPaneHeight = value;
    }

    private void SyncPreviewPaneSizeToSettings(AppSettings settings)
    {
        settings.PreviewPaneWidth = GetValidPreviewPaneSize(_previewPaneWidth.Value, DefaultPreviewPaneWidth);
        settings.PreviewPaneHeight = GetValidPreviewPaneSize(_previewPaneHeight.Value, DefaultPreviewPaneHeight);
    }

    private double ClampPreviewPaneSize(double value, PreviewPanePlacement placement)
    {
        var available = GetPreviewLayoutAvailableSize(placement);
        if (available <= 0)
        {
            return Math.Max(MinPreviewPaneSize, value);
        }

        var max = available - PreviewSplitterSize - MinFileListPaneSize;
        if (max <= 0)
        {
            return 0;
        }

        if (max < MinPreviewPaneSize)
        {
            return max;
        }

        return Math.Clamp(value, MinPreviewPaneSize, max);
    }

    private double GetPreviewLayoutAvailableSize(PreviewPanePlacement placement)
    {
        var parent = ItemsListHost.Parent as FrameworkElement;
        var available = placement == PreviewPanePlacement.Right
            ? parent?.ActualWidth ?? ActualWidth
            : parent?.ActualHeight ?? ActualHeight;
        return double.IsNaN(available) || double.IsInfinity(available) ? 0 : available;
    }

    private static double GetValidPreviewPaneSize(double? value, double fallback)
    {
        if (value is { } actual
            && !double.IsNaN(actual)
            && !double.IsInfinity(actual)
            && actual > 0)
        {
            return Math.Max(MinPreviewPaneSize, actual);
        }

        return fallback;
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

            var result = await _filePreviewController.LoadAsync(path, cancellationToken);
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
                ApplyTextPreview(result, generation, cancellationToken, "BuiltInTextPreviewProvider");
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

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Shell && result.Clsid is not null:
                PreviewDiagnostics.Info("Preview", $"Provider result kind=\"Shell\" path=\"{result.FileInfo?.FullPath ?? ""}\" clsid=\"{result.Clsid.Value:B}\" generation={generation}");
                await ReplacePreviewWithShellAsync(result.FileInfo?.FullPath ?? "", result.Clsid.Value, result.FileInfo, generation, cancellationToken);
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.WebView && result.FileInfo is not null:
                PreviewDiagnostics.Info("Preview", $"Provider result kind=\"WebView\" path=\"{result.FileInfo.FullPath}\" generation={generation}");
                LogPreviewUiState("Before WebView preview", generation, result.FileInfo.FullPath);
                ReplacePreviewWithWebView(result.FileInfo.FullPath, result.FileInfo, generation);
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Video && result.FileInfo is not null:
                PreviewDiagnostics.Info("Preview", $"Provider result kind=\"Video\" path=\"{result.FileInfo.FullPath}\" generation={generation}");
                LogPreviewUiState("Before Video preview", generation, result.FileInfo.FullPath);
                ReplacePreviewWithVideo(result.FileInfo.FullPath, generation);
                break;

            case FilePreviewStatus.Unsupported when result.FileInfo is not null:
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    ReplacePreviewWithUnsupportedInfo(result.FileInfo, result.ErrorMessage, "");
                }
                else
                {
                    ReplacePreviewWithUnsupportedInfo(result.FileInfo);
                }
                break;

            case FilePreviewStatus.Unsupported:
                ReplacePreviewWithMessage(_text.Get("PreviewUnsupported"));
                break;

            case FilePreviewStatus.TooLarge when result.FileInfo is not null:
                ReplacePreviewWithUnsupportedInfo(
                    result.FileInfo,
                    _text.Format("PreviewTooLarge", FormatPreviewSize(result.SizeLimit ?? 0)),
                    "");
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

    private void LogPreviewUiState(string label, int generation, string path)
    {
        PreviewDiagnostics.Verbose(
            "Preview",
            $"{label} path=\"{path}\" generation={generation} currentGeneration={_previewGeneration} webViewVisibility={PreviewWebView.Visibility} shellHostVisibility={PreviewShellHostContainer.Visibility} textVisibility={PreviewTextBox.Visibility} imageVisibility={PreviewImageScrollViewer.Visibility} videoVisibility={PreviewVideoHost.Visibility} unsupportedVisibility={PreviewUnsupportedCard.Visibility} messageVisibility={PreviewMessageText.Visibility}");
    }

    private void ApplyTextPreview(
        FilePreviewResult result,
        int generation,
        CancellationToken cancellationToken,
        string source)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != _previewGeneration)
        {
            PreviewDiagnostics.Verbose("Preview", $"ApplyTextPreview skipped reason=\"generation-mismatch\" source=\"{source}\" current={_previewGeneration} requested={generation} path=\"{result.FileInfo?.FullPath ?? ""}\"");
            return;
        }

        var text = result.Text ?? "";
        LogTextPreviewPayload(
            "ApplyTextPreview payload",
            result.FileInfo?.FullPath ?? "",
            text,
            generation,
            result.Status,
            result.Kind,
            source,
            result.EncodingName);
        ReplacePreviewWithText(text, generation, cancellationToken, source, result.FileInfo?.FullPath ?? "");
    }

    private void ReplacePreviewWithText(
        string text,
        int generation,
        CancellationToken cancellationToken,
        string source,
        string path)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != _previewGeneration)
        {
            PreviewDiagnostics.Verbose("Preview", $"ReplacePreviewWithText skipped before-clear reason=\"generation-mismatch\" source=\"{source}\" current={_previewGeneration} requested={generation} path=\"{path}\"");
            return;
        }

        var beforeLength = PreviewTextBox.Text?.Length ?? 0;
        PreviewDiagnostics.Verbose(
            "Preview",
            $"Text UI set before source=\"{source}\" path=\"{path}\" generation={generation} currentGeneration={_previewGeneration} control=\"PreviewTextBox\" beforeLength={beforeLength} incomingLength={text.Length} visibility={PreviewTextBox.Visibility} isVisible={PreviewTextBox.IsVisible} opacity={PreviewTextBox.Opacity} fontFamily=\"{PreviewTextBox.FontFamily}\" fontSize={PreviewTextBox.FontSize} foreground=\"{PreviewTextBox.Foreground}\" background=\"{PreviewTextBox.Background}\" textWrapping={PreviewTextBox.TextWrapping} cancellationRequested={cancellationToken.IsCancellationRequested}");

        ClearPreviewContent();

        if (generation != _previewGeneration)
        {
            PreviewDiagnostics.Verbose("Preview", $"ReplacePreviewWithText skipped after-clear reason=\"generation-mismatch\" source=\"{source}\" current={_previewGeneration} requested={generation} path=\"{path}\"");
            return;
        }

        PreviewTextBox.Text = text;
        PreviewTextBox.Visibility = Visibility.Visible;

        PreviewDiagnostics.Verbose(
            "Preview",
            $"Text UI set after source=\"{source}\" path=\"{path}\" generation={generation} currentGeneration={_previewGeneration} control=\"PreviewTextBox\" afterLength={PreviewTextBox.Text.Length} visibility={PreviewTextBox.Visibility} isVisible={PreviewTextBox.IsVisible} opacity={PreviewTextBox.Opacity} fontFamily=\"{PreviewTextBox.FontFamily}\" fontSize={PreviewTextBox.FontSize} foreground=\"{PreviewTextBox.Foreground}\" background=\"{PreviewTextBox.Background}\" textWrapping={PreviewTextBox.TextWrapping} cancellationRequested={cancellationToken.IsCancellationRequested}");
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
        _isPreviewMediaVideo = IsVideoPreviewPath(path);
        PreviewVideoHost.Visibility = Visibility.Visible;
        PreviewVideoHost.Opacity = 0;
        PreviewVideoHost.IsHitTestVisible = false;
        PreviewMediaPlayPauseButton.IsEnabled = false;
        PreviewMediaStopButton.IsEnabled = false;
        PreviewMediaElement.IsMuted = ShouldMutePreviewMedia();
        UpdatePreviewMediaPlayState(false);
        PreviewMediaElement.Source = _previewMediaUri;
    }

    private void ReplacePreviewWithMessage(string message)
    {
        ClearPreviewContent();
        ShowPreviewMessage(message);
    }

    private void ReplacePreviewWithUnsupportedInfo(FilePreviewInfo fileInfo, string? customTitle = null, string? customHint = null)
    {
        ClearPreviewContent();
        PreviewInfoNameText.Text = fileInfo.FileName;
        PreviewInfoExtensionText.Text = string.IsNullOrWhiteSpace(fileInfo.Extension)
            ? _text.Get("PreviewInfoNoExtension")
            : fileInfo.Extension;
        PreviewInfoSizeText.Text = FormatPreviewFileSize(fileInfo.Size);
        PreviewInfoModifiedText.Text = fileInfo.LastWriteTime.ToString("g");
        PreviewInfoPathText.Text = fileInfo.FullPath;

        PreviewUnsupportedTitleText.Text = customTitle ?? _text.Get("PreviewUnsupportedTitle");
        PreviewUnsupportedHintText.Text = customHint ?? _text.Get("PreviewUnsupportedHint");

        PreviewUnsupportedCard.Visibility = Visibility.Visible;
    }

    private async Task ReplacePreviewWithShellAsync(
        string path,
        Guid clsid,
        FilePreviewInfo? fileInfo,
        int generation,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (generation != _previewGeneration)
            {
                PreviewDiagnostics.Verbose("PreviewShell", $"ReplacePreviewWithShell skipped reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
                return;
            }

            PreviewDiagnostics.Info("PreviewShell", $"Host attach start path=\"{path}\" clsid=\"{clsid:B}\" attempt={attempt}");
            ClearPreviewContent();
            ShellPreviewHost? shellHost = null;
            try
            {
                PreviewDiagnostics.Verbose("PreviewShell", "Instantiating ShellPreviewHost");
                var initializationPreference = attempt == 1
                    ? ShellPreviewInitializationPreference.Default
                    : ShellPreviewInitializationPreference.FileFirst;
                shellHost = new ShellPreviewHost(path, clsid, initializationPreference);

                ApplyShellPreviewHostBackground();
                PreviewShellHostContainer.Child = shellHost;
                PreviewShellHostContainer.Visibility = Visibility.Visible;
                PreviewDiagnostics.Info("PreviewShell", $"Host attached path=\"{path}\" clsid=\"{clsid:B}\" attempt={attempt}");
                return;
            }
            catch (Exception ex)
            {
                PreviewDiagnostics.Error("PreviewShell", $"Host attach failed path=\"{path}\" clsid=\"{clsid:B}\" HRESULT=0x{ex.HResult:X8} reason=\"{ex.Message}\" attempt={attempt}");
                if (shellHost is not null && !ReferenceEquals(PreviewShellHostContainer.Child, shellHost))
                {
                    try
                    {
                        shellHost.Dispose();
                    }
                    catch (Exception disposeEx)
                    {
                        PreviewDiagnostics.Error("PreviewShell", $"Local host dispose failed reason=\"{disposeEx.Message}\"");
                    }
                }
                ClearShellPreviewHost();

                if (ShouldRetryShellPreview(clsid, ex) && attempt < maxAttempts)
                {
                    PreviewDiagnostics.Info("PreviewShell", $"Retry scheduled path=\"{path}\" clsid=\"{clsid:B}\" delayMs=150");
                    await Task.Delay(150, cancellationToken);
                    continue;
                }

                PreviewDiagnostics.Info("PreviewShell", $"Fallback reason=\"shell-host-failed\" path=\"{path}\" clsid=\"{clsid:B}\"");
                await FallbackFromShellToBuiltInTextAsync(path, fileInfo, generation, cancellationToken, ex);
                return;
            }
        }
    }

    private static bool ShouldRetryShellPreview(Guid clsid, Exception exception)
    {
        if (exception.Data.Contains(ShellPreviewHost.AllInitializersENoInterfaceDataKey))
        {
            PreviewDiagnostics.Info("PreviewShell", $"Retry skipped reason=\"all-initializers-e-nointerface\" clsid=\"{clsid:B}\" shellHResult=0x{exception.HResult:X8}");
            return false;
        }

        if (clsid == WindowsTxtPreviewerClsid)
        {
            PreviewDiagnostics.Info("PreviewShell", $"Retry skipped reason=\"windows-txt-previewer-no-initializer\" clsid=\"{clsid:B}\" shellHResult=0x{exception.HResult:X8}");
            return false;
        }

        return true;
    }

    private async Task FallbackFromShellToBuiltInTextAsync(
        string path,
        FilePreviewInfo? fileInfo,
        int generation,
        CancellationToken cancellationToken,
        Exception shellException)
    {
        var ext = Path.GetExtension(path);
        var isOffice = OfficeExtensions.Contains(ext ?? "");

        if (isOffice)
        {
            PreviewDiagnostics.Info("PreviewShell", $"Office fallback path=\"{path}\" reason=\"possible-protected-view-or-mark-of-the-web\"");
        }
        else
        {
            var textProvider = new BuiltInTextPreviewProvider();
            if (!textProvider.CanPreview(path))
            {
                PreviewDiagnostics.Info("PreviewShell", $"Fallback skipped path=\"{path}\" ext=\"{ext}\" reason=\"not-text-preview-type\"");
                if (fileInfo is not null)
                {
                    ReplacePreviewWithUnsupportedInfo(fileInfo);
                }
                return;
            }

            try
            {
                PreviewDiagnostics.Info("PreviewShell", $"Fallback to provider=\"BuiltInTextPreviewProvider\" path=\"{path}\" ext=\"{ext}\"");
                var result = await textProvider
                    .CreatePreviewAsync(new PreviewRequest(path), cancellationToken);

                if (generation != _previewGeneration)
                {
                    PreviewDiagnostics.Verbose("PreviewShell", $"Fallback skipped reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
                    return;
                }

                LogTextPreviewPayload(
                    "Shell fallback text loaded",
                    path,
                    result.Text ?? "",
                    generation,
                    result.Status,
                    result.Kind,
                    "ShellFallbackBuiltInTextPreviewProvider",
                    result.EncodingName);

                if (result.Status == FilePreviewStatus.Success && result.Kind == FilePreviewKind.Text)
                {
                    PreviewDiagnostics.Info("PreviewShell", $"Fallback selected provider=\"BuiltInTextPreviewProvider\" path=\"{path}\"");
                    ApplyTextPreview(result, generation, cancellationToken, "ShellFallbackBuiltInTextPreviewProvider");
                    return;
                }

                PreviewDiagnostics.Info("PreviewShell", $"Fallback provider result status={result.Status} kind={result.Kind} path=\"{path}\"");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                PreviewDiagnostics.Error("PreviewShell", $"Fallback provider failed HRESULT=0x{ex.HResult:X8} reason=\"{ex.Message}\" path=\"{path}\"");
            }
        }

        if (fileInfo is not null)
        {
            if (isOffice)
            {
                PreviewDiagnostics.Info("PreviewShell", "Fallback display=\"office-protected-view-message\"");
                ReplacePreviewWithUnsupportedInfo(
                    fileInfo,
                    _text.Get("PreviewOfficeFailedTitle"),
                    _text.Get("PreviewOfficeFailedHint"));
            }
            else
            {
                PreviewDiagnostics.Info("PreviewShell", $"Fallback display=\"unsupported-metadata-card\" shellHResult=0x{shellException.HResult:X8}");
                ReplacePreviewWithUnsupportedInfo(fileInfo);
            }
        }
        else
        {
            if (isOffice)
            {
                ReplacePreviewWithMessage(
                    _text.Get("PreviewOfficeFailedTitle") + "\n\n" + _text.Get("PreviewOfficeFailedHint"));
            }
            else
            {
                PreviewDiagnostics.Info("PreviewShell", $"Fallback display=\"generic-unsupported-message\" shellHResult=0x{shellException.HResult:X8}");
                ReplacePreviewWithMessage(_text.Get("PreviewUnsupported"));
            }
        }
    }

    private void ClearShellPreviewHost()
    {
        if (PreviewShellHostContainer.Child is ShellPreviewHost host)
        {
            PreviewDiagnostics.Verbose("PreviewShell", "ClearShellPreviewHost disposing active host");
            try
            {
                host.Dispose();
                PreviewDiagnostics.Verbose("PreviewShell", "ClearShellPreviewHost disposed");
            }
            catch (Exception ex)
            {
                PreviewDiagnostics.Error("PreviewShell", $"ClearShellPreviewHost dispose failed reason=\"{ex.Message}\"");
            }
        }

        PreviewShellHostContainer.Child = null;
        ApplyShellPreviewHostBackground();
        PreviewShellHostContainer.Visibility = Visibility.Collapsed;
    }

    private void ApplyShellPreviewHostBackground()
    {
        PreviewShellHostContainer.SetResourceReference(Border.BackgroundProperty, "PanelBackgroundBrush");
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

    private static void LogTextPreviewPayload(
        string label,
        string path,
        string text,
        int generation,
        FilePreviewStatus status,
        FilePreviewKind kind,
        string source,
        string? encodingName)
    {
        var lineCount = CountLines(text);
        PreviewDiagnostics.Verbose(
            "Preview",
            $"{label} source=\"{source}\" path=\"{path}\" length={text.Length} lineCount={lineCount} generation={generation} status={status} kind={kind} encoding=\"{encodingName ?? ""}\"");
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private void PreviewMediaElement_MediaOpened(object sender, RoutedEventArgs e)
    {
        var generation = _previewMediaGeneration;
        var mediaUri = _previewMediaUri;
        if (!IsCurrentPreviewMedia(generation, mediaUri))
        {
            return;
        }

        ShowOpenedPreviewMedia(ShouldAutoPlayPreviewMedia());
    }

    private void PreviewMediaElement_MediaEnded(object sender, RoutedEventArgs e)
    {
        var generation = _previewMediaGeneration;
        var mediaUri = _previewMediaUri;
        if (!IsCurrentPreviewMedia(generation, mediaUri))
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

        PreviewDiagnostics.Error("PreviewMedia", $"Media failed uri=\"{mediaUri}\" reason=\"{e.ErrorException?.Message}\"");

        if (!IsCurrentPreviewMedia(generation, mediaUri))
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
            PreviewMediaElement.IsMuted = false;
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
        PreviewMediaElement.IsMuted = ShouldMutePreviewMedia();
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

    private bool IsCurrentPreviewMedia(int generation, Uri? mediaUri)
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
            _isPreviewMediaVideo = false;
        }

        PreviewMediaElement.IsMuted = false;
        PreviewMediaPlayPauseButton.IsEnabled = false;
        PreviewMediaStopButton.IsEnabled = false;
        UpdatePreviewMediaPlayState(false);
    }

    private bool ShouldMutePreviewMedia()
    {
        return _isPreviewMediaVideo
            && _settingsService.Settings.AutoPlayVideoPreview
            && _settingsService.Settings.MuteVideoPreviewOnAutoPlay;
    }

    private bool ShouldAutoPlayPreviewMedia()
    {
        return _isPreviewMediaVideo
            ? _settingsService.Settings.AutoPlayVideoPreview
            : _settingsService.Settings.AutoPlayAudioPreview == true;
    }

    private static bool IsVideoPreviewPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".webm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAudioPreviewPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase);
    }

    private void ClearNonVideoPreviewContent()
    {
        PreviewTextBox.Text = "";
        PreviewTextBox.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImageScrollViewer.Visibility = Visibility.Collapsed;
        PreviewUnsupportedCard.Visibility = Visibility.Collapsed;
        PreviewMessageText.Visibility = Visibility.Collapsed;
        ClearShellPreviewHost();
        ClearWebView();
    }

    private void ClearPreviewContent(bool keepWebView = false)
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
        ClearShellPreviewHost();
        if (!keepWebView)
        {
            ClearWebView();
        }
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

    private async void ReplacePreviewWithWebView(string path, FilePreviewInfo fileInfo, int generation)
    {
        PreviewDiagnostics.Info("PreviewWebView", $"Navigation preparing path=\"{path}\" generation={generation}");
        LogPreviewUiState("WebView before clear", generation, path);
        ClearPreviewContent(keepWebView: true);
        LogPreviewUiState("WebView after clear", generation, path);

        try
        {
            if (new BuiltInVideoPreviewProvider().CanPreview(path))
            {
                var originalMediaPath = path;
                var isVideo = IsVideoPreviewPath(path);
                var isAudio = IsAudioPreviewPath(path);
                var autoPlayVideoSetting = _settingsService.Settings.AutoPlayVideoPreview;
                var muteVideoSetting = _settingsService.Settings.MuteVideoPreviewOnAutoPlay;
                var autoPlayAudioSetting = _settingsService.Settings.AutoPlayAudioPreview == true;
                var document = MediaPreviewHtmlBuilder.Build(
                    path,
                    isVideo,
                    isAudio,
                    autoPlayVideoSetting,
                    muteVideoSetting,
                    autoPlayAudioSetting);

                try
                {
                    DeleteCurrentTempMediaHtml();
                }
                catch (Exception ex)
                {
                    PreviewDiagnostics.Error("PreviewMedia", $"Temporary media HTML delete failed reason=\"{ex.Message}\"");
                }

                try
                {
                    var tempFileName = $"FileKakari_media_preview_{Environment.ProcessId}_{generation}_{Guid.NewGuid():N}.html";
                    var tempHtmlPath = Path.Combine(Path.GetTempPath(), tempFileName);
                    await File.WriteAllTextAsync(tempHtmlPath, document.Html, System.Text.Encoding.UTF8);
                    if (generation != _previewGeneration)
                    {
                        TryDeleteTempMediaHtml(tempHtmlPath, generation, "generation-mismatch-after-write");
                        return;
                    }

                    _currentTempMediaHtmlPath = tempHtmlPath;
                    _currentTempMediaHtmlGeneration = generation;
                    _currentWebViewMediaGeneration = generation;
                    _currentWebViewMediaType = document.MediaType;
                    _currentWebViewMediaAutoPlayVideoSetting = autoPlayVideoSetting;
                    _currentWebViewMediaMuteVideoSetting = muteVideoSetting;
                    _currentWebViewMediaAutoPlayAudioSetting = autoPlayAudioSetting;
                    _currentWebViewMediaEffectiveAutoPlay = document.EffectiveAutoPlay;
                    _currentWebViewMediaEffectiveMuted = document.EffectiveMuted;
                    path = tempHtmlPath;
                    PreviewDiagnostics.Verbose("PreviewMedia", $"Temporary media HTML created path=\"{tempHtmlPath}\" generation={generation} sourcePath=\"{originalMediaPath}\" mediaType=\"{document.MediaType}\" autoPlayVideoSetting={autoPlayVideoSetting} muteVideoSetting={muteVideoSetting} autoPlayAudioSetting={autoPlayAudioSetting} effectiveAutoPlay={document.EffectiveAutoPlay} effectiveMuted={document.EffectiveMuted}");
                }
                catch (Exception ex)
                {
                    PreviewDiagnostics.Error("PreviewMedia", $"Temporary media HTML create failed reason=\"{ex.Message}\"");
                }
            }

            PreviewDiagnostics.Verbose("PreviewWebView", $"InitializeWebViewAsync begin generation={generation}");
            bool wasInitialized = _isWebViewInitialized;
            await InitializeWebViewAsync();
            PreviewDiagnostics.Verbose("PreviewWebView", $"InitializeWebViewAsync completed generation={generation}");

            if (generation != _previewGeneration)
            {
                PreviewDiagnostics.Verbose("PreviewWebView", $"Navigate skipped reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
                return;
            }

            if (PreviewWebView.CoreWebView2 is null)
            {
                throw new InvalidOperationException("CoreWebView2 is null after initialization.");
            }

            _webViewNavigationGeneration = generation;
            _currentWebViewFileInfo = fileInfo;
            _hasRetriedCurrentMhtml = false;

            bool justInitialized = !wasInitialized;
            if (justInitialized)
            {
                var ext = Path.GetExtension(path);
                if (string.Equals(ext, ".mht", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".mhtml", StringComparison.OrdinalIgnoreCase))
                {
                    PreviewDiagnostics.Verbose("PreviewWebView", $"MHTML first-load candidate path=\"{path}\" generation={generation}");
                    PreviewDiagnostics.Verbose("PreviewWebView", $"Delaying navigation for MHTML after WebView2 initialization generation={generation}");
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    if (generation != _previewGeneration)
                    {
                        PreviewDiagnostics.Verbose("PreviewWebView", $"Navigate skipped after delay reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
                        return;
                    }
                }
            }

            PreviewDiagnostics.Verbose("PreviewWebView", $"Hide WebView before navigation generation={generation}");
            PreviewDiagnostics.Verbose("PreviewWebView", $"WebView visibility changed Hidden reason=\"Hiding before navigation\" generation={generation}");
            PreviewWebView.Visibility = Visibility.Hidden;
            LogPreviewUiState("WebView before navigate", generation, path);

            var absoluteUri = new Uri(path).AbsoluteUri;
            _currentWebViewUri = absoluteUri; // Track target URI before navigating
            PreviewDiagnostics.Info("PreviewWebView", $"Navigation started uri=\"{absoluteUri}\" generation={generation}");
            PreviewWebView.CoreWebView2.Navigate(absoluteUri);
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewWebView", $"Navigation failed reason=\"{ex.Message}\"");

            ClearWebView();
            LogPreviewUiState("WebView after initialization failure clear", generation, path);

            if (generation != _previewGeneration)
            {
                PreviewDiagnostics.Verbose("PreviewWebView", $"Fallback skipped reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
                return;
            }

            var videoProvider = new BuiltInVideoPreviewProvider();
            bool isMedia = videoProvider.CanPreview(path);

            if (!isMedia && ShellPreviewHandlerRegistry.TryGetPreviewHandlerClsid(path, out var clsid))
            {
                PreviewDiagnostics.Info("PreviewWebView", $"Fallback to provider=\"ShellPreviewHandlerProvider\" reason=\"{ex.Message}\" path=\"{path}\"");
                await ReplacePreviewWithShellAsync(path, clsid, fileInfo, generation, CancellationToken.None);
            }
            else
            {
                PreviewDiagnostics.Info("PreviewWebView", $"Fallback to unsupported reason=\"{ex.Message}\" path=\"{path}\" isMedia={isMedia}");
                ReplacePreviewWithUnsupportedInfo(fileInfo);
            }
        }
    }

    private async System.Threading.Tasks.Task InitializeWebViewAsync()
    {
        if (_isWebViewInitialized)
        {
            return;
        }

        PreviewDiagnostics.Verbose("PreviewWebView", "CoreWebView2 initialization started");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userDataFolder = Path.Combine(localAppData, "FileKakari", "WebView2");

        PreviewDiagnostics.Verbose("PreviewWebView", $"WebView2 UserDataFolder=\"{userDataFolder}\"");

        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await PreviewWebView.EnsureCoreWebView2Async(env);

        // Security restrictions on WebView2 settings
        PreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        PreviewWebView.CoreWebView2.Settings.AreHostObjectsAllowed = false;
        PreviewWebView.CoreWebView2.Settings.IsWebMessageEnabled = false;
        PreviewWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // Register security events to lock down navigation, new windows, and downloads
        PreviewWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
        PreviewWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
        PreviewWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
        PreviewWebView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;

        _isWebViewInitialized = true;
        PreviewDiagnostics.Verbose("PreviewWebView", "CoreWebView2 initialization completed");
    }

    private void CoreWebView2_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri;
        if (uri == "about:blank")
        {
            return;
        }

        bool allowed = false;
        try
        {
            if (!string.IsNullOrEmpty(_currentWebViewUri))
            {
                var targetUriObj = new Uri(_currentWebViewUri);
                var currentUriObj = new Uri(uri);
                if (string.Equals(targetUriObj.AbsoluteUri, currentUriObj.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                }
            }
        }
        catch
        {
            allowed = string.Equals(uri, _currentWebViewUri, StringComparison.OrdinalIgnoreCase);
        }

        PreviewDiagnostics.Verbose("PreviewWebView", $"NavigationStarting uri=\"{uri}\" currentWebViewUri=\"{_currentWebViewUri ?? ""}\" allowed={allowed} generation={_previewGeneration}");

        if (allowed)
        {
            return;
        }

        // Block all document redirections, link clicks or page jumps
        e.Cancel = e.Cancel || true;
        PreviewDiagnostics.Info("PreviewWebView", $"Navigation blocked reason=\"external-navigation-or-redirection\" uri=\"{uri}\"");
    }

    private void CoreWebView2_NewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        PreviewDiagnostics.Info("PreviewWebView", $"Navigation blocked reason=\"new-window\" uri=\"{e.Uri}\"");
    }

    private void CoreWebView2_DownloadStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        PreviewDiagnostics.Info("PreviewWebView", $"Navigation blocked reason=\"download\" uri=\"{e.DownloadOperation.Uri}\"");
    }

    private void CoreWebView2_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        var currentUri = PreviewWebView.Source?.AbsoluteUri ?? _currentWebViewUri ?? "";
        if (currentUri == "about:blank")
        {
            // Do not show for blank page transitions (like ClearWebView)
            return;
        }

        var completedGen = _webViewNavigationGeneration;
        if (completedGen != _previewGeneration)
        {
            PreviewDiagnostics.Verbose("PreviewWebView", $"NavigationCompleted ignored reason=\"generation-mismatch\" current={_previewGeneration} completed={completedGen}");
            return;
        }

        var ext = Path.GetExtension(currentUri);
        bool isMediaOrAudio = string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(ext, ".webm", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(ext, ".m4a", StringComparison.OrdinalIgnoreCase);

        if (e.IsSuccess || isMediaOrAudio)
        {
            PreviewDiagnostics.Verbose("PreviewWebView", $"NavigationCompleted uri=\"{currentUri}\" success={e.IsSuccess} isMediaOrAudio={isMediaOrAudio} generation={completedGen}");
            PreviewDiagnostics.Verbose("PreviewWebView", $"WebView visibility changed Visible reason=\"Navigation completed\" generation={completedGen}");
            PreviewWebView.Visibility = Visibility.Visible;

            if (e.IsSuccess && currentUri.Contains("FileKakari_media_preview", StringComparison.OrdinalIgnoreCase))
            {
                _ = LogMediaPlaybackStateAsync(completedGen);
            }
        }
        else
        {
            PreviewDiagnostics.Error("PreviewWebView", $"Navigation failed uri=\"{currentUri}\" webErrorStatus={e.WebErrorStatus} generation={completedGen}");

            bool isMhtml = string.Equals(ext, ".mht", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ext, ".mhtml", StringComparison.OrdinalIgnoreCase);

            if (isMhtml && !_hasRetriedCurrentMhtml && completedGen == _previewGeneration)
            {
                _hasRetriedCurrentMhtml = true;
                PreviewDiagnostics.Info("PreviewWebView", $"Navigation retry uri=\"{currentUri}\" reason=\"mhtml-first-load-failed\" generation={completedGen}");
                PreviewWebView.CoreWebView2.Navigate(currentUri);
            }
            else
            {
                PreviewDiagnostics.Error("PreviewWebView", $"Navigation failed uri=\"{currentUri}\" error={e.WebErrorStatus}");
                PreviewDiagnostics.Verbose("PreviewWebView", $"WebView visibility changed Collapsed reason=\"Navigation failed\" generation={completedGen}");
                PreviewWebView.Visibility = Visibility.Collapsed;

                if (_currentWebViewFileInfo is not null)
                {
                    ReplacePreviewWithUnsupportedInfo(_currentWebViewFileInfo);
                }
            }
        }
    }

    private void ClearWebView()
    {
        try
        {
            DeleteCurrentTempMediaHtml();
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewMedia", $"Temporary media HTML delete failed reason=\"{ex.Message}\"");
        }

        _currentWebViewUri = null; // Clear tracked target URI
        _currentWebViewFileInfo = null;
        _currentWebViewMediaGeneration = -1;
        _currentWebViewMediaType = "";
        if (_isWebViewInitialized && PreviewWebView.CoreWebView2 is not null)
        {
            try
            {
                PreviewDiagnostics.Verbose("PreviewWebView", "ClearWebView navigating to about:blank");
                PreviewWebView.CoreWebView2.Navigate("about:blank");
            }
            catch (Exception ex)
            {
                PreviewDiagnostics.Error("PreviewWebView", $"ClearWebView navigate failed reason=\"{ex.Message}\"");
            }
        }
        PreviewDiagnostics.Verbose("PreviewWebView", $"WebView visibility changed Collapsed reason=\"Clearing WebView\" generation={_previewGeneration}");
        PreviewWebView.Visibility = Visibility.Collapsed;
    }

    private void TogglePreviewMaximized()
    {
        SetPreviewMaximized(!_isPreviewMaximized);
    }

    private void SetPreviewMaximized(bool maximized)
    {
        if (_isPreviewMaximized == maximized)
        {
            return;
        }

        _isPreviewMaximized = maximized;
        PreviewDiagnostics.Verbose("Preview", $"SetPreviewMaximized maximized={maximized}");

        if (maximized)
        {
            // Save state prior to maximize
            RememberPreviewPaneSize();
            _previousPreviewRowHeight = PreviewPaneRow.Height;
            _previousPreviewColumnWidth = PreviewPaneColumn.Width;

            ItemsListHost.Visibility = Visibility.Collapsed;
            PreviewGridSplitter.Visibility = Visibility.Collapsed;

            var placement = AppSettings.NormalizePreviewPanePlacement(_settingsService.Settings.PreviewPanePlacement);
            if (placement == PreviewPanePlacement.Right)
            {
                PreviewSplitterColumn.Width = new GridLength(0);
                PreviewPaneColumn.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                PreviewSplitterRow.Height = new GridLength(0);
                PreviewPaneRow.Height = new GridLength(1, GridUnitType.Star);
            }

            PreviewDiagnostics.Verbose("Preview", "Preview maximize enabled");
        }
        else
        {
            ItemsListHost.Visibility = Visibility.Visible;
            PreviewGridSplitter.Visibility = Visibility.Visible;

            ApplyPreviewPanePlacement(isVisible: true);

            PreviewDiagnostics.Verbose("Preview", "Preview maximize disabled");
            PreviewDiagnostics.Verbose("Preview", "Preview maximize layout restored");
        }

        if (PreviewMaximizeButton is not null)
        {
            PreviewMaximizeButton.Content = maximized ? "\uE923" : "\uE922";
            PreviewMaximizeButton.ToolTip = _text.Get(maximized ? "PreviewRestore" : "PreviewMaximize");
        }

        // Run UpdateLayout to notify HwndHost and WebView2
        PreviewShellHostContainer.UpdateLayout();
        PreviewWebView.UpdateLayout();
    }

    private void PreviewMaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePreviewMaximized();
    }

    private async Task LogMediaPlaybackStateAsync(int generation)
    {
        await Task.Delay(700);
        if (generation != _previewGeneration)
        {
            return;
        }

        try
        {
            var isCurrentMedia = generation == _currentWebViewMediaGeneration;
            var mediaType = isCurrentMedia ? _currentWebViewMediaType : "";
            var autoPlayVideoSetting = isCurrentMedia && _currentWebViewMediaAutoPlayVideoSetting;
            var muteVideoSetting = isCurrentMedia && _currentWebViewMediaMuteVideoSetting;
            var autoPlayAudioSetting = isCurrentMedia && _currentWebViewMediaAutoPlayAudioSetting;
            var expectedAutoPlay = isCurrentMedia && _currentWebViewMediaEffectiveAutoPlay;
            var expectedMuted = isCurrentMedia && _currentWebViewMediaEffectiveMuted;
            var js = @"(() => {
                var media = document.getElementById('media');
                if (!media) return JSON.stringify({ error: 'No media element found' });
                var playError = window.playError || '';
                var autoplaySatisfied = media.autoplay ? (!media.paused || media.currentTime > 0 || !!playError) : true;
                var stoppedSatisfied = media.autoplay ? true : (media.paused && media.currentTime < 0.1);
                return JSON.stringify({
                    paused: media.paused,
                    muted: media.muted,
                    autoplay: media.autoplay,
                    currentTime: media.currentTime,
                    readyState: media.readyState,
                    playError: playError,
                    autoplaySatisfied: autoplaySatisfied,
                    stoppedSatisfied: stoppedSatisfied
                });
            })()";

            if (_isWebViewInitialized && PreviewWebView.CoreWebView2 != null)
            {
                var jsonResult = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(js);
                if (!string.IsNullOrEmpty(jsonResult) && jsonResult != "null")
                {
                    if (string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        PreviewDiagnostics.Info("PreviewMedia", $"Media state mediaType=\"video\" autoPlayVideoSetting={autoPlayVideoSetting} muteVideoSetting={muteVideoSetting} effectiveAutoPlay={expectedAutoPlay} effectiveMuted={expectedMuted} state={jsonResult} generation={generation}");
                    }
                    else if (string.Equals(mediaType, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        PreviewDiagnostics.Info("PreviewMedia", $"Media state mediaType=\"audio\" autoPlayAudioSetting={autoPlayAudioSetting} effectiveAutoPlay={expectedAutoPlay} effectiveMuted=false state={jsonResult} generation={generation}");
                    }
                    else
                    {
                        PreviewDiagnostics.Info("PreviewMedia", $"Media state mediaType=\"{mediaType}\" effectiveAutoPlay={expectedAutoPlay} effectiveMuted={expectedMuted} state={jsonResult} generation={generation}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewMedia", $"Media state query failed reason=\"{ex.Message}\"");
        }
    }

    private void DeleteCurrentTempMediaHtml()
    {
        if (string.IsNullOrEmpty(_currentTempMediaHtmlPath))
        {
            return;
        }

        var path = _currentTempMediaHtmlPath;
        var ownerGeneration = _currentTempMediaHtmlGeneration;
        _currentTempMediaHtmlPath = null;
        _currentTempMediaHtmlGeneration = -1;

        TryDeleteTempMediaHtml(path, ownerGeneration, "owner-cleared");
    }

    private static void TryDeleteTempMediaHtml(string path, int generation, string reason)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            PreviewDiagnostics.Verbose("PreviewMedia", $"Temporary media HTML deleted path=\"{path}\" generation={generation} reason=\"{reason}\"");
        }
    }
}
