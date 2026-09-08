using AcDream.Core.World;

namespace AcDream.Core.Tests.World;

public sealed class ProceduralSceneryIdAllocatorTests
{
    [Fact]
    public void Base_AlwaysSetsSceneryBitAndAvoidsStabPrefix()
    {
        for (uint y = 0; y <= 255; y += 51)
        for (uint x = 0; x <= 255; x += 51)
        {
            uint value = ProceduralSceneryIdAllocator.Base(x, y);
            Assert.NotEqual(0u, value & 0x80000000u);
            Assert.NotEqual(0xC0000000u, value & 0xC0000000u);
        }
    }

    [Fact]
    public void MoreThan256SceneryEntriesRemainInOwningLandblockRange()
    {
        uint id = ProceduralSceneryIdAllocator.Base(0x12u, 0x36u) + 300u;

        Assert.True(id < ProceduralSceneryIdAllocator.Base(0x12u, 0x37u));
        Assert.NotEqual(0u, id & 0x80000000u);
    }

    [Fact]
    public void MaximumCounterDoesNotAliasAdjacentLandblock()
    {
        for (uint y = 0; y < 255; y++)
        {
            uint current = ProceduralSceneryIdAllocator.Base(0, y);
            uint next = ProceduralSceneryIdAllocator.Base(0, y + 1);
            Assert.True(current + ProceduralSceneryIdAllocator.MaxCounter < next);
        }
    }

    [Fact]
    public void Allocate_AcceptsLastCounterThenFailsBeforeNamespaceWrap()
    {
        uint counter = ProceduralSceneryIdAllocator.MaxCounter;

        uint last = ProceduralSceneryIdAllocator.Allocate(0x12u, 0x36u, ref counter);

        Assert.Equal(
            ProceduralSceneryIdAllocator.Base(0x12u, 0x36u)
                + ProceduralSceneryIdAllocator.MaxCounter,
            last);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ProceduralSceneryIdAllocator.Allocate(0x12u, 0x36u, ref counter));
        Assert.Contains("4096-entry", error.Message, StringComparison.Ordinal);
    }
}
