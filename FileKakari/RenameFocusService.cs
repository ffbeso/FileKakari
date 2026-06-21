using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileKakari;

internal sealed class RenameFocusService
{
    private readonly ListView _itemsList;

    public RenameFocusService(ListView itemsList)
    {
        _itemsList = itemsList;
    }

    public async Task FocusRenameTextBoxAsync(FileEntry entry)
    {
        await FocusRenameTextBoxAsync(_itemsList, entry);
    }

    public async Task FocusRenameTextBoxAsync(ListView listView, FileEntry entry)
    {
        await listView.Dispatcher.InvokeAsync(() =>
        {
            listView.UpdateLayout();
            var item = listView.ItemContainerGenerator.ContainerFromItem(entry) as ListViewItem;
            if (item is null)
            {
                listView.ScrollIntoView(entry);
                listView.UpdateLayout();
                item = listView.ItemContainerGenerator.ContainerFromItem(entry) as ListViewItem;
            }

            if (item is null)
            {
                return;
            }

            var textBox = FindRenameTextBox(item, entry);
            if (textBox is null)
            {
                return;
            }

            FocusAndSelectRenameText(textBox, entry);
        }, DispatcherPriority.ContextIdle);
    }

    public void FocusAndSelectRenameText(TextBox textBox, FileEntry entry)
    {
        textBox.Focus();
        Keyboard.Focus(textBox);

        if (entry.IsDirectory)
        {
            textBox.SelectAll();
            return;
        }

        var extension = Path.GetExtension(entry.Name);
        var selectionLength = string.IsNullOrEmpty(extension)
            ? entry.Name.Length
            : entry.Name.Length - extension.Length;

        textBox.Select(0, Math.Max(0, selectionLength));
    }

    private static TextBox? FindRenameTextBox(DependencyObject parent, FileEntry entry)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox textBox && ReferenceEquals(textBox.DataContext, entry))
            {
                return textBox;
            }

            var result = FindRenameTextBox(child, entry);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
