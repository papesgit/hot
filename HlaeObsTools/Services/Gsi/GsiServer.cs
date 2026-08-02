using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace HlaeObsTools.Services.Gsi;

public enum GsiRelayHealthLevel
{
    Unknown,
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record GsiRelayEndpointHealth(
    string Endpoint,
    GsiRelayHealthLevel Level,
    string Message,
    DateTimeOffset? LastUpdatedUtc);

/// <summary>
/// Lightweight HTTP listener for CS2 Game State Integration callbacks.
/// </summary>
public sealed class GsiServer : IDisposable
{
    private sealed class PlayerDefuserTracker
    {
        public bool WasAlive { get; set; }
        public bool HadDefuser { get; set; }
        public Vec3 LastAlivePosition { get; set; }
    }

    private sealed class RelayEnvelope
    {
        public required byte[] RawBody { get; init; }
        public required Dictionary<string, string[]> Headers { get; init; }
    }

    private sealed class RelayEndpointState
    {
        public RelayEndpointState(Uri endpoint, CancellationTokenSource cancellation)
        {
            Endpoint = endpoint;
            Cancellation = cancellation;
        }

        public Uri Endpoint { get; }
        public CancellationTokenSource Cancellation { get; }
        public object Sync { get; } = new();
        public RelayEnvelope? PendingPayload { get; set; }
        public bool IsWorkerRunning { get; set; }
        public GsiRelayHealthLevel HealthLevel { get; set; } = GsiRelayHealthLevel.Unknown;
        public string HealthMessage { get; set; } = "No relay attempt yet.";
        public DateTimeOffset? LastUpdatedUtc { get; set; }
    }

    private readonly object _lifecycleLock = new();
    private readonly object _relayLock = new();
    private readonly object _trackingLock = new();
    private readonly CancellationTokenSource _relayShutdownCts = new();
    private readonly HttpClient _relayHttpClient = CreateRelayHttpClient();
    private Dictionary<string, RelayEndpointState> _relayEndpointStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlayerDefuserTracker> _playerDefuserTrackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GsiDroppedDefuser> _droppedDefusers = new(StringComparer.Ordinal);
    private IHost? _host;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private long _heartbeat;
    private long _lastRequestUtcTicks;
    private long _nextDroppedDefuserId;
    private string? _lastTrackedMapName;
    private string? _lastTrackedRoundPhase;
    private static readonly Dictionary<string, int> LastKnownObserverSlots = new(StringComparer.Ordinal);
    private static readonly HashSet<string> HopByHopRelayHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
        "Content-Length"
    };

