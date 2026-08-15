using System;
using System.Collections.ObjectModel;

namespace HlaeObsTools.ViewModels;

public sealed class HotLinkSettings : ViewModelBase
{
    public static ObservableCollection<string> RoleOptions { get; } = new()
    {
        "Off",
        "Publisher",
        "Client"
    };

    public static ObservableCollection<string> ClientModeOptions { get; } = new()
    {
        "Delayed Observer Cues",
        "Replay Director"
    };

    private string _role = "Off";
    private int _publisherPort = 31341;
    private string _publisherIp = "127.0.0.1";
    private bool _manualHost;
    private bool _clientConnectionEnabled;
    private double _preSwitchSeconds = 2.0;
    private double _switchLockSeconds = 0.75;
    private bool _onlyFollowMissedKills;
    private string _delayedVmixChannel = "B";
    private int _delayedVmixCamera = 2;
    private bool _acceptReplayMarkRequests = true;
    private string _clientMode = "Delayed Observer Cues";
    private bool _cueTimelineEnabled = true;
    private bool _cueRadarEnabled = true;
    private bool _cueViewportEnabled = true;
    private bool _cueTimelineAutoRange = true;
    private double _cueTimelineFixedUpcomingSeconds = 15;
    private string _status = "HOT Link disabled.";
    private string _lastKill = "No kill event received.";
    private string _localGameTime = "Local game time unknown.";
    private string _scheduledTarget = "No scheduled target.";
    private string _lastSwitch = "No switch sent.";
    private string _lastVmixMark = "No delayed replay mark.";

    public string Role
    {
        get => _role;
        set
        {
            if (!SetProperty(ref _role, string.IsNullOrWhiteSpace(value) ? "Off" : value))
                return;

            OnPropertyChanged(nameof(IsPublisher));
            OnPropertyChanged(nameof(IsClient));
            OnPropertyChanged(nameof(IsReplayDirectorMode));
            OnPropertyChanged(nameof(IsCueMode));
            OnPropertyChanged(nameof(IsActive));
            if (!IsClient)
                ClientConnectionEnabled = false;
        }
    }

    public int PublisherPort
    {
        get => _publisherPort;
        set => SetProperty(ref _publisherPort, Math.Clamp(value, 1, 65535));
    }

    public string PublisherIp
    {
        get => _publisherIp;
        set => SetProperty(ref _publisherIp, value?.Trim() ?? string.Empty);
    }

    public bool ManualHost
    {
        get => _manualHost;
        set
        {
            if (SetProperty(ref _manualHost, value))
                OnPropertyChanged(nameof(IsDiscoveryHostEnabled));
        }
    }

    /// <summary>Whether this client should actively poll the selected HOT Link publisher.</summary>
    public bool ClientConnectionEnabled
    {
        get => _clientConnectionEnabled;
        set
        {
            if (SetProperty(ref _clientConnectionEnabled, value))
                OnPropertyChanged(nameof(IsClientDisconnected));
        }
    }

    public double PreSwitchSeconds
    {
        get => _preSwitchSeconds;
        set => SetProperty(ref _preSwitchSeconds, Math.Max(0, value));
    }

    public double SwitchLockSeconds
    {
        get => _switchLockSeconds;
        set => SetProperty(ref _switchLockSeconds, Math.Max(0, value));
    }

    /// <summary>
    /// When enabled, the Replay Director client only covers kills the main observer did not catch.
    /// </summary>
    public bool OnlyFollowMissedKills
    {
        get => _onlyFollowMissedKills;
        set => SetProperty(ref _onlyFollowMissedKills, value);
    }

    public string DelayedVmixChannel
    {
        get => _delayedVmixChannel;
        set => SetProperty(ref _delayedVmixChannel, (value ?? string.Empty).Trim());
    }

    public int DelayedVmixCamera
    {
        get => _delayedVmixCamera;
        set => SetProperty(ref _delayedVmixCamera, Math.Clamp(value, 1, 8));
    }

    public bool AcceptReplayMarkRequests
    {
        get => _acceptReplayMarkRequests;
        set => SetProperty(ref _acceptReplayMarkRequests, value);
    }

    public string ClientMode
    {
        get => _clientMode;
        set
        {
            if (!SetProperty(ref _clientMode, string.Equals(value, "Replay Director", StringComparison.Ordinal) ? "Replay Director" : "Delayed Observer Cues"))
                return;
            OnPropertyChanged(nameof(IsReplayDirectorMode));
            OnPropertyChanged(nameof(IsCueMode));
        }
    }

    public bool CueTimelineEnabled { get => _cueTimelineEnabled; set => SetProperty(ref _cueTimelineEnabled, value); }
    public bool CueRadarEnabled { get => _cueRadarEnabled; set => SetProperty(ref _cueRadarEnabled, value); }
    public bool CueViewportEnabled { get => _cueViewportEnabled; set => SetProperty(ref _cueViewportEnabled, value); }
    public bool CueTimelineAutoRange { get => _cueTimelineAutoRange; set => SetProperty(ref _cueTimelineAutoRange, value); }
    public double CueTimelineFixedUpcomingSeconds
    {
        get => _cueTimelineFixedUpcomingSeconds;
        set => SetProperty(ref _cueTimelineFixedUpcomingSeconds, Math.Clamp(value, 1, 300));
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value ?? string.Empty);
    }

    public string LastKill
    {
        get => _lastKill;
        set => SetProperty(ref _lastKill, value ?? string.Empty);
    }

    public string LocalGameTime
    {
        get => _localGameTime;
        set => SetProperty(ref _localGameTime, value ?? string.Empty);
    }

    public string ScheduledTarget
    {
        get => _scheduledTarget;
        set => SetProperty(ref _scheduledTarget, value ?? string.Empty);
    }

    public string LastSwitch
    {
        get => _lastSwitch;
        set => SetProperty(ref _lastSwitch, value ?? string.Empty);
    }

    public string LastVmixMark
    {
        get => _lastVmixMark;
        set => SetProperty(ref _lastVmixMark, value ?? string.Empty);
    }

    public bool IsPublisher => string.Equals(Role, "Publisher", StringComparison.Ordinal);

    public bool IsClient => string.Equals(Role, "Client", StringComparison.Ordinal);

    public bool IsReplayDirectorMode => IsClient && string.Equals(ClientMode, "Replay Director", StringComparison.Ordinal);

    public bool IsCueMode => IsClient && string.Equals(ClientMode, "Delayed Observer Cues", StringComparison.Ordinal);

    public bool IsDiscoveryHostEnabled => !ManualHost;

    public bool IsClientDisconnected => !ClientConnectionEnabled;

    public bool IsActive => IsPublisher || IsClient;
}
