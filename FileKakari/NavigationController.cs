using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FileKakari;

public partial class MainWindow
{
    private sealed class NavigationController
    {
        private readonly MainWindow _owner;

        public NavigationController(MainWindow owner)
        {
            _owner = owner;
        }

        public async Task NavigateToFolderAsync(string path, NavigationKind navigationKind)
        {
            if (_owner.ActiveNavigation is not { } navigation || _owner.ActiveTab is not { } activeTab)
            {
                _owner._performanceLogger.Write($"path-navigation-skip reason=no-active-tab path=\"{path}\"");
                return;
            }

            _owner.CancelPendingRenameClick();
            if (_owner._isSwitchingWorkspacePane && navigationKind is NavigationKind.New or NavigationKind.Up)
            {
                _owner._performanceLogger.Write($"path-navigation-blocked reason=workspace-pane-switch path=\"{path}\"");
                return;
            }

            if (_owner._activeWorkspaceSession.IsWorkspace
                && navigationKind is NavigationKind.New or NavigationKind.Up
                && (_owner._isLoading || _owner._isSwitchingTabs || _owner._isRestoringTabState))
            {
                _owner._performanceLogger.Write($"path-navigation-blocked reason=workspace-session-busy path=\"{path}\" loading={_owner._isLoading} switchingTabs={_owner._isSwitchingTabs} restoring={_owner._isRestoringTabState}");
                return;
            }

            if (_owner.BlockIfRenameInProgress($"navigate-{navigationKind}"))
            {
                _owner.UpdatePathDisplay(navigation.CurrentPath);
                return;
            }

            var normalizedPath = NavigationState.NormalizePath(path);
            if (normalizedPath is null)
            {
                _owner.StatusText.Text = _owner._text.Format("PathNotFound", path);
                _owner.UpdatePathDisplay(navigation.CurrentPath);
                return;
            }

            if (!SpecialLocationService.IsSpecialUri(normalizedPath))
            {
                var availability = await _owner._driveAvailabilityService.CheckAsync(normalizedPath);
                var sameAsCurrentPath = string.Equals(normalizedPath, navigation.CurrentPath, StringComparison.OrdinalIgnoreCase);
                if (!availability.IsAvailable)
                {
                    if (sameAsCurrentPath || navigationKind == NavigationKind.Refresh)
                    {
                        _owner.MarkActiveLocationDisconnected(availability, "navigate");
                    }
                    else
                    {
                        _owner.ShowDisconnectedStatus();
                        _owner._performanceLogger.Write($"location-unavailable path=\"{normalizedPath}\" reason=navigate-other root=\"{availability.RootPath}\" exists={availability.RootExists} ready={availability.IsReady} error=\"{availability.Error}\"");
                    }

                    _owner.UpdatePathDisplay(navigation.CurrentPath);
                    return;
                }

                if (!await _owner._driveAvailabilityService.DirectoryExistsAsync(normalizedPath))
                {
                    _owner.StatusText.Text = _owner._text.Format("PathNotFound", path);
                    _owner.UpdatePathDisplay(navigation.CurrentPath);
                    return;
                }

                if (sameAsCurrentPath)
                {
                    _owner.ClearActiveLocationDisconnected();
                }
            }

            if (string.Equals(normalizedPath, navigation.CurrentPath, StringComparison.OrdinalIgnoreCase)
                && navigationKind != NavigationKind.Refresh)
            {
                _owner.UpdatePathDisplay(navigation.CurrentPath);
                return;
            }

            if ((navigationKind is NavigationKind.New or NavigationKind.Up) && !_owner._isSwitchingWorkspacePane)
            {
                _owner.ClearWorkspacePaneContext(force: false);
            }

            var policy = FileListRestorePolicy.None;
            if (navigationKind == NavigationKind.Refresh)
            {
                policy = FileListRestorePolicy.ExactRestore;
            }
            else if (navigationKind is NavigationKind.Back or NavigationKind.Forward)
            {
                bool hasSavedState = activeTab.State.TryGetNavigationViewState(
                    normalizedPath,
                    MainWindow.NormalizeSortColumn(activeTab.State.SortColumn),
                    activeTab.State.SortAscending,
                    activeTab.State.FilterText,
                    out _);
                policy = hasSavedState ? FileListRestorePolicy.ExactRestore : FileListRestorePolicy.FocusPathFallback;
            }
            else if (navigationKind == NavigationKind.Up)
            {
                bool hasSavedState = activeTab.State.TryGetNavigationViewState(
                    normalizedPath,
                    MainWindow.NormalizeSortColumn(activeTab.State.SortColumn),
                    activeTab.State.SortAscending,
                    "",
                    out _);
                policy = hasSavedState ? FileListRestorePolicy.ExactRestore : FileListRestorePolicy.FocusPathFallback;
            }

            var restoreState = navigationKind == NavigationKind.Refresh
                ? _owner.CaptureListViewRestoreState(activeTab, normalizedPath)
                : _owner.CreateNavigationViewRestoreState(activeTab, normalizedPath, navigationKind);
            _owner.SaveNavigationViewState(activeTab);
            navigation.Commit(normalizedPath, navigationKind);
            await _owner.LoadFolderAsync(normalizedPath, restoreState, activeTab, policy);
            UpdateNavigationButtons();
        }

