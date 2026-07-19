using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace FileKakari;

public partial class MainWindow
{
    private readonly HashSet<string> _displayedWorkspaceSessionIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isPreservingMultiSelection;

    public IReadOnlyList<WorkspaceSession> GetDisplayedWorkspaceSessionsInTabOrder()
    {
        return _mainTabs
            .Where(tab => tab.WorkspaceSession is not null)
            .Select(tab => tab.WorkspaceSession!)
            .Where(session => _displayedWorkspaceSessionIds.Contains(session.Id))
            .ToList();
    }

    public WorkspaceSession? PrimaryDisplayedWorkspaceSession =>
        GetDisplayedWorkspaceSessionsInTabOrder().FirstOrDefault();

    public WorkspaceSession? SecondaryDisplayedWorkspaceSession =>
        GetDisplayedWorkspaceSessionsInTabOrder().Skip(1).FirstOrDefault();

    private void SynchronizeDisplayedWorkspaceState(string reason)
    {
        var validSessionIds = _workspaceSessions.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _displayedWorkspaceSessionIds.RemoveWhere(id => !validSessionIds.Contains(id));

        if (_activeWorkspaceSession is not null && validSessionIds.Contains(_activeWorkspaceSession.Id))
        {
            _displayedWorkspaceSessionIds.Add(_activeWorkspaceSession.Id);
        }
        else if (_displayedWorkspaceSessionIds.Count > 0)
        {
            var firstId = _displayedWorkspaceSessionIds.First();
            _activeWorkspaceSession = _workspaceSessions.FirstOrDefault(s => string.Equals(s.Id, firstId, StringComparison.OrdinalIgnoreCase)) ?? _workspaceSessions.FirstOrDefault();
        }
        else if (_workspaceSessions.Count > 0)
        {
            _activeWorkspaceSession = _workspaceSessions[0];
            _displayedWorkspaceSessionIds.Add(_activeWorkspaceSession.Id);
        }

        if (_displayedWorkspaceSessionIds.Count > 2)
        {
            var activeId = _activeWorkspaceSession?.Id;
            var tabOrderedSessionIds = _mainTabs
                .Where(t => t.WorkspaceSession is not null && _displayedWorkspaceSessionIds.Contains(t.WorkspaceSession.Id))
                .Select(t => t.WorkspaceSession!.Id)
                .ToList();

            _displayedWorkspaceSessionIds.Clear();
            if (activeId is not null)
            {
                _displayedWorkspaceSessionIds.Add(activeId);
            }
            foreach (var id in tabOrderedSessionIds)
            {
                if (_displayedWorkspaceSessionIds.Count >= 2)
                {
                    break;
                }
                _displayedWorkspaceSessionIds.Add(id);
            }
        }

        foreach (var tab in _mainTabs)
        {
            if (tab.WorkspaceSession is not null)
            {
                tab.IsDisplayed = _displayedWorkspaceSessionIds.Contains(tab.WorkspaceSession.Id);
            }
            else
            {
                tab.IsDisplayed = false;
            }
        }

        if (_activeWorkspaceSession is not null)
        {
            var activeTab = GetMainTabItem(_activeWorkspaceSession);
            if (activeTab is not null && !ReferenceEquals(TabsControl.SelectedItem, activeTab))
            {
                _isSwitchingTabs = true;
                try
                {
                    TabsControl.SelectedItem = activeTab;
                }
                finally
                {
                    _isSwitchingTabs = false;
                }
            }
        }

        SynchronizeWorkspaceSessionHostVisibility(reason);

        var displayedSessions = GetDisplayedWorkspaceSessionsInTabOrder();
        var primaryId = PrimaryDisplayedWorkspaceSession?.Id ?? "null";
        var secondaryId = SecondaryDisplayedWorkspaceSession?.Id ?? "null";
        _performanceLogger.Write(
            $"displayed-workspace-state-synced reason={reason} " +
            $"activeId={_activeWorkspaceSession?.Id ?? "null"} " +
            $"displayedCount={_displayedWorkspaceSessionIds.Count} " +
            $"primaryId={primaryId} secondaryId={secondaryId}");
    }

