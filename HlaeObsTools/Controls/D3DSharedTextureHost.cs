using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.Rendering;
using System.IO;
using System.Collections.Generic;
using Vortice.Mathematics;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using HlaeObsTools.Services.Graphics;

namespace HlaeObsTools.Controls;

/// <summary>
/// Native child-window host that renders the shared D3D11 texture directly to a swapchain.
/// Windows-only; falls back to nothing on other platforms.
/// </summary>
public class D3DSharedTextureHost : NativeControlHost
{
    private const int KeyedMutexAcquireTimeoutMs = 200;
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

    public event EventHandler? RightButtonDown;
    public event EventHandler? RightButtonUp;

    private IntPtr _hwnd;
    private ID3D11Device? _device;
    private ID3D11Device1? _device1;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;
    private object? _deviceLock;
    private bool _ownsDevice;
    private IDXGIAdapter1? _adapter;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _sharedTexture;
    private IDXGIKeyedMutex? _sharedKeyedMutex;
    private IntPtr _sharedHandle;
    private bool _sharedHandleInvalidNotified;
    private CancellationTokenSource? _cts;
    private Task? _renderLoop;
    private int _swapWidth;
    private int _swapHeight;
    private int _targetWidth;
    private int _targetHeight;
    private bool _loggedFallback;
    private bool _loggedDevice1Missing;
    private bool _loggedFirstFrame;
    private double _texAspect;

    public event EventHandler? SharedHandleInvalidated;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        var parentHwnd = parent.Handle;
        _hwnd = CreateChildWindow(parentHwnd);
        RegisterHostWindow(_hwnd, this);
        StartRenderer();

        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopRenderer();
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHostWindow(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        base.DestroyNativeControlCore(control);
    }

    protected override void OnMeasureInvalidated()
    {
        base.OnMeasureInvalidated();
        if (_hwnd != IntPtr.Zero)
        {
            UpdateChildWindowSize();
        }
    }

    public void StartRenderer()
    {
        if (_renderLoop != null) return;

        _cts = new CancellationTokenSource();
        _renderLoop = Task.Run(() => RenderLoop(_cts.Token));
    }

