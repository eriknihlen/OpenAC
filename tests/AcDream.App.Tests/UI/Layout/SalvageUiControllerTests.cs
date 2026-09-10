using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;

namespace AcDream.App.Tests.UI.Layout;

public sealed class SalvageUiControllerTests
{
    private const uint Player = 100;
    private const uint Tool = 101;
    private sealed class Element : UiElement { }

    private sealed class Harness : IDisposable
    {
        public ClientObjectTable Objects { get; } = new();
        public UiItemList List { get; } = new() { Width = 320, Height = 32 };
        public UiButton Button { get; } = new(new ElementInfo { Id = SalvageUiController.SalvageButtonId, Type = 1 }, _ => (0u, 0, 0));
        public SalvageUiController Controller { get; }
        public bool Visible;
        public bool Multiple = true;
        public bool SendSucceeds = true;
        public List<(uint Tool, uint[] Items)> Sends { get; } = [];
        public List<string> Errors { get; } = [];

        public Harness()
        {
            var root = new Element();
            root.AddChild(List);
            root.AddChild(Button);
            var layout = new ImportedLayout(root, new()
            {
                [SalvageUiController.ItemListId] = List,
                [SalvageUiController.SalvageButtonId] = Button,
            });
            Objects.AddOrUpdate(new ClientObject { ObjectId = Player, Type = ItemType.Creature });
            Objects.AddOrUpdate(new ClientObject { ObjectId = Tool, Type = ItemType.TinkeringTool });
            Objects.MoveItem(Tool, Player, 0);
            Controller = SalvageUiController.Bind(layout, new(
                Objects, id => Objects.IsOwnedByObject(id, Player),
                (tool, items) => { Sends.Add((tool, items.ToArray())); return SendSucceeds; },
                () => Multiple, (_, icon, _, _, _) => icon,
                visible => Visible = visible, Errors.Add))!;
            Controller.Open(Tool);
        }

        public ClientObject Item(uint id, uint material = 60, int structure = 0, uint container = Player)
        {
            var item = new ClientObject { ObjectId = id, MaterialType = material, Structure = structure, IconId = 123 };
            Objects.AddOrUpdate(item);
            Objects.MoveItem(id, container, 0);
            return item;
        }

        public void Dispose() => Controller.Dispose();
    }

    [Fact]
    public void Eligible_drop_marks_item_and_submit_sends_once_without_removing_inventory()
    {
        using var h = new Harness();
        var item = h.Item(1);
        var cell = new UiItemSlot();
        var payload = new ItemDragPayload(1, ItemDragSource.Inventory, 0, cell);
        Assert.True(h.Visible);
        Assert.False(h.Button.Enabled);
        Assert.Equal(ItemDragAcceptance.Accept, h.Controller.OnDragOver(h.List, cell, payload));
        h.Controller.HandleDropRelease(h.List, cell, payload);
        Assert.Equal(1, h.Controller.ItemCount);
        Assert.Equal(1, item.TradeState);
        Assert.True(h.Button.Enabled);
        Assert.Equal(ItemDragAcceptance.Reject, h.Controller.OnDragOver(h.List, cell, payload));
        Assert.True(h.Controller.Salvage());
        Assert.False(h.Controller.Salvage());
        var sent = Assert.Single(h.Sends);
        Assert.Equal(Tool, sent.Tool);
        Assert.Equal(new uint[] { 1 }, sent.Items);
        Assert.Same(item, h.Objects.Get(1));
        Assert.Equal(0, item.TradeState);
        Assert.Equal(0, h.Controller.ItemCount);
        Assert.False(h.Button.Enabled);
        Assert.True(h.Visible);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(9, 0)]
    [InlineData(56, 0)]
    [InlineData(65, 0)]
    [InlineData(72, 0)]
    [InlineData(60, 100)]
    public void Invalid_materials_and_full_bags_are_rejected(uint material, int structure)
    {
        using var h = new Harness();
        h.Item(1, material, structure);
        Assert.False(h.Controller.CanAdd(1));
        Assert.False(h.Controller.AddItem(1));
        Assert.Empty(h.Sends);
    }

