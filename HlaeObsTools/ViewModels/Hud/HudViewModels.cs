using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Media;
using HlaeObsTools.ViewModels;
using System;

namespace HlaeObsTools.ViewModels.Hud;

public sealed class HudWeaponViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private string _iconPath = string.Empty;
    private bool _isActive;
    private bool _isPrimary;
    private bool _isSecondary;
    private bool _isGrenade;
    private bool _isBomb;
    private bool _isKnife;
    private bool _isTaser;
    private int _ammoClip;
    private int _ammoReserve;
    private IBrush _accentBrush = Brushes.White;

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string IconPath
    {
        get => _iconPath;
        private set => SetProperty(ref _iconPath, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(AmmoText));
            }
        }
    }

    public bool IsPrimary
    {
        get => _isPrimary;
        private set
        {
            if (SetProperty(ref _isPrimary, value))
            {
                OnPropertyChanged(nameof(ChipWidth));
                OnPropertyChanged(nameof(IconSize));
            }
        }
    }

    public bool IsSecondary
    {
        get => _isSecondary;
        private set => SetProperty(ref _isSecondary, value);
    }

    public bool IsGrenade
    {
        get => _isGrenade;
        private set => SetProperty(ref _isGrenade, value);
    }

    public bool IsBomb
    {
        get => _isBomb;
        private set => SetProperty(ref _isBomb, value);
    }

    public bool IsKnife
    {
        get => _isKnife;
        private set => SetProperty(ref _isKnife, value);
    }

    public bool IsTaser
    {
        get => _isTaser;
        private set => SetProperty(ref _isTaser, value);
    }

    public int AmmoClip
    {
        get => _ammoClip;
        private set
        {
            if (SetProperty(ref _ammoClip, value))
            {
                OnPropertyChanged(nameof(AmmoText));
            }
        }
    }

    public int AmmoReserve
    {
        get => _ammoReserve;
        private set
        {
            if (SetProperty(ref _ammoReserve, value))
            {
                OnPropertyChanged(nameof(AmmoText));
            }
        }
    }

    public IBrush AccentBrush
    {
        get => _accentBrush;
        private set => SetProperty(ref _accentBrush, value);
    }

    public double ChipWidth => IsPrimary ? 48 : 28;
    public double IconSize => IsPrimary ? 40 : 20;

    public string AmmoText => AmmoClip > 0 || AmmoReserve > 0
        ? $"{AmmoClip}/{AmmoReserve}"
        : string.Empty;

    public void Update(string name, string iconPath, bool isActive, bool isPrimary, bool isSecondary, bool isGrenade, bool isBomb, bool isKnife, bool isTaser, int ammoClip, int ammoReserve, IBrush accentBrush)
    {
        Name = name;
        IconPath = iconPath;
        IsActive = isActive;
        IsPrimary = isPrimary;
        IsSecondary = isSecondary;
        IsGrenade = isGrenade;
        IsBomb = isBomb;
        IsKnife = isKnife;
        IsTaser = isTaser;
        AmmoClip = ammoClip;
        AmmoReserve = ammoReserve;
        AccentBrush = accentBrush;
    }
}

