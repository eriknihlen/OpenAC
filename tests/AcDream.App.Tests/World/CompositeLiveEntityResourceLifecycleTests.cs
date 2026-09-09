using AcDream.App.World;
using AcDream.Core.World;

namespace AcDream.App.Tests.World;

public sealed class CompositeLiveEntityResourceLifecycleTests
{
    [Fact]
    public void LateReleaseFailureDoesNotReplayEarlierCommittedOwner()
    {
        var entity = Entity();
        int firstReleases = 0;
        int secondReleases = 0;
        var lifecycle = new CompositeLiveEntityResourceLifecycle(
            new(_ => { }, _ =>
            {
                firstReleases++;
                if (firstReleases == 1)
                    throw new InvalidOperationException("first failed");
            }),
            new(_ => { }, _ => secondReleases++));
        lifecycle.Register(entity);

        Assert.Throws<AggregateException>(() => lifecycle.Unregister(entity));
        Assert.Equal(1, secondReleases);

        lifecycle.Unregister(entity);

        Assert.Equal(2, firstReleases);
        Assert.Equal(1, secondReleases);
    }

    [Fact]
    public void RegistrationFailureAttemptsEveryEnteredOwnerRollback()
    {
        var entity = Entity();
        var releases = new List<int>();
        var lifecycle = new CompositeLiveEntityResourceLifecycle(
            new(_ => { }, _ => releases.Add(1)),
            new(
                _ => throw new InvalidOperationException("second registration failed"),
                _ => releases.Add(2)),
            new(_ => throw new InvalidOperationException("must not enter"), _ => releases.Add(3)));

        Assert.Throws<InvalidOperationException>(() => lifecycle.Register(entity));

        Assert.Equal([2, 1], releases);
        lifecycle.Unregister(entity);
        Assert.Equal([2, 1], releases);
    }

    [Fact]
    public void RegisterCallbackUnregistersSameEntityWithoutOrphaningLaterOwners()
    {
        var entity = Entity();
        CompositeLiveEntityResourceLifecycle? lifecycle = null;
        int firstRegisters = 0;
        int firstReleases = 0;
        int laterRegisters = 0;
        lifecycle = new CompositeLiveEntityResourceLifecycle(
            new(
                current =>
                {
                    firstRegisters++;
                    lifecycle!.Unregister(current);
                },
                _ => firstReleases++),
            new(_ => laterRegisters++, _ => { }));

        lifecycle.Register(entity);
        lifecycle.Unregister(entity);

        Assert.Equal(1, firstRegisters);
        Assert.Equal(1, firstReleases);
        Assert.Equal(0, laterRegisters);
    }

    [Fact]
    public void UnregisterCallbackReentryDoesNotRepeatActiveOwnerRelease()
    {
        var entity = Entity();
        CompositeLiveEntityResourceLifecycle? lifecycle = null;
        int releases = 0;
        lifecycle = new CompositeLiveEntityResourceLifecycle(
            new CompositeLiveEntityResourceLifecycle.Owner(
                _ => { },
                current =>
                {
                    releases++;
                    lifecycle!.Unregister(current);
                }));
        lifecycle.Register(entity);

        lifecycle.Unregister(entity);
        lifecycle.Unregister(entity);

        Assert.Equal(1, releases);
    }

    private static WorldEntity Entity() => new()
    {
        Id = 1,
        ServerGuid = 0x70000001u,
        SourceGfxObjOrSetupId = 0x01000001u,
        Position = System.Numerics.Vector3.Zero,
        Rotation = System.Numerics.Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };
}
