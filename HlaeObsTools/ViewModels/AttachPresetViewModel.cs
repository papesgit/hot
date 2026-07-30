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
        "POV","pistol","knife","eholster","grenade0","grenade1","grenade2","grenade3","grenade4","defusekit", 
        "c4","primary_smg","primary","clip_limit","weapon","weapon_hand_r","weapon_hand_l","weapon_center"
    };
    public static IReadOnlyList<string> DefaultAttachmentOptionsList => DefaultAttachmentOptions;
    private static readonly string[] DefaultBoneOptions = new[]
    {
        "root_motion", "pelvis", "spine_0", "spine_1", "spine_2", "spine_3", "neck_0", "head_0", "eyeball_l", "eyeball_r", "eye_target", "jiggle_hood (CT)", "clavicle_L", "arm_upper_L", "arm_lower_L", "hand_L", "finger_middle_meta_L", "finger_middle_0_L", "finger_middle_1_L", "finger_middle_2_L", "finger_pinky_meta_L", "finger_pinky_0_L", "finger_pinky_1_L", "finger_pinky_2_L", "finger_index_meta_L", "finger_index_0_L", "finger_index_1_L", "finger_index_2_L", "finger_thumb_0_L", "finger_thumb_1_L", "finger_thumb_2_L", "finger_ring_meta_L", "finger_ring_0_L", "finger_ring_1_L", "finger_ring_2_L", "arm_lower_L_TWIST", "arm_lower_L_TWIST1", "arm_upper_L_TWIST", "arm_upper_L_TWIST1", "scapula_L (CT)", "clavicle_R", "arm_upper_R", "arm_lower_R", "hand_R", "finger_middle_meta_R", "finger_middle_0_R", "finger_middle_1_R", "finger_middle_2_R", "finger_pinky_meta_R", "finger_pinky_0_R", "finger_pinky_1_R", "finger_pinky_2_R", "finger_index_meta_R", "finger_index_0_R", "finger_index_1_R", "finger_index_2_R", "finger_thumb_0_R", "finger_thumb_1_R", "finger_thumb_2_R", "finger_ring_meta_R", "finger_ring_0_R", "finger_ring_1_R", "finger_ring_2_R", "arm_lower_R_TWIST", "arm_lower_R_TWIST1", "arm_upper_R_TWIST", "arm_upper_R_TWIST1", "scapula_R (CT)", "jiggle_primary", "jiggle_front_micropouches (CT)", "jiggle_radio (CT)", "jiggle_front_pouch_01 (CT)", "jiggle_front_pouch_02 (CT)", "leg_upper_L", "leg_lower_L", "ankle_L", "ball_L", "leg_upper_L_TWIST", "leg_upper_L_TWIST1", "jiggle_climbinggear_01 (CT)", "jiggle_climbinggear_02 (CT)", "leg_upper_R", "leg_lower_R", "ankle_R", "ball_R", "leg_upper_R_TWIST", "leg_upper_R_TWIST1", "jiggle_holster (CT)", "wpnPivot", "wpn"
    };
    public static IReadOnlyList<string> DefaultBoneOptionsList => DefaultBoneOptions;
    private string _title;
    private string _name = string.Empty;
    private string _attachmentName = string.Empty;
    private string _boneName = string.Empty;
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
    public IReadOnlyList<string> BoneOptions { get; } = DefaultBoneOptions;
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
            UpdateDuplicateKeyframeWarnings();
            OnPropertyChanged(nameof(AnimationSummary));
        };
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public int PresetIndex { get; set; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string AttachmentName
    {
        get => _attachmentName;
        set
        {
            if (!SetProperty(ref _attachmentName, value ?? string.Empty)) return;
            if (!string.IsNullOrEmpty(_attachmentName)) BoneName = string.Empty;
        }
    }

    public string BoneName
    {
        get => _boneName;
        set
        {
            var normalized = (value ?? string.Empty).Replace(" (CT)", string.Empty);
            if (!SetProperty(ref _boneName, normalized)) return;
            OnPropertyChanged(nameof(BoneSelection));
            if (!string.IsNullOrEmpty(_boneName)) AttachmentName = string.Empty;
        }
    }

    public string BoneSelection
    {
        get => BoneOptions.FirstOrDefault(option => option.Replace(" (CT)", string.Empty) == BoneName) ?? BoneName;
        set => BoneName = value;
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
        BoneName = preset.BoneName;
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
            BoneName = BoneName ?? string.Empty,
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
        var baseKeyframe = _animationEvents.FirstOrDefault(e => e.IsBaseKeyframe);
        if (baseKeyframe == null)
        {
            _animationEvents.Insert(0, new AttachPresetAnimationEventViewModel(isBaseKeyframe: true));
            OnPropertyChanged(nameof(AnimationSummary));
            return;
        }

        var index = _animationEvents.IndexOf(baseKeyframe);
        if (index > 0)
        {
            _animationEvents.Move(index, 0);
        }

        NormalizeAnimationEvents();
        OnPropertyChanged(nameof(AnimationSummary));
    }

    private void LoadAnimationFrom(HudSettings.AttachmentPresetAnimation animation)
    {
        AnimationEnabled = animation.Enabled;

        _animationEvents.Clear();
        EnsureBaseKeyframe();

        foreach (var e in animation.Events.OrderBy(EventSortKey))
        {
            var isBaseKeyframe = e.Type == HudSettings.AttachmentPresetAnimationEventType.Keyframe
                && e.Time == 0.0
                && e.Order == 0;
            if (isBaseKeyframe)
            {
                var baseVm = _animationEvents.FirstOrDefault(x => x.IsBaseKeyframe);
                if (baseVm != null)
                {
                    baseVm.DeltaPosX = e.DeltaPosX;
                    baseVm.DeltaPosY = e.DeltaPosY;
                    baseVm.DeltaPosZ = e.DeltaPosZ;
                    baseVm.DeltaPitch = e.DeltaPitch;
                    baseVm.DeltaYaw = e.DeltaYaw;
                    baseVm.DeltaRoll = e.DeltaRoll;
                    baseVm.Fov = e.Fov;
                    baseVm.RotationSampling = e.RotationSampling;
                    baseVm.FollowAttachmentPitch = e.FollowAttachmentPitch;
                    baseVm.FollowAttachmentYaw = e.FollowAttachmentYaw;
                    baseVm.FollowAttachmentRoll = e.FollowAttachmentRoll;
                    baseVm.TransitionDuration = e.TransitionDuration;
                    baseVm.TransitionEasing = e.TransitionEasing ?? HudSettings.AttachmentPresetAnimationTransitionEasing.Smoothstep;
                    baseVm.KeyframeEasingCurve = e.KeyframeEasingCurve ?? HudSettings.AttachmentPresetAnimationKeyframeCurve.Linear;
                    baseVm.KeyframeEasingMode = e.KeyframeEasingMode ?? HudSettings.AttachmentPresetAnimationKeyframeEase.EaseInOut;
                }
                continue;
            }

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
                RotationSampling = e.RotationSampling,
                FollowAttachmentPitch = e.FollowAttachmentPitch,
                FollowAttachmentYaw = e.FollowAttachmentYaw,
                FollowAttachmentRoll = e.FollowAttachmentRoll,
                TransitionDuration = e.TransitionDuration,
                TransitionEasing = e.TransitionEasing ?? HudSettings.AttachmentPresetAnimationTransitionEasing.Smoothstep,
                KeyframeEasingCurve = e.KeyframeEasingCurve ?? HudSettings.AttachmentPresetAnimationKeyframeCurve.Linear,
                KeyframeEasingMode = e.KeyframeEasingMode ?? HudSettings.AttachmentPresetAnimationKeyframeEase.EaseInOut
            });
        }

        HookAnimationEventChanges();
        NormalizeAnimationEvents();
        OnPropertyChanged(nameof(AnimationSummary));
    }

    private HudSettings.AttachmentPresetAnimation ToAnimationModel()
    {
        var events = _animationEvents
            .OrderBy(EventSortKey)
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
                RotationSampling = e.IsKeyframe ? e.RotationSampling : HudSettings.AttachmentPresetAnimationRotationSampling.Live,
                FollowAttachmentPitch = e.IsKeyframe && e.FollowAttachmentPitch,
                FollowAttachmentYaw = e.IsKeyframe && e.FollowAttachmentYaw,
                FollowAttachmentRoll = e.IsKeyframe && e.FollowAttachmentRoll,
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
        if (e.PropertyName == nameof(AttachPresetAnimationEventViewModel.Time))
        {
            NormalizeAnimationEvents();
        }

        OnPropertyChanged(nameof(AnimationSummary));
    }

    private void NormalizeAnimationEvents()
    {
        if (_animationEvents.Count <= 1)
        {
            UpdateDuplicateKeyframeWarnings();
            return;
        }

        var ordered = _animationEvents
            .OrderBy(EventSortKey)
            .ToList();

        for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var currentIndex = _animationEvents.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
            {
                _animationEvents.Move(currentIndex, targetIndex);
            }
        }

        var nextOrderByTime = new Dictionary<double, int>();
        foreach (var ev in _animationEvents)
        {
            if (!nextOrderByTime.TryGetValue(ev.Time, out var nextOrder))
            {
                nextOrder = 0;
            }

            ev.Order = nextOrder;
            nextOrderByTime[ev.Time] = nextOrder + 1;
        }
        
        UpdateDuplicateKeyframeWarnings();
    }

    private void UpdateDuplicateKeyframeWarnings()
    {
        var duplicateKeyframeTimes = _animationEvents
            .Where(e => e.IsKeyframe)
            .GroupBy(e => e.Time)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        foreach (var ev in _animationEvents)
        {
            ev.HasDuplicateKeyframeTime = ev.IsKeyframe
                && !ev.IsBaseKeyframe
                && duplicateKeyframeTimes.Contains(ev.Time);
        }
    }
    private static (int Group, double Time, int TypeOrder, int Order) EventSortKey(AttachPresetAnimationEventViewModel e)
    {
        return (
            e.IsBaseKeyframe ? 0 : 1,
            e.IsBaseKeyframe ? 0.0 : e.Time,
            e.IsTransition ? 0 : 1,
            e.Order
        );
    }

    private static (int Group, double Time, int TypeOrder, int Order) EventSortKey(HudSettings.AttachmentPresetAnimationEvent e)
    {
        var isBaseKeyframe = e.Type == HudSettings.AttachmentPresetAnimationEventType.Keyframe && e.Time == 0.0 && e.Order == 0;
        return (
            isBaseKeyframe ? 0 : 1,
            isBaseKeyframe ? 0.0 : e.Time,
            e.Type == HudSettings.AttachmentPresetAnimationEventType.Transition ? 0 : 1,
            e.Order
        );
    }
}
