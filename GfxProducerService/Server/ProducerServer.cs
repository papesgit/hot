using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace GfxProducerService.Server;

public sealed class ProducerServer : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly AtlasManager _atlasManager = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private WebApplication? _app;
    private readonly object _socketLock = new();
    private readonly HashSet<WebSocket> _sockets = new();

    public ProducerServer(string host, int port)
    {
        _port = port;
        _host = NormalizeHost(host);
        _atlasManager.TriggerCompleted += OnTriggerCompleted;
    }

    public void Initialize()
    {
        _atlasManager.EnsureInitialized();
    }

    public async Task StartAsync(CancellationToken token)
    {
        using var reg = token.Register(() => Stop());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://{_host}:{_port}");
        var app = builder.Build();
        _app = app;

        app.UseWebSockets();
        app.Map("/gfxp", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            WebSocket? socket = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync();
                var remote = context.Connection.RemoteIpAddress != null
                    ? $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}"
                    : "unknown";
                Console.WriteLine($"[gfxp] client connected: {remote}");
                lock (_socketLock)
                {
                    _sockets.Add(socket);
                }
                await ReceiveLoopAsync(socket, token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gfxp] client error: {ex.Message}");
            }
            finally
            {
                if (socket != null)
                {
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                    catch
                    {
                        // ignore
                    }
                    lock (_socketLock)
                    {
                        _sockets.Remove(socket);
                    }
                    socket.Dispose();
                }
                Console.WriteLine("[gfxp] client disconnected");
            }
        });

        await app.StartAsync(token);
        await app.WaitForShutdownAsync(token);
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var messageStream = new MemoryStream();

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            WebSocketReceiveResult? result = null;
            try
            {
                result = await socket.ReceiveAsync(buffer, token);
            }
            catch (WebSocketException)
            {
                break;
            }
            catch (OperationCanceledException)
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

            await HandleMessageAsync(socket, json);
        }
    }

    private async Task HandleMessageAsync(WebSocket socket, string json)
    {
        string? id = null;
        string? cmd = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idProp))
                id = idProp.GetString();
            if (root.TryGetProperty("cmd", out var cmdProp))
                cmd = cmdProp.GetString();

            if (string.IsNullOrWhiteSpace(cmd))
            {
                await SendErrorAsync(socket, id, "Missing cmd");
                return;
            }

            if (!string.Equals(cmd, "gfxp.gsi.update", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"[gfxp] cmd {cmd}");
            var payload = root.TryGetProperty("data", out var dataProp) ? dataProp : root;

            switch (cmd)
            {
                case "gfxp.atlas.create":
                    await HandleAtlasCreateAsync(socket, id, payload);
                    break;
                case "gfxp.atlas.reload":
                    await HandleAtlasReloadAsync(socket, id, payload);
                    break;
                case "gfxp.atlas.destroy":
                    await HandleAtlasDestroyAsync(socket, id, payload);
                    break;
                case "gfxp.trigger":
                    await HandleTriggerAsync(socket, id, payload);
                    break;
                case "gfxp.gsi.update":
                    await HandleGsiUpdateAsync(socket, id, payload);
                    break;
                default:
                    await SendErrorAsync(socket, id, $"Unknown cmd: {cmd}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gfxp] cmd error: {ex.Message}");
            await SendErrorAsync(socket, id, ex.Message);
        }
    }

    private async Task HandleAtlasCreateAsync(WebSocket socket, string? id, JsonElement root)
    {
        var request = new AtlasCreateRequest
        {
            Name = GetString(root, "name") ?? string.Empty,
            Width = GetInt(root, "width", 0),
            Height = GetInt(root, "height", 0),
            Format = GetString(root, "format") ?? "BGRA8",
            AlphaMode = GetString(root, "alphaMode") ?? "premultiplied",
            HtmlPath = GetString(root, "htmlPath") ?? string.Empty,
            KeyedMutex = GetBool(root, "keyedMutex", true),
            TargetFps = GetInt(root, "targetFps", 30)
        };

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await SendErrorAsync(socket, id, "Atlas name required");
            return;
        }

        var info = _atlasManager.CreateAtlas(request);
        Console.WriteLine($"[gfxp] atlas created '{info.Name}' {info.Width}x{info.Height} handle={info.Handle}");
        await SendResponseAsync(socket, id, new
        {
            name = info.Name,
            handle = info.Handle.ToString(),
            width = info.Width,
            height = info.Height,
            format = info.Format,
            alphaMode = info.AlphaMode,
            keyedMutex = info.KeyedMutex
        });
    }

    private async Task HandleAtlasReloadAsync(WebSocket socket, string? id, JsonElement root)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            await SendErrorAsync(socket, id, "Atlas name required");
            return;
        }

        var renderer = _atlasManager.GetRenderer(name);
        if (renderer == null)
        {
            await SendErrorAsync(socket, id, $"Atlas not found: {name}");
            return;
        }

        await renderer.ReloadAsync();
        Console.WriteLine($"[gfxp] atlas reloaded '{name}'");
        await SendResponseAsync(socket, id, new { name });
    }

    private async Task HandleAtlasDestroyAsync(WebSocket socket, string? id, JsonElement root)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            await SendErrorAsync(socket, id, "Atlas name required");
            return;
        }

        _atlasManager.DestroyAtlas(name);
        Console.WriteLine($"[gfxp] atlas destroyed '{name}'");
        await SendResponseAsync(socket, id, new { name });
    }

    private async Task HandleTriggerAsync(WebSocket socket, string? id, JsonElement root)
    {
        var atlas = GetString(root, "atlas");
        var action = GetString(root, "action");
        var target = GetString(root, "target") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(atlas) || string.IsNullOrWhiteSpace(action))
        {
            await SendErrorAsync(socket, id, "Atlas and action required");
            return;
        }

        var renderer = _atlasManager.GetRenderer(atlas);
        if (renderer == null)
        {
            await SendErrorAsync(socket, id, $"Atlas not found: {atlas}");
            return;
        }

        await renderer.TriggerAsync(action, target);
        Console.WriteLine($"[gfxp] trigger '{action}' atlas='{atlas}' target='{target}'");
        await SendResponseAsync(socket, id, new { atlas, action, target });
    }

    private async Task HandleGsiUpdateAsync(WebSocket socket, string? id, JsonElement root)
    {
        var gsiJson = GetString(root, "gsiJson");
        if (string.IsNullOrWhiteSpace(gsiJson))
        {
            await SendErrorAsync(socket, id, "gsiJson required");
            return;
        }

        long? heartbeat = null;
        if (root.TryGetProperty("heartbeat", out var hbProp) && hbProp.TryGetInt64(out var hb))
            heartbeat = hb;

        string? extrasJson = null;
        if (root.TryGetProperty("extras", out var extrasProp))
            extrasJson = extrasProp.GetRawText();

        await _atlasManager.BroadcastGsiAsync(gsiJson, heartbeat, extrasJson);
        await SendResponseAsync(socket, id, new { heartbeat });
    }

    private async Task SendResponseAsync(WebSocket socket, string? id, object data)
    {
        var payload = new
        {
            id,
            ok = true,
            data
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        await SendAsync(socket, json);
    }

    private async Task SendErrorAsync(WebSocket socket, string? id, string error)
    {
        Console.WriteLine($"[gfxp] error: {error}");
        var payload = new
        {
            id,
            ok = false,
            error
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        await SendAsync(socket, json);
    }

    private static async Task SendAsync(WebSocket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private void OnTriggerCompleted(string atlas, string action, string target)
    {
        _ = BroadcastAsync(new
        {
            type = "gfxp.trigger.done",
            data = new { atlas, action, target }
        });
    }

    private async Task BroadcastAsync(object payload)
    {
        WebSocket[] sockets;
        lock (_socketLock)
        {
            sockets = _sockets.ToArray();
        }
        if (sockets.Length == 0)
            return;

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var socket in sockets)
        {
            if (socket.State != WebSocketState.Open)
                continue;
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // ignore send errors
            }
        }
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        if (root.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value))
            return value;
        return fallback;
    }

    private static bool GetBool(JsonElement root, string name, bool fallback)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True)
            return true;
        if (root.TryGetProperty(name, out prop) && prop.ValueKind == JsonValueKind.False)
            return false;
        return fallback;
    }

    public void Stop()
    {
        if (_app == null)
            return;
        try
        {
            _app.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        Stop();
        if (_app != null)
        {
            try
            {
                _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // ignore
            }
        }
        _atlasManager.TriggerCompleted -= OnTriggerCompleted;
        _atlasManager.Dispose();
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "127.0.0.1";
        if (host == "*" || host == "+")
            return "0.0.0.0";
        return host;
    }
}
