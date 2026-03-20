using System.Numerics;

namespace HlaeObsTools.Services.Viewport3D;

public struct ViewportFreecamState
{
    public Vector3 RawPosition { get; init; }
    public Vector3 RawForward { get; init; }
    public Vector3 RawUp { get; init; }
    public Quaternion RawOrientation { get; init; }
    public float RawPitch { get; init; }
    public float RawYaw { get; init; }
    public float RawRoll { get; init; }
    public float RawFov { get; init; }
    public Vector3 SmoothedPosition { get; init; }
    public Vector3 SmoothedForward { get; init; }
    public Vector3 SmoothedUp { get; init; }
    public Quaternion SmoothedOrientation { get; init; }
    public float SmoothedFov { get; init; }
    public float SpeedScalar { get; init; }
    public bool WalkModeEnabled { get; init; }
    public bool HandheldEffectsEnabled { get; init; }
    public Vector3 WalkVelocity { get; init; }
    public float WalkVerticalVelocity { get; init; }
    public bool WalkOnGround { get; init; }
    public float WalkCrouchAmount { get; init; }
    public float WalkBobPhase { get; init; }
    public float WalkEffectTime { get; init; }
    public float WalkTargetPitch { get; init; }
    public float WalkTargetYaw { get; init; }
    public float WalkTargetFov { get; init; }
}
