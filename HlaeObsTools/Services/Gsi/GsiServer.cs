using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HlaeObsTools.Services.Gsi;

/// <summary>
/// Lightweight HTTP listener for CS2 Game State Integration callbacks.
/// </summary>
public sealed class GsiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private long _heartbeat;

    public event EventHandler<GsiGameState>? GameStateUpdated;

    public bool IsRunning => _listener.IsListening;

    public void Start(int port = 31982, string path = "/gsi/")
    {
        if (IsRunning) return;

        try
        {
            _listener.Prefixes.Clear();
            var normalizedPath = path.StartsWith("/") ? path : "/" + path;
            if (!normalizedPath.EndsWith("/")) normalizedPath += "/";

            // Add both the specific path and a root fallback to catch mismatched trailing slashes.
            _listener.Prefixes.Add($"http://127.0.0.1:{port}{normalizedPath}");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"GSI listener failed to start: {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        Console.WriteLine($"GSI listener started on http://127.0.0.1:{port}{path}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener.Stop();
            _loopTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignore
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GSI listener error: {ex.Message}");
            }

            if (ctx == null) continue;

            _ = Task.Run(() => HandleRequestAsync(ctx), token);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                ctx.Response.Close();
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            // Increment heartbeat on each GSI update
            var currentHeartbeat = System.Threading.Interlocked.Increment(ref _heartbeat);

            var state = ParseState(body, currentHeartbeat);
            if (state != null)
            {
                GameStateUpdated?.Invoke(this, state);
            }

            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GSI request handling failed: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                ctx.Response.Close();
            }
            catch { }
        }
    }

    private static GsiGameState? ParseState(string body, long heartbeat)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var mapName = root.TryGetProperty("map", out var mapElem) && mapElem.TryGetProperty("name", out var nameElem)
                ? nameElem.GetString() ?? string.Empty
                : string.Empty;

            var players = new List<GsiPlayer>();
            if (root.TryGetProperty("allplayers", out var playersElem))
            {
                foreach (var playerProp in playersElem.EnumerateObject())
                {
                    var playerElem = playerProp.Value;
                    string team = playerElem.TryGetProperty("team", out var teamElem) ? teamElem.GetString() ?? string.Empty : string.Empty;
                    string pname = playerElem.TryGetProperty("name", out var pnameElem) ? pnameElem.GetString() ?? string.Empty : string.Empty;
                    string steamId = playerProp.Name;
                    var pos = playerElem.TryGetProperty("position", out var posElem) ? Vec3.Parse(posElem.GetString()) : default;
                    var forward = playerElem.TryGetProperty("forward", out var fwdElem) ? Vec3.Parse(fwdElem.GetString()) : default;
                    bool alive = playerElem.TryGetProperty("state", out var stateElem)
                        && stateElem.TryGetProperty("health", out var hpElem)
                        && hpElem.GetInt32() > 0;
                    bool hasBomb = false;
                    if (playerElem.TryGetProperty("weapons", out var weaponsElem))
                    {
                        foreach (var weaponProp in weaponsElem.EnumerateObject())
                        {
                            var weapon = weaponProp.Value;
                            if (weapon.TryGetProperty("type", out var typeElem))
                            {
                                var type = typeElem.GetString();
                                if (string.Equals(type, "C4", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasBomb = true;
                                    break;
                                }
                            }
                        }
                    }

                    int playerSlot = playerElem.TryGetProperty("observer_slot", out var slotElem) ? slotElem.GetInt32() : -1;

                    players.Add(new GsiPlayer
                    {
                        SteamId = steamId,
                        Name = pname,
                        Team = team,
                        Position = pos,
                        Forward = forward,
                        IsAlive = alive,
                        HasBomb = hasBomb,
                        Slot = playerSlot
                    });
                }
            }

            GsiBombState? bombState = null;
            if (root.TryGetProperty("bomb", out var bombElem))
            {
                bombState = new GsiBombState
                {
                    State = bombElem.TryGetProperty("state", out var sElem) ? sElem.GetString() ?? string.Empty : string.Empty,
                    Position = bombElem.TryGetProperty("position", out var bPosElem) ? Vec3.Parse(bPosElem.GetString()) : default
                };
            }

            string? focusedPlayerSteamId = null;
            if (root.TryGetProperty("player", out var focusedPlayerElem) && focusedPlayerElem.TryGetProperty("steamid", out var steamIdElem))
            {
                focusedPlayerSteamId = steamIdElem.GetString();
            }

            var grenades = new List<GsiGrenade>();
            if (root.TryGetProperty("grenades", out var grenadesElem))
            {
                foreach (var grenadeProperty in grenadesElem.EnumerateObject())
                {
                    var grenadeId = grenadeProperty.Name;
                    var grenade = grenadeProperty.Value;

                    var type = grenade.TryGetProperty("type", out var typeElem) ? typeElem.GetString() ?? string.Empty : string.Empty;
                    var position = grenade.TryGetProperty("position", out var posElem) ? Vec3.Parse(posElem.GetString()) : default;
                    var velocity = grenade.TryGetProperty("velocity", out var velElem) ? Vec3.Parse(velElem.GetString()) : default;
                    var lifetime = grenade.TryGetProperty("lifetime", out var ltElem) && double.TryParse(ltElem.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lt) ? lt : 0.0;
                    var owner = grenade.TryGetProperty("owner", out var ownerElem) ? ownerElem.GetString() : null;

                    double? effectTime = null;
                    if (grenade.TryGetProperty("effecttime", out var effectTimeElem) && double.TryParse(effectTimeElem.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var et))
                    {
                        effectTime = et;
                    }

                    List<Vec3>? flames = null;
                    if (grenade.TryGetProperty("flames", out var flamesElem))
                    {
                        flames = new List<Vec3>();
                        foreach (var flameProperty in flamesElem.EnumerateObject())
                        {
                            var flamePos = Vec3.Parse(flameProperty.Value.GetString());
                            flames.Add(flamePos);
                        }
                    }

                    grenades.Add(new GsiGrenade
                    {
                        Id = grenadeId,
                        Type = type,
                        Position = position,
                        Velocity = velocity,
                        LifeTime = lifetime,
                        OwnerSteamId = owner,
                        EffectTime = effectTime,
                        Flames = flames
                    });
                }
            }

            return new GsiGameState
            {
                MapName = mapName,
                Players = players,
                Grenades = grenades,
                Bomb = bombState,
                FocusedPlayerSteamId = focusedPlayerSteamId,
                Heartbeat = heartbeat
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse GSI payload: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
    }
}
