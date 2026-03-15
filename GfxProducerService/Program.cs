using System;
using System.Threading;
using System.Threading.Tasks;
using GfxProducerService.Server;

namespace GfxProducerService;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = "*";
        var port = 31340;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var parsed))
            {
                port = parsed;
            }
            if (string.Equals(args[i], "--host", StringComparison.OrdinalIgnoreCase))
            {
                host = args[i + 1];
            }
        }

        var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staThread = new Thread(() => RunServerSta(host, port, exitTcs))
        {
            IsBackground = false
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();

        await exitTcs.Task;
    }

    private static void RunServerSta(string host, int port, TaskCompletionSource<bool> exitTcs)
    {
        using var cts = new CancellationTokenSource();
        ProducerServer? server = null;
        ConsoleCancelEventHandler? cancelHandler = null;
        var stopping = 0;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (Interlocked.Exchange(ref stopping, 1) != 0)
                return;

            Console.WriteLine("Stopping...");
            cts.Cancel();
            server?.Stop();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            using var localServer = new ProducerServer(host, port);
            server = localServer;
            localServer.Initialize();
            Console.WriteLine($"GfxProducerService listening on ws://{host}:{port}/gfxp/");
            Console.WriteLine("Press Ctrl+C to stop.");
            try
            {
                localServer.StartAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // expected during shutdown
            }
        }
        finally
        {
            if (cancelHandler != null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
            server = null;
            exitTcs.TrySetResult(true);
        }
    }

}
