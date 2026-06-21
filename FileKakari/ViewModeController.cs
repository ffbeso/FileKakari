using System.Windows;
using System.Windows.Controls;

namespace FileKakari;

internal sealed class ViewModeController
{
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _text;
    private readonly PerformanceLogger _performanceLogger;
    private readonly Func<FileDisplayMode> _getCurrentMode;
    private readonly Action<FileDisplayMode> _setCurrentMode;
    private readonly Func<IReadOnlyList<string>> _getSelectedPaths;
    private readonly Func<double> _getVerticalOffset;
    private readonly Action _saveColumnWidths;
    private readonly Action _applyDisplayMode;
    private readonly Action _applyColumnSettings;
    private readonly Action<IReadOnlyList<string>> _restoreSelection;
    private readonly Func<double, Task> _restoreScrollOffsetAsync;
    private readonly Func<int> _getItemCount;

    public ViewModeController(
        SettingsService settingsService,
        LocalizationService text,
        PerformanceLogger performanceLogger,
        Func<FileDisplayMode> getCurrentMode,
        Action<FileDisplayMode> setCurrentMode,
        Func<IReadOnlyList<string>> getSelectedPaths,
        Func<double> getVerticalOffset,
        Action saveColumnWidths,
        Action applyDisplayMode,
        Action applyColumnSettings,
        Action<IReadOnlyList<string>> restoreSelection,
        Func<double, Task> restoreScrollOffsetAsync,
        Func<int> getItemCount)
    {
        _settingsService = settingsService;
        _text = text;
        _performanceLogger = performanceLogger;
        _getCurrentMode = getCurrentMode;
        _setCurrentMode = setCurrentMode;
        _getSelectedPaths = getSelectedPaths;
        _getVerticalOffset = getVerticalOffset;
        _saveColumnWidths = saveColumnWidths;
        _applyDisplayMode = applyDisplayMode;
        _applyColumnSettings = applyColumnSettings;
        _restoreSelection = restoreSelection;
        _restoreScrollOffsetAsync = restoreScrollOffsetAsync;
        _getItemCount = getItemCount;
    }

    public void ShowMenu(Button placementTarget)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget
        };

        PopulateMenu(menu);
        menu.IsOpen = true;
    }

    public void PopulateMenu(ItemsControl parent)
    {
        AddMenuItem(parent, FileDisplayMode.Details, _text.Get("ViewModeDetails"));
        AddMenuItem(parent, FileDisplayMode.Compact, _text.Get("ViewModeCompact"));
        AddMenuItem(parent, FileDisplayMode.List, _text.Get("ViewModeList"));
    }

    private void AddMenuItem(ItemsControl parent, FileDisplayMode mode, string header)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = _getCurrentMode() == mode,
            Tag = mode
        };
        if (Application.Current?.TryFindResource(typeof(MenuItem)) is Style style)
        {
            item.Style = style;
        }
        item.Click += async (_, _) =>
        {
            if (item.Tag is FileDisplayMode selectedMode)
            {
                await SwitchAsync(selectedMode);
            }
        };
        parent.Items.Add(item);
    }

    private async Task SwitchAsync(FileDisplayMode mode)
    {
        if (_getCurrentMode() == mode)
        {
            return;
        }

        var selectedPaths = _getSelectedPaths();
        var offset = _getVerticalOffset();
        _saveColumnWidths();
        _setCurrentMode(mode);
        _settingsService.Settings.DisplayMode = mode;
        _applyDisplayMode();
        _applyColumnSettings();
        _restoreSelection(selectedPaths);
        await _restoreScrollOffsetAsync(offset);
        _settingsService.SaveCurrent();
        _performanceLogger.Write($"display-mode-switch mode={mode} selected={selectedPaths.Count} offset={offset:N1} items={_getItemCount()}");
    }

    public void ShowWorkspaceMenu(
        FrameworkElement placementTarget,
        FolderPane pane,
        WorkspaceTabState state,
        Action<string> markDirty,
        Action<FolderPane> applyDisplayMode)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget
        };

        PopulateWorkspaceMenu(menu, pane, state, markDirty, applyDisplayMode);
        menu.IsOpen = true;
    }

    public void PopulateWorkspaceMenu(
        ItemsControl menu,
        FolderPane pane,
        WorkspaceTabState state,
        Action<string> markDirty,
        Action<FolderPane> applyDisplayMode)
    {
        AddWorkspaceMenuItem(menu, pane, state, FileDisplayMode.Details, _text.Get("ViewModeDetails"), markDirty, applyDisplayMode);
        AddWorkspaceMenuItem(menu, pane, state, FileDisplayMode.Compact, _text.Get("ViewModeCompact"), markDirty, applyDisplayMode);
        AddWorkspaceMenuItem(menu, pane, state, FileDisplayMode.List, _text.Get("ViewModeList"), markDirty, applyDisplayMode);
    }

    private void AddWorkspaceMenuItem(
        ItemsControl menu,
        FolderPane pane,
        WorkspaceTabState state,
        FileDisplayMode mode,
        string header,
        Action<string> markDirty,
        Action<FolderPane> applyDisplayMode)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = state.ViewMode == mode,
            Tag = mode
        };
        if (Application.Current?.TryFindResource(typeof(MenuItem)) is Style style)
        {
            item.Style = style;
        }
        item.Click += (_, _) =>
        {
            if (item.Tag is FileDisplayMode selectedMode)
            {
                state.ViewMode = AppSettings.NormalizeDisplayMode(selectedMode);
                applyDisplayMode(pane);
                pane.RefreshDisplay();
                markDirty("pane-view-mode");
                _performanceLogger.Write($"folder-pane-view-mode paneId={pane.Id} stateId={state.Id} mode={state.ViewMode}");
            }
        };
        menu.Items.Add(item);
    }
}
