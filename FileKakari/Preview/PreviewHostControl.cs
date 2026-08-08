using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileKakari.Preview;

public sealed class PreviewHostControl : HwndHost
{
    private readonly PreviewHostProcessManager _processManager;
    private IntPtr _containerHwnd = IntPtr.Zero;
    private long _currentChildHwndValue;
    private TaskCompletionSource<bool>? _attachTcs;

    public PreviewHostProcessManager ProcessManager => _processManager;
    public bool IsAttached { get; private set; }


    public PreviewHostControl(PreviewHostProcessManager processManager)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _processManager.OnAttached += ProcessManager_OnAttached;
        _processManager.OnExited += ProcessManager_OnExited;
        IsVisibleChanged += PreviewHostControl_IsVisibleChanged;
    }

    private void PreviewHostControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            _ = _processManager.SendMessageAsync(new ShowCommand { PaneId = "active-pane" });
        }
        else
        {
            _ = _processManager.SendMessageAsync(new HideCommand { PaneId = "active-pane" });
        }
    }

    private void ProcessManager_OnAttached(AttachedEvent evt)
    {
        if (evt.ParentHwnd == _currentChildHwndValue && evt.ChildHwnd != 0)
        {
            IsAttached = true;
            PreviewDiagnostics.Info("PreviewHostControl", $"AttachedEvent accepted. parentHwnd=0x{evt.ParentHwnd:X} childHwnd=0x{evt.ChildHwnd:X}");
            _attachTcs?.TrySetResult(true);
        }
        else
        {
            PreviewDiagnostics.Info("PreviewHostControl", $"AttachedEvent rejected (mismatch). evt.ParentHwnd=0x{evt.ParentHwnd:X} current=0x{_currentChildHwndValue:X}");
        }
        Dispatcher.Invoke(InvalidateMeasure);
    }

    private void ProcessManager_OnExited()
    {
        ResetIsAttached("ProcessManager_OnExited");
    }

    public void ResetIsAttached(string reason)
    {
        if (IsAttached || _currentChildHwndValue != 0)
        {
            PreviewDiagnostics.Info("PreviewHostControl", $"IsAttached reset reason=\"{reason}\" previousIsAttached={IsAttached} currentHwnd=0x{_currentChildHwndValue:X}");
        }
        IsAttached = false;
        _attachTcs?.TrySetResult(false);
    }

    private Task<bool>? _pendingAttachTask;

    public Task<bool> EnsureAttachedAsync(string requestId, TimeSpan timeout, System.Threading.CancellationToken cancellationToken)
    {
        if (IsAttached && _currentChildHwndValue != 0)
        {
            PreviewDiagnostics.Info("PreviewHostControl", $"[req=\"{requestId}\"] existing attach reused. hwnd=0x{_currentChildHwndValue:X}");
            return Task.FromResult(true);
        }

        lock (this)
        {
            if (_pendingAttachTask is not null && !_pendingAttachTask.IsCompleted)
            {
                PreviewDiagnostics.Info("PreviewHostControl", $"[req=\"{requestId}\"] attach task deduplicated.");
                return _pendingAttachTask;
            }

            _pendingAttachTask = ExecuteEnsureAttachedInternalAsync(requestId, timeout, cancellationToken);
            return _pendingAttachTask;
        }
    }

    private async Task<bool> ExecuteEnsureAttachedInternalAsync(string requestId, TimeSpan timeout, System.Threading.CancellationToken cancellationToken)
    {
        PreviewDiagnostics.Info("PreviewHostControl", $"[req=\"{requestId}\"] EnsureAttached start. IsAttached={IsAttached} currentHwnd=0x{_currentChildHwndValue:X}");

        if (IsAttached && _currentChildHwndValue != 0)
        {
            return true;
        }

        _attachTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        double scaleX = 1.0;
        double scaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            scaleX = source.CompositionTarget.TransformToDevice.M11;
            scaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        // If container HWND already exists, send AttachCommand explicitly
        if (_currentChildHwndValue != 0)
        {
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth * scaleX));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight * scaleY));

            PreviewDiagnostics.Info("PreviewHostControl", $"[req=\"{requestId}\"] explicit attach sent. hwnd=0x{_currentChildHwndValue:X} size={pixelWidth}x{pixelHeight}");
            await _processManager.SendMessageAsync(new AttachCommand
            {
                ParentHwnd = _currentChildHwndValue,
                PaneId = "active-pane",
                WidthPx = pixelWidth,
                HeightPx = pixelHeight,
                DpiX = scaleX,
                DpiY = scaleY
            }).ConfigureAwait(false);
        }

        return await WaitForAttachedAsync(requestId, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> WaitForAttachedAsync(string requestId, TimeSpan timeout, System.Threading.CancellationToken cancellationToken)
    {
        if (IsAttached)
        {
            return true;
        }

        var tcs = _attachTcs;
        if (tcs is null)
        {
            return false;
        }

        using var timeoutCts = new System.Threading.CancellationTokenSource(timeout);
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using (linkedCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                var res = await tcs.Task.ConfigureAwait(false);
                if (res)
                {
                    PreviewDiagnostics.Info("PreviewHostControl", $"[req=\"{requestId}\"] Attach completed successfully.");
                }
                return res;
            }
        }
        catch (OperationCanceledException)
        {
            if (timeoutCts.IsCancellationRequested)
            {
                PreviewDiagnostics.Error("PreviewHostControl", $"[req=\"{requestId}\"] Attach timeout.");
            }
            return false;
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _containerHwnd = hwndParent.Handle;
        IsAttached = false;
        _attachTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        double scaleX = 1.0;
        double scaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            scaleX = source.CompositionTarget.TransformToDevice.M11;
            scaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth * scaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight * scaleY));

        // Create a static win32 container child window
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

        if (hwndChild != IntPtr.Zero)
        {
            _currentChildHwndValue = hwndChild.ToInt64();
            PreviewDiagnostics.Info("PreviewHostControl", $"BuildWindowCore created container HWND=0x{_currentChildHwndValue:X}");
            // Send Attach IPC to PreviewHost with container HWND and Physical Pixels
            _ = _processManager.SendMessageAsync(new AttachCommand
            {
                ParentHwnd = _currentChildHwndValue,
                PaneId = "active-pane",
                WidthPx = pixelWidth,
                HeightPx = pixelHeight,
                DpiX = scaleX,
                DpiY = scaleY
            });
        }

        return new HandleRef(this, hwndChild);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        ResetIsAttached("DestroyWindowCore");
        _currentChildHwndValue = 0;
        _ = _processManager.SendMessageAsync(new HideCommand { PaneId = "active-pane" });
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }
    }



    private int _lastSentWidthPx = -1;
    private int _lastSentHeightPx = -1;

    public int LastSentWidthPx => _lastSentWidthPx;
    public int LastSentHeightPx => _lastSentHeightPx;

    public RECT GetHostClientRect()
    {
        if (_currentChildHwndValue != 0)
        {
            GetClientRect(new IntPtr(_currentChildHwndValue), out var rect);
            return rect;
        }
        return default;
    }

    public async Task RequestResizeAsync(bool isCorrection = false, string? requestId = null, int generation = 0)
    {
        double scaleX = 1.0;
        double scaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            scaleX = source.CompositionTarget.TransformToDevice.M11;
            scaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        var pixelWidth = Math.Max(0, (int)Math.Ceiling(ActualWidth * scaleX));
        var pixelHeight = Math.Max(0, (int)Math.Ceiling(ActualHeight * scaleY));

        var clientRect = GetHostClientRect();
        if (!isCorrection && pixelWidth == _lastSentWidthPx && pixelHeight == _lastSentHeightPx)
        {
            return;
        }

        var prevWidth = _lastSentWidthPx;
        var prevHeight = _lastSentHeightPx;
        _lastSentWidthPx = pixelWidth;
        _lastSentHeightPx = pixelHeight;

        if (isCorrection)
        {
            PreviewDiagnostics.Info("PreviewHostControl", $"[req=\"{requestId}\" gen={generation}] Delayed layout correction executing. ActualSize={ActualWidth:F1}x{ActualHeight:F1} scale={scaleX:F2}x{scaleY:F2} DIP->px={pixelWidth}x{pixelHeight} GetClientRect=({clientRect.Left},{clientRect.Top},{clientRect.Right},{clientRect.Bottom}) w={clientRect.Right - clientRect.Left} h={clientRect.Bottom - clientRect.Top} prevSent={prevWidth}x{prevHeight} newSent={pixelWidth}x{pixelHeight}");
        }
        else
        {
            PreviewDiagnostics.Verbose("PreviewHostControl", $"Resize sent. ActualSize={ActualWidth:F1}x{ActualHeight:F1} scale={scaleX:F2}x{scaleY:F2} DIP->px={pixelWidth}x{pixelHeight} GetClientRect=({clientRect.Left},{clientRect.Top},{clientRect.Right},{clientRect.Bottom})");
        }

        await _processManager.SendMessageAsync(new ResizeCommand
        {
            PaneId = "active-pane",
            WidthPx = pixelWidth,
            HeightPx = pixelHeight,
            DpiX = scaleX,
            DpiY = scaleY
        }).ConfigureAwait(false);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _ = RequestResizeAsync(isCorrection: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsVisibleChanged -= PreviewHostControl_IsVisibleChanged;
            _processManager.OnAttached -= ProcessManager_OnAttached;
            _processManager.OnExited -= ProcessManager_OnExited;
            _processManager.Dispose();
        }
        base.Dispose(disposing);
    }


    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

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
