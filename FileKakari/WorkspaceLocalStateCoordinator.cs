using System.Windows.Threading;

namespace FileKakari;

sealed class WorkspaceLocalStateCoordinator
{
    private readonly WorkspaceSessionFolderTabSync _tabSync;
    private readonly PerformanceLogger _performanceLogger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveTimer;
    private readonly Func<WorkspaceSession?> _getActiveSession;
    private readonly Func<int> _getSelectedIndex;
    private readonly System.Action _saveSessionState;
    private bool _isDirty;
    private bool _captureQueued;

    internal WorkspaceLocalStateCoordinator(
        WorkspaceSessionFolderTabSync tabSync,
        PerformanceLogger performanceLogger,
        Dispatcher dispatcher,
        TimeSpan saveDelay,
        Func<WorkspaceSession?> getActiveSession,
        Func<int> getSelectedIndex,
        System.Action saveSessionState)
    {
        _tabSync = tabSync;
        _performanceLogger = performanceLogger;
        _dispatcher = dispatcher;
        _getActiveSession = getActiveSession;
        _getSelectedIndex = getSelectedIndex;
        _saveSessionState = saveSessionState;
        _saveTimer = new DispatcherTimer { Interval = saveDelay };
        _saveTimer.Tick += SaveTimer_Tick;
    }

    internal void Stop()
    {
        _saveTimer.Stop();
    }

    internal void Capture(bool markDirty = false, string reason = "workspace-session")
    {
        var session = _getActiveSession();
        if (session is null)
        {
            return;
        }

        if (session.ActivePaneGroup is { } activeWorkspacePaneGroup)
        {
            var rootOffset = (session.Workspace is not null && session.Workspace.HasRootPath) ? 1 : 0;
            session.SelectedTabIndex = Math.Clamp(
                activeWorkspacePaneGroup.SelectedTabIndex + rootOffset,
                0,
                Math.Max(0, activeWorkspacePaneGroup.Tabs.Count));
            activeWorkspacePaneGroup.RefreshDisplay();

            if (markDirty)
            {
                MarkDirty(reason);
            }

            return;
        }

        _tabSync.CaptureFromDisplay(session, _getSelectedIndex());
        if (session.ActivePaneGroup is { } activePaneGroup)
        {
            activePaneGroup.RefreshDisplay();
        }

        if (markDirty)
        {
            MarkDirty(reason);
        }
    }

    internal void QueueCapture(bool markDirty = false, string reason = "workspace-session")
    {
        if (_captureQueued)
        {
            return;
        }

        _captureQueued = true;
        _dispatcher.BeginInvoke(
            new Action(() =>
            {
                _captureQueued = false;
                if (_tabSync.IsApplying || _getActiveSession() is null)
                {
                    return;
                }

                Capture(markDirty, reason);
            }),
            DispatcherPriority.Background);
    }

    internal void SaveActiveLocalState()
    {
        _saveTimer.Stop();
        var session = _getActiveSession();
        if (session is null)
        {
            _isDirty = false;
            return;
        }

        Capture();
        _saveSessionState?.Invoke();
        _isDirty = false;
    }

    internal void MarkDirty(string reason)
    {
        var session = _getActiveSession();
        if (session is null
            || _tabSync.IsApplying)
        {
            return;
        }

        _isDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        _performanceLogger.Write($"workspace-local-dirty reason={reason} root=\"{session.RootPath}\"");
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_isDirty)
        {
            SaveActiveLocalState();
        }
    }
}
