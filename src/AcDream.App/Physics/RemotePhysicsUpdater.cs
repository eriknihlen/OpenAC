using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using AcDream.Runtime.Physics;

namespace AcDream.App.Physics;

internal sealed class RemotePhysicsUpdater
{
    private readonly RuntimeRemotePhysicsUpdater _runtime;
    private readonly Func<uint, WorldEntity, (float Radius, float Height)>
        _getSetupCylinder;
    private readonly Func<uint, WorldEntity,
            (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)>
        _getSetupMoverShape;
    private readonly Func<uint, ObjectInfoState> _getMoverPvpState;
    private readonly Action<uint, LiveEntityAnimationState, RemoteMotion, Vector3>
        _applyServerControlledVelocityCycle;
    private readonly List<LiveEntityRecord> _spatialRemoteSnapshot = new();

    internal RemotePhysicsUpdater(
        RuntimePhysicsState physics,
        Func<uint, WorldEntity, (float Radius, float Height)> getSetupCylinder,
        Func<uint, WorldEntity,
                (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)>
            getSetupMoverShape,
        Action<uint, LiveEntityAnimationState, RemoteMotion, Vector3>
            applyServerControlledVelocityCycle,
        Func<uint, ObjectInfoState>? getMoverPvpState = null)
    {
        _runtime = new RuntimeRemotePhysicsUpdater(
            physics ?? throw new ArgumentNullException(nameof(physics)));
        _getSetupCylinder = getSetupCylinder
            ?? throw new ArgumentNullException(nameof(getSetupCylinder));
        _getSetupMoverShape = getSetupMoverShape
            ?? throw new ArgumentNullException(nameof(getSetupMoverShape));
        _getMoverPvpState = getMoverPvpState ?? (static _ => ObjectInfoState.None);
        _applyServerControlledVelocityCycle =
            applyServerControlledVelocityCycle
            ?? throw new ArgumentNullException(
                nameof(applyServerControlledVelocityCycle));
    }

    public void TickHiddenEntities(
        LiveEntityRuntime liveEntities,
        uint localPlayerServerGuid,
        float dt,
        Action<WorldEntity> publishRootPose,
        Action<uint, AnimationSequencer>? processAnimationHooks = null,
        Action<uint>? markPartPoseDirty = null)
    {
        ArgumentNullException.ThrowIfNull(liveEntities);
        ArgumentNullException.ThrowIfNull(publishRootPose);

        Vector3? playerPosition = null;
        if (localPlayerServerGuid != 0
            && liveEntities.TryGetWorldEntity(
                localPlayerServerGuid,
                out WorldEntity playerEntity))
        {
            playerPosition = playerEntity.Position;
        }

        liveEntities.CopySpatialRemoteMotionRecordsTo(
            _spatialRemoteSnapshot);
        foreach (LiveEntityRecord record in _spatialRemoteSnapshot)
        {
            if (record.ServerGuid == localPlayerServerGuid
                || (record.FinalPhysicsState & PhysicsStateFlags.Hidden) == 0
                || record.RemoteMotionRuntime is not RemoteMotion remote
                || !liveEntities.IsCurrentSpatialRemoteMotion(record, remote)
                || record.WorldEntity is not { } entity)
            {
                continue;
            }

            LiveEntityAnimationState? animation =
                record.AnimationRuntime as LiveEntityAnimationState;
            RetailObjectActivityResult activity =
                RetailObjectActivityGate.Evaluate(
                    record.ObjectClock,
                    remote.Body,
                    liveEntities.GetRootObjectClockDisposition(
                        record.ServerGuid)
                        is RetailObjectClockDisposition.Advance,
                    record.HasPartArray,
                    (record.FinalPhysicsState
                        & PhysicsStateFlags.Static) != 0,
                    entity.Position,
                    playerPosition,
                    dt);
            if (activity is not RetailObjectActivityResult.Active)
                continue;

            AnimationSequencer? sequencer = animation?.Sequencer;
            ulong objectClockEpoch = record.ObjectClockEpoch;
            RetailObjectQuantumBatch batch = record.ObjectClock.Advance(dt);
            for (int qi = 0; qi < batch.Count; qi++)
            {
                if (!IsCurrent(
                        liveEntities,
                        record,
                        entity,
                        remote,
                        objectClockEpoch)
                    || !TickHidden(
                        remote,
                        entity,
                        batch.GetQuantum(qi),
                        sequencer?.Manager,
                        processAnimationHooks,
                        sequencer,
                        liveEntities,
                        record,
                        objectClockEpoch))
                {
                    break;
                }
            }

            if (batch.Count > 0
                && IsCurrent(
                    liveEntities,
                    record,
                    entity,
                    remote,
                    objectClockEpoch))
            {
                markPartPoseDirty?.Invoke(record.ServerGuid);
                publishRootPose(entity);
            }
        }
    }

    public bool Tick(
        RemoteMotion remote,
        LiveEntityAnimationState animation,
        float dt,
        MotionDeltaFrame rootMotionLocalFrame,
        int liveCenterX,
        int liveCenterY,
        Action<uint, AnimationSequencer>? processAnimationHooks = null,
        LiveEntityRuntime? ownerRuntime = null,
        LiveEntityRecord? ownerRecord = null,
        ulong ownerClockEpoch = 0)
    {
        ArgumentNullException.ThrowIfNull(animation);
        return Tick(
            remote,
            animation.Entity,
            animation.Scale,
            animation.Sequencer,
            animation,
            dt,
            rootMotionLocalFrame,
            liveCenterX,
            liveCenterY,
            processAnimationHooks,
            ownerRuntime,
            ownerRecord,
            ownerClockEpoch);
    }

