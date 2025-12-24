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
    private string _serverAddress;
    private int _serverPort;
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
    private bool _mouseButton4;
    private bool _mouseButton5;
    private readonly byte[] _keyBitmap = new byte[InputPacket.KeyBitmapSize];
    private bool _analogEnabled;
    private float _analogLX;
    private float _analogLY;
    private float _analogRY;
    private float _analogRX;

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
    public void UpdateMouseButtons(bool left, bool right, bool middle, bool button4, bool button5)
    {
        lock (_inputLock)
        {
            _mouseLeft = left;
            _mouseRight = right;
            _mouseMiddle = middle;
            _mouseButton4 = button4;
            _mouseButton5 = button5;
        }
    }

    /// <summary>
    /// Update keyboard state from Keys enum
    /// </summary>
    public void UpdateKeys(Keys[] keysDown)
    {
        Span<byte> bitmap = stackalloc byte[InputPacket.KeyBitmapSize];
        bitmap.Clear();

        foreach (var key in keysDown)
        {
            int vk = ((int)key) & 0xFF; // Keys enum maps to VK codes in low byte
            if (vk <= 0 || vk >= 256)
                continue;

            int byteIndex = vk >> 3;
            int bitIndex = vk & 0x07;
            bitmap[byteIndex] |= (byte)(1 << bitIndex);
        }

        lock (_inputLock)
        {
            bitmap.CopyTo(_keyBitmap);
        }
    }

    public void SetAnalogEnabled(bool enabled)
    {
        lock (_inputLock)
        {
            _analogEnabled = enabled;
            if (!enabled)
            {
                _analogLX = 0.0f;
                _analogLY = 0.0f;
                _analogRY = 0.0f;
                _analogRX = 0.0f;
            }
        }
    }

    public void UpdateAnalog(float lx, float ly, float ry, float rx)
    {
        lock (_inputLock)
        {
            _analogLX = lx;
            _analogLY = ly;
            _analogRY = ry;
            _analogRX = rx;
        }
    }

    public bool TryGetAnalogState(out bool enabled, out float lx, out float ly, out float ry, out float rx)
    {
        lock (_inputLock)
        {
            enabled = _analogEnabled;
            lx = _analogLX;
            ly = _analogLY;
            ry = _analogRY;
            rx = _analogRX;
        }
        return enabled;
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
                InputPacketV2 packetV2;
                bool useAnalog;
                lock (_inputLock)
                {
                    var keyBitmapSnapshot = new byte[InputPacket.KeyBitmapSize];
                    Array.Copy(_keyBitmap, keyBitmapSnapshot, InputPacket.KeyBitmapSize);

                    useAnalog = _analogEnabled;
                    if (useAnalog)
                    {
                        packetV2 = new InputPacketV2
                        {
                            Version = 2,
                            Flags = 0x01,
                            Reserved = 0,
                            Sequence = _sequence++,
                            MouseDx = (short)Math.Clamp(_mouseDx, short.MinValue, short.MaxValue),
                            MouseDy = (short)Math.Clamp(_mouseDy, short.MinValue, short.MaxValue),
                            MouseWheel = (sbyte)Math.Clamp(_mouseWheel, sbyte.MinValue, sbyte.MaxValue),
                            MouseButtons = (byte)(
                                (_mouseLeft ? 0x01 : 0x00) |
                                (_mouseRight ? 0x02 : 0x00) |
                                (_mouseMiddle ? 0x04 : 0x00) |
                                (_mouseButton4 ? 0x08 : 0x00) |
                                (_mouseButton5 ? 0x10 : 0x00)
                            ),
                            KeyBitmap = keyBitmapSnapshot,
                            Timestamp = (ulong)_stopwatch.Elapsed.TotalMicroseconds,
                            AnalogLX = _analogLX,
                            AnalogLY = _analogLY,
                            AnalogRY = _analogRY,
                            AnalogRX = _analogRX
                        };
                        packet = default;
                    }
                    else
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
                                (_mouseMiddle ? 0x04 : 0x00) |
                                (_mouseButton4 ? 0x08 : 0x00) |
                                (_mouseButton5 ? 0x10 : 0x00)
                            ),
                            KeyBitmap = keyBitmapSnapshot,
                            Timestamp = (ulong)_stopwatch.Elapsed.TotalMicroseconds
                        };
                        packetV2 = default;
                    }

                    // Clear accumulated deltas
                    _mouseDx = 0;
                    _mouseDy = 0;
                    _mouseWheel = 0;
                }

                // Send packet
                var bytes = useAnalog ? packetV2.ToBytes() : packet.ToBytes();
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

    public void ConfigureEndpoint(string serverAddress, int serverPort, bool restartIfActive = true)
    {
        var wasActive = IsActive;
        if (IsActive && restartIfActive)
        {
            Stop();
        }

        _serverAddress = serverAddress;
        _serverPort = serverPort;

        if (wasActive && restartIfActive)
        {
            Start();
        }
    }
}
