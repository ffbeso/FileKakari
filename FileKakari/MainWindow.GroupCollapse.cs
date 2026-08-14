using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FileKakari;

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

    private static void OnTrackCollapseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Expander expander) return;

        if ((bool)e.NewValue)
        {
            expander.DataContextChanged += Expander_DataContextChanged;
            expander.Expanded += FileGroupExpander_Expanded;
            expander.Collapsed += FileGroupExpander_Collapsed;
            ApplyCollapseState(expander);
        }
        else
        {
            expander.DataContextChanged -= Expander_DataContextChanged;
            expander.Expanded -= FileGroupExpander_Expanded;
            expander.Collapsed -= FileGroupExpander_Collapsed;
        }
    }

    private static void Expander_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Expander expander)
        {
            ApplyCollapseState(expander);
        }
    }

    private static void ApplyCollapseState(Expander expander)
    {
        if (expander.DataContext is not CollectionViewGroup group) return;

        var pane = FindParentFolderPane(expander);
        var tabState = pane?.ActiveTabState;
        if (tabState == null) return;

        string groupKey = group.Name?.ToString() ?? "";
        if (string.IsNullOrEmpty(groupKey)) return;

        bool shouldBeCollapsed = tabState.CollapsedGroupKeys.Contains(groupKey);
        expander.IsExpanded = !shouldBeCollapsed;
    }

    private static void FileGroupExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        if (expander.DataContext is not CollectionViewGroup group) return;

        var pane = FindParentFolderPane(expander);
        var tabState = pane?.ActiveTabState;
        if (tabState == null) return;

        string groupKey = group.Name?.ToString() ?? "";
        if (!string.IsNullOrEmpty(groupKey))
        {
            tabState.CollapsedGroupKeys.Add(groupKey);
        }
    }

    private static void FileGroupExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        if (expander.DataContext is not CollectionViewGroup group) return;

        var pane = FindParentFolderPane(expander);
        var tabState = pane?.ActiveTabState;
        if (tabState == null) return;

        string groupKey = group.Name?.ToString() ?? "";
        if (!string.IsNullOrEmpty(groupKey))
        {
            tabState.CollapsedGroupKeys.Remove(groupKey);
        }
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
        var (pane, rootElement) = GetParentFolderPaneInfoFromElement(dep);
        if (pane?.ActiveTabState == null) return;

        pane.ActiveTabState.CollapsedGroupKeys.Clear();
        if (rootElement != null)
        {
            UpdateGroupExpandersInElement(rootElement);
        }
    }

    private void GroupHeader_CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject dep) return;
        var (pane, rootElement) = GetParentFolderPaneInfoFromElement(dep);
        if (pane?.ActiveTabState == null) return;

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

        if (rootElement != null)
        {
            UpdateGroupExpandersInElement(rootElement);
        }
    }

    private static (FolderPane? Pane, FrameworkElement? Element) GetParentFolderPaneInfoFromElement(DependencyObject element)
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

        var curr = target ?? element;
        while (curr != null)
        {
            if (curr is FrameworkElement fe && fe.DataContext is FolderPane pane)
            {
                return (pane, fe);
            }
            curr = VisualTreeHelper.GetParent(curr);
        }
        return (null, null);
    }

    private static void UpdateGroupExpandersInElement(FrameworkElement rootElement)
    {
        var expanders = FindVisualChildren<Expander>(rootElement);
        foreach (var expander in expanders)
        {
            ApplyCollapseState(expander);
        }
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
}
