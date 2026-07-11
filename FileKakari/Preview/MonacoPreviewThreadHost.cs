using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Diagnostics;
using System.Collections.Generic;

namespace FileKakari;

public sealed class MonacoPreviewThreadHost : IDisposable
{
    private static void LogDiag(string message)
    {
        PerfLog.Write($"[MonacoPreviewThreadHost] {message}");
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public Win32WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr Win32WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        else return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    private interface IPreviewHandler
    {
        [PreserveSig] int SetWindow(IntPtr hwnd, ref RECT rect);
        [PreserveSig] int SetRect(ref RECT rect);
        [PreserveSig] int DoPreview();
        [PreserveSig] int Unload();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
    private interface IInitializeWithFile
    {
        [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int GWL_STYLE = -16;
    private const int SW_SHOW = 5;

    private const int WM_USER = 0x0400;
    private const int WM_MONACO_OPEN = WM_USER + 101;
    private const int WM_MONACO_RESIZE = WM_USER + 102;
    private const int WM_MONACO_SET_PARENT = WM_USER + 104;
    private const int WM_MONACO_SHUTDOWN = WM_USER + 105;

    private static readonly Guid MonacoPreviewHandlerClsid = new("D8034CFA-F34B-41FE-AD45-62FCBB52A6DA");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private const uint CLSCTX_LOCAL_SERVER = 4;

    private static readonly Win32WndProc DefaultWndProcInstance = MonacoWndProc;
    private static bool _isClassRegistered;
    private static readonly object ClassRegisterLock = new();

    private static void EnsureClassRegisteredDedicated()
    {
        lock (ClassRegisterLock)
        {
            if (_isClassRegistered) return;

            var wndClass = new WNDCLASSEX();
            wndClass.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wndClass.style = 0;
            wndClass.lpfnWndProc = DefaultWndProcInstance;
            wndClass.cbClsExtra = 0;
            wndClass.cbWndExtra = 0;
            wndClass.hInstance = GetModuleHandle(null);
            wndClass.hIcon = IntPtr.Zero;
            wndClass.hCursor = IntPtr.Zero;
            wndClass.hbrBackground = IntPtr.Zero;
            wndClass.lpszMenuName = null!;
            wndClass.lpszClassName = "FileKakariShellPreviewHost";
            wndClass.hIconSm = IntPtr.Zero;

            ushort regResult = RegisterClassEx(ref wndClass);
            if (regResult == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
                {
                    LogDiag($"RegisterClassEx failed with error: {err}");
                    throw new System.ComponentModel.Win32Exception(err);
                }
            }
            _isClassRegistered = true;
        }
    }

    private Thread? _dedicatedThread;
    private IntPtr _dedicatedHwnd = IntPtr.Zero;
    private readonly AutoResetEvent _threadReadyEvent = new(false);
    private IPreviewHandler? _dedicatedPreviewHandler;
    private bool _isDisposed;
    private string _currentFilePath = "";
    private bool _activationSuccess;

    public IntPtr HostHwnd => _dedicatedHwnd;

    public MonacoPreviewThreadHost()
    {
    }

    public bool Start(int timeoutMs = 2000)
    {
        _dedicatedThread = new Thread(DedicatedThreadProc);
        _dedicatedThread.SetApartmentState(ApartmentState.STA);
        _dedicatedThread.IsBackground = true;
        _dedicatedThread.Start();

        bool ready = _threadReadyEvent.WaitOne(timeoutMs);
        if (!ready)
        {
            LogDiag("Timeout waiting for dedicated thread startup.");
            return false;
        }
        return _dedicatedHwnd != IntPtr.Zero;
    }

    private void DedicatedThreadProc()
    {
        try
        {
            EnsureClassRegisteredDedicated();

            _dedicatedHwnd = CreateWindowEx(
                0,
                "FileKakariShellPreviewHost",
                "MonacoDedicatedHost",
                0, 
                0, 0, 100, 100,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

            if (_dedicatedHwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LogDiag($"CreateWindowEx failed: {err}");
                _threadReadyEvent.Set();
                return;
            }

            LogDiag($"Created host HWND=0x{_dedicatedHwnd.ToInt64():X}");
            
            SetWindowProperty(this);

            _threadReadyEvent.Set();

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            LogDiag($"Thread exception: {ex.Message}");
            _threadReadyEvent.Set();
        }
        finally
        {
            RemoveWindowProperty();
            _dedicatedHwnd = IntPtr.Zero;
            LogDiag("Thread stopped.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);

    private void SetWindowProperty(MonacoPreviewThreadHost host)
    {
        GCHandle handle = GCHandle.Alloc(host, GCHandleType.Weak);
        SetProp(_dedicatedHwnd, "MonacoHostPtr", GCHandle.ToIntPtr(handle));
    }

    private void RemoveWindowProperty()
    {
        if (_dedicatedHwnd != IntPtr.Zero)
        {
            IntPtr ptr = RemoveProp(_dedicatedHwnd, "MonacoHostPtr");
            if (ptr != IntPtr.Zero)
            {
                GCHandle handle = GCHandle.FromIntPtr(ptr);
                if (handle.IsAllocated) handle.Free();
            }
        }
    }

    private static MonacoPreviewThreadHost? GetHostFromHwnd(IntPtr hwnd)
    {
        IntPtr ptr = GetProp(hwnd, "MonacoHostPtr");
        if (ptr == IntPtr.Zero) return null;
        GCHandle handle = GCHandle.FromIntPtr(ptr);
        return handle.Target as MonacoPreviewThreadHost;
    }

    private static IntPtr MonacoWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var host = GetHostFromHwnd(hWnd);
        if (host != null)
        {
            switch (msg)
            {
                case WM_MONACO_SET_PARENT:
                    {
                        IntPtr parentHwnd = wParam;
                        SetParent(hWnd, parentHwnd);
                        
                        const int styleVal = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
                        SetWindowLongPtr(hWnd, GWL_STYLE, new IntPtr(styleVal));
                        ShowWindow(hWnd, SW_SHOW);
                        return IntPtr.Zero;
                    }
                case WM_MONACO_OPEN:
                    {
                        string? path = Marshal.PtrToStringUni(lParam);
                        if (path != null)
                        {
                            host.InitializeAndShowMonaco(path);
                        }
                        Marshal.FreeHGlobal(lParam);
                        return IntPtr.Zero;
                    }
                case WM_MONACO_RESIZE:
                    {
                        int w = wParam.ToInt32();
                        int h = lParam.ToInt32();
                        host.ResizeMonaco(w, h);
                        return IntPtr.Zero;
                    }
                case WM_MONACO_SHUTDOWN:
                    {
                        host.CloseMonaco();
                        DestroyWindow(hWnd);
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                    }
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void InitializeAndShowMonaco(string path)
    {
        CloseMonaco();
        _currentFilePath = path;
        _activationSuccess = false;

        try
        {
            LogDiag($"Activating Monaco for path='{path}' hwnd=0x{_dedicatedHwnd.ToInt64():X}");
            
            IntPtr pUnk = IntPtr.Zero;
            int hr = CoCreateInstance(in MonacoPreviewHandlerClsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, in IID_IUnknown, out pUnk);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            
            var instance = Marshal.GetObjectForIUnknown(pUnk);
            Marshal.Release(pUnk);

            _dedicatedPreviewHandler = instance as IPreviewHandler;
            if (_dedicatedPreviewHandler == null)
            {
                throw new InvalidOperationException("Monaco instance does not implement IPreviewHandler");
            }

            if (_dedicatedPreviewHandler is IInitializeWithFile fileInit)
            {
                hr = fileInit.Initialize(path, 0);
                LogDiag($"IInitializeWithFile.Initialize HRESULT=0x{hr:X8}");
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
            else
            {
                throw new InvalidOperationException("Monaco does not implement IInitializeWithFile");
            }

            RECT rect;
            GetClientRect(_dedicatedHwnd, out rect);
            hr = _dedicatedPreviewHandler.SetWindow(_dedicatedHwnd, ref rect);
            LogDiag($"IPreviewHandler.SetWindow HRESULT=0x{hr:X8}");
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            hr = _dedicatedPreviewHandler.DoPreview();
            LogDiag($"IPreviewHandler.DoPreview HRESULT=0x{hr:X8}");
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            LogDiag("Monaco DoPreview successful.");
            _activationSuccess = true;
        }
        catch (Exception ex)
        {
            LogDiag($"Monaco activation failed: {ex.Message}");
            CloseMonaco();
        }
    }

    private void CloseMonaco()
    {
        if (_dedicatedPreviewHandler != null)
        {
            try
            {
                LogDiag("Unloading Monaco Preview Handler...");
                _dedicatedPreviewHandler.Unload();
            }
            catch (Exception ex)
            {
                LogDiag($"Unload exception: {ex.Message}");
            }
            finally
            {
                Marshal.ReleaseComObject(_dedicatedPreviewHandler);
                _dedicatedPreviewHandler = null;
            }
        }
        _activationSuccess = false;
    }

    private void ResizeMonaco(int w, int h)
    {
        if (_dedicatedPreviewHandler != null)
        {
            var rect = new RECT(0, 0, w, h);
            int hr = _dedicatedPreviewHandler.SetRect(ref rect);
            LogDiag($"SetRect size={w}x{h} HRESULT=0x{hr:X8}");
        }
    }

    public bool IsActivated()
    {
        return _activationSuccess;
    }

    public void SetParentWindow(IntPtr parent)
    {
        if (_dedicatedHwnd != IntPtr.Zero)
        {
            PostMessage(_dedicatedHwnd, WM_MONACO_SET_PARENT, parent, IntPtr.Zero);
        }
    }

    public void Resize(int width, int height)
    {
        if (_dedicatedHwnd != IntPtr.Zero)
        {
            PostMessage(_dedicatedHwnd, WM_MONACO_RESIZE, new IntPtr(width), new IntPtr(height));
        }
    }

    public void Open(string path)
    {
        if (_dedicatedHwnd != IntPtr.Zero)
        {
            var pathPtr = Marshal.StringToHGlobalUni(path);
            PostMessage(_dedicatedHwnd, WM_MONACO_OPEN, IntPtr.Zero, pathPtr);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_dedicatedHwnd != IntPtr.Zero)
        {
            LogDiag("Triggering dedicated thread shutdown...");
            PostMessage(_dedicatedHwnd, WM_MONACO_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
        }

        if (_dedicatedThread != null && _dedicatedThread.IsAlive)
        {
            if (!_dedicatedThread.Join(500))
            {
                LogDiag("Warning: Dedicated thread did not exit within timeout. Abandoning.");
            }
        }
        _dedicatedThread = null;
    }
}
