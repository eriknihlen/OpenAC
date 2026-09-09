using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Spells;

namespace AcDream.App.Tests.UI.Layout;

public sealed class IndicatorBarControllerTests
{
    private const uint Player = 0x50000001u;
    private static (uint, int, int) NoTex(uint _) => (0u, 0, 0);

    [Fact]
    public void AuthoredFixture_BuildsAllSevenRetailIndicatorButtons()
    {
        ElementInfo info = FixtureLoader.LoadIndicatorsInfos();
        Assert.Equal(0x10000610u, info.Id);
        Assert.Equal(150f, info.Width);
        Assert.Equal(30f, info.Height);

        ImportedLayout layout = LayoutImporter.Build(info, NoTex, datFont: null);
        uint[] ids =
        [
            IndicatorBarController.LinkButtonId,
            IndicatorBarController.HelpfulButtonId,
            IndicatorBarController.HarmfulButtonId,
            IndicatorBarController.VitaeButtonId,
            IndicatorBarController.BurdenButtonId,
            IndicatorBarController.MiniGameButtonId,
            IndicatorBarController.EndCharacterSessionButtonId,
        ];
        foreach (uint id in ids)
            Assert.IsType<UiButton>(layout.FindElement(id));
    }

    [Fact]
    public void Enchantments_UpdateHelpfulHarmfulAndVitae_AndDispatchPanels()
    {
        var h = CreateHarness();
        using IndicatorBarController controller = h.Controller;
        UiButton helpful = h.Button(IndicatorBarController.HelpfulButtonId);
        UiButton harmful = h.Button(IndicatorBarController.HarmfulButtonId);
        UiButton vitae = h.Button(IndicatorBarController.VitaeButtonId);

        Assert.False(helpful.Enabled);
        Assert.False(harmful.Enabled);
        Assert.False(vitae.Enabled);

        h.Spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 1u, 60f, 1u, Bucket: 1u, SpellCategory: 99u));
        h.Spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            43u, 2u, 60f, 2u, Bucket: 2u, SpellCategory: 99u));
        Assert.True(helpful.Enabled);
        Assert.True(harmful.Enabled);

        helpful.OnEvent(new UiEvent(0, helpful, UiEventType.Click));
        harmful.OnEvent(new UiEvent(0, harmful, UiEventType.Click));
        Assert.Equal(
            [RetailPanelCatalog.PositiveEffects, RetailPanelCatalog.NegativeEffects],
            h.ToggledPanels);

        h.Spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            50u, 3u, 60f, 1u, StatModValue: 1f, Bucket: 4u));
        Assert.False(vitae.Enabled);
        h.Spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            50u, 3u, 60f, 1u, StatModValue: 0.99f, Bucket: 4u));
        Assert.True(vitae.Enabled);
        Assert.Equal(UiButtonStateMachine.Normal, vitae.ActiveRetailStateId);
        vitae.OnEvent(new UiEvent(0, vitae, UiEventType.Click));
        Assert.Equal(RetailPanelCatalog.Vitae, h.ToggledPanels[^1]);

        h.Spellbook.OnEnchantmentRemoved(3u, 50u);
        Assert.False(vitae.Enabled);
    }

    [Theory]
    [InlineData(1499, IndicatorBarController.UnencumberedState)]
    [InlineData(1500, IndicatorBarController.EncumberedState)]
    [InlineData(2999, IndicatorBarController.EncumberedState)]
    [InlineData(3000, IndicatorBarController.HeavilyEncumberedState)]
    public void Burden_UsesExactRetailLoadBoundaries(int burden, uint expectedState)
    {
        var h = CreateHarness(strength: 10);
        using IndicatorBarController controller = h.Controller;
        h.Objects.UpdateIntProperty(Player, 5u, burden);

        UiButton button = h.Button(IndicatorBarController.BurdenButtonId);
        Assert.Equal(expectedState, button.ActiveRetailStateId);
    }

    [Fact]
    public void EnchantmentChange_ReevaluatesBurdenFromEffectiveStrength()
    {
        var h = CreateHarness(strength: 10);
        using IndicatorBarController controller = h.Controller;
        h.Objects.UpdateIntProperty(Player, 5u, 1600);
        UiButton button = h.Button(IndicatorBarController.BurdenButtonId);
        Assert.Equal(IndicatorBarController.EncumberedState, button.ActiveRetailStateId);

        h.Strength = 20;
        h.Spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 1u, 60d, Player, Bucket: 1u));
        Assert.Equal(IndicatorBarController.UnencumberedState, button.ActiveRetailStateId);

        h.Strength = 10;
        h.Spellbook.OnPurgeAll();
        Assert.Equal(IndicatorBarController.EncumberedState, button.ActiveRetailStateId);
    }

    [Fact]
    public void Burden_ClickOpensCharacterInformationPanel()
    {
        var h = CreateHarness();
        using IndicatorBarController controller = h.Controller;
        UiButton burden = h.Button(IndicatorBarController.BurdenButtonId);

        burden.OnEvent(new UiEvent(0, burden, UiEventType.Click));

        Assert.Equal([RetailPanelCatalog.CharacterInformation], h.ToggledPanels);
    }

    [Theory]
    [InlineData(true, 4.999, IndicatorBarController.ConnectionGoodState)]
    [InlineData(true, 5.0, IndicatorBarController.ConnectionUncertainState)]
    [InlineData(true, 19.999, IndicatorBarController.ConnectionUncertainState)]
    [InlineData(true, 20.0, IndicatorBarController.ConnectionBadState)]
    [InlineData(true, 39.999, IndicatorBarController.ConnectionBadState)]
    [InlineData(true, 40.0, IndicatorBarController.ConnectionDisconnectedState)]
    [InlineData(false, 0.0, IndicatorBarController.ConnectionDisconnectedState)]
    public void LinkStatus_UsesRetailThresholds(
        bool connected,
        double age,
        uint expectedState)
    {
        var h = CreateHarness();
        using IndicatorBarController controller = h.Controller;
        h.LinkStatus = new LinkStatusSnapshot(connected, age);
        h.Time = 4.0;

        controller.Tick();

        Assert.Equal(
            expectedState,
            h.Button(IndicatorBarController.LinkButtonId).ActiveRetailStateId);
    }

    [Fact]
    public void LinkStatus_UpdatesAtFourSeconds_AndBadFlashesEveryPointSevenFive()
    {
        var h = CreateHarness();
        using IndicatorBarController controller = h.Controller;
        UiButton link = h.Button(IndicatorBarController.LinkButtonId);
        h.LinkStatus = new LinkStatusSnapshot(true, 20d);

        h.Time = 3.999;
        controller.Tick();
        Assert.Equal(IndicatorBarController.ConnectionGoodState, link.ActiveRetailStateId);

        h.Time = 4.0;
        controller.Tick();
        Assert.Equal(IndicatorBarController.ConnectionBadState, link.ActiveRetailStateId);

        h.Time = 4.749;
        controller.Tick();
        Assert.Equal(IndicatorBarController.ConnectionBadState, link.ActiveRetailStateId);

        h.Time = 4.75;
        controller.Tick();
        Assert.Equal(IndicatorBarController.ConnectionUncertainState, link.ActiveRetailStateId);

        h.Time = 5.5;
        controller.Tick();
        Assert.Equal(IndicatorBarController.ConnectionBadState, link.ActiveRetailStateId);
    }

    [Fact]
    public void MiniGameNoticesAndEndSessionActionUseAuthoredButtons()
    {
        var h = CreateHarness();
        using IndicatorBarController controller = h.Controller;
        UiButton miniGame = h.Button(IndicatorBarController.MiniGameButtonId);
        UiButton endSession = h.Button(IndicatorBarController.EndCharacterSessionButtonId);

        Assert.False(miniGame.Enabled);
        Assert.True(endSession.Enabled);
        Assert.Equal(UiButtonStateMachine.Normal, endSession.ActiveRetailStateId);
        controller.SetMiniGameActive(true);
        Assert.True(miniGame.Enabled);
        miniGame.OnEvent(new UiEvent(0, miniGame, UiEventType.Click));
        Assert.Equal(RetailPanelCatalog.MiniGame, h.ToggledPanels[^1]);
        controller.SetMiniGameActive(false);
        Assert.False(miniGame.Enabled);

        endSession.OnEvent(new UiEvent(0, endSession, UiEventType.Click));
        Assert.Equal(1, h.EndSessionRequests);
    }

    [Fact]
    public void LinkButton_DispatchesSharedLinkStatusPanel()
    {
        var h = CreateHarness();
        using IndicatorBarController controller = h.Controller;
        UiButton link = h.Button(IndicatorBarController.LinkButtonId);

        link.OnEvent(new UiEvent(0, link, UiEventType.Click));

        Assert.Equal([RetailPanelCatalog.LinkStatus], h.ToggledPanels);
    }

    private static Harness CreateHarness(int strength = 10)
    {
        var table = SpellTable.LoadFromReader(new StringReader(
            "Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n43,Bane,0x0\n50,Vitae,0x0\n"));
        var spellbook = new Spellbook(table);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player });
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadIndicatorsInfos(), NoTex, datFont: null);
        var harness = new Harness(layout, spellbook, objects, strength);
        harness.Controller = IndicatorBarController.Bind(
            layout,
            new IndicatorBarBindings(
                spellbook,
                objects,
                () => Player,
                () => harness.Strength,
                () => harness.LinkStatus,
                () => harness.Time,
                harness.ToggledPanels.Add,
                () => harness.EndSessionRequests++))!;
        return harness;
    }

    private sealed class Harness(
        ImportedLayout layout,
        Spellbook spellbook,
        ClientObjectTable objects,
        int strength)
    {
        public ImportedLayout Layout { get; } = layout;
        public Spellbook Spellbook { get; } = spellbook;
        public ClientObjectTable Objects { get; } = objects;
        public int Strength { get; set; } = strength;
        public List<uint> ToggledPanels { get; } = [];
        public double Time { get; set; }
        public LinkStatusSnapshot LinkStatus { get; set; } = new(true, 0d);
        public int EndSessionRequests { get; set; }
        public IndicatorBarController Controller { get; set; } = null!;

        public UiButton Button(uint id) => Assert.IsType<UiButton>(Layout.FindElement(id));
    }
}
