using System;
using System.Collections.ObjectModel;

namespace HlaeObsTools.ViewModels;

public sealed class ReplayDirectorSettings : ViewModelBase
{
    public static ObservableCollection<string> RoleOptions { get; } = new()
    {
        "Off",
        "Main Publisher",
        "Delayed Follower"
    };

    private string _role = "Off";
    private int _publisherPort = 31341;
    private string _publisherIp = "127.0.0.1";
    private bool _manualHost;
    private bool _followerConnectionEnabled;
    private double _preSwitchSeconds = 2.0;
    private double _switchLockSeconds = 0.75;
    private bool _onlyFollowMissedKills;
    private string _delayedVmixChannel = "B";
    private int _delayedVmixCamera = 2;
    private bool _delayedVmixEnabled = true;
    private string _status = "Replay director disabled.";
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
            OnPropertyChanged(nameof(IsFollower));
            OnPropertyChanged(nameof(IsActive));
            if (!IsFollower)
                FollowerConnectionEnabled = false;
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

    /// <summary>Whether the follower should actively poll and schedule replay events.</summary>
    public bool FollowerConnectionEnabled
    {
        get => _followerConnectionEnabled;
        set
        {
            if (SetProperty(ref _followerConnectionEnabled, value))
                OnPropertyChanged(nameof(IsFollowerDisconnected));
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
    /// When enabled, the delayed follower only covers kills the main observer did not catch.
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

    public bool DelayedVmixEnabled
    {
        get => _delayedVmixEnabled;
        set => SetProperty(ref _delayedVmixEnabled, value);
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

    public bool IsPublisher => string.Equals(Role, "Main Publisher", StringComparison.Ordinal);

    public bool IsFollower => string.Equals(Role, "Delayed Follower", StringComparison.Ordinal);

    public bool IsDiscoveryHostEnabled => !ManualHost;

    public bool IsFollowerDisconnected => !FollowerConnectionEnabled;

    public bool IsActive => IsPublisher || IsFollower;
}
