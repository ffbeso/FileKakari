using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace FileKakari;

public class GroupCollapseConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values != null && values.Length >= 2 &&
            values[0] is string groupKey &&
            values[1] is WorkspaceTabState tabState)
        {
            return !tabState.CollapsedGroupKeys.Contains(groupKey);
        }
        return true;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public partial class MainWindow
{
    public static readonly DependencyProperty TrackCollapseProperty =
        DependencyProperty.RegisterAttached(
            "TrackCollapse",
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false, OnTrackCollapseChanged));

    public static bool GetTrackCollapse(DependencyObject obj) => (bool)obj.GetValue(TrackCollapseProperty);
    public static void SetTrackCollapse(DependencyObject obj, bool value) => obj.SetValue(TrackCollapseProperty, value);

    public static readonly DependencyProperty GroupCollapseTabStateProperty =
        DependencyProperty.RegisterAttached(
            "GroupCollapseTabState",
            typeof(WorkspaceTabState),
            typeof(MainWindow),
            new PropertyMetadata(null));

    public static WorkspaceTabState? GetGroupCollapseTabState(DependencyObject obj) => (WorkspaceTabState?)obj.GetValue(GroupCollapseTabStateProperty);
    public static void SetGroupCollapseTabState(DependencyObject obj, WorkspaceTabState? value) => obj.SetValue(GroupCollapseTabStateProperty, value);

    public static void SyncGroupCollapseTabState(ListView? listView, WorkspaceTabState? tabState)
    {
        if (listView == null) return;
        if (!ReferenceEquals(GetGroupCollapseTabState(listView), tabState))
        {
            SetGroupCollapseTabState(listView, tabState);
        }
    }

    public ListView? FindWorkspacePaneListView(FolderPane pane)
    {
        return FindVisualChildren<ListView>(WorkspaceSplitGrid)
            .FirstOrDefault(lv => ReferenceEquals(lv.DataContext, pane));
    }

    public static void SyncPaneGroupCollapseState(FolderPane? pane)
    {
        if (pane?.ActiveTabState == null) return;

        if (Application.Current?.MainWindow is MainWindow window)
        {
            if (ReferenceEquals(pane, window.GetNormalFolderPane()))
            {
                SyncGroupCollapseTabState(window.ItemsList, pane.ActiveTabState);
            }
            else
            {
                var listView = window.FindWorkspacePaneListView(pane);
                if (listView != null)
                {
                    SyncGroupCollapseTabState(listView, pane.ActiveTabState);
                }
            }
        }
    }

    private static void OnTrackCollapseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Expander expander) return;

        if ((bool)e.NewValue)
        {
            expander.PreviewMouseLeftButtonDown += Expander_PreviewMouseLeftButtonDown;
            expander.PreviewKeyDown += Expander_PreviewKeyDown;
        }
        else
        {
            expander.PreviewMouseLeftButtonDown -= Expander_PreviewMouseLeftButtonDown;
            expander.PreviewKeyDown -= Expander_PreviewKeyDown;
        }
    }

    private static void Expander_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Expander expander) return;
        var source = e.OriginalSource as DependencyObject;
        if (!IsInsideGroupHeader(source)) return;

        bool isArrowClick = IsInsideGroupArrow(source);
        if (isArrowClick || e.ClickCount == 2)
        {
            ToggleGroupStateByUser(expander);
            e.Handled = true;
        }
    }

    private static void Expander_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Expander expander) return;
        if (e.Key == Key.Space || e.Key == Key.Enter)
        {
            ToggleGroupStateByUser(expander);
            e.Handled = true;
        }
    }

    private static void ToggleGroupStateByUser(Expander expander)
    {
        if (expander.DataContext is not CollectionViewGroup group) return;

        var listView = FindParentListView(expander);
        var tabState = listView != null ? GetGroupCollapseTabState(listView) : FindParentFolderPane(expander)?.ActiveTabState;
        if (tabState == null) return;

        string groupKey = group.Name?.ToString() ?? "";
        if (string.IsNullOrEmpty(groupKey)) return;

        if (tabState.CollapsedGroupKeys.Contains(groupKey))
        {
            tabState.CollapsedGroupKeys.Remove(groupKey);
        }
        else
        {
            tabState.CollapsedGroupKeys.Add(groupKey);
        }

        tabState.NotifyGroupCollapseChanged();
    }

    private static ListView? FindParentListView(DependencyObject child)
    {
        var curr = child;
        while (curr != null)
        {
            if (curr is ListView listView)
            {
                return listView;
            }
            curr = VisualTreeHelper.GetParent(curr);
        }
        return null;
    }

    private static FolderPane? FindParentFolderPane(DependencyObject child)
    {
        var curr = child;
        while (curr != null)
        {
            if (curr is FrameworkElement fe && fe.DataContext is FolderPane pane)
            {
                return pane;
            }
            curr = VisualTreeHelper.GetParent(curr);
        }
        return null;
    }

    private void GroupHeader_ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject dep) return;
        var pane = GetParentFolderPaneFromElement(dep);
        if (pane?.ActiveTabState == null) return;

        if (GetParentListViewFromElement(dep) is { } listView)
        {
            SyncGroupCollapseTabState(listView, pane.ActiveTabState);
        }

        pane.ActiveTabState.CollapsedGroupKeys.Clear();
        pane.ActiveTabState.NotifyGroupCollapseChanged();
    }

    private void GroupHeader_CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject dep) return;
        var pane = GetParentFolderPaneFromElement(dep);
        if (pane?.ActiveTabState == null) return;

        if (GetParentListViewFromElement(dep) is { } listView)
        {
            SyncGroupCollapseTabState(listView, pane.ActiveTabState);
        }

        var tabState = pane.ActiveTabState;
        var groups = pane.ItemsView?.Groups;

        if (groups != null)
        {
            foreach (var g in groups)
            {
                if (g is CollectionViewGroup cvg)
                {
                    string key = cvg.Name?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(key))
                    {
                        tabState.CollapsedGroupKeys.Add(key);
                    }
                }
            }
        }

        tabState.NotifyGroupCollapseChanged();
    }

    private static ListView? GetParentListViewFromElement(DependencyObject element)
    {
        DependencyObject? target = element;
        if (element is MenuItem menuItem)
        {
            var cm = menuItem.Parent as ContextMenu
                     ?? ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;
            if (cm?.PlacementTarget != null)
            {
                target = cm.PlacementTarget;
            }
        }

        return FindParentListView(target ?? element);
    }

    private static FolderPane? GetParentFolderPaneFromElement(DependencyObject element)
    {
        DependencyObject? target = element;
        if (element is MenuItem menuItem)
        {
            var cm = menuItem.Parent as ContextMenu
                     ?? ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;
            if (cm?.PlacementTarget != null)
            {
                target = cm.PlacementTarget;
            }
        }

        return FindParentFolderPane(target ?? element);
    }

    public static bool IsInsideGroupHeader(DependencyObject? source)
    {
        var curr = source;
        while (curr != null && curr is not ListView)
        {
            if (curr is ItemsPresenter)
            {
                return false;
            }

            if (curr is FrameworkElement fe && Equals(fe.Tag, "GroupHeader"))
            {
                return true;
            }

            curr = VisualTreeHelper.GetParent(curr);
        }
        return false;
    }

    public static bool IsInsideGroupArrow(DependencyObject? source)
    {
        var curr = source;
        while (curr != null && curr is not ListView)
        {
            if (curr is ItemsPresenter)
            {
                return false;
            }

            if (curr is FrameworkElement fe && Equals(fe.Tag, "GroupHeaderArrow"))
            {
                return true;
            }

            curr = VisualTreeHelper.GetParent(curr);
        }
        return false;
    }
}
