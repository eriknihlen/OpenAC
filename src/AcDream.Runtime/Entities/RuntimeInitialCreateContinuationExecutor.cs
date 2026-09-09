using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

internal enum RuntimeInitialCreateExecutionStatus : byte
{
    Completed,
    /// <summary>Initial authored placement not yet acknowledged; retry later.</summary>
    PendingPlacement,
    AwaitingContinuationPlacement,
    RejectedToken,
    RejectedAuthority,
}

internal readonly record struct RuntimeInitialCreateExecutionInputs(
    bool UsePositionFromServer,
    float PlayerDistance);

internal enum RuntimeInitialCreateExecutedActionKind : byte
{
    InitialAdoption,
    TeleportHookRequest,
    DeferredChildReplay,
    ParentRelationReplay,
    PreTailDescriptionAdaptation,
    ObjDesc,
    CreateParent,
    Parent,
    Pickup,
    Position,
    Movement,
    State,
    Vector,
    WeenieDescription,
    ResidentCellCleanup,
}

internal enum RuntimeDeferredChildReplayOutcome : byte
{
    Registered,
    ReDeferred,
    Rejected,
}

internal enum RuntimeParentRelationOutcome : byte
{
    Applied,
    DiscardedStaleParent,
    DeferredAwaitingParent,
    Rejected,
}

internal enum RuntimeResidentCellCleanupDisposition : byte
{
    ResidentUnmarked,
    DeferredUnderLostCellOwnership,
    CelllessNoWeenieMarkUnreachable,
}

internal readonly record struct RuntimeInitialCreateExecutedAction(
    RuntimeInitialCreateExecutedActionKind Kind,
    ulong Sequence,
    int Stage,
    RuntimeAuthoritativePositionDisposition? PositionDisposition,
    RuntimeTeleportHookPhase HookPhase,
    RuntimeDeferredChildReplayOutcome? DeferredChildOutcome = null,
    RuntimeResidentCellCleanupDisposition? ResidentCellCleanupDisposition = null,
    RuntimePositionConstrainPhase ConstrainPhase = RuntimePositionConstrainPhase.None,
    bool StopInterpolating = false,
    bool ZeroVelocity = false,
    bool PreserveHeading = false,
    bool SendPositionImmediately = false,
    bool UnparentBeforeRouting = false,
    RuntimeParentRelationOutcome? ParentRelationOutcome = null);

internal readonly record struct RuntimeInitialCreateExecutionReceipt(
    RuntimeEntityKey Entity,
    uint FullCellId,
    RuntimeTeleportHookPhase TeleportHookPhase,
    ImmutableArray<RuntimeInitialCreateExecutedAction> Trace,
    int ReplayedDeferredChildCount);

public enum RuntimeInitialCreateTeleportHookPhase : byte
{
    None,
    BeforePositionOperation,
    AfterPositionOperation,
    AfterEnterWorld,
}

public enum RuntimeInitialCreatePositionDisposition : byte
{
    RejectedAuthority,
    RejectedData,
    AwaitFreshPosition,
    NoPositionOperation,
    Interpolate,
    SetPosition,
    SetPositionSimple,
}

public enum RuntimeInitialCreatePositionConstrainPhase : byte
{
    None,
    BeforePositionOperation,
    AfterPositionOperation,
}

public readonly record struct RuntimeInitialCreatePositionRouteFact(
    ulong Sequence,
    RuntimeInitialCreatePositionDisposition Disposition,
    RuntimeInitialCreateTeleportHookPhase HookPhase,
    RuntimeInitialCreatePositionConstrainPhase ConstrainPhase,
    bool StopInterpolating,
    bool ZeroVelocity,
    bool PreserveHeading,
    bool SendPositionImmediately);

public readonly record struct RuntimeInitialCreatePlacementCompletion(
    RuntimeEntityKey Entity,
    uint FullCellId,
    RuntimeInitialCreateTeleportHookPhase TeleportHookPhase,
    ImmutableArray<RuntimeInitialCreatePositionRouteFact> PositionRouteFacts,
    int ReplayedDeferredChildCount);

internal sealed class RuntimeInitialCreateContinuationExecutor
{
    private enum InitialTailPhase : byte
    {
        NotStarted,
        Adopted,
        HookRecorded,
        DeferredReplayed,
        RelationsReplayed,
    }

    private readonly record struct PendingPublish(
        RuntimeEntityChange Change,
        Func<bool> Matches,
        RuntimePlacementCancellationReceipt Cancellation);

    private sealed class Progress
    {
        internal required ulong LeaseId { get; init; }
        internal ulong AppliedThroughSequence { get; set; }
        internal InitialTailPhase TailPhase { get; set; }
        internal int EnvelopeStageIndex { get; set; } = -1;
        internal RuntimeEntityPlacementToken PendingContinuationPlacement { get; set; }
        internal ulong PendingContinuationSequence { get; set; }
        internal RuntimeAuthoritativePositionRoute PendingContinuationRoute { get; set; }
        internal bool PositionMergeCommittedForRetry { get; set; }
        internal ulong PositionMergeCommittedVersion { get; set; }
        internal int ReplayedDeferredChildCount { get; set; }
        internal List<PendingPublish> EnvelopeBuffer { get; } = [];
        internal ImmutableArray<RuntimeInitialCreateExecutedAction>.Builder Trace { get; } =
            ImmutableArray.CreateBuilder<RuntimeInitialCreateExecutedAction>();
    }

    private readonly RuntimeEntityDirectory _entities;
    private readonly RuntimeInitialCreateResidenceState _residences;
    private readonly RuntimePhysicsState _physics;
    private readonly RuntimeEntityObjectEventStream _events;
    private readonly Func<WorldSession.EntitySpawn, bool, RuntimeEntityRegistrationResult>
        _registerDeferredChild;
    private readonly Func<RuntimeEntityRecord, ulong, WorldSession.EntitySpawn, bool, bool>
        _applyAcceptedSpawn;
    private readonly Dictionary<RuntimeEntityKey, Progress> _progress = [];
    private readonly HashSet<RuntimeEntityKey> _executing = [];
    private readonly Dictionary<RuntimeEntityKey,
        (ulong Sequence, RuntimeInitialCreateExecutionReceipt Receipt,
            RuntimeInitialCreatePlacementCompletion Public)>
        _completionReceipts = [];
    private Func<RuntimeGenerationToken>? _generation;
    private Func<bool>? _usePositionFromServer;
    private Func<Vector3?>? _localPlayerPosition;
    private bool _liveInputsBound;

