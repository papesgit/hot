using System;
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
    public const int AttachPresetPageCount = 5;
    public const int AttachPresetCountPerPage = 5;
    public const double MinHudSize = 0.25;
    public const double MaxHudSize = 3.0;

    public record AttachmentPreset
    {
        public string Name { get; init; } = string.Empty;
        public string AttachmentName { get; init; } = string.Empty;
        public string BoneName { get; init; } = string.Empty;
        public double OffsetPosX { get; init; }
        public double OffsetPosY { get; init; }
        public double OffsetPosZ { get; init; }
        public double OffsetPitch { get; init; }
        public double OffsetYaw { get; init; }
        public double OffsetRoll { get; init; }
        public double Fov { get; init; } = 90.0;
        public AttachmentPresetRotationReference RotationReference { get; init; }
            = AttachmentPresetRotationReference.Attachment;
        public AttachmentPresetRotationBasis RotationBasisPitch { get; init; }
            = AttachmentPresetRotationBasis.Attachment;
        public AttachmentPresetRotationBasis RotationBasisYaw { get; init; }
            = AttachmentPresetRotationBasis.Attachment;
        public AttachmentPresetRotationBasis RotationBasisRoll { get; init; }
            = AttachmentPresetRotationBasis.Attachment;
        public bool RotationLockPitch { get; init; }
        public bool RotationLockYaw { get; init; }
        public bool RotationLockRoll { get; init; }
        public AttachmentPresetAnimation Animation { get; init; } = new();
    }

    public record AttachmentPresetPage
    {
        public string Name { get; init; } = string.Empty;
        public List<AttachmentPreset> Presets { get; init; } = new();
    }

    public record AttachmentPresetAnimation
    {
        public bool Enabled { get; init; }
        public List<AttachmentPresetAnimationEvent> Events { get; init; } = new();
    }

    public enum AttachmentPresetAnimationEventType
    {
        Keyframe,
        Transition
    }

    public enum AttachmentPresetAnimationTransitionEasing
    {
        Linear,
        Smoothstep,
        EaseInOutCubic
    }

    public enum AttachmentPresetRotationReference
    {
        Attachment,
        OffsetLocal
    }

    public enum AttachmentPresetRotationBasis
    {
        Attachment,
        World
    }

    public enum AttachmentPresetAnimationKeyframeCurve
    {
        Linear,
        Smoothstep,
        Cubic
    }

    public enum AttachmentPresetAnimationKeyframeEase
    {
        EaseIn,
        EaseOut,
        EaseInOut
    }

    public enum AttachmentPresetAnimationRotationSampling
    {
        Live,
        FreezeAtSegmentStart
    }

    public record AttachmentPresetAnimationEvent
    {
        public AttachmentPresetAnimationEventType Type { get; init; } = AttachmentPresetAnimationEventType.Keyframe;
        public double Time { get; init; }
        public int Order { get; init; }

        public double? DeltaPosX { get; init; }
        public double? DeltaPosY { get; init; }
        public double? DeltaPosZ { get; init; }

        public double? DeltaPitch { get; init; }
        public double? DeltaYaw { get; init; }
        public double? DeltaRoll { get; init; }

        public double? Fov { get; init; }
        public AttachmentPresetAnimationRotationSampling RotationSampling { get; init; }
            = AttachmentPresetAnimationRotationSampling.Live;
        public bool FollowAttachmentPitch { get; init; } = true;
        public bool FollowAttachmentYaw { get; init; } = true;
        public bool FollowAttachmentRoll { get; init; } = true;

        public double? TransitionDuration { get; init; }
        public AttachmentPresetAnimationTransitionEasing? TransitionEasing { get; init; }
        public AttachmentPresetAnimationKeyframeCurve? KeyframeEasingCurve { get; init; }
        public AttachmentPresetAnimationKeyframeEase? KeyframeEasingMode { get; init; }
    }

    private bool _isHudEnabled = true;
    private bool _useAltPlayerBinds;
    private bool _showKillfeed = true;
    private bool _showKillfeedAttackerSlot = true;
    private double _hudSize = 1.0;

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
    /// Whether the killfeed overlay is shown.
    /// </summary>
    public bool ShowKillfeed
    {
        get => _showKillfeed;
        set => SetProperty(ref _showKillfeed, value);
    }

    /// <summary>
    /// Whether to show attacker bind labels in the killfeed.
    /// </summary>
    public bool ShowKillfeedAttackerSlot
    {
        get => _showKillfeedAttackerSlot;
        set => SetProperty(ref _showKillfeedAttackerSlot, value);
    }

    /// <summary>
    /// Relative HUD element size. 1.0 uses the default 1600x900 design density.
    /// </summary>
    public double HudSize
    {
        get => _hudSize;
        set => SetProperty(ref _hudSize, Math.Clamp(value, MinHudSize, MaxHudSize));
    }

    /// <summary>
    /// Attach action preset pages (5 pages x 5 slots).
    /// </summary>
    public List<AttachmentPresetPage> AttachPresetPages { get; } = Enumerable.Range(0, AttachPresetPageCount)
        .Select(_ => new AttachmentPresetPage
        {
            Name = string.Empty,
            Presets = Enumerable.Range(0, AttachPresetCountPerPage).Select(_ => new AttachmentPreset()).ToList()
        })
        .ToList();

    private int _activeAttachPresetPage;
    public int ActiveAttachPresetPage
    {
        get => _activeAttachPresetPage;
        set => SetProperty(ref _activeAttachPresetPage, Math.Clamp(value, 0, AttachPresetPageCount - 1));
    }

    public IReadOnlyList<AttachmentPreset> GetActiveAttachPresets()
    {
        var pageIndex = Math.Clamp(ActiveAttachPresetPage, 0, AttachPresetPages.Count - 1);
        var page = AttachPresetPages.ElementAtOrDefault(pageIndex);
        return page?.Presets ?? new List<AttachmentPreset>();
    }

    public IReadOnlyList<AttachmentPreset> GetAttachPresets(int pageIndex)
    {
        var clampedPageIndex = Math.Clamp(pageIndex, 0, AttachPresetPages.Count - 1);
        var page = AttachPresetPages.ElementAtOrDefault(clampedPageIndex);
        return page?.Presets ?? new List<AttachmentPreset>();
    }

    public string GetAttachPresetPageName(int pageIndex)
    {
        var clampedPageIndex = Math.Clamp(pageIndex, 0, AttachPresetPageCount - 1);
        var page = AttachPresetPages.ElementAtOrDefault(clampedPageIndex);
        if (page == null || string.IsNullOrWhiteSpace(page.Name))
        {
            return $"Page {clampedPageIndex + 1}";
        }

        return page.Name;
    }

    public void SetAttachPresetPageName(int pageIndex, string? name)
    {
        if (pageIndex < 0 || pageIndex >= AttachPresetPages.Count)
            return;

        var normalizedName = name?.Trim() ?? string.Empty;
        var page = AttachPresetPages[pageIndex];
        if (string.Equals(page.Name, normalizedName, System.StringComparison.Ordinal))
            return;

        AttachPresetPages[pageIndex] = page with { Name = normalizedName };
        NotifyAttachPresetPagesChanged();
    }

    public void NotifyAttachPresetPagesChanged()
    {
        OnPropertyChanged(nameof(AttachPresetPages));
    }

    public void ApplyAttachPresetPages(IEnumerable<AttachmentPresetPageData> pages, IEnumerable<AttachmentPresetData>? legacyPresets = null)
    {
        var pageItems = pages?.ToList() ?? new List<AttachmentPresetPageData>();
        if (pageItems.Count == 0 && legacyPresets != null)
        {
            pageItems.Add(new AttachmentPresetPageData { Presets = legacyPresets.ToList() });
        }

        AttachPresetPages.Clear();

        foreach (var page in pageItems)
        {
            var presets = page.Presets ?? new List<AttachmentPresetData>();
            var mapped = presets.Select(FromData).ToList();
            while (mapped.Count < AttachPresetCountPerPage)
            {
                mapped.Add(new AttachmentPreset());
            }
            AttachPresetPages.Add(new AttachmentPresetPage
            {
                Name = page.Name ?? string.Empty,
                Presets = mapped
            });
        }

        while (AttachPresetPages.Count < AttachPresetPageCount)
        {
            AttachPresetPages.Add(new AttachmentPresetPage
            {
                Name = string.Empty,
                Presets = Enumerable.Range(0, AttachPresetCountPerPage).Select(_ => new AttachmentPreset()).ToList()
            });
        }

        NotifyAttachPresetPagesChanged();
    }

    public IEnumerable<AttachmentPresetPageData> ToAttachPresetPageData()
    {
        return AttachPresetPages.Select(page => new AttachmentPresetPageData
        {
            Name = page.Name,
            Presets = (page.Presets ?? new List<AttachmentPreset>())
                .Select(ToData)
                .ToList()
        });
    }

    public IEnumerable<AttachmentPresetData> ToLegacyAttachPresetData()
    {
        return GetActiveAttachPresets().Select(ToData);
    }

    private static AttachmentPreset FromData(AttachmentPresetData preset)
    {
        return new AttachmentPreset
        {
            Name = preset.Name,
            AttachmentName = preset.AttachmentName,
            BoneName = preset.BoneName,
            OffsetPosX = preset.OffsetPosX,
            OffsetPosY = preset.OffsetPosY,
            OffsetPosZ = preset.OffsetPosZ,
            OffsetPitch = preset.OffsetPitch,
            OffsetYaw = preset.OffsetYaw,
            OffsetRoll = preset.OffsetRoll,
            Fov = preset.Fov,
            RotationReference = ParseRotationReference(preset.RotationReference),
            RotationBasisPitch = ParseRotationBasis(preset.RotationBasisPitch),
            RotationBasisYaw = ParseRotationBasis(preset.RotationBasisYaw),
            RotationBasisRoll = ParseRotationBasis(preset.RotationBasisRoll),
            RotationLockPitch = preset.RotationLockPitch,
            RotationLockYaw = preset.RotationLockYaw,
            RotationLockRoll = preset.RotationLockRoll,
            Animation = FromData(preset.Animation)
        };
    }

    private static AttachmentPresetData ToData(AttachmentPreset preset)
    {
        return new AttachmentPresetData
        {
            Name = preset.Name,
            AttachmentName = preset.AttachmentName,
            BoneName = preset.BoneName,
            OffsetPosX = preset.OffsetPosX,
            OffsetPosY = preset.OffsetPosY,
            OffsetPosZ = preset.OffsetPosZ,
            OffsetPitch = preset.OffsetPitch,
            OffsetYaw = preset.OffsetYaw,
            OffsetRoll = preset.OffsetRoll,
            Fov = preset.Fov,
            RotationReference = ToRotationReference(preset.RotationReference),
            RotationBasisPitch = ToRotationBasis(preset.RotationBasisPitch),
            RotationBasisYaw = ToRotationBasis(preset.RotationBasisYaw),
            RotationBasisRoll = ToRotationBasis(preset.RotationBasisRoll),
            RotationLockPitch = preset.RotationLockPitch,
            RotationLockYaw = preset.RotationLockYaw,
            RotationLockRoll = preset.RotationLockRoll,
            Animation = ToData(preset.Animation)
        };
    }

    private static AttachmentPresetAnimation FromData(AttachmentPresetAnimationData? data)
    {
        if (data == null)
        {
            return new AttachmentPresetAnimation();
        }

        var events = (data.Events ?? new List<AttachmentPresetAnimationEventData>())
            .Select(e => new AttachmentPresetAnimationEvent
            {
                Type = string.Equals(e.Type, "transition", System.StringComparison.OrdinalIgnoreCase)
                    ? AttachmentPresetAnimationEventType.Transition
                    : AttachmentPresetAnimationEventType.Keyframe,
                Time = e.Time,
                Order = e.Order,
                DeltaPosX = e.DeltaPosX,
                DeltaPosY = e.DeltaPosY,
                DeltaPosZ = e.DeltaPosZ,
                DeltaPitch = e.DeltaPitch,
                DeltaYaw = e.DeltaYaw,
                DeltaRoll = e.DeltaRoll,
                Fov = e.Fov,
                RotationSampling = ParseRotationSampling(e.RotationSampling),
                FollowAttachmentPitch = e.FollowAttachmentPitch ?? true,
                FollowAttachmentYaw = e.FollowAttachmentYaw ?? true,
                FollowAttachmentRoll = e.FollowAttachmentRoll ?? true,
                TransitionDuration = e.TransitionDuration,
                TransitionEasing = ParseTransitionEasing(e.TransitionEasing),
                KeyframeEasingCurve = ParseKeyframeEasingCurve(e.KeyframeEasingCurve),
                KeyframeEasingMode = ParseKeyframeEasingMode(e.KeyframeEasingMode)
            })
            .ToList();

        return new AttachmentPresetAnimation
        {
            Enabled = data.Enabled,
            Events = events
        };
    }

    private static AttachmentPresetAnimationData? ToData(AttachmentPresetAnimation animation)
    {
        if (!animation.Enabled && (animation.Events == null || animation.Events.Count == 0))
        {
            return null;
        }

        return new AttachmentPresetAnimationData
        {
            Enabled = animation.Enabled,
            Events = (animation.Events ?? new List<AttachmentPresetAnimationEvent>())
                .Select(e => new AttachmentPresetAnimationEventData
                {
                    Type = e.Type == AttachmentPresetAnimationEventType.Transition ? "transition" : "keyframe",
                    Time = e.Time,
                    Order = e.Order,
                    DeltaPosX = e.DeltaPosX,
                    DeltaPosY = e.DeltaPosY,
                    DeltaPosZ = e.DeltaPosZ,
                    DeltaPitch = e.DeltaPitch,
                    DeltaYaw = e.DeltaYaw,
                    DeltaRoll = e.DeltaRoll,
                    Fov = e.Fov,
                    RotationSampling = ToRotationSampling(e.RotationSampling),
                    FollowAttachmentPitch = e.FollowAttachmentPitch,
                    FollowAttachmentYaw = e.FollowAttachmentYaw,
                    FollowAttachmentRoll = e.FollowAttachmentRoll,
                    TransitionDuration = e.TransitionDuration,
                    TransitionEasing = ToTransitionEasing(e.TransitionEasing),
                    KeyframeEasingCurve = ToKeyframeEasingCurve(e.KeyframeEasingCurve),
                    KeyframeEasingMode = ToKeyframeEasingMode(e.KeyframeEasingMode)
                })
                .ToList()
        };
    }

    private static AttachmentPresetAnimationTransitionEasing? ParseTransitionEasing(string? easing)
    {
        if (string.IsNullOrWhiteSpace(easing)) return null;

        return easing.Trim().ToLowerInvariant() switch
        {
            "linear" => AttachmentPresetAnimationTransitionEasing.Linear,
            "smoothstep" => AttachmentPresetAnimationTransitionEasing.Smoothstep,
            "easeinoutcubic" => AttachmentPresetAnimationTransitionEasing.EaseInOutCubic,
            _ => AttachmentPresetAnimationTransitionEasing.Smoothstep
        };
    }

    private static string? ToTransitionEasing(AttachmentPresetAnimationTransitionEasing? easing)
    {
        if (easing == null) return null;

        return easing.Value switch
        {
            AttachmentPresetAnimationTransitionEasing.Linear => "linear",
            AttachmentPresetAnimationTransitionEasing.Smoothstep => "smoothstep",
            AttachmentPresetAnimationTransitionEasing.EaseInOutCubic => "easeinoutcubic",
            _ => "smoothstep"
        };
    }

    private static AttachmentPresetAnimationKeyframeCurve? ParseKeyframeEasingCurve(string? curve)
    {
        if (string.IsNullOrWhiteSpace(curve)) return null;

        return curve.Trim().ToLowerInvariant() switch
        {
            "linear" => AttachmentPresetAnimationKeyframeCurve.Linear,
            "smoothstep" => AttachmentPresetAnimationKeyframeCurve.Smoothstep,
            "cubic" => AttachmentPresetAnimationKeyframeCurve.Cubic,
            _ => AttachmentPresetAnimationKeyframeCurve.Linear
        };
    }

    private static string? ToKeyframeEasingCurve(AttachmentPresetAnimationKeyframeCurve? curve)
    {
        if (curve == null) return null;

        return curve.Value switch
        {
            AttachmentPresetAnimationKeyframeCurve.Linear => "linear",
            AttachmentPresetAnimationKeyframeCurve.Smoothstep => "smoothstep",
            AttachmentPresetAnimationKeyframeCurve.Cubic => "cubic",
            _ => "linear"
        };
    }

    private static AttachmentPresetAnimationKeyframeEase? ParseKeyframeEasingMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;

        return mode.Trim().ToLowerInvariant() switch
        {
            "ease_in" => AttachmentPresetAnimationKeyframeEase.EaseIn,
            "ease_out" => AttachmentPresetAnimationKeyframeEase.EaseOut,
            "ease_in_out" => AttachmentPresetAnimationKeyframeEase.EaseInOut,
            _ => AttachmentPresetAnimationKeyframeEase.EaseInOut
        };
    }

    private static string? ToKeyframeEasingMode(AttachmentPresetAnimationKeyframeEase? mode)
    {
        if (mode == null) return null;

        return mode.Value switch
        {
            AttachmentPresetAnimationKeyframeEase.EaseIn => "ease_in",
            AttachmentPresetAnimationKeyframeEase.EaseOut => "ease_out",
            AttachmentPresetAnimationKeyframeEase.EaseInOut => "ease_in_out",
            _ => "ease_in_out"
        };
    }

    private static AttachmentPresetAnimationRotationSampling ParseRotationSampling(string? sampling)
    {
        if (string.Equals(sampling, "freeze_at_segment_start", System.StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentPresetAnimationRotationSampling.FreezeAtSegmentStart;
        }

        return AttachmentPresetAnimationRotationSampling.Live;
    }

    private static string ToRotationSampling(AttachmentPresetAnimationRotationSampling sampling)
    {
        return sampling == AttachmentPresetAnimationRotationSampling.FreezeAtSegmentStart
            ? "freeze_at_segment_start"
            : "live";
    }

    private static AttachmentPresetRotationReference ParseRotationReference(string? reference)
    {
        if (string.Equals(reference, "offset_local", System.StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentPresetRotationReference.OffsetLocal;
        }

        return AttachmentPresetRotationReference.Attachment;
    }

    private static string ToRotationReference(AttachmentPresetRotationReference reference)
    {
        return reference == AttachmentPresetRotationReference.OffsetLocal
            ? "offset_local"
            : "attachment";
    }

    private static AttachmentPresetRotationBasis ParseRotationBasis(string? basis)
    {
        if (string.Equals(basis, "world", System.StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentPresetRotationBasis.World;
        }

        return AttachmentPresetRotationBasis.Attachment;
    }

    private static string ToRotationBasis(AttachmentPresetRotationBasis basis)
    {
        return basis == AttachmentPresetRotationBasis.World
            ? "world"
            : "attachment";
    }
}