    private async void TabsControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCrashContextSnapshot("tab-selection-changed");
        ClearWorkspacePaneRangeSelection();
        var selectedSessionAtEvent = GetSelectedWorkspaceSession();
        var shouldProcessDuringPaneSwitch = _isSwitchingWorkspacePane
            && selectedSessionAtEvent is not null
            && !IsSameWorkspaceSession(selectedSessionAtEvent, _activeWorkspaceSession);

        if (!ReferenceEquals(e.Source, TabsControl)
            || _isSwitchingTabs
            || (_isSwitchingWorkspacePane && !shouldProcessDuringPaneSwitch))
        {
            var reason = !ReferenceEquals(e.Source, TabsControl)
                ? "ignored-source"
                : _isSwitchingTabs
                    ? "ignored-switching-tabs"
                    : "ignored-switching-workspace-pane";
            WriteMainTabSelectionChangedLog(e, reason);
            if (string.Equals(reason, "ignored-switching-workspace-pane", StringComparison.Ordinal)
                && !IsSameWorkspaceSession(selectedSessionAtEvent, _activeWorkspaceSession))
            {
                _performanceLogger.Write(
                    $"main-tab-selection-error reason=ignored-switching-workspace-pane-selected-active-mismatch " +
                    $"selectedSessionId={selectedSessionAtEvent?.Id ?? "null"} " +
                    $"activeSessionId={_activeWorkspaceSession?.Id ?? "null"} " +
                    $"selectedIndex={TabsControl.SelectedIndex}");
            }
            return;
        }

        WriteMainTabSelectionChangedLog(
            e,
            shouldProcessDuringPaneSwitch
                ? "selection-changed-during-pane-switch"
                : "selection-changed");
        var oldSession = _activeWorkspaceSession;
        SaveWorkspacePanesViewState(oldSession);

        var selectedMainTab = TabsControl.SelectedItem as MainTabItem;
        var selectedSession = selectedSessionAtEvent;
        UpdateMainTabContent(selectedMainTab);
        if (selectedSession is null)
        {
            WriteMainTabSelectionChangedLog(e, "no-workspace-session");
            if (e.RemovedItems
                .Cast<object>()
                .Select(GetWorkspaceSession)
                .FirstOrDefault(session => session is not null) is { } removedWorkspaceSession
                && GetSessionActiveTab(removedWorkspaceSession) is { } previousTab)
            {
                SaveTabViewState(previousTab);
                if (removedWorkspaceSession.IsWorkspace)
                {
                    foreach (var pane in removedWorkspaceSession.PaneGroups)
                    {
                        SaveWorkspacePaneColumnWidths(pane);
                    }
                }
                else
                {
                    SaveColumnWidths();
                }
            }

            UpdateWindowTitle();
            return;
        }

        var wasSynchronized = IsWorkspaceSessionSelectionSynchronized(selectedSession);
        var result = _workspaceController.TrySelectSession(_activeWorkspaceSession, selectedSession);
        if (!result.Success)
        {
            WriteMainTabSelectionChangedLog(e, "selection-rejected");
            _performanceLogger.Write($"tab-selection-skip selectedIndex={TabsControl.SelectedIndex} tabCount={_workspaceSessions.Count}");
            return;
        }

        WriteMainTabSelectionChangedLog(e, result.ActiveSessionChanged ? "selected-session-changed" : "selected-session-same");
        _activeWorkspaceSession = selectedSession;
        if (!_isPreservingMultiSelection)
        {
            _displayedWorkspaceSessionIds.Clear();
            _displayedWorkspaceSessionIds.Add(selectedSession.Id);
        }
        _isPreservingMultiSelection = false;
        SynchronizeDisplayedWorkspaceState("selection-changed");
        UpdateActiveWorkspaceSessionUi(selectedSession);

