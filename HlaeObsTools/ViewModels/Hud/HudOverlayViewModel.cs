using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.ViewModels.Hud;

public sealed class HudOverlayViewModel : ViewModelBase, IDisposable
{
    private readonly GsiServer _gsiServer;
    private readonly HudSettings _hudSettings;
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly Dictionary<string, HudWeaponViewModel> _weaponCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HudPlayerCardViewModel> _hudPlayerCache = new(StringComparer.Ordinal);
    private readonly ObservableCollection<HudKillfeedEntryViewModel> _killfeedEntries = new();
    private readonly DispatcherTimer _killfeedTimer;
    private readonly CampathsDockViewModel? _campathsVm;
    private readonly HudTeamViewModel _teamCt = new("CT");
    private readonly HudTeamViewModel _teamT = new("T");
    private HudPlayerCardViewModel? _focusedHudPlayer;
    private string _roundTimerText = "--:--";
    private string _roundPhase = "LIVE";
    private int _roundNumber;
    private string _mapName = string.Empty;
    private bool _hasHudDataCached;
    private bool _isFreecamActive;
    private bool _isAwaitingAttachTarget;
    private bool _isCampathPlaying;
    private double _campathPlaybackProgress;
    private int _pendingAttachSourceObserverSlot;
    private int _pendingAttachPresetIndex = -1;
    private int _pendingAttachPageIndex = -1;
    private string _hudPromptText = string.Empty;
    private DateTime _lastUiUpdateUtc;

