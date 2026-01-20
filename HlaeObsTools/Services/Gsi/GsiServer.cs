using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace HlaeObsTools.Services.Gsi;

/// <summary>
/// Lightweight HTTP listener for CS2 Game State Integration callbacks.
/// </summary>
public sealed class GsiServer : IDisposable
{
    private IHost? _host;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private long _heartbeat;
    private static readonly Dictionary<string, int> LastKnownObserverSlots = new(StringComparer.Ordinal);

    public event EventHandler<GsiGameState>? GameStateUpdated;

    public bool IsRunning => _host != null;

    public void Start(int port = 31337, string path = "/gsi/", string host = "0.0.0.0")
    {
        Stop();

        var normalizedPath = path.StartsWith("/") ? path : "/" + path;
        var basePath = normalizedPath.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = "/";

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(async () =>
        {
            try
            {
                var hostBuilder = new HostBuilder()
                    .ConfigureWebHost(webHost =>
                    {
                        webHost.UseKestrel();
                        webHost.UseUrls($"http://{NormalizeHost(host)}:{port}");
                        webHost.Configure(app =>
                        {
                            if (basePath == "/")
                            {
                                app.Run(HandleRequestAsync);
                                return;
                            }

                            app.Use(async (ctx, next) =>
                            {
                                var reqPath = ctx.Request.Path.Value ?? string.Empty;
                                if (string.Equals(reqPath, basePath, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(reqPath, basePath + "/", StringComparison.OrdinalIgnoreCase))
                                {
                                    await HandleRequestAsync(ctx).ConfigureAwait(false);
                                    return;
                                }
                                await next().ConfigureAwait(false);
                            });
                        });
                    });

                _host = hostBuilder.Build();
                await _host.StartAsync(_cts.Token).ConfigureAwait(false);
                Console.WriteLine($"GSI listener started on http://{host}:{port}{basePath}/");
                await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GSI listener error: {ex.Message}");
            }
        });
    }

    public void Stop()
    {
        var host = _host;
        var cts = _cts;
        _host = null;
        _cts = null;
        _runTask = null;

        if (host == null)
            return;

        Task.Run(async () =>
        {
            try
            {
                cts?.Cancel();
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await host.StopAsync(stopCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
                try
                {
                    host.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
            finally
            {
                cts?.Dispose();
            }
        });
    }

    private async Task HandleRequestAsync(HttpContext ctx)
    {
        try
        {
            if (!HttpMethods.IsPost(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var currentHeartbeat = Interlocked.Increment(ref _heartbeat);
            var state = ParseState(body, currentHeartbeat);
            if (state != null)
            {
                GameStateUpdated?.Invoke(this, state);
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GSI request handling failed: {ex.Message}");
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "127.0.0.1";
        if (host == "*" || host == "+")
            return "0.0.0.0";
        return host;
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
                    if (IsCoachName(pname))
                    {
                        continue;
                    }
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
                    if (playerSlot < 0 && LastKnownObserverSlots.TryGetValue(steamId, out var lastSlot))
                    {
                        playerSlot = lastSlot;
                    }
                    if (playerSlot >= 0)
                    {
                        LastKnownObserverSlots[steamId] = playerSlot;
                    }

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

            NormalizeObserverSlots(players);

            return new GsiGameState
            {
                RawJson = body,
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

    private static void NormalizeObserverSlots(List<GsiPlayer> players)
    {
        if (players.Count == 0)
            return;

        bool needsNormalize = players.Any(p =>
            p.Slot < 0 || p.Slot > 9 ||
            (string.Equals(p.Team, "CT", StringComparison.OrdinalIgnoreCase) && p.Slot >= 5) ||
            (string.Equals(p.Team, "T", StringComparison.OrdinalIgnoreCase) && p.Slot >= 0 && p.Slot < 5)) ||
            HasDuplicateSlots(players, "CT") ||
            HasDuplicateSlots(players, "T");

        if (!needsNormalize)
            return;

        NormalizeTeamSlots(players, "CT", 0);
        NormalizeTeamSlots(players, "T", 5);
    }

    private static void NormalizeTeamSlots(List<GsiPlayer> players, string team, int slotOffset)
    {
        var teamPlayers = players
            .Where(p => string.Equals(p.Team, team, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Slot < 0 ? int.MaxValue : p.Slot)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        if (teamPlayers.Count == 0)
            return;

        for (int i = 0; i < teamPlayers.Count; i++)
        {
            if (i < 5)
            {
                teamPlayers[i].Slot = slotOffset + i;
            }
            else
            {
                teamPlayers[i].Slot = -1;
            }
        }
    }

    private static bool HasDuplicateSlots(List<GsiPlayer> players, string team)
    {
        var seen = new HashSet<int>();
        foreach (var player in players)
        {
            if (!string.Equals(player.Team, team, StringComparison.OrdinalIgnoreCase))
                continue;
            if (player.Slot < 0 || player.Slot > 9)
                continue;
            if (!seen.Add(player.Slot))
                return true;
        }

        return false;
    }

    private static bool IsCoachName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        ReadOnlySpan<char> s = name.AsSpan().TrimStart();
        if (s.Length < 7) return false; // "coach" + sep + at least 1 char

        if (!s.StartsWith("coach", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsPipeLike(s[5]);
    }

    private static bool IsPipeLike(char c)
    {
        return c == '|'      // U+007C
            || c == '｜'     // U+FF5C
            || c == '¦'      // U+00A6
            || c == '∣';     // U+2223
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
    }
}
