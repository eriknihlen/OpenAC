using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Spells;

namespace AcDream.App.Tests.UI.Layout;

public sealed class EffectsUiControllerTests
{
    private static (uint, int, int) NoTex(uint _) => (0u, 0, 0);
    private static EffectRowTemplateFactory Templates()
        => new(FixtureLoader.LoadEffectRowTemplateInfos(), NoTex, defaultFont: null);

    [Fact]
    public void AuthoredEffectRowTemplate_HasRetailIconNameDurationAndSelectionStates()
    {
        ElementInfo row = FixtureLoader.LoadEffectRowTemplateInfos();

        Assert.Equal(EffectsUiController.RowTemplateId, row.Id);
        Assert.Equal((300f, 32f), (row.Width, row.Height));
        Assert.Contains(UiButtonStateMachine.Normal, row.States.Keys);
        Assert.Contains(UiButtonStateMachine.Highlight, row.States.Keys);
        Assert.Equal((0f, 32f),
            (FindInfo(row, EffectsUiController.RowIconId)!.X,
             FindInfo(row, EffectsUiController.RowIconId)!.Width));
        Assert.Equal((37f, 188f),
            (FindInfo(row, EffectsUiController.RowLabelId)!.X,
             FindInfo(row, EffectsUiController.RowLabelId)!.Width));
        Assert.Equal((225f, 50f),
            (FindInfo(row, EffectsUiController.RowDurationId)!.X,
             FindInfo(row, EffectsUiController.RowDurationId)!.Width));
    }

