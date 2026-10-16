using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FileKakari.PreviewHost;

internal static class PreviewHostDpiChecks
{
    // Hidden native windows only: no Office activation or user documents.
    public static void Run()
    {
        var original = SetThreadDpiAwarenessContext(new IntPtr(-2)); // System aware parent
        if (original == IntPtr.Zero) throw new InvalidOperationException("Cannot set test DPI context.");
        var parent = CreateWindowEx(0, "STATIC", "", unchecked((int)0x80000000), 0, 0, 800, 600,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (parent == IntPtr.Zero) throw new InvalidOperationException("Cannot create test parent.");
        using var server = new PreviewHostServer("dpi-test", Environment.ProcessId, "test");
        try
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4)); // Different host process/thread default
            var before = GetThreadDpiAwarenessContext();
            Command(new { type = "Attach", parentHwnd = parent.ToInt64(), paneId = "test", widthPx = 800, heightPx = 600, dpiX = 1.25, dpiY = 1.25 });
            var host = (IntPtr)typeof(PreviewHostServer).GetField("_hostHwnd", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
            Require(host != IntPtr.Zero, "Host creation failed.");
            Require(GetParent(host) == parent, "Host was not attached to parent.");
            Require(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(parent), GetWindowDpiAwarenessContext(host)), "Parent and host DPI modes differ.");
            Require(AreDpiAwarenessContextsEqual(before, GetThreadDpiAwarenessContext()), "Attach leaked its DPI context.");
            CheckSize(800, 600);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            foreach (var size in new[] { (1000, 750), (480, 240), (800, 600) })
            {
                Command(new { type = "Resize", paneId = "test", widthPx = size.Item1, heightPx = size.Item2, dpiX = 1.25, dpiY = 1.25 });
                CheckSize(size.Item1, size.Item2);
                Require(AreDpiAwarenessContextsEqual(before, GetThreadDpiAwarenessContext()), "Resize leaked its DPI context.");
            }
            Command(new { type = "Attach", parentHwnd = parent.ToInt64(), paneId = "test", widthPx = 640, heightPx = 400, dpiX = 1.25, dpiY = 1.25 });
            CheckSize(640, 400);

            void CheckSize(int width, int height)
            {
                var previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(host));
                try
                {
                    Require(GetClientRect(host, out var rect), "GetClientRect failed.");
                    Require(rect.Right == width && rect.Bottom == height, $"Incorrect host client size: {rect.Right}x{rect.Bottom}, expected {width}x{height}.");
                }
                finally { SetThreadDpiAwarenessContext(previous); }
            }
        }
        finally
        {
            server.RequestShutdown();
            server.ProcessPendingStaActions();
            DestroyWindow(parent);
            SetThreadDpiAwarenessContext(original);
        }

        void Command(object message)
        {
            typeof(PreviewHostServer).GetMethod("ProcessCommand", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(server, [JsonSerializer.Serialize(message)]);
            server.ProcessPendingStaActions();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
}
