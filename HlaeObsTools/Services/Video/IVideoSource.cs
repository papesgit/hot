using System;

namespace HlaeObsTools.Services.Video;

/// <summary>
/// Represents a decoded video frame in RGB format
/// </summary>
public class VideoFrame
{
    public required byte[] Data { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public long Timestamp { get; init; }
}

/// <summary>
/// Abstraction for video sources (RTP stream or D3D11 shared texture)
/// </summary>
public interface IVideoSource : IDisposable
{
    /// <summary>
    /// Event raised when a new frame is available
    /// </summary>
    event EventHandler<VideoFrame>? FrameReceived;

    /// <summary>
    /// Gets the current frame dimensions
    /// </summary>
    (int Width, int Height) Dimensions { get; }

    /// <summary>
    /// Gets whether the video source is currently active
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Start receiving video
    /// </summary>
    void Start();

    /// <summary>
    /// Stop receiving video
    /// </summary>
    void Stop();
}
