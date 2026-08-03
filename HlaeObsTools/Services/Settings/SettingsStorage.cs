using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HlaeObsTools.Services.Hotkeys;

namespace HlaeObsTools.Services.Settings;

public class SettingsStorage
{
    // All SettingsStorage instances target the same file. Serializing access prevents
    // independently-created services from interleaving a read or write in this process.
    private static readonly object StorageLock = new();
    private readonly string _storagePath;
    private readonly string _backupPath;
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
        _backupPath = Path.Combine(baseDir, "settings.json.bak");
    }

    public AppSettingsData Load()
    {
        lock (StorageLock)
        {
            return LoadUnsafe();
        }
    }

    public void Save(AppSettingsData data)
    {
        lock (StorageLock)
        {
            SaveUnsafe(data);
        }
    }

    /// <summary>
    /// Applies a small change to the latest on-disk settings while holding the storage lock.
    /// Use this for background services which do not share the application's live settings object.
    /// </summary>
    public void Update(Action<AppSettingsData> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (StorageLock)
        {
            var data = LoadUnsafe();
            update(data);
            SaveUnsafe(data);
        }
    }

    private AppSettingsData LoadUnsafe()
    {
        try
        {
            if (File.Exists(_storagePath))
                return Deserialize(_storagePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load settings from '{_storagePath}': {ex.Message}");

            try
            {
                if (File.Exists(_backupPath))
                {
                    var backup = Deserialize(_backupPath);
                    Console.WriteLine("Recovered settings from the previous backup.");
                    return backup;
                }
            }
            catch (Exception backupEx)
            {
                Console.WriteLine($"Failed to load settings backup from '{_backupPath}': {backupEx.Message}");
            }
        }

        return new AppSettingsData();
    }

    private AppSettingsData Deserialize(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettingsData>(json, _jsonOptions)
            ?? throw new JsonException("Settings file did not contain an object.");
    }

    private void SaveUnsafe(AppSettingsData data)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(_storagePath)!,
            $".{Path.GetFileName(_storagePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(temporaryPath, json);

            if (File.Exists(_storagePath))
                File.Replace(temporaryPath, _storagePath, _backupPath);
            else
                File.Move(temporaryPath, _storagePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save settings to '{_storagePath}': {ex.Message}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

public class AppSettingsData
{
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string? SkippedUpdateVersion { get; set; }
    public List<AttachmentPresetData>? AttachPresets { get; set; }
    public List<AttachmentPresetPageData> AttachPresetPages { get; set; } = new();
    public int ActiveAttachPresetPage { get; set; }
    public double RadarScale { get; set; } = 1.0;
    public double MarkerScale { get; set; } = 1.0;
    public double HeightScaleMultiplier { get; set; } = 1.0;
    public double HudSize { get; set; } = 1.0;
    public bool UseAltPlayerBinds { get; set; } = false;
    public bool DisplayNumbersTopmost { get; set; } = true;
    public bool ShowPlayerNames { get; set; } = true;
    public string RadarStyle { get; set; } = "ingame";
    public string WebSocketHost { get; set; } = "127.0.0.1";
    public int WebSocketPort { get; set; } = 31338;
    public string GraphicsProducerHost { get; set; } = "127.0.0.1";
    public int GraphicsProducerPort { get; set; } = 31340;
    public int UdpPort { get; set; } = 31339;
    public int RtpPort { get; set; } = 5000;
    public int GsiPort { get; set; } = 31337;
    public string NetConsoleHostPort { get; set; } = "127.0.0.1:54545";
    public bool NetConsoleFilterGameEvents { get; set; } = true;
    public bool NetConsoleFilterUnknownNetMessages { get; set; } = true;
    public List<string> NetConsoleUserFilters { get; set; } = new();
    public List<string> GsiRelayUris { get; set; } = new();
    public string MapObjPath { get; set; } = string.Empty;
    public string Cs2GameFolder { get; set; } = string.Empty;
    public string ViewportSelectedMapName { get; set; } = string.Empty;
    public bool ViewportActiveDutyMapsOnly { get; set; } = true;
    public bool ViewportShowPlayerPins { get; set; } = true;
    public double PinScale { get; set; } = 200.0;
    public double PinOffsetZ { get; set; } = 55.0;
    public double ViewportMouseScale { get; set; } = 0.75;
    public double ViewportFpsCap { get; set; } = 60.0;
    public bool ViewportPostprocessEnabled { get; set; } = true;
    public bool ViewportColorCorrectionEnabled { get; set; } = true;
    public bool ViewportDynamicShadowsEnabled { get; set; } = true;
    public bool ViewportWireframeEnabled { get; set; }
    public bool ViewportSkipWaterEnabled { get; set; }
    public bool ViewportSkipTranslucentEnabled { get; set; }
    public bool ViewportShowFps { get; set; }
    public bool ShowHlaeCampathControls { get; set; }
    public string DefaultCampathInterp { get; set; } = "Curves";
    public bool ViewportCampathOverlayEnabled { get; set; } = true;
    public bool ViewportCampathGizmoEnabled { get; set; } = true;
    public string HlaeSyncTimeSkipMode { get; set; } = "AfterTick";
    public bool CampathGizmoLocalSpace { get; set; } = true;
    public bool ViewportLiveLinkEnabled { get; set; }
    public bool ViewportLiveLinkItemIconsEnabled { get; set; } = true;
    public bool ViewportLiveLinkWeaponIconsEnabled { get; set; } = true;
    public bool ViewportLiveLinkGrenadeIconsEnabled { get; set; } = true;
    public bool ViewportLiveLinkProjectileIconsEnabled { get; set; } = true;
    public bool ViewportLiveLinkObjectiveIconsEnabled { get; set; } = true;
    public bool ViewportLiveLinkDeadPlayerIconsEnabled { get; set; } = true;
    public int ViewportLiveLinkPort { get; set; } = 31237;
    public int ViewportLiveLinkFps { get; set; } = 10;
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
    public string VmixReplayChannel { get; set; } = "A";
    public int VmixReplayCamera { get; set; } = 1;
    public string ReplayDirectorRole { get; set; } = "Off";
    public int ReplayDirectorPublisherPort { get; set; } = 31341;
    public string ReplayDirectorPublisherIp { get; set; } = "";
    public bool ReplayDirectorManualHost { get; set; }
    // Retained solely to migrate configurations written by older versions.
    public string ReplayDirectorFollowerEndpoint { get; set; } = "";
    public double ReplayDirectorPreSwitchSeconds { get; set; } = 2.0;
    public double ReplayDirectorMergeWindowSeconds { get; set; } = 3.0;
    public double ReplayDirectorSwitchLockSeconds { get; set; } = 0.75;
    public bool ReplayDirectorOnlyFollowMissedKills { get; set; }
    public bool ReplayDirectorDelayedVmixEnabled { get; set; } = true;
    public string ReplayDirectorDelayedVmixChannel { get; set; } = "B";
    public int ReplayDirectorDelayedVmixCamera { get; set; } = 2;
    public bool DisableFocusInputGate { get; set; }
    public int GraphicsTargetFps { get; set; } = 30;
    public List<HotkeyBindingData> Hotkeys { get; set; } = new();
    public string ActiveDockLayout { get; set; } = "Observer";
    public List<DockLayoutData> UserDockLayouts { get; set; } = new();
}

public sealed class DockLayoutData
{
    public string Name { get; set; } = string.Empty;
    public DockLayoutNodeData? Main { get; set; }
    public List<string> HiddenDockableIds { get; set; } = new();
    public Dictionary<string, string> HiddenDockableOwners { get; set; } = new();
    public List<DockLayoutWindowData> Windows { get; set; } = new();
}

public sealed class DockLayoutNodeData
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public double? Proportion { get; set; }
    public string Orientation { get; set; } = string.Empty;
    public string ActiveDockableId { get; set; } = string.Empty;
    public List<DockLayoutNodeData> Children { get; set; } = new();
}

public sealed class DockLayoutWindowData
{
    public string Id { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string WindowState { get; set; } = string.Empty;
    public DockLayoutNodeData? Layout { get; set; }
}

public class AttachmentPresetPageData
{
    public string Name { get; set; } = string.Empty;
    public List<AttachmentPresetData> Presets { get; set; } = new();
}

public class AttachmentPresetData
{
    public string Name { get; set; } = string.Empty;
    public string AttachmentName { get; set; } = string.Empty;
    public string BoneName { get; set; } = string.Empty;
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
    public string? RotationSampling { get; set; } // "live" | "freeze_at_segment_start"
    public bool? FollowAttachmentPitch { get; set; }
    public bool? FollowAttachmentYaw { get; set; }
    public bool? FollowAttachmentRoll { get; set; }

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
    public bool SwapRightClickInitMode { get; set; } = false;
    public bool AnalogKeyboardEnabled { get; set; }
    public double AnalogLeftDeadzone { get; set; }
    public double AnalogRightDeadzone { get; set; }
    public double AnalogCurve { get; set; }
    public bool ClampPitch { get; set; }

    public double WalkMoveSpeed { get; set; } = 160.0;
    public double WalkMoveAcceleration { get; set; } = 800.0;
    public double WalkMoveDeceleration { get; set; } = 800.0;
    public double WalkRunMultiplier { get; set; } = 1.8;
    public double WalkCrouchSpeedMultiplier { get; set; } = 0.6;
    public double WalkLookHalfTime { get; set; } = 0.150;
    public double WalkFovHalfTime { get; set; } = 0.40;
    public double WalkGravity { get; set; } = 800.0;
    public double WalkJumpSpeed { get; set; } = 280.0;
    public double WalkHullRadius { get; set; } = 12.0;
    public double WalkHullHalfHeight { get; set; } = 35.0;
    public double WalkCrouchHullHalfHeight { get; set; } = 12.0;
    public double WalkCameraTopInset { get; set; } = 6.0;
    public double WalkStepHeight { get; set; } = 18.0;
    public double WalkGroundProbe { get; set; } = 2.0;
    public double WalkMinGroundNormalZ { get; set; } = 0.55;
    public bool WalkModeDefaultEnabled { get; set; }
    public bool HandheldDefaultEnabled { get; set; }
    public double WalkBobAmplitudeZ { get; set; } = 2.15;
    public double WalkBobAmplitudeSide { get; set; } = 2.70;
    public double WalkBobAmplitudeRoll { get; set; } = 1.20;
    public double WalkBobFrequency { get; set; } = 0.8;
    public double HandheldShakePosAmplitude { get; set; } = 0.45;
    public double HandheldShakeAngAmplitude { get; set; } = 0.65;
    public double HandheldShakeFrequency { get; set; } = 0.4;
    public double HandheldDriftPosAmplitude { get; set; } = 3.30;
    public double HandheldDriftAngAmplitude { get; set; } = 2.36;
    public double HandheldDriftFrequency { get; set; } = 0.15;
}
