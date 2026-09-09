using AcDream.App.Spells;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class SpellcastingUiControllerTests
{
    private static (uint, int, int) NoTex(uint _) => (0u, 0, 0);

    [Fact]
    public void ImportedFixture_UsesRetailEmptyCells_ShortcutDigits_AndCompleteCastButton()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        RetailCombatLayout.FitFavoriteSlots(layout);
        layout.Root.Visible = true;
        var spellbook = new Spellbook();
        spellbook.OnSpellLearned(42u, 1f);
        spellbook.SetFavorite(0, 0, 42u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        uint[] regular = [101u, 102u, 103u, 104u, 105u, 106u, 107u, 108u, 109u];
        uint[] ghosted = [201u, 202u, 203u, 204u, 205u, 206u, 207u, 208u, 209u];
        uint[] empty = [301u, 302u, 303u, 304u, 305u, 306u, 307u, 308u, 309u];
        var digits = new UiShortcutDigitGraphics(regular, ghosted, empty);

        using SpellcastingUiController controller = Bind(
            layout, spellbook, objects, _ => { },
            shortcutDigits: digits,
            emptySlotSprite: 0x06001A97u)!;

        UiElement group = layout.FindElement(0x100000AAu)!;
        UiItemList list = Descendants(group).OfType<UiItemList>().First();
        UiScrollbar scrollbar = Descendants(group).OfType<UiScrollbar>().Single(
            candidate => candidate.DatElementId == SpellcastingUiController.FavoriteScrollbarId);
        Assert.True(list.SingleRow);
        Assert.True(list.HorizontalScroll);
        Assert.True(list.FillVisibleEmptySlots);
        Assert.Same(list.Scroll, scrollbar.Model);
        Assert.True(scrollbar.Horizontal);
        Assert.True(scrollbar.HideWhenDisabled);
        Assert.Equal(23f, scrollbar.DecrementButtonExtent);
        Assert.Equal(23f, scrollbar.IncrementButtonExtent);
        Assert.Equal(0x06004CDEu, scrollbar.UpSprite);
        Assert.Equal(0x06004CDFu, scrollbar.UpRolloverSprite);
        Assert.Equal(0x06004CDCu, scrollbar.DownSprite);
        Assert.Equal(0x06004CDDu, scrollbar.DownRolloverSprite);
        Assert.False(scrollbar.IsPresentationVisible);
        Assert.Equal(576f, list.Width);
        Assert.Equal(18, list.GetNumUIItems());

        UiCatalogSlot favorite = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        Assert.Equal(42u, favorite.EntryId);
        Assert.Equal(0, favorite.ShortcutNum);
        Assert.Same(regular, favorite.ActiveDigitArray());

        for (int i = 1; i < 9; i++)
        {
            UiCatalogSlot slot = Assert.IsType<UiCatalogSlot>(list.GetItem(i));
            Assert.True(slot.IsEmptySlot);
            Assert.Equal(0x06001A97u, slot.EmptySprite);
            Assert.Equal(i, slot.ShortcutNum);
            Assert.Same(empty, slot.ActiveDigitArray());
        }
        for (int i = 9; i < 18; i++)
        {
            Assert.Equal(0x06001A97u, list.GetItem(i)!.EmptySprite);
            Assert.Equal(-1, list.GetItem(i)!.ShortcutNum);
        }

        var cast = Assert.IsType<UiButton>(
            layout.FindElement(SpellcastingUiController.CastButtonId));
        Assert.Equal((662f, 75f), (cast.Left, cast.Width));
        Assert.Equal(3, cast.FaceSegmentCount);
        Assert.Equal(
            [
                new UiPixelRect(0, 0, 31, 31),
                new UiPixelRect(32, 0, 42, 31),
                new UiPixelRect(43, 0, 74, 31),
            ],
            cast.FaceSegmentRectsForTest());
    }

    [Fact]
    public void FavoriteOverflow_ArrowButtonsStepOneCell_AndSelectionScrollsIntoView()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        RetailCombatLayout.FitFavoriteSlots(layout);
        var spellbook = new Spellbook();
        for (uint spellId = 1u; spellId <= 20u; spellId++)
        {
            spellbook.OnSpellLearned(spellId, 1f);
            spellbook.SetFavorite(0, (int)spellId - 1, spellId);
        }
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });

        using SpellcastingUiController controller = Bind(
            layout, spellbook, objects, _ => { })!;

        UiElement group = layout.FindElement(0x100000AAu)!;
        UiItemList list = Descendants(group).OfType<UiItemList>().Single();
        UiScrollbar scrollbar = Descendants(group).OfType<UiScrollbar>().Single(
            candidate => candidate.DatElementId == SpellcastingUiController.FavoriteScrollbarId);
        Assert.True(scrollbar.IsPresentationVisible);
        Assert.Equal(64, list.Scroll.MaxScroll);

        Assert.True(scrollbar.OnEvent(new UiEvent(
            0u, scrollbar, UiEventType.MouseDown, Data1: (int)scrollbar.Width - 1)));
        Assert.Equal(32, list.Scroll.ScrollY);
        Assert.True(scrollbar.OnEvent(new UiEvent(
            0u, scrollbar, UiEventType.MouseDown, Data1: 0)));
        Assert.Equal(0, list.Scroll.ScrollY);

        controller.AddFavorite(20u);

        Assert.Equal(list.Scroll.MaxScroll, list.Scroll.ScrollY);

        Assert.True(scrollbar.OnEvent(new UiEvent(
            0u, scrollbar, UiEventType.MouseDown, Data1: 0)));
        int manualOffset = list.Scroll.ScrollY;
        objects.AddOrUpdate(new ClientObject { ObjectId = 99u, Name = "Unrelated" });
        controller.Tick();

        Assert.Equal(manualOffset, list.Scroll.ScrollY);
    }

    [Fact]
    public void ImportedFixture_BindsAllTabs_AndTracksEquippedEndowment()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        var spellbook = new Spellbook();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        var used = new List<uint>();
        using SpellcastingUiController? controller = Bind(
            layout, spellbook, objects, used.Add);

        Assert.True(controller is not null, DescribeBinding(layout));
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 2u,
            Name = "Orb",
            Type = ItemType.Caster,
            WielderId = 1u,
            CurrentlyEquippedLocation = EquipMask.Held,
            SpellId = 2670u,
            IconId = 0x06001234u,
            Useability = (ItemUseability.Self << 16) | ItemUseability.Wielded,
        });
        controller.Tick();

        UiElement host = Assert.IsAssignableFrom<UiElement>(
            layout.FindElement(SpellcastingUiController.EndowmentId));
        Assert.True(host.Visible);
        UiCatalogSlot slot = Assert.IsType<UiCatalogSlot>(host.Children[^1]);
        Assert.Equal(2u, slot.EntryId);
        Assert.Equal(2670u, slot.CatalogIconTexture);
        Assert.Equal(2u, slot.CatalogOverlayTexture);

        slot.OnEvent(new UiEvent(0, slot, UiEventType.Click));
        var cast = Assert.IsType<UiButton>(
            layout.FindElement(SpellcastingUiController.CastButtonId));
        cast.OnEvent(new UiEvent(0, cast, UiEventType.Click));
        Assert.Equal([2u], used);
    }

    [Fact]
    public void DragFavoriteOntoAnotherSlot_ThroughTheRealPointerPipeline_ReordersAndSyncsWire()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        RetailCombatLayout.FitFavoriteSlots(layout);
        layout.Root.Visible = true;
        var spellbook = new Spellbook();
        spellbook.OnSpellLearned(1u, 1f);
        spellbook.OnSpellLearned(2u, 1f);
        spellbook.OnSpellLearned(3u, 1f);
        spellbook.SetFavorite(0, 0, 1u);
        spellbook.SetFavorite(0, 1, 2u);
        spellbook.SetFavorite(0, 2, 3u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        var selection = new SelectionState();
        var casting = new RuntimeSpellCastState(spellbook, selection, new NoopSpellCastOperations());
        var adds = new List<(int Tab, int Position, uint SpellId)>();
        var removes = new List<(int Tab, uint SpellId)>();

        using SpellcastingUiController? controller = SpellcastingUiController.Bind(
            layout, spellbook, casting, objects, () => 1u,
            spellId => spellId,
            item => item.ObjectId,
            _ => { },
            selection,
            (tab, position, spellId) =>
            {
                adds.Add((tab, position, spellId));
                spellbook.SetFavorite(tab, position, spellId);
            },
            (tab, spellId) =>
            {
                removes.Add((tab, spellId));
                spellbook.RemoveFavorite(tab, spellId);
            });
        Assert.True(controller is not null, DescribeBinding(layout));

        var screen = new UiRoot { Width = 1280f, Height = 800f };
        screen.AddChild(layout.Root);
        controller.Tick();

        UiElement group = layout.FindElement(0x100000AAu)!;
        UiItemList list = Descendants(group).OfType<UiItemList>().First();
        UiCatalogSlot slot0 = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        UiCatalogSlot slot2 = Assert.IsType<UiCatalogSlot>(list.GetItem(2));
        Assert.Equal(1u, slot0.EntryId);
        Assert.Equal(3u, slot2.EntryId);

        System.Numerics.Vector2 p0 = slot0.ScreenPosition;
        System.Numerics.Vector2 p2 = slot2.ScreenPosition;
        int x0 = (int)(p0.X + slot0.Width / 2f);
        int y0 = (int)(p0.Y + slot0.Height / 2f);
        int x2 = (int)(p2.X + slot2.Width / 2f);
        int y2 = (int)(p2.Y + slot2.Height / 2f);

        screen.OnMouseDown(UiMouseButton.Left, x0, y0);
        screen.OnMouseMove(x0 + 10, y0);
        Assert.Same(slot0, screen.DragSource);
        Assert.Equal([(0, 1u)], removes);

        controller.Tick();
        Assert.Same(slot0, screen.DragSource);

        screen.OnMouseMove(x2, y2);
        screen.OnMouseUp(UiMouseButton.Left, x2, y2);

        Assert.Null(screen.DragSource);
        Assert.Equal(0, adds[0].Tab);
        Assert.Equal(1, adds[0].Position);
        Assert.Equal(1u, adds[0].SpellId);
        Assert.Equal(new uint[] { 2u, 1u, 3u }, spellbook.GetFavorites(0));

        // The list resyncs to the final state on the next tick.
        controller.Tick();
        Assert.Equal(2u, Assert.IsType<UiCatalogSlot>(list.GetItem(0)).EntryId);
        Assert.Equal(1u, Assert.IsType<UiCatalogSlot>(list.GetItem(1)).EntryId);
        Assert.Equal(3u, Assert.IsType<UiCatalogSlot>(list.GetItem(2)).EntryId);
    }

    [Fact]
    public void SpellFavoriteDrag_ShowsAuthoredAcceptRing_TracksPointer_AndNeverLiesAboutTheLanding()
    {
        var (controller, spellbook, screen, list, adds, _) = BindPointerFixture();
        using SpellcastingUiController _1 = controller;
        UiCatalogSlot slot0 = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        UiCatalogSlot slot1 = Assert.IsType<UiCatalogSlot>(list.GetItem(1));
        UiCatalogSlot slot2 = Assert.IsType<UiCatalogSlot>(list.GetItem(2));
        (int x0, int y0) = CellCenter(slot0);
        (int x1, int y1) = CellCenter(slot1);
        (int x2, int y2) = CellCenter(slot2);

        screen.OnMouseDown(UiMouseButton.Left, x0, y0);
        screen.OnMouseMove(x0 + 10, y0);
        Assert.Same(slot0, screen.DragSource);

        screen.OnMouseMove(x2, y2);
        Assert.Equal(2, SingleAcceptRingIndex(list));
        Assert.Equal(0x060011F9u, slot2.DragAcceptSprite);

        controller.Tick();
        Assert.Equal(2, SingleAcceptRingIndex(list));

        screen.OnMouseMove(x1, y1);
        Assert.Equal(1, SingleAcceptRingIndex(list));
        Assert.Equal(UiItemSlot.DragAcceptState.None, slot2.DragAcceptVisual);

        screen.OnMouseMove(x2, y2);
        var payload = Assert.IsType<SpellFavoriteDragPayload>(screen.DragPayload);
        int ringIndex = SingleAcceptRingIndex(list);
        Assert.Equal(2, ringIndex);
        int promised = controller.FavoriteDropIndex(payload, 0, list, list.GetItem(ringIndex)!);

        screen.OnMouseUp(UiMouseButton.Left, x2, y2);

        Assert.Null(screen.DragSource);
        Assert.Equal([(0, promised, 1u)], adds);
        Assert.Equal(new uint[] { 2u, 1u, 3u }, spellbook.GetFavorites(0));
        Assert.Equal(-1, SingleAcceptRingIndex(list));
    }

    [Fact]
    public void SpellFavoriteDrag_DroppedOnTheEmptyTail_AppendsAtTheEnd()
    {
        var (controller, spellbook, screen, list, adds, _) = BindPointerFixture();
        using SpellcastingUiController _1 = controller;
        UiCatalogSlot slot0 = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        UiCatalogSlot empty = Assert.IsType<UiCatalogSlot>(list.GetItem(3));
        Assert.True(empty.IsEmptySlot);
        (int x0, int y0) = CellCenter(slot0);
        (int ex, int ey) = CellCenter(empty);

        screen.OnMouseDown(UiMouseButton.Left, x0, y0);
        screen.OnMouseMove(x0 + 10, y0);
        Assert.Same(slot0, screen.DragSource);

        screen.OnMouseMove(ex, ey);
        Assert.Equal(3, SingleAcceptRingIndex(list));
        var payload = Assert.IsType<SpellFavoriteDragPayload>(screen.DragPayload);
        int promised = controller.FavoriteDropIndex(payload, 0, list, empty);
        Assert.Equal(2, promised);

        screen.OnMouseUp(UiMouseButton.Left, ex, ey);

        Assert.Equal([(0, promised, 1u)], adds);
        Assert.Equal(new uint[] { 2u, 3u, 1u }, spellbook.GetFavorites(0));
        Assert.Equal(-1, SingleAcceptRingIndex(list));
    }

    [Fact]
    public void SpellFavoriteDrag_RingClearsOnLeave_AndAnOffBarReleaseKeepsTheLiftRemoval()
    {
        var (controller, spellbook, screen, list, adds, removes) = BindPointerFixture();
        using SpellcastingUiController _1 = controller;
        UiCatalogSlot slot0 = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        UiCatalogSlot slot2 = Assert.IsType<UiCatalogSlot>(list.GetItem(2));
        (int x0, int y0) = CellCenter(slot0);
        (int x2, int y2) = CellCenter(slot2);

        screen.OnMouseDown(UiMouseButton.Left, x0, y0);
        screen.OnMouseMove(x0 + 10, y0);
        screen.OnMouseMove(x2, y2);
        Assert.Equal(2, SingleAcceptRingIndex(list));

        screen.OnMouseMove((int)screen.Width - 2, (int)screen.Height - 2);
        Assert.Equal(-1, SingleAcceptRingIndex(list));

        screen.OnMouseUp(UiMouseButton.Left, (int)screen.Width - 2, (int)screen.Height - 2);

        Assert.Null(screen.DragSource);
        Assert.Empty(adds);
        Assert.Equal([(0, 1u)], removes);
        Assert.Equal(new uint[] { 2u, 3u }, spellbook.GetFavorites(0));
        Assert.Equal(-1, SingleAcceptRingIndex(list));
    }

    [Fact]
    public void FavoriteCellRing_AcceptsSpellPayloads_StaysNeutralForPhysicalItems()
    {
        var (controller, _, _, list, _, _) = BindPointerFixture();
        using SpellcastingUiController _1 = controller;
        UiCatalogSlot occupied = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        UiCatalogSlot empty = Assert.IsType<UiCatalogSlot>(list.GetItem(3));
        var physical = new ItemDragPayload(9u, ItemDragSource.Inventory, 0, new UiItemSlot(), null);

        occupied.OnEvent(new UiEvent(0, occupied, UiEventType.DragEnter, Payload: physical));
        Assert.Equal(UiItemSlot.DragAcceptState.None, occupied.DragAcceptVisual);

        occupied.OnEvent(new UiEvent(
            0, occupied, UiEventType.DragEnter, Payload: new SpellbookShortcutDragPayload(42u)));
        Assert.Equal(UiItemSlot.DragAcceptState.Accept, occupied.DragAcceptVisual);
        occupied.OnEvent(new UiEvent(0, occupied, UiEventType.DragOver));
        Assert.Equal(UiItemSlot.DragAcceptState.None, occupied.DragAcceptVisual);

        empty.OnEvent(new UiEvent(
            0, empty, UiEventType.DragEnter, Payload: new SpellFavoriteDragPayload(0, 0, 1u)));
        Assert.Equal(UiItemSlot.DragAcceptState.Accept, empty.DragAcceptVisual);
        empty.OnEvent(new UiEvent(0, empty, UiEventType.DropReleased, Payload: physical));
        Assert.Equal(UiItemSlot.DragAcceptState.None, empty.DragAcceptVisual);
    }

    /// <summary>Shared real-pointer fixture: favorites [1,2,3] on tab 0, the layout
    /// mounted in a UiRoot, adds/removes recorded and applied to the Spellbook.</summary>
    private (SpellcastingUiController Controller,
             Spellbook Spellbook,
             UiRoot Screen,
             UiItemList List,
             List<(int Tab, int Position, uint SpellId)> Adds,
             List<(int Tab, uint SpellId)> Removes) BindPointerFixture()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        RetailCombatLayout.FitFavoriteSlots(layout);
        layout.Root.Visible = true;
        var spellbook = new Spellbook();
        spellbook.OnSpellLearned(1u, 1f);
        spellbook.OnSpellLearned(2u, 1f);
        spellbook.OnSpellLearned(3u, 1f);
        spellbook.SetFavorite(0, 0, 1u);
        spellbook.SetFavorite(0, 1, 2u);
        spellbook.SetFavorite(0, 2, 3u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        var selection = new SelectionState();
        var casting = new RuntimeSpellCastState(spellbook, selection, new NoopSpellCastOperations());
        var adds = new List<(int Tab, int Position, uint SpellId)>();
        var removes = new List<(int Tab, uint SpellId)>();

        SpellcastingUiController? controller = SpellcastingUiController.Bind(
            layout, spellbook, casting, objects, () => 1u,
            spellId => spellId,
            item => item.ObjectId,
            _ => { },
            selection,
            (tab, position, spellId) =>
            {
                adds.Add((tab, position, spellId));
                spellbook.SetFavorite(tab, position, spellId);
            },
            (tab, spellId) =>
            {
                removes.Add((tab, spellId));
                spellbook.RemoveFavorite(tab, spellId);
            });
        Assert.True(controller is not null, DescribeBinding(layout));

        var screen = new UiRoot { Width = 1280f, Height = 800f };
        screen.AddChild(layout.Root);
        controller!.Tick();

        UiElement group = layout.FindElement(0x100000AAu)!;
        UiItemList list = Descendants(group).OfType<UiItemList>().First();
        return (controller, spellbook, screen, list, adds, removes);
    }

    private static (int X, int Y) CellCenter(UiItemSlot cell)
    {
        System.Numerics.Vector2 p = cell.ScreenPosition;
        return ((int)(p.X + cell.Width / 2f), (int)(p.Y + cell.Height / 2f));
    }

    private static int SingleAcceptRingIndex(UiItemList list)
    {
        int found = -1;
        for (int i = 0; i < list.GetNumUIItems(); i++)
        {
            if (list.GetItem(i) is { } cell
                && cell.DragAcceptVisual == UiItemSlot.DragAcceptState.Accept)
            {
                Assert.Equal(-1, found);
                found = i;
            }
        }
        return found;
    }

    [Fact]
    public void FavoriteDrop_IgnoresForeignInventoryPayload()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        var spellbook = new Spellbook();
        spellbook.OnSpellLearned(42u, 1f);
        spellbook.SetFavorite(0, 0, 42u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        using SpellcastingUiController? controller = Bind(
            layout, spellbook, objects, _ => { });

        Assert.True(controller is not null, DescribeBinding(layout));
        controller.Tick();
        UiElement group = Assert.IsAssignableFrom<UiElement>(layout.FindElement(0x100000AAu));
        UiItemList list = Descendants(group).OfType<UiItemList>().First();
        UiCatalogSlot slot = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        var foreign = new ItemDragPayload(9u, ItemDragSource.Inventory, 0, new UiItemSlot(), null);

        bool handled = slot.OnEvent(new UiEvent(
            0, slot, UiEventType.DropReleased, Payload: foreign));

        Assert.True(handled);
        Assert.Equal([42u], spellbook.GetFavorites(0));
    }

    [Fact]
    public void SpellFavoritePayload_IsRejectedByAPhysicalItemListDragHandler()
    {
        var recordedDrops = new List<uint>();
        var handler = new RecordingDragHandler(recordedDrops);
        var list = new UiItemList();
        list.RegisterDragHandler(handler);
        var cell = list.Cell;
        var payload = new SpellFavoriteDragPayload(0, 0, 42u);

        bool handled = cell.OnEvent(new UiEvent(
            0, cell, UiEventType.DropReleased, Payload: payload));

        Assert.True(handled);
        Assert.Empty(recordedDrops);
    }

    private sealed class RecordingDragHandler(List<uint> drops) : IItemListDragHandler
    {
        public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
            => drops.Add(payload.ObjId);
        public ItemDragAcceptance OnDragOver(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
            => ItemDragAcceptance.None;
        public void HandleDropRelease(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
            => drops.Add(payload.ObjId);
    }

    [Fact]
    public void SpellbookShortcutDrop_AddsToOpenTabWithoutRemovingLearnedSpell()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        var spellbook = new Spellbook();
        spellbook.OnSpellLearned(42u, 1f);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        var added = new List<(int Tab, int Position, uint Spell)>();
        using SpellcastingUiController controller = Bind(
            layout, spellbook, objects, _ => { },
            (tab, position, spell) => added.Add((tab, position, spell)))!;
        UiElement group = Assert.IsAssignableFrom<UiElement>(layout.FindElement(0x100000AAu));
        UiItemList list = Descendants(group).OfType<UiItemList>().First();

        bool handled = list.OnEvent(new UiEvent(
            0,
            list,
            UiEventType.DropReleased,
            Data1: 5,
            Payload: new SpellbookShortcutDragPayload(42u)));

        Assert.True(handled);
        Assert.Equal([(0, 0, 42u)], added);
        Assert.Equal([42u], spellbook.GetFavorites(0));
        Assert.True(spellbook.Knows(42u));
    }

    [Fact]
    public void UnrelatedObjectUpdate_DoesNotRecreateFavoriteSlots()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        var spellbook = new Spellbook();
        spellbook.OnSpellLearned(42u, 1f);
        spellbook.SetFavorite(0, 0, 42u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        using SpellcastingUiController controller = Bind(
            layout, spellbook, objects, _ => { })!;
        UiElement group = layout.FindElement(0x100000AAu)!;
        UiItemList list = Descendants(group).OfType<UiItemList>().First();
        UiItemSlot before = list.GetItem(0)!;

        objects.AddOrUpdate(new ClientObject { ObjectId = 99u, Name = "Rock" });
        controller.Tick();

        Assert.Same(before, list.GetItem(0));
    }

    [Fact]
    public void ObjectTableClear_RemovesEquippedEndowmentAndCannotUseStaleGuid()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        var spellbook = new Spellbook();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 2u,
            Name = "Orb",
            Type = ItemType.Caster,
            WielderId = 1u,
            CurrentlyEquippedLocation = EquipMask.Held,
            SpellId = 2670u,
        });
        var used = new List<uint>();
        using SpellcastingUiController controller = Bind(
            layout, spellbook, objects, used.Add)!;
        UiElement host = layout.FindElement(SpellcastingUiController.EndowmentId)!;
        UiCatalogSlot slot = Assert.IsType<UiCatalogSlot>(host.Children[^1]);
        slot.OnEvent(new UiEvent(0, slot, UiEventType.Click));

        objects.Clear();
        controller.Tick();

        Assert.False(host.Visible);
        Assert.Equal(0u, slot.EntryId);
        slot.OnEvent(new UiEvent(0, slot, UiEventType.DoubleClick));
        Assert.Empty(used);
    }

    [Fact]
    public void FavoritePressSelectsImmediately_AndRightClickExaminesLocally()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);
        var spellbook = new Spellbook();
        spellbook.OnSpellLearned(42u, 1f);
        spellbook.OnSpellLearned(43u, 1f);
        spellbook.SetFavorite(0, 0, 42u);
        spellbook.SetFavorite(0, 1, 43u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        var selection = new SelectionState();
        var examined = new List<uint>();
        using SpellcastingUiController controller = Bind(
            layout,
            spellbook,
            objects,
            _ => { },
            selection: selection,
            examineSpell: examined.Add)!;
        UiElement group = layout.FindElement(0x100000AAu)!;
        UiItemList list = Descendants(group).OfType<UiItemList>().First();
        UiCatalogSlot first = Assert.IsType<UiCatalogSlot>(list.GetItem(0));
        UiCatalogSlot second = Assert.IsType<UiCatalogSlot>(list.GetItem(1));
        Assert.True(first.Selected);

        second.OnEvent(new UiEvent(0u, second, UiEventType.MouseDown));

        Assert.False(first.Selected);
        Assert.True(second.Selected);
        Assert.Null(selection.SelectedObjectId);

        second.OnEvent(new UiEvent(0u, second, UiEventType.RightClick));

        Assert.Equal(new uint[] { 43u }, examined);
        Assert.Null(selection.SelectedObjectId);
    }


    [Fact]
    public void CastAvailability_UntargetedSpell_IsEnabled_WithCastSpellNameTooltip()
    {
        SpellMetadata spell = BuildSpell(
            42u, "Test Untargeted", isUntargeted: true, isSelfTargeted: false, targetMask: 0u);
        var spellbook = new Spellbook(SpellTable.Create([spell]));
        spellbook.OnSpellLearned(42u);
        spellbook.SetFavorite(0, 0, 42u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);

        using SpellcastingUiController controller = Bind(layout, spellbook, objects, _ => { })!;

        var cast = Assert.IsType<UiButton>(layout.FindElement(SpellcastingUiController.CastButtonId));
        Assert.True(cast.Enabled);
        Assert.Equal("CAST Test Untargeted", cast.TooltipText);
    }

    [Fact]
    public void CastAvailability_TargetedSpell_CompatibleTargetSelected_AppendsOnTargetName()
    {
        SpellMetadata spell = BuildSpell(
            42u, "Test Targeted", isUntargeted: false, isSelfTargeted: false, targetMask: 1u);
        var spellbook = new Spellbook(SpellTable.Create([spell]));
        spellbook.OnSpellLearned(42u);
        spellbook.SetFavorite(0, 0, 42u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        objects.AddOrUpdate(new ClientObject { ObjectId = 5u, Name = "Drudge" });
        var selection = new SelectionState();
        var operations = new ConfigurableSpellCastOperations { TargetCompatible = true };
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);

        using SpellcastingUiController controller = Bind(
            layout, spellbook, objects, _ => { }, selection: selection, operations: operations)!;
        selection.Select(5u, SelectionChangeSource.World);

        var cast = Assert.IsType<UiButton>(layout.FindElement(SpellcastingUiController.CastButtonId));
        Assert.True(cast.Enabled);
        Assert.Equal("CAST Test Targeted on Drudge", cast.TooltipText);
    }

    [Fact]
    public void CastAvailability_TargetedSpell_NoTargetSelected_IsDisabled_WithNeedsTargetTooltip()
    {
        SpellMetadata spell = BuildSpell(
            42u, "Test Targeted", isUntargeted: false, isSelfTargeted: false, targetMask: 1u);
        var spellbook = new Spellbook(SpellTable.Create([spell]));
        spellbook.OnSpellLearned(42u);
        spellbook.SetFavorite(0, 0, 42u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);

        using SpellcastingUiController controller = Bind(layout, spellbook, objects, _ => { })!;

        var cast = Assert.IsType<UiButton>(layout.FindElement(SpellcastingUiController.CastButtonId));
        Assert.False(cast.Enabled);
        Assert.Equal("You must select a target for Test Targeted", cast.TooltipText);
    }

    [Fact]
    public void CastAvailability_TargetedSpell_IncompatibleTargetSelected_IsDisabled_WithNeedsAppropriateTargetTooltip()
    {
        // F7: this DISABLED state was previously untested.
        SpellMetadata spell = BuildSpell(
            42u, "Test Targeted", isUntargeted: false, isSelfTargeted: false, targetMask: 1u);
        var spellbook = new Spellbook(SpellTable.Create([spell]));
        spellbook.OnSpellLearned(42u);
        spellbook.SetFavorite(0, 0, 42u);
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        objects.AddOrUpdate(new ClientObject { ObjectId = 5u, Name = "Drudge" });
        var selection = new SelectionState();
        var operations = new ConfigurableSpellCastOperations { TargetCompatible = false };
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);

        using SpellcastingUiController controller = Bind(
            layout, spellbook, objects, _ => { }, selection: selection, operations: operations)!;
        selection.Select(5u, SelectionChangeSource.World);

        var cast = Assert.IsType<UiButton>(layout.FindElement(SpellcastingUiController.CastButtonId));
        Assert.False(cast.Enabled);
        Assert.Equal("You must select an appropriate target for Test Targeted", cast.TooltipText);
    }

    [Fact]
    public void CastAvailability_EndowmentSelfTarget_ComposesItemAndSpellName()
    {
        SpellMetadata spell = BuildSpell(
            2670u, "Lightning Bolt VI", isUntargeted: false, isSelfTargeted: false, targetMask: 1u);
        var spellbook = new Spellbook(SpellTable.Create([spell]));
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 2u,
            Name = "Lightning Wand",
            Type = ItemType.Caster,
            WielderId = 1u,
            CurrentlyEquippedLocation = EquipMask.Held,
            SpellId = 2670u,
            // ItemUseability.Self shifted into the TARGET half.
            Useability = ItemUseability.Self << 16,
        });
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);

        using SpellcastingUiController controller = Bind(layout, spellbook, objects, _ => { })!;

        var cast = Assert.IsType<UiButton>(layout.FindElement(SpellcastingUiController.CastButtonId));
        Assert.True(cast.Enabled);
        Assert.Equal("USE the Lightning Wand (Lightning Bolt VI)", cast.TooltipText);
    }

    [Fact]
    public void CastAvailability_EndowmentNeedsTarget_NoTargetSelected_ComposesItemAndSpellName()
    {
        SpellMetadata spell = BuildSpell(
            2670u, "Lightning Bolt VI", isUntargeted: false, isSelfTargeted: false, targetMask: 1u);
        var spellbook = new Spellbook(SpellTable.Create([spell]));
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 2u,
            Name = "Lightning Wand",
            Type = ItemType.Caster,
            WielderId = 1u,
            CurrentlyEquippedLocation = EquipMask.Held,
            SpellId = 2670u,
            Useability = ItemUseability.Remote << 16,
        });
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCombatInfos(), NoTex, datFont: null);

        using SpellcastingUiController controller = Bind(layout, spellbook, objects, _ => { })!;

        var cast = Assert.IsType<UiButton>(layout.FindElement(SpellcastingUiController.CastButtonId));
        Assert.False(cast.Enabled);
        Assert.Equal(
            "You must select a target for the Lightning Wand (Lightning Bolt VI)",
            cast.TooltipText);
    }

    private static SpellcastingUiController? Bind(
        ImportedLayout layout,
        Spellbook spellbook,
        ClientObjectTable objects,
        Action<uint> useItem,
        Action<int, int, uint>? addFavorite = null,
        UiShortcutDigitGraphics? shortcutDigits = null,
        uint emptySlotSprite = 0u,
        SelectionState? selection = null,
        Action<uint>? examineSpell = null,
        IRuntimeSpellCastOperations? operations = null)
    {
        SelectionState selectionState = selection ?? new SelectionState();
        var casting = new RuntimeSpellCastState(
            spellbook,
            selectionState,
            operations ?? new NoopSpellCastOperations());
        return SpellcastingUiController.Bind(
            layout, spellbook, casting, objects, () => 1u,
            spellId => spellId,
            item => item.ObjectId,
            useItem,
            selectionState,
            (tab, position, spellId) =>
            {
                spellbook.SetFavorite(tab, position, spellId);
                addFavorite?.Invoke(tab, position, spellId);
            },
            (tab, spellId) => spellbook.RemoveFavorite(tab, spellId),
            shortcutDigits,
            emptySlotSprite,
            examineSpell);
    }

    private sealed class NoopSpellCastOperations : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId => 1u;
        public bool CanSend => true;
        public bool HasRequiredComponents(uint spellId) => true;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => true;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }

    private sealed class ConfigurableSpellCastOperations : IRuntimeSpellCastOperations
    {
        public bool TargetCompatible = true;
        public uint LocalPlayerId => 1u;
        public bool CanSend => true;
        public bool HasRequiredComponents(uint spellId) => true;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => TargetCompatible;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }

    private static SpellMetadata BuildSpell(
        uint spellId, string name, bool isUntargeted, bool isSelfTargeted, uint targetMask) =>
        new(
            SpellId: spellId,
            Name: name,
            School: "Life",
            Family: 0u,
            IconId: 0u,
            SpellWords: "",
            Duration: 0f,
            ManaCost: 0,
            IsDebuff: false,
            IsFellowship: false,
            Description: "",
            SortKey: 0,
            Difficulty: 0,
            Flags: isSelfTargeted ? (uint)SpellFlags.SelfTargeted : 0u,
            Generation: 1,
            IsFastWindup: false,
            IsOffensive: false,
            IsUntargeted: isUntargeted,
            Speed: 0f,
            CasterEffect: 0u,
            TargetEffect: 0u,
            TargetMask: targetMask,
            SpellType: 0);

    private static void ApplyAnchors(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyAnchors(child);
        }
    }

    private static IEnumerable<UiElement> Descendants(UiElement root)
    {
        foreach (UiElement child in root.Children)
        {
            yield return child;
            foreach (UiElement nested in Descendants(child)) yield return nested;
        }
    }

    private static string DescribeBinding(ImportedLayout layout)
    {
        uint[] ids =
        [
            SpellcastingUiController.CastButtonId, SpellcastingUiController.EndowmentId,
            0x100000A3u, 0x100000AAu, 0x100005C2u, 0x100005C3u,
        ];
        return string.Join(", ", ids.Select(id =>
            $"{id:X8}={layout.FindElement(id)?.GetType().Name ?? "missing"}"));
    }
}
