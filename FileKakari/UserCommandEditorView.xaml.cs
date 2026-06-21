using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FileKakari;

public partial class UserCommandEditorView : UserControl
{
    private readonly LocalizationService _text;
    private readonly UserCommandService _userCommandService;
    private List<UserCommand> _editingCommands = new();
    private bool _isUpdatingFields = false;

    public UserCommandEditorView(LocalizationService text)
    {
        InitializeComponent();
        _text = text;
        _userCommandService = new UserCommandService();
        _userCommandService.Load();

        ApplyLocalizedText();
        ReloadUserCommands();
    }

    private void ApplyLocalizedText()
    {
        SaveButton.Content = _text.Get("SettingsSave");
        AddCommandButton.Content = _text.Get("BtnAdd");
        DuplicateCommandButton.Content = _text.Get("BtnDuplicate");
        DeleteCommandButton.Content = _text.Get("BtnDelete");
        ReloadCommandsButton.Content = _text.Get("Reload");

        CmdEnabledLabel.Text = _text.Get("CmdEnabled");
        CmdNameLabel.Text = _text.Get("CmdName");
        CmdTargetLabel.Text = _text.Get("CmdTarget");
        CmdExtensionsLabel.Text = _text.Get("CmdExtensions");
        CmdExecutableLabel.Text = _text.Get("CmdExecutable");
        CmdArgumentsLabel.Text = _text.Get("CmdArguments");
        CmdWorkingDirLabel.Text = _text.Get("CmdWorkingDirectory");
        CmdUseShellLabel.Text = _text.Get("CmdUseShellExecute");
        CmdAllowFilesLabel.Text = _text.Get("CmdAllowFiles");
        CmdAllowDirsLabel.Text = _text.Get("CmdAllowDirectories");
        CmdAllowMultipleLabel.Text = _text.Get("CmdAllowMultiple");

        CmdTargetComboBox.ItemsSource = new[]
        {
            new SettingsChoice<string>(_text.Get("TargetSelectionDesc") is var s && !string.IsNullOrEmpty(s) ? s : "Selection (選択中のアイテム)", "Selection"),
            new SettingsChoice<string>(_text.Get("TargetCurrentDirectoryDesc") is var d && !string.IsNullOrEmpty(d) ? d : "CurrentDirectory (現在のフォルダー)", "CurrentDirectory")
        };
        CmdTargetComboBox.DisplayMemberPath = "Text";
        CmdTargetComboBox.SelectedValuePath = "Value";
    }

    private void ReloadUserCommands()
    {
        _editingCommands = _userCommandService.Commands.Select(CloneUserCommand).ToList();
        RefreshCommandsList();
    }

    private void RefreshCommandsList()
    {
        var selectedIndex = CommandsListBox.SelectedIndex;

        CommandsListBox.ItemsSource = null;
        CommandsListBox.ItemsSource = _editingCommands;

        if (selectedIndex >= 0 && selectedIndex < _editingCommands.Count)
        {
            CommandsListBox.SelectedIndex = selectedIndex;
        }
        else if (_editingCommands.Count > 0)
        {
            CommandsListBox.SelectedIndex = 0;
        }
        else
        {
            CommandsListBox.SelectedIndex = -1;
            ClearCommandFields();
        }
    }

