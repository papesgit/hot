using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Platform;

namespace HlaeObsTools.Services.Vmix;

public static class VmixShortcutCatalogLoader
{
    private const string AssetPath = "avares://HlaeObsTools/Assets/vmix/vmix_shortcuts.json";

    public static VmixShortcutCatalog LoadFromAssets()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetPath));
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var raw = JsonSerializer.Deserialize<List<VmixShortcutJsonRow>>(json) ?? new List<VmixShortcutJsonRow>();

            var functions = raw
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new VmixFunctionDefinition
                {
                    Category = r.Category ?? string.Empty,
                    Name = r.Name ?? string.Empty,
                    Description = r.Description ?? string.Empty,
                    ParameterKinds = ParseParameterKinds(r.Parameters)
                })
                .ToList();

            return new VmixShortcutCatalog { Functions = functions };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VMIX] Failed to load shortcut catalog: {ex.Message}");
            return new VmixShortcutCatalog();
        }
    }

    private static IReadOnlyList<VmixFunctionParameterKind> ParseParameterKinds(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters) || string.Equals(parameters, "None", StringComparison.OrdinalIgnoreCase))
            return new List<VmixFunctionParameterKind> { VmixFunctionParameterKind.None };

        var kinds = new List<VmixFunctionParameterKind>();
        foreach (var part in parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Value", StringComparison.OrdinalIgnoreCase))
                kinds.Add(VmixFunctionParameterKind.Value);
            else if (part.Equals("Input", StringComparison.OrdinalIgnoreCase))
                kinds.Add(VmixFunctionParameterKind.Input);
            else if (part.Equals("Channel", StringComparison.OrdinalIgnoreCase))
                kinds.Add(VmixFunctionParameterKind.Channel);
            else if (part.Equals("Duration", StringComparison.OrdinalIgnoreCase))
                kinds.Add(VmixFunctionParameterKind.Duration);
            else
                kinds.Add(VmixFunctionParameterKind.Custom);
        }

        if (kinds.Count == 0)
            kinds.Add(VmixFunctionParameterKind.Custom);

        return kinds.Distinct().ToList();
    }

    private sealed class VmixShortcutJsonRow
    {
        public string? Category { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Parameters { get; set; }
    }
}
