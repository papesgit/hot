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
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private long _heartbeat;

    public event EventHandler<GsiGameState>? GameStateUpdated;

    public bool IsRunning => _listener != null && _listener.IsListening;

    public void Start(int port = 31337, string path = "/gsi/", string host = "0.0.0.0")
    {
        Stop();

        var normalizedPath = path.StartsWith("/") ? path : "/" + path;
        if (!normalizedPath.EndsWith("/")) normalizedPath += "/";

        bool started = false;
        string requestedHost = host;
        bool useWildcard = string.Equals(requestedHost, "0.0.0.0", StringComparison.OrdinalIgnoreCase) || requestedHost == "*";
        string prefixHost = useWildcard ? "+" : requestedHost;

        _listener = new HttpListener();

        // Try requested host first (may require URL ACL for non-loopback).
        try
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://{prefixHost}:{port}{normalizedPath}");
            _listener.Prefixes.Add($"http://{prefixHost}:{port}/");
            _listener.Start();
            started = true;
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"GSI listener failed on {requestedHost}:{port} ({ex.Message}). If you need non-loopback, run as administrator or add a URL ACL: netsh http add urlacl url=http://{requestedHost}:{port}/ user=Everyone");
        }

        // Fallback to loopback if requested host failed and isn't already loopback.
        if (!started && !string.Equals(requestedHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}{normalizedPath}");
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                started = true;
                host = "127.0.0.1";
            }
            catch (HttpListenerException ex)
            {
                Console.WriteLine($"GSI listener fallback to loopback failed: {ex.Message}");
            }
        }

        if (!started)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        Console.WriteLine($"GSI listener started on http://{host}:{port}{path}");
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
                if (_listener == null) break;
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

            GsiTeam? teamCt = null;
            GsiTeam? teamT = null;
            int roundNumber = 0;
            string? roundPhase = null;
            double? phaseEndsIn = null;
            var mapName = root.TryGetProperty("map", out var mapElem) && mapElem.TryGetProperty("name", out var nameElem)
                ? nameElem.GetString() ?? string.Empty
                : string.Empty;

            if (root.TryGetProperty("map", out mapElem))
            {
                if (mapElem.TryGetProperty("round", out var roundElem))
                {
                    // map.round is zero-based; display as 1-based
                    roundNumber = roundElem.GetInt32() + 1;
                }

                if (mapElem.TryGetProperty("team_ct", out var ctElem))
                {
                    teamCt = ParseTeam(ctElem, "CT");
                }

                if (mapElem.TryGetProperty("team_t", out var tElem))
                {
                    teamT = ParseTeam(tElem, "T");
                }
            }

            if (root.TryGetProperty("phase_countdowns", out var phaseElem))
            {
                if (phaseElem.TryGetProperty("phase", out var phaseProp))
                {
                    roundPhase = phaseProp.GetString();
                }

                if (phaseElem.TryGetProperty("phase_ends_in", out var endsProp))
                {
                    if (endsProp.ValueKind == System.Text.Json.JsonValueKind.Number && endsProp.TryGetDouble(out var endsNumeric))
                    {
                        phaseEndsIn = endsNumeric;
                    }
                    else if (double.TryParse(endsProp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var endsIn))
                    {
                        phaseEndsIn = endsIn;
                    }
                }
            }

            if (root.TryGetProperty("round", out var roundObj) && roundObj.TryGetProperty("phase", out var roundPhaseElem))
            {
                roundPhase ??= roundPhaseElem.GetString();
            }

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
                    int health = 0;
                    int armor = 0;
                    bool hasHelmet = false;
                    bool hasDefuseKit = false;
                    int money = 0;
                    int equipmentValue = 0;
                    int roundKills = 0;
                    int roundKillHs = 0;
                    int kills = 0;
                    int deaths = 0;
                    int assists = 0;
                    int mvps = 0;
                    int score = 0;

                    if (playerElem.TryGetProperty("state", out var stateElem))
                    {
                        if (stateElem.TryGetProperty("health", out var hpElem)) health = hpElem.GetInt32();
                        if (stateElem.TryGetProperty("armor", out var armorElem)) armor = armorElem.GetInt32();
                        if (stateElem.TryGetProperty("helmet", out var helmetElem)) hasHelmet = helmetElem.GetBoolean();
                        if (stateElem.TryGetProperty("defusekit", out var kitElem)) hasDefuseKit = kitElem.GetBoolean();
                        if (stateElem.TryGetProperty("money", out var moneyElem)) money = moneyElem.GetInt32();
                        if (stateElem.TryGetProperty("equip_value", out var equipElem)) equipmentValue = equipElem.GetInt32();
                        if (stateElem.TryGetProperty("round_kills", out var rkElem)) roundKills = rkElem.GetInt32();
                        if (stateElem.TryGetProperty("round_killhs", out var rkhElem)) roundKillHs = rkhElem.GetInt32();
                    }

                    if (playerElem.TryGetProperty("match_stats", out var statsElem))
                    {
                        if (statsElem.TryGetProperty("kills", out var killsElem)) kills = killsElem.GetInt32();
                        if (statsElem.TryGetProperty("assists", out var assistsElem)) assists = assistsElem.GetInt32();
                        if (statsElem.TryGetProperty("deaths", out var deathsElem)) deaths = deathsElem.GetInt32();
                        if (statsElem.TryGetProperty("mvps", out var mvpsElem)) mvps = mvpsElem.GetInt32();
                        if (statsElem.TryGetProperty("score", out var scoreElem)) score = scoreElem.GetInt32();
                    }

                    bool hasBomb = false;
                    var weapons = new List<GsiWeapon>();
                    if (playerElem.TryGetProperty("weapons", out var weaponsElem))
                    {
                        foreach (var weaponProp in weaponsElem.EnumerateObject())
                        {
                            var weapon = weaponProp.Value;
                            var weaponName = weapon.TryGetProperty("name", out var weaponNameElem) ? weaponNameElem.GetString() ?? string.Empty : string.Empty;
                            var weaponType = weapon.TryGetProperty("type", out var typeElem) ? typeElem.GetString() ?? string.Empty : string.Empty;
                            var weaponState = weapon.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? string.Empty : string.Empty;
                            int ammoClip = weapon.TryGetProperty("ammo_clip", out var ammoElem) ? ammoElem.GetInt32() : 0;
                            int ammoClipMax = weapon.TryGetProperty("ammo_clip_max", out var ammoMaxElem) ? ammoMaxElem.GetInt32() : 0;
                            int ammoReserve = weapon.TryGetProperty("ammo_reserve", out var ammoReserveElem) ? ammoReserveElem.GetInt32() : 0;

                            weapons.Add(new GsiWeapon
                            {
                                Name = weaponName,
                                Type = weaponType,
                                State = weaponState,
                                AmmoClip = ammoClip,
                                AmmoClipMax = ammoClipMax,
                                AmmoReserve = ammoReserve
                            });

                            if (!string.IsNullOrWhiteSpace(weaponType))
                            {
                                if (string.Equals(weaponType, "C4", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasBomb = true;
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
                        IsAlive = health > 0,
                        HasBomb = hasBomb,
                        Slot = playerSlot,
                        Health = health,
                        Armor = armor,
                        HasHelmet = hasHelmet,
                        HasDefuseKit = hasDefuseKit,
                        Money = money,
                        EquipmentValue = equipmentValue,
                        RoundKills = roundKills,
                        RoundKillHs = roundKillHs,
                        Kills = kills,
                        Assists = assists,
                        Deaths = deaths,
                        Mvps = mvps,
                        Score = score,
                        Weapons = weapons
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
                Heartbeat = heartbeat,
                TeamCt = teamCt,
                TeamT = teamT,
                RoundNumber = roundNumber,
                RoundPhase = roundPhase,
                PhaseEndsIn = phaseEndsIn
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse GSI payload: {ex.Message}");
            return null;
        }
    }

    private static GsiTeam ParseTeam(System.Text.Json.JsonElement teamElem, string side)
    {
        return new GsiTeam
        {
            Side = side,
            Name = teamElem.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : side,
            Score = teamElem.TryGetProperty("score", out var s) ? s.GetInt32() : 0,
            ConsecutiveRoundLosses = teamElem.TryGetProperty("consecutive_round_losses", out var crl) ? crl.GetInt32() : 0,
            TimeoutsRemaining = teamElem.TryGetProperty("timeouts_remaining", out var to) ? to.GetInt32() : 0,
            MatchesWonThisSeries = teamElem.TryGetProperty("matches_won_this_series", out var m) ? m.GetInt32() : 0
        };
    }

    public void Dispose()
    {
        Stop();
        if (_listener != null)
        {
            _listener.Close();
            _listener = null;
        }
    }
}
