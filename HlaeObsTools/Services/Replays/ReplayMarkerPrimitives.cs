using System;
using System.Collections.Generic;
using System.Linq;

namespace HlaeObsTools.Services.Replays;

/// <summary>Backend-neutral conventions used by replay marker policies.</summary>
public static class ReplayMarkerPrimitives
{
    /// <summary>HLAE encodes the tenth observer/player slot as zero; HOT uses 1..10.</summary>
    public static int NormalizeObserverSlot(int slot) => slot == 0 ? 10 : slot;

    public static string BuildKillLabel(int round, IEnumerable<ReplayKillReference> kills)
    {
        var byPlayer = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var kill in kills)
        {
            if (!byPlayer.TryGetValue(kill.PlayerName, out var numbers)) { numbers = new(); byPlayer[kill.PlayerName] = numbers; order.Add(kill.PlayerName); }
            numbers.Add(kill.RoundKillNumber);
        }
        var parts = order.Select(player =>
        {
            var numbers = byPlayer[player]; numbers.Sort(); var clean = Sanitize(player);
            return numbers.Count == 1 ? $"{clean}_K{numbers[0]}" : $"{clean}_K{numbers[0]}-{numbers[^1]}";
        });
        return $"R{round}_{string.Join("_", parts)}";
    }

    public static string Sanitize(string value) => new string((value ?? string.Empty).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
}

public readonly record struct ReplayKillReference(string PlayerName, int RoundKillNumber);

/// <summary>Round-local player kill numbering shared by producers that do not own a replay clip.</summary>
public sealed class ReplayKillCounter
{
    private readonly Dictionary<(int Round, string Player), int> _counts = new();

    public int Next(int round, string playerName)
    {
        var key = (round, playerName);
        _counts.TryGetValue(key, out var current);
        current++;
        _counts[key] = current;
        return current;
    }

    public void PruneRoundsBefore(int round)
    {
        var stale = _counts.Keys.Where(key => key.Round < round).ToArray();
        foreach (var key in stale)
            _counts.Remove(key);
    }

    public void Clear() => _counts.Clear();
}

/// <summary>Backend-neutral state for a group of nearby kills that becomes one replay.</summary>
public sealed class ReplayKillGroup
{
    private readonly Dictionary<(int Round, string Player), int> _roundCounts = new();
    private readonly List<ReplayKillReference> _kills = new();
    public DateTimeOffset? FirstKillUtc { get; private set; }
    public DateTimeOffset? LastKillUtc { get; private set; }
    public int Round { get; private set; }
    public int KillCount => _kills.Count;

    /// <summary>Starts a new clip while retaining the per-round player counters.</summary>
    public void Reset(int round, DateTimeOffset now)
    {
        Round = round;
        FirstKillUtc = now;
        LastKillUtc = now;
        _kills.Clear();
    }

    /// <summary>Adds a kill to the current clip and returns its round-local number.</summary>
    public int AddKill(int round, string playerName, int? suppliedRoundKillNumber, DateTimeOffset now)
    {
        if (FirstKillUtc == null)
            Reset(round, now);

        Round = round;
        var key = (round, playerName);
        _roundCounts.TryGetValue(key, out var previous);
        var number = suppliedRoundKillNumber.GetValueOrDefault() > 0 ? suppliedRoundKillNumber!.Value : previous + 1;
        _roundCounts[key] = Math.Max(previous, number);
        _kills.Add(new ReplayKillReference(playerName, number));
        LastKillUtc = now;
        return number;
    }

    public void PruneRoundsBefore(int round)
    {
        var stale = _roundCounts.Keys.Where(key => key.Round < round).ToArray();
        foreach (var key in stale)
            _roundCounts.Remove(key);
    }

    public bool Add(int round, string playerName, int? suppliedRoundKillNumber, DateTimeOffset now, double extendWindowSeconds)
    {
        var startsNewGroup = FirstKillUtc == null || (LastKillUtc.HasValue && (now - LastKillUtc.Value).TotalSeconds > Math.Max(0, extendWindowSeconds));
        if (startsNewGroup)
            Reset(round, now);
        AddKill(round, playerName, suppliedRoundKillNumber, now);
        return startsNewGroup;
    }

    /// <summary>Discards an uncommitted clip after its backend rejected the create/update request.</summary>
    public void Clear()
    {
        Round = 0;
        FirstKillUtc = null;
        LastKillUtc = null;
        _kills.Clear();
    }

    public ReplayKillGroupSnapshot Snapshot() => new(Round, FirstKillUtc, LastKillUtc, _kills.ToArray(), BuildLabel());
    private string BuildLabel() => ReplayMarkerPrimitives.BuildKillLabel(Round, _kills);
}

public readonly record struct ReplayKillGroupSnapshot(int Round, DateTimeOffset? FirstKillUtc, DateTimeOffset? LastKillUtc, IReadOnlyList<ReplayKillReference> Kills, string Label)
{
    public int KillCount => Kills.Count;
}
