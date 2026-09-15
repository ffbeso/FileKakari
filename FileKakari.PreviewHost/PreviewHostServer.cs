using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;


namespace FileKakari.PreviewHost;

public sealed class PreviewHostServer : IDisposable
{
    private readonly string _pipeName;
    private readonly int _expectedParentPid;
    private readonly string _expectedSessionToken;
    private NamedPipeServerStream? _pipeServer;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _disposeState;

    private readonly ConcurrentQueue<Action> _staActionQueue = new();
    private readonly ShellPreviewSession _previewSession = new();
    private uint _staThreadId;
    private int _shutdownState = 0; // 0: Running, 1: ShutdownRequested, 2: ShutdownCompleted
    private string _activeRequestId = "";

    public int ShutdownState => Volatile.Read(ref _shutdownState);

    private IntPtr _hostHwnd = IntPtr.Zero;
    private IntPtr _parentHwnd = IntPtr.Zero;
    private int _currentWidth = 400;
    private int _currentHeight = 300;
    private double _currentDpiX = 1.0;
    private double _currentDpiY = 1.0;


    public event Action? OnShutdownRequested;

    public PreviewHostServer(string pipeName, int parentPid, string sessionToken)
    {
        _pipeName = pipeName;
        _expectedParentPid = parentPid;
        _expectedSessionToken = sessionToken;
    }

    public void SetStaThreadId(uint threadId)
    {
        _staThreadId = threadId;
    }

