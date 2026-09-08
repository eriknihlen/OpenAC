using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.Tests.UI.Layout;
using AcDream.Core.Combat;
using AcDream.Core.Items;

namespace AcDream.App.Tests.UI;

public sealed class CursorFeedbackControllerTests
{
    private const uint Player = 0x50000001u;
    private const uint Pack = 0x50000010u;
    private const uint Source = 0x50000020u;
    private const uint Target = 0x50000021u;
    private const uint HealthKitUseability = 0x00220008u;

    [Fact]
    public void DragState_winsOverResizeAndUsesAcceptRejectCursors()
    {
        var c = new CursorFeedbackController();

        var accept = c.Resolve(new CursorFeedbackSnapshot(
            DragPayload: new object(),
            DragAccept: UiItemSlot.DragAcceptState.Accept,
            HoverResizeEdges: ResizeEdges.Right));
        var reject = c.Resolve(new CursorFeedbackSnapshot(
            DragPayload: new object(),
            DragAccept: UiItemSlot.DragAcceptState.Reject,
            HoverResizeEdges: ResizeEdges.Right));

        Assert.Equal(CursorFeedbackKind.DragAccept, accept.Kind);
        Assert.Equal(CursorFeedbackKind.DragReject, reject.Kind);
    }

    [Theory]
    [InlineData(ResizeEdges.Left, CursorFeedbackKind.ResizeHorizontal, 0x06006128u)]
    [InlineData(ResizeEdges.Right, CursorFeedbackKind.ResizeHorizontal, 0x06006128u)]
    [InlineData(ResizeEdges.Top, CursorFeedbackKind.ResizeVertical, 0x06005E66u)]
    [InlineData(ResizeEdges.Bottom, CursorFeedbackKind.ResizeVertical, 0x06005E66u)]
    [InlineData(ResizeEdges.Left | ResizeEdges.Top, CursorFeedbackKind.ResizeDiagonalNwse, 0x06006126u)]
    [InlineData(ResizeEdges.Right | ResizeEdges.Bottom, CursorFeedbackKind.ResizeDiagonalNwse, 0x06006126u)]
    [InlineData(ResizeEdges.Left | ResizeEdges.Bottom, CursorFeedbackKind.ResizeDiagonalNesw, 0x06006127u)]
    [InlineData(ResizeEdges.Right | ResizeEdges.Top, CursorFeedbackKind.ResizeDiagonalNesw, 0x06006127u)]
    public void ResizeEdges_chooseExpectedRetailCursor(
        ResizeEdges edges,
        CursorFeedbackKind expected,
        uint expectedDid)
    {
        var c = new CursorFeedbackController();

        var feedback = c.Resolve(new CursorFeedbackSnapshot(HoverResizeEdges: edges));

        Assert.Equal(expected, feedback.Kind);
        Assert.Equal(new UiCursorMedia(expectedDid, 16, 16), feedback.Cursor);
    }

    [Fact]
    public void MoveAndTextHover_useDedicatedCursorsWhenNoHigherPriorityState()
    {
        var c = new CursorFeedbackController();

        Assert.Equal(CursorFeedbackKind.WindowMove,
            c.Resolve(new CursorFeedbackSnapshot(HoverWindowMove: true)).Kind);
        Assert.Equal(new UiCursorMedia(0x06006119u, 16, 16),
            c.Resolve(new CursorFeedbackSnapshot(HoverWindowMove: true)).Cursor);
        Assert.Equal(CursorFeedbackKind.Text,
            c.Resolve(new CursorFeedbackSnapshot(HoverTextEdit: true)).Kind);
    }

