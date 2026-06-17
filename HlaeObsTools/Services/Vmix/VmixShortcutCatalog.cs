using System.Collections.Generic;
using System.Linq;

namespace HlaeObsTools.Services.Vmix;

public enum VmixFunctionParameterKind
{
    None = 0,
    Value = 1,
    Input = 2,
    Channel = 3,
    Duration = 4,
    Custom = 5
}

public sealed class VmixFunctionDefinition
{
    public string Category { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<VmixFunctionParameterKind> ParameterKinds { get; init; } = new List<VmixFunctionParameterKind> { VmixFunctionParameterKind.None };
}

public sealed class VmixShortcutCatalog
{
    public IReadOnlyList<VmixFunctionDefinition> Functions { get; init; } = new List<VmixFunctionDefinition>();

    public IReadOnlyList<string> Categories =>
        Functions
            .Select(f => f.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .OrderBy(c => c)
            .ToList();

    public IReadOnlyList<VmixFunctionDefinition> GetFunctionsByCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return new List<VmixFunctionDefinition>();

        return Functions
            .Where(f => string.Equals(f.Category, category, System.StringComparison.Ordinal))
            .OrderBy(f => f.Name)
            .ToList();
    }

    public VmixFunctionDefinition? FindFunction(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return null;

        return Functions.FirstOrDefault(f => string.Equals(f.Name, functionName, System.StringComparison.Ordinal));
    }
}

public sealed class VmixFunctionCall
{
    public string Function { get; init; } = string.Empty;
    public string? Value { get; init; }
    public int? Input { get; init; }
    public string? Channel { get; init; }
    public string? Duration { get; init; }
    public string? ExtraQuery { get; init; }
}

public sealed class VmixInputInfo
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;

    public string Display => $"#{Number} - {(string.IsNullOrWhiteSpace(Title) ? Key : Title)}";

    public override string ToString() => Display;
}

public sealed class VmixStateSnapshot
{
    public IReadOnlyList<VmixInputInfo> Inputs { get; init; } = new List<VmixInputInfo>();
    public IReadOnlyList<string> Transitions { get; init; } = new List<string>();
    public string? Active { get; init; }
    public string? Preview { get; init; }
    public int ReplayEventsA { get; init; }
    public int ReplayEventsB { get; init; }
    public int ReplayEventsTotal { get; init; }
    public string? ReplayChannelMode { get; init; }
    public int ReplayCameraA { get; init; }
    public int ReplayCameraB { get; init; }
}
