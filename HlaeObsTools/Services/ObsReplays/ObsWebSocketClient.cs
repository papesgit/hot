using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.ObsReplays;

/// <summary>Small obs-websocket v5 client for the OBS Replays Vendor API.</summary>
public sealed class ObsWebSocketClient : IDisposable
{
    private readonly ObsWebSocketSettings _settings;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private bool _disposed;
    public event EventHandler? StateChanged;
    public event EventHandler? ReplayStateChanged;
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public ObsWebSocketClient(ObsWebSocketSettings settings) => _settings = settings;

    public async Task<bool> ConnectAsync(CancellationToken token = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        _settings.ConnectionStatus = "Connecting…"; StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var socket = new ClientWebSocket();
            var host = string.IsNullOrWhiteSpace(_settings.Host) ? "127.0.0.1" : _settings.Host.Trim();
            var port = _settings.Port is > 0 and <= 65535 ? _settings.Port : 4455;
            await socket.ConnectAsync(new Uri($"ws://{host}:{port}"), token).ConfigureAwait(false);
            var hello = await ReceiveOneAsync(socket, token).ConfigureAwait(false);
            var d = hello.GetProperty("d");
            var identify = new Dictionary<string, object?> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 512 };
            if (d.TryGetProperty("authentication", out var authentication))
                identify["authentication"] = BuildAuthentication(authentication);
            await SendRawAsync(socket, new { op = 1, d = identify }, token).ConfigureAwait(false);
            var identified = await ReceiveOneAsync(socket, token).ConfigureAwait(false);
            if (identified.GetProperty("op").GetInt32() != 2) throw new InvalidOperationException("OBS did not accept Identify.");
            _socket = socket;
            _receiveCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(socket, _receiveCts.Token));
            var session = await CallVendorAsync("GetSession", new { }, token).ConfigureAwait(false);
            if (!GetSuccess(session)) _settings.ConnectionStatus = "Connected to OBS; OBS Replays unavailable";
            else _settings.ConnectionStatus = "Connected to OBS Replays";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return GetSuccess(session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _settings.ConnectionStatus = $"Connection failed: {ex.Message}";
            StateChanged?.Invoke(this, EventArgs.Empty);
            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        _receiveCts?.Cancel(); _receiveCts?.Dispose(); _receiveCts = null;
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket != null) { try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None).ConfigureAwait(false); } catch { } socket.Dispose(); }
        if (!_disposed) { _settings.ConnectionStatus = "Disconnected"; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public async Task<JsonElement> CallVendorAsync(string requestType, object requestData, CancellationToken token = default)
    {
        if (_socket is not { State: WebSocketState.Open } socket) throw new InvalidOperationException("OBS WebSocket is not connected.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(id, completion)) throw new InvalidOperationException();
        try
        {
            await SendRawAsync(socket, new { op = 6, d = new { requestType = "CallVendorRequest", requestId = id, requestData = new { vendorName = "obs-replays", requestType, requestData } } }, token).ConfigureAwait(false);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            var response = await completion.Task.ConfigureAwait(false);
            if (response.TryGetProperty("requestStatus", out var status)
                && status.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.False)
            {
                var comment = status.TryGetProperty("comment", out var value) ? value.GetString() : "OBS rejected the request.";
                using var error = JsonDocument.Parse(JsonSerializer.Serialize(new { success = false, error = comment }));
                return error.RootElement.Clone();
            }
            if (!response.TryGetProperty("responseData", out var data))
                return response.Clone();
            // OBS versions/vendor handlers may either return vendor data directly here or
            // wrap it once more in the CallVendorRequest response envelope.
            return data.TryGetProperty("responseData", out var vendorData) ? vendorData.Clone() : data.Clone();
        }
        finally { _requests.TryRemove(id, out _); }
    }

    public Task<JsonElement> ListEventsAsync(CancellationToken token = default) => CallVendorAsync("ListEvents", new { }, token);
    public Task<JsonElement> StartPlayoutAsync(IEnumerable<string> ids, CancellationToken token = default) => CallVendorAsync("StartPlayout", new { eventIds = BuildEventIds(ids) }, token);
    public Task<JsonElement> StopPlayoutAsync(CancellationToken token = default) => CallVendorAsync("StopPlayout", new { }, token);
    public Task<JsonElement> CreateEventFromLiveAsync(int preRollMs, string? label, CancellationToken token = default) => CallVendorAsync("CreateEventFromLive", new { preRollMs, label }, token);
    public Task<JsonElement> DeleteEventAsync(string eventId, CancellationToken token = default) => CallVendorAsync("DeleteEvent", new { eventId }, token);
    public Task<JsonElement> GetRecordingStatusAsync(CancellationToken token = default) => CallVendorAsync("GetRecordingStatus", new { }, token);
    public Task<JsonElement> UpdateEventAsync(string eventId, int inMs, int outMs, string label, CancellationToken token = default) => CallVendorAsync("UpdateEvent", new { eventId, inMs, outMs, label }, token);
    private static object[] BuildEventIds(IEnumerable<string> ids) { var list = new List<object>(); foreach (var id in ids) list.Add(new { eventId = id }); return list.ToArray(); }
    private static bool GetSuccess(JsonElement data) => !data.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.False;
    private string BuildAuthentication(JsonElement auth)
    {
        var salt = auth.GetProperty("salt").GetString() ?? string.Empty; var challenge = auth.GetProperty("challenge").GetString() ?? string.Empty;
        static string Hash(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return Hash(Hash(_settings.Password + salt) + challenge);
    }
    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        try { while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) { var json = await ReceiveOneAsync(socket, token).ConfigureAwait(false); var op = json.GetProperty("op").GetInt32(); var d = json.GetProperty("d"); if (op == 7 && d.TryGetProperty("requestId", out var id) && _requests.TryGetValue(id.GetString() ?? string.Empty, out var pending)) pending.TrySetResult(d.Clone()); else if (op == 5 && IsObsReplaysVendorEvent(d)) ReplayStateChanged?.Invoke(this, EventArgs.Empty); } }
        catch { if (!_disposed) { _settings.ConnectionStatus = "Disconnected"; StateChanged?.Invoke(this, EventArgs.Empty); } }
    }
    private static bool IsObsReplaysVendorEvent(JsonElement data)
    {
        // Normal obs-websocket VendorEvent: d.eventData.vendorName. Accept the direct
        // form too, since it is useful for compatible implementations and diagnostics.
        if (data.TryGetProperty("vendorName", out var directVendor))
            return directVendor.GetString() == "obs-replays";
        return data.TryGetProperty("eventData", out var eventData)
            && eventData.TryGetProperty("vendorName", out var vendor)
            && vendor.GetString() == "obs-replays";
    }
    private async Task SendRawAsync(ClientWebSocket socket, object value, CancellationToken token) { var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)); await _sendLock.WaitAsync(token).ConfigureAwait(false); try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false); } finally { _sendLock.Release(); } }
    private static async Task<JsonElement> ReceiveOneAsync(ClientWebSocket socket, CancellationToken token) { var buffer = new byte[8192]; using var stream = new System.IO.MemoryStream(); WebSocketReceiveResult result; do { result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false); if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("OBS closed the connection."); stream.Write(buffer, 0, result.Count); } while (!result.EndOfMessage); using var doc = JsonDocument.Parse(stream.ToArray()); return doc.RootElement.Clone(); }
    public void Dispose() { if (_disposed) return; _disposed = true; DisconnectAsync().GetAwaiter().GetResult(); _sendLock.Dispose(); }
}