        public async Task NavigateHistoryAsync(NavigationDirection direction, FolderPane pane)
        {
            var tab = _owner.GetActiveFolderPaneTab(pane);
            if (tab is null)
            {
                return;
            }

            var navigation = tab.Navigation;
            var canNavigate = direction == NavigationDirection.Back ? navigation.CanGoBack : navigation.CanGoForward;
            if (!canNavigate)
            {
                return;
            }

            var targetPath = direction == NavigationDirection.Back ? navigation.PeekBack() : navigation.PeekForward();
            if (targetPath is null)
            {
                return;
            }

            var navigationKind = direction == NavigationDirection.Back ? NavigationKind.Back : NavigationKind.Forward;

            if (ReferenceEquals(pane, _owner.GetNormalFolderPane()))
            {
                if (!NavigationState.IsExistingDirectory(targetPath))
                {
                    _owner.StatusText.Text = _owner._text.Format("PathNotFound", targetPath);
                    UpdateNavigationButtons();
                    return;
                }

                await NavigateToFolderAsync(targetPath, navigationKind);
            }
            else
            {
                await NavigateWorkspacePaneToFolderAsync(pane, targetPath, navigationKind);
            }
        }

        public async Task<bool> TryOpenWorkspaceFileAsync(FileEntry entry)
        {
            return !entry.IsDirectory
                && WorkspaceService.IsWorkspaceFile(entry.FullPath)
                && await _owner.OpenWorkspaceFileAsync(entry.FullPath);
        }

        public async Task<bool> TryOpenDirectoryEntryAsync(FileEntry entry, bool openDirectoryInNewTab)
        {
            if (!entry.IsDirectory)
            {
                return false;
            }

            if (_owner.ActiveTab is not { } activeTab)
            {
                return true;
            }

            if (openDirectoryInNewTab || activeTab.IsFolderLocked)
            {
                await _owner.CreateNewTabAsync(entry.FullPath);
                return true;
            }

            await NavigateToFolderAsync(entry.FullPath, NavigationKind.New);
            return true;
        }

        public async Task HandleUpButtonClickAsync()
        {
            _owner.ClearFilterIfNeeded();
            await OpenParentAsync();
        }

        public async Task OpenParentAsync()
        {
            if (!CanStartUserPathNavigation("up"))
            {
                return;
            }

            if (_owner.GetActiveFolderPane() is { } pane && _owner.IsWorkspaceDisplayPane(pane))
            {
                await OpenWorkspacePaneParentAsync(pane);
                return;
            }

            if (_owner.ActiveNavigation is not { } navigation)
            {
                return;
            }

            if (SpecialLocationService.IsSpecialUri(navigation.CurrentPath))
            {
                return;
            }

            if (NavigationState.IsFileSystemRoot(navigation.CurrentPath))
            {
                await NavigateToFolderAsync(SpecialLocationService.ThisPcUri, NavigationKind.Up);
                return;
            }

            var parent = Directory.GetParent(navigation.CurrentPath);
            if (parent is not null)
            {
                await NavigateToFolderAsync(parent.FullName, NavigationKind.Up);
            }
        }

        public async Task OpenWorkspacePaneParentAsync(FolderPane pane)
        {
            if (_owner.GetActiveFolderPaneTab(pane) is not { } tab
                || SpecialLocationService.IsSpecialUri(tab.Navigation.CurrentPath))
            {
                return;
            }

            var parent = Directory.GetParent(tab.Navigation.CurrentPath);
            var targetPath = NavigationState.IsFileSystemRoot(tab.Navigation.CurrentPath)
                ? SpecialLocationService.ThisPcUri
                : parent?.FullName ?? Directory.GetDirectoryRoot(tab.Navigation.CurrentPath);
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                await NavigateWorkspacePaneToFolderAsync(pane, targetPath, NavigationKind.Up);
            }
        }

