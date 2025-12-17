using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using SharpGen.Runtime;

namespace HlaeObsTools.Services.Video.RTP;

/// <summary>
/// Minimal Win32/D3D11 swapchain viewer for RTP video frames.
/// Keeps a dedicated device/swapchain and presents immediately on new frames.
/// </summary>
public sealed class RtpSwapchainViewer : IDisposable
{
    private const string WindowClassName = "HLAE_RTPViewer";
    private readonly IntPtr _parentHwnd;
    private IntPtr _hwnd;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _frameTexture;
    private int _frameWidth;
    private int _frameHeight;
    private bool _hasHostedBounds;
    private int _hostX;
    private int _hostY;
    private int _hostW;
    private int _hostH;
    private CancellationTokenSource? _cts;
    private Thread? _renderThread;
    private readonly object _frameLock = new();
    private VideoFrame? _latestFrame;
    private long _lastLogUs;
    private bool _firstFrameLogged;

    public bool IsRunning { get; private set; }
    public IntPtr Hwnd => _hwnd;

    public RtpSwapchainViewer(IntPtr parentHwnd = default)
    {
        _parentHwnd = parentHwnd;
    }

    public void Start()
    {
        if (IsRunning)
            return;

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("RtpSwapchainViewer is Windows-only.");
            return;
        }

        _cts = new CancellationTokenSource();
        var started = new ManualResetEventSlim(false);
        Exception? threadEx = null;

        _renderThread = new Thread(() =>
        {
            try
            {
                if (!CreateWindowAndDevice())
                    return;

                IsRunning = true;
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                started.Set();
            }

            if (!IsRunning)
                return;

            RenderLoop(_cts!.Token);
            CleanupOnRenderThread();
        })
        {
            IsBackground = true,
            Name = "RtpSwapchainViewer",
        };
        _renderThread.SetApartmentState(ApartmentState.STA);
        _renderThread.Start();

        started.Wait(3000);
        if (threadEx != null)
        {
            Console.WriteLine($"RtpSwapchainViewer failed to start: {threadEx.Message}");
            Stop();
        }
        else if (!IsRunning)
        {
            Console.WriteLine("RtpSwapchainViewer did not start (window/device init failed).");
            Stop();
        }
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        _cts?.Cancel();
        if (_renderThread != null && _renderThread.IsAlive)
        {
            if (!_renderThread.Join(500))
            {
                try { _renderThread.Interrupt(); } catch { }
            }
        }
        _cts?.Dispose();
        _cts = null;
        _renderThread = null;

