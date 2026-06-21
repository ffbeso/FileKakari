using System.Windows.Controls;

namespace FileKakari;

public partial class MainWindow
{
    private async void TabsControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCrashContextSnapshot("tab-selection-changed");
        ClearWorkspacePaneRangeSelection();
        if (!ReferenceEquals(e.Source, TabsControl)
            || _isSwitchingTabs
            || _isSwitchingWorkspacePane)
        {
            return;
        }

        SaveWorkspacePanesViewState();

        var selectedMainTab = TabsControl.SelectedItem as MainTabItem;
        UpdateMainTabContent(selectedMainTab);
        if (selectedMainTab?.WorkspaceSession is not { } selectedSession)
        {
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

        var result = _workspaceController.TrySelectSession(_activeWorkspaceSession, selectedSession);
        if (!result.Success)
        {
            _performanceLogger.Write($"tab-selection-skip selectedIndex={TabsControl.SelectedIndex} tabCount={_workspaceSessions.Count}");
            return;
        }

        var wasInternalPage = e.RemovedItems
            .OfType<MainTabItem>()
            .Any(tab => tab.IsInternalPage);

        if (!result.ActiveSessionChanged)
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
        _activeWorkspaceSession = result.ActiveSession!;
        UpdateActiveWorkspaceSessionUi(_activeWorkspaceSession);
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

    private void SelectWorkspaceSession(WorkspaceSession? session)
    {
        var tab = GetMainTabItem(session);
        TabsControl.SelectedItem = tab;
        UpdateMainTabContent(tab);
    }
}
