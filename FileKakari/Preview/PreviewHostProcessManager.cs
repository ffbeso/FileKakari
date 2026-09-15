using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileKakari.Preview;

public sealed class PreviewHostProcessManager : IDisposable
{
    private Process? _process;
    private NamedPipeClientStream? _pipeClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly PreviewHostJobObject _jobObject = new();
    private int _disposeState;
    private readonly string _sessionToken = Guid.NewGuid().ToString("N");
    private readonly string _pipeName = $"FileKakari.PreviewHost.{Process.GetCurrentProcess().Id}.{Guid.NewGuid():N}";

    public bool IsConnected => _pipeClient is { IsConnected: true } && _process is { HasExited: false };
    public int HostProcessId => _process?.Id ?? 0;
    public long ChildHwnd { get; private set; }

    public event Action<AttachedEvent>? OnAttached;
    public event Action<ResizedEvent>? OnResized;
    public event Action<ProcessErrorEvent>? OnProcessError;
    public event Action<PreviewLoadedEvent>? OnPreviewLoaded;
    public event Action<PreviewFailedEvent>? OnPreviewFailed;
    public event Action<PreviewUnloadedEvent>? OnPreviewUnloaded;
    public event Action? OnExited;

    public async Task LoadPreviewAsync(string paneId, string filePath, string clsid, int widthPx, int heightPx, string requestId = "")
    {
        var cmd = new LoadPreviewCommand
        {
            RequestId = string.IsNullOrEmpty(requestId) ? Guid.NewGuid().ToString("N") : requestId,
            PaneId = paneId,
            FilePath = filePath,
            PreviewHandlerClsid = clsid,
            WidthPx = widthPx,
            HeightPx = heightPx
        };
        await SendMessageAsync(cmd).ConfigureAwait(false);
    }

    public async Task UnloadPreviewAsync(string paneId, string requestId = "")
    {
        var cmd = new UnloadPreviewCommand
        {
            RequestId = string.IsNullOrEmpty(requestId) ? Guid.NewGuid().ToString("N") : requestId,
            PaneId = paneId
        };
        await SendMessageAsync(cmd).ConfigureAwait(false);
    }


    public async Task<bool> StartAsync()
    {
        var exePath = PreviewHostPathResolver.ResolvePreviewHostPath();
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            PreviewDiagnostics.Error("PreviewHost", "PreviewHost.exe path not found.");
            return false;
        }

        var currentPid = Process.GetCurrentProcess().Id;
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--pipe \"{_pipeName}\" --parent-pid {currentPid} --token \"{_sessionToken}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            _process = Process.Start(startInfo);
            if (_process is null)
            {
                PreviewDiagnostics.Error("PreviewHost", "Failed to start PreviewHost process.");
                return false;
            }

