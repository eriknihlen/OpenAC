using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class DynamicBufferCapacityTests
{
    [Theory]
    [InlineData(0, 1, 4096)]
    [InlineData(4096, 4096, 4096)]
    [InlineData(4096, 4097, 8192)]
    [InlineData(8192, 9000, 16384)]
    [InlineData(0, 8193, 12288)]
    public void Grow_UsesAlignedGeometricCapacity(int current, int required, int expected)
        => Assert.Equal(expected, DynamicBufferCapacity.Grow(current, required));

    [Fact]
    public void Grow_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DynamicBufferCapacity.Grow(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DynamicBufferCapacity.Grow(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DynamicBufferCapacity.Grow(0, 1, 0));
    }
}
