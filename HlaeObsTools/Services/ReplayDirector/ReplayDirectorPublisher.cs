using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.Vmix;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace HlaeObsTools.Services.ReplayDirector;

public sealed class ReplayDirectorPublisher : IDisposable
{
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly GsiServer _gsiServer;
    private readonly ReplayDirectorSettings _settings;
    private readonly VmixReplaySettings _replaySettings;
    private readonly VmixReplayMarker _delayedReplayMarker;
    private readonly object _sync = new();
    private readonly List<ReplayDirectorKillEvent> _events = new();
    private IHost? _host;
    private CancellationTokenSource? _cts;
    private long _nextId;
    private int _focusedSlot = -1;
    private int _roundNumber;
    private int _labelRoundNumber;
    private string _mapName = string.Empty;
    private string _roundPhase = string.Empty;
    private Dictionary<int, int> _roundKillsBySlot = new();
    private readonly Dictionary<(int Round, string Player), int> _roundKillCounts = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ReplayDirectorPublisher(
        HlaeWebSocketClient webSocketClient,
        GsiServer gsiServer,
        ReplayDirectorSettings settings,
        VmixReplaySettings replaySettings,
        VmixReplayMarker delayedReplayMarker)
    {
        _webSocketClient = webSocketClient;
        _gsiServer = gsiServer;
        _settings = settings;
        _replaySettings = replaySettings;
        _delayedReplayMarker = delayedReplayMarker;
        _webSocketClient.MessageReceived += OnWebSocketMessage;
        _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _delayedReplayMarker.StatusChanged += OnDelayedReplayMarkerStatusChanged;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReplayDirectorSettings.Role) ||
                e.PropertyName == nameof(ReplayDirectorSettings.PublisherPort))
            {
                ApplyRole();
            }
        };
        ApplyRole();
    }

    private void ApplyRole()
    {
        if (string.Equals(_settings.Role, "Main Publisher", StringComparison.Ordinal))
            Start();
        else
            Stop();
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        var focusedSlot = -1;
        if (!string.IsNullOrWhiteSpace(state.FocusedPlayerSteamId))
        {
            var focused = state.Players.FirstOrDefault(p => string.Equals(p.SteamId, state.FocusedPlayerSteamId, StringComparison.Ordinal));
            focusedSlot = focused?.Slot ?? -1;
        }

        lock (_sync)
        {
            var mapName = state.MapName ?? string.Empty;
            if (!string.Equals(mapName, _mapName, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(mapName))
            {
                _mapName = mapName;
                _labelRoundNumber = 0;
                _roundKillCounts.Clear();
            }

            _focusedSlot = focusedSlot;
            _roundNumber = state.RoundNumber;
            _roundPhase = state.RoundPhase ?? string.Empty;
            _roundKillsBySlot = state.Players
                .Where(p => p.Slot >= 0)
                .ToDictionary(p => p.Slot, p => p.RoundKills);
        }
    }

    private void OnWebSocketMessage(object? sender, string message)
    {
        if (!string.Equals(_settings.Role, "Main Publisher", StringComparison.Ordinal))
            return;

        int focusedSlot;
        int roundNumber;
        string roundPhase;
        Dictionary<int, int> roundKillsBySlot;
        lock (_sync)
        {
            focusedSlot = _focusedSlot;
            roundNumber = _roundNumber;
            roundPhase = _roundPhase;
            roundKillsBySlot = new Dictionary<int, int>(_roundKillsBySlot);
        }

        if (!ReplayDirectorKillEvent.TryParseHlaeKillfeed(message, slot => slot == focusedSlot, roundNumber, roundPhase, out var kill) || kill == null)
            return;

        lock (_sync)
        {
            var labelRound = GetLabelRound(roundNumber, roundPhase);
            kill.LabelRoundNumber = labelRound;
            kill.RoundKillNumber = GetNextRoundKill(kill.AttackerName, labelRound);
            if (kill.Attacker != null && roundKillsBySlot.TryGetValue(kill.AttackerSlot, out var attackerRoundKills))
            {
                kill.Attacker.RoundKills = Math.Max(0, attackerRoundKills);
            }

            kill.Id = ++_nextId;
            _events.Add(kill);
            if (_events.Count > 256)
                _events.RemoveRange(0, _events.Count - 256);
        }

        _settings.LastKill = $"{kill.AttackerName} kill at {(kill.GameTime.HasValue ? kill.GameTime.Value.ToString("F2") : "no time")} ({(kill.MainCaught ? "main caught" : "uncaught")})";
    }

    private void OnDelayedReplayMarkerStatusChanged(object? sender, string status)
    {
        _settings.LastVmixMark = status;
    }

    private int GetLabelRound(int roundNumber, string roundPhase)
    {
        var phase = (roundPhase ?? string.Empty).ToUpperInvariant();
        if (!string.Equals(phase, "OVER", StringComparison.Ordinal))
        {
            if (roundNumber > 0)
                _labelRoundNumber = roundNumber;
        }
        else if (_labelRoundNumber == 0 && roundNumber > 0)
        {
            _labelRoundNumber = Math.Max(1, roundNumber - 1);
        }

        var labelRound = _labelRoundNumber > 0 ? _labelRoundNumber : roundNumber;
        CleanupOldRoundKillCounts(labelRound);
        return labelRound > 0 ? labelRound : roundNumber;
    }

    private void CleanupOldRoundKillCounts(int labelRound)
    {
        if (labelRound <= 0)
            return;

        var keysToRemove = new List<(int Round, string Player)>();
        foreach (var key in _roundKillCounts.Keys)
        {
            if (key.Round < labelRound - 2)
                keysToRemove.Add(key);
        }

        foreach (var key in keysToRemove)
        {
            _roundKillCounts.Remove(key);
        }
    }

    private int GetNextRoundKill(string playerName, int roundNumber)
    {
        var key = (roundNumber, playerName);
        _roundKillCounts.TryGetValue(key, out var count);
        count++;
        _roundKillCounts[key] = count;
        return count;
    }

    private void Start()
    {
        lock (_sync)
        {
            if (_host != null)
                return;
        }

        var cts = new CancellationTokenSource();
        var port = _settings.PublisherPort;
        _ = Task.Run(async () =>
        {
            IHost? host = null;
            try
            {
                host = new HostBuilder()
                    .ConfigureWebHost(webHost =>
                    {
                        webHost.UseKestrel(options => options.ListenAnyIP(port));
                        webHost.Configure(app =>
                        {
                            app.Map("/replay-director/events", builder => builder.Run(HandleEventsAsync));
                            app.Map("/replay-director/replay/mark", builder => builder.Run(HandleReplayMarkAsync));
                            app.Map("/replay-director/health", builder => builder.Run(HandleHealthAsync));
                        });
                    })
                    .Build();

                lock (_sync)
                {
                    _host = host;
                    _cts = cts;
                }

                await host.StartAsync(cts.Token).ConfigureAwait(false);
                _settings.Status = $"Publisher running on port {port}.";
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _settings.Status = $"Publisher error: {ex.Message}";
            }
            finally
            {
                if (host != null)
                {
                    try { await host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                    host.Dispose();
                }

                lock (_sync)
                {
                    if (ReferenceEquals(_host, host))
                    {
                        _host = null;
                        _cts = null;
                    }
                }
            }
        }, cts.Token);
    }

    private void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _cts;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { }
        if (!string.Equals(_settings.Role, "Main Publisher", StringComparison.Ordinal))
            _settings.Status = "Replay director disabled.";
    }

    private Task HandleHealthAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json";
        long lastId;
        int count;
        lock (_sync)
        {
            lastId = _nextId;
            count = _events.Count;
        }
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, lastId, count }, JsonOptions));
    }

    private Task HandleEventsAsync(HttpContext ctx)
    {
        var after = 0L;
        if (ctx.Request.Query.TryGetValue("after", out var rawAfter))
            long.TryParse(rawAfter.ToString(), out after);

        ReplayDirectorKillEvent[] events;
        lock (_sync)
        {
            events = _events.Where(e => e.Id > after).ToArray();
        }

        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { events }, JsonOptions));
    }

    private async Task HandleReplayMarkAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        ReplayDirectorReplayMarkRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ReplayDirectorReplayMarkRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = "Invalid JSON" }, JsonOptions)).ConfigureAwait(false);
            return;
        }

        if (request?.Kill == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = "Missing kill" }, JsonOptions)).ConfigureAwait(false);
            return;
        }

        _settings.LastVmixMark = $"Remote delayed mark: {request.Kill.AttackerName}";
        _delayedReplayMarker.RecordKill(request.Kill, _replaySettings, _settings);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true }, JsonOptions)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _webSocketClient.MessageReceived -= OnWebSocketMessage;
        _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _delayedReplayMarker.StatusChanged -= OnDelayedReplayMarkerStatusChanged;
    }
}
