using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlaeObsTools.Services.Graphics;

public sealed class GraphicsProfileStorage
{
    public const string EmptyProfileName = "empty";

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
        if (IsReservedProfileName(profileName))
            return new GraphicsProfile();

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

    public bool Save(string profileName, GraphicsProfile profile)
    {
        if (IsReservedProfileName(profileName))
            return false;

        var path = GetProfilePath(profileName);
        try
        {
            var json = JsonSerializer.Serialize(profile, _jsonOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            // ignore save errors
            return false;
        }
    }

    public string[] ListProfiles()
    {
        try
        {
            if (!Directory.Exists(_baseDir))
                return new[] { EmptyProfileName };

            var files = Directory.GetFiles(_baseDir, "*.json", SearchOption.TopDirectoryOnly);
            var names = new string[files.Length + 1];
            names[0] = EmptyProfileName;
            for (var i = 0; i < files.Length; i++)
            {
                names[i + 1] = Path.GetFileNameWithoutExtension(files[i]);
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => string.Equals(name, EmptyProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return new[] { EmptyProfileName };
        }
    }

    public bool Delete(string profileName)
    {
        if (IsReservedProfileName(profileName))
            return false;

        var path = GetProfilePath(profileName);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            return false;
        }
        catch
        {
            // ignore delete errors
            return false;
        }
    }

    public static bool IsReservedProfileName(string? profileName)
    {
        return string.Equals(SanitizeProfileName(profileName), EmptyProfileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeProfileName(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return EmptyProfileName;
        return profileName.Trim().ToLowerInvariant();
    }
}
