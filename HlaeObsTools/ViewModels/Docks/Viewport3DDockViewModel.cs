using System.ComponentModel;
using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Gsi;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class Viewport3DDockViewModel : Tool, IDisposable
{
    private readonly Viewport3DSettings _settings;
    private readonly FreecamSettings _freecamSettings;
    private readonly GsiServer? _gsiServer;
    private long _lastHeartbeat;

    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };

    public event Action<IReadOnlyList<ViewportPin>>? PinsUpdated;

    public Viewport3DDockViewModel(Viewport3DSettings settings, FreecamSettings freecamSettings, GsiServer? gsiServer = null)
    {
        _settings = settings;
        _freecamSettings = freecamSettings;
        _gsiServer = gsiServer;
        _settings.PropertyChanged += OnSettingsChanged;
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated += OnGameStateUpdated;

        Title = "3D Viewport";
        CanFloat = true;
        CanPin = true;
    }

    public string MapObjPath
    {
        get => _settings.MapObjPath;
        set
        {
            if (_settings.MapObjPath != value)
            {
                _settings.MapObjPath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public FreecamSettings FreecamSettings => _freecamSettings;

    public float PinScale
    {
        get => _settings.PinScale;
        set
        {
            if (Math.Abs(_settings.PinScale - value) > 0.0001f)
            {
                _settings.PinScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldScale
    {
        get => _settings.WorldScale;
        set
        {
            if (Math.Abs(_settings.WorldScale - value) > 0.0001f)
            {
                _settings.WorldScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldYaw
    {
        get => _settings.WorldYaw;
        set
        {
            if (Math.Abs(_settings.WorldYaw - value) > 0.0001f)
            {
                _settings.WorldYaw = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldPitch
    {
        get => _settings.WorldPitch;
        set
        {
            if (Math.Abs(_settings.WorldPitch - value) > 0.0001f)
            {
                _settings.WorldPitch = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldRoll
    {
        get => _settings.WorldRoll;
        set
        {
            if (Math.Abs(_settings.WorldRoll - value) > 0.0001f)
            {
                _settings.WorldRoll = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldOffsetX
    {
        get => _settings.WorldOffsetX;
        set
        {
            if (Math.Abs(_settings.WorldOffsetX - value) > 0.0001f)
            {
                _settings.WorldOffsetX = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldOffsetY
    {
        get => _settings.WorldOffsetY;
        set
        {
            if (Math.Abs(_settings.WorldOffsetY - value) > 0.0001f)
            {
                _settings.WorldOffsetY = value;
                OnPropertyChanged();
            }
        }
    }

    public float WorldOffsetZ
    {
        get => _settings.WorldOffsetZ;
        set
        {
            if (Math.Abs(_settings.WorldOffsetZ - value) > 0.0001f)
            {
                _settings.WorldOffsetZ = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapScale
    {
        get => _settings.MapScale;
        set
        {
            if (Math.Abs(_settings.MapScale - value) > 0.0001f)
            {
                _settings.MapScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapYaw
    {
        get => _settings.MapYaw;
        set
        {
            if (Math.Abs(_settings.MapYaw - value) > 0.0001f)
            {
                _settings.MapYaw = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapPitch
    {
        get => _settings.MapPitch;
        set
        {
            if (Math.Abs(_settings.MapPitch - value) > 0.0001f)
            {
                _settings.MapPitch = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapRoll
    {
        get => _settings.MapRoll;
        set
        {
            if (Math.Abs(_settings.MapRoll - value) > 0.0001f)
            {
                _settings.MapRoll = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetX
    {
        get => _settings.MapOffsetX;
        set
        {
            if (Math.Abs(_settings.MapOffsetX - value) > 0.0001f)
            {
                _settings.MapOffsetX = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetY
    {
        get => _settings.MapOffsetY;
        set
        {
            if (Math.Abs(_settings.MapOffsetY - value) > 0.0001f)
            {
                _settings.MapOffsetY = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetZ
    {
        get => _settings.MapOffsetZ;
        set
        {
            if (Math.Abs(_settings.MapOffsetZ - value) > 0.0001f)
            {
                _settings.MapOffsetZ = value;
                OnPropertyChanged();
            }
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.MapObjPath))
            OnPropertyChanged(nameof(MapObjPath));
        else if (e.PropertyName == nameof(Viewport3DSettings.PinScale))
            OnPropertyChanged(nameof(PinScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldScale))
            OnPropertyChanged(nameof(WorldScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldYaw))
            OnPropertyChanged(nameof(WorldYaw));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldPitch))
            OnPropertyChanged(nameof(WorldPitch));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldRoll))
            OnPropertyChanged(nameof(WorldRoll));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldOffsetX))
            OnPropertyChanged(nameof(WorldOffsetX));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldOffsetY))
            OnPropertyChanged(nameof(WorldOffsetY));
        else if (e.PropertyName == nameof(Viewport3DSettings.WorldOffsetZ))
            OnPropertyChanged(nameof(WorldOffsetZ));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapScale))
            OnPropertyChanged(nameof(MapScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapYaw))
            OnPropertyChanged(nameof(MapYaw));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapPitch))
            OnPropertyChanged(nameof(MapPitch));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapRoll))
            OnPropertyChanged(nameof(MapRoll));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetX))
            OnPropertyChanged(nameof(MapOffsetX));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetY))
            OnPropertyChanged(nameof(MapOffsetY));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetZ))
            OnPropertyChanged(nameof(MapOffsetZ));
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated -= OnGameStateUpdated;
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        if (state.Heartbeat == _lastHeartbeat)
            return;
        _lastHeartbeat = state.Heartbeat;

        var pins = new List<ViewportPin>();
        foreach (var p in state.Players)
        {
            if (p == null || !p.IsAlive)
                continue;

            var label = GetSlotLabel(p.Slot, _settings.UseAltPlayerBinds);
            pins.Add(new ViewportPin
            {
                Position = p.Position,
                Forward = p.Forward,
                Team = p.Team,
                Slot = p.Slot,
                Label = label,
                IsAlive = p.IsAlive
            });
        }

        Dispatcher.UIThread.Post(() => PinsUpdated?.Invoke(pins));
    }

    private static string GetSlotLabel(int slot, bool useAlt)
    {
        if (slot < 0 || slot > 9)
            return string.Empty;

        if (useAlt && slot >= 5)
            return AltBindLabels[slot - 5];

        return ((slot + 1) % 10).ToString();
    }
}
