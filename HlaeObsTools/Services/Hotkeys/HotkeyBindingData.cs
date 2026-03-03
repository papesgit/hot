using System;
using Avalonia.Input;

namespace HlaeObsTools.Services.Hotkeys;

public class HotkeyBindingData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public Key Key { get; set; } = Key.None;
    public KeyModifiers Modifiers { get; set; } = KeyModifiers.None;
    public HotkeyTargetKind TargetKind { get; set; } = HotkeyTargetKind.Command;
    public string? TargetViewModelType { get; set; }
    public string? TargetCommandProperty { get; set; }
    public string? TargetPropertyPath { get; set; }
    public Guid? TargetCampathId { get; set; }
    public Guid? TargetCampathGroupId { get; set; }
    public Guid? TargetCampathProfileId { get; set; }
    public string? TargetCampathProfileName { get; set; }
    public string? TargetGraphicsProfileName { get; set; }
    public string? TargetGraphicsAtlasName { get; set; }
    public string? TargetGraphicsInstanceName { get; set; }
    public string? TargetGraphicsAction { get; set; }
    public int? TargetAttachPresetPage { get; set; }
    public int? TargetAttachPresetIndex { get; set; }
    public int? TargetAttachSlot { get; set; }
    public string? TargetVmixFunctionCategory { get; set; }
    public string? TargetVmixFunctionName { get; set; }
    public string? TargetVmixValue { get; set; }
    public int? TargetVmixInputNumber { get; set; }
    public string? TargetVmixChannel { get; set; }
    public string? TargetVmixDuration { get; set; }
    public string? TargetVmixExtraQuery { get; set; }
    public string? DisplayName { get; set; }
}
