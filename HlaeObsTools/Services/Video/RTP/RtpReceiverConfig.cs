namespace HlaeObsTools.Services.Video.RTP;

/// <summary>
/// Configuration for the RTP video receiver.
/// </summary>
public sealed class RtpReceiverConfig
{
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5000;
    public byte PayloadType { get; set; } = 96;
}
