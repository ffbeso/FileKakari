using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileKakari;

public partial class NamedSessionOpenDialog : Window
{
    public NamedSessionInfo? SelectedSession => SessionsList.SelectedItem as NamedSessionInfo;

    public NamedSessionOpenDialog(IReadOnlyList<NamedSessionInfo> sessions)
    {
        InitializeComponent();
        Title = AppStrings.Get("NamedSessionOpenTitle");
        InstructionText.Text = AppStrings.Get("NamedSessionOpenInstruction");
        EmptyText.Text = sessions.Count == 0 ? AppStrings.Get("NamedSessionEmpty") : "";
        OpenButton.Content = AppStrings.Get("ContextOpen");
        CancelButton.Content = AppStrings.Get("Cancel");
        SessionsList.ItemsSource = sessions;
        if (sessions.Count > 0)
        {
            SessionsList.SelectedIndex = 0;
            SessionsList.Focus();
        }
    }

    private void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenButton.IsEnabled = SelectedSession is not null;
    }

    private void SessionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CompleteSelection();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        CompleteSelection();
    }

    private void CompleteSelection()
    {
        if (SelectedSession is not null)
        {
            DialogResult = true;
        }
    }
}
