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

    public string GetProfilePath(string profileName)
    {
        var name = SanitizeProfileName(profileName);
        return Path.Combine(_baseDir, $"{name}.json");
    }

    public GraphicsProfile Load(string profileName)
    {
        var path = GetProfilePath(profileName);
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

    public void Save(string profileName, GraphicsProfile profile)
    {
        var path = GetProfilePath(profileName);
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

    public string[] ListProfiles()
    {
        try
        {
            if (!Directory.Exists(_baseDir))
                return Array.Empty<string>();

            var files = Directory.GetFiles(_baseDir, "*.json", SearchOption.TopDirectoryOnly);
            var names = new string[files.Length];
            for (var i = 0; i < files.Length; i++)
            {
                names[i] = Path.GetFileNameWithoutExtension(files[i]);
            }
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Delete(string profileName)
    {
        var path = GetProfilePath(profileName);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore delete errors
        }
    }

    private static string SanitizeProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return "default";
        return profileName.Trim().ToLowerInvariant();
    }
}
