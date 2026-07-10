using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FileKakari;

public sealed class ShellPreviewHost : HwndHost, IDisposable
{
    private readonly string _filePath;
    private readonly Guid _clsid;
    private IPreviewHandler? _previewHandler;
    private bool _isDisposed;

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    public ShellPreviewHost(string filePath, Guid clsid)
    {
        _filePath = filePath;
        _clsid = clsid;

        try
        {
            var comType = Type.GetTypeFromCLSID(_clsid, true);
            if (comType is null)
            {
                throw new InvalidOperationException($"Could not get type from CLSID {_clsid}");
            }

            var instance = Activator.CreateInstance(comType);
            _previewHandler = instance as IPreviewHandler;
            if (_previewHandler is null)
            {
                throw new InvalidOperationException("Instance does not implement IPreviewHandler");
            }

            if (_previewHandler is IInitializeWithFile fileInit)
            {
                // grfMode: STGM_READ = 0
                fileInit.Initialize(_filePath, 0);
            }
            else
            {
                throw new NotSupportedException("Preview Handler does not support IInitializeWithFile");
            }
        }
        catch
        {
            DisposePreviewHandler();
            throw;
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hwndChild = CreateWindowEx(
            0,
            "static",
            "",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0,
            0,
            (int)ActualWidth,
            (int)ActualHeight,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (hwndChild == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        if (_previewHandler is not null)
        {
            try
            {
                var rect = new RECT(0, 0, (int)ActualWidth, (int)ActualHeight);
                _previewHandler.SetWindow(hwndChild, ref rect);
                _previewHandler.DoPreview();
            }
            catch
            {
                DisposePreviewHandler();
                if (hwndChild != IntPtr.Zero)
                {
                    DestroyWindow(hwndChild);
                    hwndChild = IntPtr.Zero;
                }
                throw;
            }
        }

        return new HandleRef(this, hwndChild);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DisposePreviewHandler();
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }
    }

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_previewHandler is not null && !_isDisposed)
        {
            var rect = new RECT(0, 0, (int)ActualWidth, (int)ActualHeight);
            try
            {
                _previewHandler.SetRect(ref rect);
            }
            catch
            {
                // Ignore layout resize transient exceptions
            }
        }
    }

    private void DisposePreviewHandler()
    {
        if (_previewHandler is not null)
        {
            try
            {
                _previewHandler.Unload();
            }
            catch
            {
                // Ignore exceptions during unload
            }
            finally
            {
                Marshal.ReleaseComObject(_previewHandler);
                _previewHandler = null;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                DisposePreviewHandler();
            }
            _isDisposed = true;
        }
        base.Dispose(disposing);
    }

    // Win32 imports and structs
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    public interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig]
        int TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("b773d5a1-d859-11d4-850d-00902717b6bd")]
    public interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
