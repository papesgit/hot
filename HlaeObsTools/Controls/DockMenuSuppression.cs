using Avalonia;
using Avalonia.Controls;
using Dock.Avalonia.Controls;

namespace HlaeObsTools.Controls;

/// <summary>
/// Prevents Dock from installing its generic IDE context menus on tool chrome.
/// </summary>
public sealed class DockMenuSuppression
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<DockMenuSuppression, ToolChromeControl, bool>("IsEnabled");

    static DockMenuSuppression()
    {
        IsEnabledProperty.Changed.AddClassHandler<ToolChromeControl>((control, change) =>
        {
            if (change.GetNewValue<bool>())
                SuppressToolChromeMenu(control);
        });

        ToolChromeControl.ToolFlyoutProperty.Changed.AddClassHandler<ToolChromeControl>((control, _) =>
        {
            if (GetIsEnabled(control) && control.ToolFlyout != null)
                control.SetValue(ToolChromeControl.ToolFlyoutProperty, null);
        });

        Control.ContextFlyoutProperty.Changed.AddClassHandler<ToolChromeControl>((control, _) =>
        {
            if (GetIsEnabled(control) && control.ContextFlyout != null)
                control.ContextFlyout = null;
        });

        ToolTabStripItem.TabContextMenuProperty.Changed.AddClassHandler<ToolTabStripItem>((control, _) =>
        {
            if (control.TabContextMenu != null)
                control.SetValue(ToolTabStripItem.TabContextMenuProperty, null);
        });
    }

    public static void SetIsEnabled(AvaloniaObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(AvaloniaObject element) =>
        element.GetValue(IsEnabledProperty);

    private static void SuppressToolChromeMenu(ToolChromeControl chrome)
    {
        // Dock can assign its defaults after styles are applied, so use local
        // null values instead of style setters.
        chrome.SetValue(ToolChromeControl.ToolFlyoutProperty, null);
        chrome.ContextFlyout = null;
    }
}
