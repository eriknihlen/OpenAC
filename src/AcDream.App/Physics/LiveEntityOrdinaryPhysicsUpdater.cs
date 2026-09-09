using System.Collections.Immutable;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.Types;

namespace AcDream.App.Physics;

internal sealed class LiveEntityOrdinaryPhysicsUpdater
{
    private readonly RuntimeOrdinaryPhysicsUpdater _runtime;
    private readonly Func<uint, WorldEntity, (float Radius, float Height)>
        _getSetupCylinder;
    private readonly Func<uint, WorldEntity,
            (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)>
        _getSetupMoverShape;
    private readonly Func<uint, ObjectInfoState> _getMoverPvpState;

    public LiveEntityOrdinaryPhysicsUpdater(
        RuntimePhysicsState physics,
        Func<uint, WorldEntity, (float Radius, float Height)> getSetupCylinder,
        Func<uint, WorldEntity,
                (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)>
            getSetupMoverShape,
        Func<uint, ObjectInfoState>? getMoverPvpState = null)
    {
        _runtime = new RuntimeOrdinaryPhysicsUpdater(
            physics ?? throw new ArgumentNullException(nameof(physics)));
        _getSetupCylinder = getSetupCylinder
            ?? throw new ArgumentNullException(nameof(getSetupCylinder));
        _getSetupMoverShape = getSetupMoverShape
            ?? throw new ArgumentNullException(nameof(getSetupMoverShape));
        _getMoverPvpState = getMoverPvpState ?? (static _ => ObjectInfoState.None);
    }

    public bool Tick(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        Frame rootFrame,
        float objectScale,
        float quantum,
        int liveCenterX,
        int liveCenterY,
        ulong objectClockEpoch,
        AnimationSequencer? sequencer,
        Action<uint, AnimationSequencer> captureAnimationHooks)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(rootFrame);
        ArgumentNullException.ThrowIfNull(captureAnimationHooks);
        if (record.PhysicsBody is not { } body)
            return false;
        var (radius, height) = _getSetupCylinder(record.ServerGuid, entity);
        var shape = _getSetupMoverShape(record.ServerGuid, entity);
        bool ExternalOwnerValid() =>
            IsCurrent(
                runtime,
                record,
                entity,
                body,
                objectClockEpoch);

        if (!_runtime.TryBegin(
                record.Canonical,
                rootFrame,
                objectScale,
                quantum,
                radius,
                height,
                objectClockEpoch,
                sequencer,
                captureAnimationHooks,
                ExternalOwnerValid,
                out RuntimeOrdinaryPhysicsCommit commit,
                sphereList: shape.Spheres,
                sphereScale: shape.Scale,
                stepUpHeight: shape.StepUpHeight,
                stepDownHeight: shape.StepDownHeight,
                moverPvpState: _getMoverPvpState(record.ServerGuid)))
        {
            return false;
        }

        return _runtime.Complete(
            commit,
            liveCenterX,
            liveCenterY,
            snapshot =>
            {
                if (!ExternalOwnerValid())
                    return false;

                entity.SetPosition(snapshot.Position);
                entity.Rotation = snapshot.Orientation;
                entity.ParentCellId = snapshot.FullCellId;
                return ExternalOwnerValid();
            });
    }

    private static bool IsCurrent(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        PhysicsBody body,
        ulong objectClockEpoch) =>
        runtime.IsCurrentSpatialRootObject(record)
        && record.ObjectClockEpoch == objectClockEpoch
        && ReferenceEquals(record.WorldEntity, entity)
        && ReferenceEquals(record.PhysicsBody, body)
        && record.RemoteMotionRuntime is null
        && record.ProjectileRuntime is null;
}