    [Fact]
    public void ActiveSyntheticResize_keepsControlCursorOverAuthoredCapturedElement()
    {
        var root = new UiRoot { Width = 400, Height = 300 };
        var window = new UiPanel
        {
            Left = 50,
            Top = 40,
            Width = 150,
            Height = 100,
            Draggable = true,
            Resizable = true,
        };
        var content = new UiPanel { Width = 150, Height = 100 };
        var authored = new UiCursorMedia(0x06009999u, 3, 4);
        content.SetStateCursors(new Dictionary<string, UiCursorMedia> { [""] = authored });
        window.AddChild(content);
        root.AddChild(window);
        var controller = new CursorFeedbackController();

        root.OnMouseMove(199, 90); // inside the five-pixel right-edge resizebar region
        Assert.Equal(new UiCursorMedia(0x06006128u, 16, 16), controller.Update(root).Cursor);

        root.OnMouseDown(UiMouseButton.Left, 199, 90);
        root.OnMouseMove(100, 90);

        CursorFeedback active = controller.Update(root);
        Assert.Equal(CursorFeedbackKind.ResizeHorizontal, active.Kind);
        Assert.Equal(new UiCursorMedia(0x06006128u, 16, 16), active.Cursor);
    }

    [Fact]
    public void CornerGrip_ShowsTheDiagonalResizeCursor_NotTheStraightOne()
    {
        var root = new UiRoot { Width = 400, Height = 300 };
        var window = new UiPanel
        {
            Left = 50, Top = 40, Width = 150, Height = 100,
            Draggable = true, Resizable = true,
        };
        var grip = new UiResizeGrip
        {
            BorderLocation = UiResizeGrip.Border.LowerRight,
            Left = 145, Top = 95, Width = 5, Height = 5,
        };
        window.AddChild(grip);
        root.AddChild(window);
        var controller = new CursorFeedbackController();

        var gs = grip.ScreenPosition;
        root.OnMouseMove((int)(gs.X + 2), (int)(gs.Y + 2));

        CursorFeedback feedback = controller.Update(root);
        Assert.Equal(CursorFeedbackKind.ResizeDiagonalNwse, feedback.Kind);
        Assert.Equal(new UiCursorMedia(0x06006126u, 16, 16), feedback.Cursor);
    }

    [Fact]
    public void EdgeGrip_ShowsTheStraightResizeCursor()
    {
        var root = new UiRoot { Width = 400, Height = 300 };
        var window = new UiPanel
        {
            Left = 50, Top = 40, Width = 150, Height = 100,
            Draggable = true, Resizable = true,
        };
        var grip = new UiResizeGrip
        {
            BorderLocation = UiResizeGrip.Border.Bottom,
            Left = 5, Top = 95, Width = 140, Height = 5,
        };
        window.AddChild(grip);
        root.AddChild(window);
        var controller = new CursorFeedbackController();

        var gs = grip.ScreenPosition;
        root.OnMouseMove((int)(gs.X + 2), (int)(gs.Y + 2));

        CursorFeedback feedback = controller.Update(root);
        Assert.Equal(CursorFeedbackKind.ResizeVertical, feedback.Kind);
        Assert.Equal(new UiCursorMedia(0x06005E66u, 16, 16), feedback.Cursor);
    }

    [Fact]
    public void AuthoredWidgetCursor_winsUntilSyntheticWindowMoveStarts()
    {
        var root = new UiRoot { Width = 400, Height = 300 };
        var window = new UiPanel
        {
            Left = 50,
            Top = 40,
            Width = 150,
            Height = 100,
            Draggable = true,
        };
        var dragRegion = new UiPanel { Width = 150, Height = 100 };
        var authored = new UiCursorMedia(0x06008888u, 7, 8);
        dragRegion.SetStateCursors(new Dictionary<string, UiCursorMedia> { [""] = authored });
        window.AddChild(dragRegion);
        root.AddChild(window);
        var controller = new CursorFeedbackController();

        root.OnMouseMove(100, 90);
        Assert.Equal(authored, controller.Update(root).Cursor);

        root.OnMouseDown(UiMouseButton.Left, 100, 90);
        Assert.Equal(new UiCursorMedia(0x06006119u, 16, 16), controller.Update(root).Cursor);

        root.OnMouseMove(300, 200);
        Assert.Equal(new UiCursorMedia(0x06006119u, 16, 16), controller.Update(root).Cursor);
    }

    [Fact]
    public void UiLock_suppressesAuthoredMoveHandleCursor()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var window = new UiPanel { Left = 100, Top = 500, Width = 610, Height = 90 };
        var handle = new UiPanel
        {
            Left = 5, Top = 0, Width = 600, Height = 5,
            WindowMoveHandle = true,
        };
        var authored = new UiCursorMedia(0x06007777u, 2, 2);
        handle.SetStateCursors(new Dictionary<string, UiCursorMedia> { [""] = authored });
        window.AddChild(handle);
        root.AddChild(window);
        var controller = new CursorFeedbackController();