public sealed class HudPlayerCardViewModel : ViewModelBase
{
    private string _steamId = string.Empty;
    private string _name = string.Empty;
    private string _team = string.Empty;
    private int _observerSlot;
    private int _health;
    private int _armor;
    private bool _hasHelmet;
    private bool _hasDefuseKit;
    private bool _hasBomb;
    private bool _isBombActive;
    private bool _isAlive;
    private HudWeaponViewModel? _primary;
    private HudWeaponViewModel? _secondary;
    private HudWeaponViewModel? _knife;
    private HudWeaponViewModel? _activeWeapon;
    private IBrush _accentBrush = Brushes.White;
    private IBrush _cardBackground = Brushes.Black;
    private bool _isFocused;
    private bool _isRadialMenuOpen;
    private bool _hasWeaponsAndGrenades;
    private HudPlayerActionOption? _hoveredRadialAction;
    private bool _isRadialCenterHighlighted;
    private IBrush _radialCenterBrush = new SolidColorBrush(Color.FromArgb(150, 25, 25, 30));
    private bool _isInAttachSubMenu;
    private bool _useAltBindings;
    private bool _isAttachTargetSelectionActive;
    private readonly ObservableCollection<HudPlayerActionOption> _attachSubMenuOptions = new();
    private readonly ReadOnlyObservableCollection<HudPlayerActionOption> _attachSubMenuOptionsReadonly;
    private HudPlayerActionOption? _hoveredAttachOption;
    private int _attachSubMenuPageIndex = -1;
    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };

    public HudPlayerCardViewModel(string steamId)
    {
        _steamId = steamId;
        _attachSubMenuOptionsReadonly = new ReadOnlyObservableCollection<HudPlayerActionOption>(_attachSubMenuOptions);
        AttachSubMenuOptions = _attachSubMenuOptionsReadonly;
        RadialActions.CollectionChanged += OnRadialActionsChanged;
        _attachSubMenuOptions.CollectionChanged += OnAttachSubMenuChanged;
        WeaponsAndGrenades.CollectionChanged += OnWeaponsAndGrenadesChanged;
        UpdateHasWeaponsAndGrenades();
    }

    public string SteamId
    {
        get => _steamId;
        private set => SetProperty(ref _steamId, value);
    }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string Team
    {
        get => _team;
        private set => SetProperty(ref _team, value);
    }

    public int ObserverSlot
    {
        get => _observerSlot;
        private set
        {
            if (SetProperty(ref _observerSlot, value))
            {
                OnPropertyChanged(nameof(DisplaySlot));
            }
        }
    }

    public int Health
    {
        get => _health;
        private set => SetProperty(ref _health, value);
    }

    public int Armor
    {
        get => _armor;
        private set => SetProperty(ref _armor, value);
    }

    public bool HasHelmet
    {
        get => _hasHelmet;
        private set
        {
            if (SetProperty(ref _hasHelmet, value))
            {
                OnPropertyChanged(nameof(ArmorIconPath));
            }
        }
    }

    public bool HasDefuseKit
    {
        get => _hasDefuseKit;
        private set => SetProperty(ref _hasDefuseKit, value);
    }

    public bool IsAlive
    {
        get => _isAlive;
        private set => SetProperty(ref _isAlive, value);
    }

    public HudWeaponViewModel? Primary
    {
        get => _primary;
        private set => SetProperty(ref _primary, value);
    }

    public HudWeaponViewModel? Secondary
    {
        get => _secondary;
        private set => SetProperty(ref _secondary, value);
    }

    public HudWeaponViewModel? Knife
    {
        get => _knife;
        private set => SetProperty(ref _knife, value);
    }

    public bool HasBomb
    {
        get => _hasBomb;
        private set => SetProperty(ref _hasBomb, value);
    }

    public bool IsBombActive
    {
        get => _isBombActive;
        private set => SetProperty(ref _isBombActive, value);
    }

    public ObservableCollection<HudWeaponViewModel> Grenades { get; } = new();

    public HudWeaponViewModel? ActiveWeapon
    {
        get => _activeWeapon;
        private set
        {
            if (SetProperty(ref _activeWeapon, value))
            {
                OnPropertyChanged(nameof(ActiveAmmoText));
            }
        }
    }

    public ObservableCollection<HudWeaponViewModel> WeaponsRow { get; } = new();
    public ObservableCollection<HudWeaponViewModel> WeaponsAndGrenades { get; } = new();
    public ObservableCollection<HudPlayerActionOption> RadialActions { get; } = new();
    public ReadOnlyObservableCollection<HudPlayerActionOption> AttachSubMenuOptions { get; }
    public IEnumerable<HudPlayerActionOption> CurrentRadialItems => IsInAttachSubMenu ? AttachSubMenuOptions : RadialActions;

    public bool HasWeaponsAndGrenades
    {
        get => _hasWeaponsAndGrenades;
        private set => SetProperty(ref _hasWeaponsAndGrenades, value);
    }

    public IBrush AccentBrush
    {
        get => _accentBrush;
        private set => SetProperty(ref _accentBrush, value);
    }

    public IBrush CardBackground
    {
        get => _cardBackground;
        private set => SetProperty(ref _cardBackground, value);
    }

    public bool IsFocused
    {
        get => _isFocused;
        private set
        {
            if (SetProperty(ref _isFocused, value))
            {
                OnPropertyChanged(nameof(DisplayBorderBrush));
                OnPropertyChanged(nameof(DisplayBorderThickness));
                OnPropertyChanged(nameof(DisplayMargin));
            }
        }
    }

    public bool IsRadialMenuOpen
    {
        get => _isRadialMenuOpen;
        private set => SetProperty(ref _isRadialMenuOpen, value);
    }

    public HudPlayerActionOption? HoveredRadialAction
    {
        get => _hoveredRadialAction;
        private set => SetProperty(ref _hoveredRadialAction, value);
    }

    public HudPlayerActionOption? HoveredAttachOption
    {
        get => _hoveredAttachOption;
        private set => SetProperty(ref _hoveredAttachOption, value);
    }

    public bool IsRadialCenterHighlighted
    {
        get => _isRadialCenterHighlighted;
        private set
        {
            if (SetProperty(ref _isRadialCenterHighlighted, value))
            {
                UpdateRadialCenterBrush();
            }
        }
    }

    public bool IsInAttachSubMenu
    {
        get => _isInAttachSubMenu;
        private set
        {
            if (SetProperty(ref _isInAttachSubMenu, value))
            {
                OnPropertyChanged(nameof(CurrentRadialItems));
                OnPropertyChanged(nameof(RadialCenterLabel));
            }
        }
    }

    public bool UseAltBindings
    {
        get => _useAltBindings;
        set
        {
            if (SetProperty(ref _useAltBindings, value))
            {
                OnPropertyChanged(nameof(DisplaySlot));
            }
        }
    }

    public IBrush RadialCenterBrush
    {
        get => _radialCenterBrush;
        private set => SetProperty(ref _radialCenterBrush, value);
    }

    public string RadialCenterLabel => "Cancel";

    public int AttachSubMenuPageIndex => _attachSubMenuPageIndex;

    public event EventHandler<HudPlayerActionRequestedEventArgs>? PlayerActionRequested;
    public event EventHandler? AttachTargetSelected;
    public event EventHandler? AnimateToPovRequested;

    public IBrush DisplayBorderBrush => IsFocused
        ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(51, 255, 255, 255));

    public Thickness DisplayBorderThickness => IsFocused
        ? new Thickness(3)
        : new Thickness(1);

    public Thickness DisplayMargin
    {
        get
        {
            const double baseMarginVertical = 6;
            const double baseBorderThickness = 1;
            double marginVertical = baseMarginVertical + baseBorderThickness - DisplayBorderThickness.Top;
            return new Thickness(0, marginVertical);
        }
    }

    public string DisplaySlot
    {
        get
        {
            if (ObserverSlot < 0 || ObserverSlot > 9)
            {
                return string.Empty;
            }

            if (UseAltBindings && ObserverSlot >= 5)
            {
                return AltBindLabels[ObserverSlot - 5];
            }

            return ((ObserverSlot + 1) % 10).ToString();
        }
    }

    public string ArmorIconPath => HasHelmet
        ? "avares://HlaeObsTools/Assets/hud/icons/armor-helmet.svg"
        : "avares://HlaeObsTools/Assets/hud/icons/armor.svg";

    public string HealthIconPath => "avares://HlaeObsTools/Assets/hud/icons/health.svg";

    public string DefuseKitIconPath => "avares://HlaeObsTools/Assets/hud/weapons/defuser.svg";

    public string BombIconPath => "avares://HlaeObsTools/Assets/hud/weapons/c4.svg";

    public string ActiveAmmoText => ActiveWeapon != null && (ActiveWeapon.AmmoClip > 0 || ActiveWeapon.AmmoReserve > 0)
        ? $"{ActiveWeapon.AmmoClip}/{ActiveWeapon.AmmoReserve}"
        : string.Empty;

    public bool HasArmor => Armor > 0;

    public void OpenRadialMenu()
    {
        IsRadialMenuOpen = true;
    }

    public void CloseRadialMenu()
    {
        IsRadialMenuOpen = false;
        HighlightRadialAction(null, false);
        CloseAttachSubMenu();
    }

    public void HighlightRadialAction(HudPlayerActionOption? action, bool highlightCenter)
    {
        if (IsInAttachSubMenu)
        {
            HoveredAttachOption = action;
            foreach (var option in _attachSubMenuOptions)
            {
                option.SetHighlighted(ReferenceEquals(option, action));
            }
            IsRadialCenterHighlighted = highlightCenter;
        }
        else
        {
            HoveredRadialAction = action;
            foreach (var option in RadialActions)
            {
                option.SetHighlighted(ReferenceEquals(option, action));
            }

            IsRadialCenterHighlighted = highlightCenter;
        }
    }

    public void RequestPlayerAction(HudPlayerActionOption? option)
    {
        PlayerActionRequested?.Invoke(this, new HudPlayerActionRequestedEventArgs(this, option));
    }

    public bool IsAttachTargetSelectionActive
    {
        get => _isAttachTargetSelectionActive;
        set => SetProperty(ref _isAttachTargetSelectionActive, value);
    }

    public void RequestAttachTargetSelection()
    {
        AttachTargetSelected?.Invoke(this, EventArgs.Empty);
    }

    public void RequestAnimateToPov()
    {
        AnimateToPovRequested?.Invoke(this, EventArgs.Empty);
    }

    public void OpenAttachSubMenu(int pageIndex, IEnumerable<HlaeObsTools.ViewModels.HudSettings.AttachmentPreset> presets)
    {
        _attachSubMenuPageIndex = pageIndex;
        var options = presets
            .Select((preset, i) =>
            {
                var title = string.IsNullOrWhiteSpace(preset.Name) ? $"Preset {i + 1}" : preset.Name;
                return new HudPlayerActionOption($"attach_preset_{i + 1}", title, i);
            })
            .ToList();
        SyncCollection(_attachSubMenuOptions, options);
        foreach (var option in _attachSubMenuOptions)
        {
            option.SetAccentBrush(AccentBrush);
        }
        IsInAttachSubMenu = true;
        OnPropertyChanged(nameof(CurrentRadialItems));
    }

    public void CloseAttachSubMenu()
    {
        _attachSubMenuPageIndex = -1;
        IsInAttachSubMenu = false;
        _attachSubMenuOptions.Clear();
        OnPropertyChanged(nameof(CurrentRadialItems));
    }

    public void SetRadialActions(IEnumerable<HudPlayerActionOption> actions)
    {
        var desired = actions.ToList();
        if (RadialActions.Count == desired.Count && RadialActions.SequenceEqual(desired))
        {
            return;
        }

        SyncCollection(RadialActions, desired);
        ApplyAccentToRadialActions(AccentBrush);
        OnPropertyChanged(nameof(CurrentRadialItems));
    }

    public void Update(
        string name,
        string team,
        int observerSlot,
        int health,
        int armor,
        bool hasHelmet,
        bool hasDefuseKit,
        bool hasBomb,
        bool isAlive,
        HudWeaponViewModel? primary,
        HudWeaponViewModel? secondary,
        HudWeaponViewModel? knife,
        IEnumerable<HudWeaponViewModel> grenades,
        HudWeaponViewModel? activeWeapon,
        IBrush accentBrush,
        IBrush cardBackground,
        bool isFocused = false)
    {
        Name = name;
        Team = team;
        ObserverSlot = observerSlot;
        Health = health;
        Armor = armor;
        HasHelmet = hasHelmet;
        HasDefuseKit = hasDefuseKit;
        HasBomb = hasBomb;
        IsBombActive = activeWeapon?.IsBomb == true;
        IsAlive = isAlive;
        Primary = primary;
        Secondary = secondary;
        Knife = knife;
        ActiveWeapon = activeWeapon;
        AccentBrush = accentBrush;
        CardBackground = cardBackground;
        IsFocused = isFocused;

        var row = BuildRow(primary, secondary, knife).ToList();
        var grenadesList = grenades.ToList();

        SyncCollection(WeaponsRow, row);
        SyncCollection(Grenades, grenadesList);
        SyncCollection(WeaponsAndGrenades, row.Concat(grenadesList));
        UpdateHasWeaponsAndGrenades();
    }

    private void ApplyAccentToRadialActions(IBrush accent)
    {
        foreach (var option in RadialActions)
        {
            option.SetAccentBrush(accent);
        }

        UpdateRadialCenterBrush();
    }

    private void UpdateRadialCenterBrush()
    {
        var accentColor = (AccentBrush as ISolidColorBrush)?.Color ?? Colors.White;
        var alpha = IsRadialCenterHighlighted ? (byte)200 : (byte)140;
        RadialCenterBrush = new SolidColorBrush(Color.FromArgb(alpha, accentColor.R, accentColor.G, accentColor.B));
    }

    private static IEnumerable<HudWeaponViewModel> BuildRow(params HudWeaponViewModel?[] items)
    {
        foreach (var item in items)
        {
            if (item != null) yield return item;
        }
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IEnumerable<T> desired)
    {
        var desiredList = desired.ToList();

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredList.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < desiredList.Count; i++)
        {
            var item = desiredList[i];
            if (i < target.Count)
            {
                if (!EqualityComparer<T>.Default.Equals(target[i], item))
                {
                    if (target.Contains(item))
                    {
                        target.Remove(item);
                    }
                    target.Insert(i, item);
                }
            }
            else
            {
                target.Add(item);
            }
        }
    }

    private void OnRadialActionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentRadialItems));
    }

    private void OnAttachSubMenuChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentRadialItems));
    }

    private void OnWeaponsAndGrenadesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateHasWeaponsAndGrenades();
    }

    private void UpdateHasWeaponsAndGrenades()
    {
        HasWeaponsAndGrenades = WeaponsAndGrenades.Count > 0 || HasBomb || HasDefuseKit;
    }
}

