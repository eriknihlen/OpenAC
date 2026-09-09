using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public class DragDropSpineTests
{
    // A spy handler used across the spine tests.
    private sealed class SpyHandler : IItemListDragHandler
    {
        public ItemDragAcceptance Acceptance = ItemDragAcceptance.Accept;
        public (UiItemList list, UiItemSlot cell, ItemDragPayload payload)? LastOver;
        public (UiItemList list, UiItemSlot cell, ItemDragPayload payload)? LastDrop;
        public (UiItemList list, UiItemSlot cell, ItemDragPayload payload)? LastLift;
        public bool? WaitingAtLift;
        public void OnDragLift(UiItemList list, UiItemSlot cell, ItemDragPayload p)
        {
            WaitingAtLift = cell.WaitingVisual;
            LastLift = (list, cell, p);
        }

        public ItemDragAcceptance OnDragOver(UiItemList list, UiItemSlot cell, ItemDragPayload p)
        { LastOver = (list, cell, p); return Acceptance; }

        public void HandleDropRelease(UiItemList list, UiItemSlot cell, ItemDragPayload p)
        { LastDrop = (list, cell, p); }
    }

    [Fact]
    public void Payload_holdsAllFields()
    {
        var src = new UiItemSlot();
        var p = new ItemDragPayload(0x5001u, ItemDragSource.ShortcutBar, 3, src);
        Assert.Equal(0x5001u, p.ObjId);
        Assert.Equal(ItemDragSource.ShortcutBar, p.SourceKind);
        Assert.Equal(3, p.SourceSlot);
        Assert.Same(src, p.SourceCell);
    }

    [Fact]
    public void UiItemList_registerDragHandler_roundtrips()
    {
        var list = new UiItemList(_ => (0u, 0, 0));
        Assert.Null(list.DragHandler);
        var h = new SpyHandler();
        list.RegisterDragHandler(h);
        Assert.Same(h, list.DragHandler);
    }

    // ── UiItemSlot drag-source payload/ghost ────────────────────────────────
    [Fact]
    public void GetDragPayload_emptyCell_isNull()
        => Assert.Null(new UiItemSlot().GetDragPayload());

    [Fact]
    public void GetDragPayload_boundCell_snapshotsFields()
    {
        var cell = new UiItemSlot { SlotIndex = 4, SourceKind = ItemDragSource.ShortcutBar };
        cell.SetItem(0x5001u, 0x99u);
        var p = Assert.IsType<ItemDragPayload>(cell.GetDragPayload());
        Assert.Equal(0x5001u, p.ObjId);
        Assert.Equal(ItemDragSource.ShortcutBar, p.SourceKind);
        Assert.Equal(4, p.SourceSlot);
        Assert.Same(cell, p.SourceCell);
    }

    [Fact]
    public void GetDragGhost_emptyCell_isNull()
        => Assert.Null(new UiItemSlot().GetDragGhost());

    [Fact]
    public void GetDragGhost_boundCell_returnsIconTuple()
    {
        var cell = new UiItemSlot { Width = 32, Height = 32 };
        cell.SetItem(0x5001u, 0x99u);
        var g = cell.GetDragGhost();
        Assert.NotNull(g);
        Assert.Equal(0x99u, g!.Value.tex);
        Assert.Equal(32, g.Value.w);
        Assert.Equal(32, g.Value.h);
    }

    [Fact]
    public void GetDragGhost_prefersDedicatedUnderlayFreeTexture()
    {
        var cell = new UiItemSlot { Width = 36, Height = 36 };
        cell.SetItem(0x5001u, 0x99u, dragIconTexture: 0x77u);

        Assert.Equal((0x77u, 32, 32), cell.GetDragGhost());
        Assert.Equal(0x99u, cell.IconTexture);
    }

    private static (UiItemList list, UiItemSlot cell, SpyHandler h) ListWithHandler()
    {
        var list = new UiItemList(_ => (1u, 1, 1)); // non-zero resolve so overlay draw is harmless
        var h = new SpyHandler();
        list.RegisterDragHandler(h);
        return (list, list.Cell, h);
    }

    private static ItemDragPayload SomePayload()
        => new(0x5001u, ItemDragSource.ShortcutBar, 0, new UiItemSlot());

    [Fact]
    public void DragEnter_setsAcceptOverlay_whenHandlerAccepts()
    {
        var (_, cell, h) = ListWithHandler();
        h.Acceptance = ItemDragAcceptance.Accept;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DragEnter, Payload: SomePayload()));
        Assert.Equal(UiItemSlot.DragAcceptState.Accept, cell.DragAcceptVisual);
    }

    [Fact]
    public void DragEnter_setsRejectOverlay_whenHandlerRejects()
    {
        var (_, cell, h) = ListWithHandler();
        h.Acceptance = ItemDragAcceptance.Reject;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DragEnter, Payload: SomePayload()));
        Assert.Equal(UiItemSlot.DragAcceptState.Reject, cell.DragAcceptVisual);
    }

    [Fact]
    public void DragEnter_keepsNeutralOverlay_whenHandlerIgnoresAlias()
    {
        var (_, cell, h) = ListWithHandler();
        h.Acceptance = ItemDragAcceptance.None;
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DragEnter, Payload: SomePayload()));
        Assert.Equal(UiItemSlot.DragAcceptState.None, cell.DragAcceptVisual);
    }

    [Fact]
    public void DragOver_resetsOverlayToNeutral()
    {
        var (_, cell, h) = ListWithHandler();
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DragEnter, Payload: SomePayload()));
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DragOver, Payload: SomePayload()));
        Assert.Equal(UiItemSlot.DragAcceptState.None, cell.DragAcceptVisual);
    }

    [Fact]
    public void DropReleased_accepted_dispatchesToHandler()
    {
        var (list, cell, h) = ListWithHandler();
        var p = SomePayload();
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DropReleased, Data0: 1, Payload: p));
        Assert.NotNull(h.LastDrop);
        Assert.Same(list, h.LastDrop!.Value.list);
        Assert.Same(cell, h.LastDrop.Value.cell);
        Assert.Same(p, h.LastDrop.Value.payload);
    }

    [Fact]
    public void DropReleased_dispatchesToHandler_regardlessOfData0()
    {
        var (list, cell, h) = ListWithHandler();
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DropReleased, Data0: 0, Payload: SomePayload()));
        Assert.NotNull(h.LastDrop);
    }

    [Fact]
    public void DragBegin_callsHandlerOnDragLift()
    {
        var (list, cell, h) = ListWithHandler();
        var p = SomePayload();
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DragBegin, Payload: p));
        Assert.NotNull(h.LastLift);
        Assert.Same(list, h.LastLift!.Value.list);
        Assert.Same(cell, h.LastLift.Value.cell);
        Assert.Same(p, h.LastLift.Value.payload);
    }

    [Fact]
    public void Ghost_isSnapshottedAtBeginDrag_survivesSourceCellClearing()
    {
        var (root, _, cell) = RootWithBoundSlot(0x5001u);   // icon tex 0x99
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);                            // BeginDrag → snapshot ghost
        cell.Clear();                                        // simulate the lift emptying the source
        Assert.Equal((0x99u, 32, 32), root.DragGhostForTest);
    }

    [Fact]
    public void RootOwnedDrag_survivesProceduralSourceCellReplacement_untilRelease()
    {
        var (root, list, cell) = RootWithBoundSlot(0x5001u);
        object? released = null;
        root.DragReleasedOutsideUi += (payload, _, _) => released = payload;

        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);                            // BeginDrag → root owns ghost
        object payload = Assert.IsType<ItemDragPayload>(root.DragPayload);

        list.Flush();

        Assert.Null(cell.Parent);
        Assert.Same(cell, root.DragSource);
        Assert.Same(payload, root.DragPayload);
        Assert.Equal((0x99u, 32, 32), root.DragGhostForTest);
        Assert.Same(root, root.Captured);

        root.OnMouseMove(600, 500);
        root.OnMouseUp(UiMouseButton.Left, 600, 500);

        Assert.Same(payload, released);
        Assert.Null(root.DragSource);
        Assert.Null(root.DragPayload);
        Assert.Null(root.DragGhostForTest);
        Assert.Null(root.Captured);
    }

    [Fact]
    public void FinishDrag_overNothing_deliversNoDrop_butLiftStands()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var list = new UiItemList(_ => (1u, 1, 1)) { Left = 0, Top = 0, Width = 32, Height = 32 };
        list.Cell.Width = 32; list.Cell.Height = 32;
        list.Cell.SetItem(0x5001u, 0x99u);
        var h = new SpyHandler();
        list.RegisterDragHandler(h);
        root.AddChild(list);

        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);                  // BeginDrag → OnDragLift
        Assert.False(h.WaitingAtLift);
        Assert.True(list.Cell.WaitingVisual);
        root.OnMouseUp(UiMouseButton.Left, 600, 500); // release over empty space
        Assert.NotNull(h.LastLift);                // lift happened
        Assert.Null(h.LastDrop);                   // no drop dispatched (off-bar)
        Assert.Null(root.DragSource);
    }

    [Fact]
    public void InventoryDrag_ghostsSourceUntilRelease()
    {
        var (root, _, cell) = RootWithBoundSlot(0x5001u);
        cell.SourceKind = ItemDragSource.Inventory;
        cell.Selected = true;

        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);
        Assert.True(cell.WaitingVisual);
        Assert.True(cell.Selected); // selected indicator remains active above the waiting mesh

        root.OnMouseUp(UiMouseButton.Left, 600, 500);
        Assert.False(cell.WaitingVisual);
        Assert.True(cell.Selected);
    }

    [Fact]
    public void ShortcutDrag_doesNotGhostSource()
    {
        var (root, _, cell) = RootWithBoundSlot(0x5001u);
        cell.SourceKind = ItemDragSource.ShortcutBar;

        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);

        Assert.False(cell.WaitingVisual);
    }

    private static (UiRoot root, UiItemList list, UiItemSlot cell) RootWithBoundSlot(uint itemId)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var list = new UiItemList(_ => (1u, 1, 1)) { Left = 0, Top = 0, Width = 32, Height = 32 };
        list.Cell.Width = 32; list.Cell.Height = 32;
        if (itemId != 0) list.Cell.SetItem(itemId, 0x99u);
        root.AddChild(list);
        return (root, list, list.Cell);
    }

    private static (UiRoot root, UiItemList list, UiCatalogSlot cell)
        RootWithCatalogSlot(uint entryId)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var list = new UiItemList(_ => (1u, 1, 1))
        {
            Left = 0,
            Top = 0,
            Width = 32,
            Height = 32,
        };
        list.Flush();
        var cell = new UiCatalogSlot
        {
            EntryId = entryId,
            Width = 32,
            Height = 32,
            SpriteResolve = _ => (1u, 1, 1),
        };
        list.AddItem(cell);
        root.AddChild(list);
        return (root, list, cell);
    }

    [Fact]
    public void BeginDrag_arms_whenPayloadNonNull()
    {
        var (root, _, cell) = RootWithBoundSlot(0x5001u);
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);                  // >3px → promote to drag
        Assert.Same(cell, root.DragSource);
        Assert.IsType<ItemDragPayload>(root.DragPayload);
    }

    [Fact]
    public void BeginDrag_doesNotArm_whenPayloadNull_emptySlot()
    {
        var (root, _, _) = RootWithBoundSlot(0u);
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);
        Assert.Null(root.DragSource);               // never armed
    }

    [Fact]
    public void Click_withoutDrag_firesUse()
    {
        var (root, _, cell) = RootWithBoundSlot(0x5001u);
        bool used = false;
        cell.Clicked = () => used = true;
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseUp(UiMouseButton.Left, 10, 10);
        Assert.True(used);
    }

    [Fact]
    public void PhysicalItemPress_selectsBeforeRelease_thenClickActivates()
    {
        var (root, list, cell) = RootWithBoundSlot(0x5001u);
        var selected = new List<uint>();
        bool used = false;
        list.PrimaryItemPressed = item =>
        {
            selected.Add(item);
            return false;
        };
        cell.Clicked = () => used = true;

        root.OnMouseDown(UiMouseButton.Left, 10, 10);

        Assert.Equal(new uint[] { 0x5001u }, selected);
        Assert.False(used);

        root.OnMouseUp(UiMouseButton.Left, 10, 10);

        Assert.True(used);
    }

    [Fact]
    public void ConsumedPhysicalItemPress_suppressesCompletedActivation()
    {
        var (root, list, cell) = RootWithBoundSlot(0x5001u);
        bool used = false;
        bool doubleUsed = false;
        list.PrimaryItemPressed = _ => true;
        cell.Clicked = () => used = true;
        cell.DoubleClicked = () => doubleUsed = true;

        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseUp(UiMouseButton.Left, 10, 10);
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseUp(UiMouseButton.Left, 10, 10);

        Assert.False(used);
        Assert.False(doubleUsed);
    }

    [Fact]
    public void ConsumedPhysicalItemPress_cannotPromoteIntoDrag()
    {
        var (root, list, _) = RootWithBoundSlot(0x5001u);
        list.PrimaryItemPressed = _ => true;

        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);

        Assert.Null(root.DragSource);
        Assert.Null(root.DragPayload);
    }

    [Fact]
    public void RightClick_withoutDrag_requestsItemListAppraisal()
    {
        var (root, list, _) = RootWithBoundSlot(0x5001u);
        var examined = new List<uint>();
        list.ExamineItemRequested = examined.Add;

        root.OnMouseDown(UiMouseButton.Right, 10, 10);
        root.OnMouseUp(UiMouseButton.Right, 10, 10);

        Assert.Equal(new uint[] { 0x5001u }, examined);
        Assert.Null(root.DragSource);
    }

    [Fact]
    public void RightButtonMovement_cancelsAppraisal_andNeverStartsItemDrag()
    {
        var (root, list, _) = RootWithBoundSlot(0x5001u);
        var examined = new List<uint>();
        list.ExamineItemRequested = examined.Add;

        root.OnMouseDown(UiMouseButton.Right, 10, 10);
        root.OnMouseMove(14, 10);
        root.OnMouseUp(UiMouseButton.Right, 14, 10);

        Assert.Empty(examined);
        Assert.Null(root.DragSource);
    }

    [Fact]
    public void CatalogEntryPress_selectsBeforeRelease_withoutForgingItemIdentity()
    {
        var (root, list, cell) = RootWithCatalogSlot(42u);
        var selected = new List<uint>();
        list.PrimaryCatalogEntryPressed = selected.Add;

        root.OnMouseDown(UiMouseButton.Left, 10, 10);

        Assert.Equal(new uint[] { 42u }, selected);
        Assert.Equal(0u, cell.ItemId);
    }

    [Fact]
    public void CatalogEntryRightClick_requestsLocalCatalogExamination()
    {
        var (root, list, _) = RootWithCatalogSlot(42u);
        var examined = new List<uint>();
        list.ExamineCatalogEntryRequested = examined.Add;

        root.OnMouseDown(UiMouseButton.Right, 10, 10);
        root.OnMouseUp(UiMouseButton.Right, 10, 10);

        Assert.Equal(new uint[] { 42u }, examined);
    }

    [Fact]
    public void CompletedDrag_doesNotFireUse()
    {
        var (root, _, cell) = RootWithBoundSlot(0x5001u);
        bool used = false;
        cell.Clicked = () => used = true;
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(20, 10);                   // promote to drag
        root.OnMouseUp(UiMouseButton.Left, 20, 10);
        Assert.False(used);
    }

    [Fact]
    public void DragEnter_orphanCell_noList_defaultsToReject()
    {
        var cell = new UiItemSlot();   // no parent list → FindList() null
        cell.OnEvent(new UiEvent(0u, cell, UiEventType.DragEnter, Payload: SomePayload()));
        Assert.Equal(UiItemSlot.DragAcceptState.Reject, cell.DragAcceptVisual);
    }

    [Fact]
    public void DragEnter_listWithoutHandler_defaultsToReject()
    {
        var list = new UiItemList(_ => (1u, 1, 1));   // no RegisterDragHandler
        list.Cell.OnEvent(new UiEvent(0u, list.Cell, UiEventType.DragEnter, Payload: SomePayload()));
        Assert.Equal(UiItemSlot.DragAcceptState.Reject, list.Cell.DragAcceptVisual);
    }

    private static (UiRoot root, UiPanel frame, UiItemList list) DraggableFrameWithSlot(uint itemId)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel { Left = 10, Top = 300, Width = 200, Height = 60, Draggable = true };
        var list = new UiItemList(_ => (1u, 1, 1)) { Left = 5, Top = 5, Width = 32, Height = 32 };
        list.Cell.Width = 32; list.Cell.Height = 32;
        if (itemId != 0) list.Cell.SetItem(itemId, 0x99u);
        frame.AddChild(list);
        root.AddChild(frame);
        return (root, frame, list);
    }

    [Fact]
    public void OccupiedSlotInsideDraggableWindow_armsItemDrag_doesNotMoveWindow()
    {
        var (root, frame, list) = DraggableFrameWithSlot(0x5001u);
        // Slot screen rect = frame(10,300)+list(5,5) → (15,305)..(47,337). Press inside, drag >3px.
        root.OnMouseDown(UiMouseButton.Left, 20, 310);
        root.OnMouseMove(40, 310);
        Assert.Same(list.Cell, root.DragSource);   // item drag armed
        Assert.Equal(10f, frame.Left);             // window did NOT move
        Assert.Equal(300f, frame.Top);
    }

    [Fact]
    public void EmptySlotInsideDraggableWindow_movesWindow_notItemDrag()
    {
        var (root, frame, _) = DraggableFrameWithSlot(0u);   // empty slot → not a drag source
        root.OnMouseDown(UiMouseButton.Left, 20, 310);
        root.OnMouseMove(40, 310);
        Assert.Null(root.DragSource);              // no item drag
        Assert.Equal(30f, frame.Left);             // window moved (offX=20-10=10; new Left=40-10=30)
        Assert.Equal(300f, frame.Top);             // y unchanged (310-10=300)
    }

    [Fact]
    public void OccupiedNonDragSourceSlotInsideDraggableWindow_capturesClick_doesNotMoveWindow()
    {
        var (root, frame, list) = DraggableFrameWithSlot(0x5001u);
        list.Cell.AllowDragSource = false;   // vendor/salvage row shape
        bool clicked = false;
        list.Cell.Clicked = () => clicked = true;

        root.OnMouseDown(UiMouseButton.Left, 20, 310);
        root.OnMouseMove(40, 310);           // would promote to drag if armed
        Assert.Null(root.DragSource);        // never mints a drag payload
        Assert.Equal(10f, frame.Left);       // window did NOT move
        Assert.Equal(300f, frame.Top);

        root.OnMouseUp(UiMouseButton.Left, 40, 310);
        Assert.True(clicked);
    }

    [Fact]
    public void OccupiedNonDragSourceSlotInsideDraggableWindow_hoverDoesNotShowMoveCursor()
    {
        var (root, _, list) = DraggableFrameWithSlot(0x5001u);
        list.Cell.AllowDragSource = false;

        root.OnMouseMove(20, 310);   // hover over the vendor row, no press

        Assert.False(root.HoverWindowMove);
    }
}
