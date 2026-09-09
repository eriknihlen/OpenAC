using AcDream.Core.World;

namespace AcDream.Core.Tests.World;

public sealed class LandblockStaticEntityIdAllocatorTests
{
    [Fact]
    public void Base_PreservesFullCoordinatesAndStabNamespace()
    {
        var bases = new HashSet<uint>();
        for (uint y = 0; y <= 255; y++)
        for (uint x = 0; x <= 255; x++)
        {
            uint value = LandblockStaticEntityIdAllocator.Base(x, y);
            Assert.True(LandblockStaticEntityIdAllocator.IsInNamespace(value));
            Assert.True(bases.Add(value), $"duplicate base for ({x},{y})");
        }
    }

    [Fact]
    public void MaximumCounterDoesNotAliasAdjacentLandblock()
    {
        for (uint y = 0; y < 255; y++)
        {
            uint current = LandblockStaticEntityIdAllocator.Base(0, y);
            uint next = LandblockStaticEntityIdAllocator.Base(0, y + 1);
            Assert.True(current + LandblockStaticEntityIdAllocator.MaxCounter < next);
        }
    }

    [Fact]
    public void Allocate_AcceptsLastCounterThenFailsBeforeNamespaceWrap()
    {
        uint counter = LandblockStaticEntityIdAllocator.MaxCounter;

        uint last = LandblockStaticEntityIdAllocator.Allocate(0xA9u, 0xB4u, ref counter);

        Assert.Equal(
            LandblockStaticEntityIdAllocator.Base(0xA9u, 0xB4u)
                + LandblockStaticEntityIdAllocator.MaxCounter,
            last);
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => LandblockStaticEntityIdAllocator.Allocate(0xA9u, 0xB4u, ref counter));
        Assert.Contains("4096-entry", error.Message, StringComparison.Ordinal);
    }
}