    public event EventHandler<GsiGameState>? GameStateUpdated;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _runTask != null;
            }
        }
    }

    public DateTimeOffset? LastRequestUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastRequestUtcTicks);
            if (ticks <= 0)
                return null;
            return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        }
    }

    public void ConfigureRelayEndpoints(IEnumerable<string> relayUris)
    {
        var normalized = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relayUri in relayUris ?? Enumerable.Empty<string>())
        {
            var value = relayUri?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                continue;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Add(uri.AbsoluteUri))
                continue;

            normalized.Add(uri);
        }

        RelayEndpointState[] removedStates;
        lock (_relayLock)
        {
            var nextStates = new Dictionary<string, RelayEndpointState>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in normalized)
            {
                var key = endpoint.AbsoluteUri;
                if (_relayEndpointStates.TryGetValue(key, out var existing))
                {
                    nextStates[key] = existing;
                    continue;
                }

                var endpointCts = CancellationTokenSource.CreateLinkedTokenSource(_relayShutdownCts.Token);
                nextStates[key] = new RelayEndpointState(endpoint, endpointCts);
            }

            removedStates = _relayEndpointStates
                .Where(kvp => !nextStates.ContainsKey(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToArray();
            _relayEndpointStates = nextStates;
        }

        foreach (var removed in removedStates)
        {
            try
            {
                removed.Cancellation.Cancel();
            }
            catch
            {
                // ignore cancellation errors
            }

            try
            {
                removed.Cancellation.Dispose();
            }
            catch
            {
                // ignore dispose errors
            }
        }
    }

    public IReadOnlyList<GsiRelayEndpointHealth> GetRelayEndpointHealthSnapshot()
    {
        RelayEndpointState[] states;
        lock (_relayLock)
        {
            states = _relayEndpointStates.Values.ToArray();
        }

        var result = new List<GsiRelayEndpointHealth>(states.Length);
        foreach (var state in states)
        {
            lock (state.Sync)
            {
                result.Add(new GsiRelayEndpointHealth(
                    state.Endpoint.AbsoluteUri,
                    state.HealthLevel,
                    state.HealthMessage,
                    state.LastUpdatedUtc));
            }
        }

        return result;
    }

    public void Start(int port = 31337, string path = "/gsi/")
    {
        Stop();

        var normalizedPath = path.StartsWith("/") ? path : "/" + path;
        var basePath = normalizedPath.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = "/";

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(async () =>
        {
            IHost? localHost = null;
            try
            {
                var hostBuilder = new HostBuilder()
                    .ConfigureWebHost(webHost =>
                    {
                        webHost.UseKestrel(options => options.ListenAnyIP(port));
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

                localHost = hostBuilder.Build();
                lock (_lifecycleLock)
                {
                    _host = localHost;
                }

                await localHost.StartAsync(cts.Token).ConfigureAwait(false);
                Console.WriteLine($"GSI listener started on http://0.0.0.0:{port}{basePath}/");
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GSI listener error: {ex.Message}");
            }
            finally
            {
                if (localHost != null)
                {
                    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await localHost.StopAsync(stopCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore
                    }

                    try
                    {
                        localHost.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                lock (_lifecycleLock)
                {
                    if (ReferenceEquals(_host, localHost))
                    {
                        _host = null;
                    }
                }
            }
        });

        lock (_lifecycleLock)
        {
            _cts = cts;
            _runTask = runTask;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? runTask;
        lock (_lifecycleLock)
        {
            cts = _cts;
            runTask = _runTask;
            _cts = null;
            _runTask = null;
        }

        if (runTask == null)
        {
            cts?.Dispose();
            return;
        }

        try
        {
            cts?.Cancel();
            runTask.GetAwaiter().GetResult();
        }
        catch
        {
            // ignore shutdown errors
        }
        finally
        {
            cts?.Dispose();
            lock (_trackingLock)
            {
                ResetDroppedDefuserTracking();
                _lastTrackedMapName = null;
            }
        }
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

            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer).ConfigureAwait(false);
            var rawBody = buffer.ToArray();
            var headers = CaptureRelayHeaders(ctx.Request.Headers);
            var body = Encoding.UTF8.GetString(rawBody);
            Interlocked.Exchange(ref _lastRequestUtcTicks, DateTime.UtcNow.Ticks);

            RelayPayload(new RelayEnvelope
            {
                RawBody = rawBody,
                Headers = headers
            });

            var currentHeartbeat = Interlocked.Increment(ref _heartbeat);
            var state = ParseState(body, currentHeartbeat);
            if (state != null)
            {
                state = ApplyDroppedDefuserTracking(state);
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

    private void RelayPayload(RelayEnvelope payload)
    {
        RelayEndpointState[] relayStates;
        lock (_relayLock)
        {
            relayStates = _relayEndpointStates.Values.ToArray();
        }

        if (relayStates.Length == 0)
            return;

        foreach (var relayState in relayStates)
        {
            QueueRelayPayload(relayState, payload);
        }
    }

    private void QueueRelayPayload(RelayEndpointState relayState, RelayEnvelope payload)
    {
        if (relayState.Cancellation.IsCancellationRequested)
            return;

        var shouldStartWorker = false;
        lock (relayState.Sync)
        {
            // Latest payload wins while a worker is busy.
            relayState.PendingPayload = payload;
            if (!relayState.IsWorkerRunning)
            {
                relayState.IsWorkerRunning = true;
                shouldStartWorker = true;
            }
        }

        if (shouldStartWorker)
        {
            _ = RelayEndpointLoopAsync(relayState, relayState.Cancellation.Token);
        }
    }

    private async Task RelayEndpointLoopAsync(RelayEndpointState relayState, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                lock (relayState.Sync)
                {
                    relayState.PendingPayload = null;
                    relayState.IsWorkerRunning = false;
                }
                return;
            }

            RelayEnvelope? payload;
            lock (relayState.Sync)
            {
                payload = relayState.PendingPayload;
                relayState.PendingPayload = null;
                if (payload == null)
                {
                    relayState.IsWorkerRunning = false;
                    return;
                }
            }

            await PostRelayAsync(relayState.Endpoint, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PostRelayAsync(Uri endpoint, RelayEnvelope payload, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new ByteArrayContent(payload.RawBody);

            foreach (var header in payload.Headers)
            {
                if (HopByHopRelayHeaders.Contains(header.Key))
                    continue;

                if (request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    continue;

                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content.Headers.ContentType == null)
            {
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }

            using var response = await _relayHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"GSI relay failed ({(int)response.StatusCode}) {endpoint}");
                SetRelayHealth(endpoint, GsiRelayHealthLevel.Unhealthy, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            else
            {
                SetRelayHealth(endpoint, GsiRelayHealthLevel.Healthy, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"GSI relay timeout {endpoint}");
            SetRelayHealth(endpoint, GsiRelayHealthLevel.Degraded, "Request timed out.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GSI relay error {endpoint}: {ex.Message}");
            SetRelayHealth(endpoint, GsiRelayHealthLevel.Unhealthy, ex.Message);
        }
    }

    private void SetRelayHealth(Uri endpoint, GsiRelayHealthLevel level, string message)
    {
        RelayEndpointState? state;
        lock (_relayLock)
        {
            _relayEndpointStates.TryGetValue(endpoint.AbsoluteUri, out state);
        }

        if (state == null)
            return;

        lock (state.Sync)
        {
            state.HealthLevel = level;
            state.HealthMessage = message;
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    private static Dictionary<string, string[]> CaptureRelayHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            result[header.Key] = header.Value
                .Select(value => value ?? string.Empty)
                .ToArray();
        }

        return result;
    }

    private static HttpClient CreateRelayHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Prevent system proxy settings from interfering with localhost relays.
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private GsiGameState ApplyDroppedDefuserTracking(GsiGameState state)
    {
        lock (_trackingLock)
        {
            if (!string.Equals(_lastTrackedMapName, state.MapName, StringComparison.OrdinalIgnoreCase))
            {
                ResetDroppedDefuserTracking();
                _lastTrackedMapName = state.MapName;
            }

            var currentPhase = state.RoundPhase;
            var wasFreezetime = string.Equals(_lastTrackedRoundPhase, "freezetime", StringComparison.OrdinalIgnoreCase);
            var isFreezetime = string.Equals(currentPhase, "freezetime", StringComparison.OrdinalIgnoreCase);
            if (!wasFreezetime && isFreezetime)
            {
                _droppedDefusers.Clear();
            }

            var currentPlayerIds = new HashSet<string>(state.Players.Select(p => p.SteamId), StringComparer.Ordinal);
            var stalePlayerIds = _playerDefuserTrackers.Keys
                .Where(id => !currentPlayerIds.Contains(id))
                .ToList();
            foreach (var stalePlayerId in stalePlayerIds)
            {
                _playerDefuserTrackers.Remove(stalePlayerId);
            }

            var shouldInferMidRoundTransitions = !isFreezetime && !string.IsNullOrWhiteSpace(currentPhase);

            foreach (var player in state.Players)
            {
                var hadPreviousSnapshot = _playerDefuserTrackers.TryGetValue(player.SteamId, out var tracker);
                tracker ??= new PlayerDefuserTracker();

                if (hadPreviousSnapshot)
                {
                    if (tracker.WasAlive && !player.IsAlive && tracker.HadDefuser && shouldInferMidRoundTransitions)
                    {
                        var dropId = $"defuser-{Interlocked.Increment(ref _nextDroppedDefuserId)}";
                        var dropPosition = tracker.LastAlivePosition != default ? tracker.LastAlivePosition : player.Position;
                        _droppedDefusers[dropId] = new GsiDroppedDefuser
                        {
                            Id = dropId,
                            DroppedBySteamId = player.SteamId,
                            Position = dropPosition
                        };
                    }

                    if (!tracker.HadDefuser && player.HasDefuseKit && player.IsAlive && shouldInferMidRoundTransitions)
                    {
                        var closest = _droppedDefusers
                            .OrderBy(kvp => GetDistanceSquared(kvp.Value.Position, player.Position))
                            .FirstOrDefault();

                        if (!string.IsNullOrEmpty(closest.Key))
                        {
                            _droppedDefusers.Remove(closest.Key);
                        }
                    }
                }

                tracker.WasAlive = player.IsAlive;
                tracker.HadDefuser = player.HasDefuseKit;
                if (player.IsAlive)
                {
                    tracker.LastAlivePosition = player.Position;
                }

                _playerDefuserTrackers[player.SteamId] = tracker;
            }

            _lastTrackedRoundPhase = currentPhase;
            state.DroppedDefusers = _droppedDefusers.Values.ToArray();
            return state;
        }
    }

    private void ResetDroppedDefuserTracking()
    {
        _playerDefuserTrackers.Clear();
        _droppedDefusers.Clear();
        _lastTrackedRoundPhase = null;
    }

    private static double GetDistanceSquared(Vec3 a, Vec3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
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
            string? playerActivity = null;
            if (root.TryGetProperty("player", out var focusedPlayerElem) && focusedPlayerElem.TryGetProperty("steamid", out var steamIdElem))
            {
                focusedPlayerSteamId = steamIdElem.GetString();
            }
            if (root.TryGetProperty("player", out focusedPlayerElem) && focusedPlayerElem.TryGetProperty("activity", out var activityElem))
            {
                playerActivity = activityElem.GetString();
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
                PlayerActivity = playerActivity,
                HasAllPlayers = root.TryGetProperty("allplayers", out _),
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
        _relayShutdownCts.Cancel();

        RelayEndpointState[] states;
        lock (_relayLock)
        {
            states = _relayEndpointStates.Values.ToArray();
            _relayEndpointStates.Clear();
        }

        foreach (var state in states)
        {
            try
            {
                state.Cancellation.Cancel();
            }
            catch
            {
                // ignore cancellation errors
            }

            try
            {
                state.Cancellation.Dispose();
            }
            catch
            {
                // ignore dispose errors
            }
        }

        _relayShutdownCts.Dispose();
        _relayHttpClient.Dispose();
    }
}
