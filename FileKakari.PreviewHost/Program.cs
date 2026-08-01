using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileKakari.PreviewHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 1. Match DPI Awareness with FileKakari (PerMonitorV2)
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Fallback for older OS versions
        }

        string pipeName = "";
        int parentPid = 0;
        string token = "";

        // Parse Arguments: --pipe <PipeName> --parent-pid <PID> --token <Token>
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe" && i + 1 < args.Length)
            {
                pipeName = args[++i];
            }
            else if (args[i] == "--parent-pid" && i + 1 < args.Length && int.TryParse(args[++i], out var pid))
            {
                parentPid = pid;
            }
            else if (args[i] == "--token" && i + 1 < args.Length)
            {
                token = args[++i];
            }
        }

        if (string.IsNullOrEmpty(pipeName) || parentPid <= 0 || string.IsNullOrEmpty(token))
        {
            return;
        }

        using var server = new PreviewHostServer(pipeName, parentPid, token);
        server.SetStaThreadId(NativeMethods.GetCurrentThreadId());

        var cts = new CancellationTokenSource();
        server.OnShutdownRequested += () => cts.Cancel();

        // Run server in background Task
        var serverTask = Task.Run(async () =>
        {
            await server.RunAsync().ConfigureAwait(false);
        });

        // Run Win32 Message Pump on STA Thread
        while (!cts.Token.IsCancellationRequested)
        {
            while (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                if (msg.message == NativeMethods.WM_QUIT)
                {
                    cts.Cancel();
                    break;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            server.ProcessPendingStaActions();

            if (serverTask.IsCompleted)
            {
                break;
            }

            Thread.Sleep(10);
        }

        server.Dispose();
    }
}
