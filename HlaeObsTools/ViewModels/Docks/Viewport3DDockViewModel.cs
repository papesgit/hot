using System.ComponentModel;
using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.Services.WebSocket;
using OpenTK.Mathematics;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class Viewport3DDockViewModel : Tool, IDisposable
{
    private readonly Viewport3DSettings _settings;
    private readonly FreecamSettings _freecamSettings;
    private readonly HlaeWebSocketClient? _webSocketClient;
    private readonly VideoDisplayDockViewModel? _videoDisplay;
    private readonly GsiServer? _gsiServer;
    private long _lastHeartbeat;
    private bool _awaitFreecamRelease;

    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };

    public event Action<IReadOnlyList<ViewportPin>>? PinsUpdated;

    public Viewport3DDockViewModel(Viewport3DSettings settings, FreecamSettings freecamSettings, HlaeWebSocketClient? webSocketClient = null, VideoDisplayDockViewModel? videoDisplay = null, GsiServer? gsiServer = null)
    {
        _settings = settings;
        _freecamSettings = freecamSettings;
        _webSocketClient = webSocketClient;
        _videoDisplay = videoDisplay;
        _gsiServer = gsiServer;
        _settings.PropertyChanged += OnSettingsChanged;
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated += OnGameStateUpdated;

        Title = "3D Viewport";
        CanFloat = true;
        CanPin = true;
    }

    public string MapObjPath
    {
        get => _settings.MapObjPath;
        set
        {
            if (_settings.MapObjPath != value)
            {
                _settings.MapObjPath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public FreecamSettings FreecamSettings => _freecamSettings;

    public async void HandoffFreecam(ViewportFreecamState state)
    {
        if (_webSocketClient == null)
            return;

        var rotation = GetWorldRotation();
        var invRotation = Matrix3.Transpose(rotation);
        var scale = Math.Abs(_settings.WorldScale) < 0.0001f ? 1.0f : _settings.WorldScale;
        var offset = new Vector3(_settings.WorldOffsetX, _settings.WorldOffsetY, _settings.WorldOffsetZ);

        var raw = ToGameSpace(state.RawPosition, state.RawForward, state.RawUp, invRotation, offset, scale);
        if (raw.Forward.LengthSquared < 0.0001f)
            return;

        var rawForward = Vector3.Normalize(raw.Forward);
        var rawUp = Vector3.Normalize(raw.Up);

        var smooth = ToGameSpace(state.SmoothedPosition, state.SmoothedForward, state.SmoothedUp, invRotation, offset, scale);
        var smoothForward = smooth.Forward;
        var smoothUp = smooth.Up;
        if (smoothForward.LengthSquared < 0.0001f)
        {
            smooth = raw;
            smoothForward = rawForward;
            smoothUp = rawUp;
        }
        else
        {
            smoothForward = Vector3.Normalize(smoothForward);
            smoothUp = Vector3.Normalize(smoothUp);
        }

        var (pitch, yaw, roll) = GetAngles(rawForward, rawUp);
        var (smoothPitch, smoothYaw, smoothRoll) = GetAngles(smoothForward, smoothUp);

        var args = new
        {
            posX = raw.Position.X,
            posY = raw.Position.Y,
            posZ = raw.Position.Z,
            pitch,
            yaw,
            roll,
            fov = state.RawFov,
            smoothPosX = smooth.Position.X,
            smoothPosY = smooth.Position.Y,
            smoothPosZ = smooth.Position.Z,
            smoothPitch,
            smoothYaw,
            smoothRoll,
            smoothFov = state.SmoothedFov,
            speedScalar = state.SpeedScalar,
            mouseSensitivity = (float)_freecamSettings.MouseSensitivity,
            moveSpeed = (float)_freecamSettings.MoveSpeed,
            sprintMultiplier = (float)_freecamSettings.SprintMultiplier,
            verticalSpeed = (float)_freecamSettings.VerticalSpeed,
            speedAdjustRate = (float)_freecamSettings.SpeedAdjustRate,
            speedMinMultiplier = (float)_freecamSettings.SpeedMinMultiplier,
            speedMaxMultiplier = (float)_freecamSettings.SpeedMaxMultiplier,
            rollSpeed = (float)_freecamSettings.RollSpeed,
            rollSmoothing = (float)_freecamSettings.RollSmoothing,
            leanStrength = (float)_freecamSettings.LeanStrength,
            leanAccelScale = (float)_freecamSettings.LeanAccelScale,
            leanVelocityScale = (float)_freecamSettings.LeanVelocityScale,
            leanMaxAngle = (float)_freecamSettings.LeanMaxAngle,
            leanHalfTime = (float)_freecamSettings.LeanHalfTime,
            fovMin = (float)_freecamSettings.FovMin,
            fovMax = (float)_freecamSettings.FovMax,
            fovStep = (float)_freecamSettings.FovStep,
            defaultFov = (float)_freecamSettings.DefaultFov,
            smoothEnabled = _freecamSettings.SmoothEnabled,
            halfVec = (float)_freecamSettings.HalfVec,
            halfRot = (float)_freecamSettings.HalfRot,
            lockHalfRot = (float)_freecamSettings.LockHalfRot,
            lockHalfRotTransition = (float)_freecamSettings.LockHalfRotTransition,
            halfFov = (float)_freecamSettings.HalfFov
        };

        await _webSocketClient.SendCommandAsync("freecam_handoff", args);
        _videoDisplay?.RequestFreecamInputLock();
        _awaitFreecamRelease = true;
    }

    public void ReleaseHandoffFreecamInput()
    {
        if (!_awaitFreecamRelease)
            return;

        _awaitFreecamRelease = false;
        _videoDisplay?.RequestFreecamInputRelease();
    }

    public float PinScale
    {
        get => _settings.PinScale;
        set
        {
            if (Math.Abs(_settings.PinScale - value) > 0.0001f)
            {
                _settings.PinScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float PinOffsetX
    {
        get => _settings.PinOffsetX;
        set
        {
            if (Math.Abs(_settings.PinOffsetX - value) > 0.0001f)
            {
                _settings.PinOffsetX = value;
                OnPropertyChanged();
            }
        }
    }

    public float PinOffsetY
    {
        get => _settings.PinOffsetY;
        set
        {
            if (Math.Abs(_settings.PinOffsetY - value) > 0.0001f)
            {
                _settings.PinOffsetY = value;
                OnPropertyChanged();
            }
        }
    }

    public float PinOffsetZ
    {
        get => _settings.PinOffsetZ;
        set
        {
            if (Math.Abs(_settings.PinOffsetZ - value) > 0.0001f)
            {
                _settings.PinOffsetZ = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldScale
    {
        get => _settings.WorldScale;
        set
        {
            if (Math.Abs(_settings.WorldScale - value) > 0.0001f)
            {
                _settings.WorldScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldYaw
    {
        get => _settings.WorldYaw;
        set
        {
            if (Math.Abs(_settings.WorldYaw - value) > 0.0001f)
            {
                _settings.WorldYaw = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldPitch
    {
        get => _settings.WorldPitch;
        set
        {
            if (Math.Abs(_settings.WorldPitch - value) > 0.0001f)
            {
                _settings.WorldPitch = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldRoll
    {
        get => _settings.WorldRoll;
        set
        {
            if (Math.Abs(_settings.WorldRoll - value) > 0.0001f)
            {
                _settings.WorldRoll = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldOffsetX
    {
        get => _settings.WorldOffsetX;
        set
        {
            if (Math.Abs(_settings.WorldOffsetX - value) > 0.0001f)
            {
                _settings.WorldOffsetX = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldOffsetY
    {
        get => _settings.WorldOffsetY;
        set
        {
            if (Math.Abs(_settings.WorldOffsetY - value) > 0.0001f)
            {
                _settings.WorldOffsetY = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldOffsetZ
    {
        get => _settings.WorldOffsetZ;
        set
        {
            if (Math.Abs(_settings.WorldOffsetZ - value) > 0.0001f)
            {
                _settings.WorldOffsetZ = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapScale
    {
        get => _settings.MapScale;
        set
        {
            if (Math.Abs(_settings.MapScale - value) > 0.0001f)
            {
                _settings.MapScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapYaw
    {
        get => _settings.MapYaw;
        set
        {
            if (Math.Abs(_settings.MapYaw - value) > 0.0001f)
            {
                _settings.MapYaw = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapPitch
    {
        get => _settings.MapPitch;
        set
        {
            if (Math.Abs(_settings.MapPitch - value) > 0.0001f)
            {
                _settings.MapPitch = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapRoll
    {
        get => _settings.MapRoll;
        set
        {
            if (Math.Abs(_settings.MapRoll - value) > 0.0001f)
            {
                _settings.MapRoll = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetX
    {
        get => _settings.MapOffsetX;
        set
        {
            if (Math.Abs(_settings.MapOffsetX - value) > 0.0001f)
            {
                _settings.MapOffsetX = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetY
    {
        get => _settings.MapOffsetY;
        set
        {
            if (Math.Abs(_settings.MapOffsetY - value) > 0.0001f)
            {
                _settings.MapOffsetY = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetZ
    {
        get => _settings.MapOffsetZ;
        set
        {
            if (Math.Abs(_settings.MapOffsetZ - value) > 0.0001f)
            {
                _settings.MapOffsetZ = value;
                OnPropertyChanged();
            }
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.MapObjPath))
            OnPropertyChanged(nameof(MapObjPath));
        else if (e.PropertyName == nameof(Viewport3DSettings.PinScale))
            OnPropertyChanged(nameof(PinScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.PinOffsetX))
            OnPropertyChanged(nameof(PinOffsetX));
        else if (e.PropertyName == nameof(Viewport3DSettings.PinOffsetY))
            OnPropertyChanged(nameof(PinOffsetY));
        else if (e.PropertyName == nameof(Viewport3DSettings.PinOffsetZ))
            OnPropertyChanged(nameof(PinOffsetZ));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldScale))
            OnPropertyChanged(nameof(WorldScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldYaw))
            OnPropertyChanged(nameof(WorldYaw));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldPitch))
            OnPropertyChanged(nameof(WorldPitch));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldRoll))
            OnPropertyChanged(nameof(WorldRoll));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldOffsetX))
            OnPropertyChanged(nameof(WorldOffsetX));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldOffsetY))
            OnPropertyChanged(nameof(WorldOffsetY));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldOffsetZ))
            OnPropertyChanged(nameof(WorldOffsetZ));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapScale))
            OnPropertyChanged(nameof(MapScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapYaw))
            OnPropertyChanged(nameof(MapYaw));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapPitch))
            OnPropertyChanged(nameof(MapPitch));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapRoll))
            OnPropertyChanged(nameof(MapRoll));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetX))
            OnPropertyChanged(nameof(MapOffsetX));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetY))
            OnPropertyChanged(nameof(MapOffsetY));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetZ))
            OnPropertyChanged(nameof(MapOffsetZ));
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated -= OnGameStateUpdated;
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        if (state.Heartbeat == _lastHeartbeat)
            return;
        _lastHeartbeat = state.Heartbeat;

        var pins = new List<ViewportPin>();
        foreach (var p in state.Players)
        {
            if (p == null || !p.IsAlive)
                continue;

            var label = GetSlotLabel(p.Slot, _settings.UseAltPlayerBinds);
            pins.Add(new ViewportPin
            {
                Position = p.Position,
                Forward = p.Forward,
                Team = p.Team,
                Slot = p.Slot,
                Label = label,
                IsAlive = p.IsAlive
            });
        }

        Dispatcher.UIThread.Post(() => PinsUpdated?.Invoke(pins));
    }

    private static string GetSlotLabel(int slot, bool useAlt)
    {
        if (slot < 0 || slot > 9)
            return string.Empty;

        if (useAlt && slot >= 5)
            return AltBindLabels[slot - 5];

        return ((slot + 1) % 10).ToString();
    }

    private Matrix3 GetWorldRotation()
    {
        var yaw = MathHelper.DegreesToRadians(_settings.WorldYaw);
        var pitch = MathHelper.DegreesToRadians(_settings.WorldPitch);
        var roll = MathHelper.DegreesToRadians(_settings.WorldRoll);

        var yawMat = Matrix3.CreateRotationY(yaw);
        var pitchMat = Matrix3.CreateRotationX(pitch);
        var rollMat = Matrix3.CreateRotationZ(roll);
        return yawMat * pitchMat * rollMat;
    }

    private static Vector3 Transform(Vector3 value, Matrix3 matrix)
    {
        return new Vector3(
            value.X * matrix.M11 + value.Y * matrix.M21 + value.Z * matrix.M31,
            value.X * matrix.M12 + value.Y * matrix.M22 + value.Z * matrix.M32,
            value.X * matrix.M13 + value.Y * matrix.M23 + value.Z * matrix.M33);
    }

    private static (Vector3 Position, Vector3 Forward, Vector3 Up) ToGameSpace(
        Vector3 position,
        Vector3 forward,
        Vector3 up,
        Matrix3 invRotation,
        Vector3 offset,
        float scale)
    {
        var pos = (position - offset) / scale;
        pos = Transform(pos, invRotation);
        return (pos, Transform(forward, invRotation), Transform(up, invRotation));
    }

    private static (float Pitch, float Yaw, float Roll) GetAngles(Vector3 forward, Vector3 up)
    {
        var yaw = MathHelper.RadiansToDegrees(MathF.Atan2(forward.Y, forward.X));
        var pitch = MathHelper.RadiansToDegrees(-MathF.Asin(Math.Clamp(forward.Z, -1f, 1f)));
        yaw = WrapAngle(yaw);
        pitch = Math.Clamp(pitch, -89.0f, 89.0f);

        var baseUp = GetUpVector(pitch, yaw);
        var roll = GetRollFromUp(baseUp, up, forward);
        return (pitch, yaw, roll);
    }

    private static Vector3 GetUpVector(float pitchDeg, float yawDeg)
    {
        var pitch = MathHelper.DegreesToRadians(pitchDeg);
        var yaw = MathHelper.DegreesToRadians(yawDeg);
        return new Vector3(
            MathF.Sin(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch) * MathF.Sin(yaw),
            MathF.Cos(pitch));
    }

    private static float GetRollFromUp(Vector3 baseUp, Vector3 up, Vector3 forward)
    {
        if (baseUp.LengthSquared < 0.0001f || up.LengthSquared < 0.0001f)
            return 0f;

        baseUp = Vector3.Normalize(baseUp);
        up = Vector3.Normalize(up);
        forward = Vector3.Normalize(forward);

        var cross = Vector3.Cross(baseUp, up);
        var dot = Vector3.Dot(baseUp, up);
        var rollRad = MathF.Atan2(Vector3.Dot(cross, forward), dot);
        return MathHelper.RadiansToDegrees(rollRad);
    }

    private static float WrapAngle(float degrees)
    {
        while (degrees > 180f) degrees -= 360f;
        while (degrees < -180f) degrees += 360f;
        return degrees;
    }
}
