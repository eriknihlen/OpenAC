using AcDream.Plugin.Abstractions;

namespace AcDream.App.UI;

/// <summary>
/// Maps the plugin-facing <see cref="PluginClientWindow"/> enum to the
/// retained window names a plugin cannot see (<see cref="WindowNames"/>).
/// Only the client's own windows that a player can open with a keybind or
/// toolbar button are listed; anything else stays unavailable to plugins.
/// </summary>
public static class PluginClientWindowNames
{
    public static bool TryGetName(PluginClientWindow window, out string name)
    {
        name = window switch
        {
            PluginClientWindow.Inventory => WindowNames.Inventory,
            PluginClientWindow.Character => WindowNames.Character,
            PluginClientWindow.CharacterInformation => WindowNames.CharacterInformation,
            PluginClientWindow.Spellbook => WindowNames.Spellbook,
            PluginClientWindow.Map => WindowNames.MapHouse,
            PluginClientWindow.Options => WindowNames.Options,
            PluginClientWindow.Social => WindowNames.SocialPanel,
            PluginClientWindow.Journal => WindowNames.Journal,
            PluginClientWindow.PositiveEffects => WindowNames.PositiveEffects,
            PluginClientWindow.NegativeEffects => WindowNames.NegativeEffects,
            PluginClientWindow.LinkStatus => WindowNames.LinkStatus,
            PluginClientWindow.Vitae => WindowNames.Vitae,
            PluginClientWindow.Radar => WindowNames.Radar,
            _ => string.Empty,
        };
        return name.Length != 0;
    }
}
