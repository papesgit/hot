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
    public bool IsUserImagePath { get; init; }
    public string ImageMapName { get; init; } = string.Empty;
    public double? ScaleMinAltitude { get; init; }
    public double? ScaleMaxAltitude { get; init; }
    public IReadOnlyList<RadarLevel> Levels { get; init; } = Array.Empty<RadarLevel>();
}

public sealed class RadarLevel
{
    public string Name { get; init; } = "default";
    public double AltitudeMin { get; init; }
    public double AltitudeMax { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double? ScaleMinAltitude { get; init; }
    public double? ScaleMaxAltitude { get; init; }
}

/// <summary>
/// Loads radar metadata from the bundled radars.json, with optional per-user overrides.
/// </summary>
public sealed class RadarConfigProvider
{
    private readonly Dictionary<string, RadarConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    public static string UserRadarDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HlaeObsTools", "radars");

    public static string UserRadarConfigPath => Path.Combine(UserRadarDirectory, "radars.json");

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
            LoadConfigJson(reader.ReadToEnd(), isUserOverride: false);

            if (File.Exists(UserRadarConfigPath))
            {
                LoadConfigJson(File.ReadAllText(UserRadarConfigPath), isUserOverride: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load radar configs: {ex.Message}");
        }
    }

    private void LoadConfigJson(string json, bool isUserOverride)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var key = Sanitize(entry.Name);
            _configs.TryGetValue(key, out var bundledConfig);
            _configs[key] = ParseConfig(entry.Name, entry.Value, bundledConfig, isUserOverride);
        }
    }

    private static RadarConfig ParseConfig(string name, JsonElement obj, RadarConfig? fallback, bool isUserOverride)
    {
        var hasImagePath = obj.TryGetProperty("radarImageUrl", out var imagePathElement);
        var hasLevels = obj.TryGetProperty("verticalsections", out var levelsElement);
        var levels = hasLevels ? ParseLevels(levelsElement) : fallback?.Levels ?? Array.Empty<RadarLevel>();

        return new RadarConfig
        {
            MapName = name,
            PosX = GetDoubleOrFallback(obj, "pos_x", fallback?.PosX ?? 0),
            PosY = GetDoubleOrFallback(obj, "pos_y", fallback?.PosY ?? 0),
            Scale = GetDoubleOrFallback(obj, "scale", fallback?.Scale ?? 1),
            TransparentBackground = obj.TryGetProperty("radarImageTransparentBackgrond", out var transparent)
                ? transparent.GetBoolean()
                : fallback?.TransparentBackground ?? false,
            ImagePath = hasImagePath ? imagePathElement.GetString() : fallback?.ImagePath,
            IsUserImagePath = isUserOverride && hasImagePath,
            ImageMapName = obj.TryGetProperty("imageMapName", out var imageMapName)
                ? Sanitize(imageMapName.GetString() ?? name)
                : fallback?.ImageMapName ?? Sanitize(name),
            ScaleMinAltitude = GetNullableDoubleOrFallback(obj, fallback?.ScaleMinAltitude, "ScaleMinAltitude", "ScaleAltitudeMin"),
            ScaleMaxAltitude = GetNullableDoubleOrFallback(obj, fallback?.ScaleMaxAltitude, "ScaleMaxAltitude", "ScaleAltitudeMax"),
            Levels = levels.OrderByDescending(level => level.AltitudeMin).ToList()
        };
    }

    private static IReadOnlyList<RadarLevel> ParseLevels(JsonElement levelsElement)
    {
        var levels = new List<RadarLevel>();
        foreach (var level in levelsElement.EnumerateObject())
        {
            var levelObj = level.Value;
            levels.Add(new RadarLevel
            {
                Name = level.Name,
                AltitudeMin = GetDoubleOrFallback(levelObj, "AltitudeMin", 0),
                AltitudeMax = GetDoubleOrFallback(levelObj, "AltitudeMax", 0),
                OffsetX = GetDoubleOrFallback(levelObj, "OffsetX", 0),
                OffsetY = GetDoubleOrFallback(levelObj, "OffsetY", 0),
                ScaleMinAltitude = GetNullableDouble(levelObj, "ScaleMinAltitude", "ScaleAltitudeMin"),
                ScaleMaxAltitude = GetNullableDouble(levelObj, "ScaleMaxAltitude", "ScaleAltitudeMax")
            });
        }

        return levels;
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

    private static double GetDoubleOrFallback(JsonElement obj, string name, double fallback)
    {
        return obj.TryGetProperty(name, out var property) ? GetDouble(property) : fallback;
    }

    private static double? GetNullableDouble(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var prop))
            {
                return GetDouble(prop);
            }
        }

        return null;
    }

    private static double? GetNullableDoubleOrFallback(JsonElement obj, double? fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var property))
            {
                return GetDouble(property);
            }
        }

        return fallback;
    }
}
