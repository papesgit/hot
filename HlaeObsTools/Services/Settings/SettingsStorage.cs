using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlaeObsTools.Services.Settings;

public class SettingsStorage
{
    private readonly string _storagePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SettingsStorage()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = Path.Combine(appData, "HlaeObsTools");
        Directory.CreateDirectory(baseDir);
        _storagePath = Path.Combine(baseDir, "settings.json");
    }

    public AppSettingsData Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<AppSettingsData>(json, _jsonOptions);
                if (data != null)
                    return data;
            }
        }
        catch
        {
            // ignore load errors, return defaults
        }

        return new AppSettingsData();
    }

    public void Save(AppSettingsData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(_storagePath, json);
        }
        catch
        {
            // ignore save errors
        }
    }
}

public class AppSettingsData
{
    public List<AttachmentPresetData> AttachPresets { get; set; } = new();
    public double MarkerScale { get; set; } = 1.0;
    public string WebSocketHost { get; set; } = "127.0.0.1";
    public int WebSocketPort { get; set; } = 31338;
    public int UdpPort { get; set; } = 31339;
    public int RtpPort { get; set; } = 5000;
    public int GsiPort { get; set; } = 31337;
}

public class AttachmentPresetData
{
    public string AttachmentName { get; set; } = string.Empty;
    public double OffsetPosX { get; set; }
    public double OffsetPosY { get; set; }
    public double OffsetPosZ { get; set; }
    public double OffsetPitch { get; set; }
    public double OffsetYaw { get; set; }
    public double OffsetRoll { get; set; }
    public double Fov { get; set; } = 90.0;
}
