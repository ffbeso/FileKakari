using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Specialized;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileKakari;

public partial class MainWindow : Window
{
    private static readonly object CrashContextGate = new();
    private static string _crashContextSnapshot = "reason=not-initialized stateId= activeStateId= loadingStateId= selectedIndex=-1 tabCount=0";
    private const int InitialBatchSize = 64;
    private const int SubsequentBatchSize = 2048;
    private const string TabDragFormat = "FileKakari.WorkspaceSession";
    private const string SubTabDragFormat = "FileKakari.FolderPaneSubTab";
    private const string FileDragFormat = "FileKakari.FileEntries";
    private const string BreadcrumbFolderDragFormat = "FileKakari.BreadcrumbFolder";
    private const string FileDropTargetTag = "FileDropTarget";
    private static readonly TimeSpan FileTabHoverDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan WorkspaceLocalStateSaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WorkspaceRenameClickDelay = TimeSpan.FromMilliseconds(350);
    private readonly BulkObservableCollection<FileEntry> _items = [];
    private readonly LocalizationService _text = new();
    private readonly PerformanceLogger _performanceLogger = new();
    private readonly DevListPerfOptions _devListPerfOptions = DevListPerfOptions.FromEnvironment();
    private readonly FileOperationService _fileOperationService = new();
    private readonly SettingsService _settingsService;
    private readonly SessionStateService _sessionStateService;
    private readonly UserCommandService _userCommandService;
    private readonly UserCommandExecutionService _userCommandExecutionService = new();
    private readonly Dictionary<string, FolderColumnWidthsState> _sessionFolderColumnWidths;
    private readonly Dictionary<string, double> _sessionColumnWidths;
    private readonly SpecialLocationService _specialLocationService = new();
    private readonly DriveAvailabilityService _driveAvailabilityService = new();
    private readonly FolderWatchService _folderWatchService = new();
    private readonly DeviceChangeService _deviceChangeService;
    private readonly RenameInteractionService _renameInteraction = new();
    private RenameFocusService _renameFocus = null!;
    private readonly IShellContextMenuService _shellContextMenuService = new ShellContextMenuService();
    private readonly ListViewRestoreService _listViewRestore = new();
    private readonly StatusSummaryService _statusSummary;
    private StatusSummaryCoordinator _statusSummaryCoordinator = null!;
    private ViewModeApplier _viewModeApplier = null!;
    private BreadcrumbPathBarController _breadcrumbPathBar = null!;
    private ViewModeController _viewModeController = null!;
    private NavigationController _navigationController = null!;
    private TabOperationsService _tabOperations = null!;
    private TabContextMenuController _tabContextMenus = null!;
    private FolderWatchTabTracker _folderWatchTabTracker = null!;
    private FileListHitTestService _fileListHitTest = null!;
    private FileListInputController _fileListInput = null!;
    private ColumnLayoutService _columnLayout = null!;
    private SelectionInteractionController _selectionInteraction = null!;
    private ScrollBehaviorService _scrollBehavior = null!;
    private readonly FileService _fileService;
    private readonly UndoService _undoService;
    private readonly WorkspaceSessionFactory _workspaceSessionFactory;
    private FolderPaneController _folderPaneController = null!;
    private WorkspacePaneUiController _workspacePaneUiController = null!;
    private readonly WorkspaceController _workspaceController;
    private readonly ObservableCollection<WorkspaceSession> _workspaceSessions = [];
    private readonly ObservableCollection<MainTabItem> _mainTabs = [];
    // _primaryPaneTabs is the backing tabs collection for the compatibility _primaryPaneGroup (single pane mode).
    // In single pane mode, _workspaceTabSync populates it with the active tab of the current session.
    private readonly ObservableCollection<FolderTab> _primaryPaneTabs = [];
    private readonly ObservableCollection<FolderPane> _workspaceDisplayPanes = [];
    private readonly ObservableCollection<PathBreadcrumbSegment> _normalPaneBreadcrumbSegments = [];
    private readonly WorkspaceSessionFolderTabSync _workspaceTabSync;
    private readonly WorkspaceLocalStateCoordinator _workspaceLocalState;
    private readonly ObservableCollection<WorkspacePaneGroup> _workspacePaneGroups = [];
    // _primaryPaneGroup is a compatibility fallback FolderPane (WorkspacePaneGroup) for normal single-pane mode.
    private readonly WorkspacePaneGroup _primaryPaneGroup;
    private WorkspaceSession _activeWorkspaceSession = null!;
    private WorkspacePaneGroup _activeWorkspacePaneGroup;
    private readonly WorkspaceService _workspaceService = new();
    private readonly bool _autoPerfRun;
    private bool _autoPerfStarted;
    private int _scrollTraceCount;
    private int _collectionChangedCount;
    private int _collectionAddCount;
    private int _collectionResetCount;
    private int _collectionRemoveCount;
    private int _itemsViewRefreshCount;
    private int _sortApplyCount;
    private int _sortClearCount;
    private int _filterPredicateCount;
    private int _fileEntryPropertyChangedCount;
    private int _fileEntryIconPropertyChangedCount;
    private int _selectionUserVersion;
    private int _scrollUserVersion;
    private readonly Dictionary<string, int> _itemsViewRefreshReasons = [];
    private readonly Dictionary<string, int> _fileEntryPropertyChangedNames = [];
#if DEBUG
    private int _selectionChangedCount;
    private int _scrollChangedCount;
    private int _requestBringIntoViewCount;
    private int _findScrollViewerCount;
#endif
    private CancellationTokenSource? _filterCancellation;
    private bool _isAltNavigationModifierDown;
    private bool _isRestoringTabState;
    private bool _isSwitchingTabs;
    private bool _isSwitchingWorkspacePane;
    private bool _suppressSelectionStatusUpdates;
    private bool _itemsFilterEnabled;
    private bool _isMutatingItemsForLoad;
    private bool _isFileOperationInProgress;
    private readonly FileWatcherRefreshCoordinator _fileWatcherRefreshCoordinator = new();
    private bool _driveListRefreshPending;
    private bool _driveListRefreshRunning;
    private bool _isSyncingPaneFilter;
    private bool _suppressWorkspaceSelectionSync;
    private bool _suppressWorkspaceScrollSync;
    private Point? _tabDragStartPoint;
    private WorkspaceSession? _draggedTab;
    private WorkspaceSession? _pendingWorkspaceRenameSession;
    private Point? _pendingWorkspaceRenamePoint;
    private int _workspaceRenameClickGeneration;
    private Point? _subTabDragStartPoint;
    private FolderPane? _draggedSubTabPane;
    private FolderTab? _draggedSubTab;
    private FolderPane? _workspaceSubTabClosePaneBeforeClick;
    private FolderTab? _workspaceSubTabCloseActiveTabBeforeClick;
    private Point? _breadcrumbDragStartPoint;
    private Point? _fileDragStartPoint;
    private FileEntry? _fileDragStartEntry;
    private FolderPane? _fileDragStartPane;
    private ListView? _fileDragStartListView;
    private Point? _rangeSelectionStartPoint;
    private bool _rangeSelectionStartAdditive;
    // 最後に追加・明示クリックした項目をアンカーとする (Selection Anchor)
    private FileEntry? _workspaceSelectionAnchorEntry;
    private Point? _workspacePendingRangeSelectionStartPoint;
    private FileEntry? _workspacePendingRangeSelectionClickEntry;
    private bool _workspacePendingRangeSelectionStartAdditive;
    private ListView? _workspacePendingRangeSelectionListView;
    private FolderPane? _workspacePendingRangeSelectionPane;
    private FolderPane? _workspaceRangeSelectionPane;
    private FolderPane? _lastInteractedWorkspaceDisplayPane;
    private ListView? _workspaceRangeSelectionListView;
    private Point? _workspaceRangeSelectionStartPoint;
    private bool _workspaceRangeSelectionMoved;
    private bool _workspaceRangeSelectionAdditive;
    private readonly HashSet<FileEntry> _workspaceRangeSelectionBase = [];
    private WorkspaceRangeSelectionAdorner? _workspaceRangeSelectionAdorner;
    private AdornerLayer? _workspaceRangeSelectionAdornerLayer;
    private FileEntry? _pendingSingleSelectionClickEntry;
    private Point? _pendingSingleSelectionClickPoint;
    private FolderPane? _pendingSingleSelectionClickPane;
    private ListView? _pendingSingleSelectionClickListView;
    private FileEntry? _activeRenameEntry;
    private FolderPane? _activeRenamePane;
    private TextBox? _activeRenameTextBox;
    private bool _activeRenameTextBoxMouseDown;
    private ListViewItem? _fileDropTargetItem;
    private readonly DispatcherTimer _fileTabHoverTimer = new() { Interval = FileTabHoverDelay };
    private FolderTab? _fileTabHoverTarget;
    private readonly DispatcherTimer _subTabHoverTimer = new() { Interval = FileTabHoverDelay };
    private FolderPane? _subTabHoverPane;
    private FolderTab? _subTabHoverTarget;
    private readonly DispatcherTimer _mainTabHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private WorkspaceSession? _mainTabHoverTarget;
    private ClosedWorkspaceSessionState? _lastClosedWorkspaceSession;
    private ClosedSubTabState? _lastClosedSubTab;
    private LastClosedKind _lastClosedKind = LastClosedKind.None;
    private string? _itemsOwnerStateId;
    private bool _isFileDragInProgress;
    private readonly FileListLoadController _loadController = new();

    private bool _isLoading
    {
        get => _loadController.IsLoading;
        set => _loadController.IsLoading = value;
    }
    private string? _currentLoadPath
    {
        get => _loadController.CurrentLoadPath;
        set => _loadController.CurrentLoadPath = value;
    }
    private string? _loadingStateId
    {
        get => _loadController.LoadingStateId;
        set => _loadController.LoadingStateId = value;
    }
    private string? _activeStateId
    {
        get => _loadController.ActiveStateId;
        set => _loadController.ActiveStateId = value;
    }
    private int _loadGeneration
    {
        get => _loadController.LoadGeneration;
        set => _loadController.LoadGeneration = value;
    }
    private string? _lastLoadRequestPath
    {
        get => _loadController.LastLoadRequestPath;
        set => _loadController.LastLoadRequestPath = value;
    }
    private int _lastLoadRequestId
    {
        get => _loadController.LastLoadRequestId;
        set => _loadController.LastLoadRequestId = value;
    }
    private CancellationTokenSource? _loadCancellation
    {
        get => _loadController.LoadCancellation;
        set => _loadController.LoadCancellation = value;
    }
    private bool _firstBatchDisplayed => _loadController.FirstDisplayMilliseconds > 0;
    private long _totalBindMilliseconds => _loadController.TotalBindMilliseconds;
    private long _maxBindMilliseconds => _loadController.MaxBindMilliseconds;
    private long _firstDisplayMilliseconds => _loadController.FirstDisplayMilliseconds;
    private int _bindBatchCount => _loadController.BindBatchCount;
    private int _addRangeCallCount => _loadController.AddRangeCallCount;
    private int _addRangeItemCount => _loadController.AddRangeItemCount;
    private int _diagnosticLoadId => _loadController.DiagnosticLoadId;
    private PendingFileOperation? _pendingFileOperation;
    private uint _internalClipboardSequence;
    private Dictionary<string, GridViewColumn> _columnsById = [];

    public ICollectionView ItemsView { get; }

    public ObservableCollection<FolderPane> WorkspaceDisplayPanes => _workspaceDisplayPanes;

    public ObservableCollection<WorkspaceSession> WorkspaceSessions => _workspaceSessions;

    public ObservableCollection<MainTabItem> MainTabs => _mainTabs;

    public ObservableCollection<PathBreadcrumbSegment> NormalPaneBreadcrumbSegments => _normalPaneBreadcrumbSegments;

    public static string WorkspacePaneColumnNameText => AppStrings.Get("ColumnName");

    public static string WorkspacePaneColumnSizeText => AppStrings.Get("ColumnSize");

    public static string WorkspacePaneColumnModifiedText => AppStrings.Get("ColumnModified");

    private WorkspaceSession? ActiveSession => GetSelectedWorkspaceSession() ?? _activeWorkspaceSession;

    private FolderTab? ActiveTab => GetActiveNavigationContext().Tab;

    private NavigationState? ActiveNavigation => ActiveTab?.Navigation;

    private WorkspaceTabState? ActiveTabState => ActiveTab?.State;


    private static FolderTab? GetSessionActiveTab(WorkspaceSession? session)
    {
        if (session is null)
        {
            return null;
        }

        var primaryPane = session.PaneGroups.FirstOrDefault(p => string.Equals(p.Id, "primary", StringComparison.OrdinalIgnoreCase)) ?? session.PaneGroups.FirstOrDefault();
        return primaryPane?.ActiveTab ?? primaryPane?.Tabs.FirstOrDefault();
    }

    private IEnumerable<FolderTab> EnumerateAllFolderTabs()
    {
        foreach (var session in _workspaceSessions)
        {
            foreach (var pane in session.PaneGroups)
            {
                foreach (var tab in pane.Tabs)
                {
                    yield return tab;
                }
            }
        }
    }

    private void ClearLastClosedStates()
    {
        _lastClosedWorkspaceSession = null;
        _lastClosedSubTab = null;
        _lastClosedKind = LastClosedKind.None;
    }

    private static FolderTab GetSessionRepresentativeTab(WorkspaceSession session)
    {
        var primaryPane = session.PaneGroups.FirstOrDefault(p =>
                string.Equals(p.Id, "primary", StringComparison.OrdinalIgnoreCase))
            ?? session.PaneGroups.FirstOrDefault();

        return session.ActivePaneGroup?.ActiveTab
            ?? primaryPane?.ActiveTab
            ?? primaryPane?.Tabs.FirstOrDefault()
            ?? new FolderTab(
                session.RootPath,
                viewMode: AppSettings.NormalizeDisplayMode(session.Workspace?.RootViewMode ?? FileDisplayMode.Details));
    }

    public static string GetCrashContextSnapshot()
    {
        lock (CrashContextGate)
        {
            return _crashContextSnapshot;
        }
    }

