using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;

namespace AcDream.App.Tests.Composition;

public sealed class LivePresentationCompositionTests
{
    [Fact]
    public void RuntimeBindingsReleaseInReverseAndRetryOnlyFailedEdges()
    {
        var calls = new List<string>();
        var bindings = new LivePresentationRuntimeBindings();
        var first = new RetryBinding("first", calls, failures: 0);
        var second = new RetryBinding("second", calls, failures: 1);
        bindings.Adopt("first", first);
        bindings.Adopt("second", second);

        Assert.Throws<AggregateException>(bindings.Dispose);
        Assert.Equal(["second", "first"], calls);

        bindings.Dispose();
        bindings.Dispose();

        Assert.Equal(["second", "first", "second"], calls);
        Assert.Throws<ObjectDisposedException>(() =>
            bindings.Adopt("late", new RetryBinding("late", calls, 0)));
    }

    [Fact]
    public void TransferableAdoptionRollsBackExactlyAndRetriesFailure()
    {
        var calls = new List<string>();
        var bindings = new LivePresentationRuntimeBindings();
        bindings.Adopt("stable", new RetryBinding("stable", calls, 0));
        IDisposable adoption = bindings.AdoptOwned(
            "candidate",
            new RetryBinding("candidate", calls, 1));

        Assert.Throws<InvalidOperationException>(adoption.Dispose);
        adoption.Dispose();
        adoption.Dispose();
        bindings.Dispose();

        Assert.Equal(["candidate", "candidate", "stable"], calls);
    }

    [Fact]
    public void CanonicalRuntimeSlotUsesExactOwnerBinding()
    {
        var slot = new LiveEntityRuntimeSlot();
        LiveEntityRuntime first = Runtime();
        LiveEntityRuntime second = Runtime();

        IDisposable stale = slot.BindOwned(first);
        Assert.Same(first, slot.Current);
        Assert.Throws<InvalidOperationException>(() => slot.BindOwned(second));

        stale.Dispose();
        IDisposable current = slot.BindOwned(second);
        stale.Dispose();
        Assert.Same(second, slot.Current);
        current.Dispose();
        Assert.Null(slot.Current);
    }

    [Fact]
    public void MotionBindingCannotBeClearedByAStaleOwner()
    {
        var source = new DeferredLiveEntityMotionRuntimeBindings();
        var first = new MotionRuntime(1f);
        var second = new MotionRuntime(2f);
        var entity = new WorldEntity
        {
            Id = 1u,
            SourceGfxObjOrSetupId = 1u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };

        IDisposable stale = source.BindOwned(first);
        Assert.Equal(1f, source.GetSetupCylinder(1u, entity).Radius);
        Assert.Throws<InvalidOperationException>(() => source.BindOwned(second));

        stale.Dispose();
        IDisposable current = source.BindOwned(second);
        stale.Dispose();
        Assert.Equal(2f, source.GetSetupCylinder(1u, entity).Radius);
        current.Dispose();
        Assert.Throws<InvalidOperationException>(() =>
            source.GetSetupCylinder(1u, entity));
    }

    [Fact]
    public void LandblockLoadedBridgeIsInertUntilBoundAndDeactivationIsTerminal()
    {
        var source = new DeferredLiveEntityLandblockLoadedSink();
        var first = new LandblockSink();
        var second = new LandblockSink();

        source.OnLandblockLoaded(1u);
        IDisposable stale = source.Bind(first);
        source.OnLandblockLoaded(2u);
        Assert.Equal([2u], first.Landblocks);
        Assert.Throws<InvalidOperationException>(() => source.Bind(second));

        stale.Dispose();
        IDisposable current = source.Bind(second);
        stale.Dispose();
        source.OnLandblockLoaded(3u);
        Assert.Equal([3u], second.Landblocks);

        source.Deactivate();
        current.Dispose();
        source.OnLandblockLoaded(4u);
        Assert.Equal([3u], second.Landblocks);
        Assert.Throws<ObjectDisposedException>(() => source.Bind(first));
    }

    [Fact]
    public void GameWindowUsesLivePhaseAndContainsNoPhaseSixConstructionBody()
    {
        IReadOnlyList<CompiledCall> windowCalls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));

        Assert.Single(
            windowCalls,
            call => call.Target.DeclaringType
                    == typeof(LivePresentationCompositionPhase)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            windowCalls,
            call => call.Target.IsConstructor
                && call.Target.DeclaringType is { } type
                && (type == typeof(LiveEntityRuntime)
                    || type == typeof(WbDrawDispatcher)
                    || type == typeof(LandblockRenderPublisher)));
        Assert.DoesNotContain(
            windowCalls,
            call => call.Target.Name == "AcquirePrepared");

        Assert.DoesNotContain(
            typeof(LivePresentationCompositionPhase).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(GameWindow));
    }

    [Fact]
    public void EquippedChildConstructionBindsTheCompositionCollisionPublisher()
    {
        IReadOnlyList<CompiledCall> calls =
            CompiledCallGraph.ReadOwned(typeof(LivePresentationCompositionPhase));

        Assert.Contains(calls, call =>
            call.Target.DeclaringType == typeof(EquippedChildRenderController)
            && call.Target.IsConstructor);
        Assert.Contains(calls, call =>
            call.Target.DeclaringType == typeof(LiveCollisionAssetPublisher)
            && call.Target.Name == nameof(LiveCollisionAssetPublisher.CacheGfxObj));
    }

    private static LiveEntityRuntime Runtime() => LiveEntityRuntimeFixture.Create(
        new GpuWorldState(),
        new DelegateLiveEntityResourceLifecycle(static _ => { }, static _ => { }));

    private sealed class MotionRuntime(float radius)
        : ILiveEntityMotionRuntimeBindings
    {
        public (float Radius, float Height) GetSetupCylinder(
            uint serverGuid,
            WorldEntity entity) => (radius, 1f);

        public (System.Collections.Immutable.ImmutableArray<AcDream.Core.Physics.FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)
            GetSetupMoverShape(uint serverGuid, WorldEntity entity) =>
            (System.Collections.Immutable.ImmutableArray<AcDream.Core.Physics.FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f);

        public bool RouteServerMoveTo(
            MovementManager movement,
            uint cellId,
            WorldSession.EntityMotionUpdate update) => false;

        public void StickToObjectFromWire(
            IPhysicsObjHost? host,
            uint targetGuid)
        {
        }

        public void ClearTargetForHiddenEntity(uint serverGuid)
        {
        }

        public IPhysicsObjHost? ResolvePhysicsHost(
            uint serverGuid) => null;
    }

    private sealed class LandblockSink : ILiveEntityLandblockLoadedSink
    {
        public List<uint> Landblocks { get; } = [];
        public void OnLandblockLoaded(uint landblockId) => Landblocks.Add(landblockId);
    }

    private sealed class RetryBinding(
        string name,
        List<string> calls,
        int failures) : IDisposable
    {
        private int _failures = failures;

        public void Dispose()
        {
            calls.Add(name);
            if (_failures > 0)
            {
                _failures--;
                throw new InvalidOperationException("retry");
            }
        }
    }

}
