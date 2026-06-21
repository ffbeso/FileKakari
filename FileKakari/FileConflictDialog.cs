using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FileKakari;

public enum FileConflictChoice
{
    Overwrite,
    Skip,
    Rename,
    OverwriteAll,
    SkipAll
}

public enum FileConflictPolicy
{
    Ask,
    OverwriteAll,
    SkipAll
}

public sealed record FileConflictResolution(FileConflictChoice Choice, string TargetPath);

public sealed record FileReplacementBackup(string BackupPath, bool IsDirectory);

public sealed record FileTransferItem(string SourcePath, string Name, bool IsDirectory);

public sealed class FileConflictDialog : Window
{
    private FileConflictChoice _choice = FileConflictChoice.Skip;

    private FileConflictDialog(LocalizationService text, string sourcePath, string targetPath)
    {
        Title = text.Get("FileConflictTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel
        {
            Margin = new Thickness(16)
        };

        panel.Children.Add(new TextBlock
        {
            Text = text.Get("FileConflictMessage"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        panel.Children.Add(CreatePathText(text.Format("FileConflictSource", Path.GetFileName(sourcePath), sourcePath)));
        panel.Children.Add(CreatePathText(text.Format("FileConflictTarget", Path.GetFileName(targetPath), targetPath)));

        var buttons = new UniformGrid
        {
            Columns = 5,
            Margin = new Thickness(0, 16, 0, 0)
        };

        AddChoiceButton(buttons, text.Get("FileConflictOverwrite"), FileConflictChoice.Overwrite);
        AddChoiceButton(buttons, text.Get("FileConflictSkip"), FileConflictChoice.Skip);
        AddChoiceButton(buttons, text.Get("FileConflictRename"), FileConflictChoice.Rename);
        AddChoiceButton(buttons, text.Get("FileConflictOverwriteAll"), FileConflictChoice.OverwriteAll);
        AddChoiceButton(buttons, text.Get("FileConflictSkipAll"), FileConflictChoice.SkipAll);
        panel.Children.Add(buttons);

        Content = panel;
    }

    public static FileConflictChoice Show(Window owner, LocalizationService text, string sourcePath, string targetPath)
    {
        var dialog = new FileConflictDialog(text, sourcePath, targetPath)
        {
            Owner = owner
        };

        dialog.ShowDialog();
        return dialog._choice;
    }

    private static TextBlock CreatePathText(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
    }

    private void AddChoiceButton(Panel panel, string label, FileConflictChoice choice)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(3, 0, 3, 0),
            MinHeight = 30
        };
        button.Click += (_, _) =>
        {
            _choice = choice;
            DialogResult = true;
        };
        panel.Children.Add(button);
    }
}
