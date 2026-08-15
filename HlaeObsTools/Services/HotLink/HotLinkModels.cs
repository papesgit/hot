using System;
using HlaeObsTools.Services.Gsi;

namespace HlaeObsTools.Services.HotLink;

public sealed class HotLinkKillParticipant
{
    public int ObserverSlot { get; init; } = -1;
    public Vec3 Position { get; init; }
}

public sealed class HotLinkKillEvent
{
    public long Id { get; init; }
    public double? GameTime { get; init; }
    public HotLinkKillParticipant Attacker { get; init; } = new();
    public HotLinkKillParticipant Victim { get; init; } = new();
    public string Weapon { get; init; } = string.Empty;
    public bool MainCaught { get; init; }
    public bool Headshot { get; init; }
    public bool Wallbang { get; init; }
    public bool Noscope { get; init; }
    public bool ThroughSmoke { get; init; }
    public bool InAir { get; init; }
    public bool Blind { get; init; }
}

public sealed class HotLinkEventEnvelope
{
    public int ProtocolVersion { get; init; } = HotLinkProtocol.Version;
    public Guid PublisherSessionId { get; init; }
    public long FirstAvailableEventId { get; init; }
    public long LatestEventId { get; init; }
    public bool HasGap { get; init; }
    public HotLinkKillEvent[] Events { get; init; } = Array.Empty<HotLinkKillEvent>();
}

public sealed class HotLinkReplayMarkRequest
{
    public Guid PublisherSessionId { get; init; }
    public long EventId { get; init; }
}

public sealed class HotLinkReplayMarkResponse
{
    public bool Ok { get; init; }
    public bool Scheduled { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public static class HotLinkProtocol
{
    public const int Version = 1;
    public const string ServiceType = "_hlae-hot-link._tcp";
    public const string EventsPath = "/hot-link/v1/events";
    public const string HealthPath = "/hot-link/v1/health";
    public const string ReplayMarkPath = "/hot-link/v1/replay/mark";
}
