using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HlaeObsTools.Services.NetConsole;

/// <summary>
/// Lightweight TCP client for CS2 -netconport console using built-in TcpClient/NetworkStream.
/// Provides events for connection state and incoming text lines.
/// </summary>
public class Cs2NetConsoleClient : IDisposable
{
    private readonly string _address;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _disposed;
    private bool _connectionEstablished;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<Exception>? Error;

    public Cs2NetConsoleClient(string address, int port)
    {
        _address = address;
        _port = port;
    }

    public bool IsConnected => _client?.Connected == true && _stream is { CanRead: true, CanWrite: true };

    public async Task<bool> ConnectSafeAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Cs2NetConsoleClient));

        try
        {
            await DisconnectSafeAsync(false);

            _client = new TcpClient
            {
                NoDelay = true
            };

            await _client.ConnectAsync(_address, _port);
            _stream = _client.GetStream();
            _cts = new CancellationTokenSource();

            _connectionEstablished = true;
            Connected?.Invoke(this, EventArgs.Empty);

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
            await DisconnectSafeAsync(false);
            return false;
        }
    }

    public async Task DisconnectSafeAsync(bool raiseEvent = true)
    {
        if (_disposed)
            return;

        var shouldRaise = raiseEvent && (_connectionEstablished || (_client?.Connected ?? false) || _stream != null);

        try
        {
            _cts?.Cancel();
            if (_stream != null)
            {
                await _stream.FlushAsync();
            }
        }
        catch
        {
            // Ignore errors on flush
        }

        _stream?.Dispose();
        _stream = null;

        if (_client != null)
        {
            try
            {
                _client.Close();
            }
            catch
            {
                // Ignore close errors
            }
        }

        _client = null;
        _cts?.Dispose();
        _cts = null;

        _connectionEstablished = false;

        if (shouldRaise)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<bool> SendLineAsync(string message)
    {
        if (_stream == null || !_stream.CanWrite)
            return false;

        try
        {
            var text = message.EndsWith("\n", StringComparison.Ordinal) ? message : $"{message}\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            await _stream.WriteAsync(bytes, 0, bytes.Length);
            await _stream.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
            return false;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_stream == null)
            return;

        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break; // remote closed
                }

                var text = Encoding.UTF8.GetString(buffer, 0, read);
                MessageReceived?.Invoke(this, text);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect
        }
        catch (IOException ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                Error?.Invoke(this, ex);
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                Error?.Invoke(this, ex);
        }
        finally
        {
            await DisconnectSafeAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisconnectSafeAsync().GetAwaiter().GetResult();
        _receiveTask = null;
    }
}
