using HlaeObsTools.Services.Gsi;

namespace HlaeObsTools.ViewModels;

public sealed class ViewportPin
{
    public required Vec3 Position { get; init; }
    public required Vec3 Forward { get; init; }
    public required string Team { get; init; }
    public required int Slot { get; init; }
    public required string Label { get; init; }
    public required bool IsAlive { get; init; }
}
