using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;

namespace AcDream.App.Rendering;

internal sealed class LiveEntityAnimationPresenter
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILiveStaticPartFrameSource _staticFrames;
    private readonly EntityEffectPoseRegistry _effectPoses;
    private readonly ILiveAnimationPresentationContext _context;
    private readonly AnimationPresentationDiagnostics _diagnostics;
    private readonly int _hidePartIndex;
    private readonly List<LiveEntityRecord> _snapshot = new();
    private bool _isPresenting;

    public LiveEntityAnimationPresenter(
        LiveEntityRuntime liveEntities,
        ILiveStaticPartFrameSource staticFrames,
        EntityEffectPoseRegistry effectPoses,
        ILiveAnimationPresentationContext context,
        AnimationPresentationDiagnostics diagnostics,
        int hidePartIndex)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _staticFrames = staticFrames ?? throw new ArgumentNullException(nameof(staticFrames));
        _effectPoses = effectPoses ?? throw new ArgumentNullException(nameof(effectPoses));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _hidePartIndex = hidePartIndex;
    }

    public void PrepareAnimation(
        LiveEntityRecord record,
        LiveEntityAnimationState animation)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(animation);
        LiveEntityRuntime runtime = _liveEntities;
        if (!runtime.IsCurrentAnimationOwner(record, animation)
            || !ReferenceEquals(record.WorldEntity, animation.Entity)
            || animation.Sequencer is not { MotionDoneTarget: null } sequencer)
        {
            return;
        }

        WorldEntity capturedEntity = animation.Entity;
        sequencer.MotionDoneTarget = (motion, success) =>
        {
            LiveEntityRuntime current = _liveEntities;
            if (!current.IsCurrentAnimationOwner(record, animation)
                || !ReferenceEquals(record.WorldEntity, capturedEntity)
                || !ReferenceEquals(animation.Entity, capturedEntity))
            {
                return;
            }

            MotionInterpreter? interpreter = _context.ResolveMotionInterpreter(record);
            interpreter?.MotionDone(motion, success);
            if (_diagnostics.DumpMotionEnabled)
            {
                Console.WriteLine(
                    $"[MOTIONDONE] guid={record.ServerGuid:X8} motion=0x{motion:X8} "
                    + $"success={success} pending={(interpreter?.MotionsPending() ?? false)}");
            }
        };
    }

    public void Present(
        IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        LiveEntityRuntime runtime = _liveEntities;
        if (_isPresenting)
            return;

        _isPresenting = true;
        try
        {
            runtime.CopySpatialRootObjectRecordsTo(_snapshot);
            foreach (LiveEntityRecord record in _snapshot)
            {
                if (!TryGetCurrent(runtime, record, out WorldEntity entity, out LiveEntityAnimationState animation))
                    continue;

                PrepareAnimation(record, animation);
                if (!TryGetCurrent(runtime, record, entity, animation))
                    continue;

                LiveEntityAnimationSchedule schedule = default;
                if (record.ProjectionKey is { } key)
                    schedules.TryGetValue(key, out schedule);
                bool hasOrdinarySchedule = IsCurrentSchedule(runtime, schedule, record, entity, animation);
                IReadOnlyList<PartTransform>? sequenceFrames = hasOrdinarySchedule
                    ? schedule.SequenceFrames
                    : null;
                bool composeParts = hasOrdinarySchedule && schedule.ComposeParts;
                float legacyAdvanceSeconds = hasOrdinarySchedule
                    ? schedule.LegacyAdvanceSeconds
                    : 0f;
                ulong objectClockEpoch = record.ObjectClockEpoch;
                ulong projectionVersion = record.ProjectionMutationVersion;
                ulong presentationRevision = animation.PresentationRevision;

                if (_staticFrames.TryTakeLivePartFrames(
                        record,
                        entity,
                        animation,
                        objectClockEpoch,
                        projectionVersion,
                        presentationRevision,
                        out IReadOnlyList<PartTransform> staticPartFrames) == true)
                {
                    if (!TryGetCurrent(
                            runtime,
                            record,
                            entity,
                            animation,
                            objectClockEpoch,
                            projectionVersion,
                            presentationRevision))
                    {
                        continue;
                    }
                    sequenceFrames = staticPartFrames;
                    composeParts = true;
                }

                if (animation.Sequencer is not null)
                {
                    EmitSequenceDiagnostics(record, animation, sequenceFrames);
                }
                else
                {
                    int span = animation.HighFrame - animation.LowFrame;
                    bool hiddenLegacy = (record.FinalPhysicsState & PhysicsStateFlags.Hidden) != 0;
                    if (span <= 0 && !hiddenLegacy)
                        continue;
                    if (span > 0 && legacyAdvanceSeconds > 0f)
                    {
                        animation.CurrFrame = RetailAnimationCyclePlayback.Advance(
                            animation.CurrFrame,
                            animation.LowFrame,
                            animation.HighFrame,
                            animation.Framerate,
                            legacyAdvanceSeconds);
                    }
                }

                if (!composeParts
                    || !TryGetCurrent(
                        runtime,
                        record,
                        entity,
                        animation,
                        objectClockEpoch,
                        projectionVersion,
                        presentationRevision))
                {
                    continue;
                }

                ComposeAndPublish(runtime, record, entity, animation, sequenceFrames,
                    objectClockEpoch, projectionVersion, presentationRevision);
            }
        }
        finally
        {
            _isPresenting = false;
        }
    }

    private void ComposeAndPublish(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        LiveEntityAnimationState animation,
        IReadOnlyList<PartTransform>? sequenceFrames,
        ulong objectClockEpoch,
        ulong projectionVersion,
        ulong presentationRevision)
    {
        int partCount = animation.PartTemplate.Count;
        EmitPartDiagnostics(record, animation, sequenceFrames, partCount);
        EnsureRetainedPoses(animation);
        List<MeshRef> meshRefs = animation.MeshRefsScratch;
        meshRefs.Clear();
        List<Matrix4x4> rigidPoses = animation.EffectPartPosesScratch;
        List<Matrix4x4> visualPoses = animation.VisualPartPosesScratch;
        Matrix4x4 scale = animation.Scale == 1f
            ? Matrix4x4.Identity
            : Matrix4x4.CreateScale(animation.Scale);

        for (int i = 0; i < partCount; i++)
        {
            if (TryResolvePartFrame(
                    animation,
                    sequenceFrames,
                    i,
                    out Vector3 origin,
                    out Quaternion orientation))
            {
                Vector3 defaultScale = i < animation.Setup.DefaultScale.Count
                    ? animation.Setup.DefaultScale[i]
                    : Vector3.One;
                Matrix4x4 visual = Matrix4x4.CreateScale(defaultScale)
                    * Matrix4x4.CreateFromQuaternion(orientation)
                    * Matrix4x4.CreateTranslation(origin);
                if (animation.Scale != 1f)
                    visual *= scale;
                visualPoses[i] = visual;
                rigidPoses[i] = Matrix4x4.CreateFromQuaternion(orientation)
                    * Matrix4x4.CreateTranslation(origin * animation.Scale);
            }

            LiveAnimationPartTemplate template = animation.PartTemplate[i];
            if (!template.IsDrawable
                || (_hidePartIndex >= 0 && i == _hidePartIndex && partCount >= 10))
            {
                continue;
            }
            meshRefs.Add(new MeshRef(template.GfxObjId, visualPoses[i])
            {
                SurfaceOverrides = template.SurfaceOverrides,
            });
        }

        if (!TryGetCurrent(runtime, record, entity, animation, objectClockEpoch, projectionVersion, presentationRevision))
            return;
        entity.MeshRefs = meshRefs;
        entity.SetIndexedPartPoses(rigidPoses, animation.PartAvailability);
        if (!TryGetCurrent(runtime, record, entity, animation, objectClockEpoch, projectionVersion, presentationRevision))
            return;
        _effectPoses.Publish(entity, rigidPoses, animation.PartAvailability);
    }

    private static bool TryResolvePartFrame(
        LiveEntityAnimationState animation,
        IReadOnlyList<PartTransform>? sequenceFrames,
        int partIndex,
        out Vector3 origin,
        out Quaternion orientation)
    {
        if (sequenceFrames is not null)
        {
            if (partIndex < sequenceFrames.Count)
            {
                origin = sequenceFrames[partIndex].Origin;
                orientation = sequenceFrames[partIndex].Orientation;
                return true;
            }
            origin = default;
            orientation = default;
            return false;
        }

        return RetailAnimationCyclePlayback.TryInterpolatePart(
            animation.Animation,
            animation.CurrFrame,
            animation.LowFrame,
            animation.HighFrame,
            partIndex,
            out origin,
            out orientation);
    }

    private static void EnsureRetainedPoses(LiveEntityAnimationState animation)
    {
        int partCount = animation.PartTemplate.Count;
        if (animation.PresentationPosesInitialized
            && animation.VisualPartPosesScratch.Count == partCount
            && animation.EffectPartPosesScratch.Count == partCount)
        {
            return;
        }

        List<Matrix4x4> visual = animation.VisualPartPosesScratch;
        List<Matrix4x4> rigid = animation.EffectPartPosesScratch;
        visual.Clear();
        rigid.Clear();
        bool[] consumed = new bool[animation.Entity.MeshRefs.Count];
        for (int i = 0; i < partCount; i++)
        {
            Matrix4x4 rigidRest = i < animation.Entity.IndexedPartTransforms.Count
                ? animation.Entity.IndexedPartTransforms[i]
                : Matrix4x4.Identity;
            rigid.Add(rigidRest);

            LiveAnimationPartTemplate template = animation.PartTemplate[i];
            Matrix4x4 visualRest = default;
            bool found = false;
            for (int meshIndex = 0; meshIndex < animation.Entity.MeshRefs.Count; meshIndex++)
            {
                MeshRef mesh = animation.Entity.MeshRefs[meshIndex];
                if (consumed[meshIndex] || mesh.GfxObjId != template.GfxObjId)
                    continue;
                consumed[meshIndex] = true;
                visualRest = mesh.PartTransform;
                found = true;
                break;
            }
            if (!found)
            {
                Vector3 defaultScale = i < animation.Setup.DefaultScale.Count
                    ? animation.Setup.DefaultScale[i]
                    : Vector3.One;
                visualRest = Matrix4x4.CreateScale(defaultScale * animation.Scale)
                    * rigidRest;
            }
            visual.Add(visualRest);
        }
        animation.PresentationPosesInitialized = true;
    }

    private void EmitSequenceDiagnostics(
        LiveEntityRecord record,
        LiveEntityAnimationState animation,
        IReadOnlyList<PartTransform>? sequenceFrames)
    {
        if (!_diagnostics.RemoteVelocityEnabled
            || record.ServerGuid == 0
            || record.ServerGuid == _context.LocalPlayerGuid
            || record.RemoteMotionRuntime is null)
        {
            return;
        }
        double now = _diagnostics.Now();
        if (now - animation.LastSequenceDiagnosticTime <= 1d)
            return;
        AnimationSequencer sequencer = animation.Sequencer!;
        Console.WriteLine(
            $"[SEQSTATE] guid={record.ServerGuid:X8} CurrentMotion=0x{sequencer.CurrentMotion:X8} "
            + $"CurrentSpeedMod={sequencer.CurrentSpeedMod:F3}");
        var node = sequencer.CurrentNodeDiag;
        int firstHash = sequencer.FirstCyclicAnimRefHash;
        Console.WriteLine(
            $"[CURRNODE] guid={record.ServerGuid:X8} animRef=0x{node.AnimRefHash:X8} "
            + $"firstCyclicAnimRef=0x{firstHash:X8} "
            + $"isOnCyclic={node.AnimRefHash == firstHash && firstHash != 0} "
            + $"isLooping={node.IsLooping} fr={node.Framerate:F2} "
            + $"frame=[{node.StartFrame}..{node.EndFrame}] "
            + $"pos={node.FramePosition:F2} qCount={node.QueueCount}");
        animation.LastSequenceDiagnosticTime = now;
    }

    private void EmitPartDiagnostics(
        LiveEntityRecord record,
        LiveEntityAnimationState animation,
        IReadOnlyList<PartTransform>? sequenceFrames,
        int partCount)
    {
        if (!_diagnostics.RemoteVelocityEnabled
            || record.ServerGuid == 0
            || record.ServerGuid == _context.LocalPlayerGuid
            || record.RemoteMotionRuntime is null)
        {
            return;
        }
        double now = _diagnostics.Now();
        if (now - animation.LastPartDiagnosticTime <= 1d)
            return;
        int animationPartCount = animation.Animation is not null
            && animation.Animation.PartFrames.Count > 0
                ? animation.Animation.PartFrames[0].Frames.Count
                : -1;
        double hash = 0d;
        if (sequenceFrames is not null)
        {
            foreach (PartTransform frame in sequenceFrames)
            {
                hash += frame.Origin.X + frame.Origin.Y + frame.Origin.Z
                    + frame.Orientation.X + frame.Orientation.Y
                    + frame.Orientation.Z + frame.Orientation.W;
            }
        }
        Console.WriteLine(
            $"[PARTSDIAG] guid={record.ServerGuid:X8} pt.Count={partCount} "
            + $"seqFrames.Count={sequenceFrames?.Count ?? -1} "
            + $"setup.Parts.Count={animation.Setup.Parts.Count} "
            + $"anim.PartFrames[0].Frames.Count={animationPartCount} seqHash={hash:F4}");
        animation.LastPartDiagnosticTime = now;
    }

    private static bool IsCurrentSchedule(
        LiveEntityRuntime runtime,
        LiveEntityAnimationSchedule schedule,
        LiveEntityRecord record,
        WorldEntity entity,
        LiveEntityAnimationState animation) =>
        schedule.ComposeParts
        && ReferenceEquals(schedule.Record, record)
        && ReferenceEquals(schedule.Entity, entity)
        && ReferenceEquals(schedule.Animation, animation)
        && TryGetCurrent(
            runtime,
            record,
            entity,
            animation,
            schedule.ObjectClockEpoch,
            schedule.ProjectionMutationVersion,
            schedule.PresentationRevision);

    private static bool TryGetCurrent(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        out WorldEntity entity,
        out LiveEntityAnimationState animation)
    {
        WorldEntity? currentEntity = record.WorldEntity;
        LiveEntityAnimationState? currentAnimation =
            record.AnimationRuntime as LiveEntityAnimationState;
        entity = currentEntity!;
        animation = currentAnimation!;
        return currentEntity is not null
            && currentAnimation is not null
            && TryGetCurrent(runtime, record, entity, animation);
    }

    private static bool TryGetCurrent(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        LiveEntityAnimationState animation) =>
        runtime.IsCurrentSpatialAnimation(record, animation)
        && ReferenceEquals(record.WorldEntity, entity)
        && ReferenceEquals(animation.Entity, entity);

    private static bool TryGetCurrent(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        LiveEntityAnimationState animation,
        ulong objectClockEpoch,
        ulong projectionVersion,
        ulong presentationRevision) =>
        TryGetCurrent(runtime, record, entity, animation)
        && record.ObjectClockEpoch == objectClockEpoch
        && record.ProjectionMutationVersion == projectionVersion
        && animation.PresentationRevision == presentationRevision;
}
