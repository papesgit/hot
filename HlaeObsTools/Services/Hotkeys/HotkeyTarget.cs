using Avalonia;

namespace HlaeObsTools.Services.Hotkeys;

public sealed class HotkeyTarget
{
    public static readonly AttachedProperty<string?> PathProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, string?>("Path");

    public static void SetPath(AvaloniaObject element, string? value) =>
        element.SetValue(PathProperty, value);

    public static string? GetPath(AvaloniaObject element) =>
        element.GetValue(PathProperty);
}
