using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;

namespace FileKakari;

public sealed class ShellPreviewHost : HwndHost, IDisposable
{
    private readonly string _filePath;
    private readonly Guid _clsid;
    private readonly bool _isDeferredHandler;
    private IPreviewHandler? _previewHandler;
    private FileStream? _fileStream;
    private ManagedIStream? _managedIStream;
    private IShellItem? _shellItem;
    private bool _isDisposed;
    private IntPtr _childHwnd = IntPtr.Zero;
    private bool _isDoPreviewCalled;
    private bool _pendingDoPreviewUntilNonZeroSize;

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
        _isDeferredHandler = _clsid == new Guid("D8034CFA-F34B-41FE-AD45-62FCBB52A6DA") ||
                             _clsid == new Guid("60789D87-9C3C-44AF-B18C-3DE2C2820ED3");

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
            if (instance is null)
            {
                throw new InvalidOperationException("Failed to create COM instance");
            }
            LogDiag($"Activator.CreateInstance success. Instance type: {instance.GetType().FullName}");

            _previewHandler = instance as IPreviewHandler;
            if (_previewHandler is null)
            {
                throw new InvalidOperationException("Instance does not implement IPreviewHandler");
            }
            LogDiag("Query IPreviewHandler success.");

            // Run QueryInterface audit for diagnostics
            IntPtr pUnk = IntPtr.Zero;
            try
            {
                pUnk = Marshal.GetIUnknownForObject(instance);
                if (pUnk != IntPtr.Zero)
                {
                    var iidFile = new Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B");
                    int hrFile = Marshal.QueryInterface(pUnk, in iidFile, out IntPtr ppvFile);
                    LogDiag($"QueryInterface IInitializeWithFile IID={iidFile:B} HRESULT=0x{hrFile:X8}");
                    if (hrFile == 0 && ppvFile != IntPtr.Zero)
                    {
                        Marshal.Release(ppvFile);
                    }

                    var iidStream = new Guid("B824B643-2222-4A0E-AC22-D49149F10062");
                    int hrStream = Marshal.QueryInterface(pUnk, in iidStream, out IntPtr ppvStream);
                    LogDiag($"QueryInterface IInitializeWithStream IID={iidStream:B} HRESULT=0x{hrStream:X8}");
                    if (hrStream == 0 && ppvStream != IntPtr.Zero)
                    {
                        Marshal.Release(ppvStream);
                    }

                    var iidItem = new Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841");
                    int hrItem = Marshal.QueryInterface(pUnk, in iidItem, out IntPtr ppvItem);
                    LogDiag($"QueryInterface IInitializeWithItem IID={iidItem:B} HRESULT=0x{hrItem:X8}");
                    if (hrItem == 0 && ppvItem != IntPtr.Zero)
                    {
                        Marshal.Release(ppvItem);
                    }
                }
            }
            catch (Exception qie)
            {
                LogDiag($"QueryInterface diagnostics exception: {qie.Message}");
            }
            finally
            {
                if (pUnk != IntPtr.Zero)
                {
                    Marshal.Release(pUnk);
                }
            }

            bool initialized = false;

            if (_previewHandler is IInitializeWithFile fileInit)
            {
                LogDiag("Query IInitializeWithFile success. Invoking Initialize...");
                int hr = fileInit.Initialize(_filePath, 0);
                LogDiag($"[ShellPreviewHost] IInitializeWithFile.Initialize HRESULT=0x{hr:X8}");
                if (hr == 0)
                {
                    initialized = true;
                    LogDiag("[ShellPreviewHost] Initialization selected method=File");
                }
                else
                {
                    LogDiag("[ShellPreviewHost] InitializeWithFile failed; trying stream initializer.");
                }
            }