    public void ProcessPendingStaActions()
    {
        while (_staActionQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _ = SendMessageAsync(new ProcessErrorEventMessage { ErrorMessage = $"STA action error: {ex.Message}" });
            }
        }
    }

    private void EnqueueStaAction(Action action)
    {
        if (Volatile.Read(ref _shutdownState) != 0)
        {
            return;
        }

        _staActionQueue.Enqueue(action);
        if (_staThreadId != 0)
        {
            if (!NativeMethods.PostThreadMessage(_staThreadId, NativeMethods.WM_APP, IntPtr.Zero, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                Console.WriteLine($"[PreviewHost] PostThreadMessage failed Win32Error={err}");
            }
        }
    }

    public void RequestShutdown()
    {
        if (Interlocked.CompareExchange(ref _shutdownState, 1, 0) != 0)
        {
            return;
        }

        _staActionQueue.Enqueue(() =>
        {
            try
            {
                _previewSession.Unload();
                if (_hostHwnd != IntPtr.Zero)
                {
                    NativeMethods.DestroyWindow(_hostHwnd);
                    _hostHwnd = IntPtr.Zero;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _shutdownState, 2);
                _cts.Cancel();
                OnShutdownRequested?.Invoke();
            }
        });

        if (_staThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_staThreadId, NativeMethods.WM_APP, IntPtr.Zero, IntPtr.Zero);
        }
    }




    public async Task RunAsync()
    {
        try
        {
            // Security: Restrict pipe to current user only
            var pipeSecurity = new PipeSecurity();
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));
            }

            _pipeServer = NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                pipeSecurity);

            await _pipeServer.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

            _reader = new StreamReader(_pipeServer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            _writer = new StreamWriter(_pipeServer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);

            // Read Initialize Command
            var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            using var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type != "Initialize")
            {
                return;
            }

            var parentPid = doc.RootElement.GetProperty("parentProcessId").GetInt32();
            var token = doc.RootElement.GetProperty("sessionToken").GetString();

            if (parentPid != _expectedParentPid || token != _expectedSessionToken)
            {
                await SendMessageAsync(new ProcessErrorEventMessage { ErrorMessage = "Unauthorized handshake" }).ConfigureAwait(false);
                return;
            }

            // Handshake valid: Send Ready
            await SendMessageAsync(new ReadyEventMessage
            {
                ProtocolVersion = 1,
                HostProcessId = Process.GetCurrentProcess().Id
            }).ConfigureAwait(false);

            // Command Processing Loop
            while (!_cts.Token.IsCancellationRequested && _pipeServer.IsConnected)
            {
                var msgLine = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (msgLine is null)
                {
                    break;
                }

                if (msgLine.Length > 65536)
                {
                    continue;
                }

                ProcessCommand(msgLine);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try
            {
                await SendMessageAsync(new ProcessErrorEventMessage { ErrorMessage = ex.Message }).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private void ProcessCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var requestId = root.TryGetProperty("requestId", out var reqEl) ? reqEl.GetString() ?? "" : "";

            switch (type)
            {
                case "Attach":
                    var parentHwnd = new IntPtr(root.GetProperty("parentHwnd").GetInt64());
                    var paneId = root.GetProperty("paneId").GetString() ?? "";
                    var wPx = root.GetProperty("widthPx").GetInt32();
                    var hPx = root.GetProperty("heightPx").GetInt32();
                    var dpiX = root.TryGetProperty("dpiX", out var dx) ? dx.GetDouble() : 1.0;
                    var dpiY = root.TryGetProperty("dpiY", out var dy) ? dy.GetDouble() : 1.0;

                    EnqueueStaAction(() =>
                    {
                        AttachToParent(parentHwnd, wPx, hPx, dpiX, dpiY);
                        _ = SendMessageAsync(new AttachedEventMessage
                        {
                            RequestId = requestId,
                            ChildHwnd = _hostHwnd.ToInt64(),
                            ParentHwnd = parentHwnd.ToInt64(),
                            PaneId = paneId
                        });

                    });
                    break;

                case "Resize":
                    var resPaneId = root.GetProperty("paneId").GetString() ?? "";
                    var newWPx = root.GetProperty("widthPx").GetInt32();
                    var newHPx = root.GetProperty("heightPx").GetInt32();
                    var resDpiX = root.TryGetProperty("dpiX", out var rdx) ? rdx.GetDouble() : 1.0;
                    var resDpiY = root.TryGetProperty("dpiY", out var rdy) ? rdy.GetDouble() : 1.0;

                    EnqueueStaAction(() =>
                    {
                        ResizeHost(newWPx, newHPx, resDpiX, resDpiY);
                        var (clientWidthPx, clientHeightPx) = GetHostClientSize(newWPx, newHPx);
                        _previewSession.Resize(clientWidthPx, clientHeightPx);
                        _ = SendMessageAsync(new ResizedEventMessage
                        {
                            RequestId = requestId,
                            PaneId = resPaneId,
                            WidthPx = newWPx,
                            HeightPx = newHPx
                        });
                    });
                    break;

                case "LoadPreview":
                    var loadPaneId = root.GetProperty("paneId").GetString() ?? "";
                    var filePath = root.GetProperty("filePath").GetString() ?? "";
                    var clsid = root.GetProperty("previewHandlerClsid").GetString() ?? "";
                    var loadWPx = root.GetProperty("widthPx").GetInt32();
                    var loadHPx = root.GetProperty("heightPx").GetInt32();

                    _activeRequestId = requestId;
                    var captureReqId = requestId;
                    HostPerfLog.Write($"[Server.ProcessCommand] LoadPreview command received req=\"{captureReqId}\" path='{filePath}' clsid='{clsid}'");

                    EnqueueStaAction(() =>
                    {
                        if (Volatile.Read(ref _shutdownState) != 0)
                        {
                            HostPerfLog.Write($"[Server.StaAction] LoadPreview skipped (shutdown) req=\"{captureReqId}\"");
                            return;
                        }

                        if (_parentHwnd == IntPtr.Zero || _hostHwnd == IntPtr.Zero)
                        {
                            HostPerfLog.Write($"[Server.StaAction] LoadPreview FAILED req=\"{captureReqId}\" stage=AttachNotReady");
                            _ = SendMessageAsync(new PreviewFailedEventMessage
                            {
                                RequestId = captureReqId,
                                PaneId = loadPaneId,
                                Stage = "AttachNotReady",
                                ErrorCode = -1,
                                Message = "Parent or host window is not attached.",
                                ElapsedMs = 0
                            });
                            return;
                        }

                        var (clientWidthPx, clientHeightPx) = GetHostClientSize(loadWPx, loadHPx);
                        HostPerfLog.Write($"[Server.StaAction] LoadPreview executing req=\"{captureReqId}\" threadId={Environment.CurrentManagedThreadId}");
                        var res = _previewSession.Load(captureReqId, filePath, clsid, _hostHwnd, clientWidthPx, clientHeightPx);

                        if (_activeRequestId != captureReqId)
                        {
                            HostPerfLog.Write($"[Server.StaAction] LoadPreview completed but inactive req=\"{captureReqId}\" activeReq=\"{_activeRequestId}\"");
                            return;
                        }

                        if (res.Success)
                        {
                            HostPerfLog.Write($"[Server.SendMessage] PreviewLoaded req=\"{captureReqId}\" elapsedMs={res.ElapsedMs}");
                            _ = SendMessageAsync(new PreviewLoadedEventMessage
                            {
                                RequestId = captureReqId,
                                PaneId = loadPaneId,
                                FilePath = filePath,
                                PreviewHandlerClsid = clsid,
                                ElapsedMs = res.ElapsedMs
                            });
                        }
                        else
                        {
                            HostPerfLog.Write($"[Server.SendMessage] PreviewFailed req=\"{captureReqId}\" stage={res.Stage} code=0x{res.ErrorCode:X8} msg='{res.ErrorMessage}' elapsedMs={res.ElapsedMs}");
                            _ = SendMessageAsync(new PreviewFailedEventMessage
                            {
                                RequestId = captureReqId,
                                PaneId = loadPaneId,
                                Stage = res.Stage,
                                ErrorCode = res.ErrorCode,
                                Message = res.ErrorMessage,
                                ElapsedMs = res.ElapsedMs
                            });
                        }
                    });
                    break;

                case "UnloadPreview":
                    var unloadPaneId = root.GetProperty("paneId").GetString() ?? "";
                    var unloadReqId = requestId;
                    HostPerfLog.Write($"[Server.ProcessCommand] UnloadPreview command received req=\"{unloadReqId}\"");
                    EnqueueStaAction(() =>
                    {
                        if (Volatile.Read(ref _shutdownState) != 0)
                        {
                            return;
                        }

                        _previewSession.Unload(unloadReqId);
                        HostPerfLog.Write($"[Server.SendMessage] PreviewUnloaded req=\"{unloadReqId}\"");
                        _ = SendMessageAsync(new PreviewUnloadedEventMessage
                        {
                            RequestId = unloadReqId,
                            PaneId = unloadPaneId
                        });
                    });
                    break;

                case "Show":
                    var showPaneId = root.GetProperty("paneId").GetString() ?? "";
                    EnqueueStaAction(() =>
                    {
                        if (Volatile.Read(ref _shutdownState) != 0)
                        {
                            return;
                        }

                        ShowHost(true);
                        _ = SendMessageAsync(new ShownEventMessage { RequestId = requestId, PaneId = showPaneId });
                    });
                    break;

                case "Hide":
                    var hidePaneId = root.GetProperty("paneId").GetString() ?? "";
                    EnqueueStaAction(() =>
                    {
                        if (Volatile.Read(ref _shutdownState) != 0)
                        {
                            return;
                        }

                        ShowHost(false);
                        _ = SendMessageAsync(new HiddenEventMessage { RequestId = requestId, PaneId = hidePaneId });
                    });
                    break;

                case "SetFocus":
                    var focusPaneId = root.GetProperty("paneId").GetString() ?? "";
                    _ = SendMessageAsync(new FocusedEventMessage { RequestId = requestId, PaneId = focusPaneId });
                    break;

                case "Ping":
                    _ = SendMessageAsync(new PongEventMessage { RequestId = requestId });
                    break;

                case "Shutdown":
                    RequestShutdown();
                    break;
            }
        }
        catch (Exception ex)
        {
            _ = SendMessageAsync(new ProcessErrorEventMessage { ErrorMessage = ex.Message });
        }
    }


    private void AttachToParent(IntPtr parentHwnd, int widthPx, int heightPx, double dpiX, double dpiY)
    {
        _parentHwnd = parentHwnd;
        _currentWidth = widthPx;

        _currentHeight = heightPx;
        _currentDpiX = dpiX;
        _currentDpiY = dpiY;

        if (_hostHwnd == IntPtr.Zero)
        {
            _hostHwnd = CreateHostWindow();
        }

        // 1. SetParent
        NativeMethods.SetParent(_hostHwnd, parentHwnd);

        // 2. Remove WS_POPUP, add WS_CHILD | WS_VISIBLE
        var currentStyle = unchecked(
            (int)NativeMethods.GetWindowLongPtr(_hostHwnd, NativeMethods.GWL_STYLE).ToInt64());
        currentStyle &= ~NativeMethods.WS_POPUP;
        currentStyle |= (NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN);
        NativeMethods.SetWindowLongPtr(_hostHwnd, NativeMethods.GWL_STYLE, new IntPtr(currentStyle));

        // 3. SetWindowPos with SWP_FRAMECHANGED
        NativeMethods.SetWindowPos(
            _hostHwnd,
            IntPtr.Zero,
            0,
            0,
            widthPx,
            heightPx,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
    }

    private void ResizeHost(int widthPx, int heightPx, double dpiX, double dpiY)
    {
        _currentWidth = widthPx;
        _currentHeight = heightPx;
        _currentDpiX = dpiX;
        _currentDpiY = dpiY;

        if (_hostHwnd != IntPtr.Zero)
        {
            NativeMethods.MoveWindow(_hostHwnd, 0, 0, widthPx, heightPx, true);
        }
    }

    private (int WidthPx, int HeightPx) GetHostClientSize(int fallbackWidthPx, int fallbackHeightPx)
    {
        if (_hostHwnd != IntPtr.Zero && NativeMethods.GetClientRect(_hostHwnd, out var rect))
        {
            return (Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
        }

        return (Math.Max(1, fallbackWidthPx), Math.Max(1, fallbackHeightPx));
    }

    private void ShowHost(bool show)
    {
        if (_hostHwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hostHwnd, show ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        }
    }

    private IntPtr CreateHostWindow()
    {
        var hInstance = Process.GetCurrentProcess().Handle;
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            style = 3, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = WndProc,
            hInstance = hInstance,
            lpszClassName = "FileKakari_PreviewHost_WndClass"
        };

        NativeMethods.RegisterClassEx(ref wndClass);

        // Create initial frameless top-level popup
        return NativeMethods.CreateWindowEx(
            0,
            "FileKakari_PreviewHost_WndClass",
            "FileKakari Preview Host Window",
            NativeMethods.WS_POPUP,
            0,
            0,
            _currentWidth,
            _currentHeight,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_PAINT:
                {
                    var hdc = NativeMethods.BeginPaint(hWnd, out var ps);
                    NativeMethods.GetClientRect(hWnd, out var rect);

                    // Background fill (Dark theme style #1E1E1E)
                    var bgBrush = NativeMethods.CreateSolidBrush(0x001E1E1E);
                    NativeMethods.FillRect(hdc, ref rect, bgBrush);
                    NativeMethods.DeleteObject(bgBrush);

                    NativeMethods.EndPaint(hWnd, ref ps);
                    return IntPtr.Zero;
                }

            case NativeMethods.WM_DESTROY:
                _hostHwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public async Task SendMessageAsync<T>(T message)
    {
        if (_writer is null || _pipeServer is not { IsConnected: true })
        {
            return;
        }

        var json = JsonSerializer.Serialize(message);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), _cts.Token).ConfigureAwait(false);
            await _writer.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        RequestShutdown();

        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        try
        {
            _reader?.Dispose();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        try
        {
            _pipeServer?.Dispose();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        _sendLock.Dispose();
        _cts.Dispose();
    }
}

// IPC Message classes for PreviewHost -> FileKakari
internal class EventBase
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";
}

internal sealed class ReadyEventMessage : EventBase
{
    public ReadyEventMessage() { Type = "Ready"; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("hostProcessId")]
    public int HostProcessId { get; set; }
}

internal sealed class AttachedEventMessage : EventBase
{
    public AttachedEventMessage() { Type = "Attached"; }

    [JsonPropertyName("childHwnd")]
    public long ChildHwnd { get; set; }

    [JsonPropertyName("parentHwnd")]
    public long ParentHwnd { get; set; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}


internal sealed class ResizedEventMessage : EventBase
{
    public ResizedEventMessage() { Type = "Resized"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";

    [JsonPropertyName("widthPx")]
    public int WidthPx { get; set; }

    [JsonPropertyName("heightPx")]
    public int HeightPx { get; set; }
}

internal sealed class ShownEventMessage : EventBase
{
    public ShownEventMessage() { Type = "Shown"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

internal sealed class HiddenEventMessage : EventBase
{
    public HiddenEventMessage() { Type = "Hidden"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

internal sealed class FocusedEventMessage : EventBase
{
    public FocusedEventMessage() { Type = "Focused"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

internal sealed class PongEventMessage : EventBase
{
    public PongEventMessage() { Type = "Pong"; }
}

internal sealed class ProcessErrorEventMessage : EventBase
{
    public ProcessErrorEventMessage() { Type = "ProcessError"; }

    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; } = "";
}

internal sealed class PreviewLoadedEventMessage : EventBase
{
    public PreviewLoadedEventMessage() { Type = "PreviewLoaded"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("previewHandlerClsid")]
    public string PreviewHandlerClsid { get; set; } = "";

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }
}

internal sealed class PreviewFailedEventMessage : EventBase
{
    public PreviewFailedEventMessage() { Type = "PreviewFailed"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = "";

    [JsonPropertyName("errorCode")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }
}

internal sealed class PreviewUnloadedEventMessage : EventBase
{
    public PreviewUnloadedEventMessage() { Type = "PreviewUnloaded"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}
