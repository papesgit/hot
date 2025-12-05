using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using Avalonia;
using Avalonia.Platform;

namespace HlaeObsTools.Services.Gsi;

public sealed class RadarConfig
{
    public string MapName { get; init; } = string.Empty;
    public double PosX { get; init; }
    public double PosY { get; init; }
    public double Scale { get; init; }
    public bool TransparentBackground { get; init; }
    public string? ImagePath { get; init; }
    public IReadOnlyList<(string Name, double AltitudeMin)> Levels { get; init; } = Array.Empty<(string, double)>();
}

/// <summary>
/// Loads radar metadata (pos/scale/image) from the bundled radars.json.
/// </summary>
public sealed class RadarConfigProvider
{
    private readonly Dictionary<string, RadarConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    public RadarConfigProvider()
    {
        LoadConfigs();
    }

    public bool TryGet(string? mapName, out RadarConfig config)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            config = null!;
            return false;
        }

        var key = Sanitize(mapName);
        return _configs.TryGetValue(key, out config!);
    }

    private void LoadConfigs()
    {
        try
        {
            var uri = new Uri("avares://HlaeObsTools/Assets/hud/radars.json");
            using var asset = AssetLoader.Open(uri);
            using var reader = new StreamReader(asset);
            var json = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var name = entry.Name;
                var obj = entry.Value;
                var posX = obj.TryGetProperty("pos_x", out var px) ? GetDouble(px) : 0;
                var posY = obj.TryGetProperty("pos_y", out var py) ? GetDouble(py) : 0;
                var scale = obj.TryGetProperty("scale", out var sc) ? GetDouble(sc) : 1;
                var transparent = obj.TryGetProperty("radarImageTransparentBackgrond", out var tb) && tb.GetBoolean();
                string? imageUrl = obj.TryGetProperty("radarImageUrl", out var ru) ? ru.GetString() : null;

                var levels = new List<(string, double)>();
                if (obj.TryGetProperty("verticalsections", out var vsElem))
                {
                    foreach (var level in vsElem.EnumerateObject())
                    {
                        if (level.Value.TryGetProperty("AltitudeMin", out var altMin))
                        {
                            levels.Add((level.Name, GetDouble(altMin)));
                        }
                    }
                }

                _configs[Sanitize(name)] = new RadarConfig
                {
                    MapName = name,
                    PosX = posX,
                    PosY = posY,
                    Scale = scale,
                    TransparentBackground = transparent,
                    ImagePath = imageUrl,
                    Levels = levels.OrderByDescending(l => l.Item2).ToList()
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load radar configs: {ex.Message}");
        }
    }

    public static string Sanitize(string mapName)
    {
        return mapName.Trim().ToLowerInvariant();
    }

    private static double GetDouble(JsonElement elem)
    {
        try
        {
            return elem.ValueKind switch
            {
                JsonValueKind.Number => elem.GetDouble(),
                JsonValueKind.String when double.TryParse(elem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
                _ => 0d
            };
        }
        catch
        {
            return 0d;
        }
    }
}
