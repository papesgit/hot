using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Video.FFmpeg;

namespace HlaeObsTools.Services.Video.RTP;

/// <summary>
/// Configuration for RTP video receiver
/// </summary>
public class RtpReceiverConfig
{
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5000;
    public byte PayloadType { get; set; } = 96;
}

/// <summary>
/// RTP video receiver that handles UDP reception, H.264 depayloading, and decoding
/// </summary>
public class RtpVideoReceiver : IVideoSource
{
    private readonly RtpReceiverConfig _config;
    private readonly H264Depayloader _depayloader;
    private readonly FFmpegDecoder _decoder;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveTask;
    private readonly List<byte> _h264Buffer;
    private readonly List<byte[]> _accessUnitBuffer;  // Buffer for complete access unit
    private DateTime _lastAccessUnitTime;
    private bool _disposed;

    public event EventHandler<VideoFrame>? FrameReceived;

    public (int Width, int Height) Dimensions => (_decoder.Width, _decoder.Height);
    public bool IsActive { get; private set; }

    public RtpVideoReceiver(RtpReceiverConfig? config = null)
    {
        _config = config ?? new RtpReceiverConfig();
        _depayloader = new H264Depayloader();
        _decoder = new FFmpegDecoder();
        _h264Buffer = new List<byte>();
        _accessUnitBuffer = new List<byte[]>();
    }

    public void Start()
    {
        if (IsActive)
            return;

        try
        {
            // Create UDP client
            var endpoint = new IPEndPoint(IPAddress.Parse(_config.Address), _config.Port);
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(endpoint);

            // Set receive buffer size - needs to be large enough to handle bursts
            // At 200fps with ~10KB frames and network jitter, we need substantial buffering
            _udpClient.Client.ReceiveBufferSize = 2 * 1024 * 1024; // 2MB

            Console.WriteLine($"RTP receiver listening on {_config.Address}:{_config.Port}");

            // Start receive task
            _cancellationTokenSource = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoop(_cancellationTokenSource.Token));

            IsActive = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start RTP receiver: {ex.Message}");
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        if (!IsActive)
            return;

        IsActive = false;

        _cancellationTokenSource?.Cancel();
        _receiveTask?.Wait(TimeSpan.FromSeconds(2));

        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _receiveTask = null;

        _depayloader.Reset();
        _decoder.Flush();
        _h264Buffer.Clear();
        _accessUnitBuffer.Clear();

        Console.WriteLine("RTP receiver stopped");
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var nalUnits = new List<byte[]>();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    // Receive RTP packet
                    var result = await _udpClient.ReceiveAsync(cancellationToken);

                    // Parse RTP header
                    if (!RtpPacket.TryParse(result.Buffer, out var rtpPacket) || rtpPacket == null)
                        continue;

                    // Filter by payload type
                    if (rtpPacket.PayloadType != _config.PayloadType)
                        continue;

                    // Check if access unit has been incomplete for too long (packet loss)
                    // At 60fps, frames should arrive every ~16ms. Timeout after 100ms.
                    if (_accessUnitBuffer.Count > 0)
                    {
                        var elapsed = (DateTime.Now - _lastAccessUnitTime).TotalMilliseconds;
                        if (elapsed > 100)
                        {
                            Console.WriteLine($"Access unit timeout after {elapsed:F0}ms - discarding {_accessUnitBuffer.Count} NAL units");
                            _accessUnitBuffer.Clear();
                            _depayloader.Reset();
                        }
                    }

                    // Depayload H.264
                    nalUnits.Clear();
                    if (_depayloader.ProcessPayload(rtpPacket.Payload.Span, rtpPacket.SequenceNumber, nalUnits))
                    {
                        // Add NAL units to access unit buffer
                        foreach (var nalu in nalUnits)
                        {
                            _accessUnitBuffer.Add(nalu);
                            _lastAccessUnitTime = DateTime.Now;
                        }
                    }

                    // RTP marker bit indicates last packet of access unit (frame)
                    if (rtpPacket.Marker && _accessUnitBuffer.Count > 0)
                    {
                        ProcessAccessUnit();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"Error receiving RTP packet: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void ProcessAccessUnit()
    {
        try
        {
            // Concatenate all NAL units in the access unit
            _h264Buffer.Clear();
            foreach (var nalu in _accessUnitBuffer)
            {
                _h264Buffer.AddRange(nalu);
            }

            int totalBytes = _h264Buffer.Count;

            // Clear the buffer for next access unit
            _accessUnitBuffer.Clear();
            _lastAccessUnitTime = DateTime.Now;

            // Sanity check: warn if frame seems too small for 1920x1080
            if (totalBytes < 1000)
            {
                Console.WriteLine($"Warning: Very small access unit ({totalBytes} bytes) - possible packet loss");
            }

            // Feed complete access unit to decoder
            var frame = _decoder.DecodeFrame(_h264Buffer.ToArray());
            if (frame != null)
            {
                // Raise frame event
                FrameReceived?.Invoke(this, frame);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error decoding access unit: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _decoder.Dispose();
        _disposed = true;
    }
}