    [Fact]
    public void Container_drop_filters_contents_and_obeys_material_option()
    {
        using var h = new Harness { Multiple = false };
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = 10, Type = ItemType.Container, ItemsCapacity = 24 });
        h.Objects.MoveItem(10, Player, 0);
        var first = h.Item(1, 60, container: 10);
        var other = h.Item(2, 61, container: 10);
        var full = h.Item(3, 60, 100, 10);
        Assert.True(h.Controller.AddItem(10));
        Assert.Equal(1, h.Controller.ItemCount);
        Assert.Equal(1, first.TradeState);
        Assert.Equal(0, other.TradeState);
        Assert.Equal(0, full.TradeState);
        h.Multiple = true;
        Assert.True(h.Controller.AddItem(2));
        Assert.Equal(2, h.Controller.ItemCount);
    }

    [Fact]
    public void Dragging_out_and_right_click_remove_staging()
    {
        using var h = new Harness();
        var item = h.Item(1);
        h.Controller.AddItem(1);
        var cell = new UiItemSlot();
        h.Controller.OnDragLift(h.List, cell, new(1, ItemDragSource.Inventory, 0, cell));
        Assert.Equal(0, item.TradeState);
        Assert.Equal(0, h.Controller.ItemCount);
        h.Controller.AddItem(1);
        h.List.ExamineItemRequested!(1);
        Assert.Equal(0, item.TradeState);
        Assert.False(h.Button.Enabled);
    }

    [Fact]
    public void Staged_partial_bag_keeps_its_structure_meter()
    {
        using var h = new Harness();
        var item = h.Item(1, structure: 25);
        item.MaxStructure = 100;
        h.Controller.AddItem(1);
        Assert.Equal(0.25f, h.List.GetItem(0)!.StructureFill);
        Assert.True(h.List.GetItem(0)!.ShowTradeOverlay);
    }

    [Fact]
    public void Submit_orders_items_from_last_staged_to_first()
    {
        using var h = new Harness();
        h.Item(1);
        h.Item(2);
        h.Controller.AddItem(1);
        h.Controller.AddItem(2);
        Assert.True(h.Controller.Salvage());
        Assert.Equal(new uint[] { 2, 1 }, Assert.Single(h.Sends).Items);
    }

    [Fact]
    public void Failed_send_preserves_staged_items_for_retry()
    {
        using var h = new Harness { SendSucceeds = false };
        var item = h.Item(1);
        h.Controller.AddItem(1);
        Assert.False(h.Controller.Salvage());
        Assert.Equal(1, h.Controller.ItemCount);
        Assert.Equal(1, item.TradeState);
        Assert.Single(h.Errors);
        h.SendSucceeds = true;
        Assert.True(h.Controller.Salvage());
        Assert.Equal(0, item.TradeState);
    }

    [Fact]
    public void Item_move_removes_staging_and_tool_removal_closes_window()
    {
        using var h = new Harness();
        var item = h.Item(1);
        h.Controller.AddItem(1);
        h.Objects.MoveItem(1, 999, 0);
        Assert.Equal(0, item.TradeState);
        Assert.Equal(0, h.Controller.ItemCount);
        h.Objects.Remove(Tool);
        Assert.False(h.Visible);
        Assert.Equal(0u, h.Controller.ToolId);
    }

    [Fact]
    public void Moving_container_out_of_inventory_clears_its_staged_children()
    {
        using var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = 10, Type = ItemType.Container, ItemsCapacity = 24 });
        h.Objects.MoveItem(10, Player, 0);
        var item = h.Item(1, container: 10);
        h.Controller.AddItem(1);
        h.Objects.MoveItem(10, 999, 0);
        Assert.Equal(0, item.TradeState);
        Assert.Equal(0, h.Controller.ItemCount);
        Assert.False(h.Button.Enabled);
    }

    [Fact]
    public void Hiding_and_reopening_clears_staging()
    {
        using var h = new Harness();
        var item = h.Item(1);
        h.Controller.AddItem(1);
        h.Controller.OnHidden();
        Assert.Equal(0, item.TradeState);
        Assert.Equal(0, h.Controller.ItemCount);
        Assert.False(h.Controller.CanAdd(1));
        h.Controller.Open(Tool);
        Assert.True(h.Controller.CanAdd(1));
        Assert.False(h.Button.Enabled);
    }
}