    public MainWindow(IReadOnlyList<SessionTabState> startTabs, int selectedTabIndex, SettingsService settingsService, SessionStateService sessionStateService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _sessionStateService = sessionStateService;
        _userCommandService = new UserCommandService();
        _userCommandService.Load();

        var loadedSession = _sessionStateService.Load();
        _sessionFolderColumnWidths = loadedSession.FolderColumnWidths ?? new(StringComparer.OrdinalIgnoreCase);
        _sessionColumnWidths = loadedSession.ColumnWidths ?? new(StringComparer.OrdinalIgnoreCase);

        if (loadedSession.WindowWidth.HasValue && loadedSession.WindowHeight.HasValue)
        {
            this.Left = loadedSession.WindowLeft ?? 100;
            this.Top = loadedSession.WindowTop ?? 100;
            this.Width = loadedSession.WindowWidth.Value;
            this.Height = loadedSession.WindowHeight.Value;

            // Screen boundary check to prevent loading off-screen
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            if (this.Left < virtualLeft || this.Left > virtualLeft + virtualWidth - 50 ||
                this.Top < virtualTop || this.Top > virtualTop + virtualHeight - 50)
            {
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            if (System.Enum.TryParse<WindowState>(loadedSession.WindowState, out var stateVal))
            {
                this.WindowState = stateVal;
            }
        }

        _workspaceController = new WorkspaceController();
        _workspaceTabSync = new WorkspaceSessionFolderTabSync(_primaryPaneTabs);
        _workspaceLocalState = new WorkspaceLocalStateCoordinator(
            _workspaceTabSync,
            _performanceLogger,
            Dispatcher,
            WorkspaceLocalStateSaveDelay,
            () => _activeWorkspaceSession,
            () => TabsControl.SelectedIndex,
            SaveSessionState);
        _workspaceSessionFactory = new WorkspaceSessionFactory(NormalizeSortColumn);
        _primaryPaneGroup = new WorkspacePaneGroup("primary", _primaryPaneTabs);
        _activeWorkspacePaneGroup = _primaryPaneGroup;
        _statusSummary = new StatusSummaryService(_text);
        _statusSummaryCoordinator = new StatusSummaryCoordinator(
            _statusSummary,
            _performanceLogger,
            SetNormalStatusText,
            ShowDisconnectedStatus,
            UpdateCrashContextSnapshot);
        var themeError = ThemeManager.Apply(this, _settingsService.Settings.Theme, _settingsService.Settings.CustomThemeName);
        if (themeError != null)
        {
            StatusText.Text = themeError;
        }
        _deviceChangeService = new DeviceChangeService(this);
        ApplyDevListPerfOptions();
        _viewModeApplier = new ViewModeApplier(
            ItemsList,
            DetailsGridView,
            this,
            () => _devListPerfOptions.DiagnosticRowStyleEnabled);
        _renameFocus = new RenameFocusService(ItemsList);
        ApplyFontSettings();
        ApplyLocalizedText();
        ApplyPreviewPanePlacement(isVisible: false);
        InitializeColumns();
        _columnLayout = new ColumnLayoutService(
            _settingsService.Settings,
            DetailsGridView,
            _columnsById,
            _sessionFolderColumnWidths,
            _sessionColumnWidths);
        _selectionInteraction = new SelectionInteractionController(
            ItemsList,
            RangeSelectionOverlay,
            RangeSelectionRectangle,
            () => _isLoading,
            ClearFileDragStart,
            UpdateSelectedItemStatus);
        _navigationController = new NavigationController(this);
        _breadcrumbPathBar = new BreadcrumbPathBarController(
            BreadcrumbPanel,
            BreadcrumbScroller,
            PathBox,
            _text,
            () => ActiveNavigation?.CurrentPath ?? "",
            NavigateFromBreadcrumbAsync,
            NavigateFromPathBoxAsync,
            FocusAndSelectTextBox,
            rawPath => SetNormalStatusText(_text.Format("PathNotFound", rawPath)),
            ConfigureBreadcrumbDragButton);
        _viewModeController = new ViewModeController(
            _settingsService,
            _text,
            _performanceLogger,
            () => ActiveTabState?.ViewMode ?? _settingsService.Settings.DisplayMode,
            mode =>
            {
                if (ActiveTabState is not { } state)
                {
                    return;
                }

                state.ViewMode = AppSettings.NormalizeDisplayMode(mode);
                _workspaceLocalState.MarkDirty("view-mode");
            },
            () => GetSelectedEntries().Select(entry => entry.FullPath).ToList(),
            GetCurrentVerticalOffset,
            SaveColumnWidths,
            () => ApplyDisplayMode(ActiveTab),
            ApplyColumnSettings,
            RestoreSelection,
            RestoreScrollOffsetAsync,
            () => _items.Count);
        _tabOperations = new TabOperationsService(
            EnumerateAllFolderTabs,
            () => _settingsService.Settings.DisplayMode,
            NormalizeSortColumn);
        _tabContextMenus = new TabContextMenuController(
            _text,
            _performanceLogger,
            GetSessionActiveTab,
            () => CreateNewMainWindowTabAsync(),
            (path, sourceTab) => CreateNewMainWindowTabAsync(path, sourceTab),
            RestoreLastClosedTabAsync,
            CloseSessionAsync,
            CloseOtherSessionsAsync,
            CloseSessionsToRightAsync,
            BeginWorkspaceRename,
            ToggleWorkspaceLock,
            OpenTabInExplorer,
            () => _lastClosedKind != LastClosedKind.None);
        _folderWatchTabTracker = new FolderWatchTabTracker(
            EnumerateAllFolderTabs,
            _folderWatchService,
            _performanceLogger);
        _fileListHitTest = new FileListHitTestService(
            ItemsList,
            GetCurrentDisplayMode,
            GetVisibleColumnsWidth);
        _fileListInput = new FileListInputController(this);
        _scrollBehavior = new ScrollBehaviorService(
            ItemsList,
            AutoScrollOverlay,
            AutoScrollMarker,
            () => _selectionInteraction.IsSelecting,
            () => _isLoading,
            FindItemsScrollViewer);
        _fileTabHoverTimer.Tick += FileTabHoverTimer_Tick;
        _subTabHoverTimer.Tick += SubTabHoverTimer_Tick;
        _mainTabHoverTimer.Tick += MainTabHoverTimer_Tick;
        _items.CollectionChanged += Items_CollectionChanged;
        _folderWatchService.ChangeObserved += FolderWatchService_ChangeObserved;
        _folderWatchService.FileMetadataChanged += FolderWatchService_FileMetadataChanged;
        _folderWatchService.Changed += FolderWatchService_Changed;
        _folderWatchService.WatchError += FolderWatchService_WatchError;
        _deviceChangeService.DrivesChanged += DeviceChangeService_DrivesChanged;

        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Window_PreviewKeyDown), true);
        AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(Window_PreviewKeyUp), true);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Window_PreviewMouseDown), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Window_PreviewMouseMoveDuringRangeSelection), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Window_PreviewMouseUpDuringRangeSelection), true);
        ItemsList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(GridViewColumnHeader_Click));
        ItemsList.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(ItemsList_PreviewMouseWheelDuringRangeSelection), true);
        ItemsList.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(ItemsList_RequestBringIntoView), true);
        ItemsList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ItemsList_ScrollChanged), true);
        ItemsList.LostMouseCapture += ItemsList_LostMouseCapture;
        RangeSelectionOverlay.LostMouseCapture += RangeSelectionOverlay_LostMouseCapture;
        _primaryPaneTabs.CollectionChanged += Tabs_CollectionChanged;
        _workspaceSessions.CollectionChanged += WorkspaceSessions_CollectionChanged;

        _fileService = new FileService(new FileSystemEnumerator(() => _settingsService.Settings));
        _folderPaneController = new FolderPaneController(
            _workspaceDisplayPanes,
            _fileService,
            _specialLocationService,
            _driveAvailabilityService,
            _statusSummary,
            _text,
            _performanceLogger,
            _devListPerfOptions,
            () => _settingsService.Settings.SortFoldersFirst,
            ShouldLoadExtraColumns);
        _workspacePaneUiController = new WorkspacePaneUiController(
            _workspacePaneGroups,
            _folderPaneController,
            WorkspacePaneRail,
            WorkspaceSplitGrid,
            ItemsListHost);
        _undoService = new UndoService(_fileOperationService);
        var effectiveStartTabs = _devListPerfOptions.SessionRestoreEnabled
            ? startTabs
            : Array.Empty<SessionTabState>();
        _performanceLogger.Write($"session-restore enabled={_devListPerfOptions.SessionRestoreEnabled} requestedTabs={startTabs.Count} restoredTabs={effectiveStartTabs.Count}");

        var initialSessions = new List<WorkspaceSession>();
        foreach (var tabState in effectiveStartTabs)
        {
            if (CreateInitialSession(tabState) is { } session)
            {
                initialSessions.Add(session);
            }
        }

        if (initialSessions.Count == 0)
        {
            initialSessions.Add(_workspaceController.CreateSinglePaneSession(
                new FolderTab(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), viewMode: _settingsService.Settings.DisplayMode)));
        }

        var initialActiveSession = initialSessions[Math.Clamp(selectedTabIndex, 0, initialSessions.Count - 1)];
        _activeWorkspaceSession = initialActiveSession;

        foreach (var s in initialSessions)
        {
            _workspaceSessions.Add(s);
        }

        UpdateActiveWorkspaceSessionUi(initialActiveSession);
        _workspaceTabSync.ApplyToDisplay(_activeWorkspaceSession);
        _primaryPaneGroup.SelectedTabIndex = Math.Clamp(_activeWorkspaceSession.SelectedTabIndex, 0, Math.Max(0, _primaryPaneTabs.Count - 1));
        _activeWorkspaceSession.ActivePaneGroup ??= _primaryPaneGroup;

        _autoPerfRun = string.Equals(Environment.GetEnvironmentVariable("FILEKAKARI_PERF_AUTORUN"), "1", StringComparison.Ordinal);
        ItemsView = CollectionViewSource.GetDefaultView(_items);
        DataContext = this;
        WorkspacePaneList.ItemsSource = _workspacePaneGroups;
        TabsControl.ItemsSource = _mainTabs;
        SelectWorkspaceSession(_activeWorkspaceSession);
        ApplyDisplayMode();
        ApplyColumnSettings();
        if (ActiveTab is { } startupTab)
        {
            ApplyTabSort(startupTab);
        }
        UpdateCrashContextSnapshot("startup");
        Closing += (_, _) =>
        {
            CancelPreviewLoad();
            _deviceChangeService.Dispose();
            _folderWatchService.Dispose();
            _scrollBehavior.StopAutoScroll();
            _statusSummaryCoordinator.Dispose();
            _workspaceLocalState.Stop();
            if (_activeWorkspaceSession is not null)
            {
                if (_activeWorkspaceSession.IsWorkspace)
                {
                    foreach (var pane in _activeWorkspaceSession.PaneGroups)
                    {
                        SaveWorkspacePaneColumnWidths(pane);
                    }
                }
                else
                {
                    SaveColumnWidths();
                }
            }
            _settingsService.SaveCurrent();
            SaveSessionState();
            _workspaceLocalState.SaveActiveLocalState();
        };

        Loaded += async (_, _) =>
        {
            await RestoreWorkspaceTabAsync(_activeWorkspaceSession);
            UpdateNavigationButtons();
            LogMemoryMetrics("startup");
        };
    }

    private void ApplyFontSettings()
    {
        var settings = _settingsService.Settings;
        settings.EnsureDefaults();
        var fontFamily = new FontFamily(ResolveFontFamilyName(settings.FontFamily));
        var fontSize = settings.FontSize;

        ItemsList.FontFamily = fontFamily;
        ItemsList.FontSize = fontSize;
        PathBox.FontFamily = fontFamily;
        PathBox.FontSize = fontSize;
        FilterBox.FontFamily = fontFamily;
        FilterBox.FontSize = fontSize;
        NormalPanePathBox.FontFamily = fontFamily;
        NormalPanePathBox.FontSize = fontSize;
        NormalPaneFilterBox.FontFamily = fontFamily;
        NormalPaneFilterBox.FontSize = fontSize;
        StatusText.FontFamily = fontFamily;
        StatusText.FontSize = fontSize;

        ApplyDisplayMode();
    }

    private static string ResolveFontFamilyName(string fontFamily)
    {
        var requested = string.IsNullOrWhiteSpace(fontFamily)
            ? AppSettings.DefaultFontFamily
            : fontFamily.Trim();
        return IsInstalledFontFamily(requested) ? requested : AppSettings.DefaultFontFamily;
    }

    private static bool IsInstalledFontFamily(string fontFamily)
    {
        return Fonts.SystemFontFamilies.Any(family =>
            string.Equals(family.Source, fontFamily, StringComparison.OrdinalIgnoreCase)
            || family.FamilyNames.Values.Any(name => string.Equals(name, fontFamily, StringComparison.OrdinalIgnoreCase)));
    }



    private void UpdateCrashContextSnapshot(string reason)
    {
        var selectedStateId = ActiveTab?.State.Id ?? "";
        var snapshot = $"reason={reason} stateId={selectedStateId} activeStateId={_activeStateId ?? ""} loadingStateId={_loadingStateId ?? ""} selectedIndex={TabsControl.SelectedIndex} tabCount={_workspaceSessions.Count}";
        lock (CrashContextGate)
        {
            _crashContextSnapshot = snapshot;
        }
    }

    private void LogException(string source, Exception exception, WorkspaceTabState? state = null)
    {
        UpdateCrashContextSnapshot(source);
        var stackFirst = exception.StackTrace?
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? "";
        _performanceLogger.Write($"exception source={source} type={exception.GetType().FullName} message=\"{exception.Message}\" stackFirst=\"{stackFirst}\" stateId={state?.Id ?? ""} activeStateId={_activeStateId ?? ""} loadingStateId={_loadingStateId ?? ""} selectedIndex={TabsControl.SelectedIndex} tabCount={_workspaceSessions.Count}");
    }

    private bool TryBeginLoadForState(WorkspaceTabState targetState, string path)
    {
        UpdateCrashContextSnapshot("load-begin");
        return _loadController.TryBeginLoadForState(targetState, path, _performanceLogger.Write);
    }

    private bool IsLoadCurrentForState(int loadId, WorkspaceTabState targetState)
    {
        return _loadController.IsLoadCurrentForState(loadId, targetState);
    }

    private void CancelLoadForDifferentState(WorkspaceTabState targetState, string reason)
    {
        if (_loadController.CancelLoadForDifferentState(targetState, reason, _performanceLogger.Write))
        {
            LoadingProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelActiveLoadForWorkspaceSwitch(WorkspaceSession targetSession, string reason)
    {
        if (GetSessionActiveTab(targetSession)?.State is { } targetState)
        {
            CancelLoadForDifferentState(targetState, reason);
        }
    }

    private async Task LoadFolderAsync(
        string path,
        ListViewRestoreState? restoreState = null,
        FolderTab? targetTab = null,
        FileListRestorePolicy policy = FileListRestorePolicy.ExactRestore)
    {
        var loadTab = targetTab ?? ActiveTab;
        if (loadTab is null)
        {
            _performanceLogger.Write($"load-skip reason=no-active-tab path=\"{path}\"");
            return;
        }

        var loadState = loadTab.State;
        var sameAsActiveLoad = _isLoading && string.Equals(path, _currentLoadPath, StringComparison.OrdinalIgnoreCase);
        var sameAsPreviousRequest = string.Equals(path, _lastLoadRequestPath, StringComparison.OrdinalIgnoreCase);
        _performanceLogger.Write($"load-request nextId={_loadGeneration + 1} stateId={loadState.Id} activeStateId={_activeStateId ?? ""} path=\"{path}\" isLoading={_isLoading} loadingStateId={_loadingStateId ?? ""} currentPath=\"{_currentLoadPath}\" sameAsActiveLoad={sameAsActiveLoad} previousId={_lastLoadRequestId} sameAsPreviousRequest={sameAsPreviousRequest} restoringTab={_isRestoringTabState} switchingTabs={_isSwitchingTabs}");
        _lastLoadRequestPath = path;
        _lastLoadRequestId = _loadGeneration + 1;

        if (SpecialLocationService.IsSpecialUri(path))
        {
            if (!TryBeginLoadForState(loadState, path))
            {
                return;
            }

            loadTab.ClearDisconnected();
            await LoadThisPcAsync(path, loadTab);
            return;
        }

        var availability = await _driveAvailabilityService.CheckAsync(path);
        if (!availability.IsAvailable)
        {
            MarkActiveLocationDisconnected(availability, "load");
            return;
        }

        if (!await _driveAvailabilityService.DirectoryExistsAsync(path))
        {
            return;
        }

        if (!TryBeginLoadForState(loadState, path))
        {
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        var loadId = _loadController.IncrementLoadGeneration();
        var stopwatch = Stopwatch.StartNew();
        _loadController.ResetBatchStats(loadId);
        ResetUpdateDiagnostics(loadId);

        SetFolderColumnHeaders();
        _currentLoadPath = path;
        loadTab.Navigation.SetCurrentPath(path);
        loadState.CurrentPath = path;
        loadTab.ClearDisconnected();
        loadTab.RefreshHeader();
        _activeWorkspacePaneGroup.RefreshDisplay();
        UpdateFolderWatchForOpenTabs();
        UpdatePathDisplay(loadTab.Navigation.CurrentPath);
        LoadingProgress.Visibility = Visibility.Visible;
        _isLoading = true;
        CancelPendingRenameClick();
        _activeRenameEntry = null;
        _activeRenamePane = null;
        _scrollBehavior.StopAutoScroll();
        if (_selectionInteraction.IsSelecting)
        {
            _selectionInteraction.Cancel();
        }

        _statusSummaryCoordinator.StatusMessagePrefix = null;
        SetNormalStatusText(_text.Get("LoadingZero"));
        UpdateWindowTitle();
        MutateItemsForLoad(() => _items.Clear());
        _itemsOwnerStateId = loadState.Id;
        var sortColumn = _devListPerfOptions.SortEnabled ? loadState.SortColumn : "__none";
        var sortAscending = loadState.SortAscending;
        var sortFoldersFirst = _settingsService.Settings.SortFoldersFirst;
        var extraColumnsEnabled = ShouldLoadExtraColumns(sortColumn);
        var enumerateMilliseconds = 0L;
        var iconLoadMilliseconds = 0L;
        var iconLoadCount = 0;
        var shellIconStarted = false;
        ClearItemsSort();
        _performanceLogger.Write($"load-start id={loadId} stateId={loadState.Id} paneId={loadState.PaneId} path=\"{path}\" viewMode={loadState.ViewMode} sort={sortColumn}:{sortAscending} sortEnabled={_devListPerfOptions.SortEnabled} shellIcons={_devListPerfOptions.ShellIconsEnabled} statusAggregation={_devListPerfOptions.StatusAggregationEnabled} extraColumns={extraColumnsEnabled} initialBatch={InitialBatchSize} subsequentBatch={SubsequentBatchSize}");

        try
        {
            await Task.Run(async () =>
            {
                var batchSize = InitialBatchSize;
                var batch = new List<FileEntry>(batchSize);
                var enumerateStopwatch = Stopwatch.StartNew();

                foreach (var entry in _fileService.Enumerate(
                    path,
                    sortColumn,
                    sortAscending,
                    sortFoldersFirst,
                    extraColumnsEnabled,
                    token))
                {
                    token.ThrowIfCancellationRequested();
                    enumerateMilliseconds = enumerateStopwatch.ElapsedMilliseconds;
                    if (_devListPerfOptions.ShellIconsEnabled)
                    {
                        if (!shellIconStarted)
                        {
                            shellIconStarted = true;
                            _performanceLogger.Write($"shell-icons-start id={loadId} elapsedMs={stopwatch.ElapsedMilliseconds} path=\"{path}\"");
                        }

                        var iconStopwatch = Stopwatch.StartNew();
                        await entry.LoadIconAsync();
                        iconStopwatch.Stop();
                        iconLoadMilliseconds += iconStopwatch.ElapsedMilliseconds;
                        iconLoadCount++;
                    }

                    batch.Add(entry);

                    if (batch.Count >= batchSize)
                    {
                        await AddBatchAsync(batch, token, loadId, loadState, stopwatch);
                        batchSize = SubsequentBatchSize;
                        batch = new List<FileEntry>(batchSize);
                    }
                }

                if (batch.Count > 0)
                {
                    await AddBatchAsync(batch, token, loadId, loadState, stopwatch);
                }

                enumerateMilliseconds = enumerateStopwatch.ElapsedMilliseconds;
                if (shellIconStarted)
                {
                    _performanceLogger.Write($"shell-icons-complete id={loadId} count={iconLoadCount} iconTotalMs={iconLoadMilliseconds} elapsedMs={stopwatch.ElapsedMilliseconds} path=\"{path}\"");
                }
            }, token);

            if (!IsLoadCurrentForState(loadId, loadState))
            {
                stopwatch.Stop();
                _performanceLogger.Write($"load-complete-discard reason=state-not-active id={loadId} stateId={loadState.Id} activeStateId={_activeStateId ?? ""} loadingStateId={_loadingStateId ?? ""} path=\"{path}\" elapsedMs={stopwatch.ElapsedMilliseconds}");
                return;
            }

            if (loadId == _loadGeneration)
            {
                ApplyTabSort(loadState);
                stopwatch.Stop();
                _statusSummaryCoordinator.StatusMessagePrefix = _text.Format("ItemsLoaded", _items.Count, stopwatch.ElapsedMilliseconds);
                RefreshCurrentFolderSummary();
                UpdateSelectedItemStatus();
                _performanceLogger.Write(
                    $"load-complete id={loadId} stateId={loadState.Id} paneId={loadState.PaneId} count={_items.Count} totalMs={stopwatch.ElapsedMilliseconds} firstDisplayMs={_firstDisplayMilliseconds} afterFirstDisplayMs={Math.Max(0, stopwatch.ElapsedMilliseconds - _firstDisplayMilliseconds)} enumerateMs={enumerateMilliseconds} iconCount={iconLoadCount} iconTotalMs={iconLoadMilliseconds} bindBatches={_bindBatchCount} bindTotalMs={_totalBindMilliseconds} bindMaxMs={_maxBindMilliseconds} viewMode={loadState.ViewMode} virtualization={GetVirtualizationStatus()} memory={GetProcessMemoryStatus()} sort={sortColumn}:{sortAscending} updates={GetUpdateDiagnosticsStatus()} status={GetStatusDiagnosticsStatus()}");

                var restored = await RestoreListViewStateAsync(restoreState, loadTab, loadId, policy);
                if (!restored && _items.Count > 0)
                {
                    _performanceLogger.Write($"load-default-selection-suppressed id={loadId} count={_items.Count}");
                }

                var cacheMemoryBefore = GetProcessWorkingSetBytes();
                loadTab.StoreItems(loadTab.Navigation.CurrentPath, _items.ToList());
                _itemsOwnerStateId = loadState.Id;
                loadTab.ClearPendingExternalChange();
                ClearPendingFolderWatchRefresh(loadTab.Navigation.CurrentPath);
                UpdateFolderWatchForOpenTabs();
                var cacheMemoryAfter = GetProcessWorkingSetBytes();
                _performanceLogger.Write($"tab-cache-load-store id={loadId} stateId={loadState.Id} paneId={loadState.PaneId} path=\"{loadState.CurrentPath}\" items={_items.Count} memoryDeltaMb={(cacheMemoryAfter - cacheMemoryBefore) / 1024d / 1024d:N1} memory={GetProcessMemoryStatus()}");

                if (_autoPerfRun && !_autoPerfStarted)
                {
                    _autoPerfStarted = true;
                    await RunAutoPerfChecksAsync(loadId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _performanceLogger.Write($"load-canceled id={loadId} elapsedMs={stopwatch.ElapsedMilliseconds} path=\"{path}\"");
        }
        catch (Exception ex)
        {
            if (IsLoadCurrentForState(loadId, loadState))
            {
                stopwatch.Stop();
                SetNormalStatusText(ex.Message);
                _performanceLogger.Write($"load-failed id={loadId} elapsedMs={stopwatch.ElapsedMilliseconds} error=\"{ex.Message}\"");
                MessageBox.Show(this, ex.Message, _text.Get("OpenFolderFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (loadId == _loadGeneration && string.Equals(_loadingStateId, loadState.Id, StringComparison.Ordinal))
            {
                _isLoading = false;
                _currentLoadPath = null;
                _loadingStateId = null;
                LoadingProgress.Visibility = Visibility.Collapsed;
                QueuePostLoadCleanup();
                UpdateWorkspaceButtonState();
            }
        }
    }

    private async Task LoadThisPcAsync(string path, FolderTab loadTab)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        var loadId = _loadController.IncrementLoadGeneration();
        var stopwatch = Stopwatch.StartNew();
        _loadController.ResetBatchStats(loadId);
        ResetUpdateDiagnostics(loadId);

        SetThisPcColumnHeaders();
        _currentLoadPath = path;
        loadTab.Navigation.SetCurrentPath(path);
        loadTab.State.CurrentPath = path;
        loadTab.RefreshHeader();
        _activeWorkspacePaneGroup.RefreshDisplay();
        UpdateFolderWatchForOpenTabs();
        UpdatePathDisplay(loadTab.Navigation.CurrentPath);
        LoadingProgress.Visibility = Visibility.Visible;
        _isLoading = true;
        CancelPendingRenameClick();
        _scrollBehavior.StopAutoScroll();
        if (_selectionInteraction.IsSelecting)
        {
            _selectionInteraction.Cancel();
        }

        _statusSummaryCoordinator.StatusMessagePrefix = null;
        SetNormalStatusText(_text.Get("LoadingZero"));
        UpdateWindowTitle();
        MutateItemsForLoad(() => _items.Clear());
        var loadState = loadTab.State;
        _itemsOwnerStateId = loadState.Id;
        ClearItemsSort();
        _performanceLogger.Write($"load-start id={loadId} path=\"{path}\" view=this-pc statusAggregation={_devListPerfOptions.StatusAggregationEnabled}");

        try
        {
            var drives = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return _specialLocationService.EnumerateThisPc().ToList();
            }, token);

            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoadCurrentForState(loadId, loadState) || token.IsCancellationRequested)
                {
                    return;
                }

                MutateItemsForLoad(() => _items.AddRange(drives));
            }, DispatcherPriority.Background).Task.WaitAsync(token);

            if (!IsLoadCurrentForState(loadId, loadState))
            {
                stopwatch.Stop();
                _performanceLogger.Write($"load-complete-discard reason=state-not-active id={loadId} stateId={loadState.Id} activeStateId={_activeStateId ?? ""} loadingStateId={_loadingStateId ?? ""} path=\"{path}\" elapsedMs={stopwatch.ElapsedMilliseconds} view=this-pc");
                return;
            }

            if (loadId == _loadGeneration)
            {
                ApplyTabSort(loadState);
                stopwatch.Stop();
                _statusSummaryCoordinator.StatusMessagePrefix = _text.Format("DrivesLoaded", _items.Count, stopwatch.ElapsedMilliseconds);
                RefreshCurrentFolderSummary();
                UpdateSelectedItemStatus();
                _performanceLogger.Write($"load-complete id={loadId} stateId={loadState.Id} paneId={loadState.PaneId} count={_items.Count} totalMs={stopwatch.ElapsedMilliseconds} firstDisplayMs={_firstDisplayMilliseconds} afterFirstDisplayMs={Math.Max(0, stopwatch.ElapsedMilliseconds - _firstDisplayMilliseconds)} view=this-pc memory={GetProcessMemoryStatus()} updates={GetUpdateDiagnosticsStatus()} status={GetStatusDiagnosticsStatus()}");

                if (_items.Count > 0)
                {
                    _performanceLogger.Write($"load-default-selection-suppressed id={loadId} count={_items.Count} view=this-pc");
                }

                var cacheMemoryBefore = GetProcessWorkingSetBytes();
                loadTab.StoreItems(loadTab.Navigation.CurrentPath, _items.ToList());
                _itemsOwnerStateId = loadState.Id;
                loadTab.ClearPendingExternalChange();
                ClearPendingFolderWatchRefresh(loadTab.Navigation.CurrentPath);
                var cacheMemoryAfter = GetProcessWorkingSetBytes();
                _performanceLogger.Write($"tab-cache-load-store id={loadId} stateId={loadState.Id} paneId={loadState.PaneId} path=\"{loadState.CurrentPath}\" items={_items.Count} memoryDeltaMb={(cacheMemoryAfter - cacheMemoryBefore) / 1024d / 1024d:N1} memory={GetProcessMemoryStatus()}");
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _performanceLogger.Write($"load-canceled id={loadId} elapsedMs={stopwatch.ElapsedMilliseconds} path=\"{path}\"");
        }
        catch (Exception ex)
        {
            if (IsLoadCurrentForState(loadId, loadState))
            {
                stopwatch.Stop();
                SetNormalStatusText(ex.Message);
                _performanceLogger.Write($"load-failed id={loadId} elapsedMs={stopwatch.ElapsedMilliseconds} error=\"{ex.Message}\"");
            }
        }
        finally
        {
            if (loadId == _loadGeneration && string.Equals(_loadingStateId, loadState.Id, StringComparison.Ordinal))
            {
                _isLoading = false;
                _currentLoadPath = null;
                _loadingStateId = null;
                LoadingProgress.Visibility = Visibility.Collapsed;
                QueuePostLoadCleanup();
            }
        }
    }

    private void QueuePostLoadCleanup()
    {
        Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                UpdateSelectedItemStatus();
                await ProcessPendingDriveListRefreshAsync();
            }),
            DispatcherPriority.Background);
    }

    private async Task AddBatchAsync(
        List<FileEntry> batch,
        CancellationToken token,
        int loadId,
        WorkspaceTabState loadState,
        Stopwatch stopwatch)
    {
        var waitForFirstDisplay = false;
        var isFirstBatch = !_firstBatchDisplayed;
        var batchQueuedMilliseconds = stopwatch.ElapsedMilliseconds;
        var dispatcherPriority = isFirstBatch
            ? DispatcherPriority.Render
            : DispatcherPriority.Background;

        await Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoadCurrentForState(loadId, loadState) || token.IsCancellationRequested)
            {
                return;
            }

            var bindStopwatch = Stopwatch.StartNew();
            foreach (var entry in batch)
            {
                entry.PropertyChanged += FileEntry_PropertyChanged;
            }

            MutateItemsForLoad(() => _items.AddRange(batch));
            bindStopwatch.Stop();

            var bindResult = _loadController.RecordBatchBound(
                bindStopwatch.ElapsedMilliseconds,
                batch.Count,
                stopwatch.ElapsedMilliseconds);

            var statusStopwatch = Stopwatch.StartNew();
            SetNormalStatusText(_text.Format("LoadingProgress", _items.Count, stopwatch.ElapsedMilliseconds));
            statusStopwatch.Stop();
            _statusSummaryCoordinator.RecordLoadingProgressSummary(statusStopwatch.ElapsedMilliseconds);

            if (bindResult.IsFirstBatch)
            {
                waitForFirstDisplay = true;
                _performanceLogger.Write($"first-batch-bound id={loadId} items={_items.Count} batchItems={batch.Count} queuedMs={batchQueuedMilliseconds} boundMs={bindResult.FirstDisplayMilliseconds} dispatcherWaitMs={Math.Max(0, bindResult.FirstDisplayMilliseconds - batchQueuedMilliseconds)} priority={dispatcherPriority}");
            }
        }, dispatcherPriority).Task.WaitAsync(token);

        if (waitForFirstDisplay)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _performanceLogger.Write($"first-display id={loadId} items={_items.Count} elapsedMs={stopwatch.ElapsedMilliseconds} sinceBoundMs={Math.Max(0, stopwatch.ElapsedMilliseconds - _firstDisplayMilliseconds)} virtualization={GetVirtualizationStatus()} memory={GetProcessMemoryStatus()} updates={GetUpdateDiagnosticsStatus()}");
            }, DispatcherPriority.Render).Task.WaitAsync(token);
        }
    }

    private async void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await _fileListInput.HandleMouseDoubleClickAsync(e);
    }

    private async void ItemsList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        await _fileListInput.HandlePreviewMouseDownAsync(e);
    }

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        _fileListInput.HandlePreviewMouseMove(e);
    }

    private void MarkUserSelectionIntentDuringLoad(string reason)
    {
        if (!_isLoading || _listViewRestore.IsRestoring || _isMutatingItemsForLoad)
        {
            return;
        }

        _selectionUserVersion++;
        _performanceLogger.Write($"list-restore-selection-cancel-intent reason={reason} version={_selectionUserVersion} loadId={_diagnosticLoadId}");
    }

    private void MarkUserScrollIntentDuringLoad(string reason)
    {
        if (!_isLoading || _listViewRestore.IsRestoring || _isMutatingItemsForLoad)
        {
            return;
        }

        _scrollUserVersion++;
        _performanceLogger.Write($"list-restore-scroll-cancel-intent reason={reason} version={_scrollUserVersion} loadId={_diagnosticLoadId}");
    }

    private bool IsKeyboardFocusInsideItemsList()
    {
        return Keyboard.FocusedElement is DependencyObject focused
            && IsInsideItemsList(focused);
    }

    private static bool IsSelectionKey(Key key)
    {
        return key is Key.Up
            or Key.Down
            or Key.Left
            or Key.Right
            or Key.Home
            or Key.End
            or Key.PageUp
            or Key.PageDown
            or Key.Space
            or Key.A;
    }

    private static bool IsScrollKey(Key key)
    {
        return key is Key.Up
            or Key.Down
            or Key.Home
            or Key.End
            or Key.PageUp
            or Key.PageDown;
    }

    private void ItemsList_DragOver(object sender, DragEventArgs e)
    {
        if (GetMainTabDemotionTarget(e, ActiveSession, GetNormalFolderPane()) is not null)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        _fileListInput.HandleDragOver(e);
    }

    private void ItemsList_DragLeave(object sender, DragEventArgs e)
    {
        _fileListInput.HandleDragLeave(e);
    }

    private async void ItemsList_Drop(object sender, DragEventArgs e)
    {
        if (GetMainTabDemotionTarget(e, ActiveSession, GetNormalFolderPane()) is { } demotionTarget)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            await DemoteMainTabToSubTabAsync(
                demotionTarget.DraggedSession,
                demotionTarget.TargetPane,
                demotionTarget.TargetPane.Tabs.Count);
            return;
        }

        await _fileListInput.HandleDropAsync(e);
    }

    private async void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await _fileListInput.HandlePreviewMouseLeftButtonUpAsync(e);
    }

    private void ItemsList_PreviewMouseWheelDuringRangeSelection(object sender, MouseWheelEventArgs e)
    {
        MarkUserScrollIntentDuringLoad("wheel");
        _selectionInteraction.HandleMouseWheel(e);
    }

    private void ItemsList_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
#if DEBUG
        if (_diagnosticLoadId != 0)
        {
            _requestBringIntoViewCount++;
        }
#endif
        if (_listViewRestore.IsRestoring || _isMutatingItemsForLoad)
        {
            e.Handled = true;
            return;
        }

        _selectionInteraction.HandleRequestBringIntoView(e);
    }

    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
#if DEBUG
        if (_diagnosticLoadId != 0)
        {
            _scrollChangedCount++;
        }
