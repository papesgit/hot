using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ValveResourceFormat.Renderer.Materials;

namespace HlaeObsTools.ViewModels;

/// <summary>
/// Shared settings for the 3D viewport.
/// </summary>
public sealed class Viewport3DSettings : ViewModelBase
{
    private string _mapObjPath = string.Empty;
    private string _cs2GameFolder = string.Empty;
    private string _selectedMapName = string.Empty;
    private ViewportMapOption? _selectedMap;
    private bool _activeDutyMapsOnly = true;
    private bool _useAltPlayerBinds;
    private bool _showPlayerPins = true;
    private float _pinScale = 200.0f;
    private float _pinOffsetZ = 55.0f;
    private float _viewportMouseScale = 0.75f;
    private float _viewportFpsCap = 60.0f;
    private bool _postprocessEnabled = true;
    private bool _colorCorrectionEnabled = true;
    private bool _dynamicShadowsEnabled = true;
    private bool _wireframeEnabled;
    private bool _skipWaterEnabled;
    private bool _skipTranslucentEnabled;
    private bool _showFps;
    private bool _viewportCampathMode;
    private bool _viewportCampathOverlayEnabled = true;
    private bool _campathGizmoLocalSpace = true;
    private bool _viewportCampathSyncEnabled;
    private bool _liveLinkEnabled;
    private bool _liveLinkItemIconsEnabled = true;
    private bool _liveLinkWeaponIconsEnabled = true;
    private bool _liveLinkGrenadeIconsEnabled = true;
    private bool _liveLinkProjectileIconsEnabled = true;
    private bool _liveLinkObjectiveIconsEnabled = true;
    private bool _liveLinkDeadPlayerIconsEnabled = true;
    private int _liveLinkPort = 31237;
    private int _targetOrbitResetRequest;
    private int _shadowTextureSize = 1024;
    private int _maxTextureSize = 1024;
    private string _renderMode = "Default";

    public IReadOnlyList<string> RenderModeOptions { get; } = RenderModes.Items
        .Where(mode => !mode.IsHeader)
        .Select(mode => mode.Name)
        .Concat(new[] { "FastUnlit" })
        .ToArray();

    public IReadOnlyList<int> ShadowTextureSizeOptions { get; } = new[] { 256, 512, 1024, 2048, 4096 };
    public IReadOnlyList<int> MaxTextureSizeOptions { get; } = new[] { 64, 128, 256, 512, 1024, 2048 };
    public ObservableCollection<ViewportMapOption> AvailableMaps { get; } = new();

    /// <summary>
    /// Path to the Source 2 map file.
    /// </summary>
    public string MapObjPath
    {
        get => _mapObjPath;
        set => SetProperty(ref _mapObjPath, value ?? string.Empty);
    }

    /// <summary>
    /// Counter-Strike 2 installation folder.
    /// </summary>
    public string Cs2GameFolder
    {
        get => _cs2GameFolder;
        set => SetProperty(ref _cs2GameFolder, value ?? string.Empty);
    }

    /// <summary>
    /// Name of the selected map package without extension.
    /// </summary>
    public string SelectedMapName
    {
        get => _selectedMapName;
        set => SetProperty(ref _selectedMapName, value ?? string.Empty);
    }

    public ViewportMapOption? SelectedMap
    {
        get => _selectedMap;
        set => SetProperty(ref _selectedMap, value);
    }

    /// <summary>
    /// Show only the current active duty map pool in the map dropdown.
    /// </summary>
    public bool ActiveDutyMapsOnly
    {
        get => _activeDutyMapsOnly;
        set => SetProperty(ref _activeDutyMapsOnly, value);
    }

    /// <summary>
    /// Whether to use alternative player bind labels (Q,E,R,T,Z for slots 6-0).
    /// </summary>
    public bool UseAltPlayerBinds
    {
        get => _useAltPlayerBinds;
        set => SetProperty(ref _useAltPlayerBinds, value);
    }

    /// <summary>
    /// Draw player position pins in the 3D viewport.
    /// </summary>
    public bool ShowPlayerPins
    {
        get => _showPlayerPins;
        set => SetProperty(ref _showPlayerPins, value);
    }

    /// <summary>
    /// Scale factor for player pins in the 3D viewport.
    /// </summary>
    public float PinScale
    {
        get => _pinScale;
        set => SetProperty(ref _pinScale, value);
    }

    /// <summary>
    /// Pin offset in world space (Z).
    /// </summary>
    public float PinOffsetZ
    {
        get => _pinOffsetZ;
        set => SetProperty(ref _pinOffsetZ, value);
    }

    /// <summary>
    /// Mouse sensitivity scale for the 3D viewport freecam.
    /// </summary>
    public float ViewportMouseScale
    {
        get => _viewportMouseScale;
        set => SetProperty(ref _viewportMouseScale, value);
    }

    /// <summary>
    /// FPS cap for the 3D viewport (0 = uncapped).
    /// </summary>
    public float ViewportFpsCap
    {
        get => _viewportFpsCap;
        set => SetProperty(ref _viewportFpsCap, value);
    }

    /// <summary>
    /// Toggle postprocessing in the 3D viewport.
    /// </summary>
    public bool PostprocessEnabled
    {
        get => _postprocessEnabled;
        set => SetProperty(ref _postprocessEnabled, value);
    }

    /// <summary>
    /// Toggle color correction in the 3D viewport.
    /// </summary>
    public bool ColorCorrectionEnabled
    {
        get => _colorCorrectionEnabled;
        set => SetProperty(ref _colorCorrectionEnabled, value);
    }

