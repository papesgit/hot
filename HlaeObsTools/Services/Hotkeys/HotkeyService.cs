using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HlaeObsTools.Services.Vmix;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Services.Hotkeys;

public sealed class HotkeyService
{
    private readonly List<object> _commandContexts = new();
    private readonly List<HotkeyBindingData> _bindings = new();
    private VmixApiClient? _vmixApiClient;
    private Control? _hoveredControl;
    private string _statusMessage = "Hotkey mode disabled.";
    private Guid? _rebindId;
    private HotkeyBindingData? _rebindTarget;
    private bool _isBindingMode;

    public event EventHandler<HotkeyBindingCapturedEventArgs>? BindingCaptured;
    public event EventHandler<bool>? BindingModeChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<HotkeyHoverChangedEventArgs>? HoverTargetChanged;
    public event EventHandler? BindingsChanged;

    public bool IsBindingMode => _isBindingMode;
    public string StatusMessage => _statusMessage;
    public Control? HoveredControl => _hoveredControl;

    public void RegisterCommandContext(object context)
    {
        if (!_commandContexts.Contains(context))
        {
            _commandContexts.Add(context);
        }
    }

    public void SetBindings(IEnumerable<HotkeyBindingData> bindings)
    {
        _bindings.Clear();
        _bindings.AddRange(bindings);
        BindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? GetCampathHotkeyDisplay(Guid profileId, Guid targetId, bool isGroup)
    {
        var binding = _bindings.FirstOrDefault(binding =>
            binding.Enabled &&
            binding.TargetKind == (isGroup ? HotkeyTargetKind.CampathGroup : HotkeyTargetKind.Campath) &&
            binding.TargetCampathProfileId == profileId &&
            (isGroup ? binding.TargetCampathGroupId : binding.TargetCampathId) == targetId);

        return binding == null ? null : FormatHotkeyForBadge(binding.Key, binding.Modifiers);
    }

    private static string FormatHotkeyForBadge(Key key, KeyModifiers modifiers)
    {
        if (key == Key.None)
            return string.Empty;

        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");

        parts.Add(FormatKeyForBadge(key));
        return string.Join("+", parts);
    }

    private static string FormatKeyForBadge(Key key)
    {
        var name = key.ToString();
        if (name.StartsWith("NumPad", StringComparison.Ordinal))
            return "Num" + name[6..];

        return name switch
        {
            "PageUp" => "PgUp",
            "PageDown" => "PgDn",
            "CapsLock" => "Caps",
            "Scroll" => "ScrLk",
            "PrintScreen" => "PrtSc",
            "Back" => "Bksp",
            "Delete" => "Del",
            "Insert" => "Ins",
            "Return" => "Enter",
            "OemPlus" => "+",
            "OemMinus" => "-",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemQuestion" => "?",
            "OemSemicolon" => ";",
            "OemQuotes" => "'",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "OemBackslash" => "\\",
            "OemTilde" => "~",
            _ => name
        };
    }

    public void SetVmixApiClient(VmixApiClient vmixApiClient)
    {
        _vmixApiClient = vmixApiClient;
    }

    public void BeginCapture(Guid? rebindId = null)
    {
        _rebindId = rebindId;
        _rebindTarget = null;
        SetBindingMode(true);
        UpdateStatus("Hover a button or toggle and press a key combo (Esc to exit).");
    }

    public void BeginRebind(HotkeyBindingData binding)
    {
        _rebindId = binding.Id;
        _rebindTarget = binding;
        SetBindingMode(true);
        UpdateStatus("Press a new key combo (Esc to cancel).");
    }

    public void EndCapture()
    {
        _rebindId = null;
        _rebindTarget = null;
        ClearHoveredControl();
        SetBindingMode(false);
        UpdateStatus("Hotkey mode disabled.");
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (!_isBindingMode)
            return;

        var control = FindHotkeyTarget(e.Source);
        if (control == null || !IsBindableControl(control))
        {
            ClearHoveredControl();
            return;
        }

        if (!ReferenceEquals(_hoveredControl, control))
        {
            _hoveredControl = control;
            HoverTargetChanged?.Invoke(this, new HotkeyHoverChangedEventArgs(_hoveredControl));
        }
    }

    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (_isBindingMode)
        {
            if (e.Key == Key.Escape)
            {
                EndCapture();
                return true;
            }

            return TryCaptureBinding(e);
        }

        return TryExecuteHotkey(e);
    }

    private bool TryExecuteHotkey(KeyEventArgs e)
    {
        if (IsBlacklisted(e.Key, e.KeyModifiers))
            return false;

        if (IsModifierKey(e.Key))
            return false;

        var matches = _bindings
            .Where(b => b.Enabled && b.Key == e.Key && b.Modifiers == e.KeyModifiers)
            .ToList();

        if (matches.Count == 0)
            return false;

        foreach (var binding in matches)
        {
            if (TryExecuteBinding(binding))
                return true;
        }

        return false;
    }

