using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class RetailPanelCatalogTests
{
    [Fact]
    public void Options_PanelId_Is10_MatchingTheToolbarButtonsAuthoredProperty()
        => Assert.Equal(10u, RetailPanelCatalog.Options);

    [Fact]
    public void Options_ResolvesToTheOptionsWindowName()
    {
        Assert.True(RetailPanelCatalog.TryGetWindowName(RetailPanelCatalog.Options, out string name));
        Assert.Equal(WindowNames.Options, name);
    }

    [Fact]
    public void OptionsWindowName_ResolvesBackToThePanelId()
    {
        Assert.True(RetailPanelCatalog.TryGetPanelId(WindowNames.Options, out uint panelId));
        Assert.Equal(RetailPanelCatalog.Options, panelId);
    }

    [Fact]
    public void Options_IsListedInMountedPanels()
        => Assert.Contains(
            (RetailPanelCatalog.Options, WindowNames.Options),
            RetailPanelCatalog.MountedPanels);

    [Fact]
    public void Options_IsListedInToolbarPanels()
        => Assert.Contains(
            (RetailPanelCatalog.Options, WindowNames.Options),
            RetailPanelCatalog.ToolbarPanels);


    [Fact]
    public void SocialPanel_PanelId_Is12_MatchingTheLiveDatSlotProperty()
        => Assert.Equal(12u, RetailPanelCatalog.SocialPanel);

    [Fact]
    public void SocialPanel_ResolvesToTheSocialPanelWindowName()
    {
        Assert.True(RetailPanelCatalog.TryGetWindowName(RetailPanelCatalog.SocialPanel, out string name));
        Assert.Equal(WindowNames.SocialPanel, name);
    }

    [Fact]
    public void SocialPanel_IsListedInMountedPanels()
        => Assert.Contains(
            (RetailPanelCatalog.SocialPanel, WindowNames.SocialPanel),
            RetailPanelCatalog.MountedPanels);

    [Fact]
    public void SocialPanel_IsNotListedInToolbarPanels()
        => Assert.DoesNotContain(
            (RetailPanelCatalog.SocialPanel, WindowNames.SocialPanel),
            RetailPanelCatalog.ToolbarPanels);
}
