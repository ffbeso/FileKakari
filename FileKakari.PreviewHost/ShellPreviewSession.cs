using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace FileKakari.PreviewHost;

public sealed class ShellPreviewSession : IDisposable
{
    private NativeMethods.IPreviewHandler? _currentHandler;
    private object? _comObject;
    private string _currentFilePath = "";
    private string _currentClsid = "";

    public bool HasActiveSession => _currentHandler is not null;
    public string CurrentFilePath => _currentFilePath;
    public string CurrentClsid => _currentClsid;

    public struct LoadResult
    {
        public bool Success;
        public string Stage;
        public int ErrorCode;
        public string ErrorMessage;
        public long ElapsedMs;
    }

    public LoadResult Load(string requestId, string filePath, string clsidString, IntPtr hostHwnd, int widthPx, int heightPx)
    {
        var totalSw = Stopwatch.StartNew();
        var threadId = Environment.CurrentManagedThreadId;
        var win32ThreadId = NativeMethods.GetCurrentThreadId();

        HostPerfLog.Write($"[Session.Load START] req=\"{requestId}\" threadId={threadId} win32ThreadId={win32ThreadId} path='{filePath}' clsid='{clsidString}'");

        // 1. Validate inputs
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            HostPerfLog.Write($"[Session.Load FAILED] req=\"{requestId}\" stage=ValidateInput msg='File not found: {filePath}'");
            return new LoadResult
            {
                Success = false,
                Stage = "ValidateInput",
                ErrorCode = -1,
                ErrorMessage = $"File not found or invalid path: '{filePath}'",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        if (!Guid.TryParse(clsidString, out var clsid))
        {
            HostPerfLog.Write($"[Session.Load FAILED] req=\"{requestId}\" stage=ValidateInput msg='Invalid CLSID: {clsidString}'");
            return new LoadResult
            {
                Success = false,
                Stage = "ValidateInput",
                ErrorCode = -2,
                ErrorMessage = $"Invalid CLSID string: '{clsidString}'",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        // 2. Unload previous preview session
        Unload(requestId);

        _currentFilePath = filePath;
        _currentClsid = clsidString;

        // 3. Resolve Class
        Type? handlerType = null;
        try
        {
            handlerType = Type.GetTypeFromCLSID(clsid, throwOnError: true);
        }
        catch (Exception ex)
        {
            HostPerfLog.Write($"[Session.Load FAILED] req=\"{requestId}\" stage=ResolveClass HRESULT=0x{ex.HResult:X8} msg='{ex.Message}'");
            return new LoadResult
            {
                Success = false,
                Stage = "ResolveClass",
                ErrorCode = ex.HResult,
                ErrorMessage = $"GetTypeFromCLSID failed: {ex.Message}",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        if (handlerType is null)
        {
            HostPerfLog.Write($"[Session.Load FAILED] req=\"{requestId}\" stage=ResolveClass msg='GetTypeFromCLSID returned null'");
            return new LoadResult
            {
                Success = false,
                Stage = "ResolveClass",
                ErrorCode = -3,
                ErrorMessage = "GetTypeFromCLSID returned null.",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        // 4. CreateInstance (START / END)
        var swStage = Stopwatch.StartNew();
        HostPerfLog.Write($"[Session.CreateInstance START] req=\"{requestId}\" clsid='{clsidString}' threadId={threadId}");
        try
        {
            _comObject = Activator.CreateInstance(handlerType);
            swStage.Stop();
            HostPerfLog.Write($"[Session.CreateInstance END] req=\"{requestId}\" elapsedMs={swStage.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            swStage.Stop();
            HostPerfLog.Write($"[Session.CreateInstance FAILED] req=\"{requestId}\" elapsedMs={swStage.ElapsedMilliseconds} HRESULT=0x{ex.HResult:X8} msg='{ex.Message}'");
            Unload(requestId);
            return new LoadResult
            {
                Success = false,
                Stage = "CreateInstance",
                ErrorCode = ex.HResult,
                ErrorMessage = $"Activator.CreateInstance failed: {ex.Message}",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        if (_comObject is not NativeMethods.IPreviewHandler handler)
        {
            HostPerfLog.Write($"[Session.CreateInstance FAILED] req=\"{requestId}\" msg='COM object does not implement IPreviewHandler'");
            Unload(requestId);
            return new LoadResult
            {
                Success = false,
                Stage = "CreateInstance",
                ErrorCode = -4,
                ErrorMessage = "COM object does not implement IPreviewHandler.",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        _currentHandler = handler;

        // 5. Initialize Handler (START / END)
        swStage.Restart();
        var initSuccess = false;
        string initStage = "InitializeWithFile";
        int initHr = 0;

        HostPerfLog.Write($"[Session.Initialize START] req=\"{requestId}\" threadId={threadId}");

        if (_comObject is NativeMethods.IInitializeWithFile fileInit)
        {
            HostPerfLog.Write($"[Session.InitializeWithFile START] req=\"{requestId}\" path='{filePath}'");
            initHr = fileInit.Initialize(filePath, 0); // STGM_READ = 0
            if (initHr == 0)
            {
                initSuccess = true;
            }
            HostPerfLog.Write($"[Session.InitializeWithFile END] req=\"{requestId}\" hr=0x{initHr:X8} success={initSuccess}");
        }

        if (!initSuccess && _comObject is NativeMethods.IInitializeWithStream streamInit)
        {
            initStage = "InitializeWithStream";
            HostPerfLog.Write($"[Session.InitializeWithStream START] req=\"{requestId}\" path='{filePath}'");
            IStream? stream = null;
            try
            {
                stream = SHCreateStreamOnFile(filePath, 0); // STGM_READ = 0
                if (stream is not null)
                {
                    initHr = streamInit.Initialize(stream, 0);
                    if (initHr == 0)
                    {
                        initSuccess = true;
                    }
                }
            }
            catch (Exception ex)
            {
                HostPerfLog.Write($"[Session.InitializeWithStream EXCEPTION] req=\"{requestId}\" msg='{ex.Message}'");
            }
            finally
            {
                if (stream is not null && Marshal.IsComObject(stream))
                {
                    try
                    {
                        Marshal.FinalReleaseComObject(stream);
                    }
                    catch (Exception ex)
                    {
                        HostPerfLog.Write($"[Session.StreamRelease EXCEPTION] req=\"{requestId}\" msg='{ex.Message}'");
                    }
                }
            }
            HostPerfLog.Write($"[Session.InitializeWithStream END] req=\"{requestId}\" hr=0x{initHr:X8} success={initSuccess}");
        }

        swStage.Stop();
        HostPerfLog.Write($"[Session.Initialize END] req=\"{requestId}\" stage={initStage} elapsedMs={swStage.ElapsedMilliseconds} hr=0x{initHr:X8} success={initSuccess}");

        if (!initSuccess && initHr != 0)
        {
            Unload(requestId);
            return new LoadResult
            {
                Success = false,
                Stage = initStage,
                ErrorCode = initHr,
                ErrorMessage = $"Handler initialization failed with HRESULT=0x{initHr:X8}",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        // 6. SetWindow (START / END)
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = Math.Max(1, widthPx), Bottom = Math.Max(1, heightPx) };
        swStage.Restart();
        HostPerfLog.Write($"[Session.SetWindow START] req=\"{requestId}\" hostHwnd=0x{hostHwnd.ToInt64():X} rect=(0,0,{rect.Right},{rect.Bottom}) threadId={threadId}");
        try
        {
            _currentHandler.SetWindow(hostHwnd, ref rect);
            swStage.Stop();
            HostPerfLog.Write($"[Session.SetWindow END] req=\"{requestId}\" elapsedMs={swStage.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            swStage.Stop();
            HostPerfLog.Write($"[Session.SetWindow FAILED] req=\"{requestId}\" elapsedMs={swStage.ElapsedMilliseconds} HRESULT=0x{ex.HResult:X8} msg='{ex.Message}'");
            Unload(requestId);
            return new LoadResult
            {
                Success = false,
                Stage = "SetWindow",
                ErrorCode = ex.HResult,
                ErrorMessage = $"SetWindow failed: {ex.Message}",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        // 7. SetRect & DoPreview (START / END SEPARATED)
        swStage.Restart();
        HostPerfLog.Write($"[Session.SetRect START] req=\"{requestId}\" rect=(0,0,{rect.Right},{rect.Bottom})");
        try
        {
            _currentHandler.SetRect(ref rect);
            HostPerfLog.Write($"[Session.SetRect END] req=\"{requestId}\"");

            HostPerfLog.Write($"[Session.DoPreview START] req=\"{requestId}\" threadId={threadId}");
            var doPreviewSw = Stopwatch.StartNew();
            _currentHandler.DoPreview();
            doPreviewSw.Stop();
            HostPerfLog.Write($"[Session.DoPreview END] req=\"{requestId}\" elapsedMs={doPreviewSw.ElapsedMilliseconds} totalElapsedMs={totalSw.ElapsedMilliseconds}");

            swStage.Stop();
        }
        catch (Exception ex)
        {
            swStage.Stop();
            HostPerfLog.Write($"[Session.DoPreview FAILED] req=\"{requestId}\" HRESULT=0x{ex.HResult:X8} msg='{ex.Message}' elapsedMs={swStage.ElapsedMilliseconds}");
            Unload(requestId);
            return new LoadResult
            {
                Success = false,
                Stage = "DoPreview",
                ErrorCode = ex.HResult,
                ErrorMessage = $"DoPreview failed: {ex.Message}",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        totalSw.Stop();
        HostPerfLog.Write($"[Session.Load COMPLETE] req=\"{requestId}\" totalElapsedMs={totalSw.ElapsedMilliseconds}");

        return new LoadResult
        {
            Success = true,
            Stage = "Complete",
            ErrorCode = 0,
            ErrorMessage = "",
            ElapsedMs = totalSw.ElapsedMilliseconds
        };
    }

    public void Resize(int widthPx, int heightPx)
    {
        if (_currentHandler is null)
        {
            HostPerfLog.Write($"[Session.Resize SKIPPED] _currentHandler is null width={widthPx} height={heightPx}");
            return;
        }

        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = Math.Max(1, widthPx), Bottom = Math.Max(1, heightPx) };
        var sw = Stopwatch.StartNew();
        var threadId = Environment.CurrentManagedThreadId;
        HostPerfLog.Write($"[Session.Resize START] rect=(0,0,{rect.Right},{rect.Bottom}) threadId={threadId}");
        try
        {
            _currentHandler.SetRect(ref rect);
            sw.Stop();
            HostPerfLog.Write($"[Session.Resize END] elapsedMs={sw.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            HostPerfLog.Write($"[Session.Resize EXCEPTION] HRESULT=0x{ex.HResult:X8} msg='{ex.Message}' elapsedMs={sw.ElapsedMilliseconds}");
        }
    }

    public void Unload(string requestId = "")
    {
        var threadId = Environment.CurrentManagedThreadId;
        var win32ThreadId = NativeMethods.GetCurrentThreadId();
        HostPerfLog.Write($"[Session.Unload START] req=\"{requestId}\" threadId={threadId} win32ThreadId={win32ThreadId} path='{_currentFilePath}'");

        try
        {
            if (_currentHandler is not null)
            {
                HostPerfLog.Write($"[Session.HandlerUnload START] req=\"{requestId}\" path='{_currentFilePath}'");
                var sw = Stopwatch.StartNew();
                try
                {
                    _currentHandler.Unload();
                    sw.Stop();
                    HostPerfLog.Write($"[Session.HandlerUnload END] req=\"{requestId}\" elapsedMs={sw.ElapsedMilliseconds}");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    HostPerfLog.Write($"[Session.HandlerUnload EXCEPTION] req=\"{requestId}\" elapsedMs={sw.ElapsedMilliseconds} msg='{ex.Message}'");
                }
                finally
                {
                    _currentHandler = null;
                }
            }
        }
        finally
        {
            if (_comObject is not null)
            {
                var swRel = Stopwatch.StartNew();
                HostPerfLog.Write($"[Session.FinalReleaseComObject START] req=\"{requestId}\"");
                try
                {
                    if (Marshal.IsComObject(_comObject))
                    {
                        Marshal.FinalReleaseComObject(_comObject);
                    }
                    swRel.Stop();
                    HostPerfLog.Write($"[Session.FinalReleaseComObject END] req=\"{requestId}\" elapsedMs={swRel.ElapsedMilliseconds}");
                }
                catch (Exception ex)
                {
                    swRel.Stop();
                    HostPerfLog.Write($"[Session.FinalReleaseComObject EXCEPTION] req=\"{requestId}\" elapsedMs={swRel.ElapsedMilliseconds} msg='{ex.Message}'");
                }
                finally
                {
                    _comObject = null;
                }
            }

            _currentFilePath = "";
            _currentClsid = "";
            HostPerfLog.Write($"[Session.Unload COMPLETE] req=\"{requestId}\" threadId={threadId}");
        }
    }

    public void Dispose()
    {
        Unload("dispose");
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern IStream SHCreateStreamOnFile(string pszFile, uint grfMode);

}