    private void CommandsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandsListBox.SelectedItem is UserCommand cmd)
        {
            CommandEditArea.IsEnabled = true;
            PopulateCommandFields(cmd);
        }
        else
        {
            CommandEditArea.IsEnabled = false;
            ClearCommandFields();
        }
    }

    private void PopulateCommandFields(UserCommand cmd)
    {
        _isUpdatingFields = true;
        try
        {
            CmdEnabledCheckBox.IsChecked = cmd.Enabled;
            CmdNameTextBox.Text = cmd.Name ?? "";

            var targetChoices = CmdTargetComboBox.ItemsSource as IEnumerable<SettingsChoice<string>>;
            bool hasMatch = targetChoices?.Any(c => string.Equals(c.Value, cmd.Target, StringComparison.OrdinalIgnoreCase)) == true;
            if (hasMatch)
            {
                CmdTargetComboBox.SelectedValue = cmd.Target;
            }
            else
            {
                if (!string.IsNullOrEmpty(cmd.Target))
                {
                    var list = targetChoices?.ToList() ?? new List<SettingsChoice<string>>();
                    var customChoice = new SettingsChoice<string>(cmd.Target, cmd.Target);
                    list.Add(customChoice);
                    CmdTargetComboBox.ItemsSource = list;
                    CmdTargetComboBox.SelectedValue = cmd.Target;
                }
                else
                {
                    CmdTargetComboBox.SelectedIndex = -1;
                }
            }

            CmdExtensionsTextBox.Text = string.Join(", ", cmd.Extensions);
            CmdExecutableTextBox.Text = cmd.Executable ?? "";
            CmdArgumentsTextBox.Text = cmd.Arguments ?? "";
            CmdWorkingDirTextBox.Text = cmd.WorkingDirectory ?? "";
            CmdUseShellCheckBox.IsChecked = cmd.UseShellExecute;
            CmdAllowFilesCheckBox.IsChecked = cmd.AllowFiles;
            CmdAllowDirsCheckBox.IsChecked = cmd.AllowDirectories;
            CmdAllowMultipleCheckBox.IsChecked = cmd.AllowMultiple;
        }
        finally
        {
            _isUpdatingFields = false;
        }
    }

    private void ClearCommandFields()
    {
        _isUpdatingFields = true;
        try
        {
            CmdEnabledCheckBox.IsChecked = false;
            CmdNameTextBox.Text = "";
            CmdTargetComboBox.SelectedIndex = -1;
            CmdExtensionsTextBox.Text = "";
            CmdExecutableTextBox.Text = "";
            CmdArgumentsTextBox.Text = "";
            CmdWorkingDirTextBox.Text = "";
            CmdUseShellCheckBox.IsChecked = false;
            CmdAllowFilesCheckBox.IsChecked = false;
            CmdAllowDirsCheckBox.IsChecked = false;
            CmdAllowMultipleCheckBox.IsChecked = false;
        }
        finally
        {
            _isUpdatingFields = false;
        }
    }

    private void CommandField_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFields) return;
        if (CommandsListBox.SelectedItem is not UserCommand cmd) return;

        cmd.Enabled = CmdEnabledCheckBox.IsChecked == true;
        cmd.Name = CmdNameTextBox.Text;
        cmd.Target = CmdTargetComboBox.SelectedValue as string ?? "Any";

        var extText = CmdExtensionsTextBox.Text ?? "";
        cmd.Extensions = extText.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim())
                                .Where(x => !string.IsNullOrEmpty(x))
                                .ToList();

        cmd.Executable = CmdExecutableTextBox.Text;
        cmd.Arguments = CmdArgumentsTextBox.Text;
        cmd.WorkingDirectory = CmdWorkingDirTextBox.Text;
        cmd.UseShellExecute = CmdUseShellCheckBox.IsChecked == true;
        cmd.AllowFiles = CmdAllowFilesCheckBox.IsChecked == true;
        cmd.AllowDirectories = CmdAllowDirsCheckBox.IsChecked == true;
        cmd.AllowMultiple = CmdAllowMultipleCheckBox.IsChecked == true;

        if (sender == CmdNameTextBox)
        {
            CommandsListBox.Items.Refresh();
        }
    }

    private void AddCommandButton_Click(object sender, RoutedEventArgs e)
    {
        var newCmd = new UserCommand
        {
            Name = "New Command",
            Executable = "",
            Arguments = "",
            WorkingDirectory = "",
            UseShellExecute = true,
            Target = "Selection",
            Enabled = true,
            Extensions = new(),
            AllowFiles = true,
            AllowDirectories = true,
            AllowMultiple = true
        };
        _editingCommands.Add(newCmd);
        RefreshCommandsList();
        CommandsListBox.SelectedItem = newCmd;
    }

    private void DuplicateCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandsListBox.SelectedItem is not UserCommand selected) return;
        var newCmd = CloneUserCommand(selected);
        newCmd.Name = (newCmd.Name ?? "") + " (Copy)";

        var index = _editingCommands.IndexOf(selected);
        _editingCommands.Insert(index + 1, newCmd);
        RefreshCommandsList();
        CommandsListBox.SelectedItem = newCmd;
    }

    private void DeleteCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandsListBox.SelectedItem is not UserCommand selected) return;
        var index = _editingCommands.IndexOf(selected);
        _editingCommands.Remove(selected);

        RefreshCommandsList();

        if (_editingCommands.Count > 0)
        {
            var nextIndex = Math.Min(index, _editingCommands.Count - 1);
            CommandsListBox.SelectedIndex = nextIndex;
        }
    }

    private void MoveUpCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandsListBox.SelectedItem is not UserCommand selected) return;
        var index = _editingCommands.IndexOf(selected);
        if (index <= 0) return;

        _editingCommands.RemoveAt(index);
        _editingCommands.Insert(index - 1, selected);

        RefreshCommandsList();
        CommandsListBox.SelectedItem = selected;
    }

    private void MoveDownCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandsListBox.SelectedItem is not UserCommand selected) return;
        var index = _editingCommands.IndexOf(selected);
        if (index < 0 || index >= _editingCommands.Count - 1) return;

        _editingCommands.RemoveAt(index);
        _editingCommands.Insert(index + 1, selected);

        RefreshCommandsList();
        CommandsListBox.SelectedItem = selected;
    }

    private void ReloadCommandsButton_Click(object sender, RoutedEventArgs e)
    {
        _userCommandService.Load();
        ReloadUserCommands();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _userCommandService.Save(_editingCommands);

            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.StatusText.Text = _text.Get("UserCommandsSavedStatus") is var msg && !string.IsNullOrEmpty(msg) ? msg : "ユーザーコマンドを保存しました。";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                _text.Format("UserCommandSaveFailed", ex.Message),
                _text.Get("UserCommandErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static UserCommand CloneUserCommand(UserCommand source)
    {
        return new UserCommand
        {
            Name = source.Name,
            Executable = source.Executable,
            Arguments = source.Arguments,
            WorkingDirectory = source.WorkingDirectory,
            UseShellExecute = source.UseShellExecute,
            Target = source.Target,
            Enabled = source.Enabled,
            Extensions = new List<string>(source.Extensions),
            AllowFiles = source.AllowFiles,
            AllowDirectories = source.AllowDirectories,
            AllowMultiple = source.AllowMultiple
        };
    }

    private sealed record SettingsChoice<T>(string Text, T Value);
}
