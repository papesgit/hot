using System;
using System.Collections.ObjectModel;
using Avalonia.Input;
using HlaeObsTools.Services.Hotkeys;
using HlaeObsTools.Services.Vmix;

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
    private string? _targetVmixFunctionCategory;
    private string? _targetVmixFunctionName;
    private string? _targetVmixValue;
    private int? _targetVmixInputNumber;
    private string? _targetVmixChannel;
    private string? _targetVmixDuration;
    private string? _targetVmixExtraQuery;
    private string? _targetExecCommand;
    private bool _vmixHasValueParameter;
    private bool _vmixHasInputParameter;
    private bool _vmixHasChannelParameter;
    private bool _vmixHasDurationParameter;
    private bool _vmixHasCustomParameter;
    private VmixInputInfo? _selectedVmixInput;
    private HotkeyTargetKind _targetKind = HotkeyTargetKind.Command;

    public Guid Id { get; set; } = Guid.NewGuid();
    public ObservableCollection<string> VmixFunctionOptions { get; } = new();

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

    public string? TargetVmixFunctionCategory
    {
        get => _targetVmixFunctionCategory;
        set
        {
            if (SetProperty(ref _targetVmixFunctionCategory, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetVmixFunctionName
    {
        get => _targetVmixFunctionName;
        set
        {
            if (SetProperty(ref _targetVmixFunctionName, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string? TargetVmixValue
    {
        get => _targetVmixValue;
        set
        {
            if (SetProperty(ref _targetVmixValue, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public int? TargetVmixInputNumber
    {
        get => _targetVmixInputNumber;
        set => SetProperty(ref _targetVmixInputNumber, value);
    }

    public string? TargetVmixChannel
    {
        get => _targetVmixChannel;
        set => SetProperty(ref _targetVmixChannel, value);
    }

    public string? TargetVmixDuration
    {
        get => _targetVmixDuration;
        set => SetProperty(ref _targetVmixDuration, value);
    }

    public string? TargetVmixExtraQuery
    {
        get => _targetVmixExtraQuery;
        set => SetProperty(ref _targetVmixExtraQuery, value);
    }

    public string? TargetExecCommand
    {
        get => _targetExecCommand;
        set
        {
            if (SetProperty(ref _targetExecCommand, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public bool VmixHasValueParameter
    {
        get => _vmixHasValueParameter;
        set => SetProperty(ref _vmixHasValueParameter, value);
    }

    public bool VmixHasInputParameter
    {
        get => _vmixHasInputParameter;
        set => SetProperty(ref _vmixHasInputParameter, value);
    }

    public bool VmixHasChannelParameter
    {
        get => _vmixHasChannelParameter;
        set => SetProperty(ref _vmixHasChannelParameter, value);
    }

    public bool VmixHasDurationParameter
    {
        get => _vmixHasDurationParameter;
        set => SetProperty(ref _vmixHasDurationParameter, value);
    }

    public bool VmixHasCustomParameter
    {
        get => _vmixHasCustomParameter;
        set => SetProperty(ref _vmixHasCustomParameter, value);
    }

    public VmixInputInfo? SelectedVmixInput
    {
        get => _selectedVmixInput;
        set
        {
            if (!SetProperty(ref _selectedVmixInput, value))
                return;

            if (value != null)
                TargetVmixInputNumber = value.Number;
        }
    }

    public string HotkeyDisplay => FormatHotkey(Key, Modifiers);
    public string DisplayLabel => !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName
        : (TargetExecCommand ?? TargetVmixFunctionName ?? TargetCommandProperty ?? TargetPropertyPath ?? "Unknown");

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
            TargetVmixFunctionCategory = data.TargetVmixFunctionCategory,
            TargetVmixFunctionName = data.TargetVmixFunctionName,
            TargetVmixValue = data.TargetVmixValue,
            TargetVmixInputNumber = data.TargetVmixInputNumber,
            TargetVmixChannel = data.TargetVmixChannel,
            TargetVmixDuration = data.TargetVmixDuration,
            TargetVmixExtraQuery = data.TargetVmixExtraQuery,
            TargetExecCommand = data.TargetExecCommand,
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
            TargetVmixFunctionCategory = TargetVmixFunctionCategory,
            TargetVmixFunctionName = TargetVmixFunctionName,
            TargetVmixValue = TargetVmixValue,
            TargetVmixInputNumber = TargetVmixInputNumber,
            TargetVmixChannel = TargetVmixChannel,
            TargetVmixDuration = TargetVmixDuration,
            TargetVmixExtraQuery = TargetVmixExtraQuery,
            TargetExecCommand = TargetExecCommand,
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
