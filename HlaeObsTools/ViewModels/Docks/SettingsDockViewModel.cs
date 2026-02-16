using System;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Settings;
using HlaeObsTools.Services.Campaths;
using System.Text.Json;


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
        private readonly CampathEditorViewModel _campathEditor;
        private readonly SettingsStorage _settingsStorage;
        private readonly HlaeWebSocketClient? _ws;
        private readonly Func<NetworkSettingsData, Task>? _applyNetworkSettingsAsync;
        private readonly VmixReplaySettings _vmixReplaySettings;
        private readonly Action<bool>? _setFocusInputGateDisabled;
        private bool _suppressFreecamSave;
        private bool _suppressSettingsSave;
        private bool _isLoadingPresets;

        public record NetworkSettingsData(string WebSocketHost, int WebSocketPort, int UdpPort, int RtpPort, int GsiPort);

        public SettingsDockViewModel(RadarSettings radarSettings, HudSettings hudSettings, FreecamSettings freecamSettings, Viewport3DSettings viewport3DSettings, SettingsStorage settingsStorage, HlaeWebSocketClient wsClient, Func<NetworkSettingsData, Task>? applyNetworkSettingsAsync = null, AppSettingsData? storedSettings = null, VmixReplaySettings? vmixSettings = null, Action<bool>? setFocusInputGateDisabled = null, CampathEditorViewModel? campathEditor = null)
        {
            _radarSettings = radarSettings;
            _hudSettings = hudSettings;
            _freecamSettings = freecamSettings;
            _viewport3DSettings = viewport3DSettings;
            _settingsStorage = settingsStorage;
            _ws = wsClient;
            _applyNetworkSettingsAsync = applyNetworkSettingsAsync;
            _vmixReplaySettings = vmixSettings ?? new VmixReplaySettings();
            _setFocusInputGateDisabled = setFocusInputGateDisabled;
            _campathEditor = campathEditor ?? new CampathEditorViewModel();

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
            _disableFocusInputGate = settings.DisableFocusInputGate;

            if (_ws != null)
            {
                _ws.Connected += OnWebSocketConnected;
                _ws.MessageReceived += OnWebSocketMessage;
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

            LoadAttachPresets();
            SendAltPlayerBindsMode();
            if (_ws?.IsConnected == true)
                _ = SendAllFreecamConfigAsync();
            _radarSettings.PropertyChanged += OnRadarSettingsChanged;
            _hudSettings.PropertyChanged += OnHudSettingsChanged;
            _viewport3DSettings.PropertyChanged += OnViewport3DSettingsChanged;
            _freecamSettings.PropertyChanged += OnFreecamSettingsChanged;
            _vmixReplaySettings.PropertyChanged += OnVmixSettingsChanged;
        }

        public RadarSettings RadarSettings => _radarSettings;
        public HudSettings HudSettings => _hudSettings;
        public FreecamSettings FreecamSettings => _freecamSettings;
        public Viewport3DSettings Viewport3DSettings => _viewport3DSettings;
        public VmixReplaySettings VmixReplaySettings => _vmixReplaySettings;
        public CampathEditorViewModel CampathEditor => _campathEditor;
        public AttachPresetAnimationDockViewModel AttachPresetAnimationEditor { get; }

        private bool _isEditingAttachPresetAnimation;
        public bool IsEditingAttachPresetAnimation
        {
            get => _isEditingAttachPresetAnimation;
            private set => SetProperty(ref _isEditingAttachPresetAnimation, value);
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

        public ICommand BrowseMapObjCommand => new AsyncRelay(BrowseMapObjAsync);
        public ICommand ClearMapObjCommand => new Relay(() =>
        {
            _viewport3DSettings.MapObjPath = string.Empty;
        });

        private async Task BrowseMapObjAsync()
        {
            var path = await PickObjFileToLoadAsync();
            if (string.IsNullOrWhiteSpace(path))
                return;

            _viewport3DSettings.MapObjPath = path;
            Console.WriteLine($"[Viewport3D] Map path set: {path}");
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
                    Title = "Load Map File",
                    AllowMultiple = false,
                    FileTypeFilter = _viewport3DSettings.UseLegacyD3D11Viewport
                        ? new List<FilePickerFileType>
                        {
                            new FilePickerFileType("Wavefront OBJ (legacy)")
                            {
                                Patterns = ["*.obj"]
                            }
                        }
                        : new List<FilePickerFileType>
                        {
                            new FilePickerFileType("Source 2 Map (.vmap_c, .vpk)")
                            {
                                Patterns = ["*.vmap_c", "*.vpk"]
                            }
                        }
                });

            if (result is { Count: > 0 })
                return result[0].Path.LocalPath;

            return null;
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

        public ICommand CycleForceDeathnoticesCommand => new Relay(CycleForceDeathnoticesMode);

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
        public ICommand ToggleDemouiCommand => new AsyncRelay(() => SendExecCommandAsync("demoui"));

        private void OnWebSocketConnected(object? sender, EventArgs e)
        {
            SendAltPlayerBindsMode();
            _ = SendAllFreecamConfigAsync();
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
        #endregion

        #region ==== Actions / Attach Presets ====

        public IReadOnlyList<string> AttachPresetPageOptions { get; } =
            Enumerable.Range(1, 5).Select(i => $"Page {i}").ToList();

        private int _activeAttachPresetPage;
        public int ActiveAttachPresetPage
        {
            get => _activeAttachPresetPage;
            set
            {
                if (_activeAttachPresetPage == value) return;
                _activeAttachPresetPage = Math.Clamp(value, 0, 4);
                _hudSettings.ActiveAttachPresetPage = _activeAttachPresetPage;
                OnPropertyChanged();
                LoadAttachPresets();
                SaveSettings();
            }
        }

        public ObservableCollection<AttachPresetViewModel> AttachPresets { get; }
            = new ObservableCollection<AttachPresetViewModel>(
                Enumerable.Range(0, 5).Select(i => new AttachPresetViewModel($"Preset {i + 1}")));

        public ICommand OpenAttachPresetAnimationCommand { get; }
        public ICommand CloseAttachPresetAnimationCommand { get; }

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
            SaveSettings();
        }

        private void SaveSettings()
        {
            var data = new AppSettingsData
            {
                AttachPresetPages = _hudSettings.ToAttachPresetPageData().ToList(),
                ActiveAttachPresetPage = _hudSettings.ActiveAttachPresetPage,
                RadarScale = _radarSettings.RadarScale,
                MarkerScale = _radarSettings.MarkerScale,
                HeightScaleMultiplier = _radarSettings.HeightScaleMultiplier,
                UseAltPlayerBinds = _radarSettings.UseAltPlayerBinds,
                DisplayNumbersTopmost = _radarSettings.DisplayNumbersTopmost,
                ShowPlayerNames = _radarSettings.ShowPlayerNames,
                WebSocketHost = WebSocketHost,
                WebSocketPort = WebSocketPort,
                UdpPort = UdpPort,
                RtpPort = RtpPort,
                GsiPort = GsiPort,
                MapObjPath = _viewport3DSettings.MapObjPath,
                ViewportUseLegacyD3D11 = _viewport3DSettings.UseLegacyD3D11Viewport,
                PinScale = _viewport3DSettings.PinScale,
                PinOffsetZ = _viewport3DSettings.PinOffsetZ,
                ViewportMouseScale = _viewport3DSettings.ViewportMouseScale,
                MapScale = _viewport3DSettings.MapScale,
                MapYaw = _viewport3DSettings.MapYaw,
                MapPitch = _viewport3DSettings.MapPitch,
                MapRoll = _viewport3DSettings.MapRoll,
                MapOffsetX = _viewport3DSettings.MapOffsetX,
                MapOffsetY = _viewport3DSettings.MapOffsetY,
                MapOffsetZ = _viewport3DSettings.MapOffsetZ,
                ViewportFpsCap = _viewport3DSettings.ViewportFpsCap,
                ViewportPostprocessEnabled = _viewport3DSettings.PostprocessEnabled,
                ViewportColorCorrectionEnabled = _viewport3DSettings.ColorCorrectionEnabled,
                ViewportDynamicShadowsEnabled = _viewport3DSettings.DynamicShadowsEnabled,
                ViewportWireframeEnabled = _viewport3DSettings.WireframeEnabled,
                ViewportSkipWaterEnabled = _viewport3DSettings.SkipWaterEnabled,
                ViewportSkipTranslucentEnabled = _viewport3DSettings.SkipTranslucentEnabled,
                ViewportShowFps = _viewport3DSettings.ShowFps,
                ViewportCampathMode = _viewport3DSettings.ViewportCampathMode,
                ViewportCampathOverlayEnabled = _viewport3DSettings.ViewportCampathOverlayEnabled,
                ViewportCampathSyncEnabled = _viewport3DSettings.ViewportCampathSyncEnabled,
                CampathGizmoLocalSpace = _viewport3DSettings.CampathGizmoLocalSpace,
                ViewportShadowTextureSize = _viewport3DSettings.ShadowTextureSize,
                ViewportMaxTextureSize = _viewport3DSettings.MaxTextureSize,
                ViewportRenderMode = _viewport3DSettings.RenderMode,
                FreecamSettings = _freecamSettings.ToData(),
                VmixReplayEnabled = _vmixReplaySettings.Enabled,
                VmixReplayHost = _vmixReplaySettings.Host,
                VmixReplayPort = _vmixReplaySettings.Port,
                VmixReplayPreSeconds = _vmixReplaySettings.PreSeconds,
                VmixReplayPostSeconds = _vmixReplaySettings.PostSeconds,
                VmixReplayExtendWindowSeconds = _vmixReplaySettings.ExtendWindowSeconds,
                DisableFocusInputGate = _disableFocusInputGate
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
            public bool CanExecute(object? parameter) => true;
            public async void Execute(object? parameter) => await _action();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
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
            SendExecCommand(cmd);
        });

        // Dummy camera path actions
        public ICommand AddPointCommand => new AsyncRelay(() => SendExecCommandAsync("mirv_campath add"));
        public ICommand ClearCampathCommand => new AsyncRelay(() => SendExecCommandAsync("mirv_campath clear"));
        public ICommand GotoStartCommand => new AsyncRelay(() => SendExecCommandAsync("echo \"Implement this\""));
        
        public ICommand LoadCampathCommand => new AsyncRelay(async () =>
        {
            var path = await PickCampathFileToLoadAsync();
            if (string.IsNullOrWhiteSpace(path))
                return; // user cancelled

            // You might want to escape quotes in path if that’s ever an issue
            var cmd = $"mirv_campath load \"{path}\"";
            await SendExecCommandAsync(cmd);
        });

        public ICommand SaveCampathCommand => new AsyncRelay(async () =>
        {
            var path = await PickCampathFileToSaveAsync();
            if (string.IsNullOrWhiteSpace(path))
                return; // user cancelled

            var cmd = $"mirv_campath save \"{path}\"";
            await SendExecCommandAsync(cmd);
        });

        public ICommand LoadViewportCampathCommand => new AsyncRelay(async () =>
        {
            var path = await PickCampathFileToLoadAsync();
            if (string.IsNullOrWhiteSpace(path))
                return;

            var data = CampathFileIo.Load(path);
            if (data == null)
                return;

            _campathEditor.LoadFromData(data);
        });

        public ICommand SaveViewportCampathCommand => new AsyncRelay(async () =>
        {
            var path = await PickCampathFileToSaveAsync();
            if (string.IsNullOrWhiteSpace(path))
                return;

            CampathFileIo.Save(path, _campathEditor);
        });

        private bool _awaitingCurtime;
        public ICommand GetCurrentTimeOffsetCommand => new AsyncRelay(GetCurrentTimeOffsetAsync);

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

            SaveSettings();
        }

        private void OnViewport3DSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressSettingsSave)
                return;

            if (e.PropertyName == nameof(Viewport3DSettings.UseLegacyD3D11Viewport))
            {
                _viewport3DSettings.MapObjPath = string.Empty;
            }

            SaveSettings();
        }

        private void OnVmixSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_suppressSettingsSave)
                return;

            SaveSettings();
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
            }
        }

        public ICommand ResetFreecamSettingsCommand => new Relay(ResetFreecamSettings);

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
                clampPitch = _freecamSettings.ClampPitch
            };

            await _ws.SendCommandAsync("freecam_config", config);
        }

        private Task SendExecCommandAsync(string command)
        {
            if (_ws == null)
                return Task.CompletedTask;

            return _ws.SendExecCommandAsync(command);
        }

        private void SendExecCommand(string command)
        {
            _ = SendExecCommandAsync(command);
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
