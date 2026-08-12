using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlaeObsTools.Services.Graphics;

public sealed class GsiExtrasTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, GsiPlayerDamageStats> _playerDamageStats = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentMapName;
    private bool _wasFreezeTime;

    public GsiExtrasSnapshot Update(string rawJson)
    {
        var enteredFreezeTime = false;
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
                        _playerDamageStats.Clear();
                    }
                    _currentMapName = mapName;
                }

                var phase = GetString(root, "phase_countdowns", "phase");
                var isFreezeTime = string.Equals(phase, "freezetime", StringComparison.OrdinalIgnoreCase);
                enteredFreezeTime = isFreezeTime && !_wasFreezeTime;
                _wasFreezeTime = isFreezeTime;
            }
            catch
            {
                // Keep the last authoritative snapshot on malformed GSI payloads.
            }
        }

        lock (_sync)
        {
            return new GsiExtrasSnapshot
            {
                PlayerDamageStats = new Dictionary<string, GsiPlayerDamageStats>(_playerDamageStats, StringComparer.OrdinalIgnoreCase),
                EnteredFreezeTime = enteredFreezeTime
            };
        }
    }

    public void ApplyAuthoritativeStats(JsonElement players)
    {
        if (players.ValueKind != JsonValueKind.Object)
            return;

        var updated = new Dictionary<string, GsiPlayerDamageStats>(StringComparer.OrdinalIgnoreCase);
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

            updated[player.Name] = new GsiPlayerDamageStats
            {
                TotalDamage = totalDamage,
                UtilityDamage = utilityDamage,
                EnemiesFlashed = enemiesFlashed,
                HeadshotKills = headshotKills
            };
        }

        lock (_sync)
        {
            _playerDamageStats.Clear();
            foreach (var (steamId, stats) in updated)
                _playerDamageStats[steamId] = stats;
        }
    }

    private static string? GetString(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var parentProp) || parentProp.ValueKind != JsonValueKind.Object ||
            !parentProp.TryGetProperty(child, out var childProp) || childProp.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return childProp.GetString();
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value);
    }

}

public sealed class GsiExtrasSnapshot
{
    public Dictionary<string, GsiPlayerDamageStats> PlayerDamageStats { get; init; } = new();

    [JsonIgnore]
    public bool EnteredFreezeTime { get; init; }
}

public sealed class GsiPlayerDamageStats
{
    public int TotalDamage { get; init; }
    public int UtilityDamage { get; init; }
    public int EnemiesFlashed { get; init; }
    public int HeadshotKills { get; init; }
}
