using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.ReplayDirector;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.Vmix;

public sealed class VmixReplayMarker : IDisposable
{
    private const string FunctionMark = "ReplayMarkInOutLive";
    private const string FunctionSelectLast = "ReplaySelectLastEvent";
    private const string FunctionMoveOut = "ReplayMoveSelectedOutPoint";
    private const string FunctionSetTextCamera = "ReplaySetLastEventTextCamera";
    private const string FunctionLastEventSingleCameraOn = "ReplayLastEventSingleCameraOn";
    private const string FunctionSelectChannelA = "ReplaySelectChannelA";
    private const string FunctionSelectChannelAB = "ReplaySelectChannelAB";
    private const string FunctionSelectChannelB = "ReplaySelectChannelB";

    private readonly VmixApiClient _vmixApiClient;
    private readonly VmixReplayCoordinator _replayCoordinator;
    private readonly object _sync = new();
    private readonly Dictionary<(int Round, string Player), int> _roundKillCounts = new();
    private readonly List<EventKill> _eventKills = new();
    private DateTimeOffset? _firstKillTime;
    private DateTimeOffset? _lastKillTime;
    private DateTimeOffset? _lastAppliedKillTime;
    private bool _markCreated;
    private long? _activeReplayRecordId;
    private int _roundNumber;
    private int _labelRoundNumber;
    private CancellationTokenSource? _markCts;
    private CancellationTokenSource? _extendCts;
    private bool _disposed;

    private readonly record struct EventKill(string PlayerName, int RoundKillNumber);

    public VmixReplayMarker(VmixApiClient vmixApiClient, VmixReplayCoordinator replayCoordinator)
    {
        _vmixApiClient = vmixApiClient;
        _replayCoordinator = replayCoordinator;
    }

    public event EventHandler<string>? StatusChanged;

