using System.ComponentModel;
using HlaeObsTools.Services.Settings;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

/// <summary>
/// Shared freecam settings for camera control customization.
/// </summary>
public sealed class FreecamSettings : ViewModelBase
{
    // Mouse settings
    private double _mouseSensitivity = 0.12;

    // Movement settings
    private double _moveSpeed = 200.0;
    private double _sprintMultiplier = 2.5;
    private double _verticalSpeed = 200.0;
    private double _speedAdjustRate = 1.1;
    private double _speedMinMultiplier = 0.05;
    private double _speedMaxMultiplier = 5.0;

    // Roll settings
    private double _rollSpeed = 45.0;
    private double _rollSmoothing = 0.8;
    private double _leanStrength = 1.0;
    private double _leanAccelScale = 0.025;
    private double _leanVelocityScale = 0.005;
    private double _leanMaxAngle = 20.0;
    private double _leanHalfTime = 0.30;

    // FOV settings
    private double _fovMin = 10.0;
    private double _fovMax = 150.0;
    private double _fovStep = 2.0;
    private double _defaultFov = 90.0;

    // Smoothing settings
    private bool _smoothEnabled = true;
    private double _halfVec = 0.5;
    private double _halfRot = 0.5;
    private double _lockHalfRot = 0.1;
    private double _lockHalfRotTransition = 1.0;
    private double _halfFov = 0.8;
    private bool _rotCriticalDamping = false;
    private double _rotDampingRatio = 1.0;

    // Hold settings
    private bool _holdMovementFollowsCamera = true;
    private bool _swapRightClickInitMode;

    // Analog keyboard settings
    private bool _analogKeyboardEnabled;
    private double _analogLeftDeadzone;
    private double _analogRightDeadzone;
    private double _analogCurve;
    private bool _clampPitch;

    // Walk settings
    private double _walkMoveSpeed = 160.0;
    private double _walkMoveAcceleration = 800.0;
    private double _walkMoveDeceleration = 800.0;
    private double _walkRunMultiplier = 1.8;
    private double _walkCrouchSpeedMultiplier = 0.6;
    private double _walkLookHalfTime = 0.150;
    private double _walkFovHalfTime = 0.40;
    private double _walkGravity = 800.0;
    private double _walkJumpSpeed = 280.0;
    private double _walkHullRadius = 12.0;
    private double _walkHullHalfHeight = 35.0;
    private double _walkCrouchHullHalfHeight = 12.0;
    private double _walkCameraTopInset = 6.0;
    private double _walkStepHeight = 18.0;
    private double _walkGroundProbe = 2.0;
    private double _walkMinGroundNormalZ = 0.55;
    private bool _walkModeDefaultEnabled;
    private bool _handheldDefaultEnabled;

    // Handheld settings
    private double _walkBobAmplitudeZ = 2.15;
    private double _walkBobAmplitudeSide = 2.70;
    private double _walkBobAmplitudeRoll = 1.20;
    private double _walkBobFrequency = 0.8;
    private double _handheldShakePosAmplitude = 0.45;
    private double _handheldShakeAngAmplitude = 0.65;
    private double _handheldShakeFrequency = 0.4;
    private double _handheldDriftPosAmplitude = 3.30;
    private double _handheldDriftAngAmplitude = 2.36;
    private double _handheldDriftFrequency = 0.15;

    #region Mouse Settings

    /// <summary>
    /// Mouse sensitivity for camera rotation.
    /// </summary>
    public double MouseSensitivity
    {
        get => _mouseSensitivity;
        set => SetProperty(ref _mouseSensitivity, value);
    }

    #endregion

    #region Hold Settings

    /// <summary>
    /// When enabled, hold movement follows the camera's rotation.
    /// </summary>
    public bool HoldMovementFollowsCamera
    {
        get => _holdMovementFollowsCamera;
        set => SetProperty(ref _holdMovementFollowsCamera, value);
    }

