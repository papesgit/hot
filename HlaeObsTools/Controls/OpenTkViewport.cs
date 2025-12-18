using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
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

    private bool _dragging;
    private bool _panning;
    private Point _lastPointer;

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

    private readonly ObservableCollection<PinLabel> _labels = new();
    public ReadOnlyObservableCollection<PinLabel> Labels { get; }

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

    private void HandlePointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed || updateKind == PointerUpdateKind.MiddleButtonPressed;

        UpdateInputStatus($"Input: down M:{middlePressed} Shift:{e.KeyModifiers.HasFlag(KeyModifiers.Shift)}");

        if (!middlePressed)
            return;

        _dragging = true;
        _panning = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    private void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed;
        var released = updateKind == PointerUpdateKind.MiddleButtonReleased
            || (!middlePressed);

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
        if (!_dragging)
        {
            UpdateInputStatus("Input: move");
            return;
        }

        var point = e.GetCurrentPoint(this);
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

        var zoomFactor = MathF.Pow(1.1f, (float)-e.Delta.Y);
        _distance = Math.Clamp(_distance * zoomFactor, _minDistance, _maxDistance);
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

    private void OnWorldTransformChanged()
    {
        _pinsDirty = true;
        RequestNextFrameRendering();
    }

    public void SetPins(IReadOnlyList<ViewportPin> pins)
    {
        var rotation = GetWorldRotation();
        var offset = new Vector3(WorldOffsetX, WorldOffsetY, WorldOffsetZ);
        var scale = WorldScale;

        var list = new List<PinRenderData>();
        foreach (var pin in pins)
        {
            var position = new Vector3((float)pin.Position.X, (float)pin.Position.Y, (float)pin.Position.Z);
            var forward = new Vector3((float)pin.Forward.X, (float)pin.Forward.Y, (float)pin.Forward.Z);
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
        var nearPlane = Math.Max(0.05f, _distance * 0.01f);
        var farPlane = Math.Max(100f, _distance * 10f);
        var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60f), aspect, nearPlane, farPlane);
        var view = Matrix4.LookAt(GetCameraPosition(), _target, Vector3.UnitY);
        return view * projection;
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

        var labelOffset = new Vector3(0, sphereRadius * 2.2f, 0);
        labels.Add(new PinLabel { Text = pin.Label, World = pin.Position + labelOffset });
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
            var clip = new Vector4(
                world.X * viewProjection.M11 + world.Y * viewProjection.M12 + world.Z * viewProjection.M13 + viewProjection.M14,
                world.X * viewProjection.M21 + world.Y * viewProjection.M22 + world.Z * viewProjection.M23 + viewProjection.M24,
                world.X * viewProjection.M31 + world.Y * viewProjection.M32 + world.Z * viewProjection.M33 + viewProjection.M34,
                world.X * viewProjection.M41 + world.Y * viewProjection.M42 + world.Z * viewProjection.M43 + viewProjection.M44);

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
                Screen = new Point(x, y)
            });
        }

        Dispatcher.UIThread.Post(() =>
        {
            _labels.Clear();
            foreach (var label in projected)
                _labels.Add(label);
        });
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
