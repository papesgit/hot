using System;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Settings;


namespace HlaeObsTools.ViewModels.Docks
{
    /// <summary>
    /// Settings dock for configuring UI options like radar markers and camera paths.
    /// </summary>
    public class SettingsDockViewModel : Tool
    {
        private readonly RadarSettings _radarSettings;
        private readonly HudSettings _hudSettings;
        private readonly FreecamSettings _freecamSettings;
        private readonly Viewport3DSettings _viewport3DSettings;
        private readonly SettingsStorage _settingsStorage;
        private readonly HlaeWebSocketClient? _ws;
        private readonly Func<NetworkSettingsData, Task>? _applyNetworkSettingsAsync;

        public record NetworkSettingsData(string WebSocketHost, int WebSocketPort, int UdpPort, int RtpPort, int GsiPort);

        public SettingsDockViewModel(RadarSettings radarSettings, HudSettings hudSettings, FreecamSettings freecamSettings, Viewport3DSettings viewport3DSettings, SettingsStorage settingsStorage, HlaeWebSocketClient wsClient, Func<NetworkSettingsData, Task>? applyNetworkSettingsAsync = null, AppSettingsData? storedSettings = null)
        {
            _radarSettings = radarSettings;
            _hudSettings = hudSettings;
            _freecamSettings = freecamSettings;
            _viewport3DSettings = viewport3DSettings;
            _settingsStorage = settingsStorage;
            _ws = wsClient;
            _applyNetworkSettingsAsync = applyNetworkSettingsAsync;

            Title = "Settings";
            CanClose = false;
            CanFloat = true;
            CanPin = true;

            // Initialize network fields
            var settings = storedSettings ?? new AppSettingsData();
            _webSocketHost = settings.WebSocketHost;
            _webSocketPort = settings.WebSocketPort;
            _udpPort = settings.UdpPort;
            _rtpPort = settings.RtpPort;
            _gsiPort = settings.GsiPort;
            _useAltPlayerBinds = settings.UseAltPlayerBinds;
            _mapObjPath = settings.MapObjPath ?? string.Empty;
            _pinScale = (float)settings.PinScale;
            _pinOffsetX = (float)settings.PinOffsetX;
            _pinOffsetY = (float)settings.PinOffsetY;
            _pinOffsetZ = (float)settings.PinOffsetZ;
            _worldScale = (float)settings.WorldScale;
            _worldYaw = (float)settings.WorldYaw;
            _worldPitch = (float)settings.WorldPitch;
            _worldRoll = (float)settings.WorldRoll;
            _worldOffsetX = (float)settings.WorldOffsetX;
            _worldOffsetY = (float)settings.WorldOffsetY;
            _worldOffsetZ = (float)settings.WorldOffsetZ;
            _mapScale = (float)settings.MapScale;
            _mapYaw = (float)settings.MapYaw;
            _mapPitch = (float)settings.MapPitch;
            _mapRoll = (float)settings.MapRoll;
            _mapOffsetX = (float)settings.MapOffsetX;
            _mapOffsetY = (float)settings.MapOffsetY;
            _mapOffsetZ = (float)settings.MapOffsetZ;
            _radarSettings.UseAltPlayerBinds = _useAltPlayerBinds;
            _hudSettings.UseAltPlayerBinds = _useAltPlayerBinds;
            _viewport3DSettings.UseAltPlayerBinds = _useAltPlayerBinds;
            _viewport3DSettings.MapObjPath = _mapObjPath;
            _viewport3DSettings.PinScale = _pinScale;
            _viewport3DSettings.PinOffsetX = _pinOffsetX;
            _viewport3DSettings.PinOffsetY = _pinOffsetY;
            _viewport3DSettings.PinOffsetZ = _pinOffsetZ;
            _viewport3DSettings.WorldScale = _worldScale;
            _viewport3DSettings.WorldYaw = _worldYaw;
            _viewport3DSettings.WorldPitch = _worldPitch;
            _viewport3DSettings.WorldRoll = _worldRoll;
            _viewport3DSettings.WorldOffsetX = _worldOffsetX;
            _viewport3DSettings.WorldOffsetY = _worldOffsetY;
            _viewport3DSettings.WorldOffsetZ = _worldOffsetZ;
            _viewport3DSettings.MapScale = _mapScale;
            _viewport3DSettings.MapYaw = _mapYaw;
            _viewport3DSettings.MapPitch = _mapPitch;
            _viewport3DSettings.MapRoll = _mapRoll;
            _viewport3DSettings.MapOffsetX = _mapOffsetX;
            _viewport3DSettings.MapOffsetY = _mapOffsetY;
            _viewport3DSettings.MapOffsetZ = _mapOffsetZ;

            if (_ws != null)
            {
                _ws.Connected += OnWebSocketConnected;
            }

            LoadAttachPresets();
            SendAltPlayerBindsMode();
        }

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

