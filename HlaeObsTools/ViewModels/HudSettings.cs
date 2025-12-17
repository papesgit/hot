using System.Linq;
using System.Collections.Generic;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Settings;

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
    private bool _useAltPlayerBinds;

    /// <summary>
    /// Whether the native HUD overlay in the video display is shown.
    /// </summary>
    public bool IsHudEnabled
    {
        get => _isHudEnabled;
        set => SetProperty(ref _isHudEnabled, value);
    }

    /// <summary>
    /// Whether to use alternative player bind labels for slots 6-0.
    /// </summary>
    public bool UseAltPlayerBinds
    {
        get => _useAltPlayerBinds;
        set => SetProperty(ref _useAltPlayerBinds, value);
    }

    /// <summary>
    /// Attach action presets for the radial menu (5 slots).
    /// </summary>
    public List<AttachmentPreset> AttachPresets { get; } = Enumerable.Range(0, 5).Select(_ => new AttachmentPreset()).ToList();

    public void ApplyAttachPresets(IEnumerable<AttachmentPresetData> presets)
    {
        var items = presets?.ToList() ?? new List<AttachmentPresetData>();
        AttachPresets.Clear();

        foreach (var preset in items)
        {
            AttachPresets.Add(new AttachmentPreset
            {
                AttachmentName = preset.AttachmentName,
                OffsetPosX = preset.OffsetPosX,
                OffsetPosY = preset.OffsetPosY,
                OffsetPosZ = preset.OffsetPosZ,
                OffsetPitch = preset.OffsetPitch,
                OffsetYaw = preset.OffsetYaw,
                OffsetRoll = preset.OffsetRoll,
                Fov = preset.Fov
            });
        }

        while (AttachPresets.Count < 5)
        {
            AttachPresets.Add(new AttachmentPreset());
        }
    }

    public IEnumerable<AttachmentPresetData> ToAttachPresetData()
    {
        return AttachPresets.Select(p => new AttachmentPresetData
        {
            AttachmentName = p.AttachmentName,
            OffsetPosX = p.OffsetPosX,
            OffsetPosY = p.OffsetPosY,
            OffsetPosZ = p.OffsetPosZ,
            OffsetPitch = p.OffsetPitch,
            OffsetYaw = p.OffsetYaw,
            OffsetRoll = p.OffsetRoll,
            Fov = p.Fov
        });
    }
}
