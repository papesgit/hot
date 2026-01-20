using System;
using System.Threading;
using System.Threading.Tasks;
using GfxProducerService.Server;

namespace GfxProducerService;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = "127.0.0.1";
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
        var cts = new CancellationTokenSource();
        ProducerServer? server = null;
        Console.TreatControlCAsInput = true;
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        Console.WriteLine("Stopping...");
                        server?.Stop();
                        cts.Cancel();
                        break;
                    }
                }
                await Task.Delay(50, cts.Token);
            }
        }, cts.Token);

        try
        {
            using var localServer = new ProducerServer(host, port);
            server = localServer;
            localServer.Initialize();
            Console.WriteLine($"GfxProducerService listening on ws://{host}:{port}/gfxp/");
            Console.WriteLine("Press Ctrl+C to stop.");
            localServer.StartAsync(cts.Token).GetAwaiter().GetResult();
        }
        finally
        {
            server = null;
            exitTcs.TrySetResult(true);
        }
    }

}
