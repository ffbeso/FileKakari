using System.Diagnostics;
using System.IO;

namespace FileKakari;

internal sealed class UserCommandExecutionContext
{
    public required string CurrentDirectory { get; init; }

    public required IReadOnlyList<FileEntry> SelectedEntries { get; init; }
}

internal sealed class UserCommandExecutionResult
{
    public bool Started { get; init; }

    public Process? Process { get; init; }

    public Exception? Exception { get; init; }
}

internal sealed class UserCommandExecutionService
{
    public ProcessStartInfo CreateStartInfo(UserCommand command, UserCommandExecutionContext context)
    {
        var resolvedExecutable = ExpandPlaceholders(command.Executable ?? "", context);
        var resolvedArguments = ExpandPlaceholders(command.Arguments ?? "", context);
        var resolvedWorkingDirectory = ResolveWorkingDirectory(command, context);

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedExecutable,
            Arguments = resolvedArguments,
            UseShellExecute = command.UseShellExecute
        };

        ExternalProcessStartInfo.ApplyWorkingDirectory(startInfo, resolvedWorkingDirectory);

        return startInfo;
    }

    public UserCommandExecutionResult Start(ProcessStartInfo startInfo)
    {
        try
        {
            var process = Process.Start(startInfo);
            return new UserCommandExecutionResult
            {
                Started = process is not null,
                Process = process
            };
        }
        catch (Exception ex)
        {
            return new UserCommandExecutionResult
            {
                Exception = ex
            };
        }
    }

    public static async Task<bool> WaitForExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ExpandPlaceholders(string template, UserCommandExecutionContext context)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var currentDir = context.CurrentDirectory;
        var selectedEntries = context.SelectedEntries;
        var parentDir = "";
        try
        {
            if (!string.IsNullOrEmpty(currentDir) && !SpecialLocationService.IsSpecialUri(currentDir) && Path.IsPathRooted(currentDir))
            {
                parentDir = Path.GetDirectoryName(currentDir) ?? "";
            }
        }
        catch
        {
            // Ignore
        }

        var currentDirectoryName = "";
        try
        {
            if (!string.IsNullOrEmpty(currentDir) && !SpecialLocationService.IsSpecialUri(currentDir) && Path.IsPathRooted(currentDir))
            {
                var di = new DirectoryInfo(currentDir);
                currentDirectoryName = di.Parent != null ? di.Name : di.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            // Ignore
        }

        var firstEntry = selectedEntries.FirstOrDefault();
        var firstPath = firstEntry?.FullPath ?? "";
        var firstName = firstEntry?.Name ?? "";
        var firstDir = firstEntry?.ParentPath ?? "";
        var nameWithoutExtension = firstEntry?.BaseName ?? "";
        var firstExtension = !string.IsNullOrEmpty(firstPath) ? Path.GetExtension(firstPath) : "";
        var selectedDir = firstEntry != null ? (firstEntry.IsDirectory ? firstEntry.FullPath : firstEntry.ParentPath) : "";
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var selectedDirectoryName = "";
        try
        {
            if (firstEntry != null)
            {
                if (firstEntry.IsDirectory)
                {
                    var di = new DirectoryInfo(firstEntry.FullPath);
                    selectedDirectoryName = di.Parent != null ? di.Name : di.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                else
                {
                    var parentPath = firstEntry.ParentPath;
                    if (!string.IsNullOrEmpty(parentPath))
                    {
                        var di = new DirectoryInfo(parentPath);
                        selectedDirectoryName = di.Parent != null ? di.Name : di.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }

        var currentParentDirectoryName = "";
        try
        {
            if (!string.IsNullOrEmpty(parentDir))
            {
                var di = new DirectoryInfo(parentDir);
                currentParentDirectoryName = di.Parent != null ? di.Name : di.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            // Ignore
        }

        var selectedPaths = string.Join(" ", selectedEntries.Select(e => $"\"{e.FullPath}\""));

        var result = template;

        // 置換処理（部分一致による誤動作を防ぐため、文字列の長いプレースホルダから順に処理します）
        result = result.Replace("{currentParentDirectoryName}", currentParentDirectoryName, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{currentParentDirectory}", parentDir, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selectedFileExtension}", firstExtension, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selectedDirectoryName}", selectedDirectoryName, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selectedFileBaseName}", nameWithoutExtension, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{currentDirectoryName}", currentDirectoryName, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selectedPathsQuoted}", selectedPaths, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{nameWithoutExtension}", nameWithoutExtension, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selectedDirectory}", selectedDir, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selectedFileName}", firstName, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{currentDirectory}", currentDir, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selectedFiles}", selectedPaths, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selectedFile}", firstPath, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{commandDir}", AppPaths.CommandsDirectory, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{timestamp}", timestamp, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{selected}", selectedPaths, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{current}", currentDir, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{parent}", parentDir, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{path}", firstPath, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{name}", firstName, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{dir}", firstDir, StringComparison.OrdinalIgnoreCase);

        return result;
    }

    private static string ResolveWorkingDirectory(UserCommand command, UserCommandExecutionContext context)
    {
        var explicitWorkingDirectory = ExpandPlaceholders(command.WorkingDirectory ?? "", context);
        var resolvedExplicitWorkingDirectory = ExternalProcessStartInfo.ResolveExistingDirectory(explicitWorkingDirectory);
        if (!string.IsNullOrWhiteSpace(resolvedExplicitWorkingDirectory))
        {
            return resolvedExplicitWorkingDirectory;
        }

        var selectedWorkingDirectory = ExternalProcessStartInfo.ResolveWorkingDirectoryForEntry(context.SelectedEntries.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(selectedWorkingDirectory))
        {
            return selectedWorkingDirectory;
        }

        return ExternalProcessStartInfo.ResolveExistingDirectory(context.CurrentDirectory);
    }
}
