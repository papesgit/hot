using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace GfxProducerService.Graphics;

public sealed class GraphicsD3DDevice : IDisposable
{
    private readonly object _lock = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;

    public ID3D11Device Device => _device ?? throw new InvalidOperationException("D3D device not initialized.");
    public ID3D11DeviceContext Context => _context ?? throw new InvalidOperationException("D3D context not initialized.");
    public object ContextLock => _lock;

    public void EnsureInitialized()
    {
        if (_device != null && _context != null)
            return;

        var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
        var result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device device,
            out _,
            out ID3D11DeviceContext baseContext
        );
        if (result.Failure || device == null || baseContext == null)
            throw new InvalidOperationException("Failed to create D3D11 device.");

        _device = device;
        _context = baseContext;

        try
        {
            using var multithread = baseContext.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);
        }
        catch
        {
            // Ignore if multithread interface isn't available.
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }
}
