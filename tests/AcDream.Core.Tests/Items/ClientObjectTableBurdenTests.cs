using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public class ClientObjectTableBurdenTests
{
    private const uint Player = 0x50000001u;

    [Fact]
    public void SumCarriedBurden_sums_pack_sidebag_and_wielded_but_not_unrelated()
    {
        var t = new ClientObjectTable();
        // loose pack item
        t.AddOrUpdate(new ClientObject { ObjectId = 0xA, ContainerId = Player, Burden = 100 });
        // side bag in pack
        t.AddOrUpdate(new ClientObject { ObjectId = 0xB, ContainerId = Player, Burden = 50,
            Type = ItemType.Container });
        // item inside the side bag (2-deep)
        t.AddOrUpdate(new ClientObject { ObjectId = 0xC, ContainerId = 0xB, Burden = 30 });
        t.AddOrUpdate(new ClientObject { ObjectId = 0xD, WielderId = Player, Burden = 200 });
        // unrelated object in the world
        t.AddOrUpdate(new ClientObject { ObjectId = 0xE, ContainerId = 0, Burden = 999 });

        Assert.Equal(380, t.SumCarriedBurden(Player));   // 100+50+30+200
    }

    [Fact]
    public void SumCarriedBurden_unknown_owner_is_zero()
        => Assert.Equal(0, new ClientObjectTable().SumCarriedBurden(Player));
}
