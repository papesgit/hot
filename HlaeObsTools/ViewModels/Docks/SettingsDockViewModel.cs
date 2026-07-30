using System;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Graphics;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.Input;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Settings;
using HlaeObsTools.Services.Campaths;
using System.Text.Json;
using HlaeObsTools.Services.Hotkeys;
using HlaeObsTools.Services.Vmix;
using HlaeObsTools.ViewModels.Hotkeys;


namespace HlaeObsTools.ViewModels.Docks
{
    public sealed class GsiRelayEndpointViewModel : ViewModelBase
    {
        private string _uri = string.Empty;
        private IBrush _healthBrush = Brushes.Gray;
        private string _healthTooltip = "No status available.";

        public string Uri
        {
            get => _uri;
            set => SetProperty(ref _uri, value);
        }

        public IBrush HealthBrush
        {
            get => _healthBrush;
            set => SetProperty(ref _healthBrush, value);
        }

        public string HealthTooltip
        {
            get => _healthTooltip;
            set => SetProperty(ref _healthTooltip, value);
        }
    }

    public sealed class AttachPresetPageOptionViewModel : ViewModelBase
    {
        private string _name = string.Empty;

        public int Index { get; init; }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value ?? string.Empty);
        }
    }

    /// <summary>
    /// Settings dock for configuring UI options like radar markers and camera paths.
    /// </summary>
    public class SettingsDockViewModel : Tool
    {
        private readonly RadarSettings _radarSettings;
        private readonly HudSettings _hudSettings;
        private readonly FreecamSettings _freecamSettings;
        private readonly Viewport3DSettings _viewport3DSettings;
        private readonly CampathEditorViewModel _campathEditor;
        private CampathSequenceViewModel? _campathSequence;
        private CampathEditorMode _defaultCampathInterp = CampathEditorMode.Curves;
        private bool _showHlaeCampathControls;
        private readonly SettingsStorage _settingsStorage;
        private readonly AppSettingsData _storedSettings;
        private readonly HlaeWebSocketClient? _ws;
        private readonly Func<NetworkSettingsData, Task>? _applyNetworkSettingsAsync;
        private readonly VmixSettings _vmixSettings;
        private readonly VmixReplaySettings _vmixReplaySettings;
        private readonly ReplayDirectorSettings _replayDirectorSettings;
        private readonly VmixApiClient _vmixApiClient;
        private readonly VmixShortcutCatalog _vmixShortcutCatalog;
        private readonly Dictionary<string, List<VmixFunctionDefinition>> _vmixFunctionsByCategory = new(StringComparer.Ordinal);
        private bool _isUpdatingVmixBindingUi;
        private readonly Action<bool>? _setFocusInputGateDisabled;
        private readonly HotkeyService _hotkeyService;
        private readonly CampathsDockViewModel? _campathsDockViewModel;
        private readonly GraphicsDockViewModel? _graphicsDockViewModel;
        private readonly GraphicsProducerClient? _graphicsProducerClient;
        private readonly GsiServer? _gsiServer;
        private readonly HlaeInputSender? _inputSender;
        private readonly VideoDisplayDockViewModel? _videoDisplayDockViewModel;
        private readonly DispatcherTimer _networkHealthTimer;
        private static readonly HashSet<string> ActiveDutyMapNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "de_anubis",
            "de_cache",
            "de_inferno",
            "de_mirage",
            "de_dust2",
            "de_nuke",
            "de_ancient"
        };
        private bool _suppressFreecamSave;
        private bool _suppressSettingsSave;
        private bool _isLoadingPresets;
        private bool _isLoadingHotkeys;
        private bool _suppressHotkeyModeUpdate;
        private readonly ICommand _applyNetworkSettingsCommand;
        private readonly ICommand _browseMapObjCommand;
        private readonly ICommand _clearMapObjCommand;
        private readonly ICommand _resetTargetOrbitCommand;
        private readonly ICommand _cycleForceDeathnoticesCommand;
        private readonly ICommand _toggleDemouiCommand;
        private readonly ICommand _toggleInterpModeCommand;
        private readonly ICommand _addPointCommand;
        private readonly ICommand _clearCampathCommand;
        private readonly ICommand _loadCampathCommand;
        private readonly ICommand _saveCampathCommand;
        private readonly ICommand _loadViewportCampathCommand;
        private readonly ICommand _saveViewportCampathCommand;
        private readonly ICommand _getCurrentTimeOffsetCommand;
        private readonly ICommand _resetFreecamSettingsCommand;
        private readonly ICommand _executeAttachPresetSlot1Command;
        private readonly ICommand _executeAttachPresetSlot2Command;
        private readonly ICommand _executeAttachPresetSlot3Command;
        private readonly ICommand _executeAttachPresetSlot4Command;
        private readonly ICommand _executeAttachPresetSlot5Command;
        private readonly ICommand _executeAttachPresetSlot6Command;
        private readonly ICommand _executeAttachPresetSlot7Command;
        private readonly ICommand _executeAttachPresetSlot8Command;
        private readonly ICommand _executeAttachPresetSlot9Command;
        private readonly ICommand _executeAttachPresetSlot0Command;
        private readonly ICommand _addGsiRelayEndpointCommand;
        private readonly ICommand _removeGsiRelayEndpointCommand;
        private readonly ICommand _refreshVmixStateCommand;
        private readonly ICommand _addVmixHotkeyCommand;
        private readonly ICommand _addCommandHotkeyCommand;

        public record NetworkSettingsData(string WebSocketHost, int WebSocketPort, int GraphicsProducerPort, int UdpPort, int RtpPort, int GsiPort, IReadOnlyList<string> GsiRelayUris);

        public SettingsDockViewModel(RadarSettings radarSettings, HudSettings hudSettings, FreecamSettings freecamSettings, Viewport3DSettings viewport3DSettings, SettingsStorage settingsStorage, HlaeWebSocketClient wsClient, HotkeyService hotkeyService, CampathsDockViewModel? campathsDockViewModel = null, GraphicsDockViewModel? graphicsDockViewModel = null, Func<NetworkSettingsData, Task>? applyNetworkSettingsAsync = null, AppSettingsData? storedSettings = null, VmixSettings? vmixSettings = null, VmixReplaySettings? vmixReplaySettings = null, ReplayDirectorSettings? replayDirectorSettings = null, VmixApiClient? vmixApiClient = null, Action<bool>? setFocusInputGateDisabled = null, CampathEditorViewModel? campathEditor = null, GsiServer? gsiServer = null, HlaeInputSender? inputSender = null, VideoDisplayDockViewModel? videoDisplayDockViewModel = null, GraphicsProducerClient? graphicsProducerClient = null)
        {
            _radarSettings = radarSettings;
            _hudSettings = hudSettings;
            _freecamSettings = freecamSettings;
            _viewport3DSettings = viewport3DSettings;
            _settingsStorage = settingsStorage;
            _ws = wsClient;
            _applyNetworkSettingsAsync = applyNetworkSettingsAsync;
            _vmixSettings = vmixSettings ?? new VmixSettings();
            _vmixReplaySettings = vmixReplaySettings ?? new VmixReplaySettings();
            _replayDirectorSettings = replayDirectorSettings ?? new ReplayDirectorSettings();
            _vmixApiClient = vmixApiClient ?? new VmixApiClient(_vmixSettings);
            _vmixShortcutCatalog = VmixShortcutCatalogLoader.LoadFromAssets();
            _setFocusInputGateDisabled = setFocusInputGateDisabled;
            _campathEditor = campathEditor ?? new CampathEditorViewModel();
            _hotkeyService = hotkeyService;
            _campathsDockViewModel = campathsDockViewModel;
            _graphicsDockViewModel = graphicsDockViewModel;
            _graphicsProducerClient = graphicsProducerClient;
            _gsiServer = gsiServer;
            _inputSender = inputSender;
            _videoDisplayDockViewModel = videoDisplayDockViewModel;
            _networkHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

            Title = "Settings";
            CanClose = true;
            CanFloat = true;
            CanPin = true;

            // Initialize network fields
            var settings = storedSettings ?? new AppSettingsData();
            _storedSettings = settings;
            _showHlaeCampathControls = settings.ShowHlaeCampathControls;
            if (Enum.TryParse<CampathEditorMode>(
                    settings.DefaultCampathInterp, ignoreCase: true, out var defaultCampathInterp))
                _defaultCampathInterp = defaultCampathInterp;
            _webSocketHost = settings.WebSocketHost;
            _webSocketPort = settings.WebSocketPort;
            _graphicsProducerPort = settings.GraphicsProducerPort;
            _udpPort = settings.UdpPort;
            _rtpPort = settings.RtpPort;
            _gsiPort = settings.GsiPort;
            _disableFocusInputGate = settings.DisableFocusInputGate;
            if (settings.GsiRelayUris != null)
            {
                foreach (var relayUri in settings.GsiRelayUris)
                {
                    GsiRelayEndpoints.Add(new GsiRelayEndpointViewModel { Uri = relayUri ?? string.Empty });
                }
            }

            _applyNetworkSettingsCommand = new AsyncRelay(ApplyNetworkSettingsInternalAsync);
            _addGsiRelayEndpointCommand = new Relay(AddGsiRelayEndpoint);
            _removeGsiRelayEndpointCommand = new RelayParam<GsiRelayEndpointViewModel>(RemoveGsiRelayEndpoint, endpoint => endpoint != null);
            _refreshVmixStateCommand = new AsyncRelay(RefreshVmixStateAsync);
            _addVmixHotkeyCommand = new Relay(AddVmixHotkey);
            _addCommandHotkeyCommand = new Relay(AddCommandHotkey);
            _browseMapObjCommand = new AsyncRelay(BrowseCs2GameFolderAsync);
            _clearMapObjCommand = new Relay(ClearCs2MapSelection);
            _resetTargetOrbitCommand = new Relay(() => _viewport3DSettings.TargetOrbitResetRequest++);
            _cycleForceDeathnoticesCommand = new Relay(CycleForceDeathnoticesMode);
            _toggleDemouiCommand = new AsyncRelay(() => _ws.SendExecCommandAsync("demoui"));
            _toggleInterpModeCommand = new Relay(() =>
            {
                _useCubic = !_useCubic;
                OnPropertyChanged(nameof(InterpLabel));

                var cmd = _useCubic
                    ? "mirv_campath edit interp position cubic; mirv_campath edit interp rotation cubic; mirv_campath edit interp fov cubic"
                    : "mirv_campath edit interp position linear; mirv_campath edit interp rotation sLinear; mirv_campath edit interp fov linear";
                SendExecCommand(cmd);
            });
            _addPointCommand = new AsyncRelay(() => _ws.SendExecCommandAsync("mirv_campath add"));
            _clearCampathCommand = new AsyncRelay(() => _ws.SendExecCommandAsync("mirv_campath clear"));
            _loadCampathCommand = new AsyncRelay(async () =>
            {
                var path = await PickCampathFileToLoadAsync();
                if (string.IsNullOrWhiteSpace(path))
                    return;

                var cmd = $"mirv_campath load \"{path}\"";
                await _ws.SendExecCommandAsync(cmd);
            });
            _saveCampathCommand = new AsyncRelay(async () =>
            {
                var path = await PickCampathFileToSaveAsync();
                if (string.IsNullOrWhiteSpace(path))
                    return;

                var cmd = $"mirv_campath save \"{path}\"";
                await _ws.SendExecCommandAsync(cmd);
            });
            _loadViewportCampathCommand = new AsyncRelay(async () =>
            {
                var path = await PickCampathFileToLoadAsync();
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (_campathSequence != null)
                {
                    var sequenceData = CampathFileIo.LoadSequence(path);
                    if (sequenceData != null)
                        _campathSequence.LoadFromData(sequenceData);
                    return;
                }

                var data = CampathFileIo.Load(path);
                if (data != null)
                    _campathEditor.LoadFromData(data);
            });
            _saveViewportCampathCommand = new AsyncRelay(async () =>
            {
                var path = await PickCampathFileToSaveAsync();
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (_campathSequence != null)
                    CampathFileIo.Save(path, _campathSequence);
                else
                    CampathFileIo.Save(path, _campathEditor);
            });
            _getCurrentTimeOffsetCommand = new AsyncRelay(GetCurrentTimeOffsetAsync);
            _resetFreecamSettingsCommand = new Relay(ResetFreecamSettings);
            _executeAttachPresetSlot1Command = CreateExecuteAttachPresetSlotCommand(0);
            _executeAttachPresetSlot2Command = CreateExecuteAttachPresetSlotCommand(1);
            _executeAttachPresetSlot3Command = CreateExecuteAttachPresetSlotCommand(2);
            _executeAttachPresetSlot4Command = CreateExecuteAttachPresetSlotCommand(3);
            _executeAttachPresetSlot5Command = CreateExecuteAttachPresetSlotCommand(4);
            _executeAttachPresetSlot6Command = CreateExecuteAttachPresetSlotCommand(5);
            _executeAttachPresetSlot7Command = CreateExecuteAttachPresetSlotCommand(6);
            _executeAttachPresetSlot8Command = CreateExecuteAttachPresetSlotCommand(7);
            _executeAttachPresetSlot9Command = CreateExecuteAttachPresetSlotCommand(8);
            _executeAttachPresetSlot0Command = CreateExecuteAttachPresetSlotCommand(9);

            foreach (var category in _vmixShortcutCatalog.Categories)
            {
                VmixFunctionCategories.Add(category);
                _vmixFunctionsByCategory[category] = _vmixShortcutCatalog.GetFunctionsByCategory(category).ToList();
            }

            _isLoadingHotkeys = true;
            if (settings.Hotkeys != null)
            {
                foreach (var binding in settings.Hotkeys)
                {
                    var vm = HotkeyBindingViewModel.FromData(binding);
                    HotkeyBindings.Add(vm);
                    AttachHotkeyBinding(vm);
                    AddToHotkeyLists(vm);
                }
            }
            _isLoadingHotkeys = false;
            RefreshCommandHotkeys();
            RefreshExecCommandHotkeys();
            RefreshVmixHotkeys();

            _hotkeyService.BindingCaptured += OnHotkeyBindingCaptured;
            _hotkeyService.BindingModeChanged += OnHotkeyBindingModeChanged;
            _hotkeyService.StatusChanged += OnHotkeyStatusChanged;
            SyncHotkeysToService();

            if (_campathsDockViewModel != null)
            {
                _campathsDockViewModel.PropertyChanged += OnCampathProfileChanged;
                _campathsDockViewModel.ProfileRemoved += OnCampathProfileRemoved;
                RefreshCampathHotkeys();
            }

            if (_graphicsDockViewModel != null)
            {
                _graphicsDockViewModel.PropertyChanged += OnGraphicsProfileChanged;
                _graphicsDockViewModel.ProfileRemoved += OnGraphicsProfileRemoved;
                RefreshGraphicsHotkeys();
            }

            if (_ws != null)
            {
                _ws.Connected += OnWebSocketConnected;
                _ws.MessageReceived += OnWebSocketMessage;
                _ws.Disconnected += OnWebSocketDisconnected;

                if (_ws.IsConnected)
                {
                    ApplyConnectedWebSocketState();
                }
            }

            AttachPresetAnimationEditor = new AttachPresetAnimationDockViewModel();

            OpenAttachPresetAnimationCommand = new RelayParam<AttachPresetViewModel>(
                preset =>
                {
                    if (preset == null) return;
                    AttachPresetAnimationEditor.OpenPreset(preset);
                    IsEditingAttachPresetAnimation = true;
                },
                preset => preset != null);

            CloseAttachPresetAnimationCommand = new Relay(() =>
            {
                IsEditingAttachPresetAnimation = false;
            });

            _activeAttachPresetPage = _hudSettings.ActiveAttachPresetPage;

            RefreshAttachPresetPageOptions();
            LoadAttachPresets();
            RefreshAttachHotkeys();
            _radarSettings.PropertyChanged += OnRadarSettingsChanged;
            _hudSettings.PropertyChanged += OnHudSettingsChanged;
            _viewport3DSettings.PropertyChanged += OnViewport3DSettingsChanged;
            _freecamSettings.PropertyChanged += OnFreecamSettingsChanged;
            _vmixSettings.PropertyChanged += OnVmixSettingsChanged;
            _vmixReplaySettings.PropertyChanged += OnVmixSettingsChanged;
            _replayDirectorSettings.PropertyChanged += OnVmixSettingsChanged;

            _networkHealthTimer.Tick += (_, _) => RefreshNetworkHealth();
            _networkHealthTimer.Start();
            _suppressSettingsSave = true;
            RefreshViewportMapOptions();
            _suppressSettingsSave = false;
            RefreshNetworkHealth();
        }

        public RadarSettings RadarSettings => _radarSettings;
        public HudSettings HudSettings => _hudSettings;
        public FreecamSettings FreecamSettings => _freecamSettings;
        public Viewport3DSettings Viewport3DSettings => _viewport3DSettings;
        public VmixSettings VmixSettings => _vmixSettings;
        public VmixReplaySettings VmixReplaySettings => _vmixReplaySettings;
        public ReplayDirectorSettings ReplayDirectorSettings => _replayDirectorSettings;
        public ObservableCollection<string> ReplayDirectorRoleOptions => HlaeObsTools.ViewModels.ReplayDirectorSettings.RoleOptions;
        public CampathEditorViewModel CampathEditor =>
            _campathSequence?.SelectedCamera?.Editor ?? _campathEditor;
        public IReadOnlyList<CampathEditorModeOption> DefaultCampathInterpOptions =>
            _campathEditor.EditorModeOptions;
        public CampathEditorMode DefaultCampathInterpMode => _defaultCampathInterp;
        public CampathEditorModeOption DefaultCampathInterp
        {
            get => DefaultCampathInterpOptions.First(option => option.Mode == _defaultCampathInterp);
            set
            {
                if (value == null || value.Mode == _defaultCampathInterp)
                    return;
                _defaultCampathInterp = value.Mode;
                if (_campathSequence != null)
                    _campathSequence.DefaultCameraMode = value.Mode;
                OnPropertyChanged();
                SaveSettings();
            }
        }
        public bool ShowHlaeCampathControls
        {
            get => _showHlaeCampathControls;
            set
            {
                if (!SetProperty(ref _showHlaeCampathControls, value))
                    return;
                SaveSettings();
            }
        }
        public void SetCampathSequence(CampathSequenceViewModel sequence)
        {
            if (_campathSequence == sequence)
                return;
            if (_campathSequence != null)
                _campathSequence.PropertyChanged -= OnCampathSequenceChanged;
            _campathSequence = sequence;
            _campathSequence.DefaultCameraMode = _defaultCampathInterp;
            _campathSequence.PropertyChanged += OnCampathSequenceChanged;
            OnPropertyChanged(nameof(CampathEditor));
        }

        private void OnCampathSequenceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CampathSequenceViewModel.SelectedCamera))
                OnPropertyChanged(nameof(CampathEditor));
        }
        public AttachPresetAnimationDockViewModel AttachPresetAnimationEditor { get; }
        public ObservableCollection<HotkeyBindingViewModel> HotkeyBindings { get; } = new();
        public ObservableCollection<HotkeyBindingViewModel> CommandHotkeyBindings { get; } = new();
        public ObservableCollection<HotkeyBindingViewModel> CampathHotkeyBindings { get; } = new();
        public ObservableCollection<HotkeyBindingViewModel> GraphicsHotkeyBindings { get; } = new();
        public ObservableCollection<HotkeyBindingViewModel> AttachHotkeyBindings { get; } = new();
        public ObservableCollection<HotkeyBindingViewModel> VmixHotkeyBindings { get; } = new();
        public ObservableCollection<HotkeyBindingViewModel> ExecCommandHotkeyBindings { get; } = new();
        public ObservableCollection<string> VmixFunctionCategories { get; } = new();
        public ObservableCollection<VmixInputInfo> VmixInputOptions { get; } = new();
        public ObservableCollection<GsiRelayEndpointViewModel> GsiRelayEndpoints { get; } = new();

        private static readonly IBrush HealthGreenBrush = new SolidColorBrush(Color.Parse("#43A047"));
        private static readonly IBrush HealthOrangeBrush = new SolidColorBrush(Color.Parse("#FB8C00"));
        private static readonly IBrush HealthRedBrush = new SolidColorBrush(Color.Parse("#E53935"));
        private static readonly IBrush HealthGrayBrush = new SolidColorBrush(Color.Parse("#757575"));

        private IBrush _webSocketHealthBrush = HealthGrayBrush;
        public IBrush WebSocketHealthBrush
        {
            get => _webSocketHealthBrush;
            private set => SetProperty(ref _webSocketHealthBrush, value);
        }

        private string _webSocketHealthTooltip = "WebSocket status unknown.";
        public string WebSocketHealthTooltip
        {
            get => _webSocketHealthTooltip;
            private set => SetProperty(ref _webSocketHealthTooltip, value);
        }

        private IBrush _udpHealthBrush = HealthGrayBrush;
        public IBrush UdpHealthBrush
        {
            get => _udpHealthBrush;
            private set => SetProperty(ref _udpHealthBrush, value);
        }

        private string _udpHealthTooltip = "UDP sender status unknown.";
        public string UdpHealthTooltip
        {
            get => _udpHealthTooltip;
            private set => SetProperty(ref _udpHealthTooltip, value);
        }

        private IBrush _rtpHealthBrush = HealthGrayBrush;
        public IBrush RtpHealthBrush
        {
            get => _rtpHealthBrush;
            private set => SetProperty(ref _rtpHealthBrush, value);
        }

        private string _rtpHealthTooltip = "RTP receiver status unknown.";
        public string RtpHealthTooltip
        {
            get => _rtpHealthTooltip;
            private set => SetProperty(ref _rtpHealthTooltip, value);
        }

        private IBrush _gsiHealthBrush = HealthGrayBrush;
        public IBrush GsiHealthBrush
        {
            get => _gsiHealthBrush;
            private set => SetProperty(ref _gsiHealthBrush, value);
        }

        private string _gsiHealthTooltip = "GSI listener status unknown.";
        public string GsiHealthTooltip
        {
            get => _gsiHealthTooltip;
            private set => SetProperty(ref _gsiHealthTooltip, value);
        }

        private IBrush _graphicsProducerHealthBrush = HealthGrayBrush;
        public IBrush GraphicsProducerHealthBrush
        {
            get => _graphicsProducerHealthBrush;
            private set => SetProperty(ref _graphicsProducerHealthBrush, value);
        }

        private string _graphicsProducerHealthTooltip = "Graphics producer status unknown.";
        public string GraphicsProducerHealthTooltip
        {
            get => _graphicsProducerHealthTooltip;
            private set => SetProperty(ref _graphicsProducerHealthTooltip, value);
        }

        private bool _isEditingAttachPresetAnimation;
        public bool IsEditingAttachPresetAnimation
        {
            get => _isEditingAttachPresetAnimation;
            private set => SetProperty(ref _isEditingAttachPresetAnimation, value);
        }

        #region === Hotkeys ===
        private bool _isHotkeyBindingMode;
        public bool IsHotkeyBindingMode
        {
            get => _isHotkeyBindingMode;
            set
            {
                if (!SetProperty(ref _isHotkeyBindingMode, value))
                    return;

                if (_suppressHotkeyModeUpdate)
                    return;

                if (value)
                    _hotkeyService.BeginCapture(_selectedHotkey?.Id);
                else
                    _hotkeyService.EndCapture();
            }
        }

        private HotkeyBindingViewModel? _selectedHotkey;
        public HotkeyBindingViewModel? SelectedHotkey
        {
            get => _selectedHotkey;
            set => SetProperty(ref _selectedHotkey, value);
        }

        private string _hotkeyStatusMessage = "Hotkey mode disabled.";
        public string HotkeyStatusMessage
        {
            get => _hotkeyStatusMessage;
            set => SetProperty(ref _hotkeyStatusMessage, value);
        }

        public ObservableCollection<string> HotkeyCategoryOptions { get; } = new()
        {
            "General",
            "Campath",
            "Graphics",
            "Attach",
            "Commands",
            "vMix"
        };

        private string _selectedHotkeyCategory = "General";
        public string SelectedHotkeyCategory
        {
            get => _selectedHotkeyCategory;
            set
            {
                if (!SetProperty(ref _selectedHotkeyCategory, value))
                    return;

                OnPropertyChanged(nameof(IsGeneralHotkeyCategorySelected));
                OnPropertyChanged(nameof(IsCampathHotkeyCategorySelected));
                OnPropertyChanged(nameof(IsGraphicsHotkeyCategorySelected));
                OnPropertyChanged(nameof(IsAttachHotkeyCategorySelected));
                OnPropertyChanged(nameof(IsCommandsHotkeyCategorySelected));
                OnPropertyChanged(nameof(IsVmixHotkeyCategorySelected));
            }
        }

        public bool IsGeneralHotkeyCategorySelected => string.Equals(SelectedHotkeyCategory, "General", StringComparison.Ordinal);
        public bool IsCampathHotkeyCategorySelected => string.Equals(SelectedHotkeyCategory, "Campath", StringComparison.Ordinal);
        public bool IsGraphicsHotkeyCategorySelected => string.Equals(SelectedHotkeyCategory, "Graphics", StringComparison.Ordinal);
        public bool IsAttachHotkeyCategorySelected => string.Equals(SelectedHotkeyCategory, "Attach", StringComparison.Ordinal);
        public bool IsCommandsHotkeyCategorySelected => string.Equals(SelectedHotkeyCategory, "Commands", StringComparison.Ordinal);
        public bool IsVmixHotkeyCategorySelected => string.Equals(SelectedHotkeyCategory, "vMix", StringComparison.Ordinal);

        private string _vmixStateStatusMessage = "vMix state not loaded.";
        public string VmixStateStatusMessage
        {
            get => _vmixStateStatusMessage;
            set => SetProperty(ref _vmixStateStatusMessage, value);
        }

        private ICommand? _rebindHotkeyCommand;
        public ICommand RebindHotkeyCommand => _rebindHotkeyCommand ??= new RelayParam<HotkeyBindingViewModel>(
            binding =>
            {
                if (binding == null) return;
                SelectedHotkey = binding;
                _hotkeyService.BeginRebind(binding.ToData());
            },
            binding => binding != null);


        private ICommand? _removeHotkeyCommand;
        public ICommand RemoveHotkeyCommand => _removeHotkeyCommand ??= new RelayParam<HotkeyBindingViewModel>(
            binding =>
            {
                if (binding == null) return;
                RemoveHotkey(binding);
            },
            binding => binding != null);

        private ICommand? _bindVmixHotkeyCommand;
        public ICommand BindVmixHotkeyCommand => _bindVmixHotkeyCommand ??= new RelayParam<HotkeyBindingViewModel>(
            binding =>
            {
                if (binding == null) return;
                SelectedHotkey = binding;
                _hotkeyService.BeginRebind(binding.ToData());
            },
            binding => binding != null);

        public ICommand AddVmixHotkeyCommand => _addVmixHotkeyCommand;
        public ICommand AddCommandHotkeyCommand => _addCommandHotkeyCommand;
        public ICommand RefreshVmixStateCommand => _refreshVmixStateCommand;

        private ICommand? _clearHotkeySelectionCommand;
        public ICommand ClearHotkeySelectionCommand => _clearHotkeySelectionCommand ??= new Relay(() =>
        {
            SelectedHotkey = null;
        });
        #endregion

        #region === Network Settings ===
        private string _webSocketHost = "127.0.0.1";
        public string WebSocketHost
        {
            get => _webSocketHost;
            set
            {
                if (_webSocketHost != value)
                {
                    _webSocketHost = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _webSocketPort = 31338;
        public int WebSocketPort
        {
            get => _webSocketPort;
            set
            {
                if (_webSocketPort != value)
                {
                    _webSocketPort = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _graphicsProducerPort = 31340;
        public int GraphicsProducerPort
        {
            get => _graphicsProducerPort;
            set
            {
                if (_graphicsProducerPort != value)
                {
                    _graphicsProducerPort = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _udpPort = 31339;
        public int UdpPort
        {
            get => _udpPort;
            set
            {
                if (_udpPort != value)
                {
                    _udpPort = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _rtpPort = 5000;
        public int RtpPort
        {
            get => _rtpPort;
            set
            {
                if (_rtpPort != value)
                {
                    _rtpPort = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _gsiPort = 31337;
        public int GsiPort
        {
            get => _gsiPort;
            set
            {
                if (_gsiPort != value)
                {
                    _gsiPort = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand ApplyNetworkSettingsCommand => _applyNetworkSettingsCommand;
        public ICommand AddGsiRelayEndpointCommand => _addGsiRelayEndpointCommand;
        public ICommand RemoveGsiRelayEndpointCommand => _removeGsiRelayEndpointCommand;

        private async Task ApplyNetworkSettingsInternalAsync()
        {
            SaveSettings();
            if (_applyNetworkSettingsAsync != null)
            {
                var payload = new NetworkSettingsData(WebSocketHost, WebSocketPort, GraphicsProducerPort, UdpPort, RtpPort, GsiPort, GetSanitizedGsiRelayUris());
                await _applyNetworkSettingsAsync(payload);
            }
        }

        private void AddGsiRelayEndpoint()
        {
            GsiRelayEndpoints.Add(new GsiRelayEndpointViewModel());
            RefreshRelayHealth();
        }

        private void RemoveGsiRelayEndpoint(GsiRelayEndpointViewModel? endpoint)
        {
            if (endpoint == null)
                return;

            GsiRelayEndpoints.Remove(endpoint);
            RefreshRelayHealth();
        }
        #endregion

        #region ==== 3D Viewport ====

        public ICommand BrowseMapObjCommand => _browseMapObjCommand;
        public ICommand ClearMapObjCommand => _clearMapObjCommand;
        public ICommand ResetTargetOrbitCommand => _resetTargetOrbitCommand;

        private async Task BrowseCs2GameFolderAsync()
        {
            var path = await PickCs2GameFolderAsync();
            if (string.IsNullOrWhiteSpace(path))
                return;

            _viewport3DSettings.Cs2GameFolder = path;
            RefreshViewportMapOptions();
            Console.WriteLine($"[Viewport3D] CS2 folder set: {path}");
        }

        private async Task<string?> PickCs2GameFolderAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                return null;

            var window = lifetime.MainWindow;
            if (window is null)
                return null;

            var result = await KeyboardInputGate.RunSuppressedAsync(() =>
                window.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions
                    {
                        Title = "Select Counter-Strike 2 Folder",
                        AllowMultiple = false
                    }));

            if (result is { Count: > 0 })
                return result[0].Path.LocalPath;

            return null;
        }

        private void ClearCs2MapSelection()
        {
            _viewport3DSettings.Cs2GameFolder = string.Empty;
            _viewport3DSettings.SelectedMapName = string.Empty;
            _viewport3DSettings.MapObjPath = string.Empty;
            RefreshViewportMapOptions();
        }

        private void RefreshViewportMapOptions()
        {
            var selectedName = !string.IsNullOrWhiteSpace(_viewport3DSettings.SelectedMapName)
                ? _viewport3DSettings.SelectedMapName
                : Path.GetFileNameWithoutExtension(_viewport3DSettings.MapObjPath);

            _viewport3DSettings.AvailableMaps.Clear();
            var noMap = new ViewportMapOption
            {
                Name = string.Empty,
                Path = string.Empty
            };
            _viewport3DSettings.AvailableMaps.Add(noMap);
            foreach (var option in DiscoverViewportMaps())
            {
                _viewport3DSettings.AvailableMaps.Add(option);
            }

            var selected = _viewport3DSettings.AvailableMaps
                .FirstOrDefault(map => string.Equals(map.Name, selectedName, StringComparison.OrdinalIgnoreCase))
                ?? noMap;
            _viewport3DSettings.SelectedMap = selected;
            _viewport3DSettings.SelectedMapName = selected.Name;
            _viewport3DSettings.MapObjPath = selected.Path;
        }

        private IEnumerable<ViewportMapOption> DiscoverViewportMaps()
        {
            var mapsDirectory = GetCs2MapsDirectory(_viewport3DSettings.Cs2GameFolder);
            if (string.IsNullOrWhiteSpace(mapsDirectory) || !Directory.Exists(mapsDirectory))
                return Enumerable.Empty<ViewportMapOption>();

            return Directory.EnumerateFiles(mapsDirectory, "*.vpk", SearchOption.TopDirectoryOnly)
                .Select(path => new ViewportMapOption
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Path = path
                })
                .Where(option => IsUsefulViewportMap(option.Name))
                .Where(option => !_viewport3DSettings.ActiveDutyMapsOnly || ActiveDutyMapNames.Contains(option.Name))
                .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string GetCs2MapsDirectory(string cs2GameFolder)
        {
            return string.IsNullOrWhiteSpace(cs2GameFolder)
                ? string.Empty
                : Path.Combine(cs2GameFolder, "game", "csgo", "maps");
        }

        private static bool IsUsefulViewportMap(string mapName)
        {
            return !string.IsNullOrWhiteSpace(mapName)
                && !mapName.StartsWith("workshop_preview", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mapName, "graphics_settings", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mapName, "lobby_mapveto", StringComparison.OrdinalIgnoreCase)
                && !mapName.EndsWith("_vanity", StringComparison.OrdinalIgnoreCase)
                && !mapName.EndsWith("_new_sky", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region === General Settings ===
        public bool UseAltPlayerBinds
        {
            get => _radarSettings.UseAltPlayerBinds;
            set
            {
                if (_radarSettings.UseAltPlayerBinds != value)
                {
                    _suppressSettingsSave = true;
                    _radarSettings.UseAltPlayerBinds = value;
                    _hudSettings.UseAltPlayerBinds = value;
                    _viewport3DSettings.UseAltPlayerBinds = value;
                    _suppressSettingsSave = false;
                    OnPropertyChanged();
                    SaveSettings();
                    SendAltPlayerBindsMode();
                }
            }
        }

        private bool _disableFocusInputGate;
        public bool DisableFocusInputGate
        {
            get => _disableFocusInputGate;
            set
            {
                if (_disableFocusInputGate != value)
                {
                    _disableFocusInputGate = value;
                    OnPropertyChanged();
                    _setFocusInputGateDisabled?.Invoke(value);
                    SaveSettings();
                }
            }
        }

        private bool _IsDrawHudEnabled;
        public bool IsDrawHudEnabled
        {
            get => _IsDrawHudEnabled;
            set
            {
                if (_IsDrawHudEnabled != value)
                {
                    _IsDrawHudEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "cl_drawhud 0"
                        : "cl_drawhud 1";
                    SendExecCommand(cmd);
                }
            }
        }

        private bool _IsOnlyDeathnotesEnabled;
        public bool IsOnlyDeathnotesEnabled
        {
            get => _IsOnlyDeathnotesEnabled;
            set
            {
                if (_IsOnlyDeathnotesEnabled != value)
                {
                    _IsOnlyDeathnotesEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "cl_draw_only_deathnotices 1"
                        : "cl_draw_only_deathnotices 0";
                    SendExecCommand(cmd);
                }
            }
        }

        private bool _IsXrayEnabled = true;
        public bool IsXrayEnabled
        {
            get => _IsXrayEnabled;
            set
            {
                if (_IsXrayEnabled != value)
                {
                    _IsXrayEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "spec_show_xray 1"
                        : "spec_show_xray 0";
                    SendExecCommand(cmd);
                }
            }
        }

        private int _forceDeathnoticesMode;
        public string ForceDeathnoticesLabel => _forceDeathnoticesMode.ToString();

        public ICommand CycleForceDeathnoticesCommand => _cycleForceDeathnoticesCommand;

        private void CycleForceDeathnoticesMode()
        {
            var nextMode = _forceDeathnoticesMode switch
            {
                0 => 1,
                1 => -1,
                _ => 0
            };

            if (_forceDeathnoticesMode == nextMode)
                return;

            _forceDeathnoticesMode = nextMode;
            OnPropertyChanged(nameof(ForceDeathnoticesLabel));

            var cmd = $"cl_drawhud_force_deathnotices {_forceDeathnoticesMode}";
            SendExecCommand(cmd);
        }
        public ICommand ToggleDemouiCommand => _toggleDemouiCommand;

        private void OnWebSocketConnected(object? sender, EventArgs e)
        {
            ApplyConnectedWebSocketState();
        }

        private void OnWebSocketDisconnected(object? sender, EventArgs e)
        {
            RefreshNetworkHealth();
        }

        private void OnWebSocketMessage(object? sender, string message)
        {
            if (!_awaitingCurtime)
                return;

            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp))
                    return;

                if (!string.Equals(typeProp.GetString(), "curtime", StringComparison.Ordinal))
                    return;

                _awaitingCurtime = false;

                if (root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
                    return;

                if (!root.TryGetProperty("value", out var valueProp) || valueProp.ValueKind != JsonValueKind.Number)
                    return;

                var curtime = valueProp.GetDouble();
                Dispatcher.UIThread.Post(() => _campathEditor.TimeOffset = curtime);
            }
            catch
            {
                _awaitingCurtime = false;
            }
        }

        private void SendAltPlayerBindsMode()
        {
            if (_ws == null) return;
            _ = _ws.SendCommandAsync("spectator_bindings_mode", new { useAlt = _radarSettings.UseAltPlayerBinds });
        }

        private void ApplyConnectedWebSocketState()
        {
            SendAltPlayerBindsMode();
            _ = SendAllFreecamConfigAsync();
            RefreshNetworkHealth();
        }
        #endregion

        #region ==== Actions / Attach Presets ====

        public ObservableCollection<AttachPresetPageOptionViewModel> AttachPresetPageOptions { get; } = new();

        private int _activeAttachPresetPage;
        public int ActiveAttachPresetPage
        {
            get => _activeAttachPresetPage;
            set
            {
                if (_activeAttachPresetPage == value) return;
                _activeAttachPresetPage = Math.Clamp(value, 0, HudSettings.AttachPresetPageCount - 1);
                _hudSettings.ActiveAttachPresetPage = _activeAttachPresetPage;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentAttachPresetPageName));
                LoadAttachPresets();
                RefreshAttachHotkeys();
                SaveSettings();
            }
        }

        public string CurrentAttachPresetPageName
        {
            get => _hudSettings.AttachPresetPages.ElementAtOrDefault(_activeAttachPresetPage)?.Name ?? string.Empty;
            set
            {
                var normalized = value?.Trim() ?? string.Empty;
                var current = _hudSettings.AttachPresetPages.ElementAtOrDefault(_activeAttachPresetPage)?.Name ?? string.Empty;
                if (string.Equals(current, normalized, StringComparison.Ordinal))
                    return;

                _hudSettings.SetAttachPresetPageName(_activeAttachPresetPage, normalized);
                RefreshAttachPresetPageOptions();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveAttachPresetPageName));
                SaveSettings();
            }
        }

        public ObservableCollection<AttachPresetViewModel> AttachPresets { get; }
            = new ObservableCollection<AttachPresetViewModel>(
                Enumerable.Range(0, 5).Select(i => new AttachPresetViewModel($"Preset {i + 1}") { PresetIndex = i }));

        public ICommand OpenAttachPresetAnimationCommand { get; }
        public ICommand CloseAttachPresetAnimationCommand { get; }
        public ICommand ExecuteAttachPresetSlot1Command => _executeAttachPresetSlot1Command;
        public ICommand ExecuteAttachPresetSlot2Command => _executeAttachPresetSlot2Command;
        public ICommand ExecuteAttachPresetSlot3Command => _executeAttachPresetSlot3Command;
        public ICommand ExecuteAttachPresetSlot4Command => _executeAttachPresetSlot4Command;
        public ICommand ExecuteAttachPresetSlot5Command => _executeAttachPresetSlot5Command;
        public ICommand ExecuteAttachPresetSlot6Command => _executeAttachPresetSlot6Command;
        public ICommand ExecuteAttachPresetSlot7Command => _executeAttachPresetSlot7Command;
        public ICommand ExecuteAttachPresetSlot8Command => _executeAttachPresetSlot8Command;
        public ICommand ExecuteAttachPresetSlot9Command => _executeAttachPresetSlot9Command;
        public ICommand ExecuteAttachPresetSlot0Command => _executeAttachPresetSlot0Command;

        private void LoadAttachPresets()
        {
            _isLoadingPresets = true;
            try
            {
                foreach (var preset in AttachPresets)
                {
                    preset.PropertyChanged -= OnPresetChanged;
                }

                var presets = _hudSettings.GetActiveAttachPresets();
                for (int i = 0; i < AttachPresets.Count && i < presets.Count; i++)
                {
                    AttachPresets[i].LoadFrom(presets[i]);
                }

                foreach (var preset in AttachPresets)
                {
                    preset.PropertyChanged += OnPresetChanged;
                }
            }
            finally
            {
                _isLoadingPresets = false;
            }
        }

        private void OnPresetChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isLoadingPresets) return;
            var vm = sender as AttachPresetViewModel;
            if (vm == null) return;
            var index = AttachPresets.IndexOf(vm);
            if (index < 0) return;
            var pageIndex = _hudSettings.ActiveAttachPresetPage;
            if (pageIndex < 0 || pageIndex >= _hudSettings.AttachPresetPages.Count) return;
            var page = _hudSettings.AttachPresetPages[pageIndex];
            if (index >= page.Presets.Count) return;
            page.Presets[index] = vm.ToModel();
            _hudSettings.NotifyAttachPresetPagesChanged();
            SaveSettings();
        }

        private void RefreshAttachPresetPageOptions()
        {
            for (var index = 0; index < HudSettings.AttachPresetPageCount; index++)
            {
                var pageName = _hudSettings.GetAttachPresetPageName(index);
                if (index < AttachPresetPageOptions.Count)
                {
                    if (!string.Equals(AttachPresetPageOptions[index].Name, pageName, StringComparison.Ordinal))
                    {
                        AttachPresetPageOptions[index].Name = pageName;
                    }
                }
                else
                {
                    AttachPresetPageOptions.Add(new AttachPresetPageOptionViewModel
                    {
                        Index = index,
                        Name = pageName
                    });
                }
            }

            while (AttachPresetPageOptions.Count > HudSettings.AttachPresetPageCount)
            {
                AttachPresetPageOptions.RemoveAt(AttachPresetPageOptions.Count - 1);
            }

            OnPropertyChanged(nameof(CurrentAttachPresetPageName));
            OnPropertyChanged(nameof(ActiveAttachPresetPageName));
        }

        public async Task ExecuteAttachPresetHotkeyActionAsync(int pageIndex, int presetIndex, int observerSlot)
        {
            if (_ws == null)
                return;

            if (pageIndex != _hudSettings.ActiveAttachPresetPage)
                return;

            if (presetIndex < 0 || presetIndex >= AttachPresets.Count)
                return;

            var preset = AttachPresets[presetIndex].ToModel();
            await _ws.SendCommandAsync("attach_camera", BuildAttachCameraArgs(observerSlot, preset, targetObserverSlot: null));
        }

        private ICommand CreateExecuteAttachPresetSlotCommand(int observerSlot)
        {
            return new RelayParam<AttachPresetViewModel>(
                preset => _ = ExecuteAttachPresetSlotCommandAsync(preset, observerSlot),
                preset => preset != null);
        }

        private async Task ExecuteAttachPresetSlotCommandAsync(AttachPresetViewModel? preset, int observerSlot)
        {
            if (_ws == null || preset == null)
                return;

            await _ws.SendCommandAsync("attach_camera", BuildAttachCameraArgs(observerSlot, preset.ToModel(), targetObserverSlot: null));
        }

        private void AttachHotkeyBinding(HotkeyBindingViewModel binding)
        {
            binding.PropertyChanged += OnHotkeyBindingChanged;
        }

        private void DetachHotkeyBinding(HotkeyBindingViewModel binding)
        {
            binding.PropertyChanged -= OnHotkeyBindingChanged;
        }

        private void OnHotkeyBindingChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is HotkeyBindingViewModel binding && binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.VmixFunction)
            {
                if (e.PropertyName == nameof(HotkeyBindingViewModel.TargetVmixFunctionCategory))
                {
                    ConfigureVmixBinding(binding, keepFunctionIfValid: false);
                }
                else if (e.PropertyName == nameof(HotkeyBindingViewModel.TargetVmixFunctionName))
                {
                    UpdateVmixBindingParameterState(binding, binding.TargetVmixFunctionCategory);
                    binding.DisplayName = BuildVmixDisplayName(binding.TargetVmixFunctionName, binding.TargetVmixValue);
                }
                else if (e.PropertyName == nameof(HotkeyBindingViewModel.TargetVmixValue))
                {
                    binding.DisplayName = BuildVmixDisplayName(binding.TargetVmixFunctionName, binding.TargetVmixValue);
                }
                else if (e.PropertyName == nameof(HotkeyBindingViewModel.TargetKind))
                {
                    RefreshVmixHotkeys();
                }
            }
            else if (sender is HotkeyBindingViewModel execBinding && execBinding.TargetKind == Services.Hotkeys.HotkeyTargetKind.ExecCommand)
            {
                if (e.PropertyName == nameof(HotkeyBindingViewModel.TargetExecCommand))
                {
                    execBinding.DisplayName = BuildExecCommandDisplayName(execBinding.TargetExecCommand);
                }
                else if (e.PropertyName == nameof(HotkeyBindingViewModel.TargetKind))
                {
                    RefreshExecCommandHotkeys();
                }
            }

            if (_isLoadingHotkeys)
                return;

            SyncHotkeysToService();
            SaveSettings();
        }

        private void OnHotkeyBindingCaptured(object? sender, HotkeyBindingCapturedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (e.RebindId != null)
                {
                    var existing = HotkeyBindings.FirstOrDefault(b => b.Id == e.RebindId.Value);
                    if (existing != null)
                    {
                        _isLoadingHotkeys = true;
                        existing.Key = e.Binding.Key;
                        existing.Modifiers = e.Binding.Modifiers;
                        _isLoadingHotkeys = false;
                    }
                }
                else
                {
                    var newBinding = HotkeyBindingViewModel.FromData(e.Binding);
                    HotkeyBindings.Add(newBinding);
                    AttachHotkeyBinding(newBinding);
                    AddToHotkeyLists(newBinding);
                }

                EnsureUniqueHotkey(e.Binding, e.RebindId);
                RefreshCommandHotkeys();
                RefreshCampathHotkeys();
                RefreshGraphicsHotkeys();
                RefreshAttachHotkeys();
                RefreshExecCommandHotkeys();
                RefreshVmixHotkeys();
                SyncHotkeysToService();
                SaveSettings();
            });
        }

        private void EnsureUniqueHotkey(HotkeyBindingData binding, Guid? rebindId)
        {
            var excludeId = rebindId ?? binding.Id;
            var isCampathBinding = binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.Campath
                || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.CampathGroup;
            var isGraphicsBinding = binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.GraphicsAtlasAction
                || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.GraphicsInstanceAction;
            var isAttachBinding = binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.AttachPresetSlotAction;
            var bindingProfileId = binding.TargetCampathProfileId;
            var bindingGraphicsProfile = binding.TargetGraphicsProfileName;
            var bindingAttachPage = binding.TargetAttachPresetPage;
            var duplicates = HotkeyBindings
                .Where(b => b.Key == binding.Key && b.Modifiers == binding.Modifiers && b.Id != excludeId)
                .Where(b =>
                {
                    if (!isCampathBinding)
                    {
                        if (!isGraphicsBinding)
                        {
                            if (!isAttachBinding)
                                return true;

                            var otherIsAttach = b.TargetKind == Services.Hotkeys.HotkeyTargetKind.AttachPresetSlotAction;
                            if (!otherIsAttach)
                                return true;

                            return b.TargetAttachPresetPage == bindingAttachPage;
                        }

                        var otherIsGraphics = b.TargetKind == Services.Hotkeys.HotkeyTargetKind.GraphicsAtlasAction
                            || b.TargetKind == Services.Hotkeys.HotkeyTargetKind.GraphicsInstanceAction;
                        if (!otherIsGraphics)
                            return true;

                        return string.Equals(b.TargetGraphicsProfileName, bindingGraphicsProfile, StringComparison.Ordinal);
                    }

                    var otherIsCampath = b.TargetKind == Services.Hotkeys.HotkeyTargetKind.Campath
                        || b.TargetKind == Services.Hotkeys.HotkeyTargetKind.CampathGroup;
                    if (!otherIsCampath)
                        return true;

                    return b.TargetCampathProfileId == bindingProfileId;
                })
                .ToList();

            foreach (var duplicate in duplicates)
            {
                DetachHotkeyBinding(duplicate);
                HotkeyBindings.Remove(duplicate);
                RemoveFromHotkeyLists(duplicate);
            }
        }

        private void OnHotkeyBindingModeChanged(object? sender, bool isEnabled)
        {
            if (_isHotkeyBindingMode == isEnabled)
                return;

            _suppressHotkeyModeUpdate = true;
            IsHotkeyBindingMode = isEnabled;
            _suppressHotkeyModeUpdate = false;
        }

        private void OnHotkeyStatusChanged(object? sender, string message)
        {
            HotkeyStatusMessage = message;
        }

        private void RemoveHotkey(HotkeyBindingViewModel binding)
        {
            DetachHotkeyBinding(binding);
            HotkeyBindings.Remove(binding);
            RemoveFromHotkeyLists(binding);
            if (ReferenceEquals(SelectedHotkey, binding))
                SelectedHotkey = null;

            SyncHotkeysToService();
            SaveSettings();
        }

        private void SyncHotkeysToService()
        {
            _hotkeyService.SetBindings(HotkeyBindings.Select(binding => binding.ToData()));
        }

        private void AddToHotkeyLists(HotkeyBindingViewModel binding)
        {
            if (binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.Campath
                || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.CampathGroup)
            {
                RefreshCampathHotkeys();
                return;
            }

            if (binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.GraphicsAtlasAction
                || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.GraphicsInstanceAction)
            {
                RefreshGraphicsHotkeys();
                return;
            }

            if (binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.AttachPresetSlotAction)
            {
                RefreshAttachHotkeys();
                return;
            }

            if (binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.VmixFunction)
            {
                RefreshVmixHotkeys();
                return;
            }

            if (binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.ExecCommand)
            {
                RefreshExecCommandHotkeys();
                return;
            }

            if (!CommandHotkeyBindings.Contains(binding))
                CommandHotkeyBindings.Add(binding);
        }

        private void RemoveFromHotkeyLists(HotkeyBindingViewModel binding)
        {
            RefreshCommandHotkeys();
            RefreshCampathHotkeys();
            RefreshGraphicsHotkeys();
            RefreshAttachHotkeys();
            RefreshExecCommandHotkeys();
            RefreshVmixHotkeys();
        }

        private void RefreshCommandHotkeys()
        {
            CommandHotkeyBindings.Clear();
            foreach (var binding in HotkeyBindings)
            {
                if (binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.Campath
                    || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.CampathGroup
                    || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.GraphicsAtlasAction
                    || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.GraphicsInstanceAction
                    || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.AttachPresetSlotAction
                    || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.ExecCommand
                    || binding.TargetKind == Services.Hotkeys.HotkeyTargetKind.VmixFunction)
                    continue;

                CommandHotkeyBindings.Add(binding);
            }
        }

        private void RefreshCampathHotkeys()
        {
            CampathHotkeyBindings.Clear();
            var activeProfileId = _campathsDockViewModel?.SelectedProfile?.Id;
            if (activeProfileId == null || activeProfileId == Guid.Empty)
                return;

            foreach (var binding in HotkeyBindings)
            {
                if (binding.TargetKind != Services.Hotkeys.HotkeyTargetKind.Campath
                    && binding.TargetKind != Services.Hotkeys.HotkeyTargetKind.CampathGroup)
                    continue;

                if (binding.TargetCampathProfileId == activeProfileId)
                    CampathHotkeyBindings.Add(binding);
            }

            OnPropertyChanged(nameof(ActiveCampathProfileName));
        }

        private void RefreshGraphicsHotkeys()
        {
            GraphicsHotkeyBindings.Clear();
            var activeProfileName = _graphicsDockViewModel?.SelectedProfileName;
            if (string.IsNullOrWhiteSpace(activeProfileName))
            {
                OnPropertyChanged(nameof(ActiveGraphicsProfileName));
                return;
            }

            foreach (var binding in HotkeyBindings)
            {
                if (binding.TargetKind != Services.Hotkeys.HotkeyTargetKind.GraphicsAtlasAction
                    && binding.TargetKind != Services.Hotkeys.HotkeyTargetKind.GraphicsInstanceAction)
                    continue;

                if (string.Equals(binding.TargetGraphicsProfileName, activeProfileName, StringComparison.Ordinal))
                    GraphicsHotkeyBindings.Add(binding);
            }

            OnPropertyChanged(nameof(ActiveGraphicsProfileName));
        }

        private void RefreshAttachHotkeys()
        {
            AttachHotkeyBindings.Clear();
            var activePage = _hudSettings.ActiveAttachPresetPage;
            foreach (var binding in HotkeyBindings)
            {
                if (binding.TargetKind != Services.Hotkeys.HotkeyTargetKind.AttachPresetSlotAction)
                    continue;

                if (binding.TargetAttachPresetPage == activePage)
                    AttachHotkeyBindings.Add(binding);
            }

            OnPropertyChanged(nameof(ActiveAttachPresetPageName));
        }

        private void RefreshVmixHotkeys()
        {
            VmixHotkeyBindings.Clear();
            foreach (var binding in HotkeyBindings)
            {
                if (binding.TargetKind != Services.Hotkeys.HotkeyTargetKind.VmixFunction)
                    continue;

                ConfigureVmixBinding(binding, keepFunctionIfValid: true);
                VmixHotkeyBindings.Add(binding);
            }
        }

        private void RefreshExecCommandHotkeys()
        {
            ExecCommandHotkeyBindings.Clear();
            foreach (var binding in HotkeyBindings)
            {
                if (binding.TargetKind != Services.Hotkeys.HotkeyTargetKind.ExecCommand)
                    continue;

                binding.DisplayName = BuildExecCommandDisplayName(binding.TargetExecCommand);
                ExecCommandHotkeyBindings.Add(binding);
            }
        }

        private void AddVmixHotkey()
        {
            var category = VmixFunctionCategories.FirstOrDefault() ?? string.Empty;
            var function = category.Length > 0 && _vmixFunctionsByCategory.TryGetValue(category, out var funcs)
                ? funcs.FirstOrDefault()?.Name ?? string.Empty
                : string.Empty;

            var binding = new HotkeyBindingViewModel
            {
                Id = Guid.NewGuid(),
                Enabled = true,
                Key = Key.None,
                Modifiers = KeyModifiers.None,
                TargetKind = Services.Hotkeys.HotkeyTargetKind.VmixFunction,
                TargetVmixFunctionCategory = category,
                TargetVmixFunctionName = function,
                DisplayName = BuildVmixDisplayName(function, null)
            };

            ConfigureVmixBinding(binding, keepFunctionIfValid: true);
            HotkeyBindings.Add(binding);
            AttachHotkeyBinding(binding);
            AddToHotkeyLists(binding);
            SelectedHotkey = binding;
            SyncHotkeysToService();
            SaveSettings();
        }

        private void AddCommandHotkey()
        {
            var binding = new HotkeyBindingViewModel
            {
                Id = Guid.NewGuid(),
                Enabled = true,
                Key = Key.None,
                Modifiers = KeyModifiers.None,
                TargetKind = Services.Hotkeys.HotkeyTargetKind.ExecCommand,
                TargetExecCommand = string.Empty,
                DisplayName = BuildExecCommandDisplayName(null)
            };

            HotkeyBindings.Add(binding);
            AttachHotkeyBinding(binding);
            AddToHotkeyLists(binding);
            SelectedHotkey = binding;
            SyncHotkeysToService();
            SaveSettings();
        }

        private async Task RefreshVmixStateAsync()
        {
            var snapshot = await _vmixApiClient.FetchStateAsync(System.Threading.CancellationToken.None);
            if (snapshot == null)
            {
                VmixStateStatusMessage = "Failed to fetch vMix state.";
                VmixInputOptions.Clear();
                return;
            }

            VmixInputOptions.Clear();
            foreach (var input in snapshot.Inputs.OrderBy(i => i.Number))
            {
                VmixInputOptions.Add(input);
            }

            if (snapshot.Transitions.Count > 0)
            {
                const string transitionCategory = "Transition";
                if (!_vmixFunctionsByCategory.TryGetValue(transitionCategory, out var functions))
                {
                    functions = new List<VmixFunctionDefinition>();
                    _vmixFunctionsByCategory[transitionCategory] = functions;
                    if (!VmixFunctionCategories.Contains(transitionCategory))
                        VmixFunctionCategories.Add(transitionCategory);
                }

                foreach (var transition in snapshot.Transitions)
                {
                    if (functions.Any(f => string.Equals(f.Name, transition, StringComparison.Ordinal)))
                        continue;

                    functions.Add(new VmixFunctionDefinition
                    {
                        Category = transitionCategory,
                        Name = transition,
                        Description = "Dynamic transition from vMix state.",
                        ParameterKinds = new List<VmixFunctionParameterKind> { VmixFunctionParameterKind.None }
                    });
                }
            }

            foreach (var binding in VmixHotkeyBindings)
            {
                ConfigureVmixBinding(binding, keepFunctionIfValid: true);
            }

            VmixStateStatusMessage = $"vMix state loaded. Inputs: {snapshot.Inputs.Count}, Transitions: {snapshot.Transitions.Count}.";
        }

        private void ConfigureVmixBinding(HotkeyBindingViewModel binding, bool keepFunctionIfValid)
        {
            if (_isUpdatingVmixBindingUi || binding.TargetKind != Services.Hotkeys.HotkeyTargetKind.VmixFunction)
                return;

            _isUpdatingVmixBindingUi = true;
            try
            {
                var category = binding.TargetVmixFunctionCategory;
                if (string.IsNullOrWhiteSpace(category))
                {
                    category = VmixFunctionCategories.FirstOrDefault() ?? string.Empty;
                    binding.TargetVmixFunctionCategory = category;
                }
                else if (!_vmixFunctionsByCategory.ContainsKey(category))
                {
                    // Preserve persisted/custom categories instead of forcing a reset.
                    _vmixFunctionsByCategory[category] = new List<VmixFunctionDefinition>();
                    if (!VmixFunctionCategories.Contains(category))
                        VmixFunctionCategories.Add(category);
                }

                if (!string.IsNullOrWhiteSpace(category) && _vmixFunctionsByCategory.TryGetValue(category, out var functionDefs))
                {
                    var orderedNames = functionDefs
                        .OrderBy(f => f.Name, StringComparer.Ordinal)
                        .Select(f => f.Name)
                        .ToList();

                    if (!keepFunctionIfValid)
                    {
                        binding.VmixFunctionOptions.Clear();
                    }

                    foreach (var functionName in orderedNames)
                    {
                        if (!binding.VmixFunctionOptions.Contains(functionName))
                            binding.VmixFunctionOptions.Add(functionName);
                    }

                    if (!keepFunctionIfValid || string.IsNullOrWhiteSpace(binding.TargetVmixFunctionName))
                    {
                        binding.TargetVmixFunctionName = binding.VmixFunctionOptions.FirstOrDefault();
                    }
                    else if (!binding.VmixFunctionOptions.Contains(binding.TargetVmixFunctionName))
                    {
                        // Preserve persisted/custom functions (e.g. dynamic transitions)
                        // across refreshes and app restarts.
                        binding.VmixFunctionOptions.Add(binding.TargetVmixFunctionName);
                    }
                }

                UpdateVmixBindingParameterState(binding, category);
                binding.DisplayName = BuildVmixDisplayName(binding.TargetVmixFunctionName, binding.TargetVmixValue);
            }
            finally
            {
                _isUpdatingVmixBindingUi = false;
            }
        }

        private void UpdateVmixBindingParameterState(HotkeyBindingViewModel binding, string? category)
        {
            var definition = _vmixShortcutCatalog.FindFunction(binding.TargetVmixFunctionName)
                ?? (!string.IsNullOrWhiteSpace(category) && _vmixFunctionsByCategory.TryGetValue(category, out var dynamicDefs)
                    ? dynamicDefs.FirstOrDefault(d => string.Equals(d.Name, binding.TargetVmixFunctionName, StringComparison.Ordinal))
                    : null);

            var parameterKinds = definition?.ParameterKinds ?? new List<VmixFunctionParameterKind> { VmixFunctionParameterKind.Custom };
            binding.VmixHasValueParameter = parameterKinds.Contains(VmixFunctionParameterKind.Value);
            binding.VmixHasInputParameter = parameterKinds.Contains(VmixFunctionParameterKind.Input);
            binding.VmixHasChannelParameter = parameterKinds.Contains(VmixFunctionParameterKind.Channel);
            binding.VmixHasDurationParameter = parameterKinds.Contains(VmixFunctionParameterKind.Duration);
            binding.VmixHasCustomParameter = parameterKinds.Contains(VmixFunctionParameterKind.Custom)
                || (!binding.VmixHasValueParameter && !binding.VmixHasInputParameter && !binding.VmixHasChannelParameter && !binding.VmixHasDurationParameter);

            if (!binding.VmixHasValueParameter)
                binding.TargetVmixValue = null;
            if (!binding.VmixHasInputParameter)
                binding.TargetVmixInputNumber = null;
            binding.SelectedVmixInput = VmixInputOptions.FirstOrDefault(i => i.Number == binding.TargetVmixInputNumber);
            if (!binding.VmixHasChannelParameter)
                binding.TargetVmixChannel = null;
            if (!binding.VmixHasDurationParameter)
                binding.TargetVmixDuration = null;
            if (!binding.VmixHasCustomParameter)
                binding.TargetVmixExtraQuery = null;
        }

        private static string BuildVmixDisplayName(string? functionName, string? value)
        {
            if (string.IsNullOrWhiteSpace(functionName))
                return "vMix";

            return string.IsNullOrWhiteSpace(value)
                ? $"vMix: {functionName}"
                : $"vMix: {functionName}({value})";
        }

        private static string BuildExecCommandDisplayName(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return "Command";

            return $"Command: {command}";
        }

        private void OnCampathProfileChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CampathsDockViewModel.SelectedProfile))
            {
                RefreshCampathHotkeys();
            }
        }

        private void OnCampathProfileRemoved(object? sender, Guid profileId)
        {
            var toRemove = HotkeyBindings
                .Where(b => b.TargetCampathProfileId == profileId)
                .ToList();

            foreach (var binding in toRemove)
            {
                DetachHotkeyBinding(binding);
                HotkeyBindings.Remove(binding);
                CommandHotkeyBindings.Remove(binding);
            }

            RefreshCampathHotkeys();
            SaveSettings();
        }

        private void OnGraphicsProfileChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GraphicsDockViewModel.SelectedProfileName))
            {
                RefreshGraphicsHotkeys();
            }
        }

        private void OnGraphicsProfileRemoved(object? sender, string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return;

            var toRemove = HotkeyBindings
                .Where(b => string.Equals(b.TargetGraphicsProfileName, profileName, StringComparison.Ordinal))
                .ToList();

            foreach (var binding in toRemove)
            {
                DetachHotkeyBinding(binding);
                HotkeyBindings.Remove(binding);
                CommandHotkeyBindings.Remove(binding);
            }

            RefreshGraphicsHotkeys();
            SaveSettings();
        }

        public string ActiveCampathProfileName => _campathsDockViewModel?.SelectedProfile?.Name ?? "No profile selected";
        public string ActiveGraphicsProfileName => _graphicsDockViewModel?.SelectedProfileName ?? "No profile selected";
        public string ActiveAttachPresetPageName => _hudSettings.GetAttachPresetPageName(_hudSettings.ActiveAttachPresetPage);

        private void SaveSettings()
        {
            var gsiRelayUris = GetSanitizedGsiRelayUris();
            var data = new AppSettingsData
            {
                AttachPresetPages = _hudSettings.ToAttachPresetPageData().ToList(),
                ActiveAttachPresetPage = _hudSettings.ActiveAttachPresetPage,
                RadarScale = _radarSettings.RadarScale,
                MarkerScale = _radarSettings.MarkerScale,
                HeightScaleMultiplier = _radarSettings.HeightScaleMultiplier,
                HudSize = _hudSettings.HudSize,
                UseAltPlayerBinds = _radarSettings.UseAltPlayerBinds,
                DisplayNumbersTopmost = _radarSettings.DisplayNumbersTopmost,
                ShowPlayerNames = _radarSettings.ShowPlayerNames,
                RadarStyle = _radarSettings.RadarStyle,
                WebSocketHost = WebSocketHost,
                WebSocketPort = WebSocketPort,
                GraphicsProducerHost = WebSocketHost,
                GraphicsProducerPort = GraphicsProducerPort,
                UdpPort = UdpPort,
                RtpPort = RtpPort,
                GsiPort = GsiPort,
                NetConsoleHostPort = _storedSettings.NetConsoleHostPort,
                GsiRelayUris = gsiRelayUris,
                MapObjPath = _viewport3DSettings.MapObjPath,
                Cs2GameFolder = _viewport3DSettings.Cs2GameFolder,
                ViewportSelectedMapName = _viewport3DSettings.SelectedMapName,
                ViewportActiveDutyMapsOnly = _viewport3DSettings.ActiveDutyMapsOnly,
                ViewportShowPlayerPins = _viewport3DSettings.ShowPlayerPins,
                PinScale = _viewport3DSettings.PinScale,
                PinOffsetZ = _viewport3DSettings.PinOffsetZ,
                ViewportMouseScale = _viewport3DSettings.ViewportMouseScale,
                ViewportFpsCap = _viewport3DSettings.ViewportFpsCap,
                ViewportPostprocessEnabled = _viewport3DSettings.PostprocessEnabled,
                ViewportColorCorrectionEnabled = _viewport3DSettings.ColorCorrectionEnabled,
                ViewportDynamicShadowsEnabled = _viewport3DSettings.DynamicShadowsEnabled,
                ViewportWireframeEnabled = _viewport3DSettings.WireframeEnabled,
                ViewportSkipWaterEnabled = _viewport3DSettings.SkipWaterEnabled,
                ViewportSkipTranslucentEnabled = _viewport3DSettings.SkipTranslucentEnabled,
                ViewportShowFps = _viewport3DSettings.ShowFps,
                ShowHlaeCampathControls = ShowHlaeCampathControls,
                DefaultCampathInterp = _defaultCampathInterp.ToString(),
                ViewportCampathOverlayEnabled = _viewport3DSettings.ViewportCampathOverlayEnabled,
                ViewportCampathGizmoEnabled = _viewport3DSettings.ViewportCampathGizmoEnabled,
                ViewportCampathSyncEnabled = _viewport3DSettings.ViewportCampathSyncEnabled,
                CampathGizmoLocalSpace = _viewport3DSettings.CampathGizmoLocalSpace,
                ViewportLiveLinkEnabled = _viewport3DSettings.LiveLinkEnabled,
                ViewportLiveLinkItemIconsEnabled = _viewport3DSettings.LiveLinkItemIconsEnabled,
                ViewportLiveLinkWeaponIconsEnabled = _viewport3DSettings.LiveLinkWeaponIconsEnabled,
                ViewportLiveLinkGrenadeIconsEnabled = _viewport3DSettings.LiveLinkGrenadeIconsEnabled,
                ViewportLiveLinkProjectileIconsEnabled = _viewport3DSettings.LiveLinkProjectileIconsEnabled,
                ViewportLiveLinkObjectiveIconsEnabled = _viewport3DSettings.LiveLinkObjectiveIconsEnabled,
                ViewportLiveLinkDeadPlayerIconsEnabled = _viewport3DSettings.LiveLinkDeadPlayerIconsEnabled,
                ViewportLiveLinkPort = _viewport3DSettings.LiveLinkPort,
                ViewportShadowTextureSize = _viewport3DSettings.ShadowTextureSize,
                ViewportMaxTextureSize = _viewport3DSettings.MaxTextureSize,
                ViewportRenderMode = _viewport3DSettings.RenderMode,
                FreecamSettings = _freecamSettings.ToData(),
                VmixReplayEnabled = _vmixReplaySettings.Enabled,
                VmixReplayHost = _vmixSettings.Host,
                VmixReplayPort = _vmixSettings.Port,
                VmixReplayPreSeconds = _vmixReplaySettings.PreSeconds,
                VmixReplayPostSeconds = _vmixReplaySettings.PostSeconds,
                VmixReplayExtendWindowSeconds = _vmixReplaySettings.ExtendWindowSeconds,
                VmixReplayChannel = _vmixReplaySettings.Channel,
                VmixReplayCamera = _vmixReplaySettings.Camera,
                ReplayDirectorRole = _replayDirectorSettings.Role,
                ReplayDirectorPublisherPort = _replayDirectorSettings.PublisherPort,
                ReplayDirectorPublisherIp = _replayDirectorSettings.PublisherIp,
                ReplayDirectorPreSwitchSeconds = _replayDirectorSettings.PreSwitchSeconds,
                ReplayDirectorMergeWindowSeconds = _replayDirectorSettings.MergeWindowSeconds,
                ReplayDirectorSwitchLockSeconds = _replayDirectorSettings.SwitchLockSeconds,
                ReplayDirectorOnlyFollowMissedKills = _replayDirectorSettings.OnlyFollowMissedKills,
                ReplayDirectorDelayedVmixEnabled = _replayDirectorSettings.DelayedVmixEnabled,
                ReplayDirectorDelayedVmixChannel = _replayDirectorSettings.DelayedVmixChannel,
                ReplayDirectorDelayedVmixCamera = _replayDirectorSettings.DelayedVmixCamera,
                DisableFocusInputGate = _disableFocusInputGate,
                Hotkeys = HotkeyBindings.Select(binding => binding.ToData()).ToList()
            };
            _settingsStorage.Save(data);
        }

        private List<string> GetSanitizedGsiRelayUris()
        {
            var relayUris = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var endpoint in GsiRelayEndpoints)
            {
                var value = endpoint.Uri?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    continue;

                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    continue;

                var normalized = uri.AbsoluteUri;
                if (!seen.Add(normalized))
                    continue;

                relayUris.Add(normalized);
            }

            return relayUris;
        }

        private void RefreshNetworkHealth()
        {
            RefreshWebSocketHealth();
            RefreshUdpHealth();
            RefreshRtpHealth();
            RefreshGsiHealth();
            RefreshGraphicsProducerHealth();
            RefreshRelayHealth();
        }

        private void RefreshWebSocketHealth()
        {
            if (_ws == null)
            {
                WebSocketHealthBrush = HealthGrayBrush;
                WebSocketHealthTooltip = "WebSocket client unavailable.";
                return;
            }

            if (_ws.IsConnected)
            {
                WebSocketHealthBrush = HealthGreenBrush;
                WebSocketHealthTooltip = $"Connected to {WebSocketHost}:{WebSocketPort}.";
            }
            else
            {
                WebSocketHealthBrush = HealthRedBrush;
                WebSocketHealthTooltip = $"Disconnected from {WebSocketHost}:{WebSocketPort}.";
            }
        }

        private void RefreshUdpHealth()
        {
            if (_inputSender == null)
            {
                UdpHealthBrush = HealthGrayBrush;
                UdpHealthTooltip = "UDP sender unavailable.";
                return;
            }

            if (_inputSender.IsActive)
            {
                UdpHealthBrush = HealthGreenBrush;
                UdpHealthTooltip = $"UDP sender active on {WebSocketHost}:{UdpPort}.";
            }
            else
            {
                UdpHealthBrush = HealthRedBrush;
                UdpHealthTooltip = $"UDP sender inactive on {WebSocketHost}:{UdpPort}.";
            }
        }

        private void RefreshRtpHealth()
        {
            if (_videoDisplayDockViewModel == null)
            {
                RtpHealthBrush = HealthGrayBrush;
                RtpHealthTooltip = "RTP receiver unavailable.";
                return;
            }

            if (!_videoDisplayDockViewModel.IsStreaming)
            {
                if (_videoDisplayDockViewModel.StatusText.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    RtpHealthBrush = HealthRedBrush;
                    RtpHealthTooltip = _videoDisplayDockViewModel.StatusText;
                }
                else
                {
                    RtpHealthBrush = HealthGrayBrush;
                    RtpHealthTooltip = "RTP stream not running.";
                }
                return;
            }

            if (_videoDisplayDockViewModel.StatusText.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                RtpHealthBrush = HealthRedBrush;
                RtpHealthTooltip = _videoDisplayDockViewModel.StatusText;
                return;
            }

            var lastFrame = _videoDisplayDockViewModel.LastFrameReceivedUtc;
            if (!lastFrame.HasValue)
            {
                RtpHealthBrush = HealthOrangeBrush;
                RtpHealthTooltip = "RTP running, waiting for first frame.";
                return;
            }

            var age = DateTimeOffset.UtcNow - lastFrame.Value;
            if (age <= TimeSpan.FromSeconds(2))
            {
                RtpHealthBrush = HealthGreenBrush;
                RtpHealthTooltip = $"RTP receiving frames. Last frame {age.TotalSeconds:F1}s ago.";
            }
            else
            {
                RtpHealthBrush = HealthOrangeBrush;
                RtpHealthTooltip = $"RTP running but no recent frames. Last frame {age.TotalSeconds:F1}s ago.";
            }
        }

        private void RefreshGsiHealth()
        {
            if (_gsiServer == null)
            {
                GsiHealthBrush = HealthGrayBrush;
                GsiHealthTooltip = "GSI listener unavailable.";
                return;
            }

            if (!_gsiServer.IsRunning)
            {
                GsiHealthBrush = HealthRedBrush;
                GsiHealthTooltip = $"GSI listener stopped on port {GsiPort}.";
                return;
            }

            var lastRequest = _gsiServer.LastRequestUtc;
            if (!lastRequest.HasValue)
            {
                GsiHealthBrush = HealthOrangeBrush;
                GsiHealthTooltip = $"GSI listener running on port {GsiPort}, waiting for payloads.";
                return;
            }

            var age = DateTimeOffset.UtcNow - lastRequest.Value;
            if (age <= TimeSpan.FromSeconds(5))
            {
                GsiHealthBrush = HealthGreenBrush;
                GsiHealthTooltip = $"GSI listener healthy. Last payload {age.TotalSeconds:F1}s ago.";
            }
            else
            {
                GsiHealthBrush = HealthOrangeBrush;
                GsiHealthTooltip = $"GSI listener running. Last payload {age.TotalSeconds:F1}s ago.";
            }
        }

        private void RefreshGraphicsProducerHealth()
        {
            if (_graphicsProducerClient == null)
            {
                GraphicsProducerHealthBrush = HealthGrayBrush;
                GraphicsProducerHealthTooltip = "Graphics producer client unavailable.";
                return;
            }

            if (_graphicsProducerClient.IsConnected)
            {
                GraphicsProducerHealthBrush = HealthGreenBrush;
                GraphicsProducerHealthTooltip = $"Graphics producer connected on {WebSocketHost}:{GraphicsProducerPort}.";
            }
            else
            {
                GraphicsProducerHealthBrush = HealthRedBrush;
                GraphicsProducerHealthTooltip = $"Graphics producer disconnected on {WebSocketHost}:{GraphicsProducerPort}.";
            }
        }

        private void RefreshRelayHealth()
        {
            var snapshots = _gsiServer?.GetRelayEndpointHealthSnapshot()
                ?? Array.Empty<GsiRelayEndpointHealth>();
            var byEndpoint = snapshots.ToDictionary(s => s.Endpoint, StringComparer.OrdinalIgnoreCase);

            foreach (var endpointVm in GsiRelayEndpoints)
            {
                if (!TryNormalizeRelayUri(endpointVm.Uri, out var normalized))
                {
                    endpointVm.HealthBrush = HealthRedBrush;
                    endpointVm.HealthTooltip = "Invalid relay URI. Use absolute http/https URL.";
                    continue;
                }

                if (!byEndpoint.TryGetValue(normalized, out var snapshot))
                {
                    endpointVm.HealthBrush = HealthOrangeBrush;
                    endpointVm.HealthTooltip = "Not active yet. Click Apply / Reconnect.";
                    continue;
                }

                endpointVm.HealthBrush = snapshot.Level switch
                {
                    GsiRelayHealthLevel.Healthy => HealthGreenBrush,
                    GsiRelayHealthLevel.Degraded => HealthOrangeBrush,
                    GsiRelayHealthLevel.Unhealthy => HealthRedBrush,
                    _ => HealthGrayBrush
                };

                var updatedText = snapshot.LastUpdatedUtc.HasValue
                    ? $" Last update {snapshot.LastUpdatedUtc.Value.LocalDateTime:T}."
                    : string.Empty;
                endpointVm.HealthTooltip = snapshot.Message + updatedText;
            }
        }

        private static bool TryNormalizeRelayUri(string? rawValue, out string normalized)
        {
            normalized = string.Empty;
            var value = rawValue?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            normalized = uri.AbsoluteUri;
            return true;
        }

        #endregion

        #region ==== Camera Path / Create Tab ====

        private bool _isCameraPathPreviewEnabled;
        public bool IsCameraPathPreviewEnabled
        {
            get => _isCameraPathPreviewEnabled;
            set
            {
                if (_isCameraPathPreviewEnabled != value)
                {
                    _isCameraPathPreviewEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "mirv_campath draw enabled 1"
                        : "mirv_campath draw enabled 0";
                    SendExecCommand(cmd);
                }
            }
        }

        private bool _isCampathEnabled;
        public bool IsCampathEnabled
        {
            get => _isCampathEnabled;
            set
            {
                if (_isCampathEnabled != value)
                {
                    _isCampathEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "mirv_campath enabled 1"
                        : "mirv_campath enabled 0";
                    SendExecCommand(cmd);
                }
            }
        }

        private async Task<string?> PickCampathFileToLoadAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                return null;

            var window = lifetime.MainWindow;
            if (window is null)
                return null;

            var result = await KeyboardInputGate.RunSuppressedAsync(() =>
                window.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Load Campath",
                        AllowMultiple = false
                    }));

            if (result is { Count: > 0 })
                return result[0].Path.LocalPath;

            return null;
        }

        private async Task<string?> PickCampathFileToSaveAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                return null;

            var window = lifetime.MainWindow;
            if (window is null)
                return null;

            var result = await KeyboardInputGate.RunSuppressedAsync(() =>
                window.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Save Campath"
                    }));

            return result?.Path.LocalPath;
        }

        private class AsyncRelay : ICommand
        {
            private readonly Func<Task> _action;
            public AsyncRelay(Func<Task> action) => _action = action;
            public bool CanExecute(object? parameter) => true;
            public async void Execute(object? parameter) => await _action();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }

        // Direct HLAE interpolation state for the connected CS2 campath.
        private bool _useCubic = true;
        public string InterpLabel => _useCubic ? "Interp: Cubic" : "Interp: Linear";

        public ICommand ToggleInterpModeCommand => _toggleInterpModeCommand;

        // Commands that operate directly on mirv_campath in the connected CS2 instance.
        public ICommand AddPointCommand => _addPointCommand;
        public ICommand ClearCampathCommand => _clearCampathCommand;
        public ICommand LoadCampathCommand => _loadCampathCommand;
        public ICommand SaveCampathCommand => _saveCampathCommand;
        public ICommand LoadViewportCampathCommand => _loadViewportCampathCommand;
        public ICommand SaveViewportCampathCommand => _saveViewportCampathCommand;

        private bool _awaitingCurtime;
        public ICommand GetCurrentTimeOffsetCommand => _getCurrentTimeOffsetCommand;

        private async Task GetCurrentTimeOffsetAsync()
        {
            if (_ws == null || !_ws.IsConnected)
                return;

            if (_awaitingCurtime)
                return;

            _awaitingCurtime = true;
            await _ws.SendCommandAsync("curtime_get");
        }


        #endregion

        #region ==== Settings Persistence ====

        private void OnRadarSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressSettingsSave)
                return;

            SaveSettings();
        }

        private void OnHudSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressSettingsSave)
                return;

            if (e.PropertyName == nameof(HudSettings.AttachPresetPages))
            {
                RefreshAttachPresetPageOptions();
            }

            SaveSettings();
        }

        private void OnViewport3DSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressSettingsSave)
                return;

            if (e.PropertyName == nameof(Viewport3DSettings.Cs2GameFolder) ||
                e.PropertyName == nameof(Viewport3DSettings.ActiveDutyMapsOnly))
            {
                _suppressSettingsSave = true;
                RefreshViewportMapOptions();
                _suppressSettingsSave = false;
            }
            else if (e.PropertyName == nameof(Viewport3DSettings.SelectedMap))
            {
                var selected = _viewport3DSettings.SelectedMap;
                if (selected != null)
                {
                    _suppressSettingsSave = true;
                    _viewport3DSettings.SelectedMapName = selected.Name;
                    _viewport3DSettings.MapObjPath = selected.Path;
                    _suppressSettingsSave = false;
                }
            }

            SaveSettings();
        }

        private void OnVmixSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressSettingsSave)
                return;

            if (ReferenceEquals(sender, _replayDirectorSettings) && IsReplayDirectorStatusProperty(e.PropertyName))
                return;

            SaveSettings();
        }

        private static bool IsReplayDirectorStatusProperty(string? propertyName)
        {
            return propertyName == nameof(ReplayDirectorSettings.Status)
                || propertyName == nameof(ReplayDirectorSettings.LastKill)
                || propertyName == nameof(ReplayDirectorSettings.LocalGameTime)
                || propertyName == nameof(ReplayDirectorSettings.ScheduledTarget)
                || propertyName == nameof(ReplayDirectorSettings.LastSwitch)
                || propertyName == nameof(ReplayDirectorSettings.LastVmixMark);
        }

        #endregion

        #region ==== Freecam Settings ====

        private void OnFreecamSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressFreecamSave)
                return;

            if (!_suppressSettingsSave)
                SaveSettings();

            if (_ws == null || string.IsNullOrEmpty(e.PropertyName))
                return;

            switch (e.PropertyName)
            {
                case nameof(FreecamSettings.MouseSensitivity):
                    _ = SendFreecamConfigAsync(new { mouseSensitivity = (float)_freecamSettings.MouseSensitivity });
                    break;
                case nameof(FreecamSettings.MoveSpeed):
                    _ = SendFreecamConfigAsync(new { moveSpeed = (float)_freecamSettings.MoveSpeed });
                    break;
                case nameof(FreecamSettings.SprintMultiplier):
                    _ = SendFreecamConfigAsync(new { sprintMultiplier = (float)_freecamSettings.SprintMultiplier });
                    break;
                case nameof(FreecamSettings.VerticalSpeed):
                    _ = SendFreecamConfigAsync(new { verticalSpeed = (float)_freecamSettings.VerticalSpeed });
                    break;
                case nameof(FreecamSettings.SpeedAdjustRate):
                    _ = SendFreecamConfigAsync(new { speedAdjustRate = (float)_freecamSettings.SpeedAdjustRate });
                    break;
                case nameof(FreecamSettings.SpeedMinMultiplier):
                    _ = SendFreecamConfigAsync(new { speedMinMultiplier = (float)_freecamSettings.SpeedMinMultiplier });
                    break;
                case nameof(FreecamSettings.SpeedMaxMultiplier):
                    _ = SendFreecamConfigAsync(new { speedMaxMultiplier = (float)_freecamSettings.SpeedMaxMultiplier });
                    break;
                case nameof(FreecamSettings.RollSpeed):
                    _ = SendFreecamConfigAsync(new { rollSpeed = (float)_freecamSettings.RollSpeed });
                    break;
                case nameof(FreecamSettings.RollSmoothing):
                    _ = SendFreecamConfigAsync(new { rollSmoothing = (float)_freecamSettings.RollSmoothing });
                    break;
                case nameof(FreecamSettings.LeanStrength):
                    _ = SendFreecamConfigAsync(new { leanStrength = (float)_freecamSettings.LeanStrength });
                    break;
                case nameof(FreecamSettings.LeanAccelScale):
                    _ = SendFreecamConfigAsync(new { leanAccelScale = (float)_freecamSettings.LeanAccelScale });
                    break;
                case nameof(FreecamSettings.LeanVelocityScale):
                    _ = SendFreecamConfigAsync(new { leanVelocityScale = (float)_freecamSettings.LeanVelocityScale });
                    break;
                case nameof(FreecamSettings.LeanMaxAngle):
                    _ = SendFreecamConfigAsync(new { leanMaxAngle = (float)_freecamSettings.LeanMaxAngle });
                    break;
                case nameof(FreecamSettings.LeanHalfTime):
                    _ = SendFreecamConfigAsync(new { leanHalfTime = (float)_freecamSettings.LeanHalfTime });
                    break;
                case nameof(FreecamSettings.FovMin):
                    _ = SendFreecamConfigAsync(new { fovMin = (float)_freecamSettings.FovMin });
                    break;
                case nameof(FreecamSettings.FovMax):
                    _ = SendFreecamConfigAsync(new { fovMax = (float)_freecamSettings.FovMax });
                    break;
                case nameof(FreecamSettings.FovStep):
                    _ = SendFreecamConfigAsync(new { fovStep = (float)_freecamSettings.FovStep });
                    break;
                case nameof(FreecamSettings.DefaultFov):
                    _ = SendFreecamConfigAsync(new { defaultFov = (float)_freecamSettings.DefaultFov });
                    break;
                case nameof(FreecamSettings.SmoothEnabled):
                    _ = SendFreecamConfigAsync(new { smoothEnabled = _freecamSettings.SmoothEnabled });
                    break;
                case nameof(FreecamSettings.HalfVec):
                    _ = SendFreecamConfigAsync(new { halfVec = (float)_freecamSettings.HalfVec });
                    break;
                case nameof(FreecamSettings.HalfRot):
                    _ = SendFreecamConfigAsync(new { halfRot = (float)_freecamSettings.HalfRot });
                    break;
                case nameof(FreecamSettings.LockHalfRot):
                    _ = SendFreecamConfigAsync(new { lockHalfRot = (float)_freecamSettings.LockHalfRot });
                    break;
                case nameof(FreecamSettings.LockHalfRotTransition):
                    _ = SendFreecamConfigAsync(new { lockHalfRotTransition = (float)_freecamSettings.LockHalfRotTransition });
                    break;
                case nameof(FreecamSettings.HalfFov):
                    _ = SendFreecamConfigAsync(new { halfFov = (float)_freecamSettings.HalfFov });
                    break;
                case nameof(FreecamSettings.RotCriticalDamping):
                    _ = SendFreecamConfigAsync(new { rotCriticalDamping = _freecamSettings.RotCriticalDamping });
                    break;
                case nameof(FreecamSettings.RotDampingRatio):
                    _ = SendFreecamConfigAsync(new { rotDampingRatio = (float)_freecamSettings.RotDampingRatio });
                    break;
                case nameof(FreecamSettings.ClampPitch):
                    _ = SendFreecamConfigAsync(new { clampPitch = _freecamSettings.ClampPitch });
                    break;
                case nameof(FreecamSettings.WalkMoveSpeed):
                    _ = SendFreecamConfigAsync(new { walkMoveSpeed = (float)_freecamSettings.WalkMoveSpeed });
                    break;
                case nameof(FreecamSettings.WalkMoveAcceleration):
                    _ = SendFreecamConfigAsync(new { walkMoveAcceleration = (float)_freecamSettings.WalkMoveAcceleration });
                    break;
                case nameof(FreecamSettings.WalkMoveDeceleration):
                    _ = SendFreecamConfigAsync(new { walkMoveDeceleration = (float)_freecamSettings.WalkMoveDeceleration });
                    break;
                case nameof(FreecamSettings.WalkRunMultiplier):
                    _ = SendFreecamConfigAsync(new { walkRunMultiplier = (float)_freecamSettings.WalkRunMultiplier });
                    break;
                case nameof(FreecamSettings.WalkCrouchSpeedMultiplier):
                    _ = SendFreecamConfigAsync(new { walkCrouchSpeedMultiplier = (float)_freecamSettings.WalkCrouchSpeedMultiplier });
                    break;
                case nameof(FreecamSettings.WalkLookHalfTime):
                    _ = SendFreecamConfigAsync(new { walkLookHalfTime = (float)_freecamSettings.WalkLookHalfTime });
                    break;
                case nameof(FreecamSettings.WalkFovHalfTime):
                    _ = SendFreecamConfigAsync(new { walkFovHalfTime = (float)_freecamSettings.WalkFovHalfTime });
                    break;
                case nameof(FreecamSettings.WalkGravity):
                    _ = SendFreecamConfigAsync(new { walkGravity = (float)_freecamSettings.WalkGravity });
                    break;
                case nameof(FreecamSettings.WalkJumpSpeed):
                    _ = SendFreecamConfigAsync(new { walkJumpSpeed = (float)_freecamSettings.WalkJumpSpeed });
                    break;
                case nameof(FreecamSettings.WalkHullRadius):
                    _ = SendFreecamConfigAsync(new { walkHullRadius = (float)_freecamSettings.WalkHullRadius });
                    break;
                case nameof(FreecamSettings.WalkHullHalfHeight):
                    _ = SendFreecamConfigAsync(new { walkHullHalfHeight = (float)_freecamSettings.WalkHullHalfHeight });
                    break;
                case nameof(FreecamSettings.WalkCrouchHullHalfHeight):
                    _ = SendFreecamConfigAsync(new { walkCrouchHullHalfHeight = (float)_freecamSettings.WalkCrouchHullHalfHeight });
                    break;
                case nameof(FreecamSettings.WalkCameraTopInset):
                    _ = SendFreecamConfigAsync(new { walkCameraTopInset = (float)_freecamSettings.WalkCameraTopInset });
                    break;
                case nameof(FreecamSettings.WalkStepHeight):
                    _ = SendFreecamConfigAsync(new { walkStepHeight = (float)_freecamSettings.WalkStepHeight });
                    break;
                case nameof(FreecamSettings.WalkGroundProbe):
                    _ = SendFreecamConfigAsync(new { walkGroundProbe = (float)_freecamSettings.WalkGroundProbe });
                    break;
                case nameof(FreecamSettings.WalkMinGroundNormalZ):
                    _ = SendFreecamConfigAsync(new { walkMinGroundNormalZ = (float)_freecamSettings.WalkMinGroundNormalZ });
                    break;
                case nameof(FreecamSettings.WalkModeDefaultEnabled):
                    _ = SendFreecamConfigAsync(new { walkModeDefaultEnabled = _freecamSettings.WalkModeDefaultEnabled });
                    break;
                case nameof(FreecamSettings.HandheldDefaultEnabled):
                    _ = SendFreecamConfigAsync(new { handheldDefaultEnabled = _freecamSettings.HandheldDefaultEnabled });
                    break;
                case nameof(FreecamSettings.WalkBobAmplitudeZ):
                    _ = SendFreecamConfigAsync(new { walkBobAmplitudeZ = (float)_freecamSettings.WalkBobAmplitudeZ });
                    break;
                case nameof(FreecamSettings.WalkBobAmplitudeSide):
                    _ = SendFreecamConfigAsync(new { walkBobAmplitudeSide = (float)_freecamSettings.WalkBobAmplitudeSide });
                    break;
                case nameof(FreecamSettings.WalkBobAmplitudeRoll):
                    _ = SendFreecamConfigAsync(new { walkBobAmplitudeRoll = (float)_freecamSettings.WalkBobAmplitudeRoll });
                    break;
                case nameof(FreecamSettings.WalkBobFrequency):
                    _ = SendFreecamConfigAsync(new { walkBobFrequency = (float)_freecamSettings.WalkBobFrequency });
                    break;
                case nameof(FreecamSettings.HandheldShakePosAmplitude):
                    _ = SendFreecamConfigAsync(new { handheldShakePosAmplitude = (float)_freecamSettings.HandheldShakePosAmplitude });
                    break;
                case nameof(FreecamSettings.HandheldShakeAngAmplitude):
                    _ = SendFreecamConfigAsync(new { handheldShakeAngAmplitude = (float)_freecamSettings.HandheldShakeAngAmplitude });
                    break;
                case nameof(FreecamSettings.HandheldShakeFrequency):
                    _ = SendFreecamConfigAsync(new { handheldShakeFrequency = (float)_freecamSettings.HandheldShakeFrequency });
                    break;
                case nameof(FreecamSettings.HandheldDriftPosAmplitude):
                    _ = SendFreecamConfigAsync(new { handheldDriftPosAmplitude = (float)_freecamSettings.HandheldDriftPosAmplitude });
                    break;
                case nameof(FreecamSettings.HandheldDriftAngAmplitude):
                    _ = SendFreecamConfigAsync(new { handheldDriftAngAmplitude = (float)_freecamSettings.HandheldDriftAngAmplitude });
                    break;
                case nameof(FreecamSettings.HandheldDriftFrequency):
                    _ = SendFreecamConfigAsync(new { handheldDriftFrequency = (float)_freecamSettings.HandheldDriftFrequency });
                    break;
            }
        }

        public ICommand ResetFreecamSettingsCommand => _resetFreecamSettingsCommand;

        private void ResetFreecamSettings()
        {
            _suppressFreecamSave = true;
            _freecamSettings.ResetToDefaults();
            _suppressFreecamSave = false;
            SaveSettings();
            _ = SendAllFreecamConfigAsync();
        }

        // Helper method to send freecam config updates
        private async Task SendFreecamConfigAsync(object config)
        {
            if (_ws == null)
                return;

            await _ws.SendCommandAsync("freecam_config", config);
        }

        private async Task SendAllFreecamConfigAsync()
        {
            if (_ws == null)
                return;

            var config = new
            {
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
                halfFov = (float)_freecamSettings.HalfFov,
                rotCriticalDamping = _freecamSettings.RotCriticalDamping,
                rotDampingRatio = (float)_freecamSettings.RotDampingRatio,
                clampPitch = _freecamSettings.ClampPitch,
                walkMoveSpeed = (float)_freecamSettings.WalkMoveSpeed,
                walkMoveAcceleration = (float)_freecamSettings.WalkMoveAcceleration,
                walkMoveDeceleration = (float)_freecamSettings.WalkMoveDeceleration,
                walkRunMultiplier = (float)_freecamSettings.WalkRunMultiplier,
                walkCrouchSpeedMultiplier = (float)_freecamSettings.WalkCrouchSpeedMultiplier,
                walkLookHalfTime = (float)_freecamSettings.WalkLookHalfTime,
                walkFovHalfTime = (float)_freecamSettings.WalkFovHalfTime,
                walkGravity = (float)_freecamSettings.WalkGravity,
                walkJumpSpeed = (float)_freecamSettings.WalkJumpSpeed,
                walkHullRadius = (float)_freecamSettings.WalkHullRadius,
                walkHullHalfHeight = (float)_freecamSettings.WalkHullHalfHeight,
                walkCrouchHullHalfHeight = (float)_freecamSettings.WalkCrouchHullHalfHeight,
                walkCameraTopInset = (float)_freecamSettings.WalkCameraTopInset,
                walkStepHeight = (float)_freecamSettings.WalkStepHeight,
                walkGroundProbe = (float)_freecamSettings.WalkGroundProbe,
                walkMinGroundNormalZ = (float)_freecamSettings.WalkMinGroundNormalZ,
                walkModeDefaultEnabled = _freecamSettings.WalkModeDefaultEnabled,
                handheldDefaultEnabled = _freecamSettings.HandheldDefaultEnabled,
                walkBobAmplitudeZ = (float)_freecamSettings.WalkBobAmplitudeZ,
                walkBobAmplitudeSide = (float)_freecamSettings.WalkBobAmplitudeSide,
                walkBobAmplitudeRoll = (float)_freecamSettings.WalkBobAmplitudeRoll,
                walkBobFrequency = (float)_freecamSettings.WalkBobFrequency,
                handheldShakePosAmplitude = (float)_freecamSettings.HandheldShakePosAmplitude,
                handheldShakeAngAmplitude = (float)_freecamSettings.HandheldShakeAngAmplitude,
                handheldShakeFrequency = (float)_freecamSettings.HandheldShakeFrequency,
                handheldDriftPosAmplitude = (float)_freecamSettings.HandheldDriftPosAmplitude,
                handheldDriftAngAmplitude = (float)_freecamSettings.HandheldDriftAngAmplitude,
                handheldDriftFrequency = (float)_freecamSettings.HandheldDriftFrequency
            };

            await _ws.SendCommandAsync("freecam_config", config);
        }

        private Task SendExecCommandAsync(string command)
        {
            if (_ws == null)
                return Task.CompletedTask;

            return _ws.SendExecCommandAsync(command);
        }

        public Task ExecuteHotkeyCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return Task.CompletedTask;

            return SendExecCommandAsync(command);
        }

        private void SendExecCommand(string command)
        {
            _ = SendExecCommandAsync(command);
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
                bone = preset.BoneName,
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

        #endregion

        // Simple ICommand helper (no MVVM library required)
        private class Relay : ICommand
        {
            private readonly Action _action;
            public Relay(Action action) => _action = action;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _action();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }

        private sealed class RelayParam<T> : ICommand where T : class
        {
            private readonly Action<T?> _action;
            private readonly Func<T?, bool>? _canExecute;

            public RelayParam(Action<T?> action, Func<T?, bool>? canExecute = null)
            {
                _action = action;
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter as T) ?? true;

            public void Execute(object? parameter) => _action(parameter as T);

            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }
    }
}
