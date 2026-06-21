using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace FileKakari;

public partial class LogViewerView : UserControl
{
    private const int MaxLines = 5000;
    private const int MaxBytes = 2 * 1024 * 1024;
    private readonly LocalizationService _text;
    private readonly string? _logPath;

    public LogViewerView(LocalizationService text)
    {
        InitializeComponent();
        _text = text;
        _logPath = ResolveLogPath();
        ApplyLocalizedText();
        Loaded += LogViewerView_Loaded;
    }

    private void ApplyLocalizedText()
    {
        ReloadButton.Content = _text.Get("LogViewerReload");
        OpenFolderButton.Content = _text.Get("LogViewerOpenFolder");
        LogPathText.Text = _logPath ?? _text.Get("LogViewerPathNotConfigured");
        OpenFolderButton.IsEnabled = GetExistingLogDirectory() is not null;
    }

    private async void LogViewerView_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LogViewerView_Loaded;
        await ReloadAsync();
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = GetExistingLogDirectory();
        if (directory is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                _text.Get("LogViewerOpenFolderFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task ReloadAsync()
    {
        ReloadButton.IsEnabled = false;
        StatusText.Text = _text.Get("LogViewerLoading");

        try
        {
            if (_logPath is null || !File.Exists(_logPath))
            {
                LogTextBox.Clear();
                StatusText.Text = _logPath is null
                    ? _text.Get("LogViewerPathNotConfigured")
                    : _text.Get("LogViewerFileNotFound");
                return;
            }

            var result = await Task.Run(() => ReadTail(_logPath));
            LogTextBox.Text = result.Text;
            LogTextBox.ScrollToEnd();
            StatusText.Text = _text.Format("LogViewerLoaded", result.LineCount);
        }
        catch (Exception ex)
        {
            LogTextBox.Clear();
            StatusText.Text = _text.Format("LogViewerLoadFailed", ex.Message);
        }
        finally
        {
            ReloadButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = GetExistingLogDirectory() is not null;
        }
    }

    private static LogTailResult ReadTail(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var startsMidFile = stream.Length > MaxBytes;
        if (startsMidFile)
        {
            stream.Seek(-MaxBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (startsMidFile)
        {
            _ = reader.ReadLine();
        }

        var lines = new Queue<string>(MaxLines);
        while (reader.ReadLine() is { } line)
        {
            if (lines.Count == MaxLines)
            {
                lines.Dequeue();
            }
            lines.Enqueue(line);
        }

        return new LogTailResult(string.Join(Environment.NewLine, lines), lines.Count);
    }

    private string? GetExistingLogDirectory()
    {
        var directory = _logPath is null ? null : Path.GetDirectoryName(_logPath);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    private static string? ResolveLogPath()
    {
        if (PerfLog.ConfiguredLogPath is { } configuredPath)
        {
            return configuredPath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "filekakari-perf.log"),
            Path.Combine(AppContext.BaseDirectory, "filekakari-perf.log")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record LogTailResult(string Text, int LineCount);
}
