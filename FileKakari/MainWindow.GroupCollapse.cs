using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FileKakari;

public partial class MainWindow
{
    private void FileGroupExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        if (expander.DataContext is not CollectionViewGroup group) return;

        var pane = FindParentFolderPane(expander);
        var tabState = pane?.ActiveTabState ?? ActiveTabState;
        if (tabState == null) return;

        string groupKey = group.Name?.ToString() ?? "";
        if (string.IsNullOrEmpty(groupKey)) return;

        bool shouldBeCollapsed = tabState.CollapsedGroupKeys.Contains(groupKey);
        if (expander.IsExpanded == shouldBeCollapsed)
        {
            expander.IsExpanded = !shouldBeCollapsed;
        }
    }

    private void FileGroupExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        if (expander.DataContext is not CollectionViewGroup group) return;

        var pane = FindParentFolderPane(expander);
        var tabState = pane?.ActiveTabState ?? ActiveTabState;
        if (tabState == null) return;

        string groupKey = group.Name?.ToString() ?? "";
        if (!string.IsNullOrEmpty(groupKey))
        {
            tabState.CollapsedGroupKeys.Add(groupKey);
        }
    }

    private void FileGroupExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        if (expander.DataContext is not CollectionViewGroup group) return;

        var pane = FindParentFolderPane(expander);
        var tabState = pane?.ActiveTabState ?? ActiveTabState;
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
}
