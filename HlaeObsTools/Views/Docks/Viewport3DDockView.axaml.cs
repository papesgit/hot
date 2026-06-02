using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HlaeObsTools.Controls;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;
namespace HlaeObsTools.Views.Docks;

public partial class Viewport3DDockView : UserControl
{
    private Viewport3DDockViewModel? _viewModel;
    private IViewport3DControl? _viewport;
    private Control? _viewportControl;
    private IReadOnlyList<ViewportPin>? _lastPins;
    private CampathEditorViewModel? _campathEditor;
    private bool _frameTickSubscribed;
    private bool _gizmoSubscribed;

    public Viewport3DDockView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerMovedEvent, OnViewportPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerWheelChangedEvent, OnViewportPointerWheelChanged, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(KeyDownEvent, OnViewportKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel != null)
        {
            _viewModel.PinsUpdated -= OnPinsUpdated;
            _viewModel.Viewport3DSettings.PropertyChanged -= OnViewportSettingsChanged;
            if (_campathEditor != null)
            {
                DetachCampathEditor(_campathEditor);
            }
            _viewModel.CampathStateProvider = null;
            _viewModel.PreviewFreecamPose -= OnPreviewFreecamPose;
            _viewModel.PreviewFreecamEnded -= OnPreviewFreecamEnded;
            _viewModel.CampathPreviewOverrideChanged -= OnCampathPreviewOverrideChanged;
            UnsubscribeFrameTick();
            UnsubscribeGizmo();
        }

        _viewModel = DataContext as Viewport3DDockViewModel;
        if (_viewModel != null)
        {
            _viewModel.PinsUpdated += OnPinsUpdated;
            _viewModel.Viewport3DSettings.PropertyChanged += OnViewportSettingsChanged;
            _campathEditor = _viewModel.CampathEditor;
            AttachCampathEditor(_campathEditor);
            _viewModel.CampathStateProvider = CaptureFreecamState;
            _viewModel.PreviewFreecamPose += OnPreviewFreecamPose;
            _viewModel.PreviewFreecamEnded += OnPreviewFreecamEnded;
            _viewModel.CampathPreviewOverrideChanged += OnCampathPreviewOverrideChanged;
        }

