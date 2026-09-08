using AcDream.App.Rendering.Vfx;

namespace AcDream.App.Tests.Rendering.Vfx;

public sealed class EntityEffectAdvanceSourceTests
{
    [Fact]
    public void DefaultsOpenThenForwardsOneExactOwnerAndUnbindsOnlyThatOwner()
    {
        var source = new DeferredEntityEffectAdvanceSource();
        var first = new Target(false);
        var other = new Target(true);

        Assert.True(source.CanAdvanceOwner(42));
        source.Bind(first);
        Assert.False(source.CanAdvanceOwner(42));
        source.Unbind(other);
        Assert.False(source.CanAdvanceOwner(42));
        source.Unbind(first);
        Assert.True(source.CanAdvanceOwner(42));
        source.Bind(other);
        Assert.True(source.CanAdvanceOwner(42));
    }

    [Fact]
    public void RejectsASecondOwnerAndDeactivationIsTerminalAndOpenForShutdown()
    {
        var source = new DeferredEntityEffectAdvanceSource();
        var first = new Target(false);
        source.Bind(first);

        Assert.Throws<InvalidOperationException>(() => source.Bind(new Target(true)));
        source.Deactivate();

        Assert.True(source.CanAdvanceOwner(1));
        Assert.Throws<ObjectDisposedException>(() => source.Bind(first));
    }

    private sealed class Target(bool result) : IEntityEffectAdvanceSource
    {
        public bool CanAdvanceOwner(uint ownerLocalId) => result;
    }
}
