using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HlaeObsTools.Controls;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Cues;
using HlaeObsTools.ViewModels.Docks;
namespace HlaeObsTools.Views.Docks;

public partial class Viewport3DDockView : UserControl
{
    private Viewport3DDockViewModel? _viewModel;
    private IViewport3DControl? _viewport;
    private Control? _viewportControl;
    private IReadOnlyList<ViewportPin>? _lastPins;
    private IReadOnlyList<ViewportPlayerStatus>? _lastPlayerStatuses;
    private IReadOnlyList<CueEventViewModel>? _lastCueEvents;
    private CampathEditorViewModel? _campathEditor;
    private bool _frameTickSubscribed;
    private bool _gizmoSubscribed;
    private bool _viewModelEventsAttached;

    public Viewport3DDockView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerMovedEvent, OnViewportPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerWheelChangedEvent, OnViewportPointerWheelChanged, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(KeyDownEvent, OnViewportKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AttachedToVisualTree += (_, _) =>
        {
            AttachViewModelEvents();
            EnsureViewport();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            ClearViewport();
            DetachViewModelEvents();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        ClearViewport();
        DetachViewModelEvents();

        _viewModel = DataContext as Viewport3DDockViewModel;

        if (this.IsAttachedToVisualTree())
        {
            AttachViewModelEvents();
            EnsureViewport();
        }
    }

    private void AttachViewModelEvents()
    {
        if (_viewModel == null || _viewModelEventsAttached)
            return;

        _viewModel.PinsUpdated += OnPinsUpdated;
        _viewModel.PlayerStatusesUpdated += OnPlayerStatusesUpdated;
        _viewModel.CueEventsUpdated += OnCueEventsUpdated;
        _viewModel.Viewport3DSettings.PropertyChanged += OnViewportSettingsChanged;
        _viewModel.SelectedCampathEditorChanged += OnSelectedCampathEditorChanged;
        _viewModel.SequencerGizmoChanged += OnSequencerGizmoChanged;
        SetCampathEditor(_viewModel.SelectedCampathEditor);
        _viewModel.CampathStateProvider = CaptureFreecamState;
        _viewModel.SequencerPreviewChanged += OnSequencerPreviewChanged;
        _viewModelEventsAttached = true;
    }

    private void DetachViewModelEvents()
    {
        if (_viewModel == null || !_viewModelEventsAttached)
            return;

        _viewModel.PinsUpdated -= OnPinsUpdated;
        _viewModel.PlayerStatusesUpdated -= OnPlayerStatusesUpdated;
        _viewModel.CueEventsUpdated -= OnCueEventsUpdated;
        _viewModel.Viewport3DSettings.PropertyChanged -= OnViewportSettingsChanged;
        _viewModel.SelectedCampathEditorChanged -= OnSelectedCampathEditorChanged;
        _viewModel.SequencerGizmoChanged -= OnSequencerGizmoChanged;
        if (_campathEditor != null)
            DetachCampathEditor(_campathEditor);
        _campathEditor = null;
        if (_viewModel.CampathStateProvider == CaptureFreecamState)
            _viewModel.CampathStateProvider = null;
        _viewModel.SequencerPreviewChanged -= OnSequencerPreviewChanged;
        _viewModelEventsAttached = false;
    }

    private void OnSelectedCampathEditorChanged(CampathEditorViewModel? editor)
    {
        SetCampathEditor(editor);
        UpdateDepthOfField();
        UpdateCampathOverlay();
        UpdateCampathGizmo();
    }

    private void OnSequencerGizmoChanged() => UpdateCampathGizmo();

    private void SetCampathEditor(CampathEditorViewModel? editor)
    {
        if (ReferenceEquals(_campathEditor, editor))
            return;

        if (_campathEditor != null)
            DetachCampathEditor(_campathEditor);

        _campathEditor = editor;

        if (_campathEditor != null)
            AttachCampathEditor(_campathEditor);
    }

    private void OnViewportSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.ViewportCampathOverlayEnabled))
        {
            UpdateDepthOfField();
            UpdateCampathOverlay();
            UpdateCampathGizmo();
        }
        else if (e.PropertyName is nameof(Viewport3DSettings.CampathGizmoLocalSpace) or nameof(Viewport3DSettings.ViewportCampathGizmoEnabled))
        {
            UpdateCampathGizmo();
        }
    }

    private void EnsureViewport()
    {
        if (_viewModel == null)
        {
            ClearViewport();
            return;
        }

        if (_viewportControl is VRFViewport)
            return;

        ClearViewport();

        var viewport = _viewModel.PersistentViewport;
        if (viewport == null)
        {
            viewport = CreateVrfViewport();
            _viewModel.PersistentViewport = viewport;
        }
        else if (viewport.Parent is ContentControl previousHost)
        {
            previousHost.Content = null;
        }

        // Keep bindings stable while the persistent native viewport is moved
        // between layout presenters. Inherited DataContext briefly disappears
        // during reparenting, which otherwise clears MapPath and reloads the map.
        viewport.DataContext = _viewModel;
        _viewportControl = viewport;
        _viewport = (IViewport3DControl)_viewportControl;
        viewport.MapLoadStateChanged -= OnMapLoadStateChanged;
        viewport.MapLoadStateChanged += OnMapLoadStateChanged;
        viewport.NativeHostInitialized -= OnNativeHostInitialized;
        viewport.NativeHostInitialized += OnNativeHostInitialized;
        ViewportHost.Content = _viewportControl;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_viewportControl, viewport))
                ApplyMapLoadState(viewport.MapLoadState);
        }, DispatcherPriority.Loaded);
        SubscribeFrameTick();
        SubscribeGizmo();

        if (_lastPins != null)
        {
            _viewport.SetPins(_lastPins);
        }

        if (_lastPlayerStatuses != null)
        {
            _viewport.SetPlayerStatuses(_lastPlayerStatuses);
        }
        if (_lastCueEvents != null) _viewport.SetCueEvents(_lastCueEvents);

        UpdateDepthOfField();
        UpdateCampathOverlay();
        UpdateCampathGizmo();
    }

    private void ClearViewport()
    {
        if (_viewportControl is VRFViewport vrfViewport)
        {
            vrfViewport.MapLoadStateChanged -= OnMapLoadStateChanged;
            vrfViewport.NativeHostInitialized -= OnNativeHostInitialized;
        }
        ViewportHost.Content = null;
        UnsubscribeFrameTick();
        UnsubscribeGizmo();
        _viewport = null;
        _viewportControl = null;
    }

    private void OnMapLoadStateChanged(ViewportMapLoadState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewportControl is VRFViewport)
                ApplyMapLoadState(state);
        });
    }

    private void OnNativeHostInitialized()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewportControl is VRFViewport viewport)
                ApplyMapLoadState(viewport.MapLoadState);
        });
    }

    private void ApplyMapLoadState(ViewportMapLoadState state)
    {
        var viewport = _viewportControl as VRFViewport;
        var canHideNativeHost = viewport?.HasInitializedNativeHost ?? true;
        ViewportHost.IsVisible = state.Status == ViewportMapLoadStatus.Ready || !canHideNativeHost;
        MapStatusOverlay.IsVisible = state.Status != ViewportMapLoadStatus.Ready;
        if (state.Status == ViewportMapLoadStatus.Ready && viewport != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_viewportControl, viewport) &&
                    viewport.MapLoadState.Status == ViewportMapLoadStatus.Ready &&
                    ViewportHost.IsVisible)
                {
                    viewport.RequestPresentationFrame();
                }
            }, DispatcherPriority.Render);
        }
        MapLoadingProgress.IsVisible = state.Status == ViewportMapLoadStatus.Loading;
        MapStatusTitle.Text = state.Status switch
        {
            ViewportMapLoadStatus.Loading => string.IsNullOrWhiteSpace(state.MapName)
                ? "Loading map..."
                : $"Loading {state.MapName}...",
            ViewportMapLoadStatus.Error => "Map loading failed",
            _ => "No map loaded"
        };
        MapStatusDetails.Text = state.Status switch
        {
            ViewportMapLoadStatus.Error => state.Error ?? "An unknown error occurred.",
            ViewportMapLoadStatus.Empty => "Select a map in the 3D settings to load the viewport.",
            _ => string.Empty
        };
        MapStatusDetails.IsVisible = !string.IsNullOrWhiteSpace(MapStatusDetails.Text);
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var shouldForward = ShouldForwardPointer(e);
        var point = e.GetCurrentPoint(this);
        var beginsFreecam = shouldForward && (point.Properties.IsRightButtonPressed
            || point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed);

        if (shouldForward)
            _viewport?.ForwardPointerPressed(e);

        // Begin piloting after the viewport has entered freecam input so clearing
        // the evaluated camera is the final preview ownership change.
        if (beginsFreecam)
        {
            _viewModel?.BeginSequencerPiloting();
            if (_viewModel?.IsSequencerPiloting == true)
                _viewport?.ClearExternalCamera();
        }
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ShouldForwardPointer(e))
            _viewport?.ForwardPointerReleased(e);

        if (_viewModel == null)
            return;

        var point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            _viewModel.ReleaseHandoffFreecamInput();
        }
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (ShouldForwardPointer(e))
        {
            _viewport?.ForwardPointerMoved(e);
        }
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ShouldForwardPointer(e))
        {
            _viewport?.ForwardPointerWheel(e);
        }
    }

    private bool ShouldForwardPointer(PointerEventArgs e)
    {
        if (_viewportControl == null)
            return false;

        if (e.Pointer.Captured == _viewportControl)
            return true;

        var pos = e.GetPosition(_viewportControl);
        var bounds = _viewportControl.Bounds;
        return pos.X >= 0 && pos.Y >= 0 && pos.X <= bounds.Width && pos.Y <= bounds.Height;
    }

    private void OnPinsUpdated(IReadOnlyList<ViewportPin> pins)
    {
        _lastPins = pins;
        _viewport?.SetPins(pins);
    }

    private void OnPlayerStatusesUpdated(IReadOnlyList<ViewportPlayerStatus> statuses)
    {
        _lastPlayerStatuses = statuses;
        _viewport?.SetPlayerStatuses(statuses);
    }

    private void OnCueEventsUpdated(IReadOnlyList<CueEventViewModel> cues)
    {
        _lastCueEvents = cues;
        _viewport?.SetCueEvents(cues);
    }

    private void OnCampathEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathEditorViewModel.PlayheadSample) ||
            e.PropertyName == nameof(CampathEditorViewModel.PlayheadTime) ||
            e.PropertyName == nameof(CampathEditorViewModel.DofOverride) ||
            e.PropertyName == nameof(CampathEditorViewModel.CurrentDofSettings) ||
            e.PropertyName == nameof(CampathEditorViewModel.IsDofEditorOpen))
        {
            UpdateDepthOfField();
        }

        if (e.PropertyName == nameof(CampathEditorViewModel.EditorMode) ||
            e.PropertyName == nameof(CampathEditorViewModel.CurveDocumentRevision) ||
            e.PropertyName == nameof(CampathEditorViewModel.PlayheadTime))
        {
            if (_viewModel?.IsSequencerPlaying != true)
                UpdateCampathOverlay();
        }

        if (e.PropertyName == nameof(CampathEditorViewModel.SelectedKeyframe))
        {
            UpdateDepthOfField();
            UpdateCampathGizmo();
        }
    }

    private void OnCampathKeyframesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (CampathKeyframeViewModel keyframe in e.OldItems)
            {
                keyframe.PropertyChanged -= OnCampathKeyframeChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (CampathKeyframeViewModel keyframe in e.NewItems)
            {
                keyframe.PropertyChanged += OnCampathKeyframeChanged;
            }
        }

        UpdateCampathOverlay();
    }

    private void OnCampathKeyframeChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateDepthOfField();
        UpdateCampathOverlay();
        UpdateCampathGizmo();
    }

    private void AttachCampathEditor(CampathEditorViewModel editor)
    {
        editor.PropertyChanged += OnCampathEditorChanged;
        editor.Keyframes.CollectionChanged += OnCampathKeyframesChanged;
        foreach (var keyframe in editor.Keyframes)
        {
            keyframe.PropertyChanged += OnCampathKeyframeChanged;
        }
    }

    private void DetachCampathEditor(CampathEditorViewModel editor)
    {
        editor.PropertyChanged -= OnCampathEditorChanged;
        editor.Keyframes.CollectionChanged -= OnCampathKeyframesChanged;
        foreach (var keyframe in editor.Keyframes)
        {
            keyframe.PropertyChanged -= OnCampathKeyframeChanged;
        }
    }

    private ViewportFreecamState? CaptureFreecamState()
    {
        if (_viewport == null)
            return null;

        return _viewport.TryGetFreecamState(out var state) ? state : null;
    }

    private void UpdateDepthOfField()
    {
        if (_viewport == null || _viewModel == null)
            return;

        _viewport.SetDepthOfField(_viewModel.GetSequencerDepthOfField());
    }

    private void UpdateCampathOverlay()
    {
        if (_viewport == null || _viewModel == null)
            return;

        if (!_viewModel.Viewport3DSettings.ViewportCampathOverlayEnabled ||
            _campathEditor == null)
        {
            _viewport.SetCampathOverlay(null);
            _viewport.SetCampathPlayheadFrustum(null);
            return;
        }

        var overlay = BuildCampathOverlay(_campathEditor, _campathEditor.PlayheadTime);
        _viewport.SetCampathOverlay(overlay);
        UpdateCampathPlayheadFrustum();
    }

    private void UpdateCampathPlayheadFrustum()
    {
        if (_viewport == null || _viewModel == null ||
            !_viewModel.Viewport3DSettings.ViewportCampathOverlayEnabled ||
            _viewModel.HasSequencerPossession ||
            _campathEditor?.CanEvaluate() != true)
        {
            _viewport?.SetCampathPlayheadFrustum(null);
            return;
        }

        var sample = _campathEditor.Evaluate(_campathEditor.PlayheadTime);
        var vertices = new List<CampathOverlayVertex>();
        AddCameraFrustum(vertices, sample.Position, sample.Rotation, (float)sample.Fov,
            new Vector3(0.9f, 0.95f, 1.0f));
        _viewport.SetCampathPlayheadFrustum(new CampathOverlayData(vertices));
    }

    private void UpdateCampathGizmo()
    {
        if (_viewport == null || _viewModel == null)
            return;

        if (!_viewModel.Viewport3DSettings.ViewportCampathGizmoEnabled ||
            _campathEditor == null)
        {
            _viewport.SetCampathGizmo(null);
            return;
        }

        var state = _viewModel.GetSequencerGizmoState();
        if (state == null)
        {
            _viewport.SetCampathGizmo(null);
            return;
        }

        _viewport.SetCampathGizmo(state);
    }

    private void SubscribeGizmo()
    {
        if (_viewport == null || _gizmoSubscribed)
            return;

        _viewport.CampathGizmoPoseChanged += OnCampathGizmoPoseChanged;
        _viewport.CampathGizmoDragEnded += OnCampathGizmoDragEnded;
        _gizmoSubscribed = true;
    }

    private void UnsubscribeGizmo()
    {
        if (_viewport == null || !_gizmoSubscribed)
            return;

        _viewport.CampathGizmoPoseChanged -= OnCampathGizmoPoseChanged;
        _viewport.CampathGizmoDragEnded -= OnCampathGizmoDragEnded;
        _gizmoSubscribed = false;
    }

    private void OnCampathGizmoPoseChanged(Vector3 position, Quaternion rotation)
    {
        if (_viewModel == null)
            return;

        _viewModel.NotifyGizmoDragActive();
        _viewModel.ApplySequencerGizmoPose(position, rotation);
    }

    private void OnCampathGizmoDragEnded()
    {
        _viewModel?.NotifyGizmoDragEnded();
    }

    private static CampathOverlayData? BuildCampathOverlay(CampathEditorViewModel editor, double playheadTime)
    {
        if (!editor.CanEvaluate())
            return null;

        var vertices = new List<CampathOverlayVertex>();
        var duration = Math.Max(GetEditorContentEnd(editor), 0.001);
        var playheadNorm = (float)Math.Clamp(playheadTime / duration, 0.0, 1.0);

        if (editor.CanEvaluate())
        {
            var sampleCount = Math.Clamp((int)Math.Ceiling(duration * 30.0), 32, 512);
            var prevSample = editor.Evaluate(0.0);
            var prevPos = prevSample.Position;

            for (var i = 1; i <= sampleCount; i++)
            {
                var t = duration * i / sampleCount;
                var sample = editor.Evaluate(t);
                var color = GetPlayheadGradientColor((float)Math.Clamp(t / duration, 0.0, 1.0), playheadNorm);
                AddLine(vertices, prevPos, sample.Position, color);
                prevPos = sample.Position;
            }
        }

        if (editor.IsCurveMode)
        {
            foreach (var bundle in editor.CurveDocument.GetBundleMarkers())
            {
                var sample = editor.CurveDocument.Evaluate(bundle.Time);
                var tNorm = duration > 0.0 ? bundle.Time / duration : 0.0;
                var color = bundle.Selected
                    ? new Vector3(1.0f, 1.0f, 0.2f)
                    : GetPlayheadGradientColor((float)Math.Clamp(tNorm, 0.0, 1.0), playheadNorm);
                AddCameraFrustum(vertices, sample.Position, sample.Rotation, (float)sample.Fov, color);
            }
        }
        else
        {
            foreach (var keyframe in editor.Keyframes)
            {
                var tNorm = duration > 0.0 ? keyframe.Time / duration : 0.0;
                var color = keyframe.Selected
                    ? new Vector3(1.0f, 1.0f, 0.2f)
                    : GetPlayheadGradientColor((float)Math.Clamp(tNorm, 0.0, 1.0), playheadNorm);
                AddCameraFrustum(vertices, keyframe.Position, keyframe.Rotation, (float)keyframe.Fov, color);
            }
        }

        return vertices.Count > 0 ? new CampathOverlayData(vertices) : null;
    }

    private static void AddLine(List<CampathOverlayVertex> vertices, Vector3 start, Vector3 end, Vector3 color)
    {
        vertices.Add(new CampathOverlayVertex(start, color));
        vertices.Add(new CampathOverlayVertex(end, color));
    }

    private static void AddCameraFrustum(List<CampathOverlayVertex> vertices, Vector3 position, Quaternion rotation, float fov, Vector3 color)
    {
        const float frustumLength = 32f;
        const float aspect = 16f / 9f;

        var forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rotation));
        var up = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
        var right = Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, rotation));

        var halfHeight = MathF.Tan(MathF.PI / 180f * fov * 0.5f) * frustumLength;
        var halfWidth = halfHeight * aspect;

        var center = position + forward * frustumLength;
        var upScaled = up * halfHeight;
        var rightScaled = right * halfWidth;

        var c1 = center + upScaled + rightScaled;
        var c2 = center + upScaled - rightScaled;
        var c3 = center - upScaled - rightScaled;
        var c4 = center - upScaled + rightScaled;

        AddLine(vertices, position, c1, color);
        AddLine(vertices, position, c2, color);
        AddLine(vertices, position, c3, color);
        AddLine(vertices, position, c4, color);

        AddLine(vertices, c1, c2, color);
        AddLine(vertices, c2, c3, color);
        AddLine(vertices, c3, c4, color);
        AddLine(vertices, c4, c1, color);
    }

    private static Vector3 GetPlayheadGradientColor(float t, float playheadT)
    {
        t = Math.Clamp(t, 0f, 1f);
        playheadT = Math.Clamp(playheadT, 0f, 1f);

        var pastStart = new Vector3(0.15f, 0.75f, 1.0f);
        var pastEnd = new Vector3(0.35f, 1.0f, 0.35f);
        var futureStart = new Vector3(1.0f, 0.75f, 0.2f);
        var futureEnd = new Vector3(1.0f, 0.2f, 0.2f);

        if (t <= playheadT)
        {
            var denom = Math.Max(playheadT, 0.0001f);
            return Lerp(pastStart, pastEnd, t / denom);
        }

        var futureDenom = Math.Max(1f - playheadT, 0.0001f);
        return Lerp(futureStart, futureEnd, (t - playheadT) / futureDenom);
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        return a + (b - a) * t;
    }

    private void OnSequencerPreviewChanged(CampathSample? sample)
    {
        if (_viewport == null)
            return;
        UpdateCampathPlayheadFrustum();
        if (!sample.HasValue || _viewModel?.IsSequencerPiloting == true)
        {
            _viewport.ClearExternalCamera();
            _viewport.ClearFreecamPreview();
            UpdateDepthOfField();
            return;
        }

        var value = sample.Value;
        // Sequencer evaluation drives the freecam itself. Using the viewport's
        // external-camera override here would leave freecam input moving a hidden
        // camera behind the evaluated view while the user is piloting.
        _viewport.ClearExternalCamera();
        _viewport.SetFreecamPose(value.Position, value.Rotation, (float)value.Fov);
        _viewport.SetDepthOfField(value.Dof);
    }

    private static double GetEditorContentEnd(CampathEditorViewModel editor) =>
        editor.IsCurveMode
            ? editor.CurveDocument.Channels.SelectMany(channel => channel.Keys)
                .Select(key => key.Time).DefaultIfEmpty(0.0).Max()
            : editor.Keyframes.Select(key => key.Time).DefaultIfEmpty(0.0).Max();

    private void SubscribeFrameTick()
    {
        if (_viewport == null || _frameTickSubscribed || _viewModel == null)
            return;

        _viewport.FrameTick += OnViewportFrameTick;
        _frameTickSubscribed = true;
        _viewModel.AcquireSequencerPlaybackTicks(this, ReleaseFrameTickSubscription);
    }

    private void UnsubscribeFrameTick()
    {
        ReleaseFrameTickSubscription();
        _viewModel?.ReleaseSequencerPlaybackTicks(this);
    }

    private void ReleaseFrameTickSubscription()
    {
        if (_viewport != null && _frameTickSubscribed)
            _viewport.FrameTick -= OnViewportFrameTick;
        _frameTickSubscribed = false;
    }

    private void OnViewportFrameTick(double delta)
    {
        if (_viewModel == null)
            return;

        if (_viewModel.IsSequencerPlaying)
            Dispatcher.UIThread.Post(() => _viewModel.AdvanceSequencerPlayback(this, delta));
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.B)
            return;

        if (_viewportControl == null || !_viewportControl.IsKeyboardFocusWithin)
            return;

        if (_viewport == null)
            return;

        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        if (!_viewport.IsFreecamActive || !_viewport.IsFreecamInputEnabled)
            return;

        if (!_viewport.TryGetFreecamState(out var state))
            return;

        _viewport.DisableFreecamInput();
        vm.HandoffFreecam(state);
        e.Handled = true;
    }

    private VRFViewport CreateVrfViewport()
    {
        var viewport = new VRFViewport();
        viewport.Bind(VRFViewport.MapPathProperty, new Binding("Viewport3DSettings.MapObjPath"));
        viewport.Bind(VRFViewport.ShowPlayerPinsProperty, new Binding("Viewport3DSettings.ShowPlayerPins"));
        viewport.Bind(VRFViewport.PinScaleProperty, new Binding("Viewport3DSettings.PinScale"));
        viewport.Bind(VRFViewport.PinOffsetZProperty, new Binding("Viewport3DSettings.PinOffsetZ"));
        viewport.Bind(VRFViewport.PostprocessEnabledProperty, new Binding("Viewport3DSettings.PostprocessEnabled"));
        viewport.Bind(VRFViewport.ColorCorrectionEnabledProperty, new Binding("Viewport3DSettings.ColorCorrectionEnabled"));
        viewport.Bind(VRFViewport.DynamicShadowsEnabledProperty, new Binding("Viewport3DSettings.DynamicShadowsEnabled"));
        viewport.Bind(VRFViewport.WireframeEnabledProperty, new Binding("Viewport3DSettings.WireframeEnabled"));
        viewport.Bind(VRFViewport.SkipWaterEnabledProperty, new Binding("Viewport3DSettings.SkipWaterEnabled"));
        viewport.Bind(VRFViewport.SkipTranslucentEnabledProperty, new Binding("Viewport3DSettings.SkipTranslucentEnabled"));
        viewport.Bind(VRFViewport.ShowFpsProperty, new Binding("Viewport3DSettings.ShowFps"));
        viewport.Bind(VRFViewport.ShadowTextureSizeProperty, new Binding("Viewport3DSettings.ShadowTextureSize"));
        viewport.Bind(VRFViewport.MaxTextureSizeProperty, new Binding("Viewport3DSettings.MaxTextureSize"));
        viewport.Bind(VRFViewport.RenderModeProperty, new Binding("Viewport3DSettings.RenderMode"));
        viewport.Bind(VRFViewport.FreecamSettingsProperty, new Binding("FreecamSettings"));
        viewport.Bind(VRFViewport.InputSenderProperty, new Binding("InputSender"));
        viewport.Bind(VRFViewport.LiveLinkReceiverProperty, new Binding("LiveLinkReceiver"));
        viewport.Bind(VRFViewport.LiveLinkEnabledProperty, new Binding("Viewport3DSettings.LiveLinkEnabled"));
        viewport.Bind(VRFViewport.LiveLinkItemIconsEnabledProperty, new Binding("Viewport3DSettings.LiveLinkItemIconsEnabled"));
        viewport.Bind(VRFViewport.LiveLinkWeaponIconsEnabledProperty, new Binding("Viewport3DSettings.LiveLinkWeaponIconsEnabled"));
        viewport.Bind(VRFViewport.LiveLinkGrenadeIconsEnabledProperty, new Binding("Viewport3DSettings.LiveLinkGrenadeIconsEnabled"));
        viewport.Bind(VRFViewport.LiveLinkProjectileIconsEnabledProperty, new Binding("Viewport3DSettings.LiveLinkProjectileIconsEnabled"));
        viewport.Bind(VRFViewport.LiveLinkObjectiveIconsEnabledProperty, new Binding("Viewport3DSettings.LiveLinkObjectiveIconsEnabled"));
        viewport.Bind(VRFViewport.LiveLinkDeadPlayerIconsEnabledProperty, new Binding("Viewport3DSettings.LiveLinkDeadPlayerIconsEnabled"));
        viewport.Bind(VRFViewport.LiveLinkPortProperty, new Binding("Viewport3DSettings.LiveLinkPort"));
        viewport.Bind(VRFViewport.TargetOrbitResetRequestProperty, new Binding("Viewport3DSettings.TargetOrbitResetRequest"));
        viewport.Bind(VRFViewport.ViewportMouseScaleProperty, new Binding("Viewport3DSettings.ViewportMouseScale"));
        viewport.Bind(VRFViewport.ViewportFpsCapProperty, new Binding("Viewport3DSettings.ViewportFpsCap"));
        return viewport;
    }

}