    /// <summary>
    /// Swap right-click freecam init mapping:
    /// Off: RMB=inherited motion, Hold CapsLock+RMB=static.
    /// On:  RMB=static, Hold CapsLock+RMB=inherited motion.
    /// </summary>
    public bool SwapRightClickInitMode
    {
        get => _swapRightClickInitMode;
        set => SetProperty(ref _swapRightClickInitMode, value);
    }

    #endregion

    #region Analog Keyboard Settings

    /// <summary>
    /// Enable analog keyboard mode (XInput).
    /// </summary>
    public bool AnalogKeyboardEnabled
    {
        get => _analogKeyboardEnabled;
        set => SetProperty(ref _analogKeyboardEnabled, value);
    }

    /// <summary>
    /// Deadzone for left stick movement (0-1).
    /// </summary>
    public double AnalogLeftDeadzone
    {
        get => _analogLeftDeadzone;
        set => SetProperty(ref _analogLeftDeadzone, value);
    }

    /// <summary>
    /// Deadzone for right stick movement (0-1).
    /// </summary>
    public double AnalogRightDeadzone
    {
        get => _analogRightDeadzone;
        set => SetProperty(ref _analogRightDeadzone, value);
    }

    /// <summary>
    /// Curve amount for analog response (0=linear).
    /// </summary>
    public double AnalogCurve
    {
        get => _analogCurve;
        set => SetProperty(ref _analogCurve, value);
    }

    #endregion

    #region Camera Constraints

    /// <summary>
    /// Clamp pitch to prevent flipping (off by default).
    /// </summary>
    public bool ClampPitch
    {
        get => _clampPitch;
        set => SetProperty(ref _clampPitch, value);
    }

    public double WalkMoveSpeed
    {
        get => _walkMoveSpeed;
        set => SetProperty(ref _walkMoveSpeed, value);
    }

    public double WalkMoveAcceleration
    {
        get => _walkMoveAcceleration;
        set => SetProperty(ref _walkMoveAcceleration, value);
    }

    public double WalkMoveDeceleration
    {
        get => _walkMoveDeceleration;
        set => SetProperty(ref _walkMoveDeceleration, value);
    }

    public double WalkRunMultiplier
    {
        get => _walkRunMultiplier;
        set => SetProperty(ref _walkRunMultiplier, value);
    }

    public double WalkCrouchSpeedMultiplier
    {
        get => _walkCrouchSpeedMultiplier;
        set => SetProperty(ref _walkCrouchSpeedMultiplier, value);
    }

    public double WalkLookHalfTime
    {
        get => _walkLookHalfTime;
        set => SetProperty(ref _walkLookHalfTime, value);
    }

    public double WalkFovHalfTime
    {
        get => _walkFovHalfTime;
        set => SetProperty(ref _walkFovHalfTime, value);
    }

    public double WalkGravity
    {
        get => _walkGravity;
        set => SetProperty(ref _walkGravity, value);
    }

    public double WalkJumpSpeed
    {
        get => _walkJumpSpeed;
        set => SetProperty(ref _walkJumpSpeed, value);
    }

    public double WalkHullRadius
    {
        get => _walkHullRadius;
        set => SetProperty(ref _walkHullRadius, value);
    }

    public double WalkHullHalfHeight
    {
        get => _walkHullHalfHeight;
        set => SetProperty(ref _walkHullHalfHeight, value);
    }

    public double WalkCrouchHullHalfHeight
    {
        get => _walkCrouchHullHalfHeight;
        set => SetProperty(ref _walkCrouchHullHalfHeight, value);
    }

    public double WalkCameraTopInset
    {
        get => _walkCameraTopInset;
        set => SetProperty(ref _walkCameraTopInset, value);
    }

    public double WalkStepHeight
    {
        get => _walkStepHeight;
        set => SetProperty(ref _walkStepHeight, value);
    }

    public double WalkGroundProbe
    {
        get => _walkGroundProbe;
        set => SetProperty(ref _walkGroundProbe, value);
    }

