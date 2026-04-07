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
    private sealed class HotNotifyBridge
    {
        private readonly HtmlAtlasRenderer _owner;

        public HotNotifyBridge(HtmlAtlasRenderer owner)
        {
            _owner = owner;
        }

        public void triggerDone(string action, string target)
        {
            if (string.IsNullOrWhiteSpace(action))
                return;
            _owner.RaiseTriggerCompleted(action, target ?? string.Empty);
        }
    }

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
    private readonly HotNotifyBridge _hotNotifyBridge;

    public HtmlAtlasRenderer(GraphicsD3DDevice device, int width, int height, int targetFps, string htmlPath, Format format, bool keyedMutex)
    {
        _device = device;
        _width = width;
        _height = height;
        _targetFps = targetFps;
        _htmlPath = htmlPath;
        _format = format;
        _useKeyedMutex = keyedMutex;
        _hotNotifyBridge = new HotNotifyBridge(this);
    }

    public ID3D11Texture2D? SharedTexture { get; private set; }
    public IDXGIKeyedMutex? KeyedMutex { get; private set; }
    public ulong SharedHandle { get; private set; }
    public event Action<string, string>? TriggerCompleted;

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

        _browser.JavascriptObjectRepository.Register("hotNotify", _hotNotifyBridge);
        _browser.JavascriptMessageReceived += OnJavascriptMessageReceived;
        _browser.FrameLoadEnd += OnFrameLoadEnd;

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

    public Task UpdateGsiAsync(string gsiJson, long? heartbeat, string? extrasJson)
    {
        if (_browser == null)
            return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(gsiJson))
            return Task.CompletedTask;

        var script = BuildGsiScript(gsiJson, heartbeat, extrasJson);
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

    private void OnFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
            return;

        try
        {
            e.Frame.ExecuteJavaScriptAsync(BuildHotNotifyBootstrapScript());
        }
        catch
        {
            // ignore bootstrap injection failures
        }
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
  if (window.hotTrigger) {{
    window.hotTrigger(action, target);
    return;
  }}
  const evt = new CustomEvent('hot:trigger', {{ detail: {{ action, target }} }});
  document.dispatchEvent(evt);
}})();";
    }

    private static string BuildHotNotifyBootstrapScript()
    {
        return @"(function() {
  if (!window.CefSharp || typeof window.CefSharp.BindObjectAsync !== 'function') {
    return;
  }

  if (!window.hotNotifyReady) {
    window.hotNotifyReady = window.CefSharp.BindObjectAsync('hotNotify')
      .catch(() => null);
  }
})();";
    }

    private void RaiseTriggerCompleted(string action, string target)
    {
        TriggerCompleted?.Invoke(action, target);
    }

    private void OnJavascriptMessageReceived(object? sender, JavascriptMessageReceivedEventArgs e)
    {
        try
        {
            var json = JsonSerializer.Serialize(e.Message);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (!root.TryGetProperty("type", out var typeProp))
                return;
            var type = typeProp.GetString();
            if (!string.Equals(type, "hotNotify", StringComparison.OrdinalIgnoreCase))
                return;
            var action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(action))
                return;
            var target = root.TryGetProperty("target", out var targetProp) ? targetProp.GetString() ?? string.Empty : string.Empty;
            RaiseTriggerCompleted(action, target);
        }
        catch
        {
            // ignore message parse errors
        }
    }

    private static string BuildGsiScript(string gsiJson, long? heartbeat, string? extrasJson)
    {
        var jsonLiteral = JsonSerializer.Serialize(gsiJson);
        var heartbeatLiteral = heartbeat.HasValue
            ? heartbeat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "null";
        var extrasLiteral = string.IsNullOrWhiteSpace(extrasJson) ? "null" : extrasJson;
        return $@"(function() {{
  const gsiJson = {jsonLiteral};
  let gsi = null;
  try {{
    gsi = JSON.parse(gsiJson);
  }} catch (err) {{
    gsi = null;
  }}
  window.gsi = gsi;
  window.gsiHeartbeat = {heartbeatLiteral};
  const gsiExtras = {extrasLiteral};
  window.gsiExtras = gsiExtras;
  if (window.gsiUpdate) {{
    window.gsiUpdate(gsi, window.gsiHeartbeat, gsiExtras);
  }}
  const evt = new CustomEvent('gsi:update', {{ detail: {{ gsi, heartbeat: window.gsiHeartbeat, extras: gsiExtras }} }});
  window.dispatchEvent(evt);
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
            _browser.FrameLoadEnd -= OnFrameLoadEnd;
            _browser.JavascriptMessageReceived -= OnJavascriptMessageReceived;
            _browser.Dispose();
            _browser = null;
        }

        KeyedMutex?.Dispose();
        KeyedMutex = null;
        SharedTexture?.Dispose();
        SharedTexture = null;
    }
}
