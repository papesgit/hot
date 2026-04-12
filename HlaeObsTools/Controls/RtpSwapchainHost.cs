using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using GstSharpBundle;
using HlaeObsTools.Services.Video;

namespace HlaeObsTools.Controls;

/// <summary>
/// Native child-window host that renders the RTP stream through GStreamer.
/// Windows-only; falls back to nothing on other platforms.
/// </summary>
public class RtpSwapchainHost : NativeControlHost
{
    private const string WindowClassName = "HLAE_RTPHost";
    private const int GStreamerJitterBufferLatencyMs = 5;
    private static readonly object GStreamerInitLock = new();
    private static bool s_gstreamerInitialized;

    public event EventHandler? RightButtonDown;
    public event EventHandler? RightButtonUp;
    public event EventHandler? FramePresented;

    private IntPtr _hwnd;
    private int _layoutX;
    private int _layoutY;
    private int _layoutW;
    private int _layoutH;
    private bool _layoutSet;
    private readonly object _gstreamerLock = new();
    private RtpReceiverConfig? _pendingGStreamerConfig;
    private Gst.Element? _gstreamerPipeline;
    private Gst.Bus? _gstreamerBus;
    private Gst.BusSyncHandler? _gstreamerBusSyncHandler;
    private Gst.Element? _gstreamerFrameCounter;
    private readonly Gst.SignalHandler _gstreamerFrameHandoffHandler;
    private CancellationTokenSource? _gstreamerBusCts;
    private Task? _gstreamerBusTask;

    public RtpSwapchainHost()
    {
        _gstreamerFrameHandoffHandler = OnGStreamerFrameHandoff;
    }

    public void StartRenderer()
    {
        // Kept for the existing view lifecycle. GStreamer owns rendering now.
    }

    public void StopRenderer()
    {
        // Kept for the existing view lifecycle. GStreamer owns rendering now.
    }

