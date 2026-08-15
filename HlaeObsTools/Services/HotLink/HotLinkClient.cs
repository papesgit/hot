using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.HotLink;

public sealed class HotLinkClient : IDisposable
{
    private readonly HotLinkSettings _settings;
    private readonly HttpClient _httpClient = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Guid _publisherSessionId;
    private long _lastEventId;
    private readonly List<HotLinkKillEvent> _recentEvents = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public HotLinkClient(HotLinkSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;
        ApplySettings();
    }

    public event EventHandler<HotLinkKillEvent[]>? EventsReceived;
    public event EventHandler? SessionChanged;
    public Guid PublisherSessionId => _publisherSessionId;
    public HotLinkKillEvent[] GetRecentEvents()
    {
        lock (_sync) return _recentEvents.ToArray();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HotLinkSettings.Role) or nameof(HotLinkSettings.ClientConnectionEnabled))
            ApplySettings();
    }

    private void ApplySettings()
    {
        if (_settings.IsClient && _settings.ClientConnectionEnabled)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        lock (_sync)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoopAsync(_cts.Token));
        }
        _settings.Status = "HOT Link client connecting.";
    }

    private void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync) { cts = _cts; _cts = null; }
        try { cts?.Cancel(); } catch { }
        _settings.Status = _settings.IsClient ? "HOT Link client disconnected." : "HOT Link disabled.";
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var response = await _httpClient.GetAsync(BuildUri(HotLinkProtocol.EventsPath, $"after={_lastEventId}"), token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<HotLinkEventEnvelope>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Publisher returned an empty event response.");
                if (envelope.ProtocolVersion != HotLinkProtocol.Version)
                    throw new InvalidOperationException($"Unsupported HOT Link protocol {envelope.ProtocolVersion}.");

                if (_publisherSessionId != envelope.PublisherSessionId)
                {
                    _publisherSessionId = envelope.PublisherSessionId;
                    _lastEventId = 0;
                    lock (_sync) _recentEvents.Clear();
                    SessionChanged?.Invoke(this, EventArgs.Empty);
                    if (envelope.Events.Length == 0 && envelope.LatestEventId > 0)
                        continue;
                }

                if (envelope.HasGap)
                    _settings.Status = "HOT Link event gap detected; resuming from retained events.";

                var events = envelope.Events.OrderBy(e => e.Id).ToArray();
                if (events.Length > 0)
                {
                    _lastEventId = Math.Max(_lastEventId, events[^1].Id);
                    lock (_sync)
                    {
                        foreach (var item in events)
                            if (_recentEvents.All(existing => existing.Id != item.Id)) _recentEvents.Add(item);
                        if (_recentEvents.Count > 256) _recentEvents.RemoveRange(0, _recentEvents.Count - 256);
                    }
                    EventsReceived?.Invoke(this, events);
                    var last = events[^1];
                    _settings.LastKill = $"Slots {last.Attacker.ObserverSlot + 1} → {last.Victim.ObserverSlot + 1} at {(last.GameTime?.ToString("F2", CultureInfo.InvariantCulture) ?? "unknown time")}.";
                }
                if (!envelope.HasGap)
                    _settings.Status = $"HOT Link connected. Last event {_lastEventId}.";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _settings.Status = $"HOT Link polling error: {ex.Message}"; }

            try { await Task.Delay(250, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<HotLinkReplayMarkResponse> RequestReplayMarkAsync(long eventId, CancellationToken token)
    {
        var request = new HotLinkReplayMarkRequest { PublisherSessionId = _publisherSessionId, EventId = eventId };
        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(BuildUri(HotLinkProtocol.ReplayMarkPath), content, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responseJson = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HotLinkReplayMarkResponse>(responseJson, JsonOptions)
            ?? throw new InvalidOperationException("Publisher returned an empty replay-mark response.");
    }

    private Uri BuildUri(string path, string? query = null)
    {
        var host = string.IsNullOrWhiteSpace(_settings.PublisherIp) ? "127.0.0.1" : _settings.PublisherIp;
        if (Uri.TryCreate(host, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.Host))
            host = absolute.Host;
        var builder = new UriBuilder(Uri.UriSchemeHttp, host, _settings.PublisherPort, path) { Query = query ?? string.Empty };
        return builder.Uri;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.PropertyChanged -= OnSettingsChanged;
        Stop();
        _httpClient.Dispose();
    }
}
