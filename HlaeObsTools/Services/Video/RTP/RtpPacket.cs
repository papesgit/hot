using System;
using System.Buffers.Binary;

namespace HlaeObsTools.Services.Video.RTP;

/// <summary>
/// Represents a parsed RTP packet header
/// </summary>
public class RtpPacket
{
    public byte Version { get; private set; }
    public bool Padding { get; private set; }
    public bool Extension { get; private set; }
    public byte CsrcCount { get; private set; }
    public bool Marker { get; private set; }
    public byte PayloadType { get; private set; }
    public ushort SequenceNumber { get; private set; }
    public uint Timestamp { get; private set; }
    public uint Ssrc { get; private set; }
    public ReadOnlyMemory<byte> Payload { get; private set; }
    public ulong? SenderTimestampUs { get; private set; }

    /// <summary>
    /// Parse an RTP packet from a buffer
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out RtpPacket? packet)
    {
        packet = null;

        // Minimum RTP header is 12 bytes
        if (buffer.Length < 12)
            return false;

        var rtpPacket = new RtpPacket();

        // Byte 0: V(2), P(1), X(1), CC(4)
        rtpPacket.Version = (byte)((buffer[0] >> 6) & 0x03);
        rtpPacket.Padding = ((buffer[0] >> 5) & 0x01) == 1;
        rtpPacket.Extension = ((buffer[0] >> 4) & 0x01) == 1;
        rtpPacket.CsrcCount = (byte)(buffer[0] & 0x0F);

        // Byte 1: M(1), PT(7)
        rtpPacket.Marker = ((buffer[1] >> 7) & 0x01) == 1;
        rtpPacket.PayloadType = (byte)(buffer[1] & 0x7F);

        // Bytes 2-3: Sequence number
        rtpPacket.SequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));

        // Bytes 4-7: Timestamp
        rtpPacket.Timestamp = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        // Bytes 8-11: SSRC
        rtpPacket.Ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4));

        // Calculate header size (12 + CSRC + extension)
        int headerSize = 12 + (rtpPacket.CsrcCount * 4);

        if (buffer.Length < headerSize)
            return false;

        // Skip extension if present
        if (rtpPacket.Extension)
        {
            if (buffer.Length < headerSize + 4)
                return false;

            ushort profile = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(headerSize, 2));
            ushort extLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(headerSize + 2, 2));
            int extensionDataLength = extLength * 4;
            headerSize += 4 + extensionDataLength;

            if (buffer.Length < headerSize)
                return false;

            // Parse one-byte header extensions (0xBEDE) for sender timestamp
            if (profile == 0xBEDE && extensionDataLength > 0)
            {
                int extOffset = 0;
                while (extOffset < extensionDataLength)
                {
                    byte extByte = buffer[headerSize - extensionDataLength + extOffset];

                    // 0x00 is padding, skip a single byte
                    if (extByte == 0x00)
                    {
                        extOffset++;
                        continue;
                    }

                    byte extId = (byte)(extByte >> 4);
                    byte extLen = (byte)(extByte & 0x0F); // length minus 1
                    extOffset++;

                    int dataLen = extLen + 1;
                    if (extOffset + dataLen > extensionDataLength)
                        break;

                    if (extId == 1 && dataLen == sizeof(ulong))
                    {
                        var timestampSpan = buffer.Slice(headerSize - extensionDataLength + extOffset, dataLen);
                        rtpPacket.SenderTimestampUs = BinaryPrimitives.ReadUInt64BigEndian(timestampSpan);
                    }

                    extOffset += dataLen;
                }
            }
        }

        // Extract payload
        int payloadLength = buffer.Length - headerSize;
        if (rtpPacket.Padding && payloadLength > 0)
        {
            // Last byte indicates padding length
            byte paddingLength = buffer[buffer.Length - 1];
            payloadLength -= paddingLength;
        }

        if (payloadLength < 0)
            return false;

        rtpPacket.Payload = buffer.Slice(headerSize, payloadLength).ToArray();
        packet = rtpPacket;
        return true;
    }
}
