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
    public string? DisplayName { get; set; }
}
