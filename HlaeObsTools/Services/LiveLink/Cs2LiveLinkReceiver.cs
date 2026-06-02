using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HlaeObsTools.Services.LiveLink;

public sealed class Cs2LiveLinkReceiver : IDisposable
{
    private const ushort ProtocolVersion = 12;
    private const ushort PacketTypeFrame = 1;
    private const ushort PacketTypeSkeleton = 2;
    private const ushort FrameChunkFlagFinal = 1;

    private readonly object _lock = new();
    private readonly Dictionary<int, Cs2LiveLinkSkeleton> _skeletons = new();
    private readonly Dictionary<uint, PendingFrame> _pendingFrames = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Cs2LiveLinkFrame? _latestFrame;
    private bool _enabled;
    private int _port = 31237;
    private long _packetCount;
    private long _framePacketCount;
    private long _skeletonPacketCount;
    private long _malformedPacketCount;
    private DateTimeOffset? _lastPacketUtc;
    private string _lastError = string.Empty;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;

            _enabled = value;
            if (value)
                Start();
            else
                Stop();
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            var port = Math.Clamp(value, 1, 65535);
            if (_port == port)
                return;

            _port = port;
            if (_enabled)
            {
                Stop();
                Start();
            }
        }
    }

    public Cs2LiveLinkFrame? GetLatestFrame()
    {
        lock (_lock)
        {
            return _latestFrame;
        }
    }

    public Cs2LiveLinkReceiverStats GetStats()
    {
        lock (_lock)
        {
            return new Cs2LiveLinkReceiverStats(
                _enabled,
                _port,
                _packetCount,
                _framePacketCount,
                _skeletonPacketCount,
                _malformedPacketCount,
                _skeletons.Count,
                _latestFrame?.FrameId,
                _latestFrame?.Entities.Count ?? 0,
                _lastPacketUtc,
                _lastError);
        }
    }

    public Cs2LiveLinkSkeleton? GetSkeleton(int entityId)
    {
        lock (_lock)
        {
            return _skeletons.TryGetValue(entityId, out var skeleton) ? skeleton : null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void Start()
    {
        Stop();

        try
        {
            _cts = new CancellationTokenSource();
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LiveLink receiver failed to start on UDP {_port}: {ex.Message}");
            Stop();
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;

        lock (_lock)
        {
            _pendingFrames.Clear();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                var udp = _udp;
                if (udp == null)
                    return;

                result = await udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _lastError = ex.Message;
                }
                Console.WriteLine($"LiveLink receiver error: {ex.Message}");
                continue;
            }

            TryParsePacket(result.Buffer);
        }
    }

    private void TryParsePacket(byte[] packet)
    {
        ushort packetType = 0;
        uint sequence = 0;
        try
        {
            var reader = new PacketReader(packet);
            if (reader.ReadByte() != (byte)'A'
                || reader.ReadByte() != (byte)'F'
                || reader.ReadByte() != (byte)'X'
                || reader.ReadByte() != (byte)'L')
            {
                return;
            }

            var version = reader.ReadUInt16();
            packetType = reader.ReadUInt16();
            sequence = reader.ReadUInt32();

            if (version != ProtocolVersion)
                return;

            lock (_lock)
            {
                _packetCount++;
                _lastPacketUtc = DateTimeOffset.UtcNow;
            }

            if (packetType == PacketTypeSkeleton)
                ParseSkeleton(reader);
            else if (packetType == PacketTypeFrame)
                ParseFrame(reader);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _malformedPacketCount++;
                _lastError = $"Malformed packet type={packetType} seq={sequence} bytes={packet.Length}: {ex.Message}";
            }
        }
    }

    private void ParseSkeleton(PacketReader reader)
    {
        var entityId = reader.ReadInt32();
        var modelName = reader.ReadString();
        var boneCount = checked((int)reader.ReadUInt32());
        var boneNames = new string[boneCount];
        var boneParents = new int[boneCount];

        for (var i = 0; i < boneCount; i++)
        {
            boneNames[i] = reader.ReadString();
            boneParents[i] = reader.ReadInt32();
        }

        lock (_lock)
        {
            _skeletonPacketCount++;
            _skeletons[entityId] = new Cs2LiveLinkSkeleton(entityId, modelName, boneNames, boneParents);
        }
    }

    private void ParseFrame(PacketReader reader)
    {
        reader.Stage = "frameTime";
        var frameTime = reader.ReadSingle();
        reader.Stage = "frameId";
        var frameId = reader.ReadUInt32();
        reader.Stage = "chunkIndex";
        var chunkIndex = reader.ReadUInt16();
        reader.Stage = "chunkFlags";
        var chunkFlags = reader.ReadUInt16();
        reader.Stage = "frameRateNumerator";
        var frameRateNumerator = reader.ReadUInt32();
        reader.Stage = "frameRateDenominator";
        var frameRateDenominator = reader.ReadUInt32();
        reader.Stage = "entityCount";
        var entityCount = checked((int)reader.ReadUInt32());
        if (entityCount < 0 || entityCount > 4096)
            throw new InvalidOperationException($"Unreasonable entity count {entityCount}.");

        var entities = new List<Cs2LiveLinkEntity>(entityCount);
        for (var i = 0; i < entityCount; i++)
        {
            reader.Stage = $"entity[{i}]";
            entities.Add(ParseEntity(ref reader));
        }

        var hiddenIds = Array.Empty<int>();
        var bloodEvents = Array.Empty<Cs2LiveLinkBloodEvent>();
        var shotEvents = Array.Empty<Cs2LiveLinkShotEvent>();
        Cs2LiveLinkCamera? camera = null;
        var isFinal = (chunkFlags & FrameChunkFlagFinal) != 0;

        if (isFinal)
        {
            reader.Stage = "hiddenIds";
            hiddenIds = ReadHiddenIds(ref reader);
            reader.Stage = "bloodEvents";
            bloodEvents = ReadBloodEvents(ref reader);
            reader.Stage = "shotEvents";
            shotEvents = TryReadShotEvents(ref reader);
            reader.Stage = "cameraFlag";
            if (reader.Remaining > 0 && reader.ReadByte() != 0)
            {
                reader.Stage = "camera";
                camera = new Cs2LiveLinkCamera(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle());
            }
        }
        else
        {
            reader.Stage = "nonFinalHiddenPlaceholder";
            _ = reader.ReadUInt32();
            reader.Stage = "nonFinalBloodPlaceholder";
            _ = reader.ReadUInt16();
            reader.Stage = "nonFinalShotPlaceholder";
            _ = reader.ReadUInt16();
            reader.Stage = "nonFinalCameraPlaceholder";
            _ = reader.ReadByte();
        }

        lock (_lock)
        {
            _framePacketCount++;
            var pending = GetPendingFrame(frameId, frameTime, frameRateNumerator, frameRateDenominator);
            pending.Entities.AddRange(entities);
            if (isFinal)
            {
                pending.HiddenEntityIds = hiddenIds;
                pending.BloodEvents = bloodEvents;
                pending.ShotEvents = shotEvents;
                pending.Camera = camera;
                _latestFrame = pending.ToFrame();
                _pendingFrames.Remove(frameId);
                PrunePendingFrames(frameId);
            }
        }
    }

    private PendingFrame GetPendingFrame(uint frameId, float frameTime, uint frameRateNumerator, uint frameRateDenominator)
    {
        if (!_pendingFrames.TryGetValue(frameId, out var pending))
        {
            pending = new PendingFrame(frameId, frameTime, frameRateNumerator, frameRateDenominator);
            _pendingFrames[frameId] = pending;
        }

        return pending;
    }

    private void PrunePendingFrames(uint newestFrameId)
    {
        if (_pendingFrames.Count < 16)
            return;

        var stale = new List<uint>();
        foreach (var frameId in _pendingFrames.Keys)
        {
            if (frameId + 8 < newestFrameId)
                stale.Add(frameId);
        }

        foreach (var frameId in stale)
            _pendingFrames.Remove(frameId);
    }

    private static Cs2LiveLinkEntity ParseEntity(ref PacketReader reader)
    {
        var entityStage = reader.Stage;
        reader.Stage = $"{entityStage}.id";
        var id = reader.ReadInt32();
        reader.Stage = $"{entityStage}.ownerId";
        var ownerId = reader.ReadInt32();
        reader.Stage = $"{entityStage}.projectile";
        var projectile = reader.ReadByte() != 0;
        reader.Stage = $"{entityStage}.visible";
        var visible = reader.ReadByte() != 0;
        reader.Stage = $"{entityStage}.viewModel";
        var viewModel = reader.ReadByte() != 0;
        reader.Stage = $"{entityStage}.clientClassName";
        var clientClassName = reader.ReadString();
        reader.Stage = $"{entityStage}.transform";
        var transform = reader.ReadMatrix3x4();
        reader.Stage = $"{entityStage}.hasBones";
        var hasBones = reader.ReadByte() != 0;
        reader.Stage = $"{entityStage}.boneCount";
        var boneCount = checked((int)reader.ReadUInt32());
        if (boneCount < 0 || boneCount > 4096)
            throw new InvalidOperationException($"Unreasonable bone count {boneCount} for entity {id}.");
        var localBones = new Matrix4x4[boneCount];

        for (var i = 0; i < boneCount; i++)
        {
            reader.Stage = $"{entityStage}.bone[{i}]";
            localBones[i] = reader.ReadMatrix3x4();
        }

        return new Cs2LiveLinkEntity(id, ownerId, clientClassName, projectile, visible, viewModel, transform, hasBones, localBones);
    }

    private static int[] ReadHiddenIds(ref PacketReader reader)
    {
        var count = checked((int)reader.ReadUInt32());
        var result = new int[count];
        for (var i = 0; i < count; i++)
            result[i] = reader.ReadInt32();
        return result;
    }

    private static Cs2LiveLinkBloodEvent[] ReadBloodEvents(ref PacketReader reader)
    {
        var count = reader.ReadUInt16();
        var result = new Cs2LiveLinkBloodEvent[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = new Cs2LiveLinkBloodEvent(
                reader.ReadInt32(),
                new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                reader.ReadSingle());
        }
        return result;
    }

    private static Cs2LiveLinkShotEvent[] ReadShotEvents(ref PacketReader reader)
    {
        var count = reader.ReadUInt16();
        var result = new Cs2LiveLinkShotEvent[count];
        for (var i = 0; i < count; i++)
        {
            var shooterId = reader.ReadInt32();
            var weaponId = reader.ReadInt32();
            var origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var pelletCount = reader.ReadUInt16();
            var pellets = new Vector3[pelletCount];
            for (var pelletIndex = 0; pelletIndex < pelletCount; pelletIndex++)
                pellets[pelletIndex] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            result[i] = new Cs2LiveLinkShotEvent(shooterId, weaponId, origin, pellets);
        }
        return result;
    }

    private static Cs2LiveLinkShotEvent[] TryReadShotEvents(ref PacketReader reader)
    {
        try
        {
            return ReadShotEvents(ref reader);
        }
        catch (InvalidOperationException)
        {
            reader.Position = reader.Length;
            return Array.Empty<Cs2LiveLinkShotEvent>();
        }
    }

    private sealed class PendingFrame(uint frameId, float frameTime, uint frameRateNumerator, uint frameRateDenominator)
    {
        public List<Cs2LiveLinkEntity> Entities { get; } = new();
        public int[] HiddenEntityIds { get; set; } = Array.Empty<int>();
        public Cs2LiveLinkBloodEvent[] BloodEvents { get; set; } = Array.Empty<Cs2LiveLinkBloodEvent>();
        public Cs2LiveLinkShotEvent[] ShotEvents { get; set; } = Array.Empty<Cs2LiveLinkShotEvent>();
        public Cs2LiveLinkCamera? Camera { get; set; }

        public Cs2LiveLinkFrame ToFrame()
            => new(frameId, frameTime, frameRateNumerator, frameRateDenominator, Entities.ToArray(), HiddenEntityIds, BloodEvents, ShotEvents, Camera);
    }

    private ref struct PacketReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;
        public string Stage { get; set; }
        public readonly int Length => _data.Length;
        public readonly int Remaining => _data.Length - _offset;
        public int Position
        {
            readonly get => _offset;
            set
            {
                if (value < 0 || value > _data.Length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _offset = value;
            }
        }

        public PacketReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
            Stage = "header";
        }

        public byte ReadByte()
        {
            Ensure(1);
            return _data[_offset++];
        }

        public ushort ReadUInt16()
        {
            Ensure(2);
            var result = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_offset, 2));
            _offset += 2;
            return result;
        }

        public uint ReadUInt32()
        {
            Ensure(4);
            var result = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset, 4));
            _offset += 4;
            return result;
        }

        public int ReadInt32()
        {
            Ensure(4);
            var result = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_offset, 4));
            _offset += 4;
            return result;
        }

        public float ReadSingle()
        {
            return BitConverter.Int32BitsToSingle(ReadInt32());
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            Ensure(length);
            var result = Encoding.UTF8.GetString(_data.Slice(_offset, length));
            _offset += length;
            return result;
        }

        public Matrix4x4 ReadMatrix3x4()
        {
            var r00 = ReadSingle();
            var r01 = ReadSingle();
            var r02 = ReadSingle();
            var r03 = ReadSingle();
            var r10 = ReadSingle();
            var r11 = ReadSingle();
            var r12 = ReadSingle();
            var r13 = ReadSingle();
            var r20 = ReadSingle();
            var r21 = ReadSingle();
            var r22 = ReadSingle();
            var r23 = ReadSingle();

            return new Matrix4x4(
                r00, r10, r20, 0f,
                r01, r11, r21, 0f,
                r02, r12, r22, 0f,
                r03, r13, r23, 1f);
        }

        private void Ensure(int bytes)
        {
            if (bytes < 0 || _offset + bytes > _data.Length)
                throw new InvalidOperationException($"Packet ended unexpectedly at {Stage}, offset {_offset}, need {bytes}, length {_data.Length}.");
        }
    }
}

