namespace HlaeObsTools.ViewModels;

public sealed class ViewportPlayerStatus
{
    public required int Slot { get; init; }
    public required bool IsAlive { get; init; }
    public required int Health { get; init; }
    public required string Team { get; init; }
    public required string Name { get; init; }
}
