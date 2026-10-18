using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FileKakari;

public interface IShellContextMenuService
{
    Task<bool> ShowAsync(Window owner, string currentFolderPath, IReadOnlyList<FileEntry> selectedEntries, Point position);
}

public sealed class ShellContextMenuService : IShellContextMenuService
{
    public Task<bool> ShowAsync(Window owner, string currentFolderPath, IReadOnlyList<FileEntry> selectedEntries, Point position)
    {
        return Task.FromResult(false);
    }
}

public static class ShellItemActions
{
    private const uint SeeMaskInvokeIdList = 0x0000000C;
    private const int SwShow = 5;

    public static void ShowProperties(Window owner, string path)
    {
        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskInvokeIdList,
            hwnd = new WindowInteropHelper(owner).Handle,
            lpVerb = "properties",
            lpFile = path,
            nShow = SwShow
        };

        if (!ShellExecuteEx(ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static void ShowOpenWithDialog(Window owner, string path)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        var info = new OPENASINFO
        {
            pcszFile = path,
            pcszClass = null,
            oaifInFlags = OPENASINFOFLAGS.OAIF_ALLOW_REGISTRATION | OPENASINFOFLAGS.OAIF_EXEC
        };

        int hr = SHOpenWithDialog(hwnd, ref info);
        if (hr != 0 && hr != unchecked((int)0x800704C7))
        {
            var execInfo = new ShellExecuteInfo
            {
                cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
                hwnd = hwnd,
                lpVerb = "openas",
                lpFile = path,
                nShow = SwShow
            };
            if (!ShellExecuteEx(ref execInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SHOpenWithDialog", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);

    [Flags]
    private enum OPENASINFOFLAGS
    {
        OAIF_ALLOW_REGISTRATION = 0x00000001,
        OAIF_REGISTER_EXT       = 0x00000002,
        OAIF_EXEC               = 0x00000004,
        OAIF_FORCE_REGISTRATION = 0x00000008,
        OAIF_HIDE_REGISTRATION  = 0x00000020,
        OAIF_URL_PROTOCOL       = 0x00000040,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcszFile;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pcszClass;
        public OPENASINFOFLAGS oaifInFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo lpExecInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }
}
