using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileKakari;

public partial class MainWindow
{
    private sealed class FileListInputController
    {
        private readonly MainWindow _owner;
        private Point? _pendingRangeSelectionStartPoint;
        private FileEntry? _pendingRangeSelectionClickEntry;
        private bool _pendingRangeSelectionStartAdditive;
        // 最後に追加・明示クリックした項目をアンカーとする (Selection Anchor)
        private FileEntry? _selectionAnchorEntry;

        public FileListInputController(MainWindow owner)
        {
            _owner = owner;
        }

        public async Task HandleMouseDoubleClickAsync(MouseButtonEventArgs e)
        {
            _owner.CancelPendingRenameClick();
            var source = e.OriginalSource as DependencyObject;
            var position = e.GetPosition(_owner.ItemsList);
            if (_owner.GetFileEntryFromDoubleClickHit(source, position) is not { } entry)
            {
                if (e.ChangedButton == MouseButton.Left
                    && !_owner.IsInsideScrollBar(source)
                    && FindVisualParent<GridViewColumnHeader>(source) is null)
                {
                    _owner.ClearFilterIfNeeded();
                    e.Handled = true;
                }

                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                await _owner.OpenDirectoryInNewTabAsync(entry);
                return;
            }

            e.Handled = true;
            await _owner.OpenEntryAsync(entry);
        }

