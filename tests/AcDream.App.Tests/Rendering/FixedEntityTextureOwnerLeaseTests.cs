using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering;

public sealed class FixedEntityTextureOwnerLeaseTests
{
    [Fact]
    public void RepeatedReplacementReleasesHistoricalKeysForTheFixedOwner()
    {
        const uint owner = 42;
        var lifetime = new RegistryLifetime();
        using var lease = new FixedEntityTextureOwnerLease(lifetime, owner);

        lease.Replace(hasReplacement: true);
        lifetime.Acquire(owner, "first");
        Assert.Equal(1, lifetime.ResourceCount);

        lease.Replace(hasReplacement: true);
        lifetime.Acquire(owner, "second");
        Assert.Equal(1, lifetime.ResourceCount);

        lease.Replace(hasReplacement: true);
        lifetime.Acquire(owner, "third");
        Assert.Equal(1, lifetime.ResourceCount);

        lease.Replace(hasReplacement: false);
        Assert.Equal(0, lifetime.ResourceCount);
        Assert.Equal(3, lifetime.ReleaseCount);
    }

    private sealed class RegistryLifetime : IEntityTextureLifetime
    {
        private readonly OwnerScopedResourceRegistry<string> _registry = new();
        public int ResourceCount => _registry.ResourceCount;
        public int ReleaseCount { get; private set; }

        public void Acquire(uint owner, string key) => _registry.Acquire(owner, key);

        public void ReleaseOwner(uint localEntityId)
        {
            ReleaseCount++;
            _registry.ReleaseOwner(localEntityId);
        }
    }
}