    public bool Tick(
        RemoteMotion remote,
        WorldEntity entity,
        float objectScale,
        AnimationSequencer? sequencer,
        LiveEntityAnimationState? animationForVelocityCycle,
        float dt,
        MotionDeltaFrame rootMotionLocalFrame,
        int liveCenterX,
        int liveCenterY,
        Action<uint, AnimationSequencer>? processAnimationHooks = null,
        LiveEntityRuntime? ownerRuntime = null,
        LiveEntityRecord? ownerRecord = null,
        ulong ownerClockEpoch = 0)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(entity);
        if (ownerRuntime is null
            || ownerRecord is null)
        {
            return false;
        }

        bool OwnerValid() => IsCurrent(
            ownerRuntime,
            ownerRecord,
            entity,
            remote,
            ownerClockEpoch);
        var (radius, height) =
            _getSetupCylinder(ownerRecord.ServerGuid, entity);
        var shape = _getSetupMoverShape(ownerRecord.ServerGuid, entity);
        Action<Vector3>? staleCycle =
            animationForVelocityCycle is null
                ? null
                : velocity => _applyServerControlledVelocityCycle(
                    ownerRecord.ServerGuid,
                    animationForVelocityCycle,
                    remote,
                    velocity);

        return _runtime.Tick(
            ownerRecord.Canonical,
            remote,
            objectScale,
            sequencer,
            dt,
            ownerClockEpoch,
            rootMotionLocalFrame,
            radius,
            height,
            liveCenterX,
            liveCenterY,
            processAnimationHooks,
            staleCycle,
            snapshot =>
            {
                if (!OwnerValid())
                    return false;
                entity.SetPosition(snapshot.Position);
                entity.ParentCellId = snapshot.FullCellId;
                entity.Rotation = snapshot.Orientation;
                return OwnerValid();
            },
            OwnerValid,
            sphereList: shape.Spheres,
            sphereScale: shape.Scale,
            stepUpHeight: shape.StepUpHeight,
            stepDownHeight: shape.StepDownHeight,
            moverPvpState: _getMoverPvpState(ownerRecord.ServerGuid));
    }

    public bool TickHidden(
        RemoteMotion remote,
        WorldEntity entity,
        float dt,
        MotionTableManager? partArrayHandleMovement = null,
        Action<uint, AnimationSequencer>? processAnimationHooks = null,
        AnimationSequencer? sequencer = null,
        LiveEntityRuntime? ownerRuntime = null,
        LiveEntityRecord? ownerRecord = null,
        ulong ownerClockEpoch = 0)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(entity);
        if (ownerRuntime is null
            || ownerRecord is null)
        {
            return false;
        }

        bool OwnerValid() => IsCurrent(
            ownerRuntime,
            ownerRecord,
            entity,
            remote,
            ownerClockEpoch);
        var (radius, height) =
            _getSetupCylinder(ownerRecord.ServerGuid, entity);
        var shape = _getSetupMoverShape(ownerRecord.ServerGuid, entity);
        return _runtime.TickHidden(
            ownerRecord.Canonical,
            remote,
            dt,
            ownerClockEpoch,
            radius,
            height,
            partArrayHandleMovement,
            processAnimationHooks,
            sequencer,
            snapshot =>
            {
                if (!OwnerValid())
                    return false;
                entity.SetPosition(snapshot.Position);
                entity.ParentCellId = snapshot.FullCellId;
                entity.Rotation = snapshot.Orientation;
                return OwnerValid();
            },
            OwnerValid,
            sphereList: shape.Spheres,
            sphereScale: shape.Scale,
            stepUpHeight: shape.StepUpHeight,
            stepDownHeight: shape.StepDownHeight,
            moverPvpState: _getMoverPvpState(ownerRecord.ServerGuid));
    }

    public void SyncRemoteShadowToBody(
        uint entityId,
        IRuntimeRemotePlacement remote,
        int liveCenterX,
        int liveCenterY,
        uint? authoritativeCellId = null) =>
        _runtime.SyncRemoteShadowToBody(
            entityId,
            remote,
            liveCenterX,
            liveCenterY,
            authoritativeCellId);

    public void SyncRemoteShadowToBody(
        uint entityId,
        PhysicsBody body,
        int liveCenterX,
        int liveCenterY,
        uint authoritativeCellId) =>
        _runtime.SyncRemoteShadowToBody(
            entityId,
            body,
            liveCenterX,
            liveCenterY,
            authoritativeCellId);

    internal static bool ShouldSynchronizeShadowPose(
        Vector3 currentPosition,
        Quaternion currentOrientation,
        Vector3 lastPosition,
        Quaternion lastOrientation) =>
        RuntimeRemotePhysicsUpdater.ShouldSynchronizeShadowPose(
            currentPosition,
            currentOrientation,
            lastPosition,
            lastOrientation);

    internal static bool ShouldSynchronizeShadow(
        bool cellChanged,
        Vector3 currentPosition,
        Quaternion currentOrientation,
        Vector3 lastPosition,
        Quaternion lastOrientation) =>
        RuntimeRemotePhysicsUpdater.ShouldSynchronizeShadow(
            cellChanged,
            currentPosition,
            currentOrientation,
            lastPosition,
            lastOrientation);

    private static bool IsCurrent(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        RemoteMotion remote,
        ulong objectClockEpoch) =>
        record.ObjectClockEpoch == objectClockEpoch
        && runtime.IsCurrentSpatialRemoteMotion(record, remote)
        && ReferenceEquals(record.WorldEntity, entity);
}
