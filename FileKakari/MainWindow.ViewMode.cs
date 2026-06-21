using System.Windows;
using System.Windows.Controls;

namespace FileKakari;

public partial class MainWindow : Window
{
    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedInternalPage() is not null) return;
        if (sender is Button button)
        {
            _viewModeController.ShowMenu(button);
        }
    }

    private void WorkspacePaneViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedInternalPage() is not null) return;
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (sender is not FrameworkElement placementTarget
            || GetWorkspacePaneFromSender(sender) is not { } pane
            || pane.ActiveTabState is not { } state)
        {
            return;
        }

        _viewModeController.ShowWorkspaceMenu(
            placementTarget,
            pane,
            state,
            reason => _workspaceLocalState.MarkDirty(reason),
            ApplyDisplayModeToPane);
    }

    private void ApplyDisplayMode()
    {
        if (GetSelectedInternalPage() is not null) return;
        ApplyDisplayMode(ActiveTab);
    }

    private void ApplyDisplayMode(FolderTab? tab)
    {
        if (GetSelectedInternalPage() is not null) return;
        var mode = GetDisplayMode(tab);

        _viewModeApplier.Apply(mode, _settingsService.Settings.RowHeight);

        _performanceLogger.Write($"display-mode-apply mode={mode} items={_items.Count} view={(ItemsList.View is GridView ? "gridview" : "content")}");
    }

    private void ApplyDisplayModeToPane(FolderPane pane)
    {
        if (GetSelectedInternalPage() is not null) return;
        var listView = FindListViewForPane(pane);
        if (listView is not null)
        {
            ApplyDisplayModeToPane(listView, pane);
        }
    }

    private void ApplyDisplayModeToPane(ListView listView, FolderPane pane)
    {
        if (GetSelectedInternalPage() is not null) return;
        var tab = pane.ActiveTab;
        var mode = AppSettings.NormalizeDisplayMode(tab?.State.ViewMode ?? _settingsService.Settings.DisplayMode);
        _viewModeApplier.ApplyTo(listView, mode, _settingsService.Settings.RowHeight);
    }

    private FileDisplayMode GetCurrentDisplayMode()
    {
        return GetDisplayMode(ActiveTab);
    }

    private FileDisplayMode GetDisplayMode(FolderTab? tab)
    {
        return AppSettings.NormalizeDisplayMode(tab?.State.ViewMode ?? _settingsService.Settings.DisplayMode);
    }
}
