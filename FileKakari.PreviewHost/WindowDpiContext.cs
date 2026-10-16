using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FileKakari.PreviewHost;

// COM calls and client rectangles must use the same coordinate space as the host HWND.
internal readonly struct WindowDpiContext : IDisposable
{
    private readonly IntPtr _previous;

    public WindowDpiContext(IntPtr hwnd)
    {
        _previous = IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        var context = NativeMethods.GetWindowDpiAwarenessContext(hwnd);
        if (context == IntPtr.Zero)
            throw new InvalidOperationException("Cannot determine preview parent DPI context.");

        _previous = NativeMethods.SetThreadDpiAwarenessContext(context);
        if (_previous == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot match preview parent DPI context.");
    }

    public void Dispose()
    {
        if (_previous != IntPtr.Zero)
            NativeMethods.SetThreadDpiAwarenessContext(_previous);
    }
}
