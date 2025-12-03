using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HlaeObsTools.Services.Input;

/// <summary>
/// UDP input sender for ultra-low latency freecam control
/// Sends input at 240Hz (~4.16ms intervals)
/// </summary>
public class HlaeInputSender : IDisposable
{
    private readonly string _serverAddress;
    private readonly int _serverPort;
    private UdpClient? _udpClient;
    private IPEndPoint? _serverEndpoint;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _sendTask;
    private bool _disposed;

    private uint _sequence;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    // Current input state
    private int _mouseDx;
    private int _mouseDy;
    private int _mouseWheel;
    private bool _mouseLeft;
    private bool _mouseRight;
    private bool _mouseMiddle;
    private ulong _keysDown;

    private readonly object _inputLock = new();

    public bool IsActive { get; private set; }
    public int SendRate { get; set; } = 240; // Hz

    public HlaeInputSender(string serverAddress = "127.0.0.1", int serverPort = 31339)
    {
        _serverAddress = serverAddress;
        _serverPort = serverPort;
    }

    public void Start()
    {
        if (IsActive)
            return;

        try
        {
            _udpClient = new UdpClient();
            _serverEndpoint = new IPEndPoint(IPAddress.Parse(_serverAddress), _serverPort);

            _sequence = 0;
            _cancellationTokenSource = new CancellationTokenSource();
            _sendTask = Task.Run(() => SendLoop(_cancellationTokenSource.Token));

            IsActive = true;
            Console.WriteLine($"UDP input sender started: {_serverAddress}:{_serverPort} @ {SendRate}Hz");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start UDP input sender: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        if (!IsActive)
            return;

        IsActive = false;

        _cancellationTokenSource?.Cancel();
        _sendTask?.Wait(TimeSpan.FromSeconds(1));

        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        Console.WriteLine("UDP input sender stopped");
    }

    /// <summary>
    /// Update mouse movement (accumulates delta)
    /// </summary>
    public void UpdateMouse(int dx, int dy, int wheel)
    {
        lock (_inputLock)
        {
            _mouseDx += dx;
            _mouseDy += dy;
            _mouseWheel += wheel;
        }
    }

    /// <summary>
    /// Update mouse buttons
    /// </summary>
    public void UpdateMouseButtons(bool left, bool right, bool middle)
    {
        lock (_inputLock)
        {
            _mouseLeft = left;
            _mouseRight = right;
            _mouseMiddle = middle;
        }
    }

    /// <summary>
    /// Update keyboard state from Keys enum
    /// </summary>
    public void UpdateKeys(Keys[] keysDown)
    {
        ulong keyBits = 0;

        foreach (var key in keysDown)
        {
            switch (key)
            {
                case Keys.W:
                    keyBits |= (1UL << KeyBits.W);
                    break;
                case Keys.A:
                    keyBits |= (1UL << KeyBits.A);
                    break;
                case Keys.S:
                    keyBits |= (1UL << KeyBits.S);
                    break;
                case Keys.D:
                    keyBits |= (1UL << KeyBits.D);
                    break;
                case Keys.Space:
                    keyBits |= (1UL << KeyBits.Space);
                    break;
                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey:
                    keyBits |= (1UL << KeyBits.Ctrl);
                    break;
                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                    keyBits |= (1UL << KeyBits.Shift);
                    break;
                case Keys.Q:
                    keyBits |= (1UL << KeyBits.Q);
                    break;
                case Keys.E:
                    keyBits |= (1UL << KeyBits.E);
                    break;
                case Keys.D1:
                    keyBits |= (1UL << KeyBits.Key1);
                    break;
                case Keys.D2:
                    keyBits |= (1UL << KeyBits.Key2);
                    break;
                case Keys.D3:
                    keyBits |= (1UL << KeyBits.Key3);
                    break;
                case Keys.D4:
                    keyBits |= (1UL << KeyBits.Key4);
                    break;
                case Keys.D5:
                    keyBits |= (1UL << KeyBits.Key5);
                    break;
                case Keys.D6:
                    keyBits |= (1UL << KeyBits.Key6);
                    break;
                case Keys.D7:
                    keyBits |= (1UL << KeyBits.Key7);
                    break;
                case Keys.D8:
                    keyBits |= (1UL << KeyBits.Key8);
                    break;
                case Keys.D9:
                    keyBits |= (1UL << KeyBits.Key9);
                    break;
                case Keys.D0:
                    keyBits |= (1UL << KeyBits.Key0);
                    break;
            }
        }

        lock (_inputLock)
        {
            _keysDown = keyBits;
        }
    }

    private async Task SendLoop(CancellationToken cancellationToken)
    {
        var intervalMs = 1000.0 / SendRate;
        var intervalTicks = (long)(Stopwatch.Frequency * intervalMs / 1000.0);
        var nextSendTime = _stopwatch.ElapsedTicks;

        while (!cancellationToken.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                // Wait for next send time (high precision)
                var now = _stopwatch.ElapsedTicks;
                if (now < nextSendTime)
                {
                    var waitMs = (int)((nextSendTime - now) * 1000 / Stopwatch.Frequency);
                    if (waitMs > 0)
                    {
                        await Task.Delay(waitMs, cancellationToken);
                    }
                }

                nextSendTime += intervalTicks;

                // Build and send packet
                InputPacket packet;
                lock (_inputLock)
                {
                    packet = new InputPacket
                    {
                        Sequence = _sequence++,
                        MouseDx = (short)Math.Clamp(_mouseDx, short.MinValue, short.MaxValue),
                        MouseDy = (short)Math.Clamp(_mouseDy, short.MinValue, short.MaxValue),
                        MouseWheel = (sbyte)Math.Clamp(_mouseWheel, sbyte.MinValue, sbyte.MaxValue),
                        MouseButtons = (byte)(
                            (_mouseLeft ? 0x01 : 0x00) |
                            (_mouseRight ? 0x02 : 0x00) |
                            (_mouseMiddle ? 0x04 : 0x00)
                        ),
                        KeysDown = _keysDown,
                        Timestamp = (ulong)_stopwatch.Elapsed.TotalMicroseconds
                    };

                    // Clear accumulated deltas
                    _mouseDx = 0;
                    _mouseDy = 0;
                    _mouseWheel = 0;
                }

                // Send packet
                var bytes = packet.ToBytes();
                await _udpClient.SendAsync(bytes, bytes.Length, _serverEndpoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"UDP send error: {ex.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}
