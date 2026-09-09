using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Spells;

namespace AcDream.App.Tests.UI.Layout;

public sealed class IndicatorDetailPanelControllerTests
{
    private const uint Player = 0x50000001u;

    [Fact]
    public void SharedPanelCenter_IsNotPaintedTwiceForAuthoredDetailBodies()
    {
        Assert.True(RetailUiRuntime.AuthorsFullPanelCenter(
            FixtureLoader.LoadVitaeInfos()));
        Assert.True(RetailUiRuntime.AuthorsFullPanelCenter(
            FixtureLoader.LoadCharacterInformationInfos()));
        Assert.False(RetailUiRuntime.AuthorsFullPanelCenter(
            FixtureLoader.LoadPositiveEffectsInfos()));
    }

    [Fact]
    public void AuthoredFixtures_AreRetailMainPanelPages()
    {
        AssertPage(
            FixtureLoader.LoadLinkStatusInfos(),
            LinkStatusUiController.RootId,
            0x1000001Du,
            LinkStatusUiController.MainTextId,
            LinkStatusUiController.CloseId);
        AssertPage(
            FixtureLoader.LoadVitaeInfos(),
            VitaeUiController.RootId,
            0x10000020u,
            VitaeUiController.MainTextId,
            VitaeUiController.CloseId);
        AssertPage(
            FixtureLoader.LoadMiniGameInfos(),
            MiniGameUiController.RootId,
            0x1000001Eu,
            0x1000016Eu,
            MiniGameUiController.CloseId);

        Assert.Equal(8u, RetailPanelCatalog.LinkStatus);
        Assert.Equal(9u, RetailPanelCatalog.MiniGame);
        Assert.Equal(15u, RetailPanelCatalog.Vitae);
    }

    [Fact]
    public void LinkStatus_RequestsPingOnShow_RefreshesText_AndRepeatsAfter120Seconds()
    {
        ImportedLayout layout = FixtureLoader.LoadLinkStatus();
        double now = 0d;
        int pings = 0;
        int closes = 0;
        LinkStatusSnapshot snapshot = new(
            Connected: true,
            SecondsSinceLastPacket: 0.25d,
            PacketLossPercentage: 1.25d,
            RoundTripSeconds: 0.123d);
        using LinkStatusUiController controller = LinkStatusUiController.Bind(
            layout,
            () => snapshot,
            () => now,
            () => pings++,
            LinkStatusStrings.English,
            () => closes++)!;

        controller.OnShown();

        Assert.Equal(1, pings);
        string text = VisibleText(layout, LinkStatusUiController.MainTextId);
        Assert.Contains("Link Indicator", text, StringComparison.Ordinal);
        Assert.Contains("1.25", text, StringComparison.Ordinal);
        Assert.Contains("123", text, StringComparison.Ordinal);

        controller.Tick();
        now = 119d;
        controller.Tick();
        Assert.Equal(1, pings);
        now = 124d;
        controller.Tick();
        Assert.Equal(2, pings);

        UiButton close = Assert.IsType<UiButton>(layout.FindElement(LinkStatusUiController.CloseId));
        close.OnEvent(new UiEvent(0, close, UiEventType.Click));
        Assert.Equal(1, closes);

        controller.OnHidden();
        now = 300d;
        controller.Tick();
        Assert.Equal(2, pings);
    }

    [Fact]
    public void Vitae_UsesDeathLevelPoolAndRetailThresholdFormula()
    {
        ImportedLayout layout = FixtureLoader.LoadVitae();
        var spellbook = new Spellbook();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player });
        objects.UpdateIntProperty(Player, VitaeUiController.DeathLevelProperty, 10);
        objects.UpdateIntProperty(Player, VitaeUiController.VitaeCpPoolProperty, 100);
        using VitaeUiController controller = VitaeUiController.Bind(
            layout,
            spellbook,
            objects,
            () => Player,
            VitaeStrings.English)!;

        Assert.Contains(
            "full strength",
            VisibleText(layout, VitaeUiController.MainTextId),
            StringComparison.Ordinal);

        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 666u,
            LayerId: 1u,
            Duration: -1f,
            CasterGuid: Player,
            StatModValue: 0.9f,
            Bucket: 4u));

        string text = VisibleText(layout, VitaeUiController.MainTextId);
        Assert.Contains("lost 10%", text, StringComparison.Ordinal);
        Assert.Contains(
            "health, stamina, mana, and skills are temporarily reduced by 10%",
            text,
            StringComparison.Ordinal);
        Assert.Contains("earn 379 more experience", text, StringComparison.Ordinal);
        Assert.Equal(479, VitaeUiController.VitaeCpPoolThreshold(0.9f, 10));
        Assert.Equal("61,000,000", VitaeUiController.FormatExperience(61_000_000));
    }

    [Fact]
    public void MiniGameFixture_BindsItsAuthoredCloseButton()
    {
        ImportedLayout layout = FixtureLoader.LoadMiniGame();
        int closes = 0;
        using MiniGameUiController controller = MiniGameUiController.Bind(
            layout, () => closes++)!;

        UiButton close = Assert.IsType<UiButton>(layout.FindElement(MiniGameUiController.CloseId));
        close.OnEvent(new UiEvent(0, close, UiEventType.Click));

        Assert.Equal(1, closes);
    }

    private static void AssertPage(
        ElementInfo info,
        uint rootId,
        uint type,
        uint contentId,
        uint closeId)
    {
        Assert.Equal(rootId, info.Id);
        Assert.Equal(type, info.Type);
        Assert.Equal((300f, 362f), (info.Width, info.Height));
        Assert.NotNull(Find(info, contentId));
        Assert.NotNull(Find(info, closeId));
        Assert.True(info.TryGetEffectiveBool(
            RetailPanelUiController.RestorePreviousPropertyId,
            out bool restorePrevious));
        Assert.True(restorePrevious);
    }

    private static string VisibleText(ImportedLayout layout, uint id)
    {
        UiText text = Assert.IsType<UiText>(layout.FindElement(id));
        return string.Join(' ', text.LinesProvider().Select(line => line.Text));
    }

    private static ElementInfo? Find(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo child in root.Children)
            if (Find(child, id) is { } found) return found;
        return null;
    }
}