public sealed class HudTeamViewModel : ViewModelBase
{
    public HudTeamViewModel(string side)
    {
        Side = side;
    }

    private string _name = string.Empty;
    private int _score;
    private int _timeoutsRemaining;
    private HudPlayerCardViewModel? _slot1;
    private HudPlayerCardViewModel? _slot2;
    private HudPlayerCardViewModel? _slot3;
    private HudPlayerCardViewModel? _slot4;
    private HudPlayerCardViewModel? _slot5;

    public string Side { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public int Score
    {
        get => _score;
        set => SetProperty(ref _score, value);
    }

    public int TimeoutsRemaining
    {
        get => _timeoutsRemaining;
        set => SetProperty(ref _timeoutsRemaining, value);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Side : Name;

    public HudPlayerCardViewModel? Slot1
    {
        get => _slot1;
        private set => SetProperty(ref _slot1, value);
    }

    public HudPlayerCardViewModel? Slot2
    {
        get => _slot2;
        private set => SetProperty(ref _slot2, value);
    }

    public HudPlayerCardViewModel? Slot3
    {
        get => _slot3;
        private set => SetProperty(ref _slot3, value);
    }

    public HudPlayerCardViewModel? Slot4
    {
        get => _slot4;
        private set => SetProperty(ref _slot4, value);
    }

    public HudPlayerCardViewModel? Slot5
    {
        get => _slot5;
        private set => SetProperty(ref _slot5, value);
    }

    public bool HasPlayers =>
        Slot1 != null || Slot2 != null || Slot3 != null || Slot4 != null || Slot5 != null;

    public void SetPlayers(IEnumerable<HudPlayerCardViewModel> players)
    {
        var desired = players.ToList();
        var hadPlayers = HasPlayers;

        Slot1 = desired.Count > 0 ? desired[0] : null;
        Slot2 = desired.Count > 1 ? desired[1] : null;
        Slot3 = desired.Count > 2 ? desired[2] : null;
        Slot4 = desired.Count > 3 ? desired[3] : null;
        Slot5 = desired.Count > 4 ? desired[4] : null;

        if (hadPlayers != HasPlayers)
        {
            OnPropertyChanged(nameof(HasPlayers));
        }
    }

    public IEnumerable<HudPlayerCardViewModel> EnumerateSlots()
    {
        if (Slot1 != null) yield return Slot1;
        if (Slot2 != null) yield return Slot2;
        if (Slot3 != null) yield return Slot3;
        if (Slot4 != null) yield return Slot4;
        if (Slot5 != null) yield return Slot5;
    }
}

public sealed class HudKillfeedEntryViewModel : ViewModelBase
{
    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };
    private double _opacity = 1.0;
    private bool _useAltBindings;
    private bool _showAttackerSlot;

    public HudKillfeedEntryViewModel(
        int attackerObserverSlot,
        string attackerName,
        IBrush attackerBrush,
        string victimName,
        IBrush victimBrush,
        string assisterName,
        IBrush assisterBrush,
        string weaponIconPath,
        bool isBlind,
        bool isFlashAssist,
        bool isInAir,
        bool isNoScope,
        bool isThroughSmoke,
        bool isWallbang,
        bool isHeadshot,
        bool useAltBindings,
        bool showAttackerSlot)
    {
        AttackerObserverSlot = attackerObserverSlot;
        AttackerName = attackerName ?? string.Empty;
        AttackerBrush = attackerBrush;
        VictimName = victimName ?? string.Empty;
        VictimBrush = victimBrush;
        AssisterName = assisterName ?? string.Empty;
        AssisterBrush = assisterBrush;
        WeaponIconPath = weaponIconPath ?? string.Empty;
        IsBlind = isBlind;
        IsFlashAssist = isFlashAssist;
        IsInAir = isInAir;
        IsNoScope = isNoScope;
        IsThroughSmoke = isThroughSmoke;
        IsWallbang = isWallbang;
        IsHeadshot = isHeadshot;
        UseAltBindings = useAltBindings;
        _showAttackerSlot = showAttackerSlot;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset CreatedAt { get; }

    public int AttackerObserverSlot { get; }
    public string AttackerName { get; }
    public IBrush AttackerBrush { get; }
    public string VictimName { get; }
    public IBrush VictimBrush { get; }
    public string AssisterName { get; }
    public IBrush AssisterBrush { get; }
    public string WeaponIconPath { get; }
    public bool IsBlind { get; }
    public bool IsFlashAssist { get; }
    public bool IsInAir { get; }
    public bool IsNoScope { get; }
    public bool IsThroughSmoke { get; }
    public bool IsWallbang { get; }
    public bool IsHeadshot { get; }
    public bool UseAltBindings
    {
        get => _useAltBindings;
        set
        {
            if (SetProperty(ref _useAltBindings, value))
            {
                OnPropertyChanged(nameof(AttackerSlotLabel));
            }
        }
    }

    public bool ShowAttackerSlot
    {
        get => _showAttackerSlot;
        set
        {
            if (SetProperty(ref _showAttackerSlot, value))
            {
                OnPropertyChanged(nameof(HasAttackerSlot));
            }
        }
    }

    public bool HasAttacker => !string.IsNullOrWhiteSpace(AttackerName);
    public bool HasAttackerSlot => ShowAttackerSlot && HasAttacker && AttackerObserverSlot is >= 0 and <= 9;
    public bool HasAssister => !string.IsNullOrWhiteSpace(AssisterName);
    public bool HasWeapon => !string.IsNullOrWhiteSpace(WeaponIconPath);
    public bool ShowFlashAssist => IsFlashAssist && HasAssister;

    public string BlindIconPath => "avares://HlaeObsTools/Assets/hud/deathnotice/blind_kill.svg";
    public string FlashAssistIconPath => "avares://HlaeObsTools/Assets/hud/weapons/flashbang_assist.svg";
    public string InAirIconPath => "avares://HlaeObsTools/Assets/hud/deathnotice/inairkill.svg";
    public string NoScopeIconPath => "avares://HlaeObsTools/Assets/hud/deathnotice/noscope.svg";
    public string ThroughSmokeIconPath => "avares://HlaeObsTools/Assets/hud/deathnotice/smoke_kill.svg";
    public string WallbangIconPath => "avares://HlaeObsTools/Assets/hud/deathnotice/penetrate.svg";
    public string HeadshotIconPath => "avares://HlaeObsTools/Assets/hud/deathnotice/icon_headshot.svg";

    public string AttackerSlotLabel
    {
        get
        {
            if (AttackerObserverSlot < 0 || AttackerObserverSlot > 9)
            {
                return string.Empty;
            }

            var slotIndex = AttackerObserverSlot == 0 ? 9 : AttackerObserverSlot - 1;

            if (UseAltBindings && slotIndex >= 5)
            {
                return AltBindLabels[slotIndex - 5];
            }

            return ((slotIndex + 1) % 10).ToString();
        }
    }

    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }
}
