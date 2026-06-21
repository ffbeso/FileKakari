using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Controls;

namespace FileKakari;

internal sealed class TabContextMenuController
{
    private readonly LocalizationService _text;
    private readonly PerformanceLogger _performanceLogger;
    private readonly Func<WorkspaceSession?, FolderTab?> _getSessionActiveTab;
    private readonly Func<Task> _createNewTabAsync;
    private readonly Func<string?, FolderTab?, Task> _createNewTabFromTabAsync;
    private readonly Func<Task> _restoreLastClosedTabAsync;
    private readonly Func<WorkspaceSession, Task> _closeSessionAsync;
    private readonly Func<WorkspaceSession, Task> _closeOtherSessionsAsync;
    private readonly Func<WorkspaceSession, Task> _closeSessionsToRightAsync;
    private readonly Action<WorkspaceSession> _renameWorkspace;
    private readonly Action<WorkspaceSession> _toggleWorkspaceLock;
    private readonly Action<FolderTab> _openTabInExplorer;
    private readonly Func<bool> _hasClosedTab;

    public TabContextMenuController(
        LocalizationService text,
        PerformanceLogger performanceLogger,
        Func<WorkspaceSession?, FolderTab?> getSessionActiveTab,
        Func<Task> createNewTabAsync,
        Func<string?, FolderTab?, Task> createNewTabFromTabAsync,
        Func<Task> restoreLastClosedTabAsync,
        Func<WorkspaceSession, Task> closeSessionAsync,
        Func<WorkspaceSession, Task> closeOtherSessionsAsync,
        Func<WorkspaceSession, Task> closeSessionsToRightAsync,
        Action<WorkspaceSession> renameWorkspace,
        Action<WorkspaceSession> toggleWorkspaceLock,
        Action<FolderTab> openTabInExplorer,
        Func<bool> hasClosedTab)
    {
        _text = text;
        _performanceLogger = performanceLogger;
        _getSessionActiveTab = getSessionActiveTab;
        _createNewTabAsync = createNewTabAsync;
        _createNewTabFromTabAsync = createNewTabFromTabAsync;
        _restoreLastClosedTabAsync = restoreLastClosedTabAsync;
        _closeSessionAsync = closeSessionAsync;
        _closeOtherSessionsAsync = closeOtherSessionsAsync;
        _closeSessionsToRightAsync = closeSessionsToRightAsync;
        _renameWorkspace = renameWorkspace;
        _toggleWorkspaceLock = toggleWorkspaceLock;
        _openTabInExplorer = openTabInExplorer;
        _hasClosedTab = hasClosedTab;
    }

    public void ShowTabBarContextMenu(ItemsControl placementTarget, ObservableCollection<WorkspaceSession> sessions)
    {
        _performanceLogger.Write($"tabbar-context-open count={sessions.Count} hasClosedTab={_hasClosedTab()}");
        var menu = new ContextMenu();
        var newTabItem = new MenuItem
        {
            Header = _text.Get("NewTabButton")
        };
        newTabItem.Click += async (_, _) => await _createNewTabAsync();

        menu.Items.Add(newTabItem);
        menu.Items.Add(CreateRestoreClosedTabMenuItem());
        menu.PlacementTarget = placementTarget;
        menu.IsOpen = true;
    }

    public void ShowTabContextMenu(ItemsControl placementTarget, ObservableCollection<WorkspaceSession> sessions, WorkspaceSession session)
    {
        var tab = _getSessionActiveTab(session);
        if (tab is null)
        {
            return;
        }

        var index = sessions.IndexOf(session);
        _performanceLogger.Write($"tab-context-open path=\"{tab.Navigation.CurrentPath}\" index={index} count={sessions.Count}");
        var menu = new ContextMenu();
        var closeItem = new MenuItem
        {
            Header = _text.Get("CloseThisTabMenu"),
            IsEnabled = sessions.Count > 1 && !session.IsFolderLocked
        };
        closeItem.Click += async (_, _) => await _closeSessionAsync(session);

        var closeOthersItem = new MenuItem
        {
            Header = _text.Get("CloseOtherTabsMenu"),
            IsEnabled = sessions.Any(candidate => !ReferenceEquals(candidate, session) && !candidate.IsFolderLocked)
        };
        closeOthersItem.Click += async (_, _) => await _closeOtherSessionsAsync(session);

        var closeRightItem = new MenuItem
        {
            Header = _text.Get("CloseTabsToRightMenu"),
            IsEnabled = index >= 0
                && index < sessions.Count - 1
                && sessions.Skip(index + 1).Any(candidate => !candidate.IsFolderLocked)
        };
        closeRightItem.Click += async (_, _) => await _closeSessionsToRightAsync(session);

        var newTabItem = new MenuItem
        {
            Header = _text.Get("NewTabButton")
        };
        newTabItem.Click += async (_, _) => await _createNewTabFromTabAsync(tab.Navigation.CurrentPath, tab);

        var renameItem = new MenuItem
        {
            Header = _text.Get("ContextRename"),
            IsEnabled = true
        };
        renameItem.Click += (_, _) => _renameWorkspace(session);

        var isLocked = session.IsFolderLocked;
        var lockTabItem = new MenuItem
        {
            Header = isLocked
                ? _text.Get("UnlockFolderTabMenu")
                : _text.Get("LockFolderTabMenu"),
            IsEnabled = true
        };
        lockTabItem.Click += (_, _) => _toggleWorkspaceLock(session);

        menu.Items.Add(closeItem);
        menu.Items.Add(closeOthersItem);
        menu.Items.Add(closeRightItem);
        menu.Items.Add(CreateRestoreClosedTabMenuItem());
        menu.Items.Add(new Separator());
        menu.Items.Add(newTabItem);
        menu.Items.Add(renameItem);
        menu.Items.Add(lockTabItem);
        menu.PlacementTarget = placementTarget;
        menu.IsOpen = true;
    }

    private MenuItem CreateRestoreClosedTabMenuItem()
    {
        var item = new MenuItem
        {
            Header = _text.Get("RestoreClosedTabMenu"),
            IsEnabled = _hasClosedTab()
        };
        item.Click += async (_, _) => await _restoreLastClosedTabAsync();
        return item;
    }
}
