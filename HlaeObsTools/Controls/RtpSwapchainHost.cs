using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using HlaeObsTools.Services.Graphics;
using HlaeObsTools.Services.Video;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HlaeObsTools.Controls;

/// <summary>
/// Native child-window host that renders decoded RTP frames to a D3D11 swapchain.
/// Windows-only; falls back to nothing on other platforms.
/// </summary>
public class RtpSwapchainHost : NativeControlHost
{
    private const string WindowClassName = "HLAE_RTPHost";
    private const long TargetPlayoutDelayUs = 33_000;
    private const int MaxQueuedFrames = 3;

    public event EventHandler? RightButtonDown;
    public event EventHandler? RightButtonUp;
    public event EventHandler? FramePresented;

    private IntPtr _hwnd;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;
    private object? _deviceLock;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _frameTexture;
    private CancellationTokenSource? _cts;
    private Task? _renderLoop;
    private readonly object _frameLock = new();
    private readonly List<VideoFrame> _frameQueue = new(MaxQueuedFrames + 1);
    private int _swapWidth;
    private int _swapHeight;
    private int _layoutX;
    private int _layoutY;
    private int _layoutW;
    private int _layoutH;
    private bool _layoutSet;
    private bool _firstFrameLogged;
    private bool _playoutClockSet;
    private long _playoutBaseSenderUs;
    private long _playoutBaseLocalUs;
    private long _lastLogLocalUs;
    private long _droppedQueuedFrames;
    private long _droppedLateFrames;

    public void StartRenderer()
    {
        if (_renderLoop != null || _hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        _cts = new CancellationTokenSource();
        _renderLoop = Task.Run(() => RenderLoop(_cts.Token));
    }

    public void StopRenderer()
    {
        _cts?.Cancel();
        try { _renderLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore shutdown races */ }
        _cts?.Dispose();
        _cts = null;
        _renderLoop = null;

        lock (_frameLock)
        {
            _frameQueue.Clear();
            _playoutClockSet = false;
        }

        ReleaseResources();
        _firstFrameLogged = false;
        _lastLogLocalUs = 0;
        _droppedQueuedFrames = 0;
        _droppedLateFrames = 0;
    }

    public void PresentFrame(VideoFrame frame)
    {
        if (_renderLoop == null || frame.Width <= 0 || frame.Height <= 0)
            return;

        lock (_frameLock)
        {
            if (frame.SourceTimestampUs > 0)
            {
                int index = _frameQueue.FindIndex(f => f.SourceTimestampUs > frame.SourceTimestampUs);
                if (index >= 0)
                    _frameQueue.Insert(index, frame);
                else
                    _frameQueue.Add(frame);
            }
            else
            {
                _frameQueue.Add(frame);
            }

            while (_frameQueue.Count > MaxQueuedFrames)
            {
                _frameQueue.RemoveAt(0);
                _droppedQueuedFrames++;
            }
        }
    }

    public void SetChildLayout(int x, int y, int width, int height)
    {
        SetContainerLayout(x, y, width, height);
    }

    public void SetContainerLayout(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        _layoutSet = true;
        _layoutX = x;
        _layoutY = y;
        _layoutW = width;
        _layoutH = height;
        UpdateChildBounds();
    }

    public void UpdateChildBounds()
    {
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero)
            return;

        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0)
            return;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int x = (int)Math.Round(b.X * scale);
        int y = (int)Math.Round(b.Y * scale);
        int w = (int)Math.Round(b.Width * scale);
        int h = (int)Math.Round(b.Height * scale);

        if (_layoutSet)
        {
            x += _layoutX;
            y += _layoutY;
            w = _layoutW;
            h = _layoutH;
        }

