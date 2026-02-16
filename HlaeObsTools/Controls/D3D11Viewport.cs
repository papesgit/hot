using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Platform;
using System.Numerics;
using Rect = Avalonia.Rect;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.Services.Input;
using System.Collections.ObjectModel;
using HlaeObsTools.ViewModels;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using SharpGen.Runtime;
using HlaeObsTools.Services.Graphics;

namespace HlaeObsTools.Controls;

public sealed class D3D11Viewport : NativeControlHost, IViewport3DControl
{
// TODO: Legacy D3D11 viewport does not implement campath gizmo yet.
// Keep these events to satisfy IViewport3DControl and future feature parity with VRFViewport.
#pragma warning disable CS0067
    public event Action<Vector3, Quaternion>? CampathGizmoPoseChanged;
    public event Action? CampathGizmoDragEnded;
#pragma warning restore CS0067
    private static readonly string LogPath = GetLogPath();
    private static bool _logPathAnnounced;
    private static bool _logWriteFailedLogged;
    private static readonly string WndClassName = $"HLAE_D3DViewportHost_{Guid.NewGuid():N}";
    private static bool _classRegistered;
    private static readonly Dictionary<IntPtr, WeakReference<D3D11Viewport>> _hostMap = new();
    public static readonly StyledProperty<string?> MapPathProperty =
        AvaloniaProperty.Register<D3D11Viewport, string?>(nameof(MapPath));
    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<D3D11Viewport, string?>(nameof(StatusText), string.Empty);
    public static readonly StyledProperty<float> PinScaleProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(PinScale), 200.0f);
    public static readonly StyledProperty<float> PinOffsetZProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(PinOffsetZ), 55.0f);
    public static readonly StyledProperty<float> MapScaleProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(MapScale), 1.0f);
    public static readonly StyledProperty<float> MapYawProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(MapYaw), 0.0f);
    public static readonly StyledProperty<float> MapPitchProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(MapPitch), 0.0f);
    public static readonly StyledProperty<float> MapRollProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(MapRoll), 0.0f);
    public static readonly StyledProperty<float> MapOffsetXProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(MapOffsetX), 0.0f);
    public static readonly StyledProperty<float> MapOffsetYProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(MapOffsetY), 0.0f);
    public static readonly StyledProperty<float> MapOffsetZProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(MapOffsetZ), 0.0f);
    public static readonly StyledProperty<float> ViewportMouseScaleProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(ViewportMouseScale), 0.75f);
    public static readonly StyledProperty<float> ViewportFpsCapProperty =
        AvaloniaProperty.Register<D3D11Viewport, float>(nameof(ViewportFpsCap), 60.0f);
    public static readonly StyledProperty<bool> ShowFpsProperty =
        AvaloniaProperty.Register<D3D11Viewport, bool>(nameof(ShowFps), false);
    public static readonly StyledProperty<FreecamSettings?> FreecamSettingsProperty =
        AvaloniaProperty.Register<D3D11Viewport, FreecamSettings?>(nameof(FreecamSettings));
    public static readonly StyledProperty<HlaeInputSender?> InputSenderProperty =
        AvaloniaProperty.Register<D3D11Viewport, HlaeInputSender?>(nameof(InputSender));

    private ID3D11Device? _device;
    private ID3D11Device1? _device1;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;
    private object? _deviceLock;
    private bool _ownsDevice;
    private IDXGISwapChain1? _swapChain;
    private int _swapWidth;
    private int _swapHeight;
    private IntPtr _hwnd;
    private ID3D11Texture2D? _depthTexture;
    private ID3D11DepthStencilView? _depthStencilView;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11RasterizerState? _rasterizer;
    private ID3D11DepthStencilState? _depthEnabledState;
    private ID3D11DepthStencilState? _depthDisabledState;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ID2D1Bitmap1? _d2dTarget;
    private ID2D1SolidColorBrush? _d2dTextBrush;
    private IDWriteFactory? _dwriteFactory;
    private IDWriteTextFormat? _labelFormat;
    private IDWriteTextFormat? _fpsFormat;
    private int _d2dTargetWidth;
    private int _d2dTargetHeight;
    private ID3D11Buffer? _meshBuffer;
    private int _meshVertexCount;
    private ObjMesh? _pendingMesh;
    private bool _meshDirty;
    private ObjMesh? _loadedMeshOriginal;
    private ID3D11Buffer? _gridBuffer;
    private int _gridVertexCount;
    private ID3D11Buffer? _groundBuffer;
    private int _groundVertexCount;
    private ID3D11Buffer? _debugBuffer;
    private int _debugVertexCount = 0;
    private ID3D11Buffer? _pinBuffer;
    private int _pinVertexCount;
    private int _renderWidth;
    private int _renderHeight;
    private int _targetWidth;
    private int _targetHeight;
    private bool _d3dReady;
    private bool _renderLogged;
    private bool _renderStatsLogged;
    private bool _swapchainLogged;
    private bool _renderSizeLogged;
    private bool _deviceLogged;
    private bool _swapchainFailureLogged;
    private bool _nativeInitDone;
    private float _viewportFpsCapCached;
    private bool _showFpsCached;
    private long _fpsLastTicks;
    private double _fpsAccumMs;
    private int _fpsFrameCount;
    private float _fpsValue;
    private string _statusPrefix = string.Empty;
    private bool _showDebugTriangle = false;
    private bool _showGroundPlane = true;
    private string _inputStatus = "Input: idle";
    private readonly Vector3 _lightDir = Vector3.Normalize(new Vector3(0.4f, 0.9f, 0.2f));
    private const float AmbientLight = 0.25f;
    private List<PinRenderData> _pins = new();
    private readonly List<PinDrawCall> _pinDraws = new();
    private List<PinLabel> _pinLabels = new();
    private readonly object _labelLock = new();
    private List<PinLabel> _labelHitCache = new();
    private bool _pinsDirty;
    private readonly Vector3[] _pinConeUnit = CreateUnitCone();
    private readonly Vector3[] _pinConeNormals = CreateUnitConeNormals();
    private readonly Vector3[] _pinSphereUnit;
    private readonly Vector3[] _pinSphereNormals;
    private const int VertexStride = 6 * sizeof(float);

    private Vector3 _target = Vector3.Zero;
    private float _distance = 10f;
    private float _yaw = DegToRad(45f);
    private float _pitch = DegToRad(30f);
    private float _minDistance = 0.5f;
    private float _maxDistance = 1000f;
    private Vector3 _orbitTargetBeforeFreecam;
    private float _orbitYawBeforeFreecam;
    private float _orbitPitchBeforeFreecam;
    private float _orbitDistanceBeforeFreecam;
    private bool _orbitStateSaved;

    private bool _dragging;
    private bool _panning;
    private Point _lastPointer;
    private Point _freecamCenterLocal;
    private PixelPoint _freecamCenterScreen;
    private bool _freecamCursorHidden;
    private bool _freecamActive;
    private bool _freecamInputEnabled;
    private bool _freecamInitialized;
    private bool _freecamIgnoreNextDelta;
    private float _freecamSpeedScalar = 1.0f;
    private bool _lastMouseButton4;
    private bool _lastMouseButton5;
    private float _mouseButton4Hold;
    private float _mouseButton5Hold;
    private float _freecamMouseVelocityX;
    private float _freecamMouseVelocityY;
    private float _freecamTargetRoll;
    private float _freecamCurrentRoll;
    private float _freecamRollVelocity;
    private float _freecamLastLateralVelocity;
    private Quaternion _freecamRawQuat = Quaternion.Identity;
    private Quaternion _freecamSmoothedQuat = Quaternion.Identity;
    private Vector3 _freecamRotVelocity = Vector3.Zero;
    private Vector3 _freecamLastSmoothedPosition;
    private Vector2 _freecamMouseDelta;
    private float _freecamWheelDelta;
    private DateTime _freecamLastUpdate;
    private FreecamTransform _freecamTransform;
    private FreecamTransform _freecamSmoothed;
    private FreecamConfig _freecamConfig = FreecamConfig.Default;
    private FreecamSettings? _freecamSettings;
    private bool _freecamPreviewRollOverrideActive;
    private float _freecamPreviewRollOverride;
    private long _lastFrameTimestamp;
    private bool _externalCameraActive;
    private Vector3 _externalCameraPosition;
    private Quaternion _externalCameraRotation = Quaternion.Identity;
    private float _externalCameraFov = 90f;
    private HlaeInputSender? _inputSender;
    private readonly HashSet<Key> _keysDown = new();
    private bool _mouseButton4Down;
    private bool _mouseButton5Down;
    private readonly Stopwatch _frameLimiter = Stopwatch.StartNew();
    private long _lastFrameTicks;
    private DispatcherTimer? _frameLimiterTimer;
    private bool _frameLimiterPending;
    private CancellationTokenSource? _renderCts;
    private Task? _renderLoop;
    private readonly ManualResetEventSlim _renderSignal = new(false);

    public D3D11Viewport()
    {
        Focusable = true;
        IsHitTestVisible = true;
        StatusText = "D3D11 init pending...";
        Labels = new ReadOnlyObservableCollection<PinLabel>(_labels);
        (_pinSphereUnit, _pinSphereNormals) = CreateUnitSphere(16, 32);
    }

    static D3D11Viewport()
    {
        MapPathProperty.Changed.AddClassHandler<D3D11Viewport>((sender, args) => sender.OnMapPathChanged(args));
        PinScaleProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnPinScaleChanged());
        PinOffsetZProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnPinOffsetChanged());
        MapScaleProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnMapTransformChanged());
        MapYawProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnMapTransformChanged());
        MapPitchProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnMapTransformChanged());
        MapRollProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnMapTransformChanged());
        MapOffsetXProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnMapTransformChanged());
        MapOffsetYProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnMapTransformChanged());
        MapOffsetZProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnMapTransformChanged());
        FreecamSettingsProperty.Changed.AddClassHandler<D3D11Viewport>((sender, args) => sender.OnFreecamSettingsChanged(args));
        InputSenderProperty.Changed.AddClassHandler<D3D11Viewport>((sender, args) => sender.OnInputSenderChanged(args));
        ViewportFpsCapProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnViewportFpsCapChanged());
        ShowFpsProperty.Changed.AddClassHandler<D3D11Viewport>((sender, _) => sender.OnShowFpsChanged());
    }

    public string? MapPath
    {
        get => GetValue(MapPathProperty);
        set => SetValue(MapPathProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public float PinScale
    {
        get => GetValue(PinScaleProperty);
        set => SetValue(PinScaleProperty, value);
    }

    public float PinOffsetZ
    {
        get => GetValue(PinOffsetZProperty);
        set => SetValue(PinOffsetZProperty, value);
    }

    public float MapScale
    {
        get => GetValue(MapScaleProperty);
        set => SetValue(MapScaleProperty, value);
    }

    public float MapYaw
    {
        get => GetValue(MapYawProperty);
        set => SetValue(MapYawProperty, value);
    }

    public float MapPitch
    {
        get => GetValue(MapPitchProperty);
        set => SetValue(MapPitchProperty, value);
    }

    public float MapRoll
    {
        get => GetValue(MapRollProperty);
        set => SetValue(MapRollProperty, value);
    }

    public float MapOffsetX
    {
        get => GetValue(MapOffsetXProperty);
        set => SetValue(MapOffsetXProperty, value);
    }

    public float MapOffsetY
    {
        get => GetValue(MapOffsetYProperty);
        set => SetValue(MapOffsetYProperty, value);
    }

    public float MapOffsetZ
    {
        get => GetValue(MapOffsetZProperty);
        set => SetValue(MapOffsetZProperty, value);
    }

    public float ViewportMouseScale
    {
        get => GetValue(ViewportMouseScaleProperty);
        set => SetValue(ViewportMouseScaleProperty, value);
    }

    /// <summary>
    /// FPS cap for the 3D viewport (0 = uncapped).
    /// </summary>
    public float ViewportFpsCap
    {
        get => GetValue(ViewportFpsCapProperty);
        set => SetValue(ViewportFpsCapProperty, value);
    }

    public bool ShowFps
    {
        get => GetValue(ShowFpsProperty);
        set => SetValue(ShowFpsProperty, value);
    }

    public FreecamSettings? FreecamSettings
    {
        get => GetValue(FreecamSettingsProperty);
        set => SetValue(FreecamSettingsProperty, value);
    }

    public HlaeInputSender? InputSender
    {
        get => GetValue(InputSenderProperty);
        set => SetValue(InputSenderProperty, value);
    }

    private readonly ObservableCollection<PinLabel> _labels = new();
    public ReadOnlyObservableCollection<PinLabel> Labels { get; }

    public bool IsFreecamActive => _freecamActive;
    public bool IsFreecamInputEnabled => _freecamInputEnabled;

    public event Action<double>? FrameTick;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
            return base.CreateNativeControlCore(parent);

        _hwnd = CreateChildWindow(parent.Handle);
        if (_hwnd == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            LogMessage($"CreateNativeControlCore failed: parent=0x{parent.Handle.ToInt64():X} error={error}");
            return base.CreateNativeControlCore(parent);
        }

        RegisterHostWindow(_hwnd, this);
        UpdateChildWindowSize();
        LogMessage($"CreateNativeControlCore hwnd=0x{_hwnd.ToInt64():X} parent=0x{parent.Handle.ToInt64():X}");
        InitializeAfterNativeCreated();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopRenderLoop();
        ReleaseSwapChain();
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
        UpdateChildWindowSize();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResetLogFile();
        _viewportFpsCapCached = ViewportFpsCap;
        _showFpsCached = ShowFps;
        ResetFpsCounter();
        if (_hwnd != IntPtr.Zero)
        {
            InitializeAfterNativeCreated();
        }
        else
        {
            Dispatcher.UIThread.Post(InitializeAfterNativeCreated, DispatcherPriority.Loaded);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _dragging = false;
        _panning = false;
        _nativeInitDone = false;
        DisableFreecam();
        _keysDown.Clear();
        _frameLimiterTimer?.Stop();
        _frameLimiterPending = false;
        StopRenderLoop();
        DisposeD3D();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
        {
            UpdateChildWindowSize();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _keysDown.Add(e.Key);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keysDown.Remove(e.Key);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        HandlePointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        HandlePointerReleased(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HandlePointerMoved(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        HandlePointerWheel(e);
    }

    public void ForwardPointerPressed(PointerPressedEventArgs e)
    {
        HandlePointerPressed(e);
    }

    public void ForwardPointerReleased(PointerReleasedEventArgs e)
    {
        HandlePointerReleased(e);
    }

    public void ForwardPointerMoved(PointerEventArgs e)
    {
        HandlePointerMoved(e);
    }

    public void ForwardPointerWheel(PointerWheelEventArgs e)
    {
        HandlePointerWheel(e);
    }

    public bool TryGetFreecamState(out ViewportFreecamState state)
    {
        if (!_freecamActive)
        {
            state = default;
            return false;
        }

        GetFreecamBasis(_freecamTransform, out var rawForward, out var rawUp);
        GetFreecamBasis(_freecamSmoothed, out var smoothForward, out var smoothUp);
        state = new ViewportFreecamState
        {
            RawPosition = _freecamTransform.Position,
            RawForward = Vector3.Normalize(rawForward),
            RawUp = Vector3.Normalize(rawUp),
            RawOrientation = _freecamTransform.Orientation,
            RawPitch = _freecamTransform.Pitch,
            RawYaw = _freecamTransform.Yaw,
            RawRoll = _freecamTransform.Roll,
            RawFov = _freecamTransform.Fov,
            SmoothedPosition = _freecamSmoothed.Position,
            SmoothedForward = Vector3.Normalize(smoothForward),
            SmoothedUp = Vector3.Normalize(smoothUp),
            SmoothedOrientation = _freecamSmoothed.Orientation,
            SmoothedFov = _freecamSmoothed.Fov,
            SpeedScalar = _freecamSpeedScalar
        };
        return true;
    }

    public void DisableFreecamInput()
    {
        EndFreecamInput();
    }

    public void SetExternalCamera(Vector3 position, Quaternion rotation, float fov)
    {
        _externalCameraPosition = position;
        _externalCameraRotation = Quaternion.Normalize(rotation);
        _externalCameraFov = fov;
        _externalCameraActive = true;
        RequestNextFrame();
    }

    public void ClearExternalCamera()
    {
        if (!_externalCameraActive)
            return;

        _externalCameraActive = false;
        RequestNextFrame();
    }

    public void SetFreecamPose(Vector3 position, Quaternion rotation, float fov)
    {
        var wasActive = _freecamActive;
        if (!_freecamActive)
            BeginFreecam(new Point(Bounds.Width * 0.5, Bounds.Height * 0.5));

        _freecamTransform.Position = position;
        _freecamTransform.Orientation = Quaternion.Normalize(rotation);
        UpdateAnglesFromQuat(_freecamTransform.Orientation, ref _freecamTransform);
        _freecamTransform.Fov = fov;
        _freecamPreviewRollOverride = _freecamTransform.Roll;
        _freecamPreviewRollOverrideActive = true;
        _freecamCurrentRoll = _freecamTransform.Roll;
        _freecamTargetRoll = _freecamTransform.Roll;
        _freecamRollVelocity = 0.0f;

        _freecamSmoothed = _freecamTransform;
        _freecamSmoothedQuat = _freecamSmoothed.Orientation;
        ResetFreecamState();
        if (!wasActive)
            EndFreecamInput();
        RequestNextFrame();
    }

    public void ClearFreecamPreview()
    {
        _freecamPreviewRollOverrideActive = false;
    }

    private void HandlePointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed || updateKind == PointerUpdateKind.MiddleButtonPressed;
        var rightPressed = point.Properties.IsRightButtonPressed || updateKind == PointerUpdateKind.RightButtonPressed;
        var leftPressed = point.Properties.IsLeftButtonPressed || updateKind == PointerUpdateKind.LeftButtonPressed;
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        UpdateInputStatus($"Input: down M:{middlePressed} Shift:{e.KeyModifiers.HasFlag(KeyModifiers.Shift)}");

        if (leftPressed && TryHandlePinClick(point.Position))
        {
            Focus();
            e.Handled = true;
            return;
        }

        if (rightPressed)
        {
            BeginFreecam(point.Position);
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
            return;
        }

        if (!middlePressed)
            return;

        if (_freecamActive)
            DisableFreecam();

        _dragging = true;
        _panning = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    private bool TryHandlePinClick(Point position)
    {
        if (_pins.Count == 0)
            return false;

        if (TryFindPinFromLabelHit(position, out var labelPin))
        {
            ActivateFreecamAtPin(labelPin);
            return true;
        }

        if (TryFindPinFromMarkerHit(position, out var markerPin))
        {
            ActivateFreecamAtPin(markerPin);
            return true;
        }

        return false;
    }

    private bool TryFindPinFromLabelHit(Point position, out PinRenderData pin)
    {
        pin = default!;
        List<PinLabel> labels;
        lock (_labelLock)
        {
            if (_labelHitCache.Count == 0)
                return false;
            labels = new List<PinLabel>(_labelHitCache);
        }

        const double fontSize = 16.0;
        const double fontWidthFactor = 0.6;
        const double padding = 6.0;

        foreach (var label in labels)
        {
            if (string.IsNullOrEmpty(label.Text))
                continue;

            var width = Math.Max(1.0, label.Text.Length * fontSize * fontWidthFactor) + padding;
            var height = fontSize * 1.2 + padding;
            var halfW = width * 0.5;
            var halfH = height * 0.5;

            if (Math.Abs(position.X - label.ScreenX) <= halfW && Math.Abs(position.Y - label.ScreenY) <= halfH)
            {
                for (int i = 0; i < _pins.Count; i++)
                {
                    if (string.Equals(_pins[i].Label, label.Text, StringComparison.Ordinal))
                    {
                        pin = _pins[i];
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool TryFindPinFromMarkerHit(Point position, out PinRenderData pin)
    {
        pin = default!;
        var width = Math.Max(1, (int)Bounds.Width);
        var height = Math.Max(1, (int)Bounds.Height);
        if (width <= 0 || height <= 0)
            return false;

        var viewProjection = CreateViewProjection(width, height);
        const double hitRadius = 12.0;
        var hitRadiusSq = hitRadius * hitRadius;
        var bestDistSq = double.MaxValue;
        var found = false;

        foreach (var candidate in _pins)
        {
            if (!TryProjectToScreen(candidate.Position, viewProjection, width, height, out var screen))
                continue;

            var dx = position.X - screen.X;
            var dy = position.Y - screen.Y;
            var distSq = dx * dx + dy * dy;
            if (distSq <= hitRadiusSq && distSq < bestDistSq)
            {
                bestDistSq = distSq;
                pin = candidate;
                found = true;
            }
        }

        return found;
    }

    private void ActivateFreecamAtPin(PinRenderData pin)
    {
        var keepInputEnabled = _freecamInputEnabled;
        if (!_freecamActive)
        {
            _orbitTargetBeforeFreecam = _target;
            _orbitYawBeforeFreecam = _yaw;
            _orbitPitchBeforeFreecam = _pitch;
            _orbitDistanceBeforeFreecam = _distance;
            _orbitStateSaved = true;
        }

        var forward = pin.Forward;
        if (forward.LengthSquared() < 0.0001f)
            forward = Vector3.UnitX;
        forward = Vector3.Normalize(forward);

        GetYawPitchFromForward(forward, out var yaw, out var pitch);
        var fov = _freecamActive ? _freecamTransform.Fov : _freecamConfig.DefaultFov;

        var forwardFromAngles = GetForwardVector(pitch, yaw);
        var right = Vector3.Cross(forwardFromAngles, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-6f)
            right = Vector3.Cross(forwardFromAngles, Vector3.UnitX);
        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, forwardFromAngles));
        var roll = ComputeRollForUp(pitch, yaw, up);

        _freecamTransform = new FreecamTransform
        {
            Position = pin.Position,
            Yaw = yaw,
            Pitch = pitch,
            Roll = roll,
            Fov = fov,
            Orientation = BuildQuat(pitch, yaw, roll)
        };
        _freecamSmoothed = _freecamTransform;
        _freecamActive = true;
        _freecamInitialized = true;
        _freecamInputEnabled = keepInputEnabled;
        _freecamLastUpdate = DateTime.UtcNow;
        ResetFreecamState();
        RequestNextFrame();
    }

    private void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed;
        var rightPressed = point.Properties.IsRightButtonPressed;
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        var rightReleased = updateKind == PointerUpdateKind.RightButtonReleased || !rightPressed;
        if (_freecamActive && rightReleased)
        {
            EndFreecamInput();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_dragging)
            return;

        var released = updateKind == PointerUpdateKind.MiddleButtonReleased || (!middlePressed);

        if (released)
        {
            _dragging = false;
            _panning = false;
            e.Pointer.Capture(null);
            UpdateInputStatus("Input: up");
        }
    }

    private void HandlePointerMoved(PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        if (_freecamActive && _freecamInputEnabled)
        {
            if (_freecamIgnoreNextDelta)
            {
                _freecamIgnoreNextDelta = false;
                CenterFreecamCursor();
                UpdateInputStatus("Input: freecam");
                RequestNextFrame();
                e.Handled = true;
                return;
            }

            var scale = MathF.Max(0.01f, ViewportMouseScale);
            var dx = (float)(point.Position.X - _freecamCenterLocal.X) * scale;
            var dy = (float)(point.Position.Y - _freecamCenterLocal.Y) * scale;
            if (dx != 0 || dy != 0)
                _freecamMouseDelta += new Vector2(dx, dy);
            CenterFreecamCursor();
            UpdateInputStatus("Input: freecam");
            RequestNextFrame();
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            UpdateInputStatus("Input: move");
            return;
        }

        var pos = point.Position;
        var delta = pos - _lastPointer;
        _lastPointer = pos;

        if (_panning)
        {
            Pan((float)delta.X, (float)delta.Y);
        }
        else
        {
            Orbit((float)delta.X, (float)delta.Y);
        }

        UpdateInputStatus("Input: drag");
        RequestNextFrame();
        e.Handled = true;
    }

    private void HandlePointerWheel(PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < double.Epsilon)
            return;

        if (_freecamActive && _freecamInputEnabled)
        {
            _freecamWheelDelta += (float)e.Delta.Y;
            UpdateInputStatus($"Input: freecam wheel {e.Delta.Y:0.##}");
            RequestNextFrame();
            e.Handled = true;
            return;
        }
        if (_freecamActive)
            return;

        var zoomFactor = MathF.Pow(1.1f, (float)-e.Delta.Y);
        _distance = Math.Min(_distance * zoomFactor, _maxDistance);
        if (_distance < 0.0001f)
            _distance = 0.0001f;
        UpdateInputStatus($"Input: wheel {e.Delta.Y:0.##}");
        RequestNextFrame();
        e.Handled = true;
    }

    private void RenderFrame()
    {
        if (!EnsureDevice())
        {
            LogMessage("D3D11 init failed");
            return;
        }
        if (!_renderLogged)
        {
            _renderLogged = true;
            LogMessage("RenderFrame entered");
        }

        var deviceLock = _deviceLock;
        if (deviceLock != null)
            Monitor.Enter(deviceLock);
        try
        {
        var width = Math.Max(1, _targetWidth);
        var height = Math.Max(1, _targetHeight);
        if (!EnsureSwapChain(width, height))
            return;
        if (!EnsureDepthStencil(width, height))
            return;

        if (!_renderStatsLogged)
        {
            _renderStatsLogged = true;
            LogMessage($"Render targets ready: target={width}x{height} swap={_swapWidth}x{_swapHeight} debugVerts={_debugVertexCount}");
        }
        if (!_renderSizeLogged)
        {
            _renderSizeLogged = true;
            LogMessage($"RenderFrame size: {width}x{height} hwnd=0x{_hwnd.ToInt64():X}");
        }

        UpdateFrameTick();
        UpdateFreecamForFrame();
        UpdateFpsCounter();

        if (_meshDirty)
            UploadPendingMesh();
        if (_pinsDirty)
            RebuildPins();

        if (_context == null || _swapChain == null || _depthStencilView == null)
            return;
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var renderTargetView = _device!.CreateRenderTargetView(backBuffer);
        _context.OMSetRenderTargets(renderTargetView, _depthStencilView);
        _context.RSSetViewport(new Viewport(0, 0, width, height, 0.0f, 1.0f));
        _context.ClearRenderTargetView(renderTargetView, new Color4(0.02f, 0.02f, 0.03f, 1f));
        _context.ClearDepthStencilView(_depthStencilView, DepthStencilClearFlags.Depth, 1.0f, 0);

        _context.RSSetState(_rasterizer);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.IASetInputLayout(_inputLayout);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetConstantBuffer(0, _constantBuffer);

        var viewProjection = CreateViewProjection(width, height);

        if (_showGroundPlane && _groundVertexCount > 0 && _groundBuffer != null)
        {
            ApplyConstants(viewProjection, new Vector3(0.12f, 0.14f, 0.16f));
            DrawGeometry(_groundBuffer, _groundVertexCount, PrimitiveTopology.TriangleList, depthEnabled: true);
        }

        if (_gridVertexCount > 0 && _gridBuffer != null)
        {
            ApplyConstants(viewProjection, new Vector3(0.35f, 0.5f, 0.35f));
            DrawGeometry(_gridBuffer, _gridVertexCount, PrimitiveTopology.LineList, depthEnabled: false);
        }

        if (_meshVertexCount > 0 && _meshBuffer != null)
        {
            ApplyConstants(viewProjection, new Vector3(0.82f, 0.86f, 0.9f));
            DrawGeometry(_meshBuffer, _meshVertexCount, PrimitiveTopology.TriangleList, depthEnabled: true);
        }

        if (_pinVertexCount > 0 && _pinBuffer != null)
        {
            foreach (var draw in _pinDraws)
            {
                ApplyConstants(viewProjection, draw.Color);
                DrawGeometry(_pinBuffer, draw.Count, PrimitiveTopology.TriangleList, depthEnabled: true, startVertex: draw.Start);
            }
        }

        DrawLabelOverlay(viewProjection, width, height, backBuffer);
        _context.Flush();
        try
        {
            _swapChain.Present(0, PresentFlags.AllowTearing);
        }
        catch (SharpGenException ex)
        {
            LogMessage($"Present failed: 0x{ex.ResultCode.Code:X8}");
            ReleaseSwapChain();
        }
        }
        finally
        {
            if (deviceLock != null)
                Monitor.Exit(deviceLock);
        }
    }

    private void UpdateFrameTick()
    {
        if (FrameTick == null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp == 0)
        {
            _lastFrameTimestamp = now;
            return;
        }

        var delta = (float)Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds;
        _lastFrameTimestamp = now;
        if (delta <= 0f)
            return;

        FrameTick(delta);
    }

    private void OnMapPathChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var path = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            _pendingMesh = null;
            _loadedMeshOriginal = null;
            _meshDirty = true;
            RequestNextFrame();
            return;
        }

        if (ObjMeshLoader.TryLoad(path, out var mesh, out _))
        {
            _loadedMeshOriginal = mesh;
            if (mesh == null)
            {
                SetStatusText("Failed to load map: mesh is null");
                return;
            }

            _pendingMesh = ApplyMapTransform(mesh);
            _meshDirty = true;
            RequestNextFrame();
            return;
        }

        _pendingMesh = null;
        _loadedMeshOriginal = null;
        _meshDirty = true;
        RequestNextFrame();
    }

    private void OnViewportFpsCapChanged()
    {
        _viewportFpsCapCached = ViewportFpsCap;
        _lastFrameTicks = _frameLimiter.ElapsedTicks;
        if (_frameLimiterTimer != null)
            _frameLimiterTimer.Stop();
        _frameLimiterPending = false;
        RequestNextFrame();
    }

    private void OnShowFpsChanged()
    {
        _showFpsCached = ShowFps;
        ResetFpsCounter();
        RequestNextFrame();
    }

    private void ResetFpsCounter()
    {
        _fpsLastTicks = 0;
        _fpsAccumMs = 0;
        _fpsFrameCount = 0;
        _fpsValue = 0;
    }

    private void UpdateFpsCounter()
    {
        if (!_showFpsCached)
            return;

        var nowTicks = _frameLimiter.ElapsedTicks;
        if (_fpsLastTicks == 0)
        {
            _fpsLastTicks = nowTicks;
            return;
        }

        var deltaMs = (nowTicks - _fpsLastTicks) * 1000.0 / Stopwatch.Frequency;
        _fpsLastTicks = nowTicks;
        if (deltaMs <= 0)
            return;

        _fpsAccumMs += deltaMs;
        _fpsFrameCount++;
        if (_fpsAccumMs >= 250.0)
        {
            _fpsValue = (float)(_fpsFrameCount / (_fpsAccumMs / 1000.0));
            _fpsAccumMs = 0;
            _fpsFrameCount = 0;
        }
    }

    private void OnMapTransformChanged()
    {
        if (_loadedMeshOriginal == null)
            return;

        _pendingMesh = ApplyMapTransform(_loadedMeshOriginal);
        _meshDirty = true;
        RequestNextFrame();
    }

    private void UploadPendingMesh()
    {
        _meshDirty = false;

        if (_pendingMesh == null)
        {
            _meshBuffer?.Dispose();
            _meshBuffer = null;
            _meshVertexCount = 0;
            return;
        }

        _meshBuffer?.Dispose();
        _meshBuffer = CreateVertexBuffer(_pendingMesh.Vertices, dynamic: false);
        _meshVertexCount = _pendingMesh.VertexCount;
        ResetCameraToBounds(_pendingMesh.Min, _pendingMesh.Max);
        UpdateGridFromBounds(_pendingMesh.Min, _pendingMesh.Max);
        _pendingMesh = null;
    }

    private void OnPinScaleChanged()
    {
        _pinsDirty = true;
        RequestNextFrame();
    }

    private void OnPinOffsetChanged()
    {
        _pinsDirty = true;
        RequestNextFrame();
    }

    private void OnFreecamSettingsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_freecamSettings != null)
            _freecamSettings.PropertyChanged -= OnFreecamSettingsPropertyChanged;

        _freecamSettings = e.NewValue as FreecamSettings;
        if (_freecamSettings != null)
            _freecamSettings.PropertyChanged += OnFreecamSettingsPropertyChanged;

        ApplyFreecamSettings();
    }

    private void OnInputSenderChanged(AvaloniaPropertyChangedEventArgs e)
    {
        _inputSender = e.NewValue as HlaeInputSender;
    }

    private void OnFreecamSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplyFreecamSettings();
    }

    private void ApplyFreecamSettings()
    {
        if (_freecamSettings == null)
        {
            _freecamConfig = FreecamConfig.Default;
            return;
        }

        _freecamConfig = new FreecamConfig
        {
            MouseSensitivity = (float)_freecamSettings.MouseSensitivity,
            MoveSpeed = (float)_freecamSettings.MoveSpeed,
            SprintMultiplier = (float)_freecamSettings.SprintMultiplier,
            VerticalSpeed = (float)_freecamSettings.VerticalSpeed,
            SpeedAdjustRate = (float)_freecamSettings.SpeedAdjustRate,
            SpeedMinMultiplier = (float)_freecamSettings.SpeedMinMultiplier,
            SpeedMaxMultiplier = (float)_freecamSettings.SpeedMaxMultiplier,
            RollSpeed = (float)_freecamSettings.RollSpeed,
            RollSmoothing = (float)_freecamSettings.RollSmoothing,
            LeanStrength = (float)_freecamSettings.LeanStrength,
            LeanAccelScale = (float)_freecamSettings.LeanAccelScale,
            LeanVelocityScale = (float)_freecamSettings.LeanVelocityScale,
            LeanMaxAngle = (float)_freecamSettings.LeanMaxAngle,
            LeanHalfTime = (float)_freecamSettings.LeanHalfTime,
            ClampPitch = _freecamSettings.ClampPitch,
            FovMin = (float)_freecamSettings.FovMin,
            FovMax = (float)_freecamSettings.FovMax,
            FovStep = (float)_freecamSettings.FovStep,
            DefaultFov = (float)_freecamSettings.DefaultFov,
            SmoothEnabled = _freecamSettings.SmoothEnabled,
            HalfVec = (float)_freecamSettings.HalfVec,
            HalfRot = (float)_freecamSettings.HalfRot,
            HalfFov = (float)_freecamSettings.HalfFov,
            RotCriticalDamping = _freecamSettings.RotCriticalDamping,
            RotDampingRatio = (float)_freecamSettings.RotDampingRatio
        };
    }

    public void SetPins(IReadOnlyList<ViewportPin> pins)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var snapshot = pins.ToArray();
            Dispatcher.UIThread.Post(() => SetPins(snapshot));
            return;
        }

        var pinOffset = new Vector3(0f, 0f, PinOffsetZ);

        var list = new List<PinRenderData>();
        foreach (var pin in pins)
        {
            var position = new Vector3((float)pin.Position.X, (float)pin.Position.Y, (float)pin.Position.Z);
            var forward = new Vector3((float)pin.Forward.X, (float)pin.Forward.Y, (float)pin.Forward.Z);
            position += pinOffset;

            list.Add(new PinRenderData
            {
                Position = position,
                Forward = forward,
                Color = GetTeamColor(pin.Team),
                Label = pin.Label
            });
        }

        _pins = list;
        _pinsDirty = true;
        RequestNextFrame();
    }

    public void SetCampathOverlay(CampathOverlayData? data)
    {
        // Not supported in the legacy D3D11 viewport yet.
    }

    public void SetCampathGizmo(CampathGizmoState? state)
    {
        // Not supported in the legacy D3D11 viewport yet.
    }

    private static Vector3 GetTeamColor(string team)
    {
        if (string.Equals(team, "CT", StringComparison.OrdinalIgnoreCase))
            return new Vector3(0.35f, 0.65f, 1.0f);
        if (string.Equals(team, "T", StringComparison.OrdinalIgnoreCase))
            return new Vector3(1.0f, 0.7f, 0.2f);
        return new Vector3(0.8f, 0.8f, 0.8f);
    }

    private void SetStatusText(string? text)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            StatusText = text;
        }
        else
        {
            Dispatcher.UIThread.Post(() => StatusText = text);
        }
    }

    private void UpdateStatusText()
    {
        var gridInfo = _gridVertexCount > 0 ? $"Grid verts: {_gridVertexCount}" : "Grid: none";
        var groundInfo = _groundVertexCount > 0 ? $"Ground verts: {_groundVertexCount}" : "Ground: none";
        var debugInfo = _showDebugTriangle ? "Debug: on" : "Debug: off";
        var prefix = string.IsNullOrWhiteSpace(_statusPrefix) ? "D3D11 ready" : _statusPrefix;
        SetStatusText($"{prefix} | {gridInfo} | {groundInfo} | {debugInfo} | {_inputStatus}");
    }

    private void UpdateInputStatus(string status)
    {
        _inputStatus = status;
        UpdateStatusText();
    }

    private void ResetCameraToBounds(Vector3 min, Vector3 max)
    {
        _target = Vector3.Zero;
        var radius = (max - min).Length() * 0.5f;
        if (radius < 0.1f)
            radius = 0.1f;

        _distance = radius * 2.0f;
        _minDistance = radius * 0.2f;
        _maxDistance = radius * 20f;

        if (_distance < _minDistance)
            _distance = _minDistance;
        if (_distance > _maxDistance)
            _distance = _maxDistance;

        _yaw = DegToRad(45f);
        _pitch = DegToRad(30f);
    }

    private void UpdateGridFromBounds(Vector3 min, Vector3 max)
    {
        var extent = max - min;
        var maxExtent = MathF.Max(MathF.Max(extent.X, extent.Y), extent.Z);
        var size = MathF.Max(2f, maxExtent * 1.2f);
        UpdateGrid(size, 20);
    }

    private void UpdateGrid(float size, int divisions)
    {
        if (!EnsureDevice())
            return;

        var half = size * 0.5f;
        var lines = divisions + 1;
        var vertices = new float[lines * 4 * 6];
        var step = size / divisions;
        var index = 0;

        for (var i = 0; i < lines; i++)
        {
            var offset = -half + i * step;

            AddVertex(vertices, ref index, -half, offset, 0f, 0f, 0f, 1f);
            AddVertex(vertices, ref index, half, offset, 0f, 0f, 0f, 1f);

            AddVertex(vertices, ref index, offset, -half, 0f, 0f, 0f, 1f);
            AddVertex(vertices, ref index, offset, half, 0f, 0f, 0f, 1f);
        }

        _gridBuffer?.Dispose();
        _gridBuffer = CreateVertexBuffer(vertices, dynamic: false);
        _gridVertexCount = vertices.Length / 6;
        RequestNextFrame();
        UpdateStatusText();
    }

    private Vector3 GetCameraPosition()
    {
        var cosPitch = MathF.Cos(_pitch);
        var sinPitch = MathF.Sin(_pitch);
        var cosYaw = MathF.Cos(_yaw);
        var sinYaw = MathF.Sin(_yaw);

        var direction = new Vector3(cosPitch * cosYaw, cosPitch * sinYaw, sinPitch);
        return _target + direction * _distance;
    }

    private Matrix4x4 CreateViewProjection(int width, int height)
    {
        var aspect = width / (float)height;
        if (_externalCameraActive)
        {
            var fov = GetSourceVerticalFovRadians(_externalCameraFov);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.05f, 100000f);
            var forward = GetForwardFromQuat(_externalCameraRotation);
            var up = GetUpFromQuat(_externalCameraRotation);
            var view = Matrix4x4.CreateLookAt(_externalCameraPosition, _externalCameraPosition + forward, up);
            return view * projection;
        }

        if (_freecamActive)
        {
            var fov = GetSourceVerticalFovRadians(_freecamSmoothed.Fov);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.05f, 100000f);
            var view = CreateFreecamView(_freecamSmoothed);
            return view * projection;
        }

        var nearPlane = Math.Max(0.05f, _distance * 0.01f);
        var farPlane = Math.Max(100f, _distance * 10f);
        var projectionOrbit = Matrix4x4.CreatePerspectiveFieldOfView(DegToRad(60f), aspect, nearPlane, farPlane);
        var viewOrbit = Matrix4x4.CreateLookAt(GetCameraPosition(), _target, Vector3.UnitZ);
        return viewOrbit * projectionOrbit;
    }

    private static float GetSourceVerticalFovRadians(float sourceFovDeg)
    {
        var hRad = DegToRad(Math.Clamp(sourceFovDeg, 1.0f, 179.0f));
        var vRad = 2f * MathF.Atan(MathF.Tan(hRad * 0.5f) * (3f / 4f));
        return Math.Clamp(vRad, DegToRad(1.0f), DegToRad(179.0f));
    }

    private void ApplyConstants(Matrix4x4 mvp, Vector3 color)
    {
        if (_context == null || _constantBuffer == null)
            return;

        var constants = new ShaderConstants
        {
            Mvp = mvp,
            Color = new Vector4(color.X, color.Y, color.Z, 1f),
            LightDirAmbient = new Vector4(_lightDir.X, _lightDir.Y, _lightDir.Z, AmbientLight)
        };

        _context.UpdateSubresource(in constants, _constantBuffer);
    }

    private void Orbit(float deltaX, float deltaY)
    {
        const float rotateSpeed = 0.01f;
        _yaw -= deltaX * rotateSpeed;
        _pitch += deltaY * rotateSpeed;
        _pitch = Math.Clamp(_pitch, -1.55f, 1.55f);
    }

    private void Pan(float deltaX, float deltaY)
    {
        var cameraPos = GetCameraPosition();
        var forward = Vector3.Normalize(_target - cameraPos);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        var panScale = _distance * 0.001f;
        _target += (-right * deltaX + up * deltaY) * panScale;
    }

    private void BeginFreecam(Point start)
    {
        if (!_freecamActive)
        {
            _orbitTargetBeforeFreecam = _target;
            _orbitYawBeforeFreecam = _yaw;
            _orbitPitchBeforeFreecam = _pitch;
            _orbitDistanceBeforeFreecam = _distance;
            _orbitStateSaved = true;

            if (!_freecamInitialized)
                InitializeFreecamFromOrbit();
            else
                ResetFreecamFromOrbit();
            _freecamActive = true;
        }

        _freecamInputEnabled = true;
        _freecamIgnoreNextDelta = true;
        _freecamMouseDelta = Vector2.Zero;
        _freecamWheelDelta = 0f;
        _freecamLastUpdate = DateTime.UtcNow;
        LockFreecamCursor();
        UpdateInputStatus("Input: freecam");
    }

    private void EndFreecamInput()
    {
        _freecamInputEnabled = false;
        ClearFreecamInputState();
        UnlockFreecamCursor();
        UpdateInputStatus("Input: idle");
    }

    private void DisableFreecam()
    {
        _freecamInputEnabled = false;
        _freecamActive = false;
        ClearFreecamInputState();
        UnlockFreecamCursor();
        RestoreOrbitState();
    }

    private void InitializeFreecamFromOrbit()
    {
        var cameraPos = GetCameraPosition();
        var forward = Vector3.Normalize(_target - cameraPos);
        GetYawPitchFromForward(forward, out var yaw, out var pitch);

        var forwardFromAngles = GetForwardVector(pitch, yaw);
        var worldUp = Vector3.UnitZ;
        var right = Vector3.Cross(forwardFromAngles, worldUp);
        if (right.LengthSquared() < 1e-6f)
            right = Vector3.Cross(forwardFromAngles, Vector3.UnitX);
        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, forwardFromAngles));
        var roll = ComputeRollForUp(pitch, yaw, up);

        _freecamTransform = new FreecamTransform
        {
            Position = cameraPos,
            Yaw = yaw,
            Pitch = pitch,
            Roll = roll,
            Fov = _freecamConfig.DefaultFov,
            Orientation = BuildQuat(pitch, yaw, roll)
        };
        _freecamSmoothed = _freecamTransform;
        ResetFreecamState();
        _freecamInitialized = true;
    }

    private void ResetFreecamFromOrbit()
    {
        InitializeFreecamFromOrbit();
    }

    private void ResetFreecamState()
    {
        _freecamSpeedScalar = Clamp(1.0f, _freecamConfig.SpeedMinMultiplier, _freecamConfig.SpeedMaxMultiplier);
        _lastMouseButton4 = false;
        _lastMouseButton5 = false;
        _mouseButton4Hold = 0.0f;
        _mouseButton5Hold = 0.0f;
        _freecamMouseVelocityX = 0.0f;
        _freecamMouseVelocityY = 0.0f;
        _freecamTargetRoll = 0.0f;
        _freecamCurrentRoll = 0.0f;
        _freecamRollVelocity = 0.0f;
        _freecamLastLateralVelocity = 0.0f;
        _freecamLastSmoothedPosition = _freecamSmoothed.Position;
        _freecamTransform.Orientation = BuildQuat(_freecamTransform);
        _freecamSmoothed.Orientation = BuildQuat(_freecamSmoothed);
        _freecamRawQuat = _freecamTransform.Orientation;
        _freecamSmoothedQuat = _freecamSmoothed.Orientation;
        _freecamRotVelocity = Vector3.Zero;
    }

    private void ClearFreecamInputState()
    {
        _keysDown.Clear();
        _mouseButton4Down = false;
        _mouseButton5Down = false;
        _freecamMouseDelta = Vector2.Zero;
        _freecamWheelDelta = 0f;
    }

    private void RestoreOrbitState()
    {
        if (!_orbitStateSaved)
            return;

        _target = _orbitTargetBeforeFreecam;
        _yaw = _orbitYawBeforeFreecam;
        _pitch = _orbitPitchBeforeFreecam;
        _distance = _orbitDistanceBeforeFreecam;
        _orbitStateSaved = false;
    }

    private void UpdateFreecam(float deltaTime)
    {
        if (!_freecamActive)
            return;

        deltaTime = MathF.Min(deltaTime, 0.1f);
        var wheel = _freecamWheelDelta;
        _freecamWheelDelta = 0f;

        if (_freecamInputEnabled)
        {
            UpdateFreecamSpeed(deltaTime, wheel);
            UpdateFreecamMouseLook(deltaTime);
        }

        if (_freecamInputEnabled)
        {
            UpdateFreecamMovement(deltaTime);
            UpdateFreecamFov(wheel);
        }

        UpdateFreecamRoll(deltaTime);
        _freecamTransform.Orientation = BuildQuat(_freecamTransform);
        _freecamRawQuat = _freecamTransform.Orientation;

        if (_freecamConfig.SmoothEnabled)
        {
            ApplyFreecamSmoothing(deltaTime);
        }
        else
        {
            _freecamSmoothed = _freecamTransform;
            _freecamSmoothed.Orientation = _freecamTransform.Orientation;
            _freecamSmoothedQuat = _freecamSmoothed.Orientation;
            _freecamRotVelocity = Vector3.Zero;
        }
    }

    private void UpdateFreecamMouseLook(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        var deltaYaw = -_freecamMouseDelta.X * _freecamConfig.MouseSensitivity;
        var deltaPitch = _freecamMouseDelta.Y * _freecamConfig.MouseSensitivity;
        _freecamMouseDelta = Vector2.Zero;

        _freecamTransform.Yaw += deltaYaw;
        _freecamTransform.Pitch += deltaPitch;

        _freecamMouseVelocityX = deltaYaw / deltaTime;
        _freecamMouseVelocityY = deltaPitch / deltaTime;

        if (_freecamConfig.ClampPitch)
        {
            _freecamTransform.Pitch = Clamp(_freecamTransform.Pitch, -89.0f, 89.0f);
        }
    }

    private void UpdateFreecamMovement(float deltaTime)
    {
        var moveSpeed = _freecamConfig.MoveSpeed * _freecamSpeedScalar;
        var verticalSpeed = _freecamConfig.VerticalSpeed * _freecamSpeedScalar;
        var analogEnabled = _freecamSettings?.AnalogKeyboardEnabled == true;
        var useAnalog = false;
        var analogLX = 0f;
        var analogLY = 0f;
        var analogRY = 0f;
        var analogRX = 0f;

        if (analogEnabled && _inputSender != null)
        {
            useAnalog = _inputSender.TryGetAnalogState(out var enabled, out analogLX, out analogLY, out analogRY, out analogRX) && enabled;
        }

        if (useAnalog)
        {
            var sprintInput = MathF.Max(0.0f, analogRX);
            if (sprintInput <= 0.0f && IsShiftDown())
            {
                sprintInput = 1.0f;
            }
            var sprintFactor = 1.0f + sprintInput * (_freecamConfig.SprintMultiplier - 1.0f);
            moveSpeed *= sprintFactor;
            verticalSpeed *= sprintFactor;
        }
        else if (IsShiftDown())
        {
            moveSpeed *= _freecamConfig.SprintMultiplier;
            verticalSpeed *= _freecamConfig.SprintMultiplier;
        }

        var moveQuat = BuildQuat(_freecamTransform.Pitch, _freecamTransform.Yaw, 0f);
        var forward = GetForwardFromQuat(moveQuat);
        var right = GetRightFromQuat(moveQuat);
        var up = GetUpFromQuat(moveQuat);

        var desiredVel = Vector3.Zero;

        if (useAnalog)
        {
            analogLX = Clamp(analogLX, -1.0f, 1.0f);
            analogLY = Clamp(analogLY, -1.0f, 1.0f);
            analogRY = Clamp(analogRY, -1.0f, 1.0f);

            desiredVel += forward * (moveSpeed * analogLY);
            desiredVel += right * (moveSpeed * analogLX);
            desiredVel += up * (verticalSpeed * analogRY);
        }
        else
        {
            if (IsKeyDown(Key.W))
                desiredVel += forward * moveSpeed;
            if (IsKeyDown(Key.S))
                desiredVel -= forward * moveSpeed;
            if (IsKeyDown(Key.A))
                desiredVel -= right * moveSpeed;
            if (IsKeyDown(Key.D))
                desiredVel += right * moveSpeed;
            if (IsKeyDown(Key.Space))
                desiredVel += up * verticalSpeed;
            if (IsCtrlDown())
                desiredVel -= up * verticalSpeed;
        }

        var desiredSpeed = desiredVel.Length();
        var maxSpeed = moveSpeed;
        if ((useAnalog && Math.Abs(analogRY) > 0.0001f) || (!useAnalog && (IsKeyDown(Key.Space) || IsCtrlDown())))
            maxSpeed = MathF.Max(verticalSpeed, moveSpeed);

        if (desiredSpeed > maxSpeed && desiredSpeed > 0.001f)
        {
            var scale = maxSpeed / desiredSpeed;
            desiredVel *= scale;
        }

        _freecamTransform.Position += desiredVel * deltaTime;
        _freecamTransform.Velocity = desiredVel;
    }

    private void UpdateFreecamRoll(float deltaTime)
    {
        if (_freecamPreviewRollOverrideActive)
        {
            _freecamTargetRoll = _freecamPreviewRollOverride;
            _freecamCurrentRoll = _freecamPreviewRollOverride;
            _freecamRollVelocity = 0.0f;
            _freecamTransform.Roll = _freecamPreviewRollOverride;
            return;
        }

        if (!_freecamConfig.SmoothEnabled)
        {
            if (IsKeyDown(Key.E))
                _freecamTargetRoll += _freecamConfig.RollSpeed * deltaTime;
            if (IsKeyDown(Key.Q))
                _freecamTargetRoll -= _freecamConfig.RollSpeed * deltaTime;
        }
        else
        {
            _freecamTargetRoll = 0;
        }

        var dynamicRoll = 0f;
        if (_freecamConfig.SmoothEnabled)
        {
            var view = _freecamConfig.SmoothEnabled ? _freecamSmoothed : _freecamTransform;
            var right = GetRightFromQuat(view.Orientation);

            var posBlend = _freecamConfig.HalfVec > 0f
                ? 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / _freecamConfig.HalfVec)
                : 1.0f;

            var smoothedPos = Vector3.Lerp(_freecamSmoothed.Position, _freecamTransform.Position, posBlend);
            var smoothedVel = deltaTime > 0f
                ? (smoothedPos - _freecamLastSmoothedPosition) / deltaTime
                : Vector3.Zero;
            _freecamLastSmoothedPosition = smoothedPos;

            var lateralVelocity = Vector3.Dot(smoothedVel, right);
            var lateralAccel = 0f;
            if (deltaTime > 0f)
                lateralAccel = (lateralVelocity - _freecamLastLateralVelocity) / deltaTime;
            _freecamLastLateralVelocity = lateralVelocity;

            var rawLean = (lateralAccel * _freecamConfig.LeanAccelScale)
                          + (lateralVelocity * _freecamConfig.LeanVelocityScale);
            rawLean *= _freecamConfig.LeanStrength;

            if (_freecamConfig.LeanMaxAngle > 0f)
            {
                var curved = MathF.Tanh(rawLean / _freecamConfig.LeanMaxAngle);
                dynamicRoll = curved * _freecamConfig.LeanMaxAngle;
            }
        }
        else
        {
            _freecamLastLateralVelocity = 0f;
            _freecamLastSmoothedPosition = _freecamTransform.Position;
        }

        var combinedRoll = _freecamTargetRoll + dynamicRoll;
        if (_freecamConfig.SmoothEnabled && _freecamConfig.LeanHalfTime > 0f)
        {
            _freecamCurrentRoll = SmoothDamp(_freecamCurrentRoll, combinedRoll, ref _freecamRollVelocity, _freecamConfig.LeanHalfTime, deltaTime);
        }
        else if (_freecamConfig.SmoothEnabled)
        {
            _freecamCurrentRoll = combinedRoll;
            _freecamRollVelocity = 0f;
        }
        else
        {
            _freecamCurrentRoll = Lerp(_freecamCurrentRoll, combinedRoll, 1.0f - _freecamConfig.RollSmoothing);
            _freecamRollVelocity = 0f;
        }
        _freecamTransform.Roll = _freecamCurrentRoll;
    }

    private void UpdateFreecamFov(float wheelDelta)
    {
        if (Math.Abs(wheelDelta) < float.Epsilon || IsAltDown())
            return;

        _freecamTransform.Fov += wheelDelta * _freecamConfig.FovStep;
        _freecamTransform.Fov = Clamp(_freecamTransform.Fov, _freecamConfig.FovMin, _freecamConfig.FovMax);
    }

    private void UpdateFreecamSpeed(float deltaTime, float wheelDelta)
    {
        if (deltaTime <= 0.0f)
            return;

        const float clickWindow = 0.12f;
        var held4 = _mouseButton4Down;
        var held5 = _mouseButton5Down;

        if (held4 && held5)
        {
            _mouseButton4Hold = 0.0f;
            _mouseButton5Hold = 0.0f;
            _lastMouseButton4 = held4;
            _lastMouseButton5 = held5;
            return;
        }

        var prevHold4 = _mouseButton4Hold;
        var prevHold5 = _mouseButton5Hold;
        _mouseButton4Hold = held4 ? _mouseButton4Hold + deltaTime : 0.0f;
        _mouseButton5Hold = held5 ? _mouseButton5Hold + deltaTime : 0.0f;

        static float ExtraTime(float prevHold, float curHold)
        {
            const float window = 0.12f;
            var prevOver = prevHold > window ? prevHold - window : 0.0f;
            var curOver = curHold > window ? curHold - window : 0.0f;
            var deltaOver = curOver - prevOver;
            return deltaOver > 0.0f ? deltaOver : 0.0f;
        }

        var adjustment = 0.0f;
        if (held5)
        {
            if (!_lastMouseButton5)
                adjustment += _freecamConfig.SpeedAdjustRate * clickWindow;
            adjustment += _freecamConfig.SpeedAdjustRate * ExtraTime(prevHold5, _mouseButton5Hold);
        }
        else if (held4)
        {
            if (!_lastMouseButton4)
                adjustment -= _freecamConfig.SpeedAdjustRate * clickWindow;
            adjustment -= _freecamConfig.SpeedAdjustRate * ExtraTime(prevHold4, _mouseButton4Hold);
        }

        if (IsAltDown() && Math.Abs(wheelDelta) > float.Epsilon)
            adjustment += wheelDelta * 0.05f;

        _lastMouseButton4 = held4;
        _lastMouseButton5 = held5;

        if (Math.Abs(adjustment) > float.Epsilon)
        {
            var newScalar = _freecamSpeedScalar + adjustment;
            newScalar = Clamp(newScalar, _freecamConfig.SpeedMinMultiplier, _freecamConfig.SpeedMaxMultiplier);
            _freecamSpeedScalar = newScalar;
        }
    }

    private void ApplyFreecamSmoothing(float deltaTime)
    {
        var posBlend = _freecamConfig.HalfVec > 0f
            ? 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / _freecamConfig.HalfVec)
            : 1.0f;

        var fovBlend = _freecamConfig.HalfFov > 0f
            ? 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / _freecamConfig.HalfFov)
            : 1.0f;

        _freecamSmoothed.Position = Vector3.Lerp(_freecamSmoothed.Position, _freecamTransform.Position, posBlend);
        _freecamSmoothed.Fov = Lerp(_freecamSmoothed.Fov, _freecamTransform.Fov, fovBlend);

        if (_freecamConfig.HalfRot > 0f)
        {
            if (_freecamConfig.RotCriticalDamping)
            {
                var omega = MathF.Log(2.0f) / _freecamConfig.HalfRot;
                var damping = MathF.Max(1.0f, _freecamConfig.RotDampingRatio);
                var target = _freecamRawQuat;
                var qErr = target * Quaternion.Inverse(_freecamSmoothedQuat);

                var clampedW = Math.Clamp(qErr.W, -1f, 1f);
                var angle = 2f * MathF.Acos(clampedW);
                var sinHalf = MathF.Sqrt(MathF.Max(0f, 1f - clampedW * clampedW));
                var axis = sinHalf < 1e-6f
                    ? Vector3.UnitX
                    : new Vector3(qErr.X / sinHalf, qErr.Y / sinHalf, qErr.Z / sinHalf);

                var error = axis * angle;
                var wdot = (omega * omega) * error - (2f * damping * omega) * _freecamRotVelocity;
                _freecamRotVelocity += wdot * deltaTime;
                _freecamSmoothedQuat = IntegrateQuat(_freecamSmoothedQuat, _freecamRotVelocity, deltaTime);
            }
            else
            {
                var t = deltaTime / _freecamConfig.HalfRot;
                var target = _freecamRawQuat;
                var qErr = Quaternion.Normalize(Quaternion.Inverse(_freecamSmoothedQuat) * target);
                var w = Math.Clamp(qErr.W, -1f, 1f);
                var targetAngle = 2f * MathF.Acos(w);
                var sinHalf = MathF.Sqrt(MathF.Max(0f, 1f - w * w));

                if (targetAngle > 1.0e-6f && sinHalf > 1.0e-6f)
                {
                    var axis = new Vector3(qErr.X / sinHalf, qErr.Y / sinHalf, qErr.Z / sinHalf);
                    var stepAngle = CalcDeltaExpSmooth(t, targetAngle);
                    if (Math.Abs(stepAngle) > 1.0e-6f)
                    {
                        var half = 0.5f * stepAngle;
                        var sinStep = MathF.Sin(half);
                        var dq = new Quaternion(axis.X * sinStep, axis.Y * sinStep, axis.Z * sinStep, MathF.Cos(half));
                        _freecamSmoothedQuat = Quaternion.Normalize(_freecamSmoothedQuat * dq);
                        _freecamRotVelocity = axis * (stepAngle / deltaTime);
                    }
                    else
                    {
                        _freecamRotVelocity = Vector3.Zero;
                    }
                }
                else
                {
                    _freecamRotVelocity = Vector3.Zero;
                }
            }
        }
        else
        {
            _freecamSmoothedQuat = _freecamRawQuat;
            _freecamRotVelocity = Vector3.Zero;
        }

        _freecamSmoothed.Orientation = _freecamSmoothedQuat;
        UpdateAnglesFromQuat(_freecamSmoothedQuat, ref _freecamSmoothed);
    }

    private static Quaternion BuildQuat(float pitchDeg, float yawDeg, float rollDeg)
    {
        var pitchRad = DegToRad(pitchDeg);
        var yawRad = DegToRad(yawDeg);
        var rollRad = DegToRad(rollDeg);
        var qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitY, pitchRad);
        var qYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawRad);
        var qRoll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollRad);
        return Quaternion.Normalize(qYaw * qPitch * qRoll);
    }

    private static Quaternion BuildQuat(FreecamTransform transform) =>
        BuildQuat(transform.Pitch, transform.Yaw, transform.Roll);

    private static Vector3 GetForwardFromQuat(Quaternion q)
    {
        return Vector3.Normalize(Vector3.Transform(Vector3.UnitX, q));
    }

    private static Vector3 GetUpFromQuat(Quaternion q)
    {
        return Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q));
    }

    private static Vector3 GetRightFromQuat(Quaternion q)
    {
        return Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, q));
    }

    private static Quaternion IntegrateQuat(Quaternion q, Vector3 angularVelocity, float deltaTime)
    {
        var speed = angularVelocity.Length();
        if (speed <= 1e-8f || deltaTime <= 0f)
            return q;

        var angle = speed * deltaTime;
        var axis = angularVelocity / speed;
        var dq = Quaternion.CreateFromAxisAngle(axis, angle);
        return Quaternion.Normalize(dq * q);
    }

    private static float CalcDeltaExpSmooth(float deltaT, float deltaVal)
    {
        const float limitTime = 19.931568f;
        if (deltaT < 0f)
            return 0f;
        if (deltaT > limitTime)
            return deltaVal;

        const float halfTime = 0.69314718f;
        var x = 1.0f / MathF.Exp(deltaT * halfTime);
        return (1.0f - x) * deltaVal;
    }

    private static void UpdateAnglesFromQuat(Quaternion q, ref FreecamTransform transform)
    {
        var forward = GetForwardFromQuat(q);
        var up = GetUpFromQuat(q);
        GetYawPitchFromForward(forward, out var yaw, out var pitch);
        var roll = ComputeRollForUp(pitch, yaw, up);
        transform.Yaw = yaw;
        transform.Pitch = pitch;
        transform.Roll = roll;
    }

    private static float ComputeRollForUp(float pitchDeg, float yawDeg, Vector3 desiredUp)
    {
        var forward = GetForwardVector(pitchDeg, yawDeg);
        var right = GetRightVector(yawDeg);
        var baseUp = Vector3.Normalize(Vector3.Cross(right, forward));
        var fwd = Vector3.Normalize(forward);
        var cross = Vector3.Cross(baseUp, desiredUp);
        var sin = Vector3.Dot(cross, fwd);
        var cos = Vector3.Dot(baseUp, desiredUp);
        var rollRad = MathF.Atan2(sin, cos);
        return (float)RadToDeg(rollRad);
    }

    private static void GetYawPitchFromForward(Vector3 forward, out float yawDeg, out float pitchDeg)
    {
        forward = Vector3.Normalize(forward);
        var yaw = MathF.Atan2(forward.Y, forward.X);
        var pitch = -MathF.Asin(Math.Clamp(forward.Z, -1f, 1f));
        yawDeg = (float)RadToDeg(yaw);
        pitchDeg = (float)RadToDeg(pitch);
    }

    private Matrix4x4 CreateFreecamView(FreecamTransform transform)
    {
        var forward = GetForwardFromQuat(transform.Orientation);
        var up = GetUpFromQuat(transform.Orientation);
        return Matrix4x4.CreateLookAt(transform.Position, transform.Position + forward, up);
    }

    private void GetFreecamBasis(FreecamTransform transform, out Vector3 forward, out Vector3 up)
    {
        forward = GetForwardFromQuat(transform.Orientation);
        up = GetUpFromQuat(transform.Orientation);
    }

    private bool IsKeyDown(Key key) => _keysDown.Contains(key);

    private bool IsShiftDown()
    {
        return _keysDown.Contains(Key.LeftShift)
            || _keysDown.Contains(Key.RightShift);
    }

    private bool IsCtrlDown()
    {
        return _keysDown.Contains(Key.LeftCtrl)
            || _keysDown.Contains(Key.RightCtrl);
    }

    private bool IsAltDown()
    {
        return _keysDown.Contains(Key.LeftAlt)
            || _keysDown.Contains(Key.RightAlt);
    }

    private void LockFreecamCursor()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        if (!TryGetFreecamCenter(out var centerLocal, out var centerScreen))
            return;

        _freecamCenterLocal = centerLocal;
        _freecamCenterScreen = centerScreen;
        SetCursorPosition(centerScreen.X, centerScreen.Y);
        Cursor = new Cursor(StandardCursorType.None);
        if (!_freecamCursorHidden)
        {
            ShowCursor(false);
            _freecamCursorHidden = true;
        }
    }

    private void UnlockFreecamCursor()
    {
        if (_freecamCursorHidden)
        {
            ShowCursor(true);
            Cursor = Cursor.Default;
            _freecamCursorHidden = false;
        }
    }

    private void CenterFreecamCursor()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        if (!TryGetFreecamCenter(out var centerLocal, out var centerScreen))
            return;

        _freecamCenterLocal = centerLocal;
        _freecamCenterScreen = centerScreen;
        SetCursorPosition(centerScreen.X, centerScreen.Y);
    }

    private bool TryGetScreenPoint(Point localPoint, out PixelPoint screenPoint)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            screenPoint = default;
            return false;
        }

        var translated = this.TranslatePoint(localPoint, topLevel);
        if (!translated.HasValue)
        {
            screenPoint = default;
            return false;
        }

        screenPoint = topLevel.PointToScreen(translated.Value);
        return true;
    }

    private bool TryGetLocalPointFromScreen(PixelPoint screenPoint, out Point localPoint)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            localPoint = default;
            return false;
        }

        var clientPoint = topLevel.PointToClient(screenPoint);
        var translated = topLevel.TranslatePoint(clientPoint, this);
        if (!translated.HasValue)
        {
            localPoint = default;
            return false;
        }

        localPoint = translated.Value;
        return true;
    }

    private bool TryGetFreecamCenter(out Point centerLocal, out PixelPoint centerScreen)
    {
        var localCenter = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);
        if (!TryGetScreenPoint(localCenter, out centerScreen))
        {
            centerLocal = default;
            return false;
        }

        if (!TryGetLocalPointFromScreen(centerScreen, out centerLocal))
            centerLocal = localCenter;

        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    private static void SetCursorPosition(int x, int y)
    {
        SetCursorPos(x, y);
    }

    private static Vector3 GetForwardVector(float pitchDeg, float yawDeg)
    {
        var pitch = DegToRad(pitchDeg);
        var yaw = DegToRad(yawDeg);
        var cosPitch = MathF.Cos(pitch);
        return new Vector3(
            cosPitch * MathF.Cos(yaw),
            cosPitch * MathF.Sin(yaw),
            -MathF.Sin(pitch));
    }

    private static Vector3 GetRightVector(float yawDeg)
    {
        var yaw = DegToRad(yawDeg);
        return new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
    }

    private static Vector3 GetUpVector(float pitchDeg, float yawDeg)
    {
        var pitch = DegToRad(pitchDeg);
        var yaw = DegToRad(yawDeg);
        return new Vector3(
            MathF.Sin(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch) * MathF.Sin(yaw),
            MathF.Cos(pitch));
    }

    private const float TwoPi = MathF.PI * 2f;

    private static float DegToRad(float degrees) => degrees * (MathF.PI / 180f);

    private static float RadToDeg(float radians) => radians * (180f / MathF.PI);

    private static float Clamp(float value, float min, float max)
    {
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float deltaTime)
    {
        if (smoothTime <= 0f || deltaTime <= 0f)
        {
            currentVelocity = 0f;
            return target;
        }

        var omega = 2f / smoothTime;
        var x = omega * deltaTime;
        var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

        var change = current - target;
        var temp = (currentVelocity + omega * change) * deltaTime;
        currentVelocity = (currentVelocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    private bool EnsureDevice()
    {
        if (_device != null && _context != null && _d3dReady)
            return true;

        var service = D3D11DeviceService.Instance;
        if (!service.IsReady)
        {
            SetStatusText("D3D11 init failed.");
            return false;
        }

        _device = service.Device;
        _context = service.Context;
        _device1 = service.Device1 ?? _device.QueryInterfaceOrNull<ID3D11Device1>();
        _factory = service.Factory;
        _deviceLock = service.ContextLock;
        _ownsDevice = false;
        if (!_deviceLogged)
        {
            _deviceLogged = true;
            LogMessage($"Device ready. hwnd=0x{_hwnd.ToInt64():X} device1={(_device1 != null)} factory={(_factory != null)}");
        }

        if (!InitializeShaders())
            return false;

        InitializeStates();
        InitializeDirect2D();
        _d3dReady = true;
        _statusPrefix = "D3D11 device ready";
        UpdateStatusText();
        return true;
    }

    private bool InitializeShaders()
    {
        if (_device == null)
            return false;

        _vertexShader?.Dispose();
        _pixelShader?.Dispose();
        _inputLayout?.Dispose();
        _constantBuffer?.Dispose();
        _vertexShader = null;
        _pixelShader = null;
        _inputLayout = null;
        _constantBuffer = null;

        var result = CompileShaderBlob(VertexShaderHlsl, "VSMain", "vs_5_0", ShaderFlags.OptimizationLevel3, out var vsBlob, out var vsError);
        if (result.Failure || vsBlob == null)
        {
            _statusPrefix = $"VS compile failed: {vsError?.AsString()}";
            return false;
        }

        result = CompileShaderBlob(PixelShaderHlsl, "PSMain", "ps_5_0", ShaderFlags.OptimizationLevel3, out var psBlob, out var psError);
        if (result.Failure || psBlob == null)
        {
            _statusPrefix = $"PS compile failed: {psError?.AsString()}";
            return false;
        }

        _vertexShader = _device.CreateVertexShader(vsBlob);
        _pixelShader = _device.CreatePixelShader(psBlob);

        var elements = new[]
        {
            new Vortice.Direct3D11.InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new Vortice.Direct3D11.InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0)
        };
        _inputLayout = _device.CreateInputLayout(elements, vsBlob);

        int size = AlignConstantBufferSize(Marshal.SizeOf<ShaderConstants>());
        _constantBuffer = _device.CreateBuffer(new BufferDescription((uint)size, BindFlags.ConstantBuffer, ResourceUsage.Default));
        return true;
    }

    private static Result CompileShaderBlob(string source, string entryPoint, string profile, ShaderFlags flags, out Blob? shaderBlob, out Blob? errorBlob)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        unsafe
        {
            fixed (byte* pData = bytes)
            {
                return Compiler.Compile(
                    pData,
                    new PointerUSize((ulong)bytes.Length),
                    "inline",
                    null,
                    null,
                    entryPoint,
                    profile,
                    flags,
                    EffectFlags.None,
                    out shaderBlob,
                    out errorBlob);
            }
        }
    }

    private void InitializeStates()
    {
        if (_device == null)
            return;

        _rasterizer?.Dispose();
        _depthEnabledState?.Dispose();
        _depthDisabledState?.Dispose();

        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.None,
            FillMode = Vortice.Direct3D11.FillMode.Solid,
            DepthClipEnable = true
        });

        _depthEnabledState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = Vortice.Direct3D11.ComparisonFunction.LessEqual
        });

        _depthDisabledState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = Vortice.Direct3D11.ComparisonFunction.Always
        });
    }

    private void InitializeDirect2D()
    {
        if (_device == null)
            return;

        _d2dTarget?.Dispose();
        _d2dTarget = null;
        _d2dTextBrush?.Dispose();
        _d2dTextBrush = null;
        _labelFormat?.Dispose();
        _labelFormat = null;
        _fpsFormat?.Dispose();
        _fpsFormat = null;
        _dwriteFactory?.Dispose();
        _dwriteFactory = null;
        _d2dContext?.Dispose();
        _d2dContext = null;
        _d2dDevice?.Dispose();
        _d2dDevice = null;
        _d2dFactory?.Dispose();
        _d2dFactory = null;

        _d2dFactory = Vortice.Direct2D1.D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.MultiThreaded);
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _dwriteFactory = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);
        _labelFormat = _labelFormat = _dwriteFactory.CreateTextFormat(
                                                                        "Segoe UI",
                                                                        fontCollection: null,
                                                                        fontWeight: Vortice.DirectWrite.FontWeight.Bold,
                                                                        fontStyle: Vortice.DirectWrite.FontStyle.Normal,
                                                                        fontStretch: Vortice.DirectWrite.FontStretch.Normal,
                                                                        fontSize: 18.0f,
                                                                        localeName: "en-us"
                                                                    );
        _labelFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _fpsFormat = _dwriteFactory.CreateTextFormat(
            "Segoe UI",
            fontCollection: null,
            fontWeight: Vortice.DirectWrite.FontWeight.SemiBold,
            fontStyle: Vortice.DirectWrite.FontStyle.Normal,
            fontStretch: Vortice.DirectWrite.FontStretch.Normal,
            fontSize: 14.0f,
            localeName: "en-us");
        _fpsFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Trailing;
        _fpsFormat.ParagraphAlignment = ParagraphAlignment.Near;
        _d2dTextBrush = _d2dContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
    }

    private bool EnsureSwapChain(int width, int height)
    {
        if (_factory == null || _device == null || _hwnd == IntPtr.Zero)
        {
            if (!_swapchainFailureLogged)
            {
                _swapchainFailureLogged = true;
                LogMessage($"EnsureSwapChain failed: factory={(_factory != null)} device={(_device != null)} hwnd=0x{_hwnd.ToInt64():X}");
            }
            return false;
        }

        if (_swapChain == null || _swapWidth != width || _swapHeight != height)
        {
            ResetD2DTarget();
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
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.AllowTearing
            };

            _swapChain = _factory.CreateSwapChainForHwnd(_device, _hwnd, desc, null, null);
            _swapWidth = width;
            _swapHeight = height;
            if (!_swapchainLogged)
            {
                _swapchainLogged = true;
                LogMessage($"SwapChain created: {_swapWidth}x{_swapHeight} hwnd=0x{_hwnd.ToInt64():X}");
            }
        }

        return _swapChain != null;
    }

    private bool EnsureDepthStencil(int width, int height)
    {
        if (_device == null)
            return false;

        if (_depthTexture != null && _renderWidth == width && _renderHeight == height)
            return true;

        _depthStencilView?.Dispose();
        _depthStencilView = null;
        _depthTexture?.Dispose();
        _depthTexture = null;

        var depthDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        try
        {
            _depthTexture = _device.CreateTexture2D(depthDesc);
            _depthStencilView = _device.CreateDepthStencilView(_depthTexture);
            _renderWidth = width;
            _renderHeight = height;
            return true;
        }
        catch (SharpGenException ex)
        {
            LogMessage($"Depth setup failed: 0x{ex.ResultCode.Code:X8}");
            return false;
        }
    }

    private void ReleaseSwapChain()
    {
        _swapChain?.Dispose();
        _swapChain = null;
        _swapWidth = 0;
        _swapHeight = 0;
        ResetD2DTarget();
    }

    private void DrawGeometry(ID3D11Buffer buffer, int vertexCount, PrimitiveTopology topology, bool depthEnabled, int startVertex = 0)
    {
        if (_context == null)
            return;

        _context.OMSetDepthStencilState(depthEnabled ? _depthEnabledState : _depthDisabledState);
        SetVertexBuffer(buffer, (uint)VertexStride, 0);
        _context.IASetPrimitiveTopology(topology);
        _context.Draw((uint)vertexCount, (uint)startVertex);
    }

    private void SetVertexBuffer(ID3D11Buffer buffer, uint stride, uint offset)
    {
        if (_context == null)
            return;

        var buffers = new[] { buffer };
        var strides = new[] { stride };
        var offsets = new[] { offset };
        _context.IASetVertexBuffers(0, buffers, strides, offsets);
    }

    private ID3D11Buffer CreateVertexBuffer(float[] vertices, bool dynamic)
    {
        if (_device == null)
            throw new InvalidOperationException("D3D11 device not initialized.");

        var desc = new BufferDescription((uint)(vertices.Length * sizeof(float)), BindFlags.VertexBuffer,
            dynamic ? ResourceUsage.Dynamic : ResourceUsage.Immutable,
            dynamic ? CpuAccessFlags.Write : CpuAccessFlags.None);

        if (!dynamic)
        {
            var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            try
            {
                var subresource = new SubresourceData(handle.AddrOfPinnedObject(), 0, 0);
                return _device.CreateBuffer(desc, subresource);
            }
            finally
            {
                handle.Free();
            }
        }

        var buffer = _device.CreateBuffer(desc);
        UpdateDynamicVertexBuffer(buffer, vertices);
        return buffer;
    }

    private void UpdateDynamicVertexBuffer(ID3D11Buffer buffer, float[] vertices)
    {
        if (_context == null)
            return;

        var map = _context.Map(buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.Copy(vertices, 0, map.DataPointer, vertices.Length);
        _context.Unmap(buffer, 0);
    }


    private void DisposeD3D()
    {
        ReleaseSwapChain();

        _meshBuffer?.Dispose();
        _meshBuffer = null;
        _gridBuffer?.Dispose();
        _gridBuffer = null;
        _groundBuffer?.Dispose();
        _groundBuffer = null;
        _debugBuffer?.Dispose();
        _debugBuffer = null;
        _pinBuffer?.Dispose();
        _pinBuffer = null;

        _vertexShader?.Dispose();
        _vertexShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _rasterizer?.Dispose();
        _rasterizer = null;
        _depthEnabledState?.Dispose();
        _depthEnabledState = null;
        _depthDisabledState?.Dispose();
        _depthDisabledState = null;

        _d2dTarget?.Dispose();
        _d2dTarget = null;
        _d2dTextBrush?.Dispose();
        _d2dTextBrush = null;
        _labelFormat?.Dispose();
        _labelFormat = null;
        _fpsFormat?.Dispose();
        _fpsFormat = null;
        _dwriteFactory?.Dispose();
        _dwriteFactory = null;
        _d2dContext?.Dispose();
        _d2dContext = null;
        _d2dDevice?.Dispose();
        _d2dDevice = null;
        _d2dFactory?.Dispose();
        _d2dFactory = null;

        if (_ownsDevice)
        {
            _context?.Dispose();
            _context = null;
            _device1?.Dispose();
            _device1 = null;
            _device?.Dispose();
            _device = null;
            _factory?.Dispose();
            _factory = null;
        }
        else
        {
            _context = null;
            _device1 = null;
            _device = null;
            _factory = null;
        }
        _deviceLock = null;
        _d3dReady = false;
    }

    private static int AlignConstantBufferSize(int size)
    {
        const int alignment = 16;
        return (size + alignment - 1) / alignment * alignment;
    }

    #region Win32 host
    private static ushort _wndClass;
    private static readonly object _classLock = new();
    private static WndProcDelegate? _wndProc;
    private static IntPtr _wndProcPtr = IntPtr.Zero;

    private static void EnsureClass()
    {
        if (_classRegistered)
            return;

        lock (_classLock)
        {
            if (_classRegistered)
                return;

            if (_wndProc == null)
            {
                _wndProc = HostWndProc;
                _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
            }

            SetLastError(0);
            LogMessage($"RegisterClassEx: name={WndClassName}");
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProcPtr,
                hInstance = GetModuleHandle(null),
                lpszClassName = WndClassName
            };
            _wndClass = RegisterClassEx(ref wc);
            if (_wndClass == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1410)
                {
                    _classRegistered = true;
                    LogMessage("RegisterClassEx: class already exists, continuing.");
                }
                else
                {
                    LogMessage($"RegisterClassEx failed: error={error}");
                }
            }
            else
            {
                _classRegistered = true;
            }
        }
    }

    private static IntPtr CreateChildWindow(IntPtr parent)
    {
        EnsureClass();
        if (!_classRegistered)
        {
            LogMessage("CreateChildWindow aborted: window class not registered.");
            return IntPtr.Zero;
        }
        return CreateWindowEx(
            0,
            WndClassName,
            string.Empty,
            0x40000000 | 0x10000000 | 0x02000000,
            0, 0, 32, 32,
            parent,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    private static void RegisterHostWindow(IntPtr hwnd, D3D11Viewport host)
    {
        lock (_classLock)
        {
            _hostMap[hwnd] = new WeakReference<D3D11Viewport>(host);
        }
    }

    private static void UnregisterHostWindow(IntPtr hwnd)
    {
        lock (_classLock)
        {
            _hostMap.Remove(hwnd);
        }
    }

    private void UpdateChildWindowSize()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0)
            return;

        double scale = (VisualRoot as Avalonia.Rendering.IRenderRoot)?.RenderScaling ?? 1.0;
        int x = (int)Math.Round(b.X * scale);
        int y = (int)Math.Round(b.Y * scale);
        int w = (int)Math.Round(b.Width * scale);
        int h = (int)Math.Round(b.Height * scale);
        _targetWidth = Math.Max(1, w);
        _targetHeight = Math.Max(1, h);
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, 0x0020 | 0x0002);
        RequestNextFrame();
    }

    private void HandleNativeMouse(uint msg, int x, int y, int xButton)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleNativeMouse(msg, x, y, xButton));
            return;
        }

        var local = ClientToLocalPoint(x, y);
        var shiftDown = IsShiftKeyDown();
        var updateKind = msg switch
        {
            0x0201 => PointerUpdateKind.LeftButtonPressed,
            0x0202 => PointerUpdateKind.LeftButtonReleased,
            0x0204 => PointerUpdateKind.RightButtonPressed,
            0x0205 => PointerUpdateKind.RightButtonReleased,
            0x0207 => PointerUpdateKind.MiddleButtonPressed,
            0x0208 => PointerUpdateKind.MiddleButtonReleased,
            0x020B => xButton == 1 ? PointerUpdateKind.XButton1Pressed : PointerUpdateKind.XButton2Pressed,
            0x020C => xButton == 1 ? PointerUpdateKind.XButton1Released : PointerUpdateKind.XButton2Released,
            _ => PointerUpdateKind.Other
        };

        switch (msg)
        {
            case 0x0201: // WM_LBUTTONDOWN
            case 0x0204: // WM_RBUTTONDOWN
            case 0x0207: // WM_MBUTTONDOWN
            case 0x020B: // WM_XBUTTONDOWN
                HandleNativePointerPressed(local, updateKind, shiftDown);
                break;
            case 0x0202: // WM_LBUTTONUP
            case 0x0205: // WM_RBUTTONUP
            case 0x0208: // WM_MBUTTONUP
            case 0x020C: // WM_XBUTTONUP
                HandleNativePointerReleased(local, updateKind);
                break;
            case 0x0200: // WM_MOUSEMOVE
                HandleNativePointerMoved(local);
                break;
        }
    }

    private void HandleNativePointerPressed(Point position, PointerUpdateKind updateKind, bool shiftDown)
    {
        Focus();
        var middlePressed = updateKind == PointerUpdateKind.MiddleButtonPressed;
        var rightPressed = updateKind == PointerUpdateKind.RightButtonPressed;
        var leftPressed = updateKind == PointerUpdateKind.LeftButtonPressed;
        _mouseButton4Down = updateKind == PointerUpdateKind.XButton1Pressed;
        _mouseButton5Down = updateKind == PointerUpdateKind.XButton2Pressed;

        UpdateInputStatus($"Input: down M:{middlePressed} Shift:{shiftDown}");

        if (leftPressed && TryHandlePinClick(position))
            return;

        if (rightPressed)
        {
            BeginFreecam(position);
            return;
        }

        if (!middlePressed)
            return;

        if (_freecamActive)
            DisableFreecam();

        _dragging = true;
        _panning = shiftDown;
        _lastPointer = position;
    }

    private void HandleNativePointerReleased(Point position, PointerUpdateKind updateKind)
    {
        _mouseButton4Down = updateKind == PointerUpdateKind.XButton1Pressed;
        _mouseButton5Down = updateKind == PointerUpdateKind.XButton2Pressed;

        var rightReleased = updateKind == PointerUpdateKind.RightButtonReleased;
        if (_freecamActive && rightReleased)
        {
            EndFreecamInput();
            return;
        }

        if (!_dragging)
            return;

        var released = updateKind == PointerUpdateKind.MiddleButtonReleased;
        if (released)
        {
            _dragging = false;
            _panning = false;
            UpdateInputStatus("Input: up");
        }
    }

    private void HandleNativePointerMoved(Point position)
    {
        if (_freecamActive && _freecamInputEnabled)
        {
            if (_freecamIgnoreNextDelta)
            {
                _freecamIgnoreNextDelta = false;
                CenterFreecamCursor();
                UpdateInputStatus("Input: freecam");
                RequestNextFrame();
                return;
            }

            var scale = MathF.Max(0.01f, ViewportMouseScale);
            var dx = (float)(position.X - _freecamCenterLocal.X) * scale;
            var dy = (float)(position.Y - _freecamCenterLocal.Y) * scale;
            if (dx != 0 || dy != 0)
                _freecamMouseDelta += new Vector2(dx, dy);
            CenterFreecamCursor();
            UpdateInputStatus("Input: freecam");
            RequestNextFrame();
            return;
        }

        if (!_dragging)
        {
            UpdateInputStatus("Input: move");
            return;
        }

        var delta = position - _lastPointer;
        _lastPointer = position;

        if (_panning)
            Pan((float)delta.X, (float)delta.Y);
        else
            Orbit((float)delta.X, (float)delta.Y);

        UpdateInputStatus("Input: drag");
        RequestNextFrame();
    }

    private Point ClientToLocalPoint(int x, int y)
    {
        double scale = (VisualRoot as Avalonia.Rendering.IRenderRoot)?.RenderScaling ?? 1.0;
        return new Point(x / scale, y / scale);
    }

    private static bool IsShiftKeyDown()
    {
        const int VK_SHIFT = 0x10;
        return (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    }

    private void InitializeAfterNativeCreated()
    {
        if (_nativeInitDone || _hwnd == IntPtr.Zero)
            return;

        _nativeInitDone = true;
        LogMessage($"InitializeAfterNativeCreated hwnd=0x{_hwnd.ToInt64():X}");
        EnsureDevice();
        UpdateGrid(10f, 20);
        CreateGroundPlane(10f);
        UpdateChildWindowSize();
        StartRenderLoop();
        RequestNextFrame();
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
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

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(int dwErrCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const uint WM_MOUSEMOVE = 0x0200;
        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_MBUTTONDOWN = 0x0207;
        const uint WM_MBUTTONUP = 0x0208;
        const uint WM_XBUTTONDOWN = 0x020B;
        const uint WM_XBUTTONUP = 0x020C;

        if (msg == WM_NCHITTEST)
            return new IntPtr(HTCLIENT);

        D3D11Viewport? host = null;
        lock (_classLock)
        {
            if (_hostMap.TryGetValue(hWnd, out var weak) && weak.TryGetTarget(out var target))
                host = target;
        }

        if (host == null)
            return DefWindowProc(hWnd, msg, wParam, lParam);

        if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ||
            msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP ||
            msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
        {
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            int xButton = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            host.HandleNativeMouse(msg, x, y, xButton);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }
    #endregion

    private static void AddVertex(float[] buffer, ref int index, float x, float y, float z, float nx, float ny, float nz)
    {
        buffer[index++] = x;
        buffer[index++] = y;
        buffer[index++] = z;
        buffer[index++] = nx;
        buffer[index++] = ny;
        buffer[index++] = nz;
    }

    private void CreateGroundPlane(float size)
    {
        if (!EnsureDevice())
            return;

        var half = size * 0.5f;
        var vertices = new[]
        {
            -half, -half, 0f, 0f, 0f, 1f,
            half, -half, 0f, 0f, 0f, 1f,
            half, half, 0f, 0f, 0f, 1f,

            -half, -half, 0f, 0f, 0f, 1f,
            half, half, 0f, 0f, 0f, 1f,
            -half, half, 0f, 0f, 0f, 1f
        };

        _groundBuffer?.Dispose();
        _groundBuffer = CreateVertexBuffer(vertices, dynamic: false);
        _groundVertexCount = vertices.Length / 6;
        RequestNextFrame();
        UpdateStatusText();
    }


    private void RebuildPins()
    {
        _pinsDirty = false;
        if (_pins.Count == 0 || !EnsureDevice())
        {
            _pinDraws.Clear();
            _pinLabels = new List<PinLabel>();
            lock (_labelLock)
            {
                _labelHitCache = new List<PinLabel>();
            }
            _pinVertexCount = 0;
            _pinBuffer?.Dispose();
            _pinBuffer = null;
            return;
        }

        float pinScale;
        if (Dispatcher.UIThread.CheckAccess())
        {
            pinScale = PinScale;
        }
        else
        {
            pinScale = (float)Dispatcher.UIThread.InvokeAsync(() => PinScale).GetAwaiter().GetResult();
        }

        var data = new List<float>(_pins.Count * 256);
        var labels = new List<PinLabel>();
        _pinDraws.Clear();
        foreach (var pin in _pins)
        {
            var start = data.Count / 6;
            var added = AppendPinGeometry(pin, data, labels, pinScale);
            _pinDraws.Add(new PinDrawCall { Start = start, Count = added, Color = pin.Color });
        }

        _pinBuffer?.Dispose();
        _pinBuffer = CreateVertexBuffer(data.ToArray(), dynamic: true);
        _pinVertexCount = data.Count / 6;
        _pinLabels = labels;
    }

    private int AppendPinGeometry(PinRenderData pin, List<float> buffer, List<PinLabel> labels, float pinScale)
    {
        var added = 0;
        var forward = pin.Forward;
        if (forward.LengthSquared() < 0.0001f)
            forward = new Vector3(0, 0, 1);
        forward = Vector3.Normalize(forward);

        var upHint = Vector3.UnitZ;
        if (MathF.Abs(Vector3.Dot(forward, upHint)) > 0.95f)
            upHint = Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(upHint, forward));
        var up = Vector3.Normalize(Vector3.Cross(forward, right));

        Vector3 TransformLocal(Vector3 local)
        {
            return right * local.X + up * local.Y + forward * local.Z;
        }

        var pos = pin.Position;
        var scale = pinScale;
        var sphereRadius = 0.12f * scale;
        var coneLength = sphereRadius * 1.8f;
        var coneBaseRadius = sphereRadius;
        var coneBaseOffset = 0f;

        // Cone
        for (int i = 0; i < _pinConeUnit.Length; i += 3)
        {
            var p1 = _pinConeUnit[i];
            var p2 = _pinConeUnit[i + 1];
            var p3 = _pinConeUnit[i + 2];

            p1.X *= coneBaseRadius; p1.Y *= coneBaseRadius; p1.Z = p1.Z * coneLength - coneBaseOffset;
            p2.X *= coneBaseRadius; p2.Y *= coneBaseRadius; p2.Z = p2.Z * coneLength - coneBaseOffset;
            p3.X *= coneBaseRadius; p3.Y *= coneBaseRadius; p3.Z = p3.Z * coneLength - coneBaseOffset;

            p1 = TransformLocal(p1) + pos;
            p2 = TransformLocal(p2) + pos;
            p3 = TransformLocal(p3) + pos;

            var n1 = TransformLocal(_pinConeNormals[i]);
            var n2 = TransformLocal(_pinConeNormals[i + 1]);
            var n3 = TransformLocal(_pinConeNormals[i + 2]);

            AppendVertex(p1, n1, buffer);
            AppendVertex(p2, n2, buffer);
            AppendVertex(p3, n3, buffer);
            added += 3;
        }

        // Sphere
        for (int i = 0; i < _pinSphereUnit.Length; i += 3)
        {
            var p1 = TransformLocal(_pinSphereUnit[i] * sphereRadius) + pos;
            var p2 = TransformLocal(_pinSphereUnit[i + 1] * sphereRadius) + pos;
            var p3 = TransformLocal(_pinSphereUnit[i + 2] * sphereRadius) + pos;

            var n1 = TransformLocal(_pinSphereNormals[i]);
            var n2 = TransformLocal(_pinSphereNormals[i + 1]);
            var n3 = TransformLocal(_pinSphereNormals[i + 2]);

            AppendVertex(p1, n1, buffer);
            AppendVertex(p2, n2, buffer);
            AppendVertex(p3, n3, buffer);
            added += 3;
        }

        var labelOffset = Vector3.Zero;
        labels.Add(new PinLabel
        {
            Text = pin.Label,
            World = pin.Position + labelOffset,
            LabelColor = ToAvaloniaColor(pin.Color)
        });
        return added;
    }

    private static Vector3[] CreateUnitCone()
    {
        const int segments = 16;
        var verts = new List<Vector3>();
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * TwoPi / segments;
            float a1 = (i + 1) * TwoPi / segments;
            verts.Add(new Vector3(0, 0, 1));
            verts.Add(new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0));
            verts.Add(new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0));
        }
        var arr = new Vector3[verts.Count];
        verts.CopyTo(arr);
        return arr;
    }

    private static Vector3[] CreateUnitConeNormals()
    {
        const int segments = 16;
        var norms = new List<Vector3>();
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * TwoPi / segments;
            float a1 = (i + 1) * TwoPi / segments;
            var apex = new Vector3(0, 0, 1);
            var p0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0);
            var p1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0);
            var normal = Vector3.Cross(p0 - apex, p1 - apex);
            if (normal.LengthSquared() < 0.0001f)
                normal = Vector3.UnitZ;
            else
                normal = Vector3.Normalize(normal);
            norms.Add(normal);
            norms.Add(normal);
            norms.Add(normal);
        }
        var arr = new Vector3[norms.Count];
        norms.CopyTo(arr);
        return arr;
    }

    private static (Vector3[] Vertices, Vector3[] Normals) CreateUnitSphere(int latSegments, int lonSegments)
    {
        var verts = new List<Vector3>(latSegments * lonSegments * 6);
        var norms = new List<Vector3>(latSegments * lonSegments * 6);

        for (int lat = 0; lat < latSegments; lat++)
        {
            float v0 = lat / (float)latSegments;
            float v1 = (lat + 1) / (float)latSegments;
            float t0 = v0 * MathF.PI;
            float t1 = v1 * MathF.PI;

            for (int lon = 0; lon < lonSegments; lon++)
            {
                float u0 = lon / (float)lonSegments;
                float u1 = (lon + 1) / (float)lonSegments;
                float p0 = u0 * TwoPi;
                float p1 = u1 * TwoPi;

                var a = Spherical(t0, p0);
                var b = Spherical(t1, p0);
                var c = Spherical(t1, p1);
                var d = Spherical(t0, p1);

                AppendSphereTri(a, b, c, verts, norms);
                AppendSphereTri(a, c, d, verts, norms);
            }
        }

        return (verts.ToArray(), norms.ToArray());
    }

    private static Vector3 Spherical(float theta, float phi)
    {
        var sinT = MathF.Sin(theta);
        return new Vector3(
            sinT * MathF.Cos(phi),
            MathF.Cos(theta),
            sinT * MathF.Sin(phi));
    }

    private static void AppendSphereTri(Vector3 a, Vector3 b, Vector3 c, List<Vector3> verts, List<Vector3> norms)
    {
        verts.Add(a);
        verts.Add(b);
        verts.Add(c);
        norms.Add(a);
        norms.Add(b);
        norms.Add(c);
    }
    private static void AppendVertex(Vector3 position, Vector3 normal, List<float> vertices)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);
        vertices.Add(normal.X);
        vertices.Add(normal.Y);
        vertices.Add(normal.Z);
    }

    private struct FreecamTransform
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Pitch;
        public float Yaw;
        public float Roll;
        public float Fov;
        public Quaternion Orientation;
    }

    private readonly struct FreecamConfig
    {
        public static readonly FreecamConfig Default = new()
        {
            MouseSensitivity = 0.12f,
            MoveSpeed = 200.0f,
            SprintMultiplier = 2.5f,
            VerticalSpeed = 200.0f,
            SpeedAdjustRate = 1.1f,
            SpeedMinMultiplier = 0.05f,
            SpeedMaxMultiplier = 5.0f,
            RollSpeed = 45.0f,
            RollSmoothing = 0.8f,
            LeanStrength = 1.0f,
            LeanAccelScale = 0.025f,
            LeanVelocityScale = 0.005f,
            LeanMaxAngle = 20.0f,
            LeanHalfTime = 0.18f,
            ClampPitch = false,
            FovMin = 10.0f,
            FovMax = 150.0f,
            FovStep = 2.0f,
            DefaultFov = 90.0f,
            SmoothEnabled = true,
            HalfVec = 0.5f,
            HalfRot = 0.5f,
            HalfFov = 0.5f,
            RotCriticalDamping = false,
            RotDampingRatio = 1.0f
        };

        public float MouseSensitivity { get; init; }
        public float MoveSpeed { get; init; }
        public float SprintMultiplier { get; init; }
        public float VerticalSpeed { get; init; }
        public float SpeedAdjustRate { get; init; }
        public float SpeedMinMultiplier { get; init; }
        public float SpeedMaxMultiplier { get; init; }
        public float RollSpeed { get; init; }
        public float RollSmoothing { get; init; }
        public float LeanStrength { get; init; }
        public float LeanAccelScale { get; init; }
        public float LeanVelocityScale { get; init; }
        public float LeanMaxAngle { get; init; }
        public float LeanHalfTime { get; init; }
        public bool ClampPitch { get; init; }
        public float FovMin { get; init; }
        public float FovMax { get; init; }
        public float FovStep { get; init; }
        public float DefaultFov { get; init; }
        public bool SmoothEnabled { get; init; }
        public float HalfVec { get; init; }
        public float HalfRot { get; init; }
        public float HalfFov { get; init; }
        public bool RotCriticalDamping { get; init; }
        public float RotDampingRatio { get; init; }
    }

    private sealed class PinRenderData
    {
        public required Vector3 Position { get; init; }
        public required Vector3 Forward { get; init; }
        public required Vector3 Color { get; init; }
        public required string Label { get; init; }
    }

    private sealed class PinDrawCall
    {
        public required int Start { get; init; }
        public required int Count { get; init; }
        public required Vector3 Color { get; init; }
    }

    public sealed class PinLabel
    {
        public string Text { get; set; } = string.Empty;
        public Vector3 World { get; set; }
        public Point Screen { get; set; }
        public double ScreenX { get; set; }
        public double ScreenY { get; set; }
        public Avalonia.Media.Color LabelColor { get; set; }
        public IBrush? LabelBrush { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public Matrix4x4 Mvp;
        public Vector4 Color;
        public Vector4 LightDirAmbient;
    }

    private Matrix4x4 GetMapRotation()
    {
        var yaw = DegToRad(MapYaw);
        var pitch = DegToRad(MapPitch);
        var roll = DegToRad(MapRoll);

        var yawMat = Matrix4x4.CreateRotationZ(yaw);
        var pitchMat = Matrix4x4.CreateRotationY(pitch);
        var rollMat = Matrix4x4.CreateRotationX(roll);
        return yawMat * pitchMat * rollMat;
    }

    private ObjMesh ApplyMapTransform(ObjMesh mesh)
    {
        var rotation = GetMapRotation();
        var offset = new Vector3(MapOffsetX, MapOffsetY, MapOffsetZ);
        var scale = Math.Clamp(MapScale, 0.0001f, 100000f);

        var vertices = new float[mesh.Vertices.Length];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < mesh.Vertices.Length; i += 6)
        {
            var pos = new Vector3(mesh.Vertices[i], mesh.Vertices[i + 1], mesh.Vertices[i + 2]);
            var normal = new Vector3(mesh.Vertices[i + 3], mesh.Vertices[i + 4], mesh.Vertices[i + 5]);

            pos *= scale;
            pos = Vector3.Transform(pos, rotation) + offset;
            normal = Vector3.TransformNormal(normal, rotation);

            vertices[i] = pos.X;
            vertices[i + 1] = pos.Y;
            vertices[i + 2] = pos.Z;
            vertices[i + 3] = normal.X;
            vertices[i + 4] = normal.Y;
            vertices[i + 5] = normal.Z;

            min = Vector3.Min(min, pos);
            max = Vector3.Max(max, pos);
        }

        return new ObjMesh(vertices, mesh.VertexCount, min, max);
    }

    private static Avalonia.Media.Color ToAvaloniaColor(Vector3 color)
    {
        static byte ToByte(float value)
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            return (byte)MathF.Round(clamped * 255f);
        }

        return Avalonia.Media.Color.FromRgb(ToByte(color.X), ToByte(color.Y), ToByte(color.Z));
    }

    private static Color4 ToColor4(Avalonia.Media.Color color)
    {
        return new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    private void UpdateFreecamForFrame()
    {
        if (!_freecamActive)
            return;

        var now = DateTime.UtcNow;
        if (_freecamLastUpdate == default)
            _freecamLastUpdate = now;
        var deltaTime = (float)(now - _freecamLastUpdate).TotalSeconds;
        _freecamLastUpdate = now;
        UpdateFreecam(deltaTime);
    }

    private void RequestNextFrame()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestNextFrame);
            return;
        }

        float cap = ViewportFpsCap;
        if (cap <= 0)
        {
            _lastFrameTicks = _frameLimiter.ElapsedTicks;
            RequestNextFrameRendering();
            return;
        }

        double targetMs = 1000.0 / cap;
        long nowTicks = _frameLimiter.ElapsedTicks;
        double elapsedMs = (nowTicks - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs >= targetMs)
        {
            _lastFrameTicks = nowTicks;
            RequestNextFrameRendering();
            return;
        }

        ScheduleDelayedFrame(targetMs - elapsedMs);
    }

    private void ScheduleDelayedFrame(double delayMs)
    {
        if (_frameLimiterPending)
            return;

        double clampedDelay = Math.Max(1.0, delayMs);
        _frameLimiterTimer ??= new DispatcherTimer();
        _frameLimiterTimer.Stop();
        _frameLimiterTimer.Interval = TimeSpan.FromMilliseconds(clampedDelay);
        _frameLimiterTimer.Tick -= OnFrameLimiterTick;
        _frameLimiterTimer.Tick += OnFrameLimiterTick;
        _frameLimiterPending = true;
        _frameLimiterTimer.Start();
    }

    private void OnFrameLimiterTick(object? sender, EventArgs e)
    {
        if (_frameLimiterTimer != null)
            _frameLimiterTimer.Stop();
        _frameLimiterPending = false;
        _lastFrameTicks = _frameLimiter.ElapsedTicks;
        RequestNextFrameRendering();
    }

    private void RequestNextFrameRendering()
    {
        _renderSignal.Set();
    }

    private void StartRenderLoop()
    {
        if (_renderLoop != null)
            return;

        LogMessage("StartRenderLoop");
        _renderCts = new CancellationTokenSource();
        _renderLoop = Task.Run(() => RenderLoop(_renderCts.Token));
        _renderSignal.Set();
    }

    private void StopRenderLoop()
    {
        _renderCts?.Cancel();
        try
        {
            _renderLoop?.Wait(500);
        }
        catch
        {
            // ignore shutdown issues
        }
        _renderCts?.Dispose();
        _renderCts = null;
        _renderLoop = null;
        _renderSignal.Reset();
    }

    private void RenderLoop(CancellationToken token)
    {
        LogMessage("RenderLoop started");
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool continuous = _freecamActive && _freecamInputEnabled;
                if (!continuous)
                {
                    _renderSignal.Wait(token);
                    _renderSignal.Reset();
                    RenderFrame();
                    continue;
                }

                float cap = _viewportFpsCapCached;
                if (cap > 0)
                {
                    double targetMs = 1000.0 / cap;
                    long nowTicks = _frameLimiter.ElapsedTicks;
                    double elapsedMs = (nowTicks - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs < targetMs)
                    {
                        int wait = (int)Math.Max(1.0, targetMs - elapsedMs);
                        _renderSignal.Wait(wait, token);
                    }
                    _renderSignal.Reset();
                    _lastFrameTicks = _frameLimiter.ElapsedTicks;
                    RenderFrame();
                }
                else
                {
                    _renderSignal.Wait(1, token);
                    _renderSignal.Reset();
                    RenderFrame();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"RenderLoop error: {ex}");
                Thread.Sleep(50);
            }
        }
        LogMessage("RenderLoop stopped");
    }

    private static void LogMessage(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] D3D11Viewport: {message}";
        try
        {
            Console.WriteLine(line);
            if (!_logPathAnnounced)
            {
                _logPathAnnounced = true;
                Console.WriteLine($"[D3D11Viewport] Log file: {LogPath}");
            }
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            if (!_logWriteFailedLogged)
            {
                _logWriteFailedLogged = true;
                Console.WriteLine($"[D3D11Viewport] Log file write failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void ResetLogFile()
    {
        try
        {
            File.WriteAllText(LogPath, string.Empty);
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static string GetLogPath()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "d3d11_viewport.log");
            using var _ = File.AppendText(path);
            return path;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "d3d11_viewport.log");
        }
    }

    private void DrawLabelOverlay(Matrix4x4 viewProjection, int width, int height, ID3D11Texture2D backBuffer)
    {
        var drawPins = _pinLabels.Count > 0;
        var drawFps = _showFpsCached;
        if (!drawPins && !drawFps)
        {
            lock (_labelLock)
            {
                _labelHitCache = new List<PinLabel>();
            }
            return;
        }

        if (_d2dContext == null || _d2dTextBrush == null || _labelFormat == null)
        {
            lock (_labelLock)
            {
                _labelHitCache = new List<PinLabel>();
            }
            return;
        }

        if (!EnsureD2DTarget(backBuffer, width, height))
            return;

        const float fontSize = 16f;
        const float fontWidthFactor = 0.6f;
        const float padding = 6f;

        var projected = new List<PinLabel>(_pinLabels.Count);
        _d2dContext.Target = _d2dTarget;
        _d2dContext.BeginDraw();
        _d2dContext.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _d2dContext.Transform = Matrix3x2.Identity;

        if (drawPins)
        {
            foreach (var label in _pinLabels)
            {
                if (string.IsNullOrEmpty(label.Text))
                    continue;

                if (!TryProjectToScreen(label.World, viewProjection, width, height, out var screen))
                    continue;

                var textWidth = Math.Max(1f, label.Text.Length * fontSize * fontWidthFactor) + padding;
                var textHeight = fontSize * 1.2f + padding;
                var rect = new Vortice.RawRectF(
                    (float)screen.X - textWidth * 0.5f,
                    (float)screen.Y - textHeight * 0.5f,
                    (float)screen.X + textWidth * 0.5f,
                    (float)screen.Y + textHeight * 0.5f);

                _d2dTextBrush.Color = ToColor4(label.LabelColor);
                _d2dContext.DrawText(label.Text, _labelFormat, rect, _d2dTextBrush);

                label.Screen = screen;
                label.ScreenX = screen.X;
                label.ScreenY = screen.Y;
                projected.Add(label);
            }
        }

        if (drawFps && _fpsFormat != null)
        {
            const float margin = 8f;
            var text = $"FPS: {_fpsValue:0.0}";
            var rect = new Vortice.RawRectF(margin, margin, width - margin, margin + 24f);
            _d2dTextBrush.Color = new Color4(0.9f, 0.9f, 0.9f, 0.85f);
            _d2dContext.DrawText(text, _fpsFormat, rect, _d2dTextBrush);
        }

        try
        {
            _d2dContext.EndDraw();
        }
        catch (SharpGenException ex)
        {
            LogMessage($"D2D EndDraw failed: 0x{ex.ResultCode.Code:X8}");
            _d2dTarget?.Dispose();
            _d2dTarget = null;
        }

        lock (_labelLock)
        {
            _labelHitCache = projected;
        }
    }

    private bool EnsureD2DTarget(ID3D11Texture2D backBuffer, int width, int height)
    {
        if (_d2dContext == null)
            return false;

        if (_d2dTarget != null && _d2dTargetWidth == width && _d2dTargetHeight == height)
            return true;

        _d2dTarget?.Dispose();
        _d2dTarget = null;

        using var surface = backBuffer.QueryInterface<IDXGISurface>();
        double scale = (VisualRoot as Avalonia.Rendering.IRenderRoot)?.RenderScaling ?? 1.0;
        float dpi = (float)(96.0 * scale);
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dpi,
            dpi,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _d2dTarget = _d2dContext.CreateBitmapFromDxgiSurface(surface, props);
        _d2dTargetWidth = width;
        _d2dTargetHeight = height;
        return true;
    }

    private void ResetD2DTarget()
    {
        if (_d2dContext != null)
            _d2dContext.Target = null;
        _d2dTarget?.Dispose();
        _d2dTarget = null;
        _d2dTargetWidth = 0;
        _d2dTargetHeight = 0;
    }

    private static bool TryProjectToScreen(Vector3 world, Matrix4x4 viewProjection, int width, int height, out Point screen)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (Math.Abs(clip.W) < 1e-5f)
        {
            screen = default;
            return false;
        }

        var ndc = clip / clip.W;
        if (ndc.Z < -1f || ndc.Z > 1f)
        {
            screen = default;
            return false;
        }

        var x = (ndc.X * 0.5f + 0.5f) * width;
        var y = (-ndc.Y * 0.5f + 0.5f) * height;
        screen = new Point(x, y);
        return true;
    }

    private const string VertexShaderHlsl = @"
