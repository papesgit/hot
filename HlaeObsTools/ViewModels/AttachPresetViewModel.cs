using System.ComponentModel;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

public sealed class AttachPresetViewModel : ViewModelBase
{
    private string _title;
    private string _attachmentName = string.Empty;
    private double _offsetPosX;
    private double _offsetPosY;
    private double _offsetPosZ;
    private double _offsetPitch;
    private double _offsetYaw;
    private double _offsetRoll;
    private double _fov = 90.0;

    public AttachPresetViewModel(string title)
    {
        _title = title;
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string AttachmentName
    {
        get => _attachmentName;
        set => SetProperty(ref _attachmentName, value);
    }

    public double OffsetPosX
    {
        get => _offsetPosX;
        set => SetProperty(ref _offsetPosX, value);
    }

    public double OffsetPosY
    {
        get => _offsetPosY;
        set => SetProperty(ref _offsetPosY, value);
    }

    public double OffsetPosZ
    {
        get => _offsetPosZ;
        set => SetProperty(ref _offsetPosZ, value);
    }

    public double OffsetPitch
    {
        get => _offsetPitch;
        set => SetProperty(ref _offsetPitch, value);
    }

    public double OffsetYaw
    {
        get => _offsetYaw;
        set => SetProperty(ref _offsetYaw, value);
    }

    public double OffsetRoll
    {
        get => _offsetRoll;
        set => SetProperty(ref _offsetRoll, value);
    }

    public double Fov
    {
        get => _fov;
        set => SetProperty(ref _fov, value);
    }

    public void LoadFrom(HudSettings.AttachmentPreset preset)
    {
        AttachmentName = preset.AttachmentName;
        OffsetPosX = preset.OffsetPosX;
        OffsetPosY = preset.OffsetPosY;
        OffsetPosZ = preset.OffsetPosZ;
        OffsetPitch = preset.OffsetPitch;
        OffsetYaw = preset.OffsetYaw;
        OffsetRoll = preset.OffsetRoll;
        Fov = preset.Fov;
    }

    public HudSettings.AttachmentPreset ToModel()
    {
        return new HudSettings.AttachmentPreset
        {
            AttachmentName = AttachmentName ?? string.Empty,
            OffsetPosX = OffsetPosX,
            OffsetPosY = OffsetPosY,
            OffsetPosZ = OffsetPosZ,
            OffsetPitch = OffsetPitch,
            OffsetYaw = OffsetYaw,
            OffsetRoll = OffsetRoll,
            Fov = Fov
        };
    }
}