    public double WalkMinGroundNormalZ
    {
        get => _walkMinGroundNormalZ;
        set => SetProperty(ref _walkMinGroundNormalZ, value);
    }

    public bool WalkModeDefaultEnabled
    {
        get => _walkModeDefaultEnabled;
        set => SetProperty(ref _walkModeDefaultEnabled, value);
    }

    public bool HandheldDefaultEnabled
    {
        get => _handheldDefaultEnabled;
        set => SetProperty(ref _handheldDefaultEnabled, value);
    }

    public double WalkBobAmplitudeZ
    {
        get => _walkBobAmplitudeZ;
        set => SetProperty(ref _walkBobAmplitudeZ, value);
    }

    public double WalkBobAmplitudeSide
    {
        get => _walkBobAmplitudeSide;
        set => SetProperty(ref _walkBobAmplitudeSide, value);
    }

    public double WalkBobAmplitudeRoll
    {
        get => _walkBobAmplitudeRoll;
        set => SetProperty(ref _walkBobAmplitudeRoll, value);
    }

    public double WalkBobFrequency
    {
        get => _walkBobFrequency;
        set => SetProperty(ref _walkBobFrequency, value);
    }

    public double HandheldShakePosAmplitude
    {
        get => _handheldShakePosAmplitude;
        set => SetProperty(ref _handheldShakePosAmplitude, value);
    }

    public double HandheldShakeAngAmplitude
    {
        get => _handheldShakeAngAmplitude;
        set => SetProperty(ref _handheldShakeAngAmplitude, value);
    }

    public double HandheldShakeFrequency
    {
        get => _handheldShakeFrequency;
        set => SetProperty(ref _handheldShakeFrequency, value);
    }

    public double HandheldDriftPosAmplitude
    {
        get => _handheldDriftPosAmplitude;
        set => SetProperty(ref _handheldDriftPosAmplitude, value);
    }

    public double HandheldDriftAngAmplitude
    {
        get => _handheldDriftAngAmplitude;
        set => SetProperty(ref _handheldDriftAngAmplitude, value);
    }

    public double HandheldDriftFrequency
    {
        get => _handheldDriftFrequency;
        set => SetProperty(ref _handheldDriftFrequency, value);
    }

    #endregion

    #region Movement Settings

    /// <summary>
    /// Base movement speed in units per second.
    /// </summary>
    public double MoveSpeed
    {
        get => _moveSpeed;
        set => SetProperty(ref _moveSpeed, value);
    }

    /// <summary>
    /// Sprint multiplier when holding shift.
    /// </summary>
    public double SprintMultiplier
    {
        get => _sprintMultiplier;
        set => SetProperty(ref _sprintMultiplier, value);
    }

    /// <summary>
    /// Vertical movement speed (up/down).
    /// </summary>
    public double VerticalSpeed
    {
        get => _verticalSpeed;
        set => SetProperty(ref _verticalSpeed, value);
    }

    /// <summary>
    /// Speed adjustment rate when holding mouse buttons.
    /// </summary>
    public double SpeedAdjustRate
    {
        get => _speedAdjustRate;
        set => SetProperty(ref _speedAdjustRate, value);
    }

    /// <summary>
    /// Minimum speed multiplier clamp.
    /// </summary>
    public double SpeedMinMultiplier
    {
        get => _speedMinMultiplier;
        set => SetProperty(ref _speedMinMultiplier, value);
    }

    /// <summary>
    /// Maximum speed multiplier clamp.
    /// </summary>
    public double SpeedMaxMultiplier
    {
        get => _speedMaxMultiplier;
        set => SetProperty(ref _speedMaxMultiplier, value);
    }

    #endregion

    #region Roll Settings

    /// <summary>
    /// Camera roll speed in degrees per second.
    /// </summary>
    public double RollSpeed
    {
        get => _rollSpeed;
        set => SetProperty(ref _rollSpeed, value);
    }

