using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileKakari;

public enum NavigationDirection
{
    Back,
    Forward
}

public partial class MainWindow
{
    private async Task NavigateToFolderAsync(string path, NavigationKind navigationKind)
    {
        await _navigationController.NavigateToFolderAsync(path, navigationKind);
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateHistoryAsync(NavigationDirection.Back);
    }

    private async Task NavigateBackAsync()
    {
        await NavigateHistoryAsync(NavigationDirection.Back);
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateHistoryAsync(NavigationDirection.Forward);
    }

    private async Task NavigateForwardAsync()
    {
        await NavigateHistoryAsync(NavigationDirection.Forward);
    }

    private async Task NavigateHistoryAsync(
        NavigationDirection direction,
        FolderPane? explicitPane = null)
    {
        FolderPane? targetPane = null;
        if (explicitPane is not null && IsValidFolderPane(explicitPane))
        {
            targetPane = explicitPane;
        }
        else
        {
            targetPane = GetActiveFolderPane();
        }

        WriteDiagLog($"navigate-history-entry targetPaneId={targetPane?.Id ?? "null"}");

        if (!IsValidFolderPane(targetPane))
        {
            WriteDiagLog($"navigate-history-failed reason=invalid-pane targetPaneId={targetPane?.Id ?? "null"}");
            return;
        }

        var context = GetActiveNavigationContext(targetPane);
        var pane = context.Pane;
        var tab = context.Tab;
        if (pane is null || tab is null)
        {
            WriteDiagLog($"navigate-history-failed reason=null-pane-or-tab paneId={pane?.Id ?? "null"} tab={tab is not null}");
            return;
        }

        var canNavigate = direction == NavigationDirection.Back ? tab.Navigation.CanGoBack : tab.Navigation.CanGoForward;
        WriteDiagLog($"navigate-history direction={direction} can-navigate={canNavigate} active-pane-id={pane.Id} active-path={tab.Navigation.CurrentPath}");

        var isPaneLoading = ReferenceEquals(pane, GetNormalFolderPane()) ? _isLoading : pane.IsLoading;
        if (isPaneLoading)
        {
            _performanceLogger.Write($"history-navigation-blocked reason=pane-loading paneId={pane.Id}");
            WriteDiagLog($"navigate-history-blocked reason=pane-loading paneId={pane.Id}");
            return;
        }

        await _navigationController.NavigateHistoryAsync(direction, pane);
    }

