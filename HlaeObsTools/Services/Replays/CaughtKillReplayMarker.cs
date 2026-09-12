using System;
using System.Globalization;
using System.Text.Json;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.Replays;

/// <summary>Backend-neutral listener that turns the focused observer's killfeed events into replay marks.</summary>
public sealed class CaughtKillReplayMarker : IDisposable
{
    private readonly HlaeWebSocketClient _webSocket;
    private readonly GsiServer _gsi;
    private readonly ReplaySettings _settings;
    private readonly ReplayMarkerController _marker;
    private readonly object _sync = new();
    private int _focusedSlot = -1, _round, _labelRound;
    private bool _disposed;

    public CaughtKillReplayMarker(HlaeWebSocketClient webSocket, GsiServer gsi, ReplaySettings settings, ReplayMarkerController marker)
    {
        _webSocket = webSocket; _gsi = gsi; _settings = settings; _marker = marker;
        _webSocket.MessageReceived += OnMessage; _gsi.GameStateUpdated += OnGameState;
    }

    private void OnGameState(object? sender, GsiGameState state)
    {
        var focused = -1;
        if (!string.IsNullOrWhiteSpace(state.FocusedPlayerSteamId))
            foreach (var player in state.Players)
                if (string.Equals(player.SteamId, state.FocusedPlayerSteamId, StringComparison.Ordinal)) { focused = player.Slot >= 0 ? player.Slot + 1 : player.Slot; break; }
        lock (_sync)
        {
            _focusedSlot = focused; _round = state.RoundNumber;
            if (!string.Equals(state.RoundPhase, "OVER", StringComparison.OrdinalIgnoreCase) && _round > 0) _labelRound = _round;
            else if (_labelRound == 0 && _round > 0) _labelRound = Math.Max(1, _round - 1);
        }
    }

    private void OnMessage(object? sender, string message)
    {
        if (!_settings.AutoMarkEnabled || (!_settings.IsVmixSelected && !_settings.IsObsReplaysSelected)) return;
        try
        {
            using var doc = JsonDocument.Parse(message); var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "killfeed_event" || !root.TryGetProperty("attacker", out var attacker) ||
                !attacker.TryGetProperty("observer_slot", out var slotValue) || !TryInt(slotValue, out var slot)) return;
            slot = ReplayMarkerPrimitives.NormalizeObserverSlot(slot);
            var name = attacker.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "unknown" : "unknown";
            lock (_sync)
            {
                if (slot < 0 || slot != _focusedSlot) return;
                _marker.RecordKill(_labelRound > 0 ? _labelRound : _round, name);
            }
        }
        catch (JsonException) { }
    }

    private static bool TryInt(JsonElement value, out int result) => value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out result) : int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    public void Dispose() { if (_disposed) return; _disposed = true; _webSocket.MessageReceived -= OnMessage; _gsi.GameStateUpdated -= OnGameState; }
}
