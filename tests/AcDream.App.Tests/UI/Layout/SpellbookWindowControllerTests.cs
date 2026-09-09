using AcDream.App.Spells;
using AcDream.Content;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using System.Numerics;

namespace AcDream.App.Tests.UI.Layout;

public sealed class SpellbookWindowControllerTests
{
    private static readonly SpellbookRowStyle RowStyle = new(
        Width: 280f,
        Height: 32f,
        IconLeft: 0f,
        IconTop: 0f,
        IconWidth: 32f,
        IconHeight: 32f,
        LabelLeft: 42f,
        LabelWidth: 230f,
        FontDid: 0x40000001u,
        LabelColor: Vector4.One,
        BackgroundSprite: 0x06001396u,
        SelectedSprite: 0x06001397u);

    [Fact]
    public void RetailFixture_BindsTextTabs_AndPropagatesOpenClosedState()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadSpellbookInfos(),
            _ => (0u, 0, 0),
            datFont: null,
            stringResolve: value => value.StringId switch
            {
                49867858u => "Spellbook",
                79360818u => "Components",
                _ => null,
            });
        var closed = new List<uint>();
        using SpellbookWindowController? controller = Bind(
            layout, new Spellbook(), close: () => closed.Add(1u));

        Assert.NotNull(controller);
        UiText spellTab = Assert.IsType<UiText>(layout.FindElement(SpellbookWindowController.SpellTabId));
        UiText componentTab = Assert.IsType<UiText>(layout.FindElement(SpellbookWindowController.ComponentTabId));
        UiElement spellPage = Assert.IsAssignableFrom<UiElement>(layout.FindElement(SpellbookWindowController.SpellPageId));
        UiElement componentPage = Assert.IsAssignableFrom<UiElement>(layout.FindElement(SpellbookWindowController.ComponentPageId));

        Assert.Equal("Spellbook", Assert.Single(spellTab.LinesProvider()).Text);
        Assert.Equal("Components", Assert.Single(componentTab.LinesProvider()).Text);
        Assert.False(spellTab.OneLine);
        Assert.True(spellTab.Centered);
        Assert.Equal(VJustify.Center, spellTab.VerticalJustify);
        Assert.True(spellTab.DrawTextAfterChildren);
        Assert.True(componentTab.DrawTextAfterChildren);

        Assert.Equal(RetailUiStateIds.Open, spellTab.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Closed, componentTab.ActiveRetailStateId);
        Assert.True(spellPage.Visible);
        Assert.False(componentPage.Visible);
        Assert.Equal(3, spellTab.Children.Count);
        Assert.All(spellTab.Children, child =>
            Assert.Equal(RetailUiStateIds.Open, Assert.IsAssignableFrom<IUiDatStateful>(child).ActiveRetailStateId));

        componentTab.OnEvent(new UiEvent(0, componentTab, UiEventType.Click));

        Assert.Equal(SpellbookWindowPage.Components, controller!.CurrentPage);
        Assert.Equal(RetailUiStateIds.Closed, spellTab.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Open, componentTab.ActiveRetailStateId);
        Assert.False(spellPage.Visible);
        Assert.True(componentPage.Visible);
        Assert.All(componentTab.Children, child =>
            Assert.Equal(RetailUiStateIds.Open, Assert.IsAssignableFrom<IUiDatStateful>(child).ActiveRetailStateId));
        Assert.Empty(closed);
    }

    [Fact]
    public void RetailFixture_BindsFilterIndicatorChildren_AndDeleteCaptionChild()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadSpellbookInfos(),
            _ => (0u, 0, 0),
            datFont: null,
            stringResolve: value => value.StringId == 164868556u ? "Delete" : "Filter");

        UiButton creature = Assert.IsType<UiButton>(layout.FindElement(0x10000298u));
        Assert.Equal((0f, 1f, 13f, 13f),
            (creature.FaceLeft, creature.FaceTop, creature.FaceWidth, creature.FaceHeight));
        Assert.Equal(UiButton.LabelAlignment.Left, creature.LabelAlign);
        Assert.Equal(17f, creature.LabelOffsetX);
        Assert.Equal("Filter", creature.Label);
        Assert.True(creature.ToggleBehavior);

        creature.Selected = true;
        Assert.Equal("Highlight", creature.ActiveState);
        Assert.Equal(UiButtonStateMachine.Highlight, creature.ActiveRetailStateId);

        UiButton levelSix = Assert.IsType<UiButton>(layout.FindElement(0x100002A1u));
        Assert.Equal(UiButton.LabelAlignment.Left, levelSix.LabelAlign);
        Assert.Equal(17f, levelSix.LabelOffsetX);

        UiButton delete = Assert.IsType<UiButton>(
            layout.FindElement(SpellbookWindowController.DeleteButtonId));
        Assert.Equal("Delete", delete.Label);
        Assert.Equal(UiButton.LabelAlignment.Center, delete.LabelAlign);
        Assert.Equal((187f, 61f, 100f, 32f),
            (delete.Left, delete.Top, delete.FaceWidth, delete.FaceHeight));
    }

    [Fact]
    public void RightClickingASpellRow_AssessesIt()
    {
        ImportedLayout layout = FixtureLoader.LoadSpellbook();
        Spellbook book = CreateSpellbook();
        book.OnSpellLearned(101u);
        var examined = new List<uint>();
        using SpellbookWindowController controller = Bind(
            layout, book, examineSpell: examined.Add)!;

        UiItemList list = Assert.IsType<UiItemList>(
            layout.FindElement(SpellbookWindowController.SpellListId));
        UiCatalogSlot row = Assert.IsType<UiCatalogSlot>(list.GetItem(0));

        Assert.True(row.OnEvent(new UiEvent(0, row, UiEventType.RightClick)));
        Assert.Equal([101u], examined);
    }

    [Fact]
    public void RightClickWithNoExamineSeam_BubblesInsteadOfBeingSwallowed()
    {
        ImportedLayout layout = FixtureLoader.LoadSpellbook();
        Spellbook book = CreateSpellbook();
        book.OnSpellLearned(101u);
        using SpellbookWindowController controller = Bind(layout, book)!;

        UiItemList list = Assert.IsType<UiItemList>(
            layout.FindElement(SpellbookWindowController.SpellListId));
        UiCatalogSlot row = Assert.IsType<UiCatalogSlot>(list.GetItem(0));

        Assert.False(row.OnEvent(new UiEvent(0, row, UiEventType.RightClick)));
    }

    [Fact]
    public void LearnedSpells_AreAuthoredRows_InDisplayOrder_WithSelectionAndDragPayload()
    {
        ImportedLayout layout = FixtureLoader.LoadSpellbook();
        Spellbook book = CreateSpellbook();
        book.OnSpellLearned(101u);
        book.OnSpellLearned(102u);
        book.OnSpellLearned(103u);
        var added = new List<uint>();
        using SpellbookWindowController controller = Bind(
            layout, book, addFavorite: added.Add)!;

        UiItemList list = Assert.IsType<UiItemList>(
            layout.FindElement(SpellbookWindowController.SpellListId));
        UiScrollbar scrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(SpellbookWindowController.SpellScrollbarId));

        Assert.Equal(1, list.Columns);
        Assert.Equal(280f, list.CellWidth);
        Assert.Equal(32f, list.CellHeight);
        Assert.Same(list.Scroll, scrollbar.Model);
        Assert.Equal(3, list.GetNumUIItems());

        UiCatalogSlot first = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        UiCatalogSlot second = Assert.IsType<UiCatalogSlot>(list.GetItem(1));
        UiCatalogSlot third = Assert.IsType<UiCatalogSlot>(list.GetItem(2));
        Assert.Equal([102u, 101u, 103u], [first.EntryId, second.EntryId, third.EntryId]);
        Assert.Equal("Creature One", first.Label);
        Assert.True(first.ShowLabel);
        Assert.Equal(RowStyle.BackgroundSprite, first.BackgroundSprite);
        Assert.Equal(RowStyle.SelectedSprite, first.SelectedSprite);
        Assert.True(first.SelectionBehindContent);
        Assert.Equal(RowStyle.LabelLeft, first.LabelLeft);

        second.OnEvent(new UiEvent(0, second, UiEventType.Click));
        Assert.True(second.Selected);
        Assert.False(first.Selected);

        first.OnEvent(new UiEvent(
            0, first, UiEventType.DragBegin,
            Payload: new SpellbookShortcutDragPayload(first.EntryId)));
        Assert.True(first.Selected);
        Assert.IsType<SpellbookShortcutDragPayload>(first.GetDragPayload());

        first.OnEvent(new UiEvent(0, first, UiEventType.DoubleClick));
        Assert.Equal([102u], added);
    }

    [Fact]
    public void SchoolAndLevelFilters_RebuildFromAuthoritativeBitfield()
    {
        ImportedLayout layout = FixtureLoader.LoadSpellbook();
        Spellbook book = CreateSpellbook();
        book.OnSpellLearned(101u); // War I
        book.OnSpellLearned(102u);
        book.OnSpellLearned(103u); // War VIII
        var sent = new List<uint>();
        using SpellbookWindowController controller = Bind(
            layout, book, sendFilter: sent.Add)!;
        UiItemList list = Assert.IsType<UiItemList>(
            layout.FindElement(SpellbookWindowController.SpellListId));
        UiButton war = Assert.IsType<UiButton>(layout.FindElement(0x1000029Bu));
        UiButton levelOne = Assert.IsType<UiButton>(layout.FindElement(0x1000029Cu));

        Assert.True(war.Selected);
        Assert.Equal(3, list.GetNumUIItems());

        war.OnEvent(new UiEvent(0, war, UiEventType.Click));
        controller.Tick();
        Assert.Equal([0x3FF7u], sent);
        Assert.Single(VisibleSpellIds(list));
        Assert.Equal(102u, VisibleSpellIds(list)[0]);

        war.OnEvent(new UiEvent(0, war, UiEventType.Click));
        levelOne.OnEvent(new UiEvent(0, levelOne, UiEventType.Click));
        controller.Tick();
        Assert.Equal([103u], VisibleSpellIds(list));
        Assert.False(levelOne.Selected);
    }

    [Fact]
    public void Delete_UsesSharedConfirmation_AndWaitsForServerRemovalNotice()
    {
        ImportedLayout layout = FixtureLoader.LoadSpellbook();
        Spellbook book = CreateSpellbook();
        book.OnSpellLearned(101u);
        var removed = new List<uint>();
        string? prompt = null;
        Action<bool>? complete = null;
        using SpellbookWindowController controller = Bind(
            layout,
            book,
            removeSpell: removed.Add,
            showConfirmation: (message, callback) =>
            {
                prompt = message;
                complete = callback;
            })!;
        UiItemList list = Assert.IsType<UiItemList>(
            layout.FindElement(SpellbookWindowController.SpellListId));
        UiButton delete = Assert.IsType<UiButton>(
            layout.FindElement(SpellbookWindowController.DeleteButtonId));

        delete.OnEvent(new UiEvent(0, delete, UiEventType.Click));
        Assert.Null(prompt);

        UiCatalogSlot row = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        row.OnEvent(new UiEvent(0, row, UiEventType.Click));
        delete.OnEvent(new UiEvent(0, delete, UiEventType.Click));

        Assert.Equal(
            "Are you sure you want to remove War One from your spellbook? "
            + "You will no longer be able to cast this spell unless you learn it again!",
            prompt);
        Assert.Empty(removed);
        complete!(false);
        Assert.Empty(removed);
        Assert.True(book.Knows(101u));

        delete.OnEvent(new UiEvent(0, delete, UiEventType.Click));
        complete!(true);
        Assert.Equal([101u], removed);
        Assert.True(book.Knows(101u));
        Assert.Single(VisibleSpellIds(list));

        book.OnSpellForgotten(101u);
        controller.Tick();
        Assert.False(book.Knows(101u));
        Assert.Empty(VisibleSpellIds(list));
    }

    [Fact]
    public void Components_UseAuthoredCategoryAndItemTemplates_WithRetailCountsAndSelection()
    {
        ImportedLayout layout = FixtureLoader.LoadSpellbook();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 10u,
            WeenieClassId = 100u,
            Name = "Copper Scarab",
            Type = ItemType.SpellComponents,
            IconId = 0x06000100u,
            StackSize = 2,
            ContainerId = 1u,
            IsComponentPack = true,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 11u,
            WeenieClassId = 101u,
            Name = "Diamond Scarab",
            Type = ItemType.SpellComponents,
            IconId = 0x06000101u,
            StackSize = 1,
            ContainerId = 1u,
            IsComponentPack = true,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 12u,
            WeenieClassId = 200u,
            Name = "Amaranth",
            Type = ItemType.SpellComponents,
            IconId = 0x06000200u,
            StackSize = 3,
            ContainerId = 1u,
            IsComponentPack = true,
        });
        var components = new Dictionary<uint, SpellComponentDescriptor>
        {
            [100u] = new(100u, "Copper Scarab", 0u, 0x06000100u),
            [101u] = new(101u, "Diamond Scarab", 0u, 0x06000101u),
            [200u] = new(200u, "Amaranth", 1u, 0x06000200u),
        };
        Spellbook book = CreateSpellbook();
        book.SetDesiredComponent(100u, 7u);
        var selected = new List<uint>();
        var desired = new List<(uint Component, uint Amount)>();
        var selection = new SelectionState();

        using SpellbookWindowController controller = Bind(
            layout,
            book,
            objects: objects,
            components: components,
            selection: selection,
            selectObject: objectId =>
            {
                selected.Add(objectId);
                selection.Select(objectId, SelectionChangeSource.Inventory);
            },
            setDesiredComponent: (component, amount) => desired.Add((component, amount)),
            resolveComponentIcon: icon => icon + 0x1000u)!;
        controller.ShowPage(SpellbookWindowPage.Components);

        UiItemList list = Assert.IsType<UiItemList>(
            layout.FindElement(SpellbookWindowController.ComponentListId)?.Children.Single());
        Assert.Equal(5, list.GetNumUIItems());
        Assert.Same(
            list.Scroll,
            Assert.IsType<UiScrollbar>(
                layout.FindElement(SpellbookWindowController.ComponentScrollbarId)).Model);

        UiTemplateListSlot scarabs = Assert.IsType<UiTemplateListSlot>(list.GetItem(0));
        UiText scarabTitle = Assert.IsType<UiText>(scarabs.Content.Root);
        Assert.Equal("SCARABS", Assert.Single(scarabTitle.LinesProvider()).Text);
        Assert.Equal(0x06001392u, scarabTitle.BackgroundSprite);

        UiTemplateListSlot copper = Assert.IsType<UiTemplateListSlot>(list.GetItem(1));
        UiDatElement copperRoot = Assert.IsType<UiDatElement>(copper.Content.Root);
        Assert.Equal((0x06005E21u, 1), copperRoot.ActiveMedia());
        Assert.Equal("Copper Scarab", Assert.Single(
            Assert.IsType<UiText>(copper.Content.FindElement(
                ComponentBookTemplateFactory.NameId)).LinesProvider()).Text);
        UiText ownedCount = Assert.IsType<UiText>(copper.Content.FindElement(
            ComponentBookTemplateFactory.OwnedCountId));
        Assert.Equal((224f, 0f, 60f, 15f),
            (ownedCount.Left, ownedCount.Top, ownedCount.Width, ownedCount.Height));
        Assert.False(ownedCount.OneLine);
        Assert.Equal("2", Assert.Single(ownedCount.LinesProvider()).Text);
        UiField desiredField = Assert.IsType<UiField>(copper.Content.FindElement(
            ComponentBookTemplateFactory.DesiredCountId));
        Assert.Equal((224f, 15f, 60f, 15f),
            (desiredField.Left, desiredField.Top, desiredField.Width, desiredField.Height));
        Assert.True(desiredField.RightAligned);
        Assert.Equal("7", desiredField.Text);
        UiElement iconHost = Assert.IsAssignableFrom<UiElement>(copper.Content.FindElement(
            ComponentBookTemplateFactory.IconId));
        Assert.Equal(
            0x06001100u,
            Assert.IsType<UiTextureElement>(Assert.Single(iconHost.Children)).Texture);

        copper.OnEvent(new UiEvent(0u, copper, UiEventType.Click));
        Assert.Equal([10u], selected);
        Assert.True(copper.Selected);
        Assert.Equal(UiButtonStateMachine.Highlight, copperRoot.ActiveRetailStateId);
        Assert.Equal((0x06005E22u, 1), copperRoot.ActiveMedia());

        desiredField.SetText("21");
        desiredField.OnSubmit!(desiredField.Text);
        Assert.Equal([(100u, 21u)], desired);
        desiredField.SetText("5001");
        desiredField.OnSubmit!(desiredField.Text);
        Assert.Equal("21", desiredField.Text);
        Assert.Equal([(100u, 21u)], desired);

        UiTemplateListSlot herbs = Assert.IsType<UiTemplateListSlot>(list.GetItem(3));
        Assert.Equal("HERBS", Assert.Single(
            Assert.IsType<UiText>(herbs.Content.Root).LinesProvider()).Text);
        UiTemplateListSlot amaranth = Assert.IsType<UiTemplateListSlot>(list.GetItem(4));
        selection.Select(12u, SelectionChangeSource.Radar);
        Assert.True(amaranth.Selected);
        Assert.False(copper.Selected);
        selection.Select(0xDEADBEEFu, SelectionChangeSource.World);
        Assert.False(amaranth.Selected);

        Assert.True(objects.UpdateStackSize(10u, stackSize: 1, value: 0));
        controller.Tick();
        UiTemplateListSlot updatedCopper = Assert.IsType<UiTemplateListSlot>(list.GetItem(1));
        Assert.Equal("1", Assert.Single(
            Assert.IsType<UiText>(updatedCopper.Content.FindElement(
                ComponentBookTemplateFactory.OwnedCountId)).LinesProvider()).Text);
        Assert.Equal("21", Assert.IsType<UiField>(updatedCopper.Content.FindElement(
            ComponentBookTemplateFactory.DesiredCountId)).Text);

        Assert.True(objects.Remove(10u));
        controller.Tick();
        Assert.DoesNotContain(
            Enumerable.Range(0, list.GetNumUIItems())
                .Select(list.GetItem)
                .OfType<UiTemplateListSlot>(),
            slot => slot.EntryId == 100u);
    }

    private static SpellbookWindowController? Bind(
        ImportedLayout layout,
        Spellbook spellbook,
        Action<uint>? addFavorite = null,
        Action<uint>? sendFilter = null,
        Action<uint>? removeSpell = null,
        Action<uint>? examineSpell = null,
        Action<string, Action<bool>>? showConfirmation = null,
        Action? close = null,
        ClientObjectTable? objects = null,
        IReadOnlyDictionary<uint, SpellComponentDescriptor>? components = null,
        SelectionState? selection = null,
        Action<uint>? selectObject = null,
        Action<uint, uint>? setDesiredComponent = null,
        Func<uint, uint>? resolveComponentIcon = null)
        => SpellbookWindowController.Bind(
            layout,
            spellbook,
            objects ?? new ClientObjectTable(),
            () => 1u,
            components ?? new Dictionary<uint, SpellComponentDescriptor>(),
            selection ?? new SelectionState(),
            spellId => spellId + 1000u,
            resolveComponentIcon ?? (iconId => iconId),
            spellId => spellId == 103u ? 8 : 1,
            selectObject ?? (_ => { }),
            addFavorite ?? (_ => { }),
            filters =>
            {
                spellbook.SetSpellbookFilters(filters);
                sendFilter?.Invoke(filters);
            },
            removeSpell ?? (_ => { }),
            examineSpell,
            showConfirmation ?? ((_, _) => { }),
            (componentId, amount) =>
            {
                spellbook.SetDesiredComponent(componentId, amount);
                setDesiredComponent?.Invoke(componentId, amount);
            },
            close ?? (() => { }),
            new ComponentBookTemplateFactory(
                FixtureLoader.LoadComponentCategoryTemplateInfos(),
                FixtureLoader.LoadComponentRowTemplateInfos(),
                _ => (0u, 0, 0),
                defaultFont: null),
            RowStyle,
            rowFont: null);

    private static Spellbook CreateSpellbook()
    {
        const string csv = "Spell ID,Name,School,IconId [Hex],SortKey\n"
            + "101,War One,War Magic,0x06000001,2\n"
            + "102,Creature One,Creature Enchantment,0x06000002,1\n"
            + "103,War Eight,War Magic,0x06000003,3\n";
        return new Spellbook(SpellTable.LoadFromReader(new StringReader(csv)));
    }

    private static uint[] VisibleSpellIds(UiItemList list)
        => Enumerable.Range(0, list.GetNumUIItems())
            .Select(index => Assert.IsType<UiCatalogSlot>(list.GetItem(index)).EntryId)
            .ToArray();
}