    private bool IsValidFolderPane(FolderPane? pane)
    {
        if (pane is null)
        {
            return false;
        }

        if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            return _workspaceDisplayPanes.Contains(pane);
        }
        else
        {
            return ReferenceEquals(pane, GetNormalFolderPane());
        }
    }

    private static bool IsItemDirectChildOfFolder(string itemPath, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath) || string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        var itemNormalized = NavigationState.NormalizePath(itemPath);
        var folderNormalized = NavigationState.NormalizePath(folderPath);

        if (itemNormalized is null || folderNormalized is null)
        {
            return false;
        }

        if (SpecialLocationService.IsSpecialUri(folderNormalized))
        {
            if (SpecialLocationService.IsSpecialUri(itemNormalized))
            {
                return false;
            }
            try
            {
                return NavigationState.IsFileSystemRoot(itemNormalized);
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var parent = Directory.GetParent(itemNormalized);
            if (parent is null)
            {
                return false;
            }

            var parentNormalized = NavigationState.NormalizePath(parent.FullName);
            return string.Equals(parentNormalized, folderNormalized, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }


    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        await _navigationController.HandleUpButtonClickAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentFolderAsync();
    }

    private void NormalPanePlacesButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement placementTarget)
        {
            ShowPlacesMenu(placementTarget, path => NavigateToFolderAsync(path, NavigationKind.New));
        }
    }

    private void ShowPlacesMenu(FrameworkElement placementTarget, Func<string, Task> navigateAsync)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget
        };

        foreach (var location in _specialLocationService.GetLocations())
        {
            if (!location.IsAvailable)
            {
                continue;
            }

            var item = new MenuItem
            {
                Header = location.DisplayName,
                Tag = location.Path
            };
            item.Click += async (_, _) =>
            {
                if (item.Tag is string path)
                {
                    await navigateAsync(path);
                }
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private async void WorkspacePaneBackButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await NavigateHistoryAsync(NavigationDirection.Back, pane);
        }
    }

    private async void WorkspacePaneForwardButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await NavigateHistoryAsync(NavigationDirection.Forward, pane);
        }
    }

    private async void WorkspacePaneUpButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (GetWorkspacePaneFromSender(sender) is not { } pane)
        {
            return;
        }

        await _navigationController.OpenWorkspacePaneParentAsync(pane);
    }

    private async void WorkspacePaneRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        var pane = GetWorkspacePaneFromSender(sender);
        var context = GetActiveNavigationContext(pane);
        if (context.Pane is not null && context.Tab is { } tab)
        {
            await ReloadFolderPanesShowingPathAsync(tab.Navigation.CurrentPath, context.Pane);
        }
    }

    private void WorkspacePanePlacesButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (sender is not FrameworkElement placementTarget
            || GetWorkspacePaneFromSender(sender) is not { } pane)
        {
            return;
        }

        ShowPlacesMenu(placementTarget, path => NavigateWorkspacePaneToFolderAsync(pane, path, NavigationKind.New));
    }

    private async void WorkspacePanePathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox
            || GetWorkspacePaneFromSender(sender) is not { } pane)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            textBox.Text = pane.CurrentPath;
            textBox.Visibility = Visibility.Collapsed;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await NavigateWorkspacePaneToFolderAsync(pane, textBox.Text, NavigationKind.New);
        textBox.Visibility = Visibility.Collapsed;
    }

    private ActiveNavigationContext GetActiveNavigationContext(FolderPane? targetPane = null)
    {
        if (GetSelectedInternalPage() is not null)
        {
            return new ActiveNavigationContext();
        }

        var session = ActiveSession;
        if (session is null)
        {
            return new ActiveNavigationContext();
        }

        var isWorkspace = WorkspaceSplitGrid.Visibility == Visibility.Visible;
        var pane = targetPane ?? GetActiveFolderPane();
        FolderTab? tab = null;
        string? path = null;
        ListView? listView = null;

        if (pane is not null)
        {
            tab = pane.ActiveTab;
            if (tab is not null)
            {
                path = tab.State?.CurrentPath ?? tab.Navigation?.CurrentPath;
            }
            listView = GetFolderPaneListView(pane);
        }

        if (path is not null && (SpecialLocationService.IsSpecialUri(path) || string.IsNullOrWhiteSpace(path)))
        {
            path = null;
        }

        return new ActiveNavigationContext
        {
            IsWorkspace = isWorkspace,
            Pane = pane,
            Tab = tab,
            Path = path,
            ListView = listView
        };
    }

    private async Task NavigateWorkspacePaneToFolderAsync(FolderPane pane, string path, NavigationKind navigationKind)
    {
        await _navigationController.NavigateWorkspacePaneToFolderAsync(pane, path, navigationKind);
        UpdateWindowTitle();
    }

    private async Task RefreshCurrentFolderAsync()
    {
        var context = GetActiveNavigationContext();
        if (context.Pane is { } pane && context.Tab is { } tab)
        {
            await ReloadFolderPanesShowingPathAsync(tab.Navigation.CurrentPath, pane);
            return;
        }

        await _navigationController.RefreshCurrentFolderAsync();
    }

    private void UpdatePathDisplay(string path)
    {
        _breadcrumbPathBar.Update(path);
        FolderPane.ReplaceBreadcrumbSegments(_normalPaneBreadcrumbSegments, path);
        if (!NormalPanePathBox.IsKeyboardFocusWithin
            && !string.Equals(NormalPanePathBox.Text, path, StringComparison.Ordinal))
        {
            NormalPanePathBox.Text = path;
        }

        NormalPaneTitleText.Text = GetPathDisplayName(path);
    }

    private async Task NavigateFromBreadcrumbAsync(string targetPath)
    {
        await _navigationController.NavigateFromBreadcrumbAsync(targetPath);
    }

    private async void NormalPaneBreadcrumbButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string targetPath })
        {
            await NavigateFromBreadcrumbAsync(targetPath);
        }
    }

    private async void WorkspacePaneBreadcrumbButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (sender is Button { Tag: string targetPath }
            && GetWorkspacePaneFromSender(sender) is { } pane)
        {
            await NavigateWorkspacePaneToFolderAsync(pane, targetPath, NavigationKind.New);
        }
    }

    private void PathBarHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_breadcrumbPathBar.IsEditing
            || FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null
            || IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        _breadcrumbPathBar.BeginEdit();
    }

    private void NormalPanePathBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null
            || IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        if (ActiveNavigation is { } navigation)
        {
            ShowPanePathTextBox(NormalPanePathBox, navigation.CurrentPath);
        }
    }

    private void WorkspacePanePathBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null
            || IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (FindVisualChild<TextBox>((DependencyObject)sender) is { } textBox
            && GetWorkspacePaneFromSender(sender) is { } pane)
        {
            e.Handled = true;
            ShowPanePathTextBox(textBox, pane.CurrentPath);
        }
    }

    private void WorkspacePaneTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = ActivateWorkspacePaneFromSenderAsync(sender);
    }

    private void BeginPanePathEdit()
    {
        if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
        {
            FocusWorkspacePaneTextBox("PanePathBox");
            return;
        }

        if (ActiveNavigation is { } navigation)
        {
            ShowPanePathTextBox(NormalPanePathBox, navigation.CurrentPath);
        }
    }

    private void FocusWorkspacePaneTextBox(string tag)
    {
        var candidates = FindVisualChildren<TextBox>(WorkspaceSplitGrid)
            .Where(textBox => string.Equals(textBox.Tag as string, tag, StringComparison.Ordinal))
            .ToList();
        var target = candidates.FirstOrDefault(textBox => ReferenceEquals(textBox.DataContext, _activeWorkspacePaneGroup))
            ?? candidates.FirstOrDefault();
        if (target is not null)
        {
            if (string.Equals(tag, "PanePathBox", StringComparison.Ordinal)
                && GetWorkspacePaneFromSender(target) is { } pane)
            {
                ShowPanePathTextBox(target, pane.CurrentPath);
                return;
            }

            FocusAndSelectTextBox(target);
        }
    }

    private static void ShowPanePathTextBox(TextBox textBox, string path)
    {
        textBox.Visibility = Visibility.Visible;
        textBox.Text = path;
        FocusAndSelectTextBox(textBox);
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        await _navigationController.HandlePathBoxKeyDownAsync(e);
    }

    private async void NormalPanePathBox_KeyDown(object sender, KeyEventArgs e)
    {
        await _navigationController.HandleNormalPanePathBoxKeyDownAsync(e);
    }

    private void NormalPanePathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        NormalPanePathBox.Text = ActiveNavigation?.CurrentPath ?? "";
        NormalPanePathBox.Visibility = Visibility.Collapsed;
    }

    private void WorkspacePanePathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Visibility = Visibility.Collapsed;
        }
    }

    private void PathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_breadcrumbPathBar.IsEditing)
        {
            _breadcrumbPathBar.CancelEdit();
        }
    }

    private async Task NavigateFromPathBoxAsync(string normalizedPath)
    {
        await _navigationController.NavigateFromPathBoxAsync(normalizedPath);
    }

    private bool CanStartUserPathNavigation(string operation)
    {
        return _navigationController.CanStartUserPathNavigation(operation);
    }

    private void UpdateNavigationButtons()
    {
        _navigationController.UpdateNavigationButtons();
    }

    private static string GetPathDisplayName(string path)
    {
        if (SpecialLocationService.IsSpecialUri(path))
        {
            return AppStrings.Get("LocationThisPc");
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
