using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HlaeObsTools.Services.Graphics;

public sealed class GsiExtrasTracker
{
    private string? _currentMapName;
    private readonly Dictionary<string, Dictionary<int, int>> _roundDamages = new(StringComparer.OrdinalIgnoreCase);
    private int? _lastCtScore;
    private int? _lastTScore;

    public GsiExtrasSnapshot Update(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return BuildSnapshot();

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            var playerActivity = GetPlayerActivity(root);
            var mapChanged = UpdateCurrentMapName(root);
            var matchRestarted = DetectMatchRestart(root);
            if (mapChanged ||
                matchRestarted ||
                string.Equals(playerActivity, "menu", StringComparison.OrdinalIgnoreCase))
            {
                _roundDamages.Clear();
            }
            else
            {
                UpdateRoundDamages(root);
            }
        }
        catch
        {
            // Keep last known state on parse errors.
        }

        return BuildSnapshot();
    }

    private bool UpdateCurrentMapName(JsonElement root)
    {
        var mapName = GetString(root, "map", "name");
        if (string.IsNullOrWhiteSpace(mapName))
            return false;

        var changed = !string.Equals(mapName, _currentMapName, StringComparison.OrdinalIgnoreCase);
        _currentMapName = mapName;
        return changed;
    }

    private bool DetectMatchRestart(JsonElement root)
    {
        if (!TryGetObject(root, "map", out var map))
            return false;

        var hasCtScore = TryGetNestedInt(map, "team_ct", "score", out var ctScore);
        var hasTScore = TryGetNestedInt(map, "team_t", "score", out var tScore);

        var scoreResetToZero = hasCtScore && hasTScore &&
                               ctScore == 0 && tScore == 0 &&
                               ((_lastCtScore ?? 0) != 0 || (_lastTScore ?? 0) != 0);

        var stillZeroZeroAfterRound = hasCtScore && hasTScore &&
                                      ctScore == 0 && tScore == 0 &&
                                      _roundDamages.Count > 0 &&
                                      TryGetInt(map, "round", out var rawRound) &&
                                      rawRound > 0;

        if (hasCtScore)
            _lastCtScore = ctScore;
        if (hasTScore)
            _lastTScore = tScore;

        return scoreResetToZero || stillZeroZeroAfterRound;
    }

    private void UpdateRoundDamages(JsonElement root)
    {
        var roundNumber = GetRoundNumber(root);
        if (!roundNumber.HasValue)
            return;
        var isRoundOver = IsRoundOver(root);

        if (!TryGetObject(root, "allplayers", out var players))
            return;

        foreach (var playerProp in players.EnumerateObject())
        {
            if (!TryGetObject(playerProp.Value, "state", out var state))
                continue;
            if (!TryGetInt(state, "round_totaldmg", out var dmg))
                continue;

            if (!_roundDamages.TryGetValue(playerProp.Name, out var perRound))
            {
                perRound = new Dictionary<int, int>();
                _roundDamages[playerProp.Name] = perRound;
            }

            if (dmg != 0 || isRoundOver || perRound.ContainsKey(roundNumber.Value))
            {
                perRound[roundNumber.Value] = dmg;
            }
        }
    }

    private static int? GetRoundNumber(JsonElement root)
    {
        if (!TryGetObject(root, "map", out var map))
            return null;
        if (!TryGetInt(map, "round", out var roundZeroBased))
            return null;

        var roundNumber = roundZeroBased + 1;
        var phase = GetString(root, "phase_countdowns", "phase");
        if (string.Equals(phase, "over", StringComparison.OrdinalIgnoreCase))
            roundNumber -= 1;
        return roundNumber;
    }

    private static bool IsRoundOver(JsonElement root)
    {
        var phase = GetString(root, "phase_countdowns", "phase");
        return string.Equals(phase, "over", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetPlayerActivity(JsonElement root)
    {
        return GetString(root, "player", "activity");
    }

    private GsiExtrasSnapshot BuildSnapshot()
    {
        return new GsiExtrasSnapshot
        {
            RoundDamages = CloneNested(_roundDamages)
        };
    }

    private static Dictionary<string, Dictionary<int, int>> CloneNested(Dictionary<string, Dictionary<int, int>> source)
    {
        var result = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            result[key] = new Dictionary<int, int>(value);
        }
        return result;
    }

    private static bool TryGetObject(JsonElement root, string name, out JsonElement obj)
    {
        if (root.TryGetProperty(name, out obj) && obj.ValueKind == JsonValueKind.Object)
            return true;
        obj = default;
        return false;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string? GetString(JsonElement root, string parent, string child)
    {
        if (!TryGetObject(root, parent, out var obj))
            return null;
        return GetString(obj, child);
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var prop))
            return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
            return true;
        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        return false;
    }

    private static bool TryGetNestedInt(JsonElement root, string parent, string child, out int value)
    {
        value = 0;
        return TryGetObject(root, parent, out var obj) && TryGetInt(obj, child, out value);
    }
}

public sealed class GsiExtrasSnapshot
{
    public Dictionary<string, Dictionary<int, int>> RoundDamages { get; init; } = new();
}