    private bool TryExecuteBinding(HotkeyBindingData binding)
    {
        if (binding.TargetKind == HotkeyTargetKind.Command)
        {
            if (!TryResolveCommand(binding, out var command))
                return false;

            if (!command.CanExecute(null))
                return false;

            command.Execute(null);
            return true;
        }

        if (binding.TargetKind == HotkeyTargetKind.BoolProperty)
        {
            if (!TryResolveBoolProperty(binding, out var target, out var property))
                return false;

            var current = property.GetValue(target);
            bool nextValue = false;
            if (current is bool b)
            {
                nextValue = !b;
            }
            else if (current == null && property.PropertyType == typeof(bool?))
            {
                nextValue = true;
            }

            if (property.PropertyType == typeof(bool?))
                property.SetValue(target, (bool?)nextValue);
            else
                property.SetValue(target, nextValue);

            return true;
        }

        if (binding.TargetKind == HotkeyTargetKind.Campath)
        {
            if (!TryResolveCampath(binding.TargetCampathId, binding.TargetCampathProfileId, out var campathsVm, out var campath))
                return false;

            _ = campathsVm.PlayCampathAsync(campath);
            return true;
        }

        if (binding.TargetKind == HotkeyTargetKind.CampathGroup)
        {
            if (!TryResolveCampathGroup(binding.TargetCampathGroupId, binding.TargetCampathProfileId, out var campathsVm, out var group))
                return false;

            _ = campathsVm.PlayCampathGroupAsync(group);
            return true;
        }

        if (binding.TargetKind == HotkeyTargetKind.GraphicsAtlasAction)
        {
            if (!TryResolveGraphicsDock(binding.TargetGraphicsProfileName, out var graphicsVm))
                return false;

            _ = graphicsVm.ExecuteAtlasHotkeyActionAsync(binding.TargetGraphicsAtlasName, binding.TargetGraphicsAction);
            return true;
        }

        if (binding.TargetKind == HotkeyTargetKind.GraphicsInstanceAction)
        {
            if (!TryResolveGraphicsDock(binding.TargetGraphicsProfileName, out var graphicsVm))
                return false;

            _ = graphicsVm.ExecuteInstanceHotkeyActionAsync(binding.TargetGraphicsInstanceName, binding.TargetGraphicsAction);
            return true;
        }

        if (binding.TargetKind == HotkeyTargetKind.AttachPresetSlotAction)
        {
            if (binding.TargetAttachPresetPage == null || binding.TargetAttachPresetIndex == null || binding.TargetAttachSlot == null)
                return false;

            var settingsVm = _commandContexts.OfType<SettingsDockViewModel>().FirstOrDefault();
            if (settingsVm == null)
                return false;

            _ = settingsVm.ExecuteAttachPresetHotkeyActionAsync(
                binding.TargetAttachPresetPage.Value,
                binding.TargetAttachPresetIndex.Value,
                binding.TargetAttachSlot.Value);
            return true;
        }

        if (binding.TargetKind == HotkeyTargetKind.VmixFunction)
        {
            if (_vmixApiClient == null || string.IsNullOrWhiteSpace(binding.TargetVmixFunctionName))
                return false;

            _ = ExecuteVmixBindingAsync(binding);
            return true;
        }

        if (binding.TargetKind == HotkeyTargetKind.ExecCommand)
        {
            if (string.IsNullOrWhiteSpace(binding.TargetExecCommand))
                return false;

            var settingsVm = _commandContexts.OfType<SettingsDockViewModel>().FirstOrDefault();
            if (settingsVm == null)
                return false;

            _ = settingsVm.ExecuteHotkeyCommandAsync(binding.TargetExecCommand);
            return true;
        }

        return false;
    }