    internal RuntimeInitialCreateContinuationExecutor(
        RuntimeEntityDirectory entities,
        RuntimeInitialCreateResidenceState residences,
        RuntimePhysicsState physics,
        RuntimeEntityObjectEventStream events,
        Func<WorldSession.EntitySpawn, bool, RuntimeEntityRegistrationResult>
            registerDeferredChild,
        Func<RuntimeEntityRecord, ulong, WorldSession.EntitySpawn, bool, bool>
            applyAcceptedSpawn)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _residences = residences
            ?? throw new ArgumentNullException(nameof(residences));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _registerDeferredChild = registerDeferredChild
            ?? throw new ArgumentNullException(nameof(registerDeferredChild));
        _applyAcceptedSpawn = applyAcceptedSpawn
            ?? throw new ArgumentNullException(nameof(applyAcceptedSpawn));
    }

    internal void BindGeneration(Func<RuntimeGenerationToken> generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (_generation is not null)
        {
            throw new InvalidOperationException(
                "The initial-create continuation executor's generation source is already bound.");
        }
        _generation = generation;
    }

    internal void BindLiveInputs(
        Func<bool> usePositionFromServer,
        Func<Vector3?> localPlayerPosition)
    {
        ArgumentNullException.ThrowIfNull(usePositionFromServer);
        ArgumentNullException.ThrowIfNull(localPlayerPosition);
        if (_liveInputsBound)
        {
            throw new InvalidOperationException(
                "The initial-create continuation executor's live-input sources are already bound.");
        }
        _usePositionFromServer = usePositionFromServer;
        _localPlayerPosition = localPlayerPosition;
        _liveInputsBound = true;
    }

    private RuntimeInitialCreateExecutionInputs ResolveInputs(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateExecutionInputs inputs)
    {
        bool usePositionFromServer = _usePositionFromServer is { } source
            ? source()
            : inputs.UsePositionFromServer;
        float playerDistance = inputs.PlayerDistance;
        if (_localPlayerPosition?.Invoke() is { } localPlayerPosition
            && (canonical.Snapshot.Physics?.Position
                ?? canonical.Snapshot.Position) is { } accepted)
        {
            var target = new Vector3(
                accepted.PositionX, accepted.PositionY, accepted.PositionZ);
            playerDistance = Vector3.Distance(target, localPlayerPosition);
        }
        return new RuntimeInitialCreateExecutionInputs(
            usePositionFromServer, playerDistance);
    }

    internal bool TryGetCompletionReceipt(
        in RuntimePlacementProjectionToken token,
        out RuntimeInitialCreateExecutionReceipt receipt)
    {
        if (_completionReceipts.TryGetValue(
                token.Entity,
                out (ulong Sequence, RuntimeInitialCreateExecutionReceipt Receipt,
                    RuntimeInitialCreatePlacementCompletion Public) entry)
            && entry.Sequence == token.Sequence)
        {
            receipt = entry.Receipt;
            return true;
        }
        receipt = default;
        return false;
    }

    internal bool TryGetCompletion(
        in RuntimePlacementProjectionToken token,
        out RuntimeInitialCreatePlacementCompletion completion)
    {
        if (_completionReceipts.TryGetValue(
                token.Entity,
                out (ulong Sequence, RuntimeInitialCreateExecutionReceipt Receipt,
                    RuntimeInitialCreatePlacementCompletion Public) entry)
            && entry.Sequence == token.Sequence)
        {
            completion = entry.Public;
            return true;
        }
        completion = default;
        return false;
    }

    private static RuntimeInitialCreateTeleportHookPhase MapHookPhase(
        RuntimeTeleportHookPhase phase) => phase switch
    {
        RuntimeTeleportHookPhase.None =>
            RuntimeInitialCreateTeleportHookPhase.None,
        RuntimeTeleportHookPhase.BeforePositionOperation =>
            RuntimeInitialCreateTeleportHookPhase.BeforePositionOperation,
        RuntimeTeleportHookPhase.AfterPositionOperation =>
            RuntimeInitialCreateTeleportHookPhase.AfterPositionOperation,
        RuntimeTeleportHookPhase.AfterEnterWorld =>
            RuntimeInitialCreateTeleportHookPhase.AfterEnterWorld,
        _ => throw new ArgumentOutOfRangeException(
            nameof(phase),
            phase,
            $"Unmapped {nameof(RuntimeTeleportHookPhase)} value - add an explicit arm to {nameof(MapHookPhase)} and to the public {nameof(RuntimeInitialCreateTeleportHookPhase)} projection."),
    };

    private static RuntimeInitialCreatePositionDisposition MapDisposition(
        RuntimeAuthoritativePositionDisposition disposition) => disposition switch
    {
        RuntimeAuthoritativePositionDisposition.RejectedAuthority =>
            RuntimeInitialCreatePositionDisposition.RejectedAuthority,
        RuntimeAuthoritativePositionDisposition.RejectedData =>
            RuntimeInitialCreatePositionDisposition.RejectedData,
        RuntimeAuthoritativePositionDisposition.AwaitFreshPosition =>
            RuntimeInitialCreatePositionDisposition.AwaitFreshPosition,
        RuntimeAuthoritativePositionDisposition.NoPositionOperation =>
            RuntimeInitialCreatePositionDisposition.NoPositionOperation,
        RuntimeAuthoritativePositionDisposition.Interpolate =>
            RuntimeInitialCreatePositionDisposition.Interpolate,
        RuntimeAuthoritativePositionDisposition.SetPosition =>
            RuntimeInitialCreatePositionDisposition.SetPosition,
        RuntimeAuthoritativePositionDisposition.SetPositionSimple =>
            RuntimeInitialCreatePositionDisposition.SetPositionSimple,
        _ => throw new ArgumentOutOfRangeException(
            nameof(disposition),
            disposition,
            $"Unmapped {nameof(RuntimeAuthoritativePositionDisposition)} value - add an explicit arm to {nameof(MapDisposition)} and to the public {nameof(RuntimeInitialCreatePositionDisposition)} projection."),
    };

    private static RuntimeInitialCreatePositionConstrainPhase MapConstrainPhase(
        RuntimePositionConstrainPhase phase) => phase switch
    {
        RuntimePositionConstrainPhase.None =>
            RuntimeInitialCreatePositionConstrainPhase.None,
        RuntimePositionConstrainPhase.BeforePositionOperation =>
            RuntimeInitialCreatePositionConstrainPhase.BeforePositionOperation,
        RuntimePositionConstrainPhase.AfterPositionOperation =>
            RuntimeInitialCreatePositionConstrainPhase.AfterPositionOperation,
        _ => throw new ArgumentOutOfRangeException(
            nameof(phase),
            phase,
            $"Unmapped {nameof(RuntimePositionConstrainPhase)} value - add an explicit arm to {nameof(MapConstrainPhase)} and to the public {nameof(RuntimeInitialCreatePositionConstrainPhase)} projection."),
    };

    private static RuntimeInitialCreatePlacementCompletion ProjectCompletion(
        in RuntimeInitialCreateExecutionReceipt receipt)
    {
        ImmutableArray<RuntimeInitialCreateExecutedAction> trace = receipt.Trace;
        int positionCount = 0;
        for (int i = 0; i < trace.Length; i++)
        {
            if (trace[i].Kind == RuntimeInitialCreateExecutedActionKind.Position)
                positionCount++;
        }

        ImmutableArray<RuntimeInitialCreatePositionRouteFact> positionFacts;
        if (positionCount == 0)
        {
            positionFacts = ImmutableArray<RuntimeInitialCreatePositionRouteFact>.Empty;
        }
        else
        {
            var builder = ImmutableArray.CreateBuilder<RuntimeInitialCreatePositionRouteFact>(
                positionCount);
            for (int i = 0; i < trace.Length; i++)
            {
                RuntimeInitialCreateExecutedAction action = trace[i];
                if (action.Kind != RuntimeInitialCreateExecutedActionKind.Position)
                    continue;
                if (action.PositionDisposition is not { } disposition)
                {
                    throw new InvalidOperationException(
                        "A Position-kind executor trace entry must always " +
                        "carry a non-null PositionDisposition - " +
                        "BuildPositionTrace (the sole producer of Kind.Position " +
                        "entries) always supplies route.Disposition.");
                }
                builder.Add(new RuntimeInitialCreatePositionRouteFact(
                    action.Sequence,
                    MapDisposition(disposition),
                    MapHookPhase(action.HookPhase),
                    MapConstrainPhase(action.ConstrainPhase),
                    action.StopInterpolating,
                    action.ZeroVelocity,
                    action.PreserveHeading,
                    action.SendPositionImmediately));
            }
            positionFacts = builder.MoveToImmutable();
        }

        return new RuntimeInitialCreatePlacementCompletion(
            receipt.Entity,
            receipt.FullCellId,
            MapHookPhase(receipt.TeleportHookPhase),
            positionFacts,
            receipt.ReplayedDeferredChildCount);
    }

    internal void ForgetCompletionReceipt(RuntimeEntityKey key, ulong sequence)
    {
        if (_completionReceipts.TryGetValue(
                key,
                out (ulong Sequence, RuntimeInitialCreateExecutionReceipt Receipt,
                    RuntimeInitialCreatePlacementCompletion Public) entry)
            && entry.Sequence == sequence)
        {
            _completionReceipts.Remove(key);
        }
    }

    internal int PendingCompletionReceiptCount => _completionReceipts.Count;

    internal int ProgressCount => _progress.Count;

    internal long ReplayFailureCount { get; private set; }
    internal Exception? LastReplayFailure { get; private set; }

    private void RecordReplayFailure(Exception error)
    {
        ReplayFailureCount++;
        LastReplayFailure = error;
    }

    internal bool TryGetPendingContinuationPlacement(
        RuntimeEntityKey key,
        out RuntimeEntityPlacementToken placement)
    {
        if (_progress.TryGetValue(key, out Progress? progress)
            && progress.PendingContinuationPlacement.IsValid)
        {
            placement = progress.PendingContinuationPlacement;
            return true;
        }
        placement = default;
        return false;
    }

    internal bool TryGetPendingContinuationRoute(
        RuntimeEntityKey key,
        out RuntimeAuthoritativePositionRoute route)
    {
        if (_progress.TryGetValue(key, out Progress? progress)
            && progress.PendingContinuationPlacement.IsValid)
        {
            route = progress.PendingContinuationRoute;
            return true;
        }
        route = default;
        return false;
    }

    internal void DiscardProgress(RuntimeEntityKey key)
    {
        _completionReceipts.Remove(key);
        if (!_progress.Remove(key, out Progress? progress))
            return;
        if (progress.PendingContinuationPlacement.IsValid)
        {
            RuntimePlacementCancellationReceipt cancellation =
                _physics.SetPosition.ForgetExactPlacement(
                    progress.PendingContinuationPlacement);
            _physics.SetPosition.PublishCancellation(cancellation);
        }
    }

    internal void DiscardAll()
    {
        foreach (Progress progress in _progress.Values)
        {
            if (!progress.PendingContinuationPlacement.IsValid)
                continue;
            RuntimePlacementCancellationReceipt cancellation =
                _physics.SetPosition.ForgetExactPlacement(
                    progress.PendingContinuationPlacement);
            _physics.SetPosition.PublishCancellation(cancellation);
        }
        _progress.Clear();
        _completionReceipts.Clear();
    }

    internal RuntimeInitialCreateExecutionStatus Execute(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        in RuntimeInitialCreateExecutionInputs inputs,
        out RuntimeInitialCreateExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        receipt = default;
        if (!token.IsValid || canonical.Key is not { } key)
            return RuntimeInitialCreateExecutionStatus.RejectedToken;

        if (!_executing.Add(key))
            return RuntimeInitialCreateExecutionStatus.RejectedAuthority;

        try
        {
            return ExecuteCore(canonical, token, inputs, key, out receipt);
        }
        finally
        {
            _executing.Remove(key);
        }
    }

    private RuntimeInitialCreateExecutionStatus ExecuteCore(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        in RuntimeInitialCreateExecutionInputs inputs,
        RuntimeEntityKey key,
        out RuntimeInitialCreateExecutionReceipt receipt)
    {
        receipt = default;
        RuntimeInitialCreateExecutionInputs effectiveInputs =
            ResolveInputs(canonical, inputs);

        if (_progress.TryGetValue(key, out Progress? existing)
            && existing.LeaseId != token.LeaseId)
        {
            DiscardProgress(key);
            return RuntimeInitialCreateExecutionStatus.RejectedAuthority;
        }

        Progress? progress = existing;

        while (true)
        {
            if (progress is not null && progress.PendingContinuationPlacement.IsValid)
            {
                _residences.AdvanceExecutorBaseline(
                    canonical,
                    token,
                    RuntimeExecutorBaselineFields.FullCellId
                        | RuntimeExecutorBaselineFields.PlacementCommitVersion);
            }
            RuntimeInitialCreateResidenceCompletionStatus completion =
                _residences.Complete(canonical, token, out RuntimeInitialCreateResidenceReceipt residenceReceipt);
            switch (completion)
            {
                case RuntimeInitialCreateResidenceCompletionStatus.PendingPlacement:
                    // Nothing has been drained yet for this exact lease -
                    // leave _progress exactly as found (untouched if it
                    // never existed).
                    return RuntimeInitialCreateExecutionStatus.PendingPlacement;
                case RuntimeInitialCreateResidenceCompletionStatus.RejectedToken:
                    DiscardProgress(key);
                    return RuntimeInitialCreateExecutionStatus.RejectedToken;
                case RuntimeInitialCreateResidenceCompletionStatus.RejectedAuthority:
                    DiscardProgress(key);
                    return RuntimeInitialCreateExecutionStatus.RejectedAuthority;
            }

            if (progress is null)
            {
                progress = new Progress { LeaseId = token.LeaseId };
                _progress[key] = progress;
            }

            if (progress.TailPhase != InitialTailPhase.DeferredReplayed)
            {
                RuntimeInitialCreateExecutionStatus tailStatus =
                    RunInitialTail(canonical, token, residenceReceipt, progress);
                if (tailStatus != RuntimeInitialCreateExecutionStatus.Completed)
                    return Abandon(canonical, key);
            }

            while (progress.AppliedThroughSequence
                < (ulong)residenceReceipt.Continuations.Length)
            {
                if (!_entities.IsCurrent(canonical) || canonical.Key != token.Entity)
                    return Abandon(canonical, key);

                int index = (int)progress.AppliedThroughSequence;
                RuntimeInitialCreateResidenceContinuation continuation =
                    residenceReceipt.Continuations[index];
                if (continuation.InstanceSequence != canonical.Incarnation)
                    return Abandon(canonical, key);

                RuntimeInitialCreateExecutionStatus applyStatus =
                    ApplyContinuation(canonical, token, key, continuation, effectiveInputs, progress);
                if (applyStatus
                    == RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement)
                {
                    return applyStatus;
                }
                if (applyStatus != RuntimeInitialCreateExecutionStatus.Completed)
                    return applyStatus;

                progress.AppliedThroughSequence = continuation.Sequence;
                progress.EnvelopeStageIndex = -1;
            }

            RuntimeInitialCreateResidenceExecutorReleaseStatus release =
                _residences.ConsumeExecuted(
                    canonical,
                    residenceReceipt.Adoption,
                    progress.AppliedThroughSequence);
            switch (release)
            {
                case RuntimeInitialCreateResidenceExecutorReleaseStatus.Released:
                {
                    var completedReceipt = new RuntimeInitialCreateExecutionReceipt(
                        key,
                        residenceReceipt.FullCellId,
                        residenceReceipt.TeleportHookPhase,
                        progress.Trace.ToImmutable(),
                        progress.ReplayedDeferredChildCount);
                    receipt = completedReceipt;
                    RuntimeInitialCreatePlacementCompletion publicCompletion =
                        ProjectCompletion(completedReceipt);
                    _progress.Remove(key);
                    _physics.SetPosition.PublishExecutorCompletion(
                        canonical,
                        beforePublish: token =>
                            _completionReceipts[key] =
                                (token.Sequence, completedReceipt, publicCompletion));
                    return RuntimeInitialCreateExecutionStatus.Completed;
                }
                case RuntimeInitialCreateResidenceExecutorReleaseStatus.Revised:
                    continue;
                default:
                    return Abandon(canonical, key);
            }
        }
    }

    private RuntimeInitialCreateExecutionStatus Abandon(
        RuntimeEntityRecord canonical,
        RuntimeEntityKey key)
    {
        if (_residences.Forget(
                canonical,
                out _,
                out RuntimePlacementCancellationReceipt cancellation))
        {
            _physics.SetPosition.PublishCancellation(cancellation);
        }
        DiscardProgress(key);
        return RuntimeInitialCreateExecutionStatus.RejectedAuthority;
    }

    private RuntimeInitialCreateExecutionStatus RunInitialTail(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        in RuntimeInitialCreateResidenceReceipt residenceReceipt,
        Progress progress)
    {
        if (progress.TailPhase == InitialTailPhase.NotStarted)
        {
            if (!_residences.AdoptCompletedPlacement(canonical, token))
                return RuntimeInitialCreateExecutionStatus.RejectedAuthority;
            progress.Trace.Add(new RuntimeInitialCreateExecutedAction(
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                0UL,
                -1,
                null,
                RuntimeTeleportHookPhase.None));
            progress.TailPhase = InitialTailPhase.Adopted;
        }

        if (progress.TailPhase == InitialTailPhase.Adopted)
        {
            if (residenceReceipt.TeleportHookPhase
                == RuntimeTeleportHookPhase.AfterEnterWorld)
            {
                progress.Trace.Add(new RuntimeInitialCreateExecutedAction(
                    RuntimeInitialCreateExecutedActionKind.TeleportHookRequest,
                    0UL,
                    -1,
                    null,
                    RuntimeTeleportHookPhase.AfterEnterWorld));
            }
            progress.TailPhase = InitialTailPhase.HookRecorded;
        }

        if (progress.TailPhase == InitialTailPhase.HookRecorded)
        {
            if (!ReplayDeferredChildren(canonical, progress))
                return RuntimeInitialCreateExecutionStatus.RejectedAuthority;
            progress.TailPhase = InitialTailPhase.DeferredReplayed;
        }

        if (progress.TailPhase == InitialTailPhase.DeferredReplayed)
        {
            if (!ReplayDeferredAcceptedRelations(canonical, progress))
                return RuntimeInitialCreateExecutionStatus.RejectedAuthority;
            progress.TailPhase = InitialTailPhase.RelationsReplayed;
        }

        return RuntimeInitialCreateExecutionStatus.Completed;
    }

    private bool ReplayDeferredChildren(RuntimeEntityRecord canonical, Progress progress)
    {
        if (!_entities.IsCurrent(canonical))
            return false;

        ImmutableArray<DeferredParentCreate> detached =
            _entities.ParentAttachments.DetachDeferredCreates(
                canonical.ServerGuid, out DeferredReplayWindowToken window);
        for (int index = 0; index < detached.Length; index++)
        {
            if (!_entities.IsCurrent(canonical))
            {
                _entities.ParentAttachments.RestoreDeferredCreates(
                    window,
                    detached.AsSpan()[index..]);
                return false;
            }

            DeferredParentCreate deferred = detached[index];
            RuntimeDeferredChildReplayOutcome outcome;
            try
            {
                RuntimeEntityRegistrationResult result =
                    _registerDeferredChild(deferred.Spawn, deferred.IsLocalPlayer);
                outcome = result.Canonical is not null
                    ? RuntimeDeferredChildReplayOutcome.Registered
                    : result.DeferredForParent
                        ? RuntimeDeferredChildReplayOutcome.ReDeferred
                        : RuntimeDeferredChildReplayOutcome.Rejected;
            }
            catch (Exception error)
            {
                RecordReplayFailure(error);
                outcome = RuntimeDeferredChildReplayOutcome.Rejected;
            }
            progress.Trace.Add(new RuntimeInitialCreateExecutedAction(
                RuntimeInitialCreateExecutedActionKind.DeferredChildReplay,
                0UL,
                -1,
                null,
                RuntimeTeleportHookPhase.None,
                outcome));
            progress.ReplayedDeferredChildCount++;
        }
        // Nothing left to restore on a full pass - releases the window.
        _entities.ParentAttachments.RestoreDeferredCreates(
            window, ReadOnlySpan<DeferredParentCreate>.Empty);
        return true;
    }

    private bool ReplayDeferredAcceptedRelations(RuntimeEntityRecord canonical, Progress progress)
    {
        if (!_entities.IsCurrent(canonical))
            return false;

        ImmutableArray<DeferredAcceptedParentRelation> detached =
            _entities.ParentAttachments.DetachDeferredAcceptedRelations(
                canonical.ServerGuid, out DeferredReplayWindowToken window);
        for (int index = 0; index < detached.Length; index++)
        {
            if (!_entities.IsCurrent(canonical))
            {
                _entities.ParentAttachments.RestoreDeferredAcceptedRelations(
                    window,
                    detached.AsSpan()[index..]);
                return false;
            }

            DeferredAcceptedParentRelation entry = detached[index];
            RuntimeParentRelationOutcome outcome;
            try
            {
                outcome = ApplyReplayedParentRelation(canonical, entry);
            }
            catch (Exception error)
            {
                RecordReplayFailure(error);
                outcome = RuntimeParentRelationOutcome.Rejected;
            }
            progress.Trace.Add(new RuntimeInitialCreateExecutedAction(
                RuntimeInitialCreateExecutedActionKind.ParentRelationReplay,
                0UL,
                -1,
                null,
                RuntimeTeleportHookPhase.None,
                null,
                null,
                RuntimePositionConstrainPhase.None,
                false,
                false,
                false,
                false,
                false,
                outcome));
            if (outcome == RuntimeParentRelationOutcome.DeferredAwaitingParent)
            {
                // Relation still names an incarnation that has not arrived
                // yet - wait for the NEXT one. Re-enqueues into a BRAND NEW
                // queue instance (the whole bucket was already detached
                // above), so this same detach loop never re-observes it.
                _entities.ParentAttachments.EnqueueDeferredAcceptedRelation(entry);
            }
        }
        _entities.ParentAttachments.RestoreDeferredAcceptedRelations(
            window, ReadOnlySpan<DeferredAcceptedParentRelation>.Empty);
        return true;
    }

    private RuntimeParentRelationOutcome ApplyReplayedParentRelation(
        RuntimeEntityRecord parent,
        in DeferredAcceptedParentRelation entry)
    {
        if (!_entities.TryGetActive(entry.ChildGuid, out RuntimeEntityRecord child)
            || child.Key != entry.ChildKey)
        {
            return RuntimeParentRelationOutcome.Rejected;
        }

        if (entry.ParentInstanceSequence is { } relationParentInstance
            && parent.Incarnation != relationParentInstance)
        {
            return PhysicsTimestampGate.IsNewer(relationParentInstance, parent.Incarnation)
                ? RuntimeParentRelationOutcome.DiscardedStaleParent
                : RuntimeParentRelationOutcome.DeferredAwaitingParent;
        }

        CommitParentAttachment(child, default, rebaseline: false, buffer: null);
        return RuntimeParentRelationOutcome.Applied;
    }

    private RuntimeInitialCreateExecutionStatus ApplyContinuation(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeEntityKey key,
        in RuntimeInitialCreateResidenceContinuation continuation,
        in RuntimeInitialCreateExecutionInputs inputs,
        Progress progress)
    {
        if (continuation.Kind
            == RuntimeInitialCreateContinuationKind.SameIncarnationCreate)
        {
            return ApplyEnvelope(canonical, token, key, continuation, inputs, progress);
        }

        if (progress.PendingContinuationPlacement.IsValid)
        {
            RuntimeInitialCreateExecutionStatus resumeStatus =
                ResumePendingPlacement(canonical, key, progress, out RuntimeAuthoritativePositionRoute route);
            if (resumeStatus != RuntimeInitialCreateExecutionStatus.Completed)
                return resumeStatus;
            progress.Trace.Add(BuildPositionTrace(continuation.Sequence, -1, route));
            return RuntimeInitialCreateExecutionStatus.Completed;
        }

        RuntimeInitialCreateTailAction action = continuation.Actions[0];
        switch (continuation.Kind)
        {
            case RuntimeInitialCreateContinuationKind.ObjDesc:
                if (!ApplyObjDescAction(canonical, token, action, null))
                    return Abandon(canonical, key);
                progress.Trace.Add(Simple(
                    RuntimeInitialCreateExecutedActionKind.ObjDesc,
                    continuation.Sequence));
                return RuntimeInitialCreateExecutionStatus.Completed;
            case RuntimeInitialCreateContinuationKind.Parent:
                return ApplyParentContinuation(canonical, token, key, continuation, action, progress);
            case RuntimeInitialCreateContinuationKind.Pickup:
                if (!ApplyPickupAction(canonical, token, action, null))
                    return Abandon(canonical, key);
                progress.Trace.Add(Simple(
                    RuntimeInitialCreateExecutedActionKind.Pickup,
                    continuation.Sequence));
                return RuntimeInitialCreateExecutionStatus.Completed;
            case RuntimeInitialCreateContinuationKind.Movement:
                if (!ApplyMovementAction(canonical, token, action, null))
                    return Abandon(canonical, key);
                progress.Trace.Add(Simple(
                    RuntimeInitialCreateExecutedActionKind.Movement,
                    continuation.Sequence));
                return RuntimeInitialCreateExecutionStatus.Completed;
            case RuntimeInitialCreateContinuationKind.State:
                if (!ApplyStateAction(canonical, token, action, null))
                    return Abandon(canonical, key);
                progress.Trace.Add(Simple(
                    RuntimeInitialCreateExecutedActionKind.State,
                    continuation.Sequence));
                return RuntimeInitialCreateExecutionStatus.Completed;
            case RuntimeInitialCreateContinuationKind.Vector:
                if (!ApplyVectorAction(canonical, token, action, null))
                    return Abandon(canonical, key);
                progress.Trace.Add(Simple(
                    RuntimeInitialCreateExecutedActionKind.Vector,
                    continuation.Sequence));
                return RuntimeInitialCreateExecutionStatus.Completed;
            case RuntimeInitialCreateContinuationKind.Position:
                return ApplyPositionAction(
                    canonical,
                    token,
                    key,
                    continuation.Sequence,
                    -1,
                    action,
                    inputs,
                    progress,
                    null);
            default:
                throw new InvalidOperationException(
                    $"Unsupported initial-Create continuation kind {continuation.Kind}.");
        }
    }

    private RuntimeInitialCreateExecutionStatus ApplyParentContinuation(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeEntityKey key,
        in RuntimeInitialCreateResidenceContinuation continuation,
        RuntimeInitialCreateTailAction action,
        Progress progress)
    {
        ParentEvent.Parsed parentUpdate = action.Parent!.Value;
        if (!ApplyParentPositionTimestampOnly(canonical, parentUpdate))
            return Abandon(canonical, key);

        RuntimeParentRelationOutcome outcome;
        if (!_entities.TryGetActive(parentUpdate.ParentGuid, out RuntimeEntityRecord parent))
        {
            _entities.ParentAttachments.EnqueueDeferredAcceptedRelation(
                canonical.ServerGuid, key, parentUpdate, null, action.AcceptedTimestamps);
            outcome = RuntimeParentRelationOutcome.DeferredAwaitingParent;
        }
        else if (parent.Incarnation != parentUpdate.ParentInstanceSequence)
        {
            if (PhysicsTimestampGate.IsNewer(parentUpdate.ParentInstanceSequence, parent.Incarnation))
            {
                // Live parent is NEWER than the relation's named incarnation
                // - stale, discard (Resolve's own discard branch).
                outcome = RuntimeParentRelationOutcome.DiscardedStaleParent;
            }
            else
            {
                // Relation names a FUTURE incarnation - wait for it.
                _entities.ParentAttachments.EnqueueDeferredAcceptedRelation(
                    canonical.ServerGuid, key, parentUpdate, null, action.AcceptedTimestamps);
                outcome = RuntimeParentRelationOutcome.DeferredAwaitingParent;
            }
        }
        else
        {
            CommitParentAttachment(canonical, token, rebaseline: true, null);
            outcome = RuntimeParentRelationOutcome.Applied;
        }

        progress.Trace.Add(Simple(
            RuntimeInitialCreateExecutedActionKind.Parent,
            continuation.Sequence,
            parentRelationOutcome: outcome));
        return RuntimeInitialCreateExecutionStatus.Completed;
    }

    private bool ApplyParentPositionTimestampOnly(
        RuntimeEntityRecord canonical,
        ParentEvent.Parsed update)
    {
        if (!_entities.ApplyAcceptedParentSnapshot(
                canonical.ServerGuid,
                update,
                out WorldSession.EntitySpawn stamped))
        {
            return false;
        }
        _entities.RefreshSnapshot(canonical, stamped);
        return true;
    }

    private (bool Success, RuntimeParentRelationOutcome Outcome) ApplyCreateParentContinuation(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeEntityKey key,
        RuntimeInitialCreateTailAction action,
        List<PendingPublish>? buffer)
    {
        CreateParentUpdate createParentUpdate = action.CreateParent!.Value;
        if (!ApplyCreateParentPositionTimestampOnly(canonical, createParentUpdate))
            return (false, default);

        if (!_entities.TryGetActive(createParentUpdate.ParentGuid, out _))
        {
            _entities.ParentAttachments.EnqueueDeferredAcceptedRelation(
                canonical.ServerGuid, key, null, createParentUpdate, action.AcceptedTimestamps);
            return (true, RuntimeParentRelationOutcome.DeferredAwaitingParent);
        }
        CommitParentAttachment(canonical, token, rebaseline: true, buffer);
        return (true, RuntimeParentRelationOutcome.Applied);
    }

    private bool ApplyCreateParentPositionTimestampOnly(
        RuntimeEntityRecord canonical,
        CreateParentUpdate update)
    {
        if (!_entities.ApplyAcceptedCreateParentSnapshot(
                canonical.ServerGuid,
                update,
                out WorldSession.EntitySpawn stamped))
        {
            return false;
        }
        _entities.RefreshSnapshot(canonical, stamped);
        return true;
    }

    private RuntimeInitialCreateExecutionStatus ApplyEnvelope(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeEntityKey key,
        in RuntimeInitialCreateResidenceContinuation continuation,
        in RuntimeInitialCreateExecutionInputs inputs,
        Progress progress)
    {
        int startStage = progress.EnvelopeStageIndex < 0 ? 0 : progress.EnvelopeStageIndex;

        if (progress.PendingContinuationPlacement.IsValid)
        {
            RuntimeInitialCreateExecutionStatus resumeStatus =
                ResumePendingPlacement(canonical, key, progress, out RuntimeAuthoritativePositionRoute route);
            if (resumeStatus != RuntimeInitialCreateExecutionStatus.Completed)
                return resumeStatus;
            progress.Trace.Add(BuildPositionTrace(continuation.Sequence, startStage, route));
            startStage++;
            progress.EnvelopeStageIndex = startStage;
        }

        for (int i = startStage; i < continuation.Actions.Length; i++)
        {
            if (!_entities.IsCurrent(canonical))
                return Abandon(canonical, key);

            RuntimeInitialCreateTailAction action = continuation.Actions[i];
            switch (action.Kind)
            {
                case RuntimeInitialCreateTailActionKind.PreTailDescriptionAdaptation:
                    progress.Trace.Add(Simple(
                        RuntimeInitialCreateExecutedActionKind.PreTailDescriptionAdaptation,
                        continuation.Sequence,
                        i));
                    break;
                case RuntimeInitialCreateTailActionKind.ObjDesc:
                    if (!ApplyObjDescAction(canonical, token, action, progress.EnvelopeBuffer))
                        return Abandon(canonical, key);
                    progress.Trace.Add(Simple(
                        RuntimeInitialCreateExecutedActionKind.ObjDesc,
                        continuation.Sequence,
                        i));
                    break;
                case RuntimeInitialCreateTailActionKind.CreateParent:
                {
                    (bool success, RuntimeParentRelationOutcome outcome) =
                        ApplyCreateParentContinuation(canonical, token, key, action, progress.EnvelopeBuffer);
                    if (!success)
                        return Abandon(canonical, key);
                    progress.Trace.Add(Simple(
                        RuntimeInitialCreateExecutedActionKind.CreateParent,
                        continuation.Sequence,
                        i,
                        parentRelationOutcome: outcome));
                    break;
                }
                case RuntimeInitialCreateTailActionKind.Pickup:
                    if (!ApplyPickupAction(canonical, token, action, progress.EnvelopeBuffer))
                        return Abandon(canonical, key);
                    progress.Trace.Add(Simple(
                        RuntimeInitialCreateExecutedActionKind.Pickup,
                        continuation.Sequence,
                        i));
                    break;
                case RuntimeInitialCreateTailActionKind.Position:
                {
                    progress.EnvelopeStageIndex = i;
                    RuntimeInitialCreateExecutionStatus status = ApplyPositionAction(
                        canonical,
                        token,
                        key,
                        continuation.Sequence,
                        i,
                        action,
                        inputs,
                        progress,
                        progress.EnvelopeBuffer);
                    if (status
                        == RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement)
                    {
                        return status;
                    }
                    if (status != RuntimeInitialCreateExecutionStatus.Completed)
                        return status;
                    break;
                }
                case RuntimeInitialCreateTailActionKind.Movement:
                    if (!ApplyMovementAction(canonical, token, action, progress.EnvelopeBuffer))
                        return Abandon(canonical, key);
                    progress.Trace.Add(Simple(
                        RuntimeInitialCreateExecutedActionKind.Movement,
                        continuation.Sequence,
                        i));
                    break;
                case RuntimeInitialCreateTailActionKind.State:
                    if (!ApplyStateAction(canonical, token, action, progress.EnvelopeBuffer))
                        return Abandon(canonical, key);
                    progress.Trace.Add(Simple(
                        RuntimeInitialCreateExecutedActionKind.State,
                        continuation.Sequence,
                        i));
                    break;
                case RuntimeInitialCreateTailActionKind.Vector:
                    if (!ApplyVectorAction(canonical, token, action, progress.EnvelopeBuffer))
                        return Abandon(canonical, key);
                    progress.Trace.Add(Simple(
                        RuntimeInitialCreateExecutedActionKind.Vector,
                        continuation.Sequence,
                        i));
                    break;
                case RuntimeInitialCreateTailActionKind.WeenieDescription:
                    if (!ApplyWeenieDescriptionAction(canonical, token, action, progress.EnvelopeBuffer))
                        return Abandon(canonical, key);
                    progress.Trace.Add(Simple(
                        RuntimeInitialCreateExecutedActionKind.WeenieDescription,
                        continuation.Sequence,
                        i));
                    break;
                case RuntimeInitialCreateTailActionKind.ResidentCellCleanup:
                {
                    RuntimeResidentCellCleanupDisposition? cleanupDisposition =
                        ApplyResidentCellCleanup(canonical);
                    if (cleanupDisposition is null)
                    {
                        return Abandon(canonical, key);
                    }
                    progress.Trace.Add(new RuntimeInitialCreateExecutedAction(
                        RuntimeInitialCreateExecutedActionKind.ResidentCellCleanup,
                        continuation.Sequence,
                        i,
                        null,
                        RuntimeTeleportHookPhase.None,
                        null,
                        cleanupDisposition));
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Unsupported same-incarnation tail action {action.Kind}.");
            }

            progress.EnvelopeStageIndex = i + 1;
        }

        foreach (PendingPublish pending in progress.EnvelopeBuffer)
            PublishNow(canonical, pending.Change, pending.Matches, pending.Cancellation);
        progress.EnvelopeBuffer.Clear();
        progress.EnvelopeStageIndex = -1;
        return RuntimeInitialCreateExecutionStatus.Completed;
    }

    private RuntimeInitialCreateExecutionStatus ResumePendingPlacement(
        RuntimeEntityRecord canonical,
        RuntimeEntityKey key,
        Progress progress,
        out RuntimeAuthoritativePositionRoute route)
    {
        route = progress.PendingContinuationRoute;
        RuntimeEntityPlacementToken placementToken = progress.PendingContinuationPlacement;
        if (_physics.SetPosition.IsPlacementCurrent(placementToken))
            return RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement;

        if (!_physics.SetPosition.TryPeekAcknowledgedPlacement(
                placementToken,
                out RuntimePlacementProjectionToken projection)
            || projection.Entity != placementToken.Entity
            || projection.SessionLifetimeVersion != placementToken.SessionLifetimeVersion
            || projection.PositionAuthorityVersion != placementToken.PositionAuthorityVersion
            || canonical.PositionAuthorityVersion != placementToken.PositionAuthorityVersion
            || projection.ExactCellId == 0u
            || projection.ExactCellId != canonical.FullCellId
            || projection.PlacementCommitVersion != canonical.PlacementCommitVersion)
        {
            RuntimePlacementCancellationReceipt forgotten =
                _physics.SetPosition.ForgetExactPlacement(placementToken);
            _physics.SetPosition.PublishCancellation(forgotten);
            progress.PendingContinuationPlacement = default;
            return Abandon(canonical, key);
        }

        if (!_physics.SetPosition.ConsumeAcknowledgedPlacement(placementToken, projection))
        {
            RuntimePlacementCancellationReceipt forgotten =
                _physics.SetPosition.ForgetExactPlacement(placementToken);
            _physics.SetPosition.PublishCancellation(forgotten);
            progress.PendingContinuationPlacement = default;
            return Abandon(canonical, key);
        }

        progress.PendingContinuationPlacement = default;
        progress.PendingContinuationSequence = 0UL;
        return RuntimeInitialCreateExecutionStatus.Completed;
    }

    private RuntimeInitialCreateExecutionStatus ApplyPositionAction(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeEntityKey key,
        ulong sequence,
        int stage,
        RuntimeInitialCreateTailAction action,
        in RuntimeInitialCreateExecutionInputs inputs,
        Progress progress,
        List<PendingPublish>? buffer)
    {
        if (!_residences.TryGetTransaction(canonical, out RuntimeInitialCreateResidenceLease lease)
            || canonical.Key != key)
        {
            return Abandon(canonical, key);
        }

        WorldSession.EntityPositionUpdate update = action.Position!.Value;
        RuntimePositionEntityKind entityKind = EntityKindOf(lease.Route.OperationKind);
        bool isLocalPlayer = entityKind is RuntimePositionEntityKind.LocalPlayer;

        RuntimeAuthoritativePositionRoute route;
        if (progress.PositionMergeCommittedForRetry)
        {
            route = progress.PendingContinuationRoute;
        }
        else
        {
            RuntimeAcceptedPositionRouteRequest request =
                RuntimeAcceptedPositionRouteRequests.Build(
                    CurrentGeneration(),
                    canonical,
                    key,
                    update,
                    entityKind,
                    action.PositionSource,
                    action.PositionDisposition,
                    action.PreviousTeleportSequence,
                    action.AcceptedTimestamps.Teleport,
                    inputs.PlayerDistance,
                    inputs.UsePositionFromServer);

            route = RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(request);

            if (!route.Accepted)
            {
                bool stampedOk = action.PositionDisposition
                        is PositionTimestampDisposition.Rejected
                    ? _entities.ApplyAcceptedPositionSnapshot(
                        canonical.ServerGuid,
                        update,
                        PositionTimestampDisposition.Rejected,
                        action.AcceptedTimestamps,
                        isLocalPlayer,
                        null,
                        null,
                        installPlacementFrame: false,
                        clearParent: false,
                        out WorldSession.EntitySpawn stampedOnly)
                    : _entities.ApplyAcceptedPositionExecutionRejectedSnapshot(
                        canonical.ServerGuid,
                        update.PositionSequence,
                        action.AcceptedTimestamps,
                        out stampedOnly);
                if (!stampedOk)
                    return Abandon(canonical, key);
                _entities.RefreshSnapshot(canonical, stampedOnly);
                progress.Trace.Add(BuildPositionTrace(sequence, stage, route));
                return RuntimeInitialCreateExecutionStatus.Completed;
            }

            PhysicsBody? body = canonical.PhysicsBody;
            bool mergedOk = _entities.ApplyAcceptedPositionSnapshot(
                canonical.ServerGuid,
                update,
                action.PositionDisposition,
                action.AcceptedTimestamps,
                isLocalPlayer,
                body?.Orientation,
                body?.Velocity,
                installPlacementFrame: route.ApplyPlacementFrameBeforeRouting,
                clearParent: route.UnparentBeforeRouting,
                out WorldSession.EntitySpawn merged);
            if (!mergedOk)
                return Abandon(canonical, key);
            _entities.RefreshSnapshot(canonical, merged, refreshPosition: false);
            _entities.AdvancePositionAuthority(canonical);
            _entities.ParentAttachments.EndChildProjection(canonical.ServerGuid);
            _residences.AdvanceExecutorBaseline(
                canonical, token, RuntimeExecutorBaselineFields.PositionAuthorityVersion);
            ulong positionVersion = canonical.PositionAuthorityVersion;
            ulong spatialVersion = canonical.SpatialAuthorityVersion;
            Publish(
                canonical,
                RuntimeEntityChange.Updated,
                () => canonical.PositionAuthorityVersion == positionVersion
                    && canonical.SpatialAuthorityVersion == spatialVersion,
                default,
                buffer);

            if (!route.PerformsSetPosition)
            {
                progress.Trace.Add(BuildPositionTrace(sequence, stage, route));
                return RuntimeInitialCreateExecutionStatus.Completed;
            }

            progress.PositionMergeCommittedForRetry = true;
            progress.PendingContinuationRoute = route;
            progress.PositionMergeCommittedVersion = canonical.PositionAuthorityVersion;
        }

        RuntimeEntityPlacementToken placement = _physics.SetPosition
            .TryBeginExclusiveAuthoredPlacement(
                canonical,
                canonical.PositionAuthorityVersion,
                route.OperationKind);
        if (!placement.IsValid)
        {
            if (!_entities.IsCurrent(canonical)
                || canonical.PositionAuthorityVersion != progress.PositionMergeCommittedVersion)
            {
                progress.PositionMergeCommittedForRetry = false;
                return Abandon(canonical, key);
            }
            return RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement;
        }
        if (!_physics.SetPosition.WatchPlacementCompletion(placement))
        {
            _ = _physics.SetPosition.ForgetExactPlacement(placement);
            progress.PositionMergeCommittedForRetry = false;
            return Abandon(canonical, key);
        }

        progress.PositionMergeCommittedForRetry = false;
        progress.PendingContinuationPlacement = placement;
        progress.PendingContinuationSequence = sequence;
        progress.PendingContinuationRoute = route;
        return RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement;
    }

    private bool ApplyObjDescAction(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeInitialCreateTailAction action,
        List<PendingPublish>? buffer)
    {
        if (!_entities.ApplyAcceptedObjDescSnapshot(
                canonical.ServerGuid,
                action.ObjDesc!.Value,
                out WorldSession.EntitySpawn merged))
        {
            return false;
        }
        _entities.RefreshSnapshot(canonical, merged);
        _entities.AdvanceObjDescAuthority(canonical);
        ulong version = canonical.ObjDescAuthorityVersion;
        Publish(
            canonical,
            RuntimeEntityChange.Updated,
            () => canonical.ObjDescAuthorityVersion == version,
            default,
            buffer);
        return true;
    }

    private void CommitParentAttachment(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        bool rebaseline,
        List<PendingPublish>? buffer)
    {
        _entities.AdvancePositionAuthority(canonical);
        _physics.CollisionReports.LeaveWorld(canonical);
        RuntimePlacementCancellationReceipt cancellation =
            _physics.SetPosition.Forget(canonical);
        if (rebaseline)
        {
            _residences.AdvanceExecutorBaseline(
                canonical, token, RuntimeExecutorBaselineFields.PositionAuthorityVersion);
        }
        ulong positionVersion = canonical.PositionAuthorityVersion;
        ulong spatialVersion = canonical.SpatialAuthorityVersion;
        Publish(
            canonical,
            RuntimeEntityChange.Updated,
            () => canonical.PositionAuthorityVersion == positionVersion
                && canonical.SpatialAuthorityVersion == spatialVersion,
            cancellation,
            buffer);
    }

    private bool ApplyPickupAction(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeInitialCreateTailAction action,
        List<PendingPublish>? buffer)
    {
        if (!_entities.ApplyAcceptedPickupSnapshot(
                canonical.ServerGuid,
                action.Pickup!.Value,
                out WorldSession.EntitySpawn merged))
        {
            return false;
        }
        _entities.RefreshSnapshot(canonical, merged);
        _entities.AdvancePositionAuthority(canonical);
        _entities.ParentAttachments.EndChildProjection(canonical.ServerGuid);
        _physics.CollisionReports.LeaveWorld(canonical);
        RuntimePlacementCancellationReceipt cancellation =
            _physics.SetPosition.Forget(canonical);
        _entities.SuspendObjectClock(canonical);
        _entities.SetFullCell(canonical, 0u, 0u);
        _residences.AdvanceExecutorBaseline(
            canonical,
            token,
            RuntimeExecutorBaselineFields.PositionAuthorityVersion
                | RuntimeExecutorBaselineFields.FullCellId);
        ulong positionVersion = canonical.PositionAuthorityVersion;
        ulong spatialVersion = canonical.SpatialAuthorityVersion;
        Publish(
            canonical,
            RuntimeEntityChange.Withdrawn,
            () => canonical.PositionAuthorityVersion == positionVersion
                && canonical.SpatialAuthorityVersion == spatialVersion,
            cancellation,
            buffer);
        return true;
    }

    private bool ApplyMovementAction(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeInitialCreateTailAction action,
        List<PendingPublish>? buffer)
    {
        WorldSession.EntityMotionUpdate update = action.Movement!.Value;
        if (!_entities.ApplyAcceptedMotionSnapshot(
                canonical.ServerGuid,
                update.MovementSequence,
                action.AcceptedTimestamps.ServerControlledMove,
                update,
                retainPayload: false,
                out WorldSession.EntitySpawn stamped))
        {
            return false;
        }
        _entities.RefreshSnapshot(canonical, stamped);
        if (!action.AppliesMovementPayload)
        {
            return true;
        }

        if (action.RetainMovementPayload)
        {
            if (!_entities.ApplyAcceptedMotionSnapshot(
                    canonical.ServerGuid,
                    update.MovementSequence,
                    action.AcceptedTimestamps.ServerControlledMove,
                    update,
                    retainPayload: true,
                    out WorldSession.EntitySpawn merged))
            {
                return false;
            }
            _entities.RefreshSnapshot(canonical, merged);
            _entities.AdvanceMovementAuthority(canonical);
        }
        _entities.AdvanceMovementCommit(canonical);
        ulong movementCommitVersion = canonical.MovementCommitVersion;
        Publish(
            canonical,
            RuntimeEntityChange.Updated,
            () => canonical.MovementCommitVersion == movementCommitVersion,
            default,
            buffer);
        return true;
    }

    private bool ApplyStateAction(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeInitialCreateTailAction action,
        List<PendingPublish>? buffer)
    {
        SetState.Parsed update = action.State!.Value;
        if (!_entities.ApplyAcceptedStateSnapshot(
                canonical.ServerGuid,
                update,
                out WorldSession.EntitySpawn merged))
        {
            return false;
        }
        _entities.RefreshSnapshot(canonical, merged);
        RetailPhysicsStateTransition preview = RetailPhysicsStateTransitions.Apply(
            canonical.FinalPhysicsState,
            (PhysicsStateFlags)update.PhysicsState);
        ulong priorPhysicsMutation = canonical.PhysicsStateMutationVersion;
        if (preview.HiddenTransition is RetailHiddenTransition.BecameHidden)
        {
            _physics.CollisionReports.LeaveWorld(canonical);
            if (!_entities.IsCurrent(canonical)
                || canonical.PhysicsStateMutationVersion != priorPhysicsMutation)
            {
                return false;
            }
        }
        RetailPhysicsStateTransition transition =
            _entities.ApplyRawPhysicsState(canonical, update.PhysicsState);
        if (canonical.Key is { } key)
        {
            _physics.Engine.ShadowObjects.UpdatePhysicsState(
                key.LocalEntityId,
                (uint)canonical.FinalPhysicsState);
        }
        ulong stateVersion = canonical.StateAuthorityVersion;
        ulong physicsMutationVersion = canonical.PhysicsStateMutationVersion;
        Publish(
            canonical,
            transition.HiddenTransition is RetailHiddenTransition.BecameHidden
                ? RuntimeEntityChange.Hidden
                : RuntimeEntityChange.Updated,
            () => canonical.StateAuthorityVersion == stateVersion
                && canonical.PhysicsStateMutationVersion == physicsMutationVersion,
            default,
            buffer);
        return true;
    }

    private bool ApplyVectorAction(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeInitialCreateTailAction action,
        List<PendingPublish>? buffer)
    {
        if (!_entities.ApplyAcceptedVectorSnapshot(
                canonical.ServerGuid,
                action.Vector!.Value,
                out WorldSession.EntitySpawn merged))
        {
            return false;
        }
        _entities.RefreshSnapshot(canonical, merged);
        _entities.AdvanceVectorAuthority(canonical);
        ulong version = canonical.VectorAuthorityVersion;
        Publish(
            canonical,
            RuntimeEntityChange.Updated,
            () => canonical.VectorAuthorityVersion == version,
            default,
            buffer);
        return true;
    }

    private bool ApplyWeenieDescriptionAction(
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeInitialCreateTailAction action,
        List<PendingPublish>? buffer)
    {
        if (!_entities.ApplyAcceptedWeenieDescriptionSnapshot(
                canonical.ServerGuid,
                action.WeenieDescription!.Value,
                out WorldSession.EntitySpawn merged))
        {
            return false;
        }
        _entities.RefreshSnapshot(canonical, merged, refreshPosition: false);
        _entities.AdvanceCreateAuthority(canonical);
        ulong createVersion = canonical.CreateIntegrationVersion;
        _residences.AdvanceExecutorBaseline(
            canonical,
            token,
            RuntimeExecutorBaselineFields.PositionAuthorityVersion
                | RuntimeExecutorBaselineFields.CreateIntegrationVersion);
        if (!_applyAcceptedSpawn(canonical, createVersion, merged, /* replaceGeneration: */ false))
            return false;
        Publish(
            canonical,
            RuntimeEntityChange.Updated,
            () => canonical.CreateIntegrationVersion == createVersion,
            default,
            buffer);
        return true;
    }

    private RuntimeResidentCellCleanupDisposition? ApplyResidentCellCleanup(
        RuntimeEntityRecord canonical)
    {
        uint claimedCell = canonical.Snapshot.Physics?.Position?.LandblockId
            ?? canonical.Snapshot.Position?.LandblockId
            ?? 0u;
        if (claimedCell == 0u)
        {
            return RuntimeResidentCellCleanupDisposition
                .CelllessNoWeenieMarkUnreachable;
        }
        if (canonical.FullCellId != 0u)
            return RuntimeResidentCellCleanupDisposition.ResidentUnmarked;
        if (!_physics.SetPosition.IsDeferred(canonical))
        {
            return null;
        }
        return RuntimeResidentCellCleanupDisposition.DeferredUnderLostCellOwnership;
    }

    private void Publish(
        RuntimeEntityRecord canonical,
        RuntimeEntityChange change,
        Func<bool> matches,
        RuntimePlacementCancellationReceipt cancellation,
        List<PendingPublish>? buffer)
    {
        if (buffer is not null)
        {
            buffer.Add(new PendingPublish(change, static () => true, cancellation));
            return;
        }
        PublishNow(canonical, change, matches, cancellation);
    }

    private void PublishNow(
        RuntimeEntityRecord canonical,
        RuntimeEntityChange change,
        Func<bool> matches,
        RuntimePlacementCancellationReceipt cancellation)
    {
        _physics.SetPosition.PublishCancellation(cancellation);
        if (_entities.IsCurrent(canonical) && matches())
            _events.PublishEntity(change, canonical);
    }

    private static RuntimeInitialCreateExecutedAction BuildPositionTrace(
        ulong sequence,
        int stage,
        in RuntimeAuthoritativePositionRoute route) => new(
        RuntimeInitialCreateExecutedActionKind.Position,
        sequence,
        stage,
        route.Disposition,
        route.TeleportHookPhase,
        null,
        null,
        route.ConstrainPhase,
        route.StopInterpolating,
        route.ZeroVelocity,
        route.PreserveHeading,
        route.SendPositionImmediately,
        route.UnparentBeforeRouting);

    private static RuntimeInitialCreateExecutedAction Simple(
        RuntimeInitialCreateExecutedActionKind kind,
        ulong sequence,
        int stage = -1,
        RuntimeParentRelationOutcome? parentRelationOutcome = null) => new(
        kind,
        sequence,
        stage,
        null,
        RuntimeTeleportHookPhase.None,
        ParentRelationOutcome: parentRelationOutcome);

    private static RuntimePositionEntityKind EntityKindOf(
        RuntimeSetPositionOperationKind operationKind) => operationKind switch
    {
        RuntimeSetPositionOperationKind.InitialLogin
            or RuntimeSetPositionOperationKind.LocalAuthoritative =>
            RuntimePositionEntityKind.LocalPlayer,
        RuntimeSetPositionOperationKind.ProjectileAuthoritative =>
            RuntimePositionEntityKind.Projectile,
        _ => RuntimePositionEntityKind.Remote,
    };

    private RuntimeGenerationToken CurrentGeneration() => _generation?.Invoke() ?? default;
}
