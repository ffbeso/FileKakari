using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FileKakari.Preview;

public static class PreviewHostTestRunner
{
    private static PreviewHostProcessManager? _processManager;
    private static PreviewHostControl? _hostControl;
    private static DispatcherTimer? _tickTimer;
    private static int _expectedTicks;
    private static int _actualTicks;
    private static long _maxTickDelayMs;
    private static Stopwatch? _tickStopwatch;
    private static long _lastTickTimeMs;

    public static async Task TryRunDiagnosticTestAsync(Grid previewContainer)
    {
        var testFilePath = Environment.GetEnvironmentVariable("FILEKAKARI_PREVIEWHOST_TEST_FILE");
        if (string.IsNullOrWhiteSpace(testFilePath) || !File.Exists(testFilePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(testFilePath);
        var ext = Path.GetExtension(fullPath);

        var msgStart = $"[PreviewHostTest] Triggered via environment variable. File='{fullPath}' ext='{ext}'";
        PreviewDiagnostics.Info("PreviewHostTest", msgStart);
        PerfLog.Write(msgStart);

        if (!ShellPreviewHandlerRegistry.TryGetPreviewHandlerClsid(fullPath, out var clsidGuid))
        {
            var msgNoClsid = $"[PreviewHostTest] No Shell Preview Handler CLSID resolved for extension '{ext}' file='{fullPath}'";
            PreviewDiagnostics.Error("PreviewHostTest", msgNoClsid);
            PerfLog.Write(msgNoClsid);
            return;
        }

        var clsidStr = clsidGuid.ToString("B");
        var msgClsid = $"[PreviewHostTest] Resolved CLSID extension={ext} clsid={clsidStr}";
        PreviewDiagnostics.Info("PreviewHostTest", msgClsid);
        PerfLog.Write(msgClsid);

        // 1. Create ProcessManager & Start Host
        _processManager = new PreviewHostProcessManager();
        _processManager.OnPreviewLoaded += evt =>
        {
            StopTickMeasurement(out var expected, out var actual, out var maxDelay);
            var logSuccess = $"[PreviewHostTest] OnPreviewLoaded SUCCESS path='{evt.FilePath}' clsid={evt.PreviewHandlerClsid} hostElapsed={evt.ElapsedMs}ms";
            var logDisp = $"[PreviewHostTest] Dispatcher Measurement: expectedTicks={expected} actualTicks={actual} maxTickDelay={maxDelay}ms";
            PreviewDiagnostics.Info("PreviewHostTest", logSuccess);
            PreviewDiagnostics.Info("PreviewHostTest", logDisp);
            PerfLog.Write(logSuccess);
            PerfLog.Write(logDisp);
        };

        _processManager.OnPreviewFailed += evt =>
        {
            StopTickMeasurement(out var expected, out var actual, out var maxDelay);
            var logFail = $"[PreviewHostTest] OnPreviewFailed FAILED stage={evt.Stage} errorCode=0x{evt.ErrorCode:X8} msg='{evt.Message}' hostElapsed={evt.ElapsedMs}ms";
            var logDisp = $"[PreviewHostTest] Dispatcher Measurement: expectedTicks={expected} actualTicks={actual} maxTickDelay={maxDelay}ms";
            PreviewDiagnostics.Error("PreviewHostTest", logFail);
            PreviewDiagnostics.Info("PreviewHostTest", logDisp);
            PerfLog.Write(logFail);
            PerfLog.Write(logDisp);
        };

        var started = await _processManager.StartAsync().ConfigureAwait(true);
        if (!started)
        {
            var logStartFail = "[PreviewHostTest] Failed to start PreviewHost process.";
            PreviewDiagnostics.Error("PreviewHostTest", logStartFail);
            PerfLog.Write(logStartFail);
            return;
        }


        // 2. Create PreviewHostControl and add to container
        _hostControl = new PreviewHostControl(_processManager);
        previewContainer.Children.Clear();
        previewContainer.Children.Add(_hostControl);
        previewContainer.Visibility = Visibility.Visible;

        await Task.Delay(300).ConfigureAwait(true);

        // 3. Start Dispatcher Tick Measurement (50ms interval)
        StartTickMeasurement();

        // 4. Send LoadPreviewAsync
        var wPx = Math.Max(1, (int)previewContainer.ActualWidth);
        var hPx = Math.Max(1, (int)previewContainer.ActualHeight);
        if (wPx <= 1 || hPx <= 1)
        {
            wPx = 800;
            hPx = 600;
        }

        var logSending = $"[PreviewHostTest] Sending LoadPreviewAsync file='{fullPath}' size={wPx}x{hPx}px...";
        PreviewDiagnostics.Info("PreviewHostTest", logSending);
        PerfLog.Write(logSending);
        await _processManager.LoadPreviewAsync("test-pane", fullPath, clsidStr, wPx, hPx, "test-req-1").ConfigureAwait(true);
    }


    private static void StartTickMeasurement()
    {
        _expectedTicks = 0;
        _actualTicks = 0;
        _maxTickDelayMs = 0;
        _tickStopwatch = Stopwatch.StartNew();
        _lastTickTimeMs = 0;
        _ = _expectedTicks; // Silence unused warning


        _tickTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };

        _tickTimer.Tick += (s, e) =>
        {
            _actualTicks++;
            var currentMs = _tickStopwatch.ElapsedMilliseconds;
            if (_lastTickTimeMs > 0)
            {
                var interval = currentMs - _lastTickTimeMs;
                var delay = Math.Max(0, interval - 50);
                if (delay > _maxTickDelayMs)
                {
                    _maxTickDelayMs = delay;
                }
            }
            _lastTickTimeMs = currentMs;
        };

        _tickTimer.Start();
    }

    private static void StopTickMeasurement(out int expectedTicks, out int actualTicks, out long maxTickDelayMs)
    {
        _tickTimer?.Stop();
        _tickTimer = null;

        var elapsedMs = _tickStopwatch?.ElapsedMilliseconds ?? 0;
        expectedTicks = (int)(elapsedMs / 50);
        actualTicks = _actualTicks;
        maxTickDelayMs = _maxTickDelayMs;
    }
}
