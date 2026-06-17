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
    private string _followerEndpoint = "http://127.0.0.1:31341/replay-director/events";
    private double _preSwitchSeconds = 2.0;
    private double _mergeWindowSeconds = 3.0;
    private double _switchLockSeconds = 0.75;
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
        set => SetProperty(ref _role, string.IsNullOrWhiteSpace(value) ? "Off" : value);
    }

    public int PublisherPort
    {
        get => _publisherPort;
        set => SetProperty(ref _publisherPort, Math.Clamp(value, 1, 65535));
    }

    public string FollowerEndpoint
    {
        get => _followerEndpoint;
        set => SetProperty(ref _followerEndpoint, value ?? string.Empty);
    }

    public double PreSwitchSeconds
    {
        get => _preSwitchSeconds;
        set => SetProperty(ref _preSwitchSeconds, Math.Max(0, value));
    }

    public double MergeWindowSeconds
    {
        get => _mergeWindowSeconds;
        set => SetProperty(ref _mergeWindowSeconds, Math.Max(0, value));
    }

    public double SwitchLockSeconds
    {
        get => _switchLockSeconds;
        set => SetProperty(ref _switchLockSeconds, Math.Max(0, value));
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
}
