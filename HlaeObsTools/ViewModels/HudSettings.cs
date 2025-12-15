using System.Linq;
using System.Collections.Generic;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

/// <summary>
/// Settings for HUD.
/// </summary>
public sealed class HudSettings : ViewModelBase
{
    public record AttachmentPreset
    {
        public string AttachmentName { get; init; } = string.Empty;
        public double OffsetPosX { get; init; }
        public double OffsetPosY { get; init; }
        public double OffsetPosZ { get; init; }
        public double OffsetPitch { get; init; }
        public double OffsetYaw { get; init; }
        public double OffsetRoll { get; init; }
        public double Fov { get; init; } = 90.0;
    }

    private bool _isHudEnabled;

    /// <summary>
    /// Whether the native HUD overlay in the video display is shown.
    /// </summary>
    public bool IsHudEnabled
    {
        get => _isHudEnabled;
        set => SetProperty(ref _isHudEnabled, value);
    }

    /// <summary>
    /// Attach action presets for the radial menu (5 slots).
    /// </summary>
    public List<AttachmentPreset> AttachPresets { get; } = Enumerable.Range(0, 5).Select(_ => new AttachmentPreset()).ToList();
}
