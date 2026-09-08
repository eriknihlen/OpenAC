using System.Collections.Immutable;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;

namespace AcDream.App.Physics;

internal interface ILiveEntityMotionRuntimeBindings
{
    (float Radius, float Height) GetSetupCylinder(uint serverGuid, WorldEntity entity);

    (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)
        GetSetupMoverShape(uint serverGuid, WorldEntity entity);
    bool RouteServerMoveTo(
        MovementManager movement,
        uint cellId,
        WorldSession.EntityMotionUpdate update);
    void StickToObjectFromWire(IPhysicsObjHost? host, uint targetGuid);
    void ClearTargetForHiddenEntity(uint serverGuid);
    IPhysicsObjHost? ResolvePhysicsHost(uint serverGuid);
}

internal sealed class DeferredLiveEntityMotionRuntimeBindings
    : ILiveEntityMotionRuntimeBindings
{
    private ILiveEntityMotionRuntimeBindings? _target;

    public void Bind(ILiveEntityMotionRuntimeBindings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_target is not null)
        {
            throw new InvalidOperationException(
                "Live entity motion runtime bindings are already bound.");
        }
        _target = target;
    }

    public IDisposable BindOwned(ILiveEntityMotionRuntimeBindings target)
    {
        Bind(target);
        return new Binding(this, target);
    }

    private void Unbind(ILiveEntityMotionRuntimeBindings expected)
    {
        if (ReferenceEquals(_target, expected))
            _target = null;
    }

    private ILiveEntityMotionRuntimeBindings Target =>
        _target ?? throw new InvalidOperationException(
            "Live entity motion runtime bindings were used before binding.");

    public (float Radius, float Height) GetSetupCylinder(
        uint serverGuid,
        WorldEntity entity) => Target.GetSetupCylinder(serverGuid, entity);

    public (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)
        GetSetupMoverShape(uint serverGuid, WorldEntity entity) =>
        Target.GetSetupMoverShape(serverGuid, entity);

    public bool RouteServerMoveTo(
        MovementManager movement,
        uint cellId,
        WorldSession.EntityMotionUpdate update) =>
        Target.RouteServerMoveTo(movement, cellId, update);

    public void StickToObjectFromWire(IPhysicsObjHost? host, uint targetGuid) =>
        Target.StickToObjectFromWire(host, targetGuid);

    public void ClearTargetForHiddenEntity(uint serverGuid) =>
        Target.ClearTargetForHiddenEntity(serverGuid);

    public IPhysicsObjHost? ResolvePhysicsHost(uint serverGuid) =>
        Target.ResolvePhysicsHost(serverGuid);

    private sealed class Binding : IDisposable
    {
        private DeferredLiveEntityMotionRuntimeBindings? _owner;
        private readonly ILiveEntityMotionRuntimeBindings _expected;

        public Binding(
            DeferredLiveEntityMotionRuntimeBindings owner,
            ILiveEntityMotionRuntimeBindings expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}
