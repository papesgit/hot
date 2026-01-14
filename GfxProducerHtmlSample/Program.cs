using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CefSharp;
using CefSharp.OffScreen;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace GfxProducerHtmlSample;

internal static class Program
{
    private const int Width = 1024;
    private const int Height = 512;
    private const int TargetFps = 30;
    private const string AtlasName = "sample_atlas";
    private const string RegionId = "full";

    private static async Task<int> Main()
    {
        var settings = new CefSettings
        {
            WindowlessRenderingEnabled = true,
            MultiThreadedMessageLoop = true,
            LogSeverity = LogSeverity.Disable
        };
        settings.CefCommandLineArgs.Add("no-sandbox", "1");
        Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);

        var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "index.html");
        var htmlUri = new Uri(htmlPath).AbsoluteUri;

        var browserSettings = new BrowserSettings
        {
            BackgroundColor = 0x00000000
        };
        using var browser = new ChromiumWebBrowser(htmlUri, browserSettings)
        {
            Size = new System.Drawing.Size(Width, Height)
        };

        var bufferLock = new object();
        byte[]? frontBuffer = null;
        byte[]? backBuffer = null;
        var frameReady = false;

        browser.Paint += (_, e) =>
        {
            if (e.Width != Width || e.Height != Height) return;
            var size = e.Width * e.Height * 4;
            lock (bufferLock)
            {
                backBuffer ??= new byte[size];
                if (backBuffer.Length != size) backBuffer = new byte[size];
                Marshal.Copy(e.BufferHandle, backBuffer, 0, size);
                frameReady = true;
            }
        };

        await browser.WaitForInitialLoadAsync();

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
        while (!cts.IsCancellationRequested)
        {
            byte[]? frame = null;
            lock (bufferLock)
            {
                if (frameReady)
                {
                    (frontBuffer, backBuffer) = (backBuffer, frontBuffer);
                    frameReady = false;
                    frame = frontBuffer;
                }
            }

            if (frame != null)
            {
                try
                {
                    keyedMutex.AcquireSync(0, 0);
                    unsafe
                    {
                        fixed (byte* p = frame)
                        {
                            context.UpdateSubresource(texture, 0, null, (IntPtr)p, Width * 4, 0);
                        }
                    }
                    context.Flush();
                    keyedMutex.ReleaseSync(1);
                }
                catch
                {
                    // Skip frame on sync errors.
                }
            }

            await Task.Delay(frameDelay, cts.Token);
        }

        await SendUnregister(ws);
        browser.Dispose();
        Cef.Shutdown();
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
            Format = Format.B8G8R8A8_UNorm,
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
                format = "BGRA8",
                alphaMode = "premultiplied",
                keyedMutex = true,
                regions = new[]
                {
                    new
                    {
                        id = "card_left",
                        u0 = 0.03125,
                        v0 = 0.0625,
                        u1 = 0.34375,
                        v1 = 0.3359375,
                        defaultSize = new[] { 32.0, 14.0 }
                    },
                    new
                    {
                        id = "card_right",
                        u0 = 0.65625,
                        v0 = 0.625,
                        u1 = 0.96875,
                        v1 = 0.9375,
                        defaultSize = new[] { 32.0, 16.0 }
                    },
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
}