    /// <summary>
    /// Roll smoothing factor (0-1).
    /// </summary>
    public double RollSmoothing
    {
        get => _rollSmoothing;
        set => SetProperty(ref _rollSmoothing, value);
    }

    /// <summary>
    /// Lean strength for camera banking.
    /// </summary>
    public double LeanStrength
    {
        get => _leanStrength;
        set => SetProperty(ref _leanStrength, value);
    }

    /// <summary>
    /// Lean amount per unit of lateral acceleration.
    /// </summary>
    public double LeanAccelScale
    {
        get => _leanAccelScale;
        set => SetProperty(ref _leanAccelScale, value);
    }

    /// <summary>
    /// Lean amount per unit of lateral velocity.
    /// </summary>
    public double LeanVelocityScale
    {
        get => _leanVelocityScale;
        set => SetProperty(ref _leanVelocityScale, value);
    }

    /// <summary>
    /// Maximum lean angle in degrees.
    /// </summary>
    public double LeanMaxAngle
    {
        get => _leanMaxAngle;
        set => SetProperty(ref _leanMaxAngle, value);
    }

    /// <summary>
    /// Lean response half-time in seconds.
    /// </summary>
    public double LeanHalfTime
    {
        get => _leanHalfTime;
        set => SetProperty(ref _leanHalfTime, value);
    }

    #endregion

    #region FOV Settings

    /// <summary>
    /// Minimum field of view.
    /// </summary>
    public double FovMin
    {
        get => _fovMin;
        set => SetProperty(ref _fovMin, value);
    }

    /// <summary>
    /// Maximum field of view.
    /// </summary>
    public double FovMax
    {
        get => _fovMax;
        set => SetProperty(ref _fovMax, value);
    }

    /// <summary>
    /// FOV adjustment step size.
    /// </summary>
    public double FovStep
    {
        get => _fovStep;
        set => SetProperty(ref _fovStep, value);
    }

    /// <summary>
    /// Default field of view.
    /// </summary>
    public double DefaultFov
    {
        get => _defaultFov;
        set => SetProperty(ref _defaultFov, value);
    }

    #endregion

    #region Smoothing Settings

    /// <summary>
    /// Enable camera smoothing.
    /// </summary>
    public bool SmoothEnabled
    {
        get => _smoothEnabled;
        set => SetProperty(ref _smoothEnabled, value);
    }

    /// <summary>
    /// Position smoothing half-time in seconds.
    /// </summary>
    public double HalfVec
    {
        get => _halfVec;
        set => SetProperty(ref _halfVec, value);
    }

    /// <summary>
    /// Rotation smoothing half-time in seconds.
    /// </summary>
    public double HalfRot
    {
        get => _halfRot;
        set => SetProperty(ref _halfRot, value);
    }

    /// <summary>
    /// Rotation smoothing half-time in seconds while player lock is active.
    /// </summary>
    public double LockHalfRot
    {
        get => _lockHalfRot;
        set => SetProperty(ref _lockHalfRot, value);
    }

    /// <summary>
    /// Seconds to transition between halfRot and lockHalfRot.
    /// </summary>
    public double LockHalfRotTransition
    {
        get => _lockHalfRotTransition;
        set => SetProperty(ref _lockHalfRotTransition, value);
    }

    /// <summary>
    /// FOV smoothing half-time in seconds.
    /// </summary>
    public double HalfFov
    {
        get => _halfFov;
        set => SetProperty(ref _halfFov, value);
    }

    /// <summary>
    /// Use critically damped rotation smoothing (off uses long-path slerp).
    /// </summary>
    public bool RotCriticalDamping
    {
        get => _rotCriticalDamping;
        set => SetProperty(ref _rotCriticalDamping, value);
    }

    /// <summary>
    /// Damping ratio for critical damping (>= 1.0).
    /// </summary>
    public double RotDampingRatio
    {
        get => _rotDampingRatio;
        set
        {
            var clamped = value < 1.0 ? 1.0 : value;
            SetProperty(ref _rotDampingRatio, clamped);
        }
    }