            if (!initialized && _previewHandler is IInitializeWithStream streamInit)
            {
                LogDiag("Query IInitializeWithStream success. Opening stream...");
                try
                {
                    _fileStream = new FileStream(
                        _filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    _managedIStream = new ManagedIStream(_fileStream);
                    LogDiag($"Stream opened path='{_filePath}' length={_fileStream.Length}. Invoking Initialize...");

                    int hr = streamInit.Initialize(_managedIStream, 0);
                    LogDiag($"[ShellPreviewHost] IInitializeWithStream.Initialize HRESULT=0x{hr:X8}");
                    if (hr == 0)
                    {
                        initialized = true;
                        LogDiag("[ShellPreviewHost] Initialization selected method=Stream");
                    }
                    else
                    {
                        LogDiag("[ShellPreviewHost] InitializeWithStream failed; trying item initializer.");
                        _fileStream.Dispose();
                        _fileStream = null;
                        _managedIStream = null;
                    }
                }
                catch (Exception streamEx)
                {
                    LogDiag($"IInitializeWithStream preparation failed: {streamEx.Message}");
                    if (_fileStream is not null)
                    {
                        _fileStream.Dispose();
                        _fileStream = null;
                    }
                    _managedIStream = null;
                }
            }

            if (!initialized && _previewHandler is IInitializeWithItem itemInit)
            {
                LogDiag("Query IInitializeWithItem success. Creating ShellItem...");
                try
                {
                    var shellItemGuid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    LogDiag($"SHCreateItemFromParsingName start path='{_filePath}'");
                    int hrItemCreate = SHCreateItemFromParsingName(_filePath, IntPtr.Zero, ref shellItemGuid, out _shellItem);

                    if (hrItemCreate >= 0 && _shellItem is not null)
                    {
                        LogDiag($"SHCreateItemFromParsingName success HRESULT=0x{hrItemCreate:X8}. Invoking Initialize...");
                        int hr = itemInit.Initialize(_shellItem, 0);
                        LogDiag($"[ShellPreviewHost] IInitializeWithItem.Initialize HRESULT=0x{hr:X8}");
                        if (hr == 0)
                        {
                            initialized = true;
                            LogDiag("[ShellPreviewHost] Initialization selected method=Item");
                        }
                        else
                        {
                            LogDiag("[ShellPreviewHost] InitializeWithItem failed.");
                            Marshal.ReleaseComObject(_shellItem);
                            _shellItem = null;
                        }
                    }
                    else
                    {
                        LogDiag($"SHCreateItemFromParsingName fail HRESULT=0x{hrItemCreate:X8}");
                    }
                }
                catch (Exception itemEx)
                {
                    LogDiag($"IInitializeWithItem preparation failed: {itemEx.Message}");
                    if (_shellItem is not null)
                    {
                        Marshal.ReleaseComObject(_shellItem);
                        _shellItem = null;
                    }
                }
            }

            if (!initialized)
            {
                throw new NotSupportedException("Preview Handler does not support or failed to initialize with IInitializeWithFile, IInitializeWithStream, or IInitializeWithItem");
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
        double scaleX = 1.0;
        double scaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            scaleX = source.CompositionTarget.TransformToDevice.M11;
            scaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        var pixelWidth = Math.Max(0, (int)Math.Round(ActualWidth * scaleX));
        var pixelHeight = Math.Max(0, (int)Math.Round(ActualHeight * scaleY));

        LogDiag($"BuildWindowCore start: parent HWND=0x{hwndParent.Handle.ToInt64():X}, scale={scaleX}x{scaleY}, size={ActualWidth}x{ActualHeight} -> pixels={pixelWidth}x{pixelHeight}");
        LogDiag($"BuildWindowCore initial size={pixelWidth}x{pixelHeight}");

        if (_isDeferredHandler)
        {
            string profile = _clsid == new Guid("D8034CFA-F34B-41FE-AD45-62FCBB52A6DA") ? "PowerToysMonaco" : "PowerToysMarkdown";
            LogDiag($"Handler profile: {profile} clsid=\"{_clsid:B}\"");
        }

        var hwndChild = CreateWindowEx(
            0,
            "static",
            "",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0,
            0,
            pixelWidth,
            pixelHeight,
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
        _childHwnd = hwndChild;

        if (_isDeferredHandler)
        {
            var showRes = ShowWindow(hwndChild, SW_SHOW);
            LogDiag($"ShowWindow child HWND=0x{hwndChild.ToInt64():X} result={showRes}");
        }

        LogChildWindowState("after-create");
        LogParentContainerState();

        if (_previewHandler is not null)
        {
            if (_isDeferredHandler && pixelWidth == 0 && pixelHeight == 0)
            {
                string profile = _clsid == new Guid("D8034CFA-F34B-41FE-AD45-62FCBB52A6DA") ? "PowerToys Monaco" : "PowerToys Markdown";
                LogDiag($"Deferring DoPreview until non-zero size for {profile}.");
                _pendingDoPreviewUntilNonZeroSize = true;
            }
            else
            {
                try
                {
                    var rect = new RECT(0, 0, pixelWidth, pixelHeight);
                    LogDiag($"Invoking IPreviewHandler.SetWindow: child HWND=0x{hwndChild.ToInt64():X}, rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");
                    _previewHandler.SetWindow(hwndChild, ref rect);
                    LogDiag("IPreviewHandler.SetWindow success.");

                    LogDiag("Invoking IPreviewHandler.DoPreview...");
                    _previewHandler.DoPreview();
                    LogDiag("IPreviewHandler.DoPreview success.");
                    _isDoPreviewCalled = true;

                    LogChildWindowState("after-do-preview");
                }
                catch (Exception ex)
                {
                    LogDiag($"BuildWindowCore/DoPreview exception: Type={ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}, Msg='{ex.Message}'");
                    DisposePreviewHandler();
                    if (hwndChild != IntPtr.Zero)
                    {
                        DestroyWindow(hwndChild);
                        _childHwnd = IntPtr.Zero;
                        hwndChild = IntPtr.Zero;
                    }
                    throw;
                }
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
        _childHwnd = IntPtr.Zero;
        if (hwnd.Handle != IntPtr.Zero)
        {
            var success = DestroyWindow(hwnd.Handle);
            LogDiag($"DestroyWindow success={success}");
        }
    }

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_isDisposed)
        {
            ResizePreviewHost(ActualWidth, ActualHeight);
        }
    }

    private void ResizePreviewHost(double width, double height)
    {
        double scaleX = 1.0;
        double scaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            scaleX = source.CompositionTarget.TransformToDevice.M11;
            scaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        var pixelWidth = Math.Max(0, (int)Math.Round(width * scaleX));
        var pixelHeight = Math.Max(0, (int)Math.Round(height * scaleY));

        LogDiag($"ResizePreviewHost: width={width} height={height} scale={scaleX}x{scaleY} -> pixels={pixelWidth}x{pixelHeight}");

        if (_childHwnd != IntPtr.Zero)
        {
            var success = MoveWindow(_childHwnd, 0, 0, pixelWidth, pixelHeight, true);
            LogDiag($"MoveWindow child HWND=0x{_childHwnd.ToInt64():X} size={pixelWidth}x{pixelHeight} success={success}");

            if (_isDeferredHandler)
            {
                var showRes = ShowWindow(_childHwnd, SW_SHOW);
                LogDiag($"ShowWindow (after-move) child HWND=0x{_childHwnd.ToInt64():X} result={showRes}");

                var swpRes = SetWindowPos(
                    _childHwnd,
                    HWND_TOP,
                    0,
                    0,
                    pixelWidth,
                    pixelHeight,
                    SWP_SHOWWINDOW);
                LogDiag($"SetWindowPos child HWND=0x{_childHwnd.ToInt64():X} result={swpRes}");
            }

            LogChildWindowState("after-move");
            LogParentContainerState();
        }

        if (_previewHandler is not null)
        {
            if (_pendingDoPreviewUntilNonZeroSize && pixelWidth > 0 && pixelHeight > 0)
            {
                LogDiag($"Running deferred DoPreview size={pixelWidth}x{pixelHeight}.");
                try
                {
                    var rect = new RECT(0, 0, pixelWidth, pixelHeight);

                    LogDiag($"Deferred SetWindow child HWND=0x{_childHwnd.ToInt64():X}, rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");
                    _previewHandler.SetWindow(_childHwnd, ref rect);
                    LogDiag("Deferred SetWindow success.");

                    if (_isDeferredHandler)
                    {
                        var showRes = ShowWindow(_childHwnd, SW_SHOW);
                        LogDiag($"ShowWindow (before-deferred-do-preview) child HWND=0x{_childHwnd.ToInt64():X} result={showRes}");
                    }

                    LogDiag("Invoking deferred DoPreview...");
                    _previewHandler.DoPreview();
                    LogDiag("Deferred DoPreview success.");
                    _isDoPreviewCalled = true;
                    _pendingDoPreviewUntilNonZeroSize = false;

                    if (_isDeferredHandler)
                    {
                        var showRes = ShowWindow(_childHwnd, SW_SHOW);
                        LogDiag($"ShowWindow (after-deferred-do-preview) child HWND=0x{_childHwnd.ToInt64():X} result={showRes}");
                    }

                    LogChildWindowState("after-deferred-do-preview");
                }
                catch (Exception ex)
                {
                    LogDiag($"Deferred DoPreview failed HRESULT=0x{ex.HResult:X8} exception: {ex.Message}");
                    DisposePreviewHandler();
                }
            }
            else if (_isDoPreviewCalled)
            {
                var rect = new RECT(0, 0, pixelWidth, pixelHeight);
                try
                {
                    _previewHandler.SetRect(ref rect);
                    LogDiag($"SetRect rect=(0,0,{pixelWidth},{pixelHeight}) success");
                }
                catch (Exception ex)
                {
                    LogDiag($"SetRect exception: {ex.Message}");
                }
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

        if (_shellItem is not null)
        {
            LogDiag("Release IShellItem starting...");
            try
            {
                var refCount = Marshal.ReleaseComObject(_shellItem);
                LogDiag($"Release IShellItem success. Remaining RefCount={refCount}");
            }
            catch (Exception ex)
            {
                LogDiag($"Release IShellItem fail: {ex.Message}");
            }
            finally
            {
                _shellItem = null;
            }
        }

        if (_fileStream is not null)
        {
            try
            {
                _fileStream.Dispose();
                LogDiag("Stream disposed");
            }
            catch (Exception ex)
            {
                LogDiag($"Stream dispose exception: {ex.Message}");
            }
            finally
            {
                _fileStream = null;
                _managedIStream = null;
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

    // Custom Managed IStream implementation wrapping FileStream
    private sealed class ManagedIStream : IStream
    {
        private readonly FileStream _fileStream;

        public ManagedIStream(FileStream fileStream)
        {
            _fileStream = fileStream ?? throw new ArgumentNullException(nameof(fileStream));
        }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int bytesRead = _fileStream.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero)
            {
                Marshal.WriteInt32(pcbRead, bytesRead);
            }
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten)
        {
            _fileStream.Write(pv, 0, cb);
            if (pcbWritten != IntPtr.Zero)
            {
                Marshal.WriteInt32(pcbWritten, cb);
            }
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            var origin = (SeekOrigin)dwOrigin;
            long newPos = _fileStream.Seek(dlibMove, origin);
            if (plibNewPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(plibNewPosition, newPos);
            }
        }

        public void SetSize(long libNewSize)
        {
            _fileStream.SetLength(libNewSize);
        }

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            throw new COMException("CopyTo is not implemented.", unchecked((int)0x80004001)); // E_NOTIMPL
        }

        public void Commit(int grfCommitFlags)
        {
            _fileStream.Flush();
        }

        public void Revert()
        {
            throw new COMException("Revert is not implemented.", unchecked((int)0x80004001)); // E_NOTIMPL
        }

        public void LockRegion(long libOffset, long cb, int dwLockType)
        {
            throw new COMException("LockRegion is not implemented.", unchecked((int)0x80004001)); // E_NOTIMPL
        }

        public void UnlockRegion(long libOffset, long cb, int dwLockType)
        {
            throw new COMException("UnlockRegion is not implemented.", unchecked((int)0x80004001)); // E_NOTIMPL
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG
            {
                type = 2, // STGTY_STREAM
                cbSize = _fileStream.Length,
                grfMode = 0 // STGM_READ
            };
        }

        public void Clone(out IStream ppstm)
        {
            throw new COMException("Clone is not implemented.", unchecked((int)0x80004001)); // E_NOTIMPL
        }
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
    [Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
    public interface IInitializeWithFile
    {
        [PreserveSig]
        int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("b824b643-2222-4a0e-ac22-d49149f10062")]
    public interface IInitializeWithStream
    {
        [PreserveSig]
        int Initialize(IStream stream, uint grfMode);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    public interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841")]
    public interface IInitializeWithItem
    {
        [PreserveSig]
        int Initialize([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi, [In] uint grfMode);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(
        IntPtr hWnd,
        int X,
        int Y,
        int nWidth,
        int nHeight,
        bool bRepaint);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8)
        {
            return GetWindowLongPtr64(hWnd, nIndex);
        }
        else
        {
            return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int SW_SHOW = 5;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOP = new IntPtr(0);

    private void LogChildWindowState(string stage)
    {
        if (_childHwnd == IntPtr.Zero)
        {
            LogDiag($"ChildWindowState stage=\"{stage}\" child HWND is Zero");
            return;
        }

        bool visible = IsWindowVisible(_childHwnd);
        RECT rect = default;
        RECT client = default;
        GetWindowRect(_childHwnd, out rect);
        GetClientRect(_childHwnd, out client);
        IntPtr style = GetWindowLongPtr(_childHwnd, GWL_STYLE);
        IntPtr exStyle = GetWindowLongPtr(_childHwnd, GWL_EXSTYLE);

        LogDiag($"ChildWindowState stage=\"{stage}\" visible={visible} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) client=({client.Left},{client.Top},{client.Right},{client.Bottom}) style=0x{style.ToInt64():X8} exStyle=0x{exStyle.ToInt64():X8}");
    }

    private void LogParentContainerState()
    {
        try
        {
            var parent = this.Parent as FrameworkElement;
            if (parent is not null)
            {
                LogDiag($"ParentContainer name=\"{parent.Name}\" type=\"{parent.GetType().FullName}\" visibility={parent.Visibility} size={parent.ActualWidth}x{parent.ActualHeight} opacity={parent.Opacity} isVisible={parent.IsVisible}");
            }
            else
            {
                LogDiag("ParentContainer is null or not FrameworkElement");
            }
            LogDiag($"ShellPreviewHost visibility={this.Visibility} size={this.ActualWidth}x{this.ActualHeight} opacity={this.Opacity} isVisible={this.IsVisible}");
        }
        catch (Exception ex)
        {
            LogDiag($"LogParentContainerState exception: {ex.Message}");
        }
    }
}
