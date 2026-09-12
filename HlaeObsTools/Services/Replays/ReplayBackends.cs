using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.ObsReplays;
using HlaeObsTools.Services.Vmix;

namespace HlaeObsTools.Services.Replays;

public readonly record struct ReplayBackendOptions(string Source, string Channel, int Camera, double FramesPerSecond);
public readonly record struct ReplayCreateRequest(int DurationMs, string Label, int Round, int KillCount, ReplayBackendOptions Options);
public readonly record struct ReplayExtendRequest(int ExtensionMs, string Label, int KillCount, ReplayBackendOptions Options);
public readonly record struct ReplayEventHandle(string Id, int StartMs = 0);

/// <summary>Transport boundary: replay policy supplies complete event requests; backends only execute them.</summary>
public interface IReplayBackend
{
    bool IsEnabled { get; }
    bool IsAvailable { get; }
    Task<ReplayEventHandle?> CreateAsync(ReplayCreateRequest request, CancellationToken token);
    Task<bool> ExtendAsync(ReplayEventHandle handle, ReplayExtendRequest request, CancellationToken token);
}

public sealed class ObsReplaysBackend : IReplayBackend
{
    private readonly ObsReplayEventWriter _writer;
    public ObsReplaysBackend(ObsReplayEventWriter writer) => _writer = writer;
    public bool IsEnabled => true;
    public bool IsAvailable => _writer.IsConnected;

    public async Task<ReplayEventHandle?> CreateAsync(ReplayCreateRequest request, CancellationToken token)
    {
        var created = await _writer.CreateFromLiveAsync(request.DurationMs, request.Label, request.Options.Source, request.Round, request.KillCount, token).ConfigureAwait(false);
        return created.HasValue ? new ReplayEventHandle(created.Value.Id, created.Value.InMs) : null;
    }

    public async Task<bool> ExtendAsync(ReplayEventHandle handle, ReplayExtendRequest request, CancellationToken token)
    {
        var outMs = await _writer.GetRecordedThroughAsync(token).ConfigureAwait(false);
        return outMs.HasValue && await _writer.UpdateAsync(new ObsReplayEventHandle(handle.Id, handle.StartMs), outMs.Value, request.Label, request.KillCount, token).ConfigureAwait(false);
    }
}

public sealed class VmixReplayBackend : IReplayBackend
{
    private const string Mark = "ReplayMarkInOutLive";
    private const string SelectLast = "ReplaySelectLastEvent";
    private const string MoveOut = "ReplayMoveSelectedOutPoint";
    private const string SetText = "ReplaySetLastEventTextCamera";
    private const string SetCamera = "ReplayLastEventSingleCameraOn";
    private readonly VmixApiClient _api;
    private readonly VmixReplayCoordinator _coordinator;

    public VmixReplayBackend(VmixApiClient api, VmixReplayCoordinator coordinator) { _api = api; _coordinator = coordinator; }
    public bool IsEnabled => true;
    public bool IsAvailable => true;

    public async Task<ReplayEventHandle?> CreateAsync(ReplayCreateRequest request, CancellationToken token)
    {
        var result = await _coordinator.RunAsync(async locked =>
        {
        if (!await SelectChannelAsync(request.Options.Channel, locked).ConfigureAwait(false) ||
            !await ExecuteAsync(Mark, Math.Ceiling(request.DurationMs / 1000d).ToString(CultureInfo.InvariantCulture), null, locked).ConfigureAwait(false) ||
            !await ExecuteAsync(SelectLast, null, null, locked).ConfigureAwait(false) ||
            !await ApplyEventDetailsAsync(request.Label, request.Options, locked).ConfigureAwait(false))
            return new VmixReplayCommandResult(false, null);
        var record = await _coordinator.RegisterCreatedEventAsync(new ReplayEventDraft
        {
            Source = request.Options.Source, Label = request.Label, Channel = request.Options.Channel, Camera = request.Options.Camera,
            Round = request.Round, KillCount = request.KillCount, Status = "Marked"
        }, locked).ConfigureAwait(false);
        return new VmixReplayCommandResult(true, record.LocalId.ToString(CultureInfo.InvariantCulture));
        }, token).ConfigureAwait(false);
        return result.Success && !string.IsNullOrWhiteSpace(result.Label) ? new ReplayEventHandle(result.Label) : null;
    }

    public async Task<bool> ExtendAsync(ReplayEventHandle handle, ReplayExtendRequest request, CancellationToken token)
    {
        var result = await _coordinator.RunAsync(async locked =>
        {
        var frames = Math.Max(1, (int)Math.Round(request.ExtensionMs / 1000d * request.Options.FramesPerSecond, MidpointRounding.AwayFromZero));
        if (!await SelectChannelAsync(request.Options.Channel, locked).ConfigureAwait(false) ||
            !await ExecuteAsync(SelectLast, null, null, locked).ConfigureAwait(false) ||
            !await ExecuteAsync(MoveOut, frames.ToString(CultureInfo.InvariantCulture), request.Options.Channel, locked).ConfigureAwait(false) ||
            !await ApplyEventDetailsAsync(request.Label, request.Options, locked).ConfigureAwait(false))
            return new VmixReplayCommandResult(false, null);
        if (long.TryParse(handle.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var localId))
            _coordinator.Registry.Update(localId, record => { record.Label = request.Label; record.KillCount = request.KillCount; record.Status = "Updated"; });
        return new VmixReplayCommandResult(true, request.Label);
        }, token).ConfigureAwait(false);
        return result.Success;
    }

    private async Task<bool> ApplyEventDetailsAsync(string label, ReplayBackendOptions options, CancellationToken token) =>
        await ExecuteAsync(SetCamera, options.Camera.ToString(CultureInfo.InvariantCulture), null, token).ConfigureAwait(false) &&
        await ExecuteAsync(SetText, $"{options.Camera.ToString(CultureInfo.InvariantCulture)},{label}", null, token).ConfigureAwait(false);

    private Task<bool> SelectChannelAsync(string channel, CancellationToken token) => ExecuteAsync(channel.Trim().ToUpperInvariant() switch { "A" => "ReplaySelectChannelA", "AB" => "ReplaySelectChannelAB", _ => "ReplaySelectChannelB" }, null, null, token);
    private Task<bool> ExecuteAsync(string function, string? value, string? channel, CancellationToken token) => _api.ExecuteFunctionAsync(new VmixFunctionCall { Function = function, Value = value, Channel = channel }, token, function);
}