public sealed record Cs2LiveLinkSkeleton(int EntityId, string ModelName, IReadOnlyList<string> BoneNames, IReadOnlyList<int> BoneParents);

public sealed record Cs2LiveLinkReceiverStats(
    bool Enabled,
    int Port,
    long PacketCount,
    long FramePacketCount,
    long SkeletonPacketCount,
    long MalformedPacketCount,
    int SkeletonCount,
    uint? LatestFrameId,
    int LatestFrameEntityCount,
    DateTimeOffset? LastPacketUtc,
    string LastError);

public sealed record Cs2LiveLinkFrame(
    uint FrameId,
    float FrameTime,
    uint FrameRateNumerator,
    uint FrameRateDenominator,
    IReadOnlyList<Cs2LiveLinkEntity> Entities,
    IReadOnlyList<int> HiddenEntityIds,
    IReadOnlyList<Cs2LiveLinkBloodEvent> BloodEvents,
    IReadOnlyList<Cs2LiveLinkShotEvent> ShotEvents,
    Cs2LiveLinkCamera? Camera);

public sealed record Cs2LiveLinkEntity(
    int Id,
    int OwnerId,
    string ClientClassName,
    bool Projectile,
    bool Visible,
    bool ViewModel,
    Matrix4x4 Transform,
    bool HasBones,
    IReadOnlyList<Matrix4x4> LocalBoneTransforms);

public sealed record Cs2LiveLinkCamera(float X, float Y, float Z, float Rx, float Ry, float Rz, float Fov);

public sealed record Cs2LiveLinkBloodEvent(int VictimEntityId, Vector3 Origin, Vector3 Normal, float Magnitude);

public sealed record Cs2LiveLinkShotEvent(int ShooterEntityId, int WeaponEntityId, Vector3 Origin, IReadOnlyList<Vector3> PelletDirections);
