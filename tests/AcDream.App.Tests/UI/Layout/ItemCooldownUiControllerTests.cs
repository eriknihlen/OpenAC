using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Spells;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ItemCooldownUiControllerTests
{
    [Fact]
    public void Shared_group_updates_existing_and_future_UIItems_from_one_clock()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x5001u,
            CooldownId = 42u,
            CooldownDuration = 30d,
        });

        var spellbook = new Spellbook();
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 0x802Au,
            LayerId: 1u,
            Duration: 30d,
            CasterGuid: 0u,
            Bucket: Spellbook.CooldownBucket,
            StartTime: 100d));

        double now = 112.5d;
        var root = new UiPanel();
        var list = new UiItemList();
        list.Cell.SetItem(0x5001u, 99u);
        root.AddChild(list);
        uint[] sprites = Enumerable.Range(1, 10)
            .Select(index => 0x06000000u + (uint)index)
            .ToArray();

        ItemCooldownUiController controller = ItemCooldownUiController.Bind(
            root,
            spellbook,
            objects,
            () => now,
            new ItemCooldownAssets(sprites));

        Assert.Equal(sprites[5], list.Cell.ActiveCooldownSprite());

        var future = new UiItemSlot();
        future.SetItem(0x5001u, 100u);
        list.AddItem(future);
        Assert.Equal(sprites[5], future.ActiveCooldownSprite());

        now = 129.999d;
        controller.Tick();
        Assert.Equal(sprites[0], list.Cell.ActiveCooldownSprite());

        now = 130d;
        controller.Tick();
        Assert.Equal(0u, list.Cell.ActiveCooldownSprite());
        Assert.Equal(0, spellbook.ActiveCount);
    }

    [Fact]
    public void Hidden_ancestor_removes_list_from_retail_heartbeat_scope()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x5001u,
            CooldownId = 42u,
            CooldownDuration = 30d,
        });
        var spellbook = new Spellbook();
        spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 0x802Au,
            LayerId: 1u,
            Duration: 30d,
            CasterGuid: 0u,
            Bucket: Spellbook.CooldownBucket,
            StartTime: 100d));
        var root = new UiPanel();
        var window = new UiPanel();
        var list = new UiItemList();
        list.Cell.SetItem(0x5001u, 99u);
        root.AddChild(window);
        window.AddChild(list);
        uint[] sprites = Enumerable.Range(1, 10)
            .Select(index => 0x06000000u + (uint)index)
            .ToArray();
        ItemCooldownUiController controller = ItemCooldownUiController.Bind(
            root,
            spellbook,
            objects,
            () => 112.5d,
            new ItemCooldownAssets(sprites));
        Assert.Equal(sprites[5], list.Cell.ActiveCooldownSprite());

        window.Visible = false;
        controller.Tick();
        Assert.Equal(0u, list.Cell.ActiveCooldownSprite());

        window.Visible = true;
        controller.Tick();
        Assert.Equal(sprites[5], list.Cell.ActiveCooldownSprite());
    }

    [Fact]
    public void Empty_cooldown_registry_skips_clock_and_item_walk()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x5001u,
            CooldownId = 42u,
            CooldownDuration = 30d,
        });
        var spellbook = new Spellbook();
        var root = new UiPanel();
        var list = new UiItemList();
        list.Cell.SetItem(0x5001u, 99u);
        root.AddChild(list);
        int clockReads = 0;
        ItemCooldownUiController controller = ItemCooldownUiController.Bind(
            root,
            spellbook,
            objects,
            () =>
            {
                clockReads++;
                return 100d;
            },
            new ItemCooldownAssets(
                Enumerable.Range(1, 10).Select(index => (uint)index).ToArray()));

        controller.Tick();

        Assert.Equal(0, clockReads);
        Assert.Equal(0u, list.Cell.ActiveCooldownSprite());
    }
}
