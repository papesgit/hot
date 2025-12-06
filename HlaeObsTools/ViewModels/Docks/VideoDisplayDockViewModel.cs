using Avalonia.Media.Imaging;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Video;
using HlaeObsTools.Services.Video.RTP;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.Services.Input;
using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using System.ComponentModel;
using HlaeObsTools.Services.Video.Shared;

namespace HlaeObsTools.ViewModels.Docks;

/// <summary>
/// Video display dock view model
/// </summary>
public class VideoDisplayDockViewModel : Tool, IDisposable
{
    private IVideoSource? _videoSource;
    private WriteableBitmap? _currentFrame;
    private bool _isStreaming;
    private string _statusText = "Not Connected";
    private double _frameRate;
    private DateTime _lastFrameTime;
    private int _frameCount;
    private VideoFrame? _pendingFrame;
    private bool _updateScheduled;

    // Double-buffering: alternate between two bitmaps to force reference change
    private WriteableBitmap? _bitmap0;
    private WriteableBitmap? _bitmap1;
    private bool _useFirstBitmap;

    // Freecam state
    private bool _isFreecamActive;
    private HlaeWebSocketClient? _webSocketClient;
    private HlaeInputSender? _inputSender;
    private BrowserSourcesSettings? _browserSettings;
    private bool _useSharedTextureCpu;
    private bool _useD3DHost;

    public bool ShowNoSignal => !_isStreaming && !_useSharedTextureCpu && !_useD3DHost;
    public bool CanStart => !_isStreaming && !_useSharedTextureCpu && !_useD3DHost;
    public bool CanStop => _isStreaming && !_useSharedTextureCpu && !_useD3DHost;

