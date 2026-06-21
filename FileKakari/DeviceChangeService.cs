using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FileKakari;

public sealed class DeviceChangeService : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly DispatcherTimer _debounceTimer;
    private HwndSource? _source;
    private bool _disposed;

    public DeviceChangeService(Window window)
    {
        _window = window;
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = RefreshDebounce
        };
        _debounceTimer.Tick += DebounceTimer_Tick;
        _window.SourceInitialized += Window_SourceInitialized;
    }

    public event Action? DrivesChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= Window_SourceInitialized;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTimer_Tick;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var changeKind = wParam.ToInt64();
        if (msg == WmDeviceChange
            && (changeKind == DbtDeviceArrival || changeKind == DbtDeviceRemoveComplete))
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        return IntPtr.Zero;
    }

    private void DebounceTimer_Tick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        DrivesChanged?.Invoke();
    }
}
