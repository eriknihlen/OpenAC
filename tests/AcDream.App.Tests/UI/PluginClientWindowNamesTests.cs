using AcDream.App.UI;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.UI;

public sealed class PluginClientWindowNamesTests
{
    [Theory]
    [InlineData(PluginClientWindow.Inventory, "inventory")]
    [InlineData(PluginClientWindow.Character, "character")]
    [InlineData(PluginClientWindow.CharacterInformation, "character-information")]
    [InlineData(PluginClientWindow.Spellbook, "spellbook")]
    [InlineData(PluginClientWindow.Map, "map-house")]
    [InlineData(PluginClientWindow.Options, "options")]
    [InlineData(PluginClientWindow.Social, "social-panel")]
    [InlineData(PluginClientWindow.Journal, "journal")]
    [InlineData(PluginClientWindow.PositiveEffects, "effects-positive")]
    [InlineData(PluginClientWindow.NegativeEffects, "effects-negative")]
    [InlineData(PluginClientWindow.LinkStatus, "link-status")]
    [InlineData(PluginClientWindow.Vitae, "vitae")]
    [InlineData(PluginClientWindow.Radar, "radar")]
    public void EveryEnumMemberMapsToItsRetainedWindowName(
        PluginClientWindow window, string expectedName)
    {
        Assert.True(PluginClientWindowNames.TryGetName(window, out string name));
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void EveryDefinedEnumValueResolves()
    {
        // Guards against a member added to PluginClientWindow without a
        // matching row in the switch: an un-mapped member would silently
        // report "unavailable" to every plugin forever.
        foreach (PluginClientWindow window in Enum.GetValues<PluginClientWindow>())
        {
            Assert.True(
                PluginClientWindowNames.TryGetName(window, out string name),
                $"{window} has no WindowNames mapping.");
            Assert.False(string.IsNullOrEmpty(name));
        }
    }

    [Fact]
    public void UndefinedEnumValueIsUnavailable()
    {
        Assert.False(PluginClientWindowNames.TryGetName((PluginClientWindow)(-1), out string name));
        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public void EveryPluginClientWindowMapsToAWindowWithARetailUserToggle()
    {
        // Pins every PluginClientWindow member to a window that RetailUiRuntime's
        // own input-action switch actually lets a player open: either a
        // RetailPanelCatalog.MountedPanels entry (the ToggleWindow/ShowWindow/
        // HideWindow/IsWindowVisible "panel controller" fork), or WindowNames.Radar,
        // the one window InputAction.ToggleCompass toggles straight through
        // Host.ToggleWindow because retail's compass was never a MountedPanels
        // member. A PluginClientWindow with no matching row here would let a
        // plugin claim control of a window no keybind actually reaches.
        var windowsWithARetailUserToggle = new HashSet<string>(
            RetailPanelCatalog.MountedPanels.Select(entry => entry.WindowName),
            StringComparer.Ordinal)
        {
            WindowNames.Radar,
        };

        foreach (PluginClientWindow window in Enum.GetValues<PluginClientWindow>())
        {
            Assert.True(PluginClientWindowNames.TryGetName(window, out string name));
            Assert.Contains(name, windowsWithARetailUserToggle);
        }
    }

    [Fact]
    public void ToggleClientWindow_InventoryRoutesThroughThePanelCatalog_RadarRoutesThroughHost()
    {
        // RetailUiRuntime.ToggleWindow/ShowWindow/HideWindow/IsWindowVisible all
        // fork on RetailPanelCatalog.TryGetPanelId: a hit goes to the panel
        // controller (_panelUi), a miss falls through to Host.*. This is the
        // exact discriminant PluginClientWindow.Inventory (a MountedPanels
        // entry) and PluginClientWindow.Radar (not one) exercise differently.
        Assert.True(PluginClientWindowNames.TryGetName(
            PluginClientWindow.Inventory, out string inventoryName));
        Assert.True(RetailPanelCatalog.TryGetPanelId(inventoryName, out _));

        Assert.True(PluginClientWindowNames.TryGetName(
            PluginClientWindow.Radar, out string radarName));
        Assert.False(RetailPanelCatalog.TryGetPanelId(radarName, out _));
    }
}
