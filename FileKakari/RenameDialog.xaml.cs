using System.Windows;

namespace FileKakari;

public partial class RenameDialog : Window
{
    public string NewName => NameBox.Text.Trim();

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
