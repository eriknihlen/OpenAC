using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class ItemManaStateTests
{
    [Fact]
    public void ValidResponseCachesAndRaisesEvent()
    {
        var state = new ItemManaState();
        (uint Guid, float Percent, bool Valid)? observed = null;
        state.ItemManaChanged += (guid, percent, valid) => observed = (guid, percent, valid);

        state.OnQueryItemManaResponse(0x50000A01u, 0.75f, valid: true);

        Assert.Equal((0x50000A01u, 0.75f, true), observed);
        Assert.True(state.HasMana(0x50000A01u));
        Assert.Equal(0.75f, state.GetManaPercent(0x50000A01u));
    }

    [Fact]
    public void InvalidResponseClearsCachedValueAndRaisesEvent()
    {
        var state = new ItemManaState();
        state.OnQueryItemManaResponse(0x50000A01u, 0.75f, valid: true);

        state.OnQueryItemManaResponse(0x50000A01u, 0f, valid: false);

        Assert.False(state.HasMana(0x50000A01u));
        Assert.Equal(0f, state.GetManaPercent(0x50000A01u));
    }
}
