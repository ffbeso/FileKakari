using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;

namespace FileKakari;

/// <summary>
/// Materializes Shell virtual clipboard items (for example Explorer's ZIP folder items)
/// into a short-lived local directory so they can use the normal file transfer pipeline.
/// </summary>
public static class ShellVirtualFileClipboard
{
    private const string FileGroupDescriptorWFormat = "FileGroupDescriptorW";
    private const string FileGroupDescriptorFormat = "FileGroupDescriptor";
    private const string FileContentsFormat = "FileContents";
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileDescriptorWriteTime = 0x20;
    private const int MaxDescriptorCount = 4096;
    private const int CopyBufferSize = 80 * 1024;
    private const int MaxDescriptorBytes = 4 + MaxDescriptorCount * 592;
    private static readonly string ExtractionBaseDirectory = Path.Combine(Path.GetTempPath(), "FileKakari", "VirtualClipboard");

    public static bool ContainsVirtualFiles()
    {
        try
        {
            var dataObject = Clipboard.GetDataObject();
            return dataObject is not null
                && dataObject.GetDataPresent(FileContentsFormat)
                && (dataObject.GetDataPresent(FileGroupDescriptorWFormat) || dataObject.GetDataPresent(FileGroupDescriptorFormat));
        }
        catch
        {
            return false;
        }
    }