            _process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    PreviewDiagnostics.Info("PreviewHostStdOut", e.Data);
                    PerfLog.Write($"[PreviewHostHostLog] {e.Data}");
                }
            };
            _process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    PreviewDiagnostics.Error("PreviewHostStdErr", e.Data);
                    PerfLog.Write($"[PreviewHostHostErr] {e.Data}");
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _process.EnableRaisingEvents = true;
            _process.Exited += (s, e) =>
            {
                PreviewDiagnostics.Info("PreviewHost", $"PreviewHost process exited. Code={_process.ExitCode}");
                OnExited?.Invoke();
            };


            _jobObject.AddProcess(_process);

            // Connect Named Pipe
            _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipeClient.ConnectAsync(3000, _cts.Token).ConfigureAwait(false);

            _reader = new StreamReader(_pipeClient, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            _writer = new StreamWriter(_pipeClient, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);

            // Send Initialize Command
            var initCmd = new InitializeCommand
            {
                ParentProcessId = currentPid,
                SessionToken = _sessionToken
            };
            await SendMessageAsync(initCmd).ConfigureAwait(false);

            // Wait for Ready event
            var readyLine = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            if (readyLine is null)
            {
                PreviewDiagnostics.Error("PreviewHost", "Received null response for Ready message.");
                return false;
            }

            var readyEvent = JsonSerializer.Deserialize<ReadyEvent>(readyLine);
            if (readyEvent?.Type != "Ready")
            {
                PreviewDiagnostics.Error("PreviewHost", $"Unexpected initial message from PreviewHost: {readyLine}");
                return false;
            }

            PreviewDiagnostics.Info("PreviewHost", $"PreviewHost connected successfully. HostPID={readyEvent.HostProcessId}");

            // Start listening task
            _ = ListenLoopAsync();

            return true;
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewHost", $"StartAsync failed: {ex.Message}");
            Dispose();
            return false;
        }
    }

    public async Task SendMessageAsync<T>(T message) where T : IpcMessageBase
    {
        if (_writer is null || !IsConnected)
        {
            PreviewDiagnostics.Error("PreviewHost", $"SendMessageAsync skipped (not connected or writer null) msgType={typeof(T).Name} isConnected={IsConnected} hostPid={HostProcessId}");
            return;
        }

        var reqId = message.RequestId;
        var json = JsonSerializer.Serialize(message);
        PreviewDiagnostics.Info("PreviewHost", $"[IPC Send START] req=\"{reqId}\" msgType={message.Type} hostPid={HostProcessId} pipeConnected={_pipeClient?.IsConnected}");

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), _cts.Token).ConfigureAwait(false);
            await _writer.FlushAsync(_cts.Token).ConfigureAwait(false);
            PreviewDiagnostics.Info("PreviewHost", $"[IPC Send COMPLETE] req=\"{reqId}\" msgType={message.Type}");
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewHost", $"[IPC Send EXCEPTION] req=\"{reqId}\" msgType={message.Type} msg=\"{ex.Message}\"");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested && _reader is not null && IsConnected)
        {
            try
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    PreviewDiagnostics.Info("PreviewHost", "[IPC Receive EOF] Pipe stream reached EOF.");
                    break;
                }

                if (line.Length > PreviewHostIpcProtocol.MaxMessageLength)
                {
                    PreviewDiagnostics.Error("PreviewHost", "Received message exceeding max length limit.");
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.GetProperty("type").GetString();
                var reqId = doc.RootElement.TryGetProperty("requestId", out var reqProp) ? reqProp.GetString() ?? "" : "";

                switch (type)
                {
                    case "Attached":
                        var attached = JsonSerializer.Deserialize<AttachedEvent>(line);
                        if (attached is not null)
                        {
                            ChildHwnd = attached.ChildHwnd;
                            PreviewDiagnostics.Info("PreviewHost", $"[IPC Recv Attached] req=\"{attached.RequestId}\" childHwnd=0x{attached.ChildHwnd:X} parentHwnd=0x{attached.ParentHwnd:X}");
                            OnAttached?.Invoke(attached);
                        }
                        break;

                    case "Resized":
                        var resized = JsonSerializer.Deserialize<ResizedEvent>(line);
                        if (resized is not null)
                        {
                            PreviewDiagnostics.Info("PreviewHost", $"[IPC Recv Resized] req=\"{resized.RequestId}\" size={resized.WidthPx}x{resized.HeightPx}");
                            OnResized?.Invoke(resized);
                        }
                        break;

                    case "ProcessError":
                        var procErr = JsonSerializer.Deserialize<ProcessErrorEvent>(line);
                        if (procErr is not null)
                        {
                            PreviewDiagnostics.Error("PreviewHost", $"[IPC Recv ProcessError] req=\"{procErr.RequestId}\" msg=\"{procErr.ErrorMessage}\"");
                            OnProcessError?.Invoke(procErr);
                        }
                        break;

                    case "PreviewLoaded":
                        var loaded = JsonSerializer.Deserialize<PreviewLoadedEvent>(line);
                        if (loaded is not null)
                        {
                            PreviewDiagnostics.Info("PreviewHost", $"[IPC Recv PreviewLoaded] req=\"{loaded.RequestId}\" elapsedMs={loaded.ElapsedMs} path='{loaded.FilePath}'");
                            OnPreviewLoaded?.Invoke(loaded);
                        }
                        break;

                    case "PreviewFailed":
                        var failed = JsonSerializer.Deserialize<PreviewFailedEvent>(line);
                        if (failed is not null)
                        {
                            PreviewDiagnostics.Error("PreviewHost", $"[IPC Recv PreviewFailed] req=\"{failed.RequestId}\" stage={failed.Stage} code=0x{failed.ErrorCode:X8} msg=\"{failed.Message}\" elapsedMs={failed.ElapsedMs}");
                            OnPreviewFailed?.Invoke(failed);
                        }
                        break;

                    case "PreviewUnloaded":
                        var unloaded = JsonSerializer.Deserialize<PreviewUnloadedEvent>(line);
                        if (unloaded is not null)
                        {
                            PreviewDiagnostics.Info("PreviewHost", $"[IPC Recv PreviewUnloaded] req=\"{unloaded.RequestId}\"");
                            OnPreviewUnloaded?.Invoke(unloaded);
                        }
                        break;

                    case "Pong":
                        // Heartbeat response
                        break;

                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                PreviewDiagnostics.Error("PreviewHost", $"ListenLoopAsync exception: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        if (_writer is not null)
        {
            try
            {
                SendMessageAsync(new ShutdownCommand()).Wait(200);
            }
            catch (AggregateException ex) when (ex.InnerException is not null && IsExpectedShutdownException(ex.InnerException)) { }
            catch (TaskCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        _cts.Cancel();

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
            _pipeClient?.Dispose();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        if (_process is { HasExited: false })
        {
            try
            {
                if (!_process.WaitForExit(300))
                {
                    _process.Kill();
                }
            }
            catch { }
        }

        _process?.Dispose();
        _jobObject.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }

    private static bool IsExpectedShutdownException(Exception exception)
    {
        return exception is TaskCanceledException or IOException or ObjectDisposedException;
    }
}