    [Fact]
    public void Rebuild_DoesNotAutoSelectFirstEffect_AndClearsRemovedSelection()
    {
        var table = SpellTable.LoadFromReader(new StringReader(
            "Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n"));
        var spellbook = new Spellbook(table);
        var root = new UiPanel { Width = 160f, Height = 100f };
        var list = new UiItemList(NoTex) { Width = 160f, Height = 64f };
        var info = new UiText { Width = 160f, Height = 32f };
        root.AddChild(list);
        root.AddChild(info);
        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [EffectsUiController.ListId] = list,
            [EffectsUiController.InfoTextId] = info,
        });
        using EffectsUiController controller = EffectsUiController.Bind(
            layout, spellbook, positive: true, () => 0d, NoTex, id => id,
            Templates(), "SELECT A SPELL")!;

        Assert.False(info.PreserveEndOnLayout);
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 1u, 60f, 1u, Bucket: 1u, SpellCategory: 42u));
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 2u, 60f, 1u, Bucket: 4u, SpellCategory: 42u));
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 3u, 60f, 1u, Bucket: 8u, SpellCategory: 42u));
        Assert.Null(controller.SelectedSpellId);
        Assert.Equal("SELECT A SPELL", Assert.Single(info.LinesProvider()).Text);
        Assert.Equal(1, list.GetNumUIItems());
        UiTemplateListSlot row = Assert.IsType<UiTemplateListSlot>(list.GetItem(0));
        UiText duration = Assert.IsType<UiText>(
            row.Content.FindElement(EffectsUiController.RowDurationId));
        Assert.Equal("1:00", Assert.Single(duration.LinesProvider()).Text);
        row.OnEvent(new UiEvent(0, row, UiEventType.Click));
        Assert.Equal(42u, controller.SelectedSpellId);
        Assert.Equal(["Boon", "", ""], info.LinesProvider().Select(line => line.Text));

        spellbook.OnEnchantmentRemoved(1u, 42u);
        Assert.Null(controller.SelectedSpellId);
    }

    [Fact]
    public void DuplicateLayersOfSameSpell_ShareRetailSelection()
    {
        var table = SpellTable.LoadFromReader(new StringReader(
            "Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n"));
        var spellbook = new Spellbook(table);
        var root = new UiPanel { Width = 160f, Height = 100f };
        var list = new UiItemList(NoTex) { Width = 160f, Height = 64f };
        root.AddChild(list);
        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [EffectsUiController.ListId] = list,
        });
        using EffectsUiController controller = EffectsUiController.Bind(
            layout, spellbook, positive: true, () => 0d, NoTex, id => id,
            Templates(), "SELECT A SPELL")!;
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 1u, 60f, 1u, Bucket: 1u, SpellCategory: 100u));
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 2u, 60f, 2u, Bucket: 2u, SpellCategory: 101u));

        UiTemplateListSlot first = Assert.IsType<UiTemplateListSlot>(list.GetItem(0));
        UiTemplateListSlot second = Assert.IsType<UiTemplateListSlot>(list.GetItem(1));
        first.OnEvent(new UiEvent(0, first, UiEventType.Click));

        Assert.Equal(42u, controller.SelectedSpellId);
        Assert.True(first.Selected);
        Assert.True(second.Selected);

        second.OnEvent(new UiEvent(0, second, UiEventType.Click));
        Assert.Null(controller.SelectedSpellId);
        Assert.False(first.Selected);
        Assert.False(second.Selected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedEffect_WrapsDescription_AndBindsAuthoredInfoScrollbar(
        bool positive)
    {
        string flags = positive ? "0x4" : "0x0";
        var table = SpellTable.LoadFromReader(new StringReader(
            "Spell ID,Name,Description,Flags [Hex]\n"
            + $"42,Boon,This description contains enough words to wrap across several lines.,{flags}\n"));
        var spellbook = new Spellbook(table);
        ImportedLayout layout = LayoutImporter.Build(
            positive
                ? FixtureLoader.LoadPositiveEffectsInfos()
                : FixtureLoader.LoadNegativeEffectsInfos(),
            NoTex,
            datFont: null);
        UiText info = Assert.IsType<UiText>(
            layout.FindElement(EffectsUiController.InfoTextId));
        info.Width = 96f;

        using EffectsUiController controller = EffectsUiController.Bind(
            layout, spellbook, positive, () => 0d, NoTex, id => id,
            Templates(), "SELECT A SPELL")!;
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 1u, 60f, 1u, Bucket: 1u, SpellCategory: 42u));
        UiElement listHost = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(EffectsUiController.ListId));
        UiItemList list = Assert.IsType<UiItemList>(Assert.Single(listHost.Children));
        UiTemplateListSlot row = Assert.IsType<UiTemplateListSlot>(list.GetItem(0));
        row.OnEvent(new UiEvent(0, row, UiEventType.Click));

        IReadOnlyList<UiText.Line> lines = info.LinesProvider();
        Assert.Equal("Boon", lines[0].Text);
        Assert.Equal(string.Empty, lines[1].Text);
        Assert.True(lines.Count > 3);
        Assert.All(lines, line => Assert.True(line.Text.Length <= 12));
        Assert.True(info.WheelScrollEnabled);
        Assert.False(info.ClickThrough);
        UiScrollbar listScrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(EffectsUiController.ListScrollbarId));
        UiScrollbar infoScrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(EffectsUiController.InfoScrollbarId));
        Assert.Same(list.Scroll, listScrollbar.Model);
        Assert.Same(info.Scroll, infoScrollbar.Model);

        info.Scroll.SetExtents(contentHeight: 100, viewHeight: 20);
        Assert.True(info.OnEvent(new UiEvent(
            0, info, UiEventType.Scroll, Data0: -1)));
        Assert.Equal(info.Scroll.LineHeight, info.Scroll.ScrollY);
    }

    [Fact]
    public void PermanentEffect_LeavesRetailDurationColumnEmpty()
    {
        var table = SpellTable.LoadFromReader(new StringReader(
            "Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n"));
        var spellbook = new Spellbook(table);
        var root = new UiPanel { Width = 160f, Height = 100f };
        var list = new UiItemList(NoTex) { Width = 160f, Height = 64f };
        root.AddChild(list);
        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [EffectsUiController.ListId] = list,
        });
        using EffectsUiController controller = EffectsUiController.Bind(
            layout, spellbook, positive: true, () => 0d, NoTex, id => id,
            Templates(), "SELECT A SPELL")!;

        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 1u, -1f, 1u, Bucket: 1u, SpellCategory: 42u));

        UiTemplateListSlot row = Assert.IsType<UiTemplateListSlot>(list.GetItem(0));
        UiText duration = Assert.IsType<UiText>(
            row.Content.FindElement(EffectsUiController.RowDurationId));
        Assert.Equal(string.Empty, Assert.Single(duration.LinesProvider()).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AuthoredEffectsFixture_InheritsListAndInfo_AndBinds(bool positive)
    {
        ElementInfo info = positive
            ? FixtureLoader.LoadPositiveEffectsInfos()
            : FixtureLoader.LoadNegativeEffectsInfos();
        uint expectedRoot = positive
            ? EffectsUiController.PositiveRootId
            : EffectsUiController.NegativeRootId;
        Assert.Equal(expectedRoot, info.Id);
        Assert.Equal(0x1000001Bu, info.Type);
        Assert.Equal(300f, info.Width);
        Assert.Equal(362f, info.Height);
        Assert.True(info.TryGetEffectiveProperty(0x1000000Cu, out UiPropertyValue effectType));
        Assert.Equal(UiPropertyKind.Enum, effectType.Kind);
        Assert.Equal(positive ? 1u : 2u, effectType.UnsignedValue);
        Assert.Equal(
            positive,
            info.TryGetEffectiveBool(0x10000049u, out bool restorePrevious)
                && restorePrevious);

        ElementInfo[] children = info.Children.OrderBy(child => child.ReadOrder).ToArray();
        Assert.Equal(
            [0x100000FCu, 0x10000120u, 0x10000123u, 0x10000124u, 0x10000125u],
            children.Select(child => child.Id));
        Assert.Equal([1u, 2u, 3u, 4u, 5u], children.Select(child => child.ReadOrder));
        Assert.Equal(children.Length, children.Select(child => child.Id).Distinct().Count());
        ElementInfo listInfo = Assert.Single(children, child => child.Id == EffectsUiController.ListId);
        Assert.Equal(5u, listInfo.Type);
        Assert.Equal(300f, listInfo.Width);
        Assert.Equal(249f, listInfo.Height);
        Assert.Equal(12u, FindInfo(info, EffectsUiController.InfoTextId)?.Type);

        ImportedLayout layout = LayoutImporter.Build(info, NoTex, datFont: null);
        var spellbook = new Spellbook();
        int closes = 0;

        using EffectsUiController? controller = EffectsUiController.Bind(
            layout, spellbook, positive, () => 0d, NoTex, id => id,
            Templates(), "SELECT A SPELL", () => closes++);

        Assert.NotNull(controller);
        Assert.NotNull(layout.FindElement(EffectsUiController.ListId));
        Assert.IsType<UiText>(layout.FindElement(EffectsUiController.InfoTextId));
        Assert.IsType<UiScrollbar>(layout.FindElement(EffectsUiController.ListScrollbarId));
        Assert.IsType<UiScrollbar>(layout.FindElement(EffectsUiController.InfoScrollbarId));
        Assert.Equal(expectedRoot, layout.Root.DatElementId);
        UiButton close = Assert.IsType<UiButton>(layout.FindElement(EffectsUiController.CloseId));
        close.OnEvent(new UiEvent(0, close, UiEventType.Click));
        Assert.Equal(1, closes);

        uint panelId = positive
            ? RetailPanelCatalog.PositiveEffects
            : RetailPanelCatalog.NegativeEffects;
        Assert.True(RetailPanelCatalog.TryGetWindowName(panelId, out string windowName));
        Assert.Equal(
            positive ? WindowNames.PositiveEffects : WindowNames.NegativeEffects,
            windowName);
        Assert.DoesNotContain(
            RetailPanelCatalog.ToolbarPanels,
            entry => entry.PanelId == panelId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AuthoredRestorePreviousProperty_DrivesRetailPanelLifecycle(bool positive)
    {
        ElementInfo info = positive
            ? FixtureLoader.LoadPositiveEffectsInfos()
            : FixtureLoader.LoadNegativeEffectsInfos();
        var visible = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [WindowNames.Inventory] = false,
            [positive ? WindowNames.PositiveEffects : WindowNames.NegativeEffects] = false,
        };
        var panelUi = new RetailPanelUiController(
            name => visible[name],
            name =>
            {
                visible[name] = true;
                return true;
            },
            name =>
            {
                visible[name] = false;
                return true;
            });
        uint effectsPanel = positive
            ? RetailPanelCatalog.PositiveEffects
            : RetailPanelCatalog.NegativeEffects;
        string effectsWindow = positive
            ? WindowNames.PositiveEffects
            : WindowNames.NegativeEffects;
        panelUi.Register(RetailPanelCatalog.Inventory, WindowNames.Inventory);
        panelUi.Register(
            effectsPanel,
            effectsWindow,
            info);

        panelUi.SetPanelVisibility(RetailPanelCatalog.Inventory, visible: true);
        panelUi.SetPanelVisibility(effectsPanel, visible: true);
        panelUi.SetPanelVisibility(effectsPanel, visible: false);

        Assert.Equal(positive, visible[WindowNames.Inventory]);
        Assert.False(visible[effectsWindow]);
        Assert.Equal(
            positive ? RetailPanelCatalog.Inventory : null,
            panelUi.ActivePanelId);
    }

    private static ElementInfo? FindInfo(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo child in root.Children)
            if (FindInfo(child, id) is { } found)
                return found;
        return null;
    }

    private static void ApplyLayoutPass(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyLayoutPass(child);
        }
    }

    [Fact]
    public void EffectsList_TracksItsHostHeight_EvenWhenFirstLayoutFollowsAResize()
    {
        var table = SpellTable.LoadFromReader(new StringReader(
            "Spell ID,Name,Flags [Hex]\n41,Boon,0x4\n"));
        var spellbook = new Spellbook(table);
        ImportedLayout layout = FixtureLoader.LoadPositiveEffects();

        using EffectsUiController controller = EffectsUiController.Bind(
            layout, spellbook, positive: true, () => 0d, NoTex, id => id,
            Templates(), "SELECT A SPELL")!;

        UiElement host = layout.FindElement(EffectsUiController.ListId)!;
        UiItemList list = host.Children.OfType<UiItemList>().Single();

        // No layout pass has run yet — exactly the state a restored window is in
        // when its persisted size is applied before the first draw frame.
        layout.Root.Height = 660f;
        ApplyLayoutPass(layout.Root);
        Assert.Equal(host.Height, list.Height);

        // ...and it keeps tracking in both directions afterwards.
        layout.Root.Height = 200f;
        ApplyLayoutPass(layout.Root);
        Assert.Equal(host.Height, list.Height);

        layout.Root.Height = 660f;
        ApplyLayoutPass(layout.Root);
        Assert.Equal(host.Height, list.Height);
    }
}
