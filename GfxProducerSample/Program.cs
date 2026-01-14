using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace GfxProducerSample;

internal static class Program
{
    private const int Width = 1024;
    private const int Height = 512;
    private const int TargetFps = 30;
    private const string AtlasName = "sample_atlas";
    private const string RegionId = "full";

    private static async Task<int> Main()
    {
        using var device = CreateDevice(out var context);
        using var contextRef = context;
        using var texture = CreateSharedTexture(device);
        using var keyedMutex = texture.QueryInterface<IDXGIKeyedMutex>();
        using var rtv = device.CreateRenderTargetView(texture);
        var handleValue = GetSharedHandle(texture);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:31338"), CancellationToken.None);
        await SendRegister(ws, handleValue);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var frameDelay = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (!cts.IsCancellationRequested)
        {
            var t = sw.Elapsed.TotalSeconds;
            var pulse = (float)((Math.Sin(t * 2.0) + 1.0) * 0.5);
            var color = new Color4(pulse, 0.0f, 1.0f, 1.0f);

            try
            {
                keyedMutex.AcquireSync(0, 0);
                context.ClearRenderTargetView(rtv, color);
                context.Flush();
                keyedMutex.ReleaseSync(1);
            }
            catch
            {
                // Skip frame on sync errors.
            }

            await Task.Delay(frameDelay, cts.Token);
        }

        await SendUnregister(ws);
        return 0;
    }

    private static ID3D11Device CreateDevice(out ID3D11DeviceContext context)
    {
        var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
        var result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device device,
            out _,
            out context
        );
        if (result.Failure || device == null || context == null)
        {
            throw new InvalidOperationException("Failed to create D3D11 device.");
        }
        return device;
    }

    private static ID3D11Texture2D CreateSharedTexture(ID3D11Device device)
    {
        var desc = new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex
        };

        return device.CreateTexture2D(desc);
    }

    private static ulong GetSharedHandle(ID3D11Texture2D texture)
    {
        using var dxgi = texture.QueryInterface<IDXGIResource>();
        var handle = dxgi.SharedHandle;
        return (ulong)handle.ToInt64();
    }

    private static async Task SendRegister(ClientWebSocket ws, ulong handleValue)
    {
        var payload = new
        {
            type = "cmd",
            name = "gfx.register",
            args = new
            {
                name = AtlasName,
                handle = handleValue.ToString(),
                width = Width,
                height = Height,
                format = "RGBA8",
                alphaMode = "straight",
                keyedMutex = true,
                regions = new[]
                {
                    new
                    {
                        id = RegionId,
                        u0 = 0.0,
                        v0 = 0.0,
                        u1 = 1.0,
                        v1 = 1.0,
                        defaultSize = new[] { 64.0, 32.0 }
                    }
                }
            }
        };

        await SendJson(ws, payload);
    }

    private static async Task SendUnregister(ClientWebSocket ws)
    {
        var payload = new
        {
            type = "cmd",
            name = "gfx.unregister",
            args = new { name = AtlasName }
        };

        await SendJson(ws, payload);
    }

    private static async Task SendJson(ClientWebSocket ws, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // ClearRenderTargetView updates the texture on GPU, no CPU upload needed.
}
