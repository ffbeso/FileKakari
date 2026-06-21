using System.Diagnostics;
using System.Windows;

namespace FileKakari;

public partial class SettingsWindow : Window
{
    private readonly SettingsView _settingsView;
    private readonly LocalizationService _text;

    public SettingsWindow(AppSettings settings, LocalizationService text)
    {
        InitializeComponent();
        _text = text;
        Title = text.Get("SettingsTitle");
        _settingsView = new SettingsView(settings, text);
        _settingsView.SaveRequested += SettingsView_SaveRequested;
        _settingsView.CancelRequested += SettingsView_CancelRequested;
        _settingsView.OpenFolderRequested += SettingsView_OpenFolderRequested;
        SettingsViewHost.Children.Add(_settingsView);
    }

    public AppSettings Result => _settingsView.Result;

    private void SettingsView_SaveRequested(object? sender, EventArgs e)
    {
        DialogResult = true;
    }

    private void SettingsView_CancelRequested(object? sender, EventArgs e)
    {
        DialogResult = false;
    }

    private void SettingsView_OpenFolderRequested(object? sender, OpenFolderRequestedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                _text.Get(e.ErrorTitleKey),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