        root.OnMouseMove(110, 502);
        Assert.Equal(authored, controller.Update(root).Cursor);

        root.UiLocked = true;
        Assert.NotEqual(authored, controller.Update(root).Cursor);
    }

    [Fact]
    public void UiLock_suppressesSyntheticWindowControlCursors()
    {
        var root = new UiRoot { Width = 400, Height = 300, UiLocked = true };
        root.AddChild(new UiPanel
        {
            Left = 50,
            Top = 40,
            Width = 150,
            Height = 100,
            Draggable = true,
            Resizable = true,
        });
        root.OnMouseMove(199, 90);

        CursorFeedback feedback = new CursorFeedbackController().Update(root);

        Assert.Equal(CursorFeedbackKind.Default, feedback.Kind);
        Assert.False(feedback.Cursor.IsValid);
    }

    [Fact]
    public void TargetMode_reportsValidInvalidAndPendingTargets()
    {
        var objects = SeedTargetObjects();
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
        var c = new CursorFeedbackController(interaction);

        Assert.True(interaction.ActivateItem(Source));

        Assert.Equal(CursorFeedbackKind.TargetValid,
            c.Resolve(new CursorFeedbackSnapshot(
                HoverUi: true,
                HoverTargetGuid: Player,
                HoverTargetCompatible: true)).Kind);
        Assert.Equal(CursorFeedbackKind.TargetInvalid,
            c.Resolve(new CursorFeedbackSnapshot(
                HoverUi: true,
                HoverTargetGuid: 0x5000BADu,
                HoverTargetCompatible: false)).Kind);
        Assert.Equal(CursorFeedbackKind.TargetPending,
            c.Resolve(new CursorFeedbackSnapshot(HoverUi: true)).Kind);
        Assert.Equal(CursorFeedbackKind.TargetPending,
            c.Resolve(new CursorFeedbackSnapshot()).Kind);
    }

    [Fact]
    public void UpdateFromRoot_slotItemDrivesGlobalTargetCursor_withoutInventingWidgetState()
    {
        var objects = SeedTargetObjects();
        objects.Get(Source)!.TargetType = (uint)ItemType.Misc;   // a tool that targets items
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
        var root = new UiRoot { Width = 800, Height = 600 };
        var slot = new UiItemSlot
        {
            Left = 10,
            Top = 10,
            Width = 32,
            Height = 32,
        };
        var acceptCursor = new UiCursorMedia(0x06009999u, 11, 12);
        slot.SetStateCursors(new Dictionary<string, UiCursorMedia>
        {
            ["Drag_rollover_accept"] = acceptCursor,
        });
        slot.SetItem(Target, iconTexture: 1u);
        root.AddChild(slot);
        root.OnMouseMove(20, 20);
        var c = new CursorFeedbackController(interaction);

        Assert.True(interaction.ActivateItem(Source));

        var feedback = c.Update(root);
        Assert.Equal(CursorFeedbackKind.TargetValid, feedback.Kind);
        Assert.Equal(RetailGlobalCursorKind.TargetValid, feedback.GlobalKind);
        Assert.False(feedback.Cursor.IsValid);
    }

    [Fact]
    public void UpdateFromRoot_HoveringAnItemSlot_ShowsFoundCursor_InOrdinaryPeaceMode()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var slot = new UiItemSlot { Left = 10, Top = 10, Width = 32, Height = 32 };
        slot.SetItem(Target, iconTexture: 1u);
        root.AddChild(slot);
        root.OnMouseMove(20, 20);
        var c = new CursorFeedbackController();   // no ItemInteractionController — TargetMode.None throughout

        var feedback = c.Update(root);

        Assert.Equal(RetailGlobalCursorKind.DefaultFound, feedback.GlobalKind);
    }

    [Fact]
    public void UpdateFromRoot_HoveringAnItemSlot_ShowsFoundCursor_InCombatMode()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var slot = new UiItemSlot { Left = 10, Top = 10, Width = 32, Height = 32 };
        slot.SetItem(Target, iconTexture: 1u);
        root.AddChild(slot);
        root.OnMouseMove(20, 20);
        var c = new CursorFeedbackController(combatModeProvider: () => CombatMode.Melee);

        var feedback = c.Update(root);

        Assert.Equal(RetailGlobalCursorKind.MeleeOrMissileFound, feedback.GlobalKind);
    }

    [Fact]
    public void UpdateFromRoot_worldProviderContinuesBehindNonItemUi()
    {
        var objects = SeedTargetObjects();
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
        var root = new UiRoot { Width = 800, Height = 600 };   // nothing under the mouse
        root.OnMouseMove(400, 300);
        var c = new CursorFeedbackController(interaction, worldTargetProvider: () => Player);

        Assert.True(interaction.ActivateItem(Source));

        Assert.Equal(CursorFeedbackKind.TargetValid, c.Update(root).Kind);

        var panel = new UiPanel { Left = 390, Top = 290, Width = 40, Height = 40 };
        root.AddChild(panel);
        Assert.Equal(CursorFeedbackKind.TargetValid, c.Update(root).Kind);
    }

    [Fact]
    public void UpdateFromRoot_toolbarBackpackRepresentsSelfAndShowsFoundCursorWithoutWorldHit()
    {
        ImportedLayout toolbar = FixtureLoader.LoadToolbar();
        using ToolbarController controller = ToolbarController.Bind(
            toolbar,
            new ClientObjectTable(),
            new ShortcutStore(),
            iconIds: static (_, _, _, _, _) => 0u,
            useItem: static _ => { },
            playerGuid: () => Player);
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(toolbar.Root);
        var backpack = Assert.IsType<UiButton>(toolbar.FindElement(0x100001B1u));
        System.Numerics.Vector2 position = backpack.ScreenPosition;
        root.OnMouseMove(
            (int)(position.X + backpack.Width * 0.5f),
            (int)(position.Y + backpack.Height * 0.5f));
        var c = new CursorFeedbackController(worldTargetProvider: () => 0u);

        CursorFeedback feedback = c.Update(root);

        Assert.Equal(CursorFeedbackKind.Default, feedback.Kind);
        Assert.Equal(RetailGlobalCursorKind.DefaultFound, feedback.GlobalKind);
    }

    [Fact]
    public void GenericUseAndExamineModes_driveRetailGlobalCursors()
    {
        var objects = SeedTargetObjects();
        var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null);
        var c = new CursorFeedbackController(interaction);

        Assert.True(interaction.UseSelectedOrEnterMode(0));
        Assert.Equal(RetailGlobalCursorKind.Use,
            c.Resolve(new CursorFeedbackSnapshot()).GlobalKind);
        Assert.Equal(RetailGlobalCursorKind.UseFound,
            c.Resolve(new CursorFeedbackSnapshot(HoverTargetGuid: Target)).GlobalKind);

        interaction.CancelTargetMode();
        Assert.True(interaction.ExamineSelectedOrEnterMode(0));
        Assert.Equal(RetailGlobalCursorKind.Examine,
            c.Resolve(new CursorFeedbackSnapshot()).GlobalKind);
        Assert.Equal(RetailGlobalCursorKind.ExamineFound,
            c.Resolve(new CursorFeedbackSnapshot(HoverTargetGuid: Target)).GlobalKind);
    }

    public static TheoryData<CursorFeedbackSnapshot, RetailGlobalCursorKind> GlobalCursorCases => new()
    {
        { new(), RetailGlobalCursorKind.Default },
        { new(HoverTargetGuid: Target), RetailGlobalCursorKind.DefaultFound },
        { new(CombatMode: CombatMode.Melee), RetailGlobalCursorKind.MeleeOrMissile },
        { new(HoverTargetGuid: Target, CombatMode: CombatMode.Melee), RetailGlobalCursorKind.MeleeOrMissileFound },
        { new(CombatMode: CombatMode.Missile), RetailGlobalCursorKind.MeleeOrMissile },
        { new(CombatMode: CombatMode.Magic), RetailGlobalCursorKind.Magic },
        { new(HoverTargetGuid: Target, CombatMode: CombatMode.Magic), RetailGlobalCursorKind.MagicFound },
        { new(TargetMode: RetailCursorTargetMode.Examine), RetailGlobalCursorKind.Examine },
        { new(HoverTargetGuid: Target, TargetMode: RetailCursorTargetMode.Examine), RetailGlobalCursorKind.ExamineFound },
        { new(TargetMode: RetailCursorTargetMode.Use), RetailGlobalCursorKind.Use },
        { new(HoverTargetGuid: Target, TargetMode: RetailCursorTargetMode.Use), RetailGlobalCursorKind.UseFound },
        { new(BusyCount: 1), RetailGlobalCursorKind.Busy },
        { new(HoverTargetGuid: Target, BusyCount: 1), RetailGlobalCursorKind.BusyFound },
        { new(TargetMode: RetailCursorTargetMode.UseTarget), RetailGlobalCursorKind.TargetPending },
        { new(HoverTargetGuid: Target, HoverTargetCompatible: true, TargetMode: RetailCursorTargetMode.UseTarget), RetailGlobalCursorKind.TargetValid },
        { new(HoverTargetGuid: Target, HoverTargetCompatible: false, TargetMode: RetailCursorTargetMode.UseTarget), RetailGlobalCursorKind.TargetInvalid },
    };

    [Theory]
    [MemberData(nameof(GlobalCursorCases))]
    public void GlobalCursorDecisionTree_matchesUpdateCursorState(
        CursorFeedbackSnapshot snapshot,
        RetailGlobalCursorKind expected)
    {
        var c = new CursorFeedbackController();

        Assert.Equal(expected, c.Resolve(snapshot).GlobalKind);
    }

    [Fact]
    public void BusyGlobalCursor_winsOverTargetAndCombat()
    {
        var c = new CursorFeedbackController();
        var snapshot = new CursorFeedbackSnapshot(
            HoverTargetGuid: Target,
            HoverTargetCompatible: true,
            BusyCount: 1,
            TargetMode: RetailCursorTargetMode.UseTarget,
            CombatMode: CombatMode.Magic);

        Assert.Equal(RetailGlobalCursorKind.BusyFound, c.Resolve(snapshot).GlobalKind);
    }

    [Fact]
    public void WidgetCursorPrecedence_isCapturedThenHoveredThenGlobalDefault()
    {
        var root = new UiRoot { Width = 200, Height = 100 };
        var captured = new UiPanel { Left = 0, Top = 0, Width = 50, Height = 50 };
        var hovered = new UiPanel { Left = 60, Top = 0, Width = 50, Height = 50 };
        var capturedCursor = new UiCursorMedia(0x06001001u, 1, 2);
        var hoveredCursor = new UiCursorMedia(0x06001002u, 3, 4);
        captured.SetStateCursors(new Dictionary<string, UiCursorMedia> { [""] = capturedCursor });
        hovered.SetStateCursors(new Dictionary<string, UiCursorMedia> { [""] = hoveredCursor });
        root.AddChild(captured);
        root.AddChild(hovered);
        var c = new CursorFeedbackController();

        root.OnMouseMove(10, 10);
        root.OnMouseDown(UiMouseButton.Left, 10, 10);
        root.OnMouseMove(70, 10);
        Assert.Equal(capturedCursor, c.Update(root).Cursor);

        root.OnMouseUp(UiMouseButton.Left, 70, 10);
        Assert.Equal(hoveredCursor, c.Update(root).Cursor);

        root.OnMouseMove(150, 75);
        var fallback = c.Update(root);
        Assert.False(fallback.Cursor.IsValid);
        Assert.Equal(RetailGlobalCursorKind.Default, fallback.GlobalKind);
    }

    private static ClientObjectTable SeedTargetObjects()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Name = "Player",
            Type = ItemType.Creature,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Pack,
            Name = "Backpack",
            Type = ItemType.Container,
            ItemsCapacity = 24,
        });
        objects.MoveItem(Pack, Player, 0);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Source,
            Name = "Health Kit",
            Type = ItemType.Misc,
            Useability = HealthKitUseability,
            TargetType = (uint)ItemType.Creature,
        });
        objects.MoveItem(Source, Pack, 0);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Target,
            Name = "Target",
            Type = ItemType.Misc,
        });
        objects.MoveItem(Target, Pack, 1);
        return objects;
    }
}
