using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Input;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Controls;

public interface IViewport3DControl
{
    event Action<double>? FrameTick;

    bool IsFreecamActive { get; }
    bool IsFreecamInputEnabled { get; }

    void ForwardPointerPressed(PointerPressedEventArgs e);
    void ForwardPointerReleased(PointerReleasedEventArgs e);
    void ForwardPointerMoved(PointerEventArgs e);
    void ForwardPointerWheel(PointerWheelEventArgs e);

    bool TryGetFreecamState(out ViewportFreecamState state);
    void DisableFreecamInput();
    void SetExternalCamera(Vector3 position, Quaternion rotation, float fov);
    void ClearExternalCamera();
    void SetFreecamPose(Vector3 position, Quaternion rotation, float fov);
    void ClearFreecamPreview();
    void SetPins(IReadOnlyList<ViewportPin> pins);
    void SetPlayerStatuses(IReadOnlyList<ViewportPlayerStatus> statuses);
    void SetCampathOverlay(CampathOverlayData? data);
    void SetCampathGizmo(CampathGizmoState? state);

    event Action<Vector3, Quaternion>? CampathGizmoPoseChanged;
    event Action? CampathGizmoDragEnded;
}
