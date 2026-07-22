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
    private int _previewRequestSequence;
    private string _activePreviewRequestId = "";
    private string? _previewOwnerSessionId;
    private string? _currentPreviewPath;
    private string? _currentTempMediaHtmlPath;
    private int _currentTempMediaHtmlGeneration = -1;
    private int _currentWebViewMediaGeneration = -1;
    private string _currentWebViewMediaType = "";
    private bool _currentWebViewMediaAutoPlayVideoSetting;
    private bool _currentWebViewMediaAutoPlayAudioSetting;
    private bool _currentWebViewMediaEffectiveAutoPlay;
    private bool _currentWebViewMediaEffectiveMuted;
    private GridLength _previewPaneHeight = new(240);
    private GridLength _previewPaneWidth = new(320);
    private bool _isWebViewInitialized;
    private string? _currentWebViewUri;
    private string _currentWebViewRequestId = "";
    private string _clearingWebViewRequestId = "";
    private string _blankWebViewNavigationRequestId = "";
    private int _blankWebViewNavigationGeneration = -1;
    private int _webViewNavigationGeneration;
    private FilePreviewInfo? _currentWebViewFileInfo;
    private bool _hasRetriedCurrentMhtml;
    private bool _isClearingWebView;
    private bool _isPreviewPaneTemporarilySuppressedForSettings;
    private bool _previewPaneWasVisibleBeforeSettings;
    private int _suppressPreviewForProgrammaticSelection;
    private readonly Stack<string> _programmaticSelectionOrigins = new();
    private bool _previewAwaitingExplicitSelection = true;
    private bool _isPreviewMaximized;
    private GridLength _previousPreviewRowHeight;
    private GridLength _previousPreviewColumnWidth;
    private const double DefaultPreviewPaneWidth = 320;
    private const double DefaultPreviewPaneHeight = 240;
    private const double MinPreviewPaneSize = 120;
    private const double MinFileListPaneSize = 180;
    private const double PreviewSplitterSize = 5;
    private static readonly Guid WindowsTxtPreviewerClsid = new("1531D583-8375-4D3F-B5FB-D23BBD169F22");

    private bool IsPreviewPaneEnabledByUser => _settingsService.Settings.IsPreviewPaneVisible == true;

    private bool IsPreviewPaneTemporarilySuppressed => _isPreviewPaneTemporarilySuppressedForSettings;

    private bool IsPreviewPaneActuallyVisible => PreviewPane.Visibility == Visibility.Visible;

    private void PreviewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePreviewPaneByUser();
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

    private void TogglePreviewPaneByUser()
    {
        SetPreviewPaneVisibleByUser(!IsPreviewPaneActuallyVisible);
    }

    private void PreviewCloseButton_Click(object sender, RoutedEventArgs e)
    {
        SetPreviewPaneVisibleByUser(false);
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
        if (!IsPreviewPaneActuallyVisible || delta == 0 || GetActivePreviewListView() is not { } listView || listView.Items.Count == 0)
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
        if (!IsPreviewPaneActuallyVisible
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

    private void SetPreviewPaneVisibleByUser(bool isVisible)
    {
        _settingsService.Settings.IsPreviewPaneVisible = isVisible;
        if (!isVisible)
        {
            if (_isPreviewMaximized)
            {
                SetPreviewMaximized(false);
            }
            RememberPreviewPaneSize();

            _ = CancelAndClearPreviewAsync("preview-pane-closed");
            PreviewTitleText.Text = "";
            PreviewPane.Visibility = Visibility.Collapsed;
            PreviewGridSplitter.Visibility = Visibility.Collapsed;
            ApplyPreviewPanePlacement(isVisible: false);
            return;
        }

        PreviewPane.Visibility = Visibility.Visible;
        PreviewGridSplitter.Visibility = Visibility.Visible;
        ApplyPreviewPanePlacement(isVisible: true);
        RefreshPreviewForActiveSelection("preview-pane-opened", explicitlyRequested: true);
    }

    private void InitializePreviewPaneVisibilityFromSettings()
    {
        if (!IsPreviewPaneEnabledByUser)
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
        if (IsPreviewPaneTemporarilySuppressed)
        {
            return;
        }

        _previewPaneWasVisibleBeforeSettings = IsPreviewPaneActuallyVisible;
        _isPreviewPaneTemporarilySuppressedForSettings = true;
        if (!_previewPaneWasVisibleBeforeSettings)
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
        if (!IsPreviewPaneTemporarilySuppressed)
        {
            return;
        }

        var shouldRestore = _previewPaneWasVisibleBeforeSettings;
        _previewPaneWasVisibleBeforeSettings = false;
        _isPreviewPaneTemporarilySuppressedForSettings = false;
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
        var visible = isVisible ?? IsPreviewPaneActuallyVisible;
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
        if (_isPreviewMaximized || !IsPreviewPaneActuallyVisible)
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

    private void RefreshPreviewForActiveSelection(string source = "refresh-active-selection", bool explicitlyRequested = false)
    {
        if (!IsPreviewPaneActuallyVisible || InternalPageHost.Visibility == Visibility.Visible)
        {
            PreviewDiagnostics.Info(
                "Preview",
                $"RefreshPreviewForActiveSelection skipped source=\"{source}\" visible={IsPreviewPaneActuallyVisible} internalPageVisible={InternalPageHost.Visibility == Visibility.Visible} generation={_previewGeneration}");
            return;
        }

        if (_activeWorkspaceSession is not null && !IsCurrentActiveSessionAndPane(_activeWorkspaceSession, out var skipReason, null, null))
        {
            PreviewDiagnostics.Info("Preview", $"RefreshPreviewForActiveSelection skipped source=\"{source}\" reason=\"{skipReason}\"");
            return;
        }

        if (_previewAwaitingExplicitSelection && !explicitlyRequested)
        {
            PreviewDiagnostics.Info(
                "Preview",
                $"RefreshPreviewForActiveSelection skipped source=\"{source}\" reason=\"awaiting-explicit-selection\" sessionId=\"{_activeWorkspaceSession?.Id ?? "null"}\" paneId=\"{GetActiveFolderPane()?.Id ?? "null"}\"");
            return;
        }

        if (explicitlyRequested)
        {
            _previewAwaitingExplicitSelection = false;
        }

        SchedulePreview(GetSelectedEntries(), source);
    }

    private IDisposable SuppressPreviewForProgrammaticSelection(string origin)
    {
        _suppressPreviewForProgrammaticSelection++;
        _programmaticSelectionOrigins.Push(origin);
        return new PreviewSelectionSuppressionScope(this, origin);
    }

    private bool IsPreviewSuppressedForProgrammaticSelection(out string origin)
    {
        origin = _programmaticSelectionOrigins.TryPeek(out var currentOrigin)
            ? currentOrigin
            : "programmatic";
        return _suppressPreviewForProgrammaticSelection > 0 || _listViewRestore.IsRestoring;
    }

    private void BeginPreviewAwaitingExplicitSelection(string source)
    {
        _previewAwaitingExplicitSelection = true;
        if (IsPreviewPaneActuallyVisible)
        {
            _ = CancelAndClearPreviewAsync($"selection-restore:{source}");
        }
    }

    private void RequestPreviewFromUserSelection(FolderPane pane, IReadOnlyList<FileEntry> selectedEntries, string source)
    {
        _previewAwaitingExplicitSelection = false;
        PerfLog.WriteVerbose(
            $"preview-selection source={source} sessionId={FindSessionContainingPane(pane)?.Id ?? "null"} paneId={pane.Id} " +
            $"selectedPath=\"{Path.GetFileName(selectedEntries.Count == 1 ? selectedEntries[0].FullPath : "")}\" " +
            "selectionChangeOrigin=user previewSuppressed=false previewRequested=true");
        SchedulePreview(selectedEntries, source);
    }

    private void LogPreviewSelectionSuppressed(FolderPane pane, IReadOnlyList<FileEntry> selectedEntries, string source, string origin, string skipReason)
    {
        PerfLog.WriteVerbose(
            $"preview-selection source={source} sessionId={FindSessionContainingPane(pane)?.Id ?? "null"} paneId={pane.Id} " +
            $"selectedPath=\"{Path.GetFileName(selectedEntries.Count == 1 ? selectedEntries[0].FullPath : "")}\" " +
            $"selectionChangeOrigin={origin} previewSuppressed=true previewRequested=false skipReason={skipReason}");
    }

    private sealed class PreviewSelectionSuppressionScope : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly string _origin;
        private bool _disposed;

        public PreviewSelectionSuppressionScope(MainWindow owner, string origin)
        {
            _owner = owner;
            _origin = origin;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_owner._programmaticSelectionOrigins.Count > 0
                && string.Equals(_owner._programmaticSelectionOrigins.Peek(), _origin, StringComparison.Ordinal))
            {
                _owner._programmaticSelectionOrigins.Pop();
            }
            else
            {
                _owner._programmaticSelectionOrigins.Clear();
            }

            _owner._suppressPreviewForProgrammaticSelection = Math.Max(0, _owner._suppressPreviewForProgrammaticSelection - 1);
        }
    }

    private void SchedulePreview(IReadOnlyList<FileEntry> selectedEntries, string source = "unspecified")
    {
        if (!IsPreviewPaneActuallyVisible)
        {
            PreviewDiagnostics.Info("Preview", $"SchedulePreview skipped source=\"{source}\" reason=\"preview-pane-hidden\" generation={_previewGeneration}");
            return;
        }

        var generation = Interlocked.Increment(ref _previewGeneration);
        var requestId = CreatePreviewRequestId(generation);
        _activePreviewRequestId = requestId;
        _previewOwnerSessionId = _activeWorkspaceSession?.Id;
        var selectedPath = selectedEntries.Count == 1 ? selectedEntries[0].FullPath : "";
        PreviewDiagnostics.Info(
            "Preview",
            $"SchedulePreview requestId=\"{requestId}\" source=\"{source}\" selectedCount={selectedEntries.Count} path=\"{selectedPath}\" generation={generation} previousGeneration={generation - 1}");
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;

        if (selectedEntries.Count == 0)
        {
            _currentPreviewPath = null;
            PreviewLoadingBar.Visibility = Visibility.Collapsed;
            _ = ShowNoSelectionDelayedAsync(generation, requestId, source);
            return;
        }

        if (selectedEntries.Count != 1)
        {
            _currentPreviewPath = null;
            PreviewDiagnostics.Info("Preview", $"SchedulePreview no-load requestId=\"{requestId}\" source=\"{source}\" reason=\"multi-selection\" selectedCount={selectedEntries.Count} generation={generation}");
            PreviewTitleText.Text = "";
            ReplacePreviewWithMessage(_text.Get("PreviewSingleFileOnly"));
            return;
        }

        var entry = selectedEntries[0];
        PreviewTitleText.Text = entry.Name;
        if (entry.IsDirectory)
        {
            _currentPreviewPath = null;
            PreviewDiagnostics.Info("Preview", $"SchedulePreview no-load requestId=\"{requestId}\" source=\"{source}\" reason=\"directory\" path=\"{entry.FullPath}\" generation={generation}");
            ReplacePreviewWithMessage(_text.Get("PreviewFoldersUnsupported"));
            return;
        }

        _currentPreviewPath = entry.FullPath;
        _previewCancellation = new CancellationTokenSource();
        _ = LoadPreviewAsync(entry.FullPath, generation, requestId, source, _previewCancellation.Token);
    }

    private async Task ShowNoSelectionDelayedAsync(int generation, string requestId, string source)
    {
        await Task.Delay(PreviewLoadDelay);

        if (generation != _previewGeneration
            || !IsPreviewPaneActuallyVisible
            || InternalPageHost.Visibility == Visibility.Visible)
        {
            PreviewDiagnostics.Info(
                "Preview",
                $"ShowNoSelectionDelayedAsync skipped requestId=\"{requestId}\" source=\"{source}\" reason=\"state-changed\" requested={generation} current={_previewGeneration} visible={IsPreviewPaneActuallyVisible} internalPageVisible={InternalPageHost.Visibility == Visibility.Visible}");
            return;
        }

        if (_previewAwaitingExplicitSelection)
        {
            PreviewDiagnostics.Info("Preview", $"ShowNoSelectionDelayedAsync skipped requestId=\"{requestId}\" source=\"{source}\" reason=\"awaiting-explicit-selection\"");
            PreviewTitleText.Text = "";
            ReplacePreviewWithMessage(_text.Get("PreviewSelectFile"));
            return;
        }

        var selectedEntries = GetSelectedEntries();
        if (selectedEntries.Count > 0)
        {
            SchedulePreview(selectedEntries, "show-no-selection-delayed-selection-restored");
            return;
        }

        PreviewTitleText.Text = "";
        ReplacePreviewWithMessage(_text.Get("PreviewSelectFile"));
    }

    private async Task LoadPreviewAsync(string path, int generation, string requestId, string requestSource, CancellationToken cancellationToken)
    {
        try
        {
            PreviewDiagnostics.Info("Preview", $"LoadPreviewAsync start requestId=\"{requestId}\" source=\"{requestSource}\" path=\"{path}\" generation={generation} currentGeneration={_previewGeneration}");
            PreviewLoadingBar.Visibility = Visibility.Visible;
            await Task.Delay(PreviewLoadDelay, cancellationToken);

            double scaleX = 1.0;
            double scaleY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
            {
                scaleX = source.CompositionTarget.TransformToDevice.M11;
                scaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            var targetWidth = PreviewPane.ActualWidth;
            var targetHeight = PreviewPane.ActualHeight;

            if (targetWidth <= 0 || double.IsNaN(targetWidth))
            {
                targetWidth = 1920 / scaleX;
            }
            if (targetHeight <= 0 || double.IsNaN(targetHeight))
            {
                targetHeight = 1080 / scaleY;
            }

            var result = await _filePreviewController.LoadAsync(
                path,
                targetWidth,
                targetHeight,
                scaleX,
                scaleY,
                generation,
                requestId,
                requestSource,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _previewGeneration)
            {
                PreviewDiagnostics.Info("Preview", $"LoadPreviewAsync skipped result requestId=\"{requestId}\" reason=\"generation-mismatch\" path=\"{path}\" requested={generation} current={_previewGeneration}");
                return;
            }

            await ShowPreviewResultAsync(result, generation, requestId, requestSource, cancellationToken);
            PreviewDiagnostics.Info("Preview", $"LoadPreviewAsync end requestId=\"{requestId}\" source=\"{requestSource}\" path=\"{path}\" generation={generation} currentGeneration={_previewGeneration} status={result.Status} kind={result.Kind}");
        }
        catch (OperationCanceledException)
        {
            PreviewDiagnostics.Info("Preview", $"LoadPreviewAsync canceled requestId=\"{requestId}\" source=\"{requestSource}\" path=\"{path}\" generation={generation} currentGeneration={_previewGeneration}");
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
        string requestId,
        string source,
        CancellationToken cancellationToken)
    {
        PreviewDiagnostics.Info(
            "Preview",
            $"ShowPreviewResultAsync start requestId=\"{requestId}\" source=\"{source}\" path=\"{result.FileInfo?.FullPath ?? ""}\" generation={generation} currentGeneration={_previewGeneration} status={result.Status} kind={result.Kind}");
        switch (result.Status)
        {
            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Text:
                ApplyTextPreview(result, generation, cancellationToken, "BuiltInTextPreviewProvider");
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Image && result.ImageSource is not null:
                if (generation != _previewGeneration)
                {
                    PreviewDiagnostics.Info("Preview", $"ShowPreviewResultAsync skipped requestId=\"{requestId}\" reason=\"generation-mismatch\" requested={generation} current={_previewGeneration} kind=Image");
                    return;
                }

                ReplacePreviewWithImage(result.ImageSource);
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.Shell && result.Clsid is not null:
                PreviewDiagnostics.Info("Preview", $"Provider result requestId=\"{requestId}\" kind=\"Shell\" path=\"{result.FileInfo?.FullPath ?? ""}\" clsid=\"{result.Clsid.Value:B}\" generation={generation}");
                await ReplacePreviewWithShellAsync(result.FileInfo?.FullPath ?? "", result.Clsid.Value, result.FileInfo, generation, requestId, cancellationToken);
                break;

            case FilePreviewStatus.Success when result.Kind == FilePreviewKind.WebView && result.FileInfo is not null:
                PreviewDiagnostics.Info("Preview", $"Provider result requestId=\"{requestId}\" kind=\"WebView\" path=\"{result.FileInfo.FullPath}\" generation={generation}");
                LogPreviewUiState("Before WebView preview", generation, result.FileInfo.FullPath);
                ReplacePreviewWithWebView(result.FileInfo.FullPath, result.FileInfo, generation, requestId);
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

        PreviewDiagnostics.Info(
            "Preview",
            $"ShowPreviewResultAsync end requestId=\"{requestId}\" source=\"{source}\" path=\"{result.FileInfo?.FullPath ?? ""}\" generation={generation} currentGeneration={_previewGeneration} status={result.Status} kind={result.Kind}");
    }

    private string CreatePreviewRequestId(int generation)
    {
        var sequence = Interlocked.Increment(ref _previewRequestSequence);
        return $"preview-{sequence}-{generation}";
    }

    private void LogPreviewUiState(string label, int generation, string path)
    {
        PreviewDiagnostics.Verbose(
            "Preview",
            $"{label} path=\"{path}\" generation={generation} currentGeneration={_previewGeneration} webViewVisibility={PreviewWebView.Visibility} shellHostVisibility={PreviewShellHostContainer.Visibility} textVisibility={PreviewTextBox.Visibility} imageVisibility={PreviewImageScrollViewer.Visibility} unsupportedVisibility={PreviewUnsupportedCard.Visibility} messageVisibility={PreviewMessageText.Visibility}");
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

    private void ReplacePreviewWithImage(ImageSource imageSource)
    {
        ClearPreviewContent();
        PreviewImage.Source = imageSource;
        PreviewImageScrollViewer.Visibility = Visibility.Visible;
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
        string requestId,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (generation != _previewGeneration)
            {
                PreviewDiagnostics.Info("PreviewShell", $"ReplacePreviewWithShell skipped requestId=\"{requestId}\" reason=\"generation-mismatch\" current={_previewGeneration} requested={generation} path=\"{path}\"");
                return;
            }

            PreviewDiagnostics.Info("PreviewShell", $"ShellPreviewHost create start requestId=\"{requestId}\" path=\"{path}\" clsid=\"{clsid:B}\" attempt={attempt}");
            ClearPreviewContent();
            ShellPreviewHost? shellHost = null;
            try
            {
                PreviewDiagnostics.Verbose("PreviewShell", "Instantiating ShellPreviewHost");
                var initializationPreference = attempt == 1
                    ? ShellPreviewInitializationPreference.Default
                    : ShellPreviewInitializationPreference.FileFirst;
                shellHost = new ShellPreviewHost(path, clsid, requestId, initializationPreference);

                ApplyShellPreviewHostBackground();
                PreviewShellHostContainer.Child = shellHost;
                PreviewShellHostContainer.Visibility = Visibility.Visible;
                PreviewDiagnostics.Info("PreviewShell", $"ShellPreviewHost create complete requestId=\"{requestId}\" path=\"{path}\" clsid=\"{clsid:B}\" attempt={attempt}");
                return;
            }
            catch (Exception ex)
            {
                PreviewDiagnostics.Error("PreviewShell", $"ShellPreviewHost create failed requestId=\"{requestId}\" path=\"{path}\" clsid=\"{clsid:B}\" HRESULT=0x{ex.HResult:X8} reason=\"{ex.Message}\" attempt={attempt}");
                if (shellHost is not null && !ReferenceEquals(PreviewShellHostContainer.Child, shellHost))
                {
                    try
                    {
                        shellHost.Dispose();
                    }
                    catch (Exception disposeEx)
                    {
                        PreviewDiagnostics.Error("PreviewShell", $"Local host dispose failed requestId=\"{requestId}\" reason=\"{disposeEx.Message}\"");
                    }
                }
                ClearShellPreviewHost();

                if (ShouldRetryShellPreview(clsid, ex) && attempt < maxAttempts)
                {
                    PreviewDiagnostics.Info("PreviewShell", $"Retry scheduled requestId=\"{requestId}\" path=\"{path}\" clsid=\"{clsid:B}\" delayMs=150");
                    await Task.Delay(150, cancellationToken);
                    continue;
                }

                PreviewDiagnostics.Info("PreviewShell", $"Fallback requestId=\"{requestId}\" reason=\"shell-host-failed\" path=\"{path}\" clsid=\"{clsid:B}\"");
                await FallbackFromShellToBuiltInTextAsync(path, fileInfo, generation, requestId, cancellationToken, ex);
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
        string requestId,
        CancellationToken cancellationToken,
        Exception shellException)
    {
        var ext = Path.GetExtension(path);
        var isOffice = OfficeExtensions.Contains(ext ?? "");

        if (isOffice)
        {
            PreviewDiagnostics.Info("PreviewShell", $"Office fallback requestId=\"{requestId}\" path=\"{path}\" reason=\"possible-protected-view-or-mark-of-the-web\"");
        }
        else
        {
            var textProvider = new BuiltInTextPreviewProvider();
            if (!textProvider.CanPreview(path))
            {
                PreviewDiagnostics.Info("PreviewShell", $"Fallback skipped requestId=\"{requestId}\" path=\"{path}\" ext=\"{ext}\" reason=\"not-text-preview-type\"");
                if (fileInfo is not null)
                {
                    ReplacePreviewWithUnsupportedInfo(fileInfo);
                }
                return;
            }

            try
            {
                PreviewDiagnostics.Info("PreviewShell", $"Fallback to provider=\"BuiltInTextPreviewProvider\" requestId=\"{requestId}\" path=\"{path}\" ext=\"{ext}\"");
                var result = await textProvider
                    .CreatePreviewAsync(new PreviewRequest(path) { RequestId = requestId, Source = "shell-fallback", Generation = generation }, cancellationToken);

                if (generation != _previewGeneration)
                {
                    PreviewDiagnostics.Info("PreviewShell", $"Fallback skipped requestId=\"{requestId}\" reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
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
                    PreviewDiagnostics.Info("PreviewShell", $"Fallback selected provider=\"BuiltInTextPreviewProvider\" requestId=\"{requestId}\" path=\"{path}\"");
                    ApplyTextPreview(result, generation, cancellationToken, "ShellFallbackBuiltInTextPreviewProvider");
                    return;
                }

                PreviewDiagnostics.Info("PreviewShell", $"Fallback provider result requestId=\"{requestId}\" status={result.Status} kind={result.Kind} path=\"{path}\"");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                PreviewDiagnostics.Error("PreviewShell", $"Fallback provider failed requestId=\"{requestId}\" HRESULT=0x{ex.HResult:X8} reason=\"{ex.Message}\" path=\"{path}\"");
            }
        }

        if (fileInfo is not null)
        {
            if (isOffice)
            {
                PreviewDiagnostics.Info("PreviewShell", $"Fallback display=\"office-protected-view-message\" requestId=\"{requestId}\"");
                ReplacePreviewWithUnsupportedInfo(
                    fileInfo,
                    _text.Get("PreviewOfficeFailedTitle"),
                    _text.Get("PreviewOfficeFailedHint"));
            }
            else
            {
                PreviewDiagnostics.Info("PreviewShell", $"Fallback display=\"unsupported-metadata-card\" requestId=\"{requestId}\" shellHResult=0x{shellException.HResult:X8}");
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
                PreviewDiagnostics.Info("PreviewShell", $"Fallback display=\"generic-unsupported-message\" requestId=\"{requestId}\" shellHResult=0x{shellException.HResult:X8}");
                ReplacePreviewWithMessage(_text.Get("PreviewUnsupported"));
            }
        }
    }

    private void ClearShellPreviewHost(bool failOnDisposeFailure = false)
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
                if (failOnDisposeFailure)
                {
                    throw;
                }
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

    private static bool RequiresCustomMediaHtml(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase);
    }

    private void ClearNonVideoPreviewContent()
    {
        PreviewDiagnostics.Info("Preview", $"ClearNonVideoPreviewContent requestId=\"{_activePreviewRequestId}\" generation={_previewGeneration}");
        PreviewTextBox.Text = "";
        PreviewTextBox.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImageScrollViewer.Visibility = Visibility.Collapsed;
        PreviewUnsupportedCard.Visibility = Visibility.Collapsed;
        PreviewMessageText.Visibility = Visibility.Collapsed;
        ClearShellPreviewHost();
        _ = ClearWebViewAsync("clear-non-video-content");
    }

    private void ClearPreviewContent(bool keepWebView = false, bool failOnShellHostDisposeFailure = false)
    {
        PreviewDiagnostics.Info("Preview", $"ClearPreviewContent requestId=\"{_activePreviewRequestId}\" generation={_previewGeneration} keepWebView={keepWebView}");
        PreviewTextBox.Text = "";
        PreviewTextBox.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImageScrollViewer.Visibility = Visibility.Collapsed;
        PreviewUnsupportedCard.Visibility = Visibility.Collapsed;
        PreviewMessageText.Visibility = Visibility.Collapsed;
        PreviewLoadingBar.Visibility = Visibility.Collapsed;
        ClearShellPreviewHost(failOnShellHostDisposeFailure);
        if (!keepWebView)
        {
            _ = ClearWebViewAsync("preview-type-changed");
        }
    }

    private void ShowPreviewMessage(string message)
    {
        PreviewMessageText.Text = message;
        PreviewMessageText.Visibility = Visibility.Visible;
    }

    private void CancelPreviewLoad()
    {
        var previousGeneration = _previewGeneration;
        var generation = Interlocked.Increment(ref _previewGeneration);
        PreviewDiagnostics.Info("Preview", $"CancelPreviewLoad requestId=\"{_activePreviewRequestId}\" previousGeneration={previousGeneration} generation={generation}");
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
    }

    private async Task CancelAndClearPreviewAsync(string reason, bool failOnShellHostDisposeFailure = false)
    {
        var path = _currentPreviewPath ?? "";
        PreviewDiagnostics.Info("Preview", $"CancelAndClearPreviewAsync start requestId=\"{_activePreviewRequestId}\" reason=\"{reason}\" path=\"{path}\" generation={_previewGeneration}");
        CancelPreviewLoad();
        ClearPreviewContent(keepWebView: true, failOnShellHostDisposeFailure);
        await ClearWebViewAsync(reason);
        _currentPreviewPath = null;
        _previewOwnerSessionId = null;
        PreviewDiagnostics.Info("Preview", $"CancelAndClearPreviewAsync end requestId=\"{_activePreviewRequestId}\" reason=\"{reason}\" path=\"{path}\" generation={_previewGeneration}");
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

    private async void ReplacePreviewWithWebView(string path, FilePreviewInfo fileInfo, int generation, string requestId)
    {
        PreviewDiagnostics.Info("PreviewWebView", $"Navigation preparing requestId=\"{requestId}\" path=\"{path}\" generation={generation}");
        LogPreviewUiState("WebView before clear", generation, path);
        ClearPreviewContent(keepWebView: true);
        LogPreviewUiState("WebView after clear", generation, path);

        try
        {
            if (RequiresCustomMediaHtml(path))
            {
                var originalMediaPath = path;
                var isVideo = IsVideoPreviewPath(path);
                var isAudio = IsAudioPreviewPath(path);
                var autoPlayVideoSetting = _settingsService.Settings.AutoPlayVideoPreview;
                var autoPlayAudioSetting = _settingsService.Settings.AutoPlayAudioPreview ?? _settingsService.Settings.AutoPlayVideoPreview;
                var document = MediaPreviewHtmlBuilder.Build(
                    path,
                    isVideo,
                    isAudio,
                    autoPlayVideoSetting,
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
                    var tempHtmlPath = PreviewTemporaryFileManager.CreateMediaPreviewHtmlPath(generation);
                    await PreviewTemporaryFileManager.WriteMediaPreviewHtmlAsync(tempHtmlPath, document.Html);
                    if (generation != _previewGeneration)
                    {
                        PreviewTemporaryFileManager.DeleteMediaPreviewHtml(
                            tempHtmlPath,
                            generation,
                            "generation-mismatch-after-write");
                        return;
                    }

                    _currentTempMediaHtmlPath = tempHtmlPath;
                    _currentTempMediaHtmlGeneration = generation;
                    _currentWebViewMediaGeneration = generation;
                    _currentWebViewMediaType = document.MediaType;
                    _currentWebViewMediaAutoPlayVideoSetting = autoPlayVideoSetting;
                    _currentWebViewMediaAutoPlayAudioSetting = autoPlayAudioSetting;
                    _currentWebViewMediaEffectiveAutoPlay = document.EffectiveAutoPlay;
                    _currentWebViewMediaEffectiveMuted = document.EffectiveMuted;
                    path = tempHtmlPath;
                    if (string.Equals(document.MediaType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        PreviewDiagnostics.Verbose("PreviewMedia", $"Build\r\nmediaType=\"video\"\r\nautoPlaySetting={autoPlayVideoSetting.ToString().ToLowerInvariant()}\r\neffectiveAutoPlay={document.EffectiveAutoPlay.ToString().ToLowerInvariant()}\r\neffectiveMuted={document.EffectiveMuted.ToString().ToLowerInvariant()}\r\ngeneration={generation}");
                    }
                    else
                    {
                        PreviewDiagnostics.Verbose("PreviewMedia", $"Build\r\nmediaType=\"audio\"\r\nautoPlaySetting={autoPlayAudioSetting.ToString().ToLowerInvariant()}\r\neffectiveAutoPlay={document.EffectiveAutoPlay.ToString().ToLowerInvariant()}\r\neffectiveMuted=false\r\ngeneration={generation}");
                    }
                }
                catch (Exception ex)
                {
                    PreviewDiagnostics.Error("PreviewMedia", $"Temporary media HTML create failed reason=\"{ex.Message}\"");
                }
            }

            PreviewDiagnostics.Info("PreviewWebView", $"InitializeWebViewAsync begin requestId=\"{requestId}\" generation={generation}");
            bool wasInitialized = _isWebViewInitialized;
            await InitializeWebViewAsync();
            PreviewDiagnostics.Info("PreviewWebView", $"InitializeWebViewAsync completed requestId=\"{requestId}\" generation={generation}");

            if (generation != _previewGeneration)
            {
                PreviewDiagnostics.Info("PreviewWebView", $"Navigate skipped requestId=\"{requestId}\" reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
                return;
            }

            if (PreviewWebView.CoreWebView2 is null)
            {
                throw new InvalidOperationException("CoreWebView2 is null after initialization.");
            }

            _webViewNavigationGeneration = generation;
            _currentWebViewRequestId = requestId;
            _currentWebViewFileInfo = fileInfo;
            _isClearingWebView = false;
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
                        PreviewDiagnostics.Info("PreviewWebView", $"Navigate skipped after delay requestId=\"{requestId}\" reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
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
            PreviewDiagnostics.Info("PreviewWebView", $"Navigation started requestId=\"{requestId}\" uri=\"{absoluteUri}\" generation={generation}");
            PreviewWebView.CoreWebView2.Navigate(absoluteUri);
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewWebView", $"Navigation failed requestId=\"{requestId}\" reason=\"{ex.Message}\"");

            _ = ClearWebViewAsync("navigation-failed");
            LogPreviewUiState("WebView after initialization failure clear", generation, path);

            if (generation != _previewGeneration)
            {
                PreviewDiagnostics.Info("PreviewWebView", $"Fallback skipped requestId=\"{requestId}\" reason=\"generation-mismatch\" current={_previewGeneration} requested={generation}");
                return;
            }

            bool isMedia = RequiresCustomMediaHtml(path);

            if (!isMedia && ShellPreviewHandlerRegistry.TryGetPreviewHandlerClsid(path, out var clsid))
            {
                PreviewDiagnostics.Info("PreviewWebView", $"Fallback to provider=\"ShellPreviewHandlerProvider\" requestId=\"{requestId}\" reason=\"{ex.Message}\" path=\"{path}\"");
                await ReplacePreviewWithShellAsync(path, clsid, fileInfo, generation, requestId, CancellationToken.None);
            }
            else
            {
                PreviewDiagnostics.Info("PreviewWebView", $"Fallback to unsupported requestId=\"{requestId}\" reason=\"{ex.Message}\" path=\"{path}\" isMedia={isMedia}");
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
        // Keep WebView2's standard context menu enabled so selected PDF/HTML text can be copied.
        PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
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
        if (_isClearingWebView && string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(_currentWebViewUri))
            {
                PreviewDiagnostics.Info(
                    "PreviewWebView",
                    $"NavigationStarting requestId=\"{_blankWebViewNavigationRequestId}\" uri=\"{uri}\" reason=\"about-blank-clear\" generation={_previewGeneration} navigationGeneration={_blankWebViewNavigationGeneration}");
                return;
            }

            e.Cancel = true;
            PreviewDiagnostics.Info(
                "PreviewWebView",
                $"Navigation blocked requestId=\"{_currentWebViewRequestId}\" reason=\"stale-about-blank-clear\" uri=\"{uri}\" currentWebViewUri=\"{_currentWebViewUri}\" generation={_previewGeneration} navigationGeneration={_webViewNavigationGeneration}");
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

        PreviewDiagnostics.Info("PreviewWebView", $"NavigationStarting requestId=\"{_currentWebViewRequestId}\" uri=\"{uri}\" currentWebViewUri=\"{_currentWebViewUri ?? ""}\" allowed={allowed} generation={_previewGeneration} navigationGeneration={_webViewNavigationGeneration}");

        if (allowed)
        {
            return;
        }

        // Block all document redirections, link clicks or page jumps
        e.Cancel = e.Cancel || true;
        PreviewDiagnostics.Info("PreviewWebView", $"Navigation blocked requestId=\"{_currentWebViewRequestId}\" reason=\"external-navigation-or-redirection\" uri=\"{uri}\"");
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
        if (string.Equals(currentUri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            var completedRequestId = string.IsNullOrEmpty(_blankWebViewNavigationRequestId)
                ? _clearingWebViewRequestId
                : _blankWebViewNavigationRequestId;
            if (!string.IsNullOrEmpty(_currentWebViewUri))
            {
                PreviewDiagnostics.Info(
                    "PreviewWebView",
                    $"NavigationCompleted ignored requestId=\"{completedRequestId}\" uri=\"{currentUri}\" reason=\"stale-about-blank-clear\" currentWebViewUri=\"{_currentWebViewUri}\" generation={_previewGeneration} navigationGeneration={_webViewNavigationGeneration} blankGeneration={_blankWebViewNavigationGeneration}");
                return;
            }

            _isClearingWebView = false;
            PreviewDiagnostics.Info("PreviewWebView", $"NavigationCompleted requestId=\"{completedRequestId}\" uri=\"{currentUri}\" reason=\"about-blank-clear\" generation={_previewGeneration} navigationGeneration={_webViewNavigationGeneration}");
            _clearingWebViewRequestId = "";
            _blankWebViewNavigationRequestId = "";
            _blankWebViewNavigationGeneration = -1;
            // Do not show for blank page transitions (like ClearWebView)
            return;
        }

        var completedGen = _webViewNavigationGeneration;
        if (completedGen != _previewGeneration)
        {
            PreviewDiagnostics.Info("PreviewWebView", $"NavigationCompleted ignored requestId=\"{_currentWebViewRequestId}\" reason=\"generation-mismatch\" current={_previewGeneration} completed={completedGen} uri=\"{currentUri}\"");
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
            PreviewDiagnostics.Info("PreviewWebView", $"NavigationCompleted requestId=\"{_currentWebViewRequestId}\" uri=\"{currentUri}\" success={e.IsSuccess} isMediaOrAudio={isMediaOrAudio} generation={completedGen}");
            PreviewDiagnostics.Verbose("PreviewWebView", $"WebView visibility changed Visible reason=\"Navigation completed\" generation={completedGen}");
            PreviewWebView.Visibility = Visibility.Visible;

            if (e.IsSuccess && PreviewTemporaryFileManager.IsMediaPreviewHtmlUri(currentUri))
            {
                _ = LogMediaPlaybackStateAsync(completedGen);
            }
        }
        else
        {
            PreviewDiagnostics.Error("PreviewWebView", $"Navigation failed requestId=\"{_currentWebViewRequestId}\" uri=\"{currentUri}\" webErrorStatus={e.WebErrorStatus} generation={completedGen}");

            bool isMhtml = string.Equals(ext, ".mht", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ext, ".mhtml", StringComparison.OrdinalIgnoreCase);

            if (isMhtml && !_hasRetriedCurrentMhtml && completedGen == _previewGeneration)
            {
                _hasRetriedCurrentMhtml = true;
                PreviewDiagnostics.Info("PreviewWebView", $"Navigation retry requestId=\"{_currentWebViewRequestId}\" uri=\"{currentUri}\" reason=\"mhtml-first-load-failed\" generation={completedGen}");
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

    private async Task ClearWebViewAsync(string reason)
    {
        var requestId = _currentWebViewRequestId;
        _clearingWebViewRequestId = requestId;
        var activeGen = _webViewNavigationGeneration;
        PreviewDiagnostics.Info("PreviewWebView", $"ClearWebViewAsync start requestId=\"{requestId}\" reason=\"{reason}\" generation={_previewGeneration} navigationGeneration={_webViewNavigationGeneration} initialized={_isWebViewInitialized}");
        if (!_isWebViewInitialized || PreviewWebView.CoreWebView2 == null)
        {
            try
            {
                DeleteCurrentTempMediaHtml();
            }
            catch (Exception ex)
            {
                PreviewDiagnostics.Error("PreviewMedia", $"Temporary media HTML delete failed reason=\"{ex.Message}\"");
            }
            _currentWebViewUri = null;
            _currentWebViewRequestId = "";
            _currentWebViewFileInfo = null;
            _blankWebViewNavigationRequestId = "";
            _blankWebViewNavigationGeneration = -1;
            _currentWebViewMediaGeneration = -1;
            _currentWebViewMediaType = "";
            PreviewDiagnostics.Verbose("PreviewWebView", $"WebView visibility changed Collapsed reason=\"Clearing WebView\" generation={_previewGeneration}");
            PreviewWebView.Visibility = Visibility.Collapsed;
            PreviewDiagnostics.Info("PreviewWebView", $"ClearWebViewAsync end requestId=\"{requestId}\" reason=\"{reason}\" mode=\"not-initialized\" generation={_previewGeneration}");
            return;
        }

        var currentUri = PreviewWebView.Source?.AbsoluteUri ?? _currentWebViewUri ?? "";
        var isMediaHtml = PreviewTemporaryFileManager.IsMediaPreviewHtmlUri(currentUri);

        if (isMediaHtml)
        {
            try
            {
                var js = @"(() => {
                    const media = document.querySelector('video, audio');
                    if (media) {
                        media.pause();
                        media.removeAttribute('src');
                        media.load();
                        return 'ok';
                    }
                    return 'no-media';
                })()";
                var jsTask = PreviewWebView.CoreWebView2.ExecuteScriptAsync(js);
                var delayTask = Task.Delay(2000);
                var completedTask = await Task.WhenAny(jsTask, delayTask);
                if (completedTask == jsTask)
                {
                    await jsTask;
                }
                else
                {
                    PreviewDiagnostics.Error("PreviewWebView", "Media stop JS execution timed out.");
                }
            }
            catch (Exception ex)
            {
                PreviewDiagnostics.Error("PreviewWebView", $"Media stop JS failed reason=\"{ex.Message}\"");
            }
            finally
            {
                try
                {
                    PreviewWebView.CoreWebView2.Stop();
                }
                catch (Exception ex)
                {
                    PreviewDiagnostics.Error("PreviewWebView", $"CoreWebView2.Stop failed reason=\"{ex.Message}\"");
                }
            }
            PreviewDiagnostics.Verbose("PreviewWebView", $"Media stopped\r\nreason=\"{reason}\"\r\ngeneration={activeGen}");
        }

        if (!IsCurrentWebViewClearOwner(requestId, activeGen))
        {
            PreviewDiagnostics.Info(
                "PreviewWebView",
                $"ClearWebViewAsync skipped requestId=\"{requestId}\" reason=\"owner-changed\" clearReason=\"{reason}\" currentRequestId=\"{_currentWebViewRequestId}\" clearGeneration={activeGen} currentNavigationGeneration={_webViewNavigationGeneration} previewGeneration={_previewGeneration}");
            return;
        }

        _currentWebViewUri = null;
        _currentWebViewRequestId = "";
        _currentWebViewFileInfo = null;
        _currentWebViewMediaGeneration = -1;
        _currentWebViewMediaType = "";
        _isClearingWebView = true;
        _blankWebViewNavigationRequestId = requestId;
        _blankWebViewNavigationGeneration = activeGen;

        try
        {
            PreviewDiagnostics.Info("PreviewWebView", $"ClearWebView navigating to about:blank requestId=\"{requestId}\" generation={_previewGeneration} navigationGeneration={activeGen}");
            PreviewWebView.CoreWebView2.Navigate("about:blank");
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewWebView", $"ClearWebView navigate failed reason=\"{ex.Message}\"");
        }

        try
        {
            DeleteCurrentTempMediaHtml();
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewMedia", $"Temporary media HTML delete failed reason=\"{ex.Message}\"");
        }

        PreviewDiagnostics.Verbose("PreviewWebView", $"WebView visibility changed Collapsed reason=\"Clearing WebView\" generation={_previewGeneration}");
        PreviewWebView.Visibility = Visibility.Collapsed;
        PreviewDiagnostics.Info("PreviewWebView", $"ClearWebViewAsync end requestId=\"{requestId}\" reason=\"{reason}\" generation={_previewGeneration} navigationGeneration={activeGen}");
    }

    private bool IsCurrentWebViewClearOwner(string requestId, int navigationGeneration)
    {
        return navigationGeneration == _webViewNavigationGeneration
            && string.Equals(requestId, _currentWebViewRequestId, StringComparison.Ordinal);
    }

    private void ClearWebViewForShutdown()
    {
        try
        {
            PreviewWebView.CoreWebView2?.Stop();
            PreviewWebView.Source = new Uri("about:blank");
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Verbose("PreviewWebView", $"ClearWebViewForShutdown best-effort failed reason=\"{ex.Message}\"");
        }

        try
        {
            DeleteCurrentTempMediaHtml();
        }
        catch
        {
            // Ignore
        }
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
                    try
                    {
                        var rawJson = System.Text.Json.JsonSerializer.Deserialize<string>(jsonResult);
                        if (!string.IsNullOrEmpty(rawJson))
                        {
                            using (var doc = System.Text.Json.JsonDocument.Parse(rawJson))
                            {
                                var root = doc.RootElement;
                                if (root.TryGetProperty("error", out _))
                                {
                                    PreviewDiagnostics.Error("PreviewMedia", $"Media state query reported error: {rawJson}");
                                }
                                else
                                {
                                    var paused = root.GetProperty("paused").GetBoolean().ToString().ToLowerInvariant();
                                    var mutedVal = root.GetProperty("muted").GetBoolean().ToString().ToLowerInvariant();
                                    var autoplay = root.GetProperty("autoplay").GetBoolean().ToString().ToLowerInvariant();
                                    var currentTime = root.GetProperty("currentTime").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    var readyState = root.GetProperty("readyState").GetInt32().ToString();
                                    var playError = root.GetProperty("playError").GetString() ?? "";

                                    PreviewDiagnostics.Verbose("PreviewMedia", $"State\r\nmediaType=\"{mediaType}\"\r\nexpectedAutoPlay={expectedAutoPlay.ToString().ToLowerInvariant()}\r\nexpectedMuted={expectedMuted.ToString().ToLowerInvariant()}\r\npaused={paused}\r\nmuted={mutedVal}\r\nplayError=\"{playError}\"\r\ngeneration={generation}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        PreviewDiagnostics.Error("PreviewMedia", $"Failed to parse media state JSON. Raw={jsonResult}. Error={ex.Message}");
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

        PreviewTemporaryFileManager.DeleteMediaPreviewHtml(path, ownerGeneration, "owner-cleared");
    }
}
