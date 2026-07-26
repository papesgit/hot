using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTK.Graphics.OpenGL;
using Box2i = OpenTK.Mathematics.Box2i;
using Vector2i = OpenTK.Mathematics.Vector2i;
using GLPixelFormat = OpenTK.Graphics.OpenGL.PixelFormat;
using GLWindowState = OpenTK.Windowing.Common.WindowState;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;
using HlaeObsTools.Services.Input;
using HlaeObsTools.Services.LiveLink;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.ViewModels;
using SkiaSharp;
using Svg.Skia;
using ValveResourceFormat;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.Utils;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.ResourceTypes;

namespace HlaeObsTools.Controls;

public sealed class VRFViewport : NativeControlHost, IViewport3DControl
{
    public event Action<Vector3, Quaternion>? CampathGizmoPoseChanged;
    public event Action? CampathGizmoDragEnded;
    private const float MaxUncappedFps = 1000f;
    private static readonly string LogPath = GetLogPath();
    private static bool _logPathAnnounced;
    private static bool _logWriteFailedLogged;
    private static readonly string WndClassName = $"HOT_VRFViewportHost{Guid.NewGuid():N}";
    private static readonly Dictionary<IntPtr, WeakReference<VRFViewport>> HostMap = new();
    private static bool _classRegistered;
    private static readonly object ClassLock = new();
    private static WndProcDelegate? _wndProc;
    private static IntPtr _wndProcPtr = IntPtr.Zero;
    private static readonly string[] LiveLinkIconFilePrefixes =
    [
        "weapon_rif_",
        "weapon_pist_",
        "weapon_smg_",
        "weapon_shotgun_",
        "weapon_sniper_",
        "weapon_mach_",
        "weapon_",
    ];
    private static readonly Vector3 LiveLinkProjectileIconTint = new(1.0f, 0.9f, 0.15f);

