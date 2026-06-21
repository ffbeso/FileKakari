using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace FileKakari;

public static class FileSystemOperations
{
    public static string GetAvailableNewItemPath(string targetDirectory, string baseName, string extension, bool isDirectory)
    {
        for (var number = 1; number < 10_000; number++)
        {
            var candidateName = number == 1
                ? baseName + extension
                : AppStrings.Format("NewItemNumberedName", baseName, number) + extension;
            var candidatePath = Path.Combine(targetDirectory, candidateName);

            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new IOException(AppStrings.Get("NewItemNameFailure"));
    }

    public static string GetAvailableCopyPath(string sourcePath, string targetDirectory, bool isDirectory, HashSet<string>? reservedPaths = null)
    {
        var sourceName = Path.GetFileName(sourcePath);
        var firstTargetPath = Path.Combine(targetDirectory, sourceName);
        if (!File.Exists(firstTargetPath)
            && !Directory.Exists(firstTargetPath)
            && (reservedPaths is null || !reservedPaths.Contains(firstTargetPath)))
        {
            return firstTargetPath;
        }

        var extension = isDirectory ? "" : Path.GetExtension(sourceName);
        var baseName = GetCopyBaseName(isDirectory ? sourceName : Path.GetFileNameWithoutExtension(sourceName));

        for (var copyNumber = 1; copyNumber < 10_000; copyNumber++)
        {
            var suffix = copyNumber == 1
                ? AppStrings.Get("CopySuffix")
                : AppStrings.Format("CopySuffixNumbered", copyNumber);
            var candidateName = isDirectory
                ? baseName + suffix
                : baseName + suffix + extension;
            var candidatePath = Path.Combine(targetDirectory, candidateName);

            if (!File.Exists(candidatePath)
                && !Directory.Exists(candidatePath)
                && (reservedPaths is null || !reservedPaths.Contains(candidatePath)))
            {
                return candidatePath;
            }
        }

        throw new IOException(AppStrings.Get("CopyNameFailure"));
    }

    public static string GetAvailableRenamePath(string sourcePath, string targetDirectory, bool isDirectory)
    {
        var sourceName = Path.GetFileName(sourcePath);
        var extension = isDirectory ? "" : Path.GetExtension(sourceName);
        var baseName = isDirectory ? sourceName : Path.GetFileNameWithoutExtension(sourceName);

        for (var number = 1; number < 10_000; number++)
        {
            var candidateName = isDirectory
                ? AppStrings.Format("NewItemNumberedName", baseName, number)
                : AppStrings.Format("NewItemNumberedName", baseName, number) + extension;
            var candidatePath = Path.Combine(targetDirectory, candidateName);

            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return GetAvailableCopyPath(sourcePath, targetDirectory, isDirectory);
    }

    public static Task CopyAsync(string sourcePath, string targetPath, bool isDirectory)
    {
        return Task.Run(() =>
        {
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                throw new IOException(AppStrings.Get("SameNameExists"));
            }

            if (isDirectory)
            {
                CopyDirectory(sourcePath, targetPath);
                return;
            }

            File.Copy(sourcePath, targetPath, overwrite: false);
        });
    }

    public static Task MoveAsync(string sourcePath, string targetPath, bool isDirectory)
    {
        return Task.Run(() =>
        {
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                throw new IOException(AppStrings.Get("SameNameExists"));
            }

            if (isDirectory)
            {
                Directory.Move(sourcePath, targetPath);
                return;
            }

            File.Move(sourcePath, targetPath);
        });
    }

    public static Task CreateDirectoryAsync(string path)
    {
        return Task.Run(() => Directory.CreateDirectory(path));
    }

