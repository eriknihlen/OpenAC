using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Selection;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ExternalContainerControllerTests
{
    private const uint Player = 0x50000001u;
    private const uint Chest = 0x70000001u;
    private const uint Item = 0x60000001u;

    private sealed class TestElement : UiElement { }

    [Fact]
    public void WindowPolicy_AddsOuterFrameAndAllowsHorizontalResize()
    {
        var screen = new UiRoot { Width = 1280f, Height = 720f };
        var root = new TestElement
        {
            Left = 0f,
            Top = 400f,
            Width = 800f,
            Height = 110f,
        };
        RetailWindowFrame.Options options =
            ExternalContainerController.CreateWindowOptions(root);

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            screen,
            root,
            static _ => (0u, 0, 0),
            options);

        var frame = Assert.IsType<UiNineSlicePanel>(handle.OuterFrame);
        Assert.Equal(710f, frame.Width);
        Assert.Equal(120f, frame.Height);
        Assert.Equal(170f, frame.MinWidth);
        Assert.True(frame.Draggable);
        Assert.True(frame.Resizable);
        Assert.True(frame.ResizeX);
        Assert.False(frame.ResizeY);
        Assert.Equal(ResizeEdges.Left | ResizeEdges.Right, frame.ResizableEdges);
        Assert.True(frame.ConstrainResizeToParent);
        Assert.False(frame.DrawCenterFill);
        Assert.Equal(700f, root.Width);
    }

    private sealed class Harness : IDisposable
    {
        public readonly ExternalContainerState State = new();
        public readonly ClientObjectTable Objects = new();
        public readonly SelectionState Selection = new();
        public readonly StackSplitQuantityState Split = new();
        public readonly List<uint> Uses = new();
        public readonly List<uint> NoLonger = new();
        public readonly List<uint> Pickups = new();
        public readonly List<uint> Examines = new();
        public readonly List<(uint Item, uint Container, int Placement)> Puts = new();
        public readonly List<(uint Item, uint Container, uint Placement, uint Amount)> Splits = new();
        public readonly UiRoot Screen = new() { Width = 800f, Height = 600f };
        public readonly UiItemList Top = new() { Width = 36f, Height = 36f };
        public readonly UiItemList Containers = new() { Width = 680f, Height = 36f };
        public readonly UiItemList Contents = new() { Width = 784f, Height = 32f };
        public readonly RetailWindowHandle Window;
        public readonly RuntimeItemInteraction Interaction;
        public readonly ExternalContainerLifecycleController Lifecycle;
        public readonly ExternalContainerController Controller;
        public bool InRange = true;
        public PublicWeenieFlags ContainerFlags = PublicWeenieFlags.Openable;

        public Harness()
        {
            var root = new TestElement { Width = 800f, Height = 110f };
            var close = new UiButton(
                new ElementInfo { Id = ExternalContainerController.CloseButtonId, Type = 1 },
                static _ => (0u, 0, 0));
            var scrollbar = new UiScrollbar { Width = 784f, Height = 16f };
            root.AddChild(Top);
            root.AddChild(Containers);
            root.AddChild(Contents);
            root.AddChild(close);
            root.AddChild(scrollbar);
            var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
            {
                [ExternalContainerController.TopContainerId] = Top,
                [ExternalContainerController.ContainerListId] = Containers,
                [ExternalContainerController.ContentsListId] = Contents,
                [ExternalContainerController.ContentsScrollbarId] = scrollbar,
                [ExternalContainerController.CloseButtonId] = close,
            });

            Window = RetailWindowFrame.Mount(
                Screen,
                root,
                static _ => (0u, 0, 0),
                new RetailWindowFrame.Options
                {
                    WindowName = WindowNames.ExternalContainer,
                    Chrome = RetailWindowChrome.Imported,
                    Visible = false,
                    Draggable = false,
                    Resizable = false,
                });

            Interaction = new RuntimeItemInteraction(
                Objects,
                new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(Objects)),
                new InteractionState(),
                playerGuid: () => Player,
                sendUse: Uses.Add,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: null,
                sendExamine: Examines.Add,
                groundObjectId: () => State.CurrentContainerId,
                placeInBackpack: (item, _, _) => Pickups.Add(item),
                sendPutItemInContainer: (item, container, placement) =>
                    Puts.Add((item, container, placement)),
                sendSplitToContainer: (item, container, placement, amount) =>
                    Splits.Add((item, container, placement, amount)),
                requestExternalContainer: id => State.RequestOpen(id));
            Lifecycle = new ExternalContainerLifecycleController(State, Objects, NoLonger.Add);
            Controller = ExternalContainerController.Bind(
                layout,
                State,
                Objects,
                Selection,
                Interaction,
                Split,
                static (_, _, _, _, _) => 0u,
                static (_, _, _, _, _) => 0u,
                Uses.Add,
                (item, container, placement) => Puts.Add((item, container, placement)),
                (item, container, placement, amount) =>
                    Splits.Add((item, container, placement, amount)),
                _ => InRange,
                Window,
                resolveAppropriateName: ItemTooltipCaptionNames.Resolve);
            Screen.WindowManager.AttachController(WindowNames.ExternalContainer, Controller);
        }

        public void Open(uint id = Chest, params ContainerContentEntry[] contents)
            => Open(id, itemsCapacity: 24, containersCapacity: 0, contents);

        public void Open(
            uint id,
            int itemsCapacity,
            int containersCapacity,
            params ContainerContentEntry[] contents)
        {
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = id,
                Type = ItemType.Container,
                ItemsCapacity = itemsCapacity,
                ContainersCapacity = containersCapacity,
                Useability = ItemUseability.Remote,
                PublicWeenieBitfield = (uint)ContainerFlags,
            });
            State.RequestOpen(id);
            Objects.ReplaceContents(id, contents);
            State.ApplyViewContents(id);
        }

        public void Dispose()
        {
            Lifecycle.Dispose();
            Interaction.Dispose();
            Screen.WindowManager.Dispose();
        }
    }

    // OpenAC #109: the container window applies the same meter rule as the
    // inventory window - an empty container shows no meter, not an empty rail.
    [Fact]
    public void TopContainerMeter_hiddenWhileEmpty_thenReflectsLooseItems()
    {
        using var h = new Harness();
        h.Open(Chest);
        Assert.Equal(-1f, h.Top.GetItem(0)!.CapacityFill);

        h.Objects.AddOrUpdate(ItemTooltipCaptionNames.Plain(Item));
        h.Open(Chest, new ContainerContentEntry(Item, 0u));
        Assert.Equal(1f / 24f, h.Top.GetItem(0)!.CapacityFill);
    }

    // #87: the hover caption is the same name flavour the selection caption
    // shows - the composed name, material prefix included.
    [Fact]
    public void HoverCaption_carriesTheMaterialPrefix_andStaysPlainWithoutOne()
    {
        using var h = new Harness();
        h.Objects.AddOrUpdate(ItemTooltipCaptionNames.Material(Item));
        h.Objects.AddOrUpdate(ItemTooltipCaptionNames.Plain(Item + 1u));
        h.Open(Chest,
            new ContainerContentEntry(Item, 0u),
            new ContainerContentEntry(Item + 1u, 0u));

        Assert.Equal("Pyreal Scarab", h.Contents.GetItem(0)!.GetTooltipText());
        Assert.Equal("Bread Loaf", h.Contents.GetItem(1)!.GetTooltipText());
    }

    [Fact]
    public void InteractionStateChange_refreshesCellsInPlace_withoutRebuilding()
    {
        using var h = new Harness();
        h.Open(Chest, new ContainerContentEntry(Item, 0u));

        UiItemSlot cell = h.Contents.GetItem(0)!;
        UiItemSlot chest = h.Top.GetItem(0)!;

        h.Interaction.IncrementBusyCount();

        Assert.Same(cell, h.Contents.GetItem(0));
        Assert.Same(chest, h.Top.GetItem(0));
    }

    [Fact]
    public void AppraisalResponse_keepsContentsCellsAlive()
    {
        using var h = new Harness();
        h.Open(Chest, new ContainerContentEntry(Item, 0u));

        UiItemSlot cell = h.Contents.GetItem(0)!;
        UiItemSlot chest = h.Top.GetItem(0)!;

        var properties = new PropertyBundle();
        properties.Ints[1u] = 42;
        Assert.True(h.Objects.UpdateAppraisal(Item, properties, Array.Empty<uint>()));

        Assert.Same(cell, h.Contents.GetItem(0));
        Assert.Same(chest, h.Top.GetItem(0));
    }

    [Fact]
    public void AuthoritativeView_ShowsAuthoredStripAndPopulatesLists()
    {
        using var h = new Harness();
        h.Open(Chest, new ContainerContentEntry(Item, 0u));

        Assert.True(h.Window.IsVisible);
        Assert.Equal(Chest, h.Top.GetItem(0)!.ItemId);
        Assert.Equal(Item, h.Contents.GetItem(0)!.ItemId);
        Assert.True(h.Contents.HorizontalScroll);
        Assert.Same(h.Contents.Scroll,
            Assert.IsType<UiScrollbar>(h.Window.ContentRoot.Children.Last()).Model);
    }

    // A house hook holds one item, so it opens with one cell: the strips are
    // sized from the container's capacities.
    [Fact]
    public void Hook_showsOneItemCell_andNoPackCells()
    {
        using var h = new Harness();
        h.Open(Chest, itemsCapacity: 1, containersCapacity: 0);

        Assert.Equal(1, h.Contents.GetNumUIItems());
        Assert.Equal(0u, h.Contents.GetItem(0)!.ItemId);
        Assert.Equal(0, h.Containers.GetNumUIItems());

        h.Objects.AddOrUpdate(ItemTooltipCaptionNames.Plain(Item));
        h.Open(Chest, itemsCapacity: 1, containersCapacity: 0,
            new ContainerContentEntry(Item, 0u));

        Assert.Equal(1, h.Contents.GetNumUIItems());
        Assert.Equal(Item, h.Contents.GetItem(0)!.ItemId);
    }

    // The strip never shows more items than the container holds: the items
    // past its capacity get no cell, and the cells still refresh in place.
    [Fact]
    public void ItemsPastTheCapacity_getNoCell_andCellsRefreshInPlace()
    {
        using var h = new Harness();
        h.Objects.AddOrUpdate(ItemTooltipCaptionNames.Plain(Item));
        h.Objects.AddOrUpdate(ItemTooltipCaptionNames.Plain(Item + 1u));
        h.Open(Chest, itemsCapacity: 1, containersCapacity: 0,
            new ContainerContentEntry(Item, 0u),
            new ContainerContentEntry(Item + 1u, 0u));

        Assert.Equal(1, h.Contents.GetNumUIItems());
        UiItemSlot cell = h.Contents.GetItem(0)!;
        Assert.Equal(Item, cell.ItemId);

        var properties = new PropertyBundle();
        properties.Ints[1u] = 42;
        Assert.True(h.Objects.UpdateAppraisal(Item, properties, Array.Empty<uint>()));

        Assert.Same(cell, h.Contents.GetItem(0));
    }

    // A container that is not openable has capacity 0 whatever it sends: no
    // item cells and no pack cells, even with something inside.
    [Fact]
    public void NotOpenableContainer_showsNoCells()
    {
        using var h = new Harness { ContainerFlags = PublicWeenieFlags.None };
        h.Objects.AddOrUpdate(ItemTooltipCaptionNames.Plain(Item));
        h.Open(Chest, itemsCapacity: 24, containersCapacity: 7,
            new ContainerContentEntry(Item, 0u));

        Assert.Equal(0, h.Contents.GetNumUIItems());
        Assert.Equal(0, h.Containers.GetNumUIItems());
    }

    // The player is the one container whose capacity holds without the
    // openable flag.
    [Fact]
    public void ThePlayer_keepsItsCapacity_withoutTheOpenableFlag()
    {
        using var h = new Harness { ContainerFlags = PublicWeenieFlags.Player };
        h.Open(Player, itemsCapacity: 3, containersCapacity: 2);

        Assert.Equal(3, h.Contents.GetNumUIItems());
        Assert.Equal(2, h.Containers.GetNumUIItems());
    }

    // A corpse sends its capacities like any container (here 120 items and
    // 10 packs): the item strip gets 120 cells and scrolls along them, the
    // pack strip 10.
    [Fact]
    public void Corpse_getsOneCellPerUnitOfCapacity_andTheItemStripScrolls()
    {
        using var h = new Harness
        {
            ContainerFlags = PublicWeenieFlags.Openable | PublicWeenieFlags.Corpse,
        };
        h.Objects.AddOrUpdate(ItemTooltipCaptionNames.Plain(Item));
        h.Open(Chest, itemsCapacity: 120, containersCapacity: 10,
            new ContainerContentEntry(Item, 0u));

        Assert.Equal(120, h.Contents.GetNumUIItems());
        Assert.Equal(Item, h.Contents.GetItem(0)!.ItemId);
        Assert.Equal(0u, h.Contents.GetItem(119)!.ItemId);
        Assert.Equal(10, h.Containers.GetNumUIItems());

        Assert.Equal(120 * 32, h.Contents.Scroll.ContentHeight);
        Assert.Equal(784, h.Contents.Scroll.ViewHeight);
        Assert.True(h.Contents.GetItem(0)!.Visible);
        Assert.False(h.Contents.GetItem(119)!.Visible);

        h.Contents.ScrollItemIntoView(119);
        h.Contents.LayoutCells();

        Assert.Equal(120 * 32 - 784, h.Contents.Scroll.ScrollY);
        Assert.True(h.Contents.GetItem(119)!.Visible);
        Assert.Equal(784f - 32f, h.Contents.GetItem(119)!.Left);
        Assert.False(h.Contents.GetItem(0)!.Visible);
    }

    // A negative capacity means no limit: the strips fill the window.
    [Fact]
    public void UnlimitedContainer_fillsTheVisibleWidth()
    {
        using var h = new Harness();
        h.Open(Chest, itemsCapacity: -1, containersCapacity: -1);

        Assert.Equal(24, h.Contents.GetNumUIItems());
        Assert.Equal(18, h.Containers.GetNumUIItems());
    }

    [Fact]
    public void DoubleClickLoot_UsesSharedItemPolicyAndPicksUpItem()
    {
        using var h = new Harness();
        h.Open(Chest, new ContainerContentEntry(Item, 0u));

        h.Contents.GetItem(0)!.DoubleClicked!();

        Assert.Equal(new uint[] { Item }, h.Pickups);
    }

    [Fact]
    public void RightClickLoot_selectsAndExaminesWithoutPickingUp()
    {
        using var h = new Harness();
        h.Open(Chest, new ContainerContentEntry(Item, 0u));

        UiItemSlot cell = h.Contents.GetItem(0)!;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.RightClick));

        Assert.Equal(Item, h.Selection.SelectedObjectId);
        Assert.Equal(new uint[] { Item }, h.Examines);
        Assert.Empty(h.Pickups);
    }

    [Fact]
    public void MouseDownLoot_selectsBeforeCompletedClick()
    {
        using var h = new Harness();
        h.Open(Chest, new ContainerContentEntry(Item, 0u));

        UiItemSlot cell = h.Contents.GetItem(0)!;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.MouseDown));

        Assert.Equal(Item, h.Selection.SelectedObjectId);
        Assert.True(cell.Selected);
        Assert.Empty(h.Pickups);
    }

    [Fact]
    public void CloseAndRangeExit_SendUseOnceWithoutNoLongerViewing()
    {
        using var h = new Harness();
        h.Open();
        h.InRange = false;

        h.Controller.Tick();
        h.Controller.Tick();

        Assert.Equal(new uint[] { Chest }, h.Uses);
        Assert.Empty(h.NoLonger);
        Assert.False(h.Window.IsVisible);

        h.State.ApplyClose(Chest);
        Assert.Empty(h.NoLonger);
    }

    [Fact]
    public void Replacement_SendsNoLongerViewingForPreviousRootExactlyOnce()
    {
        using var h = new Harness();
        h.Open(Chest);
        h.Open(0x70000002u);

        Assert.Equal(new uint[] { Chest }, h.NoLonger);
        Assert.Equal(0x70000002u, h.State.CurrentContainerId);
        Assert.True(h.Window.IsVisible);
    }

    [Fact]
    public void ReplacementRequest_HidesOldViewWithoutSendingRangeCloseUse()
    {
        using var h = new Harness();
        const uint replacement = 0x70000002u;
        h.Open(Chest);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = replacement,
            Type = ItemType.Container,
            ItemsCapacity = 24,
            Useability = ItemUseability.Remote,
        });

        h.State.RequestOpen(replacement);
        h.InRange = false;
        h.Controller.Tick();

        Assert.Equal(0u, h.State.CurrentContainerId);
        Assert.Equal(replacement, h.State.RequestedContainerId);
        Assert.False(h.Window.IsVisible);
        Assert.Empty(h.Uses);
        Assert.Equal(new uint[] { Chest }, h.NoLonger);

        h.State.ApplyViewContents(replacement);
        Assert.Equal(replacement, h.State.CurrentContainerId);
        Assert.True(h.Window.IsVisible);
    }

    [Fact]
    public void DropOwnedPartialStackIntoChest_UsesSplitRequestWithoutLocalMove()
    {
        using var h = new Harness();
        h.Open();
        var item = new ClientObject { ObjectId = Item, StackSize = 5, Type = ItemType.Misc };
        h.Objects.AddOrUpdate(item);
        h.Objects.MoveItem(Item, Player, 0);
        h.Selection.Select(Item, SelectionChangeSource.Inventory);
        h.Split.Reset(5u, 2u);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Inventory };
        source.SetItem(Item, 0u);
        var payload = new ItemDragPayload(Item, ItemDragSource.Inventory, 0, source);
        UiItemSlot target = h.Contents.GetItem(0)!;

        h.Controller.HandleDropRelease(h.Contents, target, payload);

        Assert.Equal(new[] { (Item, Chest, 0u, 2u) }, h.Splits);
        Assert.Equal(Player, h.Objects.Get(Item)!.ContainerId);
        Assert.Empty(h.Puts);
    }

    [Fact]
    public void PendingPickupRejectsOwnedDropIntoChest()
    {
        using var h = new Harness();
        h.Open();
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = Item, Type = ItemType.Misc });
        h.Objects.MoveItem(Item, Player, 0);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Inventory };
        source.SetItem(Item, 0u);

        Assert.True(h.Interaction.PlaceWorldItemInBackpack(0x70000A01u));
        h.Controller.HandleDropRelease(
            h.Contents,
            h.Contents.GetItem(0)!,
            new ItemDragPayload(Item, ItemDragSource.Inventory, 0, source));

        Assert.Empty(h.Puts);
        Assert.Equal(Player, h.Objects.Get(Item)!.ContainerId);
        Assert.True(h.Interaction.TryGetPendingBackpackPlacement(0x70000A01u, out _));
    }

    [Fact]
    public void PendingPickupRejectsPartialStackSplitIntoChest()
    {
        using var h = new Harness();
        h.Open();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Item,
            StackSize = 5,
            StackSizeMax = 100,
            Type = ItemType.Misc,
        });
        h.Objects.MoveItem(Item, Player, 0);
        h.Selection.Select(Item, SelectionChangeSource.Inventory);
        h.Split.Reset(5u, 2u);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Inventory };
        source.SetItem(Item, 0u);

        Assert.True(h.Interaction.PlaceWorldItemInBackpack(0x70000A02u));
        h.Controller.HandleDropRelease(
            h.Contents,
            h.Contents.GetItem(0)!,
            new ItemDragPayload(Item, ItemDragSource.Inventory, 0, source));

        Assert.Empty(h.Splits);
        Assert.Equal(Player, h.Objects.Get(Item)!.ContainerId);
        Assert.True(h.Interaction.TryGetPendingBackpackPlacement(0x70000A02u, out _));
    }

    [Fact]
    public void ExternalPutOwnsGlobalGateUntilMatchingServerMove()
    {
        using var h = new Harness();
        const uint pickup = 0x70000A03u;
        h.Open();
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = Item, Type = ItemType.Misc });
        h.Objects.MoveItem(Item, Player, 0);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Inventory };
        source.SetItem(Item, 0u);

        h.Controller.HandleDropRelease(
            h.Contents,
            h.Contents.GetItem(0)!,
            new ItemDragPayload(Item, ItemDragSource.Inventory, 0, source));
        Assert.True(h.Interaction.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(Item, pending.ItemId);

        Assert.True(h.Interaction.PlaceWorldItemInBackpack(pickup));
        Assert.Empty(h.Pickups);

        h.Objects.ApplyConfirmedServerMove(Item, Chest, 0u, 0);
        Assert.False(h.Interaction.TryGetPendingInventoryRequest(out _));
        Assert.True(h.Interaction.PlaceWorldItemInBackpack(pickup));
        Assert.Equal(new uint[] { pickup }, h.Pickups);
    }

    // ── OpenAC #250: an item from the pack lands left of the chest item it
    //    was dropped on.

    [Fact]
    public void DropOwnedItemIntoChest_landsLeftOfTheItemDroppedOn()
    {
        using var h = new Harness();
        const uint first = 0x70000101u;
        const uint second = 0x70000102u;
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = first, Type = ItemType.Misc });
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = second, Type = ItemType.Misc });
        h.Open(Chest, new ContainerContentEntry(first, 0u), new ContainerContentEntry(second, 0u));
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = Item, Type = ItemType.Misc });
        h.Objects.MoveItem(Item, Player, 0);
        var source = new UiItemSlot { SourceKind = ItemDragSource.Inventory };
        source.SetItem(Item, 0u);
        var payload = new ItemDragPayload(Item, ItemDragSource.Inventory, 0, source);

        h.Controller.HandleDropRelease(h.Contents, h.Contents.GetItem(1)!, payload);

        Assert.Equal(new[] { (Item, Chest, 1) }, h.Puts);
    }

    // ── OpenAC #249: a reorder inside the chest lands left of the item it
    //    was dropped on; the server closes the old slot up first.

    [Fact]
    public void ReorderInsideChest_landsLeftOfTheItemDroppedOn()
    {
        using var h = new Harness();
        const uint first = 0x70000101u;
        const uint second = 0x70000102u;
        const uint third = 0x70000103u;
        foreach (uint id in new[] { first, second, third })
            h.Objects.AddOrUpdate(new ClientObject { ObjectId = id, Type = ItemType.Misc, ContainerId = Chest });
        h.Open(Chest,
            new ContainerContentEntry(first, 0u),
            new ContainerContentEntry(second, 0u),
            new ContainerContentEntry(third, 0u));
        var source = new UiItemSlot { SourceKind = ItemDragSource.Inventory };
        source.SetItem(first, 0u);
        var payload = new ItemDragPayload(first, ItemDragSource.Inventory, 0, source);

        h.Controller.HandleDropRelease(h.Contents, h.Contents.GetItem(1)!, payload);
        Assert.Empty(h.Puts);                                  // already left of the second

        h.Controller.HandleDropRelease(h.Contents, h.Contents.GetItem(2)!, payload);
        Assert.Equal(new[] { (first, Chest, 1) }, h.Puts);     // 1 2 3 -> 2 1 3
    }
}