    /// <summary>
    /// Toggle dynamic shadows in the 3D viewport.
    /// </summary>
    public bool DynamicShadowsEnabled
    {
        get => _dynamicShadowsEnabled;
        set => SetProperty(ref _dynamicShadowsEnabled, value);
    }

    /// <summary>
    /// Toggle wireframe rendering in the 3D viewport.
    /// </summary>
    public bool WireframeEnabled
    {
        get => _wireframeEnabled;
        set => SetProperty(ref _wireframeEnabled, value);
    }

    /// <summary>
    /// Skip rendering water passes in the 3D viewport.
    /// </summary>
    public bool SkipWaterEnabled
    {
        get => _skipWaterEnabled;
        set => SetProperty(ref _skipWaterEnabled, value);
    }

    /// <summary>
    /// Skip rendering translucent passes in the 3D viewport.
    /// </summary>
    public bool SkipTranslucentEnabled
    {
        get => _skipTranslucentEnabled;
        set => SetProperty(ref _skipTranslucentEnabled, value);
    }

    /// <summary>
    /// Show FPS overlay in the 3D viewport.
    /// </summary>
    public bool ShowFps
    {
        get => _showFps;
        set => SetProperty(ref _showFps, value);
    }

    /// <summary>
    /// Enable viewport campath editor mode (sequencer under viewport).
    /// </summary>
    public bool ViewportCampathMode
    {
        get => _viewportCampathMode;
        set => SetProperty(ref _viewportCampathMode, value);
    }

    /// <summary>
    /// Draw campath overlay in the viewport.
    /// </summary>
    public bool ViewportCampathOverlayEnabled
    {
        get => _viewportCampathOverlayEnabled;
        set => SetProperty(ref _viewportCampathOverlayEnabled, value);
    }

    /// <summary>
    /// Use local-space axes for the campath gizmo.
    /// </summary>
    public bool CampathGizmoLocalSpace
    {
        get => _campathGizmoLocalSpace;
        set => SetProperty(ref _campathGizmoLocalSpace, value);
    }

    /// <summary>
    /// Synchronize viewport campath edits to HLAE.
    /// </summary>
    public bool ViewportCampathSyncEnabled
    {
        get => _viewportCampathSyncEnabled;
        set => SetProperty(ref _viewportCampathSyncEnabled, value);
    }

    /// <summary>
    /// Receive CS2 LiveLink UDP frames and render streamed entities in the VRF viewport.
    /// </summary>
    public bool LiveLinkEnabled
    {
        get => _liveLinkEnabled;
        set => SetProperty(ref _liveLinkEnabled, value);
    }

    /// <summary>
    /// Draw HUD icon billboards for streamed LiveLink weapons, projectiles, and bombs.
    /// </summary>
    public bool LiveLinkItemIconsEnabled
    {
        get => _liveLinkItemIconsEnabled;
        set => SetProperty(ref _liveLinkItemIconsEnabled, value);
    }

    /// <summary>
    /// Draw HUD icon billboards for dropped LiveLink weapons.
    /// </summary>
    public bool LiveLinkWeaponIconsEnabled
    {
        get => _liveLinkWeaponIconsEnabled;
        set => SetProperty(ref _liveLinkWeaponIconsEnabled, value);
    }

    /// <summary>
    /// Draw HUD icon billboards for dropped LiveLink grenades.
    /// </summary>
    public bool LiveLinkGrenadeIconsEnabled
    {
        get => _liveLinkGrenadeIconsEnabled;
        set => SetProperty(ref _liveLinkGrenadeIconsEnabled, value);
    }

    /// <summary>
    /// Draw HUD icon billboards for LiveLink projectiles.
    /// </summary>
    public bool LiveLinkProjectileIconsEnabled
    {
        get => _liveLinkProjectileIconsEnabled;
        set => SetProperty(ref _liveLinkProjectileIconsEnabled, value);
    }

    /// <summary>
    /// Draw HUD icon billboards for LiveLink defusers and C4.
    /// </summary>
    public bool LiveLinkObjectiveIconsEnabled
    {
        get => _liveLinkObjectiveIconsEnabled;
        set => SetProperty(ref _liveLinkObjectiveIconsEnabled, value);
    }

    /// <summary>
    /// Draw HUD icon billboards for dead LiveLink players.
    /// </summary>
    public bool LiveLinkDeadPlayerIconsEnabled
    {
        get => _liveLinkDeadPlayerIconsEnabled;
        set => SetProperty(ref _liveLinkDeadPlayerIconsEnabled, value);
    }

    /// <summary>
    /// UDP port used for CS2 LiveLink frames.
    /// </summary>
    public int LiveLinkPort
    {
        get => _liveLinkPort;
        set => SetProperty(ref _liveLinkPort, value < 1 ? 1 : value > 65535 ? 65535 : value);
    }

    /// <summary>
    /// Monotonic request counter used to return the VRF viewport from target orbit to normal orbit.
    /// </summary>
    public int TargetOrbitResetRequest
    {
        get => _targetOrbitResetRequest;
        set => SetProperty(ref _targetOrbitResetRequest, value);
    }

    /// <summary>
    /// Shadow map texture size (power-of-two).
    /// </summary>
    public int ShadowTextureSize
    {
        get => _shadowTextureSize;
        set => SetProperty(ref _shadowTextureSize, value);
    }

    /// <summary>
    /// Maximum texture size to load for the viewport renderer.
    /// </summary>
    public int MaxTextureSize
    {
        get => _maxTextureSize;
        set => SetProperty(ref _maxTextureSize, value);
    }

    /// <summary>
    /// Render mode for the VRF renderer.
    /// </summary>
    public string RenderMode
    {
        get => _renderMode;
        set => SetProperty(ref _renderMode, string.IsNullOrWhiteSpace(value) ? "Default" : value);
    }
}
