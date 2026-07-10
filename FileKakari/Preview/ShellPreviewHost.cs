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

    private static void LogDiag(string message)
    {
        PerfLog.Write($"[ShellPreviewHost] {message}");
    }

    public ShellPreviewHost(string filePath, Guid clsid)
    {
        _filePath = filePath;
        _clsid = clsid;

        LogDiag($"Begin constructor: path='{_filePath}', clsid='{_clsid:B}'");

        try
        {
            var comType = Type.GetTypeFromCLSID(_clsid, true);
            if (comType is null)
            {
                throw new InvalidOperationException($"Could not get type from CLSID {_clsid}");
            }
            LogDiag("COM Type resolution success.");

            var instance = Activator.CreateInstance(comType);
            LogDiag($"Activator.CreateInstance success. Instance type: {instance?.GetType().FullName ?? "null"}");

            _previewHandler = instance as IPreviewHandler;
            if (_previewHandler is null)
            {
                throw new InvalidOperationException("Instance does not implement IPreviewHandler");
            }
            LogDiag("Query IPreviewHandler success.");

            if (_previewHandler is IInitializeWithFile fileInit)
            {
                LogDiag("Query IInitializeWithFile success. Invoking Initialize...");
                fileInit.Initialize(_filePath, 0);
                LogDiag("IInitializeWithFile.Initialize success.");
            }
            else
            {
                throw new NotSupportedException("Preview Handler does not support IInitializeWithFile");
            }
        }
        catch (Exception ex)
        {
            LogDiag($"Constructor exception: Type={ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}, Msg='{ex.Message}'");
            DisposePreviewHandler();
            throw;
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        LogDiag($"BuildWindowCore start: parent HWND=0x{hwndParent.Handle.ToInt64():X}, size={ActualWidth}x{ActualHeight}");

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
            var err = Marshal.GetLastWin32Error();
            LogDiag($"CreateWindowEx failed: Win32Error={err}");
            throw new System.ComponentModel.Win32Exception(err);
        }
        LogDiag($"CreateWindowEx success: child HWND=0x{hwndChild.ToInt64():X}");

        if (_previewHandler is not null)
        {
            try
            {
                var rect = new RECT(0, 0, (int)ActualWidth, (int)ActualHeight);
                LogDiag($"Invoking IPreviewHandler.SetWindow: child HWND=0x{hwndChild.ToInt64():X}, rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");
                _previewHandler.SetWindow(hwndChild, ref rect);
                LogDiag("IPreviewHandler.SetWindow success.");

                LogDiag("Invoking IPreviewHandler.DoPreview...");
                _previewHandler.DoPreview();
                LogDiag("IPreviewHandler.DoPreview success.");
            }
            catch (Exception ex)
            {
                LogDiag($"BuildWindowCore/DoPreview exception: Type={ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}, Msg='{ex.Message}'");
                DisposePreviewHandler();
                if (hwndChild != IntPtr.Zero)
                {
                    DestroyWindow(hwndChild);
                    hwndChild = IntPtr.Zero;
                }
                throw;
            }
        }
        else
        {
            LogDiag("Warning: BuildWindowCore run but _previewHandler is null.");
        }

        return new HandleRef(this, hwndChild);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        LogDiag($"DestroyWindowCore start: HWND=0x{hwnd.Handle.ToInt64():X}");
        DisposePreviewHandler();
        if (hwnd.Handle != IntPtr.Zero)
        {
            var success = DestroyWindow(hwnd.Handle);
            LogDiag($"DestroyWindow success={success}");
        }
    }

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_previewHandler is not null && !_isDisposed)
        {
            var rect = new RECT(0, 0, (int)ActualWidth, (int)ActualHeight);
            LogDiag($"OnRenderSizeChanged: New size={ActualWidth}x{ActualHeight}, invoking SetRect...");
            try
            {
                _previewHandler.SetRect(ref rect);
                LogDiag("IPreviewHandler.SetRect success.");
            }
            catch (Exception ex)
            {
                LogDiag($"IPreviewHandler.SetRect exception (ignored): Type={ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}, Msg='{ex.Message}'");
            }
        }
    }

    private void DisposePreviewHandler()
    {
        if (_previewHandler is not null)
        {
            LogDiag("DisposePreviewHandler starting...");
            try
            {
                LogDiag("Invoking IPreviewHandler.Unload...");
                _previewHandler.Unload();
                LogDiag("IPreviewHandler.Unload success.");
            }
            catch (Exception ex)
            {
                LogDiag($"IPreviewHandler.Unload exception: Type={ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}, Msg='{ex.Message}'");
            }
            finally
            {
                var refCount = Marshal.ReleaseComObject(_previewHandler);
                _previewHandler = null;
                LogDiag($"ReleaseComObject done. Remaining RefCount={refCount}");
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        LogDiag($"Dispose called: disposing={disposing}, _isDisposed={_isDisposed}");
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
