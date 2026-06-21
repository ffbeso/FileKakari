using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FileKakari;

internal record struct WorkspaceMutationResult(
    bool Success,
    WorkspaceSession? ActiveSession,
    bool RequiresSaveActiveLocalState,
    IReadOnlyList<WorkspaceSession>? SessionsToRemove = null,
    int? ClosedSessionIndex = null,
    bool ActiveSessionChanged = false,
    WorkspaceSession? ReplacedSession = null,
    int? InsertIndex = null
);

internal class WorkspaceController
{
    public WorkspaceSession CreateSinglePaneSession(FolderTab tab)
    {
        tab.SetHeaderOverride(null);
        var representativeTab = new FolderTab(
            tab.Navigation.CurrentPath,
            viewMode: tab.State.ViewMode);
        var tabs = new ObservableCollection<FolderTab>([representativeTab]);
        var paneTabs = new ObservableCollection<FolderTab>([tab]);
        var session = new WorkspaceSession(
            tab.Navigation.CurrentPath,
            tabs,
            workspace: null,
            tab.State.ViewMode)
        {
            SelectedTabIndex = 0
        };
        var primaryPane = new WorkspacePaneGroup("primary", paneTabs, tab.Navigation.CurrentPath)
        {
            SelectedTabIndex = 0,
            SelectedTabId = tab.Id
        };
        session.PaneGroups.Add(primaryPane);
        session.ActivePaneGroup = primaryPane;
        session.LayoutRoot = new WorkspacePaneGroupDefinition(primaryPane.Id, primaryPane.SelectedTabIndex, []);
        return session;
    }

    public WorkspaceMutationResult AddSession(WorkspaceSession? activeSession, WorkspaceSession session)
    {
        var requiresSave = activeSession is not null;
        return new WorkspaceMutationResult(
            Success: true,
            ActiveSession: session,
            RequiresSaveActiveLocalState: requiresSave,
            ActiveSessionChanged: !ReferenceEquals(activeSession, session)
        );
    }

    public WorkspaceMutationResult InsertSession(int index, IReadOnlyList<WorkspaceSession> sessions, WorkspaceSession? activeSession, WorkspaceSession session)
    {
        var clampedIndex = Math.Clamp(index, 0, sessions.Count);
        return new WorkspaceMutationResult(
            Success: true,
            ActiveSession: session,
            RequiresSaveActiveLocalState: activeSession is not null,
            InsertIndex: clampedIndex,
            ActiveSessionChanged: !ReferenceEquals(activeSession, session)
        );
    }

    public WorkspaceMutationResult TrySelectSession(WorkspaceSession? activeSession, WorkspaceSession selectedSession)
    {
        if (ReferenceEquals(selectedSession, activeSession))
        {
            return new WorkspaceMutationResult(
                Success: true,
                ActiveSession: activeSession,
                RequiresSaveActiveLocalState: false
            );
        }

        return new WorkspaceMutationResult(
            Success: true,
            ActiveSession: selectedSession,
            RequiresSaveActiveLocalState: true,
            ActiveSessionChanged: true
        );
    }

    public WorkspaceMutationResult CloseSession(IReadOnlyList<WorkspaceSession> sessions, WorkspaceSession activeSession, WorkspaceSession session)
    {
        if (sessions.Count <= 1 || session.IsFolderLocked)
        {
            return new WorkspaceMutationResult(Success: false, ActiveSession: activeSession, RequiresSaveActiveLocalState: false);
        }

        var index = GetIndexOf(sessions, session);
        if (index < 0)
        {
            return new WorkspaceMutationResult(Success: false, ActiveSession: activeSession, RequiresSaveActiveLocalState: false);
        }

        var requiresSave = ReferenceEquals(activeSession, session);
        var nextIndex = index < sessions.Count - 1 ? index + 1 : index - 1;
        var nextSession = sessions[nextIndex];

        return new WorkspaceMutationResult(
            Success: true,
            ActiveSession: nextSession,
            RequiresSaveActiveLocalState: requiresSave,
            SessionsToRemove: new[] { session },
            ClosedSessionIndex: index,
            ActiveSessionChanged: !ReferenceEquals(activeSession, nextSession)
        );
    }

    public WorkspaceMutationResult CloseOtherSessions(IReadOnlyList<WorkspaceSession> sessions, WorkspaceSession activeSession, WorkspaceSession session)
    {
        if (sessions.Count <= 1 || !sessions.Contains(session))
        {
            return new WorkspaceMutationResult(Success: false, ActiveSession: activeSession, RequiresSaveActiveLocalState: false);
        }

        var toRemove = sessions.Where(s => !ReferenceEquals(s, session) && !s.IsFolderLocked).ToList();
        if (toRemove.Count == 0)
        {
            return new WorkspaceMutationResult(Success: false, ActiveSession: activeSession, RequiresSaveActiveLocalState: false);
        }

        return new WorkspaceMutationResult(
            Success: true,
            ActiveSession: session,
            RequiresSaveActiveLocalState: true,
            SessionsToRemove: toRemove,
            ActiveSessionChanged: !ReferenceEquals(activeSession, session)
        );
    }

    public WorkspaceMutationResult CloseSessionsToRight(IReadOnlyList<WorkspaceSession> sessions, WorkspaceSession activeSession, WorkspaceSession session)
    {
        var index = GetIndexOf(sessions, session);
        if (index < 0 || index >= sessions.Count - 1)
        {
            return new WorkspaceMutationResult(Success: false, ActiveSession: activeSession, RequiresSaveActiveLocalState: false);
        }

        var toRemove = new List<WorkspaceSession>();
        for (var i = sessions.Count - 1; i > index; i--)
        {
            if (!sessions[i].IsFolderLocked)
            {
                toRemove.Add(sessions[i]);
            }
        }

        if (toRemove.Count == 0)
        {
            return new WorkspaceMutationResult(Success: false, ActiveSession: activeSession, RequiresSaveActiveLocalState: false);
        }

        var activeIndex = GetIndexOf(sessions, activeSession);
        var activeRemoved = activeIndex > index;
        var nextSession = activeRemoved ? session : activeSession;

        return new WorkspaceMutationResult(
            Success: true,
            ActiveSession: nextSession,
            RequiresSaveActiveLocalState: true,
            SessionsToRemove: toRemove,
            ActiveSessionChanged: !ReferenceEquals(activeSession, nextSession)
        );
    }

    private static int GetIndexOf(IReadOnlyList<WorkspaceSession> list, WorkspaceSession item)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }
        return -1;
    }
}
