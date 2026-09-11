using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class ClientObjectTableUpdateTests
{
    [Fact]
    public void UpsertProperties_unknownObject_createsThenMerges()
    {
        var t = new ClientObjectTable();
        bool added = false;
        t.ObjectAdded += _ => added = true;

        var bundle = new PropertyBundle();
        bundle.Ints[5] = 1234;     // EncumbranceVal

        t.UpsertProperties(0x50000001u, bundle);

        var o = t.Get(0x50000001u);
        Assert.NotNull(o);
        Assert.Equal(1234, o!.Properties.Ints[5]);
        Assert.True(added);
    }

    [Fact]
    public void UpsertProperties_existingObject_mergesAndFiresUpdated()
    {
        var t = new ClientObjectTable();
        t.AddOrUpdate(new ClientObject { ObjectId = 0x50000001u });
        bool updated = false;
        t.ObjectUpdated += _ => updated = true;

        var bundle = new PropertyBundle();
        bundle.Ints[5] = 99;
        t.UpsertProperties(0x50000001u, bundle);

        Assert.Equal(99, t.Get(0x50000001u)!.Properties.Ints[5]);
        Assert.True(updated);
    }

    [Fact]
    public void UpdateStackSize_knownObject_setsFieldsAndFiresUpdated()
    {
        var t = new ClientObjectTable();
        t.AddOrUpdate(new ClientObject { ObjectId = 0x600u, StackSize = 1, Value = 5 });
        bool updated = false;
        t.ObjectUpdated += _ => updated = true;

        bool ok = t.UpdateStackSize(0x600u, stackSize: 25, value: 125);

        Assert.True(ok);
        Assert.Equal(25, t.Get(0x600u)!.StackSize);
        Assert.Equal(125, t.Get(0x600u)!.Value);
        Assert.True(updated);
    }

    [Fact]
    public void UpdateStackSize_unknownObject_returnsFalse()
        => Assert.False(new ClientObjectTable().UpdateStackSize(0xDEADu, 1, 1));

    [Fact]
    public void UpdateAppraisal_retainsPropertiesAndDefensiveSpellSnapshot()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject { ObjectId = 0x700u });
        int updates = 0;
        table.ObjectUpdated += _ => updates++;
        var bundle = new PropertyBundle();
        bundle.Ints[106] = 420;
        uint[] spells = [1327u, 1132u];

        Assert.True(table.UpdateAppraisal(0x700u, bundle, spells, 12.345d));
        spells[0] = 0u;

        ClientObject item = table.Get(0x700u)!;
        Assert.Equal(420, item.Properties.Ints[106]);
        Assert.Equal([1327u, 1132u], item.AppraisedSpellIds);
        Assert.Equal(12345, item.LastAppraisalTimeMs);
        Assert.Equal(1, updates);
    }

    [Fact]
    public void NotifyObjectUpdated_publishesAClientSideChangeToObservers()
    {
        var t = new ClientObjectTable();
        t.AddOrUpdate(new ClientObject { ObjectId = 0x50000001u });
        ClientObject? seen = null;
        t.ObjectUpdated += o => seen = o;

        t.Get(0x50000001u)!.SellState = 1;
        Assert.True(t.NotifyObjectUpdated(0x50000001u));

        Assert.Same(t.Get(0x50000001u), seen);
        Assert.Equal(1, seen!.SellState);
        Assert.False(t.NotifyObjectUpdated(0x50000002u));
    }
}
