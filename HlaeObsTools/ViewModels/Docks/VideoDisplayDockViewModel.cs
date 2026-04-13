using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Video;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.Services.Input;
using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using System.Collections.Generic;
using System.Text.Json;
using HlaeObsTools.ViewModels.Hud;
using HlaeObsTools.Views;
using System.Threading.Tasks;

namespace HlaeObsTools.ViewModels.Docks;

public enum FreecamInitMode
{
    InheritMotion,
    Static
}

/// <summary>
/// Video display dock view model
/// </summary>
public class VideoDisplayDockViewModel : Tool, IDisposable
{
    private bool _isStreaming;
    private string _statusText = "Not Connected";
    private double _frameRate;
    private DateTime _lastFrameTime;
    private DateTimeOffset? _lastFrameReceivedUtc;
    private int _frameCount;
    private double _rtpFrameAspect;


    // Freecam state
    private bool _isFreecamActive;
    private HlaeWebSocketClient? _webSocketClient;
    private HlaeInputSender? _inputSender;
    private FreecamSettings? _freecamSettings;
    private bool _useD3DHost;
    private double _freecamSpeed;
    private RtpReceiverConfig _rtpConfig = new();
    private HlaeWebSocketClient? _speedWebSocketClient;
    private readonly IReadOnlyList<double> _speedTicks;
    private double _speedMultiplier = 1.0;
    private HudOverlayWindow? _hudOverlayWindow;
    private HudOverlayViewModel? _hudOverlay;
    private IntPtr _sharedTextureHandle;
    private bool _isDisposing;

    public bool ShowNoSignal => !_isStreaming && !_useD3DHost;
    public bool CanStart => !_isStreaming && !_useD3DHost;
    public bool CanStop => _isStreaming && !_useD3DHost;

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

    public DateTimeOffset? LastFrameReceivedUtc
    {
        get => _lastFrameReceivedUtc;
        private set
        {
            if (_lastFrameReceivedUtc == value)
                return;
            _lastFrameReceivedUtc = value;
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
            if (_hudOverlay != null)
            {
                _hudOverlay.IsFreecamActive = value;
            }
        }
    }

    public event EventHandler<bool>? FreecamStateChanged;
    public event EventHandler<bool>? FreecamSprintStateChanged;
    public event EventHandler<RtpReceiverConfig>? RtpStreamRequested;
    public event EventHandler? RtpStreamStopRequested;
    public event EventHandler? FreecamInputLockRequested;
    public event EventHandler? FreecamInputReleaseRequested;
    public double RtpFrameAspect
    {
        get => _rtpFrameAspect;
        private set
        {
            if (Math.Abs(_rtpFrameAspect - value) < 0.0001)
                return;
            _rtpFrameAspect = value;
            OnPropertyChanged();
        }
    }

    public HudOverlayViewModel? HudOverlay
    {
        get => _hudOverlay;
        private set
        {
            if (ReferenceEquals(_hudOverlay, value))
                return;
            _hudOverlay = value;
            OnPropertyChanged();
        }
    }

    public void SetHudOverlay(HudOverlayViewModel hudOverlay)
    {
        HudOverlay = hudOverlay;
        hudOverlay.IsFreecamActive = _isFreecamActive;
        if (_hudOverlayWindow != null)
        {
            _hudOverlayWindow.DataContext = hudOverlay;
        }
    }

    public VideoDisplayDockViewModel()
    {
        CanClose = false;
        CanFloat = true;
        CanPin = true;
        _speedTicks = BuildTicks();
    }

    /// <summary>
    /// Set WebSocket client for sending commands to HLAE
    /// </summary>
    public void SetWebSocketClient(HlaeWebSocketClient client)
    {
        _webSocketClient = client;
        _speedWebSocketClient = client;
        _speedWebSocketClient.MessageReceived -= OnWebSocketMessage;
        _speedWebSocketClient.MessageReceived += OnWebSocketMessage;
        _speedWebSocketClient.Connected -= OnWebSocketConnected;
        _speedWebSocketClient.Connected += OnWebSocketConnected;
    }

    /// <summary>
    /// Set input sender for freecam control
    /// </summary>
    public void SetInputSender(HlaeInputSender sender)
    {
        _inputSender = sender;
    }

