using System;
using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using Xunit;

namespace AcDream.App.Tests.UI;

/// <summary>A drag started on the world: pressing on your own character and
/// moving lifts your main pack, which can then be dropped on the hotbar; a
/// loose item on the ground lifts itself.</summary>
public class WorldDragTests
{
    private const uint Player = 0x50000A01u;
    private const uint MainPackDragIcon = 0x0600AA01u;
    private const uint MainPackIcon = 0x0600AA02u;
    private const uint Loot = 0x70000001u;
    private const uint LootIconId = 0x06001234u;
    private const uint LootDragIcon = 0x0600BB01u;
    private const uint LootIcon = 0x0600BB02u;

    private sealed class DropRecorder : UiElement
    {
        public object? Dropped;
        public override bool OnEvent(in UiEvent e)
        {
            if (e.Type == UiEventType.DropReleased)
                Dropped = e.Payload;
            return true;
        }
    }

    private static WorldDragStart SelfLift() => new(
        new ItemDragPayload(Player, ItemDragSource.Inventory, 0, SourceCell: null),
        (MainPackDragIcon, 32, 32));

    [Fact]
    public void A_press_on_the_world_that_moves_asks_for_the_lift_at_the_press_point()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var asked = new List<(int x, int y)>();
        root.WorldDragRequested = (x, y) => { asked.Add((x, y)); return SelfLift(); };

        root.OnMouseDown(UiMouseButton.Left, 300, 300);
        root.OnMouseMove(302, 301);
        Assert.Empty(asked);
        root.OnMouseMove(320, 310);