cbuffer Constants : register(b0)
{
    row_major float4x4 uMvp;
    float4 uColor;
    float4 uLightDirAmbient;
};

struct VS_IN
{
    float3 Pos : POSITION;
    float3 Normal : NORMAL;
};

struct VS_OUT
{
    float4 Pos : SV_POSITION;
    float3 Normal : NORMAL;
};

VS_OUT VSMain(VS_IN input)
{
    VS_OUT output;
    float4 clip = mul(float4(input.Pos, 1.0), uMvp);
    clip.z = clip.z * 0.5 + clip.w * 0.5;
    output.Pos = clip;
    output.Normal = input.Normal;
    return output;
}
";

    private const string PixelShaderHlsl = @"
cbuffer Constants : register(b0)
{
    row_major float4x4 uMvp;
    float4 uColor;
    float4 uLightDirAmbient;
};

struct VS_OUT
{
    float4 Pos : SV_POSITION;
    float3 Normal : NORMAL;
};

float4 PSMain(VS_OUT input) : SV_Target
{
    float3 n = normalize(input.Normal);
    float ndl = max(dot(n, normalize(uLightDirAmbient.xyz)), 0.0);
    float3 lit = uColor.xyz * (uLightDirAmbient.w + (1.0 - uLightDirAmbient.w) * ndl);
    return float4(lit, 1.0);
}
";
}