    public void StopRenderer()
    {
        _cts?.Cancel();
        try { _renderLoop?.Wait(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _renderLoop = null;

        ReleaseSwapChain();
        _sharedTexture?.Release();
        _sharedTexture = null;
        _sharedKeyedMutex?.Release();
        _sharedKeyedMutex = null;
        _sharedHandle = IntPtr.Zero;
        _sharedHandleInvalidNotified = false;
        _loggedFallback = false;
        _loggedDevice1Missing = false;
        _loggedFirstFrame = false;
        _texAspect = 0;
        _targetWidth = 0;
        _targetHeight = 0;
        if (_ownsDevice)
        {
            _context?.Release();
            _context = null;
            _device1?.Release();
            _device1 = null;
            _device?.Release();
            _device = null;
            _adapter?.Release();
            _adapter = null;
            _factory?.Release();
            _factory = null;
        }
        else
        {
            _context = null;
            _device1 = null;
            _device = null;
            _adapter = null;
            _factory = null;
        }
        _deviceLock = null;
    }

    public void SetSharedTextureHandle(IntPtr handle)
    {
        if (handle == _sharedHandle)
            return;

        if (_sharedHandle != IntPtr.Zero)
        {
            _sharedHandle = IntPtr.Zero;
        }

        _sharedHandle = handle;
        _sharedHandleInvalidNotified = false;

        _sharedKeyedMutex?.Release();
        _sharedKeyedMutex = null;
        _sharedTexture?.Release();
        _sharedTexture = null;
        _texAspect = 0;
    }

    private void RenderLoop(CancellationToken token)
    {
        if (!CreateDeviceAndFactory()) return;

        Log("RenderLoop started");

        int frame = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                frame++;
                if (!_loggedFirstFrame)
                {
                    Log("RenderLoop first frame");
                    _loggedFirstFrame = true;
                }
                EnsureSharedTexture();
                if (_sharedTexture != null && _context != null)
                {
                    var desc = _sharedTexture.Description;
                    if (_swapChain == null || _swapWidth != desc.Width || _swapHeight != desc.Height)
                    {
                        ResizeSwapChain((int)desc.Width, (int)desc.Height);
                    }
                }
                else if (_swapChain == null)
                {
                    // Create a small swapchain so we can see the magenta fallback even without the shared texture.
                    ResizeSwapChain(320, 180);
                }

                if (_swapChain != null && _context != null)
                {
                    using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                    var deviceLock = _deviceLock;
                    if (deviceLock != null)
                        Monitor.Enter(deviceLock);
                    try
                    {
                        if (_sharedTexture != null)
                        {
                            _loggedFallback = false;
                            bool acquired = true;
                            bool waitTimedOut = false;
                            bool locked = false;
                            try
                            {
                                if (_sharedKeyedMutex != null)
                                {
                                    _sharedKeyedMutex.AcquireSync(1, KeyedMutexAcquireTimeoutMs);
                                    locked = true;
                                }

                                _context.CopyResource(backBuffer, _sharedTexture);
                            }
                            catch (SharpGen.Runtime.SharpGenException ex)
                            {
                                acquired = false;
                                waitTimedOut = ex.ResultCode.Code == DxgiErrorWaitTimeout;
                                if (!waitTimedOut)
                                {
                                    Log($"CopyResource/Acquire failed: {ex.ResultCode.Code}");
                                }
                            }
                            catch (Exception ex)
                            {
                                acquired = false;
                                Log($"CopyResource threw {ex.GetType().Name}: {ex.Message}");
                            }
                            finally
                            {
                                if (locked && _sharedKeyedMutex != null)
                                {
                                    try { _sharedKeyedMutex.ReleaseSync(0); } catch { /* ignore */ }
                                }
                            }

                            if (!acquired && token.IsCancellationRequested)
                            {
                                break;
                            }
                            else if (!acquired && waitTimedOut)
                            {
                                continue;
                            }
                            else if (!acquired)
                            {
                                ReleaseSwapChain();
                                _sharedTexture?.Release();
                                _sharedTexture = null;
                                _sharedKeyedMutex?.Release();
                                _sharedKeyedMutex = null;
                                _texAspect = 0;
                                continue;
                            }
                        }
                        else
                        {
                            using var rtv = _device!.CreateRenderTargetView(backBuffer);
                            _context.ClearRenderTargetView(rtv, new Color4(1f, 0f, 1f, 1f));
                            if (!_loggedFallback)
                            {
                                Log("Shared texture still null; showing magenta fallback.");
                                _loggedFallback = true;
                            }
                        }
                    }
                    finally
                    {
                        if (deviceLock != null)
                            Monitor.Exit(deviceLock);
                    }
                    try
                    {
                        _swapChain.Present(0, PresentFlags.None);
                    }
                    catch (SharpGen.Runtime.SharpGenException ex)
                    {
                        Log($"Present failed: {ex.ResultCode.Code}");
                        ReleaseSwapChain();
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Log($"Present threw {ex.GetType().Name}: {ex.Message}");
                        ReleaseSwapChain();
                        continue;
                    }
                }
                else
                {
                    Task.Delay(5, token).Wait(token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                Task.Delay(5, token).Wait(token);
            }
        }
    }

    private bool CreateDeviceAndFactory()
    {
        if (_device != null) return true;

        var service = D3D11DeviceService.Instance;
        if (!service.IsReady)
            return false;

        _device = service.Device;
        _context = service.Context;
        _device1 = service.Device1 ?? _device.QueryInterfaceOrNull<ID3D11Device1>();
        _factory = service.Factory;
        _deviceLock = service.ContextLock;
        _ownsDevice = false;

        if (_device1 == null && !_loggedDevice1Missing)
        {
            Log("CreateDeviceAndFactory: ID3D11Device1 not available; cannot open shared texture by name.");
            _loggedDevice1Missing = true;
        }

        bool ok = _factory != null;
        Log($"CreateDeviceAndFactory: shared device ok={ok}");
        return ok;
    }

    private void EnsureSharedTexture()
    {
        if (_device == null) return;
        if (_sharedTexture != null) return;

        if (_sharedHandle == IntPtr.Zero)
        {
            if (!_sharedHandleInvalidNotified)
            {
                Log("EnsureSharedTexture: no shared handle available yet.");
                _sharedHandleInvalidNotified = true;
            }
            return;
        }

        ID3D11Texture2D? tex = null;
        try
        {
            tex = _device.OpenSharedResource<ID3D11Texture2D>(_sharedHandle);
        }
        catch (Exception ex)
        {
            Log($"OpenSharedResource threw {ex.GetType().Name}: {ex.Message}");
        }

        if (tex != null)
        {
            _sharedTexture = tex;
            _loggedFallback = false;
            _sharedKeyedMutex?.Release();
            _sharedKeyedMutex = _sharedTexture.QueryInterfaceOrNull<IDXGIKeyedMutex>();
            Log($"SharedTextureHost: keyed mutex {( _sharedKeyedMutex != null ? "available" : "unavailable")} (handle)");
            LogTextureDesc("opened handle", _sharedTexture.Description);
            _texAspect = tex.Description.Width / (double)tex.Description.Height;
            UpdateChildWindowSize();
            return;
        }

        if (!_sharedHandleInvalidNotified)
        {
            Log("OpenSharedResource failed for shared handle; will retry after re-register.");
            _sharedHandleInvalidNotified = true;
            _sharedHandle = IntPtr.Zero;
            SharedHandleInvalidated?.Invoke(this, EventArgs.Empty);
        }
        TryRecreateDeviceForSharedTexture();
        // If it fails we leave _sharedTexture null and will try again next loop.
    }

    private void TryRecreateDeviceForSharedTexture()
    {
        if (!_ownsDevice)
            return;
        if (_factory == null) return;

        for (uint i = 0; ; i++)
        {
            if (_factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Failure)
                break;

            if (TryCreateDeviceOnAdapter(adapter))
            {
                adapter.Release();
                break;
            }

            adapter.Release();
        }
    }

    private bool TryCreateDeviceOnAdapter(IDXGIAdapter1 adapter)
    {
        if (!_ownsDevice)
            return false;
        ReleaseSwapChain();
        _sharedTexture?.Release();
        _sharedTexture = null;
        _context?.Release();
        _context = null;
        _device1?.Release();
        _device1 = null;
        _device?.Release();
        _device = null;
        _adapter?.Release();
        _adapter = null;

        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var flags = DeviceCreationFlags.BgraSupport;

        Result res = D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            flags,
            levels,
            out _device,
            out _,
            out _context);

        if (res.Failure || _device == null || _context == null)
        {
            return false;
        }

        _device1 = _device.QueryInterfaceOrNull<ID3D11Device1>();

        if (_sharedHandle != IntPtr.Zero)
        {
            try
            {
                var tex = _device.OpenSharedResource<ID3D11Texture2D>(_sharedHandle);
                if (tex != null)
                {
                    _sharedTexture = tex;
                    _sharedKeyedMutex?.Release();
                    _sharedKeyedMutex = _sharedTexture.QueryInterfaceOrNull<IDXGIKeyedMutex>();
                    Log($"SharedTextureHost: keyed mutex {(_sharedKeyedMutex != null ? "available" : "unavailable")} (adapter handle)");
                    LogTextureDesc("opened handle on adapter", _sharedTexture.Description);
                    _texAspect = tex.Description.Width / (double)tex.Description.Height;
                    UpdateChildWindowSize();
                    _adapter = adapter;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"TryCreateDeviceOnAdapter: OpenSharedResource threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Failed, clean up:
        _sharedTexture?.Release();
        _sharedTexture = null;
        _context?.Release();
        _context = null;
        _device1?.Release();
        _device1 = null;
        _device?.Release();
        _device = null;
        return false;
    }

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try
        {
            Console.WriteLine(line);
            File.AppendAllText("shared_texture_host.log", line + Environment.NewLine);
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static void LogTextureDesc(string tag, Texture2DDescription desc)
    {
        Log($"SharedTextureHost: {tag} size={desc.Width}x{desc.Height} fmt={desc.Format} sampleCount={desc.SampleDescription.Count}");
    }

    private void ResizeSwapChain(int width, int height)
    {
        if (_factory == null || _device == null || _hwnd == IntPtr.Zero) return;

        ReleaseSwapChain();

        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.R8G8B8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore
        };

        _swapChain = _factory.CreateSwapChainForHwnd(_device, _hwnd, desc, null, null);
        _swapWidth = width;
        _swapHeight = height;
        _targetWidth = width;
        _targetHeight = height;
        if (_texAspect <= 0 && height != 0)
        {
            _texAspect = width / (double)height;
        }
        UpdateChildWindowSize();
        Log($"ResizeSwapChain -> {width}x{height}");
    }

    private void ReleaseSwapChain()
    {
        _swapChain?.Release();
        _swapChain = null;
    }

    private void UpdateChildWindowSize()
    {
        if (_hwnd == IntPtr.Zero) return;

        var b = this.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        int targetW = (int)b.Width;
        int targetH = (int)b.Height;

        if (_texAspect > 0 && targetW > 0 && targetH > 0)
        {
            double containerAspect = b.Width / b.Height;
            if (containerAspect > _texAspect)
            {
                targetH = (int)b.Height;
                targetW = (int)(b.Height * _texAspect);
            }
            else
            {
                targetW = (int)b.Width;
                targetH = (int)(b.Width / _texAspect);
            }
        }

        _targetWidth = targetW;
        _targetHeight = targetH;

        double scale = (this.VisualRoot as IRenderRoot)?.RenderScaling ?? 1.0;

        int x = (int)Math.Round((b.X + (b.Width - targetW) / 2) * scale);
        int y = (int)Math.Round((b.Y + (b.Height - targetH) / 2) * scale);
        int pxW = (int)Math.Round(targetW * scale);
        int pxH = (int)Math.Round(targetH * scale);

        SetWindowPos(_hwnd, IntPtr.Zero, x, y, pxW, pxH, 0x0020 | 0x0002); // SWP_NOZORDER | SWP_NOACTIVATE
    }

    #region Win32 interop
    private static ushort _wndClass;
    private static readonly object _classLock = new();
    private static WndProcDelegate? _wndProc;
    private static IntPtr _wndProcPtr = IntPtr.Zero;
    private static readonly Dictionary<IntPtr, WeakReference<D3DSharedTextureHost>> _hostMap = new();

    private static void EnsureClass()
    {
        if (_wndClass != 0) return;
        lock (_classLock)
        {
            if (_wndClass != 0) return;
            if (_wndProc == null)
            {
                _wndProc = HostWndProc;
                _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
            }
            var wc = new WNDCLASS
            {
                lpfnWndProc = _wndProcPtr,
                lpszClassName = "HLAE_D3DHost"
            };
            _wndClass = RegisterClass(ref wc);
        }
    }

    private static IntPtr CreateChildWindow(IntPtr parent)
    {
        EnsureClass();
        return CreateWindowEx(
            0,
            _wndClass,
            "",
            0x40000000 | 0x10000000 | 0x02000000, // WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS
            0, 0, 32, 32,
            parent,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    private static void RegisterHostWindow(IntPtr hwnd, D3DSharedTextureHost host)
    {
        lock (_classLock)
        {
            _hostMap[hwnd] = new WeakReference<D3DSharedTextureHost>(host);
        }
    }

    private static void UnregisterHostWindow(IntPtr hwnd)
    {
        lock (_classLock)
        {
            _hostMap.Remove(hwnd);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        ushort lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);


    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_NCHITTEST = 0x0084;
        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_MOUSEMOVE = 0x0200;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_RBUTTONUP = 0x0205;
        const int HTCLIENT = 1;

        if (msg == WM_NCHITTEST)
        {
            return new IntPtr(HTCLIENT);
        }

        if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ||
            msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP)
        {
            D3DSharedTextureHost? host = null;
            lock (_classLock)
            {
                if (_hostMap.TryGetValue(hWnd, out var weak) && weak.TryGetTarget(out var target))
                {
                    host = target;
                }
            }

            if (host != null)
            {
                if (msg == WM_RBUTTONDOWN)
                    host.RightButtonDown?.Invoke(host, EventArgs.Empty);
                else if (msg == WM_RBUTTONUP)
                    host.RightButtonUp?.Invoke(host, EventArgs.Empty);
                return IntPtr.Zero; // handled
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }
    #endregion
}