        SetWindowPos(_hwnd, IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, h), 0x0014); // SWP_NOZORDER | SWP_NOACTIVATE
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
            return base.CreateNativeControlCore(parent);

        _hwnd = CreateChildWindow(parent.Handle);
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
        UpdateChildBounds();
    }

    private void RenderLoop(CancellationToken token)
    {
        if (!CreateDeviceAndFactory())
            return;

        Console.WriteLine("RTP host render loop started");
        while (!token.IsCancellationRequested)
        {
            try
            {
                var frame = DequeueFrameForPlayout();
                if (frame == null)
                {
                    Task.Delay(1, token).Wait(token);
                    continue;
                }

                EnsureSwapchain(frame.Width, frame.Height);
                UploadAndPresent(frame);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RTP host render error: {ex.Message}");
            }
        }
    }

    private VideoFrame? DequeueFrameForPlayout()
    {
        long nowUs = StopwatchUs();
        lock (_frameLock)
        {
            if (_frameQueue.Count == 0)
                return null;

            var first = _frameQueue[0];
            if (first.SourceTimestampUs <= 0)
            {
                _frameQueue.RemoveAt(0);
                return first;
            }

            if (!_playoutClockSet || first.SourceTimestampUs + 250_000 < _playoutBaseSenderUs)
            {
                _playoutBaseSenderUs = first.SourceTimestampUs;
                _playoutBaseLocalUs = nowUs + TargetPlayoutDelayUs;
                _playoutClockSet = true;
            }

            VideoFrame? ready = null;
            while (_frameQueue.Count > 0)
            {
                var candidate = _frameQueue[0];
                long dueUs = _playoutBaseLocalUs + (candidate.SourceTimestampUs - _playoutBaseSenderUs);
                if (dueUs > nowUs)
                    break;

                ready = candidate;
                _frameQueue.RemoveAt(0);
                if (_frameQueue.Count > 0)
                    _droppedLateFrames++;
            }

            return ready;
        }
    }

    private void UploadAndPresent(VideoFrame frame)
    {
        if (_context == null || _swapChain == null || _device == null)
            return;

        EnsureFrameTexture(frame.Width, frame.Height);
        if (_frameTexture == null)
            return;

        var deviceLock = _deviceLock;
        if (deviceLock != null)
            Monitor.Enter(deviceLock);
        try
        {
            var mapped = _context.Map(_frameTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    byte* destBase = (byte*)mapped.DataPointer;
                    fixed (byte* srcBase = frame.Data)
                    {
                        if (!_firstFrameLogged)
                        {
                            Console.WriteLine($"RTP host first frame: {frame.Width}x{frame.Height} stride={frame.Stride} rowPitch={mapped.RowPitch}");
                            _firstFrameLogged = true;
                        }

                        for (int y = 0; y < frame.Height; y++)
                        {
                            Buffer.MemoryCopy(
                                srcBase + y * frame.Stride,
                                destBase + y * mapped.RowPitch,
                                mapped.RowPitch,
                                frame.Stride);
                        }
                    }
                }
            }
            finally
            {
                _context.Unmap(_frameTexture, 0);
            }

            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _context.CopyResource(backBuffer, _frameTexture);
            _swapChain.Present(0, PresentFlags.None);
        }
        catch (SharpGenException ex)
        {
            Console.WriteLine($"RTP host D3D error hr=0x{ex.HResult:X8}: {ex.Message}");
            ReleaseSwapChain();
            return;
        }
        finally
        {
            if (deviceLock != null)
                Monitor.Exit(deviceLock);
        }

        FramePresented?.Invoke(this, EventArgs.Empty);
        LogLatency(frame);
    }

    private void LogLatency(VideoFrame frame)
    {
        if (frame.SourceTimestampUs <= 0)
            return;

        long localUs = StopwatchUs();
        if (localUs - _lastLogLocalUs < 1_000_000)
            return;

        long wallUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        var presentMs = Math.Max(0, (wallUs - frame.SourceTimestampUs) / 1000.0);
        var captureToReceiveMs = frame.ReceivedTimestampUs > 0
            ? Math.Max(0, (frame.ReceivedTimestampUs - frame.SourceTimestampUs) / 1000.0)
            : double.NaN;

        int queued;
        lock (_frameLock)
        {
            queued = _frameQueue.Count;
        }

        Console.WriteLine($"RTP present latency: {presentMs:F2} ms (capture->receive: {captureToReceiveMs:F2} ms, queued={queued}, dropQueue={_droppedQueuedFrames}, dropLate={_droppedLateFrames})");
        _lastLogLocalUs = localUs;
    }

    private bool CreateDeviceAndFactory()
    {
        if (_device != null)
            return true;

        var service = D3D11DeviceService.Instance;
        if (!service.IsReady)
            return false;

        _device = service.Device;
        _context = service.Context;
        _factory = service.Factory;
        _deviceLock = service.ContextLock;
        return _factory != null;
    }

    private void EnsureSwapchain(int width, int height)
    {
        if (_factory == null || _device == null || _hwnd == IntPtr.Zero)
            return;

        if (_swapChain != null && _swapWidth == width && _swapHeight == height)
            return;

        ReleaseSwapChain();
        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore
        };

        _swapChain = _factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
        _swapWidth = width;
        _swapHeight = height;
        UpdateChildBounds();
    }

    private void EnsureFrameTexture(int width, int height)
    {
        if (_frameTexture != null &&
            _frameTexture.Description.Width == width &&
            _frameTexture.Description.Height == height)
            return;

        _frameTexture?.Dispose();
        _frameTexture = null;
        if (_device == null)
            return;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        };
        _frameTexture = _device.CreateTexture2D(desc);
    }

    private void ReleaseResources()
    {
        _frameTexture?.Dispose();
        _frameTexture = null;
        ReleaseSwapChain();
        _context = null;
        _device = null;
        _factory = null;
        _deviceLock = null;
    }

    private void ReleaseSwapChain()
    {
        _swapChain?.Dispose();
        _swapChain = null;
        _swapWidth = 0;
        _swapHeight = 0;
    }

    private static long StopwatchUs() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

    private static ushort _wndClass;
    private static readonly object ClassLock = new();
    private static WndProcDelegate? _wndProc;
    private static IntPtr _wndProcPtr;
    private static readonly Dictionary<IntPtr, WeakReference<RtpSwapchainHost>> HostMap = new();

    private static void EnsureClass()
    {
        if (_wndClass != 0)
            return;

        lock (ClassLock)
        {
            if (_wndClass != 0)
                return;

            _wndProc ??= HostWndProc;
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
            var wc = new WNDCLASS
            {
                lpfnWndProc = _wndProcPtr,
                lpszClassName = WindowClassName,
                hInstance = GetModuleHandle(IntPtr.Zero)
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
            0x40000000 | 0x10000000 | 0x02000000, // WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN
            0, 0, 32, 32,
            parent,
            IntPtr.Zero,
            GetModuleHandle(IntPtr.Zero),
            IntPtr.Zero);
    }

    private static void RegisterHostWindow(IntPtr hwnd, RtpSwapchainHost host)
    {
        lock (ClassLock)
        {
            HostMap[hwnd] = new WeakReference<RtpSwapchainHost>(host);
        }
    }

    private static void UnregisterHostWindow(IntPtr hwnd)
    {
        lock (ClassLock)
        {
            HostMap.Remove(hwnd);
        }
    }

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
            return new IntPtr(HTCLIENT);

        if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ||
            msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP)
        {
            RtpSwapchainHost? host = null;
            lock (ClassLock)
            {
                if (HostMap.TryGetValue(hWnd, out var weak))
                    weak.TryGetTarget(out host);
            }

            if (host != null)
            {
                if (msg == WM_RBUTTONDOWN)
                    host.RightButtonDown?.Invoke(host, EventArgs.Empty);
                else if (msg == WM_RBUTTONUP)
                    host.RightButtonUp?.Invoke(host, EventArgs.Empty);
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
