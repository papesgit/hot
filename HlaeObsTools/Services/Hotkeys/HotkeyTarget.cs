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
    public static readonly AttachedProperty<string?> GraphicsAtlasNameProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, string?>("GraphicsAtlasName");
    public static readonly AttachedProperty<string?> GraphicsInstanceNameProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, string?>("GraphicsInstanceName");
    public static readonly AttachedProperty<string?> GraphicsActionProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, string?>("GraphicsAction");
    public static readonly AttachedProperty<int> AttachPresetPageProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, int>("AttachPresetPage", -1);
    public static readonly AttachedProperty<int> AttachPresetIndexProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, int>("AttachPresetIndex", -1);
    public static readonly AttachedProperty<int> AttachSlotProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, int>("AttachSlot", -1);
    public static readonly AttachedProperty<string?> AttachActionProperty =
        AvaloniaProperty.RegisterAttached<HotkeyTarget, AvaloniaObject, string?>("AttachAction");

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

    public static void SetGraphicsAtlasName(AvaloniaObject element, string? value) =>
        element.SetValue(GraphicsAtlasNameProperty, value);

    public static string? GetGraphicsAtlasName(AvaloniaObject element) =>
        element.GetValue(GraphicsAtlasNameProperty);

    public static void SetGraphicsInstanceName(AvaloniaObject element, string? value) =>
        element.SetValue(GraphicsInstanceNameProperty, value);

    public static string? GetGraphicsInstanceName(AvaloniaObject element) =>
        element.GetValue(GraphicsInstanceNameProperty);

    public static void SetGraphicsAction(AvaloniaObject element, string? value) =>
        element.SetValue(GraphicsActionProperty, value);

    public static string? GetGraphicsAction(AvaloniaObject element) =>
        element.GetValue(GraphicsActionProperty);

    public static void SetAttachPresetPage(AvaloniaObject element, int value) =>
        element.SetValue(AttachPresetPageProperty, value);

    public static int GetAttachPresetPage(AvaloniaObject element) =>
        element.GetValue(AttachPresetPageProperty);

    public static void SetAttachPresetIndex(AvaloniaObject element, int value) =>
        element.SetValue(AttachPresetIndexProperty, value);

    public static int GetAttachPresetIndex(AvaloniaObject element) =>
        element.GetValue(AttachPresetIndexProperty);

    public static void SetAttachSlot(AvaloniaObject element, int value) =>
        element.SetValue(AttachSlotProperty, value);

    public static int GetAttachSlot(AvaloniaObject element) =>
        element.GetValue(AttachSlotProperty);

    public static void SetAttachAction(AvaloniaObject element, string? value) =>
        element.SetValue(AttachActionProperty, value);

    public static string? GetAttachAction(AvaloniaObject element) =>
        element.GetValue(AttachActionProperty);
}
