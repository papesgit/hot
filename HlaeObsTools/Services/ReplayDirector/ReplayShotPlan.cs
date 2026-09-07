using System;
using System.Collections.Generic;
using System.Linq;
using HlaeObsTools.Services.HotLink;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.ReplayDirector;

public sealed record ReplayShotPlan(HotLinkKillEvent Kill, int TargetSlot, double Start, double End,
    bool ThrowBased, bool FollowGrenade, string? FallbackReason)
{
    public double OutcomeGameTime { get; init; } = Kill.GameTime!.Value;
    public long[] CoveredEventIds { get; init; } = new[] { Kill.Id };
    public double ThrowPostRollSeconds { get; init; } = 1;
    public const double StartGraceSeconds = 0.1;
    public const double MaxThrowSequenceSeconds = 10;

    public bool Covers(HotLinkKillEvent other) => Kill.GrenadeThrow != null &&
        other.GrenadeThrow?.Id == Kill.GrenadeThrow.Id && other.GameTime is double time &&
        (!ThrowBased || !GrenadeWeapons.IsFire(other.Weapon)) &&
        (ThrowBased ? time >= Kill.GrenadeThrow.GameTime && time <= OutcomeGameTime + 0.1 :
            Math.Abs(time - OutcomeGameTime) <= 0.1);

    public ReplayShotPlan IncludeRelatedOutcomes(IEnumerable<HotLinkKillEvent> kills, double switchLock)
    {
        if (!ThrowBased || Kill.GrenadeThrow == null) return this;
        var last = kills.Where(k => k.GrenadeThrow?.Id == Kill.GrenadeThrow.Id && !GrenadeWeapons.IsFire(k.Weapon))
            .Select(k => k.GameTime ?? OutcomeGameTime)
            .Where(t => t >= OutcomeGameTime && t + ThrowPostRollSeconds - Start <= MaxThrowSequenceSeconds)
            .DefaultIfEmpty(OutcomeGameTime).Max();
        return this with
        {
            OutcomeGameTime = last, End = last + Math.Max(ThrowPostRollSeconds, switchLock),
            CoveredEventIds = kills.Where(k => k.GrenadeThrow?.Id == Kill.GrenadeThrow.Id &&
                !GrenadeWeapons.IsFire(k.Weapon) && k.GameTime >= Kill.GrenadeThrow.GameTime && k.GameTime <= last)
                .Select(k => k.Id).Append(Kill.Id).Distinct().ToArray()
        };
    }

    public static ReplayShotPlan? Create(HotLinkKillEvent kill, HotLinkSettings settings, double now,
        IReadOnlySet<int> aliveSlots)
    {
        if (kill.GameTime is not double time || !double.IsFinite(time)) return null;
        var grenade = GrenadeWeapons.IsGrenade(kill.Weapon);
        if (grenade ? !settings.GrenadeKillsEnabled : settings.OnlyFollowMissedKills && kill.MainCaught) return null;
        string? fallback = null;
        if (grenade && !GrenadeWeapons.IsFire(kill.Weapon) && settings.GrenadePov != "Show Victim POV")
        {
            var throwPreRoll = settings.GrenadeThrowPreRollSeconds;
            var killPostRoll = settings.GrenadeKillPostRollSeconds;
            var thrown = kill.GrenadeThrow;
            if (thrown == null) fallback = "throw history unavailable or ambiguous";
            else if (thrown.Thrower.ObserverSlot != kill.Attacker.ObserverSlot ||
                !GrenadeWeapons.Matches(thrown.Weapon, GrenadeWeapons.Normalize(kill.Weapon)))
                fallback = "throw does not match the killer or grenade type";
            else if (!double.IsFinite(thrown.GameTime) || thrown.GameTime > time ||
                time - thrown.GameTime + throwPreRoll + killPostRoll > MaxThrowSequenceSeconds)
                fallback = "throw sequence too long or invalid";
            else if (now > thrown.GameTime - throwPreRoll + StartGraceSeconds)
                fallback = "insufficient delay or camera busy at throw start";
            else if (!aliveSlots.Contains(thrown.Thrower.ObserverSlot)) fallback = "thrower unavailable";
            else return new(kill, thrown.Thrower.ObserverSlot, thrown.GameTime - throwPreRoll,
                time + Math.Max(killPostRoll, settings.SwitchLockSeconds), true,
                settings.GrenadePov == "Follow grenade", null) { ThrowPostRollSeconds = killPostRoll };
        }
        var target = grenade ? kill.Victim.ObserverSlot : kill.Attacker.ObserverSlot;
        if (!aliveSlots.Contains(target)) return null;
        // A victim must be acquired before the death, not during the normal late-switch grace.
        if (now > time + (grenade ? -0.1 : 0.25)) return null;
        return new(kill, target, time - (grenade ? Math.Max(0.1, settings.PreSwitchSeconds) : settings.PreSwitchSeconds),
            time + Math.Max(0, settings.SwitchLockSeconds), false, false, fallback);
    }
}
