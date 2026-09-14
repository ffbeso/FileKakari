using System.IO;
using System.Windows;

namespace FileKakari;

public partial class MainWindow
{
    private readonly NamedSessionStore _namedSessionStore = new();

    private void SaveNamedSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NamedSessionSaveDialog
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path;
        NamedSessionInfo? existingSession;
        try
        {
            existingSession = _namedSessionStore.FindByName(dialog.SessionName);
            path = existingSession?.Path ?? _namedSessionStore.GetPath(dialog.SessionName);
        }
        catch (Exception ex)
        {
            ShowNamedSessionError("NamedSessionSaveFailed", ex.Message);
            return;
        }

        if (existingSession is not null && File.Exists(path))
        {
            var overwrite = MessageBox.Show(
                this,
                _text.Format("NamedSessionOverwriteConfirm", dialog.SessionName),
                _text.Get("NamedSessionSaveTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (overwrite != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var state = CaptureCurrentSessionState();
        if (!_sessionStateService.Save(path, state))
        {
            ShowNamedSessionError("NamedSessionSaveFailed", path);
            return;
        }

        StatusText.Text = _text.Format("NamedSessionSavedStatus", dialog.SessionName);
    }

    private async void OpenNamedSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<NamedSessionInfo> sessions;
        try
        {
            sessions = _namedSessionStore.GetSessions();
        }
        catch (Exception ex)
        {
            ShowNamedSessionError("NamedSessionListFailed", ex.Message);
            return;
        }

        var dialog = new NamedSessionOpenDialog(sessions)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedSession is not { } selectedSession)
        {
            return;
        }

        var loadResult = _sessionStateService.Load(selectedSession.Path);
        if (!loadResult.IsSuccess || loadResult.State is null)
        {
            var detail = loadResult.ErrorMessage ?? GetNamedSessionLoadStatusText(loadResult.Status);
            ShowNamedSessionError("NamedSessionLoadFailed", detail);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            _text.Format("NamedSessionOpenConfirm", selectedSession.Name),
            _text.Get("NamedSessionOpenTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await ApplySessionStateAsync(loadResult.State))
        {
            ShowNamedSessionError("NamedSessionApplyFailed", selectedSession.Name);
            return;
        }

        SaveSessionState();
        StatusText.Text = _text.Format("NamedSessionOpenedStatus", selectedSession.Name);
    }

    private string GetNamedSessionLoadStatusText(SessionStateLoadStatus status)
    {
        return status switch
        {
            SessionStateLoadStatus.FileNotFound => _text.Get("NamedSessionLoadFileNotFound"),
            SessionStateLoadStatus.InvalidJson => _text.Get("NamedSessionLoadInvalidJson"),
            SessionStateLoadStatus.IoError => _text.Get("NamedSessionLoadIoError"),
            _ => _text.Get("NamedSessionLoadUnknown")
        };
    }

    private void ShowNamedSessionError(string messageKey, string detail)
    {
        MessageBox.Show(
            this,
            _text.Format(messageKey, detail),
            _text.Get("NamedSessionErrorTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
