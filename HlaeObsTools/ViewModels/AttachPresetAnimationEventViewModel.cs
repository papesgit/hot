using System;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

public enum AttachPresetAnimationEventType
{
    Keyframe,
    Transition
}

public sealed class AttachPresetAnimationEventViewModel : ViewModelBase
{
    private AttachPresetAnimationEventType _type = AttachPresetAnimationEventType.Keyframe;
    private double _time;
    private int _order;

    private double? _deltaPosX;
    private double? _deltaPosY;
    private double? _deltaPosZ;

    private double? _deltaPitch;
    private double? _deltaYaw;
    private double? _deltaRoll;

    private double? _fov;

    private double? _transitionDuration = 0.0;
    private HudSettings.AttachmentPresetAnimationTransitionEasing _transitionEasing
        = HudSettings.AttachmentPresetAnimationTransitionEasing.Smoothstep;
    private HudSettings.AttachmentPresetAnimationKeyframeCurve _keyframeEasingCurve
        = HudSettings.AttachmentPresetAnimationKeyframeCurve.Linear;
    private HudSettings.AttachmentPresetAnimationKeyframeEase _keyframeEasingMode
        = HudSettings.AttachmentPresetAnimationKeyframeEase.EaseInOut;
    private HudSettings.AttachmentPresetAnimationRotationSampling _rotationSampling
        = HudSettings.AttachmentPresetAnimationRotationSampling.Live;
    private bool _followAttachmentPitch = true;
    private bool _followAttachmentYaw = true;
    private bool _followAttachmentRoll = true;
    private bool _hasDuplicateKeyframeTime;

    public AttachPresetAnimationEventViewModel()
        : this(isBaseKeyframe: false)
    {
    }

    public AttachPresetAnimationEventViewModel(bool isBaseKeyframe)
    {
        IsBaseKeyframe = isBaseKeyframe;
        if (isBaseKeyframe)
        {
            Type = AttachPresetAnimationEventType.Keyframe;
            Time = 0.0;
            Order = 0;
        }
    }

    public bool IsBaseKeyframe { get; }

    public static HudSettings.AttachmentPresetAnimationTransitionEasing[] TransitionEasingOptions { get; } =
    {
        HudSettings.AttachmentPresetAnimationTransitionEasing.Linear,
        HudSettings.AttachmentPresetAnimationTransitionEasing.Smoothstep,
        HudSettings.AttachmentPresetAnimationTransitionEasing.EaseInOutCubic
    };

    public static HudSettings.AttachmentPresetAnimationKeyframeCurve[] KeyframeEasingCurveOptions { get; } =
    {
        HudSettings.AttachmentPresetAnimationKeyframeCurve.Linear,
        HudSettings.AttachmentPresetAnimationKeyframeCurve.Smoothstep,
        HudSettings.AttachmentPresetAnimationKeyframeCurve.Cubic
    };

    public static HudSettings.AttachmentPresetAnimationKeyframeEase[] KeyframeEasingModeOptions { get; } =
    {
        HudSettings.AttachmentPresetAnimationKeyframeEase.EaseIn,
        HudSettings.AttachmentPresetAnimationKeyframeEase.EaseOut,
        HudSettings.AttachmentPresetAnimationKeyframeEase.EaseInOut
    };

    public static HudSettings.AttachmentPresetAnimationRotationSampling[] RotationSamplingOptions { get; } =
    {
        HudSettings.AttachmentPresetAnimationRotationSampling.Live,
        HudSettings.AttachmentPresetAnimationRotationSampling.FreezeAtSegmentStart
    };

    public AttachPresetAnimationEventType Type
    {
        get => _type;
        set
        {
            if (IsBaseKeyframe)
            {
                value = AttachPresetAnimationEventType.Keyframe;
            }

            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(IsKeyframe));
                OnPropertyChanged(nameof(IsTransition));
                OnPropertyChanged(nameof(UsesKeyframeEasingMode));
            }
        }
    }

    public bool IsKeyframe => Type == AttachPresetAnimationEventType.Keyframe;
    public bool IsTransition => Type == AttachPresetAnimationEventType.Transition;

    public double Time
    {
        get => _time;
        set
        {
            var coercedValue = IsBaseKeyframe ? 0.0 : Math.Max(0.0, value);
            if (SetProperty(ref _time, coercedValue))
            {
                OnPropertyChanged(nameof(DisplayTime));
            }
        }
    }

    public int Order
    {
        get => _order;
        set => SetProperty(ref _order, value);
    }

    public string DisplayTime => $"{Time:0.###}s";

    public double? DeltaPosX { get => _deltaPosX; set => SetProperty(ref _deltaPosX, value); }
    public double? DeltaPosY { get => _deltaPosY; set => SetProperty(ref _deltaPosY, value); }
    public double? DeltaPosZ { get => _deltaPosZ; set => SetProperty(ref _deltaPosZ, value); }

    public double? DeltaPitch { get => _deltaPitch; set => SetProperty(ref _deltaPitch, value); }
    public double? DeltaYaw { get => _deltaYaw; set => SetProperty(ref _deltaYaw, value); }
    public double? DeltaRoll { get => _deltaRoll; set => SetProperty(ref _deltaRoll, value); }

    public double? Fov { get => _fov; set => SetProperty(ref _fov, value); }

    public HudSettings.AttachmentPresetAnimationRotationSampling RotationSampling
    {
        get => _rotationSampling;
        set => SetProperty(ref _rotationSampling, value);
    }

    public bool FollowAttachmentPitch
    {
        get => _followAttachmentPitch;
        set => SetProperty(ref _followAttachmentPitch, value);
    }

    public bool FollowAttachmentYaw
    {
        get => _followAttachmentYaw;
        set => SetProperty(ref _followAttachmentYaw, value);
    }

    public bool FollowAttachmentRoll
    {
        get => _followAttachmentRoll;
        set => SetProperty(ref _followAttachmentRoll, value);
    }

    public double? TransitionDuration
    {
        get => _transitionDuration;
        set => SetProperty(ref _transitionDuration, value);
    }

    public HudSettings.AttachmentPresetAnimationTransitionEasing TransitionEasing
    {
        get => _transitionEasing;
        set => SetProperty(ref _transitionEasing, value);
    }

    public HudSettings.AttachmentPresetAnimationKeyframeCurve KeyframeEasingCurve
    {
        get => _keyframeEasingCurve;
        set
        {
            if (SetProperty(ref _keyframeEasingCurve, value))
            {
                OnPropertyChanged(nameof(UsesKeyframeEasingMode));
            }
        }
    }

    public HudSettings.AttachmentPresetAnimationKeyframeEase KeyframeEasingMode
    {
        get => _keyframeEasingMode;
        set => SetProperty(ref _keyframeEasingMode, value);
    }

    public bool UsesKeyframeEasingMode =>
        IsKeyframe && KeyframeEasingCurve != HudSettings.AttachmentPresetAnimationKeyframeCurve.Linear;

    public bool HasDuplicateKeyframeTime
    {
        get => _hasDuplicateKeyframeTime;
        set => SetProperty(ref _hasDuplicateKeyframeTime, value);
    }
}