        EnsureViewport();
    }

    private void OnViewportSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.UseLegacyD3D11Viewport))
        {
            EnsureViewport();
        }
        else if (e.PropertyName == nameof(Viewport3DSettings.ViewportCampathMode) ||
                 e.PropertyName == nameof(Viewport3DSettings.ViewportCampathOverlayEnabled))
        {
            UpdateCampathPreview();
            UpdateCampathOverlay();
            UpdateCampathGizmo();
        }
        else if (e.PropertyName == nameof(Viewport3DSettings.CampathGizmoLocalSpace))
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

        var useLegacy = _viewModel.Viewport3DSettings.UseLegacyD3D11Viewport;
        if (_viewportControl is D3D11Viewport && useLegacy)
            return;
        if (_viewportControl is VRFViewport && !useLegacy)
            return;

        ClearViewport();

        _viewportControl = useLegacy ? CreateD3D11Viewport() : CreateVrfViewport();
        _viewport = (IViewport3DControl)_viewportControl;
        ViewportHost.Content = _viewportControl;
        SubscribeFrameTick();
        SubscribeGizmo();

        if (_lastPins != null)
        {
            _viewport.SetPins(_lastPins);
        }

        UpdateCampathPreview();
        UpdateCampathOverlay();
        UpdateCampathGizmo();
    }

    private void ClearViewport()
    {
        ViewportHost.Content = null;
        UnsubscribeFrameTick();
        UnsubscribeGizmo();
        _viewport = null;
        _viewportControl = null;
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ShouldForwardPointer(e))
            _viewport?.ForwardPointerPressed(e);
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

    private void OnCampathEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathEditorViewModel.PlayheadSample) ||
            e.PropertyName == nameof(CampathEditorViewModel.PlayheadTime) ||
            e.PropertyName == nameof(CampathEditorViewModel.IsPlaying) ||
            e.PropertyName == nameof(CampathEditorViewModel.PreviewDuringPlayback))
        {
            UpdateCampathPreview();
        }

        if (e.PropertyName == nameof(CampathEditorViewModel.UseCubic) ||
            e.PropertyName == nameof(CampathEditorViewModel.Duration) ||
            e.PropertyName == nameof(CampathEditorViewModel.PlayheadTime) ||
            e.PropertyName == nameof(CampathEditorViewModel.IsPlaying) ||
            e.PropertyName == nameof(CampathEditorViewModel.PreviewDuringPlayback))
        {
            UpdateCampathOverlay();
        }

        if (e.PropertyName == nameof(CampathEditorViewModel.SelectedKeyframe))
        {
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

    private void UpdateCampathPreview()
    {
        if (_viewport == null || _viewModel == null)
            return;

        if (!_viewModel.Viewport3DSettings.ViewportCampathMode)
        {
            _viewport.ClearExternalCamera();
            return;
        }

        var editor = _viewModel.CampathEditor;
        var allowPlaybackPreview = editor.IsPlaying && editor.PreviewDuringPlayback;
        var allowPreview = allowPlaybackPreview || _viewModel.IsCampathPreviewOverrideActive;
        if (!allowPreview)
        {
            _viewport.ClearExternalCamera();
            return;
        }

        var sample = editor.PlayheadSample;
        if (sample == null)
        {
            _viewport.ClearExternalCamera();
            return;
        }

        _viewport.SetExternalCamera(sample.Value.Position, sample.Value.Rotation, (float)sample.Value.Fov);
    }

    private void OnCampathPreviewOverrideChanged()
    {
        UpdateCampathPreview();
        UpdateCampathOverlay();
    }

    private void UpdateCampathOverlay()
    {
        if (_viewport == null || _viewModel == null || _campathEditor == null)
            return;

        if (!_viewModel.Viewport3DSettings.ViewportCampathMode ||
            !_viewModel.Viewport3DSettings.ViewportCampathOverlayEnabled)
        {
            _viewport.SetCampathOverlay(null);
            return;
        }

        var hidePlayheadFrustum = (_campathEditor.IsPlaying && _campathEditor.PreviewDuringPlayback)
            || _viewModel.IsCampathPreviewOverrideActive
            || _viewModel.IsFreecamPreviewActive;
        var overlay = BuildCampathOverlay(_campathEditor, _campathEditor.PlayheadTime, hidePlayheadFrustum);
        _viewport.SetCampathOverlay(overlay);
    }

    private void UpdateCampathGizmo()
    {
        if (_viewport == null || _viewModel == null || _campathEditor == null)
            return;

        if (!_viewModel.Viewport3DSettings.ViewportCampathMode)
        {
            _viewport.SetCampathGizmo(null);
            return;
        }

        var selected = _campathEditor.SelectedKeyframe;
        if (selected == null)
        {
            _viewport.SetCampathGizmo(null);
            return;
        }

        var state = new CampathGizmoState(
            visible: true,
            position: selected.Position,
            rotation: selected.Rotation,
            useLocalSpace: _viewModel.Viewport3DSettings.CampathGizmoLocalSpace);
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
        if (_campathEditor?.SelectedKeyframe == null)
            return;

        _viewModel?.NotifyGizmoDragActive();
        _campathEditor.SelectedKeyframe.Position = position;
        _campathEditor.SelectedKeyframe.Rotation = rotation;
    }

    private void OnCampathGizmoDragEnded()
    {
        _viewModel?.NotifyGizmoDragEnded();
    }

    private static CampathOverlayData? BuildCampathOverlay(CampathEditorViewModel editor, double playheadTime, bool hidePlayheadFrustum)
    {
        if (editor.Keyframes.Count == 0)
            return null;

        var vertices = new List<CampathOverlayVertex>();
        var duration = Math.Max(editor.Duration, 0.001);
        var playheadNorm = (float)Math.Clamp(playheadTime / duration, 0.0, 1.0);

        if (editor.Curve.CanEvaluate() && editor.Keyframes.Count > 1)
        {
            var sampleCount = Math.Clamp((int)Math.Ceiling(duration * 30.0), 32, 512);
            var prevSample = editor.Curve.Evaluate(0.0);
            var prevPos = prevSample.Position;

            for (var i = 1; i <= sampleCount; i++)
            {
                var t = duration * i / sampleCount;
                var sample = editor.Curve.Evaluate(t);
                var color = GetPlayheadGradientColor((float)Math.Clamp(t / duration, 0.0, 1.0), playheadNorm);
                AddLine(vertices, prevPos, sample.Position, color);
                prevPos = sample.Position;
            }
        }

        if (!hidePlayheadFrustum && editor.Curve.CanEvaluate())
        {
            var sample = editor.Curve.Evaluate(playheadTime);
            var color = new Vector3(0.9f, 0.95f, 1.0f);
            AddCameraFrustum(vertices, sample.Position, sample.Rotation, (float)sample.Fov, color);
        }

        foreach (var keyframe in editor.Keyframes)
        {
            var tNorm = duration > 0.0 ? keyframe.Time / duration : 0.0;
            var color = keyframe.Selected
                ? new Vector3(1.0f, 1.0f, 0.2f)
                : GetPlayheadGradientColor((float)Math.Clamp(tNorm, 0.0, 1.0), playheadNorm);
            AddCameraFrustum(vertices, keyframe.Position, keyframe.Rotation, (float)keyframe.Fov, color);
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

    private void OnPreviewFreecamPose(Vector3 position, Quaternion rotation, float fov)
    {
        _viewport?.SetFreecamPose(position, rotation, fov);
        UpdateCampathOverlay();
    }

    private void OnPreviewFreecamEnded()
    {
        _viewport?.ClearFreecamPreview();
        UpdateCampathOverlay();
    }

    private void SubscribeFrameTick()
    {
        if (_viewport == null || _frameTickSubscribed || _viewModel == null)
            return;

        _viewport.FrameTick += OnViewportFrameTick;
        _frameTickSubscribed = true;
        _viewModel.CampathEditor.UseExternalPlaybackTicks = true;
    }

    private void UnsubscribeFrameTick()
    {
        if (_viewport == null || !_frameTickSubscribed || _viewModel == null)
            return;

        _viewport.FrameTick -= OnViewportFrameTick;
        _frameTickSubscribed = false;
        _viewModel.CampathEditor.UseExternalPlaybackTicks = false;
    }

    private void OnViewportFrameTick(double delta)
    {
        if (_viewModel == null)
            return;

        if (!_viewModel.CampathEditor.IsPlaying)
            return;

        Dispatcher.UIThread.Post(() => _viewModel.CampathEditor.AdvancePlayback(delta));
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

        vm.HandoffFreecam(state);
        _viewport.DisableFreecamInput();
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
        viewport.Bind(VRFViewport.LiveLinkPortProperty, new Binding("Viewport3DSettings.LiveLinkPort"));
        viewport.Bind(VRFViewport.ViewportMouseScaleProperty, new Binding("Viewport3DSettings.ViewportMouseScale"));
        viewport.Bind(VRFViewport.ViewportFpsCapProperty, new Binding("Viewport3DSettings.ViewportFpsCap"));
        return viewport;
    }

    private D3D11Viewport CreateD3D11Viewport()
    {
        var viewport = new D3D11Viewport();
        viewport.Bind(D3D11Viewport.MapPathProperty, new Binding("Viewport3DSettings.MapObjPath"));
        viewport.Bind(D3D11Viewport.ShowPlayerPinsProperty, new Binding("Viewport3DSettings.ShowPlayerPins"));
        viewport.Bind(D3D11Viewport.PinScaleProperty, new Binding("Viewport3DSettings.PinScale"));
        viewport.Bind(D3D11Viewport.PinOffsetZProperty, new Binding("Viewport3DSettings.PinOffsetZ"));
        viewport.Bind(D3D11Viewport.ViewportMouseScaleProperty, new Binding("Viewport3DSettings.ViewportMouseScale"));
        viewport.Bind(D3D11Viewport.ViewportFpsCapProperty, new Binding("Viewport3DSettings.ViewportFpsCap"));
        viewport.Bind(D3D11Viewport.ShowFpsProperty, new Binding("Viewport3DSettings.ShowFps"));
        viewport.Bind(D3D11Viewport.MapScaleProperty, new Binding("Viewport3DSettings.MapScale"));
        viewport.Bind(D3D11Viewport.MapYawProperty, new Binding("Viewport3DSettings.MapYaw"));
        viewport.Bind(D3D11Viewport.MapPitchProperty, new Binding("Viewport3DSettings.MapPitch"));
        viewport.Bind(D3D11Viewport.MapRollProperty, new Binding("Viewport3DSettings.MapRoll"));
        viewport.Bind(D3D11Viewport.MapOffsetXProperty, new Binding("Viewport3DSettings.MapOffsetX"));
        viewport.Bind(D3D11Viewport.MapOffsetYProperty, new Binding("Viewport3DSettings.MapOffsetY"));
        viewport.Bind(D3D11Viewport.MapOffsetZProperty, new Binding("Viewport3DSettings.MapOffsetZ"));
        viewport.Bind(D3D11Viewport.FreecamSettingsProperty, new Binding("FreecamSettings"));
        viewport.Bind(D3D11Viewport.InputSenderProperty, new Binding("InputSender"));
        return viewport;
    }
}
