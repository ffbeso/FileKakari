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
    private GridLength _previewPaneHeight = new(240);
    private GridLength _previewPaneWidth = new(320);
    private bool _isWebViewInitialized;
    private string? _currentWebViewUri;
    private int _webViewNavigationGeneration;
    private FilePreviewInfo? _currentWebViewFileInfo;
    private bool _hasRetriedCurrentMhtml;
    private bool _isPreviewMaximized;
    private GridLength _previousPreviewRowHeight;
    private GridLength _previousPreviewColumnWidth;
    private const double DefaultPreviewPaneWidth = 320;
    private const double DefaultPreviewPaneHeight = 240;
    private const double MinPreviewPaneSize = 120;
    private const double MinFileListPaneSize = 180;
    private const double PreviewSplitterSize = 5;

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

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Shell && result.Clsid is not null:
                PerfLog.Write($"[MainWindow.Preview] Received FilePreviewKind.Shell for path='{result.FileInfo?.FullPath ?? ""}' CLSID='{result.Clsid.Value:B}'");
                await ReplacePreviewWithShellAsync(result.FileInfo?.FullPath ?? "", result.Clsid.Value, result.FileInfo, generation);
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.WebView && result.FileInfo is not null:
                PerfLog.Write($"[MainWindow.Preview] Received FilePreviewKind.WebView for path='{result.FileInfo.FullPath}'");
                LogPreviewUiState("Before WebView preview", generation, result.FileInfo.FullPath);
                ReplacePreviewWithWebView(result.FileInfo.FullPath, result.FileInfo, generation);
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Video && result.FileInfo is not null:
                PerfLog.Write($"[MainWindow.Preview] Received FilePreviewKind.Video for path='{result.FileInfo.FullPath}'");
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
        PerfLog.Write(
            $"[MainWindow.Preview] {label} path=\"{path}\" generation={generation} currentGeneration={_previewGeneration} webViewVisibility={PreviewWebView.Visibility} shellHostVisibility={PreviewShellHostContainer.Visibility} textVisibility={PreviewTextBox.Visibility} imageVisibility={PreviewImageScrollViewer.Visibility} videoVisibility={PreviewVideoHost.Visibility} unsupportedVisibility={PreviewUnsupportedCard.Visibility} messageVisibility={PreviewMessageText.Visibility}");
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
        _isPreviewMediaVideo = IsVideoPreviewPath(path);
        PreviewVideoHost.Visibility = Visibility.Visible;
        PreviewVideoHost.Opacity = 0;
        PreviewVideoHost.IsHitTestVisible = false;
        PreviewMediaPlayPauseButton.IsEnabled = false;
        PreviewMediaStopButton.IsEnabled = false;
        PreviewMediaElement.IsMuted = ShouldMutePreviewMedia(autoPlay: _settingsService.Settings.AutoPlayVideoPreview);
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

    private async Task ReplacePreviewWithShellAsync(string path, Guid clsid, FilePreviewInfo? fileInfo, int generation)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (generation != _previewGeneration)
            {
                PerfLog.Write($"[MainWindow.Preview] ReplacePreviewWithShell skipped reason=generation-mismatch current={_previewGeneration} requested={generation}");
                return;
            }

            PerfLog.Write($"[MainWindow.Preview] ReplacePreviewWithShell: path='{path}', clsid='{clsid:B}', attempt={attempt}, container_child_type='{PreviewShellHostContainer.Child?.GetType().FullName ?? "null"}'");
            ClearPreviewContent();
            ShellPreviewHost? shellHost = null;
            try
            {
                PerfLog.Write("[MainWindow.Preview] ReplacePreviewWithShell: Instantiating ShellPreviewHost...");
                var initializationPreference = attempt == 1
                    ? ShellPreviewInitializationPreference.Default
                    : ShellPreviewInitializationPreference.FileFirst;
                shellHost = new ShellPreviewHost(path, clsid, initializationPreference);
                ApplyShellPreviewHostBackground();
                PreviewShellHostContainer.Child = shellHost;
                PreviewShellHostContainer.Visibility = Visibility.Visible;
                PerfLog.Write("[MainWindow.Preview] ReplacePreviewWithShell: Attached ShellPreviewHost to container successfully.");
                return;
            }
            catch (Exception ex)
            {
                PerfLog.Write($"[MainWindow.Preview] ReplacePreviewWithShell exception: Type={ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}, Msg='{ex.Message}', attempt={attempt}");
                if (shellHost is not null && !ReferenceEquals(PreviewShellHostContainer.Child, shellHost))
                {
                    try
                    {
                        shellHost.Dispose();
                    }
                    catch (Exception disposeEx)
                    {
                        PerfLog.Write($"[MainWindow.Preview] ReplacePreviewWithShell local host dispose exception: {disposeEx.Message}");
                    }
                }
                ClearShellPreviewHost();

                if (attempt < maxAttempts)
                {
                    PerfLog.Write($"[MainWindow.Preview] ReplacePreviewWithShell retry scheduled path=\"{path}\" clsid=\"{clsid:B}\" delayMs=150");
                    await Task.Delay(150);
                    continue;
                }

                PerfLog.Write($"[MainWindow.Preview] ReplacePreviewWithShell fallback reason=\"shell-host-failed\" path=\"{path}\" clsid=\"{clsid:B}\"");
                await FallbackFromShellToBuiltInTextAsync(path, fileInfo, generation, ex);
                return;
            }
        }
    }

    private async Task FallbackFromShellToBuiltInTextAsync(string path, FilePreviewInfo? fileInfo, int generation, Exception shellException)
    {
        var ext = Path.GetExtension(path);
        var isOffice = OfficeExtensions.Contains(ext ?? "");

        if (isOffice)
        {
            PerfLog.Write($"[MainWindow.Preview] Office preview failed; possible Protected View or Mark-of-the-Web block path=\"{path}\"");
        }
        else
        {
            var textProvider = new BuiltInTextPreviewProvider();
            if (!textProvider.CanPreview(path))
            {
                PerfLog.Write($"[MainWindow.Preview] Shell fallback skipped reason=\"not-built-in-text-target\" path=\"{path}\" ext=\"{ext}\"");
            }
            else
            {
                try
                {
                    PerfLog.Write($"[MainWindow.Preview] Shell fallback: trying BuiltInTextPreviewProvider path=\"{path}\" ext=\"{ext}\"");
                    var result = await textProvider
                        .CreatePreviewAsync(new PreviewRequest(path), CancellationToken.None);

                    if (generation != _previewGeneration)
                    {
                        PerfLog.Write($"[MainWindow.Preview] Shell fallback skipped reason=generation-mismatch current={_previewGeneration} requested={generation}");
                        return;
                    }

                    if (result.Status == FilePreviewStatus.Success && result.Kind == FilePreviewKind.Text)
                    {
                        PerfLog.Write($"[MainWindow.Preview] Shell fallback selected provider=\"BuiltInTextPreviewProvider\" path=\"{path}\"");
                        ReplacePreviewWithText(result.Text ?? "");
                        return;
                    }

                    PerfLog.Write($"[MainWindow.Preview] Shell fallback BuiltInTextPreviewProvider status={result.Status} kind={result.Kind} path=\"{path}\"");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    PerfLog.Write($"[MainWindow.Preview] Shell fallback BuiltInTextPreviewProvider failed HRESULT=0x{ex.HResult:X8} message=\"{ex.Message}\" path=\"{path}\"");
                }
            }
        }

        if (fileInfo is not null)
        {
            if (isOffice)
            {
                PerfLog.Write("[MainWindow.Preview] ReplacePreviewWithShell fallback: Showing Office Protected View message.");
                ReplacePreviewWithUnsupportedInfo(
                    fileInfo,
                    _text.Get("PreviewOfficeFailedTitle"),
                    _text.Get("PreviewOfficeFailedHint"));
            }
            else
            {
                PerfLog.Write($"[MainWindow.Preview] ReplacePreviewWithShell fallback: Showing unsupported metadata card. shellHResult=0x{shellException.HResult:X8}");
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
                PerfLog.Write($"[MainWindow.Preview] ReplacePreviewWithShell fallback: Showing generic unsupported message. shellHResult=0x{shellException.HResult:X8}");
                ReplacePreviewWithMessage(_text.Get("PreviewUnsupported"));
            }
        }
    }

    private void ClearShellPreviewHost()
    {
        if (PreviewShellHostContainer.Child is ShellPreviewHost host)
        {
            PerfLog.Write("[MainWindow.Preview] ClearShellPreviewHost: Found active ShellPreviewHost, disposing...");
            try
            {
                host.Dispose();
                PerfLog.Write("[MainWindow.Preview] ClearShellPreviewHost: Disposed successfully.");
            }
            catch (Exception ex)
            {
                PerfLog.Write($"[MainWindow.Preview] ClearShellPreviewHost dispose exception: {ex.Message}");
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

    private void PreviewMediaElement_MediaOpened(object sender, RoutedEventArgs e)
    {
        var generation = _previewMediaGeneration;
        var mediaUri = _previewMediaUri;
        if (!IsCurrentPreviewMedia(generation, mediaUri))
        {
            return;
        }

        ShowOpenedPreviewMedia(_settingsService.Settings.AutoPlayVideoPreview);
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

        PerfLog.Write($"[PreviewMediaElement] MediaFailed: Uri='{mediaUri}', ErrorMessage='{e.ErrorException?.Message}', Exception='{e.ErrorException?.ToString() ?? "null"}'");

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
        PreviewMediaElement.IsMuted = ShouldMutePreviewMedia(autoPlay);
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

    private bool ShouldMutePreviewMedia(bool autoPlay)
    {
        return autoPlay
            && _isPreviewMediaVideo
            && _settingsService.Settings.MuteVideoPreviewOnAutoPlay;
    }

    private static bool IsVideoPreviewPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase);
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
        PerfLog.Write($"[WebViewPreview] Begin path=\"{path}\" generation={generation}");
        LogPreviewUiState("WebView before clear", generation, path);
        ClearPreviewContent(keepWebView: true);
        LogPreviewUiState("WebView after clear", generation, path);

        try
        {
            PerfLog.Write($"[WebViewPreview] InitializeWebViewAsync begin generation={generation}");
            bool wasInitialized = _isWebViewInitialized;
            await InitializeWebViewAsync();
            PerfLog.Write($"[WebViewPreview] InitializeWebViewAsync completed generation={generation}");

            if (generation != _previewGeneration)
            {
                PerfLog.Write($"[WebViewPreview] Navigate skipped reason=generation-mismatch current={_previewGeneration} requested={generation}");
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
                    PerfLog.Write($"[WebViewPreview] MHTML first-load candidate path=\"{path}\" generation={generation}");
                    PerfLog.Write($"[WebViewPreview] Delaying navigation for MHTML after WebView2 initialization generation={generation}");
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    if (generation != _previewGeneration)
                    {
                        PerfLog.Write($"[WebViewPreview] Navigate skipped after delay reason=generation-mismatch current={_previewGeneration} requested={generation}");
                        return;
                    }
                }
            }

            PerfLog.Write($"[WebViewPreview] Hide WebView before navigation generation={generation}");
            PerfLog.Write($"[WebViewPreview] WebView visibility changed Hidden reason=\"Hiding before navigation\" generation={generation}");
            PreviewWebView.Visibility = Visibility.Hidden;
            LogPreviewUiState("WebView before navigate", generation, path);

            var absoluteUri = new Uri(path).AbsoluteUri;
            _currentWebViewUri = absoluteUri; // Track target URI before navigating
            PerfLog.Write($"[WebViewPreview] Navigate requested uri=\"{absoluteUri}\" generation={generation}");
            PreviewWebView.CoreWebView2.Navigate(absoluteUri);
        }
        catch (Exception ex)
        {
            PerfLog.Write($"[MainWindow.Preview] WebView2 initialization/navigation failed: Type={ex.GetType().FullName}, Msg='{ex.Message}'.");

            ClearWebView();
            LogPreviewUiState("WebView after initialization failure clear", generation, path);

            if (generation != _previewGeneration)
            {
                PerfLog.Write($"[WebViewPreview] Fallback skipped reason=generation-mismatch current={_previewGeneration} requested={generation}");
                return;
            }

            if (ShellPreviewHandlerRegistry.TryGetPreviewHandlerClsid(path, out var clsid))
            {
                PerfLog.Write($"[WebViewPreview] Fallback to Shell reason=\"{ex.Message}\" path=\"{path}\"");
                await ReplacePreviewWithShellAsync(path, clsid, fileInfo, generation);
            }
            else
            {
                PerfLog.Write($"[WebViewPreview] Fallback to metadata reason=\"{ex.Message}\" path=\"{path}\"");
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

        PerfLog.Write("[MainWindow.Preview] InitializeWebViewAsync: Starting CoreWebView2 initialization...");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userDataFolder = Path.Combine(localAppData, "FileKakari", "WebView2");

        PerfLog.Write($"[MainWindow.Preview] WebView2 UserDataFolder: '{userDataFolder}'");

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
        PerfLog.Write("[MainWindow.Preview] InitializeWebViewAsync: CoreWebView2 initialization completed.");
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
                if (targetUriObj.AbsoluteUri == currentUriObj.AbsoluteUri)
                {
                    allowed = true;
                }
            }
        }
        catch
        {
            allowed = (uri == _currentWebViewUri);
        }

        PerfLog.Write($"[WebViewPreview] NavigationStarting uri=\"{uri}\" currentWebViewUri=\"{_currentWebViewUri ?? ""}\" allowed={allowed} generation={_previewGeneration}");

        if (allowed)
        {
            return;
        }

        // Block all document redirections, link clicks or page jumps
        e.Cancel = e.Cancel || true;
        PerfLog.Write($"[WebViewPreview] NavigationStarting blocked reason=\"Blocked external navigation or redirection\" uri=\"{uri}\"");
    }

    private void CoreWebView2_NewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        PerfLog.Write($"[WebViewPreview] NewWindow blocked uri='{e.Uri}'");
    }

    private void CoreWebView2_DownloadStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        PerfLog.Write($"[WebViewPreview] Download blocked uri='{e.DownloadOperation.Uri}'");
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
            PerfLog.Write($"[WebViewPreview] NavigationCompleted ignored reason=generation-mismatch current={_previewGeneration} completed={completedGen}");
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
            PerfLog.Write($"[WebViewPreview] NavigationCompleted uri=\"{currentUri}\" success={e.IsSuccess} (isMediaOrAudio={isMediaOrAudio}) generation={completedGen}");
            PerfLog.Write($"[WebViewPreview] Show WebView after navigation generation={completedGen}");
            PerfLog.Write($"[WebViewPreview] WebView visibility changed Visible reason=\"Navigation completed (success={e.IsSuccess})\" generation={completedGen}");
            PreviewWebView.Visibility = Visibility.Visible;
        }
        else
        {
            PerfLog.Write($"[WebViewPreview] NavigationCompleted uri=\"{currentUri}\" success=False webErrorStatus={e.WebErrorStatus} generation={completedGen}");

            bool isMhtml = string.Equals(ext, ".mht", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ext, ".mhtml", StringComparison.OrdinalIgnoreCase);

            if (isMhtml && !_hasRetriedCurrentMhtml && completedGen == _previewGeneration)
            {
                _hasRetriedCurrentMhtml = true;
                PerfLog.Write($"[WebViewPreview] MHTML navigation failed; retrying once uri=\"{currentUri}\" generation={completedGen}");
                PreviewWebView.CoreWebView2.Navigate(currentUri);
            }
            else
            {
                PerfLog.Write($"[WebViewPreview] Navigation failed uri=\"{currentUri}\" error={e.WebErrorStatus}");
                PerfLog.Write($"[WebViewPreview] WebView visibility changed Collapsed reason=\"Navigation failed error={e.WebErrorStatus}\" generation={completedGen}");
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
        _currentWebViewUri = null; // Clear tracked target URI
        _currentWebViewFileInfo = null;
        if (_isWebViewInitialized && PreviewWebView.CoreWebView2 is not null)
        {
            try
            {
                PerfLog.Write("[MainWindow.Preview] ClearWebView: Navigating WebView to about:blank to release file lock.");
                PreviewWebView.CoreWebView2.Navigate("about:blank");
            }
            catch (Exception ex)
            {
                PerfLog.Write($"[MainWindow.Preview] ClearWebView navigate exception: {ex.Message}");
            }
        }
        PerfLog.Write($"[WebViewPreview] WebView visibility changed Collapsed reason=\"Clearing WebView\" generation={_previewGeneration}");
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
        PerfLog.Write($"[MainWindow.Preview] SetPreviewMaximized: {maximized}");

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

            PerfLog.Write("[MainWindow.Preview] Preview maximize enabled");
        }
        else
        {
            ItemsListHost.Visibility = Visibility.Visible;
            PreviewGridSplitter.Visibility = Visibility.Visible;

            ApplyPreviewPanePlacement(isVisible: true);

            PerfLog.Write("[MainWindow.Preview] Preview maximize disabled");
            PerfLog.Write("[MainWindow.Preview] Preview maximize layout restored");
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
}
