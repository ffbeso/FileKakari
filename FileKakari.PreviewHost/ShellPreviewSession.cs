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

    public LoadResult Load(string filePath, string clsidString, IntPtr hostHwnd, int widthPx, int heightPx)
    {
        var totalSw = Stopwatch.StartNew();

        // 1. Validate inputs
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
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
        Unload();

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
            return new LoadResult
            {
                Success = false,
                Stage = "ResolveClass",
                ErrorCode = -3,
                ErrorMessage = "GetTypeFromCLSID returned null.",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        // 4. CreateInstance
        var swStage = Stopwatch.StartNew();
        try
        {
            _comObject = Activator.CreateInstance(handlerType);
            swStage.Stop();
            Console.WriteLine($"[PreviewHost] CreateInstance elapsed={swStage.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Unload();
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
            Unload();
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

        // 5. Initialize Handler
        swStage.Restart();
        var initSuccess = false;
        string initStage = "InitializeWithFile";
        int initHr = 0;

        if (_comObject is NativeMethods.IInitializeWithFile fileInit)
        {
            initHr = fileInit.Initialize(filePath, 0); // STGM_READ = 0
            if (initHr == 0)
            {
                initSuccess = true;
            }
        }

        if (!initSuccess && _comObject is NativeMethods.IInitializeWithStream streamInit)
        {
            initStage = "InitializeWithStream";
            try
            {
                var stream = SHCreateStreamOnFile(filePath, 0); // STGM_READ = 0
                if (stream is not null)
                {
                    initHr = streamInit.Initialize(stream, 0);
                    if (initHr == 0)
                    {
                        initSuccess = true;
                    }
                }
            }
            catch { }
        }

        swStage.Stop();
        Console.WriteLine($"[PreviewHost] Initialize ({initStage}) elapsed={swStage.ElapsedMilliseconds}ms hr=0x{initHr:X8}");

        if (!initSuccess && initHr != 0)
        {
            Unload();
            return new LoadResult
            {
                Success = false,
                Stage = initStage,
                ErrorCode = initHr,
                ErrorMessage = $"Handler initialization failed with HRESULT=0x{initHr:X8}",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        // 6. SetWindow
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = Math.Max(1, widthPx), Bottom = Math.Max(1, heightPx) };
        swStage.Restart();
        try
        {
            _currentHandler.SetWindow(hostHwnd, ref rect);
            swStage.Stop();
            Console.WriteLine($"[PreviewHost] SetWindow elapsed={swStage.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Unload();
            return new LoadResult
            {
                Success = false,
                Stage = "SetWindow",
                ErrorCode = ex.HResult,
                ErrorMessage = $"SetWindow failed: {ex.Message}",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

        // 7. SetRect & DoPreview
        swStage.Restart();
        try
        {
            _currentHandler.SetRect(ref rect);
            _currentHandler.DoPreview();
            swStage.Stop();
            Console.WriteLine($"[PreviewHost] DoPreview elapsed={swStage.ElapsedMilliseconds}ms total={totalSw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Unload();
            return new LoadResult
            {
                Success = false,
                Stage = "DoPreview",
                ErrorCode = ex.HResult,
                ErrorMessage = $"DoPreview failed: {ex.Message}",
                ElapsedMs = totalSw.ElapsedMilliseconds
            };
        }

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
            return;
        }

        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = Math.Max(1, widthPx), Bottom = Math.Max(1, heightPx) };
        try
        {
            _currentHandler.SetRect(ref rect);
        }
        catch { }
    }

    public void Unload()
    {
        if (_currentHandler is not null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _currentHandler.Unload();
            }
            catch { }
            finally
            {
                _currentHandler = null;
            }
            sw.Stop();
            Console.WriteLine($"[PreviewHost] Unload elapsed={sw.ElapsedMilliseconds}ms");
        }

        if (_comObject is not null)
        {
            try
            {
                Marshal.FinalReleaseComObject(_comObject);
            }
            catch { }
            finally
            {
                _comObject = null;
            }
        }

        _currentFilePath = "";
        _currentClsid = "";
    }

    public void Dispose()
    {
        Unload();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern IStream SHCreateStreamOnFile(string pszFile, uint grfMode);

}
