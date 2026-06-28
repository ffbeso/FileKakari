using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace FileKakari;

public partial class MainWindow
{
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
        UpdateActiveWorkspaceSessionUi(selectedSession);

        var wasInternalPage = e.RemovedItems
            .OfType<MainTabItem>()
            .Any(tab => tab.IsInternalPage);

        if (!result.ActiveSessionChanged && wasSynchronized)
        {
            if (wasInternalPage)
            {
                UpdateWindowTitle();
                UpdateWorkspaceButtonState();

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
            UpdateWorkspaceButtonState();
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
