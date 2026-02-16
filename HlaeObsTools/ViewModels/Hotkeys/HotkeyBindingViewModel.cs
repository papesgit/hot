using System;
using Avalonia.Input;
using HlaeObsTools.Services.Hotkeys;

namespace HlaeObsTools.ViewModels.Hotkeys;

public class HotkeyBindingViewModel : ViewModelBase
{
    private Key _key;
    private KeyModifiers _modifiers;
    private bool _enabled = true;
    private string? _displayName;
    private string? _targetViewModelType;
    private string? _targetCommandProperty;
    private string? _targetPropertyPath;
    private Guid? _targetCampathId;
    private Guid? _targetCampathGroupId;
    private Guid? _targetCampathProfileId;
    private string? _targetCampathProfileName;
    private string? _targetGraphicsProfileName;
    private string? _targetGraphicsAtlasName;
    private string? _targetGraphicsInstanceName;
    private string? _targetGraphicsAction;
    private int? _targetAttachPresetPage;
    private int? _targetAttachPresetIndex;
    private int? _targetAttachSlot;
    private HotkeyTargetKind _targetKind = HotkeyTargetKind.Command;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Key Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value))
                OnPropertyChanged(nameof(HotkeyDisplay));
        }
    }

    public KeyModifiers Modifiers
    {
        get => _modifiers;
        set
        {
            if (SetProperty(ref _modifiers, value))
                OnPropertyChanged(nameof(HotkeyDisplay));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string? DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetViewModelType
    {
        get => _targetViewModelType;
        set => SetProperty(ref _targetViewModelType, value);
    }

    public string? TargetCommandProperty
    {
        get => _targetCommandProperty;
        set
        {
            if (SetProperty(ref _targetCommandProperty, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetPropertyPath
    {
        get => _targetPropertyPath;
        set
        {
            if (SetProperty(ref _targetPropertyPath, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public Guid? TargetCampathId
    {
        get => _targetCampathId;
        set
        {
            if (SetProperty(ref _targetCampathId, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public Guid? TargetCampathGroupId
    {
        get => _targetCampathGroupId;
        set
        {
            if (SetProperty(ref _targetCampathGroupId, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public Guid? TargetCampathProfileId
    {
        get => _targetCampathProfileId;
        set
        {
            if (SetProperty(ref _targetCampathProfileId, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetCampathProfileName
    {
        get => _targetCampathProfileName;
        set
        {
            if (SetProperty(ref _targetCampathProfileName, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetGraphicsProfileName
    {
        get => _targetGraphicsProfileName;
        set
        {
            if (SetProperty(ref _targetGraphicsProfileName, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetGraphicsAtlasName
    {
        get => _targetGraphicsAtlasName;
        set
        {
            if (SetProperty(ref _targetGraphicsAtlasName, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetGraphicsInstanceName
    {
        get => _targetGraphicsInstanceName;
        set
        {
            if (SetProperty(ref _targetGraphicsInstanceName, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetGraphicsAction
    {
        get => _targetGraphicsAction;
        set
        {
            if (SetProperty(ref _targetGraphicsAction, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public HotkeyTargetKind TargetKind
    {
        get => _targetKind;
        set => SetProperty(ref _targetKind, value);
    }

    public int? TargetAttachPresetPage
    {
        get => _targetAttachPresetPage;
        set
        {
            if (SetProperty(ref _targetAttachPresetPage, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public int? TargetAttachPresetIndex
    {
        get => _targetAttachPresetIndex;
        set
        {
            if (SetProperty(ref _targetAttachPresetIndex, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public int? TargetAttachSlot
    {
        get => _targetAttachSlot;
        set
        {
            if (SetProperty(ref _targetAttachSlot, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string HotkeyDisplay => FormatHotkey(Key, Modifiers);
    public string DisplayLabel => !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName
        : (TargetCommandProperty ?? TargetPropertyPath ?? "Unknown");

    public static HotkeyBindingViewModel FromData(HotkeyBindingData data)
    {
        return new HotkeyBindingViewModel
        {
            Id = data.Id,
            Key = data.Key,
            Modifiers = data.Modifiers,
            Enabled = data.Enabled,
            TargetKind = data.TargetKind,
            TargetViewModelType = data.TargetViewModelType,
            TargetCommandProperty = data.TargetCommandProperty,
            TargetPropertyPath = data.TargetPropertyPath,
            TargetCampathId = data.TargetCampathId,
            TargetCampathGroupId = data.TargetCampathGroupId,
            TargetCampathProfileId = data.TargetCampathProfileId,
            TargetCampathProfileName = data.TargetCampathProfileName,
            TargetGraphicsProfileName = data.TargetGraphicsProfileName,
            TargetGraphicsAtlasName = data.TargetGraphicsAtlasName,
            TargetGraphicsInstanceName = data.TargetGraphicsInstanceName,
            TargetGraphicsAction = data.TargetGraphicsAction,
            TargetAttachPresetPage = data.TargetAttachPresetPage,
            TargetAttachPresetIndex = data.TargetAttachPresetIndex,
            TargetAttachSlot = data.TargetAttachSlot,
            DisplayName = data.DisplayName
        };
    }

    public HotkeyBindingData ToData()
    {
        return new HotkeyBindingData
        {
            Id = Id,
            Key = Key,
            Modifiers = Modifiers,
            Enabled = Enabled,
            TargetKind = TargetKind,
            TargetViewModelType = TargetViewModelType,
            TargetCommandProperty = TargetCommandProperty,
            TargetPropertyPath = TargetPropertyPath,
            TargetCampathId = TargetCampathId,
            TargetCampathGroupId = TargetCampathGroupId,
            TargetCampathProfileId = TargetCampathProfileId,
            TargetCampathProfileName = TargetCampathProfileName,
            TargetGraphicsProfileName = TargetGraphicsProfileName,
            TargetGraphicsAtlasName = TargetGraphicsAtlasName,
            TargetGraphicsInstanceName = TargetGraphicsInstanceName,
            TargetGraphicsAction = TargetGraphicsAction,
            TargetAttachPresetPage = TargetAttachPresetPage,
            TargetAttachPresetIndex = TargetAttachPresetIndex,
            TargetAttachSlot = TargetAttachSlot,
            DisplayName = DisplayName
        };
    }

    private static string FormatHotkey(Key key, KeyModifiers modifiers)
    {
        if (key == Key.None)
            return string.Empty;

        var parts = new System.Collections.Generic.List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Win");

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