        public async Task RefreshCurrentFolderAsync()
        {
            _owner.CancelPendingRenameClick();
            if (_owner.BlockIfRenameInProgress("refresh"))
            {
                return;
            }

            if (_owner.ActiveNavigation is not { } navigation)
            {
                return;
            }

            await NavigateToFolderAsync(navigation.CurrentPath, NavigationKind.Refresh);
        }

        public async Task NavigateWorkspacePaneToFolderAsync(FolderPane pane, string path, NavigationKind navigationKind)
        {
            var normalizedPath = NavigationState.NormalizePath(path);
            if (normalizedPath is null)
            {
                return;
            }



            var targetTab = _owner.GetActiveFolderPaneTab(pane);
            if (targetTab is null)
            {
                return;
            }

            if (string.Equals(normalizedPath, targetTab.Navigation.CurrentPath, StringComparison.OrdinalIgnoreCase)
                && navigationKind != NavigationKind.Refresh)
            {
                return;
            }

            if (navigationKind is NavigationKind.New or NavigationKind.Up && targetTab.IsFolderLocked)
            {
                await _owner.CreateWorkspacePaneSubTabAsync(pane, normalizedPath, targetTab);
                return;
            }

            if (!SpecialLocationService.IsSpecialUri(normalizedPath))
            {
                var availability = await _owner._driveAvailabilityService.CheckAsync(normalizedPath);
                if (!availability.IsAvailable || !await _owner._driveAvailabilityService.DirectoryExistsAsync(normalizedPath))
                {
                    pane.FileList.StatusText = _owner._text.Format("PathNotFound", path);
                    _owner.StatusText.Text = _owner._text.Format("PathNotFound", path);
                    _owner._performanceLogger.Write($"folder-pane-navigation-failed paneId={pane.Id} path=\"{normalizedPath}\" root=\"{availability.RootPath}\" exists={availability.RootExists} ready={availability.IsReady} error=\"{availability.Error}\"");
                    return;
                }
            }

            var policy = FileListRestorePolicy.None;
            if (navigationKind == NavigationKind.Refresh)
            {
                policy = FileListRestorePolicy.ExactRestore;
            }
            else if (navigationKind is NavigationKind.Back or NavigationKind.Forward)
            {
                bool hasSavedState = targetTab.State.TryGetNavigationViewState(
                    normalizedPath,
                    MainWindow.NormalizeSortColumn(targetTab.State.SortColumn),
                    targetTab.State.SortAscending,
                    "",
                    out _);
                policy = hasSavedState ? FileListRestorePolicy.ExactRestore : FileListRestorePolicy.FocusPathFallback;
            }
            else if (navigationKind == NavigationKind.Up)
            {
                bool hasSavedState = targetTab.State.TryGetNavigationViewState(
                    normalizedPath,
                    MainWindow.NormalizeSortColumn(targetTab.State.SortColumn),
                    targetTab.State.SortAscending,
                    "",
                    out _);
                policy = hasSavedState ? FileListRestorePolicy.ExactRestore : FileListRestorePolicy.FocusPathFallback;
            }

            _owner.SaveWorkspacePaneNavigationViewState(pane, targetTab);
            var restoreViewState = _owner.GetWorkspacePaneNavigationViewState(
                targetTab,
                normalizedPath,
                navigationKind);

            targetTab.Navigation.Commit(normalizedPath, navigationKind);
            targetTab.State.CurrentPath = normalizedPath;
            targetTab.RefreshHeader();
            pane.ResolveTabHeaders();
            targetTab.State.FilterText = "";
            targetTab.State.SelectedPaths = restoreViewState.SelectedPaths.ToList();
            targetTab.State.VerticalOffset = restoreViewState.VerticalOffset;
            pane.FileList.SelectedPaths = restoreViewState.SelectedPaths.ToList();
            pane.FileList.ScrollOffset = restoreViewState.VerticalOffset;
            pane.FileList.StatusMessagePrefix = null;
            pane.FileList.StatusText = _owner._text.Get("LoadingZero");
            pane.RefreshDisplay();
            if (navigationKind == NavigationKind.Refresh)
            {
                targetTab.State.ClearItems();
            }

            _owner._performanceLogger.Write($"folder-pane-navigation paneId={pane.Id} stateId={targetTab.State.Id} path=\"{normalizedPath}\" kind={navigationKind}");
            await _owner.LoadFolderPaneItemsAsync(pane, policy);
            _owner.UpdateFolderWatchForWorkspacePanes();

            _owner.ApplyColumnWidthsToWorkspacePane(pane);

            _owner._workspaceLocalState.MarkDirty("pane-navigation");
        }

