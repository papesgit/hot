using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GfxProducerService.Server;

public sealed class ProducerServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly AtlasManager _atlasManager = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProducerServer(string host, int port)
    {
        _port = port;
        _listener = new HttpListener();
        var prefixHost = NormalizeHost(host);
        _listener.Prefixes.Add($"http://{prefixHost}:{port}/gfxp/");
    }

    private int GetPort() => _port;

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "127.0.0.1";
        if (host == "0.0.0.0" || host == "*" || host == "+")
            return "+";
        return host;
    }

    public void Initialize()
    {
        _atlasManager.EnsureInitialized();
    }

    public async Task StartAsync(CancellationToken token)
    {
        using var reg = token.Register(() => Stop());
        _listener.Start();
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException ex)
            {
                if (ex.ErrorCode == 5)
                {
                    Console.WriteLine("[gfxp] access denied while binding listener.");
                    Console.WriteLine("[gfxp] run: dotnet run -- --show-urlacl --host 0.0.0.0 --port {0}", GetPort());
                    break;
                }
                if (!_listener.IsListening)
                {
                    Console.WriteLine("[gfxp] listener stopped.");
                    break;
                }
                if (token.IsCancellationRequested)
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (context == null)
                continue;

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            _ = HandleClientAsync(context, token);
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context, CancellationToken token)
    {
        WebSocket? socket = null;
        try
        {
            var ws = await context.AcceptWebSocketAsync(subProtocol: null);
            socket = ws.WebSocket;
            Console.WriteLine($"[gfxp] client connected: {context.Request.RemoteEndPoint}");
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
                socket.Dispose();
            }
            Console.WriteLine("[gfxp] client disconnected");
        }
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
        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
        _atlasManager.Dispose();
    }
}
