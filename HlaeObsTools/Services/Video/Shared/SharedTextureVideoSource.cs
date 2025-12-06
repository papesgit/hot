using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Video;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using static Vortice.Direct3D11.D3D11;

namespace HlaeObsTools.Services.Video.Shared;

/// <summary>
/// Reads frames from a named D3D11 shared texture and emits them as BGRA frames.
/// </summary>
public sealed class SharedTextureVideoSource : IVideoSource
{
    public const string DefaultSharedTextureName = "HLAE_ObsSharedTexture";

    private readonly string _sharedName;
    private ID3D11Device1? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _sharedTexture;
    private ID3D11Texture2D? _stagingTexture;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private bool _disposed;
    private int _width;
    private int _height;

    public event EventHandler<VideoFrame>? FrameReceived;

    public (int Width, int Height) Dimensions => (_width, _height);

    public bool IsActive { get; private set; }

    public SharedTextureVideoSource(string? sharedName = null)
    {
        _sharedName = string.IsNullOrWhiteSpace(sharedName) ? DefaultSharedTextureName : sharedName!;
    }

    public void Start()
    {
        if (IsActive)
            return;

        try
        {
            CreateDevice();
            OpenSharedTexture();
            CreateStagingTexture();

            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpFramesAsync(_cts.Token));
            IsActive = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        if (!IsActive)
            return;

        _cts?.Cancel();
        try
        {
            _pumpTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignore pump task failures during shutdown
        }
        _cts?.Dispose();
        _cts = null;
        _pumpTask = null;

        ReleaseResources();
        IsActive = false;
    }

    private void CreateDevice()
    {
        ID3D11Device tempDevice;
        Result result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out tempDevice);

        if (result.Failure)
        {
            result = D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                null,
                out tempDevice);
        }

        if (result.Failure)
        {
            throw new InvalidOperationException("Failed to create D3D11 device for shared texture access.");
        }

        var device1 = tempDevice.QueryInterfaceOrNull<ID3D11Device1>();
        if (device1 == null)
        {
            tempDevice.Dispose();
            throw new InvalidOperationException("ID3D11Device1 is required for shared texture access.");
        }

        _device = device1;
        _context = _device.ImmediateContext;

        tempDevice.Dispose();
    }

    private void OpenSharedTexture()
    {
        if (_device == null)
            throw new InvalidOperationException("D3D11 device not initialized.");

        var accessFlags = Vortice.Direct3D11.SharedResourceFlags.Read | Vortice.Direct3D11.SharedResourceFlags.Write;

        _sharedTexture = _device.OpenSharedResourceByName<ID3D11Texture2D>(
            _sharedName,
            accessFlags)
            ?? throw new InvalidOperationException($"Shared texture '{_sharedName}' not available.");

        var desc = _sharedTexture.Description;
        _width = (int)desc.Width;
        _height = (int)desc.Height;

        if (desc.Format != Format.R8G8B8A8_UNorm)
        {
            throw new InvalidOperationException($"Unexpected shared texture format: {desc.Format}. Expected R8G8B8A8_UNorm.");
        }
    }

    private void CreateStagingTexture()
    {
        if (_device == null || _sharedTexture == null)
            throw new InvalidOperationException("Shared texture not opened.");

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1u, 0u),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        _stagingTexture = _device.CreateTexture2D(stagingDesc);
    }
    private byte[]? _frameBuffer;
    private async Task PumpFramesAsync(CancellationToken token)
    {
        if (_context == null || _stagingTexture == null || _sharedTexture == null)
            return;

        int rowSize = _width * 4;
        _frameBuffer ??= new byte[_height * rowSize];
        var data = _frameBuffer;

        try
        {
            while (!token.IsCancellationRequested)
            {
                _context.CopyResource(_stagingTexture, _sharedTexture);

                var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    for (int y = 0; y < _height; y++)
                    {
                        IntPtr src = IntPtr.Add(mapped.DataPointer, (int)(y * mapped.RowPitch));
                        int destOffset = y * rowSize;
                        Marshal.Copy(src, data, destOffset, rowSize);
                        SwapRedBlue(data, destOffset, rowSize);
                    }

                    FrameReceived?.Invoke(this, new VideoFrame
                    {
                        Data = data,
                        Width = _width,
                        Height = _height,
                        Stride = rowSize,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
                finally
                {
                    _context.Unmap(_stagingTexture, 0);
                }

                // Yield without throttling to keep up with the game's Present rate.
                await Task.Delay(5, token);
            }
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown
        }
        catch
        {
            IsActive = false;
            ReleaseResources();
        }
    }

    private static void SwapRedBlue(byte[] buffer, int offset, int length)
    {
        for (int i = offset; i < offset + length; i += 4)
        {
            byte r = buffer[i];
            buffer[i] = buffer[i + 2];
            buffer[i + 2] = r;
        }
    }

    private void ReleaseResources()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _sharedTexture?.Dispose();
        _sharedTexture = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}
