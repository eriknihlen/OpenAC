using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class OwnerScopedResourceRegistryTests
{
    [Fact]
    public void RepeatedAcquireBySameOwnerIsIdempotent()
    {
        var registry = new OwnerScopedResourceRegistry<string>();

        Assert.True(registry.Acquire(1, "shared"));
        Assert.False(registry.Acquire(1, "shared"));
        Assert.Equal(1, registry.OwnerCount);
        Assert.Equal(1, registry.ResourceCount);
        Assert.Equal(["shared"], registry.ReleaseOwner(1));
    }

    [Fact]
    public void SharedResourceRetiresOnlyAfterFinalOwner()
    {
        var registry = new OwnerScopedResourceRegistry<string>();
        registry.Acquire(1, "shared");
        registry.Acquire(2, "shared");

        Assert.Empty(registry.ReleaseOwner(1));
        Assert.Equal(1, registry.ResourceCount);
        Assert.Equal(["shared"], registry.ReleaseOwner(2));
        Assert.Equal(0, registry.ResourceCount);
    }

    [Fact]
    public void ReleaseOwnerReturnsAllNewlyUnownedResourcesOnce()
    {
        var registry = new OwnerScopedResourceRegistry<int>();
        registry.Acquire(7, 10);
        registry.Acquire(7, 20);

        Assert.Equal([10, 20], registry.ReleaseOwner(7).Order());
        Assert.Empty(registry.ReleaseOwner(7));
        Assert.Equal(0, registry.OwnerCount);
        Assert.Equal(0, registry.ResourceCount);
    }
}
