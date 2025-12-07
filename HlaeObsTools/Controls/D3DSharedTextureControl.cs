using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using D3D11SharedResourceFlags = Vortice.Direct3D11.SharedResourceFlags;
using DXGISharedResourceFlags = Vortice.DXGI.SharedResourceFlags;

namespace HlaeObsTools.Controls;

/// <summary>
/// Avalonia control that renders a D3D11 shared texture using GPU composition.
/// Uses Avalonia's ICompositionGpuInterop to import the texture directly on GPU (no CPU copy).
/// </summary>
public class D3DSharedTextureControl : Control
{
    private const string SharedTextureName = "HLAE_ObsSharedTexture";

    private ID3D11Device? _device;
    private ID3D11Device1? _device1;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _sharedTexture;  // Texture opened by name from HLAE
    private ID3D11Texture2D? _intermediateTexture;  // Our own texture with shared handle and keyed mutex
    private IDXGIKeyedMutex? _keyedMutex;
    private IntPtr _sharedHandle;
    private CancellationTokenSource? _cts;
    private Task? _updateLoop;
    private bool _loggedDevice1Missing;
    private bool _loggedFirstFrame;
    private bool _loggedFirstUpdate;
    private int _textureWidth;
    private int _textureHeight;
    private readonly object _textureLock = new();
    private int _updateSurfaceCallCount;

    // Composition
    private CompositionSurfaceVisual? _surfaceVisual;
    private CompositionDrawingSurface? _drawingSurface;
    private Compositor? _compositor;
    private ICompositionGpuInterop? _gpuInterop;
    private ICompositionImportedGpuImage? _importedImage;
    private bool _initialized;

