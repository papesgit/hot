using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlaeObsTools.Services.Campaths;

public class CampathStorage
{
    private readonly string _storagePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CampathStorage()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = Path.Combine(appData, "HlaeObsTools");
        Directory.CreateDirectory(baseDir);
        _storagePath = Path.Combine(baseDir, "campaths.json");
    }

    public CampathStorageData Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<CampathStorageData>(json, _jsonOptions);
                if (data != null)
                    return data;
            }
        }
        catch
        {
            // ignore load errors, return empty
        }

        return new CampathStorageData();
    }

    public void Save(CampathStorageData data)
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

public class CampathStorageData
{
    public List<CampathProfileData> Profiles { get; set; } = new();
    public Guid? SelectedProfileId { get; set; }
    public double? Scale { get; set; }
    public double? CampathScale { get; set; }
    public double? GroupScale { get; set; }
}

public class CampathProfileData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Profile";
    public List<CampathData> Campaths { get; set; } = new();
    public List<CampathGroupData> Groups { get; set; } = new();
    public double? Scale { get; set; }
}

public class CampathData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Campath";
    public string? FilePath { get; set; }
    public string? ImagePath { get; set; }
    public double Offset { get; set; }
}

public class CampathGroupData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Group";
    public CampathGroupMode Mode { get; set; } = CampathGroupMode.Seq;
    public List<Guid> CampathIds { get; set; } = new();
}

public enum CampathGroupMode
{
    Seq,
    Rnd
}
