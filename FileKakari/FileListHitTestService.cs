using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FileKakari;

internal sealed class FileListHitTestService
{
    private const string FileNameHitTargetTag = "FileNameHitTarget";
    private const string FileRenameHitTargetTag = "FileRenameHitTarget";

    private readonly ListView _itemsList;
    private readonly Func<FileDisplayMode> _getDisplayMode;
    private readonly Func<double> _getVisibleColumnsWidth;

    public FileListHitTestService(
        ListView itemsList,
        Func<FileDisplayMode> getDisplayMode,
        Func<double> getVisibleColumnsWidth)
    {
        _itemsList = itemsList;
        _getDisplayMode = getDisplayMode;
        _getVisibleColumnsWidth = getVisibleColumnsWidth;
    }

    public bool IsInsideListViewItem(DependencyObject? source)
    {
        return FindVisualParent<ListViewItem>(source) is not null;
    }

    public FileEntry? GetFileEntryFromNameHitTarget(DependencyObject? source)
    {
        if (!IsInsideFileNameHitTarget(source))
        {
            return null;
        }

        return FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
    }

    public FileEntry? GetFileEntryFromItemHitTarget(DependencyObject? source)
    {
        return FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
    }

    public FileEntry? GetFileEntryFromDisplayedContentHitTarget(DependencyObject? source)
    {
        return GetFileEntryFromDisplayedContentHitTarget(source, _getDisplayMode());
    }

    public static FileEntry? GetFileEntryFromDisplayedContentHitTarget(
        DependencyObject? source,
        FileDisplayMode displayMode)
    {
        if (source is null
            || FindVisualParent<ScrollBar>(source) is not null
            || FindVisualParent<GridViewColumnHeader>(source) is not null)
        {
            return null;
        }

        if (displayMode == FileDisplayMode.List)
        {
            if (!IsInsideFileNameHitTarget(source))
            {
                return null;
            }

            return FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
        }

        if (IsInsideFileNameHitTarget(source)
            && FindVisualParent<ListViewItem>(source)?.DataContext is FileEntry nameEntry)
        {
            return nameEntry;
        }

        var current = source;
        while (current is not null)
        {
            if (current is ListViewItem)
            {
                return null;
            }

            if (current is FrameworkElement element
                && element.DataContext is FileEntry entry
                && IsDisplayedCellContentElement(element))
            {
                return entry;
            }

            current = GetVisualOrLogicalParent(current);
        }

        return null;
    }

    public FileEntry? GetFileEntryFromRenameHitTarget(DependencyObject? source)
    {
        if (!IsInsideFileRenameHitTarget(source))
        {
            return null;
        }

        return FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
    }

    public FileEntry? GetFileEntryFromDoubleClickHit(DependencyObject? source, Point itemsListPosition)
    {
        if (source is null
            || IsInsideScrollBar(source)
            || FindVisualParent<GridViewColumnHeader>(source) is not null
            || !IsPointInsideVisibleColumnRange(itemsListPosition))
        {
            return null;
        }

        return FindVisualParent<ListViewItem>(source)?.DataContext as FileEntry;
    }

    public bool IsFileListBackgroundHit(DependencyObject? source)
    {
        return source is not null
            && IsInsideItemsList(source)
            && !IsInsideScrollBar(source)
            && FindVisualParent<GridViewColumnHeader>(source) is null
            && GetFileEntryFromDisplayedContentHitTarget(source) is null;
    }

    public bool IsInsideRenameTextBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox { DataContext: FileEntry })
            {
                return true;
            }

            source = GetVisualOrLogicalParent(source);
        }

        return false;
    }

    public bool IsInsideScrollBar(DependencyObject? source)
    {
        return FindVisualParent<ScrollBar>(source) is not null;
    }

    public bool IsInsideItemsList(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, _itemsList))
            {
                return true;
            }

            source = GetVisualOrLogicalParent(source);
        }

        return false;
    }

    private bool IsPointInsideVisibleColumnRange(Point itemsListPosition)
    {
        return itemsListPosition.X >= 0
            && itemsListPosition.X <= _getVisibleColumnsWidth();
    }

    private static bool IsDisplayedCellContentElement(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            return false;
        }

        return element switch
        {
            TextBlock textBlock => !string.IsNullOrEmpty(textBlock.Text),
            Image => true,
            TextBox => true,
            _ => false
        };
    }

    public static bool IsInsideFileNameHitTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element
                && (Equals(element.Tag, FileNameHitTargetTag) || Equals(element.Tag, FileRenameHitTargetTag)))
            {
                return true;
            }

            if (source is ListViewItem)
            {
                return false;
            }

            source = GetVisualOrLogicalParent(source);
        }

        return false;
    }

    private static bool IsInsideFileRenameHitTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && Equals(element.Tag, FileRenameHitTargetTag))
            {
                return true;
            }

            if (source is ListViewItem)
            {
                return false;
            }

            source = GetVisualOrLogicalParent(source);
        }

        return false;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T typedSource)
            {
                return typedSource;
            }

            source = GetVisualOrLogicalParent(source);
        }

        return null;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject source)
    {
        try
        {
            return VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }
        catch (InvalidOperationException)
        {
            return LogicalTreeHelper.GetParent(source);
        }
    }
}
