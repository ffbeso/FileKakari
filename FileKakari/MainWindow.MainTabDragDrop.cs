using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FileKakari;

public partial class MainWindow
{
    private void TabsControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        CancelScheduledWorkspaceRenameClick();

        var tabItem = FindVisualParent<TabItem>(source);
        if (tabItem?.DataContext is MainTabItem { IsInternalPage: true })
        {
            ClearTabDragState();
            return;
        }

        if (IsWorkspaceRenameTextBoxTarget(source)
            || IsMainTabCommandTarget(source)
            || e.ClickCount != 1
            || GetWorkspaceSession(tabItem?.DataContext) is not { } session)
        {
            ClearTabDragState();
            return;
        }

        var isCtrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var wasAlreadySingleDisplayed = _displayedWorkspaceSessionIds.Count == 1 && _displayedWorkspaceSessionIds.Contains(session.Id);
        var wasAlreadyActiveSession = IsSameWorkspaceSession(_activeWorkspaceSession, session);
        var isEligibleForRenameBeforeClick = !isCtrlPressed && wasAlreadySingleDisplayed && wasAlreadyActiveSession;

        if (isCtrlPressed)
        {
            ClearTabDragState();
            ClearPendingWorkspaceRenameClick();

            var isCurrentlyDisplayed = _displayedWorkspaceSessionIds.Contains(session.Id);
            if (isCurrentlyDisplayed)
            {
                if (_displayedWorkspaceSessionIds.Count <= 1)
                {
                    StatusText.Text = "少なくとも1つのWorkspaceを表示する必要があります";
                    _performanceLogger.Write($"main-tab-ctrl-click-rejected reason=min-displayed-count sessionId={session.Id}");
                    e.Handled = true;
                    return;
                }

                _displayedWorkspaceSessionIds.Remove(session.Id);
                if (IsSameWorkspaceSession(_activeWorkspaceSession, session))
                {
                    var remainingSession = GetDisplayedWorkspaceSessionsInTabOrder().FirstOrDefault();
                    if (remainingSession is not null)
                    {
                        _activeWorkspaceSession = remainingSession;
                    }
                }
            }
            else
            {
                if (_displayedWorkspaceSessionIds.Count >= 2)
                {
                    StatusText.Text = "同時に表示できるWorkspaceは最大2つです";
                    _performanceLogger.Write($"main-tab-ctrl-click-rejected reason=max-displayed-count sessionId={session.Id}");
                    e.Handled = true;
                    return;
                }

                _displayedWorkspaceSessionIds.Add(session.Id);
                _activeWorkspaceSession = session;
            }

            _isPreservingMultiSelection = true;
            SynchronizeDisplayedWorkspaceState("ctrl-click");
            e.Handled = true;
            return;
        }

        if (!isCtrlPressed)
        {
            ResetToSingleWorkspaceDisplay(session, "normal-tab-click");
        }

        if (isEligibleForRenameBeforeClick
            && IsWorkspaceTabTitleTarget(source)
            && !session.IsRenaming)
        {
            _pendingWorkspaceRenameSession = session;
            _pendingWorkspaceRenamePoint = e.GetPosition(TabsControl);
        }
        else
        {
            ClearPendingWorkspaceRenameClick();
        }