    public WriteableBitmap? CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            _currentFrame = value;
            OnPropertyChanged();
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            _isStreaming = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowNoSignal));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public double FrameRate
    {
        get => _frameRate;
        private set
        {
            _frameRate = value;
            OnPropertyChanged();
        }
    }

    public bool IsFreecamActive
    {
        get => _isFreecamActive;
        private set
        {
            _isFreecamActive = value;
            OnPropertyChanged();
        }
    }

    public event EventHandler<bool>? FreecamStateChanged;

    public VideoDisplayDockViewModel()
    {
        CanClose = false;
        CanFloat = true;
        CanPin = true;
    }

    /// <summary>
    /// Set WebSocket client for sending commands to HLAE
    /// </summary>
    public void SetWebSocketClient(HlaeWebSocketClient client)
    {
        _webSocketClient = client;
    }

    /// <summary>
    /// Set input sender for freecam control
    /// </summary>
    public void SetInputSender(HlaeInputSender sender)
    {
        _inputSender = sender;
    }

    /// <summary>
    /// Configure HUD/browser source settings.
    /// </summary>
    public void SetBrowserSourcesSettings(BrowserSourcesSettings settings)
    {
        if (_browserSettings != null)
        {
            _browserSettings.PropertyChanged -= OnBrowserSettingsChanged;
        }

        _browserSettings = settings;
        _browserSettings.PropertyChanged += OnBrowserSettingsChanged;

        OnPropertyChanged(nameof(HudAddress));
        OnPropertyChanged(nameof(IsHudEnabled));
    }

    public string HudAddress => _browserSettings?.HudUrl ?? BrowserSourcesSettings.DefaultHudUrl;
    public bool IsHudEnabled => _browserSettings?.IsHudEnabled ?? false;

    public bool UseSharedTextureCpu
    {
        get => _useSharedTextureCpu;
        set
        {
            if (_useSharedTextureCpu == value) return;
            _useSharedTextureCpu = value;
            if (value && _useD3DHost)
            {
                _useD3DHost = false;
                OnPropertyChanged(nameof(UseD3DHost));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowNoSignal));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    public bool UseD3DHost
    {
        get => _useD3DHost;
        set
        {
            if (_useD3DHost == value) return;
            _useD3DHost = value;
            if (value && _useSharedTextureCpu)
            {
                _useSharedTextureCpu = false;
                OnPropertyChanged(nameof(UseSharedTextureCpu));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowNoSignal));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    /// <summary>
    /// Activate freecam (called when right mouse button pressed)
    /// </summary>
    public async void ActivateFreecam()
    {
        if (_webSocketClient == null)
            return;

        // Send freecam enable command to HLAE
        await _webSocketClient.SendCommandAsync("freecam_enable");

        IsFreecamActive = true;
        FreecamStateChanged?.Invoke(this, true);
        Console.WriteLine("Freecam activated");
    }

    /// <summary>
    /// Deactivate freecam (called when right mouse button released)
    /// </summary>
    public async void DeactivateFreecam()
    {
        if (!IsFreecamActive || _webSocketClient == null)
            return;

        // Send freecam disable command to HLAE
        await _webSocketClient.SendCommandAsync("freecam_disable");

        IsFreecamActive = false;
        FreecamStateChanged?.Invoke(this, false);

        Console.WriteLine("Freecam deactivated");
    }

    /// <summary>
    /// Refresh spectator bindings (keys 1-0 to switch players)
    /// </summary>
    public async void RefreshSpectatorBindings()
    {
        if (_webSocketClient == null)
            return;

        // Send refresh_binds command to HLAE
        await _webSocketClient.SendCommandAsync("refresh_binds");

        Console.WriteLine("Spectator bindings refresh requested");
    }

    public void StartStream(RtpReceiverConfig? config = null)
    {
        StopStream();

        try
        {
            if (UseSharedTextureCpu)
            {
                StartSharedTexture();
            }
            else
            {
                StartRtpInternal(config);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            IsStreaming = false;
            Console.WriteLine($"Failed to start video source: {ex}");
        }
    }

    public void StartRtpStream(RtpReceiverConfig? config = null)
    {
        UseSharedTextureCpu = false;
        StartStream(config);
    }

    public void StopStream()
    {
        if (_videoSource != null)
        {
            if (_videoSource is RtpVideoReceiver receiver)
            {
                receiver.FrameReceived -= OnFrameReceived;
            }
            else if (_videoSource is SharedTextureVideoSource shared)
            {
                shared.FrameReceived -= OnFrameReceived;
            }

            _videoSource.Stop();
            _videoSource.Dispose();
            _videoSource = null;
        }

        IsStreaming = false;
        StatusText = "Not Connected";
        CurrentFrame = null;
        _bitmap0 = null;
        _bitmap1 = null;
        FrameRate = 0;
    }

    private void OnFrameReceived(object? sender, VideoFrame frame)
    {
        // Calculate frame rate
        _frameCount++;
        var now = DateTime.Now;
        var elapsed = (now - _lastFrameTime).TotalSeconds;
        if (elapsed >= 1.0)
        {
            FrameRate = _frameCount / elapsed;
            _frameCount = 0;
            _lastFrameTime = now;
        }

        // Always store the latest frame (drop old pending frames for low latency)
        _pendingFrame = frame;

        if (!_updateScheduled)
        {
            _updateScheduled = true;

            // Use Send instead of InvokeAsync for lower latency
            // This processes the frame immediately on the UI thread
            Dispatcher.UIThread.Post(() =>
            {
                _updateScheduled = false;
                if (_pendingFrame != null)
                {
                    UpdateBitmap(_pendingFrame);
                }
            }, DispatcherPriority.MaxValue);
        }
    }

    private void UpdateBitmap(VideoFrame frame)
    {
        try
        {
            // Double-buffering: alternate between two bitmaps to provide different reference each frame
            // This forces Avalonia's Image control to detect the change while avoiding GC pressure
            // Only 2 bitmaps total vs 60+ per second

            var needsRecreate = _bitmap0 == null || _bitmap1 == null ||
                                _bitmap0.PixelSize.Width != frame.Width ||
                                _bitmap0.PixelSize.Height != frame.Height;

            if (needsRecreate)
            {
                // Create both bitmaps with the same dimensions
                _bitmap0 = new WriteableBitmap(
                    new PixelSize(frame.Width, frame.Height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888);

                _bitmap1 = new WriteableBitmap(
                    new PixelSize(frame.Width, frame.Height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888);

                _useFirstBitmap = true;
            }

            // Alternate between the two bitmaps
            var targetBitmap = _useFirstBitmap ? _bitmap0! : _bitmap1!;
            _useFirstBitmap = !_useFirstBitmap;

            // Copy frame data to the target bitmap
            using (var buffer = targetBitmap.Lock())
            {
                unsafe
                {
                    var dest = (byte*)buffer.Address;
                    var destStride = buffer.RowBytes;

                    // Copy line by line
                    for (int y = 0; y < frame.Height; y++)
                    {
                        int srcOffset = y * frame.Stride;
                        int destOffset = y * destStride;

                        Marshal.Copy(
                            frame.Data,
                            srcOffset,
                            (IntPtr)(dest + destOffset),
                            Math.Min(frame.Stride, destStride));
                    }
                }
            }

            // Set the newly updated bitmap - different reference from last frame
            CurrentFrame = targetBitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating bitmap: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopStream();
    }

    private void OnBrowserSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserSourcesSettings.HudUrl))
        {
            OnPropertyChanged(nameof(HudAddress));
        }
        else if (e.PropertyName == nameof(BrowserSourcesSettings.IsHudEnabled))
        {
            OnPropertyChanged(nameof(IsHudEnabled));
        }
    }

    private void StartRtpInternal(RtpReceiverConfig? config = null)
    {
        var receiver = new RtpVideoReceiver(config);
        receiver.FrameReceived += OnFrameReceived;
        receiver.Start();

        _videoSource = receiver;
        IsStreaming = true;
        StatusText = $"Connected - {config?.Address ?? "127.0.0.1"}:{config?.Port ?? 5000}";
        _lastFrameTime = DateTime.Now;
        _frameCount = 0;
    }

    private void StartSharedTexture()
    {
        var receiver = new SharedTextureVideoSource();
        receiver.FrameReceived += OnFrameReceived;
        receiver.Start();

        _videoSource = receiver;
        IsStreaming = true;
        StatusText = $"Shared texture ({receiver.Dimensions.Width}x{receiver.Dimensions.Height})";
        _lastFrameTime = DateTime.Now;
        _frameCount = 0;
    }
}