        public async Task HandlePreviewMouseDownAsync(MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (_owner.IsInsideActiveRenameTextBox(source))
            {
                ClearPendingSelectionInput();
                _owner.ClearFileDragStart();
                _owner.CancelPendingRenameClick();
                return;
            }

            var position = e.GetPosition(_owner.ItemsList);
            if (_owner._isLoading && _owner.IsInsideItemsList(source))
            {
                if (e.ChangedButton is MouseButton.Left or MouseButton.Right)
                {
                    _owner.MarkUserSelectionIntentDuringLoad("mouse-down");
                }

                if (_owner.IsInsideScrollBar(source) || e.ChangedButton == MouseButton.Middle)
                {
                    _owner.MarkUserScrollIntentDuringLoad("mouse-down");
                }
            }

            if (ShouldAllowSingleSelectionInputDuringLoad(e.ChangedButton, e.ClickCount, source))
            {
                _owner.ClearFileDragStart();
                _owner.CancelPendingRenameClick();
                return;
            }

            if (ShouldSuppressItemsListInputDuringLoad(e.ChangedButton, source))
            {
                _owner.ClearFileDragStart();
                _owner.CancelPendingRenameClick();
                _owner.ItemsList.Focus();
                e.Handled = true;
                return;
            }

            if (_owner._scrollBehavior.IsAutoScrolling)
            {
                _owner._scrollBehavior.StopAutoScroll();
                if (e.ChangedButton == MouseButton.Middle
                    && _owner.IsFileListBackgroundHit(source)
                    && !_owner.IsInsideScrollBar(source))
                {
                    e.Handled = true;
                    return;
                }
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                var modifiers = Keyboard.Modifiers;
                var hasSelectionModifier = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
                var hasControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                var hasShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                var clickedEntry = GetFileEntryFromItemHitTarget(source);
                _owner._fileDragStartPoint = null;
                _owner._fileDragStartEntry = null;
                _owner._fileDragStartPane = null;
                _owner._fileDragStartListView = null;
                _owner._rangeSelectionStartPoint = null;
                _owner._rangeSelectionStartAdditive = false;
                _pendingRangeSelectionStartPoint = null;
                _pendingRangeSelectionClickEntry = null;
                _pendingRangeSelectionStartAdditive = false;
                _owner.CancelPendingRenameClick();

                var dragEntry = _owner.GetFileEntryFromNameHitTarget(source);
                if (dragEntry is null && clickedEntry is not null && !_owner.IsInsideRenameTextBox(source))
                {
                    if (e.ClickCount == 1)
                    {
                        _pendingRangeSelectionStartPoint = position;
                        _pendingRangeSelectionClickEntry = clickedEntry;
                        _pendingRangeSelectionStartAdditive = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                        e.Handled = true;
                        return;
                    }
                }

                if (dragEntry is not null
                    && !_owner.IsInsideRenameTextBox(source))
                {
                    if (!hasShift)
                    {
                        _selectionAnchorEntry = dragEntry;
                        if (_owner.GetNormalFolderPane() is { } pane)
                        {
                            _owner.BeginFileDragStart(_owner.ItemsList, pane, dragEntry, position);
                        }
                    }

                    if (e.ClickCount == 1 && _owner.ItemsList.SelectedItems.Contains(dragEntry))
                    {
                        if (!hasSelectionModifier || (hasControl && !hasShift))
                        {
                            _owner._pendingSingleSelectionClickEntry = dragEntry;
                            _owner._pendingSingleSelectionClickPoint = position;
                            if (!hasSelectionModifier
                                && (modifiers & ModifierKeys.Alt) == ModifierKeys.None
                                && _owner.GetFileEntryFromRenameHitTarget(source) is FileEntry renameEntry
                                && ReferenceEquals(renameEntry, dragEntry)
                                && _owner.ItemsList.SelectedItems.Count == 1
                                && !dragEntry.IsRenaming)
                            {
                                _owner._renameInteraction.SetPendingClick(dragEntry, position);
                            }

                            e.Handled = true;
                        }
                    }
                    else if (e.ClickCount >= 2)
                    {
                        _owner.CancelPendingRenameClick();
                    }
                }

                if (e.ClickCount == 1
                    && clickedEntry is not null
                    && hasShift
                    && !_owner.IsInsideRenameTextBox(source))
                {
                    _pendingRangeSelectionStartPoint = position;
                    _pendingRangeSelectionClickEntry = clickedEntry;
                    _pendingRangeSelectionStartAdditive = hasControl;
                    e.Handled = true;
                    return;
                }

                if (e.ClickCount == 1
                    && clickedEntry is not null
                    && hasControl
                    && !hasShift
                    && !_owner.IsInsideRenameTextBox(source))
                {
                    _owner._pendingSingleSelectionClickEntry = clickedEntry;
                    _owner._pendingSingleSelectionClickPoint = position;
                    e.Handled = true;
                    return;
                }

                if (clickedEntry is not null)
                {
                    if (!hasShift
                        && _owner.GetFileEntryFromNameHitTarget(source) is null
                        && !_owner.IsInsideRenameTextBox(source))
                    {
                        _owner._rangeSelectionStartPoint = position;
                        _owner._rangeSelectionStartAdditive = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                    }

                    if (hasSelectionModifier)
                    {
                        _owner.ClearFileDragStart();
                        return;
                    }

                    if (_owner.GetFileEntryFromNameHitTarget(source) is not null)
                    {
                        return;
                    }
                }

                if (hasSelectionModifier)
                {
                    return;
                }
            }

            if (e.ChangedButton == MouseButton.Middle
                && _owner.GetFileEntryFromNameHitTarget(source) is FileEntry entry)
            {
                e.Handled = true;
                await _owner.OpenDirectoryInNewTabAsync(entry);
                return;
            }

            if (e.ChangedButton == MouseButton.Middle
                && _owner.IsFileListBackgroundHit(source)
                && !_owner.IsInsideScrollBar(source))
            {
                e.Handled = true;
                _owner._scrollBehavior.StartAutoScroll(position, dragMode: true);
                return;
            }

            if (e.ChangedButton == MouseButton.Left && _owner.IsFileListBackgroundHit(source))
            {
                var modifiers = Keyboard.Modifiers;
                var hasControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                var hasShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                if (hasShift)
                {
                    _owner.ItemsList.Focus();
                    return;
                }

                if (_owner._isLoading)
                {
                    _owner.ItemsList.Focus();
                    e.Handled = true;
                    return;
                }

                if (e.ClickCount >= 2)
                {
                    if (_owner.GetFileEntryFromDoubleClickHit(source, position) is null)
                    {
                        _owner.ClearFilterIfNeeded();
                        e.Handled = true;
                    }

                    return;
                }

                _owner._scrollBehavior.StopAutoScroll();
                _owner._selectionInteraction.Start(position, hasControl || hasShift);

                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left
                && e.ClickCount >= 2
                && _owner.GetFileEntryFromDoubleClickHit(source, position) is null
                && !_owner.IsInsideScrollBar(source)
                && FindVisualParent<GridViewColumnHeader>(source) is null)
            {
                _owner.ClearFilterIfNeeded();
                e.Handled = true;
            }
        }

