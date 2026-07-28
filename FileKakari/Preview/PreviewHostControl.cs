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

    public PreviewHostProcessManager ProcessManager => _processManager;


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
        Dispatcher.Invoke(InvalidateMeasure);
    }

    private void ProcessManager_OnExited()
    {
    }


    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _containerHwnd = hwndParent.Handle;

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
            // Send Attach IPC to PreviewHost with container HWND and Physical Pixels
            _ = _processManager.SendMessageAsync(new AttachCommand
            {
                ParentHwnd = hwndChild.ToInt64(),
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
        _ = _processManager.SendMessageAsync(new HideCommand { PaneId = "active-pane" });
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

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

        _ = _processManager.SendMessageAsync(new ResizeCommand
        {
            PaneId = "active-pane",
            WidthPx = pixelWidth,
            HeightPx = pixelHeight,
            DpiX = scaleX,
            DpiY = scaleY
        });
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
