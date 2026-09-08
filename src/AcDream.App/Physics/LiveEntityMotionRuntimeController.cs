using System.Collections.Immutable;
using AcDream.App.Interaction;
using AcDream.App.Rendering;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.World;

namespace AcDream.App.Physics;

internal sealed class LiveEntityMotionRuntimeController
    : ILiveEntityMotionRuntimeBindings
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly PhysicsDataCache _physicsDataCache;
    private readonly Func<SelectionInteractionController?> _selectionInteractions;
    private readonly SelectionState _selection;
    private readonly LiveWorldOriginState _origin;

    public LiveEntityMotionRuntimeController(
        LiveEntityRuntime liveEntities,
        PhysicsDataCache physicsDataCache,
        Func<SelectionInteractionController?> selectionInteractions,
        SelectionState selection,
        LiveWorldOriginState origin)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _physicsDataCache = physicsDataCache ?? throw new ArgumentNullException(nameof(physicsDataCache));
        _selectionInteractions = selectionInteractions ?? throw new ArgumentNullException(nameof(selectionInteractions));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
    }
    internal AcDream.Core.Physics.Motion.MotionTableDispatchSink? EnsureRemoteMotionBindings(
        RemoteMotion rm, LiveEntityAnimationState? ae, uint serverGuid)
    {
        AcDream.Core.Physics.AnimationSequencer? sequencer = ae?.Sequencer;
        if (sequencer is not null && rm.Sink is null)
        {
            rm.Sink = new AcDream.Core.Physics.Motion.MotionTableDispatchSink(sequencer);
            rm.Motion.DefaultSink = rm.Sink;
        }
        if (sequencer is not null)
        {
            rm.Motion.RemoveLinkAnimations = () => sequencer.Manager.HandleEnterWorld();
            rm.Motion.InitializeMotionTables = () => sequencer.Manager.InitializeState();
            rm.Motion.CheckForCompletedMotions = sequencer.Manager.CheckForCompletedMotions;
        }

        if (rm.Host is not null)
            return rm.Sink;
        if (_liveEntities is not { } liveEntities
            || !liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord hostRecord)
            || !ReferenceEquals(hostRecord.RemoteMotionRuntime, rm))
        {
            return rm.Sink;
        }
        var rmT = rm;
        var mtBody = rm.Body;
        if (!_liveEntities.TryGetWorldEntity(serverGuid, out var selfEntity))
            return rm.Sink;
        EntityPhysicsHost host = null!;
        double NowSeconds() => (System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalSeconds;
        rm.Movement.MoveToFactory = () =>
        {
            var mtm = new AcDream.Core.Physics.Motion.MoveToManager(
                rm.Motion,
                stopCompletely: () => rmT.Movement.PerformMovement(
                    new AcDream.Core.Physics.MovementStruct
                    {
                        Type = AcDream.Core.Physics.MovementType.StopCompletely,
                    }),
                getPosition: () => new AcDream.Core.Physics.Position(
                    rmT.CellId, mtBody.Position, mtBody.Orientation),
                getHeading: () => AcDream.Core.Physics.Motion.MoveToMath.GetHeading(
                    mtBody.Orientation),
                setHeading: (h, _) => mtBody.Orientation =
                    AcDream.Core.Physics.Motion.MoveToMath.SetHeading(mtBody.Orientation, h),
                getOwnRadius: () => GetSetupCylinder(serverGuid, selfEntity).Radius,
                getOwnHeight: () => GetSetupCylinder(serverGuid, selfEntity).Height,
                contact: () => mtBody.OnWalkable,
                isInterpolating: () => rmT.Interp.IsActive,
                getVelocity: () => mtBody.Velocity,
                getSelfId: () => serverGuid,
                setTarget: (ctx, tlid, radius, q) => host.SetTarget(ctx, tlid, radius, q),
                clearTarget: () => host.ClearTarget(),
                getTargetQuantum: () => host.TargetManager.GetTargetQuantum(),
                setTargetQuantum: q => host.TargetManager.SetTargetQuantum(q),
                curTime: NowSeconds);
            mtm.StickTo = (tlid, radius, height) =>
                host.PositionManager.StickTo(tlid, radius, height);
            mtm.Unstick = host.PositionManager.UnStick;
            return mtm;
        };
        rm.Motion.InterruptCurrentMovement =
            () => rmT.Movement.CancelMoveTo(
                AcDream.Core.Physics.WeenieError.ActionCancelled);

        var configuredHost = new EntityPhysicsHost(
            serverGuid,
            getPosition: () => new AcDream.Core.Physics.Position(
                hostRecord.FullCellId,
                hostRecord.WorldEntity?.Position ?? mtBody.Position,
                mtBody.Orientation),
            getVelocity: () => mtBody.Velocity,
            getRadius: () => GetSetupCylinder(serverGuid, selfEntity).Radius,
            inContact: () => mtBody.OnWalkable,
            minterpMaxSpeed: () => rmT.Motion.GetAdjustedMaxSpeed(),
            curTime: NowSeconds,
            physicsTimerTime: NowSeconds,
            getObjectA: ResolvePhysicsHost,
            handleUpdateTarget: info => rmT.Movement.HandleUpdateTarget(info),
            interruptCurrentMovement: () => rmT.Movement.CancelMoveTo(
                AcDream.Core.Physics.WeenieError.ActionCancelled));
        host = EntityPhysicsHostComposition.InstallOrRebind(
            liveEntities,
            hostRecord,
            configuredHost);
        rm.MarkFullPhysicsHostBound();

        rm.Movement.MakeMoveToManager();
        rm.Motion.UnstickFromObject = host.PositionManager.UnStick;
        return rm.Sink;
    }

    public AcDream.Core.Physics.Motion.IPhysicsObjHost? ResolvePhysicsHost(uint id)
    {
        if (_liveEntities is not { } liveEntities)
            return null;

        bool isActive = liveEntities.TryGetRecord(id, out LiveEntityRecord activeRecord);
        if (!isActive)
            return null;

        if (liveEntities.IsHidden(id))
        {
            return null;
        }
        if (liveEntities.TryGetPhysicsHost(id, out var existing))
            return existing;

        double NowSeconds() => (System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalSeconds;
        var minimal = EntityPhysicsHostComposition.CreateMinimal(
            activeRecord,
            ResolvePhysicsHost,
            NowSeconds);
        liveEntities.InstallPhysicsHost(activeRecord, minimal);
        return minimal;
    }

    public (float Radius, float Height) GetSetupCylinder(
        uint serverGuid, AcDream.Core.World.WorldEntity entity)
    {
        FlatSetupCollision? setup =
            _physicsDataCache.GetFlatSetup(entity.SourceGfxObjOrSetupId);
        if (setup is null)
            return (0f, 0f);
        float scale =
            _liveEntities.Snapshots.TryGetValue(serverGuid, out var sp)
                && sp.ObjScale is { } objScale && objScale > 0f
            ? objScale
            : (entity.Scale > 0f ? entity.Scale : 1f);
        return (setup.Radius * scale, setup.Height * scale);
    }

    public (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)
        GetSetupMoverShape(uint serverGuid, AcDream.Core.World.WorldEntity entity)
    {
        FlatSetupCollision? setup =
            _physicsDataCache.GetFlatSetup(entity.SourceGfxObjOrSetupId);
        if (setup is null)
            return (ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f);

        float scale =
            _liveEntities.Snapshots.TryGetValue(serverGuid, out var sp)
                && sp.ObjScale is { } objScale && objScale > 0f
            ? objScale
            : (entity.Scale > 0f ? entity.Scale : 1f);

        float stepUp = setup.StepUpHeight > 0f ? setup.StepUpHeight * scale : 0.4f;
        float stepDown = setup.StepDownHeight > 0f ? setup.StepDownHeight * scale : 0.4f;
        return (setup.Spheres, scale, stepUp, stepDown);
    }


    public void StickToObjectFromWire(
        AcDream.Core.Physics.Motion.IPhysicsObjHost? host,
        uint targetGuid)
    {
        if (host is not EntityPhysicsHost entityHost)
            return;
        if (_liveEntities is not { } liveEntities
            || !liveEntities.TryGetInteractionEligibleEntity(targetGuid, out var tgtEnt))
            return;
        var (radius, height) = GetSetupCylinder(targetGuid, tgtEnt);
        entityHost.PositionManager.StickTo(targetGuid, radius, height);
    }

    public void ClearTargetForHiddenEntity(uint serverGuid)
    {
        if (_selectionInteractions() is { } interactions)
            interactions.OnEntityHidden(serverGuid);
        else if (_selection.SelectedObjectId == serverGuid)
            _selection.Clear(
                AcDream.Core.Selection.SelectionChangeSource.System,
                AcDream.Core.Selection.SelectionChangeReason.Cleared);

        if (_liveEntities?.TryGetPhysicsHost(serverGuid, out var hiddenHost) == true
            && hiddenHost is EntityPhysicsHost hiddenEntityHost)
            hiddenEntityHost.NotifyHidden();
    }

    public bool RouteServerMoveTo(
        AcDream.Core.Physics.Motion.MovementManager movement,
        uint cellId,
        AcDream.Core.Net.WorldSession.EntityMotionUpdate update)
    {
        if (update.MotionState.IsServerControlledMoveTo
            && update.MotionState.MoveToPath is { } path)
        {
            // my_run_rate write (unpack_movement @300603).
            if (update.MotionState.MoveToRunRate is { } mtRunRate)
                movement.Minterp.MyRunRate = mtRunRate;

            var destWorld = AcDream.Core.Physics.Motion.MoveToMath
                .OriginToWorld(
                    path.OriginCellId, path.OriginX, path.OriginY, path.OriginZ,
                    _origin.CenterX, _origin.CenterY);
            var mp = AcDream.Core.Physics.Motion.MovementParameters.FromWire(
                path.Bitfield,
                path.DistanceToObject,
                path.MinDistance,
                path.FailDistance,
                update.MotionState.MoveToSpeed ?? 1f,
                path.WalkRunThreshold,
                path.DesiredHeading);

            var ms = new AcDream.Core.Physics.MovementStruct
            {
                Params = mp,
            };
            // mt 6 with a resolvable target → MoveToObject (the P4 tracker
            // feeds position updates per tick); else degrade to
            // MoveToPosition at the wire origin (§2f).
            if (update.MotionState.MovementType == 6
                && path.TargetGuid is { } tgtGuid
                && _liveEntities is { } liveMoveEntities
                && liveMoveEntities.TryGetInteractionEligibleEntity(tgtGuid, out var tgtEnt)
                && ResolvePhysicsHost(tgtGuid) is not null)
            {
                ms.Type = AcDream.Core.Physics.MovementType.MoveToObject;
                ms.ObjectId = tgtGuid;
                ms.TopLevelId = tgtGuid;
                (ms.Radius, ms.Height) = GetSetupCylinder(tgtGuid, tgtEnt);
                ms.Pos = new AcDream.Core.Physics.Position(
                    cellId, tgtEnt.Position,
                    System.Numerics.Quaternion.Identity);
            }
            else
            {
                ms.Type = AcDream.Core.Physics.MovementType.MoveToPosition;
                ms.Pos = new AcDream.Core.Physics.Position(
                    cellId, destWorld,
                    System.Numerics.Quaternion.Identity);
            }
            movement.PerformMovement(ms);
            return true;
        }

        if (update.MotionState.IsServerControlledTurnTo
            && update.MotionState.TurnToPath is { } turnPath)
        {
            var mp = AcDream.Core.Physics.Motion.MovementParameters.FromWireTurnTo(
                turnPath.Bitfield,
                turnPath.Speed,
                turnPath.DesiredHeading);

            var ms = new AcDream.Core.Physics.MovementStruct { Params = mp };
            if (update.MotionState.MovementType == 8
                && turnPath.TargetGuid is { } turnTgt
                && _liveEntities is { } liveTurnEntities
                && liveTurnEntities.TryGetInteractionEligibleEntity(turnTgt, out var turnEnt))
            {
                ms.Type = AcDream.Core.Physics.MovementType.TurnToObject;
                ms.ObjectId = turnTgt;
                ms.TopLevelId = turnTgt;
                ms.Pos = new AcDream.Core.Physics.Position(
                    cellId, turnEnt.Position,
                    System.Numerics.Quaternion.Identity);
            }
            else
            {
                ms.Type = AcDream.Core.Physics.MovementType.TurnToHeading;
                if (update.MotionState.MovementType == 8
                    && turnPath.WireHeading is { } wireHeading)
                {
                    mp.DesiredHeading = wireHeading;
                }
            }
            movement.PerformMovement(ms);
            return true;
        }

        return false;
    }


}
