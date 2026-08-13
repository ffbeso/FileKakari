using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileKakari;

public partial class MainWindow
{
    private async void ItemsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        await _fileListInput.HandlePreviewMouseRightButtonDownAsync(e);
    }

    private void PrepareRightClickSelection(FileEntry? clickedEntry)
    {
        FileListSelectionHelper.PrepareRightClickSelection(ItemsList, clickedEntry);
        ItemsList.Focus();
        SyncNormalPaneSelectionFromView();
        UpdateSelectedItemStatus();
    }

    private void ShowLightweightContextMenu(FileEntry? clickedEntry)
    {
        if (ActiveNavigation is not { } navigation || ActiveTab is not { } activeTab)
        {
            return;
        }

        var selectedEntries = GetSelectedEntries();
        var menu = BuildLightweightContextMenu(
            pane: null,
            placementTarget: ItemsList,
            state: ActiveTabState,
            currentPath: navigation.CurrentPath,
            isDisconnected: activeTab.IsDisconnected,
            selectedEntries: selectedEntries,
            clickedEntry: clickedEntry);

        menu.IsOpen = true;
    }

    private MenuItem CreateMenuItem(string header, bool isEnabled, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem CreateMenuItem(string header, bool isEnabled, Func<Task> action)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };
        item.Click += async (_, _) => await action();
        return item;
    }

    private void PopulateGroupSubMenu(ItemsControl parentMenu, WorkspaceTabState targetState, ListView targetListView)
    {
        var groupSubMenu = new MenuItem
        {
            Header = _text.Get("ContextGroup")
        };
        ApplyMenuItemStyle(groupSubMenu);

        AddGroupMenuItem(groupSubMenu, targetState, targetListView, GroupMode.None, _text.Get("GroupModeNone"));
        AddGroupMenuItem(groupSubMenu, targetState, targetListView, GroupMode.Extension, _text.Get("GroupModeExtension"));
        AddGroupMenuItem(groupSubMenu, targetState, targetListView, GroupMode.Date, _text.Get("GroupModeDate"));
        AddGroupMenuItem(groupSubMenu, targetState, targetListView, GroupMode.SimilarName, _text.Get("GroupModeSimilarName"));

        parentMenu.Items.Add(groupSubMenu);
    }

    private void AddGroupMenuItem(MenuItem parentMenu, WorkspaceTabState targetState, ListView targetListView, GroupMode mode, string header)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = targetState.GroupMode == mode
        };
        ApplyMenuItemStyle(item);
        item.Click += async (_, _) =>
        {
            await SwitchGroupModeAsync(targetState, targetListView, mode);
        };
        parentMenu.Items.Add(item);
    }

    private async Task SwitchGroupModeAsync(WorkspaceTabState targetState, ListView targetListView, GroupMode mode)
    {
        if (targetState.GroupMode == mode)
        {
            return;
        }

        var selectedPaths = targetState.SelectedPaths;
        var offset = targetState.VerticalOffset;

        targetState.GroupMode = mode;

        if (!FolderWatchService.CanWatchFolder(targetState.CurrentPath))
        {
            var pane = targetListView.DataContext as FolderPane ?? FindPaneForState(targetState);
            if (pane is not null)
            {
                await ReloadFolderPaneAsync(pane);
                return;
            }
        }

        if (ReferenceEquals(targetListView, ItemsList))
        {
            ApplyTabSort(targetState);
        }
        else if (targetListView.DataContext is FolderPane pane)
        {
            SimilarNameGroupIndex? groupIndex = mode == GroupMode.SimilarName ? pane.FileList.EnsureSimilarNameIndex() : null;
            pane.FileList.ApplySort(targetState.SortColumn, targetState.SortAscending, _settingsService.Settings.SortFoldersFirst, null, mode);
            FileGroupHelper.ApplyGroupMode(pane.FileList, mode, groupIndex);
        }

        if (ReferenceEquals(targetListView, ItemsList))
        {
            await RestoreScrollOffsetAsync(offset);
            RestoreSelection(selectedPaths);
        }
        else if (targetListView.DataContext is FolderPane pane)
        {
            await RestoreWorkspacePaneScrollOffsetAsync(pane, offset);
            SelectItemsInPaneByPaths(pane, selectedPaths, focus: false, scrollIntoView: false);
        }

        _workspaceLocalState.MarkDirty("group-mode");
    }

    private void CopyPathsToClipboard(IReadOnlyList<FileEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, entries.Select(entry => entry.FullPath)));
            StatusText.Text = entries.Count == 1
                ? _text.Format("PathCopied", entries[0].FullPath)
                : _text.Format("PathsCopied", entries.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = _text.Format("CopyPathFailedPrefix", ex.Message);
            MessageBox.Show(this, ex.Message, _text.Get("CopyPathFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ShowPropertiesAsync(IReadOnlyList<FileEntry> entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                if (!File.Exists(entry.FullPath) && !Directory.Exists(entry.FullPath))
                {
                    StatusText.Text = _text.Get("OpenFailedMissing");
                    return;
                }

                ShellItemActions.ShowProperties(this, entry.FullPath);
            }
            catch (Exception ex)
            {
                StatusText.Text = _text.Format("PropertiesFailedPrefix", ex.Message);
                MessageBox.Show(this, ex.Message, _text.Get("PropertiesFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        await Task.CompletedTask;
    }

    private async Task ShowNativeShellContextMenuAsync(FileEntry? clickedEntry, Point position)
    {
        IReadOnlyList<FileEntry> selectedEntries = clickedEntry is null
            ? []
            : GetSelectedEntries();
        if (ActiveNavigation is not { } navigation)
        {
            return;
        }

        var shown = await _shellContextMenuService.ShowAsync(this, navigation.CurrentPath, selectedEntries, position);
        if (!shown)
        {
            StatusText.Text = _text.Get("ShellContextMenuDeferred");
        }
    }

    private void PreparePaneRightClickSelection(FolderPane pane, ListView listView, FileEntry? clickedEntry)
    {
        if (clickedEntry is not null)
        {
            _workspaceSelectionAnchorEntry = clickedEntry;
        }
        FileListSelectionHelper.PrepareRightClickSelection(listView, clickedEntry);

        listView.Focus();
        SyncPaneSelectionFromListView(pane, listView);
    }

    private void ShowWorkspacePaneContextMenu(FolderPane pane, ListView listView, FileEntry? clickedEntry)
    {
        if (FindWorkspaceSessionForPane(pane) is { } session && _displayedWorkspaceSessionIds.Contains(session.Id))
        {
            if (!IsSameWorkspaceSession(session, _activeWorkspaceSession))
            {
                _ = ActivateDisplayedWorkspaceSessionAsync(session, "context-menu", FocusRestoreStrategy.None);
            }
        }

        if (pane.ActiveTab is not { } tab || pane.ActiveTabState is not { } state)
        {
            return;
        }

        var selectedEntries = listView.SelectedItems.OfType<FileEntry>().ToList();
        var menu = BuildLightweightContextMenu(
            pane: pane,
            placementTarget: listView,
            state: state,
            currentPath: tab.Navigation.CurrentPath,
            isDisconnected: tab.IsDisconnected,
            selectedEntries: selectedEntries,
            clickedEntry: clickedEntry);

        menu.IsOpen = true;
    }

    private ContextMenu BuildLightweightContextMenu(
        FolderPane? pane,
        ListView placementTarget,
        WorkspaceTabState? state,
        string currentPath,
        bool isDisconnected,
        IReadOnlyList<FileEntry> selectedEntries,
        FileEntry? clickedEntry)
    {
        var isSpecialView = SpecialLocationService.IsSpecialUri(currentPath);
        var canOperateInFolder = !isSpecialView && !isDisconnected;
        var canPaste = (IsInternalClipboardValid() || ClipboardContainsExternalFileTransfer()) && canOperateInFolder;
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget
        };

        if (clickedEntry is null)
        {
            menu.Items.Add(CreateMenuItem(_text.Get("ContextPaste"), canPaste, () => PastePendingFileOperationAsync(pane)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("NewFolderButton"), canOperateInFolder, () => CreateNewItemAsync(NewItemKind.Folder, pane)));
            menu.Items.Add(CreateMenuItem(_text.Get("NewFileButton"), canOperateInFolder, () => CreateNewItemAsync(NewItemKind.TextFile, pane)));

            var sep = new Separator();
            ApplySeparatorStyle(sep);
            menu.Items.Add(sep);

            var viewSubMenu = new MenuItem
            {
                Header = _text.Get("ContextView")
            };
            ApplyMenuItemStyle(viewSubMenu);
            if (pane is not null && state is not null)
            {
                _viewModeController.PopulateWorkspaceMenu(viewSubMenu, pane, state, reason => _workspaceLocalState.MarkDirty(reason), ApplyDisplayModeToPane);
            }
            else
            {
                _viewModeController.PopulateMenu(viewSubMenu);
            }
            menu.Items.Add(viewSubMenu);

            if (state is not null)
            {
                PopulateGroupSubMenu(menu, state, placementTarget);
            }

            AddUserCommandsSubMenu(menu, currentPath, selectedEntries, addLeadingSeparator: true);
        }
        else
        {
            var singleSelection = selectedEntries.Count == 1;
            var singleDirectory = singleSelection && selectedEntries[0].IsDirectory;

            if (pane is not null)
            {
                menu.Items.Add(CreateMenuItem(_text.Get("ContextOpen"), singleSelection && !isDisconnected, () => OpenWorkspacePaneSelectionAsync(pane, selectedEntries[0])));
                AddUserCommandsSubMenu(menu, currentPath, selectedEntries, addLeadingSeparator: false);
                menu.Items.Add(CreateMenuItem(_text.Get("ContextOpenInNewTab"), singleDirectory && !isDisconnected, () => CreateWorkspacePaneSubTabAsync(pane, selectedEntries[0].FullPath, pane.ActiveTab!)));
            }
            else
            {
                menu.Items.Add(CreateMenuItem(_text.Get("ContextOpen"), singleSelection && !isDisconnected, () => OpenSelectedAsync()));
                AddUserCommandsSubMenu(menu, currentPath, selectedEntries, addLeadingSeparator: false);
                menu.Items.Add(CreateMenuItem(_text.Get("ContextOpenInNewTab"), singleDirectory && !isDisconnected, () => OpenSelectedAsync(openDirectoryInNewTab: true)));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCopy"), canOperateInFolder, () => SetPendingFileOperationAsync(PendingFileOperationKind.Copy, pane)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCut"), canOperateInFolder, () => SetPendingFileOperationAsync(PendingFileOperationKind.Move, pane)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextPaste"), canPaste, () => PastePendingFileOperationAsync(pane)));
            menu.Items.Add(new Separator());

            var canRename = singleSelection && canOperateInFolder && (pane is null || IsRenameable(selectedEntries[0]));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextRename"), canRename, () => BeginRenameSelected(pane)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextDelete"), canOperateInFolder, () => DeleteSelectedAsync(pane)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCopyPath"), true, () => CopyPathsToClipboard(selectedEntries)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextProperties"), true, () => ShowPropertiesAsync(selectedEntries)));
        }

        return menu;
    }

    private void WorkspacePaneSubTabBar_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox
            || listBox.DataContext is not FolderPane pane)
        {
            return;
        }

        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is not null && item.DataContext is FolderTab tab)
        {
            e.Handled = true;
            pane.SelectedTabId = tab.Id;
            ShowWorkspacePaneSubTabContextMenu(listBox, pane, tab);
            return;
        }

        // Blank area right-clicked
        e.Handled = true;
        ShowWorkspacePaneSubTabBarContextMenu(listBox, pane);
    }

    private void WorkspacePaneSubTabBarBackground_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsWorkspacePaneSubTabBarBackgroundInput(e.OriginalSource as DependencyObject)
            || !TryGetWorkspacePaneSubTabBarTarget(sender, out var listBox, out var pane))
        {
            return;
        }

        e.Handled = true;
        ShowWorkspacePaneSubTabBarContextMenu(sender as FrameworkElement ?? listBox, pane);
    }

    private void ShowWorkspacePaneSubTabContextMenu(FrameworkElement placementTarget, FolderPane pane, FolderTab tab)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget
        };
        menu.Items.Add(CreateMenuItem(
            tab.IsFolderLocked ? _text.Get("UnlockFolderTabMenu") : _text.Get("LockFolderTabMenu"),
            true,
            () => ToggleWorkspacePaneSubTabLock(pane, tab)));

        var isExplorerEnabled = !SpecialLocationService.IsSpecialUri(tab.Navigation.CurrentPath)
            && Directory.Exists(tab.Navigation.CurrentPath);
        menu.Items.Add(CreateMenuItem(
            _text.Get("OpenInExplorerMenu"),
            isExplorerEnabled,
            () => OpenTabInExplorer(tab)));

        menu.Items.Add(new Separator());
        var session = _workspaceSessions.FirstOrDefault(s => s.PaneGroups.Any(pg => ReferenceEquals(pg, pane)));
        var totalTabsInSession = session?.PaneGroups.Sum(pg => pg.Tabs.Count) ?? 0;
        var canClose = !tab.IsFolderLocked;
        if (canClose && totalTabsInSession == 1 && session is not null)
        {
            canClose = !session.IsLocked && _workspaceSessions.Count > 1;
        }

        menu.Items.Add(CreateMenuItem(_text.Get("CloseThisTabMenu"), canClose, () => CloseWorkspacePaneSubTabAsync(pane, tab, placementTarget as ListBox)));
        var isRestoreEnabled = _lastClosedSubTab is not null && _lastClosedSubTab.PaneId == pane.Id;
        menu.Items.Add(CreateMenuItem(_text.Get("RestoreClosedTabMenu"), isRestoreEnabled, () => RestoreLastClosedSubTabAsync(pane)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateSubTabPlacementMenu(pane));
        menu.IsOpen = true;
    }

    private void ShowWorkspacePaneSubTabBarContextMenu(FrameworkElement placementTarget, FolderPane pane)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget
        };
        var isRestoreEnabled = _lastClosedSubTab is not null && _lastClosedSubTab.PaneId == pane.Id;
        menu.Items.Add(CreateMenuItem(_text.Get("RestoreClosedTabMenu"), isRestoreEnabled, () => RestoreLastClosedSubTabAsync(pane)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateSubTabPlacementMenu(pane));
        menu.Items.Add(CreateMenuItem(_text.Get("ResetSubTabBarWidthToDefault"), true, () => ResetFolderPaneSubTabBarWidth(pane)));
        menu.IsOpen = true;
    }

    private MenuItem CreateSubTabPlacementMenu(FolderPane pane)
    {
        var placementMenu = new MenuItem { Header = "サブタブの配置" };
        var current = pane.SubTabPlacement;

        var itemTop = new MenuItem { Header = "上 (Top)", IsChecked = current == SubTabPlacement.Top };
        itemTop.Click += (_, _) => SetFolderPaneSubTabPlacement(pane, SubTabPlacement.Top);
        placementMenu.Items.Add(itemTop);

        var itemLeft = new MenuItem { Header = "左 (Left)", IsChecked = current == SubTabPlacement.Left };
        itemLeft.Click += (_, _) => SetFolderPaneSubTabPlacement(pane, SubTabPlacement.Left);
        placementMenu.Items.Add(itemLeft);

        var itemRight = new MenuItem { Header = "右 (Right)", IsChecked = current == SubTabPlacement.Right };
        itemRight.Click += (_, _) => SetFolderPaneSubTabPlacement(pane, SubTabPlacement.Right);
        placementMenu.Items.Add(itemRight);

        var itemBottom = new MenuItem { Header = "下 (Bottom)", IsChecked = current == SubTabPlacement.Bottom };
        itemBottom.Click += (_, _) => SetFolderPaneSubTabPlacement(pane, SubTabPlacement.Bottom);
        placementMenu.Items.Add(itemBottom);

        return placementMenu;
    }

    private void SetFolderPaneSubTabPlacement(FolderPane pane, SubTabPlacement placement)
    {
        pane.SubTabPlacement = placement;
        _workspaceLocalState.MarkDirty("subtab-placement-change");
    }

    private void ResetFolderPaneSubTabBarWidth(FolderPane pane)
    {
        var defaultWidth = AppSettings.NormalizeSubTabBarWidth(_settingsService.Settings.DefaultSubTabBarWidth);
        pane.SubTabBarWidth = defaultWidth;
        _workspaceLocalState.MarkDirty("subtab-bar-width-reset");
    }

    private void TabsControl_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        CancelScheduledWorkspaceRenameClick();
        ClearPendingWorkspaceRenameClick();

        if (FindVisualParent<TabItem>(e.OriginalSource as DependencyObject)?.DataContext is MainTabItem { IsInternalPage: true } internalPage)
        {
            e.Handled = true;
            ShowInternalPageTabContextMenu(internalPage);
            return;
        }

        if (GetWorkspaceSession(FindVisualParent<TabItem>(e.OriginalSource as DependencyObject)?.DataContext) is not { } session)
        {
            e.Handled = true;
            ShowTabBarContextMenu();
            return;
        }

        e.Handled = true;
        ShowTabContextMenu(session);
    }

    private void ShowTabBarContextMenu()
    {
        _tabContextMenus.ShowTabBarContextMenu(TabsControl, _workspaceSessions);
    }

    private void ShowTabContextMenu(WorkspaceSession session)
    {
        _tabContextMenus.ShowTabContextMenu(TabsControl, _workspaceSessions, session);
    }

    private void AddUserCommandsSubMenu(
        ContextMenu menu,
        string currentDir,
        IReadOnlyList<FileEntry> selectedEntries,
        bool addLeadingSeparator)
    {
        _userCommandService.Load();

        if (addLeadingSeparator)
        {
            var sep = new Separator();
            ApplySeparatorStyle(sep);
            menu.Items.Add(sep);
        }

        var subMenu = new MenuItem
        {
            Header = _text.Get("ContextCommands")
        };
        ApplyMenuItemStyle(subMenu);

        foreach (var cmd in _userCommandService.Commands)
        {
            if (!cmd.ShouldShow(selectedEntries))
            {
                continue;
            }

            var isEnabled = true;
            if (string.Equals(cmd.Target ?? "", "Selection", StringComparison.OrdinalIgnoreCase))
            {
                isEnabled = selectedEntries.Count > 0;
            }

            var item = new MenuItem
            {
                Header = cmd.Name ?? "",
                IsEnabled = isEnabled
            };
            ApplyMenuItemStyle(item);
            item.Click += (_, _) => ExecuteUserCommand(cmd, currentDir, selectedEntries);
            subMenu.Items.Add(item);
        }

        if (_userCommandService.Commands.Count > 0)
        {
            var commandsSeparator = new Separator();
            ApplySeparatorStyle(commandsSeparator);
            subMenu.Items.Add(commandsSeparator);
        }

        var openCommandsFileItem = new MenuItem
        {
            Header = _text.Get("OpenCommandsFileMenu")
        };
        ApplyMenuItemStyle(openCommandsFileItem);
        openCommandsFileItem.Click += (_, _) => OpenCommandsFile();
        subMenu.Items.Add(openCommandsFileItem);

        var openCommandsLocationItem = new MenuItem
        {
            Header = _text.Get("OpenCommandsLocationMenu")
        };
        ApplyMenuItemStyle(openCommandsLocationItem);
        openCommandsLocationItem.Click += (_, _) => OpenUserCommandPath(
            AppPaths.LocalDirectory,
            _text.Get("OpenCommandsLocationMenu"));
        subMenu.Items.Add(openCommandsLocationItem);

        var openCommandScriptsDirectoryItem = new MenuItem
        {
            Header = _text.Get("OpenCommandScriptsDirectoryMenu")
        };
        ApplyMenuItemStyle(openCommandScriptsDirectoryItem);
        openCommandScriptsDirectoryItem.Click += (_, _) => OpenUserCommandPath(
            AppPaths.CommandsDirectory,
            _text.Get("OpenCommandScriptsDirectoryMenu"));
        subMenu.Items.Add(openCommandScriptsDirectoryItem);

        menu.Items.Add(subMenu);
    }

    private void OpenCommandsFile()
    {
        try
        {
            Process.Start(ExternalProcessStartInfo.CreateShellExecute(AppPaths.CommandsPath));
        }
        catch
        {
            try
            {
                var startInfo = new ProcessStartInfo("notepad.exe")
                {
                    UseShellExecute = true
                };
                startInfo.ArgumentList.Add(AppPaths.CommandsPath);
                ExternalProcessStartInfo.ApplyWorkingDirectory(startInfo, Path.GetDirectoryName(AppPaths.CommandsPath));
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                ShowUserCommandManagementError(_text.Get("OpenCommandsFileMenu"), ex);
            }
        }
    }

    private void OpenUserCommandPath(string path, string operationName)
    {
        try
        {
            Directory.CreateDirectory(path);
            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(path);
            ExternalProcessStartInfo.ApplyWorkingDirectory(startInfo, path);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            ShowUserCommandManagementError(operationName, ex);
        }
    }

    private void ShowUserCommandManagementError(string operationName, Exception ex)
    {
        MessageBox.Show(
            this,
            _text.Format("UserCommandExecuteFailed", operationName, ex.Message),
            _text.Get("UserCommandErrorTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void ApplyMenuItemStyle(MenuItem item)
    {
        if (Application.Current?.TryFindResource(typeof(MenuItem)) is Style style)
        {
            item.Style = style;
        }
    }

    private void ApplySeparatorStyle(Separator separator)
    {
        if (Application.Current?.TryFindResource(typeof(Separator)) is Style style)
        {
            separator.Style = style;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private bool IsInternalClipboardValid()
    {
        if (_pendingFileOperation is null)
        {
            return false;
        }

        if (GetClipboardSequenceNumber() != _internalClipboardSequence)
        {
            if (ClipboardContainsExternalFileTransfer())
            {
                _pendingFileOperation = null;
                return false;
            }
        }

        return true;
    }

    private bool ClipboardContainsFileDropList()
    {
        try
        {
            return Clipboard.ContainsFileDropList();
        }
        catch
        {
            return false;
        }
    }

    private bool ClipboardContainsExternalFileTransfer() =>
        ClipboardContainsFileDropList() || ShellVirtualFileClipboard.ContainsVirtualFiles();

}