    #endregion

    public FreecamSettingsData ToData()
    {
        return new FreecamSettingsData
        {
            MouseSensitivity = MouseSensitivity,
            MoveSpeed = MoveSpeed,
            SprintMultiplier = SprintMultiplier,
            VerticalSpeed = VerticalSpeed,
            SpeedAdjustRate = SpeedAdjustRate,
            SpeedMinMultiplier = SpeedMinMultiplier,
            SpeedMaxMultiplier = SpeedMaxMultiplier,
            RollSpeed = RollSpeed,
            RollSmoothing = RollSmoothing,
            LeanStrength = LeanStrength,
            LeanAccelScale = LeanAccelScale,
            LeanVelocityScale = LeanVelocityScale,
            LeanMaxAngle = LeanMaxAngle,
            LeanHalfTime = LeanHalfTime,
            FovMin = FovMin,
            FovMax = FovMax,
            FovStep = FovStep,
            DefaultFov = DefaultFov,
            SmoothEnabled = SmoothEnabled,
            HalfVec = HalfVec,
            HalfRot = HalfRot,
            LockHalfRot = LockHalfRot,
            LockHalfRotTransition = LockHalfRotTransition,
            HalfFov = HalfFov,
            RotCriticalDamping = RotCriticalDamping,
            RotDampingRatio = RotDampingRatio,
            HoldMovementFollowsCamera = HoldMovementFollowsCamera,
            SwapRightClickInitMode = SwapRightClickInitMode,
            AnalogKeyboardEnabled = AnalogKeyboardEnabled,
            AnalogLeftDeadzone = AnalogLeftDeadzone,
            AnalogRightDeadzone = AnalogRightDeadzone,
            AnalogCurve = AnalogCurve,
            ClampPitch = ClampPitch,
            WalkMoveSpeed = WalkMoveSpeed,
            WalkMoveAcceleration = WalkMoveAcceleration,
            WalkMoveDeceleration = WalkMoveDeceleration,
            WalkRunMultiplier = WalkRunMultiplier,
            WalkCrouchSpeedMultiplier = WalkCrouchSpeedMultiplier,
            WalkLookHalfTime = WalkLookHalfTime,
            WalkFovHalfTime = WalkFovHalfTime,
            WalkGravity = WalkGravity,
            WalkJumpSpeed = WalkJumpSpeed,
            WalkHullRadius = WalkHullRadius,
            WalkHullHalfHeight = WalkHullHalfHeight,
            WalkCrouchHullHalfHeight = WalkCrouchHullHalfHeight,
            WalkCameraTopInset = WalkCameraTopInset,
            WalkStepHeight = WalkStepHeight,
            WalkGroundProbe = WalkGroundProbe,
            WalkMinGroundNormalZ = WalkMinGroundNormalZ,
            WalkModeDefaultEnabled = WalkModeDefaultEnabled,
            HandheldDefaultEnabled = HandheldDefaultEnabled,
            WalkBobAmplitudeZ = WalkBobAmplitudeZ,
            WalkBobAmplitudeSide = WalkBobAmplitudeSide,
            WalkBobAmplitudeRoll = WalkBobAmplitudeRoll,
            WalkBobFrequency = WalkBobFrequency,
            HandheldShakePosAmplitude = HandheldShakePosAmplitude,
            HandheldShakeAngAmplitude = HandheldShakeAngAmplitude,
            HandheldShakeFrequency = HandheldShakeFrequency,
            HandheldDriftPosAmplitude = HandheldDriftPosAmplitude,
            HandheldDriftAngAmplitude = HandheldDriftAngAmplitude,
            HandheldDriftFrequency = HandheldDriftFrequency
        };
    }

