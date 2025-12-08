using System;
using System.Collections.Generic;

namespace HlaeObsTools.Services.Gsi;

public record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;
        var parts = value.Split(',');
        if (parts.Length != 3) return default;
        return new Vec3(
            double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0,
            double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ? y : 0,
            double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z) ? z : 0
        );
    }
}

public sealed class GsiWeapon
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public int AmmoClip { get; init; }
    public int AmmoClipMax { get; init; }
    public int AmmoReserve { get; init; }
}

public sealed class GsiPlayer
{
    public string SteamId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Team { get; init; } = string.Empty; // "T" or "CT"
    public Vec3 Position { get; init; }
    public Vec3 Forward { get; init; }
    public bool IsAlive { get; init; }
    public bool HasBomb { get; init; }
    public int Slot { get; init; }
    public int Health { get; init; }
    public int Armor { get; init; }
    public bool HasHelmet { get; init; }
    public bool HasDefuseKit { get; init; }
    public int Money { get; init; }
    public int EquipmentValue { get; init; }
    public int RoundKills { get; init; }
    public int RoundKillHs { get; init; }
    public int Kills { get; init; }
    public int Assists { get; init; }
    public int Deaths { get; init; }
    public int Mvps { get; init; }
    public int Score { get; init; }
    public IReadOnlyList<GsiWeapon> Weapons { get; init; } = Array.Empty<GsiWeapon>();
}

public sealed class GsiBombState
{
    public string State { get; init; } = string.Empty; // carried, dropped, planted, exploded
    public Vec3 Position { get; init; }
}

public sealed class GsiGrenade
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty; // decoy, firebomb, flashbang, frag, smoke, inferno
    public Vec3 Position { get; init; }
    public Vec3 Velocity { get; init; }
    public double LifeTime { get; init; }
    public string? OwnerSteamId { get; init; }
    public double? EffectTime { get; init; } // for smoke/decoy
    public IReadOnlyList<Vec3>? Flames { get; init; } // for inferno
}

public sealed class GsiTeam
{
    public string Side { get; init; } = string.Empty; // CT or T
    public string Name { get; init; } = string.Empty;
    public int Score { get; init; }
    public int ConsecutiveRoundLosses { get; init; }
    public int TimeoutsRemaining { get; init; }
    public int MatchesWonThisSeries { get; init; }
}

public sealed class GsiGameState
{
    public string MapName { get; init; } = string.Empty;
    public IReadOnlyList<GsiPlayer> Players { get; init; } = Array.Empty<GsiPlayer>();
    public IReadOnlyList<GsiGrenade> Grenades { get; init; } = Array.Empty<GsiGrenade>();
    public GsiBombState? Bomb { get; init; }
    public string? FocusedPlayerSteamId { get; init; }
    public long Heartbeat { get; init; }
    public GsiTeam? TeamCt { get; init; }
    public GsiTeam? TeamT { get; init; }
    public int RoundNumber { get; init; }
    public string? RoundPhase { get; init; }
    public double? PhaseEndsIn { get; init; }
}
