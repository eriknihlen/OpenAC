using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.App.Tests.World;

public sealed class DeferredLiveEntityRuntimeComponentLifecycleTests
{
    [Fact]
    public void TearDownBeforeBind_FailsFast()
    {
        var bridge = new DeferredLiveEntityRuntimeComponentLifecycle();

        Assert.False(bridge.IsBound);
        Assert.Throws<InvalidOperationException>(() => bridge.TearDown(null!));
    }

    [Fact]
    public void Bind_IsSingleAssignmentAndForwardsExactRecord()
    {
        var bridge = new DeferredLiveEntityRuntimeComponentLifecycle();
        LiveEntityRecord? observed = null;
        var owner = new DelegateLiveEntityRuntimeComponentLifecycle(record => observed = record);
        var replacement = new DelegateLiveEntityRuntimeComponentLifecycle(_ => { });
        LiveEntityRecord record = CreateRecord();

        bridge.Bind(owner);
        bridge.TearDown(record);

        Assert.True(bridge.IsBound);
        Assert.Same(record, observed);
        Assert.Throws<InvalidOperationException>(() => bridge.Bind(replacement));
    }

    [Fact]
    public void BindRejectsNullAndSelf()
    {
        var bridge = new DeferredLiveEntityRuntimeComponentLifecycle();

        Assert.Throws<ArgumentNullException>(() => bridge.Bind(null!));
        Assert.Throws<ArgumentException>(() => bridge.Bind(bridge));
    }

    [Fact]
    public void OwnedBindingReleasesExactlyAndAllowsRebind()
    {
        var bridge = new DeferredLiveEntityRuntimeComponentLifecycle();
        int firstCalls = 0;
        int secondCalls = 0;
        var first = new DelegateLiveEntityRuntimeComponentLifecycle(
            _ => firstCalls++);
        var second = new DelegateLiveEntityRuntimeComponentLifecycle(
            _ => secondCalls++);

        IDisposable firstBinding = bridge.BindOwned(first);
        Assert.Throws<InvalidOperationException>(() => bridge.BindOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = bridge.BindOwned(second);
        firstBinding.Dispose();
        bridge.TearDown(CreateRecord());

        Assert.Equal(0, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public async Task ConcurrentBind_HasOneWinnerAndPublishesThatOwner()
    {
        var bridge = new DeferredLiveEntityRuntimeComponentLifecycle();
        int firstCalls = 0;
        int secondCalls = 0;
        var owners = new ILiveEntityRuntimeComponentLifecycle[]
        {
            new DelegateLiveEntityRuntimeComponentLifecycle(_ => Interlocked.Increment(ref firstCalls)),
            new DelegateLiveEntityRuntimeComponentLifecycle(_ => Interlocked.Increment(ref secondCalls)),
        };
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();

        Task<bool>[] attempts = owners.Select(owner => Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            try
            {
                bridge.Bind(owner);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })).ToArray();

        ready.Wait();
        start.Set();
        bool[] results = await Task.WhenAll(attempts);
        LiveEntityRecord record = CreateRecord();
        bridge.TearDown(record);

        Assert.Single(results, value => value);
        Assert.True(bridge.IsBound);
        Assert.Equal(1, firstCalls + secondCalls);
    }

    [Fact]
    public void RuntimeTeardownBeforeBind_RetainsTombstoneAndRetryConverges()
    {
        const uint guid = 0x70000002u;
        var bridge = new DeferredLiveEntityRuntimeComponentLifecycle();
        var runtime = LiveEntityRuntimeFixture.Create(
            new GpuWorldState(),
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }),
            bridge);
        WorldSession.EntitySpawn spawn = CreateSpawn(guid);
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(spawn);
        var delete = new DeleteObject.Parsed(guid, InstanceSequence: 1);

        Assert.Throws<AggregateException>(() =>
            runtime.UnregisterLiveEntity(delete, isLocalPlayer: false));
        Assert.Equal(1, runtime.PendingTeardownCount);

        int calls = 0;
        bridge.Bind(new DelegateLiveEntityRuntimeComponentLifecycle(exact =>
        {
            Assert.Same(record, exact);
            calls++;
        }));

        Assert.True(runtime.UnregisterLiveEntity(delete, isLocalPlayer: false));
        Assert.Equal(1, calls);
        Assert.Equal(0, runtime.PendingTeardownCount);
    }

    private static LiveEntityRecord CreateRecord()
    {
        var runtime = LiveEntityRuntimeFixture.Create(
            new GpuWorldState(),
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        return runtime.RegisterAndMaterializeProjection(
            CreateSpawn(0x70000001u));
    }

    private static WorldSession.EntitySpawn CreateSpawn(uint guid) =>
        new WorldSession.EntitySpawn(
            Guid: guid,
            Position: null,
            SetupTableId: null,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "test",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            InstanceSequence: 1).WithConsistentPhysics();
}