#endif
        _selectionInteraction.HandleScrollChanged(e);
        if (GetNormalFolderPane() is { } pane)
        {
            pane.ScrollOffset = e.VerticalOffset;
        }
    }

    private void ItemsList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _selectionInteraction.HandleLostMouseCapture();
    }

    private void RangeSelectionOverlay_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _selectionInteraction.HandleLostMouseCapture();
    }

    private void Window_PreviewMouseMoveDuringRangeSelection(object sender, MouseEventArgs e)
    {
        if (_selectionInteraction.HandlePreviewMouseMove(e.GetPosition(ItemsList)))
        {
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseUpDuringRangeSelection(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle
            && _scrollBehavior.CompleteMiddleButtonPress(e.GetPosition(ItemsList)))
        {
            e.Handled = true;
            return;
        }

        if (!_selectionInteraction.HandlePreviewMouseUp(e.ChangedButton))
        {
            return;
        }

        e.Handled = true;
        _ = ProcessPendingFolderWatchRefreshAsync();
        _ = ProcessPendingDriveListRefreshAsync();
    }

    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (!TryCommitFileRenameOnExternalMouseDown(source))
        {
            CommitWorkspaceRenameOnExternalMouseDown(source);
        }

        TrackActiveRenameTextBoxMouseDown(source);

        if (e.ChangedButton == MouseButton.XButton1)
        {
            _scrollBehavior.StopAutoScroll();
            e.Handled = true;
            await NavigateBackAsync();
            return;
        }

        if (e.ChangedButton == MouseButton.XButton2)
        {
            _scrollBehavior.StopAutoScroll();
            e.Handled = true;
            await NavigateForwardAsync();
            return;
        }

        if (_scrollBehavior.IsAutoScrolling && !IsInsideItemsList(e.OriginalSource as DependencyObject))
        {
            _scrollBehavior.StopAutoScroll();
            _ = ProcessPendingFolderWatchRefreshAsync();
            _ = ProcessPendingDriveListRefreshAsync();
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focusedTextBox = Keyboard.FocusedElement as TextBox;
        var isRenameTextBoxFocused = focusedTextBox?.DataContext is FileEntry
            || GetWorkspaceSession(focusedTextBox?.DataContext) is not null;

        if (_scrollBehavior.IsAutoScrolling && e.Key == Key.Escape)
        {
            _scrollBehavior.StopAutoScroll();
            e.Handled = true;
            _ = ProcessPendingFolderWatchRefreshAsync();
            _ = ProcessPendingDriveListRefreshAsync();
            return;
        }

        if (_selectionInteraction.IsSelecting && e.Key == Key.Escape)
        {
            _selectionInteraction.Cancel();
            e.Handled = true;
            _ = ProcessPendingFolderWatchRefreshAsync();
            _ = ProcessPendingDriveListRefreshAsync();
            return;
        }

        if (isRenameTextBoxFocused)
        {
            return;
        }

        if (_isLoading && IsKeyboardFocusInsideItemsList())
        {
            if (IsSelectionKey(e.Key))
            {
                MarkUserSelectionIntentDuringLoad("key");
            }

            if (IsScrollKey(e.Key))
            {
                MarkUserScrollIntentDuringLoad("key");
            }
        }

        if (IsAltKey(e))
        {
            _isAltNavigationModifierDown = true;
        }

        LogPreviewKeyInput("down", e, _isAltNavigationModifierDown);

        var modifiers = Keyboard.Modifiers;
        var hasControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var hasShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var hasAlt = (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Alt単体（他の修飾キーなし）の判定を厳密に行う
        var isAltOnly = (modifiers == ModifierKeys.Alt)
            || (_isAltNavigationModifierDown && !hasControl && !hasShift);

        var isAltLeft = (isAltOnly && key == Key.Left)
            || key == Key.BrowserBack;
        var isAltRight = (isAltOnly && key == Key.Right)
            || key == Key.BrowserForward;
        var isAltUp = isAltOnly && key == Key.Up;

        if (GetSelectedInternalPage() is not null)
        {
            if (hasControl && e.Key == Key.Tab)
            {
                e.Handled = true;
                await SwitchTabByOffsetAsync(hasShift ? -1 : 1);
            }
            else if (hasControl && e.Key == Key.W && !hasShift)
            {
                e.Handled = true;
                await CloseActiveTabAsync();
            }
            else if (hasControl && GetTabShortcutIndex(e.Key) is { } tabIndex)
            {
                e.Handled = true;
                await SwitchToTabShortcutAsync(tabIndex);
            }

            return;
        }

        if (e.Key == Key.Escape && !isRenameTextBoxFocused)
        {
            if (!string.IsNullOrEmpty(FilterBox.Text))
            {
                e.Handled = true;
                ClearFilterIfNeeded();
            }
            else if (IsPreviewVisible
                && (focusedTextBox is null || ReferenceEquals(focusedTextBox, PreviewTextBox)))
            {
                e.Handled = true;
                SetPreviewVisible(false);
                FocusActiveFileList();
            }

            return;
        }

        if (HandlePreviewNavigationKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (isAltLeft)
        {
            e.Handled = true;
            await NavigateBackAsync();
            return;
        }

        if (isAltRight)
        {
            e.Handled = true;
            await NavigateForwardAsync();
            return;
        }

        if (isAltUp)
        {
            e.Handled = true;
            ClearFilterIfNeeded();
            await OpenParentAsync();
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RefreshCurrentFolderAsync();
            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            TogglePreview();
            return;
        }

        if (hasControl)
        {
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                await SwitchTabByOffsetAsync(hasShift ? -1 : 1);
                return;
            }

            if (e.Key == Key.T && hasShift)
            {
                e.Handled = true;
                await RestoreLastClosedTabAsync();
                return;
            }

            if (e.Key == Key.T)
            {
                e.Handled = true;
                await CreateNewTabAsync();
                return;
            }

            if (e.Key == Key.W && !hasShift)
            {
                e.Handled = true;
                await CloseActiveTabAsync();
                return;
            }

            if (GetTabShortcutIndex(e.Key) is { } tabIndex)
            {
                e.Handled = true;
                await SwitchToTabShortcutAsync(tabIndex);
                return;
            }

            if (e.Key == Key.L)
            {
                e.Handled = true;
                BeginPanePathEdit();
                return;
            }

            if (e.Key == Key.F)
            {
                e.Handled = true;
                FocusPaneFilterBox();
                return;
            }
        }

        if (isAltOnly && key == Key.D)
        {
            e.Handled = true;
            BeginPanePathEdit();
            return;
        }

        if (focusedTextBox is not null)
        {
            return;
        }

        if (hasControl && e.Key == Key.A)
        {
            e.Handled = true;
            SelectAllVisibleItems();
            return;
        }

        if (hasControl && e.Key == Key.C)
        {
            e.Handled = true;
            await SetPendingFileOperationAsync(PendingFileOperationKind.Copy);
            return;
        }

        if (hasControl && e.Key == Key.X)
        {
            e.Handled = true;
            await SetPendingFileOperationAsync(PendingFileOperationKind.Move);
            return;
        }

        if (hasControl && e.Key == Key.V)
        {
            e.Handled = true;
            await PastePendingFileOperationAsync();
            return;
        }

        if (hasControl && e.Key == Key.Z)
        {
            e.Handled = true;
            await UndoLastActionAsync();
            return;
        }

        if (hasControl && e.Key == Key.N)
        {
            e.Handled = true;
            if (hasShift)
            {
                await CreateNewItemAsync(NewItemKind.Folder);
            }
            else
            {
                await CreateNewItemAsync(NewItemKind.TextFile);
            }

            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await OpenSelectedAsync(hasControl);
            return;
        }

        if (e.Key == Key.Back)
        {
            e.Handled = true;
            ClearFilterIfNeeded();
            await OpenParentAsync();
            return;
        }

        if (e.Key == Key.F2)
        {
            e.Handled = true;
            if (IsKeyboardFocusInsideMainTabs())
            {
                BeginWorkspaceRename(ActiveSession ?? _activeWorkspaceSession);
                return;
            }

            BeginRenameSelected();
            return;
        }

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (hasShift)
            {
                // TODO: Implement permanent delete separately from recycle-bin delete.
                _performanceLogger.Write("shortcut-todo key=Shift+Delete action=permanent-delete");
                return;
            }

            await DeleteSelectedAsync();
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (IsAltKey(e))
        {
            _isAltNavigationModifierDown = false;
            LogPreviewKeyInput("up", e, _isAltNavigationModifierDown);
        }
    }

    private async Task OpenSelectedAsync(bool openDirectoryInNewTab = false)
    {
        var activePane = GetActiveFolderPane();
        var selectedEntries = activePane is not null
            ? GetSelectedEntries(activePane)
            : GetSelectedEntries();
        if (selectedEntries.Count != 1)
        {
            if (selectedEntries.Count > 1)
            {
                StatusText.Text = _text.Get("OpenSingleSelectionOnly");
            }
            return;
        }

        if (activePane is not null && IsWorkspaceDisplayPane(activePane))
        {
            if (openDirectoryInNewTab && selectedEntries[0].IsDirectory)
            {
                await CreateNewTabAsync(selectedEntries[0].FullPath);
                return;
            }

            await OpenWorkspacePaneSelectionAsync(activePane, selectedEntries[0]);
            return;
        }

        await OpenEntryAsync(selectedEntries[0], openDirectoryInNewTab);
    }

    private async Task OpenEntryAsync(FileEntry entry, bool openDirectoryInNewTab = false)
    {
        if (!await EnsureActiveFolderReadyForOperationAsync("open"))
        {
            return;
        }

        if (!File.Exists(entry.FullPath) && !Directory.Exists(entry.FullPath))
        {
            StatusText.Text = _text.Get("OpenFailedMissing");
            return;
        }

        if (await _navigationController.TryOpenWorkspaceFileAsync(entry))
        {
            return;
        }

        if (await _navigationController.TryOpenDirectoryEntryAsync(entry, openDirectoryInNewTab))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = _text.Format("OpenFailedPrefix", ex.Message);
            MessageBox.Show(this, ex.Message, _text.Get("OpenFileFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private async Task LaunchToolWithFileItemsAsync(FileEntry executableEntry, IReadOnlyList<FileDragItem> dragItems)
    {
        if (ShouldConfirmToolLaunch()
            && !ConfirmToolLaunch(executableEntry, dragItems.Count))
        {
            StatusText.Text = _text.Get("ToolLaunchCanceled");
            return;
        }

        try
        {
            var arguments = string.Join(" ", dragItems.Select(item => QuoteProcessArgument(item.SourcePath)));
            var startInfo = new ProcessStartInfo(executableEntry.FullPath)
            {
                Arguments = arguments,
                UseShellExecute = true
            };

            await Task.Run(() => Process.Start(startInfo));
            StatusText.Text = _text.Format("ToolLaunchStarted", executableEntry.Name, dragItems.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = _text.Format("ToolLaunchFailedPrefix", ex.Message);
            MessageBox.Show(this, ex.Message, _text.Get("ToolLaunchFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool ShouldConfirmToolLaunch()
    {
        return true;
    }

    private bool ConfirmToolLaunch(FileEntry executableEntry, int itemCount)
    {
        var result = MessageBox.Show(
            this,
            _text.Format("ToolLaunchConfirmMessage", executableEntry.FullPath, itemCount),
            _text.Get("ToolLaunchConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private static string QuoteProcessArgument(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var pendingBackslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', pendingBackslashes * 2 + 1);
                builder.Append('"');
                pendingBackslashes = 0;
                continue;
            }

            builder.Append('\\', pendingBackslashes);
            pendingBackslashes = 0;
            builder.Append(character);
        }

        builder.Append('\\', pendingBackslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private string? GetItemsListFileDropTargetDirectory(DragEventArgs e)
    {
        if (ActiveNavigation is not { } navigation
            || SpecialLocationService.IsSpecialUri(navigation.CurrentPath)
            || !Directory.Exists(navigation.CurrentPath))
        {
            return null;
        }

        return GetFileDropTargetDirectory(navigation.CurrentPath, e);
    }

    private static string? GetFileDropTargetDirectory(string currentPath, DragEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(currentPath)
            || SpecialLocationService.IsSpecialUri(currentPath)
            || !Directory.Exists(currentPath))
        {
            return null;
        }

        var source = e.OriginalSource as DependencyObject;
        var item = FindVisualParent<ListViewItem>(source);
        if (item?.DataContext is FileEntry entry
            && entry.IsDirectory
            && FileListHitTestService.IsInsideFileNameHitTarget(source))
        {
            if (!string.IsNullOrWhiteSpace(entry.FullPath)
                && !SpecialLocationService.IsSpecialUri(entry.FullPath)
                && Directory.Exists(entry.FullPath))
            {
                return entry.FullPath;
            }
        }

        return currentPath;
    }

    private static FileEntry? GetFileDropTargetEntry(DragEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var item = FindVisualParent<ListViewItem>(source);
        if (item?.DataContext is FileEntry { IsDirectory: true } entry
            && FileListHitTestService.IsInsideFileNameHitTarget(source))
        {
            if (!string.IsNullOrWhiteSpace(entry.FullPath)
                && !SpecialLocationService.IsSpecialUri(entry.FullPath)
                && Directory.Exists(entry.FullPath))
            {
                return entry;
            }
        }
        return null;
    }

    private static FileEntry? GetExecutableDropTargetEntry(DragEventArgs e)
    {
        return FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is FileEntry entry
            && IsExternalToolEntry(entry)
            ? entry
            : null;
    }

    private static IReadOnlyList<FileDragItem>? GetFileDragItems(DragEventArgs e)
    {
        return e.Data.GetDataPresent(FileDragFormat)
            ? e.Data.GetData(FileDragFormat) as IReadOnlyList<FileDragItem>
            : null;
    }

    private static IReadOnlyList<FileDragItem>? GetFileOperationDragItems(DragEventArgs e)
    {
        if (GetFileDragItems(e) is { } dragItems)
        {
            return dragItems.Any(item => item.IsTabOnly) ? null : dragItems;
        }

        return TryGetExternalFileDropItems(e)?
            .Select(item => new FileDragItem(item.SourcePath, item.Name, item.IsDirectory))
            .ToList();
    }

    private static IReadOnlyList<FileTransferItem>? TryGetExternalFileDropItems(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths
            || paths.Length == 0)
        {
            return null;
        }

        var items = new List<FileTransferItem>(paths.Length);
        foreach (var path in paths)
        {
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                return null;
            }

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            items.Add(new FileTransferItem(path, name, isDirectory));
        }

        return items;
    }

    private static FileDragItem? GetSingleExistingDirectoryDragItem(IReadOnlyList<FileDragItem>? dragItems)
    {
        if (dragItems is not { Count: 1 })
        {
            return null;
        }

        var item = dragItems[0];
        return item.IsDirectory && Directory.Exists(item.SourcePath)
            ? item
            : null;
    }

    private static string? GetSingleExistingDirectoryDropPath(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(BreadcrumbFolderDragFormat)
            && e.Data.GetData(BreadcrumbFolderDragFormat) is string breadcrumbPath
            && IsTabDropDirectoryPath(breadcrumbPath))
        {
            return breadcrumbPath;
        }

        if (GetSingleExistingDirectoryDragItem(GetFileDragItems(e)) is { } dragItem
            && IsTabDropDirectoryPath(dragItem.SourcePath))
        {
            return dragItem.SourcePath;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths
            || paths.Length != 1
            || !IsTabDropDirectoryPath(paths[0]))
        {
            return null;
        }

        return paths[0];
    }

    private static bool IsTabDropDirectoryPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && !SpecialLocationService.IsSpecialUri(path)
            && Directory.Exists(path);
    }

    private static PendingFileOperationKind GetFileDropOperationKind(DragEventArgs e, string? targetDirectory = null)
    {
        if (IsExplicitCopyDrop(e))
        {
            return PendingFileOperationKind.Copy;
        }
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) == DragDropKeyStates.ShiftKey)
        {
            return PendingFileOperationKind.Move;
        }

        if (!string.IsNullOrEmpty(targetDirectory))
        {
            string? firstSource = null;
            var dragItems = GetFileDragItems(e);
            if (dragItems is { Count: > 0 })
            {
                firstSource = dragItems[0].SourcePath;
            }
            else
            {
                var externalItems = TryGetExternalFileDropItems(e);
                if (externalItems is { Count: > 0 })
                {
                    firstSource = externalItems[0].SourcePath;
                }
            }

            if (!string.IsNullOrEmpty(firstSource))
            {
                try
                {
                    var sourceRoot = Path.GetPathRoot(firstSource);
                    var targetRoot = Path.GetPathRoot(targetDirectory);
                    if (!string.IsNullOrEmpty(sourceRoot) && !string.IsNullOrEmpty(targetRoot))
                    {
                        if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            return PendingFileOperationKind.Move;
                        }
                        else
                        {
                            return PendingFileOperationKind.Copy;
                        }
                    }
                }
                catch
                {
                    // Fallback
                }
            }
        }

        return PendingFileOperationKind.Copy;
    }

    private static bool IsExplicitCopyDrop(DragEventArgs e)
    {
        return (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
    }

    private bool CanDropFileItems(IReadOnlyList<FileDragItem> dragItems, string targetDirectory, PendingFileOperationKind operationKind)
    {
        if (!Directory.Exists(targetDirectory))
        {
            return false;
        }

        if (dragItems.Any(item => item.IsTabOnly))
        {
            return false;
        }

        if (dragItems.Count > 0)
        {
            var allSameFolder = true;
            foreach (var item in dragItems)
            {
                try
                {
                    var itemDir = Path.GetDirectoryName(item.SourcePath);
                    if (!string.Equals(itemDir, targetDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        allSameFolder = false;
                        break;
                    }
                }
                catch
                {
                    allSameFolder = false;
                    break;
                }
            }
            if (allSameFolder)
            {
                if (operationKind != PendingFileOperationKind.Copy)
                {
                    return false;
                }
            }
        }

        foreach (var item in dragItems)
        {
            // if (string.Equals(item.SourcePath, executableEntry.FullPath, StringComparison.OrdinalIgnoreCase))
            // {
            //     return false;
            // }

            if (!File.Exists(item.SourcePath) && !Directory.Exists(item.SourcePath))
            {
                return false;
            }

            if (item.IsDirectory && IsSameOrChildDirectory(item.SourcePath, targetDirectory, sourceIsDirectory: true))
            {
                return false;
            }

            if (operationKind == PendingFileOperationKind.Move
                && !item.IsDirectory
                && IsSameOrChildDirectory(item.SourcePath, targetDirectory, sourceIsDirectory: false))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanLaunchToolWithFileItems(IReadOnlyList<FileDragItem> dragItems, FileEntry executableEntry)
    {
        if (!IsExternalToolEntry(executableEntry) || !File.Exists(executableEntry.FullPath))
        {
            return false;
        }

        foreach (var item in dragItems)
        {
            if (!File.Exists(item.SourcePath) && !Directory.Exists(item.SourcePath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExternalToolEntry(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            return false;
        }

        return string.Equals(entry.Extension, "exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Extension, "bat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Extension, "cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrChildDirectory(string sourcePath, string targetDirectory, bool sourceIsDirectory)
    {
        var fullTargetDirectory = EnsureTrailingSeparator(Path.GetFullPath(targetDirectory));
        if (!sourceIsDirectory)
        {
            var sourceParent = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            return sourceParent is not null
                && string.Equals(EnsureTrailingSeparator(sourceParent), fullTargetDirectory, StringComparison.OrdinalIgnoreCase);
        }

        var fullSourceDirectory = EnsureTrailingSeparator(Path.GetFullPath(sourcePath));
        return fullTargetDirectory.StartsWith(fullSourceDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private void HighlightFileDropTarget(FileEntry targetEntry)
    {
        HighlightFileDropTarget(ItemsList, targetEntry);
    }

    private void HighlightFileDropTarget(ListView listView, FileEntry targetEntry)
    {
        var item = listView.ItemContainerGenerator.ContainerFromItem(targetEntry) as ListViewItem;
        if (ReferenceEquals(_fileDropTargetItem, item))
        {
            return;
        }

        ClearFileDropHighlight();
        _fileDropTargetItem = item;
        if (_fileDropTargetItem is not null)
        {
            _fileDropTargetItem.Tag = FileDropTargetTag;
        }
    }

    private void ClearFileDropHighlight()
    {
        if (_fileDropTargetItem is not null && Equals(_fileDropTargetItem.Tag, FileDropTargetTag))
        {
            _fileDropTargetItem.ClearValue(FrameworkElement.TagProperty);
        }

        _fileDropTargetItem = null;
    }

    private void ClearFileDragStart()
    {
        _fileDragStartPoint = null;
        _fileDragStartEntry = null;
        _fileDragStartPane = null;
        _fileDragStartListView = null;
        _workspacePendingRangeSelectionStartPoint = null;
        _workspacePendingRangeSelectionClickEntry = null;
        _workspacePendingRangeSelectionStartAdditive = false;
        _workspacePendingRangeSelectionListView = null;
        _workspacePendingRangeSelectionPane = null;
        ClearRangeSelectionStart();
        _pendingSingleSelectionClickEntry = null;
        _pendingSingleSelectionClickPoint = null;
        _pendingSingleSelectionClickPane = null;
        _pendingSingleSelectionClickListView = null;
    }

    private void BeginFileDragStart(ListView sourceListView, FolderPane pane, FileEntry entry, Point startPoint)
    {
        _fileDragStartPoint = startPoint;
        _fileDragStartEntry = entry;
        _fileDragStartPane = pane;
        _fileDragStartListView = sourceListView;
    }

    private bool TryStartDrag(ListView sourceListView, FolderPane pane, MouseEventArgs e)
    {
        if (_fileDragStartPoint is not { } startPoint
            || _fileDragStartEntry is not { } dragEntry
            || !ReferenceEquals(_fileDragStartPane, pane)
            || !ReferenceEquals(_fileDragStartListView, sourceListView)
            || e.LeftButton != MouseButtonState.Pressed
            || pane.ActiveTabState is not { } state)
        {
            if (e.LeftButton != MouseButtonState.Pressed
                || pane.ActiveTabState is null)
            {
                ClearFileDragStart();
            }

            return false;
        }

        var position = e.GetPosition(sourceListView);
        if (Math.Abs(position.X - startPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return false;
        }

        var isNormalPane = ReferenceEquals(pane, GetNormalFolderPane());
        if ((isNormalPane && _isLoading)
            || pane.IsLoading
            || IsRenameInteractionActive()
            || _selectionInteraction.IsSelecting
            || _scrollBehavior.IsAutoScrolling
            || !sourceListView.SelectedItems.Contains(dragEntry))
        {
            ClearFileDragStart();
            return false;
        }

        ClearPendingRenameClick();
        var tabOnlyDrag = SpecialLocationService.IsSpecialUri(state.CurrentPath);
        var dragItems = GetSelectedEntries(pane)
            .Where(entry => File.Exists(entry.FullPath) || Directory.Exists(entry.FullPath))
            .Where(entry => !tabOnlyDrag || entry.IsDirectory)
            .Select(entry => new FileDragItem(entry.FullPath, entry.Name, entry.IsDirectory, tabOnlyDrag))
            .ToList();
        if (dragItems.Count == 0)
        {
            ClearFileDragStart();
            return false;
        }

        StartFileDrag(sourceListView, dragItems);
        return true;
    }

    private void StartFileDrag(ListView sourceListView, IReadOnlyList<FileDragItem> dragItems)
    {
        try
        {
            var data = new DataObject(FileDragFormat, dragItems);
            if (!dragItems.Any(item => item.IsTabOnly))
            {
                var fileDropList = new StringCollection();
                foreach (var item in dragItems)
                {
                    fileDropList.Add(item.SourcePath);
                }

                data.SetFileDropList(fileDropList);
            }

            _isFileDragInProgress = true;
            DragDrop.DoDragDrop(sourceListView, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _isFileDragInProgress = false;
            ClearFileDragStart();
            ClearFileDropHighlight();
            ClearFileTabHover();
            ClearWorkspacePaneSubTabHover();
            ClearMainTabHover();
            _ = ProcessPendingFolderWatchRefreshAsync();
            _ = ProcessPendingDriveListRefreshAsync();
        }
    }

    private void ClearRangeSelectionStart()
    {
        _rangeSelectionStartPoint = null;
        _rangeSelectionStartAdditive = false;
        ClearWorkspacePaneRangeSelection();
    }

    private void MutateItemsForLoad(Action action)
    {
        _isMutatingItemsForLoad = true;
        try
        {
            action();
        }
        finally
        {
            _isMutatingItemsForLoad = false;
        }
    }

    private void ClearPendingRenameClick()
    {
        _renameInteraction.ClearPendingClick();
    }

    private void CancelPendingRenameClick()
    {
        _renameInteraction.CancelPendingClick();
    }

    private bool IsRenameable(FileEntry entry)
    {
        if (SpecialLocationService.IsSpecialUri(entry.FullPath))
        {
            return false;
        }

        try
        {
            var dirName = Path.GetDirectoryName(entry.FullPath);
            if (string.IsNullOrEmpty(dirName))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private FolderPane? FindPaneContainingEntry(FileEntry entry)
    {
        if (ActiveSession is not { } session)
        {
            return null;
        }

        if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            foreach (var pane in _workspaceDisplayPanes)
            {
                if (pane.Items.Contains(entry))
                {
                    return pane;
                }
            }
        }
        else
        {
            var normalPane = GetNormalFolderPane();
            if (normalPane != null && normalPane.Items.Contains(entry))
            {
                return normalPane;
            }
        }

        return null;
    }

    private IList<FileEntry> GetPaneItems(FolderPane pane)
    {
        return IsWorkspaceDisplayPane(pane)
            ? pane.Items
            : _items;
    }

    private bool HasActiveRename()
    {
        if (_activeRenameEntry?.IsRenaming == true)
        {
            return true;
        }
        if (GetActiveFolderPane() is { } pane)
        {
            return _renameInteraction.HasActiveRename(GetPaneItems(pane));
        }
        return _renameInteraction.HasActiveRename(_items);
    }

    private bool IsRenameInteractionActive()
    {
        return _renameInteraction.IsCommitInProgress
            || _renameInteraction.IsCanceling
            || _activeRenameEntry?.IsRenaming == true;
    }

    private void ClearActiveRenameEntry(FileEntry entry)
    {
        if (ReferenceEquals(_activeRenameEntry, entry))
        {
            _activeRenameEntry = null;
            _activeRenamePane = null;
            _activeRenameTextBox = null;
            _activeRenameTextBoxMouseDown = false;
        }
    }

    private bool BlockIfRenameInProgress(string operation)
    {
        if (!HasActiveRename())
        {
            return false;
        }

        _performanceLogger.Write($"operation-blocked-rename operation={operation} path=\"{ActiveNavigation?.CurrentPath ?? ""}\"");
        return true;
    }

    private async Task OpenDirectoryInNewTabAsync(FileEntry entry)
    {
        if (!await EnsureActiveFolderReadyForOperationAsync("open-new-tab"))
        {
            return;
        }

        if (!entry.IsDirectory)
        {
            return;
        }

        if (!Directory.Exists(entry.FullPath))
        {
            StatusText.Text = _text.Get("OpenFailedMissing");
            return;
        }

        await CreateNewTabAsync(entry.FullPath);
    }

    private async Task OpenParentAsync()
    {
        await _navigationController.OpenParentAsync();
    }

    private bool IsPathInDirectory(string path, string directory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory))
        {
            return false;
        }
        try
        {
            var parent = Path.GetDirectoryName(path);
            return string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void SelectItemInPaneByPath(FolderPane pane, string path, bool focus = true)
    {
        var paneItems = GetPaneItems(pane);
        var item = paneItems.FirstOrDefault(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        var listView = GetFolderPaneListView(pane);
        if (listView is not null)
        {
            listView.SelectedItem = item;
            listView.ScrollIntoView(item);
            if (focus)
            {
                bool shouldFocus = true;
                if (Keyboard.FocusedElement is DependencyObject focusedElement)
                {
                    var focusedPane = GetWorkspacePaneFromSender(focusedElement);
                    if (focusedPane is not null && !string.Equals(focusedPane.Id, pane.Id, StringComparison.Ordinal))
                    {
                        shouldFocus = false;
                    }
                }
                if (shouldFocus)
                {
                    listView.Focus();
                }
            }
        }

        if (IsWorkspaceDisplayPane(pane))
        {
            SyncPaneSelectionFromListView(pane, listView!);
        }
        else
        {
            UpdateSelectedItemStatus();
        }
    }

    private void SelectItemByPath(string path)
    {
        SelectItemInPaneByPath(GetNormalFolderPane() ?? _primaryPaneGroup, path);
    }

    private void SelectItemsByPaths(IReadOnlyCollection<string> paths)
    {
        SelectItemsInPaneByPaths(GetNormalFolderPane() ?? _primaryPaneGroup, paths);
    }

    private async Task ApplySettingsAsync(AppSettings settings)
    {
        SaveColumnWidths();
        RememberPreviewPaneSize();
        var previousTheme = _settingsService.Settings.Theme;
        var previousCustomThemeName = _settingsService.Settings.CustomThemeName;
        var themeError = ThemeManager.Apply(this, settings.Theme, settings.CustomThemeName);
        if (themeError != null)
        {
            settings.Theme = previousTheme;
            settings.CustomThemeName = previousCustomThemeName;
        }

        // Check if we transition from not needing extra columns to needing extra columns
        bool prevRequiresExtra = _settingsService.Settings.VisibleColumns.Any(ColumnLayoutService.RequiresExtraColumnMetadata);
        bool newRequiresExtra = settings.VisibleColumns.Any(ColumnLayoutService.RequiresExtraColumnMetadata);
        bool transitionToExtra = !prevRequiresExtra && newRequiresExtra;

        bool needsReload = _settingsService.Settings.ShowHiddenFiles != settings.ShowHiddenFiles
            || _settingsService.Settings.ShowSystemFiles != settings.ShowSystemFiles
            || _settingsService.Settings.Language != settings.Language
            || transitionToExtra;

        await _settingsService.SaveAsync(settings);
        _columnLayout.UpdateSettings(_settingsService.Settings);
        AppStrings.Configure(_settingsService.Settings.Language);
        ApplyFontSettings();
        ApplyLocalizedText();
        ApplyPreviewPanePlacement();
        ApplyColumnSettings();

        if (_workspaceDisplayPanes is not null)
        {
            foreach (var pane in _workspaceDisplayPanes)
            {
                ApplyColumnSettingsToWorkspacePane(pane);
                if (needsReload)
                {
                    await LoadFolderPaneItemsAsync(pane);
                }
            }
        }

        if (ActiveTab is not { } activeTab || ActiveNavigation is not { } navigation)
        {
            return;
        }

        ApplyTabSort(activeTab);
        SetNormalStatusText(themeError ?? _text.Get("SettingsSaved"));

        if (needsReload)
        {
            ItemsList.SelectedItems.Clear();
            await RefreshItemsViewLayoutAsync();
            _performanceLogger.Write($"settings-apply-updates beforeReload updates={GetUpdateDiagnosticsStatus()}");
            await LoadFolderAsync(navigation.CurrentPath, targetTab: activeTab);
            await RefreshItemsViewLayoutAsync();
            _performanceLogger.Write($"settings-apply-updates afterReload updates={GetUpdateDiagnosticsStatus()}");
        }
        else
        {
            await RefreshItemsViewLayoutAsync();
        }
    }

    private async void WorkspacePaneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSwitchingWorkspacePane
            || sender is not FrameworkElement element
            || element.DataContext is not WorkspacePaneGroup paneGroup)
        {
            return;
        }

        await SwitchWorkspacePaneGroupAsync(paneGroup);
    }

    private void WorkspacePaneFileList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSwitchingWorkspacePane
            || sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            PrepareWorkspacePaneFileListLeftMouseDown(listView, pane, e);
        }

        RememberWorkspacePaneInteraction(pane);
        ScheduleWorkspacePaneActivation(pane);
    }

    private const double SplitterGuardWidth = 6.0;

    private bool IsNearPaneSplitter(Point pointInListView, FrameworkElement listView)
    {
        if (listView.ActualWidth <= 0)
            return false;

        return pointInListView.X <= SplitterGuardWidth
            || pointInListView.X >= listView.ActualWidth - SplitterGuardWidth;
    }

    private void PrepareWorkspacePaneFileListLeftMouseDown(ListView listView, FolderPane pane, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(listView);
        if (IsNearPaneSplitter(position, listView))
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (IsInsideActiveRenameTextBox(source))
        {
            ClearWorkspacePanePendingMouseInput();
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var hasSelectionModifier = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
        var hasControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var hasShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var clickedEntry = FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
        var dragEntry = GetWorkspacePaneDragEntryFromTextHitTarget(source);
        ClearFileDragStart();

        if (clickedEntry is null)
        {
            if (e.ClickCount != 1
                || IsInsideScrollBar(source)
                || FindVisualParent<GridViewColumnHeader>(source) is not null)
            {
                return;
            }

            if (hasShift)
            {
                listView.Focus();
                return;
            }

            if (hasControl)
            {
                BeginPendingSelection(listView, pane, e.GetPosition(listView), additive: true);
                e.Handled = true;
                return;
            }

            if (!hasSelectionModifier)
            {
                BeginPendingSelection(listView, pane, e.GetPosition(listView), additive: false);
                e.Handled = true;
            }

            return;
        }

        if (dragEntry is null && clickedEntry is not null && !IsInsideScrollBar(source))
        {
            if (e.ClickCount == 1)
            {
                _workspacePendingRangeSelectionStartPoint = position;
                _workspacePendingRangeSelectionClickEntry = clickedEntry;
                _workspacePendingRangeSelectionStartAdditive = hasSelectionModifier;
                _workspacePendingRangeSelectionListView = listView;
                _workspacePendingRangeSelectionPane = pane;
                e.Handled = true;
                return;
            }
        }

        if (IsInsideScrollBar(source))
        {
            return;
        }

        if (dragEntry is not null)
        {
            if (!hasShift)
            {
                _workspaceSelectionAnchorEntry = dragEntry;
            }
            BeginFileDragStart(listView, pane, dragEntry, position);

            if (e.ClickCount == 1 && listView.SelectedItems.Contains(dragEntry))
            {
                if (!hasSelectionModifier || (hasControl && !hasShift))
                {
                    _pendingSingleSelectionClickEntry = dragEntry;
                    _pendingSingleSelectionClickPoint = position;
                    _pendingSingleSelectionClickPane = pane;
                    _pendingSingleSelectionClickListView = listView;

                    if (!hasSelectionModifier
                        && (modifiers & ModifierKeys.Alt) == ModifierKeys.None
                        && GetFileEntryFromRenameHitTarget(source) is FileEntry renameEntry
                        && ReferenceEquals(renameEntry, dragEntry)
                        && listView.SelectedItems.Count == 1
                        && !dragEntry.IsRenaming)
                    {
                        _renameInteraction.SetPendingClick(dragEntry, position);
                    }
                    e.Handled = true;
                }
            }
        }

        if (e.ClickCount == 1
            && clickedEntry is not null
            && hasShift
            && !IsInsideRenameTextBox(source))
        {
            _workspacePendingRangeSelectionStartPoint = position;
            _workspacePendingRangeSelectionClickEntry = clickedEntry;
            _workspacePendingRangeSelectionStartAdditive = hasControl;
            _workspacePendingRangeSelectionListView = listView;
            _workspacePendingRangeSelectionPane = pane;
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 1
            && clickedEntry is not null
            && hasControl
            && !hasShift
            && !IsInsideRenameTextBox(source))
        {
            _pendingSingleSelectionClickEntry = clickedEntry;
            _pendingSingleSelectionClickPoint = position;
            _pendingSingleSelectionClickPane = pane;
            _pendingSingleSelectionClickListView = listView;
            e.Handled = true;
        }
    }

    private async void WorkspacePaneFileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _workspacePendingRangeSelectionClickEntry is { } pendingEntry)
        {
            var entry = pendingEntry;
            var listView = _workspacePendingRangeSelectionListView;
            var pane = _workspacePendingRangeSelectionPane;
            ClearFileDragStart();

            if (listView is not null && pane is not null)
            {
                var modifiers = Keyboard.Modifiers;
                var hasControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                var hasShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                if (hasShift)
                {
                    FileListSelectionHelper.PerformShiftSelection(listView, _workspaceSelectionAnchorEntry, entry, hasControl);
                }
                else
                {
                    if (hasControl)
                    {
                        FileListSelectionHelper.ApplyControlSelection(listView, entry);
                        _workspaceSelectionAnchorEntry = entry;
                    }
                    else
                    {
                        FileListSelectionHelper.ApplySingleSelection(listView, entry);
                        _workspaceSelectionAnchorEntry = entry;
                    }
                }

                listView.Focus();
                SyncPaneSelectionFromListView(pane, listView);
            }
            e.Handled = true;
            return;
        }

        if (sender is ListView upListView
            && upListView.DataContext is FolderPane upPane
            && _renameInteraction.PendingClickEntry is { } renameEntry)
        {
            var currentPoint = e.GetPosition(upListView);
            var startPoint = _renameInteraction.PendingClickPoint;
            ClearPendingRenameClick();
            ClearFileDragStart();

            if (startPoint is not null
                && Math.Abs(currentPoint.X - startPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(currentPoint.Y - startPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance
                && upListView.SelectedItems.Count == 1
                && upListView.SelectedItems.Contains(renameEntry)
                && e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                await BeginRenameAfterClickDelayAsync(renameEntry, _renameInteraction.AdvanceGeneration(), upPane);
                return;
            }
        }

        if (CommitPendingSelection(sender, e))
        {
            e.Handled = true;
            return;
        }

        if (TryApplyWorkspacePanePendingSingleSelectionClick(sender, e))
        {
            e.Handled = true;
        }
    }

    private async Task OpenWorkspacePaneSelectionAsync(FolderPane pane, FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            await NavigateWorkspacePaneToFolderAsync(pane, entry.FullPath, NavigationKind.New);
            return;
        }

        await OpenWorkspacePaneFileAsync(entry);
    }

    private bool TryApplyWorkspacePanePendingSingleSelectionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not ListView listView
            || _pendingSingleSelectionClickEntry is not { } entry
            || _pendingSingleSelectionClickPoint is not { } startPoint
            || _pendingSingleSelectionClickPane is not { } pane
            || !ReferenceEquals(_pendingSingleSelectionClickListView, listView)
            || !ReferenceEquals(listView.DataContext, pane))
        {
            return false;
        }

        var currentPoint = e.GetPosition(listView);
        ClearFileDragStart();
        if (Math.Abs(currentPoint.X - startPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(currentPoint.Y - startPoint.Y) >= SystemParameters.MinimumVerticalDragDistance
            || !listView.Items.Contains(entry))
        {
            return false;
        }

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            FileListSelectionHelper.ApplyControlSelection(listView, entry);
            _workspaceSelectionAnchorEntry = entry;
        }
        else
        {
            FileListSelectionHelper.ApplySingleSelection(listView, entry);
            _workspaceSelectionAnchorEntry = entry;
        }
        listView.Focus();
        SyncPaneSelectionFromListView(pane, listView);
        return true;
    }

    private static FileEntry? GetWorkspacePaneDragEntryFromTextHitTarget(DependencyObject? source)
    {
        if (FileListHitTestService.IsInsideFileNameHitTarget(source))
        {
            return FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
        }

        return null;
    }

    private void WorkspacePaneFileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (HandleWorkspacePaneRangeSelectionMove(sender, e))
        {
            e.Handled = true;
            return;
        }

        if (sender is ListView listView
            && listView.DataContext is FolderPane pane
            && TryStartDrag(listView, pane, e))
        {
            e.Handled = true;
        }
    }

    private void BeginPendingSelection(ListView listView, FolderPane pane, Point startPoint, bool additive)
    {
        ClearFileDragStart();
        _workspaceRangeSelectionPane = pane;
        _workspaceRangeSelectionListView = listView;
        _workspaceRangeSelectionStartPoint = startPoint;
        _workspaceRangeSelectionMoved = false;
        _workspaceRangeSelectionAdditive = additive;
        _workspaceRangeSelectionBase.Clear();
        foreach (var entry in listView.SelectedItems.OfType<FileEntry>())
        {
            _workspaceRangeSelectionBase.Add(entry);
        }

        listView.Focus();
        EnsureWorkspaceRangeSelectionAdorner(listView);
        listView.CaptureMouse();
    }

    private bool HandleWorkspacePaneRangeSelectionMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            return false;
        }

        if (_workspacePendingRangeSelectionStartPoint is { } pendingStart
            && ReferenceEquals(_workspacePendingRangeSelectionListView, listView)
            && ReferenceEquals(_workspacePendingRangeSelectionPane, pane))
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ClearFileDragStart();
                return false;
            }

            var current = e.GetPosition(listView);
            if (Math.Abs(current.X - pendingStart.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(current.Y - pendingStart.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                var additive = _workspacePendingRangeSelectionStartAdditive;
                ClearFileDragStart();
                BeginPendingSelection(listView, pane, pendingStart, additive);
            }

            return true;
        }

        if (_workspaceRangeSelectionStartPoint is not { } startPoint
            || !ReferenceEquals(_workspaceRangeSelectionListView, listView)
            || !ReferenceEquals(listView.DataContext, _workspaceRangeSelectionPane))
        {
            return false;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CommitPendingSelection(listView, changedButton: MouseButton.Left);
            return true;
        }

        var currentPoint = e.GetPosition(listView);
        if (!_workspaceRangeSelectionMoved
            && Math.Abs(currentPoint.X - startPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return true;
        }

        _workspaceRangeSelectionMoved = true;
        var selectionRect = FileListRangeSelectionHelper.CreateSelectionRect(startPoint, currentPoint);
        DrawWorkspacePaneRangeSelection(listView, selectionRect);
        ApplyWorkspacePaneRangeSelection(listView, selectionRect);
        return true;
    }

    private bool CommitPendingSelection(object sender, MouseButtonEventArgs e)
    {
        return CommitPendingSelection(sender, e.ChangedButton);
    }

    private bool CommitPendingSelection(object sender, MouseButton changedButton)
    {
        if (changedButton != MouseButton.Left
            || sender is not ListView listView
            || _workspaceRangeSelectionStartPoint is null
            || !ReferenceEquals(_workspaceRangeSelectionListView, listView)
            || _workspaceRangeSelectionPane is not { } pane
            || !ReferenceEquals(listView.DataContext, pane))
        {
            return false;
        }

        if (!_workspaceRangeSelectionMoved && !_workspaceRangeSelectionAdditive)
        {
            ClearSelectionForPane(pane, listView, updateStatus: false);
        }

        if (listView.IsMouseCaptured)
        {
            listView.ReleaseMouseCapture();
        }

        ClearWorkspacePaneRangeSelection();
        SyncPaneSelectionFromListView(pane, listView);
        return true;
    }

    private void ApplyWorkspacePaneRangeSelection(ListView listView, Rect selectionRect)
    {
        FileListRangeSelectionHelper.SelectItemsInRange(
            listView,
            selectionRect,
            _workspaceRangeSelectionAdditive,
            _workspaceRangeSelectionBase);
    }

    private void EnsureWorkspaceRangeSelectionAdorner(ListView listView)
    {
        if (_workspaceRangeSelectionAdorner is not null
            && ReferenceEquals(_workspaceRangeSelectionAdorner.AdornedElement, listView))
        {
            return;
        }

        ClearWorkspaceRangeSelectionAdorner();
        var layer = AdornerLayer.GetAdornerLayer(listView);
        if (layer is null)
        {
            return;
        }

        _workspaceRangeSelectionAdorner = new WorkspaceRangeSelectionAdorner(listView);
        _workspaceRangeSelectionAdornerLayer = layer;
        layer.Add(_workspaceRangeSelectionAdorner);
    }

    private void DrawWorkspacePaneRangeSelection(ListView listView, Rect selectionRect)
    {
        EnsureWorkspaceRangeSelectionAdorner(listView);
        _workspaceRangeSelectionAdorner?.Update(selectionRect);
    }

    private void ClearWorkspaceRangeSelectionAdorner()
    {
        if (_workspaceRangeSelectionAdorner is not null
            && _workspaceRangeSelectionAdornerLayer is not null)
        {
            _workspaceRangeSelectionAdornerLayer.Remove(_workspaceRangeSelectionAdorner);
        }

        _workspaceRangeSelectionAdorner = null;
        _workspaceRangeSelectionAdornerLayer = null;
    }

    private void ClearWorkspacePaneRangeSelection()
    {
        if (_workspaceRangeSelectionListView?.IsMouseCaptured == true)
        {
            _workspaceRangeSelectionListView.ReleaseMouseCapture();
        }

        _workspaceRangeSelectionPane = null;
        _workspaceRangeSelectionListView = null;
        _workspaceRangeSelectionStartPoint = null;
        _workspaceRangeSelectionMoved = false;
        _workspaceRangeSelectionAdditive = false;
        _workspaceRangeSelectionBase.Clear();
        ClearWorkspaceRangeSelectionAdorner();
    }

    private async void WorkspacePaneFileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CancelPendingRenameClick();
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible
            || sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton != MouseButton.Left
            || IsInsideScrollBar(source)
            || FindVisualParent<GridViewColumnHeader>(source) is not null)
        {
            return;
        }

        var entry = FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
        if (entry is null)
        {
            ClearFilterIfNeeded();
            e.Handled = true;
            return;
        }

        e.Handled = true;
        if (entry.IsDirectory)
        {
            await NavigateWorkspacePaneToFolderAsync(pane, entry.FullPath, NavigationKind.New);
            return;
        }

        await OpenWorkspacePaneFileAsync(entry);
    }

    private void WorkspacePaneFileList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (GetMainTabDemotionTarget(e, FindSessionContainingPane(pane), pane) is not null)
        {
            e.Effects = DragDropEffects.Move;
            ClearFileDropHighlight();
            e.Handled = true;
            return;
        }

        var targetDirectory = GetWorkspacePaneFileDropTargetDirectory(pane, e);
        if (targetDirectory is null)
        {
            e.Effects = DragDropEffects.None;
            ClearFileDropHighlight();
            e.Handled = true;
            return;
        }

        var dragItems = GetFileOperationDragItems(e);
        var operationKind = GetFileDropOperationKind(e, targetDirectory);
        if (dragItems is null || !CanDropFileItems(dragItems, targetDirectory, operationKind))
        {
            e.Effects = DragDropEffects.None;
            ClearFileDropHighlight();
            e.Handled = true;
            return;
        }

        e.Effects = operationKind == PendingFileOperationKind.Copy
            ? DragDropEffects.Copy
            : DragDropEffects.Move;
        HighlightWorkspacePaneFileDropTarget(listView, e);
        e.Handled = true;
    }

    private void WorkspacePaneFileList_DragLeave(object sender, DragEventArgs e)
    {
        ClearFileDropHighlight();
    }

    private async void WorkspacePaneFileList_Drop(object sender, DragEventArgs e)
    {
        ClearFileDropHighlight();
        if (sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (GetMainTabDemotionTarget(e, FindSessionContainingPane(pane), pane) is { } demotionTarget)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            await DemoteMainTabToSubTabAsync(
                demotionTarget.DraggedSession,
                demotionTarget.TargetPane,
                demotionTarget.TargetPane.Tabs.Count);
            return;
        }

        if (GetWorkspacePaneFileDropTargetDirectory(pane, e) is not { } targetDirectory)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dragItems = GetFileOperationDragItems(e);
        var operationKind = GetFileDropOperationKind(e, targetDirectory);
        if (dragItems is null || !CanDropFileItems(dragItems, targetDirectory, operationKind))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = operationKind == PendingFileOperationKind.Copy
            ? DragDropEffects.Copy
            : DragDropEffects.Move;
        e.Handled = true;
        var transferItems = dragItems
            .Select(item => new FileTransferItem(item.SourcePath, item.Name, item.IsDirectory))
            .ToList();
        await ExecuteFileTransferAsync(
            transferItems,
            targetDirectory,
            operationKind,
            refreshActiveFolder: true,
            refreshTab: pane.ActiveTab,
            confirmNonSelfCopy: IsExplicitCopyDrop(e),
            refreshPane: pane);
    }

    private void HighlightWorkspacePaneFileDropTarget(ListView listView, DragEventArgs e)
    {
        if (GetFileDropTargetEntry(e) is { } targetEntry)
        {
            HighlightFileDropTarget(listView, targetEntry);
            return;
        }

        ClearFileDropHighlight();
    }

    private static string? GetWorkspacePaneFileDropTargetDirectory(FolderPane pane, DragEventArgs e)
    {
        return pane.ActiveTabState is { } state
            ? GetFileDropTargetDirectory(state.CurrentPath, e)
            : null;
    }

    private async void WorkspacePaneNewSubTabButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane && pane.ActiveTab is { } activeTab)
        {
            var newTab = _tabOperations.CreateNewTab(activeTab.Navigation.CurrentPath, activeTab);
            pane.AddTab(newTab);

            await _navigationController.NavigateWorkspacePaneToFolderAsync(pane, newTab.Navigation.CurrentPath, NavigationKind.New);
            ScheduleSessionSave("new-subtab");
        }
    }

    private async void WorkspacePaneSubTabClose_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (sender is FrameworkElement element
            && element.DataContext is FolderTab targetTab
            && FindVisualParent<ListBox>(element) is ListBox listBox
            && listBox.DataContext is FolderPane pane)
        {
            var activeTabBeforeClose = ReferenceEquals(_workspaceSubTabClosePaneBeforeClick, pane)
                ? _workspaceSubTabCloseActiveTabBeforeClick
                : null;
            _workspaceSubTabClosePaneBeforeClick = null;
            _workspaceSubTabCloseActiveTabBeforeClick = null;
            await CloseWorkspacePaneSubTabAsync(pane, targetTab, listBox, activeTabBeforeClose);
            e.Handled = true;
        }
        else
        {
            _workspaceSubTabClosePaneBeforeClick = null;
            _workspaceSubTabCloseActiveTabBeforeClick = null;
        }
    }

    private void WorkspacePaneSubTabClose_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element
            && FindVisualParent<ListBox>(element) is ListBox { DataContext: FolderPane pane })
        {
            _workspaceSubTabClosePaneBeforeClick = pane;
            _workspaceSubTabCloseActiveTabBeforeClick = pane.ActiveTab;
        }
    }

    private async Task CloseWorkspacePaneSubTabAsync(
        FolderPane pane,
        FolderTab tab,
        ListBox? listBox = null,
        FolderTab? activeTabBeforeClose = null)
    {
        SaveWorkspacePanesViewState();
        var session = _workspaceSessions.FirstOrDefault(s => s.PaneGroups.Any(pg => ReferenceEquals(pg, pane)));
        if (session is null)
        {
            return;
        }

        // 1. ロック済みサブタブの場合: 当然閉じない
        if (tab.IsFolderLocked)
        {
            _performanceLogger.Write($"close-subtab-blocked reason=subtab-locked path=\"{tab.Navigation.CurrentPath}\"");
            return;
        }

        var totalTabsInSession = session.PaneGroups.Sum(pg => pg.Tabs.Count);

        // 2. 最後のサブタブの場合
        if (totalTabsInSession == 1)
        {
            // ロック済みメインタブの場合: 最後のサブタブを閉じようとしても拒否
            if (session.IsLocked)
            {
                _performanceLogger.Write($"close-subtab-blocked reason=maintab-locked path=\"{tab.Navigation.CurrentPath}\"");
                return;
            }

            if (_workspaceSessions.Count > 1)
            {
                await CloseSessionAsync(session);
            }
            return;
        }

        // 3. 複数ペインWorkspaceで対象ペインの最後のサブタブを閉じる場合
        if (pane.Tabs.Count == 1)
        {
            SaveTabViewState(tab);
            _lastClosedSubTab = new ClosedSubTabState(
                pane.Id,
                _tabOperations.CaptureClosedTabState(tab, 0, 1) with { Index = 0 });
            _lastClosedKind = LastClosedKind.SubTab;

            CloseWorkspacePane(pane);
            return;
        }

        // 4. それ以外の場合
        var oldIndex = pane.Tabs.IndexOf(tab);
        if (oldIndex < 0)
        {
            return;
        }

        var previousActiveTab = activeTabBeforeClose ?? pane.ActiveTab;
        var wasActiveTab = ReferenceEquals(previousActiveTab, tab);
        var previousSelectedTabId = previousActiveTab?.Id ?? pane.SelectedTabId;
        SaveTabViewState(tab);
        _lastClosedSubTab = new ClosedSubTabState(
            pane.Id,
            _tabOperations.CaptureClosedTabState(tab, oldIndex, pane.Tabs.Count) with { Index = oldIndex });
        _lastClosedKind = LastClosedKind.SubTab;

        pane.RemoveTab(tab, session.RootPath);
        if (wasActiveTab)
        {
            await ActivateWorkspacePaneAfterSubTabCloseAsync(pane, oldIndex, listBox);
        }
        else
        {
            await RestoreWorkspacePaneActiveSubTabAfterNonActiveCloseAsync(pane, previousActiveTab, previousSelectedTabId, listBox);
        }

        _workspaceLocalState.QueueCapture(markDirty: true, reason: "remove-subtab");
        UpdateFolderWatch();
        UpdateWindowTitle();
    }

    private async Task ActivateWorkspacePaneAfterSubTabCloseAsync(FolderPane pane, int oldIndex, ListBox? listBox)
    {
        if (_activeWorkspaceSession is not { } session || pane.Tabs.Count == 0)
        {
            return;
        }

        var newIndex = Math.Clamp(oldIndex, 0, pane.Tabs.Count - 1);
        var nextTab = pane.Tabs[newIndex];
        pane.SelectedTabId = nextTab.Id;
        if (pane is WorkspacePaneGroup paneGroup)
        {
            _lastInteractedWorkspaceDisplayPane = paneGroup;
            _activeWorkspacePaneGroup = paneGroup;
            session.ActivePaneGroup = paneGroup;
            session.ActivePaneId = paneGroup.Id;
            var rootOffset = session.Workspace?.HasRootPath == true ? 1 : 0;
            session.SelectedTabIndex = Math.Clamp(
                paneGroup.SelectedTabIndex + rootOffset,
                0,
                Math.Max(0, paneGroup.Tabs.Count));
        }

        RestoreWorkspacePaneSubTabSelection(listBox, pane, nextTab);
        ApplyWorkspaceSessionToFolderTabs();
        ApplyDisplayModeToPane(pane);
        pane.RefreshDisplay();
        await LoadFolderPaneItemsAsync(pane);
        UpdateWorkspacePaneActiveStates();
        UpdateFolderWatchForWorkspacePanes();
        ApplyColumnSettingsToWorkspacePane(pane);
        RefreshPreviewForActiveSelection();
        ScheduleSessionSave("subtab-close-active");
    }

    private async Task RestoreWorkspacePaneActiveSubTabAfterNonActiveCloseAsync(
        FolderPane pane,
        FolderTab? previousActiveTab,
        string? previousSelectedTabId,
        ListBox? listBox)
    {
        var targetTab = previousActiveTab is not null && pane.Tabs.Contains(previousActiveTab)
            ? previousActiveTab
            : pane.Tabs.FirstOrDefault(tab => string.Equals(tab.Id, previousSelectedTabId, StringComparison.Ordinal));

        if (targetTab is null)
        {
            RestoreWorkspacePaneSubTabSelection(listBox, pane, previousSelectedTabId);
            return;
        }

        pane.SelectedTabId = targetTab.Id;
        RestoreWorkspacePaneSubTabSelection(listBox, pane, targetTab);
        ApplyWorkspaceSessionToFolderTabs();
        pane.RefreshDisplay();
        await LoadFolderPaneItemsAsync(pane);
        UpdateFolderWatchForWorkspacePanes();
        ApplyColumnSettingsToWorkspacePane(pane);
        RefreshPreviewForActiveSelection();
        ScheduleSessionSave("subtab-close-inactive");
    }

    private async Task RestoreLastClosedSubTabAsync(FolderPane? preferredPane = null)
    {
        if (_lastClosedSubTab is not { } closedSubTab)
        {
            _performanceLogger.Write("restore-closed-subtab-skip reason=empty");
            return;
        }

        var pane = FindWorkspacePaneById(closedSubTab.PaneId)
            ?? GetActiveFolderPane()
            ?? preferredPane;
        if (pane is null)
        {
            _performanceLogger.Write("restore-closed-subtab-skip reason=no-pane");
            return;
        }

        _lastClosedSubTab = null;
        if (_lastClosedKind == LastClosedKind.SubTab)
        {
            _lastClosedKind = LastClosedKind.None;
        }
        var tab = _tabOperations.RestoreClosedTabState(closedSubTab.TabState);
        var insertIndex = Math.Clamp(closedSubTab.TabState.Index, 0, pane.Tabs.Count);
        pane.Tabs.Insert(insertIndex, tab);
        pane.SelectedTabId = tab.Id;
        pane.ResolveTabHeaders();
        pane.RefreshDisplay();
        await _navigationController.NavigateWorkspacePaneToFolderAsync(pane, tab.Navigation.CurrentPath, NavigationKind.New);
        _workspaceLocalState.QueueCapture(markDirty: true, reason: "restore-subtab");
        UpdateFolderWatch();
    }

    private FolderPane? FindWorkspacePaneById(string paneId)
    {
        return _workspaceDisplayPanes.FirstOrDefault(pane => string.Equals(pane.Id, paneId, StringComparison.Ordinal));
    }

    private void RestoreWorkspacePaneSubTabSelection(ListBox? listBox, FolderPane pane, FolderTab selectedTab)
    {
        RestoreWorkspacePaneSubTabSelection(listBox, pane, selectedTab.Id, selectedTab);
    }

    private void RestoreWorkspacePaneSubTabSelection(ListBox? listBox, FolderPane pane, string? selectedTabId = null)
    {
        RestoreWorkspacePaneSubTabSelection(listBox, pane, selectedTabId, selectedTab: null);
    }

    private void RestoreWorkspacePaneSubTabSelection(ListBox? listBox, FolderPane pane, string? selectedTabId, FolderTab? selectedTab)
    {
        if (listBox is null)
        {
            return;
        }

        ApplyWorkspacePaneSubTabSelection(listBox, pane, selectedTabId, selectedTab);
        _ = Dispatcher.InvokeAsync(() =>
        {
            ApplyWorkspacePaneSubTabSelection(listBox, pane, selectedTabId, selectedTab);
        }, DispatcherPriority.ContextIdle);
    }

    private void ApplyWorkspacePaneSubTabSelection(ListBox listBox, FolderPane pane, string? selectedTabId, FolderTab? selectedTab)
    {
        if (listBox.DataContext is not FolderPane currentPane
            || !ReferenceEquals(currentPane, pane))
        {
            return;
        }

        var targetTab = selectedTab is not null && pane.Tabs.Contains(selectedTab)
            ? selectedTab
            : pane.Tabs.FirstOrDefault(tab => string.Equals(tab.Id, selectedTabId ?? pane.SelectedTabId, StringComparison.Ordinal));
        if (targetTab is null)
        {
            return;
        }

        if (!string.Equals(pane.SelectedTabId, targetTab.Id, StringComparison.Ordinal))
        {
            pane.SelectedTabId = targetTab.Id;
        }

        if (!ReferenceEquals(listBox.SelectedItem, targetTab))
        {
            listBox.SelectedItem = targetTab;
        }

        if (!string.Equals(listBox.SelectedValue as string, targetTab.Id, StringComparison.Ordinal))
        {
            listBox.SelectedValue = targetTab.Id;
        }

        BringWorkspacePaneSelectedSubTabIntoView(listBox);
    }

    private async void WorkspacePaneSubTabBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox
            && listBox.DataContext is FolderPane pane)
        {
            if (e.RemovedItems.OfType<FolderTab>().FirstOrDefault() is { } previousTab)
            {
                SaveWorkspacePaneColumnWidthsForTab(pane, previousTab);
                SaveWorkspacePaneNavigationViewState(pane, previousTab);
            }

            if (pane.ActiveTab is { } activeTab)
            {
                if (_activeWorkspaceSession is not null)
                {
                    _ = ActivateWorkspacePaneFromSenderAsync(listBox);
                }
                BringWorkspacePaneSelectedSubTabIntoView(listBox);
                activeTab.State.CurrentPath = activeTab.Navigation.CurrentPath;
                ApplyDisplayModeToPane(pane);
                pane.RefreshDisplay();
                await LoadFolderPaneItemsAsync(pane);
                UpdateFolderWatchForWorkspacePanes();
                ApplyColumnSettingsToWorkspacePane(pane);

                UpdateWindowTitle();
                ScheduleSessionSave("subtab-selection-changed");
            }
        }
    }

    private void WorkspacePaneFileList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView && listView.DataContext is FolderPane pane)
        {
            ApplyDisplayModeToPane(listView, pane);
            ApplyColumnSettingsToWorkspacePane(listView, pane);
            HookWorkspacePaneColumnWidthChanges(listView, pane);
        }
    }

    private void WorkspacePaneFileList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
        {
            UnhookWorkspacePaneColumnWidthChanges(listView);
        }
    }

    private void UpdateWorkspacePaneColumnHeadersForPane(FolderPane pane)
    {
        var listView = FindListViewForPane(pane);
        if (listView is not null)
        {
            UpdateWorkspacePaneColumnHeaders(listView, pane);
        }
    }

    private void UpdateWorkspacePaneColumnHeaders(ListView listView, FolderPane pane)
    {
        if (listView.View is GridView gridView)
        {
            UpdateWorkspacePaneColumnHeaders(gridView, pane);
        }
    }

    private void UpdateWorkspacePaneColumnHeaders(GridView gridView, FolderPane pane)
    {
        var targetState = pane.ActiveTabState;
        var path = pane.ActiveTab?.Navigation.CurrentPath ?? "";
        bool isSpecial = SpecialLocationService.IsSpecialUri(path);

        foreach (var column in gridView.Columns)
        {
            if (column.Header is TextBlock textBlock && textBlock.Tag is string columnId)
            {
                var normalizedId = ColumnLayoutService.NormalizeColumnId(columnId);
                var resourceKey = GetHeaderResourceKey(normalizedId, isSpecial);
                var baseText = _text.Get(resourceKey);

                if (targetState is not null && string.Equals(targetState.SortColumn, normalizedId, StringComparison.OrdinalIgnoreCase))
                {
                    textBlock.Text = baseText + (targetState.SortAscending ? " ↑" : " ↓");
                }
                else
                {
                    textBlock.Text = baseText;
                }
            }
        }
    }

    private ListView? FindListViewForPane(FolderPane pane)
    {
        return FindVisualChildren<ListView>(WorkspaceSessionsHost)
            .FirstOrDefault(lv =>
            {
                if (lv.DataContext is FolderPane p)
                {
                    return string.Equals(p.Id, pane.Id, StringComparison.Ordinal);
                }
                if (lv.DataContext is not null)
                {
                    var dcType = lv.DataContext.GetType();
                    var idProp = dcType.GetProperty("Id") ?? dcType.GetProperty("PaneId");
                    if (idProp is not null && idProp.GetValue(lv.DataContext) is string idVal)
                    {
                        return string.Equals(idVal, pane.Id, StringComparison.Ordinal);
                    }
                    var paneProp = dcType.GetProperties().FirstOrDefault(prop => typeof(FolderPane).IsAssignableFrom(prop.PropertyType));
                    if (paneProp is not null && paneProp.GetValue(lv.DataContext) is FolderPane wrappedPane)
                    {
                        return string.Equals(wrappedPane.Id, pane.Id, StringComparison.Ordinal);
                    }
                }
                return false;
            });
    }

    private void WorkspacePaneSubTabBar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox
            || FindVisualChild<ScrollViewer>(listBox) is not { } scrollViewer
            || scrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        var nextOffset = Math.Clamp(
            scrollViewer.HorizontalOffset - e.Delta,
            0,
            scrollViewer.ScrollableWidth);
        scrollViewer.ScrollToHorizontalOffset(nextOffset);
        e.Handled = true;
    }

    private void BringWorkspacePaneSelectedSubTabIntoView(ListBox listBox)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (listBox.SelectedItem is not { } selectedItem)
            {
                return;
            }

            listBox.UpdateLayout();
            if (listBox.ItemContainerGenerator.ContainerFromItem(selectedItem) is FrameworkElement item)
            {
                item.BringIntoView();
            }
        }, DispatcherPriority.ContextIdle);
    }

    private async void WorkspacePaneSubTabBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeWorkspaceSession is not null
            && e.ChangedButton == MouseButton.Left)
        {
            await ActivateWorkspacePaneFromSenderAsync(sender);
        }

        if (e.ChangedButton == MouseButton.Middle
            && sender is ListBox closeListBox
            && closeListBox.DataContext is FolderPane closePane
            && FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } closeItem
            && closeItem.DataContext is FolderTab closeTab)
        {
            await CloseWorkspacePaneSubTabAsync(closePane, closeTab, closeListBox);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left
            && e.ClickCount == 1
            && sender is ListBox dragListBox
            && dragListBox.DataContext is FolderPane dragPane
            && FindVisualParent<Button>(e.OriginalSource as DependencyObject) is null
            && FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } dragItem
            && dragItem.DataContext is FolderTab dragTab)
        {
            _subTabDragStartPoint = e.GetPosition(dragListBox);
            _draggedSubTabPane = dragPane;
            _draggedSubTab = dragTab;
        }

        if (e.ChangedButton != MouseButton.Left
            || e.ClickCount != 2
            || sender is not ListBox listBox
            || listBox.DataContext is not FolderPane pane)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var listItem = FindVisualParent<ListBoxItem>(source);
        if (listItem is not null)
        {
            // Close button click should not trigger lock toggling
            if (FindVisualParent<Button>(source) is null && listItem.DataContext is FolderTab tab)
            {
                e.Handled = true;
                ToggleWorkspacePaneSubTabLock(pane, tab);
            }
            return;
        }

        e.Handled = true;
        await CreateWorkspacePaneSubTabAsync(pane);
    }

    private void WorkspacePaneSubTabBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_subTabDragStartPoint is null
            || _draggedSubTabPane is null
            || _draggedSubTab is null
            || sender is not ListBox listBox
            || !ReferenceEquals(listBox.DataContext, _draggedSubTabPane)
            || e.LeftButton != MouseButtonState.Pressed)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ClearSubTabDragState();
            }
            return;
        }

        var position = e.GetPosition(listBox);
        if (Math.Abs(position.X - _subTabDragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _subTabDragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        try
        {
            var data = new DataObject(SubTabDragFormat, _draggedSubTab);
            e.Handled = true;
            DragDrop.DoDragDrop(listBox, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            ClearSubTabDragState();
            ClearWorkspacePaneSubTabHover();
            ClearMainTabHover();
        }
    }

    private void WorkspacePaneSubTabBar_DragOver(object sender, DragEventArgs e)
    {
        QueueWorkspacePaneSubTabHover(sender, e);

        if (CanDropSubTab(sender, e))
        {
            if (e.Data.GetDataPresent(TabDragFormat))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                var isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
                e.Effects = isCopy ? DragDropEffects.Copy : DragDropEffects.Move;
            }
            e.Handled = true;
            return;
        }

        if (GetWorkspacePaneSubTabFileDropTarget(sender, e) is { } fileDropTarget)
        {
            var dragItems = GetFileOperationDragItems(e);
            var operationKind = GetFileDropOperationKind(e, fileDropTarget.Navigation.CurrentPath);
            e.Effects = dragItems is not null
                && CanDropFileItems(dragItems, fileDropTarget.Navigation.CurrentPath, operationKind)
                    ? operationKind == PendingFileOperationKind.Copy
                        ? DragDropEffects.Copy
                        : DragDropEffects.Move
                    : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (GetWorkspacePaneSubTabFolderDropPath(sender, e) is not null)
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void WorkspacePaneSubTabBar_DragLeave(object sender, DragEventArgs e)
    {
        ClearWorkspacePaneSubTabHover();
    }

    private async void WorkspacePaneSubTabBar_Drop(object sender, DragEventArgs e)
    {
        ClearWorkspacePaneSubTabHover();
        if (sender is ListBox listBox
            && listBox.DataContext is FolderPane targetPane)
        {
            if (CanDropSubTab(sender, e) && e.Data.GetDataPresent(TabDragFormat))
            {
                var draggedSession = e.Data.GetData(TabDragFormat) as WorkspaceSession;
                if (draggedSession is not null
                    && !draggedSession.IsWorkspace
                    && draggedSession.PaneGroups.Count == 1
                    && draggedSession.PaneGroups[0].Tabs.Count == 1
                    && _workspaceSessions.Count > 1)
                {
                    var targetIndex = GetSubTabDropTargetIndex(e, targetPane);
                    if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is null)
                    {
                        targetIndex = targetPane.Tabs.Count;
                    }
                    targetIndex = Math.Clamp(targetIndex, 0, targetPane.Tabs.Count);
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                    await DemoteMainTabToSubTabAsync(draggedSession, targetPane, targetIndex);
                }
                return;
            }

            if (CanDropSubTab(sender, e)
                && _draggedSubTab is { } draggedTab
                && _draggedSubTabPane is WorkspacePaneGroup sourcePane
                && targetPane is WorkspacePaneGroup targetPaneGroup
                && FindSessionContainingPane(sourcePane) is { } sourceSession
                && FindSessionContainingPane(targetPaneGroup) is { } targetSession)
            {
                var targetItem = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
                var targetTab = targetItem?.DataContext as FolderTab;
                if (ReferenceEquals(draggedTab, targetTab))
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }

                var targetIndex = GetSubTabDropTargetIndex(e, targetPane);
                var isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
                e.Effects = isCopy ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Handled = true;

                if (isCopy)
                {
                    if (await CopySubTabToPane(sourceSession, sourcePane, draggedTab, targetSession, targetPaneGroup, targetIndex, listBox))
                    {
                        _performanceLogger.Write($"workspace-subtab-drop-copy sourceSessionId=\"{sourceSession.Id}\" targetSessionId=\"{targetSession.Id}\" sourcePaneId=\"{sourcePane.Id}\" targetPaneId=\"{targetPane.Id}\" tabId=\"{draggedTab.Id}\" targetIndex={targetIndex}");
                    }
                }
                else
                {
                    if (await MoveSubTabToPane(sourceSession, sourcePane, draggedTab, targetSession, targetPaneGroup, targetIndex, listBox))
                    {
                        _performanceLogger.Write($"workspace-subtab-drop-move sourceSessionId=\"{sourceSession.Id}\" targetSessionId=\"{targetSession.Id}\" sourcePaneId=\"{sourcePane.Id}\" targetPaneId=\"{targetPane.Id}\" tabId=\"{draggedTab.Id}\" targetIndex={targetIndex}");
                    }
                }
                return;
            }

            if (GetWorkspacePaneSubTabFileDropTarget(sender, e) is { } fileDropTarget)
            {
                var dragItems = GetFileOperationDragItems(e);
                var targetDirectory = fileDropTarget.Navigation.CurrentPath;
                var operationKind = GetFileDropOperationKind(e, targetDirectory);
                if (dragItems is null || !CanDropFileItems(dragItems, targetDirectory, operationKind))
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }

                e.Effects = operationKind == PendingFileOperationKind.Copy
                    ? DragDropEffects.Copy
                    : DragDropEffects.Move;
                e.Handled = true;
                var transferItems = dragItems
                    .Select(item => new FileTransferItem(item.SourcePath, item.Name, item.IsDirectory))
                    .ToList();
                await ExecuteFileTransferAsync(
                    transferItems,
                    targetDirectory,
                    operationKind,
                    refreshActiveFolder: true,
                    refreshTab: fileDropTarget,
                    confirmNonSelfCopy: IsExplicitCopyDrop(e),
                    refreshPane: targetPane);
                return;
            }

            if (GetWorkspacePaneSubTabFolderDropPath(sender, e) is { } folderPath)
            {
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
                await CreateWorkspacePaneSubTabAsync(targetPane, folderPath, targetPane.ActiveTab);
                _performanceLogger.Write($"workspace-subtab-folder-drop paneId=\"{targetPane.Id}\" path=\"{folderPath}\"");
                return;
            }
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void WorkspacePaneSubTabAddTarget_DragOver(object sender, DragEventArgs e)
    {
        ClearWorkspacePaneSubTabHover();
        e.Effects = GetWorkspacePaneFromSender(sender) is not null
            && GetSingleExistingDirectoryDropPath(e) is not null
                ? DragDropEffects.Link
                : DragDropEffects.None;
        e.Handled = true;
    }

    private async void WorkspacePaneSubTabAddTarget_Drop(object sender, DragEventArgs e)
    {
        ClearWorkspacePaneSubTabHover();
        if (GetWorkspacePaneFromSender(sender) is not { } pane
            || GetSingleExistingDirectoryDropPath(e) is not { } folderPath)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Link;
        e.Handled = true;
        await CreateWorkspacePaneSubTabAsync(pane, folderPath, pane.ActiveTab);
        _performanceLogger.Write($"workspace-subtab-add-target-folder-drop paneId=\"{pane.Id}\" path=\"{folderPath}\"");
    }

    private void QueueWorkspacePaneSubTabHover(object sender, DragEventArgs e)
    {
        if (sender is not ListBox { DataContext: FolderPane pane }
            || FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not FolderTab tab
            || !IsWorkspacePaneSubTabHoverDrag(e)
            || ReferenceEquals(pane.ActiveTab, tab))
        {
            ClearWorkspacePaneSubTabHover();
            return;
        }

        if (ReferenceEquals(_subTabHoverPane, pane)
            && ReferenceEquals(_subTabHoverTarget, tab)
            && _subTabHoverTimer.IsEnabled)
        {
            return;
        }

        _subTabHoverPane = pane;
        _subTabHoverTarget = tab;
        _subTabHoverTimer.Stop();
        _subTabHoverTimer.Start();
    }

    private static bool IsWorkspacePaneSubTabHoverDrag(DragEventArgs e)
    {
        return e.Data.GetDataPresent(TabDragFormat)
            || e.Data.GetDataPresent(SubTabDragFormat)
            || e.Data.GetDataPresent(FileDragFormat)
            || e.Data.GetDataPresent(BreadcrumbFolderDragFormat)
            || e.Data.GetDataPresent(DataFormats.FileDrop);
    }

    private void SubTabHoverTimer_Tick(object? sender, EventArgs e)
    {
        _subTabHoverTimer.Stop();
        var pane = _subTabHoverPane;
        var tab = _subTabHoverTarget;
        _subTabHoverPane = null;
        _subTabHoverTarget = null;

        if (pane is null
            || tab is null
            || !_workspaceDisplayPanes.Contains(pane)
            || !pane.Tabs.Contains(tab)
            || ReferenceEquals(pane.ActiveTab, tab))
        {
            return;
        }

        pane.SelectedTabId = tab.Id;
    }

    private void ClearWorkspacePaneSubTabHover()
    {
        _subTabHoverPane = null;
        _subTabHoverTarget = null;
        _subTabHoverTimer.Stop();
    }

    private static FolderTab? GetWorkspacePaneSubTabFileDropTarget(object sender, DragEventArgs e)
    {
        if (sender is not ListBox { DataContext: FolderPane }
            || FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not FolderTab tab
            || GetFileOperationDragItems(e) is null
            || SpecialLocationService.IsSpecialUri(tab.Navigation.CurrentPath)
            || !Directory.Exists(tab.Navigation.CurrentPath))
        {
            return null;
        }

        return tab;
    }

    private bool CanDropSubTab(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox
            || listBox.DataContext is not FolderPane pane)
        {
            return false;
        }

        if (e.Data.GetDataPresent(TabDragFormat))
        {
            var targetSession = FindSessionContainingPane(pane);
            return GetMainTabDemotionTarget(e, targetSession, pane) is not null;
        }

        if (e.Data.GetDataPresent(SubTabDragFormat) && _draggedSubTab is not null && _draggedSubTabPane is not null)
        {
            var sourceSession = FindSessionContainingPane(_draggedSubTabPane);
            var targetSession = FindSessionContainingPane(pane);
            var isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
            if (sourceSession is null
                || targetSession is null
                || !_draggedSubTabPane.Tabs.Contains(_draggedSubTab)
                || (!isCopy && targetSession.PaneGroups
                    .SelectMany(candidate => candidate.Tabs)
                    .Any(tab => !ReferenceEquals(tab, _draggedSubTab)
                        && string.Equals(tab.Id, _draggedSubTab.Id, StringComparison.Ordinal))))
            {
                return false;
            }

            if (ReferenceEquals(pane, _draggedSubTabPane))
            {
                return pane.Tabs.Count > 1 || isCopy;
            }
            return true;
        }

        return false;
    }

    private WorkspaceSession? FindSessionContainingPane(FolderPane pane)
    {
        return _workspaceSessions.FirstOrDefault(session =>
            session.PaneGroups.Any(candidate => ReferenceEquals(candidate, pane)));
    }

    private MainTabDemotionTarget? GetMainTabDemotionTarget(
        DragEventArgs e,
        WorkspaceSession? targetSession,
        FolderPane? explicitPane = null)
    {
        if (!e.Data.GetDataPresent(TabDragFormat)
            || e.Data.GetData(TabDragFormat) is not WorkspaceSession draggedSession
            || targetSession is null
            || ReferenceEquals(draggedSession, targetSession)
            || _workspaceSessions.Count <= 1
            || draggedSession.IsWorkspace
            || draggedSession.PaneGroups.Count != 1
            || draggedSession.PaneGroups[0].Tabs.Count != 1
            || ResolveMainTabDemotionPane(targetSession, explicitPane) is not { } targetPane)
        {
            return null;
        }

        return new MainTabDemotionTarget(draggedSession, targetPane);
    }

    private static FolderPane? ResolveMainTabDemotionPane(
        WorkspaceSession targetSession,
        FolderPane? explicitPane)
    {
        if (explicitPane is not null
            && targetSession.PaneGroups.Any(pane => ReferenceEquals(pane, explicitPane)))
        {
            return explicitPane;
        }

        if (targetSession.PaneGroups.Count == 1)
        {
            return targetSession.PaneGroups[0];
        }

        return targetSession.ActivePaneGroup
            ?? targetSession.PaneGroups.FirstOrDefault(pane =>
                string.Equals(pane.Id, targetSession.ActivePaneId, StringComparison.OrdinalIgnoreCase))
            ?? targetSession.PaneGroups.FirstOrDefault();
    }

    private string? GetWorkspacePaneSubTabFolderDropPath(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox
            || listBox.DataContext is not FolderPane pane
            || FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is not null
            || GetSingleExistingDirectoryDropPath(e) is not { } directoryPath)
        {
            return null;
        }

        return directoryPath;
    }

    private int GetSubTabDropTargetIndex(DragEventArgs e, FolderPane pane)
    {
        if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } targetItem
            || targetItem.DataContext is not FolderTab targetTab)
        {
            var isDifferentPane = _draggedSubTabPane is not null && !ReferenceEquals(pane, _draggedSubTabPane);
            var defaultIndex = isDifferentPane ? pane.Tabs.Count : Math.Max(0, pane.Tabs.Count - 1);
            return Math.Clamp(defaultIndex, 0, pane.Tabs.Count);
        }

        var targetIndex = pane.Tabs.IndexOf(targetTab);
        if (targetIndex < 0)
        {
            var isDifferentPane = _draggedSubTabPane is not null && !ReferenceEquals(pane, _draggedSubTabPane);
            var defaultIndex = isDifferentPane ? pane.Tabs.Count : Math.Max(0, pane.Tabs.Count - 1);
            return Math.Clamp(defaultIndex, 0, pane.Tabs.Count);
        }

        var insertAfterTarget = e.GetPosition(targetItem).X > targetItem.ActualWidth / 2;
        var targetMoveIndex = insertAfterTarget ? targetIndex + 1 : targetIndex;

        var isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
        if (!isCopy)
        {
            var sourceIndex = _draggedSubTab is { } draggedTab ? pane.Tabs.IndexOf(draggedTab) : -1;
            if (sourceIndex >= 0 && sourceIndex < targetMoveIndex)
            {
                targetMoveIndex--;
            }
        }

        return Math.Clamp(targetMoveIndex, 0, pane.Tabs.Count);
    }

    private void ClearSubTabDragState()
    {
        _subTabDragStartPoint = null;
        _draggedSubTabPane = null;
        _draggedSubTab = null;
    }

    private async Task PromoteSubTabToMainTabAsync(FolderPane pane, FolderTab tab)
    {
        if (_activeWorkspaceSession is not { } session)
        {
            return;
        }

        SaveActiveTabViewState();

        // 1. Remove the subtab from its original pane
        var wasActiveInSource = string.Equals(pane.SelectedTabId, tab.Id, StringComparison.Ordinal);
        var removedIndex = pane.Tabs.IndexOf(tab);
        pane.Tabs.Remove(tab);

        // 2. Fallback tab handling or pane deletion if the original pane becomes empty
        if (pane.Tabs.Count == 0)
        {
            if (session.PaneGroups.Count > 1)
            {
                CloseWorkspacePane(pane);
            }
            else
            {
                var fallbackPath = session.RootPath;
                var newTabId = $"tab_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                var state = session.GetOrCreateTabState(pane.Id, fallbackPath, FileDisplayMode.Details, "Name", true, id: newTabId);
                var fallbackTab = new FolderTab(fallbackPath, newTabId, FileDisplayMode.Details, state);
                pane.Tabs.Add(fallbackTab);
                pane.SelectedTabId = fallbackTab.Id;
                pane.ResolveTabHeaders();
                pane.RefreshDisplay();
            }
        }
        else
        {
            if (wasActiveInSource)
            {
                var fallbackIndex = Math.Clamp(removedIndex, 0, pane.Tabs.Count - 1);
                pane.SelectedTabId = pane.Tabs[fallbackIndex].Id;
            }
            pane.ResolveTabHeaders();
            pane.RefreshDisplay();
        }

        if (wasActiveInSource && pane.Tabs.Count > 0)
        {
            await LoadFolderPaneItemsAsync(pane);
        }

        // 3. Remove tab's state from the original session dictionary
        session.UnregisterTabState(tab.Id, pane.Id);

        // 4. Create new single pane session using WorkspaceController
        var newSession = _workspaceController.CreateSinglePaneSession(tab);

        // 5. Update the PaneId of the moved tab's state to "primary"
        tab.State.PaneId = "primary";

        // 6. Register tab states in the new session
        newSession.RegisterTabState(tab.State);
        var representativeTab = newSession.Tabs.FirstOrDefault();
        if (representativeTab is not null)
        {
            newSession.RegisterTabState(representativeTab.State);
        }

        // 7. Add new session to _workspaceSessions and switch to it
        _isSwitchingTabs = true;
        try
        {
            _workspaceSessions.Add(newSession);
            _activeWorkspaceSession = newSession;
            UpdateActiveWorkspaceSessionUi(newSession);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            SelectWorkspaceSession(newSession);
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        // 8. Capture local state and restore
        _workspaceLocalState.Capture(markDirty: true, reason: "tabs");
        UpdateFolderWatchForOpenTabs();
        UpdateWindowTitle();
        await RestoreActiveTabAsync();
        ScheduleSessionSave("promote-subtab");
    }

    private async Task DemoteMainTabToSubTabAsync(WorkspaceSession draggedSession, FolderPane targetPane, int targetIndex)
    {
        var targetSession = FindSessionContainingPane(targetPane);
        if (targetSession is null
            || ReferenceEquals(targetSession, draggedSession)
            || _workspaceSessions.Count <= 1
            || draggedSession.IsWorkspace)
        {
            return;
        }

        if (draggedSession.PaneGroups.Count != 1 || draggedSession.PaneGroups[0].Tabs.Count != 1)
        {
            return;
        }

        var sourcePane = draggedSession.PaneGroups[0];
        var tab = sourcePane.Tabs[0];

        SaveActiveTabViewState();

        var clampedIndex = Math.Clamp(targetIndex, 0, targetPane.Tabs.Count);
        var oldPaneId = tab.State.PaneId;

        // Unregister from the dragged session
        draggedSession.UnregisterTabState(tab.Id, sourcePane.Id);

        // Update PaneId to targetPane.Id
        tab.State.PaneId = targetPane.Id;

        // Register tab state in the target session
        targetSession.RegisterTabState(tab.State);

        bool addedSuccessfully = false;
        try
        {
            targetPane.Tabs.Insert(clampedIndex, tab);
            targetPane.SelectedTabId = tab.Id;
            targetPane.ResolveTabHeaders();
            targetPane.RefreshDisplay();
            addedSuccessfully = true;
        }
        catch (Exception ex)
        {
            _performanceLogger.Write($"demote-insert-failed error=\"{ex.Message}\"");
        }

        if (!addedSuccessfully)
        {
            // Roll back
            tab.State.PaneId = oldPaneId;
            targetSession.UnregisterTabState(tab.Id, targetPane.Id);
            draggedSession.RegisterTabState(tab.State);
            return;
        }

        var closeResult = _workspaceController.CloseSession(_workspaceSessions, _activeWorkspaceSession, draggedSession);
        if (!closeResult.Success)
        {
            // Roll back insertion and registration
            try
            {
                targetPane.Tabs.Remove(tab);
                targetPane.ResolveTabHeaders();
                targetPane.RefreshDisplay();
            }
            catch { }
            tab.State.PaneId = oldPaneId;
            targetSession.UnregisterTabState(tab.Id, targetPane.Id);
            draggedSession.RegisterTabState(tab.State);
            return;
        }

        _isSwitchingTabs = true;
        try
        {
            _workspaceSessions.Remove(draggedSession);
            _activeWorkspaceSession = targetSession;
            UpdateActiveWorkspaceSessionUi(targetSession);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            SelectWorkspaceSession(targetSession);
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        targetPane.SelectedTabId = tab.Id;
        targetSession.ActivePaneId = targetPane.Id;
        if (targetPane is WorkspacePaneGroup wpg)
        {
            targetSession.ActivePaneGroup = wpg;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "tabs");
        UpdateFolderWatchForOpenTabs();
        UpdateWindowTitle();
        await RestoreActiveTabAsync();
        ScheduleSessionSave("demote-main-tab");
    }

    private void ToggleWorkspacePaneSubTabLock(FolderPane pane, FolderTab tab)
    {
        tab.SetFolderLocked(!tab.IsFolderLocked);
        pane.ResolveTabHeaders();
        pane.RefreshDisplay();
        _workspaceLocalState.Capture(markDirty: true, reason: "subtab-lock");
    }

    private void WorkspacePaneOperationsButton_Click(object sender, RoutedEventArgs e)
    {
        var pane = GetWorkspacePaneFromSender(sender);
        if (pane is null) return;

        if (sender is Button button && button.ContextMenu is not null)
        {
            button.Tag = pane;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Tag = pane;

            foreach (var item in button.ContextMenu.Items)
            {
                if (item is MenuItem menuItem)
                {
                    menuItem.IsEnabled = true;

                    var headerStr = menuItem.Header?.ToString() ?? "";
                    if (headerStr == "ペインを閉じる" || headerStr == _text.Get("WorkspacePaneMenuClose"))
                    {
                        menuItem.IsEnabled = _activeWorkspaceSession is not null && _activeWorkspaceSession.PaneGroups.Count > 1;
                    }
                }
            }

            button.ContextMenu.IsOpen = true;
        }

        ScheduleWorkspacePaneActivation(pane);
    }

    private void WorkspacePaneSplitRightMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetWorkspacePaneFromMenuItem(sender) is { } pane)
        {
            SplitWorkspacePane(pane, WorkspaceSplitOrientation.Horizontal);
        }
    }

    private void WorkspacePaneSplitDownMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetWorkspacePaneFromMenuItem(sender) is { } pane)
        {
            SplitWorkspacePane(pane, WorkspaceSplitOrientation.Vertical);
        }
    }

    private void WorkspacePaneCloseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetWorkspacePaneFromMenuItem(sender) is { } pane)
        {
            CloseWorkspacePane(pane);
        }
    }

    private static FolderPane? GetWorkspacePaneFromMenuItem(object sender)
    {
        if (sender is MenuItem menuItem)
        {
            if (menuItem.CommandParameter is FolderPane commandPane)
            {
                return commandPane;
            }

            DependencyObject? current = menuItem;
            while (current is not null)
            {
                if (current is ContextMenu contextMenu)
                {
                    if (contextMenu.Tag is FolderPane contextPane)
                    {
                        return contextPane;
                    }

                    if (contextMenu.PlacementTarget is FrameworkElement { Tag: FolderPane placementPane })
                    {
                        return placementPane;
                    }

                    if (contextMenu.PlacementTarget is DependencyObject placementTarget)
                    {
                        return GetWorkspacePaneFromSender(placementTarget);
                    }
                    break;
                }
                current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
            }
        }
        return null;
    }

    private async void SplitWorkspacePane(FolderPane pane, WorkspaceSplitOrientation orientation)
    {
        if (_activeWorkspaceSession is not { } session) return;

        var activeTab = pane.ActiveTab;
        if (activeTab is null) return;
        var path = activeTab.Navigation.CurrentPath;
        var paneId = $"pane_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        var tabId = $"tab_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        var state = session.GetOrCreateTabState(paneId, path, activeTab.State.ViewMode, activeTab.State.SortColumn, activeTab.State.SortAscending, id: tabId);
        state.FilterText = activeTab.State.FilterText;
        state.SelectedPaths = activeTab.State.SelectedPaths;
        state.VerticalOffset = activeTab.State.VerticalOffset;
        var clonedTab = new FolderTab(path, tabId, activeTab.State.ViewMode, state);
        clonedTab.SetFolderLocked(activeTab.IsFolderLocked);
        var newPaneTabs = new ObservableCollection<FolderTab> { clonedTab };
        var newPaneGroup = new WorkspacePaneGroup(paneId, newPaneTabs, path) { SelectedTabIndex = 0, SelectedTabId = tabId };
        if (session.Workspace is not null) newPaneGroup.SetWorkspace(session.Workspace);

        EnsureWorkspaceLayoutRoot(session);

        var layoutReplaced = false;
        var nextLayout = session.LayoutRoot is { } layoutRoot
            ? ReplacePaneInLayout(layoutRoot, pane.Id, newPaneGroup, orientation, out layoutReplaced)
            : null;

        var insertIndex = pane is WorkspacePaneGroup wp ? session.PaneGroups.IndexOf(wp) : -1;
        if (insertIndex >= 0) session.PaneGroups.Insert(insertIndex + 1, newPaneGroup);
        else session.PaneGroups.Add(newPaneGroup);

        session.LayoutRoot = nextLayout is not null && layoutReplaced
            ? nextLayout
            : BuildLayoutRootFromPaneGroups(session.PaneGroups, orientation);

        // DisplayLayoutRoot は LayoutRoot から再構築する
        session.DisplayLayoutRoot = BuildDisplayLayoutRoot(session);

        session.PaneSplitOrientation = orientation;
        session.ActivePaneGroup = newPaneGroup;
        RefreshWorkspaceDisplayPanes();
        await LoadFolderPaneItemsAsync(newPaneGroup);
        ScheduleSessionSave("split-pane");
        UpdateWindowTitle();
    }

    private void CloseWorkspacePane(FolderPane pane)
    {
        SaveWorkspacePanesViewState();
        if (_activeWorkspaceSession is not { } session)
        {
            return;
        }

        CloseWorkspacePane(session, pane);
    }

    private bool CloseWorkspacePane(WorkspaceSession session, FolderPane pane)
    {
        var isActiveSession = ReferenceEquals(session, _activeWorkspaceSession);

        // user pane の数が1つ以下なら閉じられない
        if (session.PaneGroups.Count <= 1)
        {
            if (isActiveSession)
            {
                StatusText.Text = "最後のペインは閉じられません。";
            }
            return false;
        }

        var index = pane is WorkspacePaneGroup wp ? session.PaneGroups.IndexOf(wp) : -1;
        if (index < 0)
        {
            return false;
        }

        var nextActivePane = session.ActivePaneGroup;
        if (ReferenceEquals(pane, session.ActivePaneGroup))
        {
            nextActivePane = index > 0 ? session.PaneGroups[index - 1] : session.PaneGroups[index + 1];
        }

        if (isActiveSession && ReferenceEquals(pane, _lastInteractedWorkspaceDisplayPane))
        {
            _lastInteractedWorkspaceDisplayPane = nextActivePane;
        }

        if (pane is WorkspacePaneGroup wpRemove) session.PaneGroups.Remove(wpRemove);

        // Close後に user pane が1つなら必ず単一 WorkspacePaneGroupDefinition へ畳む
        if (session.PaneGroups.Count == 1)
        {
            var remainingPane = session.PaneGroups[0];
            session.LayoutRoot = new WorkspacePaneGroupDefinition(remainingPane.Id, remainingPane.SelectedTabIndex, []);
        }
        else
        {
            var nextLayout = RemovePaneFromLayout(session.LayoutRoot, pane.Id, out var removed);
            session.LayoutRoot = removed
                ? nextLayout ?? BuildLayoutRootFromPaneGroups(session.PaneGroups, session.PaneSplitOrientation)
                : BuildLayoutRootFromPaneGroups(session.PaneGroups, session.PaneSplitOrientation);
        }

        // DisplayLayoutRoot は常に派生表示用として再生成する
        session.DisplayLayoutRoot = BuildDisplayLayoutRoot(session);

        session.ActivePaneGroup = nextActivePane;
        if (nextActivePane is not null)
        {
            session.ActivePaneId = nextActivePane.Id;
            if (isActiveSession)
            {
                _activeWorkspacePaneGroup = nextActivePane;
            }
        }

        if (isActiveSession)
        {
            RefreshWorkspaceDisplayPanes();
            UpdateFolderWatch();
            UpdateWindowTitle();
        }
        _workspaceLocalState.Capture(markDirty: true, reason: "close-pane");
        return true;
    }

    private void ShowWorkspacePaneActionPlaceholder(FolderPane pane, string actionName)
    {
        var currentPath = string.IsNullOrWhiteSpace(pane.CurrentPath)
            ? ""
            : $" ({pane.CurrentPath})";
        StatusText.Text = $"{actionName} は未実装です{currentPath}";
    }

    private async Task ActivateWorkspacePaneFromSenderAsync(object sender, bool allowRefresh = true)
    {
        if (_activeWorkspaceSession is null)
        {
            return;
        }

        if (GetWorkspacePaneFromSender(sender) is not { } pane)
        {
            return;
        }

        _lastInteractedWorkspaceDisplayPane = pane;
        if (pane is not WorkspacePaneGroup paneGroup)
        {
            if (allowRefresh)
            {
                RefreshWorkspaceDisplayPanes();
            }
            UpdateWindowTitle();
            return;
        }

        if (ReferenceEquals(paneGroup, _activeWorkspaceSession.ActivePaneGroup))
        {
            UpdateWindowTitle();
            return;
        }

        await SwitchWorkspacePaneGroupAsync(paneGroup);
    }

    private void ScheduleWorkspacePaneActivation(FolderPane pane)
    {
        Dispatcher.BeginInvoke(
            new Action(() => ActivateWorkspacePaneAfterDeferredInput(pane)),
            DispatcherPriority.Background);
    }

    private void RememberWorkspacePaneInteraction(FolderPane pane)
    {
        if (_activeWorkspaceSession is null || !IsWorkspaceDisplayPane(pane))
        {
            return;
        }

        _lastInteractedWorkspaceDisplayPane = pane;
        if (pane is WorkspacePaneGroup paneGroup)
        {
            _activeWorkspacePaneGroup = paneGroup;
            _activeWorkspaceSession.ActivePaneGroup = paneGroup;
            _activeWorkspaceSession.ActivePaneId = paneGroup.Id;
        }
    }

    private void ActivateWorkspacePaneAfterDeferredInput(FolderPane pane)
    {
        if (_activeWorkspaceSession is null || !IsWorkspaceDisplayPane(pane))
        {
            return;
        }

        RememberWorkspacePaneInteraction(pane);
        if (pane is WorkspacePaneGroup paneGroup)
        {
            EnsureWorkspacePaneHasFallbackTab(paneGroup);
            var rootOffset = _activeWorkspaceSession.Workspace?.HasRootPath == true ? 1 : 0;
            _activeWorkspaceSession.SelectedTabIndex = Math.Clamp(
                paneGroup.SelectedTabIndex + rootOffset,
                0,
                Math.Max(0, paneGroup.Tabs.Count));
            ApplyWorkspaceSessionToFolderTabs();
            paneGroup.RefreshDisplay();
            RefreshPreviewForActiveSelection();
            ScheduleSessionSave("active-pane");
        }

        UpdateWorkspacePaneActiveStates();
        UpdateWindowTitle();
    }

    private static FolderPane? GetWorkspacePaneFromSender(object sender)
    {
        var current = sender as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: FolderPane pane })
            {
                return pane;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private FolderPane? GetActiveFolderPane()
    {
        var session = ActiveSession;
        if (session is null)
        {
            return null;
        }

        if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            return (IsWorkspaceDisplayPane(_lastInteractedWorkspaceDisplayPane)
                    ? _lastInteractedWorkspaceDisplayPane
                    : null)
                ?? session.ActiveFolderPane
                ?? session.PaneGroups.FirstOrDefault(pane => string.Equals(pane.Id, "primary", StringComparison.OrdinalIgnoreCase))
                ?? session.PaneGroups.FirstOrDefault();
        }

        // Next integration point: normal mode still renders through ItemsList, but model-side
        // operations should continue moving toward this single FolderPane access path.
        return session.ActiveFolderPane ?? _primaryPaneGroup;
    }

    private FolderPane? GetNormalFolderPane()
    {
        if (ActiveSession is not { } session
            || session.IsWorkspace
            || WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            return null;
        }

        return session.ActiveFolderPane ?? _primaryPaneGroup;
    }

    private void SyncNormalPaneDisplayStateFromView(FolderPane pane)
    {
        // Drawing compatibility: normal mode still renders through ItemsList, so this is the
        // single boundary that copies view-owned state into the normal FolderPane model.
        pane.ScrollOffset = GetCurrentVerticalOffset();
        var selectedPaths = ItemsList.SelectedItems
            .OfType<FileEntry>()
            .Select(entry => entry.FullPath)
            .ToList();
        pane.SelectedPaths = selectedPaths;
        SyncNormalPaneStatusFromView(pane);
        if (pane.ActiveTabState is { } state)
        {
            state.SelectedPaths = selectedPaths;
            state.VerticalOffset = pane.ScrollOffset;
        }
    }

    private bool IsWorkspaceDisplayPane(FolderPane? pane)
    {
        return pane is not null
            && WorkspaceSplitGrid.Visibility == Visibility.Visible
            && _workspaceDisplayPanes.Contains(pane);
    }

    private ListView? GetFolderPaneListView(FolderPane pane)
    {
        return IsWorkspaceDisplayPane(pane)
            ? FindWorkspacePaneListView(WorkspaceSplitGrid, pane)
            : ItemsList;
    }

    private static ListView? FindWorkspacePaneListView(DependencyObject root, FolderPane pane)
    {
        return FindVisualChildren<ListView>(root)
            .FirstOrDefault(listView => ReferenceEquals(listView.DataContext, pane));
    }

    private IReadOnlyList<FileEntry> GetSelectedWorkspacePaneEntries(FolderPane pane)
    {
        return GetSelectedEntries(pane);
    }



    private FolderTab? GetActiveFolderPaneTab(FolderPane pane)
    {
        if (pane.Tabs.Count == 0)
        {
            return null;
        }

        return pane.Tabs[Math.Clamp(pane.SelectedTabIndex, 0, pane.Tabs.Count - 1)];
    }

    private async Task OpenWorkspacePaneFileAsync(FileEntry entry)
    {
        if (!File.Exists(entry.FullPath))
        {
            StatusText.Text = _text.Get("OpenFailedMissing");
            return;
        }

        if (WorkspaceService.IsWorkspaceFile(entry.FullPath)
            && await OpenWorkspaceFileAsync(entry.FullPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            _performanceLogger.Write($"folder-pane-file-open-failed path=\"{entry.FullPath}\" error=\"{ex.Message}\"");
            MessageBox.Show(this, ex.Message, _text.Get("OpenFileFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void WorkspacePaneFileList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible
            || sender is not FrameworkElement element
            || element.DataContext is not FolderPane pane)
        {
            return;
        }

        if (_suppressWorkspaceScrollSync)
        {
            return;
        }

        pane.ScrollOffset = e.VerticalOffset;
        if (pane.ActiveTabState is { } state)
        {
            state.VerticalOffset = e.VerticalOffset;
        }
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_workspaceTabSync.IsApplying || _activeWorkspaceSession is null)
        {
            return;
        }

        _workspaceLocalState.QueueCapture(markDirty: true, reason: "tabs");
    }

    private void WorkspaceSessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (var i = 0; i < e.NewItems!.Count; i++)
                {
                    var session = (WorkspaceSession)e.NewItems[i]!;
                    _mainTabs.Insert(e.NewStartingIndex + i, MainTabItem.FromWorkspace(session));
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < e.OldItems!.Count; i++)
                {
                    var tab = _mainTabs[e.OldStartingIndex];
                    _mainTabs.RemoveAt(e.OldStartingIndex);
                    tab.Dispose();
                }
                break;

            case NotifyCollectionChangedAction.Replace:
                for (var i = 0; i < e.NewItems!.Count; i++)
                {
                    var index = e.NewStartingIndex + i;
                    var oldTab = _mainTabs[index];
                    _mainTabs[index] = MainTabItem.FromWorkspace((WorkspaceSession)e.NewItems[i]!);
                    oldTab.Dispose();
                }
                break;

            case NotifyCollectionChangedAction.Move:
                _mainTabs.Move(e.OldStartingIndex, e.NewStartingIndex);
                break;

            case NotifyCollectionChangedAction.Reset:
                foreach (var tab in _mainTabs)
                {
                    tab.Dispose();
                }
                _mainTabs.Clear();
                foreach (var session in _workspaceSessions)
                {
                    _mainTabs.Add(MainTabItem.FromWorkspace(session));
                }
                break;
        }
    }

    private void SelectFolderTab(FolderTab? tab)
    {
        if (tab is null || _activeWorkspaceSession is null)
        {
            return;
        }

        var pane = _activeWorkspaceSession.PaneGroups
            .FirstOrDefault(candidate => candidate.Tabs.Contains(tab));
        if (pane is null)
        {
            return;
        }

        pane.SelectedTabIndex = pane.Tabs.IndexOf(tab);
        _activeWorkspaceSession.ActivePaneGroup = pane;
        _activeWorkspaceSession.SelectedTabIndex = pane.SelectedTabIndex;
        _activeWorkspacePaneGroup = pane;
    }

    private static WorkspaceSession? GetWorkspaceSession(object? item)
    {
        return item switch
        {
            MainTabItem tab => tab.WorkspaceSession,
            WorkspaceSession session => session,
            _ => null
        };
    }

    private async void TabsControl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var mainTab = FindVisualParent<TabItem>(source)?.DataContext as MainTabItem;
        if (e.ChangedButton != MouseButton.Left)
        {
            CancelScheduledWorkspaceRenameClick();
            ClearPendingWorkspaceRenameClick();
        }

        if (IsWorkspaceRenameTextBoxTarget(source))
        {
            return;
        }

        if (mainTab?.IsInternalPage == true)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                CloseInternalPage(mainTab);
            }

            return;
        }

        if (GetWorkspaceSession(mainTab) is not { } session)
        {
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
            {
                e.Handled = true;
                var isReserved = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                var activePath = ActiveNavigation?.CurrentPath ?? "";
                _performanceLogger.Write($"tab-empty-double-click path=\"{activePath}\" ctrl={isReserved}");
                if (!isReserved && ActiveTab is { } activeTab)
                {
                    await CreateNewMainWindowTabAsync(activePath, activeTab);
                }
            }

            return;
        }

        var tab = GetSessionActiveTab(session);
        if (tab is null)
        {
            return;
        }
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            await CloseSessionAsync(session);
            return;
        }

        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            e.Handled = true;
            ToggleWorkspaceLock(session);
        }
    }

    private async Task CreateNewTabAsync(string? path = null, FolderTab? sourceTab = null)
    {
        if (_activeWorkspaceSession?.IsWorkspace == true)
        {
            if (GetActiveFolderPane() is { } activePane)
            {
                await CreateWorkspacePaneSubTabAsync(activePane, path, sourceTab);
            }
            return;
        }

        await CreateNewMainWindowTabAsync(path, sourceTab);
    }

    private async Task CreateNewMainWindowTabAsync(string? path = null, FolderTab? sourceTab = null, bool forceNewTab = false)
    {
        if (!forceNewTab)
        {
            sourceTab ??= ActiveTab;
        }

        string newTabPath;
        if (sourceTab is not null && ActiveNavigation?.CurrentPath is { } currentPath && !string.IsNullOrWhiteSpace(currentPath))
        {
            newTabPath = _tabOperations.ResolveNewTabPath(path, currentPath);
        }
        else
        {
            newTabPath = path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        SaveActiveTabViewState();

        FolderTab tab;
        if (sourceTab is not null)
        {
            tab = _tabOperations.CreateNewTab(newTabPath, sourceTab);
            SeedNewTabCache(tab, sourceTab);
        }
        else
        {
            tab = new FolderTab(newTabPath, viewMode: _settingsService.Settings.DisplayMode);
        }

        var session = _workspaceController.CreateSinglePaneSession(tab);
        var result = _workspaceController.AddSession(_activeWorkspaceSession, session);

        _isSwitchingTabs = true;
        try
        {
            _workspaceSessions.Add(session);
            _activeWorkspaceSession = session;
            UpdateActiveWorkspaceSessionUi(session);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            SelectWorkspaceSession(session);
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "tabs");
        await RestoreActiveTabAsync();
    }

    private async Task OpenFolderInNewMainTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!SpecialLocationService.IsSpecialUri(path) && !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(_text.Get("OpenFailedMissing"));
        }

        await CreateNewMainWindowTabAsync(path, sourceTab: null, forceNewTab: true);
    }

    private async Task CreateWorkspacePaneSubTabAsync(
        FolderPane pane,
        string? path = null,
        FolderTab? sourceTab = null)
    {
        if (_activeWorkspaceSession is null)
        {
            return;
        }

        var activeTab = pane.ActiveTab ?? pane.Tabs.FirstOrDefault();
        if (activeTab is null)
        {
            return;
        }
        var paneCurrentPath = activeTab.Navigation.CurrentPath ?? _activeWorkspaceSession.RootPath;
        var paneNewTabPath = _tabOperations.ResolveNewTabPath(path, paneCurrentPath);

        var source = sourceTab ?? activeTab;
        var newTab = _tabOperations.CreateNewTab(paneNewTabPath, source);
        SeedNewTabCache(newTab, source);

        pane.AddTab(newTab);
        await _navigationController.NavigateWorkspacePaneToFolderAsync(pane, newTab.Navigation.CurrentPath, NavigationKind.New);
        _workspaceLocalState.QueueCapture(markDirty: true, reason: "new-subtab");
    }



    private void SeedNewTabCache(FolderTab tab, FolderTab? preferredSource)
    {
        var path = tab.Navigation.CurrentPath;
        var result = _tabOperations.SeedNewTabCache(tab, preferredSource, ActiveTab, _itemsOwnerStateId, _items);
        if (result.Kind == TabCacheSeedKind.Shared)
        {
            var sourcePane = FindPaneForTab(result.SourceTab!);
            var sourceIndex = sourcePane?.Tabs.IndexOf(result.SourceTab!) ?? -1;
            _performanceLogger.Write($"tab-cache-shared path=\"{path}\" items={result.ItemCount} sourceIndex={sourceIndex} memory={GetProcessMemoryStatus()}");
            return;
        }

        if (result.Kind == TabCacheSeedKind.Cloned)
        {
            _performanceLogger.Write($"tab-cache-cloned path=\"{path}\" items={result.ItemCount} memory={GetProcessMemoryStatus()}");
        }
    }

    private async Task CloseActiveTabAsync()
    {
        if (GetSelectedInternalPage() is { } internalPage)
        {
            CloseInternalPage(internalPage);
            return;
        }

        if (ActiveSession is not { } session)
        {
            return;
        }

        var activePane = session.ActivePaneGroup ?? session.PaneGroups.FirstOrDefault();
        if (activePane is not null && activePane.ActiveTab is { } activeSubTab)
        {
            await CloseWorkspacePaneSubTabAsync(activePane, activeSubTab);
        }
        else
        {
            await CloseSessionAsync(session);
        }
    }

    private async Task CloseSessionAsync(WorkspaceSession session)
    {
        var result = _workspaceController.CloseSession(_workspaceSessions, _activeWorkspaceSession, session);
        if (!result.Success)
        {
            return;
        }

        SaveActiveTabViewState();
        if (result.RequiresSaveActiveLocalState)
        {
            _workspaceLocalState.SaveActiveLocalState();
        }

        if (result.ClosedSessionIndex is { } index)
        {
            _lastClosedWorkspaceSession = new ClosedWorkspaceSessionState(session, index);
            _lastClosedKind = LastClosedKind.MainSession;
        }

        _isSwitchingTabs = true;
        try
        {
            _workspaceSessions.Remove(session);
            var nextSession = result.ActiveSession!;
            if (ReferenceEquals(nextSession, session))
            {
                nextSession = _workspaceSessions.FirstOrDefault(s => !ReferenceEquals(s, session))!;
            }
            _activeWorkspaceSession = nextSession;
            UpdateActiveWorkspaceSessionUi(nextSession);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            SelectWorkspaceSession(nextSession);
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "tabs");
        UpdateFolderWatchForOpenTabs();
        await RestoreActiveTabAsync();
        LogMemoryMetrics("tab-closed");
    }


    private async Task RestoreLastClosedTabAsync()
    {
        if (_lastClosedKind == LastClosedKind.MainSession && _lastClosedWorkspaceSession is not null)
        {
            await RestoreLastClosedWorkspaceSessionAsync();
        }
        else if (_lastClosedKind == LastClosedKind.SubTab && _lastClosedSubTab is not null)
        {
            await RestoreLastClosedSubTabAsync();
        }
        else
        {
            if (_lastClosedWorkspaceSession is not null)
            {
                await RestoreLastClosedWorkspaceSessionAsync();
            }
            else if (_lastClosedSubTab is not null)
            {
                await RestoreLastClosedSubTabAsync();
            }
            else
            {
                _performanceLogger.Write("restore-closed-tab-skip reason=empty");
            }
        }
    }

    private async Task RestoreLastClosedWorkspaceSessionAsync()
    {
        if (_lastClosedWorkspaceSession is not { } closedSessionState)
        {
            return;
        }

        var session = closedSessionState.Session;
        var insertIndex = Math.Clamp(closedSessionState.Index, 0, _workspaceSessions.Count);

        SaveActiveTabViewState();
        _lastClosedWorkspaceSession = null;
        if (_lastClosedKind == LastClosedKind.MainSession)
        {
            _lastClosedKind = LastClosedKind.None;
        }

        var result = _workspaceController.InsertSession(insertIndex, _workspaceSessions, _activeWorkspaceSession, session);

        _isSwitchingTabs = true;
        try
        {
            _workspaceSessions.Insert(result.InsertIndex ?? insertIndex, session);
            _activeWorkspaceSession = session;
            UpdateActiveWorkspaceSessionUi(session);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            SelectWorkspaceSession(session);
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "restore-session");
        UpdateFolderWatchForOpenTabs();
        await RestoreActiveTabAsync();
    }

    private async Task SwitchTabByOffsetAsync(int offset)
    {
        if (_mainTabs.Count != _workspaceSessions.Count)
        {
            if (_mainTabs.Count <= 1)
            {
                return;
            }

            var selectedIndex = Math.Max(0, TabsControl.SelectedIndex);
            TabsControl.SelectedIndex = (selectedIndex + offset + _mainTabs.Count) % _mainTabs.Count;
            return;
        }

        if (_workspaceSessions.Count <= 1)
        {
            return;
        }

        SaveActiveTabViewState();
        var nextIndex = (TabsControl.SelectedIndex + offset + _workspaceSessions.Count) % _workspaceSessions.Count;
        var nextSession = _workspaceSessions[nextIndex];

        var result = _workspaceController.TrySelectSession(_activeWorkspaceSession, nextSession);
        if (!result.Success)
        {
            return;
        }

        if (result.RequiresSaveActiveLocalState)
        {
            _workspaceLocalState.SaveActiveLocalState();
        }

        if (result.ActiveSessionChanged)
        {
            CancelActiveLoadForWorkspaceSwitch(nextSession, "workspace-shortcut-switch");
        }

        _isSwitchingTabs = true;
        try
        {
            var activeSession = result.ActiveSession!;
            _activeWorkspaceSession = activeSession;
            UpdateActiveWorkspaceSessionUi(activeSession);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            TabsControl.SelectedIndex = nextIndex;
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "selected-tab");
        await RestoreActiveTabAsync();
    }

    private async Task SwitchToTabShortcutAsync(int shortcutIndex)
    {
        if (_mainTabs.Count != _workspaceSessions.Count)
        {
            if (_mainTabs.Count == 0)
            {
                return;
            }

            var targetIndex = shortcutIndex == 8
                ? _mainTabs.Count - 1
                : Math.Min(shortcutIndex, _mainTabs.Count - 1);
            TabsControl.SelectedIndex = targetIndex;
            return;
        }

        if (_workspaceSessions.Count == 0)
        {
            return;
        }

        var nextIndex = shortcutIndex == 8
            ? _workspaceSessions.Count - 1
            : Math.Min(shortcutIndex, _workspaceSessions.Count - 1);
        if (nextIndex == TabsControl.SelectedIndex)
        {
            return;
        }

        SaveActiveTabViewState();
        var nextSession = _workspaceSessions[nextIndex];

        var result = _workspaceController.TrySelectSession(_activeWorkspaceSession, nextSession);
        if (!result.Success)
        {
            return;
        }

        if (result.RequiresSaveActiveLocalState)
        {
            _workspaceLocalState.SaveActiveLocalState();
        }

        if (result.ActiveSessionChanged)
        {
            CancelActiveLoadForWorkspaceSwitch(nextSession, "workspace-shortcut-switch");
        }

        _isSwitchingTabs = true;
        try
        {
            var activeSession = result.ActiveSession!;
            _activeWorkspaceSession = activeSession;
            UpdateActiveWorkspaceSessionUi(activeSession);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            TabsControl.SelectedIndex = nextIndex;
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "selected-tab");
        await RestoreActiveTabAsync();
    }


    private async Task CloseOtherSessionsAsync(WorkspaceSession session)
    {
        var result = _workspaceController.CloseOtherSessions(_workspaceSessions, _activeWorkspaceSession, session);
        if (!result.Success)
        {
            return;
        }

        SaveActiveTabViewState();
        _workspaceLocalState.SaveActiveLocalState();
        _isSwitchingTabs = true;
        try
        {
            if (result.SessionsToRemove is { } toRemove)
            {
                foreach (var r in toRemove)
                {
                    _workspaceSessions.Remove(r);
                }
            }

            _activeWorkspaceSession = session;
            UpdateActiveWorkspaceSessionUi(session);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            SelectWorkspaceSession(session);
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "tabs");
        await RestoreActiveTabAsync();
    }


    private void SaveTabViewState(FolderTab tab)
    {
        if (_isLoading || !string.IsNullOrEmpty(_loadingStateId))
        {
            _performanceLogger.Write($"tab-cache-save-skip reason=loading stateId={tab.State.Id} activeStateId={_activeStateId ?? ""} loadingStateId={_loadingStateId ?? ""} path=\"{tab.Navigation.CurrentPath}\"");
            return;
        }

        if (!string.Equals(_itemsOwnerStateId, tab.State.Id, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var targetState = tab.State;
            var normalPane = GetNormalFolderPane();
            if (normalPane is not null)
            {
                SyncNormalPaneDisplayStateFromView(normalPane);
            }

            SaveCurrentFilterToState(targetState);
            targetState.SortColumn = NormalizeSortColumn(targetState.SortColumn);
            targetState.VerticalOffset = normalPane?.ScrollOffset ?? GetCurrentVerticalOffset();
            targetState.SelectedPaths = normalPane?.SelectedPaths.ToList()
                ?? ItemsList.SelectedItems
                    .OfType<FileEntry>()
                    .Select(entry => entry.FullPath)
                    .ToList();
            var cacheMemoryBefore = GetProcessWorkingSetBytes();
            tab.StoreItems(tab.Navigation.CurrentPath, _items.ToList());
            var cacheMemoryAfter = GetProcessWorkingSetBytes();
            _performanceLogger.Write($"tab-cache-save stateId={targetState.Id} path=\"{targetState.CurrentPath}\" items={targetState.CachedItems?.Count ?? 0} selected={targetState.SelectedPaths.Count} offset={targetState.VerticalOffset:N1} memoryDeltaMb={(cacheMemoryAfter - cacheMemoryBefore) / 1024d / 1024d:N1} memory={GetProcessMemoryStatus()}");
        }
        catch (Exception ex)
        {
            LogException("tab-cache-save", ex, tab.State);
            throw;
        }
    }

    private async Task CloseSessionsToRightAsync(WorkspaceSession session)
    {
        var result = _workspaceController.CloseSessionsToRight(_workspaceSessions, _activeWorkspaceSession, session);
        if (!result.Success)
        {
            return;
        }

        SaveActiveTabViewState();
        _workspaceLocalState.SaveActiveLocalState();
        _isSwitchingTabs = true;
        try
        {
            if (result.SessionsToRemove is { } toRemove)
            {
                foreach (var r in toRemove)
                {
                    _workspaceSessions.Remove(r);
                }
            }

            var nextSession = result.ActiveSession!;
            _activeWorkspaceSession = nextSession;
            UpdateActiveWorkspaceSessionUi(nextSession);
            ApplyWorkspaceSessionToFolderTabs();
            RefreshWorkspaceDisplayPanes();
            SelectWorkspaceSession(nextSession);
        }
        finally
        {
            _isSwitchingTabs = false;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "tabs");
        if (result.ActiveSessionChanged)
        {
            await RestoreActiveTabAsync();
        }
    }

    private void SaveActiveTabViewState()
    {
        SaveWorkspacePanesViewState();
        if (GetSelectedWorkspaceSession() is not null)
        {
            var tab = ActiveTab;
            if (tab is null || _activeWorkspaceSession is null)
            {
                return;
            }

            SaveTabViewState(tab);
            if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
            {
                var rootOffset = (_activeWorkspaceSession.Workspace is not null && _activeWorkspaceSession.Workspace.HasRootPath) ? 1 : 0;
                _activeWorkspaceSession.SelectedTabIndex = Math.Clamp(
                    _activeWorkspacePaneGroup.SelectedTabIndex + rootOffset,
                    0,
                    Math.Max(0, _activeWorkspacePaneGroup.Tabs.Count));
            }
            else
            {
                var primaryPane = _activeWorkspaceSession.PaneGroups.FirstOrDefault(p => string.Equals(p.Id, "primary", StringComparison.OrdinalIgnoreCase)) ?? _activeWorkspaceSession.PaneGroups.FirstOrDefault();
                _activeWorkspaceSession.SelectedTabIndex = primaryPane is not null ? Math.Clamp(primaryPane.Tabs.IndexOf(tab), 0, Math.Max(0, primaryPane.Tabs.Count - 1)) : 0;
                _activeWorkspacePaneGroup.SelectedTabIndex = _activeWorkspaceSession.SelectedTabIndex;
            }
            _activeWorkspacePaneGroup.RefreshDisplay();
        }
    }

    private ListViewRestoreState? CreateRestoreStateFromTab(FolderTab tab)
    {
        return ListViewRestoreService.CreateFromTab(tab, _selectionUserVersion, _scrollUserVersion);
    }

    private ListViewRestoreState? CreateNavigationViewRestoreState(
        FolderTab tab,
        string path,
        NavigationKind navigationKind)
    {
        if (navigationKind == NavigationKind.Up)
        {
            var sourcePath = tab.Navigation.CurrentPath;
            bool hasSavedState = tab.State.TryGetNavigationViewState(
                path,
                NormalizeSortColumn(tab.State.SortColumn),
                tab.State.SortAscending,
                "",
                out var savedViewState);

            if (hasSavedState)
            {
                return new ListViewRestoreState(
                    tab.State.Id,
                    path,
                    savedViewState.SelectedPaths,
                    savedViewState.SelectedPaths.FirstOrDefault(),
                    null,
                    savedViewState.VerticalOffset,
                    _selectionUserVersion,
                    _scrollUserVersion);
            }
            else
            {
                return new ListViewRestoreState(
                    tab.State.Id,
                    path,
                    [sourcePath],
                    sourcePath,
                    null,
                    0,
                    _selectionUserVersion,
                    _scrollUserVersion);
            }
        }

        if (navigationKind is NavigationKind.Back or NavigationKind.Forward)
        {
            var sourcePath = tab.Navigation.CurrentPath;
            bool hasSavedState = tab.State.TryGetNavigationViewState(
                path,
                NormalizeSortColumn(tab.State.SortColumn),
                tab.State.SortAscending,
                tab.State.FilterText,
                out var viewState);

            if (hasSavedState)
            {
                tab.State.VerticalOffset = viewState.VerticalOffset;
                tab.State.SelectedPaths = viewState.SelectedPaths.ToList();
                return new ListViewRestoreState(
                    tab.State.Id,
                    path,
                    viewState.SelectedPaths,
                    viewState.SelectedPaths.FirstOrDefault(),
                    null,
                    viewState.VerticalOffset,
                    _selectionUserVersion,
                    _scrollUserVersion);
            }
            else
            {
                var fallbackPaths = IsItemDirectChildOfFolder(sourcePath, path)
                    ? new List<string> { sourcePath }
                    : new List<string>();
                return new ListViewRestoreState(
                    tab.State.Id,
                    path,
                    fallbackPaths,
                    fallbackPaths.FirstOrDefault(),
                    null,
                    0,
                    _selectionUserVersion,
                    _scrollUserVersion);
            }
        }

        return null;
    }


    private async Task<bool> RestoreTabFromCacheAsync(FolderTab tab)
    {
        var targetState = tab.State;
        if (_isLoading || !string.IsNullOrEmpty(_loadingStateId))
        {
            _performanceLogger.Write($"cache-restore-skip reason=loading stateId={targetState.Id} activeStateId={_activeStateId ?? ""} loadingStateId={_loadingStateId ?? ""}");
            return true;
        }

        var cachedItems = tab.CachedItems;
        if (!tab.HasCachedItemsForCurrentPath || cachedItems is null)
        {
            _performanceLogger.Write($"cache-restore-miss stateId={targetState.Id} path=\"{targetState.CurrentPath}\" cachedPath=\"{tab.CachedPath}\"");
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GetProcessWorkingSetBytes();
        _loadCancellation?.Cancel();
        _loadController.IncrementLoadGeneration();
        _loadingStateId = null;
        _activeStateId = targetState.Id;
        SetColumnHeadersForPath(tab.Navigation.CurrentPath);
        tab.Navigation.SetCurrentPath(tab.Navigation.CurrentPath);
        targetState.CurrentPath = tab.Navigation.CurrentPath;
        tab.RefreshHeader();
        UpdatePathDisplay(tab.Navigation.CurrentPath);
        LoadingProgress.Visibility = Visibility.Collapsed;
        _isLoading = false;
        _statusSummaryCoordinator.StatusMessagePrefix = null;
        UpdateWindowTitle();

        MutateItemsForLoad(() =>
        {
            _items.Clear();
            _items.AddRange(cachedItems);
        });
        _itemsOwnerStateId = targetState.Id;
        ApplyTabSort(targetState);
        RefreshCurrentFolderSummary();
        RestoreSelection(targetState.SelectedPaths);
        UpdateNavigationButtons();
        UpdateFolderWatchForOpenTabs();
        await RestoreScrollOffsetAsync(targetState.VerticalOffset);
        stopwatch.Stop();
        var memoryAfter = GetProcessWorkingSetBytes();
        _performanceLogger.Write($"cache-restore-applied stateId={targetState.Id} paneId={targetState.PaneId} path=\"{targetState.CurrentPath}\" items={_items.Count} currentOperationAddRangeItems={cachedItems.Count} currentOperationAddRangeCalls=1 updatesScope=last-diagnostic-reset selected={targetState.SelectedPaths.Count} offset={targetState.VerticalOffset:N1} elapsedMs={stopwatch.ElapsedMilliseconds} memoryDeltaMb={(memoryAfter - memoryBefore) / 1024d / 1024d:N1} memory={GetProcessMemoryStatus()} updates={GetUpdateDiagnosticsStatus()} status={GetStatusDiagnosticsStatus()}");
        return true;
    }


    private double GetCurrentVerticalOffset()
    {
        return ListViewRestoreService.GetCurrentVerticalOffset(FindItemsScrollViewer);
    }

    private void SaveNavigationViewState(FolderTab tab)
    {
        var filterText = ReferenceEquals(ActiveTab, tab)
            ? FilterBox.Text
            : tab.State.FilterText;
        var verticalOffset = ReferenceEquals(ActiveTab, tab)
            ? GetCurrentVerticalOffset()
            : tab.State.VerticalOffset;
        var selectedPaths = ReferenceEquals(ActiveTab, tab)
            ? ItemsList.SelectedItems
                .OfType<FileEntry>()
                .Select(entry => entry.FullPath)
                .ToList()
            : tab.State.SelectedPaths;
        SaveNavigationViewState(tab, tab.Navigation.CurrentPath, filterText, verticalOffset, selectedPaths);
    }

    private void SaveWorkspacePaneNavigationViewState(FolderPane pane, FolderTab tab)
    {
        var verticalOffset = GetFolderPaneVerticalOffset(pane);
        var selectedPaths = GetFolderPaneListView(pane)?.SelectedItems
            .OfType<FileEntry>()
            .Select(entry => entry.FullPath)
            .ToList()
            ?? pane.SelectedPaths;
        pane.ScrollOffset = verticalOffset;
        tab.State.VerticalOffset = verticalOffset;
        tab.State.SelectedPaths = selectedPaths;
        SaveNavigationViewState(tab, tab.Navigation.CurrentPath, tab.State.FilterText, verticalOffset, selectedPaths);

        if (_performanceLogger.IsEnabled)
        {
            var firstPath = selectedPaths.Count > 0 ? selectedPaths[0] : "";
            var lastPath = selectedPaths.Count > 0 ? selectedPaths[^1] : "";
            _performanceLogger.Write($"workspace-save-state paneId={pane.Id} path=\"{tab.Navigation.CurrentPath}\" offset={verticalOffset} selectedCount={selectedPaths.Count} first=\"{firstPath}\" last=\"{lastPath}\"");
        }
    }

    private NavigationViewState GetWorkspacePaneNavigationViewState(
        FolderTab tab,
        string path,
        NavigationKind navigationKind)
    {
        if (navigationKind == NavigationKind.Up)
        {
            bool hasSavedState = tab.State.TryGetNavigationViewState(
                path,
                NormalizeSortColumn(tab.State.SortColumn),
                tab.State.SortAscending,
                "",
                out var savedViewState);

            if (hasSavedState)
            {
                return savedViewState;
            }
            else
            {
                return new NavigationViewState(0, [tab.Navigation.CurrentPath]);
            }
        }

        if (navigationKind is NavigationKind.Back or NavigationKind.Forward)
        {
            bool hasSavedState = tab.State.TryGetNavigationViewState(
                path,
                NormalizeSortColumn(tab.State.SortColumn),
                tab.State.SortAscending,
                "",
                out var viewState);

            if (hasSavedState)
            {
                return viewState;
            }
            else
            {
                var fallbackPaths = IsItemDirectChildOfFolder(tab.Navigation.CurrentPath, path)
                    ? new List<string> { tab.Navigation.CurrentPath }
                    : new List<string>();
                return new NavigationViewState(0, fallbackPaths);
            }
        }

        return NavigationViewState.Empty;
    }

    private void SaveNavigationViewState(
        FolderTab tab,
        string path,
        string? filterText,
        double verticalOffset,
        IReadOnlyList<string> selectedPaths)
    {
        tab.State.SaveNavigationViewState(
            path,
            NormalizeSortColumn(tab.State.SortColumn),
            tab.State.SortAscending,
            filterText,
            verticalOffset,
            selectedPaths);
    }

    private double GetFolderPaneVerticalOffset(FolderPane pane)
    {
        if (GetFolderPaneListView(pane) is { } listView
            && FindVisualChild<ScrollViewer>(listView) is { } scrollViewer)
        {
            return scrollViewer.VerticalOffset;
        }

        return pane.ScrollOffset;
    }




    private async Task RestoreScrollOffsetAsync(double offset)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            FindItemsScrollViewer()?.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.ContextIdle);
    }

    private void RestoreSelection(IReadOnlyList<string> paths)
    {
        _suppressSelectionStatusUpdates = true;
        try
        {
            _listViewRestore.RestoreSelection(ItemsList, _items, paths, UpdateSelectedItemStatus);
        }
        finally
        {
            Dispatcher.InvokeAsync(
                () => _suppressSelectionStatusUpdates = false,
                DispatcherPriority.ContextIdle);
        }
    }

    private async Task RestoreActiveTabAsync()
    {
        if (GetSelectedWorkspaceSession() is not { } targetSession)
        {
            _performanceLogger.Write($"tab-restore-skip reason=no-selected-tab selectedIndex={TabsControl.SelectedIndex} tabCount={_workspaceSessions.Count}");
            return;
        }

        await RestoreWorkspaceTabAsync(targetSession);
    }

    private async Task RestoreWorkspaceTabAsync(WorkspaceSession workspaceSession)
    {
        UpdateCrashContextSnapshot("workspace-tab-restore");
        var result = _workspaceController.TrySelectSession(_activeWorkspaceSession, workspaceSession);
        if (!result.Success)
        {
            return;
        }

        if (result.RequiresSaveActiveLocalState)
        {
            _workspaceLocalState.SaveActiveLocalState();
        }

        if (result.ActiveSessionChanged)
        {
            CancelActiveLoadForWorkspaceSwitch(workspaceSession, "workspace-restore");
        }

        var activeSession = result.ActiveSession!;
        _activeWorkspaceSession = activeSession;
        UpdateActiveWorkspaceSessionUi(activeSession);
        ApplyWorkspaceSessionToFolderTabs();
        _workspacePaneGroups.Clear();
        foreach (var paneGroup in workspaceSession.PaneGroups)
        {
            _workspacePaneGroups.Add(paneGroup);
        }

        _activeWorkspacePaneGroup = workspaceSession.ActivePaneGroup
            ?? workspaceSession.PaneGroups.FirstOrDefault()
            ?? _primaryPaneGroup;
        if (workspaceSession.ActivePaneGroup is null && _activeWorkspacePaneGroup is not null)
        {
            workspaceSession.ActivePaneGroup = _activeWorkspacePaneGroup;
        }

        _workspacePaneUiController.ShowWorkspace(workspaceSession.ActivePaneGroup);
        UpdatePathDisplay(workspaceSession.RootPath);
        RefreshWorkspaceDisplayPanes();
        _performanceLogger.Write($"workspace-tab-restore sessionId={workspaceSession.Id} root=\"{workspaceSession.RootPath}\" panes={workspaceSession.PaneGroups.Count} selected={TabsControl.SelectedIndex}");
        await LoadWorkspaceDisplayPanesOnSwitchAsync();

        if (workspaceSession.IsWorkspace)
        {
            foreach (var pane in _workspaceDisplayPanes)
            {
                ApplyColumnSettingsToWorkspacePane(pane);
            }
        }
        else
        {
            ApplyColumnSettings();
        }

        UpdateWindowTitle();

        var activePane = GetActiveFolderPane();
        if (activePane is not null)
        {
            RestoreActiveWorkspacePaneFocus(activePane);
        }
    }

    private void RestoreActiveWorkspacePaneFocus(FolderPane activePane)
    {
        var listView = GetFolderPaneListView(activePane);
        if (listView is null || !listView.IsVisible || !listView.IsEnabled)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsActive)
            {
                return;
            }

            if (GetSelectedInternalPage() is not null)
            {
                return;
            }

            if (IsRenameInteractionActive() || _isFileDragInProgress)
            {
                return;
            }

            if (Keyboard.FocusedElement is DependencyObject focusedElement)
            {
                if (focusedElement is TextBox
                    || FindVisualParent<ContextMenu>(focusedElement) is not null
                    || FindVisualParent<MenuItem>(focusedElement) is not null)
                {
                    return;
                }
            }

            var currentListView = GetFolderPaneListView(activePane);
            if (currentListView is null || !currentListView.IsVisible || !currentListView.IsEnabled)
            {
                return;
            }

            currentListView.Focus();
            Keyboard.Focus(currentListView);
        }), DispatcherPriority.ContextIdle);
    }

    private async void DeviceChangeService_DrivesChanged()
    {
        await RequestDriveListRefreshAsync();
    }

    private async Task RequestDriveListRefreshAsync()
    {
        if (ActiveNavigation is not { } navigation
            || !SpecialLocationService.IsSpecialUri(navigation.CurrentPath))
        {
            return;
        }

        _driveListRefreshPending = true;
        await ProcessPendingDriveListRefreshAsync();
    }

    private async Task ProcessPendingDriveListRefreshAsync()
    {
        if (!_driveListRefreshPending || _driveListRefreshRunning)
        {
            return;
        }

        if (ActiveNavigation is not { } navigation
            || !SpecialLocationService.IsSpecialUri(navigation.CurrentPath))
        {
            _driveListRefreshPending = false;
            return;
        }

        if (!CanRefreshDriveListFromDeviceChange())
        {
            _performanceLogger.Write($"drive-change-refresh-deferred loading={_isLoading} rename={IsRenameInteractionActive()} drag={_isFileDragInProgress} fileOperation={_isFileOperationInProgress} selecting={_selectionInteraction.IsSelecting} autoScroll={_scrollBehavior.IsAutoScrolling}");
            return;
        }

        _driveListRefreshRunning = true;
        _driveListRefreshPending = false;
        try
        {
            _performanceLogger.Write("drive-change-refresh path=\"special://this-pc\"");
            await NavigateToFolderAsync(SpecialLocationService.ThisPcUri, NavigationKind.Refresh);
        }
        finally
        {
            _driveListRefreshRunning = false;
        }
    }

    private bool CanRefreshDriveListFromDeviceChange()
    {
        return InputSuppressionService.CanProcessBackgroundRefresh(GetInputBusyState())
            && ActiveNavigation is { } navigation
            && SpecialLocationService.IsSpecialUri(navigation.CurrentPath);
    }



    private static string NormalizePathForComparison(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        try
        {
            if (SpecialLocationService.IsSpecialUri(path))
            {
                return path.TrimEnd('/', '\\').ToLowerInvariant();
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return EnsureTrailingSlash(fullPath).ToLowerInvariant();
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch
        {
            return path.TrimEnd('/', '\\').ToLowerInvariant();
        }
    }

    private static string EnsureTrailingSlash(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }
        return path + Path.DirectorySeparatorChar;
    }

    private FolderPane? FindPaneForTab(FolderTab tab)
    {
        if (tab is null) return null;
        if (_primaryPaneGroup?.Tabs.Contains(tab) == true)
        {
            return _primaryPaneGroup;
        }
        foreach (var pane in _workspaceDisplayPanes)
        {
            if (pane.Tabs.Contains(tab))
            {
                return pane;
            }
        }
        return null;
    }


    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.ToString("O") ?? "";
    }

    private async Task<bool> EnsureActiveFolderReadyForOperationAsync(string operation)
    {
        if (ActiveNavigation is not { } navigation || ActiveTab is not { } activeTab)
        {
            _performanceLogger.Write($"operation-blocked-no-active-tab operation={operation} selectedIndex={TabsControl.SelectedIndex} tabCount={_workspaceSessions.Count}");
            return false;
        }

        if (SpecialLocationService.IsSpecialUri(navigation.CurrentPath))
        {
            return true;
        }

        if (activeTab.IsDisconnected)
        {
            ShowDisconnectedStatus();
            _performanceLogger.Write($"operation-blocked-disconnected operation={operation} path=\"{navigation.CurrentPath}\" reason=tab-state");
            return false;
        }

        return await EnsurePathReadyForOperationAsync(navigation.CurrentPath, operation);
    }

    private async Task<bool> EnsurePathReadyForOperationAsync(string path, string operation)
    {
        if (SpecialLocationService.IsSpecialUri(path))
        {
            return true;
        }

        var availability = await _driveAvailabilityService.CheckAsync(path);
        var activePath = ActiveNavigation?.CurrentPath;
        if (availability.IsAvailable)
        {
            if (string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase))
            {
                ClearActiveLocationDisconnected();
            }

            return true;
        }

        if (string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase))
        {
            MarkActiveLocationDisconnected(availability, operation);
        }
        else
        {
            ShowDisconnectedStatus();
            _performanceLogger.Write($"operation-blocked-disconnected operation={operation} path=\"{path}\" root=\"{availability.RootPath}\" exists={availability.RootExists} ready={availability.IsReady} error=\"{availability.Error}\"");
        }

        return false;
    }

    private void MarkActiveLocationDisconnected(DriveAvailabilityResult availability, string reason)
    {
        if (ActiveTab is not { } activeTab || ActiveNavigation is not { } navigation)
        {
            return;
        }

        activeTab.MarkDisconnected();
        UpdateFolderWatchForOpenTabs();
        ShowDisconnectedStatus();
        UpdateNavigationButtons();
        _performanceLogger.Write($"location-disconnected reason={reason} path=\"{navigation.CurrentPath}\" root=\"{availability.RootPath}\" exists={availability.RootExists} ready={availability.IsReady} error=\"{availability.Error}\"");
    }

    private void ClearActiveLocationDisconnected()
    {
        if (ActiveTab is not { } activeTab || ActiveNavigation is not { } navigation)
        {
            return;
        }

        if (activeTab.IsDisconnected)
        {
            _performanceLogger.Write($"location-reconnected path=\"{navigation.CurrentPath}\"");
        }

        activeTab.ClearDisconnected();
    }

    private void RefreshItemsView(string reason)
    {
        _itemsViewRefreshCount++;
        IncrementReason(_itemsViewRefreshReasons, reason);
        if (_isLoading)
        {
            _performanceLogger.Write($"view-refresh-during-load id={_diagnosticLoadId} reason={reason} count={_itemsViewRefreshCount} items={_items.Count} path=\"{_currentLoadPath}\"");
        }

        ItemsView.Refresh();
    }

    private void ResetUpdateDiagnostics(int loadId)
    {
        _collectionChangedCount = 0;
        _collectionAddCount = 0;
        _collectionResetCount = 0;
        _collectionRemoveCount = 0;
        _itemsViewRefreshCount = 0;
        _sortApplyCount = 0;
        _sortClearCount = 0;
        _filterPredicateCount = 0;
        _fileEntryPropertyChangedCount = 0;
        _fileEntryIconPropertyChangedCount = 0;
        _statusSummaryCoordinator.ResetDiagnostics();
        _itemsViewRefreshReasons.Clear();
        _fileEntryPropertyChangedNames.Clear();
#if DEBUG
        _selectionChangedCount = 0;
        _scrollChangedCount = 0;
        _requestBringIntoViewCount = 0;
        _findScrollViewerCount = 0;
        _scrollBehavior.ResetDiagnostics();
#endif
    }

    private string GetUpdateDiagnosticsStatus()
    {
        var status = $"collectionChanged={_collectionChangedCount},collectionAdd={_collectionAddCount},collectionReset={_collectionResetCount},collectionRemove={_collectionRemoveCount},addRangeCalls={_addRangeCallCount},addRangeItems={_addRangeItemCount},viewRefresh={_itemsViewRefreshCount},refreshReasons={FormatDiagnosticCounts(_itemsViewRefreshReasons)},sortApply={_sortApplyCount},sortClear={_sortClearCount},filterPredicate={_filterPredicateCount},fileEntryPropertyChanged={_fileEntryPropertyChangedCount},iconPropertyChanged={_fileEntryIconPropertyChangedCount},propertyNames={FormatDiagnosticCounts(_fileEntryPropertyChangedNames)}";
#if DEBUG
        status += $",selectionChanged={_selectionChangedCount},scrollChanged={_scrollChangedCount},requestBringIntoView={_requestBringIntoViewCount},findScrollViewer={_findScrollViewerCount},scrollTick={_scrollBehavior.DiagnosticTickCount}";
#endif
        return status;
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_diagnosticLoadId == 0)
        {
            return;
        }

        _collectionChangedCount++;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                _collectionAddCount++;
                break;
            case NotifyCollectionChangedAction.Reset:
                _collectionResetCount++;
                break;
            case NotifyCollectionChangedAction.Remove:
                _collectionRemoveCount++;
                break;
        }
    }

    private void FileEntry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_diagnosticLoadId == 0)
        {
            return;
        }

        var propertyName = string.IsNullOrWhiteSpace(e.PropertyName) ? "(empty)" : e.PropertyName!;
        _fileEntryPropertyChangedCount++;
        IncrementReason(_fileEntryPropertyChangedNames, propertyName);
        if (string.Equals(propertyName, nameof(FileEntry.Icon), StringComparison.Ordinal))
        {
            _fileEntryIconPropertyChangedCount++;
        }
    }

    private static void IncrementReason(Dictionary<string, int> counts, string reason)
    {
        counts.TryGetValue(reason, out var count);
        counts[reason] = count + 1;
    }

    private static string FormatDiagnosticCounts(Dictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        return string.Join("|", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{pair.Value}"));
    }

    private async Task RefreshItemsViewLayoutAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            RefreshItemsView("refresh-layout");
            ItemsList.Items.Refresh();
            ItemsList.UpdateLayout();
        }, DispatcherPriority.ContextIdle);
    }

    private void ConfigureBreadcrumbDragButton(Button button)
    {
        button.PreviewMouseLeftButtonDown += BreadcrumbButton_PreviewMouseLeftButtonDown;
        button.PreviewMouseMove += BreadcrumbButton_PreviewMouseMove;
    }

    private void BreadcrumbButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _breadcrumbDragStartPoint = e.GetPosition(this);
    }

    private void BreadcrumbButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _breadcrumbDragStartPoint is not { } startPoint
            || sender is not Button { Tag: string targetPath }
            || !IsTabDropDirectoryPath(targetPath))
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - startPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(BreadcrumbFolderDragFormat, targetPath);
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Link);
        }
        finally
        {
            _breadcrumbDragStartPoint = null;
            ClearWorkspacePaneSubTabHover();
            ClearMainTabHover();
        }
        e.Handled = true;
    }

    private InputBusyState GetInputBusyState()
    {
        return new InputBusyState(
            _isLoading,
            IsRenameInteractionActive(),
            _isFileDragInProgress,
            _isFileOperationInProgress,
            _selectionInteraction.IsSelecting,
            _scrollBehavior.IsAutoScrolling);
    }

    private static int? GetTabShortcutIndex(Key key)
    {
        if (key is >= Key.D1 and <= Key.D9)
        {
            return (int)key - (int)Key.D1;
        }

        if (key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            return (int)key - (int)Key.NumPad1;
        }

        return null;
    }

    private static void FocusAndSelectTextBox(TextBox textBox)
    {
        textBox.Focus();
        Keyboard.Focus(textBox);
        textBox.SelectAll();
    }


    private static bool IsAltKey(KeyEventArgs e)
    {
        return e.Key is Key.LeftAlt or Key.RightAlt
            || e.SystemKey is Key.LeftAlt or Key.RightAlt
            || e.ImeProcessedKey is Key.LeftAlt or Key.RightAlt;
    }

    private void LogPreviewKeyInput(string phase, KeyEventArgs e, bool trackedAltDown)
    {
        var modifiers = Keyboard.Modifiers;
        var shouldLog = (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt
            || trackedAltDown
            || IsAltKey(e)
            || e.Key is Key.Left or Key.Right
            || e.SystemKey is Key.Left or Key.Right
            || e.ImeProcessedKey is Key.Left or Key.Right
            || e.Key is Key.BrowserBack or Key.BrowserForward
            || e.SystemKey is Key.BrowserBack or Key.BrowserForward
            || e.ImeProcessedKey is Key.BrowserBack or Key.BrowserForward;

        if (!shouldLog)
        {
            return;
        }

        _performanceLogger.Write($"preview-key phase={phase} key={e.Key} systemKey={e.SystemKey} imeProcessedKey={e.ImeProcessedKey} modifiers={modifiers} trackedAltDown={trackedAltDown}");
    }

    private static bool IsInsideListViewItem(DependencyObject? source)
    {
        return FindVisualParent<ListViewItem>(source) is not null;
    }

    private FileEntry? GetFileEntryFromNameHitTarget(DependencyObject? source)
    {
        return _fileListHitTest.GetFileEntryFromNameHitTarget(source);
    }

    private static FileEntry? GetFileEntryFromItemHitTarget(DependencyObject? source)
    {
        return FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
    }

    private FileEntry? GetFileEntryFromRenameHitTarget(DependencyObject? source)
    {
        return _fileListHitTest.GetFileEntryFromRenameHitTarget(source);
    }

    private FileEntry? GetFileEntryFromDoubleClickHit(DependencyObject? source, Point itemsListPosition)
    {
        return _fileListHitTest.GetFileEntryFromDoubleClickHit(source, itemsListPosition);
    }

    private double GetVisibleColumnsWidth()
    {
        return ItemsList.View is GridView gridView
            ? gridView.Columns.Cast<GridViewColumn>().Sum(column => Math.Max(0, column.ActualWidth))
            : ItemsList.ActualWidth;
    }

    private bool IsFileListBackgroundHit(DependencyObject? source)
    {
        return _fileListHitTest.IsFileListBackgroundHit(source);
    }

    private bool IsInsideRenameTextBox(DependencyObject? source)
    {
        return _fileListHitTest.IsInsideRenameTextBox(source);
    }

    private bool IsInsideActiveRenameTextBox(DependencyObject? source)
    {
        if (_activeRenameEntry?.IsRenaming != true || source is null)
        {
            return false;
        }

        if (_activeRenameTextBox is not null && IsDescendantOf(source, _activeRenameTextBox))
        {
            return true;
        }

        var textBox = source as TextBox ?? FindVisualParent<TextBox>(source);
        return textBox?.DataContext is FileEntry entry
            && ReferenceEquals(entry, _activeRenameEntry)
            && entry.IsRenaming;
    }

    private void TrackActiveRenameTextBoxMouseDown(DependencyObject? source)
    {
        _activeRenameTextBoxMouseDown = IsInsideActiveRenameTextBox(source);
        if (_activeRenameTextBoxMouseDown)
        {
            var textBox = source as TextBox ?? FindVisualParent<TextBox>(source);
            if (textBox is not null)
            {
                PrepareRenameTextBoxMouseInput(textBox);
            }
            else
            {
                ClearFileDragStart();
                ClearPendingRenameClick();
                ClearWorkspacePanePendingMouseInput();
            }
        }
    }

    private bool TryCommitFileRenameOnExternalMouseDown(DependencyObject? source)
    {
        if (_activeRenameEntry is not { IsRenaming: true } entry)
        {
            return false;
        }

        if (IsInsideActiveRenameTextBox(source))
        {
            return true;
        }

        var textBox = _activeRenameTextBox;
        _activeRenameTextBoxMouseDown = false;
        if (textBox?.DataContext is FileEntry textBoxEntry && ReferenceEquals(textBoxEntry, entry))
        {
            entry.RenameText = textBox.Text;
        }

        _ = CommitFileRenameFromExternalMouseDownAsync(entry, textBox);
        return true;
    }

    private async Task CommitFileRenameFromExternalMouseDownAsync(FileEntry entry, TextBox? textBox)
    {
        try
        {
            var committed = await CommitRenameAsync(entry);
            if (!committed && entry.IsRenaming && textBox is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    textBox.Focus();
                    Keyboard.Focus(textBox);
                }, DispatcherPriority.ContextIdle);
            }
        }
        catch (Exception ex)
        {
            LogException("file-rename-external-mousedown", ex);
        }
    }

    private void PrepareRenameTextBoxMouseInput(TextBox textBox)
    {
        _activeRenameTextBox = textBox;
        _activeRenameTextBoxMouseDown = true;
        ClearFileDragStart();
        ClearPendingRenameClick();
        ClearWorkspacePanePendingMouseInput();
    }

    private void ClearWorkspacePanePendingMouseInput()
    {
        _workspacePendingRangeSelectionStartPoint = null;
        _workspacePendingRangeSelectionClickEntry = null;
        _workspacePendingRangeSelectionStartAdditive = false;
        _workspacePendingRangeSelectionListView = null;
        _workspacePendingRangeSelectionPane = null;
        _pendingSingleSelectionClickEntry = null;
        _pendingSingleSelectionClickPoint = null;
        _pendingSingleSelectionClickPane = null;
        _pendingSingleSelectionClickListView = null;
        ClearFileDragStart();
        ClearPendingRenameClick();
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsInsideScrollBar(DependencyObject? source)
    {
        return _fileListHitTest.IsInsideScrollBar(source);
    }

    private bool IsInsideItemsList(DependencyObject? source)
    {
        return _fileListHitTest.IsInsideItemsList(source);
    }

    private IReadOnlyList<FileEntry> GetSelectedEntries()
    {
        return GetActiveFolderPane() is { } pane
            ? GetSelectedEntries(pane)
            : [];
    }

    private IReadOnlyList<FileEntry> GetSelectedEntries(FolderPane pane)
    {
        if (GetFolderPaneListView(pane) is { } listView)
        {
            return listView.SelectedItems.OfType<FileEntry>().ToList();
        }

        var selectedPaths = pane.SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pane.FileList.Items
            .Where(entry => selectedPaths.Contains(entry.FullPath))
            .ToList();
    }

    private void SelectAllVisibleItems()
    {
        if (GetActiveFolderPane() is { } activePane && IsWorkspaceDisplayPane(activePane))
        {
            if (GetFolderPaneListView(activePane) is { } listView)
            {
                listView.SelectAll();
                listView.Focus();
                SyncPaneSelectionFromListView(activePane, listView);
            }

            return;
        }

        ItemsList.SelectAll();
        ItemsList.Focus();
        UpdateSelectedItemStatus();
    }

    private string GetDiagnosticViewMode()
    {
        if (_devListPerfOptions.DiagnosticRowStyleEnabled)
        {
            return "gridview-diagnostic-style";
        }

        return (ActiveTab?.State.ViewMode ?? _settingsService.Settings.DisplayMode).ToString().ToLowerInvariant();
    }

    private int GetRealizedChildCount()
    {
        return FindVisualChild<VirtualizingStackPanel>(ItemsList)?.Children.Count ?? -1;
    }

    private ScrollViewer? FindItemsScrollViewer()
    {
#if DEBUG
        if (_diagnosticLoadId != 0)
        {
            _findScrollViewerCount++;
        }
#endif
        return FindVisualChild<ScrollViewer>(ItemsList);
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var result = FindVisualChild<T>(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T typedSource)
            {
                return typedSource;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private sealed record FileDragItem(string SourcePath, string Name, bool IsDirectory, bool IsTabOnly = false);

    private sealed record ClosedSubTabState(string PaneId, ClosedTabState TabState);

    private sealed record MainTabDemotionTarget(
        WorkspaceSession DraggedSession,
        FolderPane TargetPane);

    private sealed record ClosedWorkspaceSessionState(WorkspaceSession Session, int Index);

    private enum LastClosedKind
    {
        None,
        MainSession,
        SubTab
    }

    private sealed class WorkspaceRangeSelectionAdorner : Adorner
    {
        private static readonly Brush SelectionFill = new SolidColorBrush(Color.FromArgb(0x33, 0x5D, 0x9C, 0xEC));
        private static readonly Pen SelectionStroke = new(new SolidColorBrush(Color.FromArgb(0xCC, 0x5D, 0x9C, 0xEC)), 1);
        private Rect _selectionRect = Rect.Empty;

        public WorkspaceRangeSelectionAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public void Update(Rect selectionRect)
        {
            _selectionRect = selectionRect;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_selectionRect.IsEmpty)
            {
                return;
            }

            drawingContext.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize)));
            drawingContext.DrawRectangle(SelectionFill, SelectionStroke, _selectionRect);
            drawingContext.Pop();
        }
    }

    private string? GetCurrentActivePath()
    {
        return GetActiveNavigationContext().Path;
    }

    private string? GetCurrentActivePathForTitle()
    {
        return GetActiveNavigationContext().Path;
    }

    private void UpdateWindowTitle()
    {
        if (GetSelectedInternalPage() is { } internalPage)
        {
            Title = $"FileKakari - {internalPage.Title}";
            return;
        }

        var path = GetCurrentActivePathForTitle();
        Title = string.IsNullOrWhiteSpace(path)
            ? "FileKakari"
            : $"FileKakari - {path}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.UpdateWindowTitleBar(this);
    }

    private void SaveWorkspacePanesViewState()
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_performanceLogger.IsEnabled)
        {
            var activeSession = GetSelectedWorkspaceSession();
            var layoutDump = activeSession != null ? DebugDumpLayout(activeSession.LayoutRoot) : "null";
            var displayLayoutDump = activeSession != null ? DebugDumpLayout(activeSession.DisplayLayoutRoot) : "null";
            var displayPaneIds = string.Join(",", _workspaceDisplayPanes.Select(p => p.Id));
            var paneGroupIds = string.Join(",", _workspacePaneGroups.Select(p => p.Id));
            var activeSessionId = _activeWorkspaceSession?.Id ?? "null";

            _performanceLogger.Write($"workspace-save-viewstate-debug " +
                $"activeSessionId={activeSessionId} " +
                $"displayPanes=[{displayPaneIds}] " +
                $"paneGroups=[{paneGroupIds}] " +
                $"layoutRoot=\"{layoutDump}\" " +
                $"displayLayoutRoot=\"{displayLayoutDump}\"");

            if (activeSession != null && activeSession.LayoutRoot is null)
            {
                _performanceLogger.Write($"workspace-save-viewstate-layoutroot-null " +
                    $"sessionId={activeSession.Id} " +
                    $"sessionName=\"{activeSession.Name}\" " +
                    $"_activeWorkspaceSessionId={activeSessionId}");
            }

            foreach (var pane in _workspaceDisplayPanes)
            {
                var listView = GetFolderPaneListView(pane);
                var scrollViewer = listView != null ? FindVisualChild<ScrollViewer>(listView) : null;
                var offset = scrollViewer?.VerticalOffset ?? pane.ScrollOffset;
                var selectedCount = listView?.SelectedItems.Count ?? 0;
                var activeTabStateSelectedCount = pane.ActiveTab?.State.SelectedPaths.Count ?? 0;

                var selectedItem = listView?.SelectedItem;
                var itemType = selectedItem?.GetType().FullName ?? "null";
                var itemPath = "";
                if (selectedItem is FileEntry entry)
                {
                    itemPath = entry.FullPath;
                }
                else if (selectedItem != null)
                {
                    itemPath = selectedItem.ToString();
                }

                var paneSession = _workspaceSessions.FirstOrDefault(s => s.PaneGroups.Any(pg => ReferenceEquals(pg, pane)));
                var paneSessionId = paneSession?.Id ?? "null";

                _performanceLogger.Write($"workspace-pane-save-debug paneId={pane.Id} " +
                    $"paneSessionId={paneSessionId} " +
                    $"activeSessionId={activeSessionId} " +
                    $"listViewExists={listView != null} " +
                    $"scrollViewerExists={scrollViewer != null} " +
                    $"offset={offset} " +
                    $"selectedCount={selectedCount} " +
                    $"activeTabStateSelectedCount={activeTabStateSelectedCount} " +
                    $"selectedItemType=\"{itemType}\" " +
                    $"selectedItemPath=\"{itemPath}\"");
            }
        }

        foreach (var pane in _workspaceDisplayPanes)
        {
            if (pane.ActiveTab is { } tab)
            {
                SaveWorkspacePaneNavigationViewState(pane, tab);
            }
        }
    }


}

public class PaneCountToColumnsConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is int count)
        {
            return Math.Max(1, count);
        }
        return 1;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
