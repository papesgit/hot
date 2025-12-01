using System;
using System.Buffers;
using System.Collections.Generic;

namespace HlaeObsTools.Services.Video.RTP;

/// <summary>
/// H.264 RTP depayloader supporting single NALUs and FU-A fragmentation (RFC 6184)
/// Produces Annex-B byte stream with 00 00 00 01 start codes
/// </summary>
public class H264Depayloader
{
    private readonly List<byte> _fragmentBuffer = new();
    private ushort _lastSequenceNumber;
    private bool _isFirstFragment = true;

    // NAL unit type constants
    private const byte NAL_TYPE_MASK = 0x1F;
    private const byte FU_A = 28;
    private const byte FU_B = 29;
    private const byte STAP_A = 24;

    // Annex-B start code
    private static readonly byte[] StartCode = { 0x00, 0x00, 0x00, 0x01 };

    /// <summary>
    /// Process an RTP payload and extract complete NAL units
    /// </summary>
    /// <param name="payload">RTP payload containing H.264 data</param>
    /// <param name="sequenceNumber">RTP sequence number</param>
    /// <param name="nalUnits">Output list of complete NAL units with start codes</param>
    /// <returns>True if one or more complete NAL units were extracted</returns>
    public bool ProcessPayload(ReadOnlySpan<byte> payload, ushort sequenceNumber, List<byte[]> nalUnits)
    {
        if (payload.Length == 0)
            return false;

        byte naluType = (byte)(payload[0] & NAL_TYPE_MASK);

        if (naluType >= 1 && naluType <= 23)
        {
            // Single NALU packet
            return ProcessSingleNalu(payload, nalUnits);
        }
        else if (naluType == FU_A)
        {
            // FU-A fragmented NALU
            return ProcessFuA(payload, sequenceNumber, nalUnits);
        }
        else if (naluType == STAP_A)
        {
            // STAP-A (single-time aggregation packet) - multiple NALUs in one RTP packet
            return ProcessStapA(payload, nalUnits);
        }

        // Unsupported or reserved type
        return false;
    }

    /// <summary>
    /// Process a single NALU packet
    /// </summary>
    private bool ProcessSingleNalu(ReadOnlySpan<byte> payload, List<byte[]> nalUnits)
    {
        // Create Annex-B NALU: start code + NALU
        var nalu = new byte[StartCode.Length + payload.Length];
        StartCode.CopyTo(nalu, 0);
        payload.CopyTo(nalu.AsSpan(StartCode.Length));
        nalUnits.Add(nalu);
        return true;
    }

    /// <summary>
    /// Process a fragmentation unit A (FU-A) packet
    /// </summary>
    private bool ProcessFuA(ReadOnlySpan<byte> payload, ushort sequenceNumber, List<byte[]> nalUnits)
    {
        if (payload.Length < 2)
            return false;

        byte fuIndicator = payload[0];
        byte fuHeader = payload[1];

        bool start = (fuHeader & 0x80) != 0;
        bool end = (fuHeader & 0x40) != 0;
        byte naluType = (byte)(fuHeader & NAL_TYPE_MASK);

        ReadOnlySpan<byte> fragmentData = payload.Slice(2);

        if (start)
        {
            // Start of fragmented NALU
            _fragmentBuffer.Clear();

            // Reconstruct original NALU header
            byte naluHeader = (byte)((fuIndicator & 0xE0) | naluType);

            // Add start code and NALU header
            _fragmentBuffer.AddRange(StartCode);
            _fragmentBuffer.Add(naluHeader);
            _fragmentBuffer.AddRange(fragmentData.ToArray());

            _lastSequenceNumber = sequenceNumber;
            _isFirstFragment = false;
        }
        else if (!_isFirstFragment)
        {
            // Check for sequence discontinuity
            ushort expectedSeq = (ushort)(_lastSequenceNumber + 1);
            if (sequenceNumber != expectedSeq)
            {
                // Packet loss - discard fragment
                _fragmentBuffer.Clear();
                _isFirstFragment = true;
                return false;
            }

            // Middle or end fragment
            _fragmentBuffer.AddRange(fragmentData.ToArray());
            _lastSequenceNumber = sequenceNumber;

            if (end)
            {
                // Complete NALU assembled
                nalUnits.Add(_fragmentBuffer.ToArray());
                _fragmentBuffer.Clear();
                _isFirstFragment = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Process a single-time aggregation packet A (STAP-A)
    /// </summary>
    private bool ProcessStapA(ReadOnlySpan<byte> payload, List<byte[]> nalUnits)
    {
        if (payload.Length < 3)
            return false;

        int offset = 1; // Skip STAP-A header
        bool addedNalu = false;

        while (offset + 2 < payload.Length)
        {
            // Read NALU size (16-bit big-endian)
            ushort naluSize = (ushort)((payload[offset] << 8) | payload[offset + 1]);
            offset += 2;

            if (offset + naluSize > payload.Length)
                break;

            // Extract NALU and add start code
            ReadOnlySpan<byte> naluData = payload.Slice(offset, naluSize);
            var nalu = new byte[StartCode.Length + naluSize];
            StartCode.CopyTo(nalu, 0);
            naluData.CopyTo(nalu.AsSpan(StartCode.Length));
            nalUnits.Add(nalu);

            offset += naluSize;
            addedNalu = true;
        }

        return addedNalu;
    }

    /// <summary>
    /// Reset the depayloader state (e.g., on stream restart)
    /// </summary>
    public void Reset()
    {
        _fragmentBuffer.Clear();
        _isFirstFragment = true;
    }
}
