using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;


namespace HlaeObsTools.ViewModels.Docks
{
    /// <summary>
    /// Settings dock for configuring UI options like radar markers and camera paths.
    /// </summary>
    public class SettingsDockViewModel : Tool
    {
        private readonly RadarSettings _radarSettings;
        private readonly BrowserSourcesSettings _browserSettings;
        private readonly FreecamSettings _freecamSettings;
        private readonly HlaeWebSocketClient? _ws;

        public SettingsDockViewModel(RadarSettings radarSettings, BrowserSourcesSettings browserSettings, FreecamSettings freecamSettings, HlaeWebSocketClient wsClient)
        {
            _radarSettings = radarSettings;
            _browserSettings = browserSettings;
            _freecamSettings = freecamSettings;
            _ws = wsClient;

            Title = "Settings";
            CanClose = false;
            CanFloat = true;
            CanPin = true;
        }

        #region === General Settings ===
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
                }
            }
        }

        #endregion

        #region ==== Browser / HUD ====

        public string BrowserSourceUrl
        {
            get => _browserSettings.BrowserSourceUrl;
            set
            {
                if (_browserSettings.BrowserSourceUrl != value)
                {
                    _browserSettings.BrowserSourceUrl = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsHudEnabled
        {
            get => _browserSettings.IsHudEnabled;
            set
            {
                if (_browserSettings.IsHudEnabled != value)
                {
                    _browserSettings.IsHudEnabled = value;
                    OnPropertyChanged();
                }
            }
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
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Campath files")
                        {
                            Patterns = new[] { "*.txt", "*.campath", "*.*" }
                        }
                    }
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
                    Title = "Save Campath",
                    SuggestedFileName = "camera_path",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Campath files")
                        {
                            Patterns = new[] { "*.txt", "*.campath" }
                        }
                    }
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
                ? "mirv_campath edit interp position cubic"
                : "mirv_campath edit interp position linear";
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

        public double MouseAcceleration
        {
            get => _freecamSettings.MouseAcceleration;
            set
            {
                if (_freecamSettings.MouseAcceleration != value)
                {
                    _freecamSettings.MouseAcceleration = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { mouseAcceleration = (float)value });
                }
            }
        }

        public double MouseSmoothing
        {
            get => _freecamSettings.MouseSmoothing;
            set
            {
                if (_freecamSettings.MouseSmoothing != value)
                {
                    _freecamSettings.MouseSmoothing = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { mouseSmoothing = (float)value });
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
