using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GfxProducerService.Graphics;

public sealed class HtmlAtlasRenderer : IDisposable
{
    private readonly GraphicsD3DDevice _device;
    private readonly object _bufferLock = new();
    private readonly object _reloadLock = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _targetFps;
    private readonly string _htmlPath;
    private readonly Format _format;
    private readonly bool _useKeyedMutex;
    private ChromiumWebBrowser? _browser;
    private byte[]? _frontBuffer;
    private byte[]? _backBuffer;
    private bool _frameReady;
    private CancellationTokenSource? _cts;
    private Task? _renderTask;
    private TaskCompletionSource<bool>? _reloadPaintTcs;

    public HtmlAtlasRenderer(GraphicsD3DDevice device, int width, int height, int targetFps, string htmlPath, Format format, bool keyedMutex)
    {
        _device = device;
        _width = width;
        _height = height;
        _targetFps = targetFps;
        _htmlPath = htmlPath;
        _format = format;
        _useKeyedMutex = keyedMutex;
    }

    public ID3D11Texture2D? SharedTexture { get; private set; }
    public IDXGIKeyedMutex? KeyedMutex { get; private set; }
    public ulong SharedHandle { get; private set; }

    public void Start()
    {
        if (_browser != null)
            return;

        _device.EnsureInitialized();

        var htmlUri = new Uri(_htmlPath).AbsoluteUri;
        var browserSettings = new BrowserSettings
        {
            BackgroundColor = 0x00000000
        };

        _browser = new ChromiumWebBrowser(htmlUri, browserSettings)
        {
            Size = new System.Drawing.Size(_width, _height)
        };

        _browser.Paint += OnPaint;

        CreateSharedTexture();

        _cts = new CancellationTokenSource();
        _renderTask = Task.Run(() => RenderLoopAsync(_cts.Token));
    }

    public async Task ReloadAsync()
    {
        if (_browser == null)
            return;
        TaskCompletionSource<bool> tcs;
        lock (_reloadLock)
        {
            _reloadPaintTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _reloadPaintTcs;
        }

        await _browser.LoadUrlAsync(new Uri(_htmlPath).AbsoluteUri);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
        if (!completed)
        {
            lock (_reloadLock)
            {
                if (ReferenceEquals(_reloadPaintTcs, tcs))
                    _reloadPaintTcs = null;
            }
        }
    }

    public Task TriggerAsync(string action, string target)
    {
        if (_browser == null)
            return Task.CompletedTask;

        var script = BuildTriggerScript(action, target);
        _browser.GetMainFrame().ExecuteJavaScriptAsync(script);
        return Task.CompletedTask;
    }

    private void CreateSharedTexture()
    {
        var miscFlags = _useKeyedMutex ? ResourceOptionFlags.SharedKeyedMutex : ResourceOptionFlags.Shared;
        var desc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = _format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = miscFlags
        };

        SharedTexture = _device.Device.CreateTexture2D(desc);
        if (_useKeyedMutex)
        {
            KeyedMutex = SharedTexture.QueryInterface<IDXGIKeyedMutex>();
        }

        using var dxgi = SharedTexture.QueryInterface<IDXGIResource>();
        SharedHandle = (ulong)dxgi.SharedHandle.ToInt64();
    }

    private void OnPaint(object? sender, OnPaintEventArgs e)
    {
        if (e.Width != _width || e.Height != _height)
            return;

        var size = e.Width * e.Height * 4;
        lock (_bufferLock)
        {
            _backBuffer ??= new byte[size];
            if (_backBuffer.Length != size)
                _backBuffer = new byte[size];
            Marshal.Copy(e.BufferHandle, _backBuffer, 0, size);
            _frameReady = true;
        }

        TaskCompletionSource<bool>? tcs = null;
        lock (_reloadLock)
        {
            if (_reloadPaintTcs != null)
            {
                tcs = _reloadPaintTcs;
                _reloadPaintTcs = null;
            }
        }
        tcs?.TrySetResult(true);
    }

    private async Task RenderLoopAsync(CancellationToken token)
    {
        var frameDelay = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
        while (!token.IsCancellationRequested)
        {
            byte[]? frame = null;
            lock (_bufferLock)
            {
                if (_frameReady)
                {
                    (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
                    _frameReady = false;
                    frame = _frontBuffer;
                }
            }

            if (frame != null && SharedTexture != null)
            {
                try
                {
                    if (KeyedMutex != null)
                        KeyedMutex.AcquireSync(0, 0);
                    lock (_device.ContextLock)
                    {
                        unsafe
                        {
                            fixed (byte* p = frame)
                            {
                                _device.Context.UpdateSubresource(SharedTexture, 0, null, (IntPtr)p, (uint)(_width * 4), 0);
                            }
                        }
                        _device.Context.Flush();
                    }
                    if (KeyedMutex != null)
                        KeyedMutex.ReleaseSync(1);
                }
                catch
                {
                    // Skip frame on sync errors.
                }
            }

            try
            {
                await Task.Delay(frameDelay, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static string BuildTriggerScript(string action, string target)
    {
        var actionJson = JsonSerializer.Serialize(action ?? string.Empty);
        var targetJson = JsonSerializer.Serialize(target ?? string.Empty);
        return $@"(function() {{
  const action = {actionJson};
  const target = {targetJson};
  if (window.hlaeTrigger) {{
    window.hlaeTrigger(action, target);
    return;
  }}
  const evt = new CustomEvent('hlae:trigger', {{ detail: {{ action, target }} }});
  document.dispatchEvent(evt);
}})();";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _renderTask?.Wait(500);
        }
        catch
        {
            // ignore
        }

        if (_browser != null)
        {
            _browser.Paint -= OnPaint;
            _browser.Dispose();
            _browser = null;
        }

        KeyedMutex?.Dispose();
        KeyedMutex = null;
        SharedTexture?.Dispose();
        SharedTexture = null;
    }
}
