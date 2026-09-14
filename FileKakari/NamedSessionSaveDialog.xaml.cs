using System.Windows;

namespace FileKakari;

public partial class NamedSessionSaveDialog : Window
{
    public string SessionName { get; private set; } = "";

    public NamedSessionSaveDialog(string defaultName = "")
    {
        InitializeComponent();
        Title = AppStrings.Get("NamedSessionSaveTitle");
        NameLabel.Text = AppStrings.Get("NamedSessionName");
        SaveButton.Content = AppStrings.Get("Save");
        CancelButton.Content = AppStrings.Get("Cancel");
        NameBox.Text = defaultName;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var validation = NamedSessionStore.ValidateName(NameBox.Text);
        if (!validation.IsValid)
        {
            var messageKey = validation.Error switch
            {
                NamedSessionNameError.Empty => "NamedSessionNameEmpty",
                NamedSessionNameError.ReservedName => "NamedSessionNameReserved",
                NamedSessionNameError.TooLong => "NamedSessionNameTooLong",
                _ => "NamedSessionNameInvalid"
            };
            MessageBox.Show(
                this,
                AppStrings.Get(messageKey),
                AppStrings.Get("NamedSessionSaveTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SessionName = validation.Name;
        DialogResult = true;
    }
}
