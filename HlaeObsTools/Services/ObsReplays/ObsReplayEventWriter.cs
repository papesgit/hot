using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Vmix;

namespace HlaeObsTools.Services.ObsReplays;

/// <summary>Single authoritative path for writing OBS Replays events and HOT's UUID mapping.</summary>
public sealed class ObsReplayEventWriter
{
    private readonly ObsWebSocketClient _client;
    private readonly ReplayEventRegistry _registry;
    public ObsReplayEventWriter(ObsWebSocketClient client, ReplayEventRegistry registry) { _client = client; _registry = registry; }
    public bool IsConnected => _client.IsConnected;

    public async Task<ObsReplayEventHandle?> CreateFromLiveAsync(int preRollMs, string label, string source, int round, int killCount, CancellationToken token)
    {
        var response = await _client.CreateEventFromLiveAsync(preRollMs, label, token).ConfigureAwait(false);
        if (Failed(response) || !response.TryGetProperty("event", out var created) || !created.TryGetProperty("id", out var id) || !created.TryGetProperty("inMs", out var input) || !input.TryGetInt32(out var inMs)) return null;
        var eventId = id.GetString();
        if (string.IsNullOrWhiteSpace(eventId)) return null;
        _registry.Add(new ReplayEventDraft { VmixEventId = eventId, Source = source, Label = label, Round = round, KillCount = killCount, Status = "Marked" });
        return new ObsReplayEventHandle(eventId, inMs);
    }

    public async Task<bool> UpdateAsync(ObsReplayEventHandle handle, int outMs, string label, int killCount, CancellationToken token)
    {
        var response = await _client.UpdateEventAsync(handle.Id, handle.InMs, outMs, label, token).ConfigureAwait(false);
        if (Failed(response)) return false;
        _registry.UpdateByEventId(handle.Id, record => { record.Label = label; record.KillCount = killCount; record.Status = "Updated"; });
        return true;
    }

    public async Task<int?> GetRecordedThroughAsync(CancellationToken token)
    {
        var response = await _client.GetRecordingStatusAsync(token).ConfigureAwait(false);
        return !Failed(response) && response.TryGetProperty("recordedThroughMs", out var through) && through.TryGetInt32(out var value) ? value : null;
    }
    private static bool Failed(JsonElement value) => value.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False;
}

public readonly record struct ObsReplayEventHandle(string Id, int InMs);
