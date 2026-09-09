using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public class ShortcutStoreTests
{
    [Fact]
    public void Load_preservesAllRawFieldsAndRejectsOutOfRangeIndex()
    {
        var store = new ShortcutStore();
        store.Load(new[]
        {
            new ShortcutEntry(0,  0x5001u, 0x11223344u),
            new ShortcutEntry(3,  0x5002u, 0u),
            new ShortcutEntry(5,  0u,      0xA5C31234u),
            new ShortcutEntry(-1, 0x5003u, 1u),
            new ShortcutEntry(18, 0x5004u, 2u),
        });
        Assert.Equal(0x5001u, store.Get(0));
        Assert.Equal(0x5002u, store.Get(3));
        Assert.Equal(0u, store.Get(5));
        Assert.Equal(new ShortcutEntry(5, 0u, 0xA5C31234u), store.GetEntry(5));
        Assert.Equal(0x11223344u, store.GetEntry(0)!.Value.SpellId);
        Assert.True(store.IsEmpty(1));
        Assert.Null(store.GetEntry(-1));
        Assert.Null(store.GetEntry(18));
    }

    [Fact]
    public void SetRemoveGet_roundtrip_andBoundsSafe()
    {
        var store = new ShortcutStore();
        store.SetItem(4, 0xABCDu);
        Assert.Equal(0xABCDu, store.Get(4));
        Assert.False(store.IsEmpty(4));
        store.Remove(4);
        Assert.True(store.IsEmpty(4));
        Assert.Equal(0u, store.Get(-1));
        Assert.Equal(0u, store.Get(18));
        store.SetItem(18, 1u); // no-op, no throw
        store.Remove(-1);      // no-op, no throw
    }

    [Fact]
    public void RevisionObserversAndDisposalTrackTheOneCanonicalSlotMap()
    {
        var store = new ShortcutStore();
        int observed = 0;
        store.Changed += () => throw new InvalidOperationException("observer");
        store.Changed += () => observed++;

        store.Load([new ShortcutEntry(2, 0x5001u, 0u)]);

        Assert.Equal(1, observed);
        Assert.Equal(1, store.Revision);
        Assert.Equal(1, store.DispatchFailureCount);
        Assert.Equal(2, store.SubscriberCount);
        Assert.Equal(1, store.Count);

        store.Set(new ShortcutEntry(2, 0x5001u, 0u));
        Assert.Equal(1, store.Revision);
        Assert.Equal(1, observed);

        store.Set(new ShortcutEntry(2, 0x5002u, 0u));
        Assert.Equal(2, store.Revision);
        Assert.Equal(2, observed);

        store.Dispose();
        store.Dispose();

        Assert.True(store.IsDisposed);
        Assert.Empty(store.Items);
        Assert.Equal(0, store.SubscriberCount);
        Assert.Equal(3, observed);
        Assert.Equal(3, store.Revision);
        Assert.Equal(3, store.DispatchFailureCount);
        Assert.Throws<ObjectDisposedException>(
            () => store.SetItem(1, 1u));
    }
}