    private async Task ExecuteVmixBindingAsync(HotkeyBindingData binding)
    {
        if (_vmixApiClient == null || string.IsNullOrWhiteSpace(binding.TargetVmixFunctionName))
            return;

        var call = new VmixFunctionCall
        {
            Function = binding.TargetVmixFunctionName,
            Value = binding.TargetVmixValue,
            Input = binding.TargetVmixInputNumber,
            Channel = binding.TargetVmixChannel,
            Duration = binding.TargetVmixDuration,
            ExtraQuery = binding.TargetVmixExtraQuery
        };

        var ok = await _vmixApiClient.ExecuteFunctionAsync(call, CancellationToken.None, binding.DisplayName).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (ok)
                UpdateStatus($"Executed vMix: {binding.TargetVmixFunctionName}");
            else
                UpdateStatus($"vMix request failed: {binding.TargetVmixFunctionName}");
        });
    }

    private bool TryCaptureBinding(KeyEventArgs e)
    {
        if (IsBlacklisted(e.Key, e.KeyModifiers))
        {
            UpdateStatus("That key combo is reserved.");
            return true;
        }

        if (IsModifierKey(e.Key) || e.Key == Key.None)
        {
            UpdateStatus("Press a non-modifier key.");
            return true;
        }

        if (_rebindTarget != null)
        {
            var binding = new HotkeyBindingData
            {
                Id = _rebindTarget.Id,
                Enabled = _rebindTarget.Enabled,
                Key = e.Key,
                Modifiers = e.KeyModifiers,
                TargetKind = _rebindTarget.TargetKind,
                TargetViewModelType = _rebindTarget.TargetViewModelType,
                TargetCommandProperty = _rebindTarget.TargetCommandProperty,
                TargetPropertyPath = _rebindTarget.TargetPropertyPath,
                TargetCampathId = _rebindTarget.TargetCampathId,
                TargetCampathGroupId = _rebindTarget.TargetCampathGroupId,
                TargetCampathProfileId = _rebindTarget.TargetCampathProfileId,
                TargetCampathProfileName = _rebindTarget.TargetCampathProfileName,
                TargetGraphicsProfileName = _rebindTarget.TargetGraphicsProfileName,
                TargetGraphicsAtlasName = _rebindTarget.TargetGraphicsAtlasName,
                TargetGraphicsInstanceName = _rebindTarget.TargetGraphicsInstanceName,
                TargetGraphicsAction = _rebindTarget.TargetGraphicsAction,
                TargetAttachPresetPage = _rebindTarget.TargetAttachPresetPage,
                TargetAttachPresetIndex = _rebindTarget.TargetAttachPresetIndex,
                TargetAttachSlot = _rebindTarget.TargetAttachSlot,
                TargetVmixFunctionCategory = _rebindTarget.TargetVmixFunctionCategory,
                TargetVmixFunctionName = _rebindTarget.TargetVmixFunctionName,
                TargetVmixValue = _rebindTarget.TargetVmixValue,
                TargetVmixInputNumber = _rebindTarget.TargetVmixInputNumber,
                TargetVmixChannel = _rebindTarget.TargetVmixChannel,
                TargetVmixDuration = _rebindTarget.TargetVmixDuration,
                TargetVmixExtraQuery = _rebindTarget.TargetVmixExtraQuery,
                TargetExecCommand = _rebindTarget.TargetExecCommand,
                DisplayName = _rebindTarget.DisplayName
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, _rebindId));
            UpdateStatus($"Rebound to {FormatHotkey(binding.Key, binding.Modifiers)}.");
            EndCapture();
            return true;
        }

        if (_hoveredControl == null)
        {
            Console.WriteLine("[Hotkeys] Capture: no hovered control. Pointer move not detected or control not bindable.");
            UpdateStatus("Hover a button or toggle first.");
            return true;
        }

        if (TryGetCommandBindingTarget(_hoveredControl, out var viewModelType, out var commandProperty, out var displayName))
        {
            var binding = new HotkeyBindingData
            {
                Id = _rebindId ?? Guid.NewGuid(),
                Enabled = true,
                Key = e.Key,
                Modifiers = e.KeyModifiers,
                TargetKind = HotkeyTargetKind.Command,
                TargetViewModelType = viewModelType,
                TargetCommandProperty = commandProperty,
                DisplayName = displayName
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, _rebindId));
            UpdateStatus($"Bound {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
            return true;
        }

        if (TryGetBoolBindingTarget(_hoveredControl, out var boolViewModelType, out var propertyPath, out var boolDisplayName))
        {
            var binding = new HotkeyBindingData
            {
                Id = _rebindId ?? Guid.NewGuid(),
                Enabled = true,
                Key = e.Key,
                Modifiers = e.KeyModifiers,
                TargetKind = HotkeyTargetKind.BoolProperty,
                TargetViewModelType = boolViewModelType,
                TargetPropertyPath = propertyPath,
                DisplayName = boolDisplayName
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, _rebindId));
            UpdateStatus($"Bound {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
            return true;
        }

        if (TryGetGraphicsAtlasTarget(_hoveredControl, out var graphicsProfileName, out var graphicsAtlasName, out var graphicsAtlasAction, out var graphicsAtlasDisplay))
        {
            var binding = new HotkeyBindingData
            {
                Id = _rebindId ?? Guid.NewGuid(),
                Enabled = true,
                Key = e.Key,
                Modifiers = e.KeyModifiers,
                TargetKind = HotkeyTargetKind.GraphicsAtlasAction,
                TargetViewModelType = typeof(GraphicsDockViewModel).FullName,
                TargetGraphicsProfileName = graphicsProfileName,
                TargetGraphicsAtlasName = graphicsAtlasName,
                TargetGraphicsAction = graphicsAtlasAction,
                DisplayName = graphicsAtlasDisplay
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, _rebindId));
            UpdateStatus($"Bound {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
            return true;
        }

        if (TryGetGraphicsInstanceTarget(_hoveredControl, out var graphicsInstanceProfileName, out var graphicsInstanceName, out var graphicsInstanceAction, out var graphicsInstanceDisplay))
        {
            var binding = new HotkeyBindingData
            {
                Id = _rebindId ?? Guid.NewGuid(),
                Enabled = true,
                Key = e.Key,
                Modifiers = e.KeyModifiers,
                TargetKind = HotkeyTargetKind.GraphicsInstanceAction,
                TargetViewModelType = typeof(GraphicsDockViewModel).FullName,
                TargetGraphicsProfileName = graphicsInstanceProfileName,
                TargetGraphicsInstanceName = graphicsInstanceName,
                TargetGraphicsAction = graphicsInstanceAction,
                DisplayName = graphicsInstanceDisplay
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, _rebindId));
            UpdateStatus($"Bound {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
            return true;
        }

        if (TryGetAttachPresetTarget(_hoveredControl, out var attachPresetPage, out var attachPresetIndex, out var attachSlot, out var attachDisplay))
        {
            var binding = new HotkeyBindingData
            {
                Id = _rebindId ?? Guid.NewGuid(),
                Enabled = true,
                Key = e.Key,
                Modifiers = e.KeyModifiers,
                TargetKind = HotkeyTargetKind.AttachPresetSlotAction,
                TargetViewModelType = typeof(SettingsDockViewModel).FullName,
                TargetAttachPresetPage = attachPresetPage,
                TargetAttachPresetIndex = attachPresetIndex,
                TargetAttachSlot = attachSlot,
                DisplayName = attachDisplay
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, _rebindId));
            UpdateStatus($"Bound {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
            return true;
        }

        if (TryGetCampathTarget(_hoveredControl, out var campathId, out var campathName))
        {
            if (!TryGetActiveCampathProfile(out var profileId, out var profileName))
            {
                UpdateStatus("No active campath profile.");
                return true;
            }

            var rebindId = _rebindId ?? FindExistingCampathBindingId(profileId, campathId, isGroup: false);
            var binding = new HotkeyBindingData
            {
                Id = rebindId ?? Guid.NewGuid(),
                Enabled = true,
                Key = e.Key,
                Modifiers = e.KeyModifiers,
                TargetKind = HotkeyTargetKind.Campath,
                TargetViewModelType = typeof(CampathsDockViewModel).FullName,
                TargetCampathId = campathId,
                TargetCampathProfileId = profileId,
                TargetCampathProfileName = profileName,
                DisplayName = campathName
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, rebindId));
            UpdateStatus($"{(rebindId.HasValue ? "Rebound" : "Bound")} {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
            return true;
        }

        if (TryGetCampathGroupTarget(_hoveredControl, out var groupId, out var groupName))
        {
            if (!TryGetActiveCampathProfile(out var profileId, out var profileName))
            {
                UpdateStatus("No active campath profile.");
                return true;
            }

            var rebindId = _rebindId ?? FindExistingCampathBindingId(profileId, groupId, isGroup: true);
            var binding = new HotkeyBindingData
            {
                Id = rebindId ?? Guid.NewGuid(),
                Enabled = true,
                Key = e.Key,
                Modifiers = e.KeyModifiers,
                TargetKind = HotkeyTargetKind.CampathGroup,
                TargetViewModelType = typeof(CampathsDockViewModel).FullName,
                TargetCampathGroupId = groupId,
                TargetCampathProfileId = profileId,
                TargetCampathProfileName = profileName,
                DisplayName = groupName
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, rebindId));
            UpdateStatus($"{(rebindId.HasValue ? "Rebound" : "Bound")} {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
            return true;
        }

        Console.WriteLine($"[Hotkeys] Capture: hovered control not bindable: {_hoveredControl.GetType().Name}. {GetBindFailureReason(_hoveredControl)}");
        UpdateStatus("That control cannot be bound yet.");
        return true;
    }

    private Guid? FindExistingCampathBindingId(Guid profileId, Guid targetId, bool isGroup)
    {
        var binding = _bindings.FirstOrDefault(binding =>
            binding.TargetKind == (isGroup ? HotkeyTargetKind.CampathGroup : HotkeyTargetKind.Campath) &&
            binding.TargetCampathProfileId == profileId &&
            (isGroup ? binding.TargetCampathGroupId : binding.TargetCampathId) == targetId);

        return binding?.Id;
    }

    private string GetBindFailureReason(Control control)
    {
        if (control is ToggleButton toggle)
        {
            if (!string.IsNullOrWhiteSpace(HotkeyTarget.GetGraphicsAtlasName(toggle)) &&
                !string.IsNullOrWhiteSpace(HotkeyTarget.GetGraphicsAction(toggle)))
                return "Graphics atlas toggle should be bindable.";

            if (!string.IsNullOrWhiteSpace(HotkeyTarget.GetGraphicsInstanceName(toggle)) &&
                !string.IsNullOrWhiteSpace(HotkeyTarget.GetGraphicsAction(toggle)))
                return "Graphics instance toggle should be bindable.";

            var explicitPath = HotkeyTarget.GetPath(toggle);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                var contextExplicit = toggle.DataContext;
                if (contextExplicit == null)
                    return "Toggle has no DataContext.";

                if (!_commandContexts.Any(c => c.GetType() == contextExplicit.GetType()))
                    return $"DataContext type {contextExplicit.GetType().Name} is not registered.";

                if (!TryResolveBoolProperty(contextExplicit, explicitPath, out _, out _))
                    return $"Property path '{explicitPath}' not found or not a writable bool.";

                return "Attached path is valid but still not bindable.";
            }

            if (!TryGetBindingExpression(toggle, ToggleButton.IsCheckedProperty, out var expression))
                return "No binding expression on IsChecked.";

            var binding = GetBindingFromExpression(expression);
            if (binding == null)
            {
                var pathFromExpression = GetPathFromExpression(expression);
                if (string.IsNullOrWhiteSpace(pathFromExpression))
                    return "Binding expression has no binding or path.";

                var contextFallback = toggle.DataContext;
                if (contextFallback == null)
                    return "Toggle has no DataContext.";

                if (!_commandContexts.Any(c => c.GetType() == contextFallback.GetType()))
                    return $"DataContext type {contextFallback.GetType().Name} is not registered.";

                if (!TryResolveBoolProperty(contextFallback, pathFromExpression, out _, out _))
                    return $"Property path '{pathFromExpression}' not found or not a writable bool.";

                return "Binding has path but is not a standard Binding object.";
            }

            if (binding.Source != null || binding.ElementName != null || binding.RelativeSource != null)
                return "Binding uses Source/ElementName/RelativeSource.";

            if (string.IsNullOrWhiteSpace(binding.Path))
                return "Binding has no path.";

            var context = toggle.DataContext;
            if (context == null)
                return "Toggle has no DataContext.";

            if (!_commandContexts.Any(c => c.GetType() == context.GetType()))
                return $"DataContext type {context.GetType().Name} is not registered.";

            if (!TryResolveBoolProperty(context, binding.Path, out _, out _))
                return $"Property path '{binding.Path}' not found or not a writable bool.";

            return "Unknown toggle bind failure.";
        }

        if (control is Border border)
        {
            var campathId = HotkeyTarget.GetCampathId(border);
            if (campathId != null)
                return "Campath target should be bindable.";

            var groupId = HotkeyTarget.GetCampathGroupId(border);
            if (groupId != null)
                return "Campath group target should be bindable.";
        }

        if (control is Button button)
        {
            if (TryGetAttachPresetTarget(button, out _, out _, out _, out _))
                return "Attach preset action should be bindable.";

            if (!string.IsNullOrWhiteSpace(HotkeyTarget.GetGraphicsAtlasName(button)) &&
                !string.IsNullOrWhiteSpace(HotkeyTarget.GetGraphicsAction(button)))
                return "Graphics atlas target should be bindable.";

            if (!string.IsNullOrWhiteSpace(HotkeyTarget.GetGraphicsInstanceName(button)) &&
                !string.IsNullOrWhiteSpace(HotkeyTarget.GetGraphicsAction(button)))
                return "Graphics instance target should be bindable.";

            if (button.Command == null)
                return "Button has no Command.";
            if (button.CommandParameter != null)
                return "Button has CommandParameter.";
            return "Command not found on registered contexts.";
        }

        return "Unsupported control type.";
    }

    private bool TryResolveCommand(HotkeyBindingData binding, out ICommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(binding.TargetViewModelType) || string.IsNullOrWhiteSpace(binding.TargetCommandProperty))
            return false;

        var context = _commandContexts.FirstOrDefault(c => c.GetType().FullName == binding.TargetViewModelType);
        if (context == null)
            return false;

        var property = context.GetType().GetProperty(binding.TargetCommandProperty, BindingFlags.Instance | BindingFlags.Public);
        if (property == null)
            return false;

        if (property.GetValue(context) is ICommand cmd)
        {
            command = cmd;
            return true;
        }

        return false;
    }

    private bool IsBindableControl(Control control)
    {
        return TryGetCommandBindingTarget(control, out _, out _, out _)
            || TryGetBoolBindingTarget(control, out _, out _, out _)
            || TryGetGraphicsAtlasTarget(control, out _, out _, out _, out _)
            || TryGetGraphicsInstanceTarget(control, out _, out _, out _, out _)
            || TryGetAttachPresetTarget(control, out _, out _, out _, out _)
            || TryGetCampathTarget(control, out _, out _)
            || TryGetCampathGroupTarget(control, out _, out _);
    }

    private bool TryGetCommandBindingTarget(Control control, out string viewModelType, out string commandProperty, out string displayName)
    {
        viewModelType = string.Empty;
        commandProperty = string.Empty;
        displayName = string.Empty;

        if (control is not Button button)
            return false;

        var command = button.Command;
        if (command == null || button.CommandParameter != null)
            return false;

        foreach (var context in _commandContexts)
        {
            var type = context.GetType();
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(ICommand).IsAssignableFrom(property.PropertyType))
                    continue;

                if (ReferenceEquals(property.GetValue(context), command))
                {
                    viewModelType = type.FullName ?? type.Name;
                    commandProperty = property.Name;
                    displayName = GetButtonDisplayName(button, type.Name, property.Name);
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryGetBoolBindingTarget(Control control, out string viewModelType, out string propertyPath, out string displayName)
    {
        viewModelType = string.Empty;
        propertyPath = string.Empty;
        displayName = string.Empty;

        if (control is not ToggleButton toggle)
            return false;

        var path = HotkeyTarget.GetPath(toggle);
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!TryGetBindingPath(toggle, ToggleButton.IsCheckedProperty, out path))
                return false;
        }

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var context = toggle.DataContext;
        if (context == null)
            return false;

        if (!_commandContexts.Any(c => c.GetType() == context.GetType()))
            return false;

        if (!TryResolveBoolProperty(context, path, out _, out _))
            return false;

        viewModelType = context.GetType().FullName ?? context.GetType().Name;
        propertyPath = path;
        displayName = GetToggleDisplayName(toggle, context.GetType().Name, path);
        return true;
    }

    private bool TryResolveBoolProperty(HotkeyBindingData binding, out object target, out PropertyInfo property)
    {
        target = null!;
        property = null!;

        if (string.IsNullOrWhiteSpace(binding.TargetViewModelType) || string.IsNullOrWhiteSpace(binding.TargetPropertyPath))
            return false;

        var context = _commandContexts.FirstOrDefault(c => c.GetType().FullName == binding.TargetViewModelType);
        if (context == null)
            return false;

        return TryResolveBoolProperty(context, binding.TargetPropertyPath, out target, out property);
    }

    private static bool TryResolveBoolProperty(object context, string propertyPath, out object target, out PropertyInfo property)
    {
        target = context;
        property = null!;

        var parts = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        object? current = context;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (current == null)
                return false;

            var intermediate = current.GetType().GetProperty(parts[i], BindingFlags.Instance | BindingFlags.Public);
            if (intermediate == null)
                return false;

            current = intermediate.GetValue(current);
        }

        if (current == null)
            return false;

        var finalProp = current.GetType().GetProperty(parts[^1], BindingFlags.Instance | BindingFlags.Public);
        if (finalProp == null || !finalProp.CanWrite)
            return false;

        if (finalProp.PropertyType != typeof(bool) && finalProp.PropertyType != typeof(bool?))
            return false;

        target = current;
        property = finalProp;
        return true;
    }

    private static string GetButtonDisplayName(Button button, string typeName, string commandProperty)
    {
        if (button.Content is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
            return textBlock.Text;

        var content = button.Content?.ToString();
        if (!string.IsNullOrWhiteSpace(content))
            return content;

        return $"{typeName}.{commandProperty}";
    }

    private static string GetToggleDisplayName(ToggleButton toggle, string typeName, string propertyPath)
    {
        if (toggle.Content is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
            return textBlock.Text;

        var content = toggle.Content?.ToString();
        if (!string.IsNullOrWhiteSpace(content))
            return content;

        return $"{typeName}.{propertyPath}";
    }

    private void ClearHoveredControl()
    {
        if (_hoveredControl == null)
            return;

        _hoveredControl = null;
        HoverTargetChanged?.Invoke(this, new HotkeyHoverChangedEventArgs(null));
    }

    private void SetBindingMode(bool enabled)
    {
        if (_isBindingMode == enabled)
            return;

        _isBindingMode = enabled;
        if (!enabled)
            ClearHoveredControl();

        BindingModeChanged?.Invoke(this, _isBindingMode);
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;
    }

    private static bool IsBlacklisted(Key key, KeyModifiers modifiers)
    {
        if (key == Key.Escape)
            return true;

        if (modifiers == KeyModifiers.Control && (key == Key.C || key == Key.V || key == Key.X || key == Key.Z || key == Key.Y || key == Key.A))
            return true;

        if (modifiers == KeyModifiers.Alt && (key == Key.F4 || key == Key.Space || key == Key.Tab))
            return true;

        return false;
    }

    private static Control? FindHotkeyTarget(object? source)
    {
        if (source is ToggleButton toggle)
            return toggle;

        if (source is Border border)
        {
            if (HotkeyTarget.GetCampathId(border) != null || HotkeyTarget.GetCampathGroupId(border) != null)
                return border;
        }

        if (source is Button button)
            return button;

        if (source is Avalonia.Visual visual)
        {
            var toggleAncestor = visual.GetSelfAndVisualAncestors().OfType<ToggleButton>().FirstOrDefault();
            if (toggleAncestor != null)
                return toggleAncestor;

            var borderAncestor = visual.GetSelfAndVisualAncestors()
                .OfType<Border>()
                .FirstOrDefault(b => HotkeyTarget.GetCampathId(b) != null || HotkeyTarget.GetCampathGroupId(b) != null);
            if (borderAncestor != null)
                return borderAncestor;

            return visual.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault();
        }

        return null;
    }

    private bool TryResolveCampath(Guid? campathId, Guid? profileId, out CampathsDockViewModel campathsVm, out CampathItemViewModel campath)
    {
        campathsVm = null!;
        campath = null!;

        if (campathId == null || campathId == Guid.Empty)
            return false;

        var vm = _commandContexts.OfType<CampathsDockViewModel>().FirstOrDefault();
        if (vm == null)
            return false;

        if (profileId == null || profileId == Guid.Empty)
            return false;

        if (vm.SelectedProfile == null || vm.SelectedProfile.Id != profileId)
            return false;

        var profile = vm.SelectedProfile;
        var match = profile.Campaths.FirstOrDefault(c => c.Id == campathId.Value);
        if (match != null)
        {
            campathsVm = vm;
            campath = match;
            return true;
        }

        return false;
    }

    private bool TryResolveGraphicsDock(string? profileName, out GraphicsDockViewModel graphicsVm)
    {
        graphicsVm = null!;
        if (string.IsNullOrWhiteSpace(profileName))
            return false;

        var vm = _commandContexts.OfType<GraphicsDockViewModel>().FirstOrDefault();
        if (vm == null)
            return false;

        if (!vm.IsProfileActive(profileName))
            return false;

        graphicsVm = vm;
        return true;
    }

    private bool TryResolveCampathGroup(Guid? groupId, Guid? profileId, out CampathsDockViewModel campathsVm, out CampathGroupViewModel group)
    {
        campathsVm = null!;
        group = null!;

        if (groupId == null || groupId == Guid.Empty)
            return false;

        var vm = _commandContexts.OfType<CampathsDockViewModel>().FirstOrDefault();
        if (vm == null)
            return false;

        if (profileId == null || profileId == Guid.Empty)
            return false;

        if (vm.SelectedProfile == null || vm.SelectedProfile.Id != profileId)
            return false;

        var profile = vm.SelectedProfile;
        var match = profile.Groups.FirstOrDefault(g => g.Id == groupId.Value);
        if (match != null)
        {
            campathsVm = vm;
            group = match;
            return true;
        }

        return false;
    }

    private bool TryGetActiveCampathProfile(out Guid profileId, out string profileName)
    {
        profileId = Guid.Empty;
        profileName = string.Empty;

        var vm = _commandContexts.OfType<CampathsDockViewModel>().FirstOrDefault();
        if (vm?.SelectedProfile == null)
            return false;

        profileId = vm.SelectedProfile.Id;
        profileName = vm.SelectedProfile.Name;
        return true;
    }

    private bool TryGetCampathTarget(Control control, out Guid campathId, out string displayName)
    {
        campathId = Guid.Empty;
        displayName = string.Empty;

        if (control is not Border border)
            return false;

        var id = HotkeyTarget.GetCampathId(border);
        if (id == null || id == Guid.Empty)
            return false;

        campathId = id.Value;
        displayName = GetDataContextName(border) ?? $"Campath {campathId}";
        return true;
    }

    private bool TryGetCampathGroupTarget(Control control, out Guid groupId, out string displayName)
    {
        groupId = Guid.Empty;
        displayName = string.Empty;

        if (control is not Border border)
            return false;

        var id = HotkeyTarget.GetCampathGroupId(border);
        if (id == null || id == Guid.Empty)
            return false;

        groupId = id.Value;
        displayName = GetDataContextName(border) ?? $"Group {groupId}";
        return true;
    }

    private bool TryGetGraphicsAtlasTarget(Control control, out string profileName, out string atlasName, out string action, out string displayName)
    {
        profileName = string.Empty;
        atlasName = string.Empty;
        action = string.Empty;
        displayName = string.Empty;

        var target = control as AvaloniaObject;
        if (target == null)
            return false;

        var atlas = HotkeyTarget.GetGraphicsAtlasName(target);
        var targetAction = HotkeyTarget.GetGraphicsAction(target);
        if (string.IsNullOrWhiteSpace(atlas) || string.IsNullOrWhiteSpace(targetAction))
            return false;

        if (!TryGetActiveGraphicsProfile(out profileName))
            return false;

        atlasName = atlas;
        action = targetAction;
        var controlName = control is Button button ? GetButtonDisplayName(button, nameof(GraphicsDockViewModel), targetAction) : (control as ToggleButton)?.Content?.ToString();
        displayName = $"[{profileName}] {controlName ?? targetAction} ({atlasName})";
        return true;
    }

    private bool TryGetGraphicsInstanceTarget(Control control, out string profileName, out string instanceName, out string action, out string displayName)
    {
        profileName = string.Empty;
        instanceName = string.Empty;
        action = string.Empty;
        displayName = string.Empty;

        var target = control as AvaloniaObject;
        if (target == null)
            return false;

        var instance = HotkeyTarget.GetGraphicsInstanceName(target);
        var targetAction = HotkeyTarget.GetGraphicsAction(target);
        if (string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(targetAction))
            return false;

        if (!TryGetActiveGraphicsProfile(out profileName))
            return false;

        instanceName = instance;
        action = targetAction;
        var controlName = control is Button button ? GetButtonDisplayName(button, nameof(GraphicsDockViewModel), targetAction) : (control as ToggleButton)?.Content?.ToString();
        displayName = $"[{profileName}] {controlName ?? targetAction} ({instanceName})";
        return true;
    }

    private bool TryGetActiveGraphicsProfile(out string profileName)
    {
        profileName = string.Empty;
        var vm = _commandContexts.OfType<GraphicsDockViewModel>().FirstOrDefault();
        if (vm == null || string.IsNullOrWhiteSpace(vm.SelectedProfileName))
            return false;

        profileName = vm.SelectedProfileName;
        return true;
    }

    private bool TryGetAttachPresetTarget(Control control, out int presetPage, out int presetIndex, out int slot, out string displayName)
    {
        presetPage = -1;
        presetIndex = -1;
        slot = -1;
        displayName = string.Empty;

        if (control is not Button button)
            return false;

        var page = HotkeyTarget.GetAttachPresetPage(button);
        var index = HotkeyTarget.GetAttachPresetIndex(button);
        var targetSlot = HotkeyTarget.GetAttachSlot(button);
        var action = HotkeyTarget.GetAttachAction(button);
        if (!string.Equals(action, "execute", StringComparison.OrdinalIgnoreCase))
            return false;

        if (page < 0 || index < 0 || targetSlot < 0 || targetSlot > 9)
            return false;

        var presetName = button.DataContext switch
        {
            AttachPresetViewModel presetVm when !string.IsNullOrWhiteSpace(presetVm.Name) => presetVm.Name,
            AttachPresetViewModel presetVm => presetVm.Title,
            _ => "Preset"
        };

        presetPage = page;
        presetIndex = index;
        slot = targetSlot;
        displayName = $"[Page {page + 1}] Attach {presetName} -> Slot {GetSlotLabel(targetSlot)}";
        return true;
    }

    private static string GetSlotLabel(int slot)
    {
        return slot == 9 ? "0" : (slot + 1).ToString();
    }

    private static string? GetDataContextName(Control control)
    {
        var dataContext = control.DataContext;
        if (dataContext == null)
            return null;

        var nameProp = dataContext.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
        return nameProp?.GetValue(dataContext) as string;
    }


    private static bool TryGetBindingPath(AvaloniaObject target, AvaloniaProperty property, out string? path)
    {
        path = null;
        if (!TryGetBindingExpression(target, property, out var expression))
            return false;

        var binding = GetBindingFromExpression(expression);
        if (binding != null)
        {
            if (binding.Source != null || binding.ElementName != null || binding.RelativeSource != null)
                return false;

            path = binding.Path;
            return !string.IsNullOrWhiteSpace(path);
        }

        path = GetPathFromExpression(expression);
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryGetBindingExpression(
        AvaloniaObject target,
        AvaloniaProperty property,
        [NotNullWhen(true)] out BindingExpressionBase? expression)
    {
        expression = BindingOperations.GetBindingExpressionBase(target, property);
        return expression != null;
    }

    private static Binding? GetBindingFromExpression(BindingExpressionBase expression)
    {
        var type = expression.GetType();
        var parentBindingProp = type.GetProperty("ParentBinding", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var parentBinding = parentBindingProp?.GetValue(expression) as Binding;
        if (parentBinding != null)
            return parentBinding;

        var bindingProp = type.GetProperty("Binding", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return bindingProp?.GetValue(expression) as Binding;
    }

    private static string? GetPathFromExpression(BindingExpressionBase expression)
    {
        var type = expression.GetType();
        var pathProp = type.GetProperty("Path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pathProp?.GetValue(expression) is string path && !string.IsNullOrWhiteSpace(path))
            return path;

        var parentBindingProp = type.GetProperty("ParentBinding", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (parentBindingProp?.GetValue(expression) is { } parentBinding)
        {
            var parentPathProp = parentBinding.GetType().GetProperty("Path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (parentPathProp?.GetValue(parentBinding) is string parentPath && !string.IsNullOrWhiteSpace(parentPath))
                return parentPath;
        }

        var bindingProp = type.GetProperty("Binding", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (bindingProp?.GetValue(expression) is { } binding)
        {
            var bindingPathProp = binding.GetType().GetProperty("Path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (bindingPathProp?.GetValue(binding) is string bindingPath && !string.IsNullOrWhiteSpace(bindingPath))
                return bindingPath;
        }

        return null;
    }

    public static string FormatHotkey(Key key, KeyModifiers modifiers)
    {
        if (key == Key.None)
            return string.Empty;

        var parts = new List<string>();
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

    private void UpdateStatus(string message)
    {
        _statusMessage = message;
        StatusChanged?.Invoke(this, message);
    }
}

public sealed class HotkeyBindingCapturedEventArgs : EventArgs
{
    public HotkeyBindingCapturedEventArgs(HotkeyBindingData binding, Guid? rebindId)
    {
        Binding = binding;
        RebindId = rebindId;
    }

    public HotkeyBindingData Binding { get; }
    public Guid? RebindId { get; }
}

public sealed class HotkeyHoverChangedEventArgs : EventArgs
{
    public HotkeyHoverChangedEventArgs(Control? control)
    {
        Control = control;
    }

    public Control? Control { get; }
}
