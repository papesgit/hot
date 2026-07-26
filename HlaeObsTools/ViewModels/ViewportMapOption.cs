namespace HlaeObsTools.ViewModels;

public sealed class ViewportMapOption
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? "None" : Name;
}
