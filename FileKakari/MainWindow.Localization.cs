using System.Windows;
using System.Windows.Controls;

namespace FileKakari;

public partial class MainWindow : Window
{
    private void ApplyLocalizedText()
    {
        SetToolbarButtonToolTip(InternalPageMenuButton, _text.Get("InternalPageMenuButton"));
        InternalPageSettingsMenuItem.Header = _text.Get("SettingsTitle");
        InternalPageLogViewerMenuItem.Header = _text.Get("LogViewerTitle");
        InternalPageUserCommandEditorMenuItem.Header = _text.Get("UserCommandEditorTitle");
        SetToolbarButtonToolTip(NormalPanePlacesButton, _text.Get("PlacesButton"));
        SetToolbarButtonToolTip(NormalPaneViewModeButton, _text.Get("ViewModeButton"));
        SetToolbarButtonToolTip(NormalPaneNewFolderButton, _text.Get("NewFolderButton"));
        SetToolbarButtonToolTip(NormalPaneNewFileButton, _text.Get("NewFileButton"));
        SetToolbarButtonToolTip(NormalPanePreviewButton, _text.Get("PreviewTitle"));
        SetToolbarButtonToolTip(PreviewCloseButton, _text.Get("PreviewCloseButton"));
        SetToolbarButtonToolTip(PreviewMediaPlayPauseButton, _text.Get("PreviewMediaPlay"));
        SetToolbarButtonToolTip(PreviewMediaStopButton, _text.Get("PreviewMediaStop"));
        PreviewHeaderText.Text = _text.Get("PreviewTitle");
        PreviewUnsupportedTitleText.Text = _text.Get("PreviewUnsupportedTitle");
        PreviewUnsupportedHintText.Text = _text.Get("PreviewUnsupportedHint");
        PreviewInfoNameLabel.Text = _text.Get("PreviewInfoName");
        PreviewInfoExtensionLabel.Text = _text.Get("PreviewInfoExtension");
        PreviewInfoSizeLabel.Text = _text.Get("PreviewInfoSize");
        PreviewInfoModifiedLabel.Text = _text.Get("PreviewInfoModified");
        PreviewInfoPathLabel.Text = _text.Get("PreviewInfoPath");
        FilterPlaceholder.Text = _text.Get("FilterPlaceholder");
        if (GetSelectedWorkspaceSession() is not null
            && ActiveNavigation is { } navigation
            && SpecialLocationService.IsSpecialUri(navigation.CurrentPath))
        {
            SetThisPcColumnHeaders();
        }
        else
        {
            SetFolderColumnHeaders();
        }

        if (_workspaceDisplayPanes is not null)
        {
            foreach (var pane in _workspaceDisplayPanes)
            {
                UpdateWorkspacePaneColumnHeadersForPane(pane);
            }
        }
    }

    private static void SetToolbarButtonToolTip(Button button, string text)
    {
        button.ToolTip = text;
        button.SetResourceReference(ForegroundProperty, "TextBrush");
    }

    private void SetFolderColumnHeaders()
    {
        foreach (var def in ColumnsDefinition)
        {
            if (_columnsById.TryGetValue(def.ColumnId, out var column))
            {
                var resourceKey = GetHeaderResourceKey(def.ColumnId, false);
                SetColumnHeaderTextWithSort(column, _text.Get(resourceKey), def.ColumnId);
            }
        }
    }

    private void SetThisPcColumnHeaders()
    {
        foreach (var def in ColumnsDefinition)
        {
            if (_columnsById.TryGetValue(def.ColumnId, out var column))
            {
                var resourceKey = GetHeaderResourceKey(def.ColumnId, true);
                SetColumnHeaderTextWithSort(column, _text.Get(resourceKey), def.ColumnId);
            }
        }
    }

    private void SetColumnHeaderTextWithSort(GridViewColumn column, string baseText, string columnId)
    {
        var targetState = ActiveTabState;
        var text = baseText;
        if (targetState is not null && string.Equals(targetState.SortColumn, columnId, StringComparison.OrdinalIgnoreCase))
        {
            text += targetState.SortAscending ? " ↑" : " ↓";
        }
        SetColumnHeaderText(column, text);
    }

    private void UpdateNormalPaneColumnHeaders()
    {
        var path = ActiveNavigation?.CurrentPath ?? "";
        if (SpecialLocationService.IsSpecialUri(path))
        {
            SetThisPcColumnHeaders();
        }
        else
        {
            SetFolderColumnHeaders();
        }
    }

    private void SetColumnHeadersForPath(string path)
    {
        if (SpecialLocationService.IsSpecialUri(path))
        {
            SetThisPcColumnHeaders();
            return;
        }

        SetFolderColumnHeaders();
    }
}
