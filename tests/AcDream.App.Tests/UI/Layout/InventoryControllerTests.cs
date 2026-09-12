using System;
using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public class InventoryControllerTests
{
    private const uint Player = 0x50000001u;

    private sealed class TestElement : UiElement { }

    // Element ids (spec §1).
    private const uint ContentsGrid    = 0x100001C6u;
    private const uint ContainerList   = 0x100001CAu;
    private const uint TopContainer    = 0x100001C9u;
    private const uint BurdenMeter     = 0x100001D9u;
    private const uint BurdenText      = 0x100001D8u;
    private const uint BurdenCaption   = 0x100001D7u;
    private const uint ContentsCaption = 0x100001C5u;
    private const uint TitleText       = 0x100001D3u;
    private const uint ContentsScrollbar = 0x100001C7u;

    private static (ImportedLayout layout, UiItemList grid, UiItemList containers,
                    UiItemList top, UiMeter meter, UiElement burdenText,
                    UiElement burdenCap, UiElement contentsCap) BuildLayout()
    {
        var grid        = new UiItemList { Width = 192, Height = 96 };
        var containers  = new UiItemList { Width = 36,  Height = 252 };
        var top         = new UiItemList { Width = 36,  Height = 36 };
        var meter       = new UiMeter    { Width = 11,  Height = 58 };
        var burdenText  = new TestElement { Width = 36,  Height = 15 };
        var burdenCap   = new TestElement { Width = 36,  Height = 15 };
        var contentsCap = new TestElement { Width = 192, Height = 15 };
        var titleText   = new TestElement { Width = 276, Height = 25 };
        var scrollbar   = new UiScrollbar { Width = 16,  Height = 96 };
        var root        = new TestElement { Width = 300, Height = 362 };
        root.AddChild(grid); root.AddChild(containers); root.AddChild(top);
        root.AddChild(meter); root.AddChild(burdenText); root.AddChild(burdenCap);
        root.AddChild(contentsCap); root.AddChild(titleText); root.AddChild(scrollbar);
        var byId = new Dictionary<uint, UiElement>
        {
            [ContentsGrid]    = grid, [ContainerList] = containers, [TopContainer] = top,
            [BurdenMeter]     = meter, [BurdenText] = burdenText, [BurdenCaption] = burdenCap,
            [ContentsCaption] = contentsCap, [TitleText] = titleText, [ContentsScrollbar] = scrollbar,
        };
        return (new ImportedLayout(root, byId), grid, containers, top, meter,
                burdenText, burdenCap, contentsCap);
    }

    private static InventoryController Bind(ImportedLayout layout, ClientObjectTable objects,
        int? strength = 100, List<uint>? uses = null,
        List<(uint item, uint container, int placement)>? puts = null,
        List<(uint item, uint container, uint placement, uint amount)>? splits = null,
        List<(uint source, uint target, uint amount)>? merges = null,
        List<(uint source, uint target)>? mergeNotices = null,
        string? ownerName = null,
        Action? onClose = null,
        SelectionState? selection = null,
        StackSplitQuantityState? stackSplitQuantity = null,
        ItemInteractionController? itemInteraction = null,
        Func<int?>? strengthProvider = null,
        Spellbook? burdenSpellbook = null,
        ShortcutStore? shortcuts = null,
        UiShortcutDigitGraphics? shortcutDigits = null,
        CombatState? combat = null,
        Func<ItemType, uint, uint, uint, uint, uint>? iconIds = null)
        => InventoryController.Bind(layout, objects, () => Player,
            iconIds: iconIds ?? ((_, _, _, _, _) => 0u),
            strength: strengthProvider ?? (() => strength), datFont: null,
            ownerName: ownerName is null ? null : () => ownerName,
            sendUse: uses is null ? null : g => uses.Add(g),
            sendPutItemInContainer: puts is null ? null : (i, c, p) => puts.Add((i, c, p)),
            sendStackableSplitToContainer: splits is null
                ? null
                : (i, c, p, a) => splits.Add((i, c, p, a)),
            sendStackableMerge: merges is null ? null : (s, t, a) => merges.Add((s, t, a)),
            notifyMergeAttempt: mergeNotices is null ? null : (s, t) => mergeNotices.Add((s, t)),
            onClose: onClose,
            selection: selection ?? new SelectionState(),
            stackSplitQuantity: stackSplitQuantity,
            itemInteraction: itemInteraction,
            burdenSpellbook: burdenSpellbook,
            shortcuts: shortcuts,
            shortcutDigits: shortcutDigits,
            combat: combat);

    // ── #35: a rend or imbue re-sends the whole item description; the cell has
    //        to pick up the new underlay without a relog.

    [Fact]
    public void RefreshedDescription_redrawsTheCellWithTheNewUnderlay()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);

        Bind(layout, objects, iconIds: (_, _, underlay, _, _) => underlay);

        Assert.Equal(0u, grid.GetItem(0)!.IconTexture);

        objects.Ingest(new WeenieData(
            Guid: 0xA,
            Name: null,
            Type: null,
            WeenieClassId: 0u,
            IconId: 0u,
            IconOverlayId: 0u,
            IconUnderlayId: 0x06001234u,
            Effects: 0u,
            Value: null,
            StackSize: null,
            StackSizeMax: null,
            Burden: null,
            ContainerId: null,
            WielderId: null,
            ValidLocations: null,
            CurrentWieldedLocation: null,
            Priority: null,
            ItemsCapacity: null,
            ContainersCapacity: null,
            Structure: null,
            MaxStructure: null,
            Workmanship: null));

        Assert.Equal(0x06001234u, grid.GetItem(0)!.IconTexture);
    }

    [Fact]
    public void ChangedIconOverlay_redrawsTheCell()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);

        Bind(layout, objects, iconIds: (_, _, _, overlay, _) => overlay);

        Assert.Equal(0u, grid.GetItem(0)!.IconTexture);

        objects.UpdateDataIdProperty(
            0xA,
            (uint)AcDream.Core.Properties.PropertyDataId.IconOverlay,
            0x06005678u);

        Assert.Equal(0x06005678u, grid.GetItem(0)!.IconTexture);
    }

    private static UiButton MakeButton(uint id)
    {
        var info = new ElementInfo { Id = id, Type = 1 };
        return new UiButton(info, static _ => (0u, 0, 0)) { Width = 16, Height = 16 };
    }

    private static void SeedBag(ClientObjectTable t, uint bag, int slot, int itemsCapacity = 24)
    {
        t.AddOrUpdate(new ClientObject { ObjectId = bag, Type = ItemType.Container, ItemsCapacity = itemsCapacity });
        t.MoveItem(bag, Player, slot);
    }

    private static void SeedContained(ClientObjectTable t, uint guid, uint container, int slot,
        int burden = 0, ItemType type = ItemType.None)
    {
        t.AddOrUpdate(new ClientObject { ObjectId = guid, Burden = burden, Type = type });
        t.MoveItem(guid, container, slot);
    }

    [Fact]
    public void Populate_fills_contents_grid_with_loose_items_only()
    {
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0, burden: 10);                // loose
        SeedContained(objects, 0xB, Player, slot: 1, burden: 20);                // loose
        SeedContained(objects, 0xC, Player, slot: 2, type: ItemType.Container);  // side bag

        Bind(layout, objects);

        Assert.Equal(102, grid.GetNumUIItems());
        Assert.Equal(0xAu, grid.GetItem(0)!.ItemId);
        Assert.Equal(0xBu, grid.GetItem(1)!.ItemId);
        Assert.Equal(0u, grid.GetItem(2)!.ItemId);      // padded empty
        Assert.Equal(7, containers.GetNumUIItems());
        Assert.Equal(0xCu, containers.GetItem(0)!.ItemId);
        Assert.Equal(0u, containers.GetItem(1)!.ItemId); // padded empty frame
    }

    [Fact]
    public void SessionTableClearRebuildsInventoryWithoutOldObjects()
    {
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        SeedBag(objects, 0xCu, slot: 1);
        Bind(layout, objects);
        Assert.Equal(0xAu, grid.GetItem(0)!.ItemId);
        Assert.Equal(0xCu, containers.GetItem(0)!.ItemId);

        objects.Clear();

        Assert.Equal(0u, grid.GetItem(0)!.ItemId);
        Assert.Equal(0u, containers.GetItem(0)!.ItemId);
    }

    [Fact]
    public void GenerationReplacementRemovesOldOwnedProjectionAndSelection()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        var selection = new SelectionState();
        selection.Select(0xAu, SelectionChangeSource.Inventory);
        Bind(layout, objects, selection: selection);
        Assert.Equal(0xAu, grid.GetItem(0)!.ItemId);

        objects.ReplaceGeneration(WorldReplacement(0xAu), generation: 2);

        Assert.Equal(0u, grid.GetItem(0)!.ItemId);
        Assert.Null(selection.SelectedObjectId);
    }

    [Fact]
    public void Populate_uses_manifest_container_hint_before_create_object_details()
    {
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.ReplaceContents(Player, new[]
        {
            new ContainerContentEntry(0xC, 1u),
            new ContainerContentEntry(0xA, 0u),
        });

        Bind(layout, objects);

        Assert.Equal(0xCu, containers.GetItem(0)!.ItemId);
        Assert.Equal(0xAu, grid.GetItem(0)!.ItemId);
    }

    [Fact]
    public void Equipped_items_are_excluded_from_the_grid_and_selector()
    {
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);                 // loose pack item
        // A self-wielded item: MoveItem with an equip location indexes it under the player
        // (the live wield path), but it must NOT show in the pack grid or the selector.
        objects.AddOrUpdate(new ClientObject { ObjectId = 0xD });
        objects.MoveItem(0xD, Player, newSlot: 1, newEquipLocation: EquipMask.MeleeWeapon);

        Bind(layout, objects);

        Assert.Equal(102, grid.GetNumUIItems());
        Assert.Equal(0xAu, grid.GetItem(0)!.ItemId);
        Assert.Equal(7, containers.GetNumUIItems());    // 7 empty slots (no bags; the equipped item isn't here)
        Assert.Equal(0u, containers.GetItem(0)!.ItemId);
    }

    [Fact]
    public void Grid_is_six_columns_thirtytwo_px()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        Bind(layout, new ClientObjectTable());
        Assert.Equal(6, grid.Columns);
        Assert.Equal(UiItemListFlow.RowMajor, grid.Flow);
        Assert.Equal(32f, grid.CellWidth);
        Assert.Equal(32f, grid.CellHeight);
    }

    [Fact]
    public void ObjectAdded_for_player_item_rebuilds_grid()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        Bind(layout, objects);
        Assert.Equal(102, grid.GetNumUIItems());        // empty grid still shows the full 102-slot pack
        Assert.Equal(0u, grid.GetItem(0)!.ItemId);      // slot 0 empty before the add

        objects.Ingest(new WeenieData(0xA, "Sword", ItemType.MeleeWeapon, 1, 0, 0, 0, 0,
            null, null, null, 5, Player, null, null, null, null, null, null, null, null, null));

        Assert.Equal(102, grid.GetNumUIItems());        // still 102; the item fills slot 0
        Assert.Equal(0xAu, grid.GetItem(0)!.ItemId);
    }

    [Fact]
    public void Burden_meter_fill_and_percent_from_load()
    {
        var (layout, _, _, _, meter, burdenText, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 0xA, ContainerId = Player, Burden = 7500 });
        Bind(layout, objects, strength: 100);

        Assert.Equal(0.16667f, meter.Fill() ?? -1f, 3);
        Assert.True(meter.Vertical);
        Assert.Contains("50%", CaptionText(burdenText));
    }

    [Fact]
    public void EnchantmentChange_RefreshesBurdenFromEffectiveStrength()
    {
        var (layout, _, _, _, meter, burdenText, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        var props = new PropertyBundle();
        props.Ints[5] = 1600;
        objects.UpsertProperties(Player, props);
        int effectiveStrength = 10;
        var spellbook = new Spellbook();
        using InventoryController controller = Bind(
            layout,
            objects,
            strengthProvider: () => effectiveStrength,
            burdenSpellbook: spellbook);

        Assert.Equal(1600f / 1500f / 3f, meter.Fill() ?? -1f, 3);
        Assert.Contains("106%", CaptionText(burdenText));

        effectiveStrength = 20;
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            42u, 1u, 60d, Player, Bucket: 1u));
        Assert.Equal(1600f / 3000f / 3f, meter.Fill() ?? -1f, 3);
        Assert.Contains("53%", CaptionText(burdenText));

        effectiveStrength = 10;
        spellbook.OnPurgeAll();
        Assert.Equal(1600f / 1500f / 3f, meter.Fill() ?? -1f, 3);
        Assert.Contains("106%", CaptionText(burdenText));
    }

    [Fact]
    public void Captions_render_known_strings()
    {
        var (layout, _, _, _, _, _, burdenCap, contentsCap) = BuildLayout();
        var title = layout.FindElement(TitleText)!;
        Bind(layout, new ClientObjectTable(), ownerName: "Horan");
        Assert.Contains("Inventory of Horan", CaptionText(title));
        Assert.Contains("Burden", CaptionText(burdenCap));
        Assert.Contains("Contents of Backpack", CaptionText(contentsCap));
    }

    [Fact]
    public void Window_chrome_button_invokes_close_callback()
    {
        var (layout, _, _, _, _, _, _, _) = BuildLayout();
        var close = MakeButton(WindowChromeController.InventoryCloseButtonId);
        layout.Root.AddChild(close);
        int closes = 0;

        Bind(layout, new ClientObjectTable(), onClose: () => closes++);
        close.OnEvent(new UiEvent(0u, close, UiEventType.Click));

        Assert.Equal(1, closes);
    }

    [Fact]
    public void Window_maximize_button_is_not_bound_as_close()
    {
        var (layout, _, _, _, _, _, _, _) = BuildLayout();
        var maximize = MakeButton(WindowChromeController.MaxMinButtonId);
        layout.Root.AddChild(maximize);
        int closes = 0;

        Bind(layout, new ClientObjectTable(), onClose: () => closes++);
        maximize.OnEvent(new UiEvent(0u, maximize, UiEventType.Click));

        Assert.Equal(0, closes);
    }

    [Fact]
    public void Burden_reads_wire_EncumbranceVal_over_carried_sum()
    {
        var (layout, _, _, _, meter, burdenText, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 0xA, ContainerId = Player, Burden = 3000 });
        var bundle = new PropertyBundle();
        bundle.Ints[5] = 7500;                          // EncumbranceVal (PropertyInt 5)
        objects.UpsertProperties(Player, bundle);

        Bind(layout, objects, strength: 100);

        Assert.Equal(0.16667f, meter.Fill() ?? -1f, 3); // 7500/15000/3 (wire), not 3000-based 0.0667
        Assert.Contains("50%", CaptionText(burdenText)); // not "20%"
    }

    [Fact]
    public void Live_player_int_update_refreshes_burden()
    {
        var (layout, _, _, _, meter, burdenText, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        var bundle = new PropertyBundle();
        bundle.Ints[5] = 3000;                          // initial EncumbranceVal → load 0.2 → "20%"
        objects.UpsertProperties(Player, bundle);
        Bind(layout, objects, strength: 100);
        Assert.Contains("20%", CaptionText(burdenText));

        objects.UpdateIntProperty(Player, 5u, 9000);    // live 0x02CD: → load 0.6 → "60%"

        Assert.Contains("60%", CaptionText(burdenText));
        Assert.Equal(0.2f, meter.Fill() ?? -1f, 3);     // 9000/15000/3
    }

    [Fact]
    public void Contents_grid_scrollbar_binds_to_the_grid_scroll_model()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        Bind(layout, new ClientObjectTable());
        var bar = (UiScrollbar)layout.FindElement(ContentsScrollbar)!;
        Assert.Same(grid.Scroll, bar.Model);            // the bar drives the grid's scroll
    }

    [Fact]
    public void Inventory_refresh_preserves_contents_pixel_scroll_offset()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player, ItemsCapacity = 102 });
        Bind(layout, objects);
        grid.Scroll.SetScrollY(40);

        objects.UpdateIntProperty(Player, 5u, 1000);

        Assert.Equal(40, grid.Scroll.ScrollY);
        Assert.Equal(-8f, grid.GetItem(6)!.Top);
    }

    [Fact]
    public void Contents_grid_pads_empty_slots_to_main_pack_capacity()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player, ItemsCapacity = 102 });
        SeedContained(objects, 0xA, Player, slot: 0, burden: 5);    // one loose item

        Bind(layout, objects);

        Assert.Equal(102, grid.GetNumUIItems());        // 1 item + 101 empty frames = 102
        Assert.Equal(0xAu, grid.GetItem(0)!.ItemId);
        Assert.Equal(0u, grid.GetItem(50)!.ItemId);     // a padded empty slot
    }

    [Fact]
    public void Side_bag_column_pads_empty_slots_up_to_capacity()
    {
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player, ContainersCapacity = 3 });
        SeedContained(objects, 0xC, Player, slot: 0, type: ItemType.Container);  // one side bag

        Bind(layout, objects);

        Assert.Equal(3, containers.GetNumUIItems());
        Assert.Equal(0xCu, containers.GetItem(0)!.ItemId);  // the bag
        Assert.Equal(0u,  containers.GetItem(1)!.ItemId);   // empty frame
        Assert.Equal(0u,  containers.GetItem(2)!.ItemId);   // empty frame
    }

    [Fact]
    public void Empty_sprites_and_drop_feedback_are_applied_per_list_role()
    {
        var (layout, grid, containers, top, _, _, _, _) = BuildLayout();
        InventoryController.Bind(layout, new ClientObjectTable(), () => Player,
            iconIds: (_, _, _, _, _) => 0u, strength: () => 100,
            selection: new SelectionState(), datFont: null,
            contentsEmptySprite: 0x06004D20u, sideBagEmptySprite: 0x06005D9Cu, mainPackEmptySprite: 0x06005D9Cu);

        Assert.Equal(0x06004D20u, grid.GetItem(0)!.EmptySprite);
        Assert.Equal(0x06005D9Cu, containers.GetItem(0)!.EmptySprite);
        Assert.Equal(0x06005D9Cu, top.GetItem(0)!.EmptySprite);
        Assert.Equal(0x060011F9u, grid.GetItem(0)!.DragAcceptSprite);
        Assert.Equal(0x060011F7u, containers.GetItem(0)!.DragAcceptSprite);
        Assert.Equal(0x060011F7u, top.GetItem(0)!.DragAcceptSprite);
    }

    [Fact]
    public void ClickSideBag_sendsUse_andSwapsGridToBagContents()
    {
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);          // a loose main-pack item
        SeedBag(objects, 0xC, slot: 1);                        // side bag (player slot 1)
        SeedContained(objects, 0xB1, 0xC, slot: 0);            // a thing already known to be in the bag
        var uses = new List<uint>();
        Bind(layout, objects, uses: uses);

        containers.GetItem(0)!.Clicked!();

        Assert.Contains(0xCu, uses);                           // Use(bag) sent
        Assert.Equal(0xB1u, grid.GetItem(0)!.ItemId);
        Assert.Equal(24, grid.GetNumUIItems());                // padded to the bag's ItemsCapacity
    }

    [Fact]
    public void ClickMainPackCell_afterBag_returnsToMainPack_withoutExternalCloseWire()
    {
        var (layout, grid, containers, top, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);          // loose main-pack item
        SeedBag(objects, 0xC, slot: 1);
        SeedContained(objects, 0xB1, 0xC, slot: 0);
        var uses = new List<uint>();
        var ctrl = Bind(layout, objects, uses: uses);

        containers.GetItem(0)!.Clicked!();                     // open the bag
        top.GetItem(0)!.Clicked!();

        Assert.DoesNotContain(Player, uses);                   // no Use for the main pack
        Assert.Equal(0xAu, grid.GetItem(0)!.ItemId);           // grid back to the main pack
        Assert.Equal(102, grid.GetNumUIItems());
    }

    [Fact]
    public void SwitchBetweenTwoBags_opensEach_withoutExternalCloseWire()
    {
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedBag(objects, 0xC1, slot: 0);
        SeedBag(objects, 0xC2, slot: 1);
        var uses = new List<uint>();
        Bind(layout, objects, uses: uses);

        containers.GetItem(0)!.Clicked!();
        containers.GetItem(1)!.Clicked!();

        Assert.Equal(new[] { 0xC1u, 0xC2u }, uses.ToArray());  // Use(A) then Use(B)
    }

    [Fact]
    public void OpenBag_marksTriangleAndSquare_onTheBagCell()
    {
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedBag(objects, 0xC, slot: 0);
        Bind(layout, objects, uses: new List<uint>());

        containers.GetItem(0)!.Clicked!();

        Assert.True(containers.GetItem(0)!.IsOpenContainer);   // triangle on the open bag
        Assert.True(containers.GetItem(0)!.Selected);          // square — the bag is also the selected item
    }

    [Fact]
    public void DoubleClickOwnedBag_opensOnceOnFirstPress_andNeverRunsGenericUse()
    {
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedBag(objects, 0xCu, slot: 0);
        var uses = new List<uint>();
        Bind(layout, objects, uses: uses);

        UiItemSlot bag = containers.GetItem(0)!;
        bag.OnEvent(new UiEvent(0u, bag, UiEventType.MouseDown));
        bag.OnEvent(new UiEvent(0u, bag, UiEventType.Click));
        bag.OnEvent(new UiEvent(0u, bag, UiEventType.MouseDown));
        bag.OnEvent(new UiEvent(0u, bag, UiEventType.Click));
        bag.OnEvent(new UiEvent(0u, bag, UiEventType.DoubleClick));

        Assert.Equal(new[] { 0xCu }, uses);
        Assert.Null(bag.DoubleClicked);
        Assert.True(containers.GetItem(0)!.IsOpenContainer);
        Assert.Equal(0xCu, objects.Get(0xCu)!.ObjectId);
    }

    [Fact]
    public void MouseDownGridItem_movesSquareImmediately_noWire_keepsOpenContainer()
    {
        var (layout, grid, _, top, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);
        var uses = new List<uint>();
        Bind(layout, objects, uses: uses);

        UiItemSlot item = grid.GetItem(0)!;
        item.OnEvent(new UiEvent(0u, item, UiEventType.MouseDown));

        Assert.True(grid.GetItem(0)!.Selected);                // square on the selected grid item
        Assert.True(top.GetItem(0)!.IsOpenContainer);
        Assert.Empty(uses);                                    // selection sends no wire
    }

    [Fact]
    public void MouseDownGridItem_updatesSharedSelection_andExternalSelectionMovesSquare()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        SeedContained(objects, 0xBu, Player, slot: 1);
        var selection = new SelectionState();
        Bind(layout, objects, selection: selection);

        UiItemSlot item = grid.GetItem(0)!;
        item.OnEvent(new UiEvent(0u, item, UiEventType.MouseDown));

        Assert.Equal(0xAu, selection.SelectedObjectId);
        Assert.True(grid.GetItem(0)!.Selected);

        selection.Select(0xBu, SelectionChangeSource.World);

        Assert.False(grid.GetItem(0)!.Selected);
        Assert.True(grid.GetItem(1)!.Selected);
    }

    [Fact]
    public void RightClickGridItem_selectsAndUsesSharedAppraisalOwner()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        var selection = new SelectionState();
        var appraisals = new List<uint>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            sendExamine: appraisals.Add);
        Bind(
            layout,
            objects,
            selection: selection,
            itemInteraction: interaction);

        grid.GetItem(0)!.OnEvent(
            new UiEvent(0u, grid.GetItem(0), UiEventType.RightClick));

        Assert.Equal(0xAu, selection.SelectedObjectId);
        Assert.True(grid.GetItem(0)!.Selected);
        Assert.Equal(new uint[] { 0xAu }, appraisals);
        Assert.Equal(1, interaction.BusyCount);
    }

    [Fact]
    public void InteractionStateChange_refreshesCellsInPlace_withoutRebuilding()
    {
        var (layout, grid, containers, top, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(
                new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null);
        Bind(layout, objects, itemInteraction: interaction);

        UiItemSlot cell = grid.GetItem(0)!;
        UiItemSlot bagCell = containers.GetItem(0)!;
        UiItemSlot mainPack = top.GetItem(0)!;

        interaction.IncrementBusyCount();

        Assert.Same(cell, grid.GetItem(0));
        Assert.Same(bagCell, containers.GetItem(0));
        Assert.Same(mainPack, top.GetItem(0));
    }

    [Fact]
    public void PressedCellSurvivesAppraisalOnSelection_soTheFirstClickCanDrag()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        var selection = new SelectionState();
        var appraisals = new List<uint>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(
                new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            sendExamine: appraisals.Add);
        Bind(layout, objects, selection: selection, itemInteraction: interaction);

        // What AppraisalUiController does while its window is open.
        selection.Changed += transition =>
        {
            if (transition.SelectedObjectId is uint id && id != 0u)
                interaction.ExamineSelectedOrEnterMode(id);
        };

        // BuildLayout leaves every list at the origin.
        grid.Left = 0;
        grid.Top = 260;
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(layout.Root);

        UiItemSlot cell = grid.GetItem(0)!;
        var (cellX, cellY) = AbsoluteCentre(cell);

        root.OnMouseMove(cellX, cellY);
        root.OnMouseDown(UiMouseButton.Left, cellX, cellY);

        Assert.Equal(0xAu, selection.SelectedObjectId);
        Assert.Equal(new uint[] { 0xAu }, appraisals);
        Assert.Equal(1, interaction.BusyCount);

        Assert.Same(cell, grid.GetItem(0));
        Assert.Same(cell, root.Captured);

        root.OnMouseMove(cellX + 12, cellY);

        Assert.Same(cell, root.DragSource);
        var payload = Assert.IsType<ItemDragPayload>(root.DragPayload);
        Assert.Equal(0xAu, payload.ObjId);
    }

    [Fact]
    public void PressedCellSurvivesAppraisalResponse_soTheFirstClickCanDrag()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        Bind(layout, objects);

        grid.Left = 0;
        grid.Top = 260;
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(layout.Root);

        UiItemSlot cell = grid.GetItem(0)!;
        var (cellX, cellY) = AbsoluteCentre(cell);

        root.OnMouseMove(cellX, cellY);
        root.OnMouseDown(UiMouseButton.Left, cellX, cellY);
        Assert.Same(cell, root.Captured);

        // What GameEventWiring does with an appraisal reply.
        var properties = new PropertyBundle();
        properties.Ints[1u] = 42;
        Assert.True(objects.UpdateAppraisal(0xAu, properties, Array.Empty<uint>()));

        Assert.Same(cell, grid.GetItem(0));
        Assert.Same(cell, root.Captured);

        root.OnMouseMove(cellX + 12, cellY);

        Assert.Same(cell, root.DragSource);
        var payload = Assert.IsType<ItemDragPayload>(root.DragPayload);
        Assert.Equal(0xAu, payload.ObjId);
    }

    private static (int x, int y) AbsoluteCentre(UiElement element)
    {
        float x = 0f, y = 0f;
        for (UiElement? e = element; e is not null; e = e.Parent)
        {
            x += e.Left;
            y += e.Top;
        }
        return ((int)(x + element.Width / 2f), (int)(y + element.Height / 2f));
    }

    [Fact]
    public void TargetMode_suppressesSelectedSquare_onPendingSource()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Type = ItemType.Creature,
            ItemsCapacity = 102,
        });
        SeedContained(objects, 0xA, Player, slot: 0);
        objects.Get(0xA)!.Useability = 0x000A0008u;
        var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            nowMs: () => 1_000);

        InventoryController.Bind(layout, objects, () => Player,
            iconIds: (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            itemInteraction: interaction);
        UiItemSlot item = grid.GetItem(0)!;
        item.OnEvent(new UiEvent(0u, item, UiEventType.MouseDown));
        Assert.True(grid.GetItem(0)!.Selected);

        Assert.True(interaction.ActivateItem(0xAu));

        Assert.True(interaction.IsTargetModeActive);
        Assert.False(grid.GetItem(0)!.Selected);
    }

    [Fact]
    public void Default_mainPackCell_isOpenContainer()
    {
        var (layout, _, _, top, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        Bind(layout, objects);
        Assert.True(top.GetItem(0)!.IsOpenContainer);
    }

    [Fact]
    public void MainPackCell_requestsConstantBackpackIcon_notPlayerBodyIcon()
    {
        var (layout, _, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player, IconId = 0x06001234u });
        (ItemType type, uint icon)? mainPackCall = null;
        InventoryController.Bind(layout, objects, () => Player,
            iconIds: (t, icon, _, _, _) => { if (icon == 0x0600127Eu) mainPackCall = (t, icon); return 0u; },
            strength: () => 100, selection: new SelectionState(), datFont: null);

        Assert.NotNull(mainPackCall);
        Assert.Equal(ItemType.Container, mainPackCall!.Value.type);
        Assert.Equal(0x0600127Eu, mainPackCall.Value.icon);
    }

    [Fact]
    public void SideBagCell_capacityBar_reflectsContents()
    {
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedBag(objects, 0xC, slot: 0, itemsCapacity: 24);                 // a side bag (cap 24)
        for (uint i = 0; i < 6; i++) SeedContained(objects, 0xB0u + i, 0xC, slot: (int)i);  // 6 items inside
        Bind(layout, objects);

        Assert.Equal(0.25f, containers.GetItem(0)!.CapacityFill);
    }

    [Fact]
    public void LooseGridItem_hasNoCapacityBar()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);
        Bind(layout, objects);
        Assert.Equal(-1f, grid.GetItem(0)!.CapacityFill);
    }

    [Fact]
    public void SalvageBag_structureIndicator_tracksPartialFullAndDepletedUpdates()
    {
        const uint salvageBag = 0xAu;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, salvageBag, Player, slot: 0);
        ClientObject bag = objects.Get(salvageBag)!;
        bag.Structure = 30;
        bag.MaxStructure = 100;
        objects.AddOrUpdate(bag);
        using var controller = Bind(layout, objects);

        Assert.Equal(0.3f, grid.GetItem(0)!.StructureFill);

        bag.Structure = 100;
        objects.AddOrUpdate(bag);
        Assert.Equal(-1f, grid.GetItem(0)!.StructureFill);

        bag.Structure = 0;
        objects.AddOrUpdate(bag);
        Assert.Equal(0f, grid.GetItem(0)!.StructureFill);
    }

    private static ItemDragPayload Payload(uint obj) => new(obj, ItemDragSource.Inventory, 0, new UiItemSlot());

    private static WeenieData WorldReplacement(uint guid) => new(
        Guid: guid,
        Name: "replacement",
        Type: ItemType.Misc,
        WeenieClassId: 1,
        IconId: 0,
        IconOverlayId: 0,
        IconUnderlayId: 0,
        Effects: 0,
        Value: null,
        StackSize: null,
        StackSizeMax: null,
        Burden: null,
        ContainerId: null,
        WielderId: null,
        ValidLocations: null,
        CurrentWieldedLocation: null,
        Priority: null,
        ItemsCapacity: null,
        ContainersCapacity: null,
        Structure: null,
        MaxStructure: null,
        Workmanship: null);

    [Fact]
    public void Drop_fromElsewhereOntoOccupiedGridCell_goesToTheTop()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);
        SeedContained(objects, 0xB, Player, slot: 1);
        var puts = new List<(uint, uint, int)>();
        var ctrl = Bind(layout, objects, puts: puts);

        objects.AddOrUpdate(new ClientObject { ObjectId = 0xFFFFu });
        var bCell = grid.GetItem(1)!;                          // ItemId == 0xB, SlotIndex 1
        ((IItemListDragHandler)ctrl).HandleDropRelease(grid, bCell, Payload(0xFFFFu));

        Assert.Contains((0xFFFFu, Player, 0), puts);
        Assert.Equal(0u, objects.Get(0xFFFFu)!.ContainerId);
    }

    [Fact]
    public void LootDrop_InsertsWaitingProjectionAtChosenSlotUntilServerConfirms()
    {
        const uint chest = 0x70000001u;
        const uint loot = 0x70000002u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        SeedContained(objects, 0xBu, Player, slot: 1);
        SeedContained(objects, loot, chest, slot: 0, type: ItemType.Misc);
        var selection = new SelectionState();
        selection.Select(loot, SelectionChangeSource.ExternalContainer);
        var puts = new List<(uint item, uint container, int placement)>();
        using var ctrl = Bind(layout, objects, puts: puts, selection: selection);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Ground };
        source.SetItem(loot, 0u);
        var payload = new ItemDragPayload(loot, ItemDragSource.Ground, 0, source);

        ctrl.HandleDropRelease(grid, grid.GetItem(1)!, payload);

        Assert.Equal(new[] { (loot, Player, 0) }, puts);
        Assert.Equal(chest, objects.Get(loot)!.ContainerId);
        Assert.Equal(loot, grid.GetItem(0)!.ItemId);
        Assert.True(grid.GetItem(0)!.WaitingVisual);
        Assert.True(grid.GetItem(0)!.Selected);
        Assert.Equal(0xAu, grid.GetItem(1)!.ItemId);
        Assert.Equal(0xBu, grid.GetItem(2)!.ItemId);

        objects.ApplyConfirmedServerMove(loot, Player, 0u, newSlot: 0);

        UiItemSlot confirmed = Enumerable.Range(0, grid.GetNumUIItems())
            .Select(i => grid.GetItem(i)!)
            .Single(cell => cell.ItemId == loot);
        Assert.False(confirmed.WaitingVisual);
        Assert.Equal(Player, objects.Get(loot)!.ContainerId);
    }

    [Fact]
    public void DoubleClickLoot_ReservesFirstOpenPackSlotBeforePickupIsSent()
    {
        const uint chest = 0x70000011u;
        const uint loot = 0x70000012u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        SeedContained(objects, 0xBu, Player, slot: 1);
        SeedContained(objects, loot, chest, slot: 0, type: ItemType.Misc);
        var pickups = new List<(uint item, uint container, int placement)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            nowMs: () => 1_000,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: (item, container, placement) =>
                pickups.Add((item, container, placement)));
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            itemInteraction: interaction);

        Assert.True(interaction.ActivateItem(loot));

        Assert.Equal(new[] { (loot, Player, 0) }, pickups);
        Assert.Equal(chest, objects.Get(loot)!.ContainerId);
        Assert.Equal(loot, grid.GetItem(0)!.ItemId);
        Assert.True(grid.GetItem(0)!.WaitingVisual);
        Assert.Equal(0xAu, grid.GetItem(1)!.ItemId);
        Assert.Equal(0xBu, grid.GetItem(2)!.ItemId);
    }

    [Fact]
    public void DirectLootWhilePendingKeepsTheOriginalProjectionAndRequest()
    {
        const uint chest = 0x70000021u;
        const uint firstLoot = 0x70000022u;
        const uint secondLoot = 0x70000023u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, firstLoot, chest, slot: 0, type: ItemType.Misc);
        SeedContained(objects, secondLoot, chest, slot: 1, type: ItemType.Misc);
        long now = 1_000;
        var eventOrder = new List<(string Kind, uint Item, ulong Token)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            nowMs: () => now,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { });
        interaction.PendingBackpackPlacementRequested += pending =>
            eventOrder.Add(("request", pending.ItemId, pending.Token));
        interaction.PendingBackpackPlacementCancelled += pending =>
            eventOrder.Add(("cancel", pending.ItemId, pending.Token));
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            itemInteraction: interaction);

        Assert.True(interaction.ActivateItem(firstLoot));
        now += 200;
        Assert.False(interaction.ActivateItem(secondLoot));

        Assert.Collection(
            eventOrder,
            first => Assert.Equal(("request", firstLoot), (first.Kind, first.Item)));
        Assert.Equal(firstLoot, grid.GetItem(0)!.ItemId);
        Assert.True(grid.GetItem(0)!.WaitingVisual);
        Assert.DoesNotContain(
            Enumerable.Range(0, grid.GetNumUIItems()).Select(i => grid.GetItem(i)!.ItemId),
            id => id == secondLoot);
    }

    [Fact]
    public void GroundDragThenDirectPickupKeepsTheFirstPendingProjection()
    {
        const uint chest = 0x70000031u;
        const uint draggedLoot = 0x70000032u;
        const uint directLoot = 0x70000033u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, draggedLoot, chest, slot: 0, type: ItemType.Misc);
        SeedContained(objects, directLoot, chest, slot: 1, type: ItemType.Misc);
        var puts = new List<(uint Item, uint Container, int Placement)>();
        var eventOrder = new List<(string Kind, uint Item)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: (item, container, placement) =>
                puts.Add((item, container, placement)));
        interaction.PendingBackpackPlacementRequested += pending =>
            eventOrder.Add(("request", pending.ItemId));
        interaction.PendingBackpackPlacementCancelled += pending =>
            eventOrder.Add(("cancel", pending.ItemId));
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            sendPutItemInContainer: (item, container, placement) =>
                puts.Add((item, container, placement)),
            itemInteraction: interaction);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Ground };
        source.SetItem(draggedLoot, 0u);

        inventory.HandleDropRelease(
            grid,
            grid.GetItem(4)!,
            new ItemDragPayload(draggedLoot, ItemDragSource.Ground, 0, source));
        Assert.True(interaction.PlaceWorldItemInBackpack(directLoot));

        Assert.Equal(new[] { ("request", draggedLoot) }, eventOrder);
        Assert.Equal(new[] { (draggedLoot, Player, 0) }, puts);
        Assert.Equal(draggedLoot, grid.GetItem(0)!.ItemId);
        Assert.True(grid.GetItem(0)!.WaitingVisual);
        Assert.DoesNotContain(
            Enumerable.Range(0, grid.GetNumUIItems()).Select(i => grid.GetItem(i)!.ItemId),
            id => id == directLoot);
    }

    [Fact]
    public void DirectPickupThenGroundDragAcceptsHoverButRejectsTheSecondRequest()
    {
        const uint chest = 0x70000041u;
        const uint directLoot = 0x70000042u;
        const uint draggedLoot = 0x70000043u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, directLoot, chest, slot: 0, type: ItemType.Misc);
        SeedContained(objects, draggedLoot, chest, slot: 1, type: ItemType.Misc);
        var eventOrder = new List<(string Kind, uint Item)>();
        var puts = new List<(uint Item, uint Container, int Placement)>();
        var messages = new List<string>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: (item, container, placement) =>
                puts.Add((item, container, placement)),
            systemMessage: messages.Add);
        interaction.PendingBackpackPlacementRequested += pending =>
            eventOrder.Add(("request", pending.ItemId));
        interaction.PendingBackpackPlacementCancelled += pending =>
            eventOrder.Add(("cancel", pending.ItemId));
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            sendPutItemInContainer: (item, container, placement) =>
                puts.Add((item, container, placement)),
            itemInteraction: interaction);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Ground };
        source.SetItem(draggedLoot, 0u);

        Assert.True(interaction.PlaceWorldItemInBackpack(directLoot));
        Assert.Equal(
            ItemDragAcceptance.Accept,
            inventory.OnDragOver(
                grid,
                grid.GetItem(5)!,
                new ItemDragPayload(draggedLoot, ItemDragSource.Ground, 0, source)));
        inventory.HandleDropRelease(
            grid,
            grid.GetItem(5)!,
            new ItemDragPayload(draggedLoot, ItemDragSource.Ground, 0, source));

        Assert.Equal(new[] { ("request", directLoot) }, eventOrder);
        Assert.Equal(new[] { (directLoot, Player, 0) }, puts);
        Assert.Equal(directLoot, grid.GetItem(0)!.ItemId);
        Assert.True(grid.GetItem(0)!.WaitingVisual);
        Assert.Equal(
            new[] { "Already attempting to place that item here" },
            messages);
        Assert.DoesNotContain(
            Enumerable.Range(0, grid.GetNumUIItems()).Select(i => grid.GetItem(i)!.ItemId),
            id => id == draggedLoot);
    }

    [Fact]
    public void PendingContentsProjectionUsesGenericGlobalMessageOnBagColumnDrop()
    {
        const uint chest = 0x70000044u;
        const uint pendingLoot = 0x70000045u;
        const uint ownedItem = 0x50000046u;
        const uint bag = 0x50000047u;
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, pendingLoot, chest, 0, type: ItemType.Misc);
        SeedContained(objects, ownedItem, Player, 0, type: ItemType.Misc);
        SeedBag(objects, bag, 1);
        var puts = new List<(uint Item, uint Container, int Placement)>();
        var messages = new List<string>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { },
            systemMessage: messages.Add);
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            sendPutItemInContainer: (item, container, placement) =>
                puts.Add((item, container, placement)),
            itemInteraction: interaction);

        Assert.True(interaction.PlaceWorldItemInBackpack(pendingLoot));
        inventory.HandleDropRelease(
            containers,
            containers.GetItem(0)!,
            Payload(ownedItem));

        Assert.Empty(puts);
        Assert.Equal(
            new[] { ItemInteractionController.InventoryRequestBusyMessage },
            messages);
    }

    [Fact]
    public void PendingPickupRejectsCompatibleGroundMergeBeforeWireDispatch()
    {
        const uint chest = 0x70000071u;
        const uint pendingLoot = 0x70000072u;
        const uint sourceStack = 0x70000073u;
        const uint targetStack = 0x70000074u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, pendingLoot, chest, slot: 0, type: ItemType.Misc);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = sourceStack,
            WeenieClassId = 0x1234u,
            StackSize = 10,
            StackSizeMax = 100,
        });
        objects.MoveItem(sourceStack, chest, 1);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = targetStack,
            WeenieClassId = 0x1234u,
            StackSize = 50,
            StackSizeMax = 100,
        });
        objects.MoveItem(targetStack, Player, 0);
        var merges = new List<(uint Source, uint Target, uint Amount)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { });
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            sendStackableMerge: (source, target, amount) =>
                merges.Add((source, target, amount)),
            itemInteraction: interaction);
        var sourceCell = new UiItemSlot { SourceKind = ItemDragSource.Ground };
        sourceCell.SetItem(sourceStack, 0u);

        Assert.True(interaction.PlaceWorldItemInBackpack(pendingLoot));
        UiItemSlot targetCell = Enumerable.Range(0, grid.GetNumUIItems())
            .Select(i => grid.GetItem(i)!)
            .Single(cell => cell.ItemId == targetStack);
        inventory.HandleDropRelease(
            grid,
            targetCell,
            new ItemDragPayload(sourceStack, ItemDragSource.Ground, 0, sourceCell));

        Assert.Empty(merges);
        Assert.True(interaction.TryGetPendingBackpackPlacement(pendingLoot, out _));
    }

    [Fact]
    public void PendingPickupRejectsPartialGroundSplitBeforeWireDispatch()
    {
        const uint chest = 0x70000081u;
        const uint pendingLoot = 0x70000082u;
        const uint sourceStack = 0x70000083u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, pendingLoot, chest, slot: 0, type: ItemType.Misc);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = sourceStack,
            StackSize = 10,
            StackSizeMax = 100,
        });
        objects.MoveItem(sourceStack, chest, 1);
        var selection = new SelectionState();
        selection.Select(sourceStack, SelectionChangeSource.Inventory);
        var splitQuantity = new StackSplitQuantityState();
        splitQuantity.Reset(10u);
        splitQuantity.SetValue(1u);
        var splits = new List<(uint Item, uint Container, uint Placement, uint Amount)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { });
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: selection,
            datFont: null,
            sendStackableSplitToContainer: (item, container, placement, amount) =>
                splits.Add((item, container, placement, amount)),
            itemInteraction: interaction,
            stackSplitQuantity: splitQuantity);
        var sourceCell = new UiItemSlot { SourceKind = ItemDragSource.Ground };
        sourceCell.SetItem(sourceStack, 0u);

        Assert.True(interaction.PlaceWorldItemInBackpack(pendingLoot));
        inventory.HandleDropRelease(
            grid,
            grid.GetItem(5)!,
            new ItemDragPayload(sourceStack, ItemDragSource.Ground, 0, sourceCell));

        Assert.Empty(splits);
        Assert.True(interaction.TryGetPendingBackpackPlacement(pendingLoot, out _));
    }

    [Fact]
    public void PendingPickupRejectsOwnedInventoryMoveBeforeOptimisticMutation()
    {
        const uint chest = 0x70000091u;
        const uint pendingLoot = 0x70000092u;
        const uint ownedItem = 0x70000093u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, pendingLoot, chest, slot: 0, type: ItemType.Misc);
        SeedContained(objects, ownedItem, Player, slot: 0, type: ItemType.Misc);
        var puts = new List<(uint Item, uint Container, int Placement)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { });
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            sendPutItemInContainer: (item, container, placement) =>
                puts.Add((item, container, placement)),
            itemInteraction: interaction);

        Assert.True(interaction.PlaceWorldItemInBackpack(pendingLoot));
        inventory.HandleDropRelease(grid, grid.GetItem(5)!, Payload(ownedItem));

        Assert.Empty(puts);
        Assert.Equal(Player, objects.Get(ownedItem)!.ContainerId);
        Assert.Equal(0, objects.Get(ownedItem)!.ContainerSlot);
        Assert.True(interaction.TryGetPendingBackpackPlacement(pendingLoot, out _));
    }

    [Fact]
    public void PendingLootRemovalImmediatelyWithdrawsTheProjection()
    {
        const uint chest = 0x70000051u;
        const uint loot = 0x70000052u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, loot, chest, slot: 0, type: ItemType.Misc);
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { });
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            itemInteraction: interaction);

        Assert.True(interaction.PlaceWorldItemInBackpack(loot));
        Assert.Equal(loot, grid.GetItem(0)!.ItemId);
        Assert.True(grid.GetItem(0)!.WaitingVisual);

        Assert.True(objects.Remove(loot));

        Assert.Equal(0u, grid.GetItem(0)!.ItemId);
        Assert.False(interaction.TryGetPendingBackpackPlacement(loot, out _));
    }

    [Fact]
    public void PendingLootGenerationReplacementDoesNotLeaveTheOldProjection()
    {
        const uint chest = 0x70000061u;
        const uint loot = 0x70000062u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, loot, chest, slot: 0, type: ItemType.Misc);
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { });
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            itemInteraction: interaction);

        Assert.True(interaction.PlaceWorldItemInBackpack(loot));
        Assert.Equal(loot, grid.GetItem(0)!.ItemId);

        objects.ReplaceGeneration(WorldReplacement(loot), generation: 2);

        Assert.Equal(0u, grid.GetItem(0)!.ItemId);
        Assert.False(interaction.TryGetPendingBackpackPlacement(loot, out _));
    }

    [Fact]
    public void LootDrop_ServerFailureRemovesOnlyThePendingProjection()
    {
        const uint chest = 0x70000001u;
        const uint loot = 0x70000002u;
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        SeedContained(objects, loot, chest, slot: 0, type: ItemType.Misc);
        var puts = new List<(uint item, uint container, int placement)>();
        using var ctrl = Bind(layout, objects, puts: puts);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Ground };
        source.SetItem(loot, 0u);
        var payload = new ItemDragPayload(loot, ItemDragSource.Ground, 0, source);

        ctrl.HandleDropRelease(grid, grid.GetItem(1)!, payload);
        Assert.Equal(loot, grid.GetItem(0)!.ItemId);

        objects.RejectMove(loot, weenieError: 0x29u);

        Assert.DoesNotContain(
            Enumerable.Range(0, grid.GetNumUIItems()).Select(i => grid.GetItem(i)!.ItemId),
            id => id == loot);
        Assert.Equal(chest, objects.Get(loot)!.ContainerId);
    }

    [Fact]
    public void Drop_fromElsewhereOntoEmptyGridCell_goesToTheTop()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);          // 1 loose item → first empty = slot 1
        var puts = new List<(uint, uint, int)>();
        var ctrl = Bind(layout, objects, puts: puts);

        objects.AddOrUpdate(new ClientObject { ObjectId = 0xFFFFu });
        var emptyCell = grid.GetItem(5)!;
        ((IItemListDragHandler)ctrl).HandleDropRelease(grid, emptyCell, Payload(0xFFFFu));

        Assert.Contains((0xFFFFu, Player, 0), puts);
    }

    [Fact]
    public void Drop_selectedPartialStack_splitsIntoContainer_withoutMovingOriginal()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xAu,
            StackSize = 10,
            StackSizeMax = 100,
        });
        objects.MoveItem(0xAu, Player, 0);
        SeedBag(objects, 0xCu, slot: 1);
        var selection = new SelectionState();
        selection.Select(0xAu, SelectionChangeSource.Inventory);
        var splitQuantity = new StackSplitQuantityState();
        splitQuantity.Reset(10u);
        splitQuantity.SetValue(2u);
        var splits = new List<(uint item, uint container, uint placement, uint amount)>();
        var puts = new List<(uint item, uint container, int placement)>();
        var ctrl = Bind(layout, objects, puts: puts, splits: splits,
            selection: selection, stackSplitQuantity: splitQuantity);

        ctrl.HandleDropRelease(grid, grid.GetItem(5)!, Payload(0xAu));

        Assert.Equal(new[] { (0xAu, Player, 1u, 2u) }, splits);
        Assert.Empty(puts);
        Assert.Equal(Player, objects.Get(0xAu)!.ContainerId);
        Assert.Equal(0, objects.Get(0xAu)!.ContainerSlot);
        Assert.Equal(10, objects.Get(0xAu)!.StackSize);
    }

    [Fact]
    public void Drop_unselectedStack_ignoresOtherSelectionSplitQuantity_andMovesWholeStack()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 0xAu, StackSize = 10 });
        objects.MoveItem(0xAu, Player, 0);
        objects.AddOrUpdate(new ClientObject { ObjectId = 0xBu, StackSize = 20 });
        objects.MoveItem(0xBu, Player, 1);
        var selection = new SelectionState();
        selection.Select(0xBu, SelectionChangeSource.Inventory);
        var splitQuantity = new StackSplitQuantityState();
        splitQuantity.Reset(20u);
        splitQuantity.SetValue(1u);
        var splits = new List<(uint item, uint container, uint placement, uint amount)>();
        var puts = new List<(uint item, uint container, int placement)>();
        var ctrl = Bind(layout, objects, puts: puts, splits: splits,
            selection: selection, stackSplitQuantity: splitQuantity);

        ctrl.HandleDropRelease(grid, grid.GetItem(4)!, Payload(0xAu));

        Assert.Empty(splits);
        Assert.Equal(new[] { (0xAu, Player, 2) }, puts);
    }

    [Fact]
    public void Drop_matchingStackOnOccupiedStack_mergesBeforeMoveAndBroadcastsToolbarNotice()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xA,
            WeenieClassId = 0x1234,
            StackSize = 40,
            StackSizeMax = 100,
        });
        objects.MoveItem(0xA, Player, 0);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xB,
            WeenieClassId = 0x1234,
            StackSize = 95,
            StackSizeMax = 100,
        });
        objects.MoveItem(0xB, Player, 1);
        var puts = new List<(uint item, uint container, int placement)>();
        var merges = new List<(uint source, uint target, uint amount)>();
        var notices = new List<(uint source, uint target)>();
        var selection = new SelectionState();
        var ctrl = Bind(layout, objects, puts: puts, merges: merges,
            mergeNotices: notices, selection: selection);

        ctrl.HandleDropRelease(grid, grid.GetItem(1)!, Payload(0xAu));

        Assert.Equal(new[] { (0xAu, 0xBu, 5u) }, merges);
        Assert.Equal(new[] { (0xAu, 0xBu) }, notices);
        Assert.Empty(puts);
        Assert.Equal(0xBu, selection.SelectedObjectId);
    }

    [Fact]
    public void Drop_selectedStack_usesSharedToolbarSplitQuantity()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xA, WeenieClassId = 0x1234, StackSize = 40, StackSizeMax = 100,
        });
        objects.MoveItem(0xA, Player, 0);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xB, WeenieClassId = 0x1234, StackSize = 50, StackSizeMax = 100,
        });
        objects.MoveItem(0xB, Player, 1);
        var selection = new SelectionState();
        selection.Select(0xAu, SelectionChangeSource.Inventory);
        var split = new StackSplitQuantityState();
        split.Reset(40u);
        split.SetValue(7u);
        var merges = new List<(uint source, uint target, uint amount)>();
        var ctrl = Bind(layout, objects, merges: merges, selection: selection,
            stackSplitQuantity: split);

        ctrl.HandleDropRelease(grid, grid.GetItem(1)!, Payload(0xAu));

        Assert.Equal(new[] { (0xAu, 0xBu, 7u) }, merges);
    }

    [Fact]
    public void Drop_differentStackType_fallsThroughToNormalInventoryInsert()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xA, WeenieClassId = 0x1111, StackSize = 10, StackSizeMax = 100,
        });
        objects.MoveItem(0xA, Player, 0);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xB, WeenieClassId = 0x2222, StackSize = 10, StackSizeMax = 100,
        });
        objects.MoveItem(0xB, Player, 1);
        var puts = new List<(uint item, uint container, int placement)>();
        var merges = new List<(uint source, uint target, uint amount)>();
        var ctrl = Bind(layout, objects, puts: puts, merges: merges);

        ctrl.HandleDropRelease(grid, grid.GetItem(1)!, Payload(0xAu));

        Assert.Empty(merges);
        Assert.Contains((0xAu, Player, 1), puts);
    }

    [Fact]
    public void Drop_onSideBagCell_movesIntoThatBag()
    {
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedBag(objects, 0xC, slot: 0, itemsCapacity: 24);
        var puts = new List<(uint, uint, int)>();
        var ctrl = Bind(layout, objects, puts: puts);

        objects.AddOrUpdate(new ClientObject { ObjectId = 0xFFFFu });
        var bagCell = containers.GetItem(0)!;                  // ItemId == 0xC (the bag)
        ((IItemListDragHandler)ctrl).HandleDropRelease(containers, bagCell, Payload(0xFFFFu));

        Assert.Contains((0xFFFFu, 0xCu, 0), puts);                // into the bag, append (placement 0)
        Assert.Equal(0u, objects.Get(0xFFFFu)!.ContainerId);
    }

    [Fact]
    public void OnDragOver_fullSideBag_acceptsBecauseTheDropFallsThrough_andGridAccepts()
    {
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedBag(objects, 0xC, slot: 0, itemsCapacity: 1);
        SeedContained(objects, 0xB0, 0xC, slot: 0);            // ...and already full
        objects.AddOrUpdate(new ClientObject { ObjectId = 0xFFFFu });
        var ctrl = (IItemListDragHandler)Bind(layout, objects);

        // Hover tests legality only; a full bag still accepts because the
        // drop falls through to a pack with room.
        Assert.Equal(ItemDragAcceptance.Accept,
            ctrl.OnDragOver(containers, containers.GetItem(0)!, Payload(0xFFFFu)));  // full bag → red
        Assert.Equal(ItemDragAcceptance.Accept,
            ctrl.OnDragOver(grid, grid.GetItem(0)!, Payload(0xFFFFu)));               // grid → green
    }

    [Fact]
    public void MainPackFullness_countsLooseItems_notSideBags_afterAFreeSlotAppears()
    {
        var (layout, _, _, top, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Type = ItemType.Creature,
            ItemsCapacity = 2,
        });
        SeedContained(objects, 0xA0u, Player, slot: 0);
        SeedContained(objects, 0xA1u, Player, slot: 1);
        SeedBag(objects, 0xC0u, slot: 0);
        SeedContained(objects, 0xB0u, 0xC0u, slot: 0);
        var controller = (IItemListDragHandler)Bind(layout, objects);
        UiItemSlot mainPack = top.GetItem(0)!;

        // The main pack is full, but the drop still has somewhere to go: the
        // item's own side bag takes it back, so the hover accepts.
        Assert.Equal(ItemDragAcceptance.Accept,
            controller.OnDragOver(top, mainPack, Payload(0xB0u)));

        Assert.True(objects.Remove(0xA1u));

        Assert.Equal(ItemDragAcceptance.Accept,
            controller.OnDragOver(top, top.GetItem(0)!, Payload(0xB0u)));
        Assert.Equal(0.5f, top.GetItem(0)!.CapacityFill);
    }

    [Fact]
    public void GroundPack_droppedOnTheContentsGrid_fallsThroughToThePackList()
    {
        const uint droppedPack = 0x700000C0u;
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Type = ItemType.Creature,
            ItemsCapacity = 102,
            ContainersCapacity = 7,
        });
        SeedBag(objects, 0x500000C1u, slot: 0);
        SeedBag(objects, 0x500000C2u, slot: 1);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = droppedPack,
            Name = "Dropped Pack",
            Type = ItemType.Container,
            ItemsCapacity = 24,
        });
        var puts = new List<(uint Item, uint Container, int Placement)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(
                new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => droppedPack,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { });
        using var controller = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            sendPutItemInContainer: (item, container, placement) =>
                puts.Add((item, container, placement)),
            itemInteraction: interaction);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Ground };
        source.SetItem(droppedPack, 0u);
        var payload = new ItemDragPayload(
            droppedPack,
            ItemDragSource.Ground,
            SourceSlot: 0,
            SourceCell: source);

        // Hover tests legality only, and the drop falls through to the
        // pack list when the player can carry another container.
        Assert.Equal(
            ItemDragAcceptance.Accept,
            controller.OnDragOver(grid, grid.GetItem(0)!, payload));
        controller.HandleDropRelease(grid, grid.GetItem(0)!, payload);

        Assert.Equal(new[] { (droppedPack, Player, 0) }, puts);
        Assert.True(interaction.TryGetPendingBackpackPlacement(droppedPack, out var pending));
        Assert.Equal(Player, pending.ContainerId);
        Assert.Equal(0, pending.Placement);
    }

    [Fact]
    public void GroundPack_emptyPackSlotAcceptsAndPicksUpAtThatSlot()
    {
        const uint droppedPack = 0x700000C0u;
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Type = ItemType.Creature,
            ItemsCapacity = 102,
            ContainersCapacity = 7,
        });
        SeedBag(objects, 0x500000C1u, slot: 0);
        SeedBag(objects, 0x500000C2u, slot: 1);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = droppedPack,
            Name = "Dropped Pack",
            Type = ItemType.Container,
            ItemsCapacity = 24,
        });
        var puts = new List<(uint Item, uint Container, int Placement)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(
                new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => droppedPack,
            backpackContainerId: () => Player,
            placeInBackpack: static (_, _, _) => { });
        using var controller = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            sendPutItemInContainer: (item, container, placement) =>
                puts.Add((item, container, placement)),
            itemInteraction: interaction);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Ground };
        source.SetItem(droppedPack, 0u);
        var payload = new ItemDragPayload(
            droppedPack,
            ItemDragSource.Ground,
            SourceSlot: 0,
            SourceCell: source);

        UiItemSlot emptyPackSlot = containers.GetItem(2)!;
        Assert.Equal(0u, emptyPackSlot.ItemId);
        Assert.Equal(
            ItemDragAcceptance.Accept,
            controller.OnDragOver(containers, emptyPackSlot, payload));
        controller.HandleDropRelease(containers, emptyPackSlot, payload);

        Assert.Equal(new[] { (droppedPack, Player, 0) }, puts);
        Assert.True(interaction.TryGetPendingBackpackPlacement(droppedPack, out var pending));
        Assert.Equal(Player, pending.ContainerId);
        Assert.Equal(0, pending.Placement);
    }

    [Fact]
    public void OnDragLift_selectsItem_butKeepsItUntilServerConfirms()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);
        var selection = new SelectionState();
        var ctrl = (IItemListDragHandler)Bind(layout, objects, selection: selection);
        ((IItemListDragHandler)ctrl).OnDragLift(grid, grid.GetItem(0)!, Payload(0xAu));

        Assert.Equal(0xAu, selection.SelectedObjectId);
        Assert.True(grid.GetItem(0)!.Selected);
        Assert.Equal(Player, objects.Get(0xAu)!.ContainerId);   // NOT removed on lift (unlike the toolbar)
    }

    [Fact]
    public void OnDragOver_closedBag_unknownCount_advisoryAccepts()
    {
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedBag(objects, 0xC, slot: 0, itemsCapacity: 24);
        objects.AddOrUpdate(new ClientObject { ObjectId = 0xFFFFu });
        var ctrl = (IItemListDragHandler)Bind(layout, objects);

        Assert.Equal(ItemDragAcceptance.Accept,
            ctrl.OnDragOver(containers, containers.GetItem(0)!, Payload(0xFFFFu)));
    }

    [Fact]
    public void ShortcutAliasDropOnInventory_removesOnlyAlias_andNeverMovesEquippedItem()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        const uint helmet = 0xD00Du;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = helmet,
            Type = ItemType.Armor,
            ValidLocations = EquipMask.HeadWear,
        });
        objects.MoveItem(helmet, Player, newSlot: 0, newEquipLocation: EquipMask.HeadWear);
        var puts = new List<(uint item, uint container, int placement)>();
        var ctrl = (IItemListDragHandler)Bind(layout, objects, puts: puts);
        var payload = new ItemDragPayload(
            helmet,
            ItemDragSource.ShortcutBar,
            SourceSlot: 3,
            SourceCell: new UiItemSlot());

        Assert.Equal(ItemDragAcceptance.None,
            ctrl.OnDragOver(grid, grid.GetItem(0)!, payload));
        ctrl.HandleDropRelease(grid, grid.GetItem(0)!, payload);

        Assert.Empty(puts);
        Assert.Equal(Player, objects.Get(helmet)!.ContainerId);
        Assert.Equal(EquipMask.HeadWear, objects.Get(helmet)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void Drop_thenServerReject_keepsCanonicalPlacement()
    {
        var (layout, _, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 3);          // item starts in the main pack at slot 3
        SeedBag(objects, 0xC, slot: 0, itemsCapacity: 24);
        var ctrl = Bind(layout, objects);

        ((IItemListDragHandler)ctrl).HandleDropRelease(containers, containers.GetItem(0)!, Payload(0xAu));
        Assert.Equal(Player, objects.Get(0xAu)!.ContainerId);
        Assert.False(objects.RejectMove(0xAu, 0x426u));
        Assert.Equal(Player, objects.Get(0xAu)!.ContainerId);
        Assert.Equal(3, objects.Get(0xAu)!.ContainerSlot);
    }

    private static string CaptionText(UiElement host)
    {
        foreach (var c in host.Children)
            if (c is UiText t)
            {
                var lines = t.LinesProvider();
                if (lines.Count > 0) return lines[0].Text;
            }
        return "";
    }

    // ── OpenAC #5: a press must survive the appraisal-driven rebuild ─────────

    private sealed class ExaminingInventoryFixture : IDisposable
    {
        public readonly UiRoot Root = new() { Width = 800, Height = 600 };
        public readonly UiItemList Grid;
        public readonly ClientObjectTable Objects = new();
        public readonly SelectionState Selection = new();
        public readonly List<uint> Appraisals = [];
        public readonly ItemInteractionController Interaction;
        public readonly InventoryController Inventory;
        public readonly AppraisalUiController Appraisal;

        public ExaminingInventoryFixture()
        {
            var (layout, grid, _, _, _, _, _, _) = BuildLayout();
            // The shared layout stacks every widget at (0,0); move the grid
            // clear of its front siblings so a press lands on a cell.
            grid.Left = 100f;
            grid.Top = 100f;
            Grid = grid;
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Type = ItemType.Creature,
                ItemsCapacity = 102,
            });
            SeedContained(Objects, 0xAu, Player, slot: 0);
            Interaction = new ItemInteractionController(
                Objects,
                new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(Objects)),
                new InteractionState(),
                playerGuid: () => Player,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: null,
                sendExamine: Appraisals.Add,
                nowMs: () => 1_000);
            Appraisal = AppraisalUiController.Bind(
                FixtureLoader.LoadExamination(),
                Objects,
                Interaction,
                Selection,
                new CombatState(),
                new Spellbook(),
                () => "Tester",
                (_, _) => { },
                _ => { },
                () => { },
                () => { })!;
            Appraisal.OnShown();
            Inventory = InventoryController.Bind(
                layout,
                Objects,
                () => Player,
                iconIds: static (_, _, _, _, _) => 0x99u,
                strength: () => 100,
                selection: Selection,
                datFont: null,
                itemInteraction: Interaction);
            Root.AddChild(layout.Root);
        }

        public (int x, int y) CentreOf(UiItemSlot cell)
        {
            var sp = cell.ScreenPosition;
            return ((int)sp.X + 8, (int)sp.Y + 8);
        }

        public void Dispose()
        {
            Appraisal.Dispose();
            Inventory.Dispose();
            Interaction.Dispose();
        }
    }

    [Fact]
    public void PressWithExaminationWindowOpen_keepsCaptureAndPromotesToDrag()
    {
        using var f = new ExaminingInventoryFixture();
        UiItemSlot cell = f.Grid.GetItem(0)!;
        Assert.Equal(0xAu, cell.ItemId);
        var (x, y) = f.CentreOf(cell);

        Assert.Same(cell, f.Root.Pick(x, y));
        f.Root.OnMouseDown(UiMouseButton.Left, x, y);

        // The press selected the item and the open window examined it —
        // the exact chain the reporter had running.
        Assert.Equal(0xAu, f.Selection.SelectedObjectId);
        Assert.Equal(new uint[] { 0xAu }, f.Appraisals);
        Assert.Equal(1, f.Interaction.BusyCount);
        Assert.Same(cell, f.Grid.GetItem(0));
        Assert.Same(cell, f.Root.Captured);

        f.Root.OnMouseMove(x + 10, y);

        Assert.Same(cell, f.Root.DragSource);
        Assert.Equal(0xAu, Assert.IsType<ItemDragPayload>(f.Root.DragPayload).ObjId);
    }

    [Fact]
    public void DoubleClickWithExaminationWindowOpen_reachesThePressedSlot()
    {
        using var f = new ExaminingInventoryFixture();
        UiItemSlot cell = f.Grid.GetItem(0)!;
        int doubleClicks = 0;
        Action? inner = cell.DoubleClicked;
        cell.DoubleClicked = () => { doubleClicks++; inner?.Invoke(); };
        var (x, y) = f.CentreOf(cell);

        f.Root.Tick(0d, 1_000);
        f.Root.OnMouseDown(UiMouseButton.Left, x, y);
        f.Root.OnMouseUp(UiMouseButton.Left, x, y);
        f.Root.Tick(0d, 1_100);
        f.Root.OnMouseDown(UiMouseButton.Left, x, y);
        f.Root.OnMouseUp(UiMouseButton.Left, x, y);

        Assert.Same(cell, f.Grid.GetItem(0));
        Assert.Equal(1, doubleClicks);
    }

    // ── Pack choice when the named pack is full (the client picks a pack with room) ──

    private static void SeedPlayerAndPacks(ClientObjectTable objects, int mainCapacity)
    {
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Name = "Tester",
            Type = ItemType.Creature,
            ItemsCapacity = mainCapacity,
            ContainersCapacity = 7,
        });
    }

    private static void SeedPack(ClientObjectTable objects, uint guid, int slot, int capacity)
    {
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = guid,
            Name = $"Pack {guid:X}",
            Type = ItemType.Container,
            ItemsCapacity = capacity,
        });
        objects.MoveItem(guid, Player, slot);
    }

    [Fact]
    public void Drop_onto_full_main_pack_goes_to_the_first_side_pack_with_room()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedPlayerAndPacks(objects, mainCapacity: 1);
        SeedContained(objects, 0xA, Player, slot: 0, type: ItemType.Misc);      // main pack full
        SeedPack(objects, 0xC, slot: 1, capacity: 2);                          // room here
        SeedPack(objects, 0xD, slot: 2, capacity: 2);
        SeedContained(objects, 0xB, 0xD, slot: 0, type: ItemType.Misc);        // the dragged item
        var puts = new List<(uint item, uint container, int placement)>();
        var messages = new List<string>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            systemMessage: messages.Add,
            sendPutItemInContainer: (i, c, p) => puts.Add((i, c, p)));
        using var controller = Bind(layout, objects, puts: puts, itemInteraction: interaction);
        UiItemSlot cell = grid.GetItem(0)!;

        Assert.Equal(
            ItemDragAcceptance.Accept,
            controller.OnDragOver(grid, cell, new ItemDragPayload(0xB, ItemDragSource.Inventory, 0, cell)));
        controller.HandleDropRelease(grid, cell, new ItemDragPayload(0xB, ItemDragSource.Inventory, 0, cell));

        Assert.Equal(new[] { (0xBu, 0xCu, 0) }, puts);
        Assert.Empty(messages);
    }

    [Fact]
    public void Drop_when_every_pack_is_full_reports_the_backpack_full_and_sends_nothing()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedPlayerAndPacks(objects, mainCapacity: 1);
        SeedContained(objects, 0xA, Player, slot: 0, type: ItemType.Misc);
        SeedPack(objects, 0xC, slot: 1, capacity: 1);
        SeedContained(objects, 0xE, 0xC, slot: 0, type: ItemType.Misc);        // side pack full
        const uint chest = 0x70000001u;
        objects.AddOrUpdate(new ClientObject { ObjectId = chest, Type = ItemType.Container, ItemsCapacity = 10 });
        SeedContained(objects, 0xB, chest, slot: 0, type: ItemType.Misc);      // dragged from a chest
        var puts = new List<(uint item, uint container, int placement)>();
        var messages = new List<string>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            groundObjectId: () => chest,
            systemMessage: messages.Add,
            sendPutItemInContainer: (i, c, p) => puts.Add((i, c, p)));
        using var controller = Bind(layout, objects, puts: puts, itemInteraction: interaction);
        UiItemSlot cell = grid.GetItem(0)!;

        Assert.Equal(
            ItemDragAcceptance.Reject,
            controller.OnDragOver(grid, cell, new ItemDragPayload(0xB, ItemDragSource.Ground, 0, cell)));
        controller.HandleDropRelease(grid, cell, new ItemDragPayload(0xB, ItemDragSource.Ground, 0, cell));

        Assert.Empty(puts);
        Assert.Equal(new[] { "Backpack is completely full!" }, messages);
    }

    [Fact]
    public void Pickup_with_a_full_main_pack_goes_to_the_first_side_pack_with_room()
    {
        var (layout, _, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedPlayerAndPacks(objects, mainCapacity: 1);
        SeedContained(objects, 0xA, Player, slot: 0, type: ItemType.Misc);      // main pack full
        SeedPack(objects, 0xC, slot: 1, capacity: 2);
        const uint chest = 0x70000001u;
        const uint loot = 0x70000002u;
        objects.AddOrUpdate(new ClientObject { ObjectId = chest, Type = ItemType.Container, ItemsCapacity = 10 });
        SeedContained(objects, loot, chest, slot: 0, type: ItemType.Misc);
        var pickups = new List<(uint item, uint container, int placement)>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            nowMs: () => 1_000,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: (item, container, placement) =>
                pickups.Add((item, container, placement)));
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            itemInteraction: interaction);

        Assert.True(interaction.ActivateItem(loot));

        Assert.Equal(new[] { (loot, 0xCu, 0) }, pickups);
    }

    [Fact]
    public void Pickup_when_every_pack_is_full_reports_the_backpack_full_and_sends_nothing()
    {
        var (layout, _, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedPlayerAndPacks(objects, mainCapacity: 1);
        SeedContained(objects, 0xA, Player, slot: 0, type: ItemType.Misc);
        SeedPack(objects, 0xC, slot: 1, capacity: 1);
        SeedContained(objects, 0xE, 0xC, slot: 0, type: ItemType.Misc);
        const uint chest = 0x70000001u;
        const uint loot = 0x70000002u;
        objects.AddOrUpdate(new ClientObject { ObjectId = chest, Type = ItemType.Container, ItemsCapacity = 10 });
        SeedContained(objects, loot, chest, slot: 0, type: ItemType.Misc);
        var pickups = new List<(uint item, uint container, int placement)>();
        var messages = new List<string>();
        using var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            nowMs: () => 1_000,
            groundObjectId: () => chest,
            backpackContainerId: () => Player,
            placeInBackpack: (item, container, placement) =>
                pickups.Add((item, container, placement)),
            systemMessage: messages.Add);
        using var inventory = InventoryController.Bind(
            layout,
            objects,
            () => Player,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: () => 100,
            selection: new SelectionState(),
            datFont: null,
            itemInteraction: interaction);

        Assert.True(interaction.ActivateItem(loot));

        Assert.Empty(pickups);
        Assert.Equal(new[] { "Backpack is completely full!" }, messages);
    }

    // ── #32: the sale and trade markers and the shortcut slot number live on
    //        the object, so the inventory cells show them.

    [Fact]
    public void Populate_marksCellsStagedForSaleOrTrade()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);
        SeedContained(objects, 0xB, Player, slot: 1);
        objects.Get(0xA)!.SellState = 1;
        objects.Get(0xB)!.TradeState = 1;

        Bind(layout, objects);

        UiItemSlot forSale = grid.GetItem(0)!;
        Assert.True(forSale.ShowSellOverlay);
        Assert.Equal(ItemCellOverlaySprites.Sell, forSale.SellOverlaySprite);
        Assert.False(forSale.ShowTradeOverlay);
        UiItemSlot forTrade = grid.GetItem(1)!;
        Assert.True(forTrade.ShowTradeOverlay);
        Assert.Equal(ItemCellOverlaySprites.Trade, forTrade.TradeOverlaySprite);
        Assert.False(forTrade.ShowSellOverlay);

        objects.Get(0xA)!.SellState = 0;
        objects.NotifyObjectUpdated(0xA);
        Assert.False(grid.GetItem(0)!.ShowSellOverlay);
    }

    [Fact]
    public void Populate_stampsTheShortcutSlotNumberOnTheItemsCell()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);
        SeedContained(objects, 0xB, Player, slot: 1);
        using var shortcuts = new ShortcutStore();
        shortcuts.Load([new ShortcutEntry(2, 0xA, 0u)]);
        uint[] regular = [0x101u, 0x102u, 0x103u, 0x104u, 0x105u, 0x106u, 0x107u, 0x108u, 0x109u];
        uint[] ghosted = [0x201u, 0x202u, 0x203u, 0x204u, 0x205u, 0x206u, 0x207u, 0x208u, 0x209u];
        var digits = new UiShortcutDigitGraphics(regular, ghosted, EmptyDigits: null);
        var combat = new CombatState();

        Bind(layout, objects, shortcuts: shortcuts, shortcutDigits: digits, combat: combat);

        Assert.Equal(2, grid.GetItem(0)!.ShortcutNum);
        Assert.False(grid.GetItem(0)!.ShortcutGhosted);
        Assert.Same(regular, grid.GetItem(0)!.RegularDigits);
        Assert.Equal(-1, grid.GetItem(1)!.ShortcutNum);

        // Rebinding the shortcut moves the number without a rebuild.
        shortcuts.Load([new ShortcutEntry(4, 0xB, 0u)]);
        Assert.Equal(-1, grid.GetItem(0)!.ShortcutNum);
        Assert.Equal(4, grid.GetItem(1)!.ShortcutNum);

        // Magic mode ghosts the digits, the way the shortcut bar does.
        combat.SetCombatMode(CombatMode.Magic);
        Assert.True(grid.GetItem(1)!.ShortcutGhosted);
        Assert.Same(ghosted, grid.GetItem(1)!.GhostedDigits);
    }

    [Fact]
    public void Populate_bottomRowShortcutsCarryNoNumber()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xA, Player, slot: 0);
        using var shortcuts = new ShortcutStore();
        shortcuts.Load([new ShortcutEntry(9, 0xA, 0u)]);

        Bind(layout, objects, shortcuts: shortcuts,
            shortcutDigits: new UiShortcutDigitGraphics([0x1u], [0x2u], null));

        Assert.Equal(-1, grid.GetItem(0)!.ShortcutNum);
    }

    // ── OpenAC #34: a move between containers lands at the top; only a
    //    reorder inside the same list keeps the hovered slot.

    [Fact]
    public void SidePackItem_droppedOnTheMainGrid_goesToTheTop()
    {
        var (layout, grid, _, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        SeedContained(objects, 0xBu, Player, slot: 1);
        SeedBag(objects, 0xC0u, slot: 0);
        SeedContained(objects, 0xC1u, 0xC0u, slot: 0, type: ItemType.Misc);
        var puts = new List<(uint item, uint container, int placement)>();
        using var ctrl = Bind(layout, objects, puts: puts);

        ctrl.HandleDropRelease(grid, grid.GetItem(1)!, Payload(0xC1u));

        Assert.Equal(new[] { (0xC1u, Player, 0) }, puts);
    }

    [Fact]
    public void MainGridItem_droppedOnAPackIcon_goesToThatPacksTop()
    {
        var (layout, grid, containers, _, _, _, _, _) = BuildLayout();
        var objects = new ClientObjectTable();
        SeedContained(objects, 0xAu, Player, slot: 0);
        SeedBag(objects, 0xC0u, slot: 0);
        SeedContained(objects, 0xC1u, 0xC0u, slot: 0, type: ItemType.Misc);
        SeedContained(objects, 0xC2u, 0xC0u, slot: 1, type: ItemType.Misc);
        var puts = new List<(uint item, uint container, int placement)>();
        using var ctrl = Bind(layout, objects, puts: puts);

        ctrl.HandleDropRelease(containers, containers.GetItem(0)!, Payload(0xAu));

        Assert.Equal(new[] { (0xAu, 0xC0u, 0) }, puts);
    }
}
