using System;
using Avalonia;

namespace HlaeObsTools.Services.Hotkeys;

public sealed class HotkeyTarget
{
    public static readonly AttachedProperty<string?> PathProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, string?>("Path");

    public static readonly AttachedProperty<Guid?> CampathIdProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, Guid?>("CampathId");

    public static readonly AttachedProperty<Guid?> CampathGroupIdProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, Guid?>("CampathGroupId");

    public static void SetPath(AvaloniaObject element, string? value) =>
        element.SetValue(PathProperty, value);

    public static string? GetPath(AvaloniaObject element) =>
        element.GetValue(PathProperty);

    public static void SetCampathId(AvaloniaObject element, Guid? value) =>
        element.SetValue(CampathIdProperty, value);

    public static Guid? GetCampathId(AvaloniaObject element) =>
        element.GetValue(CampathIdProperty);

    public static void SetCampathGroupId(AvaloniaObject element, Guid? value) =>
        element.SetValue(CampathGroupIdProperty, value);

    public static Guid? GetCampathGroupId(AvaloniaObject element) =>
        element.GetValue(CampathGroupIdProperty);
}
