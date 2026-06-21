using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileKakari
{
    // Provides per-extension shell icons fetched via SHGetFileInfo. Caches ImageSource per key.
    internal sealed class ShellIconProvider
    {
        public static readonly ShellIconProvider Instance = new ShellIconProvider();

        private readonly ConcurrentDictionary<string, ImageSource> _cache = new();
        private readonly ConcurrentDictionary<string, Task<ImageSource?>> _loadTasks = new();

        private ShellIconProvider() { }

        public Task<ImageSource?> GetIconAsync(string? extension, bool isDirectory, string? path = null)
        {
            var useFilePath = !isDirectory
                && ShouldUseFilePathIcon(extension)
                && !string.IsNullOrWhiteSpace(path);
            var key = useFilePath
                ? "<PATH>:" + path!.ToLowerInvariant()
                : isDirectory ? "<DIR>" : string.IsNullOrEmpty(extension) ? "<FILE>" : "." + extension.ToLowerInvariant();
            if (_cache.TryGetValue(key, out var img))
            {
                return Task.FromResult<ImageSource?>(img);
            }

            var task = _loadTasks.GetOrAdd(key, _ => Task.Run(() => LoadIconInternalAsync(extension, isDirectory, useFilePath ? path : null)));
            return task.ContinueWith(t =>
            {
                _loadTasks.TryRemove(key, out _);
                if (t.IsCompletedSuccessfully && t.Result is not null)
                {
                    _cache[key] = t.Result;
                }

                return t.Result;
            });
        }

        private static bool ShouldUseFilePathIcon(string? extension)
        {
            return extension is not null
                && (string.Equals(extension, "exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, "lnk", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, "url", StringComparison.OrdinalIgnoreCase));
        }

        private ImageSource? LoadIconInternalAsync(string? extension, bool isDirectory, string? path)
        {
            try
            {
                var useFileAttributes = string.IsNullOrWhiteSpace(path);
                var flags = SHGFI_ICON | SHGFI_SMALLICON;
                if (useFileAttributes)
                {
                    flags |= SHGFI_USEFILEATTRIBUTES;
                }

                // For extension-based lookup, pass a fake file name (use dot extension) so shell returns the correct icon.
                var lookup = useFileAttributes
                    ? isDirectory ? "dummy" : (string.IsNullOrEmpty(extension) ? "dummy" : "dummy." + extension)
                    : path!;
                uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                SHFILEINFO shfi = default;
                var res = SHGetFileInfo(lookup, attrs, out shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                if (shfi.hIcon == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                    bmp.Freeze();
                    return bmp;
                }
                finally
                {
                    DestroyIcon(shfi.hIcon);
                }
            }
            catch
            {
                return null;
            }
        }

        #region Win32 interop
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        #endregion
    }
}
