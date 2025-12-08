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
        private readonly HlaeWebSocketClient? _ws;

        public SettingsDockViewModel(RadarSettings radarSettings, BrowserSourcesSettings browserSettings, HlaeWebSocketClient wsClient)
        {
            _radarSettings = radarSettings;
            _browserSettings = browserSettings;
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