        public async Task NavigateFromBreadcrumbAsync(string targetPath)
        {
            try
            {
                if (!CanStartUserPathNavigation("breadcrumb"))
                {
                    return;
                }

                await NavigateToFolderAsync(targetPath, NavigationKind.New);
            }
            catch (Exception ex)
            {
                _owner.StatusText.Text = _owner._text.Format("PathNotFound", targetPath);
                _owner._performanceLogger.Write($"breadcrumb-navigation-failed path=\"{targetPath}\" error=\"{ex.Message}\"");
            }
        }

        public async Task HandlePathBoxKeyDownAsync(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _owner._breadcrumbPathBar.CancelEdit();
                _owner.ItemsList.Focus();
                return;
            }

            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            if (!CanStartUserPathNavigation("path-input"))
            {
                _owner._breadcrumbPathBar.CancelEdit();
                return;
            }

            await _owner._breadcrumbPathBar.NavigateFromPathBoxAsync();
        }

        public async Task HandleNormalPanePathBoxKeyDownAsync(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _owner.NormalPanePathBox.Text = _owner.ActiveNavigation?.CurrentPath ?? "";
                _owner.NormalPanePathBox.Visibility = Visibility.Collapsed;
                _owner.ItemsList.Focus();
                return;
            }

            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            if (!CanStartUserPathNavigation("pane-path-input"))
            {
                _owner.NormalPanePathBox.Text = _owner.ActiveNavigation?.CurrentPath ?? "";
                return;
            }

            var normalizedPath = NavigationState.NormalizePath(_owner.NormalPanePathBox.Text);
            if (normalizedPath is null)
            {
                _owner.StatusText.Text = _owner._text.Format("PathNotFound", _owner.NormalPanePathBox.Text);
                _owner.NormalPanePathBox.Text = _owner.ActiveNavigation?.CurrentPath ?? "";
                return;
            }

            await NavigateFromPathBoxAsync(normalizedPath);
            _owner.NormalPanePathBox.Visibility = Visibility.Collapsed;
        }

        public async Task NavigateFromPathBoxAsync(string normalizedPath)
        {
            await NavigateToFolderAsync(normalizedPath, NavigationKind.New);
        }

        public bool CanStartUserPathNavigation(string operation)
        {
            if (_owner._isSwitchingWorkspacePane)
            {
                _owner._performanceLogger.Write($"path-navigation-blocked operation={operation} reason=workspace-pane-switch");
                return false;
            }

            if (!InputSuppressionService.CanStartUserPathNavigation(_owner.GetInputBusyState()))
            {
                _owner._performanceLogger.Write($"path-navigation-blocked operation={operation} loading={_owner._isLoading} drag={_owner._isFileDragInProgress} fileOperation={_owner._isFileOperationInProgress} selecting={_owner._selectionInteraction.IsSelecting} autoScroll={_owner._scrollBehavior.IsAutoScrolling}");
                return false;
            }

            return !_owner.BlockIfRenameInProgress(operation);
        }

        public void UpdateNavigationButtons()
        {
            var navigation = _owner.ActiveNavigation;
            var activeTab = _owner.ActiveTab;
            if (navigation is null || activeTab is null)
            {
                _owner.NormalPaneBackButton.IsEnabled = false;
                _owner.NormalPaneForwardButton.IsEnabled = false;
                _owner.NormalPaneUpButton.IsEnabled = false;
                _owner.NormalPaneRefreshButton.IsEnabled = false;
                _owner.NormalPaneNewFolderButton.IsEnabled = false;
                _owner.NormalPaneNewFileButton.IsEnabled = false;
                return;
            }

            var isSpecialView = SpecialLocationService.IsSpecialUri(navigation.CurrentPath);
            var isDisconnected = activeTab.IsDisconnected;
            _owner.NormalPaneBackButton.IsEnabled = navigation.CanGoBack;
            _owner.NormalPaneForwardButton.IsEnabled = navigation.CanGoForward;
            _owner.NormalPaneUpButton.IsEnabled = navigation.CanGoUp;
            _owner.NormalPaneRefreshButton.IsEnabled = true;
            _owner.NormalPaneNewFolderButton.IsEnabled = !isSpecialView && !isDisconnected;
            _owner.NormalPaneNewFileButton.IsEnabled = !isSpecialView && !isDisconnected;
        }
    }
}
