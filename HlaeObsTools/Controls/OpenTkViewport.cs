using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using HlaeObsTools.Services.Viewport3D;
using System.Collections.ObjectModel;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Controls;

public sealed class OpenTkViewport : OpenGlControlBase
{
    public static readonly StyledProperty<string?> MapPathProperty =
        AvaloniaProperty.Register<OpenTkViewport, string?>(nameof(MapPath));
    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<OpenTkViewport, string?>(nameof(StatusText), string.Empty);
    public static readonly StyledProperty<float> PinScaleProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(PinScale), 1.0f);
    public static readonly StyledProperty<float> PinOffsetXProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(PinOffsetX), 0.0f);
    public static readonly StyledProperty<float> PinOffsetYProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(PinOffsetY), 0.0f);
    public static readonly StyledProperty<float> PinOffsetZProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(PinOffsetZ), 0.0f);
    public static readonly StyledProperty<float> MapScaleProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(MapScale), 1.0f);
    public static readonly StyledProperty<float> MapYawProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(MapYaw), 0.0f);
    public static readonly StyledProperty<float> MapPitchProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(MapPitch), 0.0f);
    public static readonly StyledProperty<float> MapRollProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(MapRoll), 0.0f);
    public static readonly StyledProperty<float> MapOffsetXProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(MapOffsetX), 0.0f);
    public static readonly StyledProperty<float> MapOffsetYProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(MapOffsetY), 0.0f);
    public static readonly StyledProperty<float> MapOffsetZProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(MapOffsetZ), 0.0f);
    public static readonly StyledProperty<float> WorldScaleProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(WorldScale), 1.0f);
    public static readonly StyledProperty<float> WorldYawProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(WorldYaw), 0.0f);
    public static readonly StyledProperty<float> WorldPitchProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(WorldPitch), 0.0f);
    public static readonly StyledProperty<float> WorldRollProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(WorldRoll), 0.0f);
    public static readonly StyledProperty<float> WorldOffsetXProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(WorldOffsetX), 0.0f);
    public static readonly StyledProperty<float> WorldOffsetYProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(WorldOffsetY), 0.0f);
    public static readonly StyledProperty<float> WorldOffsetZProperty =
        AvaloniaProperty.Register<OpenTkViewport, float>(nameof(WorldOffsetZ), 0.0f);
    public static readonly StyledProperty<FreecamSettings?> FreecamSettingsProperty =
        AvaloniaProperty.Register<OpenTkViewport, FreecamSettings?>(nameof(FreecamSettings));

    private int _vao;
    private int _vbo;
    private int _shaderProgram;
    private int _vertexCount;
    private int _mvpLocation;
    private int _colorLocation;
    private int _lightDirLocation;
    private int _ambientLocation;
    private bool _glReady;
    private bool _supportsVao;
    private ObjMesh? _pendingMesh;
    private bool _meshDirty;
    private ObjMesh? _loadedMeshOriginal;
    private int _gridVao;
    private int _gridVbo;
    private int _gridVertexCount;
    private int _groundVao;
    private int _groundVbo;
    private int _groundVertexCount;
    private int _debugVao;
    private int _debugVbo;
    private int _debugVertexCount;
    private int _pinVao;
    private int _pinVbo;
    private int _pinVertexCount;
    private string _statusPrefix = string.Empty;
    private bool _showDebugTriangle = true;
    private bool _showGroundPlane = true;
    private string _inputStatus = "Input: idle";
    private readonly Vector3 _lightDir = Vector3.Normalize(new Vector3(0.4f, 0.9f, 0.2f));
    private const float AmbientLight = 0.25f;
    private List<PinRenderData> _pins = new();
    private readonly List<PinDrawCall> _pinDraws = new();
    private List<PinLabel> _pinLabels = new();
    private bool _pinsDirty;
    private readonly Vector3[] _pinConeUnit = CreateUnitCone();
    private readonly Vector3[] _pinConeNormals = CreateUnitConeNormals();
    private readonly Vector3[] _pinSphereUnit;
    private readonly Vector3[] _pinSphereNormals;

    private Vector3 _target = Vector3.Zero;
    private float _distance = 10f;
    private float _yaw = MathHelper.DegreesToRadians(45f);
    private float _pitch = MathHelper.DegreesToRadians(30f);
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
    private Vector3 _freecamLastSmoothedPosition;
    private Vector2 _freecamMouseDelta;
    private float _freecamWheelDelta;
    private DateTime _freecamLastUpdate;
    private FreecamTransform _freecamTransform;
    private FreecamTransform _freecamSmoothed;
    private FreecamConfig _freecamConfig = FreecamConfig.Default;
    private FreecamSettings? _freecamSettings;
    private readonly HashSet<Key> _keysDown = new();
    private bool _mouseButton4Down;
    private bool _mouseButton5Down;

    public OpenTkViewport()
    {
        Focusable = true;
        IsHitTestVisible = true;
        StatusText = "GL init pending...";
        Labels = new ReadOnlyObservableCollection<PinLabel>(_labels);
        (_pinSphereUnit, _pinSphereNormals) = CreateUnitSphere(16, 32);
    }

    static OpenTkViewport()
    {
        MapPathProperty.Changed.AddClassHandler<OpenTkViewport>((sender, args) => sender.OnMapPathChanged(args));
        PinScaleProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnPinScaleChanged());
        PinOffsetXProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnPinOffsetChanged());
        PinOffsetYProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnPinOffsetChanged());
        PinOffsetZProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnPinOffsetChanged());
        MapScaleProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnMapTransformChanged());
        MapYawProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnMapTransformChanged());
        MapPitchProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnMapTransformChanged());
        MapRollProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnMapTransformChanged());
        MapOffsetXProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnMapTransformChanged());
        MapOffsetYProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnMapTransformChanged());
        MapOffsetZProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnMapTransformChanged());
        WorldScaleProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnWorldTransformChanged());
        WorldYawProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnWorldTransformChanged());
        WorldPitchProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnWorldTransformChanged());
        WorldRollProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnWorldTransformChanged());
        WorldOffsetXProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnWorldTransformChanged());
        WorldOffsetYProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnWorldTransformChanged());
        WorldOffsetZProperty.Changed.AddClassHandler<OpenTkViewport>((sender, _) => sender.OnWorldTransformChanged());
        FreecamSettingsProperty.Changed.AddClassHandler<OpenTkViewport>((sender, args) => sender.OnFreecamSettingsChanged(args));
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

    public float PinOffsetX
    {
        get => GetValue(PinOffsetXProperty);
        set => SetValue(PinOffsetXProperty, value);
    }

    public float PinOffsetY
    {
        get => GetValue(PinOffsetYProperty);
        set => SetValue(PinOffsetYProperty, value);
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

    public float WorldScale
    {
        get => GetValue(WorldScaleProperty);
        set => SetValue(WorldScaleProperty, value);
    }

    public float WorldYaw
    {
        get => GetValue(WorldYawProperty);
        set => SetValue(WorldYawProperty, value);
    }

    public float WorldPitch
    {
        get => GetValue(WorldPitchProperty);
        set => SetValue(WorldPitchProperty, value);
    }

    public float WorldRoll
    {
        get => GetValue(WorldRollProperty);
        set => SetValue(WorldRollProperty, value);
    }

    public float WorldOffsetX
    {
        get => GetValue(WorldOffsetXProperty);
        set => SetValue(WorldOffsetXProperty, value);
    }

    public float WorldOffsetY
    {
        get => GetValue(WorldOffsetYProperty);
        set => SetValue(WorldOffsetYProperty, value);
    }

    public float WorldOffsetZ
    {
        get => GetValue(WorldOffsetZProperty);
        set => SetValue(WorldOffsetZProperty, value);
    }

    public FreecamSettings? FreecamSettings
    {
        get => GetValue(FreecamSettingsProperty);
        set => SetValue(FreecamSettingsProperty, value);
    }

    private readonly ObservableCollection<PinLabel> _labels = new();
    public ReadOnlyObservableCollection<PinLabel> Labels { get; }

    public bool IsFreecamActive => _freecamActive;
    public bool IsFreecamInputEnabled => _freecamInputEnabled;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestNextFrameRendering();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _dragging = false;
        _panning = false;
        DisableFreecam();
        _keysDown.Clear();
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
            RawFov = _freecamTransform.Fov,
            SmoothedPosition = _freecamSmoothed.Position,
            SmoothedForward = Vector3.Normalize(smoothForward),
            SmoothedUp = Vector3.Normalize(smoothUp),
            SmoothedFov = _freecamSmoothed.Fov,
            SpeedScalar = _freecamSpeedScalar
        };
        return true;
    }

    public void DisableFreecamInput()
    {
        EndFreecamInput();
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
        if (_labels.Count == 0)
            return false;

        const double fontSize = 16.0;
        const double fontWidthFactor = 0.6;
        const double padding = 6.0;

        foreach (var label in _labels)
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
        if (forward.LengthSquared < 0.0001f)
            forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);

        var yaw = MathHelper.RadiansToDegrees(MathF.Atan2(forward.Z, forward.X));
        var pitch = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)));
        var fov = _freecamActive ? _freecamTransform.Fov : _freecamConfig.DefaultFov;

        _freecamTransform = new FreecamTransform
        {
            Position = pin.Position,
            Yaw = yaw,
            Pitch = pitch,
            Roll = 0f,
            Fov = fov
        };
        _freecamSmoothed = _freecamTransform;
        _freecamActive = true;
        _freecamInitialized = true;
        _freecamInputEnabled = keepInputEnabled;
        _freecamLastUpdate = DateTime.UtcNow;
        ResetFreecamState();
        RequestNextFrameRendering();
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
                RequestNextFrameRendering();
                e.Handled = true;
                return;
            }

            if (TryGetScreenPoint(point.Position, out var screenPoint))
            {
                var dx = screenPoint.X - _freecamCenterScreen.X;
                var dy = screenPoint.Y - _freecamCenterScreen.Y;
                if (dx != 0 || dy != 0)
                    _freecamMouseDelta += new Vector2(dx, dy);
            }
            CenterFreecamCursor();
            UpdateInputStatus("Input: freecam");
            RequestNextFrameRendering();
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
        RequestNextFrameRendering();
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
            RequestNextFrameRendering();
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
        RequestNextFrameRendering();
        e.Handled = true;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        GL.LoadBindings(new AvaloniaBindingsContext(gl));
        _shaderProgram = CreateShaderProgram();
        _mvpLocation = _shaderProgram == 0 ? -1 : GL.GetUniformLocation(_shaderProgram, "uMvp");
        _colorLocation = _shaderProgram == 0 ? -1 : GL.GetUniformLocation(_shaderProgram, "uColor");
        _lightDirLocation = _shaderProgram == 0 ? -1 : GL.GetUniformLocation(_shaderProgram, "uLightDir");
        _ambientLocation = _shaderProgram == 0 ? -1 : GL.GetUniformLocation(_shaderProgram, "uAmbient");
        _glReady = true;
        _supportsVao = CheckVaoSupport();

        GL.Enable(EnableCap.DepthTest);
        GL.LineWidth(1.5f);
        UpdateGrid(10f, 20);
        CreateGroundPlane(10f);
        UpdateStatusText();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        base.OnOpenGlDeinit(gl);
        _glReady = false;

        if (_vao != 0)
            GL.DeleteVertexArray(_vao);
        if (_vbo != 0)
            GL.DeleteBuffer(_vbo);
        if (_gridVao != 0)
            GL.DeleteVertexArray(_gridVao);
        if (_gridVbo != 0)
            GL.DeleteBuffer(_gridVbo);
        if (_groundVao != 0)
            GL.DeleteVertexArray(_groundVao);
        if (_groundVbo != 0)
            GL.DeleteBuffer(_groundVbo);
        if (_debugVao != 0)
            GL.DeleteVertexArray(_debugVao);
        if (_debugVbo != 0)
            GL.DeleteBuffer(_debugVbo);
        if (_pinVao != 0)
            GL.DeleteVertexArray(_pinVao);
        if (_pinVbo != 0)
            GL.DeleteBuffer(_pinVbo);
        if (_shaderProgram != 0)
            GL.DeleteProgram(_shaderProgram);

        _vao = 0;
        _vbo = 0;
        _gridVao = 0;
        _gridVbo = 0;
        _groundVao = 0;
        _groundVbo = 0;
        _debugVao = 0;
        _debugVbo = 0;
        _pinVao = 0;
        _pinVbo = 0;
        _shaderProgram = 0;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_glReady)
            return;

        if (_meshDirty)
            UploadPendingMesh();
        if (_pinsDirty)
            RebuildPins();

        var width = Math.Max(1, (int)Bounds.Width);
        var height = Math.Max(1, (int)Bounds.Height);
        GL.Viewport(0, 0, width, height);

        GL.ClearColor(0.02f, 0.02f, 0.03f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_freecamActive)
        {
            var now = DateTime.UtcNow;
            if (_freecamLastUpdate == default)
                _freecamLastUpdate = now;
            var deltaTime = (float)(now - _freecamLastUpdate).TotalSeconds;
            _freecamLastUpdate = now;
            UpdateFreecam(deltaTime);
        }

        var viewProjection = CreateViewProjection(width, height);

        if (_showDebugTriangle && _debugVertexCount > 0 && _debugVbo != 0 && _shaderProgram != 0)
        {
            var mvp = Matrix4.Identity;
            ApplyCommonUniforms(ref mvp, new Vector3(1.0f, 0.2f, 0.8f));

            GL.Disable(EnableCap.DepthTest);
            BindGeometry(_debugVao, _debugVbo);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _debugVertexCount);
            UnbindGeometry();
            GL.Enable(EnableCap.DepthTest);
        }

        if (_showGroundPlane && _groundVertexCount > 0 && _groundVbo != 0 && _shaderProgram != 0)
        {
            var mvp = viewProjection;
            ApplyCommonUniforms(ref mvp, new Vector3(0.12f, 0.14f, 0.16f));

            BindGeometry(_groundVao, _groundVbo);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _groundVertexCount);
            UnbindGeometry();
        }

        if (_gridVertexCount > 0 && _gridVbo != 0 && _shaderProgram != 0)
        {
            var mvp = viewProjection;
            ApplyCommonUniforms(ref mvp, new Vector3(0.35f, 0.5f, 0.35f));

            GL.Disable(EnableCap.DepthTest);
            BindGeometry(_gridVao, _gridVbo);
            GL.DrawArrays(PrimitiveType.Lines, 0, _gridVertexCount);
            UnbindGeometry();
            GL.Enable(EnableCap.DepthTest);
        }

        if (_vertexCount > 0 && _vbo != 0 && _shaderProgram != 0)
        {
            var mvp = viewProjection;
            ApplyCommonUniforms(ref mvp, new Vector3(0.82f, 0.86f, 0.9f));

            BindGeometry(_vao, _vbo);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _vertexCount);
            UnbindGeometry();
        }

        if (_pinVertexCount > 0 && _pinVbo != 0 && _shaderProgram != 0)
        {
            var mvp = viewProjection;
            GL.UseProgram(_shaderProgram);
            if (_mvpLocation >= 0)
                GL.UniformMatrix4(_mvpLocation, false, ref mvp);
            if (_lightDirLocation >= 0)
                GL.Uniform3(_lightDirLocation, _lightDir);
            if (_ambientLocation >= 0)
                GL.Uniform1(_ambientLocation, AmbientLight);

            BindGeometry(_pinVao, _pinVbo);
            foreach (var draw in _pinDraws)
            {
                if (_colorLocation >= 0)
                    GL.Uniform3(_colorLocation, draw.Color);
                GL.DrawArrays(PrimitiveType.Triangles, draw.Start, draw.Count);
            }
            UnbindGeometry();
        }

        UpdateLabelOverlay(viewProjection, width, height);
        RequestNextFrameRendering();
    }

    private void OnMapPathChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var path = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            _pendingMesh = null;
            _loadedMeshOriginal = null;
            _meshDirty = true;
            RequestNextFrameRendering();
            return;
        }

        if (ObjMeshLoader.TryLoad(path, out var mesh, out _))
        {
            _loadedMeshOriginal = mesh;
            _pendingMesh = ApplyMapTransform(mesh);
            _meshDirty = true;
            RequestNextFrameRendering();
            return;
        }

        _pendingMesh = null;
        _loadedMeshOriginal = null;
        _meshDirty = true;
        RequestNextFrameRendering();
    }

    private void OnMapTransformChanged()
    {
        if (_loadedMeshOriginal == null)
            return;

        _pendingMesh = ApplyMapTransform(_loadedMeshOriginal);
        _meshDirty = true;
        RequestNextFrameRendering();
    }

    private void UploadPendingMesh()
    {
        _meshDirty = false;

        if (_pendingMesh == null)
        {
            if (_vao != 0)
                GL.DeleteVertexArray(_vao);
            if (_vbo != 0)
                GL.DeleteBuffer(_vbo);

            _vao = 0;
            _vbo = 0;
            _vertexCount = 0;
            return;
        }

        if (_vao != 0)
            GL.DeleteVertexArray(_vao);
        if (_vbo != 0)
            GL.DeleteBuffer(_vbo);

        _vao = _supportsVao ? GL.GenVertexArray() : 0;
        _vbo = GL.GenBuffer();

        if (_supportsVao)
            GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _pendingMesh.Vertices.Length * sizeof(float), _pendingMesh.Vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _vertexCount = _pendingMesh.VertexCount;
        ResetCameraToBounds(_pendingMesh.Min, _pendingMesh.Max);
        UpdateGridFromBounds(_pendingMesh.Min, _pendingMesh.Max);
        _pendingMesh = null;
    }

    private void OnPinScaleChanged()
    {
        _pinsDirty = true;
        RequestNextFrameRendering();
    }

    private void OnPinOffsetChanged()
    {
        _pinsDirty = true;
        RequestNextFrameRendering();
    }

    private void OnWorldTransformChanged()
    {
        _pinsDirty = true;
        RequestNextFrameRendering();
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
            FovMin = (float)_freecamSettings.FovMin,
            FovMax = (float)_freecamSettings.FovMax,
            FovStep = (float)_freecamSettings.FovStep,
            DefaultFov = (float)_freecamSettings.DefaultFov,
            SmoothEnabled = _freecamSettings.SmoothEnabled,
            HalfVec = (float)_freecamSettings.HalfVec,
            HalfRot = (float)_freecamSettings.HalfRot,
            HalfFov = (float)_freecamSettings.HalfFov
        };
    }

    public void SetPins(IReadOnlyList<ViewportPin> pins)
    {
        var rotation = GetWorldRotation();
        var offset = new Vector3(WorldOffsetX, WorldOffsetY, WorldOffsetZ);
        var scale = WorldScale;
        var pinOffset = new Vector3(PinOffsetX, PinOffsetY, PinOffsetZ);

        var list = new List<PinRenderData>();
        foreach (var pin in pins)
        {
            var position = new Vector3((float)pin.Position.X, (float)pin.Position.Y, (float)pin.Position.Z);
            var forward = new Vector3((float)pin.Forward.X, (float)pin.Forward.Y, (float)pin.Forward.Z);
            position += pinOffset;
            position *= scale;
            position = Transform(position, rotation) + offset;
            forward = Transform(forward, rotation);

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
        RequestNextFrameRendering();
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
        var prefix = string.IsNullOrWhiteSpace(_statusPrefix) ? "GL ready" : _statusPrefix;
        SetStatusText($"{prefix} | {gridInfo} | {groundInfo} | {debugInfo} | {_inputStatus}");
    }

    private void UpdateInputStatus(string status)
    {
        _inputStatus = status;
        UpdateStatusText();
    }

    private void ResetCameraToBounds(Vector3 min, Vector3 max)
    {
        _target = (min + max) * 0.5f;
        var radius = (max - min).Length * 0.5f;
        if (radius < 0.1f)
            radius = 0.1f;

        _distance = radius * 2.0f;
        _minDistance = radius * 0.2f;
        _maxDistance = radius * 20f;

        if (_distance < _minDistance)
            _distance = _minDistance;
        if (_distance > _maxDistance)
            _distance = _maxDistance;

        _yaw = MathHelper.DegreesToRadians(45f);
        _pitch = MathHelper.DegreesToRadians(30f);
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
        if (!_glReady)
            return;

        var half = size * 0.5f;
        var lines = divisions + 1;
        var vertices = new float[lines * 4 * 6];
        var step = size / divisions;
        var index = 0;

        for (var i = 0; i < lines; i++)
        {
            var offset = -half + i * step;

            AddVertex(vertices, ref index, -half, 0f, offset, 0f, 1f, 0f);
            AddVertex(vertices, ref index, half, 0f, offset, 0f, 1f, 0f);

            AddVertex(vertices, ref index, offset, 0f, -half, 0f, 1f, 0f);
            AddVertex(vertices, ref index, offset, 0f, half, 0f, 1f, 0f);
        }

        if (_gridVao != 0)
            GL.DeleteVertexArray(_gridVao);
        if (_gridVbo != 0)
            GL.DeleteBuffer(_gridVbo);

        _gridVao = _supportsVao ? GL.GenVertexArray() : 0;
        _gridVbo = GL.GenBuffer();

        if (_supportsVao)
            GL.BindVertexArray(_gridVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _gridVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _gridVertexCount = vertices.Length / 6;
        RequestNextFrameRendering();
        UpdateStatusText();
    }

    private Vector3 GetCameraPosition()
    {
        var cosPitch = MathF.Cos(_pitch);
        var sinPitch = MathF.Sin(_pitch);
        var cosYaw = MathF.Cos(_yaw);
        var sinYaw = MathF.Sin(_yaw);

        var direction = new Vector3(cosPitch * cosYaw, sinPitch, cosPitch * sinYaw);
        return _target + direction * _distance;
    }

    private Matrix4 CreateViewProjection(int width, int height)
    {
        var aspect = width / (float)height;
        if (_freecamActive)
        {
            var fov = GetSourceVerticalFovRadians(_freecamSmoothed.Fov);
            var projection = Matrix4.CreatePerspectiveFieldOfView(fov, aspect, 0.05f, 100000f);
            var view = CreateFreecamView(_freecamSmoothed);
            return view * projection;
        }

        var nearPlane = Math.Max(0.05f, _distance * 0.01f);
        var farPlane = Math.Max(100f, _distance * 10f);
        var projectionOrbit = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60f), aspect, nearPlane, farPlane);
        var viewOrbit = Matrix4.LookAt(GetCameraPosition(), _target, Vector3.UnitY);
        return viewOrbit * projectionOrbit;
    }

    private static float GetSourceVerticalFovRadians(float sourceFovDeg)
    {
        var hRad = MathHelper.DegreesToRadians(Math.Clamp(sourceFovDeg, 1.0f, 179.0f));
        var vRad = 2f * MathF.Atan(MathF.Tan(hRad * 0.5f) * (3f / 4f));
        return Math.Clamp(vRad, MathHelper.DegreesToRadians(1.0f), MathHelper.DegreesToRadians(179.0f));
    }

    private void ApplyCommonUniforms(ref Matrix4 mvp, Vector3 color)
    {
        GL.UseProgram(_shaderProgram);
        if (_mvpLocation >= 0)
            GL.UniformMatrix4(_mvpLocation, false, ref mvp);
        if (_colorLocation >= 0)
            GL.Uniform3(_colorLocation, color);
        if (_lightDirLocation >= 0)
            GL.Uniform3(_lightDirLocation, _lightDir);
        if (_ambientLocation >= 0)
            GL.Uniform1(_ambientLocation, AmbientLight);
    }

    private void Orbit(float deltaX, float deltaY)
    {
        const float rotateSpeed = 0.01f;
        _yaw += deltaX * rotateSpeed;
        _pitch += deltaY * rotateSpeed;
        _pitch = Math.Clamp(_pitch, -1.55f, 1.55f);
    }

    private void Pan(float deltaX, float deltaY)
    {
        var cameraPos = GetCameraPosition();
        var forward = Vector3.Normalize(_target - cameraPos);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
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
        var yaw = MathHelper.RadiansToDegrees(MathF.Atan2(forward.Z, forward.X));
        var pitch = MathHelper.RadiansToDegrees(MathF.Asin(forward.Y));

        _freecamTransform = new FreecamTransform
        {
            Position = cameraPos,
            Yaw = yaw,
            Pitch = pitch,
            Roll = 0f,
            Fov = _freecamConfig.DefaultFov
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

        if (_freecamConfig.SmoothEnabled)
        {
            ApplyFreecamSmoothing(deltaTime);
        }
        else
        {
            _freecamSmoothed = _freecamTransform;
        }
    }

    private void UpdateFreecamMouseLook(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        var deltaYaw = _freecamMouseDelta.X * _freecamConfig.MouseSensitivity;
        var deltaPitch = -_freecamMouseDelta.Y * _freecamConfig.MouseSensitivity;
        _freecamMouseDelta = Vector2.Zero;

        _freecamTransform.Yaw += deltaYaw;
        _freecamTransform.Pitch += deltaPitch;

        _freecamMouseVelocityX = deltaYaw / deltaTime;
        _freecamMouseVelocityY = deltaPitch / deltaTime;

        _freecamTransform.Pitch = Clamp(_freecamTransform.Pitch, -89.0f, 89.0f);

        while (_freecamTransform.Yaw > 180.0f) _freecamTransform.Yaw -= 360.0f;
        while (_freecamTransform.Yaw < -180.0f) _freecamTransform.Yaw += 360.0f;
    }

    private void UpdateFreecamMovement(float deltaTime)
    {
        var moveSpeed = _freecamConfig.MoveSpeed * _freecamSpeedScalar;
        var verticalSpeed = _freecamConfig.VerticalSpeed * _freecamSpeedScalar;

        if (IsShiftDown())
        {
            moveSpeed *= _freecamConfig.SprintMultiplier;
            verticalSpeed *= _freecamConfig.SprintMultiplier;
        }

        var forward = GetForwardVector(_freecamTransform.Pitch, _freecamTransform.Yaw);
        var right = GetRightVector(_freecamTransform.Yaw);
        var up = GetUpVector(_freecamTransform.Pitch, _freecamTransform.Yaw);

        var desiredVel = Vector3.Zero;

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

        var desiredSpeed = desiredVel.Length;
        var maxSpeed = moveSpeed;
        if (IsKeyDown(Key.Space) || IsCtrlDown())
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
        if (!_freecamConfig.SmoothEnabled)
        {
            if (IsKeyDown(Key.Q))
                _freecamTargetRoll += _freecamConfig.RollSpeed * deltaTime;
            if (IsKeyDown(Key.E))
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
            var right = GetRightVector(view.Yaw);

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

        var rotBlend = _freecamConfig.HalfRot > 0f
            ? 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / _freecamConfig.HalfRot)
            : 1.0f;

        var fovBlend = _freecamConfig.HalfFov > 0f
            ? 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / _freecamConfig.HalfFov)
            : 1.0f;

        _freecamSmoothed.Position = Vector3.Lerp(_freecamSmoothed.Position, _freecamTransform.Position, posBlend);

        var targetYaw = _freecamTransform.Yaw;
        var currentYaw = _freecamSmoothed.Yaw;
        while (targetYaw - currentYaw > 180.0f) targetYaw -= 360.0f;
        while (targetYaw - currentYaw < -180.0f) targetYaw += 360.0f;

        _freecamSmoothed.Pitch = Lerp(_freecamSmoothed.Pitch, _freecamTransform.Pitch, rotBlend);
        _freecamSmoothed.Yaw = Lerp(currentYaw, targetYaw, rotBlend);
        _freecamSmoothed.Roll = Lerp(_freecamSmoothed.Roll, _freecamTransform.Roll, rotBlend);
        _freecamSmoothed.Fov = Lerp(_freecamSmoothed.Fov, _freecamTransform.Fov, fovBlend);
    }

    private Matrix4 CreateFreecamView(FreecamTransform transform)
    {
        var forward = GetForwardVector(transform.Pitch, transform.Yaw);
        var right = GetRightVector(transform.Yaw);
        var up = GetUpVector(transform.Pitch, transform.Yaw);

        if (Math.Abs(transform.Roll) > 0.001f)
        {
            var rollRad = MathHelper.DegreesToRadians(transform.Roll);
            var rollMat = Matrix3.CreateFromAxisAngle(Vector3.Normalize(forward), rollRad);
            right = Transform(right, rollMat);
            up = Transform(up, rollMat);
        }

        return Matrix4.LookAt(transform.Position, transform.Position + forward, up);
    }

    private void GetFreecamBasis(FreecamTransform transform, out Vector3 forward, out Vector3 up)
    {
        forward = GetForwardVector(transform.Pitch, transform.Yaw);
        var right = GetRightVector(transform.Yaw);
        up = GetUpVector(transform.Pitch, transform.Yaw);

        if (Math.Abs(transform.Roll) > 0.001f)
        {
            var rollRad = MathHelper.DegreesToRadians(transform.Roll);
            var rollMat = Matrix3.CreateFromAxisAngle(Vector3.Normalize(forward), rollRad);
            right = Transform(right, rollMat);
            up = Transform(up, rollMat);
        }
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

        var centerLocal = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);
        if (!TryGetScreenPoint(centerLocal, out var centerScreen))
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

        var centerLocal = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);
        if (!TryGetScreenPoint(centerLocal, out var centerScreen))
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
        var pitch = MathHelper.DegreesToRadians(pitchDeg);
        var yaw = MathHelper.DegreesToRadians(yawDeg);
        var cosPitch = MathF.Cos(pitch);
        return new Vector3(
            cosPitch * MathF.Cos(yaw),
            MathF.Sin(pitch),
            cosPitch * MathF.Sin(yaw));
    }

    private static Vector3 GetRightVector(float yawDeg)
    {
        var yaw = MathHelper.DegreesToRadians(yawDeg);
        return new Vector3(-MathF.Sin(yaw), 0f, MathF.Cos(yaw));
    }

    private static Vector3 GetUpVector(float pitchDeg, float yawDeg)
    {
        var forward = GetForwardVector(pitchDeg, yawDeg);
        var right = GetRightVector(yawDeg);
        return Vector3.Normalize(Vector3.Cross(right, forward));
    }

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

    private int CreateShaderProgram()
    {
        var version = GL.GetString(StringName.Version) ?? "unknown";
        var glsl = GL.GetString(StringName.ShadingLanguageVersion) ?? "unknown";
        var isEs = version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        var errors = new List<string>();

        var esVariants = new[]
        {
            new ShaderVariant("es300", VertexEs300, FragmentEs300, BindAttribLocation: false),
            new ShaderVariant("es100", VertexEs100, FragmentEs100, BindAttribLocation: true)
        };
        var desktopVariants = new[]
        {
            new ShaderVariant("gl330", Vertex330, Fragment330, BindAttribLocation: false),
            new ShaderVariant("gl150", Vertex150, Fragment150, BindAttribLocation: false),
            new ShaderVariant("gl120", Vertex120, Fragment120, BindAttribLocation: true)
        };

        var variants = new List<ShaderVariant>();
        if (isEs)
        {
            variants.AddRange(esVariants);
            variants.AddRange(desktopVariants);
        }
        else
        {
            variants.AddRange(desktopVariants);
            variants.AddRange(esVariants);
        }

        foreach (var variant in variants)
        {
            var vertexShader = CompileShader(ShaderType.VertexShader, variant.VertexSource, out var vertexError);
            if (vertexShader == 0)
            {
                if (!string.IsNullOrWhiteSpace(vertexError))
                    errors.Add($"Vertex {variant.Name}: {vertexError}");
                continue;
            }

            var fragmentShader = CompileShader(ShaderType.FragmentShader, variant.FragmentSource, out var fragmentError);
            if (fragmentShader == 0)
            {
                if (!string.IsNullOrWhiteSpace(fragmentError))
                    errors.Add($"Fragment {variant.Name}: {fragmentError}");
                GL.DeleteShader(vertexShader);
                continue;
            }

            var program = GL.CreateProgram();
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            if (variant.BindAttribLocation)
            {
                GL.BindAttribLocation(program, 0, "aPos");
                GL.BindAttribLocation(program, 1, "aNormal");
            }

            GL.LinkProgram(program);

            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
            if (linked == 0)
            {
                var info = GL.GetProgramInfoLog(program);
                if (!string.IsNullOrWhiteSpace(info))
                    errors.Add($"Link {variant.Name}: {info}");
                GL.DeleteProgram(program);
                program = 0;
            }

            if (program != 0)
            {
                GL.DetachShader(program, vertexShader);
                GL.DetachShader(program, fragmentShader);
            }
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);

            if (program != 0)
            {
                _statusPrefix = $"GL: {version} | GLSL: {glsl} | Shader: {variant.Name}";
                return program;
            }
        }

        _statusPrefix = errors.Count > 0
            ? $"Shader compile failed ({version}). {string.Join(" | ", errors)}"
            : $"Shader compile failed ({version}).";

        return 0;
    }

    private int CompileShader(ShaderType type, string source, out string? error)
    {
        error = null;
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out var status);
        if (status == 0)
        {
            error = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            return 0;
        }

        return shader;
    }

    private bool CheckVaoSupport()
    {
        var version = GL.GetString(StringName.Version) ?? string.Empty;
        var extensions = GL.GetString(StringName.Extensions) ?? string.Empty;
        if (version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase))
            return version.Contains("OpenGL ES 3", StringComparison.OrdinalIgnoreCase) || extensions.Contains("GL_OES_vertex_array_object", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private void BindGeometry(int vao, int vbo)
    {
        if (_supportsVao && vao != 0)
        {
            GL.BindVertexArray(vao);
            return;
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
    }

    private void UnbindGeometry()
    {
        if (_supportsVao)
        {
            GL.BindVertexArray(0);
            return;
        }

        GL.DisableVertexAttribArray(0);
        GL.DisableVertexAttribArray(1);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

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
        if (!_glReady)
            return;

        var half = size * 0.5f;
        var vertices = new[]
        {
            -half, 0f, -half, 0f, 1f, 0f,
            half, 0f, -half, 0f, 1f, 0f,
            half, 0f, half, 0f, 1f, 0f,

            -half, 0f, -half, 0f, 1f, 0f,
            half, 0f, half, 0f, 1f, 0f,
            -half, 0f, half, 0f, 1f, 0f
        };

        if (_groundVao != 0)
            GL.DeleteVertexArray(_groundVao);
        if (_groundVbo != 0)
            GL.DeleteBuffer(_groundVbo);

        _groundVao = _supportsVao ? GL.GenVertexArray() : 0;
        _groundVbo = GL.GenBuffer();

        if (_supportsVao)
            GL.BindVertexArray(_groundVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _groundVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _groundVertexCount = vertices.Length / 6;
        RequestNextFrameRendering();
        UpdateStatusText();
    }

    private void CreateDebugTriangle()
    {
        if (!_glReady)
            return;

        var vertices = new[]
        {
            -0.8f, -0.8f, 0f, 0f, 0f, 1f,
            0.8f, -0.8f, 0f, 0f, 0f, 1f,
            0.0f, 0.8f, 0f, 0f, 0f, 1f
        };

        if (_debugVao != 0)
            GL.DeleteVertexArray(_debugVao);
        if (_debugVbo != 0)
            GL.DeleteBuffer(_debugVbo);

        _debugVao = _supportsVao ? GL.GenVertexArray() : 0;
        _debugVbo = GL.GenBuffer();

        if (_supportsVao)
            GL.BindVertexArray(_debugVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _debugVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _debugVertexCount = vertices.Length / 6;
        RequestNextFrameRendering();
    }

    private void RebuildPins()
    {
        _pinsDirty = false;
        if (_pins.Count == 0 || !_glReady)
        {
            _pinDraws.Clear();
            _pinLabels = new List<PinLabel>();
            _pinVertexCount = 0;
            if (_pinVao != 0)
                GL.DeleteVertexArray(_pinVao);
            if (_pinVbo != 0)
                GL.DeleteBuffer(_pinVbo);
            _pinVao = 0;
            _pinVbo = 0;
            return;
        }

        var data = new List<float>(_pins.Count * 256);
        var labels = new List<PinLabel>();
        _pinDraws.Clear();
        foreach (var pin in _pins)
        {
            var start = data.Count / 6;
            var added = AppendPinGeometry(pin, data, labels);
            _pinDraws.Add(new PinDrawCall { Start = start, Count = added, Color = pin.Color });
        }

        if (_pinVao != 0)
            GL.DeleteVertexArray(_pinVao);
        if (_pinVbo != 0)
            GL.DeleteBuffer(_pinVbo);

        _pinVao = _supportsVao ? GL.GenVertexArray() : 0;
        _pinVbo = GL.GenBuffer();
        if (_supportsVao)
            GL.BindVertexArray(_pinVao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _pinVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Count * sizeof(float), data.ToArray(), BufferUsageHint.DynamicDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _pinVertexCount = data.Count / 6;
        _pinLabels = labels;
    }

    private int AppendPinGeometry(PinRenderData pin, List<float> buffer, List<PinLabel> labels)
    {
        var added = 0;
        var forward = pin.Forward;
        if (forward.LengthSquared < 0.0001f)
            forward = new Vector3(0, 0, 1);
        forward = Vector3.Normalize(forward);

        var upHint = Vector3.UnitY;
        if (MathF.Abs(Vector3.Dot(forward, upHint)) > 0.95f)
            upHint = Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(upHint, forward));
        var up = Vector3.Normalize(Vector3.Cross(forward, right));

        Vector3 TransformLocal(Vector3 local)
        {
            return right * local.X + up * local.Y + forward * local.Z;
        }

        var pos = pin.Position;
        var scale = Math.Clamp(PinScale, 0.1f, 100f);
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
            LabelBrush = new SolidColorBrush(ToAvaloniaColor(pin.Color))
        });
        return added;
    }

    private static Vector3[] CreateUnitCone()
    {
        const int segments = 16;
        var verts = new List<Vector3>();
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathHelper.TwoPi / segments;
            float a1 = (i + 1) * MathHelper.TwoPi / segments;
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
            float a0 = i * MathHelper.TwoPi / segments;
            float a1 = (i + 1) * MathHelper.TwoPi / segments;
            var apex = new Vector3(0, 0, 1);
            var p0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0);
            var p1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0);
            var normal = Vector3.Cross(p0 - apex, p1 - apex);
            if (normal.LengthSquared < 0.0001f)
                normal = Vector3.UnitY;
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
                float p0 = u0 * MathHelper.TwoPi;
                float p1 = u1 * MathHelper.TwoPi;

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
            LeanAccelScale = 0.0015f,
            LeanVelocityScale = 0.01f,
            LeanMaxAngle = 20.0f,
            LeanHalfTime = 0.18f,
            FovMin = 10.0f,
            FovMax = 150.0f,
            FovStep = 2.0f,
            DefaultFov = 90.0f,
            SmoothEnabled = true,
            HalfVec = 0.5f,
            HalfRot = 0.5f,
            HalfFov = 0.5f
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
        public float FovMin { get; init; }
        public float FovMax { get; init; }
        public float FovStep { get; init; }
        public float DefaultFov { get; init; }
        public bool SmoothEnabled { get; init; }
        public float HalfVec { get; init; }
        public float HalfRot { get; init; }
        public float HalfFov { get; init; }
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
        public IBrush? LabelBrush { get; set; }
    }

    private readonly record struct ShaderVariant(string Name, string VertexSource, string FragmentSource, bool BindAttribLocation);

    private Matrix3 GetWorldRotation()
    {
        var yaw = MathHelper.DegreesToRadians(WorldYaw);
        var pitch = MathHelper.DegreesToRadians(WorldPitch);
        var roll = MathHelper.DegreesToRadians(WorldRoll);

        var yawMat = Matrix3.CreateRotationY(yaw);
        var pitchMat = Matrix3.CreateRotationX(pitch);
        var rollMat = Matrix3.CreateRotationZ(roll);
        return yawMat * pitchMat * rollMat;
    }

    private Matrix3 GetMapRotation()
    {
        var yaw = MathHelper.DegreesToRadians(MapYaw);
        var pitch = MathHelper.DegreesToRadians(MapPitch);
        var roll = MathHelper.DegreesToRadians(MapRoll);

        var yawMat = Matrix3.CreateRotationY(yaw);
        var pitchMat = Matrix3.CreateRotationX(pitch);
        var rollMat = Matrix3.CreateRotationZ(roll);
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
            pos = Transform(pos, rotation) + offset;
            normal = Transform(normal, rotation);

            vertices[i] = pos.X;
            vertices[i + 1] = pos.Y;
            vertices[i + 2] = pos.Z;
            vertices[i + 3] = normal.X;
            vertices[i + 4] = normal.Y;
            vertices[i + 5] = normal.Z;

            min = Vector3.ComponentMin(min, pos);
            max = Vector3.ComponentMax(max, pos);
        }

        return new ObjMesh(vertices, mesh.VertexCount, min, max);
    }

    private static Vector3 Transform(Vector3 value, Matrix3 matrix)
    {
        return new Vector3(
            value.X * matrix.M11 + value.Y * matrix.M21 + value.Z * matrix.M31,
            value.X * matrix.M12 + value.Y * matrix.M22 + value.Z * matrix.M32,
            value.X * matrix.M13 + value.Y * matrix.M23 + value.Z * matrix.M33);
    }

    private static Color ToAvaloniaColor(Vector3 color)
    {
        static byte ToByte(float value)
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            return (byte)MathF.Round(clamped * 255f);
        }

        return Color.FromRgb(ToByte(color.X), ToByte(color.Y), ToByte(color.Z));
    }

    private void UpdateLabelOverlay(Matrix4 viewProjection, int width, int height)
    {
        if (_pinLabels.Count == 0)
        {
            if (_labels.Count > 0)
                Dispatcher.UIThread.Post(_labels.Clear);
            return;
        }

        var projected = new List<PinLabel>(_pinLabels.Count);
        foreach (var label in _pinLabels)
        {
            var world = label.World;
            var clip = Vector4.TransformRow(new Vector4(world, 1f), viewProjection);

            if (Math.Abs(clip.W) < 1e-5f)
                continue;
            var ndc = clip / clip.W;
            if (ndc.Z < -1f || ndc.Z > 1f)
                continue;

            var x = (ndc.X * 0.5f + 0.5f) * width;
            var y = (-ndc.Y * 0.5f + 0.5f) * height;
            projected.Add(new PinLabel
            {
                Text = label.Text,
                World = label.World,
                Screen = new Point(x, y),
                ScreenX = x,
                ScreenY = y,
                LabelBrush = label.LabelBrush
            });
        }

        Dispatcher.UIThread.Post(() =>
        {
            _labels.Clear();
            foreach (var label in projected)
                _labels.Add(label);
        });
    }

    private static bool TryProjectToScreen(Vector3 world, Matrix4 viewProjection, int width, int height, out Point screen)
    {
        var clip = Vector4.TransformRow(new Vector4(world, 1f), viewProjection);
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

    private const string Vertex330 = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uMvp;
        out vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string Fragment330 = """
        #version 330 core
        in vec3 vNormal;
        out vec4 FragColor;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            FragColor = vec4(lit, 1.0);
        }
        """;

    private const string Vertex150 = """
        #version 150
        in vec3 aPos;
        in vec3 aNormal;
        uniform mat4 uMvp;
        out vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string Fragment150 = """
        #version 150
        in vec3 vNormal;
        out vec4 FragColor;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            FragColor = vec4(lit, 1.0);
        }
        """;

    private const string Vertex120 = """
        #version 120
        attribute vec3 aPos;
        attribute vec3 aNormal;
        uniform mat4 uMvp;
        varying vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string Fragment120 = """
        #version 120
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        varying vec3 vNormal;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            gl_FragColor = vec4(lit, 1.0);
        }
        """;

    private const string VertexEs300 = """
        #version 300 es
        precision mediump float;
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uMvp;
        out vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentEs300 = """
        #version 300 es
        precision mediump float;
        out vec4 FragColor;
        in vec3 vNormal;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            FragColor = vec4(lit, 1.0);
        }
        """;

    private const string VertexEs100 = """
        attribute vec3 aPos;
        attribute vec3 aNormal;
        uniform mat4 uMvp;
        varying vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentEs100 = """
        precision mediump float;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        varying vec3 vNormal;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            gl_FragColor = vec4(lit, 1.0);
        }
        """;

    private sealed class AvaloniaBindingsContext : OpenTK.IBindingsContext
    {
        private readonly GlInterface _gl;

        public AvaloniaBindingsContext(GlInterface gl)
        {
            _gl = gl;
        }

        public IntPtr GetProcAddress(string procName)
        {
            return _gl.GetProcAddress(procName);
        }
    }
}
