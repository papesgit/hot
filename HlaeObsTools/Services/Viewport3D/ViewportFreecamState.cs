using OpenTK.Mathematics;

namespace HlaeObsTools.Services.Viewport3D;

public struct ViewportFreecamState
{
    public Vector3 RawPosition { get; init; }
    public Vector3 RawForward { get; init; }
    public Vector3 RawUp { get; init; }
    public float RawFov { get; init; }
    public Vector3 SmoothedPosition { get; init; }
    public Vector3 SmoothedForward { get; init; }
    public Vector3 SmoothedUp { get; init; }
    public float SmoothedFov { get; init; }
    public float SpeedScalar { get; init; }
}
