using System.ComponentModel;
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

    // FOV settings
    private double _fovMin = 10.0;
    private double _fovMax = 150.0;
    private double _fovStep = 2.0;
    private double _defaultFov = 90.0;

    // Smoothing settings
    private bool _smoothEnabled = true;
    private double _halfVec = 0.5;
    private double _halfRot = 0.5;
    private double _lockHalfRot = 0.2;
    private double _lockHalfRotTransition = 1.0;
    private double _halfFov = 0.5;

    // Hold settings
    private bool _holdMovementFollowsCamera = true;

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

    #endregion
}