    private static readonly HashSet<string> PrimaryWeaponTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Machine Gun",
        "Rifle",
        "Shotgun",
        "SniperRifle",
        "Submachine Gun"
    };

    private static readonly Dictionary<string, int> GrenadeOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["molotov"] = 0,
        ["incgrenade"] = 1,
        ["decoy"] = 2,
        ["smokegrenade"] = 3,
        ["flashbang"] = 4,
        ["hegrenade"] = 5
    };

    private const double KillfeedLifetimeSeconds = 8.0;
    private const double KillfeedFadeSeconds = 1.0;

    private static readonly SolidColorBrush CtAccentBrush = new(Color.Parse("#6EB4FF"));
    private static readonly SolidColorBrush TAccentBrush = new(Color.Parse("#FF9B4A"));
    private static readonly SolidColorBrush CtCardBackgroundBrush = new(Color.Parse("#192434"));
    private static readonly SolidColorBrush TCardBackgroundBrush = new(Color.Parse("#2E1E15"));

    public HudOverlayViewModel(
        GsiServer gsiServer,
        HudSettings hudSettings,
        HlaeWebSocketClient webSocketClient,
        CampathsDockViewModel? campathsVm = null)
    {
        _gsiServer = gsiServer;
        _hudSettings = hudSettings;
        _webSocketClient = webSocketClient;
        _campathsVm = campathsVm;

        CancelHudPromptCommand = new Relay(_ => CancelPendingAttachTargetSelection());

        _killfeedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _killfeedTimer.Tick += OnKillfeedTimerTick;
        _killfeedTimer.Start();

        _gsiServer.GameStateUpdated += OnHudGameStateUpdated;
        _hudSettings.PropertyChanged += OnHudSettingsChanged;
        _webSocketClient.MessageReceived += OnWebSocketMessage;

        if (_campathsVm != null)
        {
            _campathsVm.PropertyChanged += OnCampathsPropertyChanged;
            UpdateCampathPlaybackState(_campathsVm);
        }
    }

    public bool IsHudEnabled => _hudSettings.IsHudEnabled;
    public bool ShowNativeHud => IsHudEnabled;
    public bool ShowKillfeed => _hudSettings.ShowKillfeed;

    public HudTeamViewModel TeamCt => _teamCt;
    public HudTeamViewModel TeamT => _teamT;
    public ObservableCollection<HudKillfeedEntryViewModel> KillfeedEntries => _killfeedEntries;

    public HudPlayerCardViewModel? FocusedHudPlayer
    {
        get => _focusedHudPlayer;
        private set
        {
            if (ReferenceEquals(_focusedHudPlayer, value))
                return;

            _focusedHudPlayer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFocusedHudPlayer));
        }
    }

    public bool HasFocusedHudPlayer => _focusedHudPlayer != null;
    public bool HasHudData => _teamCt.HasPlayers || _teamT.HasPlayers;

    public string RoundTimerText
    {
        get => _roundTimerText;
        private set
        {
            SetProperty(ref _roundTimerText, value);
        }
    }

    public string RoundPhase
    {
        get => _roundPhase;
        private set
        {
            SetProperty(ref _roundPhase, value);
        }
    }

    public int RoundNumber
    {
        get => _roundNumber;
        private set
        {
            SetProperty(ref _roundNumber, value);
        }
    }

    public string MapName
    {
        get => _mapName;
        private set
        {
            SetProperty(ref _mapName, value);
        }
    }

    public bool IsFreecamActive
    {
        get => _isFreecamActive;
        set => SetProperty(ref _isFreecamActive, value);
    }

    public ICommand CancelHudPromptCommand { get; }

    public bool IsCampathPlaying
    {
        get => _isCampathPlaying;
        private set => SetProperty(ref _isCampathPlaying, value);
    }

    public double CampathPlaybackProgress
    {
        get => _campathPlaybackProgress;
        private set => SetProperty(ref _campathPlaybackProgress, value);
    }

    public string HudPromptText
    {
        get => _hudPromptText;
        private set
        {
            if (_hudPromptText == value) return;
            _hudPromptText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHudPrompt));
        }
    }

    public bool HasHudPrompt => !string.IsNullOrWhiteSpace(HudPromptText);

    public void Dispose()
    {
        _gsiServer.GameStateUpdated -= OnHudGameStateUpdated;
        _hudSettings.PropertyChanged -= OnHudSettingsChanged;
        _webSocketClient.MessageReceived -= OnWebSocketMessage;
        _killfeedTimer.Tick -= OnKillfeedTimerTick;
        _killfeedTimer.Stop();

        if (_campathsVm != null)
        {
            _campathsVm.PropertyChanged -= OnCampathsPropertyChanged;
        }
    }

    private void OnHudSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudSettings.IsHudEnabled))
        {
            OnPropertyChanged(nameof(IsHudEnabled));
            OnPropertyChanged(nameof(ShowNativeHud));
        }
        else if (e.PropertyName == nameof(HudSettings.ShowKillfeed))
        {
            OnPropertyChanged(nameof(ShowKillfeed));
        }
        else if (e.PropertyName == nameof(HudSettings.UseAltPlayerBinds))
        {
            var useAlt = _hudSettings.UseAltPlayerBinds;
            foreach (var vm in _hudPlayerCache.Values)
            {
                vm.UseAltBindings = useAlt;
            }
            foreach (var entry in _killfeedEntries)
            {
                entry.UseAltBindings = useAlt;
            }
        }
        else if (e.PropertyName == nameof(HudSettings.ShowKillfeedAttackerSlot))
        {
            var show = _hudSettings.ShowKillfeedAttackerSlot;
            foreach (var entry in _killfeedEntries)
            {
                entry.ShowAttackerSlot = show;
            }
        }
        else if (e.PropertyName == nameof(HudSettings.AttachPresetPages))
        {
            foreach (var vm in _hudPlayerCache.Values)
            {
                ConfigurePlayerRadialActions(vm);
            }
        }
    }

    private void OnCampathsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_campathsVm == null)
            return;

        if (e.PropertyName == nameof(CampathsDockViewModel.IsCampathPlaying) ||
            e.PropertyName == nameof(CampathsDockViewModel.CampathPlaybackProgress))
        {
            UpdateCampathPlaybackState(_campathsVm);
        }
    }

    private void UpdateCampathPlaybackState(CampathsDockViewModel campathsVm)
    {
        IsCampathPlaying = campathsVm.IsCampathPlaying;
        CampathPlaybackProgress = campathsVm.CampathPlaybackProgress;
    }

    private void OnHudGameStateUpdated(object? sender, GsiGameState state)
    {
        Dispatcher.UIThread.Post(() => ApplyHudState(state));
    }

    private void ApplyHudState(GsiGameState state)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdateUtc).TotalMilliseconds < 33)
            return;
        _lastUiUpdateUtc = now;

        TeamCt.Name = state.TeamCt?.Name ?? "CT";
        TeamCt.Score = state.TeamCt?.Score ?? 0;
        TeamCt.TimeoutsRemaining = state.TeamCt?.TimeoutsRemaining ?? 0;

        TeamT.Name = state.TeamT?.Name ?? "T";
        TeamT.Score = state.TeamT?.Score ?? 0;
        TeamT.TimeoutsRemaining = state.TeamT?.TimeoutsRemaining ?? 0;

        RoundNumber = state.RoundNumber;
        RoundPhase = (state.RoundPhase ?? "LIVE").ToUpperInvariant();
        RoundTimerText = FormatPhaseTimer(state.PhaseEndsIn);
        MapName = state.MapName ?? string.Empty;

        var focusedSteamId = state.FocusedPlayerSteamId;
        TeamCt.SetPlayers(BuildTeamPlayers(state.Players, "CT", focusedSteamId));
        TeamT.SetPlayers(BuildTeamPlayers(state.Players, "T", focusedSteamId));

        FocusedHudPlayer = FindFocusedPlayer(focusedSteamId);

        var hasHudData = TeamCt.HasPlayers || TeamT.HasPlayers;
        if (hasHudData != _hasHudDataCached)
        {
            _hasHudDataCached = hasHudData;
            OnPropertyChanged(nameof(HasHudData));
        }
    }

    private IEnumerable<HudPlayerActionOption> CreateAttachPageActions()
    {
        return Enumerable.Range(0, HudSettings.AttachPresetPageCount)
            .Select(i => new HudPlayerActionOption($"attach_page_{i + 1}", _hudSettings.GetAttachPresetPageName(i), i, hasSubMenu: true));
    }

    private void ConfigurePlayerRadialActions(HudPlayerCardViewModel player)
    {
        player.SetRadialActions(CreateAttachPageActions());

        player.PlayerActionRequested -= OnPlayerActionRequested;
        player.PlayerActionRequested += OnPlayerActionRequested;

        player.AttachTargetSelected -= OnAttachTargetSelected;
        player.AttachTargetSelected += OnAttachTargetSelected;

        player.IsAttachTargetSelectionActive = _isAwaitingAttachTarget;
    }

    private IEnumerable<HudPlayerCardViewModel> BuildTeamPlayers(IEnumerable<GsiPlayer> players, string team, string? focusedSteamId)
    {
        var ordered = players
            .Where(p => string.Equals(p.Team, team, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Slot switch
            {
                < 0 => int.MaxValue,
                _ => p.Slot
            })
            .ToList();

        var result = new List<HudPlayerCardViewModel>();
        foreach (var player in ordered)
        {
            var isFocused = !string.IsNullOrWhiteSpace(focusedSteamId) &&
                           string.Equals(player.SteamId, focusedSteamId, StringComparison.Ordinal);
            result.Add(BuildHudPlayer(player, isFocused));
        }

        return result;
    }

    private HudPlayerCardViewModel BuildHudPlayer(GsiPlayer player, bool isFocused = false)
    {
        var accent = CreateAccent(player.Team);
        var background = CreateCardBackground(player.Team);

        var weaponVms = player.Weapons?
            .Select(w => BuildWeapon(player.SteamId, w, accent))
            .ToList() ?? new List<HudWeaponViewModel>();

        var primary = weaponVms.FirstOrDefault(w => w.IsPrimary);
        var secondary = weaponVms.FirstOrDefault(w => w.IsSecondary);
        var knife = weaponVms.FirstOrDefault(w => w.IsKnife);
        var bomb = weaponVms.FirstOrDefault(w => w.IsBomb);
        var active = weaponVms.FirstOrDefault(w => w.IsActive) ?? primary ?? secondary ?? knife ?? weaponVms.FirstOrDefault();
        var grenades = BuildGrenadeList(weaponVms.Where(w => w.IsGrenade));

        if (!_hudPlayerCache.TryGetValue(player.SteamId, out var vm))
        {
            vm = new HudPlayerCardViewModel(player.SteamId);
            _hudPlayerCache[player.SteamId] = vm;
        }

        vm.Update(
            player.Name,
            player.Team,
            player.Slot,
            player.Health,
            player.Armor,
            player.HasHelmet,
            player.HasDefuseKit,
            player.IsAlive,
            primary,
            secondary,
            knife,
            bomb,
            grenades,
            active,
            accent,
            background,
            isFocused);

        vm.UseAltBindings = _hudSettings.UseAltPlayerBinds;

        ConfigurePlayerRadialActions(vm);

        return vm;
    }

    private HudWeaponViewModel BuildWeapon(string steamId, GsiWeapon weapon, IBrush accent)
    {
        var normalizedName = NormalizeWeaponName(weapon.Name);
        var icon = GetWeaponIconPath(normalizedName);

        var isGrenade = string.Equals(weapon.Type, "Grenade", StringComparison.OrdinalIgnoreCase) ||
                        normalizedName.Contains("grenade", StringComparison.OrdinalIgnoreCase);

        var isBomb = normalizedName.Contains("c4", StringComparison.OrdinalIgnoreCase);
        var isKnife = string.Equals(weapon.Type, "Knife", StringComparison.OrdinalIgnoreCase) || normalizedName.Contains("knife", StringComparison.OrdinalIgnoreCase);
        var isTaser = normalizedName.Contains("taser", StringComparison.OrdinalIgnoreCase);
        var isPrimary = PrimaryWeaponTypes.Contains(weapon.Type);
        var isSecondary = string.Equals(weapon.Type, "Pistol", StringComparison.OrdinalIgnoreCase);
        var isActive = string.Equals(weapon.State, "active", StringComparison.OrdinalIgnoreCase);

        var cacheKey = $"{steamId}:{normalizedName}";
        if (!_weaponCache.TryGetValue(cacheKey, out var vm))
        {
            vm = new HudWeaponViewModel();
            _weaponCache[cacheKey] = vm;
        }

        vm.Update(
            weapon.Name,
            icon,
            isActive,
            isPrimary,
            isSecondary,
            isGrenade,
            isBomb,
            isKnife,
            isTaser,
            weapon.AmmoClip,
            weapon.AmmoReserve,
            accent);

        return vm;
    }

    private IReadOnlyList<HudWeaponViewModel> BuildGrenadeList(IEnumerable<HudWeaponViewModel> grenades)
    {
        var list = new List<HudWeaponViewModel>();
        foreach (var grenade in grenades)
        {
            var count = Math.Max(1, grenade.AmmoReserve > 0 ? grenade.AmmoReserve : 1);
            for (int i = 0; i < count; i++)
            {
                list.Add(grenade);
            }
        }

        return list
            .OrderBy(g => GrenadeOrder.TryGetValue(NormalizeWeaponName(g.Name), out var idx) ? idx : 99)
            .ThenBy(g => g.Name)
            .ToList();
    }

    private HudPlayerCardViewModel? FindFocusedPlayer(string? steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
            return null;

        return TeamCt.EnumerateSlots().FirstOrDefault(p => string.Equals(p.SteamId, steamId, StringComparison.Ordinal))
               ?? TeamT.EnumerateSlots().FirstOrDefault(p => string.Equals(p.SteamId, steamId, StringComparison.Ordinal));
    }

    private static string FormatPhaseTimer(double? seconds)
    {
        if (seconds == null || double.IsNaN(seconds.Value))
            return "--:--";

        var clamped = Math.Max(0, seconds.Value);
        var span = TimeSpan.FromSeconds(clamped);
        return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }

    private static string NormalizeWeaponName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "knife";

        var normalized = name.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            ? name.Substring("weapon_".Length)
            : name;

        return normalized.ToLowerInvariant();
    }

    private static string GetWeaponIconPath(string weaponName)
    {
        var sanitized = NormalizeWeaponName(weaponName);
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "knife";

        return $"avares://HlaeObsTools/Assets/hud/weapons/{sanitized}.svg";
    }

    private static SolidColorBrush CreateAccent(string team)
    {
        return string.Equals(team, "CT", StringComparison.OrdinalIgnoreCase)
            ? CtAccentBrush
            : TAccentBrush;
    }

    private static SolidColorBrush CreateTeamBrush(int teamId)
    {
        return teamId switch
        {
            3 => new SolidColorBrush(Color.Parse("#6EB4FF")),
            2 => new SolidColorBrush(Color.Parse("#FF9B4A")),
            _ => new SolidColorBrush(Color.Parse("#E6E6E6"))
        };
    }

    private static SolidColorBrush CreateCardBackground(string team)
    {
        return string.Equals(team, "CT", StringComparison.OrdinalIgnoreCase)
            ? CtCardBackgroundBrush
            : TCardBackgroundBrush;
    }

    private void OnPlayerActionRequested(object? sender, HudPlayerActionRequestedEventArgs e)
    {
        HandlePlayerActionRequest(e.Player, e.Option);
    }

    private void HandlePlayerActionRequest(HudPlayerCardViewModel player, HudPlayerActionOption? option)
    {
        if (option == null)
            return;

        if (_isAwaitingAttachTarget)
        {
            CancelPendingAttachTargetSelection();
        }

        if (!player.IsInAttachSubMenu)
        {
            player.OpenAttachSubMenu(option.Index, _hudSettings.GetAttachPresets(option.Index));
            return;
        }

        if (player.IsInAttachSubMenu)
        {
            var pageIndex = player.AttachSubMenuPageIndex;
            var presetIndex = option.Index;
            var preset = _hudSettings.GetAttachPresets(pageIndex).ElementAtOrDefault(presetIndex);
            if (preset == null) return;

            if (PresetRequiresTarget(preset))
            {
                BeginAwaitAttachTargetSelection(player.ObserverSlot, pageIndex, presetIndex);
                player.CloseAttachSubMenu();
                return;
            }

            _ = _webSocketClient.SendCommandAsync("attach_camera", BuildAttachCameraArgs(player.ObserverSlot, preset, targetObserverSlot: null));
            player.CloseAttachSubMenu();
            return;
        }
    }

    private void OnAttachTargetSelected(object? sender, EventArgs e)
    {
        if (!_isAwaitingAttachTarget) return;
        if (sender is not HudPlayerCardViewModel targetPlayer) return;

        var preset = _hudSettings.GetAttachPresets(_pendingAttachPageIndex).ElementAtOrDefault(_pendingAttachPresetIndex);
        if (preset == null)
        {
            CancelPendingAttachTargetSelection();
            return;
        }

        _ = _webSocketClient.SendCommandAsync(
            "attach_camera",
            BuildAttachCameraArgs(_pendingAttachSourceObserverSlot, preset, targetObserverSlot: targetPlayer.ObserverSlot));

        CancelPendingAttachTargetSelection();
    }

    private void BeginAwaitAttachTargetSelection(int sourceObserverSlot, int pageIndex, int presetIndex)
    {
        _pendingAttachSourceObserverSlot = sourceObserverSlot;
        _pendingAttachPageIndex = pageIndex;
        _pendingAttachPresetIndex = presetIndex;
        _isAwaitingAttachTarget = true;

        SetAttachTargetSelectionMode(true);
        HudPromptText = "Select target player for transition (click a player card)";
    }

    private void CancelPendingAttachTargetSelection()
    {
        _isAwaitingAttachTarget = false;
        _pendingAttachSourceObserverSlot = 0;
        _pendingAttachPageIndex = -1;
        _pendingAttachPresetIndex = -1;
        SetAttachTargetSelectionMode(false);
        HudPromptText = string.Empty;
    }

    private void SetAttachTargetSelectionMode(bool enabled)
    {
        foreach (var vm in _hudPlayerCache.Values)
        {
            vm.IsAttachTargetSelectionActive = enabled;
        }
    }

    private static bool PresetRequiresTarget(HudSettings.AttachmentPreset preset)
    {
        return preset.Animation.Enabled &&
               preset.Animation.Events.Any(e => e.Type == HudSettings.AttachmentPresetAnimationEventType.Transition);
    }

    private static object BuildAttachCameraArgs(int observerSlot, HudSettings.AttachmentPreset preset, int? targetObserverSlot)
    {
        object? animation = null;
        if (preset.Animation.Enabled)
        {
            var events = (preset.Animation.Events ?? new List<HudSettings.AttachmentPresetAnimationEvent>())
                .Select(ev =>
                {
                    if (ev.Type == HudSettings.AttachmentPresetAnimationEventType.Transition)
                    {
                        return (object)new
                        {
                            type = "transition",
                            time = ev.Time,
                            order = ev.Order,
                            duration = ev.TransitionDuration ?? 0.0,
                            easing = ToTransitionEasing(ev.TransitionEasing)
                        };
                    }

                    return (object)new
                    {
                        type = "keyframe",
                        time = ev.Time,
                        order = ev.Order,
                        delta_pos = new { x = ev.DeltaPosX, y = ev.DeltaPosY, z = ev.DeltaPosZ },
                        delta_angles = new { pitch = ev.DeltaPitch, yaw = ev.DeltaYaw, roll = ev.DeltaRoll },
                        fov = ev.Fov,
                        rotation_sampling = ToRotationSampling(ev.RotationSampling),
                        follow_attachment = new
                        {
                            pitch = ev.FollowAttachmentPitch,
                            yaw = ev.FollowAttachmentYaw,
                            roll = ev.FollowAttachmentRoll
                        },
                        easing_curve = ToKeyframeEasingCurve(ev.KeyframeEasingCurve),
                        easing_mode = ToKeyframeEasingMode(ev.KeyframeEasingMode)
                    };
                })
                .ToList();

            animation = new
            {
                enabled = preset.Animation.Enabled,
                events
            };
        }

        return new
        {
            observer_slot = observerSlot,
            target_observer_slot = targetObserverSlot,
            attachment = preset.AttachmentName,
            offset_pos = new { x = preset.OffsetPosX, y = preset.OffsetPosY, z = preset.OffsetPosZ },
            offset_angles = new { pitch = preset.OffsetPitch, yaw = preset.OffsetYaw, roll = preset.OffsetRoll },
            fov = preset.Fov,
            rotation_reference = ToRotationReference(preset.RotationReference),
            rotation_basis = new
            {
                pitch = ToRotationBasis(preset.RotationBasisPitch),
                yaw = ToRotationBasis(preset.RotationBasisYaw),
                roll = ToRotationBasis(preset.RotationBasisRoll)
            },
            rotation_axis_lock = new
            {
                pitch = preset.RotationLockPitch,
                yaw = preset.RotationLockYaw,
                roll = preset.RotationLockRoll
            },
            animation
        };
    }

    private static string ToTransitionEasing(HudSettings.AttachmentPresetAnimationTransitionEasing? easing)
    {
        return (easing ?? HudSettings.AttachmentPresetAnimationTransitionEasing.Smoothstep) switch
        {
            HudSettings.AttachmentPresetAnimationTransitionEasing.Linear => "linear",
            HudSettings.AttachmentPresetAnimationTransitionEasing.Smoothstep => "smoothstep",
            HudSettings.AttachmentPresetAnimationTransitionEasing.EaseInOutCubic => "easeinoutcubic",
            _ => "smoothstep"
        };
    }

    private static string ToKeyframeEasingCurve(HudSettings.AttachmentPresetAnimationKeyframeCurve? curve)
    {
        return (curve ?? HudSettings.AttachmentPresetAnimationKeyframeCurve.Linear) switch
        {
            HudSettings.AttachmentPresetAnimationKeyframeCurve.Linear => "linear",
            HudSettings.AttachmentPresetAnimationKeyframeCurve.Smoothstep => "smoothstep",
            HudSettings.AttachmentPresetAnimationKeyframeCurve.Cubic => "cubic",
            _ => "linear"
        };
    }

    private static string ToKeyframeEasingMode(HudSettings.AttachmentPresetAnimationKeyframeEase? mode)
    {
        return (mode ?? HudSettings.AttachmentPresetAnimationKeyframeEase.EaseInOut) switch
        {
            HudSettings.AttachmentPresetAnimationKeyframeEase.EaseIn => "easein",
            HudSettings.AttachmentPresetAnimationKeyframeEase.EaseOut => "easeout",
            HudSettings.AttachmentPresetAnimationKeyframeEase.EaseInOut => "easeinout",
            _ => "easeinout"
        };
    }

    private static string ToRotationSampling(HudSettings.AttachmentPresetAnimationRotationSampling sampling)
    {
        return sampling == HudSettings.AttachmentPresetAnimationRotationSampling.FreezeAtSegmentStart
            ? "freeze_at_segment_start"
            : "live";
    }

    private static string ToRotationReference(HudSettings.AttachmentPresetRotationReference reference)
    {
        return reference == HudSettings.AttachmentPresetRotationReference.OffsetLocal
            ? "offset_local"
            : "attachment";
    }

    private static string ToRotationBasis(HudSettings.AttachmentPresetRotationBasis basis)
    {
        return basis == HudSettings.AttachmentPresetRotationBasis.World
            ? "world"
            : "attachment";
    }

    private void OnWebSocketMessage(object? sender, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var messageType = typeProp.GetString();
            if (string.Equals(messageType, "killfeed_event", StringComparison.Ordinal) &&
                TryParseKillfeedPayload(root, out var payload))
            {
                Dispatcher.UIThread.Post(() => AddKillfeedEntry(payload), DispatcherPriority.Background);
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private readonly record struct KillfeedPayload(
        int AttackerObserverSlot,
        string AttackerName,
        int AttackerTeam,
        string VictimName,
        int VictimTeam,
        string AssisterName,
        int AssisterTeam,
        string Weapon,
        bool Blind,
        bool FlashAssist,
        bool InAir,
        bool NoScope,
        bool ThroughSmoke,
        bool Wallbang,
        bool Headshot,
        bool UseAltBindings,
        bool ShowAttackerSlot);

    private bool TryParseKillfeedPayload(JsonElement root, out KillfeedPayload payload)
    {
        payload = default;

        var attacker = ReadPlayer(root, "attacker");
        var victim = ReadPlayer(root, "victim");
        var assister = ReadPlayer(root, "assister");
        var weapon = ReadString(root, "weapon");
        var attackerObserverSlot = attacker.raw.ValueKind == JsonValueKind.Object
            ? ReadInt(attacker.raw, "observer_slot")
            : -1;

        payload = new KillfeedPayload(
            attackerObserverSlot,
            attacker.name,
            attacker.team,
            victim.name,
            victim.team,
            assister.name,
            assister.team,
            weapon,
            ReadBool(root, "blind"),
            ReadBool(root, "flash_assist"),
            ReadBool(root, "in_air"),
            ReadBool(root, "noscope"),
            ReadBool(root, "through_smoke"),
            ReadBool(root, "wallbang"),
            ReadBool(root, "headshot"),
            _hudSettings.UseAltPlayerBinds,
            _hudSettings.ShowKillfeedAttackerSlot);

        return true;
    }

    private static (string name, int team, JsonElement raw) ReadPlayer(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var playerProp) ||
            playerProp.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, 0, default);
        }

        var name = ReadString(playerProp, "name");
        var team = ReadInt(playerProp, "team");
        return (name, team, playerProp);
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return prop.GetString() ?? string.Empty;
    }

    private static int ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return 0;

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var value) => value,
            _ => 0
        };
    }

    private static bool ReadBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value != 0,
            JsonValueKind.String when bool.TryParse(prop.GetString(), out var value) => value,
            _ => false
        };
    }

    private void AddKillfeedEntry(KillfeedPayload payload)
    {
        var weaponIcon = string.IsNullOrWhiteSpace(payload.Weapon)
            ? string.Empty
            : GetWeaponIconPath(payload.Weapon);

        var entry = new HudKillfeedEntryViewModel(
            payload.AttackerObserverSlot,
            payload.AttackerName,
            CreateTeamBrush(payload.AttackerTeam),
            payload.VictimName,
            CreateTeamBrush(payload.VictimTeam),
            payload.AssisterName,
            CreateTeamBrush(payload.AssisterTeam),
            weaponIcon,
            payload.Blind,
            payload.FlashAssist,
            payload.InAir,
            payload.NoScope,
            payload.ThroughSmoke,
            payload.Wallbang,
            payload.Headshot,
            payload.UseAltBindings,
            payload.ShowAttackerSlot);

        _killfeedEntries.Add(entry);
    }

    private void OnKillfeedTimerTick(object? sender, EventArgs e)
    {
        if (_killfeedEntries.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var fadeStart = KillfeedLifetimeSeconds - KillfeedFadeSeconds;

        for (int i = _killfeedEntries.Count - 1; i >= 0; i--)
        {
            var entry = _killfeedEntries[i];
            var ageSeconds = (now - entry.CreatedAt).TotalSeconds;

            if (ageSeconds >= KillfeedLifetimeSeconds)
            {
                _killfeedEntries.RemoveAt(i);
                continue;
            }

            var opacity = 1.0;
            if (KillfeedFadeSeconds > 0 && ageSeconds > fadeStart)
            {
                opacity = Math.Clamp((KillfeedLifetimeSeconds - ageSeconds) / KillfeedFadeSeconds, 0.0, 1.0);
            }

            entry.Opacity = opacity;
        }
    }

    private sealed class Relay : ICommand
    {
        private readonly Action<object?> _action;

        public Relay(Action<object?> action)
        {
            _action = action;
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _action(parameter);

        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
