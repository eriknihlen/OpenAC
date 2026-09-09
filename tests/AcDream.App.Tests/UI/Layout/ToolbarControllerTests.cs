using System;
using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public class ToolbarControllerTests
{
    private static readonly uint[] Row1 =
        { 0x100001A7,0x100001A8,0x100001A9,0x100001AA,0x100001AB,0x100001AC,0x100001AD,0x100001AE,0x100001AF };
    private static readonly uint[] Row2 =
        { 0x100006B7,0x100006B8,0x100006B9,0x100006BA,0x100006BB,0x100006BC,0x100006BD,0x100006BE,0x100006BF };

    private static readonly uint[] CombatIds = { 0x10000192u, 0x10000193u, 0x10000194u, 0x10000195u };
    private const uint CharacterButtonId = 0x10000199u;
    private const uint InventoryButtonId = 0x100001B1u;
    private const uint UseButtonId = 0x1000019Du;
    private const uint ExamineButtonId = 0x100001A5u;
    private static readonly (uint ElementId, uint PanelId)[] PanelButtons =
    {
        (0x10000197u, 12u),
        (0x10000198u, 13u),
        (CharacterButtonId, RetailPanelCatalog.Character),
        (0x1000055Au, 25u),
        (0x1000019Au, 16u),
        (0x1000019Bu, 10u),
        (InventoryButtonId, RetailPanelCatalog.Inventory),
    };

    private static ShortcutStore Store(
        IEnumerable<ShortcutEntry> shortcuts)
    {
        var store = new ShortcutStore();
        store.Load(shortcuts);
        return store;
    }

    private static (ImportedLayout layout, Dictionary<uint, UiItemList> slots,
                    Dictionary<uint, UiElement> indicators) FakeToolbar()
    {
        var dict = new Dictionary<uint, UiElement>();
        var slots = new Dictionary<uint, UiItemList>();
        var indicators = new Dictionary<uint, UiElement>();
        var root = new UiPanel();
        foreach (var id in Row1) AddSlot(id);
        foreach (var id in Row2) AddSlot(id);
        foreach (var id in CombatIds)
        {
            AddButton(id);
            indicators[id] = dict[id];
        }
        foreach (var (elementId, panelId) in PanelButtons)
            AddButton(elementId, panelId);
        AddButton(UseButtonId);
        AddButton(ExamineButtonId);
        return (new ImportedLayout(root, dict), slots, indicators);

        void AddSlot(uint id)
        {
            var list = new UiItemList(_ => (0u, 0, 0)) { Width = 32, Height = 32 };
            dict[id] = list; slots[id] = list; root.AddChild(list);
        }

        void AddButton(uint id, uint? panelId = null)
        {
            var info = new ElementInfo
            {
                Id = id,
                Type = 1,
                Width = 32,
                Height = 32,
                DefaultStateName = "Normal",
            };
            info.StateMedia["Normal"] = (0x1u, 1);
            info.StateMedia["Highlight"] = (0x2u, 1);
            info.StateMedia["Ghosted"] = (0x3u, 1);
            if (panelId is { } value)
            {
                info.States[UiStateInfo.DirectStateId] = new UiStateInfo
                {
                    Id = UiStateInfo.DirectStateId,
                    Properties = new UiPropertyBag
                    {
                        Values =
                        {
                            [0x10000029u] = new UiPropertyValue
                            {
                                Kind = UiPropertyKind.Enum,
                                UnsignedValue = value,
                            },
                        },
                    },
                };
            }
            var button = new UiButton(info, _ => (0u, 0, 0)) { Width = 32, Height = 32 };
            dict[id] = button;
            root.AddChild(button);
        }
    }

    [Fact]
    public void Populate_bindsShortcutToCorrectSlot()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, WeenieClassId = 1u, IconId = 0x06001234u });
        var shortcuts = new List<ShortcutEntry>
        { new(Index: 0, ObjectId: 0x5001u, SpellId: 0) };

        ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0x77u, useItem: _ => { });

        Assert.Equal(0x5001u, slots[Row1[0]].Cell.ItemId);
        Assert.Equal(0x77u,   slots[Row1[0]].Cell.IconTexture);
        Assert.Equal(0u,      slots[Row1[1]].Cell.ItemId); // others empty
    }

    [Fact]
    public void SessionTableClearRemovesResolvedShortcutPresentation()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x5001u,
            WeenieClassId = 1u,
            IconId = 0x06001234u,
        });
        var shortcuts = new List<ShortcutEntry>
        {
            new(Index: 0, ObjectId: 0x5001u, SpellId: 0),
        };
        ToolbarController.Bind(
            layout,
            repo,
            Store(shortcuts),
            iconIds: (_, _, _, _, _) => 0x77u,
            useItem: _ => { });
        Assert.Equal(0x5001u, slots[Row1[0]].Cell.ItemId);

        repo.Clear();

        Assert.Equal(0u, slots[Row1[0]].Cell.ItemId);
    }

    [Fact]
    public void DeferredRebind_whenItemArrivesLate()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable(); // item NOT present yet
        var shortcuts = new List<ShortcutEntry>
        { new(Index: 2, ObjectId: 0x5002u, SpellId: 0) };

        ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0x88u, useItem: _ => { });
        Assert.Equal(0u, slots[Row1[2]].Cell.ItemId); // not bound yet

        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5002u, WeenieClassId = 1u, IconId = 0x06005678u });

        Assert.Equal(0x5002u, slots[Row1[2]].Cell.ItemId); // rebound on ItemAdded
    }

    [Fact]
    public void Dispose_UnsubscribesDeferredRebind_AndIsIdempotent()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        var shortcuts = new List<ShortcutEntry>
        { new(Index: 2, ObjectId: 0x5002u, SpellId: 0) };
        var controller = ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0x88u, useItem: _ => { });

        controller.Dispose();
        controller.Dispose();
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x5002u,
            WeenieClassId = 1u,
            IconId = 0x06005678u,
        });

        Assert.Equal(0u, slots[Row1[2]].Cell.ItemId);
    }

    [Fact]
    public void Click_emitsUseForBoundItem()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, WeenieClassId = 1u, IconId = 0x06001234u });
        var shortcuts = new List<ShortcutEntry>
        { new(Index: 0, ObjectId: 0x5001u, SpellId: 0) };
        uint used = 0;
        var selection = new SelectionState();

        ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0x77u,
            useItem: g => used = g,
            selection: selection);
        UiItemSlot cell = slots[Row1[0]].Cell;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.MouseDown));

        Assert.Equal(0x5001u, selection.SelectedObjectId);
        Assert.Equal(0u, used);
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.Click));

        Assert.Equal(0x5001u, used);
    }

    [Fact]
    public void PanelButtons_bindByDatPanelId_andGhostUnavailablePanels()
    {
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        var toggles = new List<uint>();

        var ctrl = ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { });
        ctrl.BindPanelButtons(
            panelId => panelId is RetailPanelCatalog.Inventory or RetailPanelCatalog.Character,
            toggles.Add);

        ((UiButton)layout.FindElement(InventoryButtonId)!).OnEvent(new UiEvent(0u, null, UiEventType.Click));
        ((UiButton)layout.FindElement(CharacterButtonId)!).OnEvent(new UiEvent(0u, null, UiEventType.Click));

        Assert.Equal(new[] { RetailPanelCatalog.Inventory, RetailPanelCatalog.Character }, toggles);
        foreach (var (elementId, panelId) in PanelButtons)
        {
            var button = (UiButton)layout.FindElement(elementId)!;
            Assert.Equal(
                panelId is RetailPanelCatalog.Inventory or RetailPanelCatalog.Character,
                button.Enabled);
        }
    }

    [Fact]
    public void InventoryButton_whenTargetModeActive_targetsPlayerInsteadOfTogglingInventory()
    {
        const uint player = 0x50000001u;
        const uint pack = 0x50000010u;
        const uint kit = 0x50000A01u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = pack, Type = ItemType.Container, ItemsCapacity = 24 });
        repo.MoveItem(pack, player, 0);
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = kit, Type = ItemType.Misc,
            Useability = 0x00220008u,               // USEABLE_SOURCE_CONTAINED_TARGET_REMOTE_OR_SELF
            TargetType = (uint)ItemType.Creature,
        });
        repo.MoveItem(kit, pack, 0);
        var useWithTarget = new List<(uint Source, uint Target)>();
        var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            playerGuid: () => player,
            sendUse: null,
            sendUseWithTarget: (source, target) => useWithTarget.Add((source, target)),
            sendWield: null,
            sendDrop: null,
            nowMs: () => 1_000);
        int inventoryClicks = 0;

        var ctrl = ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u,
            useItem: _ => { },
            itemInteraction: interaction);
        ctrl.BindPanelButtons(
            panelId => panelId == RetailPanelCatalog.Inventory,
            _ => inventoryClicks++);
        interaction.ActivateItem(kit);

        ((UiButton)layout.FindElement(InventoryButtonId)!).OnEvent(new UiEvent(0u, null, UiEventType.Click));

        Assert.Equal(0, inventoryClicks);
        Assert.Equal(new[] { (kit, player) }, useWithTarget);
        Assert.False(interaction.IsTargetModeActive);
    }

    [Fact]
    public void InventoryButton_freshItemDrop_acceptsAndPlacesInPlayerBackpack()
    {
        const uint player = 0x5000u;
        const uint item = 0x5001u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = item, Type = ItemType.Misc });
        var puts = new List<(uint Item, uint Container, int Placement)>();
        ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { },
            playerGuid: () => player,
            sendPutItemInContainer: (i, c, p) => puts.Add((i, c, p)));
        var button = (UiButton)layout.FindElement(InventoryButtonId)!;
        var payload = new ItemDragPayload(item, ItemDragSource.Inventory, 12, new UiItemSlot());

        Assert.True(button.OnEvent(new UiEvent(0u, button, UiEventType.DragEnter, Payload: payload)));
        Assert.Equal(ItemDragAcceptance.Accept, button.ItemDragAcceptanceForTest);
        Assert.Equal(0x060011F7u, button.ItemDragAcceptSprite);
        Assert.True(button.OnEvent(new UiEvent(0u, button, UiEventType.DropReleased, Payload: payload)));

        Assert.Equal(ItemDragAcceptance.None, button.ItemDragAcceptanceForTest);
        Assert.Equal(new[] { (item, player, 0) }, puts);
    }

    [Fact]
    public void InventoryButton_unownedGroundDropUsesDirectAttemptWithoutDestinationProjection()
    {
        const uint player = 0x5000u;
        const uint item = 0x70005001u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = item, Name = "Loot", Type = ItemType.Misc });
        var placements = new List<(uint Item, uint Container, int Placement)>();
        var directPuts = new List<(uint Item, uint Container, int Placement)>();
        using var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            playerGuid: () => player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            placeInBackpack: (i, c, p) => placements.Add((i, c, p)));
        ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: static (_, _, _, _, _) => 0u,
            useItem: static _ => { },
            itemInteraction: interaction,
            playerGuid: () => player,
            sendPutItemInContainer: (i, c, p) => directPuts.Add((i, c, p)));
        var button = (UiButton)layout.FindElement(InventoryButtonId)!;
        var payload = new ItemDragPayload(item, ItemDragSource.Ground, 0, new UiItemSlot());

        Assert.True(button.OnEvent(new UiEvent(0u, button, UiEventType.DragEnter, Payload: payload)));
        Assert.True(button.OnEvent(new UiEvent(0u, button, UiEventType.DropReleased, Payload: payload)));

        Assert.Empty(placements);
        Assert.Equal(new[] { (item, player, 0) }, directPuts);
        Assert.False(interaction.TryGetPendingBackpackPlacement(item, out _));
        Assert.True(interaction.TryGetPendingInventoryRequest(out var request));
        Assert.Equal(item, request.ItemId);
    }

    [Fact]
    public void InventoryButton_ownedDropPublishesDestinationAndGloballyRejectsSecondRequest()
    {
        const uint player = 0x5000u;
        const uint first = 0x70005002u;
        const uint second = 0x70005003u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        const uint pack = 0x5004u;
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Name = "Player", Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = pack, Name = "Pack", Type = ItemType.Container });
        repo.MoveItem(pack, player, 0);
        repo.AddOrUpdate(new ClientObject { ObjectId = first, Name = "First Loot", Type = ItemType.Misc });
        repo.AddOrUpdate(new ClientObject { ObjectId = second, Name = "Second Loot", Type = ItemType.Misc });
        repo.MoveItem(first, pack, 0);
        repo.MoveItem(second, pack, 1);
        var puts = new List<(uint Item, uint Container, int Placement)>();
        var messages = new List<string>();
        using var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            playerGuid: () => player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            systemMessage: messages.Add);
        ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: static (_, _, _, _, _) => 0u,
            useItem: static _ => { },
            itemInteraction: interaction,
            playerGuid: () => player,
            sendPutItemInContainer: (i, c, p) => puts.Add((i, c, p)));
        var button = (UiButton)layout.FindElement(InventoryButtonId)!;

        foreach (uint item in new[] { first, second })
        {
            var payload = new ItemDragPayload(item, ItemDragSource.Inventory, 0, new UiItemSlot());
            button.OnEvent(new UiEvent(0u, button, UiEventType.DragEnter, Payload: payload));
            button.OnEvent(new UiEvent(0u, button, UiEventType.DropReleased, Payload: payload));
        }

        Assert.Equal(new[] { (first, player, 0) }, puts);
        Assert.True(interaction.TryGetPendingBackpackPlacement(first, out _));
        Assert.True(interaction.TryGetPendingInventoryRequest(out var request));
        Assert.Equal(first, request.ItemId);
        Assert.Equal(new[] { ItemInteractionController.InventoryRequestBusyMessage }, messages);
    }

    [Fact]
    public void InventoryButton_shortcutAliasDrop_staysNeutralAndDoesNotMovePhysicalItem()
    {
        const uint player = 0x5000u;
        const uint item = 0x5001u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = item, Type = ItemType.Misc });
        var puts = new List<(uint Item, uint Container, int Placement)>();
        ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { },
            playerGuid: () => player,
            sendPutItemInContainer: (i, c, p) => puts.Add((i, c, p)));
        var button = (UiButton)layout.FindElement(InventoryButtonId)!;
        var payload = new ItemDragPayload(item, ItemDragSource.ShortcutBar, 3, new UiItemSlot());

        button.OnEvent(new UiEvent(0u, button, UiEventType.DragEnter, Payload: payload));
        Assert.Equal(ItemDragAcceptance.None, button.ItemDragAcceptanceForTest);
        button.OnEvent(new UiEvent(0u, button, UiEventType.DropReleased, Payload: payload));

        Assert.Empty(puts);
    }

    [Fact]
    public void WindowButtonState_highlightsWhenOpen()
    {
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        var ctrl = ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { });
        var inventoryButton = (UiButton)layout.FindElement(InventoryButtonId)!;
        var characterButton = (UiButton)layout.FindElement(CharacterButtonId)!;

        ctrl.BindPanelButtons(_ => true, _ => { });
        ctrl.SetPanelOpen(RetailPanelCatalog.Inventory, true);
        ctrl.SetPanelOpen(RetailPanelCatalog.Character, true);

        Assert.Equal("Highlight", inventoryButton.ActiveState);
        Assert.Equal("Highlight", characterButton.ActiveState);

        ctrl.SetPanelOpen(RetailPanelCatalog.Inventory, false);
        ctrl.SetPanelOpen(RetailPanelCatalog.Character, false);

        Assert.Equal("Normal", inventoryButton.ActiveState);
        Assert.Equal("Normal", characterButton.ActiveState);
    }

    [Fact]
    public void RetailFixture_inventoryButtonPressKeepsClosedFaceThenOpensDirectly()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadToolbarInfos(),
            static file => (file, 8, 8),
            null);
        var controller = ToolbarController.Bind(
            layout,
            new ClientObjectTable(),
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { });
        controller.BindPanelButtons(
            _ => true,
            panelId => controller.SetPanelOpen(panelId, open: true));
        var inventory = Assert.IsType<UiButton>(layout.FindElement(InventoryButtonId));

        Assert.Equal("Normal", inventory.ActiveState);
        Assert.Equal(0x06004CF7u, DrawnFaceFile(inventory));

        inventory.OnEvent(new UiEvent(
            inventory.EventId,
            inventory,
            UiEventType.MouseDown,
            Data1: 31,
            Data2: 29));

        Assert.Equal("Normal_pressed", inventory.ActiveState);
        Assert.Equal(0x06004CF7u, DrawnFaceFile(inventory));

        inventory.OnEvent(new UiEvent(
            inventory.EventId,
            inventory,
            UiEventType.MouseUp,
            Data1: 31,
            Data2: 29));

        Assert.Equal("Highlight", inventory.ActiveState);
        Assert.Equal(0x06004CF8u, DrawnFaceFile(inventory));
    }

    [InstalledDatFact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledDat_inventoryButtonPressStateKeepsTheCurrentFace()
    {
        string datDirectory = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new AcDream.App.Tests.BoundedTestDatCollection(
            datDirectory,
            DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo root = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, 0x21000016u));
        ElementInfo inventory = FindInfo(root, InventoryButtonId);

        Assert.Equal(1, inventory.States[UiButtonStateMachine.Normal].MediaCount);
        Assert.Equal(1, inventory.States[UiButtonStateMachine.Normal].ImageMediaCount);
        Assert.Equal(1, inventory.States[UiButtonStateMachine.NormalPressed].MediaCount);
        Assert.Equal(0, inventory.States[UiButtonStateMachine.NormalPressed].ImageMediaCount);
        Assert.Equal(1, inventory.States[UiButtonStateMachine.Highlight].MediaCount);
        Assert.Equal(1, inventory.States[UiButtonStateMachine.Highlight].ImageMediaCount);
        Assert.DoesNotContain("Normal_pressed", inventory.StateMedia.Keys);
    }

    [Fact]
    public void RetailFixture_panelButtonsExposeExactDatPanelIds()
    {
        ImportedLayout layout = FixtureLoader.LoadToolbar();

        foreach (var (elementId, panelId) in PanelButtons)
        {
            var button = Assert.IsType<UiButton>(layout.FindElement(elementId));
            Assert.True(button.TryGetEnumAttribute(0x10000029u, out uint actual));
            Assert.Equal(panelId, actual);
        }
    }

    [Fact]
    public void RetailFixture_ammoNumberUsesAuthoredMissileIndicatorElement()
    {
        ImportedLayout layout = FixtureLoader.LoadToolbar();

        var ammoIndicator = Assert.IsType<UiButton>(layout.FindElement(0x10000194u));

        Assert.Equal(55f, ammoIndicator.Width);
        Assert.Equal(58f, ammoIndicator.Height);
    }

    [Fact]
    public void UseAndExamineButtons_dispatchSelectedObjectPolicy()
    {
        const uint player = 0x50000001u;
        const uint pack = 0x50000002u;
        const uint item = 0x50000003u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = pack, Type = ItemType.Container });
        repo.MoveItem(pack, player, 0);
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            Type = ItemType.Misc,
            Useability = ItemUseability.Contained,
        });
        repo.MoveItem(item, pack, 0);
        var uses = new List<uint>();
        var examines = new List<uint>();
        uint selected = item;
        var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            () => player,
            sendUse: uses.Add,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            sendExamine: examines.Add);
        ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { },
            itemInteraction: interaction,
            selectedObjectId: () => selected);

        ((UiButton)layout.FindElement(UseButtonId)!).OnClick!.Invoke();
        ((UiButton)layout.FindElement(ExamineButtonId)!).OnClick!.Invoke();

        Assert.Equal(new[] { item }, uses);
        Assert.Equal(new[] { item }, examines);

        selected = 0;
        ((UiButton)layout.FindElement(ExamineButtonId)!).OnClick!.Invoke();
        Assert.Equal(InteractionModeKind.Examine, interaction.InteractionState.Current.Kind);
    }

    [Fact]
    public void UseButton_tracksEmptyUnusableAndTargetedSelection()
    {
        const uint player = 0x50000001u;
        const uint pack = 0x50000002u;
        const uint component = 0x50000003u;
        const uint healthKit = 0x50000004u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = pack, Type = ItemType.Container });
        repo.MoveItem(pack, player, 0);
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = component,
            Type = ItemType.SpellComponents,
            Useability = ItemUseability.No,
        });
        repo.MoveItem(component, pack, 0);
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = healthKit,
            Type = ItemType.Misc,
            Useability = 0x00220008u,
            TargetType = (uint)ItemType.Creature,
        });
        repo.MoveItem(healthKit, pack, 1);

        var selection = new SelectionState();
        using var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            () => player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null);
        using var controller = ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { },
            itemInteraction: interaction,
            selectedObjectId: () => selection.SelectedObjectId ?? 0u,
            selection: selection);
        var use = Assert.IsType<UiButton>(layout.FindElement(UseButtonId));

        Assert.False(use.Enabled);
        Assert.Equal("Ghosted", use.ActiveState);
        use.OnEvent(new UiEvent(0u, use, UiEventType.Click));
        Assert.Equal(InteractionModeKind.None, interaction.InteractionState.Current.Kind);

        selection.Select(component, SelectionChangeSource.Inventory);
        Assert.False(use.Enabled);
        Assert.Equal("Ghosted", use.ActiveState);

        selection.Select(healthKit, SelectionChangeSource.Inventory);
        Assert.True(use.Enabled);
        Assert.Equal("Normal", use.ActiveState);
        use.OnEvent(new UiEvent(0u, use, UiEventType.Click));
        Assert.Equal(
            InteractionModeKind.UseItemOnTarget,
            interaction.InteractionState.Current.Kind);

        selection.Clear(SelectionChangeSource.Inventory);
        Assert.False(use.Enabled);
        Assert.Equal("Ghosted", use.ActiveState);
    }

    [Fact]
    public void UseButton_selectedCombatItem_routesExistingWieldPath()
    {
        const uint player = 0x50000001u;
        const uint pack = 0x50000002u;
        const uint sword = 0x50000003u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = pack, Type = ItemType.Container });
        repo.MoveItem(pack, player, 0);
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = sword,
            Type = ItemType.MeleeWeapon,
            CombatUse = 1,
            Useability = ItemUseability.No,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        repo.MoveItem(sword, pack, 0);

        var selection = new SelectionState();
        selection.Select(sword, SelectionChangeSource.Inventory);
        var wields = new List<(uint Item, uint Location)>();
        using var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            () => player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: (item, location) => wields.Add((item, location)),
            sendDrop: null);
        using var controller = ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { },
            itemInteraction: interaction,
            selectedObjectId: () => selection.SelectedObjectId ?? 0u,
            selection: selection);
        var use = Assert.IsType<UiButton>(layout.FindElement(UseButtonId));

        Assert.True(use.Enabled);
        use.OnEvent(new UiEvent(0u, use, UiEventType.Click));

        Assert.Equal(new[] { (sword, (uint)EquipMask.MeleeWeapon) }, wields);
    }


    [Fact]
    public void AmmoNumber_usesStackableMissileWeapon_andTracksStackChanges()
    {
        const uint player = 0x50000001u;
        const uint thrownWeapon = 0x50000002u;
        const uint separateAmmo = 0x50000003u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = thrownWeapon,
            StackSize = 7,
            StackSizeMax = 25,
        });
        repo.MoveItem(thrownWeapon, player, newEquipLocation: EquipMask.MissileWeapon);
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = separateAmmo,
            StackSize = 80,
            StackSizeMax = 100,
        });
        repo.MoveItem(separateAmmo, player, newEquipLocation: EquipMask.MissileAmmo);

        ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { },
            playerGuid: () => player);
        var ammoButton = (UiButton)layout.FindElement(0x10000194u)!;

        Assert.Equal("7", ammoButton.Label);

        Assert.True(repo.UpdateStackSize(thrownWeapon, 6, value: 0));
        Assert.Equal("6", ammoButton.Label);
    }

    [Fact]
    public void AmmoNumber_forLauncher_usesAmmoSlot_andClearsOnUnwield()
    {
        const uint player = 0x50000001u;
        const uint pack = 0x50000010u;
        const uint bow = 0x50000002u;
        const uint arrows = 0x50000003u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = pack, Type = ItemType.Container });
        repo.AddOrUpdate(new ClientObject { ObjectId = bow, StackSize = 1, StackSizeMax = 1 });
        repo.ApplyServerMove(
            bow,
            newContainerId: 0u,
            newWielderId: player,
            newEquipLocation: EquipMask.MissileWeapon);
        repo.AddOrUpdate(new ClientObject { ObjectId = arrows, StackSize = 42, StackSizeMax = 100 });
        repo.ApplyServerMove(
            arrows,
            newContainerId: 0u,
            newWielderId: player,
            newEquipLocation: EquipMask.MissileAmmo);

        ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { },
            playerGuid: () => player);
        var ammoButton = (UiButton)layout.FindElement(0x10000194u)!;

        Assert.Equal("42", ammoButton.Label);

        Assert.True(repo.ApplyServerMove(arrows, pack, newWielderId: 0u));
        Assert.Null(ammoButton.Label);
    }

    [Fact]
    public void CombatIndicators_allDispatchSharedRetailToggleCommand()
    {
        var (layout, _, indicators) = FakeToolbar();
        var repo = new ClientObjectTable();
        int toggles = 0;

        ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 0u,
            useItem: _ => { },
            toggleCombat: () => toggles++);

        foreach (uint id in CombatIds)
            ((UiButton)indicators[id]).OnClick?.Invoke();

        Assert.Equal(CombatIds.Length, toggles);
    }

    [Fact]
    public void UseShortcut_usesOrSelectsAccordingToRetailIntent()
    {
        const uint itemId = 0x50001001u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = itemId, Type = ItemType.Misc });
        var shortcuts = new[]
        {
            new ShortcutEntry(0, itemId, 0),
        };
        uint used = 0;
        uint selected = 0;
        var controller = ToolbarController.Bind(
            layout,
            repo,
            Store(shortcuts),
            iconIds: (_, _, _, _, _) => 1u,
            useItem: id => used = id,
            selectItem: id => selected = id);

        Assert.True(controller.UseShortcut(0, use: true));
        Assert.Equal(itemId, used);
        Assert.Equal(0u, selected);

        Assert.True(controller.UseShortcut(0, use: false));
        Assert.Equal(itemId, selected);
    }

    [Fact]
    public void RightClickPhysicalShortcut_selectsAndExaminesItem()
    {
        const uint player = 0x50000001u;
        const uint itemId = 0x50001001u;
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = itemId, Type = ItemType.Misc });
        var shortcuts = new[] { new ShortcutEntry(0, itemId, 0) };
        var selection = new SelectionState();
        var appraisals = new List<uint>();
        using var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            playerGuid: () => player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            sendExamine: appraisals.Add);
        using var controller = ToolbarController.Bind(
            layout,
            repo,
            Store(shortcuts),
            iconIds: (_, _, _, _, _) => 1u,
            useItem: _ => { },
            itemInteraction: interaction,
            selectItem: id => selection.Select(id, SelectionChangeSource.Toolbar),
            selection: selection);

        UiItemSlot cell = slots[Row1[0]].Cell;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.RightClick));

        Assert.Equal(itemId, selection.SelectedObjectId);
        Assert.Equal(new uint[] { itemId }, appraisals);
    }

    [Fact]
    public void UseShortcut_targetModePrecedesUseAndClearsOneShotMode()
    {
        const uint player = 0x50000001u;
        const uint pack = 0x50000002u;
        const uint source = 0x50000003u;
        const uint target = 0x50000004u;
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = pack, Type = ItemType.Container });
        repo.MoveItem(pack, player, 0);
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = source,
            Type = ItemType.Misc,
            Useability = 0x00220008u,
            TargetType = (uint)ItemType.Creature,
        });
        repo.MoveItem(source, pack, 0);
        repo.AddOrUpdate(new ClientObject { ObjectId = target, Type = ItemType.Creature });
        uint sentSource = 0;
        uint sentTarget = 0;
        var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            () => player,
            sendUse: null,
            sendUseWithTarget: (s, t) => (sentSource, sentTarget) = (s, t),
            sendWield: null,
            sendDrop: null,
            nowMs: () => 1_000);
        var shortcuts = new[]
        {
            new ShortcutEntry(0, target, 0),
        };
        var controller = ToolbarController.Bind(
            layout,
            repo,
            Store(shortcuts),
            iconIds: (_, _, _, _, _) => 1u,
            useItem: _ => { },
            itemInteraction: interaction);

        Assert.True(interaction.ActivateItem(source));
        Assert.True(controller.UseShortcut(0, use: false));

        Assert.Equal(source, sentSource);
        Assert.Equal(target, sentTarget);
        Assert.False(interaction.IsTargetModeActive);
    }

    [Fact]
    public void CreateShortcutToItem_addsFirstEmptyOwnedEligibleItemOnce()
    {
        const uint player = 0x50000001u;
        const uint pack = 0x50000002u;
        const uint item = 0x50000003u;
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = player, Type = ItemType.Creature });
        repo.AddOrUpdate(new ClientObject { ObjectId = pack, Type = ItemType.Container });
        repo.MoveItem(pack, player, 0);
        repo.AddOrUpdate(new ClientObject { ObjectId = item, Type = ItemType.Misc });
        repo.MoveItem(item, pack, 0);
        var interaction = new ItemInteractionController(
            repo,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(repo)),
            new InteractionState(),
            () => player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null);
        var sends = new List<(uint Slot, uint Item)>();
        var controller = ToolbarController.Bind(
            layout,
            repo,
            new ShortcutStore(),
            iconIds: (_, _, _, _, _) => 1u,
            useItem: _ => { },
            itemInteraction: interaction,
            sendAddShortcut: entry => sends.Add(((uint)entry.Index, entry.ObjectId)));

        Assert.True(controller.CreateShortcutToItem(item));
        Assert.Equal(item, slots[Row1[0]].Cell.ItemId);
        Assert.Equal(new[] { (0u, item) }, sends);

        Assert.False(controller.CreateShortcutToItem(item));
        Assert.Single(sends);
    }

    [Fact]
    public void CombatIndicator_defaultNonCombat_onlyPeaceVisible()
    {
        var (layout, _, indicators) = FakeToolbar();
        var repo = new ClientObjectTable();

        ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) =>0u, useItem: _ => { });

        Assert.True (indicators[0x10000192u].Visible, "peace indicator should be visible after bind");
        Assert.False(indicators[0x10000193u].Visible, "melee indicator should be hidden after bind");
        Assert.False(indicators[0x10000194u].Visible, "missile indicator should be hidden after bind");
        Assert.False(indicators[0x10000195u].Visible, "magic indicator should be hidden after bind");
    }

    /// <summary>
    /// SetCombatMode(Melee) hides peace/missile/magic and shows only the melee indicator.
    /// </summary>
    [Fact]
    public void CombatIndicator_setCombatModeMelee_onlyMeleeVisible()
    {
        var (layout, _, indicators) = FakeToolbar();
        var repo = new ClientObjectTable();

        var ctrl = ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) =>0u, useItem: _ => { });

        ctrl.SetCombatMode(CombatMode.Melee);

        Assert.False(indicators[0x10000192u].Visible, "peace indicator should be hidden in melee mode");
        Assert.True (indicators[0x10000193u].Visible, "melee indicator should be visible in melee mode");
        Assert.False(indicators[0x10000194u].Visible, "missile indicator should be hidden in melee mode");
        Assert.False(indicators[0x10000195u].Visible, "magic indicator should be hidden in melee mode");
    }

    [Fact]
    public void CombatIndicator_liveSignal_updatesWhenCombatStateChanges()
    {
        var (layout, _, indicators) = FakeToolbar();
        var repo = new ClientObjectTable();
        var combat = new CombatState();

        ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) =>0u, useItem: _ => { },
            combatState: combat);

        // Initially NonCombat after bind.
        Assert.True(indicators[0x10000192u].Visible, "peace should be visible initially");

        combat.SetCombatMode(CombatMode.Magic);

        Assert.False(indicators[0x10000192u].Visible, "peace should be hidden in magic mode");
        Assert.False(indicators[0x10000193u].Visible, "melee should be hidden in magic mode");
        Assert.False(indicators[0x10000194u].Visible, "missile should be hidden in magic mode");
        Assert.True (indicators[0x10000195u].Visible, "magic indicator should be visible");
    }


    // Fake digit arrays: 9 regular entries (0x10..0x18), 9 ghosted entries (0x20..0x28),
    // 9 empty (background) entries (0x30..0x38).
    private static readonly uint[] FakeRegular = { 0x10u,0x11u,0x12u,0x13u,0x14u,0x15u,0x16u,0x17u,0x18u };
    private static readonly uint[] FakeGhosted = { 0x20u,0x21u,0x22u,0x23u,0x24u,0x25u,0x26u,0x27u,0x28u };
    private static readonly uint[] FakeEmpty = { 0x30u,0x31u,0x32u,0x33u,0x34u,0x35u,0x36u,0x37u,0x38u };

    [Fact]
    public void ShortcutNumbers_afterBind_topRowHasNumbers_bottomRowEmpty()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();

        ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { },
            regularDigits: FakeRegular, ghostedDigits: FakeGhosted);

        // Top row: ShortcutNum == slot index, ghosted == false.
        for (int i = 0; i < Row1.Length; i++)
        {
            var cell = slots[Row1[i]].Cell;
            Assert.Equal(i,    cell.ShortcutNum);
            Assert.False(cell.ShortcutGhosted, $"top-row slot {i} should be regular at NonCombat");
        }
        // Bottom row: no shortcut number.
        foreach (var id in Row2)
            Assert.Equal(-1, slots[id].Cell.ShortcutNum);
    }

    [Theory]
    [InlineData(CombatMode.NonCombat)]
    [InlineData(CombatMode.Melee)]
    [InlineData(CombatMode.Missile)]
    public void ShortcutNumbers_nonMagicModes_useRegularDigits(CombatMode mode)
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        var ctrl = ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { },
            regularDigits: FakeRegular, ghostedDigits: FakeGhosted);

        ctrl.SetCombatMode(mode);

        for (int i = 0; i < Row1.Length; i++)
        {
            var cell = slots[Row1[i]].Cell;
            Assert.Equal(i, cell.ShortcutNum);
            Assert.False(cell.ShortcutGhosted, $"top-row slot {i} should be regular in {mode}");
            cell.SetItem((uint)(0x5000 + i), 0x99u);
            Assert.Same(FakeRegular, cell.ActiveDigitArray());
        }
        // Bottom row still has no number.
        foreach (var id in Row2)
            Assert.Equal(-1, slots[id].Cell.ShortcutNum);
    }

    /// <summary>
    /// Magic mode ghosts physical shortcut labels, and returning to NonCombat restores them.
    /// </summary>
    [Fact]
    public void ShortcutNumbers_magicGhosts_thenNonCombatRestoresRegularDigits()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        var ctrl = ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { },
            regularDigits: FakeRegular, ghostedDigits: FakeGhosted);

        ctrl.SetCombatMode(CombatMode.Magic);
        foreach (var id in Row1)
        {
            slots[id].Cell.SetItem(id, 0x99u);
            Assert.True(slots[id].Cell.ShortcutGhosted);
            Assert.Same(FakeGhosted, slots[id].Cell.ActiveDigitArray());
        }

        ctrl.SetCombatMode(CombatMode.NonCombat);

        for (int i = 0; i < Row1.Length; i++)
        {
            Assert.False(slots[Row1[i]].Cell.ShortcutGhosted,
                $"top-row slot {i} should be regular after returning to NonCombat");
            Assert.Same(FakeRegular, slots[Row1[i]].Cell.ActiveDigitArray());
        }
    }

    [Fact]
    public void ShortcutNumbers_digitArraysInjected()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();

        ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { },
            regularDigits: FakeRegular, ghostedDigits: FakeGhosted);

        foreach (var id in Row1)
        {
            Assert.Same(FakeRegular, slots[id].Cell.RegularDigits);
            Assert.Same(FakeGhosted, slots[id].Cell.GhostedDigits);
        }
    }

    [Fact]
    public void ShortcutNumbers_emptyDigitArrayInjected()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();

        ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { },
            regularDigits: FakeRegular, ghostedDigits: FakeGhosted, emptyDigits: FakeEmpty);

        foreach (var id in Row1)
            Assert.Same(FakeEmpty, slots[id].Cell.EmptyDigits);
        foreach (var id in Row2)
            Assert.Same(FakeEmpty, slots[id].Cell.EmptyDigits);
    }

    [Fact]
    public void ShortcutNumbers_nullEmptyDigits_cellsHaveNullEmptyDigits()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();

        ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { },
            regularDigits: FakeRegular, ghostedDigits: FakeGhosted, emptyDigits: null);

        foreach (var id in Row1)
            Assert.Null(slots[id].Cell.EmptyDigits);
    }

    // ── E1: Guid filter + ObjectRemoved tests (D.5.4) ───────────────────────

    [Fact]
    public void ObjectAdded_nonShortcutGuid_doesNotCallIconIds()
    {
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, WeenieClassId = 1u, IconId = 0x06001234u });
        var shortcuts = new List<ShortcutEntry>
        { new(Index: 0, ObjectId: 0x5001u, SpellId: 0) };

        int iconCallCount = 0;
        ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => { iconCallCount++; return 0x77u; }, useItem: _ => { });

        int callsAfterBind = iconCallCount;

        repo.AddOrUpdate(new ClientObject { ObjectId = 0xDEADBEEFu, WeenieClassId = 42u, IconId = 0u });

        Assert.Equal(callsAfterBind, iconCallCount);
    }

    [Fact]
    public void ObjectAdded_shortcutGuid_callsIconIds()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable(); // item NOT present yet
        var shortcuts = new List<ShortcutEntry>
        { new(Index: 1, ObjectId: 0x5003u, SpellId: 0) };

        int iconCallCount = 0;
        ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => { iconCallCount++; return 0x99u; }, useItem: _ => { });

        Assert.Equal(0, iconCallCount);
        Assert.Equal(0u, slots[Row1[1]].Cell.ItemId);

        // Now the shortcut item arrives — filter must PASS and Populate re-run.
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5003u, WeenieClassId = 1u, IconId = 0x06005678u });

        Assert.Equal(1, iconCallCount);
        Assert.Equal(0x5003u, slots[Row1[1]].Cell.ItemId);
    }

    [Fact]
    public void ObjectRemoved_shortcutGuid_clearsSlot()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5004u, WeenieClassId = 1u, IconId = 0x06001234u });
        var shortcuts = new List<ShortcutEntry>
        { new(Index: 3, ObjectId: 0x5004u, SpellId: 0) };

        ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0xAAu, useItem: _ => { });

        Assert.Equal(0x5004u, slots[Row1[3]].Cell.ItemId); // bound

        repo.Remove(0x5004u);

        Assert.Equal(0u, slots[Row1[3]].Cell.ItemId);
    }

    [Fact]
    public void ObjectRemoved_nonShortcutGuid_doesNotCallIconIds()
    {
        var (layout, _, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5005u, WeenieClassId = 1u, IconId = 0x06001234u });
        repo.AddOrUpdate(new ClientObject { ObjectId = 0xCAFEBABEu, WeenieClassId = 99u, IconId = 0u });
        var shortcuts = new List<ShortcutEntry>
        { new(Index: 4, ObjectId: 0x5005u, SpellId: 0) };

        int iconCallCount = 0;
        ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => { iconCallCount++; return 0xBBu; }, useItem: _ => { });

        int callsAfterBind = iconCallCount;

        // Remove an unrelated object — filter must block Populate.
        repo.Remove(0xCAFEBABEu);

        Assert.Equal(callsAfterBind, iconCallCount); // unchanged
    }

    // ── B.1: drag-drop spine wiring ──────────────────────────────────────────

    [Fact]
    public void Bind_registersDragHandler_andStampsSlots()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();

        var ctrl = ToolbarController.Bind(layout, repo,
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { });

        for (int i = 0; i < Row1.Length; i++)
        {
            Assert.Same(ctrl, slots[Row1[i]].DragHandler);
            Assert.Equal(i, slots[Row1[i]].Cell.SlotIndex);
            Assert.Equal(ItemDragSource.ShortcutBar, slots[Row1[i]].Cell.SourceKind);
        }
        // Bottom row slots are indices 9..17; same handler + source kind as the top row.
        for (int j = 0; j < Row2.Length; j++)
        {
            Assert.Same(ctrl, slots[Row2[j]].DragHandler);
            Assert.Equal(9 + j, slots[Row2[j]].Cell.SlotIndex);
            Assert.Equal(ItemDragSource.ShortcutBar, slots[Row2[j]].Cell.SourceKind);
        }
    }

    /// <summary>OnDragOver accepts a real item (ObjId != 0). Eligibility (IsShortcutEligible)
    /// is Stream B.2; the stub accepts any non-empty payload.</summary>
    [Fact]
    public void OnDragOver_acceptsRealItem()
    {
        var (layout, slots, _) = FakeToolbar();
        var ctrl = ToolbarController.Bind(layout, new ClientObjectTable(),
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { });

        var list = slots[Row1[0]];
        var payload = new ItemDragPayload(0x5001u, ItemDragSource.Inventory, 0, new UiItemSlot());
        Assert.Equal(ItemDragAcceptance.Accept, ctrl.OnDragOver(list, list.Cell, payload));
    }

    [Fact]
    public void OnDragOver_rejectsZeroObjId()
    {
        var (layout, slots, _) = FakeToolbar();
        var ctrl = ToolbarController.Bind(layout, new ClientObjectTable(),
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { });
        var list = slots[Row1[0]];
        var payload = new ItemDragPayload(0u, ItemDragSource.Inventory, 0, new UiItemSlot());
        Assert.Equal(ItemDragAcceptance.None, ctrl.OnDragOver(list, list.Cell, payload));
    }

    // ── B.2: live drag handler (store + reorder/remove + wire) ───────────────
    private static (System.Collections.Generic.List<ShortcutEntry> adds,
                    System.Collections.Generic.List<uint> removes) NewSpies(out System.Action<ShortcutEntry> add, out System.Action<uint> rem)
    {
        var adds = new System.Collections.Generic.List<ShortcutEntry>();
        var removes = new System.Collections.Generic.List<uint>();
        add = entry => adds.Add(entry);
        rem = i => removes.Add(i);
        return (adds, removes);
    }

    [Fact]
    public void OnDragLift_removesSourceSlot_sendsRemove_emptiesCell()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, WeenieClassId = 1u, IconId = 0x06001234u });
        var shortcuts = new System.Collections.Generic.List<ShortcutEntry>
        { new(Index: 3, ObjectId: 0x5001u, SpellId: 0) };
        var (adds, removes) = NewSpies(out var add, out var rem);
        uint selected = 0;

        var ctrl = ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0x77u, useItem: _ => { },
            sendAddShortcut: add, sendRemoveShortcut: rem,
            selectItem: item => selected = item, selectedObjectId: () => selected);
        Assert.Equal(0x5001u, slots[Row1[3]].Cell.ItemId);

        var payload = new ItemDragPayload(0x5001u, ItemDragSource.ShortcutBar, 3, slots[Row1[3]].Cell);
        ctrl.OnDragLift(slots[Row1[3]], slots[Row1[3]].Cell, payload);

        Assert.Equal(0x5001u, selected);
        Assert.Contains(3u, removes);
        Assert.Equal(0u, slots[Row1[3]].Cell.ItemId);
    }

    [Fact]
    public void HandleDropRelease_ontoOccupied_swaps_andSendsRetailSequence()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, WeenieClassId = 1u, IconId = 0x06001234u }); // A
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5002u, WeenieClassId = 1u, IconId = 0x06005678u }); // B
        var shortcuts = new System.Collections.Generic.List<ShortcutEntry>
        { new(Index: 3, ObjectId: 0x5001u, SpellId: 0),
          new(Index: 5, ObjectId: 0x5002u, SpellId: 0) };
        var (adds, removes) = NewSpies(out var add, out var rem);
        var ctrl = ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0x77u, useItem: _ => { },
            sendAddShortcut: add, sendRemoveShortcut: rem);

        var payload = new ItemDragPayload(0x5001u, ItemDragSource.ShortcutBar, 3, slots[Row1[3]].Cell);
        ctrl.OnDragLift(slots[Row1[3]], slots[Row1[3]].Cell, payload);
        ctrl.HandleDropRelease(slots[Row1[5]], slots[Row1[5]].Cell, payload);

        Assert.Equal(0x5001u, slots[Row1[5]].Cell.ItemId);
        Assert.Equal(0x5002u, slots[Row1[3]].Cell.ItemId);
        Assert.Equal(new[] { 3u, 5u }, removes.ToArray());
        Assert.Contains(new ShortcutEntry(5, 0x5001u, 0u), adds);
        Assert.Contains(new ShortcutEntry(3, 0x5002u, 0u), adds);
    }

    [Fact]
    public void HandleDropRelease_reindexesWithoutLosingRawShortcutFields()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, IconId = 1u });
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5002u, IconId = 2u });
        var shortcuts = new[]
        {
            new ShortcutEntry(3, 0x5001u, 0x11223344u),
            new ShortcutEntry(5, 0x5002u, 0xA5C31234u),
        };
        var (adds, _) = NewSpies(out var add, out var remove);
        var controller = ToolbarController.Bind(
            layout, repo, Store(shortcuts),
            iconIds: (_, _, _, _, _) => 1u,
            useItem: _ => { },
            sendAddShortcut: add,
            sendRemoveShortcut: remove);

        var payload = Assert.IsType<ItemDragPayload>(slots[Row1[3]].Cell.GetDragPayload());
        controller.OnDragLift(slots[Row1[3]], slots[Row1[3]].Cell, payload);
        controller.HandleDropRelease(slots[Row1[5]], slots[Row1[5]].Cell, payload);

        Assert.Contains(new ShortcutEntry(5, 0x5001u, 0x11223344u), adds);
        Assert.Contains(new ShortcutEntry(3, 0x5002u, 0xA5C31234u), adds);
    }

    [Fact]
    public void HandleDropRelease_ontoEmpty_placesOnly_sourceStaysEmpty()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, WeenieClassId = 1u, IconId = 0x06001234u });
        var shortcuts = new System.Collections.Generic.List<ShortcutEntry>
        { new(Index: 3, ObjectId: 0x5001u, SpellId: 0) };
        var (adds, removes) = NewSpies(out var add, out var rem);
        var ctrl = ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0x77u, useItem: _ => { },
            sendAddShortcut: add, sendRemoveShortcut: rem);

        var payload = new ItemDragPayload(0x5001u, ItemDragSource.ShortcutBar, 3, slots[Row1[3]].Cell);
        ctrl.OnDragLift(slots[Row1[3]], slots[Row1[3]].Cell, payload);
        ctrl.HandleDropRelease(slots[Row1[7]], slots[Row1[7]].Cell, payload);

        Assert.Equal(0x5001u, slots[Row1[7]].Cell.ItemId);
        Assert.Equal(0u, slots[Row1[3]].Cell.ItemId);
        Assert.Contains(new ShortcutEntry(7, 0x5001u, 0u), adds);
        Assert.DoesNotContain(adds, a => a.Index == 3);
    }

    [Fact]
    public void HandleDropRelease_freshInventoryToOccupied_usesCyclicRightSlotNotInventoryIndex()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, IconId = 1u });
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5002u, IconId = 2u });
        var shortcuts = new[]
        {
            new ShortcutEntry(5, 0x5002u, 0xA5C31234u),
        };
        var wire = new List<string>();
        var ctrl = ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_, _, _, _, _) => 1u,
            useItem: _ => { },
            sendAddShortcut: entry => wire.Add($"add:{entry.Index}:{entry.ObjectId:X8}:{entry.SpellId:X8}"),
            sendRemoveShortcut: slot => wire.Add($"remove:{slot}"));

        var payload = new ItemDragPayload(
            0x5001u, ItemDragSource.Inventory, SourceSlot: 12, new UiItemSlot());
        ctrl.HandleDropRelease(slots[Row1[5]], slots[Row1[5]].Cell, payload);

        Assert.Equal(0x5001u, slots[Row1[5]].Cell.ItemId);
        Assert.Equal(0x5002u, slots[Row1[6]].Cell.ItemId);
        Assert.Equal(0u, slots[Row2[3]].Cell.ItemId); // inventory grid index 12 is irrelevant
        Assert.Equal(new[]
        {
            "remove:5",
            "add:5:00005001:00000000",
            "add:6:00005002:A5C31234",
        }, wire);
    }

    [Fact]
    public void ReplaceFullyMergedShortcut_rekeysEntryAndPreservesRawFields()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, IconId = 1u });
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5002u, IconId = 2u });
        var shortcuts = new[]
        {
            new ShortcutEntry(4, 0x5001u, 0x11223344u),
        };
        var wire = new List<string>();
        var ctrl = ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_, _, _, _, _) => 1u,
            useItem: _ => { },
            sendAddShortcut: entry => wire.Add($"add:{entry.Index}:{entry.ObjectId:X8}:{entry.SpellId:X8}"),
            sendRemoveShortcut: slot => wire.Add($"remove:{slot}"));

        ctrl.ReplaceFullyMergedShortcut(0x5001u, 0x5002u);

        Assert.Equal(0x5002u, slots[Row1[4]].Cell.ItemId);
        Assert.Equal(new[]
        {
            "remove:4",
            "add:4:00005002:11223344",
        }, wire);
    }

    [Fact]
    public void HandleDropRelease_ontoSelf_reAddsToSource()
    {
        var (layout, slots, _) = FakeToolbar();
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x5001u, WeenieClassId = 1u, IconId = 0x06001234u });
        var shortcuts = new System.Collections.Generic.List<ShortcutEntry>
        { new(Index: 3, ObjectId: 0x5001u, SpellId: 0) };
        var (adds, removes) = NewSpies(out var add, out var rem);
        var ctrl = ToolbarController.Bind(layout, repo, Store(shortcuts),
            iconIds: (_,_,_,_,_) => 0x77u, useItem: _ => { },
            sendAddShortcut: add, sendRemoveShortcut: rem);

        var payload = new ItemDragPayload(0x5001u, ItemDragSource.ShortcutBar, 3, slots[Row1[3]].Cell);
        ctrl.OnDragLift(slots[Row1[3]], slots[Row1[3]].Cell, payload);            // slot 3 emptied
        ctrl.HandleDropRelease(slots[Row1[3]], slots[Row1[3]].Cell, payload);     // drop back on slot 3

        Assert.Equal(0x5001u, slots[Row1[3]].Cell.ItemId);   // re-added to source (net no-op)
        Assert.Contains(new ShortcutEntry(3, 0x5001u, 0u), adds); // AddShortcut(3, A) sent
        Assert.Single(removes);   // exactly RemoveShortcut(3) [from the lift]
        Assert.Single(adds);      // exactly AddShortcut(3, A) [the re-place]
    }

    [Fact]
    public void ToolbarSlots_useGreenCrossAcceptSprite()
    {
        var (layout, slots, _) = FakeToolbar();
        ToolbarController.Bind(layout, new ClientObjectTable(),
            new ShortcutStore(),
            iconIds: (_,_,_,_,_) => 0u, useItem: _ => { });
        Assert.Equal(0x060011FAu, slots[Row1[0]].Cell.DragAcceptSprite);
    }

    private static uint DrawnFaceFile(UiButton button)
    {
        var renderer = new AcDream.App.Rendering.TextRenderer(
            new AcDream.App.Tests.Rendering.Gpu.RecordingGpuDevice(),
            new NullGpuFrameSource(),
            "unused");
        renderer.Begin(new System.Numerics.Vector2(200f, 200f));
        button.DrawSelfAndChildren(new UiRenderContext(
            renderer,
            new System.Numerics.Vector2(200f, 200f)));
        return Assert.Single(renderer.DebugSpriteSegments).Texture;
    }

    private sealed class NullGpuFrameSource
        : AcDream.App.Rendering.ICurrentGpuFrameSource
    {
        public AcDream.App.Rendering.Gpu.IGpuFrame? CurrentFrame => null;
    }

    private static ElementInfo FindInfo(ElementInfo root, uint id)
        => TryFindInfo(root, id)
            ?? throw new InvalidOperationException($"Element 0x{id:X8} was not found.");

    private static ElementInfo? TryFindInfo(ElementInfo root, uint id)
    {
        if (root.Id == id)
            return root;
        foreach (ElementInfo child in root.Children)
        {
            if (TryFindInfo(child, id) is { } found)
                return found;
        }
        return null;
    }
}
