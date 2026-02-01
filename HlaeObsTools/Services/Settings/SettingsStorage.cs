using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlaeObsTools.Services.Settings;

public class SettingsStorage
{
    private readonly string _storagePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SettingsStorage()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = Path.Combine(appData, "HlaeObsTools");
        Directory.CreateDirectory(baseDir);
        _storagePath = Path.Combine(baseDir, "settings.json");
    }

    public AppSettingsData Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<AppSettingsData>(json, _jsonOptions);
                if (data != null)
                    return data;
            }
        }
        catch
        {
            // ignore load errors, return defaults
        }

        return new AppSettingsData();
    }

    public void Save(AppSettingsData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(_storagePath, json);
        }
        catch
        {
            // ignore save errors
        }
    }
}

public class AppSettingsData
{
    public List<AttachmentPresetData>? AttachPresets { get; set; }
    public List<AttachmentPresetPageData> AttachPresetPages { get; set; } = new();
    public int ActiveAttachPresetPage { get; set; }
    public double RadarScale { get; set; } = 1.0;
    public double MarkerScale { get; set; } = 1.0;
    public double HeightScaleMultiplier { get; set; } = 1.0;
    public bool UseAltPlayerBinds { get; set; } = false;
    public bool DisplayNumbersTopmost { get; set; } = true;
    public bool ShowPlayerNames { get; set; } = true;
    public string WebSocketHost { get; set; } = "127.0.0.1";
    public int WebSocketPort { get; set; } = 31338;
    public int UdpPort { get; set; } = 31339;
    public int RtpPort { get; set; } = 5000;
    public int GsiPort { get; set; } = 31337;
    public string MapObjPath { get; set; } = string.Empty;
    public bool ViewportUseLegacyD3D11 { get; set; }
    public double PinScale { get; set; } = 200.0;
    public double PinOffsetZ { get; set; } = 55.0;
    public double ViewportMouseScale { get; set; } = 0.75;
    public double MapScale { get; set; } = 1.0;
    public double MapYaw { get; set; }
    public double MapPitch { get; set; }
    public double MapRoll { get; set; }
    public double MapOffsetX { get; set; }
    public double MapOffsetY { get; set; }
    public double MapOffsetZ { get; set; }
    public double ViewportFpsCap { get; set; } = 60.0;
    public bool ViewportPostprocessEnabled { get; set; } = true;
    public bool ViewportColorCorrectionEnabled { get; set; } = true;
    public bool ViewportDynamicShadowsEnabled { get; set; } = true;
    public bool ViewportWireframeEnabled { get; set; }
    public bool ViewportSkipWaterEnabled { get; set; }
    public bool ViewportSkipTranslucentEnabled { get; set; }
    public bool ViewportShowFps { get; set; }
    public bool ViewportCampathMode { get; set; }
    public bool ViewportCampathOverlayEnabled { get; set; } = true;
    public bool ViewportCampathSyncEnabled { get; set; }
    public bool CampathGizmoLocalSpace { get; set; } = true;
    public int ViewportShadowTextureSize { get; set; } = 1024;
    public int ViewportMaxTextureSize { get; set; } = 1024;
    public string ViewportRenderMode { get; set; } = "Default";
    public FreecamSettingsData FreecamSettings { get; set; } = new();
    public bool VmixReplayEnabled { get; set; }
    public string VmixReplayHost { get; set; } = "127.0.0.1";
    public int VmixReplayPort { get; set; } = 8088;
    public double VmixReplayPreSeconds { get; set; } = 2.0;
    public double VmixReplayPostSeconds { get; set; } = 2.0;
    public double VmixReplayExtendWindowSeconds { get; set; } = 3.0;
    public bool DisableFocusInputGate { get; set; }
}

public class AttachmentPresetPageData
{
    public List<AttachmentPresetData> Presets { get; set; } = new();
}

public class AttachmentPresetData
{
    public string Name { get; set; } = string.Empty;
    public string AttachmentName { get; set; } = string.Empty;
    public double OffsetPosX { get; set; }
    public double OffsetPosY { get; set; }
    public double OffsetPosZ { get; set; }
    public double OffsetPitch { get; set; }
    public double OffsetYaw { get; set; }
    public double OffsetRoll { get; set; }
    public double Fov { get; set; } = 90.0;
    public string? RotationReference { get; set; } // "attachment" | "offset_local"
    public string? RotationBasisPitch { get; set; } // "attachment" | "world"
    public string? RotationBasisYaw { get; set; } // "attachment" | "world"
    public string? RotationBasisRoll { get; set; } // "attachment" | "world"
    public bool RotationLockPitch { get; set; }
    public bool RotationLockYaw { get; set; }
    public bool RotationLockRoll { get; set; }
    public AttachmentPresetAnimationData? Animation { get; set; }
}

public class AttachmentPresetAnimationData
{
    public bool Enabled { get; set; }
    public List<AttachmentPresetAnimationEventData> Events { get; set; } = new();
}

public class AttachmentPresetAnimationEventData
{
    public string Type { get; set; } = "keyframe"; // "keyframe" | "transition"
    public double Time { get; set; }
    public int Order { get; set; }

    public double? DeltaPosX { get; set; }
    public double? DeltaPosY { get; set; }
    public double? DeltaPosZ { get; set; }

    public double? DeltaPitch { get; set; }
    public double? DeltaYaw { get; set; }
    public double? DeltaRoll { get; set; }

    public double? Fov { get; set; }

    public double? TransitionDuration { get; set; }
    public string? TransitionEasing { get; set; } // "linear" | "smoothstep" | "easeinoutcubic"
    public string? KeyframeEasingCurve { get; set; } // "linear" | "smoothstep" | "cubic"
    public string? KeyframeEasingMode { get; set; } // "ease_in" | "ease_out" | "ease_in_out"
}

public class FreecamSettingsData
{
    public double MouseSensitivity { get; set; } = 0.12;
    public double MoveSpeed { get; set; } = 200.0;
    public double SprintMultiplier { get; set; } = 2.5;
    public double VerticalSpeed { get; set; } = 200.0;
    public double SpeedAdjustRate { get; set; } = 1.1;
    public double SpeedMinMultiplier { get; set; } = 0.05;
    public double SpeedMaxMultiplier { get; set; } = 5.0;
    public double RollSpeed { get; set; } = 45.0;
    public double RollSmoothing { get; set; } = 0.8;
    public double LeanStrength { get; set; } = 1.0;
    public double LeanAccelScale { get; set; } = 0.0250;
    public double LeanVelocityScale { get; set; } = 0.005;
    public double LeanMaxAngle { get; set; } = 20.0;
    public double LeanHalfTime { get; set; } = 0.30;
    public double FovMin { get; set; } = 10.0;
    public double FovMax { get; set; } = 150.0;
    public double FovStep { get; set; } = 2.0;
    public double DefaultFov { get; set; } = 90.0;
    public bool SmoothEnabled { get; set; } = true;
    public double HalfVec { get; set; } = 0.5;
    public double HalfRot { get; set; } = 0.5;
    public double LockHalfRot { get; set; } = 0.1;
    public double LockHalfRotTransition { get; set; } = 1.0;
    public double HalfFov { get; set; } = 0.8;
    public bool RotCriticalDamping { get; set; } = false;
    public double RotDampingRatio { get; set; } = 1.0;
    public bool HoldMovementFollowsCamera { get; set; } = true;
    public bool AnalogKeyboardEnabled { get; set; }
    public double AnalogLeftDeadzone { get; set; }
    public double AnalogRightDeadzone { get; set; }
    public double AnalogCurve { get; set; }
    public bool ClampPitch { get; set; }
}
