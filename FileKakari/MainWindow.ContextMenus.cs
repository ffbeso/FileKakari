using System.Diagnostics;
using System.IO;
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
        var isSpecialView = SpecialLocationService.IsSpecialUri(navigation.CurrentPath);
        var isDisconnected = activeTab.IsDisconnected;
        var canOperateInFolder = !isSpecialView && !isDisconnected;
        var canPaste = _pendingFileOperation is not null && canOperateInFolder;
        var menu = new ContextMenu
        {
            PlacementTarget = ItemsList
        };

        if (clickedEntry is null)
        {
            menu.Items.Add(CreateMenuItem(_text.Get("ContextPaste"), canPaste, () => PastePendingFileOperationAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("NewFolderButton"), canOperateInFolder, () => CreateNewItemAsync(NewItemKind.Folder)));
            menu.Items.Add(CreateMenuItem(_text.Get("NewFileButton"), canOperateInFolder, () => CreateNewItemAsync(NewItemKind.TextFile)));

            var sep = new Separator();
            ApplySeparatorStyle(sep);
            menu.Items.Add(sep);

            var viewSubMenu = new MenuItem
            {
                Header = _text.Get("ContextView")
            };
            ApplyMenuItemStyle(viewSubMenu);
            _viewModeController.PopulateMenu(viewSubMenu);
            menu.Items.Add(viewSubMenu);

            AddUserCommandsSubMenu(menu, navigation.CurrentPath, selectedEntries, addLeadingSeparator: true);
        }
        else
        {
            var singleSelection = selectedEntries.Count == 1;
            var singleDirectory = singleSelection && selectedEntries[0].IsDirectory;
            menu.Items.Add(CreateMenuItem(_text.Get("ContextOpen"), singleSelection && !isDisconnected, () => OpenSelectedAsync()));
            AddUserCommandsSubMenu(menu, navigation.CurrentPath, selectedEntries, addLeadingSeparator: false);
            menu.Items.Add(CreateMenuItem(_text.Get("ContextOpenInNewTab"), singleDirectory && !isDisconnected, () => OpenSelectedAsync(openDirectoryInNewTab: true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCopy"), canOperateInFolder, () => SetPendingFileOperationAsync(PendingFileOperationKind.Copy)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCut"), canOperateInFolder, () => SetPendingFileOperationAsync(PendingFileOperationKind.Move)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextPaste"), canPaste, () => PastePendingFileOperationAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("ContextRename"), singleSelection && canOperateInFolder, () => BeginRenameSelected()));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextDelete"), canOperateInFolder, () => DeleteSelectedAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCopyPath"), true, () => CopyPathsToClipboard(selectedEntries)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextProperties"), true, () => ShowPropertiesAsync(selectedEntries)));
        }

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

    private void WorkspacePaneFileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (WorkspaceSplitGrid.Visibility != Visibility.Visible
            || sender is not ListView listView
            || listView.DataContext is not FolderPane pane)
        {
            return;
        }

        if (IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var clickedEntry = FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FileEntry;
        PreparePaneRightClickSelection(pane, listView, clickedEntry);
        e.Handled = true;
        ShowWorkspacePaneContextMenu(pane, listView, clickedEntry);
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
        if (pane.ActiveTab is not { } tab || pane.ActiveTabState is not { } state)
        {
            return;
        }

        var selectedEntries = listView.SelectedItems.OfType<FileEntry>().ToList();
        var isSpecialView = SpecialLocationService.IsSpecialUri(tab.Navigation.CurrentPath);
        var canOperateInFolder = !isSpecialView && !tab.IsDisconnected;
        var canPaste = _pendingFileOperation is not null && canOperateInFolder;
        var menu = new ContextMenu
        {
            PlacementTarget = listView
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
            _viewModeController.PopulateWorkspaceMenu(viewSubMenu, pane, state, reason => _workspaceLocalState.MarkDirty(reason), ApplyDisplayModeToPane);
            menu.Items.Add(viewSubMenu);

            AddUserCommandsSubMenu(menu, tab.Navigation.CurrentPath, selectedEntries, addLeadingSeparator: true);
        }
        else
        {
            var singleSelection = selectedEntries.Count == 1;
            var singleDirectory = singleSelection && selectedEntries[0].IsDirectory;
            menu.Items.Add(CreateMenuItem(_text.Get("ContextOpen"), singleSelection && !tab.IsDisconnected, () => OpenWorkspacePaneSelectionAsync(pane, selectedEntries[0])));
            AddUserCommandsSubMenu(menu, tab.Navigation.CurrentPath, selectedEntries, addLeadingSeparator: false);
            menu.Items.Add(CreateMenuItem(_text.Get("ContextOpenInNewTab"), singleDirectory && !tab.IsDisconnected, () => CreateWorkspacePaneSubTabAsync(pane, selectedEntries[0].FullPath, tab)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCopy"), canOperateInFolder, () => SetPendingFileOperationAsync(PendingFileOperationKind.Copy, pane)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCut"), canOperateInFolder, () => SetPendingFileOperationAsync(PendingFileOperationKind.Move, pane)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextPaste"), canPaste, () => PastePendingFileOperationAsync(pane)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("ContextRename"), singleSelection && canOperateInFolder && IsRenameable(selectedEntries[0]), () => BeginRenameSelected(pane)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextDelete"), canOperateInFolder, () => DeleteSelectedAsync(pane)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(_text.Get("ContextCopyPath"), true, () => CopyPathsToClipboard(selectedEntries)));
            menu.Items.Add(CreateMenuItem(_text.Get("ContextProperties"), true, () => ShowPropertiesAsync(selectedEntries)));
        }

        menu.IsOpen = true;
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
        menu.IsOpen = true;
    }

    private void TabsControl_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        CancelScheduledWorkspaceRenameClick();
        ClearPendingWorkspaceRenameClick();

        if (FindVisualParent<TabItem>(e.OriginalSource as DependencyObject)?.DataContext is MainTabItem { IsInternalPage: true })
        {
            e.Handled = true;
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
            Process.Start(new ProcessStartInfo(AppPaths.CommandsPath) { UseShellExecute = true });
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

}