    public static string GetAvailableFormatsForLog()
    {
        try
        {
            var formats = Clipboard.GetDataObject()?.GetFormats(autoConvert: false) ?? Array.Empty<string>();
            return string.Join(",", formats.OrderBy(format => format, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return "unavailable";
        }
    }

    public static bool TryExtract(out ShellVirtualClipboardExtraction? extraction, out string error)
    {
        extraction = null;
        error = "";
        IntPtr dataObjectPointer = IntPtr.Zero;

        try
        {
            var descriptorFormat = GetDescriptorFormatName();
            if (descriptorFormat is null)
            {
                error = "Clipboard does not provide a Shell virtual file descriptor.";
                return false;
            }

            var hr = OleGetClipboard(out dataObjectPointer);
            if (hr < 0 || dataObjectPointer == IntPtr.Zero)
            {
                error = $"Clipboard data is unavailable (HRESULT: 0x{hr:X8}).";
                return false;
            }

            var dataObject = (System.Runtime.InteropServices.ComTypes.IDataObject)Marshal.GetObjectForIUnknown(dataObjectPointer);
            var descriptors = ReadDescriptors(dataObject, descriptorFormat);
            if (descriptors.Count == 0)
            {
                error = "Shell virtual file descriptor contains no items.";
                return false;
            }

            var tempRoot = CreateExtractionRoot();
            try
            {
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int extractedCount = 0;
                for (var index = 0; index < descriptors.Count; index++)
                {
                    var descriptor = descriptors[index];
                    if (!TryResolveSafeTargetPath(tempRoot, descriptor.Name, out var targetPath, out var relativePath, out var skipReason))
                    {
                        throw new InvalidDataException($"Virtual clipboard item {index} was rejected: {skipReason}");
                    }

                    var rootName = relativePath.Split(Path.DirectorySeparatorChar)[0];
                    roots.Add(rootName);
                    if (descriptor.IsDirectory)
                    {
                        Directory.CreateDirectory(targetPath);
                    }
                    else
                    {
                        var parent = Path.GetDirectoryName(targetPath);
                        if (string.IsNullOrWhiteSpace(parent))
                        {
                            throw new InvalidDataException("Virtual clipboard item has no valid parent directory.");
                        }

                        Directory.CreateDirectory(parent);
                        CopyFileContentsToPath(dataObject, index, targetPath, descriptor.Size);
                        if (descriptor.LastWriteTimeUtc is { } writeTime)
                        {
                            File.SetLastWriteTimeUtc(targetPath, writeTime);
                        }
                    }

                    extractedCount++;
                }

                var items = roots
                    .Select(rootName =>
                    {
                        var path = Path.Combine(tempRoot, rootName);
                        return new FileTransferItem(path, rootName, Directory.Exists(path));
                    })
                    .Where(item => File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath))
                    .ToList();
                if (items.Count == 0)
                {
                    throw new InvalidDataException("Shell virtual file extraction produced no usable items.");
                }

                extraction = new ShellVirtualClipboardExtraction(tempRoot, items, descriptors.Count, extractedCount);
                return true;
            }
            catch
            {
                TryDeleteExtractionRoot(tempRoot, out _);
                throw;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            PerfLog.Write($"clipboard-virtual-extract-failed exceptionType={ex.GetType().Name} skipReason=\"{SanitizeLogValue(ex.Message)}\"");
            return false;
        }
        finally
        {
            if (dataObjectPointer != IntPtr.Zero)
            {
                Marshal.Release(dataObjectPointer);
            }
        }
    }

    public static void CleanupStaleExtractions()
    {
        try
        {
            if (!Directory.Exists(ExtractionBaseDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-1);
            var removed = 0;
            foreach (var directory in Directory.EnumerateDirectories(ExtractionBaseDirectory))
            {
                var info = new DirectoryInfo(directory);
                if (info.LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                TryDeleteExtractionRoot(directory, out var cleanupSucceeded);
                if (cleanupSucceeded)
                {
                    removed++;
                }
            }

            if (removed > 0)
            {
                PerfLog.WriteVerbose($"clipboard-virtual-cleanup staleRemoved={removed}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PerfLog.WriteVerbose($"clipboard-virtual-cleanup skipReason={ex.GetType().Name}");
        }
    }

    private static string? GetDescriptorFormatName()
    {
        try
        {
            var dataObject = Clipboard.GetDataObject();
            if (dataObject is null || !dataObject.GetDataPresent(FileContentsFormat))
            {
                return null;
            }

            if (dataObject.GetDataPresent(FileGroupDescriptorWFormat))
            {
                return FileGroupDescriptorWFormat;
            }

            return dataObject.GetDataPresent(FileGroupDescriptorFormat) ? FileGroupDescriptorFormat : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<VirtualFileDescriptor> ReadDescriptors(System.Runtime.InteropServices.ComTypes.IDataObject dataObject, string formatName)
    {
        var format = CreateFormatEtc(formatName, -1, TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM);
        dataObject.GetData(ref format, out var medium);
        try
        {
            return medium.tymed switch
            {
                TYMED.TYMED_HGLOBAL => ReadDescriptorsFromGlobalMemory(medium.unionmember, formatName == FileGroupDescriptorWFormat),
                TYMED.TYMED_ISTREAM => ReadDescriptorsFromStream(medium.unionmember, formatName == FileGroupDescriptorWFormat),
                _ => throw new NotSupportedException($"Unsupported descriptor storage medium: {medium.tymed}.")
            };
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static List<VirtualFileDescriptor> ReadDescriptorsFromStream(IntPtr streamPointer, bool unicode)
    {
        var stream = (IStream)Marshal.GetObjectForIUnknown(streamPointer);
        using var bytes = new MemoryStream();
        var buffer = new byte[CopyBufferSize];
        var bytesReadPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                Marshal.WriteInt32(bytesReadPointer, 0);
                stream.Read(buffer, buffer.Length, bytesReadPointer);
                var read = Marshal.ReadInt32(bytesReadPointer);
                if (read <= 0)
                {
                    break;
                }

                if (bytes.Length + read > MaxDescriptorBytes)
                {
                    throw new InvalidDataException("Shell virtual file descriptor exceeds the supported size.");
                }

                bytes.Write(buffer, 0, read);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(bytesReadPointer);
        }

        var descriptorBytes = bytes.ToArray();
        var handle = GCHandle.Alloc(descriptorBytes, GCHandleType.Pinned);
        try
        {
            return ReadDescriptorsFromPointer(handle.AddrOfPinnedObject(), descriptorBytes.Length, unicode);
        }
        finally
        {
            handle.Free();
        }
    }

    private static List<VirtualFileDescriptor> ReadDescriptorsFromGlobalMemory(IntPtr globalMemory, bool unicode)
    {
        var size = checked((int)GlobalSize(globalMemory).ToUInt64());
        if (size <= 0)
        {
            throw new InvalidDataException("Shell virtual file descriptor is empty.");
        }

        var pointer = GlobalLock(globalMemory);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException("Failed to lock the Shell virtual file descriptor.");
        }

        try
        {
            return ReadDescriptorsFromPointer(pointer, size, unicode);
        }
        finally
        {
            GlobalUnlock(globalMemory);
        }
    }

    private static List<VirtualFileDescriptor> ReadDescriptorsFromPointer(IntPtr pointer, int totalSize, bool unicode)
    {
        if (totalSize < sizeof(uint))
        {
            throw new InvalidDataException("Shell virtual file descriptor is truncated.");
        }

        var count = Marshal.ReadInt32(pointer);
        if (count <= 0 || count > MaxDescriptorCount)
        {
            throw new InvalidDataException($"Shell virtual file descriptor count is invalid: {count}.");
        }

        var descriptorSize = unicode ? Marshal.SizeOf<FileDescriptorW>() : Marshal.SizeOf<FileDescriptorA>();
        var requiredSize = checked(sizeof(uint) + count * descriptorSize);
        if (requiredSize > totalSize)
        {
            throw new InvalidDataException("Shell virtual file descriptor is truncated.");
        }

        var descriptors = new List<VirtualFileDescriptor>(count);
        for (var index = 0; index < count; index++)
        {
            var descriptorPointer = IntPtr.Add(pointer, sizeof(uint) + index * descriptorSize);
            if (unicode)
            {
                var descriptor = Marshal.PtrToStructure<FileDescriptorW>(descriptorPointer);
                descriptors.Add(new VirtualFileDescriptor(
                    descriptor.FileName,
                    (descriptor.FileAttributes & FileAttributeDirectory) != 0,
                    CombineFileSize(descriptor.FileSizeHigh, descriptor.FileSizeLow),
                    TryGetWriteTime(descriptor.Flags, descriptor.LastWriteTime)));
            }
            else
            {
                var descriptor = Marshal.PtrToStructure<FileDescriptorA>(descriptorPointer);
                descriptors.Add(new VirtualFileDescriptor(
                    descriptor.FileName,
                    (descriptor.FileAttributes & FileAttributeDirectory) != 0,
                    CombineFileSize(descriptor.FileSizeHigh, descriptor.FileSizeLow),
                    TryGetWriteTime(descriptor.Flags, descriptor.LastWriteTime)));
            }
        }

        return descriptors;
    }

    private static void CopyFileContentsToPath(System.Runtime.InteropServices.ComTypes.IDataObject dataObject, int itemIndex, string targetPath, long expectedSize)
    {
        var format = CreateFormatEtc(FileContentsFormat, itemIndex, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL);
        dataObject.GetData(ref format, out var medium);
        try
        {
            using var destination = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
            switch (medium.tymed)
            {
                case TYMED.TYMED_ISTREAM:
                    CopyComStreamToFile(medium.unionmember, destination);
                    break;
                case TYMED.TYMED_HGLOBAL:
                    CopyGlobalMemoryToFile(medium.unionmember, destination, expectedSize);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported FileContents storage medium: {medium.tymed}.");
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static void CopyComStreamToFile(IntPtr streamPointer, Stream destination)
    {
        var stream = (IStream)Marshal.GetObjectForIUnknown(streamPointer);
        var buffer = new byte[CopyBufferSize];
        var bytesReadPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                Marshal.WriteInt32(bytesReadPointer, 0);
                stream.Read(buffer, buffer.Length, bytesReadPointer);
                var read = Marshal.ReadInt32(bytesReadPointer);
                if (read <= 0)
                {
                    break;
                }

                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(bytesReadPointer);
        }
    }

    private static void CopyGlobalMemoryToFile(IntPtr globalMemory, Stream destination, long expectedSize)
    {
        var availableSize = checked((long)GlobalSize(globalMemory).ToUInt64());
        var size = expectedSize > 0 ? Math.Min(expectedSize, availableSize) : availableSize;
        var pointer = GlobalLock(globalMemory);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException("Failed to lock Shell virtual file contents.");
        }

        try
        {
            var buffer = new byte[CopyBufferSize];
            long offset = 0;
            while (offset < size)
            {
                var count = (int)Math.Min(buffer.Length, size - offset);
                Marshal.Copy(IntPtr.Add(pointer, checked((int)offset)), buffer, 0, count);
                destination.Write(buffer, 0, count);
                offset += count;
            }
        }
        finally
        {
            GlobalUnlock(globalMemory);
        }
    }

    private static FORMATETC CreateFormatEtc(string formatName, int index, TYMED tymed) => new()
    {
        cfFormat = unchecked((short)DataFormats.GetDataFormat(formatName).Id),
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        tymed = tymed,
        ptd = IntPtr.Zero
    };

    private static string CreateExtractionRoot()
    {
        Directory.CreateDirectory(ExtractionBaseDirectory);
        var root = Path.Combine(ExtractionBaseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool TryResolveSafeTargetPath(string root, string fileName, out string targetPath, out string relativePath, out string reason)
    {
        targetPath = "";
        relativePath = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) || fileName.Contains(':'))
        {
            reason = "absolute-or-empty-path";
            return false;
        }

        var parts = fileName
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            reason = "empty-path";
            return false;
        }

        foreach (var part in parts)
        {
            if (part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedDeviceName(part))
            {
                reason = "invalid-relative-component";
                return false;
            }
        }

        relativePath = Path.Combine(parts);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        targetPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!targetPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "path-escapes-temp-root";
            return false;
        }

        return true;
    }

    private static bool IsReservedDeviceName(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (baseName is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$")
        {
            return true;
        }

        return baseName.Length == 4
            && (baseName.StartsWith("COM", StringComparison.Ordinal) || baseName.StartsWith("LPT", StringComparison.Ordinal))
            && baseName[3] is >= '1' and <= '9';
    }

    private static long CombineFileSize(uint high, uint low) => ((long)high << 32) | low;

    private static DateTime? TryGetWriteTime(uint flags, FILETIME fileTime)
    {
        if ((flags & FileDescriptorWriteTime) == 0)
        {
            return null;
        }

        try
        {
            var value = ((long)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
            return DateTime.FromFileTimeUtc(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void TryDeleteExtractionRoot(string root, out bool cleanupSucceeded)
    {
        cleanupSucceeded = false;
        try
        {
            var normalizedBase = Path.GetFullPath(ExtractionBaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!normalizedRoot.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(normalizedRoot))
            {
                var attributes = File.GetAttributes(normalizedRoot);
                Directory.Delete(normalizedRoot, recursive: (attributes & FileAttributes.ReparsePoint) == 0);
            }

            cleanupSucceeded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PerfLog.WriteVerbose($"clipboard-virtual-cleanup tempRoot=\"{Path.GetFileName(root)}\" cleanupSucceeded=false skipReason={ex.GetType().Name}");
        }
    }

    private static string SanitizeLogValue(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileDescriptorW
    {
        public uint Flags;
        public Guid ClassId;
        public int SizeX;
        public int SizeY;
        public int PointX;
        public int PointY;
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct FileDescriptorA
    {
        public uint Flags;
        public Guid ClassId;
        public int SizeX;
        public int SizeY;
        public int PointX;
        public int PointY;
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
    }

    private sealed record VirtualFileDescriptor(string Name, bool IsDirectory, long Size, DateTime? LastWriteTimeUtc);

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard(out IntPtr dataObject);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}

public sealed class ShellVirtualClipboardExtraction : IDisposable
{
    private bool _disposed;

    internal ShellVirtualClipboardExtraction(string tempRoot, IReadOnlyList<FileTransferItem> items, int descriptorCount, int extractedCount)
    {
        TempRoot = tempRoot;
        Items = items;
        DescriptorCount = descriptorCount;
        ExtractedCount = extractedCount;
    }

    public string TempRoot { get; }
    public IReadOnlyList<FileTransferItem> Items { get; }
    public int DescriptorCount { get; }
    public int ExtractedCount { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cleanupSucceeded = false;
        try
        {
            if (Directory.Exists(TempRoot))
            {
                var attributes = File.GetAttributes(TempRoot);
                Directory.Delete(TempRoot, recursive: (attributes & FileAttributes.ReparsePoint) == 0);
            }
            cleanupSucceeded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PerfLog.WriteVerbose($"clipboard-virtual-cleanup tempRoot=\"{Path.GetFileName(TempRoot)}\" cleanupSucceeded=false skipReason={ex.GetType().Name}");
        }

        PerfLog.WriteVerbose($"clipboard-virtual-cleanup tempRoot=\"{Path.GetFileName(TempRoot)}\" cleanupSucceeded={cleanupSucceeded}");
    }
}
