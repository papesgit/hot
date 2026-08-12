using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlaeObsTools.Services.Graphics;

public sealed class GsiExtrasTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, GsiPlayerStats> _playerStats = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentMapName;

    public GsiExtrasSnapshot Update(string rawJson)
    {
        var shouldRefreshPlayerStats = false;
        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;
                var mapName = GetString(root, "map", "name");
                var mapChanged = !string.IsNullOrWhiteSpace(mapName) &&
                                 !string.Equals(mapName, _currentMapName, StringComparison.OrdinalIgnoreCase);
                if (mapChanged)
                {
                    lock (_sync)
                    {
                        _playerStats.Clear();
                    }
                    _currentMapName = mapName;
                }

                shouldRefreshPlayerStats = string.Equals(
                    GetString(root, "previously", "round", "phase"),
                    "over",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Keep the last authoritative snapshot on malformed GSI payloads.
            }
        }

        lock (_sync)
        {
            return CreateSnapshot(shouldRefreshPlayerStats);
        }
    }

    public GsiExtrasSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return CreateSnapshot(false);
        }
    }

    public void ApplyAuthoritativeStats(JsonElement players)
    {
        if (players.ValueKind != JsonValueKind.Object)
            return;

        var updated = new Dictionary<string, GsiPlayerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in players.EnumerateObject())
        {
            if (player.Value.ValueKind != JsonValueKind.Object ||
                !TryGetInt(player.Value, "damage", out var totalDamage) ||
                !TryGetInt(player.Value, "utilityDamage", out var utilityDamage) ||
                !TryGetInt(player.Value, "enemiesFlashed", out var enemiesFlashed) ||
                !TryGetInt(player.Value, "headshotKills", out var headshotKills))
            {
                continue;
            }

            updated[player.Name] = new GsiPlayerStats
            {
                TotalDamage = totalDamage,
                UtilityDamage = utilityDamage,
                EnemiesFlashed = enemiesFlashed,
                HeadshotKills = headshotKills
            };
        }

        lock (_sync)
        {
            _playerStats.Clear();
            foreach (var (steamId, stats) in updated)
                _playerStats[steamId] = stats;
        }
    }

    private static string? GetString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current) || current.ValueKind == JsonValueKind.Undefined)
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private GsiExtrasSnapshot CreateSnapshot(bool shouldRefreshPlayerStats)
    {
        return new GsiExtrasSnapshot
        {
            PlayerStats = new Dictionary<string, GsiPlayerStats>(_playerStats, StringComparer.OrdinalIgnoreCase),
            ShouldRefreshPlayerStats = shouldRefreshPlayerStats
        };
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value);
    }

}

public sealed class GsiExtrasSnapshot
{
    public Dictionary<string, GsiPlayerStats> PlayerStats { get; init; } = new();

    [JsonIgnore]
    public bool ShouldRefreshPlayerStats { get; init; }
}

public sealed class GsiPlayerStats
{
    public int TotalDamage { get; init; }
    public int UtilityDamage { get; init; }
    public int EnemiesFlashed { get; init; }
    public int HeadshotKills { get; init; }
}
