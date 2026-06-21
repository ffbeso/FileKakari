using System.Windows;

namespace FileKakari;

public partial class WorkspaceSaveDialog : Window
{
    public string WorkspaceName => NameBox.Text.Trim();

    public WorkspaceSaveDialog(string defaultName)
    {
        InitializeComponent();

        Title = AppStrings.Get("WorkspaceSaveTitle");
        NameLabel.Text = AppStrings.Get("WorkspaceName");
        SaveButton.Content = AppStrings.Get("Save");
        CancelButton.Content = AppStrings.Get("Cancel");

        NameBox.Text = defaultName;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceName))
        {
            MessageBox.Show(
                AppStrings.Get("RenameFailedEmpty"),
                AppStrings.Get("RenameFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