    public static Task CreateTextFileAsync(string path)
    {
        return Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        });
    }

    public static Task DeleteAsync(string path, bool isDirectory)
    {
        return Task.Run(() =>
        {
            if (isDirectory)
            {
                FileSystem.DeleteDirectory(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
                return;
            }

            FileSystem.DeleteFile(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        });
    }

    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        var sourceFullPath = EnsureTrailingSeparator(Path.GetFullPath(sourcePath));
        var targetFullPath = EnsureTrailingSeparator(Path.GetFullPath(targetPath));
        if (targetFullPath.StartsWith(sourceFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(AppStrings.Get("CannotCopyFolderIntoItself"));
        }

        Directory.CreateDirectory(targetPath);

        foreach (var filePath in Directory.EnumerateFiles(sourcePath))
        {
            var fileName = Path.GetFileName(filePath);
            File.Copy(filePath, Path.Combine(targetPath, fileName), overwrite: false);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourcePath))
        {
            var directoryName = Path.GetFileName(directoryPath);
            CopyDirectory(directoryPath, Path.Combine(targetPath, directoryName));
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string GetCopyBaseName(string baseName)
    {
        var copySuffix = AppStrings.Get("CopySuffix");
        if (baseName.EndsWith(copySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return baseName[..^copySuffix.Length];
        }

        var numberedPattern = "^(.+)" + Regex.Escape(copySuffix) + @" \(\d+\)$";
        var match = Regex.Match(baseName, numberedPattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : baseName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT_x86
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT_x64
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(IntPtr lpFileOp);

    private const uint FO_MOVE = 0x0001;
    private const uint FO_COPY = 0x0002;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    private const ushort FOF_MULTIDESTFILES = 0x0001;

    private static int CallSHFileOperation(IntPtr hwnd, uint wFunc, string pFrom, string pTo, bool multipleDestinations, out bool fAnyOperationsAborted)
    {
        fAnyOperationsAborted = false;
        var fromPtr = Marshal.StringToHGlobalUni(pFrom);
        var toPtr = Marshal.StringToHGlobalUni(pTo);
        try
        {
            ushort fFlags = FOF_NOCONFIRMMKDIR;
            // NOTE: FOF_ALLOWUNDO allows the Windows Shell to record this file operation to the Windows system Undo stack.
            // This is solely managed by Windows Shell (allowing Explorer's Ctrl+Z to undo this operation).
            // This is NOT recorded in FileKakari's custom Undo manager, and FileKakari's Ctrl+Z will not undo this operation.
            fFlags |= FOF_ALLOWUNDO;
            if (multipleDestinations)
            {
                fFlags |= FOF_MULTIDESTFILES;
            }

            if (Environment.Is64BitProcess)
            {
                var op = new SHFILEOPSTRUCT_x64
                {
                    hwnd = hwnd,
                    wFunc = wFunc,
                    pFrom = fromPtr,
                    pTo = toPtr,
                    fFlags = fFlags,
                    fAnyOperationsAborted = false,
                    hNameMappings = IntPtr.Zero,
                    lpszProgressTitle = IntPtr.Zero
                };
                var size = Marshal.SizeOf(op);
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(op, ptr, false);
                    var result = SHFileOperationW(ptr);
                    var updatedOp = Marshal.PtrToStructure<SHFILEOPSTRUCT_x64>(ptr);
                    fAnyOperationsAborted = updatedOp.fAnyOperationsAborted;
                    return result;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            else
            {
                var op = new SHFILEOPSTRUCT_x86
                {
                    hwnd = hwnd,
                    wFunc = wFunc,
                    pFrom = fromPtr,
                    pTo = toPtr,
                    fFlags = fFlags,
                    fAnyOperationsAborted = false,
                    hNameMappings = IntPtr.Zero,
                    lpszProgressTitle = IntPtr.Zero
                };
                var size = Marshal.SizeOf(op);
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(op, ptr, false);
                    var result = SHFileOperationW(ptr);
                    var updatedOp = Marshal.PtrToStructure<SHFILEOPSTRUCT_x86>(ptr);
                    fAnyOperationsAborted = updatedOp.fAnyOperationsAborted;
                    return result;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fromPtr);
            Marshal.FreeHGlobal(toPtr);
        }
    }

    public static (int ErrorCode, bool UserAborted) CopyMultiple(IntPtr hwndOwner, IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        var from = string.Join("\0", sourcePaths) + "\0\0";
        var to = targetDirectory + "\0\0";
        bool userAborted;
        int errorCode = CallSHFileOperation(hwndOwner, FO_COPY, from, to, false, out userAborted);
        return (errorCode, userAborted);
    }

    public static (int ErrorCode, bool UserAborted) MoveMultiple(IntPtr hwndOwner, IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        var from = string.Join("\0", sourcePaths) + "\0\0";
        var to = targetDirectory + "\0\0";
        bool userAborted;
        int errorCode = CallSHFileOperation(hwndOwner, FO_MOVE, from, to, false, out userAborted);
        return (errorCode, userAborted);
    }

    public static (int ErrorCode, bool UserAborted) CopyMultipleToPaths(IntPtr hwndOwner, IReadOnlyList<string> sourcePaths, IReadOnlyList<string> targetPaths)
    {
        if (sourcePaths == null) throw new ArgumentNullException(nameof(sourcePaths));
        if (targetPaths == null) throw new ArgumentNullException(nameof(targetPaths));
        if (sourcePaths.Count != targetPaths.Count)
        {
            throw new ArgumentException("Source paths count and target paths count must match.");
        }

        var from = string.Join("\0", sourcePaths) + "\0\0";
        var to = string.Join("\0", targetPaths) + "\0\0";
        bool userAborted;
        int errorCode = CallSHFileOperation(hwndOwner, FO_COPY, from, to, true, out userAborted);
        return (errorCode, userAborted);
    }

    public static Task<(List<string> ExecutedPaths, Exception? Error)> CopySelfMultipleAsync(IReadOnlyList<string> sourcePaths, string targetDirectory)
    {
        return Task.Run(() =>
        {
            var executedPaths = new List<string>(sourcePaths.Count);
            var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Exception? error = null;

            foreach (var sourcePath in sourcePaths)
            {
                try
                {
                    var isDirectory = Directory.Exists(sourcePath);
                    var targetPath = GetAvailableCopyPath(sourcePath, targetDirectory, isDirectory, reservedPaths);
                    reservedPaths.Add(targetPath);

                    if (isDirectory)
                    {
                        CopyDirectory(sourcePath, targetPath);
                    }
                    else
                    {
                        File.Copy(sourcePath, targetPath, overwrite: false);
                    }
                    executedPaths.Add(targetPath);
                }
                catch (Exception ex)
                {
                    error = ex;
                    break;
                }
            }

            return (executedPaths, error);
        });
    }
}