        public void HandlePreviewMouseMove(MouseEventArgs e)
        {
            if (_owner._selectionInteraction.HandlePreviewMouseMove(e.GetPosition(_owner.ItemsList)))
            {
                e.Handled = true;
                return;
            }

            if (_owner._scrollBehavior.IsAutoScrolling)
            {
                _owner._scrollBehavior.UpdateAutoScrollPoint(e.GetPosition(_owner.ItemsList));
                e.Handled = true;
                return;
            }

            if (TryStartPendingRangeSelectionDrag(e.GetPosition(_owner.ItemsList), e.LeftButton))
            {
                e.Handled = true;
                return;
            }

            if (_owner.GetNormalFolderPane() is { } pane
                && _owner.TryStartDrag(_owner.ItemsList, pane, e))
            {
                e.Handled = true;
                return;
            }

            var navigation = _owner.ActiveNavigation;
            if (_owner._fileDragStartPoint is null
                || _owner._fileDragStartEntry is null
                || e.LeftButton != MouseButtonState.Pressed
                || navigation is null
                || SpecialLocationService.IsSpecialUri(navigation.CurrentPath))
            {
                if (e.LeftButton != MouseButtonState.Pressed
                    || navigation is null
                    || SpecialLocationService.IsSpecialUri(navigation.CurrentPath))
                {
                    _owner.ClearFileDragStart();
                }

                return;
            }
        }

        public async Task HandlePreviewMouseRightButtonDownAsync(MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var position = e.GetPosition(_owner.ItemsList);
            if (_owner._isLoading && _owner.IsInsideItemsList(source))
            {
                _owner.MarkUserSelectionIntentDuringLoad("right-down");
            }

            if (ShouldSuppressItemsListInputDuringLoad(e.ChangedButton, source))
            {
                _owner.ClearFileDragStart();
                _owner.CancelPendingRenameClick();
                _owner.ItemsList.Focus();
                e.Handled = true;
                return;
            }

            if (_owner.IsInsideScrollBar(source))
            {
                return;
            }

            var clickedEntry = GetFileEntryFromItemHitTarget(source);
            _owner.PrepareRightClickSelection(clickedEntry);
            e.Handled = true;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                await _owner.ShowNativeShellContextMenuAsync(clickedEntry, position);
                return;
            }

            _owner.ShowLightweightContextMenu(clickedEntry);
        }

        public void HandleDragOver(DragEventArgs e)
        {
            var dragItems = GetFileDragItems(e);
            var executableTarget = GetExecutableDropTargetEntry(e);
            if (dragItems is not null && executableTarget is not null && CanLaunchToolWithFileItems(dragItems, executableTarget))
            {
                e.Effects = DragDropEffects.Link;
                _owner.HighlightFileDropTarget(executableTarget);
                e.Handled = true;
                return;
            }

            var targetDirectory = _owner.GetItemsListFileDropTargetDirectory(e);
            dragItems = GetFileOperationDragItems(e);
            var operationKind = GetFileDropOperationKind(e, targetDirectory);
            if (targetDirectory is null || dragItems is null || !_owner.CanDropFileItems(dragItems, targetDirectory, operationKind))
            {
                e.Effects = DragDropEffects.None;
                _owner.ClearFileDropHighlight();
                e.Handled = true;
                return;
            }

            e.Effects = operationKind == PendingFileOperationKind.Copy
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
            if (GetFileDropTargetEntry(e) is { } targetEntry)
            {
                _owner.HighlightFileDropTarget(targetEntry);
            }
            else
            {
                _owner.ClearFileDropHighlight();
            }

            e.Handled = true;
        }

        public void HandleDragLeave(DragEventArgs e)
        {
            _owner.ClearFileDropHighlight();
        }