    public static readonly StyledProperty<string?> MapPathProperty =
        AvaloniaProperty.Register<VRFViewport, string?>(nameof(MapPath));
    public static readonly StyledProperty<float> PinScaleProperty =
        AvaloniaProperty.Register<VRFViewport, float>(nameof(PinScale), 200.0f);
    public static readonly StyledProperty<float> PinOffsetZProperty =
        AvaloniaProperty.Register<VRFViewport, float>(nameof(PinOffsetZ), 55.0f);
    public static readonly StyledProperty<bool> ShowPlayerPinsProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(ShowPlayerPins), true);
    public static readonly StyledProperty<float> ViewportMouseScaleProperty =
        AvaloniaProperty.Register<VRFViewport, float>(nameof(ViewportMouseScale), 0.75f);
    public static readonly StyledProperty<float> ViewportFpsCapProperty =
        AvaloniaProperty.Register<VRFViewport, float>(nameof(ViewportFpsCap), 60.0f);
    public static readonly StyledProperty<bool> PostprocessEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(PostprocessEnabled), true);
    public static readonly StyledProperty<bool> ColorCorrectionEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(ColorCorrectionEnabled), true);
    public static readonly StyledProperty<bool> DynamicShadowsEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(DynamicShadowsEnabled), true);
    public static readonly StyledProperty<bool> WireframeEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(WireframeEnabled), false);
    public static readonly StyledProperty<bool> SkipWaterEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(SkipWaterEnabled), false);
    public static readonly StyledProperty<bool> SkipTranslucentEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(SkipTranslucentEnabled), false);
    public static readonly StyledProperty<bool> ShowFpsProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(ShowFps), false);
    public static readonly StyledProperty<int> ShadowTextureSizeProperty =
        AvaloniaProperty.Register<VRFViewport, int>(nameof(ShadowTextureSize), 1024);
    public static readonly StyledProperty<int> MaxTextureSizeProperty =
        AvaloniaProperty.Register<VRFViewport, int>(nameof(MaxTextureSize), 1024);
    public static readonly StyledProperty<string> RenderModeProperty =
        AvaloniaProperty.Register<VRFViewport, string>(nameof(RenderMode), "Default");
    public static readonly StyledProperty<FreecamSettings?> FreecamSettingsProperty =
        AvaloniaProperty.Register<VRFViewport, FreecamSettings?>(nameof(FreecamSettings));
    public static readonly StyledProperty<HlaeInputSender?> InputSenderProperty =
        AvaloniaProperty.Register<VRFViewport, HlaeInputSender?>(nameof(InputSender));
    public static readonly StyledProperty<Cs2LiveLinkReceiver?> LiveLinkReceiverProperty =
        AvaloniaProperty.Register<VRFViewport, Cs2LiveLinkReceiver?>(nameof(LiveLinkReceiver));
    public static readonly StyledProperty<bool> LiveLinkEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(LiveLinkEnabled));
    public static readonly StyledProperty<bool> LiveLinkItemIconsEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(LiveLinkItemIconsEnabled), true);
    public static readonly StyledProperty<bool> LiveLinkWeaponIconsEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(LiveLinkWeaponIconsEnabled), true);
    public static readonly StyledProperty<bool> LiveLinkGrenadeIconsEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(LiveLinkGrenadeIconsEnabled), true);
    public static readonly StyledProperty<bool> LiveLinkProjectileIconsEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(LiveLinkProjectileIconsEnabled), true);
    public static readonly StyledProperty<bool> LiveLinkObjectiveIconsEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(LiveLinkObjectiveIconsEnabled), true);
    public static readonly StyledProperty<bool> LiveLinkDeadPlayerIconsEnabledProperty =
        AvaloniaProperty.Register<VRFViewport, bool>(nameof(LiveLinkDeadPlayerIconsEnabled), true);
    public static readonly StyledProperty<int> LiveLinkPortProperty =
        AvaloniaProperty.Register<VRFViewport, int>(nameof(LiveLinkPort), 31237);
    public static readonly StyledProperty<int> TargetOrbitResetRequestProperty =
        AvaloniaProperty.Register<VRFViewport, int>(nameof(TargetOrbitResetRequest));

    private IntPtr _hwnd;
    private NativeWindow? _nativeWindow;
    private readonly object _nativeWindowLock = new();
    private bool _nativeInitDone;
    private int _renderWidth;
    private int _renderHeight;

    private RendererContext? _rendererContext;
    private Renderer? _renderer;
    private CampathDofSettings _campathDofSettings = CampathDofSettings.Default;
    private TextRenderer? _textRenderer;
    private Framebuffer? _mainFramebuffer;
    private Framebuffer? _defaultFramebuffer;
    private GameFileLoader? _fileLoader;
    private Package? _mapPackage;
    private bool _rendererReady;
    private bool _mapLoadPending;
    private string? _pendingMapPath;
    private bool _showEntityModels = false;
    private bool _renderLogged;
    private bool _mapHasExternalReferences;
    private readonly Dictionary<int, LiveLinkModelNode> _liveLinkNodes = new();
    private readonly Dictionary<int, int> _liveLinkObserverSlotsByEntityId = new();
    private uint _lastLiveLinkFrameId = uint.MaxValue;
    private Cs2LiveLinkReceiver? _liveLinkReceiverCached;
    private bool _liveLinkEnabledCached;
    private bool _liveLinkItemIconsEnabledCached = true;
    private bool _liveLinkWeaponIconsEnabledCached = true;
    private bool _liveLinkGrenadeIconsEnabledCached = true;
    private bool _liveLinkProjectileIconsEnabledCached = true;
    private bool _liveLinkObjectiveIconsEnabledCached = true;
    private bool _liveLinkDeadPlayerIconsEnabledCached = true;
    private int _liveLinkPortCached = 31237;
    private readonly HashSet<int> _liveLinkLoggedMissingSkeletons = new();
    private readonly HashSet<string> _liveLinkLoggedModelFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LiveLinkIconBillboard> _liveLinkIconBillboards = new();
    private readonly object _liveLinkIconHitLock = new();
    private List<LiveLinkIconHit> _liveLinkIconHitCache = new();
    private readonly Dictionary<string, RenderTexture?> _liveLinkIconTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _liveLinkLoggedMissingIcons = new(StringComparer.OrdinalIgnoreCase);
    private int _liveLinkIconShaderProgram;
    private int _liveLinkIconSamplerLocation = -1;
    private int _liveLinkIconTintLocation = -1;
    private int _liveLinkIconVao;
    private int _liveLinkIconVbo;

    private int _pinShaderProgram;
    private int _pinMvpLocation = -1;
    private int _pinColorLocation = -1;
    private int _pinLightDirLocation = -1;
    private int _pinAmbientLocation = -1;
    private int _pinVao;
    private int _pinVbo;
    private int _pinVertexCount;
    private bool _pinsDirty;
    private readonly List<PinRenderData> _pins = new();
    private readonly List<PinDrawCall> _pinDraws = new();
    private readonly List<PinLabel> _pinLabels = new();
    private IReadOnlyList<ViewportPin>? _pinSource;
    private readonly object _pinLock = new();
    private readonly object _playerStatusLock = new();
    private Dictionary<int, ViewportPlayerStatus> _playerStatusesBySlot = new();
    private List<PinLabel> _labelHitCache = new();
    private readonly object _labelLock = new();

    private int _campathOverlayShaderProgram;
    private int _campathOverlayMvpLocation = -1;
    private int _campathOverlayVao;
    private int _campathOverlayVbo;
    private int _campathOverlayVertexCount;
    private bool _campathOverlayDirty;
    private CampathOverlayData? _campathOverlayData;
    private readonly object _campathOverlayLock = new();
    private const float CampathOverlayLineThicknessPx = 4.0f;
    private Vector3 _campathOverlayCameraPos;
    private Vector3 _campathOverlayCameraForward;
    private Vector3 _campathOverlayCameraUp;
    private float _campathOverlayCameraFov;
    private int _campathOverlayCameraHeight;

    private int _gizmoVao;
    private int _gizmoVbo;
    private int _gizmoVertexCount;
    private bool _gizmoDirty;
    private bool _gizmoVisible;
    private Vector3 _gizmoPosition;
    private Quaternion _gizmoRotation = Quaternion.Identity;
    private bool _gizmoUseLocalSpace;
    private float _gizmoLastScale;
    private Vector3 _gizmoLastPosition;
    private Quaternion _gizmoLastRotation = Quaternion.Identity;
    private bool _gizmoLastLocal;

    private bool _gizmoDragging;
    private GizmoMode _gizmoMode = GizmoMode.None;
    private Vector3 _gizmoDragAxis;
    private Vector3 _gizmoDragAxisLocal;
    private Vector3 _gizmoDragPlaneNormal;
    private Vector3 _gizmoDragStartPosition;
    private Quaternion _gizmoDragStartRotation = Quaternion.Identity;
    private Vector3 _gizmoDragStartVector;
    private float _gizmoDragStartAxisT;
    private GizmoMode _gizmoHover = GizmoMode.None;
    private readonly Vector3[] _pinConeUnit = CreateUnitCone();
    private readonly Vector3[] _pinConeNormals = CreateUnitConeNormals();
    private readonly Vector3[] _pinSphereUnit;
    private readonly Vector3[] _pinSphereNormals;
    private static readonly Vector3 PinLightDir = Vector3.Normalize(new Vector3(0.4f, 0.9f, 0.2f));
    private const float PinAmbientLight = 0.25f;

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
    private bool _targetOrbitActive;
    private Vector3 _targetOrbitTarget;
    private float _targetOrbitDistance;
    private float _targetOrbitYaw;
    private float _targetOrbitPitch;
    private readonly HashSet<Key> _keysDown = new();
    private Point _lastPointer;
    private bool _mouseButton4Down;
    private bool _mouseButton5Down;

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
    private FreecamTransform _freecamOutput;
    private FreecamConfig _freecamConfig = FreecamConfig.Default;
    private FreecamSettings? _freecamSettings;
    private bool _freecamPreviewRollOverrideActive;
    private float _freecamPreviewRollOverride;
    private bool _freecamWalkModeEnabled;
    private bool _freecamHandheldEnabled;
    private Vector3 _freecamWalkVelocity;
    private float _freecamWalkVerticalVelocity;
    private bool _freecamWalkOnGround;
    private bool _freecamWalkJumpLatch;
    private float _freecamWalkCrouchAmount;
    private float _freecamWalkBobPhase;
    private float _freecamWalkEffectTime;
    private float _freecamHandheldMotionNorm;
    private float _freecamWalkTargetPitch;
    private float _freecamWalkTargetYaw;
    private float _freecamWalkTargetFov;
    private bool _freecamLastGDown;
    private bool _freecamLastHDown;
    private bool _externalCameraActive;
    private Vector3 _externalCameraPosition;
    private Quaternion _externalCameraRotation = Quaternion.Identity;
    private float _externalCameraFov = 90f;
    private HlaeInputSender? _inputSender;

    private float _viewportFpsCapCached;
    private bool _postprocessEnabledCached = true;
    private bool _colorCorrectionEnabledCached = true;
    private bool _dynamicShadowsEnabledCached = true;
    private bool _wireframeEnabledCached;
    private bool _skipWaterEnabledCached;
    private bool _skipTranslucentEnabledCached;
    private bool _showFpsCached;
    private bool _showPlayerPinsCached = true;
    private int _shadowTextureSizeCached = 1024;
    private int _maxTextureSizeCached = 1024;
    private string _renderModeCached = "Default";

    private float _fpsAccumulator;
    private int _fpsSamples;
    private float _fpsValue;
    private readonly Stopwatch _frameLimiter = Stopwatch.StartNew();
    private long _lastLimiterTicks;
    private long _lastFrameTimestamp;
    private DispatcherTimer? _frameLimiterTimer;
    private bool _frameLimiterPending;
    private CancellationTokenSource? _renderCts;
    private Task? _renderLoop;
    private readonly ManualResetEventSlim _renderSignal = new(false);

    public VRFViewport()
    {
        Focusable = true;
        IsHitTestVisible = true;
        (_pinSphereUnit, _pinSphereNormals) = CreateUnitSphere(16, 32);
    }

    static VRFViewport()
    {
        MapPathProperty.Changed.AddClassHandler<VRFViewport>((sender, args) => sender.OnMapPathChanged(args));
        PinScaleProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnPinScaleChanged());
        PinOffsetZProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnPinOffsetChanged());
        ShowPlayerPinsProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnShowPlayerPinsChanged());
        ViewportFpsCapProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnViewportFpsCapChanged());
        PostprocessEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnPostprocessEnabledChanged());
        ColorCorrectionEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnColorCorrectionEnabledChanged());
        DynamicShadowsEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnDynamicShadowsEnabledChanged());
        WireframeEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnWireframeEnabledChanged());
        SkipWaterEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnSkipWaterEnabledChanged());
        SkipTranslucentEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnSkipTranslucentEnabledChanged());
        ShowFpsProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnShowFpsChanged());
        ShadowTextureSizeProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnShadowTextureSizeChanged());
        MaxTextureSizeProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnMaxTextureSizeChanged());
        RenderModeProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnRenderModeChanged());
        FreecamSettingsProperty.Changed.AddClassHandler<VRFViewport>((sender, args) => sender.OnFreecamSettingsChanged(args));
        InputSenderProperty.Changed.AddClassHandler<VRFViewport>((sender, args) => sender.OnInputSenderChanged(args));
        LiveLinkReceiverProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.ApplyLiveLinkReceiverSettings());
        LiveLinkEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.ApplyLiveLinkReceiverSettings());
        LiveLinkItemIconsEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnLiveLinkItemIconsEnabledChanged());
        LiveLinkWeaponIconsEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnLiveLinkIconFilterChanged());
        LiveLinkGrenadeIconsEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnLiveLinkIconFilterChanged());
        LiveLinkProjectileIconsEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnLiveLinkIconFilterChanged());
        LiveLinkObjectiveIconsEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnLiveLinkIconFilterChanged());
        LiveLinkDeadPlayerIconsEnabledProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.OnLiveLinkIconFilterChanged());
        LiveLinkPortProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.ApplyLiveLinkReceiverSettings());
        TargetOrbitResetRequestProperty.Changed.AddClassHandler<VRFViewport>((sender, _) => sender.ResetTargetOrbit());
    }

    public string? MapPath
    {
        get => GetValue(MapPathProperty);
        set => SetValue(MapPathProperty, value);
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

    public bool ShowPlayerPins
    {
        get => GetValue(ShowPlayerPinsProperty);
        set => SetValue(ShowPlayerPinsProperty, value);
    }

    public float ViewportMouseScale
    {
        get => GetValue(ViewportMouseScaleProperty);
        set => SetValue(ViewportMouseScaleProperty, value);
    }

    public float ViewportFpsCap
    {
        get => GetValue(ViewportFpsCapProperty);
        set => SetValue(ViewportFpsCapProperty, value);
    }

    public bool PostprocessEnabled
    {
        get => GetValue(PostprocessEnabledProperty);
        set => SetValue(PostprocessEnabledProperty, value);
    }

    public bool ColorCorrectionEnabled
    {
        get => GetValue(ColorCorrectionEnabledProperty);
        set => SetValue(ColorCorrectionEnabledProperty, value);
    }

    public bool DynamicShadowsEnabled
    {
        get => GetValue(DynamicShadowsEnabledProperty);
        set => SetValue(DynamicShadowsEnabledProperty, value);
    }

    public bool WireframeEnabled
    {
        get => GetValue(WireframeEnabledProperty);
        set => SetValue(WireframeEnabledProperty, value);
    }

    public bool SkipWaterEnabled
    {
        get => GetValue(SkipWaterEnabledProperty);
        set => SetValue(SkipWaterEnabledProperty, value);
    }

    public bool SkipTranslucentEnabled
    {
        get => GetValue(SkipTranslucentEnabledProperty);
        set => SetValue(SkipTranslucentEnabledProperty, value);
    }

    public bool ShowFps
    {
        get => GetValue(ShowFpsProperty);
        set => SetValue(ShowFpsProperty, value);
    }

    public int ShadowTextureSize
    {
        get => GetValue(ShadowTextureSizeProperty);
        set => SetValue(ShadowTextureSizeProperty, value);
    }

    public int MaxTextureSize
    {
        get => GetValue(MaxTextureSizeProperty);
        set => SetValue(MaxTextureSizeProperty, value);
    }

    public string RenderMode
    {
        get => GetValue(RenderModeProperty);
        set => SetValue(RenderModeProperty, value ?? "Default");
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

    public Cs2LiveLinkReceiver? LiveLinkReceiver
    {
        get => GetValue(LiveLinkReceiverProperty);
        set => SetValue(LiveLinkReceiverProperty, value);
    }

    public bool LiveLinkEnabled
    {
        get => GetValue(LiveLinkEnabledProperty);
        set => SetValue(LiveLinkEnabledProperty, value);
    }

    public bool LiveLinkItemIconsEnabled
    {
        get => GetValue(LiveLinkItemIconsEnabledProperty);
        set => SetValue(LiveLinkItemIconsEnabledProperty, value);
    }

    public bool LiveLinkWeaponIconsEnabled
    {
        get => GetValue(LiveLinkWeaponIconsEnabledProperty);
        set => SetValue(LiveLinkWeaponIconsEnabledProperty, value);
    }

    public bool LiveLinkGrenadeIconsEnabled
    {
        get => GetValue(LiveLinkGrenadeIconsEnabledProperty);
        set => SetValue(LiveLinkGrenadeIconsEnabledProperty, value);
    }

    public bool LiveLinkProjectileIconsEnabled
    {
        get => GetValue(LiveLinkProjectileIconsEnabledProperty);
        set => SetValue(LiveLinkProjectileIconsEnabledProperty, value);
    }

    public bool LiveLinkObjectiveIconsEnabled
    {
        get => GetValue(LiveLinkObjectiveIconsEnabledProperty);
        set => SetValue(LiveLinkObjectiveIconsEnabledProperty, value);
    }

    public bool LiveLinkDeadPlayerIconsEnabled
    {
        get => GetValue(LiveLinkDeadPlayerIconsEnabledProperty);
        set => SetValue(LiveLinkDeadPlayerIconsEnabledProperty, value);
    }

    public int LiveLinkPort
    {
        get => GetValue(LiveLinkPortProperty);
        set => SetValue(LiveLinkPortProperty, value);
    }

    public int TargetOrbitResetRequest
    {
        get => GetValue(TargetOrbitResetRequestProperty);
        set => SetValue(TargetOrbitResetRequestProperty, value);
    }

    public bool IsFreecamActive => _freecamActive;
    public bool IsFreecamInputEnabled => _freecamInputEnabled;

    public event Action<double>? FrameTick;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        _hwnd = CreateChildWindow(parent.Handle);
        if (_hwnd == IntPtr.Zero)
        {
            return base.CreateNativeControlCore(parent);
        }

        RegisterHostWindow(_hwnd, this);
        UpdateChildWindowSize();
        InitializeAfterNativeCreated();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopRenderLoop();
        DisposeRenderer();
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
        _viewportFpsCapCached = ViewportFpsCap;
        _postprocessEnabledCached = PostprocessEnabled;
        _colorCorrectionEnabledCached = ColorCorrectionEnabled;
        _dynamicShadowsEnabledCached = DynamicShadowsEnabled;
        _wireframeEnabledCached = WireframeEnabled;
        _skipWaterEnabledCached = SkipWaterEnabled;
        _skipTranslucentEnabledCached = SkipTranslucentEnabled;
        _showFpsCached = ShowFps;
        _showPlayerPinsCached = ShowPlayerPins;
        _shadowTextureSizeCached = ShadowTextureSize;
        _maxTextureSizeCached = MaxTextureSize;
        _renderModeCached = string.IsNullOrWhiteSpace(RenderMode) ? "Default" : RenderMode;
        _liveLinkReceiverCached = LiveLinkReceiver;
        _liveLinkEnabledCached = LiveLinkEnabled;
        _liveLinkItemIconsEnabledCached = LiveLinkItemIconsEnabled;
        _liveLinkWeaponIconsEnabledCached = LiveLinkWeaponIconsEnabled;
        _liveLinkGrenadeIconsEnabledCached = LiveLinkGrenadeIconsEnabled;
        _liveLinkProjectileIconsEnabledCached = LiveLinkProjectileIconsEnabled;
        _liveLinkObjectiveIconsEnabledCached = LiveLinkObjectiveIconsEnabled;
        _liveLinkDeadPlayerIconsEnabledCached = LiveLinkDeadPlayerIconsEnabled;
        _liveLinkPortCached = LiveLinkPort;
        ApplyLiveLinkReceiverSettings();
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
        _keysDown.Clear();
        DisableFreecam();
        _frameLimiterTimer?.Stop();
        _frameLimiterPending = false;
        StopRenderLoop();
        DisposeRenderer();
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
        RequestNextFrame();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keysDown.Remove(e.Key);
        RequestNextFrame();
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

    private void HandlePointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed || updateKind == PointerUpdateKind.MiddleButtonPressed;
        var rightPressed = point.Properties.IsRightButtonPressed || updateKind == PointerUpdateKind.RightButtonPressed;
        var leftPressed = point.Properties.IsLeftButtonPressed || updateKind == PointerUpdateKind.LeftButtonPressed;
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        if (leftPressed && TryBeginGizmoDrag(point.Position, e.KeyModifiers))
        {
            Focus();
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (leftPressed && TryHandlePinClick(point.Position))
        {
            Focus();
            e.Handled = true;
            return;
        }

        if (leftPressed && TryHandleLiveLinkIconClick(point.Position))
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

        BeginOrbitDrag(
            point.Position,
            e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift),
            e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control));
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    private void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed || updateKind == PointerUpdateKind.MiddleButtonPressed;
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        if (_gizmoDragging)
        {
            _gizmoDragging = false;
            _gizmoMode = GizmoMode.None;
            CampathGizmoDragEnded?.Invoke();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        var rightReleased = updateKind == PointerUpdateKind.RightButtonReleased;
        if (_freecamActive && rightReleased)
        {
            EndFreecamInput();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_dragging)
            return;

        var released = updateKind == PointerUpdateKind.MiddleButtonReleased || !middlePressed;
        if (released)
        {
            EndOrbitDrag();
            e.Pointer.Capture(null);
        }
    }

    private void HandlePointerMoved(PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        if (_gizmoDragging)
        {
            UpdateGizmoDrag(point.Position, e.KeyModifiers);
            e.Handled = true;
            return;
        }

        UpdateGizmoHover(point.Position);

        if (_freecamActive && _freecamInputEnabled)
        {
            if (_freecamIgnoreNextDelta)
            {
                _freecamIgnoreNextDelta = false;
                CenterFreecamCursor();
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
            RequestNextFrame();
            e.Handled = true;
            return;
        }

        if (!_dragging)
            return;

        var pos = point.Position;
        var delta = pos - _lastPointer;

        if (_panning)
        {
            Pan((float)delta.X, (float)delta.Y);
            _lastPointer = pos;
        }
        else
        {
            ApplyOrbitPointerMove(pos);
        }

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
            RequestNextFrame();
            e.Handled = true;
            return;
        }
        if (_freecamActive)
            return;

        var zoomFactor = MathF.Pow(1.1f, (float)-e.Delta.Y);
        ZoomOrbitDistance(zoomFactor);
        RequestNextFrame();
        e.Handled = true;
    }

    private void HandleNativeMouse(uint msg, int x, int y, int xButton)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleNativeMouse(msg, x, y, xButton));
            return;
        }

        const uint WM_MOUSEMOVE = 0x0200;
        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_MBUTTONDOWN = 0x0207;
        const uint WM_MBUTTONUP = 0x0208;
        const uint WM_XBUTTONDOWN = 0x020B;
        const uint WM_XBUTTONUP = 0x020C;

        var position = ClientToLocalPoint(x, y);
        var updateKind = msg switch
        {
            WM_LBUTTONDOWN => PointerUpdateKind.LeftButtonPressed,
            WM_LBUTTONUP => PointerUpdateKind.LeftButtonReleased,
            WM_RBUTTONDOWN => PointerUpdateKind.RightButtonPressed,
            WM_RBUTTONUP => PointerUpdateKind.RightButtonReleased,
            WM_MBUTTONDOWN => PointerUpdateKind.MiddleButtonPressed,
            WM_MBUTTONUP => PointerUpdateKind.MiddleButtonReleased,
            WM_XBUTTONDOWN => xButton == 1 ? PointerUpdateKind.XButton1Pressed : PointerUpdateKind.XButton2Pressed,
            WM_XBUTTONUP => xButton == 1 ? PointerUpdateKind.XButton1Released : PointerUpdateKind.XButton2Released,
            _ => PointerUpdateKind.Other
        };

        if (msg == WM_MOUSEMOVE)
        {
            HandleNativePointerMoved(position);
            return;
        }

        if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN)
        {
            HandleNativePointerPressed(position, updateKind, IsShiftKeyDown());
        }
        else if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP || msg == WM_MBUTTONUP || msg == WM_XBUTTONUP)
        {
            HandleNativePointerReleased(position, updateKind);
        }
    }

    private Point ClientToLocalPoint(int x, int y)
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return new Point(x / scale, y / scale);
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private static bool IsShiftKeyDown()
    {
        const int VK_SHIFT = 0x10;
        return (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    }

    private static bool IsCtrlKeyDown()
    {
        const int VK_CONTROL = 0x11;
        return (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    }

    private static AvaloniaKeyModifiers BuildModifiers(bool shiftDown, bool ctrlDown)
    {
        var modifiers = AvaloniaKeyModifiers.None;
        if (shiftDown)
            modifiers |= AvaloniaKeyModifiers.Shift;
        if (ctrlDown)
            modifiers |= AvaloniaKeyModifiers.Control;
        return modifiers;
    }

    private void HandleNativePointerPressed(Point position, PointerUpdateKind updateKind, bool shiftDown)
    {
        var middlePressed = updateKind == PointerUpdateKind.MiddleButtonPressed;
        var rightPressed = updateKind == PointerUpdateKind.RightButtonPressed;
        var leftPressed = updateKind == PointerUpdateKind.LeftButtonPressed;
        var ctrlDown = IsCtrlKeyDown();
        var modifiers = BuildModifiers(shiftDown, ctrlDown);

        if (updateKind == PointerUpdateKind.XButton1Pressed)
            _mouseButton4Down = true;
        if (updateKind == PointerUpdateKind.XButton2Pressed)
            _mouseButton5Down = true;

        if (leftPressed && TryBeginGizmoDrag(position, modifiers))
        {
            Focus();
            return;
        }

        if (leftPressed && TryHandlePinClick(position))
        {
            Focus();
            return;
        }

        if (leftPressed && TryHandleLiveLinkIconClick(position))
        {
            Focus();
            return;
        }

        if (rightPressed)
        {
            BeginFreecam(position);
            Focus();
            return;
        }

        if (!middlePressed)
            return;

        if (_freecamActive)
            DisableFreecam();

        BeginOrbitDrag(position, shiftDown, ctrlDown);
        Focus();
    }

    private void HandleNativePointerReleased(Point position, PointerUpdateKind updateKind)
    {
        var middlePressed = updateKind == PointerUpdateKind.MiddleButtonPressed;
        if (updateKind == PointerUpdateKind.XButton1Released)
            _mouseButton4Down = false;
        if (updateKind == PointerUpdateKind.XButton2Released)
            _mouseButton5Down = false;

        if (_gizmoDragging)
        {
            _gizmoDragging = false;
            _gizmoMode = GizmoMode.None;
            CampathGizmoDragEnded?.Invoke();
            return;
        }

        var rightReleased = updateKind == PointerUpdateKind.RightButtonReleased;
        if (_freecamActive && rightReleased)
        {
            EndFreecamInput();
            return;
        }

        if (!_dragging)
            return;

        var released = updateKind == PointerUpdateKind.MiddleButtonReleased || !middlePressed;
        if (released)
        {
            EndOrbitDrag();
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
                RequestNextFrame();
                return;
            }

            var scale = MathF.Max(0.01f, ViewportMouseScale);
            var dx = (float)(position.X - _freecamCenterLocal.X) * scale;
            var dy = (float)(position.Y - _freecamCenterLocal.Y) * scale;
            if (dx != 0 || dy != 0)
                _freecamMouseDelta += new Vector2(dx, dy);
            CenterFreecamCursor();
            RequestNextFrame();
            return;
        }

        if (_gizmoDragging)
        {
            var modifiers = BuildModifiers(IsShiftKeyDown(), IsCtrlKeyDown());
            UpdateGizmoDrag(position, modifiers);
            return;
        }

        UpdateGizmoHover(position);

        if (!_dragging)
            return;

        var delta = position - _lastPointer;

        if (_panning)
        {
            Pan((float)delta.X, (float)delta.Y);
            _lastPointer = position;
        }
        else
        {
            ApplyOrbitPointerMove(position);
        }

        RequestNextFrame();
    }

    public void ForwardPointerPressed(PointerPressedEventArgs e)
    {
        OnPointerPressed(e);
    }

    public void ForwardPointerReleased(PointerReleasedEventArgs e)
    {
        OnPointerReleased(e);
    }

    public void ForwardPointerMoved(PointerEventArgs e)
    {
        OnPointerMoved(e);
    }

    public void ForwardPointerWheel(PointerWheelEventArgs e)
    {
        OnPointerWheelChanged(e);
    }

    public void ForwardKeyDown(KeyEventArgs e)
    {
        OnKeyDown(e);
    }

    public void ForwardKeyUp(KeyEventArgs e)
    {
        OnKeyUp(e);
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
            SpeedScalar = _freecamSpeedScalar,
            WalkModeEnabled = _freecamWalkModeEnabled,
            HandheldEffectsEnabled = _freecamHandheldEnabled,
            WalkVelocity = _freecamWalkVelocity,
            WalkVerticalVelocity = _freecamWalkVerticalVelocity,
            WalkOnGround = _freecamWalkOnGround,
            WalkCrouchAmount = _freecamWalkCrouchAmount,
            WalkBobPhase = _freecamWalkBobPhase,
            WalkEffectTime = _freecamWalkEffectTime,
            WalkTargetPitch = _freecamWalkTargetPitch,
            WalkTargetYaw = _freecamWalkTargetYaw,
            WalkTargetFov = _freecamWalkTargetFov
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

    public void SetDepthOfField(CampathDofSettings settings)
    {
        _campathDofSettings = settings;
        ApplyDepthOfField();
        RequestNextFrame();
    }

    private void ApplyDepthOfField()
    {
        if (_renderer == null)
            return;

        var dof = _renderer.Postprocess.DOF;
        dof.Enabled = _campathDofSettings.Enabled;
        // CS2 r_dof_override distances are measured from the camera plane. VRF's
        // standalone DOF defaults to a 100-unit focal-plane offset, which would make
        // near crisp 0 blur geometry between the camera and that focal plane.
        dof.FocalDistance = 0.0f;
        dof.NearBlurry = (float)Math.Min(_campathDofSettings.NearBlurry, _campathDofSettings.NearCrisp - 0.001);
        dof.NearCrisp = (float)_campathDofSettings.NearCrisp;
        dof.FarCrisp = (float)_campathDofSettings.FarCrisp;
        dof.FarBlurry = (float)Math.Max(_campathDofSettings.FarBlurry, _campathDofSettings.FarCrisp + 0.001);
        dof.MaxBlurSize = (float)Math.Max(0.0, _campathDofSettings.MaxBlurSize);
        dof.RadScale = (float)Math.Max(0.0, _campathDofSettings.RadiusScale);
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
        _freecamOutput = _freecamTransform;
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

    public void ResetTargetOrbit()
    {
        _targetOrbitActive = false;
        RequestNextFrame();
    }

    private void Orbit(float deltaX, float deltaY)
    {
        const float rotateSpeed = 0.01f;
        if (_targetOrbitActive)
        {
            _targetOrbitYaw -= deltaX * rotateSpeed;
            _targetOrbitPitch += deltaY * rotateSpeed;
            _targetOrbitPitch = Math.Clamp(_targetOrbitPitch, -1.55f, 1.55f);
            return;
        }

        _yaw -= deltaX * rotateSpeed;
        _pitch += deltaY * rotateSpeed;
        _pitch = Math.Clamp(_pitch, -1.55f, 1.55f);
    }

    private void BeginOrbitDrag(Point position, bool pan, bool targetOrbit)
    {
        _dragging = true;
        _panning = pan;
        if (targetOrbit)
        {
            TryBeginTargetOrbit(position);
        }
        _lastPointer = position;
        CaptureOrbitMouse();
    }

    private void EndOrbitDrag()
    {
        _dragging = false;
        _panning = false;
        ReleaseOrbitMouse();
    }

    private Vector3 GetCameraPosition()
    {
        var pitch = GetActiveOrbitPitch();
        var yaw = GetActiveOrbitYaw();
        var cosPitch = MathF.Cos(pitch);
        var sinPitch = MathF.Sin(pitch);
        var cosYaw = MathF.Cos(yaw);
        var sinYaw = MathF.Sin(yaw);

        var direction = new Vector3(cosPitch * cosYaw, cosPitch * sinYaw, sinPitch);
        return GetActiveOrbitTarget() + direction * GetActiveOrbitDistance();
    }

    private Vector3 GetActiveOrbitTarget()
    {
        return _targetOrbitActive ? _targetOrbitTarget : _target;
    }

    private float GetActiveOrbitDistance()
    {
        return _targetOrbitActive ? _targetOrbitDistance : _distance;
    }

    private float GetActiveOrbitYaw()
    {
        return _targetOrbitActive ? _targetOrbitYaw : _yaw;
    }

    private float GetActiveOrbitPitch()
    {
        return _targetOrbitActive ? _targetOrbitPitch : _pitch;
    }

    private void ZoomOrbitDistance(float zoomFactor)
    {
        if (_targetOrbitActive)
        {
            _targetOrbitDistance = Math.Min(_targetOrbitDistance * zoomFactor, _maxDistance);
            if (_targetOrbitDistance < 0.0001f)
                _targetOrbitDistance = 0.0001f;
            return;
        }

        _distance = Math.Min(_distance * zoomFactor, _maxDistance);
        if (_distance < 0.0001f)
            _distance = 0.0001f;
    }

    private void Pan(float deltaX, float deltaY)
    {
        var cameraPos = GetCameraPosition();
        var target = GetActiveOrbitTarget();
        var distance = GetActiveOrbitDistance();
        var forward = Vector3.Normalize(target - cameraPos);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        var panScale = distance * 0.001f;
        var panOffset = (-right * deltaX + up * deltaY) * panScale;
        if (_targetOrbitActive)
            _targetOrbitTarget += panOffset;
        else
            _target += panOffset;
    }

    private bool TryBeginTargetOrbit(Point position)
    {
        if (_renderer?.Scene?.PhysicsWorld == null)
            return false;

        if (!TryGetRay(position, out var rayOrigin, out var rayDir))
            return false;

        var trace = _renderer.Scene.PhysicsWorld.TraceRay(rayOrigin, rayOrigin + rayDir * 10000f);
        if (!trace.Hit)
            return false;

        var cameraPos = GetCameraPosition();
        var target = trace.HitPosition;
        var fromTarget = cameraPos - target;
        var distance = fromTarget.Length();
        if (distance < 0.0001f)
            return false;

        var direction = fromTarget / distance;
        _targetOrbitYaw = MathF.Atan2(direction.Y, direction.X);
        _targetOrbitPitch = MathF.Asin(Math.Clamp(direction.Z, -1f, 1f));
        _targetOrbitPitch = Math.Clamp(_targetOrbitPitch, -1.55f, 1.55f);
        _targetOrbitTarget = target;
        _targetOrbitDistance = distance;
        _targetOrbitActive = true;
        return true;
    }

    private void BeginFreecam(Point start)
    {
        // Entering manual freecam control ends any evaluated-pose roll lock.
        // SetFreecamPose reapplies the override after calling this method, while a
        // real user-initiated BeginFreecam call leaves Q/E free to control roll.
        _freecamPreviewRollOverrideActive = false;

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
        RequestNextFrame();
    }

    private void EndFreecamInput()
    {
        _freecamInputEnabled = false;
        ClearFreecamInputState();
        UnlockFreecamCursor();
        RequestNextFrame();
    }

    private void DisableFreecam()
    {
        _freecamInputEnabled = false;
        _freecamActive = false;
        ClearFreecamInputState();
        UnlockFreecamCursor();
        RestoreOrbitState();
        RequestNextFrame();
    }

    private void InitializeFreecamFromOrbit()
    {
        var cameraPos = GetCameraPosition();
        var forward = Vector3.Normalize(GetActiveOrbitTarget() - cameraPos);
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
        _freecamOutput = _freecamTransform;
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
        _freecamOutput = _freecamSmoothed;
        _freecamRawQuat = _freecamTransform.Orientation;
        _freecamSmoothedQuat = _freecamSmoothed.Orientation;
        _freecamRotVelocity = Vector3.Zero;
        _freecamWalkModeEnabled = _freecamConfig.WalkModeDefaultEnabled;
        _freecamHandheldEnabled = _freecamConfig.HandheldDefaultEnabled;
        _freecamWalkVelocity = Vector3.Zero;
        _freecamWalkVerticalVelocity = 0f;
        _freecamWalkOnGround = false;
        _freecamWalkJumpLatch = false;
        _freecamWalkCrouchAmount = 0f;
        _freecamWalkBobPhase = 0f;
        _freecamWalkEffectTime = 0f;
        _freecamHandheldMotionNorm = 0f;
        _freecamWalkTargetPitch = _freecamTransform.Pitch;
        _freecamWalkTargetYaw = _freecamTransform.Yaw;
        _freecamWalkTargetFov = _freecamTransform.Fov;
        _freecamLastGDown = false;
        _freecamLastHDown = false;
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

    private void ApplyCameraForFrame(int width, int height)
    {
        if (_renderer == null || _rendererContext == null)
        {
            return;
        }

        if (_externalCameraActive)
        {
            var fovRad = GetSourceVerticalFovRadians(_externalCameraFov);
            _rendererContext.FieldOfView = RadToDeg(fovRad);
            _renderer.Camera.SetViewportSize(width, height);
            var forward = GetForwardFromQuat(_externalCameraRotation);
            var up = GetUpFromQuat(_externalCameraRotation);
            _renderer.Camera.SetLocationForwardUp(_externalCameraPosition, forward, up);
        }
        else if (_freecamActive)
        {
            var output = _freecamOutput;
            var fovRad = GetSourceVerticalFovRadians(output.Fov);
            _rendererContext.FieldOfView = RadToDeg(fovRad);
            _renderer.Camera.SetViewportSize(width, height);
            var forward = GetForwardFromQuat(output.Orientation);
            var up = GetUpFromQuat(output.Orientation);
            _renderer.Camera.SetLocationForwardUp(output.Position, forward, up);
        }
        else
        {
            _rendererContext.FieldOfView = 60f;
            _renderer.Camera.SetViewportSize(width, height);
            var target = GetActiveOrbitTarget();
            var cameraPos = GetCameraPosition();
            var forward = Vector3.Normalize(target - cameraPos);
            GetYawPitchFromForward(forward, out var yawDeg, out var pitchDeg);
            var rollDeg = ComputeRollForUp(pitchDeg, yawDeg, Vector3.UnitZ);
            _renderer.Camera.SetLocationPitchYawRoll(
                cameraPos,
                DegToRad(pitchDeg),
                DegToRad(yawDeg),
                DegToRad(rollDeg));
        }
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
            UpdateWalkModeToggles();
            UpdateFreecamSpeed(deltaTime, wheel);
        }

        if (_freecamWalkModeEnabled)
        {
            UpdateWalkLook(deltaTime, wheel);
            UpdateWalkMovement(deltaTime);

            _freecamTransform.Roll = 0f;
            _freecamTransform.Orientation = BuildQuat(_freecamTransform);
            _freecamRawQuat = _freecamTransform.Orientation;
            _freecamSmoothed = _freecamTransform;
            _freecamSmoothed.Orientation = _freecamTransform.Orientation;
            _freecamSmoothedQuat = _freecamSmoothed.Orientation;
            _freecamRotVelocity = Vector3.Zero;

            ApplyWalkHandheldEffects(deltaTime);
            return;
        }

        if (_freecamInputEnabled)
        {
            UpdateFreecamMouseLook(deltaTime);
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

        ApplyWalkHandheldEffects(deltaTime);
    }

    private void UpdateWalkModeToggles()
    {
        var gDown = IsKeyDown(Key.G);
        if (gDown && !_freecamLastGDown)
        {
            _freecamWalkModeEnabled = !_freecamWalkModeEnabled;
            _freecamWalkVelocity = Vector3.Zero;
            _freecamWalkVerticalVelocity = 0f;
            _freecamWalkOnGround = false;
            _freecamWalkJumpLatch = false;
            _freecamWalkBobPhase = 0f;
            _freecamWalkEffectTime = 0f;
            _freecamWalkTargetPitch = _freecamTransform.Pitch;
            _freecamWalkTargetYaw = _freecamTransform.Yaw;
            _freecamWalkTargetFov = _freecamTransform.Fov;
        }
        _freecamLastGDown = gDown;

        var hDown = IsKeyDown(Key.H);
        if (!_freecamWalkModeEnabled && hDown && !_freecamLastHDown)
        {
            _freecamHandheldEnabled = !_freecamHandheldEnabled;
        }
        _freecamLastHDown = hDown;
    }

    private void UpdateWalkLook(float deltaTime, float wheelDelta)
    {
        if (deltaTime <= 0f)
            return;

        var deltaYaw = _freecamInputEnabled ? -_freecamMouseDelta.X * _freecamConfig.MouseSensitivity : 0f;
        var deltaPitch = _freecamInputEnabled ? _freecamMouseDelta.Y * _freecamConfig.MouseSensitivity : 0f;
        _freecamMouseDelta = Vector2.Zero;

        _freecamWalkTargetYaw += deltaYaw;
        _freecamWalkTargetPitch = Clamp(_freecamWalkTargetPitch + deltaPitch, -89f, 89f);
        _freecamMouseVelocityX = deltaYaw / deltaTime;
        _freecamMouseVelocityY = deltaPitch / deltaTime;

        var lookHalf = MathF.Max(_freecamConfig.WalkLookHalfTime, 1e-4f);
        _freecamTransform.Pitch = Clamp(
            CalcDeltaExpSmooth(deltaTime / lookHalf, _freecamWalkTargetPitch - _freecamTransform.Pitch) + _freecamTransform.Pitch,
            -89f,
            89f);
        _freecamTransform.Yaw = CalcDeltaExpSmooth(deltaTime / lookHalf, _freecamWalkTargetYaw - _freecamTransform.Yaw) + _freecamTransform.Yaw;

        if (_freecamInputEnabled && Math.Abs(wheelDelta) > float.Epsilon && !IsAltDown())
        {
            _freecamWalkTargetFov += wheelDelta * _freecamConfig.FovStep;
            _freecamWalkTargetFov = Clamp(_freecamWalkTargetFov, _freecamConfig.FovMin, _freecamConfig.FovMax);
        }

        var fovHalf = MathF.Max(_freecamConfig.WalkFovHalfTime, 1e-4f);
        _freecamTransform.Fov = CalcDeltaExpSmooth(deltaTime / fovHalf, _freecamWalkTargetFov - _freecamTransform.Fov) + _freecamTransform.Fov;
    }

    private void UpdateWalkMovement(float deltaTime)
    {
        var inputActive = _freecamInputEnabled;
        var analogLX = 0f;
        var analogLY = 0f;
        var analogRY = 0f;
        var analogRX = 0f;
        var useAnalog = inputActive && TryGetAnalogState(out analogLX, out analogLY, out analogRY, out analogRX);
        var moveX = useAnalog ? Clamp(analogLX, -1f, 1f) : inputActive ? (IsKeyDown(Key.D) ? 1f : 0f) - (IsKeyDown(Key.A) ? 1f : 0f) : 0f;
        var moveY = useAnalog ? Clamp(analogLY, -1f, 1f) : inputActive ? (IsKeyDown(Key.W) ? 1f : 0f) - (IsKeyDown(Key.S) ? 1f : 0f) : 0f;
        var crouchInput = useAnalog ? Clamp(-analogRY, 0f, 1f) : 0f;
        var sprintInput = useAnalog ? Clamp(analogRX, 0f, 1f) : 0f;

        if (sprintInput <= 0f && inputActive && IsShiftDown())
            sprintInput = 1f;

        var targetCrouch = useAnalog
            ? (crouchInput > 0f ? crouchInput : (inputActive && IsCtrlDown() ? 1f : 0f))
            : (inputActive && IsCtrlDown() ? 1f : 0f);
        var crouchBlend = 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / 0.08f);
        _freecamWalkCrouchAmount = Lerp(_freecamWalkCrouchAmount, Clamp(targetCrouch, 0f, 1f), crouchBlend);

        var moveMagnitude = MathF.Sqrt(moveX * moveX + moveY * moveY);
        if (moveMagnitude > 1f)
        {
            moveX /= moveMagnitude;
            moveY /= moveMagnitude;
        }

        var yawRad = DegToRad(_freecamTransform.Yaw);
        var forward = new Vector3(MathF.Cos(yawRad), MathF.Sin(yawRad), 0f);
        var left = new Vector3(-MathF.Sin(yawRad), MathF.Cos(yawRad), 0f);

        var crouchScale = 1f + _freecamWalkCrouchAmount * (_freecamConfig.WalkCrouchSpeedMultiplier - 1f);
        var runScale = 1f + sprintInput * (_freecamConfig.WalkRunMultiplier - 1f);
        var walkSpeed = _freecamConfig.WalkMoveSpeed * runScale * crouchScale;

        var desiredVelocity = (forward * moveY) + (left * -moveX);
        desiredVelocity *= walkSpeed;

        var velocityDelta = desiredVelocity - _freecamWalkVelocity;
        var desiredPlanar = desiredVelocity.Length();
        var currentPlanar = _freecamWalkVelocity.Length();
        var acceleration = desiredPlanar > currentPlanar
            ? _freecamConfig.WalkMoveAcceleration
            : _freecamConfig.WalkMoveDeceleration;
        var maxDelta = MathF.Max(0f, acceleration) * deltaTime;
        var deltaLength = velocityDelta.Length();
        if (deltaLength > maxDelta && deltaLength > 0.0001f)
        {
            _freecamWalkVelocity += velocityDelta * (maxDelta / deltaLength);
        }
        else
        {
            _freecamWalkVelocity = desiredVelocity;
        }

        if (!TryGetWalkPhysics(out var physics))
        {
            var verticalVelocity = 0f;
            if (inputActive && IsKeyDown(Key.Space))
                verticalVelocity += _freecamConfig.VerticalSpeed;
            if (inputActive && IsCtrlDown())
                verticalVelocity -= _freecamConfig.VerticalSpeed;

            _freecamTransform.Position += _freecamWalkVelocity * deltaTime;
            _freecamTransform.Position += Vector3.UnitZ * (verticalVelocity * deltaTime);
            _freecamWalkVerticalVelocity = 0f;
            _freecamWalkOnGround = false;
            return;
        }

        var physicsHalfHeight = _freecamConfig.WalkHullHalfHeight;
        var physicsCameraHeight = MathF.Max(0f, _freecamConfig.WalkHullHalfHeight - _freecamConfig.WalkCameraTopInset);
        var hullPosition = _freecamTransform.Position - new Vector3(0f, 0f, physicsCameraHeight);

        if (TryWalkHorizontalMove(physics, hullPosition, new Vector3(_freecamWalkVelocity.X * deltaTime, 0f, 0f), _freecamWalkOnGround, physicsHalfHeight, out var movedX))
            hullPosition = movedX;
        if (TryWalkHorizontalMove(physics, hullPosition, new Vector3(0f, _freecamWalkVelocity.Y * deltaTime, 0f), _freecamWalkOnGround, physicsHalfHeight, out var movedY))
            hullPosition = movedY;

        if (_freecamWalkVerticalVelocity <= 0f
            && ProbeWalkGround(physics, hullPosition, _freecamConfig.WalkGroundProbe, physicsHalfHeight, out var groundTrace)
            && groundTrace.Hit
            && groundTrace.HitNormal.Z >= _freecamConfig.WalkMinGroundNormalZ)
        {
            _freecamWalkOnGround = true;
            hullPosition = ResolveWalkTracePosition(groundTrace, hullPosition);
            if (_freecamWalkVerticalVelocity < 0f)
                _freecamWalkVerticalVelocity = 0f;
        }
        else
        {
            _freecamWalkOnGround = false;
        }

        var jumpPressed = inputActive && IsKeyDown(Key.Space);
        if (jumpPressed && !_freecamWalkJumpLatch && _freecamWalkOnGround)
        {
            _freecamWalkVerticalVelocity = _freecamConfig.WalkJumpSpeed;
            _freecamWalkOnGround = false;
        }
        _freecamWalkJumpLatch = jumpPressed;

        _freecamWalkVerticalVelocity -= _freecamConfig.WalkGravity * deltaTime;

        if (TryTraceWalkHullMove(physics, hullPosition, hullPosition + new Vector3(0f, 0f, _freecamWalkVerticalVelocity * deltaTime), physicsHalfHeight, out var verticalTrace))
        {
            hullPosition = ResolveWalkTracePosition(verticalTrace, hullPosition + new Vector3(0f, 0f, _freecamWalkVerticalVelocity * deltaTime));
            if (verticalTrace.Hit)
            {
                if (_freecamWalkVerticalVelocity < 0f)
                    _freecamWalkOnGround = true;
                _freecamWalkVerticalVelocity = 0f;
            }
        }

        _freecamTransform.Position = hullPosition + new Vector3(0f, 0f, physicsCameraHeight);
    }

    private void ApplyWalkHandheldEffects(float deltaTime)
    {
        _freecamOutput = _freecamSmoothed;

        if (_freecamWalkModeEnabled)
        {
            var physicsCameraHeight = MathF.Max(0f, _freecamConfig.WalkHullHalfHeight - _freecamConfig.WalkCameraTopInset);
            var visualCameraHeight = GetWalkCameraHeight(_freecamWalkCrouchAmount);
            _freecamOutput.Position += new Vector3(0f, 0f, visualCameraHeight - physicsCameraHeight);
        }

        var applyHandheld = _freecamWalkModeEnabled || _freecamHandheldEnabled;
        if (!applyHandheld)
            return;

        _freecamWalkEffectTime += deltaTime;

        var speedNorm = 1f;
        if (_freecamWalkModeEnabled)
        {
            var speed = _freecamWalkVelocity.Length();
            var baseSpeed = _freecamConfig.WalkMoveSpeed * MathF.Max(1f, _freecamConfig.WalkRunMultiplier);
            var targetSpeedNorm = Clamp(speed / baseSpeed, 0f, 1f);
            var motionBlend = 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / 0.06f);
            _freecamHandheldMotionNorm = Lerp(_freecamHandheldMotionNorm, targetSpeedNorm, motionBlend);
            speedNorm = _freecamHandheldMotionNorm;

            if (_freecamWalkOnGround && speed > 1f)
            {
                _freecamWalkBobPhase += deltaTime * _freecamConfig.WalkBobFrequency * (0.5f + 1.5f * speedNorm) * 2f * MathF.PI;
            }
        }
        else
        {
            _freecamHandheldMotionNorm = 1f;
        }

        var bobSin = MathF.Sin(_freecamWalkBobPhase);
        var bobCos = MathF.Cos(_freecamWalkBobPhase);
        var bobZ = _freecamWalkModeEnabled ? bobSin * _freecamConfig.WalkBobAmplitudeZ * speedNorm : 0f;
        var bobSide = _freecamWalkModeEnabled ? bobCos * _freecamConfig.WalkBobAmplitudeSide * speedNorm : 0f;
        var bobRoll = _freecamWalkModeEnabled ? bobSin * _freecamConfig.WalkBobAmplitudeRoll * speedNorm : 0f;

        var shakeT = _freecamWalkEffectTime * _freecamConfig.HandheldShakeFrequency;
        var shakeBaseA = MathF.Sin(shakeT * 9.73f) + 0.6f * MathF.Sin(shakeT * 17.11f + 1.31f);
        var shakeBaseB = MathF.Sin(shakeT * 11.41f + 0.77f) + 0.5f * MathF.Sin(shakeT * 21.37f + 2.03f);
        var shakeGain = _freecamWalkModeEnabled
            ? Clamp(0.2f + 0.8f * speedNorm, 0f, 1.5f)
            : 0.2f;

        var shakeSide = shakeBaseA * _freecamConfig.HandheldShakePosAmplitude * shakeGain;
        var shakeUp = shakeBaseB * _freecamConfig.HandheldShakePosAmplitude * 0.75f * shakeGain;
        var shakePitch = shakeBaseB * _freecamConfig.HandheldShakeAngAmplitude * 0.6f * shakeGain;
        var shakeYaw = shakeBaseA * _freecamConfig.HandheldShakeAngAmplitude * 0.4f * shakeGain;
        var shakeRoll = shakeBaseA * _freecamConfig.HandheldShakeAngAmplitude * shakeGain;

        var driftT = _freecamWalkEffectTime * _freecamConfig.HandheldDriftFrequency * 2f * MathF.PI;
        var driftSide = (MathF.Sin(driftT + 0.4f) + 0.4f * MathF.Sin(driftT * 0.47f + 1.1f)) * _freecamConfig.HandheldDriftPosAmplitude;
        var driftUp = MathF.Sin(driftT * 0.63f + 2.3f) * _freecamConfig.HandheldDriftPosAmplitude * 0.7f;
        var driftPitch = MathF.Sin(driftT * 0.71f + 0.9f) * _freecamConfig.HandheldDriftAngAmplitude * 0.7f;
        var driftYaw = MathF.Sin(driftT * 0.53f + 1.7f) * _freecamConfig.HandheldDriftAngAmplitude * 0.6f;
        var driftRoll = MathF.Sin(driftT * 0.81f + 0.2f) * _freecamConfig.HandheldDriftAngAmplitude * 0.8f;

        var right = GetRightVector(_freecamOutput.Yaw);
        _freecamOutput.Position += right * -(bobSide + shakeSide + driftSide);
        _freecamOutput.Position += new Vector3(0f, 0f, bobZ + shakeUp + driftUp);
        _freecamOutput.Pitch += shakePitch + driftPitch;
        _freecamOutput.Yaw += shakeYaw + driftYaw;
        _freecamOutput.Roll += bobRoll + shakeRoll + driftRoll;
        _freecamOutput.Orientation = BuildQuat(_freecamOutput);
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
            // Match game freecam: use yaw-only right vector for lean to avoid roll feedback.
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
            _freecamLastSmoothedPosition = _freecamConfig.SmoothEnabled ? _freecamSmoothed.Position : _freecamTransform.Position;
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

    private bool TryBeginGizmoDrag(Point screenPos, AvaloniaKeyModifiers modifiers)
    {
        if (!_gizmoVisible || _renderer == null || _renderWidth <= 0 || _renderHeight <= 0)
            return false;

        if (!TryGetRay(screenPos, out var rayOrigin, out var rayDir))
            return false;

        var (axisX, axisY, axisZ) = GetGizmoAxes();
        var scale = Math.Clamp(Vector3.Distance(_renderer.Camera.Location, _gizmoPosition) * 0.12f, 24f, 120f);
        var axisLength = scale;
        var axisThreshold = scale * 0.08f;
        var ringRadius = scale * 0.75f;
        var ringThreshold = scale * 0.08f;

        GizmoMode bestMode = GizmoMode.None;
        var bestDistance = float.MaxValue;
        var debug = string.Empty;

        if (TryPickTranslationScreen(screenPos, axisX, axisLength, 10f, out var dx) && dx < bestDistance)
        {
            bestDistance = dx;
            bestMode = GizmoMode.TranslateX;
        }
        if (TryPickTranslationScreen(screenPos, axisY, axisLength, 10f, out var dy) && dy < bestDistance)
        {
            bestDistance = dy;
            bestMode = GizmoMode.TranslateY;
        }
        if (TryPickTranslationScreen(screenPos, axisZ, axisLength, 10f, out var dz) && dz < bestDistance)
        {
            bestDistance = dz;
            bestMode = GizmoMode.TranslateZ;
        }

        if (bestMode == GizmoMode.None)
        {
            if (TryPickRotationScreen(screenPos, axisX, ringRadius, 10f, out var rx) && rx < bestDistance)
            {
                bestDistance = rx;
                bestMode = GizmoMode.RotateX;
            }
            if (TryPickRotationScreen(screenPos, axisY, ringRadius, 10f, out var ry) && ry < bestDistance)
            {
                bestDistance = ry;
                bestMode = GizmoMode.RotateY;
            }
            if (TryPickRotationScreen(screenPos, axisZ, ringRadius, 10f, out var rz) && rz < bestDistance)
            {
                bestDistance = rz;
                bestMode = GizmoMode.RotateZ;
            }
        }

        if (bestMode == GizmoMode.None)
            return false;

        _gizmoDragging = true;
        _gizmoMode = bestMode;
        _gizmoDragStartPosition = _gizmoPosition;
        _gizmoDragStartRotation = _gizmoRotation;

        _gizmoDragAxis = bestMode switch
        {
            GizmoMode.TranslateX or GizmoMode.RotateX => axisX,
            GizmoMode.TranslateY or GizmoMode.RotateY => axisY,
            GizmoMode.TranslateZ or GizmoMode.RotateZ => axisZ,
            _ => axisX
        };

        _gizmoDragAxisLocal = bestMode switch
        {
            GizmoMode.TranslateX or GizmoMode.RotateX => Vector3.UnitX,
            GizmoMode.TranslateY or GizmoMode.RotateY => Vector3.UnitY,
            GizmoMode.TranslateZ or GizmoMode.RotateZ => Vector3.UnitZ,
            _ => Vector3.UnitX
        };

        if (bestMode is GizmoMode.TranslateX or GizmoMode.TranslateY or GizmoMode.TranslateZ)
        {
            if (ClosestPointsRayLine(rayOrigin, rayDir, _gizmoPosition, _gizmoDragAxis, out _, out var t))
                _gizmoDragStartAxisT = t;
            else
                _gizmoDragStartAxisT = 0f;
        }
        else
        {
            _gizmoDragPlaneNormal = Vector3.Normalize(_gizmoDragAxis);
            if (TryRayPlane(rayOrigin, rayDir, _gizmoPosition, _gizmoDragPlaneNormal, out var hit))
            {
                var dir = hit - _gizmoPosition;
                if (dir.LengthSquared() < 1e-6f)
                    dir = _renderer.Camera.Right;
                _gizmoDragStartVector = Vector3.Normalize(dir);
            }
            else
            {
                _gizmoDragStartVector = _renderer.Camera.Right;
            }
        }

        return true;
    }

    private void UpdateGizmoDrag(Point screenPos, AvaloniaKeyModifiers modifiers)
    {
        if (!_gizmoDragging || _renderer == null)
            return;

        if (!TryGetRay(screenPos, out var rayOrigin, out var rayDir))
            return;

        var snap = modifiers.HasFlag(AvaloniaKeyModifiers.Shift);

        if (_gizmoMode is GizmoMode.TranslateX or GizmoMode.TranslateY or GizmoMode.TranslateZ)
        {
            if (!ClosestPointsRayLine(rayOrigin, rayDir, _gizmoDragStartPosition, _gizmoDragAxis, out _, out var t))
                return;

            var delta = t - _gizmoDragStartAxisT;
            if (snap)
            {
                const float snapStep = 1.0f;
                delta = MathF.Round(delta / snapStep) * snapStep;
            }

            var newPos = _gizmoDragStartPosition + _gizmoDragAxis * delta;
            _gizmoPosition = newPos;
            _gizmoDirty = true;
            CampathGizmoPoseChanged?.Invoke(newPos, _gizmoRotation);
            RequestNextFrame();
            return;
        }

        if (!TryRayPlane(rayOrigin, rayDir, _gizmoDragStartPosition, _gizmoDragPlaneNormal, out var rotHit))
            return;

        var dirVec = rotHit - _gizmoDragStartPosition;
        if (dirVec.LengthSquared() < 1e-6f)
            return;
        var currentVector = Vector3.Normalize(dirVec);
        var angle = SignedAngleAroundAxis(_gizmoDragStartVector, currentVector, _gizmoDragAxis);
        if (snap)
        {
            const float snapDeg = 15.0f;
            angle = MathF.Round(angle / snapDeg) * snapDeg;
        }

        var rotAxis = _gizmoUseLocalSpace ? _gizmoDragAxisLocal : _gizmoDragAxis;
        var deltaRot = Quaternion.CreateFromAxisAngle(rotAxis, DegToRad(angle));
        var newRot = _gizmoUseLocalSpace
            ? Quaternion.Normalize(_gizmoDragStartRotation * deltaRot)
            : Quaternion.Normalize(deltaRot * _gizmoDragStartRotation);

        _gizmoRotation = newRot;
        _gizmoDirty = true;
        CampathGizmoPoseChanged?.Invoke(_gizmoPosition, newRot);
        RequestNextFrame();
    }

    private void UpdateGizmoHover(Point screenPos)
    {
        if (!_gizmoVisible || _renderer == null || _gizmoDragging)
            return;

        var (axisX, axisY, axisZ) = GetGizmoAxes();
        var scale = Math.Clamp(Vector3.Distance(_renderer.Camera.Location, _gizmoPosition) * 0.12f, 24f, 120f);
        var axisLength = scale;
        var ringRadius = scale * 0.75f;

        GizmoMode bestMode = GizmoMode.None;
        var bestDistance = float.MaxValue;

        if (TryPickTranslationScreen(screenPos, axisX, axisLength, 10f, out var dx) && dx < bestDistance)
        {
            bestDistance = dx;
            bestMode = GizmoMode.TranslateX;
        }
        if (TryPickTranslationScreen(screenPos, axisY, axisLength, 10f, out var dy) && dy < bestDistance)
        {
            bestDistance = dy;
            bestMode = GizmoMode.TranslateY;
        }
        if (TryPickTranslationScreen(screenPos, axisZ, axisLength, 10f, out var dz) && dz < bestDistance)
        {
            bestDistance = dz;
            bestMode = GizmoMode.TranslateZ;
        }

        if (bestMode == GizmoMode.None)
        {
            if (TryPickRotationScreen(screenPos, axisX, ringRadius, 10f, out var rx) && rx < bestDistance)
            {
                bestDistance = rx;
                bestMode = GizmoMode.RotateX;
            }
            if (TryPickRotationScreen(screenPos, axisY, ringRadius, 10f, out var ry) && ry < bestDistance)
            {
                bestDistance = ry;
                bestMode = GizmoMode.RotateY;
            }
            if (TryPickRotationScreen(screenPos, axisZ, ringRadius, 10f, out var rz) && rz < bestDistance)
            {
                bestDistance = rz;
                bestMode = GizmoMode.RotateZ;
            }
        }

        if (bestMode != _gizmoHover)
        {
            _gizmoHover = bestMode;
            _gizmoDirty = true;
            RequestNextFrame();
        }
    }

    private bool TryPickTranslationScreen(Point screenPos, Vector3 axis, float axisLength, float pixelThreshold, out float distance)
    {
        distance = float.MaxValue;
        if (_renderer == null)
            return false;

        var origin = _gizmoPosition;
        var tip = origin + axis * axisLength;
        if (!TryProjectGizmoToScreenRaw(origin, _renderWidth, _renderHeight, out var a))
            return false;
        if (!TryProjectGizmoToScreenRaw(tip, _renderWidth, _renderHeight, out var b))
            return false;

        distance = DistancePointToSegment2D(screenPos, a, b);
        return distance <= pixelThreshold;
    }

    private bool TryPickRotationScreen(Point screenPos, Vector3 axis, float radius, float pixelThreshold, out float distance)
    {
        distance = float.MaxValue;
        if (_renderer == null)
            return false;

        var (u, v) = GetOrthonormalBasis(axis);
        const int segments = 36;
        Point? prev = null;
        var min = float.MaxValue;
        for (var i = 0; i <= segments; i++)
        {
            var t = i / (float)segments * MathF.PI * 2f;
            var world = _gizmoPosition + (u * MathF.Cos(t) + v * MathF.Sin(t)) * radius;
            if (!TryProjectGizmoToScreenRaw(world, _renderWidth, _renderHeight, out var p))
            {
                prev = null;
                continue;
            }

            if (prev.HasValue)
            {
                var d = DistancePointToSegment2D(screenPos, prev.Value, p);
                if (d < min)
                    min = d;
            }

            prev = p;
        }

        distance = min;
        return distance <= pixelThreshold;
    }

    private static float DistancePointToSegment2D(Point p, Point a, Point b)
    {
        var ab = b - a;
        var ap = p - a;
        var abLenSq = ab.X * ab.X + ab.Y * ab.Y;
        if (abLenSq <= double.Epsilon)
            return (float)Math.Sqrt(ap.X * ap.X + ap.Y * ap.Y);

        var t = (ap.X * ab.X + ap.Y * ab.Y) / abLenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        var closest = new Point(a.X + ab.X * t, a.Y + ab.Y * t);
        var dx = p.X - closest.X;
        var dy = p.Y - closest.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    private bool TryProjectGizmoToScreen(Vector3 world, int width, int height, out Point screen)
    {
        if (_renderer == null)
        {
            screen = default;
            return false;
        }

        return TryProjectToScreen(world, _renderer.Camera, width, height, out screen);
    }

    private bool TryProjectGizmoToScreenRaw(Vector3 world, int width, int height, out Point screen)
    {
        screen = default;
        if (_renderer == null)
            return false;

        var camera = _renderer.Camera;
        var toWorld = world - camera.Location;
        var z = Vector3.Dot(camera.Forward, toWorld);
        if (z <= 0.001f)
            return false;

        var x = Vector3.Dot(camera.Right, toWorld);
        var y = Vector3.Dot(camera.Up, toWorld);

        var fovY = DegToRad(_rendererContext?.FieldOfView ?? 60f);
        var tanY = MathF.Tan(fovY * 0.5f);
        var tanX = tanY * camera.AspectRatio;
        if (tanX <= 1e-6f || tanY <= 1e-6f)
            return false;

        var nx = x / (z * tanX);
        var ny = y / (z * tanY);
        var screenX = (nx * 0.5f + 0.5f) * width;
        var screenY = (-ny * 0.5f + 0.5f) * height;
        screen = new Point(screenX, screenY);
        return true;
    }

    private static bool TryRayPlane(Vector3 rayOrigin, Vector3 rayDir, Vector3 planeOrigin, Vector3 planeNormal, out Vector3 hit)
    {
        var denom = Vector3.Dot(rayDir, planeNormal);
        if (MathF.Abs(denom) < 1e-6f)
        {
            hit = default;
            return false;
        }

        var t = Vector3.Dot(planeOrigin - rayOrigin, planeNormal) / denom;
        if (t < 0f)
        {
            hit = default;
            return false;
        }

        hit = rayOrigin + rayDir * t;
        return true;
    }

    private static bool ClosestPointsRayLine(Vector3 rayOrigin, Vector3 rayDir, Vector3 lineOrigin, Vector3 lineDir, out float s, out float t)
    {
        var r = rayOrigin - lineOrigin;
        var a = Vector3.Dot(rayDir, rayDir);
        var e = Vector3.Dot(lineDir, lineDir);
        var f = Vector3.Dot(lineDir, r);
        var c = Vector3.Dot(rayDir, r);
        var b = Vector3.Dot(rayDir, lineDir);
        var denom = a * e - b * b;
        if (MathF.Abs(denom) < 1e-6f)
        {
            s = 0f;
            t = 0f;
            return false;
        }

        s = (b * f - c * e) / denom;
        t = (a * f - b * c) / denom;
        if (s < 0f)
            s = 0f;
        return true;
    }

    private bool TryGetRay(Point screenPos, out Vector3 origin, out Vector3 dir)
    {
        origin = default;
        dir = default;
        if (_renderer == null || _rendererContext == null || _renderWidth <= 0 || _renderHeight <= 0)
            return false;

        var camera = _renderer.Camera;
        var nx = (float)(screenPos.X / _renderWidth * 2.0 - 1.0);
        var ny = (float)(1.0 - screenPos.Y / _renderHeight * 2.0);

        var fovY = DegToRad(_rendererContext.FieldOfView);
        var tanY = MathF.Tan(fovY * 0.5f);
        var tanX = tanY * camera.AspectRatio;

        var direction = camera.Forward
            + camera.Right * (nx * tanX)
            + camera.Up * (ny * tanY);

        origin = camera.Location;
        dir = Vector3.Normalize(direction);
        return true;
    }

    private static float SignedAngleAroundAxis(Vector3 from, Vector3 to, Vector3 axis)
    {
        var cross = Vector3.Cross(from, to);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        var angle = MathF.Atan2(Vector3.Dot(axis, cross), dot);
        return RadToDeg(angle);
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
        transform.Yaw = NormalizeNear(yaw, transform.Yaw);
        transform.Pitch = NormalizeNear(pitch, transform.Pitch);
        transform.Roll = NormalizeNear(roll, transform.Roll);
    }

    private static float NormalizeNear(float value, float target)
    {
        var delta = target - value;
        var turns = MathF.Round(delta / 360f);
        return value + turns * 360f;
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

    private bool TryGetAnalogState(out float analogLX, out float analogLY, out float analogRY, out float analogRX)
    {
        analogLX = 0f;
        analogLY = 0f;
        analogRY = 0f;
        analogRX = 0f;

        if (!_freecamInputEnabled || _freecamSettings?.AnalogKeyboardEnabled != true || _inputSender == null)
            return false;

        return _inputSender.TryGetAnalogState(out var enabled, out analogLX, out analogLY, out analogRY, out analogRX) && enabled;
    }

    private bool TryGetWalkPhysics(out Rubikon physics)
    {
        var physicsWorld = _renderer?.Scene?.PhysicsWorld;
        if (physicsWorld == null)
        {
            physics = null!;
            return false;
        }

        physics = physicsWorld;
        return true;
    }

    private float GetWalkHalfHeight(float crouchAmount)
    {
        var clamped = Clamp(crouchAmount, 0f, 1f);
        return _freecamConfig.WalkHullHalfHeight
            + (_freecamConfig.WalkCrouchHullHalfHeight - _freecamConfig.WalkHullHalfHeight) * clamped;
    }

    private float GetWalkCameraHeight(float crouchAmount)
    {
        return MathF.Max(0f, GetWalkHalfHeight(crouchAmount) - _freecamConfig.WalkCameraTopInset);
    }

    private bool TryTraceWalkHullMove(Rubikon physics, Vector3 from, Vector3 to, float halfHeight, out Rubikon.TraceResult trace)
    {
        var aabb = new AABB(
            new Vector3(-_freecamConfig.WalkHullRadius, -_freecamConfig.WalkHullRadius, -halfHeight),
            new Vector3(_freecamConfig.WalkHullRadius, _freecamConfig.WalkHullRadius, halfHeight));
        trace = physics.TraceAABB(from, to, aabb, "player");
        return true;
    }

    private bool ProbeWalkGround(Rubikon physics, Vector3 from, float probeDistance, float halfHeight, out Rubikon.TraceResult trace)
    {
        return TryTraceWalkHullMove(physics, from, from - new Vector3(0f, 0f, probeDistance), halfHeight, out trace);
    }

    private static Vector3 ResolveWalkTracePosition(in Rubikon.TraceResult trace, Vector3 fallback)
    {
        const float surfaceEpsilon = 0.03125f;
        return trace.Hit
            ? trace.HitPosition + trace.HitNormal * surfaceEpsilon
            : fallback;
    }

    private bool TryWalkHorizontalMove(Rubikon physics, Vector3 from, Vector3 delta, bool allowStep, float halfHeight, out Vector3 result)
    {
        var directTo = from + delta;
        TryTraceWalkHullMove(physics, from, directTo, halfHeight, out var directTrace);
        if (!directTrace.Hit)
        {
            result = directTo;
            return true;
        }

        if (!allowStep || _freecamConfig.WalkStepHeight <= 0f)
        {
            result = ResolveWalkTracePosition(directTrace, directTo);
            return true;
        }

        TryTraceWalkHullMove(physics, from, from + new Vector3(0f, 0f, _freecamConfig.WalkStepHeight), halfHeight, out var upTrace);
        if (upTrace.Hit)
        {
            result = ResolveWalkTracePosition(directTrace, directTo);
            return true;
        }

        var stepUp = ResolveWalkTracePosition(upTrace, from + new Vector3(0f, 0f, _freecamConfig.WalkStepHeight));
        TryTraceWalkHullMove(physics, stepUp, stepUp + delta, halfHeight, out var forwardTrace);
        var stepForward = ResolveWalkTracePosition(forwardTrace, stepUp + delta);

        TryTraceWalkHullMove(
            physics,
            stepForward,
            stepForward - new Vector3(0f, 0f, _freecamConfig.WalkStepHeight + _freecamConfig.WalkGroundProbe),
            halfHeight,
            out var downTrace);
        result = ResolveWalkTracePosition(downTrace, stepForward);
        return true;
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
        LockFreecamCursorToViewport();
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.None);
        if (!_freecamCursorHidden)
        {
            ShowCursor(false);
            _freecamCursorHidden = true;
        }
    }

    private void UnlockFreecamCursor()
    {
        ClipCursor(IntPtr.Zero);
        if (_freecamCursorHidden)
        {
            ShowCursor(true);
            Cursor = Avalonia.Input.Cursor.Default;
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
        LockFreecamCursorToViewport();
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

    private void ApplyOrbitPointerMove(Point localPoint)
    {
        var effectivePoint = GetOrbitEffectivePointerPoint(localPoint, out var wrappedPoint);
        var delta = effectivePoint - _lastPointer;
        Orbit((float)delta.X, (float)delta.Y);

        if (wrappedPoint.HasValue)
        {
            WarpOrbitCursor(wrappedPoint.Value);
        }
        else
        {
            _lastPointer = localPoint;
        }
    }

    private Point GetOrbitEffectivePointerPoint(Point localPoint, out Point? wrappedPoint)
    {
        wrappedPoint = null;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 24 || height <= 24)
            return localPoint;

        const double margin = 8.0;
        var effectiveX = localPoint.X;
        var effectiveY = localPoint.Y;
        var wrappedX = double.NaN;
        var wrappedY = double.NaN;

        if (localPoint.X <= margin)
        {
            effectiveX = margin;
            wrappedX = width - margin - 1;
        }
        else if (localPoint.X >= width - margin - 1)
        {
            effectiveX = width - margin - 1;
            wrappedX = margin;
        }

        if (localPoint.Y <= margin)
        {
            effectiveY = margin;
            wrappedY = height - margin - 1;
        }
        else if (localPoint.Y >= height - margin - 1)
        {
            effectiveY = height - margin - 1;
            wrappedY = margin;
        }

        if (!double.IsNaN(wrappedX) || !double.IsNaN(wrappedY))
        {
            wrappedPoint = new Point(
                double.IsNaN(wrappedX) ? effectiveX : wrappedX,
                double.IsNaN(wrappedY) ? effectiveY : wrappedY);
        }

        return new Point(effectiveX, effectiveY);
    }

    private void WarpOrbitCursor(Point wrappedLocal)
    {
        if (!TryGetScreenPoint(wrappedLocal, out var wrappedScreen))
            return;

        SetCursorPosition(wrappedScreen.X, wrappedScreen.Y);
        _lastPointer = TryGetLocalPointFromScreen(wrappedScreen, out var actualLocal)
            ? actualLocal
            : wrappedLocal;
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

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    private static void SetCursorPosition(int x, int y)
    {
        SetCursorPos(x, y);
    }

    private void LockFreecamCursorToViewport()
    {
        if (!TryGetScreenPoint(new Point(0, 0), out var topLeft) ||
            !TryGetScreenPoint(new Point(Bounds.Width, Bounds.Height), out var bottomRight))
        {
            return;
        }

        var rect = new RECT
        {
            left = Math.Min(topLeft.X, bottomRight.X),
            top = Math.Min(topLeft.Y, bottomRight.Y),
            right = Math.Max(topLeft.X, bottomRight.X),
            bottom = Math.Max(topLeft.Y, bottomRight.Y)
        };

        if (rect.right <= rect.left || rect.bottom <= rect.top)
            return;

        ClipCursor(ref rect);
    }

    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private void CaptureOrbitMouse()
    {
        if (_hwnd != IntPtr.Zero)
            SetCapture(_hwnd);
    }

    private static void ReleaseOrbitMouse()
    {
        ReleaseCapture();
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

    private static float GetSourceVerticalFovRadians(float sourceFovDeg)
    {
        var hRad = DegToRad(Math.Clamp(sourceFovDeg, 1.0f, 179.0f));
        var vRad = 2f * MathF.Atan(MathF.Tan(hRad * 0.5f) * (3f / 4f));
        return Math.Clamp(vRad, DegToRad(1.0f), DegToRad(179.0f));
    }

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

        // Match C++ FreecamController implementation.
        var omega = 2f / smoothTime;
        var x = omega * deltaTime;
        var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

        var change = current - target;
        var temp = (currentVelocity + omega * change) * deltaTime;
        currentVelocity = (currentVelocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    private void InitializeAfterNativeCreated()
    {
        if (_nativeInitDone || !OperatingSystem.IsWindows())
        {
            return;
        }

        _nativeInitDone = true;
        InitializeNativeWindow();
        StartRenderLoop();
        RequestNextFrame();
    }

    private void InitializeNativeWindow()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(InitializeNativeWindow).GetAwaiter().GetResult();
            return;
        }

        lock (_nativeWindowLock)
        {
        if (_nativeWindow != null)
        {
            return;
        }

        GLFWProvider.CheckForMainThread = false;
        GLFWProvider.EnsureInitialized();

        var settings = new NativeWindowSettings
        {
            APIVersion = GLEnvironment.RequiredVersion,
            Flags = ContextFlags.ForwardCompatible,
            StartFocused = false,
            StartVisible = false,
            WindowBorder = WindowBorder.Hidden,
            WindowState = GLWindowState.Normal,
            Title = "HOT VRF Viewport",
            ClientSize = new Vector2i(32, 32),
        };

        _nativeWindow = new NativeWindow(settings);
        IntPtr hwnd;
        unsafe
        {
            hwnd = GLFW.GetWin32Window(_nativeWindow.WindowPtr);
        }
        SetWindowAsChild(hwnd, _hwnd);
        _nativeWindow.IsVisible = true;
        _nativeWindow.Context.MakeNoneCurrent();
        LogMessage($"NativeWindow created hwnd=0x{hwnd.ToInt64():X}");

        _renderWidth = Math.Max(1, (int)Bounds.Width);
        _renderHeight = Math.Max(1, (int)Bounds.Height);
        _nativeWindow.ClientRectangle = new Box2i(0, 0, _renderWidth, _renderHeight);
        }
    }

    private void DisposeRenderer(bool disposeWindow = true)
    {
        LogMessage("DisposeRenderer");
        _rendererReady = false;
        ClearLiveLinkNodes();
        _textRenderer = null;
        _renderer?.Dispose();
        _renderer = null;
        _rendererContext?.Dispose();
        _rendererContext = null;
        _fileLoader?.Dispose();
        _fileLoader = null;
        _mapPackage?.Dispose();
        _mapPackage = null;
        if (_mainFramebuffer != null && _mainFramebuffer != _defaultFramebuffer)
        {
            _mainFramebuffer.Delete();
        }
        _mainFramebuffer = null;
        _defaultFramebuffer = null;
        DisableFreecam();
        _mapHasExternalReferences = false;

        lock (_nativeWindowLock)
        {
            if (_nativeWindow != null)
            {
                try
                {
                    _nativeWindow.Context.MakeCurrent();
                    DisposePinResources();
                    DisposeLiveLinkIconResources();
                    DisposeCampathOverlayResources();
                    DisposeGizmoResources();
                }
                catch (Exception ex)
                {
                    LogMessage($"DisposePinResources failed: {ex.GetType().Name}: {ex.Message}");
                }
                _nativeWindow.Context.MakeNoneCurrent();
                if (disposeWindow)
                {
                    _nativeWindow.Dispose();
                    _nativeWindow = null;
                }
            }
        }
    }

    private void OnViewportFpsCapChanged()
    {
        _viewportFpsCapCached = ViewportFpsCap;
        _lastLimiterTicks = _frameLimiter.ElapsedTicks;
        if (_frameLimiterTimer != null)
        {
            _frameLimiterTimer.Stop();
        }
        _frameLimiterPending = false;
        RequestNextFrame();
    }

    private void OnPostprocessEnabledChanged()
    {
        _postprocessEnabledCached = PostprocessEnabled;
        ApplyRendererOptions();
    }

    private void OnColorCorrectionEnabledChanged()
    {
        _colorCorrectionEnabledCached = ColorCorrectionEnabled;
        ApplyRendererOptions();
    }

    private void OnDynamicShadowsEnabledChanged()
    {
        _dynamicShadowsEnabledCached = DynamicShadowsEnabled;
        ApplyRendererOptions();
    }

    private void OnWireframeEnabledChanged()
    {
        _wireframeEnabledCached = WireframeEnabled;
        ApplyRendererOptions();
    }

    private void OnSkipWaterEnabledChanged()
    {
        _skipWaterEnabledCached = SkipWaterEnabled;
        ApplyRendererOptions();
    }

    private void OnSkipTranslucentEnabledChanged()
    {
        _skipTranslucentEnabledCached = SkipTranslucentEnabled;
        ApplyRendererOptions();
    }

    private void OnShowFpsChanged()
    {
        _showFpsCached = ShowFps;
        RequestNextFrame();
    }

    private void OnShowPlayerPinsChanged()
    {
        _showPlayerPinsCached = ShowPlayerPins;
        RequestNextFrame();
    }

    private void OnLiveLinkItemIconsEnabledChanged()
    {
        _liveLinkItemIconsEnabledCached = LiveLinkItemIconsEnabled;
        RequestNextFrame();
    }

    private void OnLiveLinkIconFilterChanged()
    {
        _liveLinkWeaponIconsEnabledCached = LiveLinkWeaponIconsEnabled;
        _liveLinkGrenadeIconsEnabledCached = LiveLinkGrenadeIconsEnabled;
        _liveLinkProjectileIconsEnabledCached = LiveLinkProjectileIconsEnabled;
        _liveLinkObjectiveIconsEnabledCached = LiveLinkObjectiveIconsEnabled;
        _liveLinkDeadPlayerIconsEnabledCached = LiveLinkDeadPlayerIconsEnabled;
        RequestNextFrame();
    }

    private void OnShadowTextureSizeChanged()
    {
        _shadowTextureSizeCached = ShadowTextureSize;
        RequestRendererReload();
    }

    private void OnMaxTextureSizeChanged()
    {
        _maxTextureSizeCached = MaxTextureSize;
        RequestRendererReload();
    }

    private void OnRenderModeChanged()
    {
        var wasFastUnlit = IsFastUnlit();
        _renderModeCached = string.IsNullOrWhiteSpace(RenderMode) ? "Default" : RenderMode;
        var isFastUnlit = IsFastUnlit();
        if (wasFastUnlit != isFastUnlit)
        {
            RequestRendererReload();
            return;
        }
        ApplyRendererOptions();
    }

    private void OnFreecamSettingsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_freecamSettings != null)
        {
            _freecamSettings.PropertyChanged -= OnFreecamSettingsPropertyChanged;
        }

        _freecamSettings = e.NewValue as FreecamSettings;
        if (_freecamSettings != null)
        {
            _freecamSettings.PropertyChanged += OnFreecamSettingsPropertyChanged;
        }

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
            RotDampingRatio = (float)_freecamSettings.RotDampingRatio,
            WalkMoveSpeed = (float)_freecamSettings.WalkMoveSpeed,
            WalkMoveAcceleration = (float)_freecamSettings.WalkMoveAcceleration,
            WalkMoveDeceleration = (float)_freecamSettings.WalkMoveDeceleration,
            WalkRunMultiplier = (float)_freecamSettings.WalkRunMultiplier,
            WalkCrouchSpeedMultiplier = (float)_freecamSettings.WalkCrouchSpeedMultiplier,
            WalkLookHalfTime = (float)_freecamSettings.WalkLookHalfTime,
            WalkFovHalfTime = (float)_freecamSettings.WalkFovHalfTime,
            WalkGravity = (float)_freecamSettings.WalkGravity,
            WalkJumpSpeed = (float)_freecamSettings.WalkJumpSpeed,
            WalkHullRadius = (float)_freecamSettings.WalkHullRadius,
            WalkHullHalfHeight = (float)_freecamSettings.WalkHullHalfHeight,
            WalkCrouchHullHalfHeight = (float)_freecamSettings.WalkCrouchHullHalfHeight,
            WalkCameraTopInset = (float)_freecamSettings.WalkCameraTopInset,
            WalkStepHeight = (float)_freecamSettings.WalkStepHeight,
            WalkGroundProbe = (float)_freecamSettings.WalkGroundProbe,
            WalkMinGroundNormalZ = (float)_freecamSettings.WalkMinGroundNormalZ,
            WalkModeDefaultEnabled = _freecamSettings.WalkModeDefaultEnabled,
            HandheldDefaultEnabled = _freecamSettings.HandheldDefaultEnabled,
            WalkBobAmplitudeZ = (float)_freecamSettings.WalkBobAmplitudeZ,
            WalkBobAmplitudeSide = (float)_freecamSettings.WalkBobAmplitudeSide,
            WalkBobAmplitudeRoll = (float)_freecamSettings.WalkBobAmplitudeRoll,
            WalkBobFrequency = (float)_freecamSettings.WalkBobFrequency,
            HandheldShakePosAmplitude = (float)_freecamSettings.HandheldShakePosAmplitude,
            HandheldShakeAngAmplitude = (float)_freecamSettings.HandheldShakeAngAmplitude,
            HandheldShakeFrequency = (float)_freecamSettings.HandheldShakeFrequency,
            HandheldDriftPosAmplitude = (float)_freecamSettings.HandheldDriftPosAmplitude,
            HandheldDriftAngAmplitude = (float)_freecamSettings.HandheldDriftAngAmplitude,
            HandheldDriftFrequency = (float)_freecamSettings.HandheldDriftFrequency
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

        _pinSource = pins;
        UpdatePinsFromSource();
    }

    public void SetPlayerStatuses(IReadOnlyList<ViewportPlayerStatus> statuses)
    {
        var snapshot = statuses
            .Where(status => status.Slot is >= 0 and <= 9)
            .GroupBy(status => status.Slot)
            .ToDictionary(group => group.Key, group => group.Last());

        lock (_playerStatusLock)
        {
            _playerStatusesBySlot = snapshot;
        }
        RequestNextFrame();
    }

    public void SetCampathOverlay(CampathOverlayData? data)
    {
        lock (_campathOverlayLock)
        {
            _campathOverlayData = data;
            _campathOverlayDirty = true;
        }
        RequestNextFrame();
    }

    public void SetCampathGizmo(CampathGizmoState? state)
    {
        if (state == null || !state.Value.Visible)
        {
            if (_gizmoVisible)
            {
                _gizmoVisible = false;
                _gizmoDirty = true;
                RequestNextFrame();
            }
            return;
        }

        _gizmoVisible = true;
        _gizmoPosition = state.Value.Position;
        _gizmoRotation = Quaternion.Normalize(state.Value.Rotation);
        _gizmoUseLocalSpace = state.Value.UseLocalSpace;
        _gizmoDirty = true;
        RequestNextFrame();
    }

    private void UpdatePinsFromSource()
    {
        lock (_pinLock)
        {
            _pins.Clear();
            _pinLabels.Clear();

            if (_pinSource == null || _pinSource.Count == 0)
            {
                _pinsDirty = true;
                lock (_labelLock)
                {
                    _labelHitCache = new List<PinLabel>();
                }
                RequestNextFrame();
                return;
            }

            var pinOffset = new Vector3(0f, 0f, PinOffsetZ);
            foreach (var pin in _pinSource)
            {
                var position = new Vector3((float)pin.Position.X, (float)pin.Position.Y, (float)pin.Position.Z) + pinOffset;
                var forward = new Vector3((float)pin.Forward.X, (float)pin.Forward.Y, (float)pin.Forward.Z);
                var color = GetTeamColor(pin.Team);

                _pins.Add(new PinRenderData
                {
                    Position = position,
                    Forward = forward,
                    Color = color,
                    Label = pin.Label
                });

                if (!string.IsNullOrEmpty(pin.Label))
                {
                    _pinLabels.Add(new PinLabel
                    {
                        Text = pin.Label,
                        World = position,
                        Color = ToColor32(color)
                    });
                }
            }
        }

        _pinsDirty = true;
        RequestNextFrame();
    }

    private bool TryHandlePinClick(Point position)
    {
        lock (_pinLock)
        {
            if (_pins.Count == 0)
                return false;
        }

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
                lock (_pinLock)
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
        }

        return false;
    }

    private bool TryFindPinFromMarkerHit(Point position, out PinRenderData pin)
    {
        pin = default!;
        var width = Math.Max(1, _renderWidth);
        var height = Math.Max(1, _renderHeight);
        if (width <= 0 || height <= 0 || _renderer == null)
            return false;

        var camera = _renderer.Camera;
        const double hitRadius = 12.0;
        var hitRadiusSq = hitRadius * hitRadius;
        var bestDistSq = double.MaxValue;
        var found = false;

        lock (_pinLock)
        {
            foreach (var candidate in _pins)
            {
                if (!TryProjectToScreen(candidate.Position, camera, width, height, out var screen))
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
        }

        return found;
    }

    private bool TryHandleLiveLinkIconClick(Point position)
    {
        List<LiveLinkIconHit> hits;
        lock (_liveLinkIconHitLock)
        {
            if (_liveLinkIconHitCache.Count == 0)
                return false;
            hits = new List<LiveLinkIconHit>(_liveLinkIconHitCache);
        }

        LiveLinkIconHit? bestHit = null;
        var bestArea = double.MaxValue;
        foreach (var hit in hits)
        {
            if (position.X < hit.X0 || position.X > hit.X1 || position.Y < hit.Y0 || position.Y > hit.Y1)
                continue;

            var area = Math.Max(1.0, (hit.X1 - hit.X0) * (hit.Y1 - hit.Y0));
            if (area < bestArea)
            {
                bestArea = area;
                bestHit = hit;
            }
        }

        if (bestHit == null)
            return false;

        ActivateFreecamAtPosition(bestHit.Value.World);
        return true;
    }

    private void ActivateFreecamAtPosition(Vector3 position)
    {
        var keepInputEnabled = _freecamInputEnabled;
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
        }

        _freecamTransform.Position = position;
        _freecamSmoothed = _freecamTransform;
        _freecamOutput = _freecamTransform;
        _freecamSmoothedQuat = _freecamSmoothed.Orientation;
        _freecamActive = true;
        _freecamInitialized = true;
        _freecamInputEnabled = keepInputEnabled;
        _freecamLastUpdate = DateTime.UtcNow;
        ResetFreecamState();
        RequestNextFrame();
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

    private void OnPinScaleChanged()
    {
        _pinsDirty = true;
        RequestNextFrame();
    }

    private void OnPinOffsetChanged()
    {
        if (_pinSource != null)
        {
            UpdatePinsFromSource();
        }
    }

    private static Vector3 GetTeamColor(string team)
    {
        if (string.Equals(team, "CT", StringComparison.OrdinalIgnoreCase))
            return new Vector3(0.35f, 0.65f, 1.0f);
        if (string.Equals(team, "T", StringComparison.OrdinalIgnoreCase))
            return new Vector3(1.0f, 0.7f, 0.2f);
        return new Vector3(0.8f, 0.8f, 0.8f);
    }

    private static Color32 ToColor32(Vector3 color)
    {
        static byte ToByte(float value)
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            return (byte)MathF.Round(clamped * 255f);
        }

        return new Color32(ToByte(color.X), ToByte(color.Y), ToByte(color.Z), 255);
    }

    private void OnMapPathChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var path = e.NewValue as string;
        LogMessage($"OnMapPathChanged: {path ?? "<null>"}");
        _pendingMapPath = string.IsNullOrWhiteSpace(path) ? null : path;
        _mapLoadPending = true;
        RequestNextFrame();
    }

    private void ApplyRendererOptions()
    {
        if (_renderer == null || !_rendererReady)
        {
            return;
        }

        var effectiveRenderMode = GetEffectiveRenderMode();
        _renderer.Postprocess.Enabled = _postprocessEnabledCached && IsRenderModeDefault();
        _renderer.Postprocess.ColorCorrectionEnabled = _colorCorrectionEnabledCached;
        _renderer.Scene.LightingInfo.EnableDynamicShadows = _dynamicShadowsEnabledCached && !IsFastUnlit();
        _renderer.IsWireframe = _wireframeEnabledCached;
        _renderer.ShowWater = !_skipWaterEnabledCached;
        _renderer.ShowTranslucent = !_skipTranslucentEnabledCached;

        ApplyRenderModeToScene(_renderer.Scene, effectiveRenderMode);
        if (_renderer.SkyboxScene != null)
        {
            ApplyRenderModeToScene(_renderer.SkyboxScene, effectiveRenderMode);
        }

        if (_renderer.ViewBuffer?.Data != null)
        {
            _renderer.ViewBuffer.Data.RenderMode = RenderModes.GetShaderId(effectiveRenderMode);
        }
    }

    private static void ApplyRenderModeToScene(Scene scene, string renderMode)
    {
        foreach (var node in scene.AllNodes)
        {
            node.SetRenderMode(renderMode);
        }
    }

    private bool IsRenderModeDefault()
    {
        return string.Equals(_renderModeCached, "Default", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsFastUnlit()
    {
        return string.Equals(_renderModeCached, "FastUnlit", StringComparison.OrdinalIgnoreCase);
    }

    private string GetEffectiveRenderMode()
    {
        return IsFastUnlit() ? "Color" : _renderModeCached;
    }

    private void RequestRendererReload()
    {
        if (!_rendererReady)
        {
            return;
        }

        var mapPath = MapPath;
        if (string.IsNullOrWhiteSpace(mapPath))
        {
            return;
        }

        _pendingMapPath = mapPath;
        _mapLoadPending = true;
        RequestNextFrame();
    }

    private void StartRenderLoop()
    {
        if (_renderLoop != null)
        {
            return;
        }

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        _lastLimiterTicks = _frameLimiter.ElapsedTicks;
        _renderCts = new CancellationTokenSource();
        _renderLoop = Task.Run(() => RenderLoop(_renderCts.Token));
        LogMessage("StartRenderLoop");
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
        LogMessage("StopRenderLoop");
    }

    private void RenderLoop(CancellationToken token)
    {
        LogMessage("RenderLoop started");
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool continuous = _freecamActive;
                if (!continuous)
                {
                    _renderSignal.Wait(token);
                    _renderSignal.Reset();
                    RenderFrame();
                    continue;
                }

                float cap = GetEffectiveFpsCap(_viewportFpsCapCached);
                double targetMs = 1000.0 / cap;
                long nowTicks = _frameLimiter.ElapsedTicks;
                double elapsedMs = (nowTicks - _lastLimiterTicks) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < targetMs)
                {
                    int wait = (int)Math.Max(1.0, targetMs - elapsedMs);
                    _renderSignal.Wait(wait, token);
                }
                _renderSignal.Reset();
                _lastLimiterTicks = _frameLimiter.ElapsedTicks;

                RenderFrame();
            }
            catch (Exception ex)
            {
                LogMessage($"RenderLoop error: {ex}");
                Thread.Sleep(20);
            }
        }
        LogMessage("RenderLoop stopped");
    }

    private void RenderFrame()
    {
        NativeWindow? nativeWindow;
        lock (_nativeWindowLock)
        {
            nativeWindow = _nativeWindow;
        }
        if (nativeWindow == null || !nativeWindow.Exists)
        {
            LogMessage("RenderFrame skipped: no native window");
            return;
        }

        try
        {
            nativeWindow.Context.MakeCurrent();
        }
        catch (Exception ex)
        {
            LogMessage($"RenderFrame MakeCurrent failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        try
        {
            if (_mapLoadPending)
            {
                _mapLoadPending = false;
                if (!string.IsNullOrWhiteSpace(_pendingMapPath))
                {
                    LoadMap(_pendingMapPath);
                    return;
                }
                else
                {
                    DisposeRenderer(disposeWindow: false);
                    return;
                }
            }

            if (!_rendererReady || _renderer == null || _mainFramebuffer == null || _defaultFramebuffer == null)
            {
                if (_renderWidth > 0 && _renderHeight > 0)
                {
                    GL.Viewport(0, 0, _renderWidth, _renderHeight);
                    GL.ClearColor(0.08f, 0.08f, 0.08f, 1f);
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                }
                return;
            }

            if (!_renderLogged)
            {
                _renderLogged = true;
                LogMessage($"RenderFrame active size={_renderWidth}x{_renderHeight}");
            }

            var now = Stopwatch.GetTimestamp();
            if (_lastFrameTimestamp == 0)
            {
                _lastFrameTimestamp = now;
            }
            var delta = (float)Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds;
            _lastFrameTimestamp = now;

            UpdateFps(delta);
            RaiseFrameTick(delta);

            var width = Math.Max(1, _renderWidth);
            var height = Math.Max(1, _renderHeight);
            if (_mainFramebuffer.Width != width || _mainFramebuffer.Height != height)
            {
                _mainFramebuffer.Resize(width, height);
            }

            var updateContext = new Scene.UpdateContext
            {
                Camera = _renderer.Camera,
                TextRenderer = _textRenderer!,
                Timestep = delta,
            };

            UpdateFreecamForFrame();
            ApplyCameraForFrame(width, height);
            UpdateCampathOverlayCameraState();
            ApplyLiveLinkFrame();

            _renderer.Update(updateContext);
            RefreshLiveLinkLighting();

            var renderContext = new Scene.RenderContext
            {
                Camera = _renderer.Camera,
                Framebuffer = _mainFramebuffer,
                Scene = _renderer.Scene,
                Textures = _renderer.Textures,
            };

            _renderer.Render(renderContext);
            if (_pinsDirty)
            {
                RebuildPins();
            }
            if (_showPlayerPinsCached)
            {
                DrawPins(width, height);
            }
            if (_campathOverlayDirty)
            {
                RebuildCampathOverlay();
            }
            DrawCampathOverlay(width, height);
            if (_mainFramebuffer != _defaultFramebuffer)
            {
                _renderer.PostprocessRender(_mainFramebuffer, _defaultFramebuffer);
            }
            DrawLiveLinkIcons(width, height);
            AddPinLabels(width, height);
            AddFpsOverlay(width);
            _textRenderer?.Render(_renderer.Camera);
            try
            {
                nativeWindow.Context.SwapBuffers();
            }
            catch (Exception ex)
            {
                LogMessage($"SwapBuffers failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            nativeWindow.Context.MakeNoneCurrent();
        }
    }

    private void RaiseFrameTick(float delta)
    {
        if (delta <= 0f)
            return;

        if (FrameTick == null)
            return;

        FrameTick(delta);
    }

    private void LoadMap(string mapPath)
    {
        LogMessage($"LoadMap request: {mapPath}");
        DisposeRenderer();
        InitializeNativeWindow();
        if (_nativeWindow == null)
        {
            LogMessage("LoadMap aborted: NativeWindow not available.");
            return;
        }

        _nativeWindow.Context.MakeCurrent();
        try
        {
            if (!EnsureOpenGLBindings())
            {
                return;
            }

            var resolvedMapPath = ResolveMapPath(mapPath, out var mapPackage);
            if (string.IsNullOrWhiteSpace(resolvedMapPath))
            {
                LogMessage("LoadMap aborted: could not resolve map path.");
        DisposeRenderer();
                return;
            }

            _fileLoader = new GameFileLoader(null, mapPath);
            if (mapPackage != null)
            {
                _fileLoader.CurrentPackage = mapPackage;
                _fileLoader.AddPackageToSearch(mapPackage);
                _mapPackage = mapPackage;
                LogMessage($"Using VPK package: {mapPackage.FileName}");
            }

            _rendererContext = new RendererContext(_fileLoader, NullLogger.Instance);
            _rendererContext.MaxTextureSize = _maxTextureSizeCached;
            _renderer = new Renderer(_rendererContext);
            _renderer.ShadowTextureSize = _shadowTextureSizeCached;
            _textRenderer = new TextRenderer(_rendererContext, _renderer.Camera);

            GLEnvironment.Initialize(NullLogger.Instance);
            GLEnvironment.SetDefaultRenderState();

            try
            {
                _renderer.LoadRendererResources();
                _renderer.Postprocess.Load(1);
                _renderer.Postprocess.Enabled = _postprocessEnabledCached && IsRenderModeDefault();
                _renderer.Postprocess.ColorCorrectionEnabled = _colorCorrectionEnabledCached;
                ApplyDepthOfField();
                _renderer.Initialize();
                _textRenderer.Load();
                LogMessage("Renderer initialized.");
            }
            catch (Exception ex)
            {
                LogMessage($"Renderer init failed: {ex}");
                DisposeRenderer();
                return;
            }

            _defaultFramebuffer = Framebuffer.GLDefaultFramebuffer;
            _mainFramebuffer = Framebuffer.Prepare(
                nameof(_mainFramebuffer),
                Math.Max(1, _renderWidth),
                Math.Max(1, _renderHeight),
                1,
                new Framebuffer.AttachmentFormat(PixelInternalFormat.Rgba16f, GLPixelFormat.Rgba, PixelType.HalfFloat),
                Framebuffer.DepthAttachmentFormat.Depth32FStencil8);
            var status = _mainFramebuffer.Initialize();
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                _mainFramebuffer.Delete();
                _mainFramebuffer = _defaultFramebuffer;
            }

            _renderer.MainFramebuffer = _mainFramebuffer;

            var worldPath = WorldLoader.GetWorldNameFromMap(resolvedMapPath);
            LogMessage($"Resolved world path: {worldPath}");
            using var worldResource = _fileLoader.LoadFileCompiled(worldPath);
            if (worldResource?.DataBlock is not World world)
            {
                LogMessage("LoadMap failed: world resource not found or invalid.");
                DisposeRenderer();
                return;
            }

            if (IsFastUnlit())
            {
                _renderer.Scene.RenderAttributes["VRF_FAST_UNLIT"] = 1;
                _renderer.Scene.RenderAttributes["F_UNLIT"] = 1;
                _renderer.Scene.RenderAttributes["F_FULLBRIGHT"] = 1;
            }

            _mapHasExternalReferences = worldResource.ExternalReferences != null;
            var worldLoader = new WorldLoader(world, _renderer.Scene);
            worldLoader.Load(worldResource.ExternalReferences);
            _renderer.SkyboxScene = worldLoader.SkyboxScene;
            _renderer.Skybox2D = worldLoader.Skybox2D;

            PostSceneLoad(worldLoader.DefaultEnabledLayers);
            _rendererReady = true;
            ApplyRendererOptions();
            _renderLogged = false;
            LogMessage("LoadMap completed.");
        }
        finally
        {
            _nativeWindow.Context.MakeNoneCurrent();
        }
    }

    private static string? ResolveMapPath(string mapPath, out Package? mapPackage)
    {
        mapPackage = null;

        if (mapPath.EndsWith(".vmap_c", StringComparison.OrdinalIgnoreCase))
        {
            LogMessage($"ResolveMapPath: direct map {mapPath}");
            return mapPath.Replace('\\', '/');
        }

        if (!mapPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            LogMessage("ResolveMapPath: unsupported file type.");
            return null;
        }

        var package = new Package();
        package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
        package.Read(mapPath);
        mapPackage = package;

        var candidate = FindVmapInPackage(package, Path.GetFileNameWithoutExtension(mapPath));
        LogMessage($"ResolveMapPath: candidate={candidate ?? "null"}");
        return candidate;
    }

    private static string? FindVmapInPackage(Package package, string mapNameHint)
    {
        if (package.Entries == null)
        {
            LogMessage("FindVmapInPackage: entries missing.");
            return null;
        }

        PackageEntry? bestMatch = null;
        var desiredName = $"{mapNameHint}.vmap_c";

        foreach (var entries in package.Entries.Values)
        {
            foreach (var entry in entries)
            {
                var fullPath = entry.GetFullPath();
                if (!fullPath.EndsWith(".vmap_c", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = entry.GetFileName();
                if (fileName.Equals(desiredName, StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage($"FindVmapInPackage: exact match {fullPath}");
                    return NormalizePackagePath(fullPath);
                }

                if (bestMatch == null)
                {
                    bestMatch = entry;
                }
                else if (fullPath.Contains($"{Package.DirectorySeparatorChar}maps{Package.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    bestMatch = entry;
                }
            }
        }

        if (bestMatch != null)
        {
            LogMessage($"FindVmapInPackage: fallback match {bestMatch.GetFullPath()}");
        }
        return bestMatch == null ? null : NormalizePackagePath(bestMatch.GetFullPath());
    }

    private static string NormalizePackagePath(string path)
    {
        return path.Replace(Package.DirectorySeparatorChar, '/');
    }

    private static void LogMessage(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] VRFViewport: {message}";
        try
        {
            Console.WriteLine(line);
            if (!_logPathAnnounced)
            {
                _logPathAnnounced = true;
                Console.WriteLine($"[VRFViewport] Log file: {LogPath}");
            }
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            if (!_logWriteFailedLogged)
            {
                _logWriteFailedLogged = true;
                Console.WriteLine($"[VRFViewport] Log file write failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string GetLogPath()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "gl_viewport.log");
            File.WriteAllText(path, string.Empty);
            return path;
        }
        catch
        {
            var path = Path.Combine(Path.GetTempPath(), "gl_viewport.log");
            try
            {
                File.WriteAllText(path, string.Empty);
            }
            catch
            {
                // Logging failures are reported by LogMessage.
            }
            return path;
        }
    }

    private void ApplyLiveLinkReceiverSettings()
    {
        _liveLinkReceiverCached = LiveLinkReceiver;
        _liveLinkEnabledCached = LiveLinkEnabled;
        _liveLinkPortCached = LiveLinkPort;

        var receiver = _liveLinkReceiverCached;
        if (receiver == null)
        {
            if (!_liveLinkEnabledCached)
            {
                ClearLiveLinkNodes();
            }
            return;
        }

        receiver.Port = _liveLinkPortCached;
        receiver.Enabled = _liveLinkEnabledCached;
        LogMessage($"LiveLink receiver {(_liveLinkEnabledCached ? "enabled" : "disabled")} UDP {_liveLinkPortCached}");
        if (!_liveLinkEnabledCached)
        {
            ClearLiveLinkNodes();
        }
        RequestNextFrame();
    }

    private void ApplyLiveLinkFrame()
    {
        var receiver = _liveLinkReceiverCached;
        if (!_liveLinkEnabledCached || receiver == null)
            return;

        if (_renderer?.Scene == null || _fileLoader == null)
            return;

        var frame = receiver.GetLatestFrame();
        if (frame == null || frame.FrameId == _lastLiveLinkFrameId)
            return;

        _lastLiveLinkFrameId = frame.FrameId;
        _liveLinkIconBillboards.Clear();
        Dictionary<int, ViewportPlayerStatus> playerStatusesBySlot;
        lock (_playerStatusLock)
        {
            playerStatusesBySlot = new Dictionary<int, ViewportPlayerStatus>(_playerStatusesBySlot);
        }

        foreach (var hiddenId in frame.HiddenEntityIds)
        {
            RemoveLiveLinkNode(hiddenId);
        }

        var activeEntityIds = new HashSet<int>();
        foreach (var entity in frame.Entities)
        {
            if (entity.ViewModel)
                continue;

            var observerSlot = ResolveLiveLinkObserverSlot(entity);

            if (!entity.Visible)
            {
                RemoveLiveLinkNode(entity.Id);
                continue;
            }

            activeEntityIds.Add(entity.Id);
            var skeleton = receiver.GetSkeleton(entity.Id);
            var modelName = skeleton?.ModelName;
            if (skeleton == null || string.IsNullOrWhiteSpace(modelName))
            {
                if (_liveLinkLoggedMissingSkeletons.Add(entity.Id))
                    LogMessage($"LiveLink skipped entity {entity.Id}: missing skeleton metadata.");
                continue;
            }

            var node = GetOrCreateLiveLinkNode(entity.Id, modelName);
            if (node == null)
                continue;

            node.Node.Transform = entity.Transform;
            if (entity.HasBones && entity.LocalBoneTransforms.Count > 0)
            {
                node.Node.SetExternalPose(entity.LocalBoneTransforms);
            }

            var iconKey = TryGetLiveLinkIconKey(modelName, entity);
            if (iconKey != null && ShouldDrawLiveLinkItemIcon(entity, iconKey) && ShouldDrawLiveLinkIconCategory(entity, iconKey))
            {
                _liveLinkIconBillboards.Add(new LiveLinkIconBillboard(
                    new Vector3(entity.Transform.M41, entity.Transform.M42, entity.Transform.M43 + 18f),
                    iconKey,
                    entity.Projectile,
                    entity.Projectile ? LiveLinkProjectileIconTint : Vector3.One));
            }

            if (observerSlot is >= 0 and <= 9
                && _liveLinkDeadPlayerIconsEnabledCached
                && playerStatusesBySlot.TryGetValue(observerSlot, out var playerStatus)
                && !playerStatus.IsAlive)
            {
                var deadIconWorld = TryGetLiveLinkBoneWorldPosition(entity, skeleton, out var pelvisWorld)
                    ? pelvisWorld + new Vector3(0f, 0f, 20f)
                    : new Vector3(entity.Transform.M41, entity.Transform.M42, entity.Transform.M43 + 64f);
                _liveLinkIconBillboards.Add(new LiveLinkIconBillboard(
                    deadIconWorld,
                    "dead_player",
                    false,
                    GetTeamColor(playerStatus.Team)));
            }

            _renderer.Scene.MarkParentOctreeDirty(node.Node);
        }

        foreach (var entityId in _liveLinkNodes.Keys.ToArray())
        {
            if (!activeEntityIds.Contains(entityId))
            {
                RemoveLiveLinkNode(entityId);
            }
        }
    }

    private int ResolveLiveLinkObserverSlot(Cs2LiveLinkEntity entity)
    {
        if (entity.ObserverSlot is >= 0 and <= 9)
        {
            _liveLinkObserverSlotsByEntityId[entity.Id] = entity.ObserverSlot;
            return entity.ObserverSlot;
        }

        return _liveLinkObserverSlotsByEntityId.TryGetValue(entity.Id, out var cachedSlot)
            ? cachedSlot
            : -1;
    }

    private LiveLinkModelNode? GetOrCreateLiveLinkNode(int entityId, string modelName)
    {
        if (_renderer?.Scene == null || _fileLoader == null)
            return null;

        if (_liveLinkNodes.TryGetValue(entityId, out var existing)
            && string.Equals(existing.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        RemoveLiveLinkNode(entityId);

        try
        {
            var resource = _fileLoader.LoadFileCompiled(modelName);
            if (resource?.DataBlock is not Model model)
            {
                if (_liveLinkLoggedModelFailures.Add(modelName))
                    LogMessage($"LiveLink model load failed: {modelName}");
                return null;
            }

            var node = new ModelSceneNode(_renderer.Scene, model, isWorldPreview: true, skipAnimations: true)
            {
                LayerName = "HLAE LiveLink",
                Name = modelName
            };
            node.SetRenderMode(GetEffectiveRenderMode());
            _renderer.Scene.Add(node, dynamic: true);
            _renderer.Scene.MarkParentOctreeDirty(node);
            var liveNode = new LiveLinkModelNode(modelName, node);
            _liveLinkNodes[entityId] = liveNode;
            return liveNode;
        }
        catch (Exception ex)
        {
            if (_liveLinkLoggedModelFailures.Add(modelName))
                LogMessage($"LiveLink model load exception for {modelName}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void RemoveLiveLinkNode(int entityId)
    {
        _liveLinkObserverSlotsByEntityId.Remove(entityId);
        if (!_liveLinkNodes.Remove(entityId, out var liveNode))
            return;

        try
        {
            _renderer?.Scene.Remove(liveNode.Node, dynamic: true);
            liveNode.Node.Delete();
        }
        catch (Exception ex)
        {
            LogMessage($"LiveLink node removal failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ClearLiveLinkNodes()
    {
        ClearLiveLinkIconHitCache();
        foreach (var entityId in _liveLinkNodes.Keys.ToArray())
        {
            RemoveLiveLinkNode(entityId);
        }

        _liveLinkIconBillboards.Clear();
        _lastLiveLinkFrameId = uint.MaxValue;
        _liveLinkLoggedMissingSkeletons.Clear();
        _liveLinkLoggedModelFailures.Clear();
    }

    private void RefreshLiveLinkLighting()
    {
        var scene = _renderer?.Scene;
        if (scene == null || _liveLinkNodes.Count == 0)
            return;

        foreach (var liveNode in _liveLinkNodes.Values)
        {
            scene.RefreshLighting(liveNode.Node);
        }
    }

    private void PostSceneLoad(HashSet<string> defaultEnabledLayers)
    {
        if (_renderer == null)
        {
            return;
        }

        _renderer.Scene.EnableOcclusionCulling = _mapHasExternalReferences;
        _renderer.Scene.FogEnabled = false;
        _renderer.Scene.Initialize();
        _renderer.SkyboxScene?.Initialize();

        if (_renderer.Scene.FogInfo.CubeFogActive)
        {
            var cubemapTexture = _renderer.Scene.FogInfo.CubemapFog?.CubemapFogTexture;
            if (cubemapTexture != null)
            {
                _renderer.Textures.RemoveAll(t => t.Slot == ReservedTextureSlots.FogCubeTexture);
                _renderer.Textures.Add(new(ReservedTextureSlots.FogCubeTexture, "g_tFogCubeTexture", cubemapTexture));
            }
        }

        defaultEnabledLayers.Remove("Entities");
        defaultEnabledLayers.Remove("Particles");
        _renderer.Scene.SetEnabledLayers(defaultEnabledLayers);
        ApplyModelVisibility();
        _renderer.Scene.UpdateOctrees();

        ResetCameraToScene();
        _renderer.Camera.SetViewportSize(_renderWidth, _renderHeight);
    }

    private void ResetCameraToScene()
    {
        if (_renderer == null)
        {
            return;
        }

        var first = true;
        var bbox = new AABB();
        foreach (var node in _renderer.Scene.AllNodes)
        {
            bbox = first ? node.BoundingBox : bbox.Union(node.BoundingBox);
            first = false;
        }

        if (!first)
        {
            ResetOrbitToBounds(bbox.Min, bbox.Max);
        }
    }

    private void ResetOrbitToBounds(Vector3 min, Vector3 max)
    {
        _target = (min + max) * 0.5f;
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

    private void ApplyModelVisibility()
    {
        if (_renderer == null)
        {
            return;
        }

        foreach (var node in _renderer.Scene.AllNodes)
        {
            if (node is ModelSceneNode || node is SceneAggregate || node is SceneAggregate.Fragment)
            {
                var isEntity = node.EntityData != null
                    || string.Equals(node.LayerName, "Entities", StringComparison.OrdinalIgnoreCase);
                if (isEntity)
                {
                    node.LayerEnabled = _showEntityModels;
                }
            }
        }
    }

    private bool EnsureOpenGLBindings()
    {
        if (_bindingsLoaded)
        {
            return true;
        }

        var provider = new GLFWBindingsContext();
        OpenTK.Graphics.OpenGL.GL.LoadBindings(provider);
        _bindingsLoaded = true;
        return true;
    }

    private void DrawPins(int width, int height)
    {
        if (_pinVertexCount <= 0 || _pinShaderProgram == 0 || _renderer == null || _mainFramebuffer == null)
        {
            return;
        }

        if (!EnsurePinResources())
        {
            return;
        }

        var framebuffer = _mainFramebuffer;
        var renderer = _renderer;
        if (framebuffer == null || renderer == null)
            return;

        framebuffer.Bind(FramebufferTarget.Framebuffer);
        GL.Viewport(0, 0, width, height);

        var mvp = ToMatrix4(renderer.Camera.ViewProjectionMatrix);
        GL.UseProgram(_pinShaderProgram);
        if (_pinMvpLocation >= 0)
        {
            GL.UniformMatrix4(_pinMvpLocation, false, ref mvp);
        }
        if (_pinLightDirLocation >= 0)
        {
            GL.Uniform3(_pinLightDirLocation, PinLightDir.X, PinLightDir.Y, PinLightDir.Z);
        }
        if (_pinAmbientLocation >= 0)
        {
            GL.Uniform1(_pinAmbientLocation, PinAmbientLight);
        }

        GL.BindVertexArray(_pinVao);
        foreach (var draw in _pinDraws)
        {
            if (_pinColorLocation >= 0)
            {
                GL.Uniform3(_pinColorLocation, draw.Color.X, draw.Color.Y, draw.Color.Z);
            }
            GL.DrawArrays(PrimitiveType.Triangles, draw.Start, draw.Count);
        }
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private void DrawCampathOverlay(int width, int height)
    {
        if (_campathOverlayVertexCount <= 0 || _campathOverlayShaderProgram == 0 || _renderer == null || _mainFramebuffer == null)
        {
            if (!_gizmoVisible)
                return;
        }

        if (!EnsureCampathOverlayResources())
        {
            return;
        }

        var framebuffer = _mainFramebuffer;
        var renderer = _renderer;
        if (framebuffer == null || renderer == null)
            return;

        framebuffer.Bind(FramebufferTarget.Framebuffer);
        GL.Viewport(0, 0, width, height);

        var mvp = ToMatrix4(renderer.Camera.ViewProjectionMatrix);
        GL.UseProgram(_campathOverlayShaderProgram);
        if (_campathOverlayMvpLocation >= 0)
        {
            GL.UniformMatrix4(_campathOverlayMvpLocation, false, ref mvp);
        }

        if (_campathOverlayVertexCount > 0)
        {
            var cullEnabled = GL.IsEnabled(EnableCap.CullFace);
            if (cullEnabled)
                GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_campathOverlayVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _campathOverlayVertexCount);
            GL.BindVertexArray(0);
            if (cullEnabled)
                GL.Enable(EnableCap.CullFace);
        }

        if (_gizmoVisible)
        {
            if (EnsureGizmoResources())
            {
                UpdateGizmoVertices();
                if (_gizmoVertexCount > 0)
                {
                    GL.BindVertexArray(_gizmoVao);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, _gizmoVertexCount);
                    GL.BindVertexArray(0);
                }
            }
        }
        GL.UseProgram(0);

    }

    private void AddPinLabels(int width, int height)
    {
        if (_textRenderer == null || _renderer == null || _pinLabels.Count == 0)
        {
            lock (_labelLock)
            {
                _labelHitCache = new List<PinLabel>();
            }
            return;
        }

        const float labelScale = 16f;
        var projected = new List<PinLabel>(_pinLabels.Count);
        var camera = _renderer.Camera;
        lock (_pinLock)
        {
            foreach (var label in _pinLabels)
            {
                if (string.IsNullOrEmpty(label.Text))
                {
                    continue;
                }

                if (TryProjectToScreen(label.World, camera, width, height, out var screen))
                {
                    label.ScreenX = screen.X;
                    label.ScreenY = screen.Y;
                    projected.Add(label);
                }

                _textRenderer.AddTextBillboard(label.World, new TextRenderer.TextRenderRequest
                {
                    Text = label.Text,
                    Scale = labelScale,
                    Color = label.Color,
                    CenterHorizontal = true,
                    CenterVertical = true,
                }, _renderer.Camera, fixedScale: true);
            }
        }

        lock (_labelLock)
        {
            _labelHitCache = projected;
        }
    }

    private void DrawLiveLinkIcons(int width, int height)
    {
        if (!_liveLinkItemIconsEnabledCached || _renderer == null || _liveLinkIconBillboards.Count == 0)
        {
            ClearLiveLinkIconHitCache();
            return;
        }

        if (!EnsureLiveLinkIconResources())
        {
            ClearLiveLinkIconHitCache();
            return;
        }

        var camera = _renderer.Camera;
        var iconHits = new List<LiveLinkIconHit>(_liveLinkIconBillboards.Count);
        var blendEnabled = GL.IsEnabled(EnableCap.Blend);
        var depthEnabled = GL.IsEnabled(EnableCap.DepthTest);
        var cullEnabled = GL.IsEnabled(EnableCap.CullFace);
        var previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        var previousVertexArray = GL.GetInteger(GetPName.VertexArrayBinding);
        var previousActiveTexture = GL.GetInteger(GetPName.ActiveTexture);
        var previousTexture = GL.GetInteger(GetPName.TextureBinding2D);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);
        if (cullEnabled)
            GL.Disable(EnableCap.CullFace);

        GL.UseProgram(_liveLinkIconShaderProgram);
        GL.Uniform1(_liveLinkIconSamplerLocation, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindVertexArray(_liveLinkIconVao);

        foreach (var billboard in _liveLinkIconBillboards)
        {
            var texture = GetLiveLinkIconTexture(billboard.IconKey);
            if (texture == null)
                continue;

            if (!TryProjectToScreen(billboard.World, camera, width, height, out var screen))
                continue;

            if (_liveLinkIconTintLocation >= 0)
            {
                GL.Uniform3(_liveLinkIconTintLocation, billboard.Tint.X, billboard.Tint.Y, billboard.Tint.Z);
            }

            var iconHeight = billboard.Projectile ? 30f : 28f;
            var iconWidth = Math.Max(20f, iconHeight * texture.Width / Math.Max(1f, texture.Height));
            var x0 = (float)screen.X - iconWidth * 0.5f;
            var x1 = (float)screen.X + iconWidth * 0.5f;
            var y0 = (float)screen.Y - iconHeight * 0.5f;
            var y1 = (float)screen.Y + iconHeight * 0.5f;
            iconHits.Add(new LiveLinkIconHit(billboard.World, x0, y0, x1, y1));

            Span<float> vertices =
            [
                ToNdcX(x0, width), ToNdcY(y0, height), 0f, 0f,
                ToNdcX(x1, width), ToNdcY(y0, height), 1f, 0f,
                ToNdcX(x1, width), ToNdcY(y1, height), 1f, 1f,
                ToNdcX(x0, width), ToNdcY(y0, height), 0f, 0f,
                ToNdcX(x1, width), ToNdcY(y1, height), 1f, 1f,
                ToNdcX(x0, width), ToNdcY(y1, height), 0f, 1f,
            ];

            GL.BindTexture(TextureTarget.Texture2D, texture.Handle);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _liveLinkIconVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices.ToArray(), BufferUsageHint.StreamDraw);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        GL.BindTexture(TextureTarget.Texture2D, previousTexture);
        GL.ActiveTexture((TextureUnit)previousActiveTexture);
        GL.BindVertexArray(previousVertexArray);
        GL.UseProgram(previousProgram);
        if (depthEnabled)
            GL.Enable(EnableCap.DepthTest);
        else
            GL.Disable(EnableCap.DepthTest);
        if (cullEnabled)
            GL.Enable(EnableCap.CullFace);
        if (!blendEnabled)
            GL.Disable(EnableCap.Blend);

        lock (_liveLinkIconHitLock)
        {
            _liveLinkIconHitCache = iconHits;
        }

        static float ToNdcX(float x, int w) => 2f * x / Math.Max(1, w) - 1f;
        static float ToNdcY(float y, int h) => 1f - 2f * y / Math.Max(1, h);
    }

    private void ClearLiveLinkIconHitCache()
    {
        lock (_liveLinkIconHitLock)
        {
            if (_liveLinkIconHitCache.Count != 0)
                _liveLinkIconHitCache = new List<LiveLinkIconHit>();
        }
    }

    private bool EnsureLiveLinkIconResources()
    {
        if (_liveLinkIconShaderProgram == 0)
        {
            _liveLinkIconShaderProgram = CreateLiveLinkIconShaderProgram();
            if (_liveLinkIconShaderProgram == 0)
                return false;

            _liveLinkIconSamplerLocation = GL.GetUniformLocation(_liveLinkIconShaderProgram, "uTexture");
            _liveLinkIconTintLocation = GL.GetUniformLocation(_liveLinkIconShaderProgram, "uTint");
        }

        if (_liveLinkIconVao == 0)
        {
            _liveLinkIconVao = GL.GenVertexArray();
            _liveLinkIconVbo = GL.GenBuffer();
            GL.BindVertexArray(_liveLinkIconVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _liveLinkIconVbo);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
            GL.BindVertexArray(0);
        }

        return true;
    }

    private RenderTexture? GetLiveLinkIconTexture(string iconKey)
    {
        if (_liveLinkIconTextures.TryGetValue(iconKey, out var cached))
            return cached;

        try
        {
            var uri = GetLiveLinkIconUri(iconKey);
            using var stream = AssetLoader.Open(uri);
            using var svg = new SKSvg();
            svg.Load(stream);
            if (svg.Picture == null)
            {
                _liveLinkIconTextures[iconKey] = null;
                return null;
            }

            using var bitmap = RasterizeSvg(svg, 128);
            var texture = MaterialLoader.LoadBitmapTexture(bitmap);
            texture.SetWrapMode(TextureWrapMode.ClampToEdge);
            texture.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
            _liveLinkIconTextures[iconKey] = texture;
            return texture;
        }
        catch (Exception ex)
        {
            if (_liveLinkLoggedMissingIcons.Add(iconKey))
                LogMessage($"LiveLink icon load failed for {iconKey}: {ex.GetType().Name}: {ex.Message}");
            _liveLinkIconTextures[iconKey] = null;
            return null;
        }
    }

    private static Uri GetLiveLinkIconUri(string iconKey)
    {
        return iconKey switch
        {
            "dead_player" => new Uri("avares://HlaeObsTools/Assets/hud/icons/dead.svg"),
            "planted_c4" => new Uri("avares://HlaeObsTools/Assets/hud/icons/planted-bomb.svg"),
            _ => new Uri($"avares://HlaeObsTools/Assets/hud/weapons/{iconKey}.svg")
        };
    }

    private static SKBitmap RasterizeSvg(SKSvg svg, int size)
    {
        var bounds = svg.Picture!.CullRect;
        var aspect = bounds.Width > 0f && bounds.Height > 0f ? bounds.Width / bounds.Height : 1f;
        var width = Math.Max(1, (int)MathF.Ceiling(size * aspect));
        var info = new SKImageInfo(width, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        if (bounds.Width <= 0f || bounds.Height <= 0f)
            return bitmap;

        var scale = size / bounds.Height * 0.86f;
        var tx = (width - bounds.Width * scale) * 0.5f - bounds.Left * scale;
        var ty = (size - bounds.Height * scale) * 0.5f - bounds.Top * scale;
        canvas.Translate(tx, ty);
        canvas.Scale(scale);
        canvas.DrawPicture(svg.Picture);
        canvas.Flush();
        return bitmap;
    }

    private static bool ShouldDrawLiveLinkItemIcon(Cs2LiveLinkEntity entity, string iconKey)
    {
        return entity.Projectile || iconKey == "planted_c4" || entity.OwnerId < 0;
    }

    private bool ShouldDrawLiveLinkIconCategory(Cs2LiveLinkEntity entity, string iconKey)
    {
        if (entity.Projectile)
            return _liveLinkProjectileIconsEnabledCached;

        if (IsLiveLinkObjectiveIcon(iconKey))
            return _liveLinkObjectiveIconsEnabledCached;

        if (IsLiveLinkGrenadeIcon(iconKey))
            return _liveLinkGrenadeIconsEnabledCached;

        return _liveLinkWeaponIconsEnabledCached;
    }

    private static bool IsLiveLinkObjectiveIcon(string iconKey)
    {
        return iconKey is "defuser" or "c4" or "planted_c4";
    }

    private static bool IsLiveLinkGrenadeIcon(string iconKey)
    {
        return iconKey is "flashbang"
            or "smokegrenade"
            or "hegrenade"
            or "incgrenade"
            or "molotov"
            or "decoy"
            or "tagrenade"
            or "breachcharge"
            or "breachcharge_projectile"
            or "bumpmine";
    }

    private static bool TryGetLiveLinkBoneWorldPosition(Cs2LiveLinkEntity entity, Cs2LiveLinkSkeleton skeleton, out Vector3 world)
    {
        world = default;
        if (!entity.HasBones || entity.LocalBoneTransforms.Count == 0 || skeleton.BoneNames.Count == 0)
            return false;

        var boneIndex = FindLiveLinkAnchorBoneIndex(skeleton);
        if (boneIndex < 0 || boneIndex >= entity.LocalBoneTransforms.Count)
            return false;

        var worldTransforms = new Matrix4x4[entity.LocalBoneTransforms.Count];
        var resolved = new bool[entity.LocalBoneTransforms.Count];
        var boneWorld = ResolveLiveLinkBoneWorldTransform(boneIndex, entity, skeleton, worldTransforms, resolved);
        world = new Vector3(boneWorld.M41, boneWorld.M42, boneWorld.M43);
        return true;
    }

    private static Matrix4x4 ResolveLiveLinkBoneWorldTransform(
        int boneIndex,
        Cs2LiveLinkEntity entity,
        Cs2LiveLinkSkeleton skeleton,
        Matrix4x4[] worldTransforms,
        bool[] resolved)
    {
        if (boneIndex < 0 || boneIndex >= entity.LocalBoneTransforms.Count)
            return entity.Transform;

        if (resolved[boneIndex])
            return worldTransforms[boneIndex];

        var local = entity.LocalBoneTransforms[boneIndex];
        var parentIndex = boneIndex < skeleton.BoneParents.Count ? skeleton.BoneParents[boneIndex] : -1;
        var parentWorld = parentIndex >= 0 && parentIndex < entity.LocalBoneTransforms.Count
            ? ResolveLiveLinkBoneWorldTransform(parentIndex, entity, skeleton, worldTransforms, resolved)
            : entity.Transform;

        var boneWorld = local * parentWorld;
        worldTransforms[boneIndex] = boneWorld;
        resolved[boneIndex] = true;
        return boneWorld;
    }

    private static int FindLiveLinkAnchorBoneIndex(Cs2LiveLinkSkeleton skeleton)
    {
        var exactCandidates = new[]
        {
            "pelvis",
            "valvebiped.bip01_pelvis",
            "bip01_pelvis"
        };

        foreach (var candidate in exactCandidates)
        {
            for (var i = 0; i < skeleton.BoneNames.Count; ++i)
            {
                if (string.Equals(skeleton.BoneNames[i], candidate, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        for (var i = 0; i < skeleton.BoneNames.Count; ++i)
        {
            if (skeleton.BoneNames[i].Contains("pelvis", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        var fallbackCandidates = new[] { "spine_0", "spine", "root" };
        foreach (var candidate in fallbackCandidates)
        {
            for (var i = 0; i < skeleton.BoneNames.Count; ++i)
            {
                if (string.Equals(skeleton.BoneNames[i], candidate, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return -1;
    }

    private static string? TryGetLiveLinkIconKey(string modelName, Cs2LiveLinkEntity entity)
    {
        var path = modelName.Replace('\\', '/').ToLowerInvariant();
        var clientClassName = entity.ClientClassName.ToLowerInvariant();

        if (path.Contains("/defuser/") || path.EndsWith("/defuser.vmdl", StringComparison.Ordinal))
            return "defuser";
        if (clientClassName == "c_plantedc4")
            return "planted_c4";
        if (path.Contains("/c4/") || path.Contains("weapon_c4") || path.Contains("planted_c4"))
            return "c4";
        if (path.Contains("flashbang"))
            return "flashbang";
        if (path.Contains("smokegrenade") || path.Contains("smoke_grenade"))
            return "smokegrenade";
        if (path.Contains("hegrenade") || path.Contains("fraggrenade") || path.Contains("frag_grenade"))
            return "hegrenade";
        if (path.Contains("incgrenade") || path.Contains("incendiarygrenade"))
            return "incgrenade";
        if (path.Contains("molotov") || path.Contains("firebomb"))
            return "molotov";
        if (path.Contains("decoy"))
            return "decoy";
        if (path.Contains("tagrenade"))
            return "tagrenade";
        if (path.Contains("breachcharge"))
            return entity.Projectile ? "breachcharge_projectile" : "breachcharge";
        if (path.Contains("bumpmine"))
            return "bumpmine";
        if (path.Contains("healthshot"))
            return "healthshot";
        if (path.Contains("taser"))
            return "taser";

        if (!path.Contains("weapons/models/", StringComparison.Ordinal))
            return null;

        var file = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(file))
            return null;

        foreach (var prefix in LiveLinkIconFilePrefixes)
        {
            if (file.StartsWith(prefix, StringComparison.Ordinal))
            {
                file = file[prefix.Length..];
                break;
            }
        }

        if (file == "m4a4")
            return "m4a1";

        return string.IsNullOrWhiteSpace(file) ? null : file;
    }

    private int CreateLiveLinkIconShaderProgram()
    {
        const string Vertex330 = @"#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aTex;
out vec2 vTex;
void main()
{
    vTex = aTex;
    gl_Position = vec4(aPos, 0.0, 1.0);
}";
        const string Fragment330 = @"#version 330 core
in vec2 vTex;
out vec4 FragColor;
uniform sampler2D uTexture;
uniform vec3 uTint;
void main()
{
    vec4 tex = texture(uTexture, vTex);
    FragColor = vec4(tex.rgb * uTint, tex.a);
}";
        const string Vertex120 = @"#version 120
attribute vec2 aPos;
attribute vec2 aTex;
varying vec2 vTex;
void main()
{
    vTex = aTex;
    gl_Position = vec4(aPos, 0.0, 1.0);
}";
        const string Fragment120 = @"#version 120
varying vec2 vTex;
uniform sampler2D uTexture;
uniform vec3 uTint;
void main()
{
    vec4 tex = texture2D(uTexture, vTex);
    gl_FragColor = vec4(tex.rgb * uTint, tex.a);
}";
        const string VertexEs300 = @"#version 300 es
precision mediump float;
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aTex;
out vec2 vTex;
void main()
{
    vTex = aTex;
    gl_Position = vec4(aPos, 0.0, 1.0);
}";
        const string FragmentEs300 = @"#version 300 es
precision mediump float;
in vec2 vTex;
out vec4 FragColor;
uniform sampler2D uTexture;
uniform vec3 uTint;
void main()
{
    vec4 tex = texture(uTexture, vTex);
    FragColor = vec4(tex.rgb * uTint, tex.a);
}";
        const string VertexEs100 = @"#version 100
precision mediump float;
attribute vec2 aPos;
attribute vec2 aTex;
varying vec2 vTex;
void main()
{
    vTex = aTex;
    gl_Position = vec4(aPos, 0.0, 1.0);
}";
        const string FragmentEs100 = @"#version 100
precision mediump float;
varying vec2 vTex;
uniform sampler2D uTexture;
uniform vec3 uTint;
void main()
{
    vec4 tex = texture2D(uTexture, vTex);
    gl_FragColor = vec4(tex.rgb * uTint, tex.a);
}";

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
            var vertexShader = CompilePinShader(ShaderType.VertexShader, variant.VertexSource, out var vertexError);
            if (vertexShader == 0)
            {
                if (!string.IsNullOrWhiteSpace(vertexError))
                    errors.Add($"Vertex {variant.Name}: {vertexError}");
                continue;
            }

            var fragmentShader = CompilePinShader(ShaderType.FragmentShader, variant.FragmentSource, out var fragmentError);
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
                GL.BindAttribLocation(program, 1, "aTex");
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
                LogMessage($"LiveLink icon shader: {variant.Name} (GL {version} | GLSL {glsl})");
                return program;
            }
        }

        if (errors.Count > 0)
            LogMessage($"LiveLink icon shader compile failed ({version}): {string.Join(" | ", errors)}");
        else
            LogMessage($"LiveLink icon shader compile failed ({version}).");

        return 0;
    }

    private bool EnsurePinResources()
    {
        if (_pinShaderProgram == 0)
        {
            _pinShaderProgram = CreatePinShaderProgram();
            if (_pinShaderProgram == 0)
            {
                return false;
            }

            _pinMvpLocation = GL.GetUniformLocation(_pinShaderProgram, "uMvp");
            _pinColorLocation = GL.GetUniformLocation(_pinShaderProgram, "uColor");
            _pinLightDirLocation = GL.GetUniformLocation(_pinShaderProgram, "uLightDir");
            _pinAmbientLocation = GL.GetUniformLocation(_pinShaderProgram, "uAmbient");
        }

        return true;
    }

    private void DisposePinResources()
    {
        if (_pinVao != 0)
        {
            GL.DeleteVertexArray(_pinVao);
            _pinVao = 0;
        }
        if (_pinVbo != 0)
        {
            GL.DeleteBuffer(_pinVbo);
            _pinVbo = 0;
        }
        if (_pinShaderProgram != 0)
        {
            GL.DeleteProgram(_pinShaderProgram);
            _pinShaderProgram = 0;
        }
        _pinVertexCount = 0;
        _pinDraws.Clear();
    }

    private void DisposeLiveLinkIconResources()
    {
        if (_liveLinkIconVao != 0)
        {
            GL.DeleteVertexArray(_liveLinkIconVao);
            _liveLinkIconVao = 0;
        }
        if (_liveLinkIconVbo != 0)
        {
            GL.DeleteBuffer(_liveLinkIconVbo);
            _liveLinkIconVbo = 0;
        }
        if (_liveLinkIconShaderProgram != 0)
        {
            GL.DeleteProgram(_liveLinkIconShaderProgram);
            _liveLinkIconShaderProgram = 0;
        }

        foreach (var texture in _liveLinkIconTextures.Values)
        {
            texture?.Delete();
        }
        _liveLinkIconTextures.Clear();
        _liveLinkIconBillboards.Clear();
        _liveLinkIconSamplerLocation = -1;
    }

    private bool EnsureCampathOverlayResources()
    {
        if (_campathOverlayShaderProgram == 0)
        {
            _campathOverlayShaderProgram = CreateCampathOverlayShaderProgram();
            if (_campathOverlayShaderProgram == 0)
            {
                return false;
            }

            _campathOverlayMvpLocation = GL.GetUniformLocation(_campathOverlayShaderProgram, "uMvp");
        }

        return true;
    }

    private void DisposeCampathOverlayResources()
    {
        if (_campathOverlayVao != 0)
        {
            GL.DeleteVertexArray(_campathOverlayVao);
            _campathOverlayVao = 0;
        }
        if (_campathOverlayVbo != 0)
        {
            GL.DeleteBuffer(_campathOverlayVbo);
            _campathOverlayVbo = 0;
        }
        if (_campathOverlayShaderProgram != 0)
        {
            GL.DeleteProgram(_campathOverlayShaderProgram);
            _campathOverlayShaderProgram = 0;
        }
        _campathOverlayVertexCount = 0;
    }

    private bool EnsureGizmoResources()
    {
        if (_campathOverlayShaderProgram == 0)
        {
            if (!EnsureCampathOverlayResources())
                return false;
        }

        return true;
    }

    private void DisposeGizmoResources()
    {
        if (_gizmoVao != 0)
        {
            GL.DeleteVertexArray(_gizmoVao);
            _gizmoVao = 0;
        }
        if (_gizmoVbo != 0)
        {
            GL.DeleteBuffer(_gizmoVbo);
            _gizmoVbo = 0;
        }
        _gizmoVertexCount = 0;
    }

    private void UpdateGizmoVertices()
    {
        if (_renderer == null || !_gizmoVisible)
            return;

        var camera = _renderer.Camera;
        var distance = Vector3.Distance(camera.Location, _gizmoPosition);
        var scale = Math.Clamp(distance * 0.12f, 24f, 120f);

        if (_gizmoDirty ||
            MathF.Abs(scale - _gizmoLastScale) > 0.01f ||
            Vector3.DistanceSquared(_gizmoPosition, _gizmoLastPosition) > 0.01f ||
            Quaternion.Dot(_gizmoRotation, _gizmoLastRotation) < 0.999f ||
            _gizmoUseLocalSpace != _gizmoLastLocal)
        {
            _gizmoDirty = false;
            _gizmoLastScale = scale;
            _gizmoLastPosition = _gizmoPosition;
            _gizmoLastRotation = _gizmoRotation;
            _gizmoLastLocal = _gizmoUseLocalSpace;

            var verts = BuildGizmoVertices(scale);
            UploadGizmoVertices(verts);
        }
    }

    private List<CampathOverlayVertex> BuildGizmoVertices(float scale)
    {
        var vertices = new List<CampathOverlayVertex>();
        var axisLength = scale;
        var shaftLength = axisLength * 0.75f;
        var coneLength = axisLength * 0.25f;
        var shaftRadius = scale * 0.04f;
        var coneRadius = scale * 0.08f;
        var ringRadius = scale * 0.75f;
        var ringThickness = scale * 0.03f;

        var (axisX, axisY, axisZ) = GetGizmoAxes();
        AppendAxis(vertices, axisX, new Vector3(0.95f, 0.2f, 0.2f), shaftLength, coneLength, shaftRadius, coneRadius,
            _gizmoHover is GizmoMode.TranslateX);
        AppendAxis(vertices, axisY, new Vector3(0.2f, 0.95f, 0.2f), shaftLength, coneLength, shaftRadius, coneRadius,
            _gizmoHover is GizmoMode.TranslateY);
        AppendAxis(vertices, axisZ, new Vector3(0.2f, 0.5f, 0.95f), shaftLength, coneLength, shaftRadius, coneRadius,
            _gizmoHover is GizmoMode.TranslateZ);

        AppendRing(vertices, axisX, new Vector3(0.9f, 0.4f, 0.4f), ringRadius, ringThickness,
            _gizmoHover is GizmoMode.RotateX);
        AppendRing(vertices, axisY, new Vector3(0.4f, 0.9f, 0.4f), ringRadius, ringThickness,
            _gizmoHover is GizmoMode.RotateY);
        AppendRing(vertices, axisZ, new Vector3(0.4f, 0.6f, 0.95f), ringRadius, ringThickness,
            _gizmoHover is GizmoMode.RotateZ);

        return vertices;
    }

    private void UploadGizmoVertices(List<CampathOverlayVertex> vertices)
    {
        if (vertices.Count == 0)
        {
            _gizmoVertexCount = 0;
            return;
        }

        var data = new float[vertices.Count * 6];
        var idx = 0;
        foreach (var vertex in vertices)
        {
            data[idx++] = vertex.Position.X;
            data[idx++] = vertex.Position.Y;
            data[idx++] = vertex.Position.Z;
            data[idx++] = vertex.Color.X;
            data[idx++] = vertex.Color.Y;
            data[idx++] = vertex.Color.Z;
        }

        if (_gizmoVao != 0)
            GL.DeleteVertexArray(_gizmoVao);
        if (_gizmoVbo != 0)
            GL.DeleteBuffer(_gizmoVbo);

        _gizmoVao = GL.GenVertexArray();
        _gizmoVbo = GL.GenBuffer();

        GL.BindVertexArray(_gizmoVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _gizmoVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.DynamicDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        GL.BindVertexArray(0);
        _gizmoVertexCount = vertices.Count;
    }


    private (Vector3 x, Vector3 y, Vector3 z) GetGizmoAxes()
    {
        if (_gizmoUseLocalSpace)
        {
            var rot = _gizmoRotation;
            return (
                Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rot)),
                Vector3.Normalize(Vector3.Transform(Vector3.UnitY, rot)),
                Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rot))
            );
        }

        return (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    }

    private void AppendAxis(List<CampathOverlayVertex> vertices, Vector3 axis, Vector3 color, float shaftLength, float coneLength, float shaftRadius, float coneRadius, bool highlight)
    {
        if (highlight)
            color = Vector3.Clamp(color * 1.4f, Vector3.Zero, Vector3.One);

        var origin = _gizmoPosition;
        axis = Vector3.Normalize(axis);
        var (u, v) = GetOrthonormalBasis(axis);
        var shaftEnd = origin + axis * shaftLength;
        var tip = shaftEnd + axis * coneLength;

        AppendCylinder(vertices, origin, shaftEnd, axis, u, v, shaftRadius, color);
        AppendCone(vertices, shaftEnd, tip, axis, u, v, coneRadius, color);
    }

    private void AppendRing(List<CampathOverlayVertex> vertices, Vector3 axis, Vector3 color, float radius, float thickness, bool highlight)
    {
        if (highlight)
            color = Vector3.Clamp(color * 1.4f, Vector3.Zero, Vector3.One);

        axis = Vector3.Normalize(axis);
        var (u, v) = GetOrthonormalBasis(axis);
        AppendTorus(vertices, _gizmoPosition, axis, u, v, radius, thickness, color);
    }

    private static void AppendCylinder(List<CampathOverlayVertex> vertices, Vector3 start, Vector3 end, Vector3 axis, Vector3 u, Vector3 v, float radius, Vector3 color)
    {
        const int segments = 16;
        for (var i = 0; i < segments; i++)
        {
            var t0 = i / (float)segments * MathF.PI * 2f;
            var t1 = (i + 1) / (float)segments * MathF.PI * 2f;
            var r0 = u * MathF.Cos(t0) * radius + v * MathF.Sin(t0) * radius;
            var r1 = u * MathF.Cos(t1) * radius + v * MathF.Sin(t1) * radius;

            var p0 = start + r0;
            var p1 = start + r1;
            var p2 = end + r1;
            var p3 = end + r0;

            vertices.Add(new CampathOverlayVertex(p0, color));
            vertices.Add(new CampathOverlayVertex(p1, color));
            vertices.Add(new CampathOverlayVertex(p2, color));

            vertices.Add(new CampathOverlayVertex(p0, color));
            vertices.Add(new CampathOverlayVertex(p2, color));
            vertices.Add(new CampathOverlayVertex(p3, color));
        }
    }

    private static void AppendCone(List<CampathOverlayVertex> vertices, Vector3 baseCenter, Vector3 tip, Vector3 axis, Vector3 u, Vector3 v, float radius, Vector3 color)
    {
        const int segments = 16;
        for (var i = 0; i < segments; i++)
        {
            var t0 = i / (float)segments * MathF.PI * 2f;
            var t1 = (i + 1) / (float)segments * MathF.PI * 2f;
            var r0 = u * MathF.Cos(t0) * radius + v * MathF.Sin(t0) * radius;
            var r1 = u * MathF.Cos(t1) * radius + v * MathF.Sin(t1) * radius;

            var p0 = baseCenter + r0;
            var p1 = baseCenter + r1;

            vertices.Add(new CampathOverlayVertex(p0, color));
            vertices.Add(new CampathOverlayVertex(p1, color));
            vertices.Add(new CampathOverlayVertex(tip, color));
        }
    }

    private static void AppendTorus(List<CampathOverlayVertex> vertices, Vector3 center, Vector3 axis, Vector3 u, Vector3 v, float radius, float thickness, Vector3 color)
    {
        const int majorSegments = 32;
        const int minorSegments = 12;

        for (var i = 0; i < majorSegments; i++)
        {
            var a0 = i / (float)majorSegments * MathF.PI * 2f;
            var a1 = (i + 1) / (float)majorSegments * MathF.PI * 2f;

            var cos0 = MathF.Cos(a0);
            var sin0 = MathF.Sin(a0);
            var cos1 = MathF.Cos(a1);
            var sin1 = MathF.Sin(a1);

            var ringCenter0 = center + (u * cos0 + v * sin0) * radius;
            var ringCenter1 = center + (u * cos1 + v * sin1) * radius;
            var ringDir0 = Vector3.Normalize(u * cos0 + v * sin0);
            var ringDir1 = Vector3.Normalize(u * cos1 + v * sin1);

            var ringU0 = Vector3.Normalize(Vector3.Cross(axis, ringDir0));
            var ringU1 = Vector3.Normalize(Vector3.Cross(axis, ringDir1));
            var ringV0 = Vector3.Normalize(Vector3.Cross(ringDir0, ringU0));
            var ringV1 = Vector3.Normalize(Vector3.Cross(ringDir1, ringU1));

            for (var j = 0; j < minorSegments; j++)
            {
                var b0 = j / (float)minorSegments * MathF.PI * 2f;
                var b1 = (j + 1) / (float)minorSegments * MathF.PI * 2f;

                var minor0 = ringU0 * MathF.Cos(b0) * thickness + ringV0 * MathF.Sin(b0) * thickness;
                var minor1 = ringU0 * MathF.Cos(b1) * thickness + ringV0 * MathF.Sin(b1) * thickness;
                var minor2 = ringU1 * MathF.Cos(b1) * thickness + ringV1 * MathF.Sin(b1) * thickness;
                var minor3 = ringU1 * MathF.Cos(b0) * thickness + ringV1 * MathF.Sin(b0) * thickness;

                var p0 = ringCenter0 + minor0;
                var p1 = ringCenter0 + minor1;
                var p2 = ringCenter1 + minor2;
                var p3 = ringCenter1 + minor3;

                vertices.Add(new CampathOverlayVertex(p0, color));
                vertices.Add(new CampathOverlayVertex(p1, color));
                vertices.Add(new CampathOverlayVertex(p2, color));

                vertices.Add(new CampathOverlayVertex(p0, color));
                vertices.Add(new CampathOverlayVertex(p2, color));
                vertices.Add(new CampathOverlayVertex(p3, color));
            }
        }
    }

    private static (Vector3 u, Vector3 v) GetOrthonormalBasis(Vector3 axis)
    {
        var up = MathF.Abs(Vector3.Dot(axis, Vector3.UnitZ)) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        var u = Vector3.Normalize(Vector3.Cross(axis, up));
        var v = Vector3.Normalize(Vector3.Cross(axis, u));
        return (u, v);
    }

    private void RebuildCampathOverlay()
    {
        _campathOverlayDirty = false;

        CampathOverlayData? data;
        lock (_campathOverlayLock)
        {
            data = _campathOverlayData;
        }

        if (data == null || data.Vertices.Count == 0)
        {
            _campathOverlayVertexCount = 0;
            if (_campathOverlayVao != 0)
            {
                GL.DeleteVertexArray(_campathOverlayVao);
                _campathOverlayVao = 0;
            }
            if (_campathOverlayVbo != 0)
            {
                GL.DeleteBuffer(_campathOverlayVbo);
                _campathOverlayVbo = 0;
            }
            return;
        }

        if (!EnsureCampathOverlayResources())
        {
            return;
        }

        var vertices = BuildCampathOverlayTriangles(data.Vertices);
        if (vertices.Count == 0)
        {
            _campathOverlayVertexCount = 0;
            return;
        }

        var vertexData = new float[vertices.Count * 6];
        var idx = 0;
        foreach (var vertex in vertices)
        {
            vertexData[idx++] = vertex.Position.X;
            vertexData[idx++] = vertex.Position.Y;
            vertexData[idx++] = vertex.Position.Z;
            vertexData[idx++] = vertex.Color.X;
            vertexData[idx++] = vertex.Color.Y;
            vertexData[idx++] = vertex.Color.Z;
        }

        if (_campathOverlayVao != 0)
        {
            GL.DeleteVertexArray(_campathOverlayVao);
        }
        if (_campathOverlayVbo != 0)
        {
            GL.DeleteBuffer(_campathOverlayVbo);
        }

        _campathOverlayVao = GL.GenVertexArray();
        _campathOverlayVbo = GL.GenBuffer();
        GL.BindVertexArray(_campathOverlayVao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _campathOverlayVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertexData.Length * sizeof(float), vertexData, BufferUsageHint.DynamicDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        GL.BindVertexArray(0);

        _campathOverlayVertexCount = vertices.Count;
    }

    private void UpdateCampathOverlayCameraState()
    {
        if (_renderer == null || _rendererContext == null)
            return;

        CampathOverlayData? data;
        lock (_campathOverlayLock)
        {
            data = _campathOverlayData;
        }

        if (data == null || data.Vertices.Count == 0)
            return;

        var camera = _renderer.Camera;
        var pos = camera.Location;
        var forward = camera.Forward;
        var up = camera.Up;
        var fov = _rendererContext.FieldOfView;
        var height = _renderHeight;

        if (VectorsChanged(_campathOverlayCameraPos, pos) ||
            VectorsChanged(_campathOverlayCameraForward, forward) ||
            VectorsChanged(_campathOverlayCameraUp, up) ||
            MathF.Abs(_campathOverlayCameraFov - fov) > 0.01f ||
            _campathOverlayCameraHeight != height)
        {
            _campathOverlayCameraPos = pos;
            _campathOverlayCameraForward = forward;
            _campathOverlayCameraUp = up;
            _campathOverlayCameraFov = fov;
            _campathOverlayCameraHeight = height;
            _campathOverlayDirty = true;
        }
    }

    private static bool VectorsChanged(Vector3 a, Vector3 b)
    {
        return (a - b).LengthSquared() > 0.0001f;
    }

    private List<CampathOverlayVertex> BuildCampathOverlayTriangles(IReadOnlyList<CampathOverlayVertex> lineVertices)
    {
        var triangles = new List<CampathOverlayVertex>();
        if (lineVertices.Count < 2 || _renderer == null || _rendererContext == null || _renderHeight <= 0)
            return triangles;

        var camera = _renderer.Camera;
        var fovY = DegToRad(_rendererContext.FieldOfView);
        var tanY = MathF.Tan(fovY * 0.5f);
        if (tanY <= 1e-6f)
            return triangles;

        var halfPixel = CampathOverlayLineThicknessPx * 0.5f;
        var height = Math.Max(_renderHeight, 1);

        var count = lineVertices.Count - (lineVertices.Count % 2);
        for (var i = 0; i < count; i += 2)
        {
            var v0 = lineVertices[i];
            var v1 = lineVertices[i + 1];
            var p0 = v0.Position;
            var p1 = v1.Position;
            var dir = p1 - p0;
            var lenSq = dir.LengthSquared();
            if (lenSq <= 1e-6f)
                continue;

            var mid = (p0 + p1) * 0.5f;
            var toMid = mid - camera.Location;
            var z = MathF.Max(0.01f, Vector3.Dot(camera.Forward, toMid));
            var worldPerPixel = 2f * z * tanY / height;
            var halfWidth = worldPerPixel * halfPixel;

            var dirNorm = dir / MathF.Sqrt(lenSq);
            var perp = Vector3.Cross(dirNorm, camera.Forward);
            if (perp.LengthSquared() < 1e-6f)
                perp = camera.Right;
            perp = Vector3.Normalize(perp);
            var offset = perp * halfWidth;

            var p0a = p0 - offset;
            var p0b = p0 + offset;
            var p1a = p1 - offset;
            var p1b = p1 + offset;

            triangles.Add(new CampathOverlayVertex(p0a, v0.Color));
            triangles.Add(new CampathOverlayVertex(p0b, v0.Color));
            triangles.Add(new CampathOverlayVertex(p1b, v1.Color));

            triangles.Add(new CampathOverlayVertex(p0a, v0.Color));
            triangles.Add(new CampathOverlayVertex(p1b, v1.Color));
            triangles.Add(new CampathOverlayVertex(p1a, v1.Color));
        }

        return triangles;
    }

    private void RebuildPins()
    {
        _pinsDirty = false;
        lock (_pinLock)
        {
            if (_pins.Count == 0)
            {
                _pinVertexCount = 0;
                _pinDraws.Clear();
                if (_pinVao != 0)
                {
                    GL.DeleteVertexArray(_pinVao);
                    _pinVao = 0;
                }
                if (_pinVbo != 0)
                {
                    GL.DeleteBuffer(_pinVbo);
                    _pinVbo = 0;
                }
                return;
            }
        }

        if (!EnsurePinResources())
        {
            return;
        }

        float pinScale;
        if (Dispatcher.UIThread.CheckAccess())
        {
            pinScale = PinScale;
        }
        else
        {
            pinScale = Dispatcher.UIThread.InvokeAsync(() => PinScale).GetAwaiter().GetResult();
        }

        List<PinRenderData> pinsSnapshot;
        lock (_pinLock)
        {
            pinsSnapshot = new List<PinRenderData>(_pins);
        }

        var data = new List<float>(pinsSnapshot.Count * 256);
        _pinDraws.Clear();
        foreach (var pin in pinsSnapshot)
        {
            var start = data.Count / 6;
            var added = AppendPinGeometry(pin, data, pinScale);
            _pinDraws.Add(new PinDrawCall { Start = start, Count = added, Color = pin.Color });
        }

        if (_pinVao != 0)
        {
            GL.DeleteVertexArray(_pinVao);
        }
        if (_pinVbo != 0)
        {
            GL.DeleteBuffer(_pinVbo);
        }

        _pinVao = GL.GenVertexArray();
        _pinVbo = GL.GenBuffer();
        GL.BindVertexArray(_pinVao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _pinVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Count * sizeof(float), data.ToArray(), BufferUsageHint.DynamicDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        GL.BindVertexArray(0);

        _pinVertexCount = data.Count / 6;
    }

    private int AppendPinGeometry(PinRenderData pin, List<float> buffer, float pinScale)
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

        return added;
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

    private static Vector3[] CreateUnitCone()
    {
        const int segments = 16;
        var verts = new List<Vector3>();
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.PI * 2f / segments;
            float a1 = (i + 1) * MathF.PI * 2f / segments;
            verts.Add(new Vector3(0, 0, 1));
            verts.Add(new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0));
            verts.Add(new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0));
        }
        return verts.ToArray();
    }

    private static Vector3[] CreateUnitConeNormals()
    {
        const int segments = 16;
        var norms = new List<Vector3>();
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.PI * 2f / segments;
            float a1 = (i + 1) * MathF.PI * 2f / segments;
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
        return norms.ToArray();
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
                float p0 = u0 * MathF.PI * 2f;
                float p1 = u1 * MathF.PI * 2f;

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

    private int CreatePinShaderProgram()
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
            var vertexShader = CompilePinShader(ShaderType.VertexShader, variant.VertexSource, out var vertexError);
            if (vertexShader == 0)
            {
                if (!string.IsNullOrWhiteSpace(vertexError))
                    errors.Add($"Vertex {variant.Name}: {vertexError}");
                continue;
            }

            var fragmentShader = CompilePinShader(ShaderType.FragmentShader, variant.FragmentSource, out var fragmentError);
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
                LogMessage($"Pin shader: {variant.Name} (GL {version} | GLSL {glsl})");
                return program;
            }
        }

        if (errors.Count > 0)
        {
            LogMessage($"Pin shader compile failed ({version}): {string.Join(" | ", errors)}");
        }
        else
        {
            LogMessage($"Pin shader compile failed ({version}).");
        }

        return 0;
    }

    private static int CompilePinShader(ShaderType type, string source, out string? error)
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

    private int CreateCampathOverlayShaderProgram()
    {
        var version = GL.GetString(StringName.Version) ?? "unknown";
        var glsl = GL.GetString(StringName.ShadingLanguageVersion) ?? "unknown";
        var isEs = version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        var errors = new List<string>();

        var esVariants = new[]
        {
            new ShaderVariant("es300", LineVertexEs300, LineFragmentEs300, BindAttribLocation: false),
            new ShaderVariant("es100", LineVertexEs100, LineFragmentEs100, BindAttribLocation: true)
        };
        var desktopVariants = new[]
        {
            new ShaderVariant("gl330", LineVertex330, LineFragment330, BindAttribLocation: false),
            new ShaderVariant("gl150", LineVertex150, LineFragment150, BindAttribLocation: false),
            new ShaderVariant("gl120", LineVertex120, LineFragment120, BindAttribLocation: true)
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
            var vertexShader = CompileOverlayShader(ShaderType.VertexShader, variant.VertexSource, out var vertexError);
            if (vertexShader == 0)
            {
                if (!string.IsNullOrWhiteSpace(vertexError))
                    errors.Add($"Vertex {variant.Name}: {vertexError}");
                continue;
            }

            var fragmentShader = CompileOverlayShader(ShaderType.FragmentShader, variant.FragmentSource, out var fragmentError);
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
                GL.BindAttribLocation(program, 1, "aColor");
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
                LogMessage($"Campath overlay shader: {variant.Name} (GL {version} | GLSL {glsl})");
                return program;
            }
        }

        if (errors.Count > 0)
        {
            LogMessage($"Campath overlay shader compile failed ({version}): {string.Join(" | ", errors)}");
        }
        else
        {
            LogMessage($"Campath overlay shader compile failed ({version}).");
        }

        return 0;
    }

    private static int CompileOverlayShader(ShaderType type, string source, out string? error)
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

    private static OpenTK.Mathematics.Matrix4 ToMatrix4(Matrix4x4 matrix)
    {
        return new OpenTK.Mathematics.Matrix4(
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44);
    }

private static bool TryProjectToScreen(Vector3 world, ValveResourceFormat.Renderer.Camera camera, int width, int height, out Point screen)
{
    var toWorld = world - camera.Location;
    if (Vector3.Dot(camera.Forward, toWorld) <= 0.001f)
    {
        screen = default;
        return false;
    }

    var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewProjectionMatrix);
    if (Math.Abs(clip.W) < 1e-5f)
    {
        screen = default;
        return false;
    }

    var ndc = clip / clip.W;
    var x = (ndc.X * 0.5f + 0.5f) * width;
    var y = (-ndc.Y * 0.5f + 0.5f) * height;
    screen = new Point(x, y);
    return true;
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

    private sealed class PinLabel
    {
        public required string Text { get; init; }
        public required Vector3 World { get; init; }
        public required Color32 Color { get; init; }
        public double ScreenX { get; set; }
        public double ScreenY { get; set; }
    }

    private sealed record LiveLinkModelNode(string ModelName, ModelSceneNode Node);

    private readonly record struct LiveLinkIconBillboard(Vector3 World, string IconKey, bool Projectile, Vector3 Tint);
    private readonly record struct LiveLinkIconHit(Vector3 World, double X0, double Y0, double X1, double Y1);

    private enum GizmoMode
    {
        None,
        TranslateX,
        TranslateY,
        TranslateZ,
        RotateX,
        RotateY,
        RotateZ
    }

    private readonly record struct ShaderVariant(string Name, string VertexSource, string FragmentSource, bool BindAttribLocation);

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

    private const string LineVertex330 = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string LineFragment330 = """
        #version 330 core
        in vec3 vColor;
        out vec4 FragColor;
        void main()
        {
            FragColor = vec4(vColor, 1.0);
        }
        """;

    private const string LineVertex150 = """
        #version 150
        in vec3 aPos;
        in vec3 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string LineFragment150 = """
        #version 150
        in vec3 vColor;
        out vec4 FragColor;
        void main()
        {
            FragColor = vec4(vColor, 1.0);
        }
        """;

    private const string LineVertex120 = """
        #version 120
        attribute vec3 aPos;
        attribute vec3 aColor;
        uniform mat4 uMvp;
        varying vec3 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string LineFragment120 = """
        #version 120
        varying vec3 vColor;
        void main()
        {
            gl_FragColor = vec4(vColor, 1.0);
        }
        """;

    private const string LineVertexEs300 = """
        #version 300 es
        precision mediump float;
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string LineFragmentEs300 = """
        #version 300 es
        precision mediump float;
        in vec3 vColor;
        out vec4 FragColor;
        void main()
        {
            FragColor = vec4(vColor, 1.0);
        }
        """;

    private const string LineVertexEs100 = """
        attribute vec3 aPos;
        attribute vec3 aColor;
        uniform mat4 uMvp;
        varying vec3 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string LineFragmentEs100 = """
        precision mediump float;
        varying vec3 vColor;
        void main()
        {
            gl_FragColor = vec4(vColor, 1.0);
        }
        """;

    private void UpdateChildWindowSize()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var width = Math.Max(1, (int)Bounds.Width);
        var height = Math.Max(1, (int)Bounds.Height);
        _renderWidth = width;
        _renderHeight = height;

        lock (_nativeWindowLock)
        {
            if (_nativeWindow != null)
            {
                _nativeWindow.ClientRectangle = new Box2i(0, 0, width, height);
            }
        }

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height, 0x0010);
        RequestNextFrame();
    }

    private void RequestNextFrame()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestNextFrame);
            return;
        }

        float cap = GetEffectiveFpsCap(ViewportFpsCap);
        double targetMs = 1000.0 / cap;
        long nowTicks = _frameLimiter.ElapsedTicks;
        double elapsedMs = (nowTicks - _lastLimiterTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs >= targetMs)
        {
            _lastLimiterTicks = nowTicks;
            _renderSignal.Set();
            return;
        }

        ScheduleDelayedFrame(targetMs - elapsedMs);
    }

    private void ScheduleDelayedFrame(double delayMs)
    {
        if (_frameLimiterPending)
        {
            return;
        }

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
        _frameLimiterTimer?.Stop();
        _frameLimiterPending = false;
        _lastLimiterTicks = _frameLimiter.ElapsedTicks;
        _renderSignal.Set();
    }

    private static float GetEffectiveFpsCap(float cap)
    {
        return cap <= 0 ? MaxUncappedFps : cap;
    }

    private void UpdateFps(float deltaSeconds)
    {
        if (!_showFpsCached || deltaSeconds <= 0f)
        {
            return;
        }

        _fpsAccumulator += deltaSeconds;
        _fpsSamples++;
        if (_fpsAccumulator < 0.5f)
        {
            return;
        }

        _fpsValue = _fpsSamples / _fpsAccumulator;
        _fpsAccumulator = 0f;
        _fpsSamples = 0;
    }

    private void AddFpsOverlay(int width)
    {
        if (!_showFpsCached || _textRenderer == null || _renderer == null)
        {
            return;
        }

        var text = $"FPS: {_fpsValue:0.0}";
        const float scale = 16f;
        const float margin = 8f;
        var textWidth = text.Length * 0.6f * scale;
        var textBaseline = margin + scale;

        _textRenderer.AddText(new TextRenderer.TextRenderRequest
        {
            Text = text,
            X = Math.Max(margin, width - margin - textWidth),
            Y = textBaseline,
            Scale = scale,
            Color = new Color32(230, 230, 230, 220),
        });
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
            RotDampingRatio = 1.0f,
            WalkMoveSpeed = 160.0f,
            WalkMoveAcceleration = 800.0f,
            WalkMoveDeceleration = 800.0f,
            WalkRunMultiplier = 1.8f,
            WalkCrouchSpeedMultiplier = 0.6f,
            WalkLookHalfTime = 0.150f,
            WalkFovHalfTime = 0.40f,
            WalkGravity = 800.0f,
            WalkJumpSpeed = 280.0f,
            WalkHullRadius = 12.0f,
            WalkHullHalfHeight = 35.0f,
            WalkCrouchHullHalfHeight = 12.0f,
            WalkCameraTopInset = 6.0f,
            WalkStepHeight = 18.0f,
            WalkGroundProbe = 2.0f,
            WalkMinGroundNormalZ = 0.55f,
            WalkModeDefaultEnabled = false,
            HandheldDefaultEnabled = false,
            WalkBobAmplitudeZ = 2.15f,
            WalkBobAmplitudeSide = 2.70f,
            WalkBobAmplitudeRoll = 1.20f,
            WalkBobFrequency = 0.8f,
            HandheldShakePosAmplitude = 0.45f,
            HandheldShakeAngAmplitude = 0.65f,
            HandheldShakeFrequency = 0.4f,
            HandheldDriftPosAmplitude = 3.30f,
            HandheldDriftAngAmplitude = 2.36f,
            HandheldDriftFrequency = 0.15f
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
        public float WalkMoveSpeed { get; init; }
        public float WalkMoveAcceleration { get; init; }
        public float WalkMoveDeceleration { get; init; }
        public float WalkRunMultiplier { get; init; }
        public float WalkCrouchSpeedMultiplier { get; init; }
        public float WalkLookHalfTime { get; init; }
        public float WalkFovHalfTime { get; init; }
        public float WalkGravity { get; init; }
        public float WalkJumpSpeed { get; init; }
        public float WalkHullRadius { get; init; }
        public float WalkHullHalfHeight { get; init; }
        public float WalkCrouchHullHalfHeight { get; init; }
        public float WalkCameraTopInset { get; init; }
        public float WalkStepHeight { get; init; }
        public float WalkGroundProbe { get; init; }
        public float WalkMinGroundNormalZ { get; init; }
        public bool WalkModeDefaultEnabled { get; init; }
        public bool HandheldDefaultEnabled { get; init; }
        public float WalkBobAmplitudeZ { get; init; }
        public float WalkBobAmplitudeSide { get; init; }
        public float WalkBobAmplitudeRoll { get; init; }
        public float WalkBobFrequency { get; init; }
        public float HandheldShakePosAmplitude { get; init; }
        public float HandheldShakeAngAmplitude { get; init; }
        public float HandheldShakeFrequency { get; init; }
        public float HandheldDriftPosAmplitude { get; init; }
        public float HandheldDriftAngAmplitude { get; init; }
        public float HandheldDriftFrequency { get; init; }
    }

    private static bool _bindingsLoaded;

    #region Win32 host
    private static void EnsureClass()
    {
        if (_classRegistered)
        {
            return;
        }

        lock (ClassLock)
        {
            if (_classRegistered)
            {
                return;
            }

            if (_wndProc == null)
            {
                _wndProc = HostWndProc;
                _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
            }

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProcPtr,
                hInstance = GetModuleHandle(null),
                lpszClassName = WndClassName
            };
            _ = RegisterClassEx(ref wc);
            _classRegistered = true;
        }
    }

    private static IntPtr CreateChildWindow(IntPtr parent)
    {
        EnsureClass();
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

    private static void RegisterHostWindow(IntPtr hwnd, VRFViewport host)
    {
        lock (ClassLock)
        {
            HostMap[hwnd] = new WeakReference<VRFViewport>(host);
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
        {
            return new IntPtr(HTCLIENT);
        }

        VRFViewport? host = null;
        lock (ClassLock)
        {
            if (HostMap.TryGetValue(hWnd, out var weak) && weak.TryGetTarget(out var target))
            {
                host = target;
            }
        }

        if (host == null)
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

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

    private static void SetWindowAsChild(IntPtr childHwnd, IntPtr parentHwnd)
    {
        if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero)
        {
            return;
        }

        var style = (IntPtr)(WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_DISABLED);
        SetWindowLongPtr(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style);
        style = (IntPtr)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        SetWindowLongPtr(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style);
        SetParent(childHwnd, parentHwnd);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, WINDOW_LONG_PTR_INDEX nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    private enum WINDOW_LONG_PTR_INDEX
    {
        GWL_STYLE = -16,
        GWL_EXSTYLE = -20
    }

    [Flags]
    private enum WINDOW_STYLE : uint
    {
        WS_CHILD = 0x40000000,
        WS_DISABLED = 0x08000000
    }

    [Flags]
    private enum WINDOW_EX_STYLE : uint
    {
        WS_EX_NOACTIVATE = 0x08000000
    }
    #endregion
}
