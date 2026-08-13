using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HlaeObsTools.Services.Vmix;

public sealed class VmixReplayCoordinator
{
    private const string FunctionPlayEventsByIdToOutput = "ReplayPlayEventsByIDToOutput";
    private const string FunctionStopEvents = "ReplayStopEvents";
    private readonly VmixApiClient _vmixApiClient;
    private readonly ReplayEventRegistry _registry;
    private readonly SemaphoreSlim _vmixLock = new(1, 1);
    private int _nextReplayEventIndex = -1;

    public VmixReplayCoordinator(VmixApiClient vmixApiClient, ReplayEventRegistry registry)
    {
        _vmixApiClient = vmixApiClient;
        _registry = registry;
    }

    public ReplayEventRegistry Registry => _registry;

    public async Task<VmixReplayCommandResult> RunAsync(Func<CancellationToken, Task<VmixReplayCommandResult>> action, CancellationToken token)
    {
        await _vmixLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await action(token).ConfigureAwait(false);
        }
        finally
        {
            _vmixLock.Release();
        }
    }

    public async Task<ReplayEventRecord> RegisterCreatedEventAsync(ReplayEventDraft draft, CancellationToken token)
    {
        var eventId = string.IsNullOrWhiteSpace(draft.VmixEventId)
            ? AssignNextReplayEventId()
            : draft.VmixEventId;
        var record = _registry.Add(new ReplayEventDraft
        {
            Source = draft.Source,
            Label = draft.Label,
            Channel = draft.Channel,
            Camera = draft.Camera,
            Round = draft.Round,
            KillCount = draft.KillCount,
            VmixEventId = eventId,
            Status = string.IsNullOrWhiteSpace(draft.Status) ? "Marked" : draft.Status
        });
        return record;
    }

    public string AssignNextReplayEventId()
    {
        var index = Interlocked.Increment(ref _nextReplayEventIndex);
        return index.ToString("D4", CultureInfo.InvariantCulture);
    }

    public void ClearTrackedEvents()
    {
        Interlocked.Exchange(ref _nextReplayEventIndex, -1);
        _registry.Clear();
    }

    public async Task<bool> PlayToOutputAsync(ReplayEventRecord record, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(record.VmixEventId))
            return false;

        await _vmixLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await _vmixApiClient.ExecuteFunctionAsync(new VmixFunctionCall
            {
                Function = FunctionPlayEventsByIdToOutput,
                Value = record.VmixEventId,
                Channel = NormalizeChannel(record.Channel)
            }, token, "ReplayPlayEventsByIDToOutput").ConfigureAwait(false);
        }
        finally
        {
            _vmixLock.Release();
        }
    }

    public async Task<bool> PlayToOutputAsync(ReplayEventRecord[] records, string? channel, CancellationToken token)
    {
        var ids = records
            .Select(r => r.VmixEventId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetReplayIdSortKey)
            .ToArray();
        if (ids.Length == 0)
            return false;

        await _vmixLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await _vmixApiClient.ExecuteFunctionAsync(new VmixFunctionCall
            {
                Function = FunctionPlayEventsByIdToOutput,
                Value = string.Join(",", ids),
                Channel = NormalizeChannel(channel ?? records.FirstOrDefault()?.Channel)
            }, token, "ReplayPlayEventsByIDToOutput").ConfigureAwait(false);
        }
        finally
        {
            _vmixLock.Release();
        }
    }

    public async Task<bool> StopReplayAsync(CancellationToken token)
    {
        await _vmixLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var channelAStopped = await _vmixApiClient.ExecuteFunctionAsync(
                FunctionStopEvents, "A", token, "ReplayStopEvents (A)").ConfigureAwait(false);
            var channelBStopped = await _vmixApiClient.ExecuteFunctionAsync(
                FunctionStopEvents, "B", token, "ReplayStopEvents (B)").ConfigureAwait(false);
            return channelAStopped && channelBStopped;
        }
        finally
        {
            _vmixLock.Release();
        }
    }

    private static string NormalizeChannel(string? channel)
    {
        var value = string.IsNullOrWhiteSpace(channel) ? "A" : channel.Trim().ToUpperInvariant();
        return value is "A" or "B" or "AB" ? value : "A";
    }

    private static int GetReplayIdSortKey(string id)
    {
        return int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.MaxValue;
    }
}

public readonly record struct VmixReplayCommandResult(bool Success, string? Label, string? Status = null);
