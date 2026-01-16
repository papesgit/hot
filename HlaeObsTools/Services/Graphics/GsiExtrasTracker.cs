using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HlaeObsTools.Services.Graphics;

public sealed class GsiExtrasTracker
{
    private string? _lastKnownMapName;
    private readonly Dictionary<string, Dictionary<int, int>> _roundDamages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _moneyAtStartOfRound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastKnownPlayerObserverSlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _observerSlotMapped = new(StringComparer.OrdinalIgnoreCase);
    private GsiBombPlantedCountdown? _lastKnownBombPlantedCountdown;
    private string? _lastRoundPhase;

    public GsiExtrasSnapshot Update(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return BuildSnapshot();

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            var mapChanged = UpdateLastKnownMapName(root);
            UpdateLastKnownBombPlantedCountdown(root);
            UpdateLastKnownPlayerObserverSlot(root);

            var roundPhase = GetRoundPhase(root);
            var wasFreezetime = string.Equals(_lastRoundPhase, "freezetime", StringComparison.OrdinalIgnoreCase);
            var isFreezetime = string.Equals(roundPhase, "freezetime", StringComparison.OrdinalIgnoreCase);
            _lastRoundPhase = roundPhase;

            if (!wasFreezetime && isFreezetime)
            {
                UpdateMoneyAtStartOfRound(root);
            }

            var playerActivity = GetPlayerActivity(root);
            if (mapChanged || string.Equals(playerActivity, "menu", StringComparison.OrdinalIgnoreCase))
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

    private bool UpdateLastKnownMapName(JsonElement root)
    {
        var mapName = GetString(root, "map", "name");
        var mapChanged = !string.IsNullOrWhiteSpace(mapName) &&
                         !string.Equals(mapName, _lastKnownMapName, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(mapName))
            _lastKnownMapName = mapName;
        return mapChanged;
    }

    private void UpdateLastKnownBombPlantedCountdown(JsonElement root)
    {
        if (!TryGetObject(root, "bomb", out var bomb))
        {
            _lastKnownBombPlantedCountdown = null;
            return;
        }

        var state = GetString(bomb, "state");
        if (string.Equals(state, "defusing", StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.Equals(state, "planted", StringComparison.OrdinalIgnoreCase))
        {
            _lastKnownBombPlantedCountdown = null;
            return;
        }

        var countdown = GetDouble(bomb, "countdown");
        if (!countdown.HasValue)
            return;

        _lastKnownBombPlantedCountdown = new GsiBombPlantedCountdown
        {
            UnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Value = countdown.Value
        };
    }

    private void UpdateLastKnownPlayerObserverSlot(JsonElement root)
    {
        if (!TryGetObject(root, "allplayers", out var players))
            return;

        foreach (var playerProp in players.EnumerateObject())
        {
            if (!TryGetInt(playerProp.Value, "observer_slot", out var slot))
                continue;
            _lastKnownPlayerObserverSlot[playerProp.Name] = slot;
            _observerSlotMapped[playerProp.Name] = MapObserverSlot(slot);
        }
    }

    private void UpdateMoneyAtStartOfRound(JsonElement root)
    {
        if (!TryGetObject(root, "allplayers", out var players))
            return;

        _moneyAtStartOfRound.Clear();
        foreach (var playerProp in players.EnumerateObject())
        {
            if (!TryGetObject(playerProp.Value, "state", out var state))
                continue;
            if (!TryGetInt(state, "money", out var money))
                continue;
            _moneyAtStartOfRound[playerProp.Name] = money;
        }
    }

    private void UpdateRoundDamages(JsonElement root)
    {
        var roundNumber = GetRoundNumber(root);
        if (!roundNumber.HasValue)
            return;

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

            if (dmg != 0 || !perRound.ContainsKey(roundNumber.Value))
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

    private static int MapObserverSlot(int rawSlot)
    {
        if (rawSlot == 9) return 0;
        return rawSlot + 1;
    }

    private static string? GetRoundPhase(JsonElement root)
    {
        var phase = GetString(root, "phase_countdowns", "phase");
        if (!string.IsNullOrWhiteSpace(phase))
            return phase;
        return GetString(root, "round", "phase");
    }

    private static string? GetPlayerActivity(JsonElement root)
    {
        return GetString(root, "player", "activity");
    }

    private GsiExtrasSnapshot BuildSnapshot()
    {
        return new GsiExtrasSnapshot
        {
            UnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastKnownMapName = _lastKnownMapName,
            LastKnownBombPlantedCountdown = _lastKnownBombPlantedCountdown,
            MoneyAtStartOfRound = Clone(_moneyAtStartOfRound),
            LastKnownPlayerObserverSlot = Clone(_lastKnownPlayerObserverSlot),
            ObserverSlotMapped = Clone(_observerSlotMapped),
            RoundDamages = CloneNested(_roundDamages)
        };
    }

    private static Dictionary<string, int> Clone(Dictionary<string, int> source)
    {
        return new Dictionary<string, int>(source, StringComparer.OrdinalIgnoreCase);
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

    private static double? GetDouble(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var val))
                return val;
            if (prop.ValueKind == JsonValueKind.String &&
                double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return null;
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
}

public sealed class GsiExtrasSnapshot
{
    public long UnixTimestamp { get; init; }
    public string? LastKnownMapName { get; init; }
    public GsiBombPlantedCountdown? LastKnownBombPlantedCountdown { get; init; }
    public Dictionary<string, int> MoneyAtStartOfRound { get; init; } = new();
    public Dictionary<string, int> LastKnownPlayerObserverSlot { get; init; } = new();
    public Dictionary<string, int> ObserverSlotMapped { get; init; } = new();
    public Dictionary<string, Dictionary<int, int>> RoundDamages { get; init; } = new();
}

public sealed class GsiBombPlantedCountdown
{
    public long UnixTimestamp { get; init; }
    public double Value { get; init; }
}
