using System.Numerics;

namespace HlaeObsTools.Services.Campaths;

[System.Flags]
public enum CampathGizmoAxes
{
    None = 0,
    X = 1,
    Y = 2,
    Z = 4,
    All = X | Y | Z
}

public readonly struct CampathGizmoState
{
    public CampathGizmoState(bool visible, Vector3 position, Quaternion rotation, bool useLocalSpace,
        CampathGizmoAxes translationAxes = CampathGizmoAxes.All,
        CampathGizmoAxes rotationAxes = CampathGizmoAxes.All)
    {
        Visible = visible;
        Position = position;
        Rotation = rotation;
        UseLocalSpace = useLocalSpace;
        TranslationAxes = translationAxes;
        RotationAxes = rotationAxes;
    }

    public bool Visible { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public bool UseLocalSpace { get; }
    public CampathGizmoAxes TranslationAxes { get; }
    public CampathGizmoAxes RotationAxes { get; }
}