        Assert.Equal(new[] { (300, 300) }, asked);
        Assert.Same(root, root.DragSource);
        var payload = Assert.IsType<ItemDragPayload>(root.DragPayload);
        Assert.Equal(Player, payload.ObjId);
        Assert.Equal((MainPackDragIcon, 32, 32), root.DragGhostForTest);
    }

    [Fact]
    public void A_world_lift_dropped_on_an_element_reaches_it()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var bar = new DropRecorder { Left = 0, Top = 0, Width = 100, Height = 40 };
        root.AddChild(bar);
        root.WorldDragRequested = (_, _) => SelfLift();

        root.OnMouseDown(UiMouseButton.Left, 300, 300);
        root.OnMouseMove(200, 200);
        root.OnMouseMove(20, 20);
        root.OnMouseUp(UiMouseButton.Left, 20, 20);

        var dropped = Assert.IsType<ItemDragPayload>(bar.Dropped);
        Assert.Equal(Player, dropped.ObjId);
        Assert.Null(root.DragSource);
        Assert.Null(root.Captured);
    }

    [Fact]
    public void A_world_lift_released_on_the_world_goes_to_the_world()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        root.WorldDragRequested = (_, _) => SelfLift();
        object? outside = null;
        root.DragReleasedOutsideUi += (p, _, _) => outside = p;

        root.OnMouseDown(UiMouseButton.Left, 300, 300);
        root.OnMouseMove(350, 350);
        root.OnMouseUp(UiMouseButton.Left, 400, 400);

        Assert.IsType<ItemDragPayload>(outside);
        Assert.Null(root.DragSource);
    }

    [Fact]
    public void A_world_press_that_lifts_nothing_leaves_the_pointer_to_the_world()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        int asked = 0;
        root.WorldDragRequested = (_, _) => { asked++; return null; };
        var moves = new List<(int, int)>();
        root.WorldMouseMoveFallThrough += (x, y) => moves.Add((x, y));

        root.OnMouseDown(UiMouseButton.Left, 300, 300);
        root.OnMouseMove(320, 320);
        root.OnMouseMove(340, 340);

        Assert.Equal(1, asked);
        Assert.Null(root.DragSource);
        Assert.Equal(new[] { (320, 320), (340, 340) }, moves);
    }

    [Fact]
    public void Only_a_left_press_on_the_world_starts_a_world_drag()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var panel = new UiPanel { Left = 0, Top = 0, Width = 100, Height = 100 };
        root.AddChild(panel);
        int asked = 0;
        root.WorldDragRequested = (_, _) => { asked++; return SelfLift(); };

        root.OnMouseDown(UiMouseButton.Right, 300, 300);
        root.OnMouseMove(350, 350);
        root.OnMouseUp(UiMouseButton.Right, 350, 350);
        root.OnMouseDown(UiMouseButton.Left, 50, 50);
        root.OnMouseMove(90, 90);
        root.OnMouseUp(UiMouseButton.Left, 90, 90);
        root.OnMouseDown(UiMouseButton.Left, 300, 300);
        root.OnMouseUp(UiMouseButton.Left, 300, 300);
        root.OnMouseMove(350, 350);

        Assert.Equal(0, asked);
    }

    [Fact]
    public void Your_own_character_lifts_your_main_pack_out_of_combat()
    {
        WorldDragStart? start = WorldDrag.Build(
            Player, Player, outOfCombat: true, World(), NothingWaits, Icons, DragIcons);

        Assert.NotNull(start);
        var payload = Assert.IsType<ItemDragPayload>(start!.Value.Payload);
        Assert.Equal(Player, payload.ObjId);
        Assert.Equal(ItemDragSource.Inventory, payload.SourceKind);
        Assert.Equal(0, payload.SourceSlot);
        Assert.Null(payload.SourceCell);
        Assert.Equal((MainPackDragIcon, 32, 32), start.Value.Ghost);
    }

    [Fact]
    public void Without_a_drag_picture_the_lift_shows_the_pack_icon()
    {
        WorldDragStart? start = WorldDrag.Build(
            Player, Player, true, World(), NothingWaits, Icons, dragIconIds: null);

        Assert.Equal((MainPackIcon, 32, 32), start!.Value.Ghost);
    }

    // A loose item on the ground lifts itself as a ground drag, so a drop on
    // a pack or the backpack button picks it up.
    [Fact]
    public void A_loose_item_on_the_ground_lifts_itself_as_a_ground_drag()
    {
        WorldDragStart? start = WorldDrag.Build(
            Loot, Player, outOfCombat: true, World(), NothingWaits, Icons, DragIcons);

        Assert.NotNull(start);
        var payload = Assert.IsType<ItemDragPayload>(start!.Value.Payload);
        Assert.Equal(Loot, payload.ObjId);
        Assert.Equal(ItemDragSource.Ground, payload.SourceKind);
        Assert.Null(payload.SourceCell);
        Assert.Equal((LootDragIcon, 32, 32), start.Value.Ghost);

        WorldDragStart? plain = WorldDrag.Build(
            Loot, Player, outOfCombat: true, World(), NothingWaits, Icons, dragIconIds: null);
        Assert.Equal((LootIcon, 32, 32), plain!.Value.Ghost);
    }

    [Fact]
    public void Nothing_lifts_in_combat()
    {
        ClientObjectTable world = World();
        Assert.Null(WorldDrag.Build(Player, Player, outOfCombat: false, world, NothingWaits, Icons, DragIcons));
        Assert.Null(WorldDrag.Build(Loot, Player, outOfCombat: false, world, NothingWaits, Icons, DragIcons));
    }

    [Fact]
    public void Nothing_and_unknown_objects_lift_nothing()
    {
        ClientObjectTable world = World();
        Assert.Null(WorldDrag.Build(0u, Player, outOfCombat: true, world, NothingWaits, Icons, DragIcons));
        Assert.Null(WorldDrag.Build(
            0x70000999u, Player, outOfCombat: true, world, NothingWaits, Icons, DragIcons));
    }

    [Fact]
    public void A_stuck_object_or_another_creature_lifts_nothing()
    {
        ClientObjectTable stuck = World();
        stuck.Get(Loot)!.PublicWeenieBitfield = (uint)PublicWeenieFlags.Stuck;
        Assert.Null(WorldDrag.Build(Loot, Player, outOfCombat: true, stuck, NothingWaits, Icons, DragIcons));

        ClientObjectTable creature = World();
        creature.Get(Loot)!.Type = ItemType.Creature;
        Assert.Null(WorldDrag.Build(Loot, Player, outOfCombat: true, creature, NothingWaits, Icons, DragIcons));
    }

    [Fact]
    public void A_wielded_object_or_one_with_a_request_in_flight_lifts_nothing()
    {
        ClientObjectTable wielded = World();
        wielded.Get(Loot)!.WielderId = 0x50000B02u;
        Assert.Null(WorldDrag.Build(Loot, Player, outOfCombat: true, wielded, NothingWaits, Icons, DragIcons));

        Assert.Null(WorldDrag.Build(
            Loot, Player, outOfCombat: true, World(), id => id == Loot, Icons, DragIcons));
        Assert.Null(WorldDrag.Build(
            Player, Player, outOfCombat: true, World(), id => id == Player, Icons, DragIcons));
    }

    /// <summary>The player and a loose sword lying on the ground.</summary>
    private static ClientObjectTable World()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Type = ItemType.Creature,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Player,
        });
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = Loot,
            Type = ItemType.MeleeWeapon,
            IconId = LootIconId,
        });
        return table;
    }

    private static bool NothingWaits(uint id) => false;

    private static uint Icons(ItemType type, uint icon, uint underlay, uint overlay, uint effects)
        => type == ItemType.Container && icon == InventoryController.PlayerPackBaseIcon
            ? MainPackIcon
            : icon == LootIconId ? LootIcon : 0u;

    private static uint DragIcons(ItemType type, uint icon, uint underlay, uint overlay, uint effects)
        => type == ItemType.Container && icon == InventoryController.PlayerPackBaseIcon
            ? MainPackDragIcon
            : icon == LootIconId ? LootDragIcon : 0u;
}