        public async Task HandleDropAsync(DragEventArgs e)
        {
            var dragItems = GetFileDragItems(e);
            var executableTarget = GetExecutableDropTargetEntry(e);
            _owner.ClearFileDropHighlight();
            if (dragItems is not null && executableTarget is not null && CanLaunchToolWithFileItems(dragItems, executableTarget))
            {
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
                await _owner.LaunchToolWithFileItemsAsync(executableTarget, dragItems);
                return;
            }

            var targetDirectory = _owner.GetItemsListFileDropTargetDirectory(e);
            dragItems = GetFileOperationDragItems(e);
            var operationKind = GetFileDropOperationKind(e, targetDirectory);

            if (targetDirectory is null || dragItems is null || !_owner.CanDropFileItems(dragItems, targetDirectory, operationKind))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = operationKind == PendingFileOperationKind.Copy
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
            e.Handled = true;
            await _owner.DropFileItemsAsync(dragItems, targetDirectory, operationKind, IsExplicitCopyDrop(e));
        }

        public async Task HandlePreviewMouseLeftButtonUpAsync(MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _pendingRangeSelectionClickEntry is { } pendingEntry)
            {
                var entry = pendingEntry;
                _pendingRangeSelectionStartPoint = null;
                _pendingRangeSelectionClickEntry = null;
                _pendingRangeSelectionStartAdditive = false;
                _owner.ClearFileDragStart();

                var modifiers = Keyboard.Modifiers;
                var hasControl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                var hasShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                if (hasShift)
                {
                    FileListSelectionHelper.PerformShiftSelection(_owner.ItemsList, _selectionAnchorEntry, entry, hasControl);
                }
                else
                {
                    if (hasControl)
                    {
                        FileListSelectionHelper.ApplyControlSelection(_owner.ItemsList, entry);
                        _selectionAnchorEntry = entry;
                    }
                    else
                    {
                        FileListSelectionHelper.ApplySingleSelection(_owner.ItemsList, entry);
                        _selectionAnchorEntry = entry;
                    }
                }

                _owner.ItemsList.Focus();
                _owner.UpdateSelectedItemStatus();
                e.Handled = true;
                return;
            }

            if (_owner._isLoading && e.ChangedButton == MouseButton.Left)
            {
                _pendingRangeSelectionStartPoint = null;
                _pendingRangeSelectionClickEntry = null;
                _pendingRangeSelectionStartAdditive = false;
                _owner.ClearFileDragStart();
                _owner.CancelPendingRenameClick();
                if (_owner._selectionInteraction.IsSelecting)
                {
                    _owner._selectionInteraction.Cancel();
                    e.Handled = true;
                }

                return;
            }

