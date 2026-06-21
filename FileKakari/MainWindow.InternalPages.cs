using System.Windows;

namespace FileKakari;

public partial class MainWindow
{
    private void InternalPageMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (InternalPageMenuButton.ContextMenu is not null)
        {
            InternalPageMenuButton.ContextMenu.PlacementTarget = InternalPageMenuButton;
            InternalPageMenuButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            InternalPageMenuButton.ContextMenu.IsOpen = true;
        }
    }

    private void InternalPageSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenInternalPage(InternalPageKind.Settings);
    }

    private void InternalPageLogViewerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenInternalPage(InternalPageKind.LogViewer);
    }

    private void InternalPageUserCommandEditorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenInternalPage(InternalPageKind.UserCommandEditor);
    }

    private MainTabItem? GetSelectedInternalPage()
    {
        return TabsControl.SelectedItem is MainTabItem { IsInternalPage: true } tab
            ? tab
            : null;
    }

    private void OpenInternalPage(InternalPageKind pageKind)
    {
        var existingTab = _mainTabs.FirstOrDefault(tab => tab.InternalPageKind == pageKind);
        if (existingTab is not null)
        {
            TabsControl.SelectedItem = existingTab;
            return;
        }

        var title = pageKind switch
        {
            InternalPageKind.Settings => _text.Get("SettingsTitle"),
            InternalPageKind.LogViewer => _text.Get("LogViewerTitle"),
            InternalPageKind.UserCommandEditor => _text.Get("UserCommandEditorTitle") is var t && !string.IsNullOrEmpty(t) ? t : "ユーザーコマンド編集",
            _ => pageKind.ToString()
        };
        var content = CreateInternalPageContent(pageKind);
        var tab = MainTabItem.CreateInternalPage(pageKind, title, content);
        _mainTabs.Add(tab);
        TabsControl.SelectedItem = tab;
    }

    private object CreateInternalPageContent(InternalPageKind pageKind)
    {
        if (pageKind == InternalPageKind.LogViewer)
        {
            return new LogViewerView(_text);
        }

        if (pageKind == InternalPageKind.UserCommandEditor)
        {
            return new UserCommandEditorView(_text);
        }

        if (pageKind != InternalPageKind.Settings)
        {
            throw new NotSupportedException($"Internal page is not implemented: {pageKind}");
        }

        var settingsView = new SettingsView(_settingsService.Settings, _text);
        settingsView.SaveRequested += SettingsView_SaveRequested;
        settingsView.CancelRequested += SettingsView_CancelRequested;
        settingsView.OpenFolderRequested += SettingsView_OpenFolderRequested;
        return settingsView;
    }

    private async void SettingsView_SaveRequested(object? sender, EventArgs e)
    {
        if (sender is not SettingsView settingsView
            || _mainTabs.FirstOrDefault(tab => ReferenceEquals(tab.Content, settingsView)) is not { } tab)
        {
            return;
        }

        await ApplySettingsAsync(settingsView.Result);
        CloseInternalPage(tab);
    }

    private void SettingsView_CancelRequested(object? sender, EventArgs e)
    {
        if (sender is SettingsView settingsView
            && _mainTabs.FirstOrDefault(tab => ReferenceEquals(tab.Content, settingsView)) is { } tab)
        {
            CloseInternalPage(tab);
        }
    }

    private async void SettingsView_OpenFolderRequested(object? sender, OpenFolderRequestedEventArgs e)
    {
        try
        {
            await OpenFolderInNewMainTabAsync(e.Path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                _text.Get(e.ErrorTitleKey),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UpdateMainTabContent(MainTabItem? tab)
    {
        var isInternalPage = tab?.IsInternalPage == true;
        InternalPageHost.Content = isInternalPage ? tab!.Content : null;
        InternalPageHost.Visibility = isInternalPage ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceButton.IsEnabled = !isInternalPage;
        NormalPanePreviewButton.IsEnabled = !isInternalPage;
        if (isInternalPage)
        {
            CancelPreviewLoad();
        }
        else
        {
            RefreshPreviewForActiveSelection();
        }
    }

    private void CloseInternalPage(MainTabItem tab)
    {
        var index = _mainTabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (tab.Content is SettingsView settingsView)
        {
            settingsView.SaveRequested -= SettingsView_SaveRequested;
            settingsView.CancelRequested -= SettingsView_CancelRequested;
            settingsView.OpenFolderRequested -= SettingsView_OpenFolderRequested;
        }

        var wasSelected = ReferenceEquals(TabsControl.SelectedItem, tab);
        _mainTabs.RemoveAt(index);
        tab.Dispose();
        if (wasSelected && _mainTabs.Count > 0)
        {
            TabsControl.SelectedItem = _mainTabs[Math.Min(index, _mainTabs.Count - 1)];
        }
    }
}
