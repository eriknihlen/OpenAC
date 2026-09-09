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
        public readonly ItemInteractionController Interaction;
        public readonly ExternalContainerLifecycleController Lifecycle;
        public readonly ExternalContainerController Controller;
        public bool InRange = true;

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

            Interaction = new ItemInteractionController(
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
                Window);
            Screen.WindowManager.AttachController(WindowNames.ExternalContainer, Controller);
        }

        public void Open(uint id = Chest, params ContainerContentEntry[] contents)
        {
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = id,
                Type = ItemType.Container,
                ItemsCapacity = 24,
                Useability = ItemUseability.Remote,
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
}
