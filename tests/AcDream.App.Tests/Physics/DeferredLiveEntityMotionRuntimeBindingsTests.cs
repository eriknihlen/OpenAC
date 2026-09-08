using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;

namespace AcDream.App.Tests.Physics;

public sealed class DeferredLiveEntityMotionRuntimeBindingsTests
{
    [Fact]
    public void EveryReverseEdgeFailsBeforeBind()
    {
        var bridge = new DeferredLiveEntityMotionRuntimeBindings();
        var entity = Entity();

        Assert.Throws<InvalidOperationException>(() =>
            bridge.GetSetupCylinder(1, entity));
        Assert.Throws<InvalidOperationException>(() =>
            bridge.RouteServerMoveTo(null!, 0, default));
        Assert.Throws<InvalidOperationException>(() =>
            bridge.StickToObjectFromWire(null, 1));
        Assert.Throws<InvalidOperationException>(() =>
            bridge.ClearTargetForHiddenEntity(1));
        Assert.Throws<InvalidOperationException>(() =>
            bridge.ResolvePhysicsHost(1));
    }

    [Fact]
    public void BindIsSingleAssignmentAndAllCallsReachExactOwner()
    {
        var bridge = new DeferredLiveEntityMotionRuntimeBindings();
        var owner = new RecordingBindings();
        bridge.Bind(owner);

        Assert.Equal((2f, 3f), bridge.GetSetupCylinder(1, Entity()));
        Assert.True(bridge.RouteServerMoveTo(null!, 4, default));
        bridge.StickToObjectFromWire(null, 5);
        bridge.ClearTargetForHiddenEntity(6);
        Assert.Null(bridge.ResolvePhysicsHost(7));

        Assert.Equal(
            ["cylinder:1", "moveto:4", "stick:5", "hidden:6", "host:7"],
            owner.Calls);
        Assert.Throws<InvalidOperationException>(() =>
            bridge.Bind(new RecordingBindings()));
    }

    private static WorldEntity Entity() => new()
    {
        Id = 1,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private sealed class RecordingBindings : ILiveEntityMotionRuntimeBindings
    {
        public List<string> Calls { get; } = [];
        public (float Radius, float Height) GetSetupCylinder(uint guid, WorldEntity entity)
        {
            Calls.Add($"cylinder:{guid}");
            return (2f, 3f);
        }
        public (System.Collections.Immutable.ImmutableArray<AcDream.Core.Physics.FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)
            GetSetupMoverShape(uint guid, WorldEntity entity) =>
            (System.Collections.Immutable.ImmutableArray<AcDream.Core.Physics.FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f);
        public bool RouteServerMoveTo(
            MovementManager movement,
            uint cellId,
            WorldSession.EntityMotionUpdate update)
        {
            Calls.Add($"moveto:{cellId}");
            return true;
        }
        public void StickToObjectFromWire(IPhysicsObjHost? host, uint targetGuid) =>
            Calls.Add($"stick:{targetGuid}");
        public void ClearTargetForHiddenEntity(uint guid) => Calls.Add($"hidden:{guid}");
        public IPhysicsObjHost? ResolvePhysicsHost(uint guid)
        {
            Calls.Add($"host:{guid}");
            return null;
        }
    }
}
