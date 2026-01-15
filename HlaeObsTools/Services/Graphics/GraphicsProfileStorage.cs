using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlaeObsTools.Services.Graphics;

public sealed class GraphicsProfileStorage
{
    private readonly string _baseDir;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GraphicsProfileStorage()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _baseDir = Path.Combine(appData, "HlaeObsTools", "graphics");
        Directory.CreateDirectory(_baseDir);
    }

    public string GetProfilePath(string mapName)
    {
        var name = SanitizeMapName(mapName);
        return Path.Combine(_baseDir, $"{name}.json");
    }

    public GraphicsProfile Load(string mapName)
    {
        var path = GetProfilePath(mapName);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<GraphicsProfile>(json, _jsonOptions);
                if (data != null)
                    return data;
            }
        }
        catch
        {
            // ignore load errors, return defaults
        }

        return new GraphicsProfile();
    }

    public void Save(string mapName, GraphicsProfile profile)
    {
        var path = GetProfilePath(mapName);
        try
        {
            var json = JsonSerializer.Serialize(profile, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // ignore save errors
        }
    }

    private static string SanitizeMapName(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return "default";
        return mapName.Trim().ToLowerInvariant();
    }
}
