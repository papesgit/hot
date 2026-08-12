using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;
using GfxProducerService.Graphics;
using Vortice.DXGI;

namespace GfxProducerService.Server;

public sealed class AtlasManager : IDisposable
{
    private static readonly object CefLock = new();
    private static bool CefInitAttempted;
    private static bool CefInitSucceeded;
    private readonly GraphicsD3DDevice _device = new();
    private readonly Dictionary<string, HtmlAtlasRenderer> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AtlasInfo> _info = new(StringComparer.OrdinalIgnoreCase);
    private bool _cefInitialized;
    public event Action<string, string, string>? TriggerCompleted;

    public void EnsureInitialized()
    {
        if (_cefInitialized)
            return;

        lock (CefLock)
        {
            if (_cefInitialized)
                return;

            if (Cef.IsInitialized == true)
            {
                _cefInitialized = true;
                return;
            }

            if (CefInitAttempted && !CefInitSucceeded)
                throw new InvalidOperationException("Cef.Initialize failed earlier. Check cef.log for details.");

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cacheDir = Path.Combine(appData, "GfxProducerService", "cef_cache");
            Directory.CreateDirectory(cacheDir);

            var subprocessPath = Path.Combine(AppContext.BaseDirectory, "CefSharp.BrowserSubprocess.exe");
            if (!File.Exists(subprocessPath))
                throw new FileNotFoundException($"Missing CefSharp.BrowserSubprocess.exe at {subprocessPath}");

            Console.WriteLine($"[gfxp] init CEF (STA={Thread.CurrentThread.GetApartmentState()})");
            Console.WriteLine($"[gfxp] subprocess={subprocessPath}");

            var settings = new CefSettings
            {
                WindowlessRenderingEnabled = true,
                MultiThreadedMessageLoop = true,
                LogSeverity = LogSeverity.Verbose,
                LogFile = Path.Combine(AppContext.BaseDirectory, "cef.log"),
                RootCachePath = cacheDir,
                BrowserSubprocessPath = subprocessPath
            };
            settings.CefCommandLineArgs.Add("no-sandbox", "1");
            settings.CefCommandLineArgs.Add("allow-file-access-from-files", "1");
            CefInitAttempted = true;
            CefInitSucceeded = Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
            if (!CefInitSucceeded && Cef.IsInitialized != true)
                throw new InvalidOperationException("Cef.Initialize failed. Check cef.log for details.");
            CefInitSucceeded = Cef.IsInitialized == true;
            _cefInitialized = CefInitSucceeded;
        }
    }

    public AtlasInfo CreateAtlas(AtlasCreateRequest request)
    {
        EnsureInitialized();

        if (!IsSupportedHtmlPath(request.HtmlPath))
            throw new FileNotFoundException($"HTML path or URL not found / not supported: {request.HtmlPath}", request.HtmlPath);

        DestroyAtlas(request.Name);

        var format = request.Format == "RGBA8" ? Format.R8G8B8A8_UNorm : Format.B8G8R8A8_UNorm;
        var renderer = new HtmlAtlasRenderer(_device, request.Width, request.Height, request.TargetFps, request.HtmlPath, format, request.KeyedMutex);
        renderer.TriggerCompleted += (action, target) => TriggerCompleted?.Invoke(request.Name, action, target);
        renderer.Start();

        var info = new AtlasInfo
        {
            Name = request.Name,
            Width = request.Width,
            Height = request.Height,
            Format = request.Format,
            AlphaMode = request.AlphaMode,
            KeyedMutex = request.KeyedMutex,
            Handle = renderer.SharedHandle
        };

        _renderers[request.Name] = renderer;
        _info[request.Name] = info;
        return info;
    }

    private static bool IsSupportedHtmlPath(string? htmlPath)
    {
        if (string.IsNullOrWhiteSpace(htmlPath))
            return false;

        if (Uri.TryCreate(htmlPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        return File.Exists(htmlPath);
    }

    public bool TryGet(string name, out AtlasInfo info) => _info.TryGetValue(name, out info!);

    public HtmlAtlasRenderer? GetRenderer(string name)
    {
        _renderers.TryGetValue(name, out var renderer);
        return renderer;
    }

    public async Task BroadcastGsiAsync(string gsiJson, long? heartbeat)
    {
        if (string.IsNullOrWhiteSpace(gsiJson))
            return;
        var tasks = new List<Task>();
        foreach (var renderer in _renderers.Values)
        {
            tasks.Add(renderer.UpdateGsiAsync(gsiJson, heartbeat));
        }
        await Task.WhenAll(tasks);
    }

    public void DestroyAtlas(string name)
    {
        if (_renderers.TryGetValue(name, out var renderer))
        {
            renderer.Dispose();
            _renderers.Remove(name);
        }
        _info.Remove(name);
    }

    public void Dispose()
    {
        foreach (var renderer in _renderers.Values)
        {
            renderer.Dispose();
        }
        _renderers.Clear();
        _info.Clear();
        _device.Dispose();
        if (_cefInitialized)
        {
            Cef.Shutdown();
            _cefInitialized = false;
            CefInitAttempted = false;
            CefInitSucceeded = false;
        }
    }
}

public sealed class AtlasCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = "BGRA8";
    public string AlphaMode { get; set; } = "premultiplied";
    public string HtmlPath { get; set; } = string.Empty;
    public bool KeyedMutex { get; set; } = true;
    public int TargetFps { get; set; } = 30;
}

public sealed class AtlasInfo
{
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = "BGRA8";
    public string AlphaMode { get; set; } = "premultiplied";
    public bool KeyedMutex { get; set; }
    public ulong Handle { get; set; }
}