    public void StartGStreamer(RtpReceiverConfig config)
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (_gstreamerLock)
        {
            _pendingGStreamerConfig = config;
            if (_hwnd == IntPtr.Zero)
                return;

            StopGStreamerLocked(clearPendingConfig: false);
            EnsureGStreamerInitialized();

            var pipelineDescription = BuildGStreamerPipeline(config);
            _gstreamerPipeline = Gst.Parse.Launch(pipelineDescription);
            _gstreamerBus = _gstreamerPipeline.Bus;
            _gstreamerBusSyncHandler = OnGStreamerBusSyncMessage;
            if (_gstreamerBus != null)
            {
                _gstreamerBus.SyncHandler = _gstreamerBusSyncHandler;
                _gstreamerBusCts = new CancellationTokenSource();
                _gstreamerBusTask = Task.Run(() => MonitorGStreamerBus(_gstreamerBus, _gstreamerBusCts.Token));
            }

            var videoSink = (_gstreamerPipeline as Gst.Bin)?.GetByName("videosink");
            _gstreamerFrameCounter = (_gstreamerPipeline as Gst.Bin)?.GetByName("fpscounter");
            _gstreamerFrameCounter?.Connect("handoff", _gstreamerFrameHandoffHandler);
            try
            {
                TrySetGStreamerOverlayWindow(videoSink);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GStreamer overlay setup error: {ex.Message}");
            }
            finally
            {
                videoSink?.Dispose();
            }

            var stateChange = _gstreamerPipeline.SetState(Gst.State.Playing);
            Console.WriteLine($"GStreamer RTP receiver listening on {config.Address}:{config.Port}; state={stateChange}");
        }
    }

    public void StopGStreamer()
    {
        lock (_gstreamerLock)
        {
            StopGStreamerLocked(clearPendingConfig: true);
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
        if (_pendingGStreamerConfig != null)
            StartGStreamer(_pendingGStreamerConfig);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        lock (_gstreamerLock)
        {
            StopGStreamerLocked(clearPendingConfig: false);
        }

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

    private static void EnsureGStreamerInitialized()
    {
        lock (GStreamerInitLock)
        {
            if (s_gstreamerInitialized)
                return;

            GStreamerBundle.Initialize();
            ConfigureGStreamerEnvironment();
            Gst.Application.Init();
            s_gstreamerInitialized = true;
            Console.WriteLine($"GStreamer initialized: {Gst.Application.VersionString()}");
        }
    }

    private static void ConfigureGStreamerEnvironment()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "gstreamer", MakeGStreamerRuntimeIdentifier());
        var scannerPath = Path.Combine(root, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
        var pluginPath = Path.Combine(root, "lib", "gstreamer-1.0");

        if (File.Exists(scannerPath))
            Environment.SetEnvironmentVariable("GST_PLUGIN_SCANNER", scannerPath, EnvironmentVariableTarget.Process);

        if (Directory.Exists(pluginPath))
            Environment.SetEnvironmentVariable("GST_PLUGIN_PATH", pluginPath, EnvironmentVariableTarget.Process);
    }

    private static string MakeGStreamerRuntimeIdentifier()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException()
        };
    }

    private static string BuildGStreamerPipeline(RtpReceiverConfig config)
    {
        var addressPart = string.IsNullOrWhiteSpace(config.Address) || config.Address == "0.0.0.0"
            ? string.Empty
            : $"address={config.Address} ";

        return $"udpsrc {addressPart}port={config.Port} " +
               $"caps=\"application/x-rtp,media=video,encoding-name=H264,payload={config.PayloadType},clock-rate=90000\" ! " +
               $"rtpjitterbuffer latency={GStreamerJitterBufferLatencyMs} drop-on-latency=true ! " +
               "rtph264depay ! h264parse config-interval=-1 ! " +
               "d3d11h264dec ! queue max-size-buffers=1 leaky=downstream ! " +
               "identity name=fpscounter signal-handoffs=true ! " +
               "d3d11videosink name=videosink sync=false async=false";
    }

    private void OnGStreamerFrameHandoff(object o, GLib.SignalArgs args)
    {
        FramePresented?.Invoke(this, EventArgs.Empty);
    }

    private Gst.BusSyncReply OnGStreamerBusSyncMessage(Gst.Bus bus, Gst.Message message)
    {
        try
        {
            if (Gst.Video.Global.IsVideoOverlayPrepareWindowHandleMessage(message))
            {
                TrySetGStreamerOverlayWindow(message.Src);
                return Gst.BusSyncReply.Drop;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GStreamer overlay setup error: {ex.Message}");
        }

        return Gst.BusSyncReply.Pass;
    }

    private void TrySetGStreamerOverlayWindow(GLib.Object? element)
    {
        if (_hwnd == IntPtr.Zero || element == null)
            return;

        var overlay = Gst.Video.VideoOverlayAdapter.GetObject(element);
        if (overlay == null)
            return;

        overlay.WindowHandle = _hwnd;
        overlay.HandleEvents(false);
        overlay.Expose();
    }

    private void MonitorGStreamerBus(Gst.Bus bus, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Gst.Message? message = null;
            try
            {
                message = bus.TimedPopFiltered(100_000_000, Gst.MessageType.Error | Gst.MessageType.Warning | Gst.MessageType.Eos);
                if (message == null)
                    continue;

                if (message.Type == Gst.MessageType.Error)
                {
                    message.ParseError(out var error, out var debug);
                    Console.WriteLine($"GStreamer RTP error: {error.Message}; {debug}");
                }
                else if (message.Type == Gst.MessageType.Warning)
                {
                    message.ParseWarning(out var error, out var debug);
                    Console.WriteLine($"GStreamer RTP warning: {error}; {debug}");
                }
                else if (message.Type == Gst.MessageType.Eos)
                {
                    Console.WriteLine("GStreamer RTP stream ended.");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    Console.WriteLine($"GStreamer RTP bus monitor error: {ex.Message}");
            }
            finally
            {
                message?.Dispose();
            }
        }
    }

    private void StopGStreamerLocked(bool clearPendingConfig)
    {
        if (clearPendingConfig)
            _pendingGStreamerConfig = null;

        _gstreamerBusCts?.Cancel();
        if (_gstreamerPipeline != null)
        {
            try { _gstreamerPipeline.SetState(Gst.State.Null); } catch { /* ignore shutdown races */ }
        }

        try { _gstreamerBusTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore shutdown races */ }
        _gstreamerBusCts?.Dispose();
        _gstreamerBusCts = null;
        _gstreamerBusTask = null;

        if (_gstreamerBus != null)
        {
            try { _gstreamerBus.SyncHandler = null; } catch { /* ignore shutdown races */ }
            _gstreamerBus.Dispose();
            _gstreamerBus = null;
        }

        if (_gstreamerFrameCounter != null)
        {
            try { _gstreamerFrameCounter.Disconnect("handoff", _gstreamerFrameHandoffHandler); } catch { /* ignore shutdown races */ }
            _gstreamerFrameCounter.Dispose();
            _gstreamerFrameCounter = null;
        }

        _gstreamerBusSyncHandler = null;
        _gstreamerPipeline?.Dispose();
        _gstreamerPipeline = null;
    }

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