    public bool AnalogKeyboardEnabled => _freecamSettings?.AnalogKeyboardEnabled ?? false;
    public FreecamSettings? FreecamSettings => _freecamSettings;

    public bool TryGetAnalogSprint(out double sprintInput)
    {
        sprintInput = 0.0;
        if (_freecamSettings?.AnalogKeyboardEnabled != true || _inputSender == null)
            return false;

        if (!_inputSender.TryGetAnalogState(out var enabled, out _, out _, out _, out var rx) || !enabled)
            return false;

        sprintInput = Math.Clamp(rx, 0.0f, 1.0f);
        return true;
    }

    /// <summary>
    /// Configure freecam settings for sprint multiplier, etc.
    /// </summary>
    public void SetFreecamSettings(FreecamSettings settings)
    {
        _freecamSettings = settings;
    }

    public void SetRtpConfig(RtpReceiverConfig config)
    {
        _rtpConfig = config;
    }

    public RtpReceiverConfig RtpConfig => _rtpConfig;

    public void RequestFreecamInputLock()
    {
        FreecamInputLockRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetSprintModifierState(bool isPressed)
    {
        FreecamSprintStateChanged?.Invoke(this, isPressed);
    }

    public void RequestFreecamInputRelease()
    {
        FreecamInputReleaseRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool UseD3DHost
    {
        get => _useD3DHost;
        set
        {
            if (_useD3DHost == value) return;
            _useD3DHost = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowNoSignal));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            if (_useD3DHost)
            {
                ResetFrameRateCounter();
                _ = RequestSharedTextureHandleAsync();
            }
            else if (!_isStreaming)
            {
                FrameRate = 0;
                LastFrameReceivedUtc = null;
            }
        }
    }

    /// <summary>
    /// Activate freecam (called when right mouse button pressed)
    /// </summary>
    public async void ActivateFreecam(FreecamInitMode initMode = FreecamInitMode.InheritMotion)
    {
        if (_webSocketClient == null)
            return;

        var initModeValue = initMode == FreecamInitMode.Static ? "static" : "inherit_motion";
        await _webSocketClient.SendCommandAsync("freecam_enable", new { initMode = initModeValue });

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

    public async void EnableFreecamHold()
    {
        if (_webSocketClient == null)
            return;

        var mode = _freecamSettings?.HoldMovementFollowsCamera != false ? "camera" : "world";
        await _webSocketClient.SendCommandAsync("freecam_hold", new { mode });

        Console.WriteLine("Freecam input hold requested");
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

    public Task PauseDemoAsync()
    {
        if (_webSocketClient == null)
            return Task.CompletedTask;

        return _webSocketClient.SendExecCommandAsync("demo_pause");
    }

    public Task ResumeDemoAsync()
    {
        if (_webSocketClient == null)
            return Task.CompletedTask;

        return _webSocketClient.SendExecCommandAsync("demo_resume");
    }

    public void StartStream(RtpReceiverConfig? config = null)
    {
        StopStream();

        try
        {
            StartRtpInternal(config);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            IsStreaming = false;
            Console.WriteLine($"Failed to start video source: {ex}");
        }
    }

    public void StopStream()
    {
        RtpStreamStopRequested?.Invoke(this, EventArgs.Empty);

        IsStreaming = false;
        StatusText = "Not Connected";
        FrameRate = 0;
        LastFrameReceivedUtc = null;
    }

    public void RecordSharedTextureFramePresented()
    {
        LastFrameReceivedUtc = DateTimeOffset.UtcNow;
        RecordFrameRateSample();
    }

    public void RecordRtpFramePresented()
    {
        LastFrameReceivedUtc = DateTimeOffset.UtcNow;
        RecordFrameRateSample();
    }

    private void RecordFrameRateSample()
    {
        _frameCount++;
        var now = DateTime.Now;
        var elapsed = (now - _lastFrameTime).TotalSeconds;
        if (elapsed < 1.0)
            return;

        FrameRate = _frameCount / elapsed;
        _frameCount = 0;
        _lastFrameTime = now;
    }

    private void ResetFrameRateCounter()
    {
        _frameCount = 0;
        _lastFrameTime = DateTime.Now;
        FrameRate = 0;
        LastFrameReceivedUtc = null;
    }

    private void UpdateRtpFrameAspect(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        double aspect = width / (double)height;
        if (Math.Abs(aspect - _rtpFrameAspect) < 0.0001)
            return;

        Dispatcher.UIThread.Post(() => RtpFrameAspect = aspect, DispatcherPriority.Background);
    }

    /// <summary>
    /// Show the HUD overlay window (called when using D3DHost mode)
    /// </summary>
    public void ShowHudOverlay()
    {
        if (_isDisposing)
            return;

        if (_hudOverlayWindow == null)
        {
            _hudOverlayWindow = new HudOverlayWindow
            {
                DataContext = _hudOverlay
            };

            // Subscribe to canvas size changes for speed scale updates
            var canvas = _hudOverlayWindow.GetSpeedScaleCanvas();
            if (canvas != null)
            {
                canvas.SizeChanged += (_, _) => OnPropertyChanged(nameof(FreecamSpeed));
            }

            // Subscribe to mouse events for freecam control
            _hudOverlayWindow.RightButtonDown += OnOverlayRightButtonDown;
            _hudOverlayWindow.RightButtonUp += OnOverlayRightButtonUp;

            // Subscribe to keyboard events for shift key detection
            _hudOverlayWindow.ShiftKeyChanged += OnOverlayShiftKeyChanged;
        }

        if (!_hudOverlayWindow.IsVisible)
        {
            // Show with main window as owner so the overlay is only topmost relative to it
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow == null || !desktop.MainWindow.IsVisible)
                    return;
                _hudOverlayWindow.Show(desktop.MainWindow);
            }
            else
            {
                _hudOverlayWindow.Show();
            }
        }
    }

