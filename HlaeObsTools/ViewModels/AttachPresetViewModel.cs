using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

public sealed class AttachPresetViewModel : ViewModelBase
{
    private static readonly string[] DefaultAttachmentOptions = new[]
    {
        "POV",
        "knife","eholster","pistol","leg_l_iktarget","leg_r_iktarget","defusekit",
        "grenade0","grenade1","grenade2","grenade3","grenade4","primary","primary_smg",
        "c4","look_straight_ahead_stand","clip_limit","weapon_hand_l","weapon_hand_r",
        "gun_accurate","weaponhier_l_iktarget","weaponhier_r_iktarget",
        "look_straight_ahead_crouch","axis_of_intent"
    };
    public static IReadOnlyList<string> DefaultAttachmentOptionsList => DefaultAttachmentOptions;
    private string _title;
    private string _name = string.Empty;
    private string _attachmentName = string.Empty;
    private double? _offsetPosX;
    private double? _offsetPosY;
    private double? _offsetPosZ;
    private double? _offsetPitch;
    private double? _offsetYaw;
    private double? _offsetRoll;
    private double? _fov;
    private HudSettings.AttachmentPresetRotationReference _rotationReference
        = HudSettings.AttachmentPresetRotationReference.Attachment;
    private HudSettings.AttachmentPresetRotationBasis _rotationBasisPitch
        = HudSettings.AttachmentPresetRotationBasis.Attachment;
    private HudSettings.AttachmentPresetRotationBasis _rotationBasisYaw
        = HudSettings.AttachmentPresetRotationBasis.Attachment;
    private HudSettings.AttachmentPresetRotationBasis _rotationBasisRoll
        = HudSettings.AttachmentPresetRotationBasis.Attachment;
    private bool _rotationLockPitch;
    private bool _rotationLockYaw;
    private bool _rotationLockRoll;
    private bool _animationEnabled;
    private readonly ObservableCollection<AttachPresetAnimationEventViewModel> _animationEvents = new();
    public IReadOnlyList<string> AttachmentOptions { get; } = DefaultAttachmentOptions;
    public IReadOnlyList<HudSettings.AttachmentPresetRotationReference> RotationReferenceOptions { get; } =
        new[]
        {
            HudSettings.AttachmentPresetRotationReference.Attachment,
            HudSettings.AttachmentPresetRotationReference.OffsetLocal
        };
    public IReadOnlyList<HudSettings.AttachmentPresetRotationBasis> RotationBasisOptions { get; } =
        new[]
        {
            HudSettings.AttachmentPresetRotationBasis.Attachment,
            HudSettings.AttachmentPresetRotationBasis.World
        };

