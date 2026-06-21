using System.Windows;

namespace FileKakari;

public partial class MainWindow
{
    private void ExecuteUserCommand(UserCommand command, string currentDir, IReadOnlyList<FileEntry> selectedEntries)
    {
        try
        {
            var context = new UserCommandExecutionContext
            {
                CurrentDirectory = currentDir,
                SelectedEntries = selectedEntries
            };
            var startInfo = _userCommandExecutionService.CreateStartInfo(command, context);

            FolderPane? pane = null;
            if (WorkspaceSplitGrid.Visibility == Visibility.Visible)
            {
                pane = _workspaceDisplayPanes.FirstOrDefault(p =>
                    string.Equals(NormalizePathForComparison(p.ActiveTab?.Navigation.CurrentPath), NormalizePathForComparison(currentDir), StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                pane = _primaryPaneGroup;
            }

            if (pane is not null)
            {
                if (IsWorkspaceDisplayPane(pane))
                {
                    if (pane.ActiveTab is { } tab)
                    {
                        SaveWorkspacePaneNavigationViewState(pane, tab);
                    }
                }
                else
                {
                    if (ActiveTab is { } tab)
                    {
                        SaveNavigationViewState(tab);
                    }
                }
            }

            var result = _userCommandExecutionService.Start(startInfo);
            if (result.Exception is not null)
            {
                throw result.Exception;
            }

            if (result.Started && result.Process is { } process)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!await UserCommandExecutionService.WaitForExitAsync(process))
                        {
                            return;
                        }

                        await Dispatcher.InvokeAsync(async () =>
                        {
                            if (pane is not null)
                            {
                                if (IsWorkspaceDisplayPane(pane))
                                {
                                    await ReloadFolderPaneAsync(pane);
                                }
                                else
                                {
                                    await RefreshCurrentFolderAsync();
                                }
                            }
                        });
                    }
                    catch
                    {
                        // Ignore refresh dispatch exceptions after the process exits.
                    }
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                _text.Format("UserCommandExecuteFailed", command.Name ?? "", ex.Message),
                _text.Get("UserCommandErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

}