        var wasInternalPage = e.RemovedItems
            .OfType<MainTabItem>()
            .Any(tab => tab.IsInternalPage);

        if (!result.ActiveSessionChanged && wasSynchronized)
        {
            if (wasInternalPage)
            {
                UpdateWindowTitle();

                if (selectedMainTab is not null && !selectedMainTab.IsInternalPage)
                {
                    var activeContext = GetActiveNavigationContext();
                    if (activeContext.ListView is not null)
                    {
                        activeContext.ListView.Focus();
                    }
                }
            }
            return;
        }

        if (e.RemovedItems
            .Cast<object>()
            .Select(GetWorkspaceSession)
            .FirstOrDefault(session => session is not null) is { } previousSession)
        {
            if (GetSessionActiveTab(previousSession) is { } previousTab)
            {
                SaveTabViewState(previousTab);
            }

            if (previousSession.IsWorkspace)
            {
                foreach (var pane in previousSession.PaneGroups)
                {
                    SaveWorkspacePaneColumnWidths(pane);
                }
            }
            else
            {
                SaveColumnWidths();
            }
        }

        if (!IsLoaded)
        {
            return;
        }

        _workspaceLocalState.Capture(markDirty: true, reason: "selected-tab");
        CancelActiveLoadForWorkspaceSwitch(selectedSession, "workspace-switch");
        ApplyWorkspaceSessionToFolderTabs();
        try
        {
            await RestoreActiveTabAsync();
        }
        catch (Exception ex)
        {
            LogException("tabs-selection-restore", ex, ActiveTabState);
            throw;
        }
        finally
        {
            UpdateWindowTitle();
            LogMemoryMetrics("tab-switch");
        }
    }

    private WorkspaceSession? GetSelectedWorkspaceSession()
    {
        return (TabsControl.SelectedItem as MainTabItem)?.WorkspaceSession;
    }

    private MainTabItem? GetMainTabItem(WorkspaceSession? session)
    {
        return session is null
            ? null
            : _mainTabs.FirstOrDefault(tab => ReferenceEquals(tab.WorkspaceSession, session));
    }

    private bool IsWorkspaceSessionSelectionSynchronized(WorkspaceSession targetSession)
    {
        return IsSameWorkspaceSession(GetSelectedWorkspaceSession(), targetSession)
            && IsSameWorkspaceSession(_activeWorkspaceSession, targetSession)
            && targetSession.IsActiveSession;
    }

    private void SelectWorkspaceSession(WorkspaceSession? session, [CallerMemberName] string? caller = null)
    {
        var tab = GetMainTabItem(session);
        _performanceLogger.Write($"main-tab-select-request caller={caller ?? "unknown"} targetSessionId={session?.Id ?? "null"} currentSelectedSessionId={GetSelectedWorkspaceSession()?.Id ?? "null"} activeSessionId={_activeWorkspaceSession?.Id ?? "null"} selectedIndex={TabsControl.SelectedIndex}");
        TabsControl.SelectedItem = tab;
        UpdateMainTabContent(tab);
    }

    private void WriteMainTabSelectionChangedLog(SelectionChangedEventArgs e, string reason)
    {
        _performanceLogger.Write(
            $"main-tab-selection-changed reason={reason} " +
            $"selectedSessionId={GetSelectedWorkspaceSession()?.Id ?? "null"} " +
            $"activeSessionId={_activeWorkspaceSession?.Id ?? "null"} " +
            $"selectedIndex={TabsControl.SelectedIndex} " +
            $"isSwitchingTabs={_isSwitchingTabs} isSwitchingWorkspacePane={_isSwitchingWorkspacePane} " +
            $"switchGeneration={_workspaceSwitchGeneration}");
    }
}
