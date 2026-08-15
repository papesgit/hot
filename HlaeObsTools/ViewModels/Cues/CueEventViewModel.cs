using System;
using HlaeObsTools.Services.Gsi;

namespace HlaeObsTools.ViewModels.Cues;

public sealed class CueEventViewModel : ViewModelBase
{
    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };
    private double _secondsUntil;
    private double _ringProgress;
    private double _timelinePosition;
    private double _spatialOpacity;
    private string _attackerTeam = string.Empty;
    private string _victimTeam = string.Empty;
    private bool _isTimelineVisible;
    private bool _useAltBindings;

    public long Id { get; init; }
    public double GameTime { get; init; }
    public double InitialLeadSeconds { get; init; }
    public int AttackerSlot { get; init; }
    public int VictimSlot { get; init; }
    public Vec3 AttackerPosition { get; init; }
    public Vec3 VictimPosition { get; init; }
    public bool HasAttackerPosition => AttackerPosition.X != 0 || AttackerPosition.Y != 0 || AttackerPosition.Z != 0;
    public bool HasVictimPosition => VictimPosition.X != 0 || VictimPosition.Y != 0 || VictimPosition.Z != 0;
    public string Weapon { get; init; } = string.Empty;
    public string WeaponIconPath => $"avares://HlaeObsTools/Assets/hud/weapons/{NormalizeWeapon(Weapon)}.svg";
    public string AttackerSlotLabel => SlotLabel(AttackerSlot, UseAltBindings);
    public string VictimSlotLabel => SlotLabel(VictimSlot, UseAltBindings);

    public double SecondsUntil { get => _secondsUntil; internal set => SetProperty(ref _secondsUntil, value); }
    public double RingProgress { get => _ringProgress; internal set => SetProperty(ref _ringProgress, value); }
    public double TimelinePosition { get => _timelinePosition; internal set => SetProperty(ref _timelinePosition, value); }
    public double SpatialOpacity { get => _spatialOpacity; internal set { if (SetProperty(ref _spatialOpacity, value)) OnPropertyChanged(nameof(IsSpatialVisible)); } }
    public bool IsSpatialVisible => SpatialOpacity > 0 && HasAttackerPosition && HasVictimPosition;
    public string AttackerTeam { get => _attackerTeam; internal set => SetProperty(ref _attackerTeam, value); }
    public string VictimTeam { get => _victimTeam; internal set => SetProperty(ref _victimTeam, value); }
    public bool IsTimelineVisible { get => _isTimelineVisible; internal set => SetProperty(ref _isTimelineVisible, value); }
    public bool UseAltBindings
    {
        get => _useAltBindings;
        set
        {
            if (!SetProperty(ref _useAltBindings, value)) return;
            OnPropertyChanged(nameof(AttackerSlotLabel));
            OnPropertyChanged(nameof(VictimSlotLabel));
        }
    }

    private static string SlotLabel(int slot, bool useAlt)
    {
        if (slot < 0) return "?";
        if (useAlt && slot is >= 5 and <= 9) return AltBindLabels[slot - 5];
        return slot == 9 ? "0" : (slot + 1).ToString();
    }
    private static string NormalizeWeapon(string value)
    {
        var normalized = value ?? string.Empty;
        if (normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)) normalized = normalized[7..];
        return string.IsNullOrWhiteSpace(normalized) ? "knife" : normalized.ToLowerInvariant();
    }
}
