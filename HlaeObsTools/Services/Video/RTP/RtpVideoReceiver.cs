using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public string Address { get; set; } = "0.0.0.0";
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
    private byte[] _h264DecodeBuffer = Array.Empty<byte>();
    private readonly List<byte[]> _accessUnitBuffer;  // Buffer for complete access unit
    private long? _currentFrameSendTimestampUs;
    private DateTime _lastLatencyLog = DateTime.MinValue;
    private long _lastAccessUnitTicks;
    private long _lastStatsTicks;
    private bool _hasLastSequenceNumber;
    private ushort _lastSequenceNumber;
    private long _rtpPacketsReceived;
    private long _rtpSequenceGaps;
    private long _rtpOutOfOrderPackets;
    private long _rtpDuplicatePackets;
    private long _rtpFramesDecoded;
    private long _rtpFramesDroppedTimeout;
    private long _rtpTinyAccessUnits;
    private bool _disposed;
    private const double IncompleteAccessUnitTimeoutMs = 30.0;

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

            ResetRtpStats();

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
        _currentFrameSendTimestampUs = null;
        _lastAccessUnitTicks = 0;
        _hasLastSequenceNumber = false;

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

                    TrackRtpPacket(rtpPacket);

                    // Check if access unit has been incomplete for too long (packet loss)
                    // At 60fps, frames should arrive every ~16ms. Timeout quickly on LAN so
                    // damaged access units do not create catch-up jumps.
                    if (_accessUnitBuffer.Count > 0)
                    {
                        var elapsed = ElapsedMs(_lastAccessUnitTicks, Stopwatch.GetTimestamp());
                        if (elapsed > IncompleteAccessUnitTimeoutMs)
                        {
                            Console.WriteLine($"Access unit timeout after {elapsed:F0}ms - discarding {_accessUnitBuffer.Count} NAL units");
                            _accessUnitBuffer.Clear();
                            _depayloader.Reset();
                            _currentFrameSendTimestampUs = null;
                            _rtpFramesDroppedTimeout++;
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
                            _lastAccessUnitTicks = Stopwatch.GetTimestamp();
                        }

                        if (rtpPacket.SenderTimestampUs.HasValue)
                        {
                            _currentFrameSendTimestampUs ??= (long)rtpPacket.SenderTimestampUs.Value;
                        }
                    }

                    // RTP marker bit indicates last packet of access unit (frame)
                    if (rtpPacket.Marker && _accessUnitBuffer.Count > 0)
                    {
                        ProcessAccessUnit();
                    }

                    LogRtpStatsIfDue();
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
            _lastAccessUnitTicks = Stopwatch.GetTimestamp();
            var sendTimestampUs = _currentFrameSendTimestampUs;
            _currentFrameSendTimestampUs = null;

            // Sanity check: warn if frame seems too small for 1920x1080
            if (totalBytes < 1000)
            {
                Console.WriteLine($"Warning: Very small access unit ({totalBytes} bytes) - possible packet loss");
                _rtpTinyAccessUnits++;
            }

            // Feed complete access unit to decoder
            var receiveCompleteUs = NowMicros();
            if (_h264DecodeBuffer.Length < totalBytes)
                _h264DecodeBuffer = new byte[totalBytes];
            _h264Buffer.CopyTo(_h264DecodeBuffer, 0);
            var frame = _decoder.DecodeFrame(_h264DecodeBuffer.AsSpan(0, totalBytes), sendTimestampUs ?? 0, receiveCompleteUs);
            if (frame != null)
            {
                _rtpFramesDecoded++;
                LogLatencyIfAvailable(frame);
                // Raise frame event
                FrameReceived?.Invoke(this, frame);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error decoding access unit: {ex.Message}");
        }
    }

    private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private static long NowMicros()
    {
        // High-resolution microseconds since Unix epoch
        return (DateTime.UtcNow.Ticks - UnixEpochTicks) / 10;
    }

    private void LogLatencyIfAvailable(VideoFrame frame)
    {
        if (frame.SourceTimestampUs <= 0)
            return;

        var nowUs = NowMicros();
        var e2eMs = Math.Max(0, (nowUs - frame.SourceTimestampUs) / 1000.0);
        var captureToReceiveMs = frame.ReceivedTimestampUs > 0
            ? Math.Max(0, (frame.ReceivedTimestampUs - frame.SourceTimestampUs) / 1000.0)
            : double.NaN;

        var now = DateTime.UtcNow;
        if ((now - _lastLatencyLog).TotalSeconds >= 1.0)
        {
            Console.WriteLine($"Video latency: {e2eMs:F2} ms (capture->receive: {captureToReceiveMs:F2} ms)");
            _lastLatencyLog = now;
        }
    }

    private void TrackRtpPacket(RtpPacket packet)
    {
        _rtpPacketsReceived++;
        if (!_hasLastSequenceNumber)
        {
            _lastSequenceNumber = packet.SequenceNumber;
            _hasLastSequenceNumber = true;
            return;
        }

        ushort expected = (ushort)(_lastSequenceNumber + 1);
        if (packet.SequenceNumber == expected)
        {
            _lastSequenceNumber = packet.SequenceNumber;
            return;
        }

        if (packet.SequenceNumber == _lastSequenceNumber)
        {
            _rtpDuplicatePackets++;
            return;
        }

        ushort forwardDistance = (ushort)(packet.SequenceNumber - expected);
        if (forwardDistance < 0x8000)
        {
            _rtpSequenceGaps += forwardDistance;
            _lastSequenceNumber = packet.SequenceNumber;
        }
        else
        {
            _rtpOutOfOrderPackets++;
        }
    }

    private void ResetRtpStats()
    {
        _lastAccessUnitTicks = 0;
        _lastStatsTicks = 0;
        _hasLastSequenceNumber = false;
        _lastSequenceNumber = 0;
        _rtpPacketsReceived = 0;
        _rtpSequenceGaps = 0;
        _rtpOutOfOrderPackets = 0;
        _rtpDuplicatePackets = 0;
        _rtpFramesDecoded = 0;
        _rtpFramesDroppedTimeout = 0;
        _rtpTinyAccessUnits = 0;
    }

    private void LogRtpStatsIfDue()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        if (_lastStatsTicks == 0)
        {
            _lastStatsTicks = nowTicks;
            return;
        }

        if (ElapsedMs(_lastStatsTicks, nowTicks) < 1000.0)
            return;

        Console.WriteLine(
            $"RTP stats: packets={_rtpPacketsReceived} decoded={_rtpFramesDecoded} gaps={_rtpSequenceGaps} " +
            $"ooo={_rtpOutOfOrderPackets} dup={_rtpDuplicatePackets} timeoutDrops={_rtpFramesDroppedTimeout} tinyAUs={_rtpTinyAccessUnits}");
        _lastStatsTicks = nowTicks;
    }

    private static double ElapsedMs(long startTicks, long endTicks)
    {
        if (startTicks == 0)
            return 0;
        return (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
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