    public void Apply(FreecamSettingsData data)
    {
        if (data == null)
            return;

        MouseSensitivity = data.MouseSensitivity;
        MoveSpeed = data.MoveSpeed;
        SprintMultiplier = data.SprintMultiplier;
        VerticalSpeed = data.VerticalSpeed;
        SpeedAdjustRate = data.SpeedAdjustRate;
        SpeedMinMultiplier = data.SpeedMinMultiplier;
        SpeedMaxMultiplier = data.SpeedMaxMultiplier;
        RollSpeed = data.RollSpeed;
        RollSmoothing = data.RollSmoothing;
        LeanStrength = data.LeanStrength;
        LeanAccelScale = data.LeanAccelScale;
        LeanVelocityScale = data.LeanVelocityScale;
        LeanMaxAngle = data.LeanMaxAngle;
        LeanHalfTime = data.LeanHalfTime;
        FovMin = data.FovMin;
        FovMax = data.FovMax;
        FovStep = data.FovStep;
        DefaultFov = data.DefaultFov;
        SmoothEnabled = data.SmoothEnabled;
        HalfVec = data.HalfVec;
        HalfRot = data.HalfRot;
        LockHalfRot = data.LockHalfRot;
        LockHalfRotTransition = data.LockHalfRotTransition;
        HalfFov = data.HalfFov;
        RotCriticalDamping = data.RotCriticalDamping;
        RotDampingRatio = data.RotDampingRatio;
        HoldMovementFollowsCamera = data.HoldMovementFollowsCamera;
        SwapRightClickInitMode = data.SwapRightClickInitMode;
        AnalogKeyboardEnabled = data.AnalogKeyboardEnabled;
        AnalogLeftDeadzone = data.AnalogLeftDeadzone;
        AnalogRightDeadzone = data.AnalogRightDeadzone;
        AnalogCurve = data.AnalogCurve;
        ClampPitch = data.ClampPitch;
        WalkMoveSpeed = data.WalkMoveSpeed;
        WalkMoveAcceleration = data.WalkMoveAcceleration;
        WalkMoveDeceleration = data.WalkMoveDeceleration;
        WalkRunMultiplier = data.WalkRunMultiplier;
        WalkCrouchSpeedMultiplier = data.WalkCrouchSpeedMultiplier;
        WalkLookHalfTime = data.WalkLookHalfTime;
        WalkFovHalfTime = data.WalkFovHalfTime;
        WalkGravity = data.WalkGravity;
        WalkJumpSpeed = data.WalkJumpSpeed;
        WalkHullRadius = data.WalkHullRadius;
        WalkHullHalfHeight = data.WalkHullHalfHeight;
        WalkCrouchHullHalfHeight = data.WalkCrouchHullHalfHeight;
        WalkCameraTopInset = data.WalkCameraTopInset;
        WalkStepHeight = data.WalkStepHeight;
        WalkGroundProbe = data.WalkGroundProbe;
        WalkMinGroundNormalZ = data.WalkMinGroundNormalZ;
        WalkModeDefaultEnabled = data.WalkModeDefaultEnabled;
        HandheldDefaultEnabled = data.HandheldDefaultEnabled;
        WalkBobAmplitudeZ = data.WalkBobAmplitudeZ;
        WalkBobAmplitudeSide = data.WalkBobAmplitudeSide;
        WalkBobAmplitudeRoll = data.WalkBobAmplitudeRoll;
        WalkBobFrequency = data.WalkBobFrequency;
        HandheldShakePosAmplitude = data.HandheldShakePosAmplitude;
        HandheldShakeAngAmplitude = data.HandheldShakeAngAmplitude;
        HandheldShakeFrequency = data.HandheldShakeFrequency;
        HandheldDriftPosAmplitude = data.HandheldDriftPosAmplitude;
        HandheldDriftAngAmplitude = data.HandheldDriftAngAmplitude;
        HandheldDriftFrequency = data.HandheldDriftFrequency;
    }

    public void ResetToDefaults()
    {
        Apply(new FreecamSettings().ToData());
    }
}
