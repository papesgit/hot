using System;

namespace HlaeObsTools.Services.Gsi;

public sealed class RadarProjector
{
    private const double DefaultRadarImageSize = 1024.0;
    private readonly RadarConfigProvider _configProvider;
    private double _radarImageWidth = DefaultRadarImageSize;
    private double _radarImageHeight = DefaultRadarImageSize;

    public RadarProjector(RadarConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    /// <summary>
    /// Sets the dimensions of the currently displayed radar image. Radar metadata is
    /// defined for a 1024x1024 image, so their scale is adjusted to match the
    /// source image while offsets retain their 1024px-reference behavior.
    /// </summary>
    public void SetRadarImageSize(int width, int height)
    {
        _radarImageWidth = width > 0 ? width : DefaultRadarImageSize;
        _radarImageHeight = height > 0 ? height : DefaultRadarImageSize;
    }

    public bool TryProject(string? mapName, Vec3 worldPos, out double relX, out double relY, out string level)
    {
        return TryProject(mapName, worldPos, null, out relX, out relY, out level);
    }

    public bool TryProject(string? mapName, Vec3 worldPos, string? forcedLevel, out double relX, out double relY, out string level)
    {
        relX = relY = 0;
        level = "default";

        if (!_configProvider.TryGet(mapName, out var config) || config.Scale == 0)
            return false;

        double offsetX = 0;
        double offsetY = 0;

        if (config.Levels.Count > 0)
        {
            RadarLevel? selected = null;
            if (!string.IsNullOrWhiteSpace(forcedLevel))
            {
                foreach (var lvl in config.Levels)
                {
                    if (string.Equals(lvl.Name, forcedLevel, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = lvl;
                        break;
                    }
                }
            }

            if (selected == null)
            {
                foreach (var lvl in config.Levels)
                {
                    if (worldPos.Z > lvl.AltitudeMin)
                    {
                        selected = lvl;
                        break;
                    }
                }
            }

            if (selected != null)
            {
                level = selected.Name;
                offsetX = selected.OffsetX;
                offsetY = selected.OffsetY;
            }
        }

        var scaleX = config.Scale * (_radarImageWidth / DefaultRadarImageSize);
        var scaleY = config.Scale * (_radarImageHeight / DefaultRadarImageSize);
        relX = ((worldPos.X - config.PosX) / scaleX + offsetX) / DefaultRadarImageSize;
        relY = ((worldPos.Y - config.PosY) / -scaleY + offsetY) / DefaultRadarImageSize;
        return true;
    }
}
