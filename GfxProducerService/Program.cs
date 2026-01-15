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
        var showUrlAcl = false;
        var installUrlAcl = false;
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
            if (string.Equals(args[i], "--show-urlacl", StringComparison.OrdinalIgnoreCase))
            {
                showUrlAcl = true;
            }
            if (string.Equals(args[i], "--install-urlacl", StringComparison.OrdinalIgnoreCase))
            {
                installUrlAcl = true;
            }
        }

        if (showUrlAcl)
        {
            PrintUrlAclCommand(host, port);
            return;
        }

        if (installUrlAcl)
        {
            InstallUrlAcl(host, port);
            return;
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
        using var server = new ProducerServer(host, port);
        server.Initialize();
        Console.WriteLine($"GfxProducerService listening on ws://{host}:{port}/gfxp/");
        Console.WriteLine("Press Ctrl+C to stop.");

        var cts = new CancellationTokenSource();
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
                        server.Stop();
                        cts.Cancel();
                        break;
                    }
                }
                await Task.Delay(50, cts.Token);
            }
        }, cts.Token);

        try
        {
            server.StartAsync(cts.Token).GetAwaiter().GetResult();
        }
        finally
        {
            exitTcs.TrySetResult(true);
        }
    }

    private static void PrintUrlAclCommand(string host, int port)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var urlHost = host;
        if (host == "0.0.0.0" || host == "*" || host == "+")
            urlHost = "+";
        var url = $"http://{urlHost}:{port}/gfxp/";
        Console.WriteLine("Run in an elevated PowerShell to allow LAN binding:");
        Console.WriteLine($"netsh http add urlacl url={url} user={user}");
    }

    private static void InstallUrlAcl(string host, int port)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var urlHost = host;
        if (host == "0.0.0.0" || host == "*" || host == "+")
            urlHost = "+";
        var url = $"http://{urlHost}:{port}/gfxp/";
        var args = $"http add urlacl url={url} user={user}";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to launch elevated netsh: {ex.Message}");
            PrintUrlAclCommand(host, port);
        }
    }
}
