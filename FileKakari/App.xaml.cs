using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FileKakari;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settingsService = new SettingsService();
        settingsService.Load();
        AppStrings.Configure(settingsService.Settings.Language);
        var sessionStateService = new SessionStateService();

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var startTabs = new List<SessionTabState>();
        var selectedTabIndex = 0;
        if (e.Args.Length > 0 && Directory.Exists(e.Args[0]))
        {
            startTabs.Add(new SessionTabState
            {
                Path = e.Args[0],
                ViewMode = settingsService.Settings.DisplayMode
            });
        }
        else
        {
            var session = sessionStateService.Load();
            var selectedValidIndex = 0;
            for (var i = 0; i < session.Tabs.Count; i++)
            {
                var savedTab = session.Tabs[i];
                var path = savedTab.Path;
                if (savedTab.IsWorkspace)
                {
                    var isUnsaved = savedTab.IsUnsavedWorkspace || string.IsNullOrWhiteSpace(savedTab.WorkspacePath);
                    if (isUnsaved)
                    {
                        var workspaceRoot = string.IsNullOrWhiteSpace(savedTab.RootPath) ? path : savedTab.RootPath;
                        if (string.IsNullOrWhiteSpace(workspaceRoot))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(savedTab.WorkspacePath) && string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(path)
                        || (!SpecialLocationService.IsSpecialUri(path) && !Directory.Exists(path)))
                    {
                        continue;
                    }
                }

                if (i < session.SelectedTabIndex)
                {
                    selectedValidIndex++;
                }

                var resolvedTabId = savedTab.TabId;
                if (string.IsNullOrWhiteSpace(resolvedTabId) || string.Equals(resolvedTabId, "root", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedTabId = Guid.NewGuid().ToString("N");
                }

                startTabs.Add(new SessionTabState
                {
                    TabId = resolvedTabId,
                    Path = path,
                    IsWorkspace = savedTab.IsWorkspace,
                    WorkspacePath = savedTab.WorkspacePath,
                    RootPath = savedTab.RootPath,
                    SortColumn = savedTab.SortColumn,
                    SortAscending = savedTab.SortAscending,
                    ViewMode = AppSettings.NormalizeDisplayMode(savedTab.ViewMode),
                    IsFolderLocked = savedTab.IsFolderLocked,
                    LocalState = savedTab.LocalState,
                    IsUnsavedWorkspace = savedTab.IsUnsavedWorkspace,
                    WorkspaceId = savedTab.WorkspaceId,
                    Name = savedTab.Name,
                    ActivePaneId = savedTab.ActivePaneId,
                    Layout = savedTab.Layout
                });
            }

            if (startTabs.Count > 0)
            {
                selectedTabIndex = Math.Clamp(selectedValidIndex, 0, startTabs.Count - 1);
            }
        }

        if (startTabs.Count == 0)
        {
            startTabs.Add(new SessionTabState
            {
                Path = userProfile,
                ViewMode = settingsService.Settings.DisplayMode
            });
        }

        var window = new MainWindow(startTabs, selectedTabIndex, settingsService, sessionStateService);
        window.Show();
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        PerfLog.Write($"unhandled-exception source=dispatcher {FormatException(e.Exception)} {FileKakari.MainWindow.GetCrashContextSnapshot()}");
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        PerfLog.Write($"unhandled-exception source=task-scheduler {FormatException(e.Exception)} {FileKakari.MainWindow.GetCrashContextSnapshot()}");
    }

    private static string FormatException(Exception exception)
    {
        var flattened = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? exception
            : exception;
        var stackFirst = flattened.StackTrace?
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? "";
        return $"type={flattened.GetType().FullName} message=\"{flattened.Message}\" stackFirst=\"{stackFirst}\"";
    }
}