    public AttachPresetViewModel(string title)
    {
        _title = title;
        EnsureBaseKeyframe();
        _animationEvents.CollectionChanged += (_, _) =>
        {
            HookAnimationEventChanges();
            OnPropertyChanged(nameof(AnimationSummary));
        };
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string AttachmentName
    {
        get => _attachmentName;
        set => SetProperty(ref _attachmentName, value);
    }

    public double? OffsetPosX
    {
        get => _offsetPosX;
        set => SetProperty(ref _offsetPosX, value);
    }

    public double? OffsetPosY
    {
        get => _offsetPosY;
        set => SetProperty(ref _offsetPosY, value);
    }

    public double? OffsetPosZ
    {
        get => _offsetPosZ;
        set => SetProperty(ref _offsetPosZ, value);
    }

    public double? OffsetPitch
    {
        get => _offsetPitch;
        set => SetProperty(ref _offsetPitch, value);
    }

    public double? OffsetYaw
    {
        get => _offsetYaw;
        set => SetProperty(ref _offsetYaw, value);
    }

    public double? OffsetRoll
    {
        get => _offsetRoll;
        set => SetProperty(ref _offsetRoll, value);
    }

    public double? Fov
    {
        get => _fov;
        set => SetProperty(ref _fov, value);
    }

    public HudSettings.AttachmentPresetRotationReference RotationReference
    {
        get => _rotationReference;
        set => SetProperty(ref _rotationReference, value);
    }

    public HudSettings.AttachmentPresetRotationBasis RotationBasisPitch
    {
        get => _rotationBasisPitch;
        set => SetProperty(ref _rotationBasisPitch, value);
    }

    public HudSettings.AttachmentPresetRotationBasis RotationBasisYaw
    {
        get => _rotationBasisYaw;
        set => SetProperty(ref _rotationBasisYaw, value);
    }

    public HudSettings.AttachmentPresetRotationBasis RotationBasisRoll
    {
        get => _rotationBasisRoll;
        set => SetProperty(ref _rotationBasisRoll, value);
    }

    public bool RotationLockPitch
    {
        get => _rotationLockPitch;
        set => SetProperty(ref _rotationLockPitch, value);
    }

    public bool RotationLockYaw
    {
        get => _rotationLockYaw;
        set => SetProperty(ref _rotationLockYaw, value);
    }

    public bool RotationLockRoll
    {
        get => _rotationLockRoll;
        set => SetProperty(ref _rotationLockRoll, value);
    }

    public bool AnimationEnabled
    {
        get => _animationEnabled;
        set => SetProperty(ref _animationEnabled, value);
    }

    public ObservableCollection<AttachPresetAnimationEventViewModel> AnimationEvents => _animationEvents;

    public string AnimationSummary
    {
        get
        {
            if (!AnimationEnabled && AnimationEvents.Count <= 1) return "Anim: off";
            var transitionCount = AnimationEvents.Count(e => e.IsTransition);
            var keyCount = AnimationEvents.Count(e => e.IsKeyframe);
            var duration = AnimationEvents.Count > 0 ? AnimationEvents.Max(e => e.Time) : 0.0;
            var transitionText = transitionCount > 0 ? ", 1 transition" : string.Empty;
            return $"Anim: {(AnimationEnabled ? "on" : "off")} ({keyCount} keys{transitionText}, {duration:0.###}s)";
        }
    }

    public void LoadFrom(HudSettings.AttachmentPreset preset)
    {
        Name = preset.Name;
        AttachmentName = preset.AttachmentName;
        OffsetPosX = preset.OffsetPosX == 0.0 ? null : preset.OffsetPosX;
        OffsetPosY = preset.OffsetPosY == 0.0 ? null : preset.OffsetPosY;
        OffsetPosZ = preset.OffsetPosZ == 0.0 ? null : preset.OffsetPosZ;
        OffsetPitch = preset.OffsetPitch == 0.0 ? null : preset.OffsetPitch;
        OffsetYaw = preset.OffsetYaw == 0.0 ? null : preset.OffsetYaw;
        OffsetRoll = preset.OffsetRoll == 0.0 ? null : preset.OffsetRoll;
        Fov = preset.Fov == 90.0 ? null : preset.Fov;
        RotationReference = preset.RotationReference;
        RotationBasisPitch = preset.RotationBasisPitch;
        RotationBasisYaw = preset.RotationBasisYaw;
        RotationBasisRoll = preset.RotationBasisRoll;
        RotationLockPitch = preset.RotationLockPitch;
        RotationLockYaw = preset.RotationLockYaw;
        RotationLockRoll = preset.RotationLockRoll;

        LoadAnimationFrom(preset.Animation);
    }

    public HudSettings.AttachmentPreset ToModel()
    {
        return new HudSettings.AttachmentPreset
        {
            Name = Name ?? string.Empty,
            AttachmentName = AttachmentName ?? string.Empty,
            OffsetPosX = OffsetPosX ?? 0.0,
            OffsetPosY = OffsetPosY ?? 0.0,
            OffsetPosZ = OffsetPosZ ?? 0.0,
            OffsetPitch = OffsetPitch ?? 0.0,
            OffsetYaw = OffsetYaw ?? 0.0,
            OffsetRoll = OffsetRoll ?? 0.0,
            Fov = Fov ?? 90.0,
            RotationReference = RotationReference,
            RotationBasisPitch = RotationBasisPitch,
            RotationBasisYaw = RotationBasisYaw,
            RotationBasisRoll = RotationBasisRoll,
            RotationLockPitch = RotationLockPitch,
            RotationLockYaw = RotationLockYaw,
            RotationLockRoll = RotationLockRoll,
            Animation = ToAnimationModel()
        };
    }

    public void EnsureBaseKeyframe()
    {
        if (_animationEvents.Count > 0 && _animationEvents[0].IsBaseKeyframe)
            return;

        _animationEvents.Insert(0, new AttachPresetAnimationEventViewModel(isBaseKeyframe: true));
        OnPropertyChanged(nameof(AnimationSummary));
    }

    private void LoadAnimationFrom(HudSettings.AttachmentPresetAnimation animation)
    {
        AnimationEnabled = animation.Enabled;

        _animationEvents.Clear();
        EnsureBaseKeyframe();

        foreach (var e in animation.Events.OrderBy(e => e.Time).ThenBy(e => e.Order))
        {
            // Base keyframe is implicit and uneditable.
            if (e.Type == HudSettings.AttachmentPresetAnimationEventType.Keyframe && e.Time == 0.0 && e.Order == 0)
                continue;

            _animationEvents.Add(new AttachPresetAnimationEventViewModel
            {
                Type = e.Type == HudSettings.AttachmentPresetAnimationEventType.Transition
                    ? AttachPresetAnimationEventType.Transition
                    : AttachPresetAnimationEventType.Keyframe,
                Time = e.Time,
                Order = e.Order,
                DeltaPosX = e.DeltaPosX,
                DeltaPosY = e.DeltaPosY,
                DeltaPosZ = e.DeltaPosZ,
                DeltaPitch = e.DeltaPitch,
                DeltaYaw = e.DeltaYaw,
                DeltaRoll = e.DeltaRoll,
                Fov = e.Fov,
                TransitionDuration = e.TransitionDuration,
                TransitionEasing = e.TransitionEasing ?? HudSettings.AttachmentPresetAnimationTransitionEasing.Smoothstep,
                KeyframeEasingCurve = e.KeyframeEasingCurve ?? HudSettings.AttachmentPresetAnimationKeyframeCurve.Linear,
                KeyframeEasingMode = e.KeyframeEasingMode ?? HudSettings.AttachmentPresetAnimationKeyframeEase.EaseInOut
            });
        }

        HookAnimationEventChanges();
        OnPropertyChanged(nameof(AnimationSummary));
    }

    private HudSettings.AttachmentPresetAnimation ToAnimationModel()
    {
        EnsureBaseKeyframe();

        var events = _animationEvents
            .Select(e => new HudSettings.AttachmentPresetAnimationEvent
            {
                Type = e.Type == AttachPresetAnimationEventType.Transition
                    ? HudSettings.AttachmentPresetAnimationEventType.Transition
                    : HudSettings.AttachmentPresetAnimationEventType.Keyframe,
                Time = e.Time,
                Order = e.Order,
                DeltaPosX = e.IsKeyframe ? e.DeltaPosX : null,
                DeltaPosY = e.IsKeyframe ? e.DeltaPosY : null,
                DeltaPosZ = e.IsKeyframe ? e.DeltaPosZ : null,
                DeltaPitch = e.IsKeyframe ? e.DeltaPitch : null,
                DeltaYaw = e.IsKeyframe ? e.DeltaYaw : null,
                DeltaRoll = e.IsKeyframe ? e.DeltaRoll : null,
                Fov = e.IsKeyframe ? e.Fov : null,
                TransitionDuration = e.IsTransition ? e.TransitionDuration : null,
                TransitionEasing = e.IsTransition ? e.TransitionEasing : null,
                KeyframeEasingCurve = e.IsKeyframe ? e.KeyframeEasingCurve : null,
                KeyframeEasingMode = e.IsKeyframe ? e.KeyframeEasingMode : null
            })
            .ToList();

        return new HudSettings.AttachmentPresetAnimation
        {
            Enabled = AnimationEnabled,
            Events = events
        };
    }

    private void HookAnimationEventChanges()
    {
        foreach (var e in _animationEvents)
        {
            e.PropertyChanged -= OnAnimationEventChanged;
            e.PropertyChanged += OnAnimationEventChanged;
        }
    }

    private void OnAnimationEventChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(AnimationSummary));
    }
}