            if (_owner._renameInteraction.PendingClickEntry is { } renameEntry)
            {
                var currentPoint = e.GetPosition(_owner.ItemsList);
                var startPoint = _owner._renameInteraction.PendingClickPoint;
                _owner.ClearPendingRenameClick();
                _owner.ClearFileDragStart();

                if (startPoint is not null
                    && Math.Abs(currentPoint.X - startPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(currentPoint.Y - startPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance
                    && _owner.ItemsList.SelectedItems.Count == 1
                    && _owner.ItemsList.SelectedItems.Contains(renameEntry)
                    && e.ChangedButton == MouseButton.Left)
                {
                    e.Handled = true;
                    await _owner.BeginRenameAfterClickDelayAsync(renameEntry, _owner._renameInteraction.AdvanceGeneration());
                    return;
                }
            }

            if (TryApplyPendingSingleSelectionClick(e))
            {
                e.Handled = true;
                return;
            }

            if (!_owner._selectionInteraction.IsSelecting)
            {
                _owner.ClearRangeSelectionStart();
                return;
            }

            _owner._selectionInteraction.HandlePreviewMouseUp(e.ChangedButton);
            e.Handled = true;
            _ = _owner.ProcessPendingFolderWatchRefreshAsync();
            _ = _owner.ProcessPendingDriveListRefreshAsync();
        }

        public void HandleSelectionChanged()
        {
#if DEBUG
            if (_owner._diagnosticLoadId != 0)
            {
                _owner._selectionChangedCount++;
            }
#endif
            if (_owner._listViewRestore.IsRestoring || _owner._suppressSelectionStatusUpdates)
            {
                return;
            }

            if (_owner._isLoading || !string.IsNullOrEmpty(_owner._loadingStateId))
            {
                _owner._performanceLogger.Write($"selection-skip reason=loading loadingStateId={_owner._loadingStateId ?? ""} selectedIndex={_owner.TabsControl.SelectedIndex}");
                return;
            }

            _owner.UpdateSelectedItemStatus();
        }

        private bool TryStartPendingRangeSelectionDrag(Point currentPoint, MouseButtonState leftButton)
        {
            Point startPoint;
            bool additive;
            bool isPending = false;

            if (_pendingRangeSelectionStartPoint is { } pendingStart)
            {
                startPoint = pendingStart;
                additive = _pendingRangeSelectionStartAdditive;
                isPending = true;
            }
            else if (_owner._rangeSelectionStartPoint is not { } normalStart)
            {
                return false;
            }
            else
            {
                startPoint = normalStart;
                additive = _owner._rangeSelectionStartAdditive;
            }

            if (leftButton != MouseButtonState.Pressed)
            {
                _pendingRangeSelectionStartPoint = null;
                _pendingRangeSelectionClickEntry = null;
                _pendingRangeSelectionStartAdditive = false;
                _owner.ClearRangeSelectionStart();
                return false;
            }

            if (Math.Abs(currentPoint.X - startPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(currentPoint.Y - startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return false;
            }

            if (_owner._isLoading
                || _owner.IsRenameInteractionActive()
                || _owner._selectionInteraction.IsSelecting
                || _owner._scrollBehavior.IsAutoScrolling)
            {
                _pendingRangeSelectionStartPoint = null;
                _pendingRangeSelectionClickEntry = null;
                _pendingRangeSelectionStartAdditive = false;
                _owner.ClearRangeSelectionStart();
                return false;
            }

            _owner.ClearPendingRenameClick();
            _owner._scrollBehavior.StopAutoScroll();

            if (isPending)
            {
                _pendingRangeSelectionStartPoint = null;
                _pendingRangeSelectionClickEntry = null;
                _pendingRangeSelectionStartAdditive = false;
            }

            _owner._selectionInteraction.Start(startPoint, additive);
            _owner._selectionInteraction.HandlePreviewMouseMove(currentPoint);
            return true;
        }

        private bool TryApplyPendingSingleSelectionClick(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left
                || _owner._pendingSingleSelectionClickEntry is not { } entry
                || _owner._pendingSingleSelectionClickPoint is not { } startPoint)
            {
                return false;
            }

            var currentPoint = e.GetPosition(_owner.ItemsList);
            _owner.ClearFileDragStart();
            if (Math.Abs(currentPoint.X - startPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(currentPoint.Y - startPoint.Y) >= SystemParameters.MinimumVerticalDragDistance
                || !_owner.ItemsList.Items.Contains(entry))
            {
                return false;
            }

            var modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                FileListSelectionHelper.ApplyControlSelection(_owner.ItemsList, entry);
                _selectionAnchorEntry = entry;
            }
            else
            {
                FileListSelectionHelper.ApplySingleSelection(_owner.ItemsList, entry);
                _selectionAnchorEntry = entry;
            }
            _owner.ItemsList.Focus();
            _owner.UpdateSelectedItemStatus();
            return true;
        }

        private void ClearPendingSelectionInput()
        {
            _pendingRangeSelectionStartPoint = null;
            _pendingRangeSelectionClickEntry = null;
            _pendingRangeSelectionStartAdditive = false;
            _owner._pendingSingleSelectionClickEntry = null;
            _owner._pendingSingleSelectionClickPoint = null;
            _owner._pendingSingleSelectionClickPane = null;
            _owner._pendingSingleSelectionClickListView = null;
        }

        private bool ShouldSuppressItemsListInputDuringLoad(MouseButton changedButton, DependencyObject? source)
        {
            return InputSuppressionService.ShouldSuppressItemsListInputDuringLoad(
                _owner._isLoading,
                changedButton,
                _owner.IsInsideScrollBar(source),
                FindVisualParent<GridViewColumnHeader>(source) is not null,
                _owner.IsInsideItemsList(source));
        }

        private bool ShouldAllowSingleSelectionInputDuringLoad(MouseButton changedButton, int clickCount, DependencyObject? source)
        {
            return InputSuppressionService.ShouldAllowSingleSelectionInputDuringLoad(
                _owner._isLoading,
                changedButton,
                clickCount,
                Keyboard.Modifiers,
                _owner.IsInsideScrollBar(source),
                FindVisualParent<GridViewColumnHeader>(source) is not null,
                GetFileEntryFromItemHitTarget(source) is not null);
        }

    }
}
