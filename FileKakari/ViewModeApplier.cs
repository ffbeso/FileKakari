using System.Windows;
using System.Windows.Controls;

namespace FileKakari;

internal sealed class ViewModeApplier
{
    private readonly ListView _itemsList;
    private readonly GridView _detailsGridView;
    private readonly FrameworkElement _resourceOwner;
    private readonly Func<bool> _isDiagnosticRowStyleEnabled;

    public ViewModeApplier(
        ListView itemsList,
        GridView detailsGridView,
        FrameworkElement resourceOwner,
        Func<bool> isDiagnosticRowStyleEnabled)
    {
        _itemsList = itemsList;
        _detailsGridView = detailsGridView;
        _resourceOwner = resourceOwner;
        _isDiagnosticRowStyleEnabled = isDiagnosticRowStyleEnabled;
    }

    public void Apply(FileDisplayMode mode, double rowHeight)
    {
        ApplyTo(_itemsList, mode, rowHeight);
    }

    public void ApplyTo(ListView listView, FileDisplayMode mode, double rowHeight)
    {
        GridView? detailsGridView = null;
        if (ReferenceEquals(listView, _itemsList))
        {
            detailsGridView = _detailsGridView;
        }
        else
        {
            if (listView.Tag is GridView cached)
            {
                detailsGridView = cached;
            }
            else
            {
                if (listView.View is GridView currentGv)
                {
                    detailsGridView = currentGv;
                    if (listView.Tag is null)
                    {
                        listView.Tag = currentGv;
                    }
                }
            }
        }

        listView.ItemTemplate = null;

        switch (mode)
        {
            case FileDisplayMode.Compact:
                if (detailsGridView is not null)
                {
                    listView.View = detailsGridView;
                }
                ApplyContainerStyle(listView, GetGridListViewItemBaseStyle(), Math.Max(AppSettings.MinRowHeight, Math.Min(rowHeight, 20)));
                break;
            case FileDisplayMode.List:
                listView.View = null;
                listView.ItemTemplate = (DataTemplate)_resourceOwner.FindResource("ListDisplayTemplate");
                ApplyContainerStyle(listView, _resourceOwner.TryFindResource("ContentListViewItemStyle") as Style, 24);
                break;
            default:
                if (detailsGridView is not null)
                {
                    listView.View = detailsGridView;
                }
                ApplyContainerStyle(listView, GetGridListViewItemBaseStyle(), rowHeight);
                break;
        }
    }

    private Style? GetGridListViewItemBaseStyle()
    {
        return _isDiagnosticRowStyleEnabled()
            ? _resourceOwner.TryFindResource("DiagnosticListViewItemStyle") as Style
            : _resourceOwner.TryFindResource(typeof(ListViewItem)) as Style;
    }

    private void ApplyContainerStyle(ListView listView, Style? baseStyle, double rowHeight)
    {
        var itemStyle = baseStyle is null
            ? new Style(typeof(ListViewItem))
            : new Style(typeof(ListViewItem), baseStyle);
        itemStyle.Setters.Add(new Setter(Control.FontFamilyProperty, listView.FontFamily));
        itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, listView.FontSize));
        itemStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, rowHeight));
        listView.ItemContainerStyle = itemStyle;
    }
}