    public D3DSharedTextureControl()
    {
        // Flip vertically to correct D3D texture orientation
        RenderTransform = new Avalonia.Media.ScaleTransform { ScaleX = 1, ScaleY = -1 };
        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InitializeComposition();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopRenderer();

        // Note: ICompositionImportedGpuImage doesn't have Dispose, it's managed by Avalonia
        _importedImage = null;
        _drawingSurface = null;
        _surfaceVisual = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty && _surfaceVisual != null)
        {
            var newBounds = (Rect)change.NewValue!;
            if (newBounds.Width > 0 && newBounds.Height > 0)
            {
                _surfaceVisual.Size = new Vector(newBounds.Width, newBounds.Height);
            }
        }
    }

    private async void InitializeComposition()
    {
        try
        {
            var elementVisual = ElementComposition.GetElementVisual(this);
            if (elementVisual == null)
            {
                Log("Failed to get element visual");
                return;
            }

            _compositor = elementVisual.Compositor;

            // Create drawing surface and visual
            _drawingSurface = _compositor.CreateDrawingSurface();
            _surfaceVisual = _compositor.CreateSurfaceVisual();
            _surfaceVisual.Size = new Vector(Bounds.Width, Bounds.Height);
            _surfaceVisual.Surface = _drawingSurface;
            ElementComposition.SetElementChildVisual(this, _surfaceVisual);

            // Get GPU interop
            _gpuInterop = await _compositor.TryGetCompositionGpuInterop();
            if (_gpuInterop == null)
            {
                Log("GPU interop not available");
                return;
            }

            var supportedTypes = _gpuInterop.SupportedImageHandleTypes;
            Log($"Supported handle types: {string.Join(", ", supportedTypes)}");

            _initialized = true;
            Log("Composition initialized");
        }
        catch (Exception ex)
        {
            Log($"InitializeComposition error: {ex.Message}");
        }
    }

    public void StartRenderer()
    {
        if (_updateLoop != null) return;

        Log("StartRenderer called");
        _cts = new CancellationTokenSource();
        _updateLoop = Task.Run(() => UpdateLoop(_cts.Token));
    }

    public void StopRenderer()
    {
        Log("StopRenderer called");
        _cts?.Cancel();

        // Wait for update loop to exit (give it time to finish current operation and respond to cancellation)
        try
        {
            if (_updateLoop != null && !_updateLoop.Wait(500))
            {
                Log("Warning: Update loop did not exit cleanly within timeout");
            }
        }
        catch { /* ignore */ }

        _cts?.Dispose();
        _cts = null;
        _updateLoop = null;

        // Clear compositor resources on UI thread to avoid deadlocks
        Dispatcher.UIThread.Post(() =>
        {
            _importedImage = null;  // Let compositor release the texture
        });

        // Give compositor time to release the mutex
        Thread.Sleep(50);

        lock (_textureLock)
        {
            // Release the mutex if we're holding it (try both keys)
            if (_keyedMutex != null)
            {
                try { _keyedMutex.ReleaseSync(0); } catch { }
                try { _keyedMutex.ReleaseSync(1); } catch { }
                _keyedMutex.Release();
                _keyedMutex = null;
            }

            _intermediateTexture?.Release();
            _intermediateTexture = null;
            _sharedTexture?.Release();
            _sharedTexture = null;
            _sharedHandle = IntPtr.Zero;
        }

        _context?.Release();
        _context = null;
        _device1?.Release();
        _device1 = null;
        _device?.Release();
        _device = null;

        _loggedDevice1Missing = false;
        _loggedFirstFrame = false;
        _loggedFirstUpdate = false;
        _updateSurfaceCallCount = 0;

        Log("StopRenderer completed");
    }

    private void UpdateLoop(CancellationToken token)
    {
        if (!CreateDevice())
        {
            Log("Failed to create D3D device");
            return;
        }

        Log("UpdateLoop started");

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_loggedFirstFrame)
                {
                    Log("UpdateLoop first frame");
                    _loggedFirstFrame = true;
                }

                EnsureSharedTexture();

                if (_sharedTexture != null && _initialized && _context != null)
                {
                    lock (_textureLock)
                    {
                        // Ensure we have an intermediate texture with the same dimensions
                        EnsureIntermediateTexture(_textureWidth, _textureHeight);

                        if (_intermediateTexture != null && _sharedHandle != IntPtr.Zero && _keyedMutex != null)
                        {
                            // Check for cancellation before blocking on mutex
                            if (token.IsCancellationRequested)
                                break;

                            try
                            {
                                // Acquire mutex with key 0 (our producer key)
                                // Use a timeout to avoid blocking forever on shutdown
                                _keyedMutex.AcquireSync(0, 100);

                                try
                                {
                                    // Copy from HLAE's shared texture to our intermediate texture
                                    _context.CopyResource(_intermediateTexture, _sharedTexture);
                                    _context.Flush();
                                }
                                finally
                                {
                                    // Release with key 1 (compositor will acquire with key 1)
                                    _keyedMutex.ReleaseSync(1);
                                }

                                // Queue composition update
                                QueueCompositionUpdate(_sharedHandle, _textureWidth, _textureHeight);
                            }
                            catch (SharpGen.Runtime.SharpGenException)
                            {
                                // Mutex operation failed/timeout, skip this frame
                            }
                        }
                    }
                }

                // Small delay to prevent busy loop while still allowing high framerates
                // GPU operations are fast, so this can run at whatever rate HLAE updates
                Task.Delay(1, token).Wait(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"UpdateLoop error: {ex.Message}");
                Task.Delay(100, token).Wait(token);
            }
        }

        Log("UpdateLoop ended");
    }

    private void QueueCompositionUpdate(IntPtr handle, int width, int height)
    {
        // Post to UI thread - UpdateWithKeyedMutexAsync must be called from UI thread
        Dispatcher.UIThread.Post(() => UpdateCompositionSurface(handle, width, height), DispatcherPriority.Render);
    }

    private void UpdateCompositionSurface(IntPtr handle, int width, int height)
    {
        _updateSurfaceCallCount++;

        // if (_updateSurfaceCallCount <= 5 || _updateSurfaceCallCount % 60 == 0)
        // {
        //     Log($"UpdateCompositionSurface called (#{_updateSurfaceCallCount}): handle=0x{handle.ToInt64():X}, size={width}x{height}");
        // }

        if (_gpuInterop == null || _drawingSurface == null)
        {
            Log("UpdateCompositionSurface: gpuInterop or drawingSurface is null");
            return;
        }

        if (handle == IntPtr.Zero)
        {
            Log("UpdateCompositionSurface: handle is zero, cannot import");
            return;
        }

        // Ensure visual size matches control bounds
        if (_surfaceVisual != null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _surfaceVisual.Size = new Vector(Bounds.Width, Bounds.Height);

            if (_updateSurfaceCallCount == 1)
            {
                Log($"Visual size set to: {Bounds.Width}x{Bounds.Height}");
            }
        }
        else if (_updateSurfaceCallCount == 1)
        {
            Log($"Visual or Bounds invalid: visual={_surfaceVisual != null}, Bounds={Bounds.Width}x{Bounds.Height}");
        }

        try
        {
            // Re-import if handle changed (only needed once per texture)
            if (_importedImage == null)
            {
                var supportedTypes = _gpuInterop.SupportedImageHandleTypes;
                var handleType = supportedTypes.Any(t => t == KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
                    ? KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle
                    : KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle;

                Log($"Attempting to import: handle=0x{handle.ToInt64():X}, type={handleType}, size={width}x{height}");

                var platformHandle = new PlatformHandle(handle, handleType);

                var properties = new PlatformGraphicsExternalImageProperties
                {
                    Width = width,
                    Height = height,
                    Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm  // Match intermediate texture format
                };

                _importedImage = _gpuInterop.ImportImage(platformHandle, properties);
                Log($"Successfully imported D3D11 texture: {width}x{height}");
            }

            // IMPORTANT: Call UpdateWithKeyedMutexAsync EVERY frame to re-read the texture
            // acquireKey=1 (matches our ReleaseSync(1)), releaseKey=0 (we'll acquire with 0 next frame)
            if (_importedImage != null)
            {
                var updateTask = _drawingSurface.UpdateWithKeyedMutexAsync(_importedImage, 1, 0);

                if (!_loggedFirstUpdate)
                {
                    Log("First UpdateWithKeyedMutexAsync call (will be called every frame)");
                    _loggedFirstUpdate = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"UpdateCompositionSurface error: {ex}");

            // Reset on error (ICompositionImportedGpuImage is managed by Avalonia)
            _importedImage = null;
        }
    }

    private bool CreateDevice()
    {
        if (_device != null) return true;

        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out _device,
            out _,
            out _context);

        if (result.Failure)
        {
            result = D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                levels,
                out _device,
                out _,
                out _context);
        }

        if (result.Failure || _device == null || _context == null)
        {
            Log("Failed to create D3D11 device");
            return false;
        }

        _device1 = _device.QueryInterfaceOrNull<ID3D11Device1>();
        if (_device1 == null && !_loggedDevice1Missing)
        {
            Log("ID3D11Device1 not available; cannot open shared texture by name.");
            _loggedDevice1Missing = true;
            return false;
        }

        Log("D3D11 device created successfully");
        return true;
    }

    private void EnsureIntermediateTexture(int width, int height)
    {
        if (_device == null || width <= 0 || height <= 0) return;

        // Check if we already have a texture with the right dimensions
        if (_intermediateTexture != null)
        {
            var desc = _intermediateTexture.Description;
            if (desc.Width == width && desc.Height == height)
                return; // Already have the right size

            // Size changed, recreate
            _keyedMutex?.Release();
            _keyedMutex = null;
            _intermediateTexture.Release();
            _intermediateTexture = null;
            _sharedHandle = IntPtr.Zero;
            _importedImage = null; // Force re-import
        }

        try
        {
            // Create our own texture with shared keyed mutex flags for synchronization
            // Match HLAE's format (R8G8B8A8) so CopyResource works correctly
            var textureDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,  // Match HLAE's source format
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.SharedKeyedMutex  // Keyed mutex for sync
            };

            _intermediateTexture = _device.CreateTexture2D(textureDesc);

            // Get the keyed mutex interface
            _keyedMutex = _intermediateTexture.QueryInterface<IDXGIKeyedMutex>();

            // Get the shared handle from OUR texture
            using var dxgiResource = _intermediateTexture.QueryInterface<IDXGIResource>();
            _sharedHandle = dxgiResource.SharedHandle;

            Log($"Created intermediate texture with keyed mutex: {width}x{height}, handle=0x{_sharedHandle.ToInt64():X}");
        }
        catch (Exception ex)
        {
            Log($"Failed to create intermediate texture: {ex.Message}");
            _keyedMutex?.Release();
            _keyedMutex = null;
            _intermediateTexture?.Release();
            _intermediateTexture = null;
            _sharedHandle = IntPtr.Zero;
        }
    }

    private void EnsureSharedTexture()
    {
        if (_device == null || _device1 == null) return;

        lock (_textureLock)
        {
            if (_sharedTexture != null) return;
        }

        const int rwAccessInt = unchecked((int)(DXGISharedResourceFlags.Read | DXGISharedResourceFlags.Write));
        const int rAccessInt = unchecked((int)DXGISharedResourceFlags.Read);

        ID3D11Texture2D tex = null!;
        SharpGen.Runtime.Result hr;

        try
        {
            hr = _device1.OpenSharedResourceByName(
                SharedTextureName,
                unchecked((D3D11SharedResourceFlags)rwAccessInt),
                out tex);
        }
        catch (Exception ex)
        {
            Log($"OpenSharedResourceByName threw: {ex.Message}");
            return;
        }

        if (hr.Failure || tex == null)
        {
            // Try read-only
            try
            {
                hr = _device1.OpenSharedResourceByName(
                    SharedTextureName,
                    unchecked((D3D11SharedResourceFlags)rAccessInt),
                    out tex);
            }
            catch (Exception ex)
            {
                Log($"OpenSharedResourceByName (r) threw: {ex.Message}");
                return;
            }
        }

        if (hr.Success && tex != null)
        {
            lock (_textureLock)
            {
                _sharedTexture = tex;
                _textureWidth = (int)tex.Description.Width;
                _textureHeight = (int)tex.Description.Height;

                Log($"Opened shared texture from HLAE: {_textureWidth}x{_textureHeight}, Format={tex.Description.Format}");
                // Note: We don't get a handle from this texture - we'll create our own intermediate texture instead
            }
        }
    }

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] D3DSharedTextureControl: {msg}";
        Console.WriteLine(line);
    }
}
