using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Data;
using Avalonia.VisualTree;

namespace HlaeObsTools.Services.Hotkeys;

public sealed class HotkeyService
{
    private readonly List<object> _commandContexts = new();
    private readonly List<HotkeyBindingData> _bindings = new();
    private Control? _hoveredControl;
    private Guid? _rebindId;
    private HotkeyBindingData? _rebindTarget;
    private bool _isBindingMode;

    public event EventHandler<HotkeyBindingCapturedEventArgs>? BindingCaptured;
    public event EventHandler<bool>? BindingModeChanged;
    public event EventHandler<string>? StatusChanged;

    public bool IsBindingMode => _isBindingMode;

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
    }

    public void BeginCapture(Guid? rebindId = null)
    {
        _rebindId = rebindId;
        _rebindTarget = null;
        SetBindingMode(true);
        StatusChanged?.Invoke(this, "Hover a button or toggle and press a key combo (Esc to exit).");
    }

    public void BeginRebind(HotkeyBindingData binding)
    {
        _rebindId = binding.Id;
        _rebindTarget = binding;
        SetBindingMode(true);
        StatusChanged?.Invoke(this, "Press a new key combo (Esc to cancel).");
    }

    public void EndCapture()
    {
        _rebindId = null;
        _rebindTarget = null;
        ClearHoveredControl();
        SetBindingMode(false);
        StatusChanged?.Invoke(this, "Hotkey mode disabled.");
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (!_isBindingMode)
            return;

        var control = FindHotkeyTarget(e.Source);
        if (control == null)
        {
            ClearHoveredControl();
            return;
        }

        if (!ReferenceEquals(_hoveredControl, control))
            _hoveredControl = control;
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

        var binding = _bindings.FirstOrDefault(b =>
            b.Enabled
            && b.Key == e.Key
            && b.Modifiers == e.KeyModifiers);

        if (binding == null)
            return false;

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

        return false;
    }

    private bool TryCaptureBinding(KeyEventArgs e)
    {
        if (IsBlacklisted(e.Key, e.KeyModifiers))
        {
            StatusChanged?.Invoke(this, "That key combo is reserved.");
            return true;
        }

        if (IsModifierKey(e.Key) || e.Key == Key.None)
        {
            StatusChanged?.Invoke(this, "Press a non-modifier key.");
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
                DisplayName = _rebindTarget.DisplayName
            };

            BindingCaptured?.Invoke(this, new HotkeyBindingCapturedEventArgs(binding, _rebindId));
            StatusChanged?.Invoke(this, $"Rebound to {FormatHotkey(binding.Key, binding.Modifiers)}.");
            EndCapture();
            return true;
        }

        if (_hoveredControl == null)
        {
            Console.WriteLine("[Hotkeys] Capture: no hovered control. Pointer move not detected or control not bindable.");
            StatusChanged?.Invoke(this, "Hover a button or toggle first.");
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
            StatusChanged?.Invoke(this, $"Bound {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
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
            StatusChanged?.Invoke(this, $"Bound {FormatHotkey(binding.Key, binding.Modifiers)} to {binding.DisplayName}.");
            return true;
        }

        Console.WriteLine($"[Hotkeys] Capture: hovered control not bindable: {_hoveredControl.GetType().Name}. {GetBindFailureReason(_hoveredControl)}");
        StatusChanged?.Invoke(this, "That control cannot be bound yet.");
        return true;
    }

    private string GetBindFailureReason(Control control)
    {
        if (control is ToggleSwitch toggle)
        {
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

            if (!TryGetBindingExpression(toggle, ToggleSwitch.IsCheckedProperty, out var expression))
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

        if (control is Button button)
        {
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
            || TryGetBoolBindingTarget(control, out _, out _, out _);
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

        if (control is not ToggleSwitch toggle)
            return false;

        var path = HotkeyTarget.GetPath(toggle);
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!TryGetBindingPath(toggle, ToggleSwitch.IsCheckedProperty, out path))
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

    private static string GetToggleDisplayName(ToggleSwitch toggle, string typeName, string propertyPath)
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
        _hoveredControl = null;
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
        if (source is ToggleSwitch toggle)
            return toggle;

        if (source is Button button)
            return button;

        if (source is Avalonia.Visual visual)
        {
            var toggleAncestor = visual.GetSelfAndVisualAncestors().OfType<ToggleSwitch>().FirstOrDefault();
            if (toggleAncestor != null)
                return toggleAncestor;

            return visual.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault();
        }

        return null;
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

    private static bool TryGetBindingExpression(AvaloniaObject target, AvaloniaProperty property, out BindingExpressionBase expression)
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

    private static string FormatHotkey(Key key, KeyModifiers modifiers)
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
