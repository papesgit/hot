using System;
using HlaeObsTools.Services.WebSocket;

namespace HlaeObsTools.Services.Gsi;

/// <summary>
/// Refreshes spectator bindings after CS2 returns from the menu and publishes a player roster.
/// </summary>
public sealed class SpectatorBindingsRefreshCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly GsiServer _gsiServer;
    private bool _wasInMenu;
    private bool _awaitingRoster;
    private bool _sawRosterUnavailable;
    private bool _disposed;

    public SpectatorBindingsRefreshCoordinator(HlaeWebSocketClient webSocketClient, GsiServer gsiServer)
    {
        _webSocketClient = webSocketClient;
        _gsiServer = gsiServer;
        _gsiServer.GameStateUpdated += OnGameStateUpdated;
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        var isInMenu = string.Equals(state.PlayerActivity, "menu", StringComparison.OrdinalIgnoreCase);
        var requestRefresh = false;

        lock (_sync)
        {
            if (_disposed)
                return;

            if (isInMenu && !_wasInMenu)
            {
                _awaitingRoster = true;
                _sawRosterUnavailable = !state.HasAllPlayers || state.Players.Count == 0;
            }
            else if (_awaitingRoster && (!state.HasAllPlayers || state.Players.Count == 0))
            {
                _sawRosterUnavailable = true;
            }
            else if (_awaitingRoster && _sawRosterUnavailable && state.HasAllPlayers && state.Players.Count > 0)
            {
                _awaitingRoster = false;
                requestRefresh = true;
            }

            _wasInMenu = isInMenu;
        }

        if (requestRefresh)
        {
            Console.WriteLine("Spectator bindings refresh requested after GSI player roster became available");
            _ = _webSocketClient.SendCommandAsync("refresh_binds");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _gsiServer.GameStateUpdated -= OnGameStateUpdated;
    }
}
