using System;
using System.Runtime.InteropServices;

namespace HlaeObsTools.Services.Input;

/// <summary>
/// Binary input packet format matching C++ InputPacket struct (26 bytes)
/// Optimized for ultra-low latency transmission over UDP
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InputPacket
{
    public uint Sequence;        // Packet sequence number
    public short MouseDx;        // Mouse delta X
    public short MouseDy;        // Mouse delta Y
    public sbyte MouseWheel;     // Mouse wheel delta
    public byte MouseButtons;    // Button flags (L=1, R=2, M=4)
    public ulong KeysDown;       // Bitmask of keys currently down
    public ulong Timestamp;      // Microsecond timestamp

    public const int Size = 26;

    /// <summary>
    /// Convert struct to byte array for network transmission
    /// </summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(this, handle.AddrOfPinnedObject(), false);
            return bytes;
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>
/// Key bit positions in keysDown bitmask
/// Must match C++ key mapping
/// </summary>
public static class KeyBits
{
    public const int W = 0;
    public const int A = 1;
    public const int S = 2;
    public const int D = 3;
    public const int Space = 4;
    public const int Ctrl = 5;
    public const int Shift = 6;
    public const int Q = 7;
    public const int E = 8;
    public const int Key1 = 9;
    public const int Key2 = 10;
    public const int Key3 = 11;
    public const int Key4 = 12;
    public const int Key5 = 13;
    public const int Key6 = 14;
    public const int Key7 = 15;
    public const int Key8 = 16;
    public const int Key9 = 17;
    public const int Key0 = 18;
}
