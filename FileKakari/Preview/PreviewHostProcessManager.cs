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
            CreateNoWindow = true
        };

        try
        {
            _process = Process.Start(startInfo);
            if (_process is null)
            {
                PreviewDiagnostics.Error("PreviewHost", "Failed to start PreviewHost process.");
                return false;
            }

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

            _reader = new StreamReader(_pipeClient, Encoding.UTF8);
            _writer = new StreamWriter(_pipeClient, Encoding.UTF8) { AutoFlush = true };

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
            return;
        }

        var json = JsonSerializer.Serialize(message);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PreviewDiagnostics.Error("PreviewHost", $"SendMessageAsync failed: {ex.Message}");
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
                    break;
                }

                if (line.Length > PreviewHostIpcProtocol.MaxMessageLength)
                {
                    PreviewDiagnostics.Error("PreviewHost", "Received message exceeding max length limit.");
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var type = doc.RootElement.GetProperty("type").GetString();

                switch (type)
                {
                    case "Attached":
                        var attached = JsonSerializer.Deserialize<AttachedEvent>(line);
                        if (attached is not null)
                        {
                            ChildHwnd = attached.ChildHwnd;
                            OnAttached?.Invoke(attached);
                        }
                        break;

                    case "Resized":
                        var resized = JsonSerializer.Deserialize<ResizedEvent>(line);
                        if (resized is not null)
                        {
                            OnResized?.Invoke(resized);
                        }
                        break;

                    case "ProcessError":
                        var procErr = JsonSerializer.Deserialize<ProcessErrorEvent>(line);
                        if (procErr is not null)
                        {
                            OnProcessError?.Invoke(procErr);
                        }
                        break;

                    case "PreviewLoaded":
                        var loaded = JsonSerializer.Deserialize<PreviewLoadedEvent>(line);
                        if (loaded is not null)
                        {
                            OnPreviewLoaded?.Invoke(loaded);
                        }
                        break;

                    case "PreviewFailed":
                        var failed = JsonSerializer.Deserialize<PreviewFailedEvent>(line);
                        if (failed is not null)
                        {
                            OnPreviewFailed?.Invoke(failed);
                        }
                        break;

                    case "PreviewUnloaded":
                        var unloaded = JsonSerializer.Deserialize<PreviewUnloadedEvent>(line);
                        if (unloaded is not null)
                        {
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
        _cts.Cancel();
        if (_writer is not null)
        {
            try
            {
                SendMessageAsync(new ShutdownCommand()).Wait(200);
            }
            catch { }
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _pipeClient?.Dispose();

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
}
