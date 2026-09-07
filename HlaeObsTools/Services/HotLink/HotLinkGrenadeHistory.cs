using System;
using System.Collections.Generic;
using System.Linq;
using HlaeObsTools.Services.Gsi;

namespace HlaeObsTools.Services.HotLink;

/// <summary>A retained GSI projectile throw, suitable for replay direction and later flash-assist cues.</summary>
public sealed record HotLinkGrenadeThrow
{
    public long Id { get; init; }
    public double GameTime { get; init; }
    public string Weapon { get; init; } = string.Empty;
    public HotLinkKillParticipant Thrower { get; init; } = new();
    public double? DetonationGameTime { get; init; }
    public Vec3? DetonationPosition { get; init; }
    public long[] KillEventIds { get; init; } = Array.Empty<long>();
}

public static class GrenadeWeapons
{
    public static string Normalize(string? weapon) => (weapon ?? string.Empty).ToLowerInvariant().Replace("weapon_", "") switch
    {
        "frag" => "hegrenade",
        "firebomb" => "molotov",
        _ => (weapon ?? string.Empty).ToLowerInvariant().Replace("weapon_", "")
    };

    // Killfeed reports burning deaths as inferno. Molotov/incendiary can still be direct-impact kills.
    public static bool IsFire(string? weapon) => Normalize(weapon) == "inferno";
    public static bool IsGrenade(string? weapon) => Normalize(weapon) is
        "hegrenade" or "flashbang" or "smokegrenade" or "decoy" or "molotov" or "incgrenade" or "inferno";
    public static bool Matches(string thrown, string outcome) => Normalize(thrown) == Normalize(outcome) ||
        (Normalize(outcome) == "inferno" && Normalize(thrown) is "molotov" or "incgrenade");
}

/// <summary>Publisher-owned GSI projectile history. Access is serialized by the publisher's lock.</summary>
public sealed class HotLinkGrenadeHistory
{
    private const double RetentionSeconds = 120;
    private const double MatchWindowSeconds = 20;
    private readonly Dictionary<string, TrackedProjectile> _projectiles = new(StringComparer.Ordinal);
    private readonly List<TrackedProjectile> _throws = new();
    private long _nextId;

    private sealed class TrackedProjectile
    {
        public required string ProjectileId { get; init; }
        public required string Weapon { get; init; }
        public required HotLinkKillParticipant Thrower { get; init; }
        public required DateTimeOffset FirstSeenUtc { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public Vec3 LastPosition { get; set; }
        public bool WasMoving { get; set; }
        public DateTimeOffset? DetonatedUtc { get; set; }
        public long Id { get; init; }
        public long[] KillEventIds { get; set; } = Array.Empty<long>();
    }

    public void Clear()
    {
        _projectiles.Clear();
        _throws.Clear();
    }

    /// <summary>Creates a throw when GSI first exposes an owned projectile, and detects its end from the same lifecycle data used by radar.</summary>
    public void RecordState(GsiGameState state, DateTimeOffset receivedUtc)
    {
        var playersBySteamId = state.Players
            .Where(player => !string.IsNullOrWhiteSpace(player.SteamId) && player.Slot is >= 0 and <= 9)
            .GroupBy(player => player.SteamId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var grenade in state.Grenades)
        {
            var weapon = GrenadeWeapons.Normalize(grenade.Type);
            if (!GrenadeWeapons.IsGrenade(weapon) || weapon == "inferno" || string.IsNullOrWhiteSpace(grenade.Id))
                continue;

            present.Add(grenade.Id);
            var moving = grenade.Velocity.X != 0 || grenade.Velocity.Y != 0 || grenade.Velocity.Z != 0;
            if (_projectiles.TryGetValue(grenade.Id, out var tracked))
            {
                tracked.LastSeenUtc = receivedUtc;
                tracked.LastPosition = grenade.Position;
                tracked.WasMoving |= moving;
                if (tracked.WasMoving && !moving) tracked.DetonatedUtc ??= receivedUtc;
                continue;
            }

            // GSI gives the projectile's owner directly. A missing owner is deliberately not guessed.
            if (string.IsNullOrWhiteSpace(grenade.OwnerSteamId) || !playersBySteamId.TryGetValue(grenade.OwnerSteamId, out var thrower))
                continue;

            tracked = new TrackedProjectile
            {
                Id = ++_nextId,
                ProjectileId = grenade.Id,
                Weapon = weapon,
                Thrower = new HotLinkKillParticipant
                {
                    ObserverSlot = thrower.Slot,
                    SteamId = thrower.SteamId,
                    Position = thrower.Position
                },
                FirstSeenUtc = receivedUtc,
                LastSeenUtc = receivedUtc,
                LastPosition = grenade.Position,
                WasMoving = moving
            };
            _projectiles.Add(grenade.Id, tracked);
            _throws.Add(tracked);
        }

        foreach (var projectileId in _projectiles.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            var tracked = _projectiles[projectileId];
            if (tracked.WasMoving) tracked.DetonatedUtc ??= receivedUtc;
            _projectiles.Remove(projectileId);
        }

        _throws.RemoveAll(throwed => (receivedUtc - throwed.FirstSeenUtc).TotalSeconds > RetentionSeconds);
        if (_throws.Count > 256) _throws.RemoveRange(0, _throws.Count - 256);
    }

    public HotLinkGrenadeThrow? AssociateKill(HotLinkPublisherKillEvent kill, string attackerSteamId)
    {
        if (kill.GameTime is not double killGameTime || !double.IsFinite(killGameTime) ||
            !GrenadeWeapons.IsGrenade(kill.Weapon) || string.IsNullOrWhiteSpace(attackerSteamId))
            return null;

        var weapon = GrenadeWeapons.Normalize(kill.Weapon);
        var candidate = _throws
            .Where(throwed => throwed.Thrower.SteamId == attackerSteamId && GrenadeWeapons.Matches(throwed.Weapon, weapon))
            .Where(throwed => kill.ReceivedUtc >= throwed.FirstSeenUtc &&
                (kill.ReceivedUtc - throwed.FirstSeenUtc).TotalSeconds <= MatchWindowSeconds)
            .OrderByDescending(throwed => throwed.FirstSeenUtc)
            .FirstOrDefault();
        if (candidate == null) return null;

        candidate.KillEventIds = candidate.KillEventIds.Append(kill.Id).Distinct().ToArray();
        return ToPublic(candidate, killGameTime, kill.ReceivedUtc);
    }

    private static HotLinkGrenadeThrow ToPublic(TrackedProjectile tracked, double referenceGameTime, DateTimeOffset referenceUtc)
    {
        var throwTime = referenceGameTime - Math.Max(0, (referenceUtc - tracked.FirstSeenUtc).TotalSeconds);
        return new HotLinkGrenadeThrow
        {
            Id = tracked.Id,
            GameTime = throwTime,
            Weapon = tracked.Weapon,
            Thrower = tracked.Thrower,
            DetonationGameTime = tracked.DetonatedUtc.HasValue
                ? referenceGameTime - Math.Max(0, (referenceUtc - tracked.DetonatedUtc.Value).TotalSeconds)
                : null,
            DetonationPosition = tracked.DetonatedUtc.HasValue ? tracked.LastPosition : null,
            KillEventIds = tracked.KillEventIds
        };
    }
}