        // In case render thread failed early, ensure resources are released.
        CleanupOnRenderThread();
    }

    public void PresentFrame(VideoFrame frame)
    {
        if (!IsRunning)
            return;

        lock (_frameLock)
        {
            _latestFrame = frame;
        }
    }

    /// <summary>
    /// When hosted inside another window, update the target bounds in parent-client coordinates.
    /// </summary>
    public void SetHostedBounds(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        _hasHostedBounds = true;
        _hostX = x;
        _hostY = y;
        _hostW = width;
        _hostH = height;

        if (_hwnd != IntPtr.Zero)
        {
            const uint flags = 0x0014; // SWP_NOZORDER | SWP_NOACTIVATE
            SetWindowPos(_hwnd, IntPtr.Zero, _hostX, _hostY, _hostW, _hostH, flags);
        }
    }

    private bool CreateWindowAndDevice()
    {
        _hwnd = CreateWindow();
        if (_hwnd == IntPtr.Zero)
        {
            Console.WriteLine("Failed to create RTP viewer window.");
            return false;
        }
        else
        {
            Console.WriteLine($"RTP viewer window created: hwnd=0x{_hwnd.ToInt64():X}");
        }

        if (!CreateDeviceAndFactory())
        {
            Console.WriteLine("Failed to create D3D11 device for RTP viewer.");
            DestroyWindowSafe();
            return false;
        }

        return true;
    }

    private void CleanupOnRenderThread()
    {
        ReleaseResources();
        DestroyWindowSafe();
    }

    private void RenderLoop(CancellationToken token)
    {
        Console.WriteLine("RTP viewer render loop started");
        while (!token.IsCancellationRequested)
        {
            PumpWindowMessages();
            VideoFrame? frame = null;
            lock (_frameLock)
            {
                frame = _latestFrame;
                _latestFrame = null;
            }

            if (frame != null)
            {
                try
                {
                    EnsureSwapchain(frame.Width, frame.Height);
                    UploadAndPresent(frame);
                }
                catch (Exception ex)
                {
                    if (ex is SharpGenException sgx)
                        Console.WriteLine($"RtpSwapchainViewer render error hr=0x{sgx.HResult:X8} msg={sgx.Message}\n{sgx.StackTrace}");
                    else
                        Console.WriteLine($"RtpSwapchainViewer render error: {ex.Message}\n{ex.StackTrace}");
                }
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    private void PumpWindowMessages()
    {
        const int PM_REMOVE = 0x0001;
        MSG msg;
        while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void UploadAndPresent(VideoFrame frame)
    {
        if (_context == null || _swapChain == null)
            return;

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            Console.WriteLine($"RtpSwapchainViewer got invalid frame size {frame.Width}x{frame.Height}");
            return;
        }

        EnsureFrameTexture(frame.Width, frame.Height);
        if (_frameTexture == null)
            return;

        // Upload CPU BGRA to GPU texture via map/discard
        MappedSubresource mapped;
        try
        {
            mapped = _context.Map(_frameTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        }
        catch (SharpGenException ex)
        {
            Console.WriteLine($"RtpSwapchainViewer Map failed (hr=0x{ex.HResult:X8}): {ex.Message}");
            return;
        }

        try
        {
            unsafe
            {
                byte* destBase = (byte*)mapped.DataPointer;
                fixed (byte* srcBase = frame.Data)
                {
                    if (!_firstFrameLogged)
                    {
                        Console.WriteLine($"RTP Viewer first frame: {frame.Width}x{frame.Height} stride={frame.Stride} rowPitch={mapped.RowPitch}");
                        _firstFrameLogged = true;
                    }
                    for (int y = 0; y < frame.Height; y++)
                    {
                        var srcOffset = y * frame.Stride;
                        var dstOffset = y * mapped.RowPitch;
                        Buffer.MemoryCopy(
                            source: srcBase + srcOffset,
                            destination: destBase + dstOffset,
                            destinationSizeInBytes: mapped.RowPitch,
                            sourceBytesToCopy: frame.Stride);
                    }
                }
            }
        }
        finally
        {
            _context.Unmap(_frameTexture, 0);
        }

        try
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            using var rtv = _device!.CreateRenderTargetView(backBuffer);
            _context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0f, 1f, 0f, 1f));
            _context.CopyResource(backBuffer, _frameTexture);
            _swapChain.Present(0, PresentFlags.None);
        }
        catch (SharpGenException ex)
        {
            Console.WriteLine($"RtpSwapchainViewer present failed (hr=0x{ex.HResult:X8}): {ex.Message}");
            return;
        }

        LogLatency(frame);
    }

    private void LogLatency(VideoFrame frame)
    {
        if (frame.SourceTimestampUs <= 0)
            return;

        const long unixTicks = 621355968000000000L;
        long nowUs = (DateTime.UtcNow.Ticks - unixTicks) / 10;
        var presentMs = Math.Max(0, (nowUs - frame.SourceTimestampUs) / 1000.0);
        var captureToReceiveMs = frame.ReceivedTimestampUs > 0
            ? Math.Max(0, (frame.ReceivedTimestampUs - frame.SourceTimestampUs) / 1000.0)
            : double.NaN;

        // Log once per second
        if (nowUs - _lastLogUs >= 1_000_000)
        {
            Console.WriteLine($"RTP Viewer latency: {presentMs:F2} ms (capture->receive: {captureToReceiveMs:F2} ms)");
            _lastLogUs = nowUs;
        }
    }

    private bool CreateDeviceAndFactory()
    {
        if (_device != null)
            return true;

        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        Result res = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out _device,
            out _,
            out _context);

        if (res.Failure)
        {
            res = D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                levels,
                out _device,
                out _,
                out _context);
        }

        if (res.Failure || _device == null || _context == null)
            return false;

        _factory = CreateDXGIFactory2<IDXGIFactory2>(false);
        return _factory != null;
    }

    private void EnsureSwapchain(int width, int height)
    {
        if (_factory == null || _device == null || _hwnd == IntPtr.Zero)
            return;

        if (_swapChain != null && _frameWidth == width && _frameHeight == height)
            return;

        _swapChain?.Dispose();
        _swapChain = null;

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

        try
        {
            _swapChain = _factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
        }
        catch (SharpGenException ex)
        {
            Console.WriteLine($"RtpSwapchainViewer swapchain creation failed (hr=0x{ex.HResult:X8}): {ex.Message} size={width}x{height} hwnd=0x{_hwnd.ToInt64():X}");
            _swapChain = null;
            return;
        }

        _frameWidth = width;
        _frameHeight = height;
        ResizeWindowToFrame(width, height);
    }

    private void EnsureFrameTexture(int width, int height)
    {
        if (_frameTexture != null &&
            _frameTexture.Description.Width == width &&
            _frameTexture.Description.Height == height)
            return;

        _frameTexture?.Dispose();
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
            // ShaderResource bind flag is required even if we only CPU-write and copy to the backbuffer.
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        };
        try
        {
            _frameTexture = _device.CreateTexture2D(desc);
        }
        catch (SharpGenException ex)
        {
            Console.WriteLine($"RtpSwapchainViewer CreateTexture2D failed (hr=0x{ex.HResult:X8}) size={width}x{height} msg={ex.Message}");
            _frameTexture = null;
        }
    }

    private void ReleaseResources()
    {
        _frameTexture?.Dispose();
        _frameTexture = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _factory?.Dispose();
        _factory = null;
    }

    #region Window
    private static ushort _wndClass;
    private static WndProcDelegate? _wndProc;
    private static IntPtr _wndProcPtr = IntPtr.Zero;
    private static readonly object _classLock = new();
    private static string? _wndClassName;

    private static void EnsureClass()
    {
        if (_wndClass != 0) return;
        lock (_classLock)
        {
            if (_wndClass != 0) return;
            if (_wndProc == null)
            {
                _wndProc = WndProc;
                _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
            }

            for (int attempt = 0; attempt < 2 && _wndClass == 0; attempt++)
            {
                _wndClassName = $"{WindowClassName}_{Environment.ProcessId}_{Guid.NewGuid():N}";
                var wc = new WNDCLASS
                {
                    lpfnWndProc = _wndProcPtr,
                    lpszClassName = _wndClassName,
                    hInstance = GetModuleHandle(IntPtr.Zero)
                };
                _wndClass = RegisterClass(ref wc);
                if (_wndClass == 0)
                {
                    var err = Marshal.GetLastWin32Error();
                    Console.WriteLine($"RtpSwapchainViewer RegisterClass failed (err={err}); class={_wndClassName}");
                    // If class already exists or any other error, try another unique name once.
                }
            }
        }
    }

    private IntPtr CreateWindow()
    {
        EnsureClass();
        int style = _parentHwnd == IntPtr.Zero
            ? 0x10CF0000 // WS_OVERLAPPEDWINDOW | WS_VISIBLE
            : unchecked((int)(0x40000000 | 0x10000000 | 0x02000000)); // WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS
        int exStyle = _parentHwnd == IntPtr.Zero ? 0 : 0;

        IntPtr hwnd = IntPtr.Zero;
        if (_wndClass != 0)
        {
            hwnd = CreateWindowEx(
                exStyle,
                _wndClass,
                "HLAE RTP Viewer",
                style,
                0, 0, 1280, 720,
                _parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine($"CreateWindowEx with atom failed (err={Marshal.GetLastWin32Error()}); retrying with class name.");
            }
        }

        if (hwnd == IntPtr.Zero)
        {
            hwnd = CreateWindowExString(
                exStyle,
                _wndClassName ?? WindowClassName,
                "HLAE RTP Viewer",
                style,
                0, 0, 1280, 720,
                _parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
        }

        if (hwnd == IntPtr.Zero)
        {
            // Fallback to a known good system class so we at least get a window.
            hwnd = CreateWindowExString(
                exStyle,
                _wndClassName ?? WindowClassName,
                "HLAE RTP Viewer",
                style,
                0, 0, 1280, 720,
                _parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine($"CreateWindowEx fallback failed (err={Marshal.GetLastWin32Error()}).");
            }
        }
        if (hwnd != IntPtr.Zero && _parentHwnd != IntPtr.Zero)
        {
            ApplyTransparentStyle(hwnd);
        }
        if (hwnd != IntPtr.Zero && _parentHwnd == IntPtr.Zero)
        {
            ShowWindow(hwnd, 5); // SW_SHOW only for top-level
            UpdateWindow(hwnd);
        }
        return hwnd;
    }

    private static void ApplyTransparentStyle(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_NOACTIVATE = 0x08000000;

        if (hwnd == IntPtr.Zero)
            return;

        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }

    private void ResizeWindowToFrame(int width, int height)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        int targetX = _hasHostedBounds ? _hostX : 100;
        int targetY = _hasHostedBounds ? _hostY : 100;
        int targetW = _hasHostedBounds ? _hostW : width;
        int targetH = _hasHostedBounds ? _hostH : height;
        const uint flags = 0x0014; // SWP_NOZORDER | SWP_NOACTIVATE
        SetWindowPos(_hwnd, IntPtr.Zero, targetX, targetY, targetW, targetH, flags);
    }

    private void DestroyWindowSafe()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_DESTROY = 0x0002;
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowExString(
        int dwExStyle,
        string lpClassName,
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

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpmsg);
    #endregion

    public void Dispose()
    {
        Stop();
    }
}