    public void RecordKill(ReplayDirectorKillEvent kill, VmixReplaySettings replaySettings, ReplayDirectorSettings directorSettings)
    {
        if (!directorSettings.DelayedVmixEnabled)
            return;

        var config = new MarkerConfig(
            Math.Max(0, replaySettings.PreSeconds),
            Math.Max(0, replaySettings.PostSeconds),
            Math.Max(0, replaySettings.ExtendWindowSeconds),
            Math.Clamp(replaySettings.FramesPerSecond, 1.0, 240.0),
            string.IsNullOrWhiteSpace(directorSettings.DelayedVmixChannel) ? null : directorSettings.DelayedVmixChannel.Trim(),
            Math.Clamp(directorSettings.DelayedVmixCamera, 1, 8));

        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var labelRound = GetLabelRound(kill);
            var roundKillNumber = GetRoundKillNumber(kill, labelRound);

            if (_firstKillTime == null || !_markCreated || _roundNumber != labelRound)
            {
                Reset(now, labelRound);
                AddKill(kill, roundKillNumber);
                ScheduleMark(config);
                return;
            }

            if (_lastKillTime.HasValue && (now - _lastKillTime.Value).TotalSeconds <= config.ExtendWindowSeconds)
            {
                _lastKillTime = now;
                AddKill(kill, roundKillNumber);
                ScheduleExtend(config);
            }
            else
            {
                Reset(now, labelRound);
                AddKill(kill, roundKillNumber);
                ScheduleMark(config);
            }
        }
    }

    private int GetLabelRound(ReplayDirectorKillEvent kill)
    {
        if (kill.LabelRoundNumber > 0)
            return kill.LabelRoundNumber;

        var phase = (kill.RoundPhase ?? string.Empty).ToUpperInvariant();
        var roundNumber = kill.RoundNumber;

        if (!string.Equals(phase, "OVER", StringComparison.Ordinal))
        {
            if (roundNumber > 0)
                _labelRoundNumber = roundNumber;
        }
        else if (_labelRoundNumber == 0 && roundNumber > 0)
        {
            _labelRoundNumber = Math.Max(1, roundNumber - 1);
        }

        var labelRound = _labelRoundNumber > 0 ? _labelRoundNumber : roundNumber;
        CleanupOldRoundKillCounts(labelRound);
        return labelRound > 0 ? labelRound : roundNumber;
    }

    private void CleanupOldRoundKillCounts(int labelRound)
    {
        if (labelRound <= 0)
            return;

        var keysToRemove = new List<(int Round, string Player)>();
        foreach (var key in _roundKillCounts.Keys)
        {
            if (key.Round < labelRound - 2)
                keysToRemove.Add(key);
        }

        foreach (var key in keysToRemove)
        {
            _roundKillCounts.Remove(key);
        }
    }

    private void Reset(DateTimeOffset time, int roundNumber)
    {
        _markCts?.Cancel();
        _extendCts?.Cancel();
        _firstKillTime = time;
        _lastKillTime = time;
        _lastAppliedKillTime = null;
        _roundNumber = roundNumber;
        _eventKills.Clear();
        _markCreated = false;
        _activeReplayRecordId = null;
    }

    private void AddKill(ReplayDirectorKillEvent kill, int roundKillNumber)
    {
        _eventKills.Add(new EventKill(kill.AttackerName, roundKillNumber));
        _lastKillTime = DateTimeOffset.UtcNow;
    }

    private int GetNextRoundKill(string playerName, int roundNumber)
    {
        var key = (roundNumber, playerName);
        _roundKillCounts.TryGetValue(key, out var count);
        count++;
        _roundKillCounts[key] = count;
        return count;
    }

    private int GetRoundKillNumber(ReplayDirectorKillEvent kill, int roundNumber)
    {
        if (kill.RoundKillNumber > 0)
            return kill.RoundKillNumber;

        return GetNextRoundKill(kill.AttackerName, roundNumber);
    }

    private void ScheduleMark(MarkerConfig config)
    {
        _markCts?.Cancel();
        _markCts = new CancellationTokenSource();
        var cts = _markCts;
        var delay = (_lastKillTime ?? DateTimeOffset.UtcNow) + TimeSpan.FromSeconds(config.PostSeconds) - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await SendMarkAsync(config, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private async Task SendMarkAsync(MarkerConfig config, CancellationToken token)
    {
        await _replayCoordinator.RunAsync(async lockedToken =>
        {
            DateTimeOffset firstKill;
            DateTimeOffset lastKill;
            lock (_sync)
            {
                firstKill = _firstKillTime ?? DateTimeOffset.UtcNow;
                lastKill = _lastKillTime ?? firstKill;
            }

            var valueSeconds = Math.Ceiling((lastKill - firstKill).TotalSeconds + config.PreSeconds + config.PostSeconds);
            if (!await SelectReplayChannelAsync(config, lockedToken).ConfigureAwait(false) ||
                !await ExecuteAsync(FunctionMark, valueSeconds.ToString(CultureInfo.InvariantCulture), config, lockedToken).ConfigureAwait(false) ||
                !await ExecuteAsync(FunctionSelectLast, null, config, lockedToken).ConfigureAwait(false) ||
                !await ApplyEventCameraAsync(config, lockedToken).ConfigureAwait(false))
            {
                StatusChanged?.Invoke(this, "Delayed replay marker failed: vMix API command failed.");
                return new VmixReplayCommandResult(false, null, "vMix API command failed");
            }

            string? label;
            int roundNumber;
            int killCount;
            lock (_sync)
            {
                label = BuildLabel();
                roundNumber = _roundNumber;
                killCount = _eventKills.Count;
            }

            if (!await ApplyLabelCoreAsync(config, lockedToken).ConfigureAwait(false))
            {
                StatusChanged?.Invoke(this, "Delayed replay marker failed: vMix label command failed.");
                return new VmixReplayCommandResult(false, label, "vMix label command failed");
            }
            lockedToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(label))
            {
                var record = await _replayCoordinator.RegisterCreatedEventAsync(new ReplayEventDraft
                {
                    Source = "Delayed",
                    Label = label,
                    Channel = config.Channel ?? "B",
                    Camera = config.Camera,
                    Round = roundNumber,
                    KillCount = killCount,
                    Status = "Marked"
                }, lockedToken).ConfigureAwait(false);

                lock (_sync)
                {
                    _activeReplayRecordId = record.LocalId;
                }
            }

            lock (_sync)
            {
                _markCreated = true;
                _markCts = null;
                _lastAppliedKillTime = lastKill;
            }

            StatusChanged?.Invoke(this, $"Delayed replay marked: {BuildLabel() ?? "unlabeled"}");
            return new VmixReplayCommandResult(true, label, "Marked");
        }, token).ConfigureAwait(false);
    }

    private void ScheduleExtend(MarkerConfig config)
    {
        _extendCts?.Cancel();
        _extendCts = new CancellationTokenSource();
        var cts = _extendCts;
        var delay = (_lastKillTime ?? DateTimeOffset.UtcNow) + TimeSpan.FromSeconds(config.PostSeconds) - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await _replayCoordinator.RunAsync(async lockedToken =>
                {
                    if (!await SelectReplayChannelAsync(config, lockedToken).ConfigureAwait(false) ||
                        !await ExecuteAsync(FunctionSelectLast, null, config, lockedToken).ConfigureAwait(false))
                        return new VmixReplayCommandResult(false, null, "vMix API command failed");

                    var extension = GetFrameExtension(config.FramesPerSecond);
                    if (extension.Frames <= 0)
                        return new VmixReplayCommandResult(true, BuildLabel(), "No extension needed");

                    if (!await ExecuteReplayChannelAsync(FunctionMoveOut, extension.Frames, config, lockedToken).ConfigureAwait(false) ||
                        !await ApplyEventCameraAsync(config, lockedToken).ConfigureAwait(false) ||
                        !await ApplyLabelCoreAsync(config, lockedToken).ConfigureAwait(false))
                        return new VmixReplayCommandResult(false, null, "vMix API command failed");
                    lock (_sync)
                    {
                        _lastAppliedKillTime = extension.AppliedThrough;
                    }
                    UpdateRegistryRecord("Updated");
                    StatusChanged?.Invoke(this, $"Delayed replay extended: {BuildLabel() ?? "unlabeled"}");
                    return new VmixReplayCommandResult(true, BuildLabel(), "Updated");
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private async Task<bool> ApplyLabelCoreAsync(MarkerConfig config, CancellationToken token)
    {
        var label = BuildLabel();
        if (string.IsNullOrWhiteSpace(label))
            return true;

        if (!await SelectReplayChannelAsync(config, token).ConfigureAwait(false) ||
            !await ExecuteAsync(FunctionSelectLast, null, config, token).ConfigureAwait(false))
            return false;
        var value = $"{config.Camera.ToString(CultureInfo.InvariantCulture)},{label}";
        return await ExecuteAsync(FunctionSetTextCamera, value, config, token).ConfigureAwait(false);
    }

    private void UpdateRegistryRecord(string status)
    {
        string? label;
        int roundNumber;
        int killCount;
        long? localId;
        lock (_sync)
        {
            label = BuildLabel();
            roundNumber = _roundNumber;
            killCount = _eventKills.Count;
            localId = _activeReplayRecordId;
        }

        if (!localId.HasValue)
            return;

        _replayCoordinator.Registry.Update(localId.Value, record =>
        {
            if (!string.IsNullOrWhiteSpace(label))
                record.Label = label;
            record.Round = roundNumber;
            record.KillCount = killCount;
            record.Status = status;
        });
    }

    private string? BuildLabel()
    {
        lock (_sync)
        {
            if (_roundNumber <= 0 || _eventKills.Count == 0)
                return null;

            var parts = new List<string>();
            var order = new List<string>();
            var byPlayer = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var kill in _eventKills)
            {
                if (!byPlayer.TryGetValue(kill.PlayerName, out var list))
                {
                    list = new List<int>();
                    byPlayer[kill.PlayerName] = list;
                    order.Add(kill.PlayerName);
                }
                list.Add(kill.RoundKillNumber);
            }

            foreach (var player in order)
            {
                var nums = byPlayer[player];
                nums.Sort();
                var cleanPlayer = SanitizeLabelPart(player);
                string segment = nums.Count == 1
                    ? $"{cleanPlayer}_K{nums[0]}"
                    : $"{cleanPlayer}_K{nums[0]}-{nums[^1]}";
                parts.Add(segment);
            }

            return $"R{_roundNumber}_{string.Join("_", parts)}";
        }
    }

    private Task<bool> ExecuteAsync(string function, string? value, MarkerConfig config, CancellationToken token)
    {
        return _vmixApiClient.ExecuteFunctionAsync(new VmixFunctionCall
        {
            Function = function,
            Value = value
        }, token, function);
    }

    private Task<bool> ExecuteReplayChannelAsync(string function, int framesToMove, MarkerConfig config, CancellationToken token)
    {
        var channel = string.IsNullOrWhiteSpace(config.Channel) ? "B" : config.Channel.Trim().ToUpperInvariant();
        return _vmixApiClient.ExecuteFunctionAsync(new VmixFunctionCall
        {
            Function = function,
            Value = framesToMove.ToString(CultureInfo.InvariantCulture),
            Channel = channel
        }, token, function);
    }

    private (int Frames, DateTimeOffset AppliedThrough) GetFrameExtension(double framesPerSecond)
    {
        lock (_sync)
        {
            if (!_lastAppliedKillTime.HasValue || !_lastKillTime.HasValue)
                return (0, default);

            var secondsToExtend = (_lastKillTime.Value - _lastAppliedKillTime.Value).TotalSeconds;
            if (secondsToExtend <= 0)
                return (0, _lastKillTime.Value);

            var frames = Math.Round(secondsToExtend * framesPerSecond, MidpointRounding.AwayFromZero);
            var frameCount = frames >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)frames);
            return (frameCount, _lastKillTime.Value);
        }
    }

    private Task<bool> ApplyEventCameraAsync(MarkerConfig config, CancellationToken token)
    {
        return ExecuteAsync(FunctionLastEventSingleCameraOn, config.Camera.ToString(CultureInfo.InvariantCulture), config, token);
    }

    private Task<bool> SelectReplayChannelAsync(MarkerConfig config, CancellationToken token)
    {
        var channel = string.IsNullOrWhiteSpace(config.Channel) ? "B" : config.Channel.Trim().ToUpperInvariant();
        var function = channel switch
        {
            "A" => FunctionSelectChannelA,
            "AB" => FunctionSelectChannelAB,
            _ => FunctionSelectChannelB
        };

        return _vmixApiClient.ExecuteFunctionAsync(new VmixFunctionCall { Function = function }, token, function);
    }

    private static string SanitizeLabelPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = new List<char>(value.Length);
        foreach (var c in value)
        {
            chars.Add(char.IsLetterOrDigit(c) ? c : '_');
        }
        return new string(chars.ToArray()).Trim('_');
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _markCts?.Cancel();
        _extendCts?.Cancel();
    }

    private readonly record struct MarkerConfig(double PreSeconds, double PostSeconds, double ExtendWindowSeconds, double FramesPerSecond, string? Channel, int Camera);
}