    private void OnOverlayRightButtonDown(object? sender, EventArgs e)
    {
        RaiseOverlayRightButtonDown();
    }

    private void OnOverlayRightButtonUp(object? sender, EventArgs e)
    {
        RaiseOverlayRightButtonUp();
    }

    /// <summary>
    /// Event raised when right button is pressed on the overlay
    /// </summary>
    public event EventHandler? OverlayRightButtonDown;

    /// <summary>
    /// Event raised when right button is released on the overlay
    /// </summary>
    public event EventHandler? OverlayRightButtonUp;

    /// <summary>
    /// Event raised when shift key state changes on the overlay
    /// </summary>
    public event EventHandler<bool>? OverlayShiftKeyChanged;

    private void RaiseOverlayRightButtonDown()
    {
        OverlayRightButtonDown?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseOverlayRightButtonUp()
    {
        OverlayRightButtonUp?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyOverlayRightButtonDown()
    {
        RaiseOverlayRightButtonDown();
    }

    public void NotifyOverlayRightButtonUp()
    {
        RaiseOverlayRightButtonUp();
    }

    private void OnOverlayShiftKeyChanged(object? sender, bool isPressed)
    {
        OverlayShiftKeyChanged?.Invoke(this, isPressed);
    }

    /// <summary>
    /// Hide the HUD overlay window
    /// </summary>
    public void HideHudOverlay()
    {
        if (_hudOverlayWindow != null && _hudOverlayWindow.IsVisible)
        {
            _hudOverlayWindow.Hide();
        }
    }

    /// <summary>
    /// Update the HUD overlay window position and size to match the shared texture bounds
    /// </summary>
    public void UpdateHudOverlayBounds(PixelPoint position, PixelSize size)
    {
        _hudOverlayWindow?.UpdatePositionAndSize(position, size);
    }

    /// <summary>
    /// Get the SpeedScaleCanvas from the overlay window (for rendering speed scale in D3DHost mode)
    /// </summary>
    public Avalonia.Controls.Canvas? GetOverlaySpeedScaleCanvas()
    {
        return _hudOverlayWindow?.GetSpeedScaleCanvas();
    }

    public void Dispose()
    {
        _isDisposing = true;

        if (_speedWebSocketClient != null)
        {
            _speedWebSocketClient.MessageReceived -= OnWebSocketMessage;
            _speedWebSocketClient.Connected -= OnWebSocketConnected;
        }
        _hudOverlay?.Dispose();
        _hudOverlayWindow?.Close();
        _hudOverlayWindow = null;
        StopStream();
    }

    public IntPtr SharedTextureHandle
    {
        get => _sharedTextureHandle;
        private set
        {
            if (_sharedTextureHandle == value)
                return;
            _sharedTextureHandle = value;
            OnPropertyChanged();
        }
    }

    private void StartRtpInternal(RtpReceiverConfig? config = null)
    {
        IsStreaming = true;
        var activeConfig = config ?? _rtpConfig;
        _rtpConfig = activeConfig;
        RtpStreamRequested?.Invoke(this, activeConfig);
        StatusText = $"Connected - {activeConfig.Address}:{activeConfig.Port}";
        LastFrameReceivedUtc = null;
        _lastFrameTime = DateTime.Now;
        _frameCount = 0;
        _ = RequestRtpIdrAsync();
    }

    private async Task RequestRtpIdrAsync()
    {
        if (_webSocketClient == null)
            return;

        try
        {
            await _webSocketClient.SendCommandAsync("rtp.request_idr");
            Console.WriteLine("RTP IDR requested");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to request RTP IDR: {ex.Message}");
        }
    }

    public double FreecamSpeed
    {
        get => _freecamSpeed;
        private set
        {
            var clamped = Math.Clamp(value, SpeedMin, SpeedMax);
            if (Math.Abs(clamped - _freecamSpeed) < 0.001) return;
            _freecamSpeed = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FreecamSpeedText));
        }
    }

