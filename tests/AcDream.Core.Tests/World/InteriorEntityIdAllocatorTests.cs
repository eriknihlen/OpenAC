using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

public class InteriorEntityIdAllocatorTests
{
    [Fact]
    public void Base_AlwaysAtOrAboveStabThreshold()
    {
        for (uint y = 0; y <= 255; y += 51)
            for (uint x = 0; x <= 255; x += 51)
                Assert.True(InteriorEntityIdAllocator.Base(x, y) >= 0x40000000u);
    }

    [Fact]
    public void Base_NeverMatchesLandblockStabPrefix()
    {
        for (uint y = 0; y <= 255; y += 51)
            for (uint x = 0; x <= 255; x += 51)
                Assert.NotEqual(0xC0000000u, InteriorEntityIdAllocator.Base(x, y) & 0xFF000000u);
    }

    [Fact]
    public void Base_NeverSetsSceneryBit()
    {
        for (uint y = 0; y <= 255; y += 51)
            for (uint x = 0; x <= 255; x += 51)
                Assert.Equal(0u, InteriorEntityIdAllocator.Base(x, y) & 0x80000000u);
    }

    [Fact]
    public void MaxCounter_StaysWithinTheSameLandblocksReservedRange()
    {
        for (uint y = 0; y < 255; y++)
        {
            uint thisBase = InteriorEntityIdAllocator.Base(0, y);
            uint nextYBase = InteriorEntityIdAllocator.Base(0, y + 1);
            Assert.True(thisBase + InteriorEntityIdAllocator.MaxCounter < nextYBase,
                $"y={y}: counter budget overflows into landblock y={y + 1}'s id range");
        }
    }

    [Fact]
    public void TownNetworkRepro_277EntitiesStaysInTheSameLandblock()
    {
        uint id = InteriorEntityIdAllocator.Base(0, 7) + 277u;
        uint decodedYByte = (id & 0xFF000000u);   // old scheme's Y read (byte-aligned)
        Assert.NotEqual(0xC0000000u, decodedYByte);   // sanity: still not a landblock-stab
        Assert.True(id < InteriorEntityIdAllocator.Base(0, 8),
            "277 entities must stay inside landblock Y=7's reserved id range");
    }

    [Fact]
    public void MaxCounter_Is4095()
    {
        Assert.Equal(0xFFFu, InteriorEntityIdAllocator.MaxCounter);
    }

    [Fact]
    public void Allocate_AcceptsLastCounterThenFailsBeforeNamespaceWrap()
    {
        uint counter = InteriorEntityIdAllocator.MaxCounter;

        uint last = InteriorEntityIdAllocator.Allocate(0xA9u, 0xB4u, ref counter);

        Assert.Equal(
            InteriorEntityIdAllocator.Base(0xA9u, 0xB4u)
                + InteriorEntityIdAllocator.MaxCounter,
            last);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => InteriorEntityIdAllocator.Allocate(0xA9u, 0xB4u, ref counter));
        Assert.Contains("4096-entry", error.Message, StringComparison.Ordinal);
    }
}
