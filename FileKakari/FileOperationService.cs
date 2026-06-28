using System.Diagnostics;
using System.IO;

namespace FileKakari;

public sealed class FileOperationService
{
    public string GetAvailableCopyPath(string sourcePath, string targetDirectory, bool isDirectory)
    {
        return FileSystemOperations.GetAvailableCopyPath(sourcePath, targetDirectory, isDirectory);
    }

    public string GetAvailableRenamePath(string sourcePath, string targetDirectory, bool isDirectory)
    {
        return FileSystemOperations.GetAvailableRenamePath(sourcePath, targetDirectory, isDirectory);
    }

    public string GetAvailableNewItemPath(string targetDirectory, string baseName, string extension, bool isDirectory)
    {
        return FileSystemOperations.GetAvailableNewItemPath(targetDirectory, baseName, extension, isDirectory);
    }

    public Task CopyAsync(string sourcePath, string targetPath, bool isDirectory)
    {
        return FileSystemOperations.CopyAsync(sourcePath, targetPath, isDirectory);
    }

    public Task MoveAsync(string sourcePath, string targetPath, bool isDirectory)
    {
        return FileSystemOperations.MoveAsync(sourcePath, targetPath, isDirectory);
    }

    public Task<(int ErrorCode, bool UserAborted)> CopyMultipleAsync(IntPtr hwndOwner, IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        return Task.Run(() => FileSystemOperations.CopyMultiple(hwndOwner, sourcePaths, targetDirectory));
    }

    public Task<(int ErrorCode, bool UserAborted)> CopyMultipleToPathsAsync(IntPtr hwndOwner, IReadOnlyList<string> sourcePaths, IReadOnlyList<string> targetPaths)
    {
        return Task.Run(() => FileSystemOperations.CopyMultipleToPaths(hwndOwner, sourcePaths, targetPaths));
    }

    public Task<(int ErrorCode, bool UserAborted)> MoveMultipleAsync(IntPtr hwndOwner, IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        return Task.Run(() => FileSystemOperations.MoveMultiple(hwndOwner, sourcePaths, targetDirectory));
    }

    public Task<(List<string> ExecutedPaths, Exception? Error)> CopySelfMultipleAsync(IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        return FileSystemOperations.CopySelfMultipleAsync(sourcePaths, targetDirectory);
    }

    public Task DeleteAsync(string path, bool isDirectory)
    {
        return FileSystemOperations.DeleteAsync(path, isDirectory);
    }

    public Task CreateDirectoryAsync(string path)
    {
        return FileSystemOperations.CreateDirectoryAsync(path);
    }

    public Task CreateTextFileAsync(string path)
    {
        return FileSystemOperations.CreateTextFileAsync(path);
    }

    public Task RenameAsync(string sourcePath, string targetPath, bool isDirectory)
    {
        return MoveAsync(sourcePath, targetPath, isDirectory);
    }

    public void OpenWithDefaultApp(string path)
    {
        Process.Start(ExternalProcessStartInfo.CreateShellExecute(path));
    }
}