    public string FreecamSpeedText => ((int)Math.Round(FreecamSpeed)).ToString();
    public double SpeedMin => 10.0;
    public double SpeedMax => 1000.0;
    public IReadOnlyList<double> SpeedTicks => _speedTicks;
    public double SprintMultiplier => _freecamSettings?.SprintMultiplier ?? 2.5;

    private IReadOnlyList<double> BuildTicks()
    {
        var ticks = new List<double>();
        const int tickCount = 12; // includes min/max
        double step = (SpeedMax - SpeedMin) / (tickCount - 1);
        for (int i = 0; i < tickCount; i++)
        {
            ticks.Add(SpeedMax - i * step);
        }
        return ticks;
    }

    private void OnWebSocketMessage(object? sender, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var messageType = typeProp.GetString();
            if (string.Equals(messageType, "freecam_speed", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("speed", out var speedProp) &&
                    speedProp.TryGetDouble(out var speed))
                {
                    Dispatcher.UIThread.Post(() => FreecamSpeed = speed, DispatcherPriority.Background);
                }
            }
            else if (string.Equals(messageType, "sharedtex_handle", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("handle", out var handleProp))
                {
                    if (TryParseHandle(handleProp, out var handleValue))
                    {
                        Dispatcher.UIThread.Post(() => SharedTextureHandle = new IntPtr(handleValue), DispatcherPriority.Background);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private void OnWebSocketConnected(object? sender, EventArgs e)
    {
        _ = RequestSharedTextureHandleAsync();
        if (_isStreaming)
        {
            _ = RequestRtpIdrAsync();
        }
    }

    private Task RequestSharedTextureHandleAsync()
    {
        if (_webSocketClient == null || !_webSocketClient.IsConnected)
            return Task.CompletedTask;

        int pid = Process.GetCurrentProcess().Id;
        return _webSocketClient.SendCommandAsync("sharedtex_register", new { pid });
    }

    public void RequestSharedTextureHandle()
    {
        _ = RequestSharedTextureHandleAsync();
    }

    private static bool TryParseHandle(JsonElement handleProp, out long handleValue)
    {
        handleValue = 0;
        try
        {
            if (handleProp.ValueKind == JsonValueKind.String)
            {
                var text = handleProp.GetString();
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    handleValue = Convert.ToInt64(text.Substring(2), 16);
                }
                else
                {
                    handleValue = Convert.ToInt64(text, 10);
                }
                return true;
            }

            if (handleProp.ValueKind == JsonValueKind.Number && handleProp.TryGetInt64(out handleValue))
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Apply a speed multiplier (e.g., 2.0 when Shift is held)
    /// </summary>
    public async void ApplySpeedMultiplier(double multiplier)
    {
        if (_webSocketClient == null)
            return;

        _speedMultiplier = multiplier;
        var effectiveSpeed = _freecamSpeed * _speedMultiplier;

        // Send the modified speed to HLAE
        var command = $"freecam_speed {effectiveSpeed:F1}";
        await _webSocketClient.SendCommandAsync(command);
    }

}
