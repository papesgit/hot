using System;

namespace HlaeObsTools.Services.Gsi;

public sealed class RadarProjector
{
    private readonly RadarConfigProvider _configProvider;

    public RadarProjector(RadarConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    public bool TryProject(string? mapName, Vec3 worldPos, out double relX, out double relY, out string level)
    {
        relX = relY = 0;
        level = "default";

        if (!_configProvider.TryGet(mapName, out var config) || config.Scale == 0)
            return false;

        if (config.Levels.Count > 0)
        {
            foreach (var lvl in config.Levels)
            {
                if (worldPos.Z > lvl.AltitudeMin)
                {
                    level = lvl.Name;
                    break;
                }
            }
        }

        relX = ((worldPos.X - config.PosX) / config.Scale) / 1024.0;
        relY = ((worldPos.Y - config.PosY) / -config.Scale) / 1024.0;
        return true;
    }
}
