using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Physics;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter.Types;
using RemoteMotion = AcDream.Runtime.Physics.RemoteMotion;

namespace AcDream.App.Rendering;

internal sealed class LiveEntityAnimationScheduler
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _localPlayer;
    private readonly RemotePhysicsUpdater _remotePhysics;
    private readonly LiveEntityOrdinaryPhysicsUpdater _ordinaryPhysics;
    private readonly ProjectileController _projectiles;
    private readonly IEntityRootPosePublisher _rootPoses;
    private readonly IAnimationHookCaptureSink _animationHooks;
    private readonly List<LiveEntityRecord> _rootSnapshot = new();
    private readonly Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        _schedules = [];
    private readonly Frame _rootFrameScratch = new();
    private readonly MotionDeltaFrame _rootDeltaScratch = new();

    public LiveEntityAnimationScheduler(
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource localPlayer,
        RemotePhysicsUpdater remotePhysics,
        LiveEntityOrdinaryPhysicsUpdater ordinaryPhysics,
        ProjectileController projectiles,
        IEntityRootPosePublisher rootPoses,
        IAnimationHookCaptureSink animationHooks)
    {
        _liveEntities = liveEntities
            ?? throw new ArgumentNullException(nameof(liveEntities));
        _localPlayer = localPlayer
            ?? throw new ArgumentNullException(nameof(localPlayer));
        _remotePhysics = remotePhysics
            ?? throw new ArgumentNullException(nameof(remotePhysics));
        _ordinaryPhysics = ordinaryPhysics
            ?? throw new ArgumentNullException(nameof(ordinaryPhysics));
        _projectiles = projectiles ?? throw new ArgumentNullException(nameof(projectiles));
        _rootPoses = rootPoses ?? throw new ArgumentNullException(nameof(rootPoses));
        _animationHooks = animationHooks ?? throw new ArgumentNullException(nameof(animationHooks));
    }

    public IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> Tick(
        float elapsedSeconds,
        Vector3? playerPosition,
        bool localHiddenPartPoseDirty,
        int liveCenterX,
        int liveCenterY,
        Action<LiveEntityRecord, LiveEntityAnimationState>? prepareAnimation = null)
    {
        _schedules.Clear();
        LiveEntityRuntime runtime = _liveEntities;

        runtime.CopySpatialRootObjectRecordsTo(_rootSnapshot);
        foreach (LiveEntityRecord record in _rootSnapshot)
        {
            if (!runtime.IsCurrentSpatialRootObject(record)
                || record.WorldEntity is not { } entity)
            {
                continue;
            }

            LiveEntityAnimationState? animation = record.AnimationRuntime as LiveEntityAnimationState;
            RemoteMotion? remote = record.RemoteMotionRuntime as RemoteMotion;
            RuntimeProjectile? projectile =
                record.ProjectileRuntime as RuntimeProjectile;
            ulong objectClockEpoch = record.ObjectClockEpoch;

            if (!IsCurrent(
                    runtime,
                    record,
                    entity,
                    animation,
                    remote,
                    projectile,
                    objectClockEpoch))
                continue;

            if (animation is not null)
            {
                prepareAnimation?.Invoke(record, animation);
                if (!IsCurrent(
                        runtime,
                        record,
                        entity,
                        animation,
                        remote,
                        projectile,
                        objectClockEpoch))
                {
                    continue;
                }
            }

            LiveEntityAnimationSchedule schedule = AdvanceRecord(
                runtime,
                record,
                entity,
                animation,
                remote,
                projectile,
                elapsedSeconds,
                playerPosition,
                localHiddenPartPoseDirty,
                liveCenterX,
                liveCenterY,
                objectClockEpoch);

            if (animation is not null
                && schedule.ComposeParts
                && IsCurrent(
                    runtime,
                    record,
                    entity,
                    animation,
                    remote,
                    projectile,
                    objectClockEpoch))
            {
                IReadOnlyList<PartTransform>? ownedFrames = schedule.SequenceFrames is { } produced
                    ? animation.CaptureScheduleFrames(produced)
                    : null;
                RuntimeEntityKey key = record.ProjectionKey
                    ?? throw new InvalidOperationException(
                        $"Live entity 0x{record.ServerGuid:X8}/" +
                        $"{record.Generation} has no exact projection key.");
                _schedules[key] = schedule with
                {
                    SequenceFrames = ownedFrames,
                    Record = record,
                    Entity = entity,
                    Animation = animation,
                    ObjectClockEpoch = objectClockEpoch,
                    ProjectionMutationVersion = record.ProjectionMutationVersion,
                    PresentationRevision = animation.PresentationRevision,
                };
            }
        }

        return _schedules;
    }

    private LiveEntityAnimationSchedule AdvanceRecord(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        LiveEntityAnimationState? animation,
        RemoteMotion? remote,
        RuntimeProjectile? projectile,
        float elapsedSeconds,
        Vector3? playerPosition,
        bool localHiddenPartPoseDirty,
        int liveCenterX,
        int liveCenterY,
        ulong objectClockEpoch)
    {
        uint serverGuid = record.ServerGuid;
        AnimationSequencer? sequencer = animation?.Sequencer;
        PhysicsStateFlags state = record.FinalPhysicsState;
        bool hidden = (state & PhysicsStateFlags.Hidden) != 0;

        if (serverGuid == _localPlayer.ServerGuid)
        {
            IReadOnlyList<PartTransform>? prepared =
                animation?.PreparedSequenceFrames;
            bool advanced = animation?.SequenceAdvancedBeforeAnimationPass == true;
            if (animation is not null)
            {
                animation.PreparedSequenceFrames = null;
                animation.SequenceAdvancedBeforeAnimationPass = false;
            }

            bool composeHidden = hidden && localHiddenPartPoseDirty;
            if (!IsCurrent(
                    runtime,
                    record,
                    entity,
                    animation,
                    remote,
                    projectile,
                    objectClockEpoch))
            {
                return default;
            }

            _rootPoses.UpdateRoot(entity);
            if (!IsCurrent(
                    runtime,
                    record,
                    entity,
                    animation,
                    remote,
                    projectile,
                    objectClockEpoch))
            {
                return default;
            }

            return new LiveEntityAnimationSchedule(
                prepared ?? (composeHidden && sequencer is not null
                    ? animation?.CaptureSequenceFrames(sequencer.SampleCurrentPose())
                    : null),
                0f,
                ComposeParts: advanced || composeHidden);
        }

        RetailObjectActivityResult activity = RetailObjectActivityGate.Evaluate(
            record.ObjectClock,
            remote?.Body ?? projectile?.Body ?? record.PhysicsBody,
            runtime.GetRootObjectClockDisposition(serverGuid)
                is RetailObjectClockDisposition.Advance,
            record.HasPartArray,
            (state & PhysicsStateFlags.Static) != 0,
            entity.Position,
            playerPosition,
            elapsedSeconds);
        if (activity is not RetailObjectActivityResult.Active)
            return default;

        RetailObjectQuantumBatch batch = record.ObjectClock.Advance(elapsedSeconds);
        if (batch.Count == 0)
            return default;

        IReadOnlyList<PartTransform>? frames = null;
        float legacyElapsed = 0f;
        bool completed = true;
        ProjectileController projectileController = _projectiles;
        bool projectileHandlesMovement = projectile is not null
            && projectileController?.HandlesMovement(serverGuid) == true;
        float objectScale = animation?.Scale
            ?? record.Snapshot.Physics?.Scale
            ?? record.Snapshot.ObjScale
            ?? entity.Scale;

        for (int qi = 0; qi < batch.Count; qi++)
        {
            if (!IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
            {
                completed = false;
                break;
            }

            float quantum = batch.GetQuantum(qi);
            if (hidden)
            {
                if (remote is not null)
                {
                    if (!_remotePhysics.TickHidden(
                        remote,
                        entity,
                        quantum,
                        sequencer?.Manager,
                        _animationHooks.Capture,
                        sequencer,
                        runtime,
                        record,
                        objectClockEpoch))
                    {
                        completed = false;
                        break;
                    }
                }
                else
                {
                    if (sequencer is not null)
                    {
                        _animationHooks.Capture(entity.Id, sequencer);
                        if (!IsCurrent(
                                runtime,
                                record,
                                entity,
                                animation,
                                remote,
                                projectile,
                                objectClockEpoch))
                        {
                            completed = false;
                            break;
                        }
                    }
                    RunManagerTail(remote, sequencer?.Manager);
                }

                if (!IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
                {
                    completed = false;
                    break;
                }
                continue;
            }

            Frame rootFrame = animation?.RootMotionScratch ?? _rootFrameScratch;
            rootFrame.Origin = Vector3.Zero;
            rootFrame.Orientation = Quaternion.Identity;
            if (sequencer is not null)
                frames = animation?.CaptureSequenceFrames(
                    sequencer.Advance(quantum, rootFrame))
                    ?? sequencer.Advance(quantum, rootFrame);
            else if (animation is not null)
                legacyElapsed += quantum;

            if (!IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
            {
                completed = false;
                break;
            }

            if (remote is not null && !projectileHandlesMovement)
            {
                if (animation is not null && quantum > 0f)
                {
                    float rootMotionSpeed = rootFrame.Origin.Length()
                        * objectScale / quantum;
                    remote.MaxRootMotionSpeedSinceLastUP = MathF.Max(
                        remote.MaxRootMotionSpeedSinceLastUP,
                        rootMotionSpeed);
                }

                MotionDeltaFrame rootDelta = animation?.RootMotionDeltaScratch
                    ?? _rootDeltaScratch;
                rootDelta.Origin = rootFrame.Origin;
                rootDelta.Orientation = rootFrame.Orientation;
                if (!_remotePhysics.Tick(
                    remote,
                    entity,
                    objectScale,
                    sequencer,
                    animation,
                    quantum,
                    rootDelta,
                    liveCenterX,
                    liveCenterY,
                    _animationHooks.Capture,
                    runtime,
                    record,
                    objectClockEpoch))
                {
                    completed = false;
                    break;
                }
            }
            else
            {
                bool ordinaryBodyHandlesMovement = projectile is null
                    && record.PhysicsBody is not null;
                if (ordinaryBodyHandlesMovement)
                {
                    if (!_ordinaryPhysics.Tick(
                            runtime,
                            record,
                            entity,
                            rootFrame,
                            objectScale,
                            quantum,
                            liveCenterX,
                            liveCenterY,
                            objectClockEpoch,
                            sequencer,
                            _animationHooks.Capture))
                    {
                        completed = false;
                        break;
                    }
                }
                else
                {
                    ApplyRootFrame(record, entity, rootFrame, objectScale);
                }
                if (!IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
                {
                    completed = false;
                    break;
                }

                ProjectileController.QuantumStep projectileStep = default;
                bool beganProjectile = projectileHandlesMovement
                    && projectileController?.TryBeginQuantum(
                        record,
                        quantum,
                        out projectileStep) == true;

                if (!ordinaryBodyHandlesMovement && sequencer is not null)
                    _animationHooks.Capture(entity.Id, sequencer);
                if (!IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
                {
                    completed = false;
                    break;
                }

                if (beganProjectile
                    && !projectileController!.CompleteQuantum(
                        projectileStep,
                        liveCenterX,
                        liveCenterY))
                {
                    completed = false;
                    break;
                }
                if (!IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
                {
                    completed = false;
                    break;
                }

                RunManagerTail(remote, sequencer?.Manager);
            }

            if (!IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
            {
                completed = false;
                break;
            }
        }

        if (!completed
            || !IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
        {
            return default;
        }

        _rootPoses.UpdateRoot(entity);
        if (!IsCurrent(runtime, record, entity, animation, remote, projectile, objectClockEpoch))
            return default;

        if (hidden)
        {
            return new LiveEntityAnimationSchedule(
                sequencer is not null && animation is not null
                    ? animation.CaptureSequenceFrames(sequencer.SampleCurrentPose())
                    : null,
                0f,
                ComposeParts: animation is not null);
        }

        return new LiveEntityAnimationSchedule(
            frames,
            legacyElapsed,
            ComposeParts: animation is not null);
    }

    private static void ApplyRootFrame(
        LiveEntityRecord record,
        WorldEntity entity,
        Frame rootFrame,
        float objectScale)
    {
        PhysicsBody? body = record.PhysicsBody;
        Vector3 position = body?.Position ?? entity.Position;
        Quaternion orientation = body?.Orientation ?? entity.Rotation;
        Vector3 localOrigin = body?.OnWalkable == true
            ? rootFrame.Origin * objectScale
            : Vector3.Zero;

        if (localOrigin != Vector3.Zero)
            position += Vector3.Transform(localOrigin, orientation);
        if (!rootFrame.Orientation.IsIdentity)
        {
            orientation = FrameOps.SetRotate(
                position,
                orientation,
                orientation * rootFrame.Orientation);
        }

        if (body is not null)
        {
            body.Position = position;
            body.Orientation = orientation;
        }

        entity.SetPosition(position);
        entity.Rotation = orientation;
    }

    private static bool IsCurrent(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        LiveEntityAnimationState? animation,
        RemoteMotion? remote,
        RuntimeProjectile? projectile,
        ulong objectClockEpoch) =>
        runtime.IsCurrentSpatialRootObject(record)
        && record.ObjectClockEpoch == objectClockEpoch
        && ReferenceEquals(record.WorldEntity, entity)
        && ReferenceEquals(record.AnimationRuntime, animation)
        && ReferenceEquals(record.RemoteMotionRuntime, remote)
        && ReferenceEquals(record.ProjectileRuntime, projectile)
        && (animation is null || runtime.IsCurrentSpatialAnimation(record, animation));

    private static void RunManagerTail(
        RemoteMotion? remote,
        MotionTableManager? partArray)
    {
        if (remote is not null)
        {
            RetailObjectManagerTail.Run(
                remote.Host?.TargetManager,
                remote.Movement,
                partArray,
                remote.Host?.PositionManager);
        }
        else
        {
            partArray?.UseTime();
        }
    }
}

internal readonly record struct LiveEntityAnimationSchedule(
    IReadOnlyList<PartTransform>? SequenceFrames,
    float LegacyAdvanceSeconds,
    bool ComposeParts,
    LiveEntityRecord? Record = null,
    WorldEntity? Entity = null,
    LiveEntityAnimationState? Animation = null,
    ulong ObjectClockEpoch = 0UL,
    ulong ProjectionMutationVersion = 0UL,
    ulong PresentationRevision = 0UL);
