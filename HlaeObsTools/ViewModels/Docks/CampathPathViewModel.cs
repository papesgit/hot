using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Docks;

public class CampathPathViewModel : ViewModelBase
{
    private bool _isHighlighted;

    public CampathPathViewModel(Guid id, string name, string? filePath, AvaloniaList<Point> points, double iconX, double iconY, double rotation, Bitmap? thumbnail)
    {
        Id = id;
        Name = name;
        FilePath = filePath;
        Points = points;
        IconX = iconX;
        IconY = iconY;
        Rotation = rotation;
        Thumbnail = thumbnail;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string? FilePath { get; }
    public AvaloniaList<Point> Points { get; }
    public double IconX { get; }
    public double IconY { get; }
    public double Rotation { get; }
    public Bitmap? Thumbnail { get; }

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }
}
