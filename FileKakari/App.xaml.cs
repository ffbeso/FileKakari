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
            var restorePlan = SessionStateRestorePlanner.Prepare(
                session,
                userProfile,
                settingsService.Settings.DisplayMode);
            startTabs.AddRange(restorePlan.Tabs);
            selectedTabIndex = restorePlan.SelectedTabIndex;
        }

        var window = new MainWindow(startTabs, selectedTabIndex, settingsService, sessionStateService);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            PreviewTemporaryFileManager.CleanupCurrentProcessMediaPreviewFiles();
        }
        catch
        {
        }
        base.OnExit(e);
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
