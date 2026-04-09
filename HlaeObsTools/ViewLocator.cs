using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Core;
using HlaeObsTools.ViewModels.Docks;
using HlaeObsTools.Views.Docks;

namespace HlaeObsTools;

public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new()
    {
        [typeof(VideoDisplayDockViewModel)] = () => new VideoDisplayDockView(),
        [typeof(PlaceholderDockViewModel)] = () => new PlaceholderDockView(),
        [typeof(RadarDockViewModel)] = () => new RadarDockView(),
        [typeof(SettingsDockViewModel)] = () => new SettingsDockView(),
        [typeof(CampathsDockViewModel)] = () => new CampathsDockView(),
        [typeof(NetConsoleDockViewModel)] = () => new NetConsoleDockView(),
        [typeof(GraphicsDockViewModel)] = () => new GraphicsDockView(),
        [typeof(Viewport3DDockViewModel)] = () => new Viewport3DDockView(),
        [typeof(AttachPresetAnimationDockViewModel)] = () => new AttachPresetAnimationDockView()
    };

    public Control? Build(object? data)
    {
        if (data is null)
        {
            return null;
        }

        var type = data.GetType();
        return Views.TryGetValue(type, out var builder)
            ? builder()
            : null;
    }

    public bool Match(object? data)
    {
        return data is IDockable || (data is not null && Views.ContainsKey(data.GetType()));
    }
}