        public ICommand ApplyNetworkSettingsCommand => new AsyncRelay(ApplyNetworkSettingsInternalAsync);

        private async Task ApplyNetworkSettingsInternalAsync()
        {
            SaveSettings();
            if (_applyNetworkSettingsAsync != null)
            {
                var payload = new NetworkSettingsData(WebSocketHost, WebSocketPort, UdpPort, RtpPort, GsiPort);
                await _applyNetworkSettingsAsync(payload);
            }
        }
        #endregion

        #region ==== 3D Viewport ====

        private string _mapObjPath = string.Empty;
        public string MapObjPath
        {
            get => _mapObjPath;
            set
            {
                if (_mapObjPath != value)
                {
                    _mapObjPath = value ?? string.Empty;
                    _viewport3DSettings.MapObjPath = _mapObjPath;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _pinScale = 1.0f;
        public float PinScale
        {
            get => _pinScale;
            set
            {
                if (Math.Abs(_pinScale - value) > 0.0001f)
                {
                    _pinScale = value;
                    _viewport3DSettings.PinScale = _pinScale;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _pinOffsetX;
        public float PinOffsetX
        {
            get => _pinOffsetX;
            set
            {
                if (Math.Abs(_pinOffsetX - value) > 0.0001f)
                {
                    _pinOffsetX = value;
                    _viewport3DSettings.PinOffsetX = _pinOffsetX;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _pinOffsetY;
        public float PinOffsetY
        {
            get => _pinOffsetY;
            set
            {
                if (Math.Abs(_pinOffsetY - value) > 0.0001f)
                {
                    _pinOffsetY = value;
                    _viewport3DSettings.PinOffsetY = _pinOffsetY;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _pinOffsetZ;
        public float PinOffsetZ
        {
            get => _pinOffsetZ;
            set
            {
                if (Math.Abs(_pinOffsetZ - value) > 0.0001f)
                {
                    _pinOffsetZ = value;
                    _viewport3DSettings.PinOffsetZ = _pinOffsetZ;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _worldScale = 1.0f;
        public float WorldScale
        {
            get => _worldScale;
            set
            {
                if (Math.Abs(_worldScale - value) > 0.0001f)
                {
                    _worldScale = value;
                    _viewport3DSettings.WorldScale = _worldScale;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _worldYaw;
        public float WorldYaw
        {
            get => _worldYaw;
            set
            {
                if (Math.Abs(_worldYaw - value) > 0.0001f)
                {
                    _worldYaw = value;
                    _viewport3DSettings.WorldYaw = _worldYaw;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _worldPitch;
        public float WorldPitch
        {
            get => _worldPitch;
            set
            {
                if (Math.Abs(_worldPitch - value) > 0.0001f)
                {
                    _worldPitch = value;
                    _viewport3DSettings.WorldPitch = _worldPitch;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _worldRoll;
        public float WorldRoll
        {
            get => _worldRoll;
            set
            {
                if (Math.Abs(_worldRoll - value) > 0.0001f)
                {
                    _worldRoll = value;
                    _viewport3DSettings.WorldRoll = _worldRoll;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _worldOffsetX;
        public float WorldOffsetX
        {
            get => _worldOffsetX;
            set
            {
                if (Math.Abs(_worldOffsetX - value) > 0.0001f)
                {
                    _worldOffsetX = value;
                    _viewport3DSettings.WorldOffsetX = _worldOffsetX;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _worldOffsetY;
        public float WorldOffsetY
        {
            get => _worldOffsetY;
            set
            {
                if (Math.Abs(_worldOffsetY - value) > 0.0001f)
                {
                    _worldOffsetY = value;
                    _viewport3DSettings.WorldOffsetY = _worldOffsetY;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _worldOffsetZ;
        public float WorldOffsetZ
        {
            get => _worldOffsetZ;
            set
            {
                if (Math.Abs(_worldOffsetZ - value) > 0.0001f)
                {
                    _worldOffsetZ = value;
                    _viewport3DSettings.WorldOffsetZ = _worldOffsetZ;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapScale = 1.0f;
        public float MapScale
        {
            get => _mapScale;
            set
            {
                if (Math.Abs(_mapScale - value) > 0.0001f)
                {
                    _mapScale = value;
                    _viewport3DSettings.MapScale = _mapScale;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapYaw;
        public float MapYaw
        {
            get => _mapYaw;
            set
            {
                if (Math.Abs(_mapYaw - value) > 0.0001f)
                {
                    _mapYaw = value;
                    _viewport3DSettings.MapYaw = _mapYaw;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapPitch;
        public float MapPitch
        {
            get => _mapPitch;
            set
            {
                if (Math.Abs(_mapPitch - value) > 0.0001f)
                {
                    _mapPitch = value;
                    _viewport3DSettings.MapPitch = _mapPitch;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapRoll;
        public float MapRoll
        {
            get => _mapRoll;
            set
            {
                if (Math.Abs(_mapRoll - value) > 0.0001f)
                {
                    _mapRoll = value;
                    _viewport3DSettings.MapRoll = _mapRoll;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapOffsetX;
        public float MapOffsetX
        {
            get => _mapOffsetX;
            set
            {
                if (Math.Abs(_mapOffsetX - value) > 0.0001f)
                {
                    _mapOffsetX = value;
                    _viewport3DSettings.MapOffsetX = _mapOffsetX;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapOffsetY;
        public float MapOffsetY
        {
            get => _mapOffsetY;
            set
            {
                if (Math.Abs(_mapOffsetY - value) > 0.0001f)
                {
                    _mapOffsetY = value;
                    _viewport3DSettings.MapOffsetY = _mapOffsetY;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapOffsetZ;
        public float MapOffsetZ
        {
            get => _mapOffsetZ;
            set
            {
                if (Math.Abs(_mapOffsetZ - value) > 0.0001f)
                {
                    _mapOffsetZ = value;
                    _viewport3DSettings.MapOffsetZ = _mapOffsetZ;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        public ICommand BrowseMapObjCommand => new AsyncRelay(BrowseMapObjAsync);
        public ICommand ClearMapObjCommand => new Relay(() =>
        {
            MapObjPath = string.Empty;
        });

        private async Task BrowseMapObjAsync()
        {
            var path = await PickObjFileToLoadAsync();
            if (string.IsNullOrWhiteSpace(path))
                return;

            MapObjPath = path;
        }

        private async Task<string?> PickObjFileToLoadAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                return null;

            var window = lifetime.MainWindow;
            if (window is null)
                return null;

            var result = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Load Map OBJ",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Wavefront OBJ")
                        {
                            Patterns = ["*.obj"]
                        }
                    ]
                });

            if (result is { Count: > 0 })
                return result[0].Path.LocalPath;

            return null;
        }

        #endregion

        #region === General Settings ===
        private bool _useAltPlayerBinds;
        public bool UseAltPlayerBinds
        {
            get => _useAltPlayerBinds;
            set
            {
                if (_useAltPlayerBinds != value)
                {
                    _useAltPlayerBinds = value;
                    OnPropertyChanged();

                    _radarSettings.UseAltPlayerBinds = value;
                    _hudSettings.UseAltPlayerBinds = value;
                    _viewport3DSettings.UseAltPlayerBinds = value;
                    SaveSettings();
                    SendAltPlayerBindsMode();
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
                    _ws.SendExecCommandAsync(cmd);
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
                    _ws.SendExecCommandAsync(cmd);
                }
            }
        }
        public ICommand ToggleDemouiCommand => new AsyncRelay(() => _ws.SendExecCommandAsync("demoui"));

        private void OnWebSocketConnected(object? sender, EventArgs e)
        {
            SendAltPlayerBindsMode();
        }

        private void SendAltPlayerBindsMode()
        {
            if (_ws == null) return;
            _ = _ws.SendCommandAsync("spectator_bindings_mode", new { useAlt = _useAltPlayerBinds });
        }
        #endregion

        #region ==== Radar Settings ====

        public double MarkerScale
        {
            get => _radarSettings.MarkerScale;
            set
            {
                if (value < 0.3) value = 0.3;
                if (value > 3.0) value = 3.0;
                if (_radarSettings.MarkerScale != value)
                {
                    _radarSettings.MarkerScale = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        #endregion

        #region ==== HUD ====


        public bool IsHudEnabled
        {
            get => _hudSettings.IsHudEnabled;
            set
            {
                if (_hudSettings.IsHudEnabled != value)
                {
                    _hudSettings.IsHudEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        #region ==== Actions / Attach Presets ====

        public ObservableCollection<AttachPresetViewModel> AttachPresets { get; }
            = new ObservableCollection<AttachPresetViewModel>(
                Enumerable.Range(0, 5).Select(i => new AttachPresetViewModel($"Preset {i + 1}")));

        private void LoadAttachPresets()
        {
            var presets = _hudSettings.AttachPresets;
            for (int i = 0; i < AttachPresets.Count && i < presets.Count; i++)
            {
                AttachPresets[i].LoadFrom(presets[i]);
                AttachPresets[i].PropertyChanged -= OnPresetChanged;
                AttachPresets[i].PropertyChanged += OnPresetChanged;
            }
            SaveSettings();
        }

        private void OnPresetChanged(object? sender, PropertyChangedEventArgs e)
        {
            var vm = sender as AttachPresetViewModel;
            if (vm == null) return;
            var index = AttachPresets.IndexOf(vm);
            if (index < 0 || index >= _hudSettings.AttachPresets.Count) return;
            _hudSettings.AttachPresets[index] = vm.ToModel();
            SaveSettings();
        }

        private void SaveSettings()
        {
            var data = new AppSettingsData
            {
                AttachPresets = _hudSettings.ToAttachPresetData().ToList(),
                MarkerScale = _radarSettings.MarkerScale,
                UseAltPlayerBinds = _useAltPlayerBinds,
                WebSocketHost = WebSocketHost,
                WebSocketPort = WebSocketPort,
                UdpPort = UdpPort,
                RtpPort = RtpPort,
                MapObjPath = _mapObjPath,
                PinScale = _pinScale,
                PinOffsetX = _pinOffsetX,
                PinOffsetY = _pinOffsetY,
                PinOffsetZ = _pinOffsetZ,
                WorldScale = _worldScale,
                WorldYaw = _worldYaw,
                WorldPitch = _worldPitch,
                WorldRoll = _worldRoll,
                WorldOffsetX = _worldOffsetX,
                WorldOffsetY = _worldOffsetY,
                WorldOffsetZ = _worldOffsetZ,
                MapScale = _mapScale,
                MapYaw = _mapYaw,
                MapPitch = _mapPitch,
                MapRoll = _mapRoll,
                MapOffsetX = _mapOffsetX,
                MapOffsetY = _mapOffsetY,
                MapOffsetZ = _mapOffsetZ
            };
            _settingsStorage.Save(data);
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
                    _ws.SendExecCommandAsync(cmd);
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
                    _ws.SendExecCommandAsync(cmd);
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

            var result = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Load Campath",
                    AllowMultiple = false
                });

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

            var result = await window.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Save Campath"
                });

            return result?.Path.LocalPath;
        }

        private class AsyncRelay : ICommand
        {
            private readonly Func<Task> _action;
            public AsyncRelay(Func<Task> action) => _action = action;
            public bool CanExecute(object parameter) => true;
            public async void Execute(object parameter) => await _action();
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }

        // Dummy interpolation state
        private bool _useCubic = true;
        public string InterpLabel => _useCubic ? "Interp: Cubic" : "Interp: Linear";

        public ICommand ToggleInterpModeCommand => new Relay(() =>
        {
            _useCubic = !_useCubic;
            OnPropertyChanged(nameof(InterpLabel));

            var cmd = _useCubic
                ? "mirv_campath edit interp position cubic; mirv_campath edit interp rotation cubic; mirv_campath edit interp fov cubic"
                : "mirv_campath edit interp position linear; mirv_campath edit interp rotation sLinear; mirv_campath edit interp fov linear";
            _ws.SendExecCommandAsync(cmd);
        });

        // Dummy camera path actions
        public ICommand AddPointCommand => new AsyncRelay(() => _ws.SendExecCommandAsync("mirv_campath add"));
        public ICommand ClearCampathCommand => new AsyncRelay(() => _ws.SendExecCommandAsync("mirv_campath clear"));
        public ICommand GotoStartCommand => new AsyncRelay(() => _ws.SendExecCommandAsync("echo \"Implement this\""));
        
        public ICommand LoadCampathCommand => new AsyncRelay(async () =>
        {
            var path = await PickCampathFileToLoadAsync();
            if (string.IsNullOrWhiteSpace(path))
                return; // user cancelled

            // You might want to escape quotes in path if that’s ever an issue
            var cmd = $"mirv_campath load \"{path}\"";
            await _ws.SendExecCommandAsync(cmd);
        });

        public ICommand SaveCampathCommand => new AsyncRelay(async () =>
        {
            var path = await PickCampathFileToSaveAsync();
            if (string.IsNullOrWhiteSpace(path))
                return; // user cancelled

            var cmd = $"mirv_campath save \"{path}\"";
            await _ws.SendExecCommandAsync(cmd);
        });


        #endregion

        #region ==== Freecam Settings ====

        // Helper method to send freecam config updates
        private async Task SendFreecamConfigAsync(object config)
        {
            await _ws.SendCommandAsync("freecam_config", config);
        }

        // Mouse Settings
        public double MouseSensitivity
        {
            get => _freecamSettings.MouseSensitivity;
            set
            {
                if (_freecamSettings.MouseSensitivity != value)
                {
                    _freecamSettings.MouseSensitivity = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { mouseSensitivity = (float)value });
                }
            }
        }

        // Movement Settings
        public double MoveSpeed
        {
            get => _freecamSettings.MoveSpeed;
            set
            {
                if (_freecamSettings.MoveSpeed != value)
                {
                    _freecamSettings.MoveSpeed = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { moveSpeed = (float)value });
                }
            }
        }

        public double SprintMultiplier
        {
            get => _freecamSettings.SprintMultiplier;
            set
            {
                if (_freecamSettings.SprintMultiplier != value)
                {
                    _freecamSettings.SprintMultiplier = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { sprintMultiplier = (float)value });
                }
            }
        }

        public double VerticalSpeed
        {
            get => _freecamSettings.VerticalSpeed;
            set
            {
                if (_freecamSettings.VerticalSpeed != value)
                {
                    _freecamSettings.VerticalSpeed = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { verticalSpeed = (float)value });
                }
            }
        }

        public double SpeedAdjustRate
        {
            get => _freecamSettings.SpeedAdjustRate;
            set
            {
                if (_freecamSettings.SpeedAdjustRate != value)
                {
                    _freecamSettings.SpeedAdjustRate = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { speedAdjustRate = (float)value });
                }
            }
        }

        public double SpeedMinMultiplier
        {
            get => _freecamSettings.SpeedMinMultiplier;
            set
            {
                if (_freecamSettings.SpeedMinMultiplier != value)
                {
                    _freecamSettings.SpeedMinMultiplier = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { speedMinMultiplier = (float)value });
                }
            }
        }

        public double SpeedMaxMultiplier
        {
            get => _freecamSettings.SpeedMaxMultiplier;
            set
            {
                if (_freecamSettings.SpeedMaxMultiplier != value)
                {
                    _freecamSettings.SpeedMaxMultiplier = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { speedMaxMultiplier = (float)value });
                }
            }
        }

        // Roll Settings
        public double RollSpeed
        {
            get => _freecamSettings.RollSpeed;
            set
            {
                if (_freecamSettings.RollSpeed != value)
                {
                    _freecamSettings.RollSpeed = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { rollSpeed = (float)value });
                }
            }
        }

        public double RollSmoothing
        {
            get => _freecamSettings.RollSmoothing;
            set
            {
                if (_freecamSettings.RollSmoothing != value)
                {
                    _freecamSettings.RollSmoothing = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { rollSmoothing = (float)value });
                }
            }
        }

        public double LeanStrength
        {
            get => _freecamSettings.LeanStrength;
            set
            {
                if (_freecamSettings.LeanStrength != value)
                {
                    _freecamSettings.LeanStrength = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { leanStrength = (float)value });
                }
            }
        }

        // FOV Settings
        public double FovMin
        {
            get => _freecamSettings.FovMin;
            set
            {
                if (_freecamSettings.FovMin != value)
                {
                    _freecamSettings.FovMin = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { fovMin = (float)value });
                }
            }
        }

        public double FovMax
        {
            get => _freecamSettings.FovMax;
            set
            {
                if (_freecamSettings.FovMax != value)
                {
                    _freecamSettings.FovMax = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { fovMax = (float)value });
                }
            }
        }

        public double FovStep
        {
            get => _freecamSettings.FovStep;
            set
            {
                if (_freecamSettings.FovStep != value)
                {
                    _freecamSettings.FovStep = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { fovStep = (float)value });
                }
            }
        }

        public double DefaultFov
        {
            get => _freecamSettings.DefaultFov;
            set
            {
                if (_freecamSettings.DefaultFov != value)
                {
                    _freecamSettings.DefaultFov = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { defaultFov = (float)value });
                }
            }
        }

        // Smoothing Settings
        public bool SmoothEnabled
        {
            get => _freecamSettings.SmoothEnabled;
            set
            {
                if (_freecamSettings.SmoothEnabled != value)
                {
                    _freecamSettings.SmoothEnabled = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { smoothEnabled = value });
                }
            }
        }

        public double HalfVec
        {
            get => _freecamSettings.HalfVec;
            set
            {
                if (_freecamSettings.HalfVec != value)
                {
                    _freecamSettings.HalfVec = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { halfVec = (float)value });
                }
            }
        }

        public double HalfRot
        {
            get => _freecamSettings.HalfRot;
            set
            {
                if (_freecamSettings.HalfRot != value)
                {
                    _freecamSettings.HalfRot = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { halfRot = (float)value });
                }
            }
        }

        public double LockHalfRot
        {
            get => _freecamSettings.LockHalfRot;
            set
            {
                if (_freecamSettings.LockHalfRot != value)
                {
                    _freecamSettings.LockHalfRot = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { lockHalfRot = (float)value });
                }
            }
        }

        public double LockHalfRotTransition
        {
            get => _freecamSettings.LockHalfRotTransition;
            set
            {
                if (_freecamSettings.LockHalfRotTransition != value)
                {
                    _freecamSettings.LockHalfRotTransition = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { lockHalfRotTransition = (float)value });
                }
            }
        }

        public double HalfFov
        {
            get => _freecamSettings.HalfFov;
            set
            {
                if (_freecamSettings.HalfFov != value)
                {
                    _freecamSettings.HalfFov = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { halfFov = (float)value });
                }
            }
        }

        public bool HoldMovementFollowsCamera
        {
            get => _freecamSettings.HoldMovementFollowsCamera;
            set
            {
                if (_freecamSettings.HoldMovementFollowsCamera != value)
                {
                    _freecamSettings.HoldMovementFollowsCamera = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        // Simple ICommand helper (no MVVM library required)
        private class Relay : ICommand
        {
            private readonly Action _action;
            public Relay(Action action) => _action = action;
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _action();
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
