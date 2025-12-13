using System;

namespace HlaeObsTools.ViewModels.Hud;

public sealed class HudPlayerActionRequestedEventArgs : EventArgs
{
    public HudPlayerActionRequestedEventArgs(HudPlayerCardViewModel player, HudPlayerActionOption? option)
    {
        Player = player;
        Option = option;
    }

    public HudPlayerCardViewModel Player { get; }

    public HudPlayerActionOption? Option { get; }

    public string? ActionId => Option?.Id;

    public int ObserverSlot => Player.ObserverSlot;
}
