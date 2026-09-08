namespace AcDream.App.UI;

public static class RetailPanelCatalog
{
    public const uint CharacterInformation = 3u;
    public const uint PositiveEffects = 4u;
    public const uint NegativeEffects = 5u;
    public const uint Inventory = 7u;
    public const uint LinkStatus = 8u;
    public const uint MiniGame = 9u;
    public const uint Character = 11u;
    public const uint Magic = 13u;
    public const uint Vitae = 15u;

    public const uint Options = 10u;

    public const uint SocialPanel = 12u;

    public const uint MapHouse = 16u;

    public const uint Journal = 25u;

    private static readonly (uint PanelId, string WindowName)[] Mounted =
    {
        (CharacterInformation, WindowNames.CharacterInformation),
        (PositiveEffects, WindowNames.PositiveEffects),
        (NegativeEffects, WindowNames.NegativeEffects),
        (Inventory, WindowNames.Inventory),
        (LinkStatus, WindowNames.LinkStatus),
        (MiniGame, WindowNames.MiniGame),
        (Character, WindowNames.Character),
        (Magic, WindowNames.Spellbook),
        (Vitae, WindowNames.Vitae),
        (Options, WindowNames.Options),
        (SocialPanel, WindowNames.SocialPanel),
        (MapHouse, WindowNames.MapHouse),
        (Journal, WindowNames.Journal),
    };

    private static readonly (uint PanelId, string WindowName)[] Toolbar =
    {
        (Inventory, WindowNames.Inventory),
        (Character, WindowNames.Character),
        (Magic, WindowNames.Spellbook),
        (Options, WindowNames.Options),
        (MapHouse, WindowNames.MapHouse),
        (Journal, WindowNames.Journal),
    };

    public static IReadOnlyList<(uint PanelId, string WindowName)> MountedPanels => Mounted;
    public static IReadOnlyList<(uint PanelId, string WindowName)> ToolbarPanels => Toolbar;

    public static bool TryGetWindowName(uint panelId, out string windowName)
    {
        foreach (var entry in Mounted)
        {
            if (entry.PanelId != panelId) continue;
            windowName = entry.WindowName;
            return true;
        }

        windowName = string.Empty;
        return false;
    }

    public static bool TryGetPanelId(string windowName, out uint panelId)
    {
        foreach (var entry in Mounted)
        {
            if (!string.Equals(entry.WindowName, windowName, StringComparison.Ordinal)) continue;
            panelId = entry.PanelId;
            return true;
        }

        panelId = 0;
        return false;
    }
}
