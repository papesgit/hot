using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HlaeObsTools.Services.WebSocket;

/// <summary>
/// WebSocket client for connecting to HLAE Observer Tools
/// Handles commands (GUI → HLAE) and state updates (HLAE → GUI)
/// </summary>
public class HlaeWebSocketClient : IDisposable
{
    private string _serverAddress;
    private int _serverPort;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly CancellationTokenSource _retryCancellationTokenSource = new();
    private Task? _receiveTask;
    private bool _disposed;
    private int _retryScheduled;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public event EventHandler<string>? MessageReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public HlaeWebSocketClient(string serverAddress = "127.0.0.1", int serverPort = 31338)
    {
        _serverAddress = serverAddress;
        _serverPort = serverPort;
    }

    public async Task<bool> ConnectAsync()
    {
        if (_disposed)
            return false;

        if (IsConnected)
            return true;

        try
        {
            _webSocket = new ClientWebSocket();
            _cancellationTokenSource = new CancellationTokenSource();

            var uri = new Uri($"ws://{_serverAddress}:{_serverPort}");
            await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);

            _receiveTask = Task.Run(() => ReceiveLoop(_cancellationTokenSource.Token));

            Connected?.Invoke(this, EventArgs.Empty);
            Console.WriteLine($"WebSocket connected to {uri}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket connection failed: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
            await DisconnectAsync();
            ScheduleReconnect();
            return false;
        }
    }

    public void ConfigureEndpoint(string serverAddress, int serverPort)
    {
        _serverAddress = serverAddress;
        _serverPort = serverPort;
    }

    public async Task ReconnectAsync()
    {
        await DisconnectAsync();
        await ConnectAsync();
    }

    public async Task DisconnectAsync()
    {
        if (_webSocket == null)
            return;

        _cancellationTokenSource?.Cancel();

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
            }
        }
        catch
        {
            // Ignore errors during close
        }

        _webSocket?.Dispose();
        _webSocket = null;

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        Disconnected?.Invoke(this, EventArgs.Empty);
        Console.WriteLine("WebSocket disconnected");
    }

    /// <summary>
    /// Send a command to HLAE
    /// </summary>
    public async Task SendCommandAsync(string commandName, object? args = null)
    {
        var command = new
        {
            type = "command",
            name = commandName,
            args = args
        };

        await SendJsonAsync(command);
    }

    /// <summary>
    /// Execute a console command on the HLAE side.
    /// </summary>
    public async Task SendExecCommandAsync(string command)
    {
        var message = new
        {
            type = "exec_cmd",
            cmd = command
        };

        await SendJsonAsync(message);
    }

    /// <summary>
    /// Request campath playback on the HLAE side.
    /// </summary>
    public async Task SendCampathPlayAsync(string campathPath, double offsetSeconds = 0)
    {
        var message = new
        {
            type = "campath_play",
            cmd = campathPath,
            offset = offsetSeconds
        };

        await SendJsonAsync(message);
    }

    /// <summary>
    /// Send raw JSON message to HLAE
    /// </summary>
    public async Task SendJsonAsync(object obj)
    {
        if (!IsConnected || _webSocket == null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            await _webSocket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket send error: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket != null)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await _webSocket.ReceiveAsync(segment, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await DisconnectAsync();
                    ScheduleReconnect();
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    MessageReceived?.Invoke(this, message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"WebSocket receive error: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex);
                await DisconnectAsync();
                ScheduleReconnect();
            }
        }
    }

    private void ScheduleReconnect()
    {
        if (_disposed || Interlocked.Exchange(ref _retryScheduled, 1) != 0)
        {
            return;
        }

        _ = RetryConnectAsync();
    }

    private async Task RetryConnectAsync()
    {
        try
        {
            while (!_disposed && !_retryCancellationTokenSource.IsCancellationRequested)
            {
                await Task.Delay(RetryDelay, _retryCancellationTokenSource.Token);

                if (IsConnected || await ConnectAsync())
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            Interlocked.Exchange(ref _retryScheduled, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _retryCancellationTokenSource.Cancel();
        DisconnectAsync().Wait();
        _retryCancellationTokenSource.Dispose();
    }
}
