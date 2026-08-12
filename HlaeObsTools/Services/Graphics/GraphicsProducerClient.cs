using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HlaeObsTools.Services.Graphics;

public sealed class GraphicsProducerClient : IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProducerResponse>> _pending = new();
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private string _host;
    private int _port;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<ProducerTriggerEvent>? TriggerCompleted;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public GraphicsProducerClient(string host = "127.0.0.1", int port = 31340)
    {
        _host = host;
        _port = port;
    }

    public void ConfigureEndpoint(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        if (IsConnected)
            return;

        _webSocket = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        var uri = new Uri($"ws://{_host}:{_port}/gfxp/");
        try
        {
            await _webSocket.ConnectAsync(uri, CancellationToken.None);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            DisposeSocket();
        }
    }

    public async Task ReconnectAsync()
    {
        DisposeSocket();
        await ConnectAsync();
    }

    public async Task<ProducerAtlasCreateResult> CreateAtlasAsync(ProducerAtlasRequest request)
    {
        var response = await SendRequestAsync("gfxp.atlas.create", request);
        if (response == null)
        {
            Console.WriteLine("[gfxp-client] atlas create failed: no response");
            return new ProducerAtlasCreateResult(ProducerCommandResult.NoResponse, null, null, null);
        }
        if (!response.Value.Ok)
        {
            Console.WriteLine($"[gfxp-client] atlas create error: {response.Value.Error}");
            return new ProducerAtlasCreateResult(ProducerCommandResult.Failed, null, response.Value.ErrorCode, response.Value.Error);
        }

        var data = response.Value.Data;
        if (data.ValueKind != JsonValueKind.Object)
            return new ProducerAtlasCreateResult(ProducerCommandResult.Failed, null, "invalidProducerResponse", "Producer returned an invalid atlas create response");
        if (!data.TryGetProperty("handle", out var handleProp))
            return new ProducerAtlasCreateResult(ProducerCommandResult.Failed, null, "invalidProducerResponse", "Producer atlas create response did not include a handle");

        var info = new ProducerAtlasInfo
        {
            Name = data.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? request.Name : request.Name,
            Handle = handleProp.GetString() ?? string.Empty,
            Width = data.TryGetProperty("width", out var widthProp) ? widthProp.GetInt32() : request.Width,
            Height = data.TryGetProperty("height", out var heightProp) ? heightProp.GetInt32() : request.Height,
            Format = data.TryGetProperty("format", out var formatProp) ? formatProp.GetString() ?? request.Format : request.Format,
            AlphaMode = data.TryGetProperty("alphaMode", out var alphaProp) ? alphaProp.GetString() ?? request.AlphaMode : request.AlphaMode,
            KeyedMutex = data.TryGetProperty("keyedMutex", out var keyedProp) && keyedProp.ValueKind == JsonValueKind.True
        };
        return new ProducerAtlasCreateResult(ProducerCommandResult.Succeeded, info, null, null);
    }

    public async Task<ProducerCommandResult> ReloadAtlasAsync(string name)
    {
        var response = await SendRequestAsync("gfxp.atlas.reload", new { name });
        if (response == null)
            return ProducerCommandResult.NoResponse;

        if (response?.Ok == false)
        {
            Console.WriteLine($"[gfxp-client] atlas reload error: {response?.Error}");
            return ProducerCommandResult.Failed;
        }

        return ProducerCommandResult.Succeeded;
    }

    public async Task<bool> DestroyAtlasAsync(string name)
    {
        var response = await SendRequestAsync("gfxp.atlas.destroy", new { name });
        if (response?.Ok == false)
            Console.WriteLine($"[gfxp-client] atlas destroy error: {response?.Error}");
        return response?.Ok == true;
    }

    public Task TriggerAsync(string atlas, string action, string target)
    {
        return SendRequestAsync("gfxp.trigger", new { atlas, action, target });
    }

    public Task SendGsiAsync(string gsiJson, long heartbeat)
    {
        if (string.IsNullOrWhiteSpace(gsiJson))
            return Task.CompletedTask;
        return SendRequestAsync("gfxp.gsi.update", new { gsiJson, heartbeat });
    }

    private async Task<ProducerResponse?> SendRequestAsync(string cmd, object data)
    {
        if (!IsConnected || _webSocket == null)
            return null;

        var id = Guid.NewGuid().ToString("N");
        var payload = new
        {
            id,
            cmd,
            data
        };

        var tcs = new TaskCompletionSource<ProducerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
        _pending.TryRemove(id, out _);
        if (!completed)
            return null;

        return await tcs.Task;
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_webSocket == null)
            return;

        var buffer = new byte[64 * 1024];
        var messageStream = new MemoryStream();
        while (!token.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult? result = null;
            try
            {
                result = await _webSocket.ReceiveAsync(buffer, token);
            }
            catch
            {
                break;
            }

            if (result == null)
                break;

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            messageStream.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            var json = Encoding.UTF8.GetString(messageStream.ToArray());
            messageStream.SetLength(0);
            HandleMessage(json);
        }

        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();
                if (string.Equals(type, "gfxp.trigger.done", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
                    {
                        var atlas = dataProp.TryGetProperty("atlas", out var atlasProp) ? atlasProp.GetString() ?? string.Empty : string.Empty;
                        var action = dataProp.TryGetProperty("action", out var actionProp) ? actionProp.GetString() ?? string.Empty : string.Empty;
                        var target = dataProp.TryGetProperty("target", out var targetProp) ? targetProp.GetString() ?? string.Empty : string.Empty;
                        TriggerCompleted?.Invoke(this, new ProducerTriggerEvent(atlas, action, target));
                    }
                    return;
                }
            }
            if (!root.TryGetProperty("id", out var idProp))
                return;
            var id = idProp.GetString();
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (_pending.TryRemove(id, out var tcs))
            {
                JsonElement data = default;
                if (root.TryGetProperty("data", out var dataProp))
                {
                    data = dataProp.Clone();
                }

                var response = new ProducerResponse
                {
                    Ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True,
                    ErrorCode = root.TryGetProperty("errorCode", out var errCodeProp) ? errCodeProp.GetString() : null,
                    Error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : null,
                    Data = data
                };
                tcs.TrySetResult(response);
            }
        }
        catch
        {
            // ignore parse errors
        }
    }

    private void DisposeSocket()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_webSocket != null)
        {
            try
            {
                _webSocket.Dispose();
            }
            catch
            {
                // ignore
            }
            _webSocket = null;
        }
    }

    public void Dispose()
    {
        DisposeSocket();
    }

    private readonly struct ProducerResponse
    {
        public bool Ok { get; init; }
        public string? ErrorCode { get; init; }
        public string? Error { get; init; }
        public JsonElement Data { get; init; }
    }
}

public sealed record ProducerTriggerEvent(string Atlas, string Action, string Target);

public sealed class ProducerAtlasRequest
{
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = "BGRA8";
    public string AlphaMode { get; set; } = "premultiplied";
    public bool KeyedMutex { get; set; } = true;
    public string HtmlPath { get; set; } = string.Empty;
    public int TargetFps { get; set; } = 30;
}

public sealed class ProducerAtlasInfo
{
    public string Name { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = string.Empty;
    public string AlphaMode { get; set; } = string.Empty;
    public bool KeyedMutex { get; set; }
}

public sealed record ProducerAtlasCreateResult(ProducerCommandResult Result, ProducerAtlasInfo? Info, string? ErrorCode, string? Error);

public enum ProducerCommandResult
{
    Succeeded,
    Failed,
    NoResponse
}
