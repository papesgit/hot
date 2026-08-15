using System;
using System.Text.Json;

namespace HlaeObsTools.Services.HotLink;

public sealed class HotLinkPublisherPlayer
{
    public int ObserverSlot { get; init; } = -1;
    public string Name { get; init; } = string.Empty;
    public int Team { get; init; }
    public int RoundKills { get; set; }
}

public sealed class HotLinkPublisherKillEvent
{
    public long Id { get; set; }
    public DateTimeOffset ReceivedUtc { get; init; } = DateTimeOffset.UtcNow;
    public double? GameTime { get; init; }
    public int RoundNumber { get; init; }
    public int LabelRoundNumber { get; set; }
    public int RoundKillNumber { get; set; }
    public string RoundPhase { get; init; } = string.Empty;
    public bool MainCaught { get; init; }
    public HotLinkPublisherPlayer? Attacker { get; init; }
    public HotLinkPublisherPlayer? Victim { get; init; }
    public HotLinkPublisherPlayer? Assister { get; init; }
    public string Weapon { get; init; } = string.Empty;
    public bool Headshot { get; init; }
    public bool Wallbang { get; init; }
    public bool Noscope { get; init; }
    public bool ThroughSmoke { get; init; }
    public bool InAir { get; init; }
    public bool Blind { get; init; }
    public bool FlashAssist { get; init; }

    public int AttackerSlot => Attacker?.ObserverSlot ?? -1;
    public string AttackerName => string.IsNullOrWhiteSpace(Attacker?.Name) ? $"slot{AttackerSlot + 1}" : Attacker!.Name;

    public static bool TryParseHlaeKillfeed(string json, Func<int, bool> isFocusedSlot, int roundNumber, string roundPhase, out HotLinkPublisherKillEvent? kill)
    {
        kill = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) ||
                !string.Equals(typeProp.GetString(), "killfeed_event", StringComparison.Ordinal))
            {
                return false;
            }

            var attacker = ReadPlayer(root, "attacker");
            var slot = attacker?.ObserverSlot ?? -1;
            kill = new HotLinkPublisherKillEvent
            {
                GameTime = ReadDouble(root, "game_time"),
                RoundNumber = roundNumber,
                RoundPhase = roundPhase,
                MainCaught = slot >= 0 && isFocusedSlot(slot),
                Attacker = attacker,
                Victim = ReadPlayer(root, "victim"),
                Assister = ReadPlayer(root, "assister"),
                Weapon = ReadString(root, "weapon"),
                Headshot = ReadBool(root, "headshot"),
                Wallbang = ReadBool(root, "wallbang"),
                Noscope = ReadBool(root, "noscope"),
                ThroughSmoke = ReadBool(root, "through_smoke"),
                InAir = ReadBool(root, "in_air"),
                Blind = ReadBool(root, "blind"),
                FlashAssist = ReadBool(root, "flash_assist")
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HotLinkPublisherPlayer? ReadPlayer(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var elem) || elem.ValueKind != JsonValueKind.Object)
            return null;

        return new HotLinkPublisherPlayer
        {
            ObserverSlot = NormalizeHlaeObserverSlot(ReadInt(elem, "observer_slot")),
            Name = ReadString(elem, "name"),
            Team = ReadInt(elem, "team")
        };
    }

    private static int NormalizeHlaeObserverSlot(int specKey)
    {
        if (specKey < 0)
            return -1;

        // HLAE killfeed uses CS spectator keys (1..9, 0), while spectate_slot
        // and GSI use zero-based binding indices (0..9).
        return specKey == 0 ? 9 : specKey - 1;
    }

    private static string ReadString(JsonElement elem, string propertyName)
    {
        return elem.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool ReadBool(JsonElement elem, string propertyName)
    {
        return elem.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt(JsonElement elem, string propertyName)
    {
        if (!elem.TryGetProperty(propertyName, out var prop))
            return -1;

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var value) => value,
            _ => -1
        };
    }

    private static double? ReadDouble(JsonElement elem, string propertyName)
    {
        if (!elem.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var value))
            return value;

        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            return value;

        return null;
    }
}