        _draggedTab = session;
        _tabDragStartPoint = e.GetPosition(TabsControl);
        TabsControl.CaptureMouse();
        e.Handled = true;
    }

    private static bool IsWorkspaceRenameTextBoxTarget(DependencyObject? source)
    {
        return GetWorkspaceSession(FindVisualParent<TextBox>(source)?.DataContext) is not null;
    }

    private static bool IsMainTabCommandTarget(DependencyObject? source)
    {
        return FindVisualParent<ButtonBase>(source) is not null;
    }

    private static bool IsWorkspaceTabTitleTarget(DependencyObject? source)
    {
        return FindVisualParent<TextBlock>(source) is { Tag: string tag }
            && string.Equals(tag, "WorkspaceTabTitle", StringComparison.Ordinal);
    }

    private async void TabsControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pendingRenameSession = _pendingWorkspaceRenameSession;
        var shouldBeginRename = e.ChangedButton == MouseButton.Left
            && pendingRenameSession is not null
            && !HasExceededWorkspaceRenamePendingDistance(e.GetPosition(TabsControl))
            && ReferenceEquals(_draggedTab, pendingRenameSession)
            && ReferenceEquals(GetSelectedWorkspaceSession(), pendingRenameSession)
            && !pendingRenameSession.IsRenaming;

        if (_draggedTab is not null)
        {
            var targetSession = _draggedTab;
            var selectedSession = GetSelectedWorkspaceSession();
            var isAlreadySynchronized = IsWorkspaceSessionSelectionSynchronized(targetSession);
            PerfLog.WriteVerbose(
                $"main-tab-preview-mouse-up targetSessionId={targetSession.Id} " +
                $"currentSelectedSessionId={selectedSession?.Id ?? "null"} " +
                $"activeSessionId={_activeWorkspaceSession?.Id ?? "null"} " +
                $"targetIsActiveSession={targetSession.IsActiveSession} " +
                $"alreadySynchronized={isAlreadySynchronized}");

            SelectWorkspaceSession(targetSession);
            if (IsSameWorkspaceSession(selectedSession, targetSession) && !isAlreadySynchronized)
            {
                await RestoreWorkspaceTabAsync(targetSession);
            }

            e.Handled = true;
        }

        ClearTabDragState();

        if (shouldBeginRename && pendingRenameSession is not null)
        {
            ScheduleWorkspaceRenameFromClick(pendingRenameSession);
        }
    }

    private void TabsControl_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedTab is null
            || _tabDragStartPoint is null
            || _workspaceSessions.Count <= 1
            || e.LeftButton != MouseButtonState.Pressed)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ClearTabDragState();
            }

            return;
        }

        var position = e.GetPosition(TabsControl);
        if (Math.Abs(position.X - _tabDragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _tabDragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ClearPendingWorkspaceRenameClick();
        var draggedTab = _draggedTab;
        try
        {
            var data = new DataObject(TabDragFormat, draggedTab);
            e.Handled = true;
            DragDrop.DoDragDrop(TabsControl, data, DragDropEffects.Move);
        }
        finally
        {
            ClearTabDragState();
            ClearMainTabHover();
            HideTabInsertIndicator();
        }
    }

    private void TabsControl_DragOver(object sender, DragEventArgs e)
    {
        var targetTabItem = FindVisualParent<TabItem>(e.OriginalSource as DependencyObject);
        if (targetTabItem?.DataContext is MainTabItem { IsInternalPage: true })
        {
            ClearFileTabHover();
            ClearMainTabHover();
            HideTabInsertIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(SubTabDragFormat))
        {
            ClearFileTabHover();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            var insertTarget = GetMainTabDragInsertDropTarget(e);
            ShowTabInsertIndicator(TabsControl, insertTarget);

            var targetSessionSub = GetDropTargetSession(e);
            if (targetSessionSub is not null)
            {
                QueueMainTabHover(targetSessionSub);
            }
            else
            {
                ClearMainTabHover();
            }
            return;
        }


        if (GetDroppedSession(e) is not null)
        {
            ClearFileTabHover();
            ShowTabInsertIndicator(TabsControl, GetMainTabDragInsertDropTarget(e));
            var targetSessionDrop = GetDropTargetSession(e);
            e.Effects = targetSessionDrop is not null
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;

            if (targetSessionDrop is not null)
            {
                var draggedSession = GetDroppedSession(e);
                if (draggedSession is not null)
                {
                    QueueMainTabHover(targetSessionDrop);
                }
                else
                {
                    ClearMainTabHover();
                }
            }
            else
            {
                ClearMainTabHover();
            }
            return;
        }

        var targetSession = GetDropTargetSession(e);
        var targetTab = targetSession is null ? null : GetSessionActiveTab(targetSession);

        var folderDropPath = GetMainTabFolderDropPath(e);
        if (folderDropPath is not null)
        {
            var insertTarget = GetMainTabInsertDropTarget(e);
            if (insertTarget.IsInsert)
            {
                e.Effects = DragDropEffects.Link;
                ClearMainTabHover();
                ShowTabInsertIndicator(TabsControl, insertTarget);
            }
            else if (targetTabItem is not null)
            {
                e.Effects = DragDropEffects.None;
                HideTabInsertIndicator();
                if (targetSession is not null)
                {
                    QueueMainTabHover(targetSession);
                }
            }
            else
            {
                e.Effects = DragDropEffects.Link;
                ClearMainTabHover();
                ShowTabInsertIndicator(TabsControl, insertTarget);
            }
            ClearFileTabHover();
            e.Handled = true;
            return;
        }

        var dragItems = GetFileOperationDragItems(e);
        if (_draggedTab is not null || targetSession is null || dragItems is null)
        {
            e.Effects = DragDropEffects.None;
            ClearFileTabHover();
            ClearMainTabHover();
            HideTabInsertIndicator();
            e.Handled = true;
            return;
        }

        var isCopyRequested = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var allowedMove = (e.AllowedEffects & DragDropEffects.Move) != 0;
        var allowedCopy = (e.AllowedEffects & DragDropEffects.Copy) != 0;

        DragDropEffects targetEffect;
        if (isCopyRequested && allowedCopy)
        {
            targetEffect = DragDropEffects.Copy;
        }
        else if (allowedMove)
        {
            targetEffect = DragDropEffects.Move;
        }
        else if (allowedCopy)
        {
            targetEffect = DragDropEffects.Copy;
        }
        else
        {
            targetEffect = DragDropEffects.None;
        }

        if (targetEffect == DragDropEffects.None)
        {
            targetEffect = allowedMove ? DragDropEffects.Move : (allowedCopy ? DragDropEffects.Copy : DragDropEffects.None);
        }

        e.Effects = targetEffect;
        QueueFileTabHover(targetSession);
        ClearMainTabHover();
        HideTabInsertIndicator();
        e.Handled = true;
    }



    private void TabsControl_DragLeave(object sender, DragEventArgs e)
    {
        ClearMainTabHover();
        HideTabInsertIndicator();
        VerifyTabsControlDragLeave(e);
    }

    private void VerifyTabsControlDragLeave(DragEventArgs e)
    {
        var captureGen = _fileTabHoverGeneration;
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (captureGen != _fileTabHoverGeneration || _fileTabHoverTargetSessionId is null)
                {
                    return;
                }

                if (!IsPointerInsideTabsControl(e))
                {
                    ClearFileTabHover();
                }
            }));
    }

    private bool IsPointerInsideTabsControl(DragEventArgs e)
    {
        try
        {
            var pos = e.GetPosition(TabsControl);
            if (pos.X >= 0 && pos.X <= TabsControl.ActualWidth && pos.Y >= 0 && pos.Y <= TabsControl.ActualHeight)
            {
                return true;
            }
        }
        catch
        {
            // Fallback to clearing hover
        }

        return false;
    }


    private async void TabsControl_Drop(object sender, DragEventArgs e)
    {
        ClearMainTabHover();
        HideTabInsertIndicator();

        var targetTabItem = FindVisualParent<TabItem>(e.OriginalSource as DependencyObject);
        if (targetTabItem?.DataContext is MainTabItem { IsInternalPage: true })
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(SubTabDragFormat) && _draggedSubTab is { } draggedSubTab && _draggedSubTabPane is { } draggedSubTabPane)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            var subtab = draggedSubTab;
            var pane = draggedSubTabPane;
            var insertTarget = GetMainTabDragInsertDropTarget(e);
            var targetInsertIndex = insertTarget.IsInsert ? insertTarget.InsertIndex : _workspaceSessions.Count;

            ClearSubTabDragState();
            await PromoteSubTabToMainTabAsync(pane, subtab, targetInsertIndex);
            return;
        }


        var draggedSession = GetDroppedSession(e);
        var targetSession = GetDropTargetSession(e);
        if (draggedSession is not null)
        {
            if (targetSession is null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            ReorderSession(draggedSession, targetSession, e.GetPosition);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        ClearFileTabHover();
        var folderDropPath = GetMainTabFolderDropPath(e);
        if (folderDropPath is not null)
        {
            var insertTarget = GetMainTabInsertDropTarget(e);
            if (insertTarget.IsInsert)
            {
                e.Effects = DragDropEffects.Link;
                await CreateNewMainWindowTabAtAsync(folderDropPath, insertTarget.InsertIndex);
            }
            else if (targetTabItem is not null && targetSession is not null)
            {
                e.Effects = DragDropEffects.None;
            }
            else
            {
                e.Effects = DragDropEffects.Link;
                await CreateNewMainWindowTabAtAsync(folderDropPath, _workspaceSessions.Count);
            }
            e.Handled = true;
            return;
        }

        var dragItems = GetFileOperationDragItems(e);
        if (dragItems is not null && targetSession is not null)
        {
            var targetPane = targetSession.ActivePaneGroup ?? targetSession.PaneGroups.FirstOrDefault();
            var targetTab = targetPane?.ActiveTab ?? targetPane?.Tabs.FirstOrDefault();
            var targetDirectory = targetTab?.Navigation.CurrentPath;

            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                var operationKind = GetFileDropOperationKind(e, targetDirectory);
                if (CanDropFileItems(dragItems, targetDirectory, operationKind))
                {
                    var isCopy = operationKind == PendingFileOperationKind.Copy;
                    e.Effects = isCopy ? DragDropEffects.Copy : DragDropEffects.Move;
                    e.Handled = true;

                    var transferItems = dragItems
                        .Select(item => new FileTransferItem(item.SourcePath, item.Name, item.IsDirectory))
                        .ToList();
                    await ExecuteFileTransferAsync(
                        transferItems,
                        targetDirectory,
                        operationKind,
                        refreshActiveFolder: true,
                        refreshTab: targetTab,
                        confirmNonSelfCopy: IsExplicitCopyDrop(e),
                        refreshPane: targetPane);
                    return;
                }
            }
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }


    private string? GetMainTabFolderDropPath(DragEventArgs e)
    {
        return GetSingleExistingDirectoryDropPath(e);
    }

    private TabInsertDropTarget GetMainTabInsertDropTarget(DragEventArgs e)
    {
        var targetItem = FindVisualParent<TabItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is MainTabItem { IsInternalPage: true })
        {
            return TabInsertDropTarget.None;
        }

        if (targetItem is not null && GetWorkspaceSession(targetItem.DataContext) is { } targetSession)
        {
            var targetIndex = _workspaceSessions.IndexOf(targetSession);
            if (targetIndex < 0)
            {
                return TabInsertDropTarget.None;
            }

            var zone = GetTabDropZone(targetItem, e.GetPosition(targetItem));
            if (zone == TabDropZone.Center)
            {
                return new TabInsertDropTarget(false, targetIndex, zone, targetItem);
            }

            var insertIndex = zone == TabDropZone.Before ? targetIndex : targetIndex + 1;
            return new TabInsertDropTarget(true, insertIndex, zone, targetItem);
        }

        return new TabInsertDropTarget(true, _workspaceSessions.Count, TabDropZone.After, GetLastMainTabItem());
    }

    private TabInsertDropTarget GetMainTabDragInsertDropTarget(DragEventArgs e)
    {
        var targetItem = FindVisualParent<TabItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is MainTabItem { IsInternalPage: true })
        {
            return TabInsertDropTarget.None;
        }

        if (targetItem is not null && GetWorkspaceSession(targetItem.DataContext) is { } targetSession)
        {
            var targetIndex = _workspaceSessions.IndexOf(targetSession);
            if (targetIndex < 0)
            {
                return TabInsertDropTarget.None;
            }

            var insertAfterTarget = IsMouseAfterMiddle(targetItem, e.GetPosition(targetItem));
            return new TabInsertDropTarget(
                true,
                insertAfterTarget ? targetIndex + 1 : targetIndex,
                insertAfterTarget ? TabDropZone.After : TabDropZone.Before,
                targetItem);
        }

        return GetMainTabEndInsertDropTarget();
    }

    private TabInsertDropTarget GetMainTabEndInsertDropTarget()
    {
        return new TabInsertDropTarget(true, _workspaceSessions.Count, TabDropZone.After, GetLastMainTabItem());
    }

    private FrameworkElement? GetLastMainTabItem()
    {
        for (var index = _workspaceSessions.Count - 1; index >= 0; index--)
        {
            if (TabsControl.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item)
            {
                return item;
            }
        }

        return null;
    }

    public enum TabStripOrientation
    {
        Horizontal,
        Vertical
    }

    public enum TabDropZone
    {
        Before,
        Center,
        After
    }

    public readonly record struct TabInsertDropTarget(
        bool IsInsert,
        int InsertIndex,
        TabDropZone Zone,
        FrameworkElement? TargetElement,
        TabStripOrientation Orientation = TabStripOrientation.Horizontal)
    {
        public static TabInsertDropTarget None => new(false, -1, TabDropZone.Center, null, TabStripOrientation.Horizontal);
    }

    private static TabDropZone GetTabDropZone(
        FrameworkElement tabItem,
        Point mousePosition,
        TabStripOrientation orientation = TabStripOrientation.Horizontal)
    {
        var size = orientation == TabStripOrientation.Horizontal ? tabItem.ActualWidth : tabItem.ActualHeight;
        var pos = orientation == TabStripOrientation.Horizontal ? mousePosition.X : mousePosition.Y;
        if (size <= 0)
        {
            return TabDropZone.Center;
        }

        if (pos < size * 0.25)
        {
            return TabDropZone.Before;
        }
        else if (pos > size * 0.75)
        {
            return TabDropZone.After;
        }
        else
        {
            return TabDropZone.Center;
        }
    }

    private static bool IsMouseAfterMiddle(
        FrameworkElement tabItem,
        Point mousePosition,
        TabStripOrientation orientation = TabStripOrientation.Horizontal)
    {
        var size = orientation == TabStripOrientation.Horizontal ? tabItem.ActualWidth : tabItem.ActualHeight;
        var pos = orientation == TabStripOrientation.Horizontal ? mousePosition.X : mousePosition.Y;
        return pos > size / 2.0;
    }

    private void ReorderSession(WorkspaceSession draggedSession, WorkspaceSession targetSession, Func<IInputElement, Point> getPosition)
    {
        var targetItem = GetTabItem(targetSession);
        var insertAfterTarget = targetItem is not null && getPosition(targetItem).X > targetItem.ActualWidth / 2;
        var sourceIndex = _workspaceSessions.IndexOf(draggedSession);
        var targetIndex = _workspaceSessions.IndexOf(targetSession);
        if (insertAfterTarget && targetIndex >= 0)
        {
            targetIndex++;
        }

        if (sourceIndex >= 0 && targetIndex > sourceIndex)
        {
            targetIndex--;
        }

        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        SaveActiveTabViewState();

        var selectedSession = ActiveSession;
        _isSwitchingTabs = true;
        try
        {
            var clampedTarget = Math.Clamp(targetIndex, 0, _workspaceSessions.Count - 1);
            _workspaceSessions.Move(sourceIndex, clampedTarget);
            _isPreservingMultiSelection = true;
            SynchronizeDisplayedWorkspaceState("tab-reorder");
            SelectWorkspaceSession(selectedSession);
        }
        finally
        {
            _isSwitchingTabs = false;
        }
    }

    private void QueueFileTabHover(WorkspaceSession? targetSession)
    {
        if (targetSession is null
            || IsSameWorkspaceSession(_activeWorkspaceSession, targetSession)
            || IsSameWorkspaceSession(GetSelectedWorkspaceSession(), targetSession))
        {
            ClearFileTabHover();
            return;
        }


        if (string.Equals(_fileTabHoverTargetSessionId, targetSession.Id, StringComparison.Ordinal)
            && _fileTabHoverTimer.IsEnabled)
        {
            return;
        }

        _fileTabHoverTargetSessionId = targetSession.Id;
        _fileTabHoverGeneration++;
        _fileTabHoverTimer.Stop();
        _fileTabHoverTimer.Start();
    }

    private async void FileTabHoverTimer_Tick(object? sender, EventArgs e)
    {
        _fileTabHoverTimer.Stop();
        var targetSessionId = _fileTabHoverTargetSessionId;
        var currentGeneration = _fileTabHoverGeneration;
        _fileTabHoverTargetSessionId = null;

        if (_draggedTab is not null || string.IsNullOrEmpty(targetSessionId))
        {
            return;
        }

        var targetSession = _workspaceSessions.FirstOrDefault(s => string.Equals(s.Id, targetSessionId, StringComparison.Ordinal));
        if (targetSession is null
            || IsSameWorkspaceSession(_activeWorkspaceSession, targetSession)
            || IsSameWorkspaceSession(GetSelectedWorkspaceSession(), targetSession))
        {
            return;
        }

        await ActivateFileDropHoverWorkspaceAsync(targetSession, currentGeneration);
    }

    private async Task ActivateFileDropHoverWorkspaceAsync(WorkspaceSession targetSession, int generation)
    {
        if (_fileTabHoverGeneration != generation)
        {
            return;
        }

        if (IsSameWorkspaceSession(_activeWorkspaceSession, targetSession)
            && _displayedWorkspaceSessionIds.Count == 1
            && _displayedWorkspaceSessionIds.Contains(targetSession.Id))
        {
            ClearFileTabHover();
            return;
        }

        // Clean hover state & bump generation before performing UI switch
        // so any delayed DragLeave calls from the old layout will be invalidated.
        _fileTabHoverTimer.Stop();
        _fileTabHoverTargetSessionId = null;
        _fileTabHoverGeneration++;

        ResetToSingleWorkspaceDisplay(targetSession, "file-drop-hover");
        SelectWorkspaceSession(targetSession);
        await Task.CompletedTask;
    }

    private void ClearFileTabHover()
    {
        _fileTabHoverTargetSessionId = null;
        _fileTabHoverGeneration++;
        _fileTabHoverTimer.Stop();
    }



    private void QueueMainTabHover(WorkspaceSession targetSession)
    {
        if (ReferenceEquals(GetSelectedWorkspaceSession(), targetSession))
        {
            ClearMainTabHover();
            return;
        }

        var draggedSession = _draggedTab;
        if (draggedSession is not null && ReferenceEquals(draggedSession, targetSession))
        {
            ClearMainTabHover();
            return;
        }

        if (ReferenceEquals(_mainTabHoverTarget, targetSession) && _mainTabHoverTimer.IsEnabled)
        {
            return;
        }

        _mainTabHoverTarget = targetSession;
        _mainTabHoverTimer.Stop();
        _mainTabHoverTimer.Start();
    }

    private void MainTabHoverTimer_Tick(object? sender, EventArgs e)
    {
        _mainTabHoverTimer.Stop();
        var targetSession = _mainTabHoverTarget;
        _mainTabHoverTarget = null;

        if (targetSession is null)
        {
            return;
        }

        if (!_workspaceSessions.Contains(targetSession))
        {
            return;
        }

        if (ReferenceEquals(GetSelectedWorkspaceSession(), targetSession))
        {
            return;
        }

        SelectWorkspaceSession(targetSession);
    }

    private void ClearMainTabHover()
    {
        _mainTabHoverTarget = null;
        _mainTabHoverTimer.Stop();
    }

    private WorkspaceSession? GetDroppedSession(DragEventArgs e)
    {
        return e.Data.GetDataPresent(TabDragFormat)
            ? e.Data.GetData(TabDragFormat) as WorkspaceSession
            : null;
    }

    private static WorkspaceSession? GetDropTargetSession(DragEventArgs e)
    {
        return GetWorkspaceSession(FindVisualParent<TabItem>(e.OriginalSource as DependencyObject)?.DataContext);
    }

    private void ClearTabDragState()
    {
        _tabDragStartPoint = null;
        _draggedTab = null;
        ClearPendingWorkspaceRenameClick();
        HideTabInsertIndicator();
        if (TabsControl.IsMouseCaptured)
        {
            TabsControl.ReleaseMouseCapture();
        }
    }

    private async Task CreateNewMainWindowTabAtAsync(string path, int index)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!SpecialLocationService.IsSpecialUri(path) && !System.IO.Directory.Exists(path))
        {
            throw new System.IO.DirectoryNotFoundException(_text.Get("OpenFailedMissing"));
        }

        var tab = new FolderTab(path, viewMode: _settingsService.Settings.DisplayMode);
        var session = CreateSinglePaneSession(tab);
        var result = _workspaceController.InsertSession(index, _workspaceSessions, _activeWorkspaceSession, session);

        _isSwitchingTabs = true;
        try
        {
            var insertIndex = result.InsertIndex ?? Math.Clamp(index, 0, _workspaceSessions.Count);
            _workspaceSessions.Insert(insertIndex, session);
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
}
