using System.Collections.Generic;

namespace HlaeObsTools.Services.Graphics;

public enum GraphicsAtlasFormat
{
    Bgra8,
    Rgba8
}

public enum GraphicsAlphaMode
{
    Premultiplied,
    Straight
}

public enum GraphicsInstanceSourceType
{
    Atlas,
    Image
}

public sealed class GraphicsProfile
{
    public List<GraphicsAtlas> Atlases { get; set; } = new();
    public List<GraphicsInstance> Instances { get; set; } = new();
}

public sealed class GraphicsAtlas
{
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 512;
    public GraphicsAtlasFormat Format { get; set; } = GraphicsAtlasFormat.Bgra8;
    public GraphicsAlphaMode AlphaMode { get; set; } = GraphicsAlphaMode.Premultiplied;
    public bool KeyedMutex { get; set; } = true;
    public string HtmlPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<GraphicsRegion> Regions { get; set; } = new();
}

public sealed class GraphicsRegion
{
    public string Id { get; set; } = string.Empty;
    public double U0 { get; set; }
    public double V0 { get; set; }
    public double U1 { get; set; } = 1.0;
    public double V1 { get; set; } = 1.0;
    public double DefaultWidth { get; set; } = 1.0;
    public double DefaultHeight { get; set; } = 1.0;
}

public sealed class GraphicsInstance
{
    public string Name { get; set; } = string.Empty;
    public GraphicsInstanceSourceType SourceType { get; set; } = GraphicsInstanceSourceType.Atlas;
    public string Atlas { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ImageFile { get; set; } = string.Empty;
    public int AttachSlot { get; set; } = -1;
    public string AttachAttachmentName { get; set; } = string.Empty;
    public string AttachBoneName { get; set; } = string.Empty;
    public bool AttachUseYaw { get; set; }
    public bool AttachUsePitch { get; set; }
    public bool AttachUseRoll { get; set; }
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double PosZ { get; set; }
    public double Pitch { get; set; }
    public double Yaw { get; set; }
    public double Roll { get; set; }
    public double ScaleX { get; set; }
    public double ScaleY { get; set; }
    public bool Visible { get; set; } = true;
    public bool DepthTest { get; set; } = true;
    public bool DepthWrite { get; set; } = true;
}
