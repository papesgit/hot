using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace HlaeObsTools.Services.Video.FFmpeg;

/// <summary>
/// Low-latency H.264 decoder using FFmpeg
/// </summary>
public unsafe class FFmpegDecoder : IDisposable
{
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVFrame* _frameRgb;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private byte* _rgbBuffer;
    private int _rgbBufferSize;
    private byte[][]? _managedFrameBuffers;
    private int _managedFrameBufferIndex;
    private bool _disposed;
    private const int ManagedFrameBufferCount = 8;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }

    public FFmpegDecoder()
    {
        FFmpegLoader.Initialize();

        // Find H.264 decoder
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
            throw new Exception("H.264 codec not found");

        // Allocate codec context
        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
            throw new Exception("Failed to allocate codec context");

        // Set low-latency options
        _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        _codecContext->thread_count = 1; // Single thread for lowest latency
        _codecContext->thread_type = 0;

        // Open codec
        int ret = ffmpeg.avcodec_open2(_codecContext, codec, null);
        if (ret < 0)
            throw new Exception($"Failed to open codec: {GetErrorMessage(ret)}");

        // Allocate frames
        _frame = ffmpeg.av_frame_alloc();
        _frameRgb = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        if (_frame == null || _frameRgb == null || _packet == null)
            throw new Exception("Failed to allocate frames/packet");
    }

    /// <summary>
    /// Decode H.264 data and return BGRA frame (matches Avalonia Bgra8888)
    /// </summary>
    /// <param name="data">H.264 encoded data (Annex-B format)</param>
    /// <param name="sourceTimestampUs">Timestamp from the sender (microseconds since Unix epoch)</param>
    /// <param name="receivedTimestampUs">Timestamp when the receiver finished assembling the access unit</param>
    /// <returns>RGB frame data or null if no frame was produced</returns>
    public VideoFrame? DecodeFrame(ReadOnlySpan<byte> data, long sourceTimestampUs = 0, long receivedTimestampUs = 0)
    {
        fixed (byte* dataPtr = data)
        {
            _packet->data = dataPtr;
            _packet->size = data.Length;

            // Send packet to decoder
            int ret = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Console.WriteLine($"Error sending packet: {GetErrorMessage(ret)}");
                return null;
            }

            // Receive decoded frame
            ret = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            {
                return null;
            }
            if (ret < 0)
            {
                Console.WriteLine($"Error receiving frame: {GetErrorMessage(ret)}");
                return null;
            }

            // Update dimensions if changed
            if (Width != _frame->width || Height != _frame->height)
            {
                Width = _frame->width;
                Height = _frame->height;
                InitializeScaler();
            }

            // Convert to RGB
            return ConvertToRgb(sourceTimestampUs, receivedTimestampUs);
        }
    }

    /// <summary>
    /// Initialize the software scaler for YUV to BGRA conversion
    /// </summary>
    private void InitializeScaler()
    {
        // Free old scaler if exists
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        // Free old RGB buffer if exists
        if (_rgbBuffer != null)
        {
            Marshal.FreeHGlobal((IntPtr)_rgbBuffer);
            _rgbBuffer = null;
        }

        // Create new scaler for YUV to BGRA
        // SWS_FAST_BILINEAR = 2 (bilinear scaling)
        _swsContext = ffmpeg.sws_getContext(
            Width, Height, (AVPixelFormat)_frame->format,
            Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA,
            2, null, null, null);

        if (_swsContext == null)
            throw new Exception("Failed to create scaler context");

        // Allocate BGRA buffer
        Stride = Width * 4; // BGRA = 4 bytes per pixel
        _rgbBufferSize = Stride * Height;
        _rgbBuffer = (byte*)Marshal.AllocHGlobal(_rgbBufferSize);
        _managedFrameBuffers = new byte[ManagedFrameBufferCount][];
        for (int i = 0; i < _managedFrameBuffers.Length; i++)
        {
            _managedFrameBuffers[i] = new byte[_rgbBufferSize];
        }
        _managedFrameBufferIndex = 0;

        // Setup BGRA frame
        _frameRgb->width = Width;
        _frameRgb->height = Height;
        _frameRgb->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;

        // Manually set up the frame data and linesize
        _frameRgb->data[0] = _rgbBuffer;
        _frameRgb->linesize[0] = Stride;
    }

    /// <summary>
    /// Convert current frame to RGBA
    /// </summary>
    private VideoFrame ConvertToRgb(long sourceTimestampUs, long receivedTimestampUs)
    {
        // Perform YUV to RGBA conversion
        ffmpeg.sws_scale(
            _swsContext,
            _frame->data,
            _frame->linesize,
            0,
            Height,
            _frameRgb->data,
            _frameRgb->linesize);

        // Copy RGB data to a reusable managed buffer ring. The presentation queue is shallow,
        // so eight buffers avoids per-frame LOH allocations without overwriting queued frames.
        if (_managedFrameBuffers == null || _managedFrameBuffers.Length == 0)
            throw new InvalidOperationException("Managed frame buffers are not initialized.");

        var frameData = _managedFrameBuffers[_managedFrameBufferIndex];
        _managedFrameBufferIndex = (_managedFrameBufferIndex + 1) % _managedFrameBuffers.Length;
        Marshal.Copy((IntPtr)_rgbBuffer, frameData, 0, _rgbBufferSize);

        return new VideoFrame
        {
            Data = frameData,
            Width = Width,
            Height = Height,
            Stride = Stride,
            Timestamp = _frame->pts,
            SourceTimestampUs = sourceTimestampUs,
            ReceivedTimestampUs = receivedTimestampUs
        };
    }

    /// <summary>
    /// Flush the decoder (call when stream ends or resets)
    /// </summary>
    public void Flush()
    {
        ffmpeg.avcodec_flush_buffers(_codecContext);
    }

    /// <summary>
    /// Get error message from FFmpeg error code
    /// </summary>
    private static string GetErrorMessage(int error)
    {
        byte* buffer = stackalloc byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
        ffmpeg.av_strerror(error, buffer, (ulong)ffmpeg.AV_ERROR_MAX_STRING_SIZE);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Error {error}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_rgbBuffer != null)
        {
            Marshal.FreeHGlobal((IntPtr)_rgbBuffer);
            _rgbBuffer = null;
        }

        if (_frame != null)
        {
            fixed (AVFrame** framePtr = &_frame)
                ffmpeg.av_frame_free(framePtr);
        }

        if (_frameRgb != null)
        {
            fixed (AVFrame** framePtr = &_frameRgb)
                ffmpeg.av_frame_free(framePtr);
        }

        if (_packet != null)
        {
            fixed (AVPacket** packetPtr = &_packet)
                ffmpeg.av_packet_free(packetPtr);
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** contextPtr = &_codecContext)
                ffmpeg.avcodec_free_context(contextPtr);
        }

        _disposed = true;
    }
}
