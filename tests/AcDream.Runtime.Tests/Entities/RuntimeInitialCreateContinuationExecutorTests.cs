using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeInitialCreateContinuationExecutorTests
{
    private const uint Landblock = 0xA9B50000u;
    private const uint Cell = Landblock | 0x0001u;
    private static readonly RuntimeInitialCreateExecutionInputs NoContact =
        new(UsePositionFromServer: false, PlayerDistance: 0f);

    // ---------------------------------------------------------------
    // A. Basic
    // ---------------------------------------------------------------

    [Fact]
    public void CompletedTopLevelCreateAdoptsOnceEmitsHookAndConvergesEveryLedger()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(0x70020001u, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Equal(RuntimeTeleportHookPhase.AfterEnterWorld, receipt.TeleportHookPhase);
        Assert.Equal(0, receipt.ReplayedDeferredChildCount);
        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.TeleportHookRequest,
            ],
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(
            0,
            lifetime.Physics.SetPosition.CaptureOwnership().AcknowledgedPlacementCompletionCount);
        Assert.False(lifetime.TryGetInitialCreateResidence(canonical, out _));

        // Retrying with the now-stale token is a distinct, safe no-op.
        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedToken,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out _));
    }

    [Fact]
    public void MixedSimpleContinuationsDrainInExactSequenceOrderAndMutateSnapshot()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 2UL);
        const uint parentGuid = 0x70021100u;
        const uint guid = 0x70021000u;
        _ = lifetime.RegisterEntity(Spawn(parentGuid, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.False(lease.Placement.IsValid); // parented -> AwaitFreshPosition, no placement

        var appearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x04000002u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(appearance, null, out _));
        var vector = new VectorUpdate.Parsed(
            guid, new Vector3(1f, 2f, 3f), Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));
        var state = new SetState.Parsed(
            guid,
            (uint)PhysicsStateFlags.Gravity,
            InstanceSequence: 1,
            StateSequence: 2);
        Assert.True(lifetime.TryApplyState(state, null, out _, out _));

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.ObjDesc,
                RuntimeInitialCreateExecutedActionKind.Vector,
                RuntimeInitialCreateExecutedActionKind.State,
            ],
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal([1UL, 2UL, 3UL],
            receipt.Trace
                .Where(static a => a.Sequence != 0UL)
                .Select(static a => a.Sequence));
        Assert.Equal(
            [RuntimeEntityChange.Updated, RuntimeEntityChange.Updated, RuntimeEntityChange.Updated],
            observed);
        Assert.Equal(0x04000002u, canonical.Snapshot.BasePaletteId);
        Assert.Equal(new Vector3(1f, 2f, 3f), canonical.Snapshot.Physics!.Value.Velocity);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void FieldMaskedBaselinePrecisionDetectsAnExternalPositionRaceDuringAnUnrelatedObjDescPublish()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 96UL);
        const uint parentGuid = 0x70038000u;
        const uint guid = 0x70038001u;
        _ = lifetime.RegisterEntity(Spawn(parentGuid, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var appearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x07000002u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(appearance, null, out _));
        var vector = new VectorUpdate.Parsed(
            guid, new Vector3(1f, 2f, 3f), Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        bool bumped = false;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (bumped || delta.Change is not RuntimeEntityChange.Updated)
                return;
            bumped = true;
            // External, non-executor mutation of PositionAuthorityVersion
            // during the ObjDesc stage's OWN publish.
            lifetime.Entities.AdvancePositionAuthority(canonical);
        }));

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.True(bumped);
        Assert.Equal(default, receipt);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void DrainedAppearanceAndPoseSurviveTheNextLegacyWireApply()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 30UL);
        const uint parentGuid = 0x7002D100u;
        const uint guid = 0x7002D000u;
        _ = lifetime.RegisterEntity(Spawn(parentGuid, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var appearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x06000002u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(appearance, null, out _));
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);
        Assert.Contains(
            RuntimeInitialCreateExecutedActionKind.ObjDesc,
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(0x06000002u, canonical.Snapshot.BasePaletteId);

        var vector = new VectorUpdate.Parsed(
            guid, new Vector3(4f, 5f, 6f), Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        Assert.Equal(0x06000002u, canonical.Snapshot.BasePaletteId);
        Assert.Equal(new Vector3(4f, 5f, 6f), canonical.Snapshot.Physics!.Value.Velocity);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void WeenieDescriptionStageWiresTheObjectTableExactlyOnce()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 34UL);
        const uint guid = 0x7002E400u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        int objectCountBeforeDrain = lifetime.Objects.ObjectCount;

        WorldSession.EntitySpawn sameCreate = Spawn(
            guid, 1, includePosition: false, positionSequence: 2);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { State = 2, Vector = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Contains(
            RuntimeInitialCreateExecutedActionKind.WeenieDescription,
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(objectCountBeforeDrain + 1, lifetime.Objects.ObjectCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void ObjectTableSubscriberReenteringAWireApplyDuringIngestDoesNotRetireTheResidenceAndTheEnvelopeCompletes()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 94UL);
        const uint guid = 0x70037000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        WorldSession.EntitySpawn sameCreate = Spawn(
            guid, 1, includePosition: false, positionSequence: 2);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { State = 2, Vector = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);

        bool reentered = false;
        lifetime.Objects.ObjectAdded += addedObject =>
        {
            if (reentered)
                return;
            reentered = true;
            var vector = new VectorUpdate.Parsed(
                guid, new Vector3(9f, 8f, 7f), Vector3.Zero, InstanceSequence: 1, VectorSequence: 3);
            Assert.True(lifetime.TryApplyVector(vector, null, out _));
        };

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.True(reentered);
        Assert.Contains(
            RuntimeInitialCreateExecutedActionKind.WeenieDescription,
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void NestedReplacementDuringObjectTableIngestAbandonsTheWeenieDescriptionStageWithNoFurtherStages()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 95UL);
        const uint guid = 0x70037100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        WorldSession.EntitySpawn sameCreate = Spawn(
            guid, 1, includePosition: false, positionSequence: 2);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { State = 2, Vector = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);

        bool reentered = false;
        lifetime.Objects.ObjectAdded += addedObject =>
        {
            if (reentered)
                return;
            reentered = true;
            _ = lifetime.RegisterEntity(Spawn(guid, 2, includePosition: false));
        };

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.True(reentered);
        Assert.Equal(default, receipt);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void SameIncarnationWeenieDescriptionMergePreservesRetainedMotionTableId()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 31UL);
        const uint parentGuid = 0x7002D300u;
        const uint guid = 0x7002D200u;
        _ = lifetime.RegisterEntity(Spawn(parentGuid, 1));

        WorldSession.EntitySpawn initial = Spawn(guid, 1, includePosition: false, parentGuid: parentGuid);
        PhysicsSpawnData initialPhysics = initial.Physics!.Value;
        initial = initial with
        {
            MotionTableId = 0x12345678u,
            Physics = initialPhysics with { MotionTableId = 0x12345678u },
        };
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(initial, isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var newAppearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x07000002u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(newAppearance, null, out _));

        WorldSession.EntitySpawn sameCreate = Spawn(
            guid, 1, includePosition: false, parentGuid: parentGuid, positionSequence: 2);
        PhysicsSpawnData samePhysics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            MotionTableId = 0x99999999u,
            Physics = samePhysics with
            {
                MotionTableId = 0x99999999u,
                Timestamps = samePhysics.Timestamps with { ObjDesc = 3, State = 2, Vector = 2 },
            },
        };
        RuntimeEntityRegistrationResult same = lifetime
            .RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);
        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration, same.Inbound.Disposition);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Contains(
            RuntimeInitialCreateExecutedActionKind.WeenieDescription,
            receipt.Trace.Select(static a => a.Kind));
        Assert.Null(canonical.Snapshot.BasePaletteId);
        // MotionTableId has no dedicated envelope stage - WeenieDescription's
        // merge must keep the RETAINED value, never adopt incoming's raw
        // packet wholesale.
        Assert.Equal(0x12345678u, canonical.Snapshot.Physics!.Value.MotionTableId);
        Assert.Equal(0x12345678u, canonical.Snapshot.MotionTableId);
        // ObjDesc's timestamp DOES have a dedicated stage that legitimately
        // advances it further as part of entry 2's own admission (unlike
        // MotionTableId).
        Assert.Equal(3, canonical.Snapshot.Physics!.Value.Timestamps.ObjDesc);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }


    [Fact]
    public void StandaloneMovementContinuationAppliesPayloadAndPublishesUpdated()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 40UL);
        const uint guid = 0x7002F000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var motion = new WorldSession.EntityMotionUpdate(
            guid,
            new CreateObject.ServerMotionState(0x3d, 0x11),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 1,
            IsAutonomous: false);
        // Movement (2) advances and ServerControl (1) is equal-not-stale, so
        // the gate accepts the payload outright.
        Assert.True(lifetime.TryApplyMotion(motion, retainPayload: true, null, out _, out _));

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.Movement,
            ],
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(new CreateObject.ServerMotionState(0x3d, 0x11),
            canonical.Snapshot.MotionState);
        Assert.Equal(2, canonical.Snapshot.Physics!.Value.Timestamps.Movement);
        Assert.Equal([RuntimeEntityChange.Updated], observed);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void StandaloneMovementContinuationTimestampOnlyStampsWithoutPayloadOrPublish()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 41UL);
        const uint guid = 0x7002F100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        Assert.Null(canonical.Snapshot.MotionState);

        var motion = new WorldSession.EntityMotionUpdate(
            guid,
            new CreateObject.ServerMotionState(0x3d, 0x11),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 0,
            IsAutonomous: false);
        Assert.False(lifetime.TryApplyMotion(motion, retainPayload: true, null, out _, out _));
        Assert.True(lifetime.InitialCreateResidences.TryGetTransaction(
            canonical, out RuntimeInitialCreateResidenceLease retained));
        Assert.Single(retained.Continuations);

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Contains(
            RuntimeInitialCreateExecutedActionKind.Movement,
            receipt.Trace.Select(static a => a.Kind));
        // Timestamp landed; no payload, no publish (matches the legacy
        // timestamp-only branch).
        Assert.Equal(2, canonical.Snapshot.Physics!.Value.Timestamps.Movement);
        Assert.Null(canonical.Snapshot.MotionState);
        Assert.Empty(observed);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void StandalonePickupContinuationLeavesWorldThroughTheDrainAndResidenceStillReleases()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 42UL);
        const uint guid = 0x7002F200u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        Assert.Equal(Cell, canonical.FullCellId);

        var relation = new ParentAttachmentRelation(
            0x7002F300u, guid, ParentLocation: 1u, PlacementId: 0u,
            ParentInstanceSequence: 1, ChildPositionSequence: 1);
        lifetime.Entities.ParentAttachments.AcceptCreateObjectRelation(relation);
        Assert.True(lifetime.Entities.ParentAttachments.CommitProjection(relation));
        Assert.True(lifetime.Entities.ParentAttachments.HasCommittedParent(guid));

        Assert.True(lifetime.TryApplyPickup(
            new PickupEvent.Parsed(guid, InstanceSequence: 1, PositionSequence: 2),
            null,
            out _));

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));
        ulong clockEpochBefore = canonical.ObjectClockEpoch;

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.Pickup,
            ],
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal([RuntimeEntityChange.Withdrawn], observed);
        Assert.Equal(0u, canonical.FullCellId);
        Assert.NotEqual(clockEpochBefore, canonical.ObjectClockEpoch);
        Assert.False(lifetime.Entities.ParentAttachments.HasCommittedParent(guid));
        Assert.False(lifetime.Entities.ParentAttachments.TryGetRecoveryProjection(guid, out _));
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void StandaloneParentContinuationAppliesPositionTimestampOnlyMergeWithNoReDefer()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 43UL);
        const uint parentGuid = 0x7002F400u;
        const uint childGuid = 0x7002F500u;
        _ = lifetime.RegisterEntity(Spawn(parentGuid, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var parentUpdate = new ParentEvent.Parsed(
            parentGuid,
            childGuid,
            ParentLocation: 1u,
            PlacementId: 0u,
            ParentInstanceSequence: 1,
            ChildPositionSequence: 2);
        Assert.True(lifetime.TryApplyParent(parentUpdate, null, out _));

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.Parent,
            ],
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(2, canonical.Snapshot.Physics!.Value.Timestamps.Position);
        Assert.Null(canonical.Snapshot.ParentGuid);
        Assert.Equal([RuntimeEntityChange.Updated], observed);
        Assert.Equal(0, lifetime.Entities.ParentAttachments.UnresolvedRelationCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void EnqueueDuringDrainExercisesConsumeExecutedRevisedArmAndDrainsTailOnlyOnce()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 44UL);
        const uint guid = 0x7002F600u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var appearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x08000002u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(appearance, null, out _));

        var observed = new List<RuntimeEntityChange>();
        bool enqueuedFromObserver = false;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            observed.Add(delta.Change);
            if (enqueuedFromObserver)
                return;
            enqueuedFromObserver = true;
            var vector = new VectorUpdate.Parsed(
                guid, new Vector3(7f, 8f, 9f), Vector3.Zero,
                InstanceSequence: 1, VectorSequence: 2);
            Assert.True(lifetime.TryApplyVector(vector, null, out _));
        }));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        // Each action appears exactly once - the Revised loop-around must
        // not replay the already-applied ObjDesc prefix.
        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.ObjDesc,
                RuntimeInitialCreateExecutedActionKind.Vector,
            ],
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(
            [RuntimeEntityChange.Updated, RuntimeEntityChange.Updated],
            observed);
        Assert.Equal(0x08000002u, canonical.Snapshot.BasePaletteId);
        Assert.Equal(new Vector3(7f, 8f, 9f), canonical.Snapshot.Physics!.Value.Velocity);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    // ---------------------------------------------------------------
    // C. Envelope atomicity
    // ---------------------------------------------------------------

    [Fact]
    public void SameIncarnationEnvelopePublishesNothingUntilEveryStageCommitsThenPublishesInStageOrder()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 3UL);
        const uint guid = 0x70022000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));

        WorldSession.EntitySpawn sameCreate = Spawn(
            guid, 1, includePosition: false, positionSequence: 2) with
        {
            Name = "same-incarnation",
        };
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { ObjDesc = 2, State = 2, Vector = 2 },
            },
        };
        RuntimeEntityRegistrationResult same = lifetime
            .RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);
        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration, same.Inbound.Disposition);

        var observed = new List<RuntimeEntityChange>();
        int countAtFirstObservation = -1;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            observed.Add(delta.Change);
            countAtFirstObservation = observed.Count;
        }));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.True(observed.Count >= 1);
        Assert.Equal(observed.Count, countAtFirstObservation);

        RuntimeInitialCreateExecutedActionKind[] envelopeStages = receipt.Trace
            .Where(static a => a.Sequence == 1UL)
            .Select(static a => a.Kind)
            .ToArray();
        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.PreTailDescriptionAdaptation,
                RuntimeInitialCreateExecutedActionKind.ObjDesc,
                RuntimeInitialCreateExecutedActionKind.Pickup,
                RuntimeInitialCreateExecutedActionKind.State,
                RuntimeInitialCreateExecutedActionKind.Vector,
                RuntimeInitialCreateExecutedActionKind.WeenieDescription,
                RuntimeInitialCreateExecutedActionKind.ResidentCellCleanup,
            ],
            envelopeStages);
        RuntimeInitialCreateExecutedAction cleanup = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.ResidentCellCleanup);
        Assert.Equal(
            RuntimeResidentCellCleanupDisposition.CelllessNoWeenieMarkUnreachable,
            cleanup.ResidentCellCleanupDisposition);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void ResidentCellCleanupUnmarksWhenCellClaimedAndAlreadyResident()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 18UL);
        const uint guid = 0x7002A000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        Assert.Equal(Cell, canonical.FullCellId);

        WorldSession.EntitySpawn sameCreate = Spawn(guid, 1, positionSequence: 2, positionX: 15f);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { ObjDesc = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: 10f);
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);

        RuntimeInitialCreateExecutedAction cleanup = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.ResidentCellCleanup);
        Assert.Equal(
            RuntimeResidentCellCleanupDisposition.ResidentUnmarked,
            cleanup.ResidentCellCleanupDisposition);
        Assert.Equal(Cell, canonical.FullCellId);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void ResidentCellCleanupFailsClosedWhenClaimedCelllessOutcomeIsNotUnderLostCellOwnership()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 19UL);
        const uint guid = 0x7002A100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.False(lease.Placement.IsValid);
        Assert.Equal(0u, canonical.FullCellId);

        WorldSession.EntitySpawn sameCreate = Spawn(guid, 1, positionSequence: 2, positionX: 15f);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { ObjDesc = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: true);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: 0f);

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, inputs, out RuntimeInitialCreateExecutionReceipt receipt);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.RejectedAuthority, status);
        Assert.Equal(default, receipt);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.False(lifetime.TryGetInitialCreateResidence(canonical, out _));

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedToken,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, inputs, out RuntimeInitialCreateExecutionReceipt retryReceipt));
        Assert.Equal(default, retryReceipt);
    }

    [Fact]
    public void EnvelopePositionStageRequiringSetPositionYieldsResumesAndPublishesOnceAfterCompletion()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 20UL);
        const uint guid = 0x7002B000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.False(lease.Placement.IsValid);
        Assert.Equal(0u, canonical.FullCellId);
        AttachDormantBody(lifetime, canonical);

        WorldSession.EntitySpawn sameCreate = Spawn(guid, 1, positionSequence: 2, positionX: 15f);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { ObjDesc = 2, State = 2, Vector = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt pending);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);
        Assert.Equal(default, pending);
        Assert.Empty(observed);

        RuntimeEntityKey key = canonical.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(key, out RuntimeAuthoritativePositionRoute route));
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPosition, route.Disposition);
        CompletePendingContinuationPlacement(lifetime, key, route);
        Assert.Empty(observed);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            [
                RuntimeEntityChange.Updated,
                RuntimeEntityChange.Updated,
                RuntimeEntityChange.Updated,
                RuntimeEntityChange.Updated,
                RuntimeEntityChange.Updated,
            ],
            observed);
        RuntimeInitialCreateExecutedActionKind[] envelopeStages = receipt.Trace
            .Where(static a => a.Sequence == 1UL)
            .Select(static a => a.Kind)
            .ToArray();
        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.PreTailDescriptionAdaptation,
                RuntimeInitialCreateExecutedActionKind.ObjDesc,
                RuntimeInitialCreateExecutedActionKind.Position,
                RuntimeInitialCreateExecutedActionKind.State,
                RuntimeInitialCreateExecutedActionKind.Vector,
                RuntimeInitialCreateExecutedActionKind.WeenieDescription,
                RuntimeInitialCreateExecutedActionKind.ResidentCellCleanup,
            ],
            envelopeStages);
        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPosition,
            positionAction.PositionDisposition);
        Assert.Equal(Cell, canonical.FullCellId);
        RuntimeInitialCreateExecutedAction cleanup = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.ResidentCellCleanup);
        Assert.Equal(
            RuntimeResidentCellCleanupDisposition.ResidentUnmarked,
            cleanup.ResidentCellCleanupDisposition);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
    }


    [Fact]
    public void EnvelopeAbandonedDuringPositionStageYieldPublishesNothingAndConvergesEveryLedger()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 21UL);
        const uint guid = 0x7002B100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.False(lease.Placement.IsValid);
        AttachDormantBody(lifetime, canonical);

        WorldSession.EntitySpawn sameCreate = Spawn(guid, 1, positionSequence: 2, positionX: 15f);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { ObjDesc = 2, State = 2, Vector = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out _);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);
        Assert.Empty(observed);

        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(guid, canonical.Incarnation),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);

        Assert.Equal([RuntimeEntityChange.Deleted], observed);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        Assert.Equal(
            0,
            lifetime.Physics.SetPosition.CaptureOwnership().AcknowledgedPlacementCompletionCount);

        // A stale Execute() with the original token, after the entity is
        // gone, must not resurrect anything either.
        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedToken,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
    }

    [Fact]
    public void EnvelopeStageRetryDoesNotDuplicateAlreadyCommittedStages()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 4UL);
        const uint guid = 0x70022100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));

        WorldSession.EntitySpawn sameCreate = Spawn(
            guid, 1, includePosition: false, positionSequence: 2);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { Vector = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);

        RuntimeInitialCreateExecutionReceipt first = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);
        ulong vectorAuthorityAfterFirst = canonical.VectorAuthorityVersion;

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedToken,
            lifetime.InitialCreateExecution.Execute(canonical, lease.Token, NoContact, out _));
        Assert.Equal(vectorAuthorityAfterFirst, canonical.VectorAuthorityVersion);
        Assert.NotEmpty(first.Trace);
    }

    // ---------------------------------------------------------------
    // D. Position routes
    // ---------------------------------------------------------------

    [Fact]
    public void LocalOrdinaryPositionInterpolatesWithoutWorldPlacement()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 5UL);
        const uint guid = 0x70023000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 0, positionX: 15f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: true, PlayerDistance: 0f);
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);

        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.Interpolate,
            positionAction.PositionDisposition);
        Assert.Equal(15f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.Equal(Cell, canonical.FullCellId);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        Assert.Equal(RuntimePositionConstrainPhase.BeforePositionOperation, positionAction.ConstrainPhase);
        Assert.True(positionAction.UnparentBeforeRouting);
        Assert.Equal(RuntimeTeleportHookPhase.None, positionAction.HookPhase);
    }

    [Fact]
    public void LocalTeleportContinuationDrivesItsOwnAuthoredPlacementLifecycle()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 6UL);
        const uint guid = 0x70023100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 1, forcePositionSequence: 0, positionX: 40f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt pending);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);
        Assert.Equal(default, pending);
        Assert.Equal(40f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.Equal(Cell, canonical.FullCellId);

        RuntimeEntityKey key = canonical.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(key, out RuntimeAuthoritativePositionRoute route));
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple, route.Disposition);
        CompletePendingContinuationPlacement(lifetime, key, route);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);
        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            positionAction.PositionDisposition);
        Assert.True(positionAction.ZeroVelocity);
        Assert.Equal(RuntimePositionConstrainPhase.AfterPositionOperation, positionAction.ConstrainPhase);
        Assert.Equal(RuntimeTeleportHookPhase.AfterPositionOperation, positionAction.HookPhase);
        Assert.True(positionAction.UnparentBeforeRouting);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
    }

    [Fact]
    public void RemoteNearContactPositionInterpolatesAfterInitialResidency()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 7UL);
        const uint guid = 0x70023200u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 0, positionX: 25f, isGrounded: true);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: false, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: 10f);
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);

        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.Interpolate,
            positionAction.PositionDisposition);
        Assert.Equal(25f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.Equal(Cell, canonical.FullCellId);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        Assert.Equal(RuntimePositionConstrainPhase.AfterPositionOperation, positionAction.ConstrainPhase);
    }

    [Fact]
    public void LocalOrdinaryPositionRouteFollowsWireGroundedTrueWhenBodyContactIsFalse()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 97UL);
        const uint guid = 0x70039000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        ForceContact(canonical, inContact: false);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 0, positionX: 15f,
            isGrounded: true);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: true, PlayerDistance: 0f);
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);

        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.Interpolate,
            positionAction.PositionDisposition);
    }

    [Fact]
    public void RemotePositionRouteFollowsWireGroundedFalseWhenBodyContactIsTrue()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 98UL);
        const uint guid = 0x70039100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        ForceContact(canonical, inContact: true);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 0, positionX: 25f,
            isGrounded: false);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: false, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: 10f);
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);

        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.NoPositionOperation,
            positionAction.PositionDisposition);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
    }

    [Fact]
    public void RemoteFarPositionStopsInterpolatingAndRunsSetPositionSimple()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 8UL);
        const uint guid = 0x70023300u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 0, positionX: 25f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: false, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: 200f);
        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, inputs, out _);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);

        RuntimeEntityKey key = canonical.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(key, out RuntimeAuthoritativePositionRoute route));
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple, route.Disposition);
        Assert.True(route.StopInterpolating);
        CompletePendingContinuationPlacement(lifetime, key, route);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.True(positionAction.StopInterpolating);
        Assert.Equal(RuntimePositionConstrainPhase.AfterPositionOperation, positionAction.ConstrainPhase);
    }

    [Fact]
    public void ParentedInitialCreateNeverRunsAWorldPlacementThroughTheExecutor()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 9UL);
        const uint parentGuid = 0x70024100u;
        const uint guid = 0x70024000u;
        _ = lifetime.RegisterEntity(Spawn(parentGuid, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.False(lease.Placement.IsValid);
        Assert.Equal(RuntimeAuthoritativePositionDisposition.AwaitFreshPosition, lease.Route.Disposition);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(0u, receipt.FullCellId);
        Assert.Equal(RuntimeTeleportHookPhase.None, receipt.TeleportHookPhase);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void ForcePositionContinuationRecordsSetPositionSimpleWithPreservedHeadingAndNoParentClear()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 45UL);
        const uint guid = 0x7002F700u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        const uint parentGuid = 0x7002F701u;
        const uint parentLocation = 3u;
        Assert.True(lifetime.Entities.TryCommitParent(
            guid, parentGuid, parentLocation, placementId: 0u, positionSequence: 1,
            out WorldSession.EntitySpawn parented));
        lifetime.Entities.RefreshSnapshot(canonical, parented);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 1, positionX: 40f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out _);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);
        RuntimeEntityKey key = canonical.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(key, out RuntimeAuthoritativePositionRoute route));
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple, route.Disposition);
        CompletePendingContinuationPlacement(lifetime, key, route);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            positionAction.PositionDisposition);
        Assert.True(positionAction.PreserveHeading);
        Assert.True(positionAction.SendPositionImmediately);
        Assert.False(positionAction.StopInterpolating);
        Assert.False(positionAction.ZeroVelocity);
        Assert.Equal(RuntimePositionConstrainPhase.None, positionAction.ConstrainPhase);
        Assert.False(positionAction.UnparentBeforeRouting);
        Assert.NotNull(canonical.Snapshot.Position);
        Assert.Equal(40f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.Equal(parentGuid, canonical.Snapshot.ParentGuid);
        Assert.Equal(parentLocation, canonical.Snapshot.ParentLocation);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
    }

    [Fact]
    public void MissileFlaggedEntityPositionContinuationClassifiesToProjectileAuthoritativeOperationKind()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 46UL);
        const uint guid = 0x7002F800u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, missile: true),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(
            RuntimeSetPositionOperationKind.ProjectileAuthoritative,
            lease.Route.OperationKind);
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 0, positionX: 25f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: false, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: 200f);
        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, inputs, out _);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);

        RuntimeEntityKey key = canonical.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(key, out RuntimeAuthoritativePositionRoute route));
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple, route.Disposition);
        Assert.Equal(RuntimeSetPositionOperationKind.ProjectileAuthoritative, route.OperationKind);
        Assert.True(route.StopInterpolating);
        CompletePendingContinuationPlacement(lifetime, key, route);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);
        Assert.NotEmpty(receipt.Trace);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.True(positionAction.StopInterpolating);
        Assert.Equal(RuntimePositionConstrainPhase.AfterPositionOperation, positionAction.ConstrainPhase);
    }

    [Fact]
    public void PickedUpInitialCreateNeverRunsAWorldPlacementThroughTheExecutor()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 47UL);
        const uint guid = 0x7002F900u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        Assert.False(lease.Placement.IsValid);
        Assert.Equal(RuntimeAuthoritativePositionDisposition.AwaitFreshPosition, lease.Route.Disposition);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(0u, receipt.FullCellId);
        Assert.Equal(RuntimeTeleportHookPhase.None, receipt.TeleportHookPhase);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void ExecutionTimeRejectedPositionStampsRetainedTimestampsWithoutPoseApplication()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 48UL);
        const uint guid = 0x7002FA00u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        Assert.Equal(Cell, canonical.FullCellId);
        float originalPositionX = canonical.Snapshot.Position!.Value.PositionX;

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 0, positionX: 25f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: false, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: float.NaN);
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);

        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.RejectedData,
            positionAction.PositionDisposition);
        Assert.Equal(2, canonical.Snapshot.Physics!.Value.Timestamps.Position);
        Assert.Equal(0, canonical.Snapshot.Physics!.Value.Timestamps.Teleport);
        Assert.Equal(0, canonical.Snapshot.Physics!.Value.Timestamps.ForcePosition);
        // ... but no pose ever applies.
        Assert.Equal(originalPositionX, canonical.Snapshot.Position!.Value.PositionX);
        Assert.Equal(Cell, canonical.FullCellId);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }


    [Fact]
    public void MissingParentReplayConsumesExactAdmissionIdAndRegistersChildThroughCanonicalRoute()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 10UL);
        const uint parentGuid = 0x70025100u;
        const uint childGuid = 0x70025000u;
        RuntimeEntityRegistrationResult deferred = lifetime.RegisterEntityWithInitialResidence(
            Spawn(childGuid, 1, includePosition: false, parentGuid: parentGuid),
            isLocalPlayer: false);
        Assert.True(deferred.DeferredForParent);
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent,
            out RuntimeInitialCreateResidenceLease parentLease));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, parent, parentLease.Token, NoContact);

        Assert.Equal(1, receipt.ReplayedDeferredChildCount);
        RuntimeInitialCreateExecutedAction replay = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.DeferredChildReplay);
        Assert.Equal(RuntimeDeferredChildReplayOutcome.Registered, replay.DeferredChildOutcome);
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.True(lifetime.Entities.TryGetActive(childGuid, out RuntimeEntityRecord child));
        Assert.True(lifetime.TryGetInitialCreateResidence(child, out _));
    }

    [Fact]
    public void StaleAdmissionIdCannotConsumeAReplacementQueuedAfterThePeek()
    {
        var parents = new ParentAttachmentState();
        const uint parentGuid = 0x70025200u;
        WorldSession.EntitySpawn spawn = Spawn(
            0x70025300u, 1, includePosition: false, parentGuid: parentGuid);
        parents.EnqueueDeferredCreate(spawn, isLocalPlayer: false);
        Assert.True(parents.TryPeekDeferredCreate(parentGuid, out DeferredParentCreate stale));

        parents.Clear();
        parents.EnqueueDeferredCreate(spawn, isLocalPlayer: false);
        Assert.True(parents.TryPeekDeferredCreate(parentGuid, out DeferredParentCreate replacement));
        Assert.NotEqual(stale.AdmissionId, replacement.AdmissionId);

        Assert.False(parents.ConsumeDeferredCreate(parentGuid, stale));
        Assert.Equal(1, parents.DeferredCreateCount);
        Assert.True(parents.ConsumeDeferredCreate(parentGuid, replacement));
        Assert.Equal(0, parents.DeferredCreateCount);
    }

    [Fact]
    public void MultipleChildrenQueuedBehindOneMissingParentReplayInFifoOrderThroughTheExecutor()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 32UL);
        const uint parentGuid = 0x7002E100u;
        const uint firstChildGuid = 0x7002E000u;
        const uint secondChildGuid = 0x7002E001u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(firstChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(secondChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(2, lifetime.CaptureOwnership().DeferredParentCreateCount);

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, parent, parentLease.Token, NoContact);

        Assert.Equal(2, receipt.ReplayedDeferredChildCount);
        RuntimeInitialCreateExecutedAction[] replays = receipt.Trace
            .Where(static a => a.Kind is RuntimeInitialCreateExecutedActionKind.DeferredChildReplay)
            .ToArray();
        Assert.Equal(2, replays.Length);
        Assert.All(
            replays,
            static a => Assert.Equal(RuntimeDeferredChildReplayOutcome.Registered, a.DeferredChildOutcome));
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.True(lifetime.Entities.TryGetActive(firstChildGuid, out _));
        Assert.True(lifetime.Entities.TryGetActive(secondChildGuid, out _));
    }

    [Fact]
    public void DeferredChildReplayContainsOneChildsThrowingRegistrationAndContinuesWithSiblings()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 92UL);
        const uint parentGuid = 0x70035000u;
        const uint firstChildGuid = 0x70035001u;
        const uint secondChildGuid = 0x70035002u;
        const uint thirdChildGuid = 0x70035003u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(firstChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(secondChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(thirdChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(3, lifetime.CaptureOwnership().DeferredParentCreateCount);

        WrapDeferredChildRegistration(lifetime, (original, spawn, isLocalPlayer) =>
        {
            if (spawn.Guid == secondChildGuid)
            {
                throw new InvalidOperationException(
                    "Injected R4-1 containment test failure.");
            }
            return original(spawn, isLocalPlayer);
        });

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        // No exception escapes Execute - it returns a typed status.
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, parent, parentLease.Token, NoContact);

        Assert.Equal(3, receipt.ReplayedDeferredChildCount);
        RuntimeInitialCreateExecutedAction[] replays = receipt.Trace
            .Where(static a => a.Kind is RuntimeInitialCreateExecutedActionKind.DeferredChildReplay)
            .ToArray();
        Assert.Equal(3, replays.Length);
        Assert.Equal(RuntimeDeferredChildReplayOutcome.Registered, replays[0].DeferredChildOutcome);
        Assert.Equal(RuntimeDeferredChildReplayOutcome.Rejected, replays[1].DeferredChildOutcome);
        Assert.Equal(RuntimeDeferredChildReplayOutcome.Registered, replays[2].DeferredChildOutcome);
        Assert.True(lifetime.Entities.TryGetActive(firstChildGuid, out _));
        Assert.False(lifetime.Entities.TryGetActive(secondChildGuid, out _));
        Assert.True(lifetime.Entities.TryGetActive(thirdChildGuid, out _));
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.Equal(1, lifetime.CaptureOwnership().ReplayFailureCount);
        Assert.True(lifetime.CaptureOwnership().HasLastReplayFailure);
    }

    [Fact]
    public void DeferredChildReplayRestoresTheUnprocessedRemainderWhenTheParentIsDeletedReentrantlyMidReplay()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 93UL);
        const uint parentGuid = 0x70036000u;
        const uint firstChildGuid = 0x70036001u;
        const uint secondChildGuid = 0x70036002u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(firstChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(secondChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(2, lifetime.CaptureOwnership().DeferredParentCreateCount);

        WrapDeferredChildRegistration(lifetime, (original, spawn, isLocalPlayer) =>
        {
            RuntimeEntityRegistrationResult result = original(spawn, isLocalPlayer);
            if (spawn.Guid == firstChildGuid)
            {
                Assert.True(lifetime.TryAcceptDelete(
                    new DeleteObject.Parsed(parentGuid, 1),
                    isLocalPlayer: false,
                    removeRetainedObject: true,
                    out RuntimeEntityDeleteAcceptance acceptance));
                lifetime.CompleteAcceptedDelete(acceptance);
            }
            return result;
        });

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                parent, parentLease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);

        Assert.True(lifetime.Entities.TryGetActive(firstChildGuid, out RuntimeEntityRecord firstChild));
        Assert.True(lifetime.Entities.ParentAttachments.ContainsDeferredCreate(secondChildGuid, 1));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(1, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.True(lifetime.TryGetInitialCreateResidence(firstChild, out _));
    }

    private static void WrapDeferredChildRegistration(
        RuntimeEntityObjectLifetime lifetime,
        Func<
            Func<WorldSession.EntitySpawn, bool, RuntimeEntityRegistrationResult>,
            WorldSession.EntitySpawn,
            bool,
            RuntimeEntityRegistrationResult> wrapper)
    {
        Type executorType = typeof(RuntimeInitialCreateContinuationExecutor);
        System.Reflection.FieldInfo field = executorType.GetField(
            "_registerDeferredChild",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var original = (Func<WorldSession.EntitySpawn, bool, RuntimeEntityRegistrationResult>)
            field.GetValue(lifetime.InitialCreateExecution)!;
        Func<WorldSession.EntitySpawn, bool, RuntimeEntityRegistrationResult> wrapped =
            (spawn, isLocalPlayer) => wrapper(original, spawn, isLocalPlayer);
        field.SetValue(lifetime.InitialCreateExecution, wrapped);
    }

    private static void AdmitThenOrphanParentRelation(
        RuntimeEntityObjectLifetime lifetime,
        uint parentGuid,
        uint childGuid,
        ushort namedParentIncarnation,
        uint parentLocation = 1u)
    {
        RuntimeEntityRecord parent = lifetime.RegisterEntity(
            Spawn(parentGuid, namedParentIncarnation, includePosition: false)).Canonical!;
        var parentUpdate = new ParentEvent.Parsed(
            parentGuid,
            childGuid,
            ParentLocation: parentLocation,
            PlacementId: 0u,
            ParentInstanceSequence: namedParentIncarnation,
            ChildPositionSequence: 2);
        Assert.True(lifetime.TryApplyParent(parentUpdate, null, out _));
        Assert.True(lifetime.Entities.RemoveActive(parent));
    }

    [Fact]
    public void ParentContinuationRevalidatesLiveParentAtExecutionAndQueuesOnMismatchThenAppliesWhenTheParentArrives()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 33UL);
        const uint parentGuid = 0x7002E200u;
        const uint childGuid = 0x7002E300u;
        _ = lifetime.RegisterEntity(Spawn(parentGuid, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var parentUpdate = new ParentEvent.Parsed(
            parentGuid,
            childGuid,
            ParentLocation: 1u,
            PlacementId: 0u,
            ParentInstanceSequence: 1,
            ChildPositionSequence: 2);
        // Admission succeeds - the parent is still active at this moment.
        Assert.True(lifetime.TryApplyParent(parentUpdate, null, out _));

        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(parentGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        RuntimeInitialCreateExecutedAction parentAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Parent);
        Assert.Equal(
            RuntimeParentRelationOutcome.DeferredAwaitingParent,
            parentAction.ParentRelationOutcome);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.True(lifetime.Entities.TryGetActive(childGuid, out _));
        Assert.True(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(childGuid, canonical.Key!.Value));
        Assert.Null(canonical.Snapshot.ParentGuid);
    }

    [Fact]
    public void DeferredAcceptedParentRelationAppliesExactlyOnceWhenTheSameParentIncarnationReturns()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 113UL);
        const uint parentGuid = 0x70049000u;
        const uint childGuid = 0x70049001u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AdmitThenOrphanParentRelation(lifetime, parentGuid, childGuid, namedParentIncarnation: 1);
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);
        RuntimeInitialCreateExecutedAction parentAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Parent);
        Assert.Equal(RuntimeParentRelationOutcome.DeferredAwaitingParent, parentAction.ParentRelationOutcome);
        Assert.True(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(childGuid, canonical.Key!.Value));

        // The SAME parent incarnation (1) reappears - the queued relation
        // replays and applies exactly once.
        RuntimeEntityRecord recreatedParent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            recreatedParent, out RuntimeInitialCreateResidenceLease parentLease));
        RuntimeInitialCreateExecutionReceipt parentReceipt = RunToCompletion(
            lifetime, recreatedParent, parentLease.Token, NoContact);

        RuntimeInitialCreateExecutedAction replayAction = Assert.Single(
            parentReceipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.ParentRelationReplay);
        Assert.Equal(RuntimeParentRelationOutcome.Applied, replayAction.ParentRelationOutcome);
        Assert.Equal(2, canonical.Snapshot.Physics!.Value.Timestamps.Position);
        Assert.Null(canonical.Snapshot.ParentGuid);
        Assert.False(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(childGuid, canonical.Key!.Value));
        Assert.Equal(0, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void DeferredAcceptedParentRelationDiscardsWhenTheArrivingParentIsANewerIncarnationThanTheRelationNamed()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 104UL);
        const uint parentGuid = 0x7003F000u;
        const uint childGuid = 0x7003F001u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AdmitThenOrphanParentRelation(lifetime, parentGuid, childGuid, namedParentIncarnation: 1);
        _ = RunToCompletion(lifetime, canonical, lease.Token, NoContact);
        Assert.True(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(childGuid, canonical.Key!.Value));

        // Parent arrives directly at incarnation 2 - NEWER than the
        // relation's named incarnation 1.
        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 2, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));
        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, parent, parentLease.Token, NoContact);

        RuntimeInitialCreateExecutedAction replayAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.ParentRelationReplay);
        Assert.Equal(RuntimeParentRelationOutcome.DiscardedStaleParent, replayAction.ParentRelationOutcome);
        Assert.False(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(childGuid, canonical.Key!.Value));
        Assert.Null(canonical.Snapshot.ParentGuid);
        Assert.Equal(0, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);
    }

    [Fact]
    public void DeferredAcceptedParentRelationStaysQueuedWhenTheArrivingParentIsOlderThanTheRelationNamedAndAppliesOnTheNextMatchingIncarnation()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 105UL);
        const uint parentGuid = 0x70040000u;
        const uint childGuid = 0x70040001u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        _ = RunToCompletion(lifetime, canonical, lease.Token, NoContact);
        var parentUpdate = new ParentEvent.Parsed(
            parentGuid, childGuid, ParentLocation: 1u, PlacementId: 0u,
            ParentInstanceSequence: 5, ChildPositionSequence: 2);
        lifetime.Entities.ParentAttachments.EnqueueDeferredAcceptedRelation(
            childGuid, canonical.Key!.Value, parentUpdate, null, default);

        // Parent arrives at incarnation 3 - OLDER than the relation's
        // named incarnation 5.
        RuntimeEntityRecord parentAtThree = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 3, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parentAtThree, out RuntimeInitialCreateResidenceLease leaseThree));
        RuntimeInitialCreateExecutionReceipt receiptThree = RunToCompletion(
            lifetime, parentAtThree, leaseThree.Token, NoContact);
        RuntimeInitialCreateExecutedAction replayThree = Assert.Single(
            receiptThree.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.ParentRelationReplay);
        Assert.Equal(RuntimeParentRelationOutcome.DeferredAwaitingParent, replayThree.ParentRelationOutcome);
        Assert.True(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(childGuid, canonical.Key!.Value));

        // Parent recreated at incarnation 5 - now matches.
        RuntimeEntityRecord parentAtFive = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 5, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parentAtFive, out RuntimeInitialCreateResidenceLease leaseFive));
        RuntimeInitialCreateExecutionReceipt receiptFive = RunToCompletion(
            lifetime, parentAtFive, leaseFive.Token, NoContact);
        RuntimeInitialCreateExecutedAction replayFive = Assert.Single(
            receiptFive.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.ParentRelationReplay);
        Assert.Equal(RuntimeParentRelationOutcome.Applied, replayFive.ParentRelationOutcome);
        Assert.Null(canonical.Snapshot.ParentGuid);
        Assert.Equal(0, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);
    }

    [Fact]
    public void DeferredAcceptedParentRelationIsCancelledWhenTheChildIsDeletedWhileQueued()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 106UL);
        const uint parentGuid = 0x70041000u;
        const uint childGuid = 0x70041001u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AdmitThenOrphanParentRelation(lifetime, parentGuid, childGuid, namedParentIncarnation: 1);
        _ = RunToCompletion(lifetime, canonical, lease.Token, NoContact);
        Assert.Equal(1, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);

        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(childGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);

        Assert.False(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(childGuid, canonical.Key!.Value));
        Assert.Equal(0, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);
    }

    [Fact]
    public void DeferredAcceptedParentRelationClearsOnSessionResetAndConverges()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 107UL);
        const uint parentGuid = 0x70042000u;
        const uint childGuid = 0x70042001u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AdmitThenOrphanParentRelation(lifetime, parentGuid, childGuid, namedParentIncarnation: 1);
        _ = RunToCompletion(lifetime, canonical, lease.Token, NoContact);
        Assert.Equal(1, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);

        IReadOnlyList<RuntimeEntityRecord> retirements = lifetime.BeginSessionClear();
        Assert.Equal(0, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);
        foreach (RuntimeEntityRecord record in retirements)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
    }

    [Fact]
    public void DeferredAcceptedParentRelationStaleWindowTokenCannotRestoreAfterClearAndRequeue()
    {
        var parents = new ParentAttachmentState();
        const uint parentGuid = 0x70043000u;
        const uint childGuid = 0x70043001u;
        var childKey = new RuntimeEntityKey(1u, 1);
        var parentUpdate = new ParentEvent.Parsed(
            parentGuid, childGuid, ParentLocation: 1u, PlacementId: 0u,
            ParentInstanceSequence: 1, ChildPositionSequence: 2);
        parents.EnqueueDeferredAcceptedRelation(childGuid, childKey, parentUpdate, null, default);

        ImmutableArray<DeferredAcceptedParentRelation> detached =
            parents.DetachDeferredAcceptedRelations(parentGuid, out DeferredReplayWindowToken staleWindow);
        DeferredAcceptedParentRelation stale = Assert.Single(detached);

        parents.Clear();

        // A restore against the now-stale token must be a silent no-op.
        parents.RestoreDeferredAcceptedRelations(staleWindow, [stale]);
        Assert.Equal(0, parents.DeferredAcceptedRelationCount);
        Assert.False(parents.ContainsDeferredAcceptedRelation(childGuid, childKey));
    }

    [Fact]
    public void EnvelopeCreateParentQueuesForUnaddressableParentAndAppliesWhenTheParentArrives()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 108UL);
        const uint parentGuid = 0x70044000u;
        const uint childGuid = 0x70044001u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        RuntimeEntityRecord orphanedParent = lifetime.RegisterEntity(
            Spawn(parentGuid, 1, includePosition: false)).Canonical!;

        WorldSession.EntitySpawn sameCreate = Spawn(
            childGuid, 1, includePosition: false, positionSequence: 2, parentGuid: parentGuid);
        PhysicsSpawnData physics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = physics with
            {
                Timestamps = physics.Timestamps with { State = 2, Vector = 2 },
            },
        };
        _ = lifetime.RegisterEntityWithInitialResidence(sameCreate, isLocalPlayer: false);

        Assert.True(lifetime.Entities.RemoveActive(orphanedParent));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);
        RuntimeInitialCreateExecutedAction createParentAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.CreateParent);
        Assert.Equal(RuntimeParentRelationOutcome.DeferredAwaitingParent, createParentAction.ParentRelationOutcome);
        Assert.True(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(childGuid, canonical.Key!.Value));

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));
        RuntimeInitialCreateExecutionReceipt parentReceipt = RunToCompletion(
            lifetime, parent, parentLease.Token, NoContact);
        RuntimeInitialCreateExecutedAction replayAction = Assert.Single(
            parentReceipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.ParentRelationReplay);
        Assert.Equal(RuntimeParentRelationOutcome.Applied, replayAction.ParentRelationOutcome);
        Assert.Null(canonical.Snapshot.ParentGuid);
        Assert.Equal(0, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);
    }


    [Fact]
    public void DeferredChildReplayWindowFiltersASiblingDeletedMidReplayFromTheRestoredRemainder()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 109UL);
        const uint parentGuid = 0x70045000u;
        const uint firstChildGuid = 0x70045001u;
        const uint secondChildGuid = 0x70045002u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(firstChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(secondChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);

        RuntimeEntityRecord? firstChild = null;
        WrapDeferredChildRegistration(lifetime, (original, spawn, isLocalPlayer) =>
        {
            RuntimeEntityRegistrationResult result = original(spawn, isLocalPlayer);
            if (spawn.Guid == firstChildGuid)
            {
                firstChild = result.Canonical;
                _ = lifetime.TryAcceptDelete(
                    new DeleteObject.Parsed(secondChildGuid, 1), isLocalPlayer: false,
                    removeRetainedObject: true, out _);

                Assert.True(lifetime.TryAcceptDelete(
                    new DeleteObject.Parsed(parentGuid, 1), isLocalPlayer: false,
                    removeRetainedObject: true, out RuntimeEntityDeleteAcceptance parentAcceptance));
                lifetime.CompleteAcceptedDelete(parentAcceptance);
            }
            return result;
        });

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                parent, parentLease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
        Assert.True(lifetime.Entities.TryGetActive(firstChildGuid, out _));
        Assert.False(lifetime.Entities.ParentAttachments.ContainsDeferredCreate(secondChildGuid, 1));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.Entities.ParentAttachments.DeferredCreateCount);

        Assert.NotNull(firstChild);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            firstChild!, out RuntimeInitialCreateResidenceLease firstChildLease));
        _ = RunToCompletion(lifetime, firstChild!, firstChildLease.Token, NoContact);

        RuntimeEntityRecord recreatedParent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 2, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            recreatedParent, out RuntimeInitialCreateResidenceLease recreatedLease));
        RuntimeInitialCreateExecutionReceipt recreatedReceipt = RunToCompletion(
            lifetime, recreatedParent, recreatedLease.Token, NoContact);
        Assert.Equal(0, recreatedReceipt.ReplayedDeferredChildCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
    }

    [Fact]
    public void DeferredChildReplayWindowReleasesSilentlyWhenSessionClearFiresMidReplay()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 110UL);
        const uint parentGuid = 0x70046000u;
        const uint firstChildGuid = 0x70046001u;
        const uint secondChildGuid = 0x70046002u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(firstChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(secondChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);

        WrapDeferredChildRegistration(lifetime, (original, spawn, isLocalPlayer) =>
        {
            RuntimeEntityRegistrationResult result = original(spawn, isLocalPlayer);
            if (spawn.Guid == firstChildGuid)
            {
                IReadOnlyList<RuntimeEntityRecord> retirements = lifetime.BeginSessionClear();
                foreach (RuntimeEntityRecord record in retirements)
                    lifetime.CompleteSessionEntityRetirement(record);
            }
            return result;
        });

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                parent, parentLease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
    }

    [Fact]
    public void DeferredAcceptedRelationReplayWindowFiltersASiblingRelationDeletedMidReplay()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 111UL);
        const uint parentGuid = 0x70047000u;
        const uint firstChildGuid = 0x70047001u;
        const uint secondChildGuid = 0x70047002u;

        RuntimeEntityRecord firstChild = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(firstChildGuid, 1, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(firstChild, out RuntimeInitialCreateResidenceLease firstLease));
        RuntimeEntityRecord secondChild = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(secondChildGuid, 1, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(secondChild, out RuntimeInitialCreateResidenceLease secondLease));

        RuntimeEntityRecord orphanedParent = lifetime.RegisterEntity(
            Spawn(parentGuid, 1, includePosition: false)).Canonical!;
        var firstParentUpdate = new ParentEvent.Parsed(
            parentGuid, firstChildGuid, ParentLocation: 1u, PlacementId: 0u,
            ParentInstanceSequence: 1, ChildPositionSequence: 2);
        Assert.True(lifetime.TryApplyParent(firstParentUpdate, null, out _));
        var secondParentUpdate = new ParentEvent.Parsed(
            parentGuid, secondChildGuid, ParentLocation: 1u, PlacementId: 0u,
            ParentInstanceSequence: 1, ChildPositionSequence: 2);
        Assert.True(lifetime.TryApplyParent(secondParentUpdate, null, out _));
        Assert.True(lifetime.Entities.RemoveActive(orphanedParent));

        _ = RunToCompletion(lifetime, firstChild, firstLease.Token, NoContact);
        _ = RunToCompletion(lifetime, secondChild, secondLease.Token, NoContact);

        Assert.Equal(2, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);

        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (delta.Change is not RuntimeEntityChange.Updated
                || delta.Entity.Identity.ServerGuid != firstChildGuid)
            {
                return;
            }
            Assert.True(lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(secondChildGuid, 1), isLocalPlayer: false,
                removeRetainedObject: true, out RuntimeEntityDeleteAcceptance secondAcceptance));
            lifetime.CompleteAcceptedDelete(secondAcceptance);

            Assert.True(lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(parentGuid, 1), isLocalPlayer: false,
                removeRetainedObject: true, out RuntimeEntityDeleteAcceptance parentAcceptance));
            lifetime.CompleteAcceptedDelete(parentAcceptance);
        }));

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                parent, parentLease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
        Assert.False(lifetime.Entities.ParentAttachments
            .ContainsDeferredAcceptedRelation(secondChildGuid, secondChild.Key!.Value));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void DeferredAcceptedRelationReplayWindowReleasesSilentlyWhenSessionClearFiresMidReplay()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 112UL);
        const uint parentGuid = 0x70048000u;
        const uint firstChildGuid = 0x70048001u;
        const uint secondChildGuid = 0x70048002u;

        RuntimeEntityRecord firstChild = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(firstChildGuid, 1, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(firstChild, out RuntimeInitialCreateResidenceLease firstLease));
        RuntimeEntityRecord secondChild = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(secondChildGuid, 1, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(secondChild, out RuntimeInitialCreateResidenceLease secondLease));

        RuntimeEntityRecord orphanedParent = lifetime.RegisterEntity(
            Spawn(parentGuid, 1, includePosition: false)).Canonical!;
        var firstParentUpdate = new ParentEvent.Parsed(
            parentGuid, firstChildGuid, ParentLocation: 1u, PlacementId: 0u,
            ParentInstanceSequence: 1, ChildPositionSequence: 2);
        Assert.True(lifetime.TryApplyParent(firstParentUpdate, null, out _));
        var secondParentUpdate = new ParentEvent.Parsed(
            parentGuid, secondChildGuid, ParentLocation: 1u, PlacementId: 0u,
            ParentInstanceSequence: 1, ChildPositionSequence: 2);
        Assert.True(lifetime.TryApplyParent(secondParentUpdate, null, out _));
        Assert.True(lifetime.Entities.RemoveActive(orphanedParent));

        _ = RunToCompletion(lifetime, firstChild, firstLease.Token, NoContact);
        _ = RunToCompletion(lifetime, secondChild, secondLease.Token, NoContact);

        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (delta.Change is not RuntimeEntityChange.Updated
                || delta.Entity.Identity.ServerGuid != firstChildGuid)
            {
                return;
            }
            IReadOnlyList<RuntimeEntityRecord> retirements = lifetime.BeginSessionClear();
            foreach (RuntimeEntityRecord record in retirements)
                lifetime.CompleteSessionEntityRetirement(record);
        }));

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                parent, parentLease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
        Assert.Equal(0, lifetime.Entities.ParentAttachments.DeferredAcceptedRelationCount);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
    }

    [Fact]
    public void DeferredChildQueuedForDeletedParentIncarnationStillReplaysAgainstTheRecreatedParentGuid()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 50UL);
        const uint parentGuid = 0x70030100u;
        const uint childGuid = 0x70030000u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);

        RuntimeEntityRecord parentGen1 = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(parentGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);
        Assert.False(lifetime.Entities.IsCurrent(parentGen1));
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);

        // Parent GUID reused at a NEWER incarnation.
        RuntimeEntityRecord parentGen2 = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 2, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parentGen2, out RuntimeInitialCreateResidenceLease parentLease));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, parentGen2, parentLease.Token, NoContact);

        Assert.Equal(1, receipt.ReplayedDeferredChildCount);
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.True(lifetime.Entities.TryGetActive(childGuid, out RuntimeEntityRecord child));
        Assert.True(lifetime.TryGetInitialCreateResidence(child, out _));
    }

    [Fact]
    public void ChildExactDeleteBeforeParentReplayCancelsOnlyThatChild()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 51UL);
        const uint parentGuid = 0x70030200u;
        const uint firstChildGuid = 0x70030300u;
        const uint secondChildGuid = 0x70030400u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(firstChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(secondChildGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(2, lifetime.CaptureOwnership().DeferredParentCreateCount);

        Assert.False(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(firstChildGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out _));
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, parent, parentLease.Token, NoContact);

        Assert.Equal(1, receipt.ReplayedDeferredChildCount);
        Assert.False(lifetime.Entities.TryGetActive(firstChildGuid, out _));
        Assert.True(lifetime.Entities.TryGetActive(secondChildGuid, out _));
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
    }

    [Fact]
    public void NewerDeferredChildGenerationSurvivesOlderGenerationCleanupAndReplays()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 52UL);
        const uint parentGuid = 0x70030500u;
        const uint childGuid = 0x70030600u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 2, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(2, lifetime.CaptureOwnership().DeferredParentCreateCount);

        Assert.False(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(childGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out _));
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, parent, parentLease.Token, NoContact);

        Assert.Equal(1, receipt.ReplayedDeferredChildCount);
        Assert.True(lifetime.Entities.TryGetActive(childGuid, out RuntimeEntityRecord child));
        Assert.Equal(2, child.Incarnation);
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
    }

    [Fact]
    public void ChildGuidReuseAfterFullLifecycleReplaysCleanlyWithoutStaleState()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 53UL);
        const uint firstParentGuid = 0x70030700u;
        const uint secondParentGuid = 0x70030800u;
        const uint childGuid = 0x70030900u;

        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false, parentGuid: firstParentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        RuntimeEntityRecord firstParent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(firstParentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            firstParent, out RuntimeInitialCreateResidenceLease firstParentLease));
        _ = RunToCompletion(lifetime, firstParent, firstParentLease.Token, NoContact);
        Assert.True(lifetime.Entities.TryGetActive(childGuid, out RuntimeEntityRecord firstChild));
        Assert.True(lifetime.TryGetInitialCreateResidence(firstChild, out RuntimeInitialCreateResidenceLease childLease));
        _ = RunToCompletion(lifetime, firstChild, childLease.Token, NoContact);

        // Fully delete the first incarnation (its gate is removed too).
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(childGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance deleteAcceptance));
        lifetime.CompleteAcceptedDelete(deleteAcceptance);
        Assert.False(lifetime.Entities.TryGetActive(childGuid, out _));

        // The SAME guid reused for a fresh incarnation, deferred behind a
        // DIFFERENT missing parent.
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 2, includePosition: false, parentGuid: secondParentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);

        RuntimeEntityRecord secondParent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(secondParentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            secondParent, out RuntimeInitialCreateResidenceLease secondParentLease));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, secondParent, secondParentLease.Token, NoContact);

        Assert.Equal(1, receipt.ReplayedDeferredChildCount);
        Assert.True(lifetime.Entities.TryGetActive(childGuid, out RuntimeEntityRecord reusedChild));
        Assert.Equal(2, reusedChild.Incarnation);
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
    }

    [Fact]
    public void SessionResetBeforeParentArrivalClearsDeferredBucket()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 54UL);
        const uint parentGuid = 0x70030A00u;
        const uint childGuid = 0x70030B00u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);

        IReadOnlyList<RuntimeEntityRecord> retirements = lifetime.BeginSessionClear();
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
        foreach (RuntimeEntityRecord record in retirements)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
    }

    [Fact]
    public void SessionResetFromWithinReplayDrivenCallbackConverges()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 55UL);
        const uint parentGuid = 0x70030C00u;
        const uint childGuid = 0x70030D00u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        IReadOnlyList<RuntimeEntityRecord>? retirements = null;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (delta.Change is not RuntimeEntityChange.Registered
                || delta.Entity.Identity.ServerGuid != childGuid
                || retirements is not null)
            {
                return;
            }
            retirements = lifetime.BeginSessionClear();
        }));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            parent, parentLease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.Equal(RuntimeInitialCreateExecutionStatus.RejectedAuthority, status);
        Assert.Equal(default, receipt);
        Assert.NotNull(retirements);
        foreach (RuntimeEntityRecord record in retirements!)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
    }

    [Fact]
    public void InstanceSequenceZeroChildReplaysCorrectly()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 56UL);
        const uint parentGuid = 0x70030E00u;
        const uint childGuid = 0x70030F00u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 0, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);

        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent, out RuntimeInitialCreateResidenceLease parentLease));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, parent, parentLease.Token, NoContact);

        Assert.Equal(1, receipt.ReplayedDeferredChildCount);
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.True(lifetime.Entities.TryGetActive(childGuid, out RuntimeEntityRecord child));
        Assert.Equal(0, child.Incarnation);
        Assert.True(lifetime.TryGetInitialCreateResidence(child, out _));
    }


    [Fact]
    public void DeleteFromObserverBeforeInitialAdoptionAbandonsFirstExecuteCleanly()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 60UL);
        const uint guid = 0x70031000u;

        RuntimeEntityRecord? capturedCanonical = null;
        RuntimeInitialCreateResidenceLease capturedLease = default;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (delta.Change is not RuntimeEntityChange.Registered
                || delta.Entity.Identity.ServerGuid != guid)
            {
                return;
            }
            Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord active));
            capturedCanonical = active;
            Assert.True(lifetime.TryGetInitialCreateResidence(active, out capturedLease));
            Assert.True(lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(guid, active.Incarnation),
                isLocalPlayer: false,
                removeRetainedObject: true,
                out RuntimeEntityDeleteAcceptance acceptance));
            lifetime.CompleteAcceptedDelete(acceptance);
        }));

        _ = lifetime.RegisterEntityWithInitialResidence(
            Spawn(guid, 1, includePosition: false),
            isLocalPlayer: false);
        Assert.NotNull(capturedCanonical);
        RuntimeEntityRecord canonical = capturedCanonical!;
        Assert.False(lifetime.Entities.IsCurrent(canonical));
        // TryAcceptDelete's own ForgetInitialCreateResidence already retired
        // the residence reentrantly - nothing was ever adopted.
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);

        var observed = new List<RuntimeEntityChange>();
        using IDisposable secondSubscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedToken,
            lifetime.InitialCreateExecution.Execute(
                canonical, capturedLease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
        Assert.Empty(observed);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void DeleteFromObserverBetweenFifoEntriesAppliesFirstOnlyAndConverges()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 61UL);
        const uint guid = 0x70031100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var appearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x09000002u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(appearance, null, out _));
        var vector = new VectorUpdate.Parsed(
            guid, new Vector3(11f, 12f, 13f), Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        var observed = new List<RuntimeEntityChange>();
        bool deletedFromObserver = false;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            observed.Add(delta.Change);
            if (deletedFromObserver || delta.Change is not RuntimeEntityChange.Updated)
                return;
            deletedFromObserver = true;
            // Between FIFO entry 1 (ObjDesc, already applied+published) and
            // entry 2 (Vector, not yet reached) - delete reentrantly.
            Assert.True(lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(guid, canonical.Incarnation),
                isLocalPlayer: false,
                removeRetainedObject: true,
                out RuntimeEntityDeleteAcceptance acceptance));
            lifetime.CompleteAcceptedDelete(acceptance);
        }));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.Equal(RuntimeInitialCreateExecutionStatus.RejectedAuthority, status);
        Assert.Equal(default, receipt);
        Assert.Equal(
            [RuntimeEntityChange.Updated, RuntimeEntityChange.Deleted],
            observed);
        Assert.Equal(0x09000002u, canonical.Snapshot.BasePaletteId);
        Assert.Null(canonical.Snapshot.Physics!.Value.Velocity);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);

        // A stale retry never resurrects anything or reapplies Vector.
        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedToken,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt retryReceipt));
        Assert.Equal(default, retryReceipt);
        Assert.Null(canonical.Snapshot.Physics!.Value.Velocity);
    }

    [Fact]
    public void ObserverThrowDuringPublishIsContainedAndDrainConvergesWithNoDuplicateRetry()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 62UL);
        const uint guid = 0x70031200u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        var vector = new VectorUpdate.Parsed(
            guid, Vector3.One, Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
            throw new InvalidOperationException("Deliberate observer failure for F(d).")));
        long failuresBefore = lifetime.Events.DispatchFailureCount;

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Contains(
            RuntimeInitialCreateExecutedActionKind.Vector,
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(Vector3.One, canonical.Snapshot.Physics!.Value.Velocity);
        Assert.True(lifetime.Events.DispatchFailureCount > failuresBefore);
        Assert.NotNull(lifetime.Events.LastDispatchFailure);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedToken,
            lifetime.InitialCreateExecution.Execute(canonical, lease.Token, NoContact, out _));
        Assert.Equal(Vector3.One, canonical.Snapshot.Physics!.Value.Velocity);
    }

    // ---------------------------------------------------------------
    // G. Reentrancy
    // ---------------------------------------------------------------

    [Fact]
    public void ReentrantExecuteForTheSameEntityFailsClosedRatherThanInterleaving()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 11UL);
        const uint guid = 0x70026000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));

        var reentrantStatuses = new List<RuntimeInitialCreateExecutionStatus>();
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (delta.Change is not RuntimeEntityChange.Updated || reentrantStatuses.Count != 0)
                return;
            reentrantStatuses.Add(lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out _));
        }));
        var vector = new VectorUpdate.Parsed(
            guid, Vector3.One, Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            [RuntimeInitialCreateExecutionStatus.RejectedAuthority],
            reentrantStatuses);
        Assert.NotEmpty(receipt.Trace);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void DeleteDuringDeferredChildReplayAbandonsExecutionWithoutResurrection()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 12UL);
        const uint parentGuid = 0x70026100u;
        const uint childGuid = 0x70026200u;
        Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(childGuid, 1, includePosition: false, parentGuid: parentGuid),
                isLocalPlayer: false)
            .DeferredForParent);
        RuntimeEntityRecord parent = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(parentGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            parent,
            out RuntimeInitialCreateResidenceLease parentLease));

        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (delta.Change is not RuntimeEntityChange.Registered
                || delta.Entity.Identity.ServerGuid != childGuid)
            {
                return;
            }
            Assert.True(lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(parentGuid, parent.Incarnation),
                isLocalPlayer: false,
                removeRetainedObject: true,
                out RuntimeEntityDeleteAcceptance acceptance));
            lifetime.CompleteAcceptedDelete(acceptance);
        }));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            parent, parentLease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.Equal(RuntimeInitialCreateExecutionStatus.RejectedAuthority, status);
        Assert.Equal(default, receipt);
        Assert.False(lifetime.Entities.IsCurrent(parent));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(1, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.True(lifetime.Entities.TryGetActive(childGuid, out RuntimeEntityRecord child));
        Assert.True(lifetime.TryGetInitialCreateResidence(child, out _));
    }

    [Fact]
    public void WireApplyDuringDrainPublishDoesNotRetireResidenceAndPreExistingFifoStillDrains()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 63UL);
        const uint guid = 0x70031300u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        var appearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x0A000002u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(appearance, null, out _));
        var vector = new VectorUpdate.Parsed(
            guid, new Vector3(21f, 22f, 23f), Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        var observed = new List<RuntimeEntityChange>();
        bool reentered = false;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            observed.Add(delta.Change);
            if (reentered || delta.Change is not RuntimeEntityChange.Updated)
                return;
            reentered = true;
            var motion = new WorldSession.EntityMotionUpdate(
                guid,
                new CreateObject.ServerMotionState(0x3d, 0x11),
                InstanceSequence: 1,
                MovementSequence: 2,
                ServerControlSequence: 1,
                IsAutonomous: false);
            Assert.True(lifetime.TryApplyMotion(motion, retainPayload: true, null, out _, out _));
        }));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        // Vector (already queued before the drain started) drains BEFORE
        // the reentrantly-enqueued Motion, and both drain exactly once.
        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.ObjDesc,
                RuntimeInitialCreateExecutedActionKind.Vector,
                RuntimeInitialCreateExecutedActionKind.Movement,
            ],
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(
            [RuntimeEntityChange.Updated, RuntimeEntityChange.Updated, RuntimeEntityChange.Updated],
            observed);
        Assert.Equal(new Vector3(21f, 22f, 23f), canonical.Snapshot.Physics!.Value.Velocity);
        Assert.Equal(new CreateObject.ServerMotionState(0x3d, 0x11), canonical.Snapshot.MotionState);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void RegisterDifferentEntityFromCallbackDuringDrainConvergesBothIndependently()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 64UL);
        const uint firstGuid = 0x70031400u;
        const uint secondGuid = 0x70031500u;
        RuntimeEntityRecord first = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(firstGuid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            first, out RuntimeInitialCreateResidenceLease firstLease));
        var vector = new VectorUpdate.Parsed(
            firstGuid, Vector3.One, Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        RuntimeEntityRecord? secondCanonical = null;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (delta.Change is not RuntimeEntityChange.Updated
                || delta.Entity.Identity.ServerGuid != firstGuid
                || secondCanonical is not null)
            {
                return;
            }
            secondCanonical = lifetime
                .RegisterEntityWithInitialResidence(
                    Spawn(secondGuid, 1, includePosition: false),
                    isLocalPlayer: false)
                .Canonical!;
        }));

        RuntimeInitialCreateExecutionReceipt firstReceipt = RunToCompletion(
            lifetime, first, firstLease.Token, NoContact);

        Assert.NotEmpty(firstReceipt.Trace);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.NotNull(secondCanonical);
        Assert.True(lifetime.Entities.IsCurrent(secondCanonical!));
        Assert.True(lifetime.TryGetInitialCreateResidence(secondCanonical!, out RuntimeInitialCreateResidenceLease secondLease));

        // The second entity's own independent residence still drains
        // normally afterward.
        RuntimeInitialCreateExecutionReceipt secondReceipt = RunToCompletion(
            lifetime, secondCanonical!, secondLease.Token, NoContact);
        Assert.Contains(
            RuntimeInitialCreateExecutedActionKind.InitialAdoption,
            secondReceipt.Trace.Select(static a => a.Kind));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void RecreateSameGuidNewerIncarnationFromCallbackAbandonsOldExecutionWithoutFifoTransfer()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 65UL);
        const uint guid = 0x70031600u;
        RuntimeEntityRecord canonicalGen1 = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonicalGen1, out RuntimeInitialCreateResidenceLease lease));

        var appearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x0B000002u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(appearance, null, out _));
        var vector = new VectorUpdate.Parsed(
            guid, new Vector3(31f, 32f, 33f), Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        bool recreated = false;
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (recreated || delta.Change is not RuntimeEntityChange.Updated)
                return;
            recreated = true;
            _ = lifetime.RegisterEntity(Spawn(guid, 2, includePosition: false));
        }));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonicalGen1, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.Equal(RuntimeInitialCreateExecutionStatus.RejectedAuthority, status);
        Assert.Equal(default, receipt);
        Assert.False(lifetime.Entities.IsCurrent(canonicalGen1));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);

        // The new incarnation is unaffected: no residence pending (a bare
        // RegisterEntity never begins one), and none of generation 1's
        // FIFO (Vector, still undrained) ever transferred to it.
        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord canonicalGen2));
        Assert.Equal(2, canonicalGen2.Incarnation);
        Assert.False(lifetime.TryGetInitialCreateResidence(canonicalGen2, out _));
        Assert.Null(canonicalGen2.Snapshot.Physics!.Value.Velocity);
    }

    [Fact]
    public void DisposeFromCallbackDuringDrainConvergesTheCompleteLedger()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 66UL);
        const uint guid = 0x70031700u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        var vector = new VectorUpdate.Parsed(
            guid, Vector3.One, Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            if (delta.Change is RuntimeEntityChange.Updated)
                lifetime.Dispose();
        }));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.Equal(RuntimeInitialCreateExecutionStatus.RejectedAuthority, status);
        Assert.Equal(default, receipt);
        Assert.True(lifetime.CaptureOwnership().IsConverged);
    }

    // ---------------------------------------------------------------
    // H. Saturation / stale-progress
    // ---------------------------------------------------------------

    [Fact]
    public void StaleProgressLeaseIdIsDiscardedAndFailsClosedThenRetrySucceedsCleanly()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 13UL);
        const uint guid = 0x70027000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        RuntimeEntityKey key = canonical.Key!.Value;

        PlantStaleProgress(lifetime, key, lease.Token.LeaseId + 1_000UL);
        Assert.Equal(1, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(canonical, lease.Token, NoContact, out _));
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);
        Assert.NotEmpty(receipt.Trace);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    private static void PlantStaleProgress(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityKey key,
        ulong staleLeaseId)
    {
        Type progressType = typeof(RuntimeInitialCreateContinuationExecutor)
            .GetNestedType("Progress", System.Reflection.BindingFlags.NonPublic)!;
        object stale = System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(progressType);
        System.Reflection.PropertyInfo leaseIdProperty = progressType.GetProperty(
            "LeaseId",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)!;
        leaseIdProperty.SetValue(stale, staleLeaseId);
        System.Reflection.FieldInfo progressField = typeof(RuntimeInitialCreateContinuationExecutor)
            .GetField(
                "_progress",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!;
        var dictionary = (System.Collections.IDictionary)progressField.GetValue(
            lifetime.InitialCreateExecution)!;
        dictionary[key] = stale;
    }

    [Fact]
    public void AdoptionRevisionSaturationFailsClosedBeforeAnyNewContinuationCanEnqueue()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 70UL);
        const uint guid = 0x70032000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        var firstVector = new VectorUpdate.Parsed(
            guid, Vector3.One, Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(firstVector, null, out _));

        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.InitialCreateResidences.Complete(canonical, lease.Token, out _));
        SetCompletedAdoptionRevision(lifetime, canonical.Key!.Value, ulong.MaxValue);

        var secondVector = new VectorUpdate.Parsed(
            guid, new Vector3(2f, 2f, 2f), Vector3.Zero, InstanceSequence: 1, VectorSequence: 3);
        Assert.False(lifetime.TryApplyVector(secondVector, null, out _));
        Assert.True(lifetime.InitialCreateResidences.TryGetTransaction(
            canonical, out RuntimeInitialCreateResidenceLease afterFailedEnqueue));
        Assert.Single(afterFailedEnqueue.Continuations);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.Vector,
            ],
            receipt.Trace.Select(static a => a.Kind));
        Assert.Equal(Vector3.One, canonical.Snapshot.Physics!.Value.Velocity);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    private static void SetCompletedAdoptionRevision(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityKey key,
        ulong revision)
    {
        Type stateType = typeof(RuntimeInitialCreateResidenceState);
        System.Reflection.FieldInfo completedField = stateType.GetField(
            "_completed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var completed = (System.Collections.IDictionary)completedField.GetValue(
            lifetime.InitialCreateResidences)!;
        object entry = completed[key]!;
        System.Reflection.PropertyInfo receiptProperty = entry.GetType().GetProperty(
            "Receipt",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)!;
        var receipt = (RuntimeInitialCreateResidenceReceipt)receiptProperty.GetValue(entry)!;
        receiptProperty.SetValue(
            entry,
            receipt with { Adoption = receipt.Adoption with { Revision = revision } });
    }


    [Fact]
    public void ExternalPositionAuthorityMutationWithNoPendingPlacementFailsClosedAndConverges()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 16UL);
        const uint guid = 0x70029000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.InitialCreateResidences.Complete(canonical, lease.Token, out _));
        Assert.True(lifetime.InitialCreateResidences.AdoptCompletedPlacement(canonical, lease.Token));

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        lifetime.Entities.AdvancePositionAuthority(canonical);

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
        Assert.Empty(observed);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void ExternalFullCellMutationWithNoPendingPlacementFailsClosedAndConverges()
    {
        using RuntimeEntityObjectLifetime lifetime = new();
        Bind(lifetime, 17UL);
        const uint guid = 0x70029100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));

        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.InitialCreateResidences.Complete(canonical, lease.Token, out _));
        Assert.True(lifetime.InitialCreateResidences.AdoptCompletedPlacement(canonical, lease.Token));

        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta => observed.Add(delta.Change)));

        // FullCellId is the second field the reviewer explicitly named -
        // exercise it independently of PositionAuthorityVersion.
        lifetime.Entities.SetFullCell(canonical, Cell, Landblock);

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
        Assert.Empty(observed);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
    }

    [Fact]
    public void ExternalFullCellMutationDuringAwaitingContinuationPlacementForgetsThePendingPlacementAndAllowsAFreshOneToBegin()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 90UL);
        const uint guid = 0x70034000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 1, forcePositionSequence: 0, positionX: 40f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null, out _, out _, out _));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out _);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);
        RuntimeEntityKey key = canonical.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(key, out RuntimeAuthoritativePositionRoute route));
        CompletePendingContinuationPlacement(lifetime, key, route);

        lifetime.Entities.SetFullCell(canonical, canonical.FullCellId + 999u, Landblock);

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.RejectedAuthority,
            lifetime.InitialCreateExecution.Execute(
                canonical, lease.Token, NoContact, out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(default, receipt);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        RuntimeSetPositionOwnershipSnapshot physicsOwnership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(0, physicsOwnership.ActiveOperationCount);
        Assert.Equal(0, physicsOwnership.AcknowledgedPlacementCompletionCount);

        // A subsequent FRESH authored placement for the SAME entity can
        // begin - proves HasRetainedCompletion no longer blocks it.
        RuntimeEntityPlacementToken fresh = lifetime.Physics.SetPosition
            .TryBeginExclusiveAuthoredPlacement(
                canonical,
                canonical.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative);
        Assert.True(fresh.IsValid);
    }

    [Fact]
    public void ThirdPartyTryGetTransactionRetireWhileAwaitingContinuationPlacementDiscardsExecutorProgressAndForgetsThePendingPlacement()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 91UL);
        const uint guid = 0x70034100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 1, forcePositionSequence: 0, positionX: 40f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null, out _, out _, out _));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out _);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);
        Assert.Equal(1, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        RuntimeEntityKey key = canonical.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationPlacement(key, out RuntimeEntityPlacementToken pendingPlacement));
        Assert.True(lifetime.Physics.SetPosition.IsPlacementCurrent(pendingPlacement));

        lifetime.Entities.AdvancePositionAuthority(canonical);

        Assert.False(lifetime.InitialCreateResidences.TryGetTransaction(canonical, out _));

        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.False(lifetime.Physics.SetPosition.IsPlacementCurrent(pendingPlacement));
        RuntimeSetPositionOwnershipSnapshot physicsOwnership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(0, physicsOwnership.ActiveOperationCount);
        Assert.Equal(0, physicsOwnership.PlacementCompletionWatchCount);
        Assert.Equal(0, physicsOwnership.AcknowledgedPlacementCompletionCount);

        // A subsequent FRESH authored placement for the SAME entity can
        // begin - no lingering block from the forgotten pending placement.
        RuntimeEntityPlacementToken fresh = lifetime.Physics.SetPosition
            .TryBeginExclusiveAuthoredPlacement(
                canonical,
                canonical.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative);
        Assert.True(fresh.IsValid);
    }


    [Fact]
    public void ResetDuringAwaitingContinuationPlacementConvergesEveryLedger()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 14UL);
        const uint guid = 0x70028000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 1, forcePositionSequence: 0, positionX: 40f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null, out _, out _, out _));

        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, NoContact, out _);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);
        Assert.Equal(1, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);

        IReadOnlyList<RuntimeEntityRecord> retirements = lifetime.BeginSessionClear();
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        foreach (RuntimeEntityRecord record in retirements)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
    }

    [Fact]
    public void DisposalConvergesTheCompleteOwnershipLedgerAfterASuccessfulExecution()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 15UL);
        const uint guid = 0x70028100u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        _ = RunToCompletion(lifetime, canonical, lease.Token, NoContact);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);

        lifetime.Dispose();

        Assert.True(lifetime.CaptureOwnership().IsConverged);
    }


    [Fact]
    public void OperationSlotContentionYieldsRetryableWithoutAbandoningThenRetryCompletesWithNoDuplicatePublish()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 80UL);
        const uint guid = 0x70033000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical, out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        Assert.Equal(Cell, canonical.FullCellId);

        var vector = new VectorUpdate.Parsed(
            guid, Vector3.One, Vector3.Zero, InstanceSequence: 1, VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(vector, null, out _));

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0, forcePositionSequence: 0, positionX: 25f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: false, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        RuntimeEntityPlacementToken unrelatedOccupant = default;
        var observed = new List<RuntimeEntityChange>();
        using IDisposable subscription = lifetime.Events.Subscribe(new EntityObserver(delta =>
        {
            observed.Add(delta.Change);
            if (unrelatedOccupant.IsValid || delta.Change is not RuntimeEntityChange.Updated)
                return;
            unrelatedOccupant = lifetime.Physics.SetPosition.TryBeginExclusiveAuthoredPlacement(
                canonical,
                canonical.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
            Assert.True(unrelatedOccupant.IsValid);
        }));

        var inputs = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: 200f);
        RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, inputs, out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, status);
        Assert.Equal(default, receipt);
        Assert.Equal(1, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(1, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(
            [RuntimeEntityChange.Updated, RuntimeEntityChange.Updated],
            observed);
        Assert.Equal(25f, canonical.Snapshot.Position!.Value.PositionX);

        // Free the slot; retry.
        lifetime.Physics.SetPosition.PublishCancellation(
            lifetime.Physics.SetPosition.ForgetExactPlacement(unrelatedOccupant));

        RuntimeInitialCreateExecutionStatus retryStatus = lifetime.InitialCreateExecution.Execute(
            canonical, lease.Token, inputs, out RuntimeInitialCreateExecutionReceipt retryReceipt);
        Assert.Equal(RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement, retryStatus);
        Assert.Equal(default, retryReceipt);
        Assert.Equal(
            [RuntimeEntityChange.Updated, RuntimeEntityChange.Updated],
            observed);

        RuntimeEntityKey key = canonical.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(key, out RuntimeAuthoritativePositionRoute route));
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPositionSimple, route.Disposition);
        CompletePendingContinuationPlacement(lifetime, key, route);

        RuntimeInitialCreateExecutionReceipt finalReceipt = RunToCompletion(
            lifetime, canonical, lease.Token, inputs);

        Assert.Equal(
            [
                RuntimeInitialCreateExecutedActionKind.InitialAdoption,
                RuntimeInitialCreateExecutedActionKind.Vector,
                RuntimeInitialCreateExecutedActionKind.Position,
            ],
            finalReceipt.Trace.Select(static a => a.Kind));
        Assert.Equal(
            [RuntimeEntityChange.Updated, RuntimeEntityChange.Updated],
            observed);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.CaptureOwnership().InitialCreateExecutorProgressCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
    }


    [Fact]
    public void ExecutorCompletion_PublishesOnTheSamePlacementStreamCorrelatedWithTheFullReceipt()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 400UL);
        const uint guid = 0x70024000u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        var observed = new List<RuntimePlacementProjectionSnapshot>();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            new PlacementObserver(delta => observed.Add(delta.Placement)));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        RuntimePlacementProjectionSnapshot completion = Assert.Single(observed);
        Assert.Equal(
            RuntimePlacementProjectionKind.ExecutorCompleted,
            completion.Kind);
        Assert.Equal(canonical.Key, completion.Token.Entity);
        Assert.True(lifetime.InitialCreateExecution.TryGetCompletionReceipt(
            completion.Token,
            out RuntimeInitialCreateExecutionReceipt correlated));
        Assert.Equal(receipt, correlated);

        // Acknowledge-only, exact-head, same as every other Kind.
        Assert.Equal(1, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            completion.Token));
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
    }

    [Fact]
    public void ExecutorCompletion_ObservedOnlyAfterAnyContinuationPlacementInFifoOrder()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 401UL);
        const uint guid = 0x70024001u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 1,
            forcePositionSequence: 0, positionX: 40f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var observed = new List<RuntimePlacementProjectionSnapshot>();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            new PlacementObserver(delta => observed.Add(delta.Placement)));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            [
                RuntimePlacementProjectionKind.Place,
                RuntimePlacementProjectionKind.ExecutorCompleted,
            ],
            observed.Select(static s => s.Kind));
        Assert.Equal(canonical.Key, observed[0].Token.Entity);
        Assert.True(lifetime.InitialCreateExecution.TryGetCompletionReceipt(
            observed[1].Token,
            out RuntimeInitialCreateExecutionReceipt correlated));
        Assert.Equal(receipt, correlated);
        Assert.Equal(1, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            observed[1].Token));
    }


    [Fact]
    public void PlacementChannel_TryGetInitialCreateCompletion_ProjectsHookPhaseCellAndReplayCount()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 420UL);
        const uint guid = 0x70024020u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        var observed = new List<RuntimePlacementProjectionSnapshot>();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            new PlacementObserver(delta => observed.Add(delta.Placement)));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        RuntimePlacementProjectionSnapshot completion = Assert.Single(observed);
        var generation = new RuntimeGenerationToken(420UL);
        Assert.True(lifetime.Placements.TryGetInitialCreateCompletion(
            generation,
            completion.Token,
            out RuntimeInitialCreatePlacementCompletion publicCompletion));
        Assert.Equal(canonical.Key, publicCompletion.Entity);
        Assert.Equal(receipt.Entity, publicCompletion.Entity);
        Assert.Equal(receipt.FullCellId, publicCompletion.FullCellId);
        Assert.Equal(receipt.ReplayedDeferredChildCount, publicCompletion.ReplayedDeferredChildCount);
        Assert.Equal(
            RuntimeInitialCreateTeleportHookPhase.AfterEnterWorld,
            publicCompletion.TeleportHookPhase);
        Assert.Equal(RuntimeTeleportHookPhase.AfterEnterWorld, receipt.TeleportHookPhase);
        Assert.Empty(publicCompletion.PositionRouteFacts);
    }

    [Fact]
    public void PlacementChannel_TryGetInitialCreateCompletion_ProjectsPositionRouteFactsForConstrainInterpolationBinding()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 421UL);
        const uint guid = 0x70024021u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 1,
            forcePositionSequence: 0, positionX: 40f);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        var observed = new List<RuntimePlacementProjectionSnapshot>();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            new PlacementObserver(delta => observed.Add(delta.Placement)));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        RuntimePlacementProjectionSnapshot executorCompletion = observed[^1];
        Assert.Equal(
            RuntimePlacementProjectionKind.ExecutorCompleted,
            executorCompletion.Kind);

        RuntimeInitialCreateExecutedAction internalPositionTrace = Assert.Single(
            receipt.Trace.Where(
                static a => a.Kind == RuntimeInitialCreateExecutedActionKind.Position));

        var generation = new RuntimeGenerationToken(421UL);
        Assert.True(lifetime.Placements.TryGetInitialCreateCompletion(
            generation,
            executorCompletion.Token,
            out RuntimeInitialCreatePlacementCompletion publicCompletion));
        RuntimeInitialCreatePositionRouteFact fact = Assert.Single(
            publicCompletion.PositionRouteFacts);
        Assert.Equal(internalPositionTrace.Sequence, fact.Sequence);
        Assert.Equal(internalPositionTrace.StopInterpolating, fact.StopInterpolating);
        Assert.Equal(internalPositionTrace.ZeroVelocity, fact.ZeroVelocity);
        Assert.Equal(internalPositionTrace.PreserveHeading, fact.PreserveHeading);
        Assert.Equal(
            internalPositionTrace.SendPositionImmediately,
            fact.SendPositionImmediately);
        Assert.Equal(
            internalPositionTrace.PositionDisposition!.Value.ToString(),
            fact.Disposition.ToString());
        Assert.Equal(
            internalPositionTrace.ConstrainPhase.ToString(),
            fact.ConstrainPhase.ToString());
        Assert.Equal(
            internalPositionTrace.HookPhase.ToString(),
            fact.HookPhase.ToString());
    }

    [Fact]
    public void PlacementChannel_TryGetInitialCreateCompletion_RejectsWrongGeneration()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 422UL);
        const uint guid = 0x70024022u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        var observed = new List<RuntimePlacementProjectionSnapshot>();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            new PlacementObserver(delta => observed.Add(delta.Placement)));
        RunToCompletion(lifetime, canonical, lease.Token, NoContact);
        RuntimePlacementProjectionSnapshot completion = Assert.Single(observed);

        Assert.False(lifetime.Placements.TryGetInitialCreateCompletion(
            new RuntimeGenerationToken(999UL),
            completion.Token,
            out RuntimeInitialCreatePlacementCompletion stale));
        Assert.Equal(default, stale);
    }

    [Fact]
    public void PlacementChannel_TryGetInitialCreateCompletion_ReturnsFalseAfterAcknowledgeReapsTheCorrelationEntry()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 423UL);
        const uint guid = 0x70024023u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        RunToCompletion(lifetime, canonical, lease.Token, NoContact);

        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot completion));
        var generation = new RuntimeGenerationToken(423UL);
        Assert.True(lifetime.Placements.TryGetInitialCreateCompletion(
            generation,
            completion.Token,
            out _));

        Assert.True(lifetime.Placements.Acknowledge(generation, completion.Token));

        Assert.False(lifetime.Placements.TryGetInitialCreateCompletion(
            generation,
            completion.Token,
            out RuntimeInitialCreatePlacementCompletion afterAck));
        Assert.Equal(default, afterAck);
    }

    [Fact]
    public void EnumProjectionMapsHaveEqualArityAndEveryInternalValueRoundTripsByName()
    {
        AssertMapIsCompleteAndNamePreserving(
            typeof(RuntimeTeleportHookPhase),
            typeof(RuntimeInitialCreateTeleportHookPhase),
            "MapHookPhase");
        AssertMapIsCompleteAndNamePreserving(
            typeof(RuntimeAuthoritativePositionDisposition),
            typeof(RuntimeInitialCreatePositionDisposition),
            "MapDisposition");
        AssertMapIsCompleteAndNamePreserving(
            typeof(RuntimePositionConstrainPhase),
            typeof(RuntimeInitialCreatePositionConstrainPhase),
            "MapConstrainPhase");
    }

    private static void AssertMapIsCompleteAndNamePreserving(
        Type internalEnumType,
        Type publicEnumType,
        string mapMethodName)
    {
        MethodInfo? method = typeof(RuntimeInitialCreateContinuationExecutor)
            .GetMethod(
                mapMethodName,
                BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(
            method is not null,
            $"{nameof(RuntimeInitialCreateContinuationExecutor)} no longer " +
            $"declares a private static method named {mapMethodName} - " +
            "update this test's reflection lookup to match.");

        Array internalValues = Enum.GetValues(internalEnumType);
        Array publicValues = Enum.GetValues(publicEnumType);
        Assert.True(
            internalValues.Length == publicValues.Length,
            $"{internalEnumType.Name} has {internalValues.Length} values " +
            $"but {publicEnumType.Name} has {publicValues.Length} - keep " +
            "the internal/public enum pair in lockstep.");

        foreach (object? internalValue in internalValues)
        {
            object? mapped = method!.Invoke(null, [internalValue]);
            Assert.Equal(internalValue!.ToString(), mapped!.ToString());
        }
    }

    [Fact]
    public void ExecutorCompletion_ReceiptIsReadableFromWithinTheSameSynchronousOnPlacementDispatch()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 410UL);
        const uint guid = 0x70024010u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        RuntimeInitialCreateExecutionReceipt? observedFromInsideDispatch = null;
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            new PlacementObserver(delta =>
            {
                if (delta.Placement.Kind
                    is not RuntimePlacementProjectionKind.ExecutorCompleted)
                {
                    return;
                }
                Assert.True(lifetime.InitialCreateExecution.TryGetCompletionReceipt(
                    delta.Placement.Token,
                    out RuntimeInitialCreateExecutionReceipt receipt));
                observedFromInsideDispatch = receipt;
            }));

        RuntimeInitialCreateExecutionReceipt receiptReturned = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);

        Assert.NotNull(observedFromInsideDispatch);
        Assert.Equal(receiptReturned, observedFromInsideDispatch!.Value);
    }

    [Fact]
    public void ExecutorCompletion_ConvergenceLedgerCountsAnUnacknowledgedReceiptAsOutstandingDebtUntilAcknowledged()
    {
        // F2: PendingCompletionReceiptCount mirrors
        // RuntimeSetPositionOwnershipSnapshot.PendingProjectionAcknowledgementCount's
        // existing "unacknowledged receipt is outstanding debt" shape for
        // the SAME underlying receipt stream - non-zero while unacknowledged,
        // reaped to zero exactly on acknowledge (never before, never left
        // dangling after).
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 411UL);
        const uint guid = 0x70024011u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        Assert.Equal(
            0,
            lifetime.CaptureOwnership().PendingCompletionReceiptCount);

        RunToCompletion(lifetime, canonical, lease.Token, NoContact);

        Assert.Equal(
            1,
            lifetime.CaptureOwnership().PendingCompletionReceiptCount);
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot completion));
        Assert.Equal(
            RuntimePlacementProjectionKind.ExecutorCompleted,
            completion.Kind);

        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            completion.Token));

        Assert.Equal(
            0,
            lifetime.CaptureOwnership().PendingCompletionReceiptCount);
        Assert.False(lifetime.InitialCreateExecution.TryGetCompletionReceipt(
            completion.Token,
            out _));
    }

    [Fact]
    public void ExecutorCompletion_CorrelationEntryIsReapedByDiscardProgress()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 413UL);
        const uint guid = 0x70024013u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        RunToCompletion(lifetime, canonical, lease.Token, NoContact);
        Assert.Equal(
            1,
            lifetime.CaptureOwnership().PendingCompletionReceiptCount);

        lifetime.InitialCreateExecution.DiscardProgress(canonical.Key!.Value);

        Assert.Equal(
            0,
            lifetime.CaptureOwnership().PendingCompletionReceiptCount);
    }

    [Fact]
    public void ExecutorCompletion_CorrelationEntryIsReapedByDiscardAll()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 414UL);
        const uint guid = 0x70024014u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);
        RunToCompletion(lifetime, canonical, lease.Token, NoContact);
        Assert.Equal(
            1,
            lifetime.CaptureOwnership().PendingCompletionReceiptCount);

        lifetime.InitialCreateExecution.DiscardAll();

        Assert.Equal(
            0,
            lifetime.CaptureOwnership().PendingCompletionReceiptCount);
    }

    [Fact]
    public void BindLiveInputs_DrivesClassificationFromTheBoundSourcesInsteadOfTheCallerStruct()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 402UL);
        const uint guid = 0x70024002u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        CompleteInitialPlacement(lifetime, lease);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0,
            forcePositionSequence: 0, positionX: 15f, isGrounded: true);
        Assert.True(lifetime.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        bool usePositionFromServer = true;
        // Position 15 units from a local player parked far away (100 units)
        // is irrelevant here (PlayerDistance only matters for the
        // Remote/Projectile near/far branch, not LocalPlayer's own
        // interpolate gate) - it exists purely to prove the DISTANCE source
        // is read at all.
        lifetime.InitialCreateExecution.BindLiveInputs(
            () => usePositionFromServer,
            () => new Vector3(115f, 20f, 7f));

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            lifetime, canonical, lease.Token, NoContact);
        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.Interpolate,
            positionAction.PositionDisposition);

        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot firstCompletion));
        Assert.Equal(
            RuntimePlacementProjectionKind.ExecutorCompleted,
            firstCompletion.Kind);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            firstCompletion.Token));

        usePositionFromServer = false;
        const uint secondGuid = 0x70024003u;
        RuntimeEntityRecord second = lifetime
            .RegisterEntityWithInitialResidence(Spawn(secondGuid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            second,
            out RuntimeInitialCreateResidenceLease secondLease));
        AttachDormantBody(lifetime, second);
        CompleteInitialPlacement(lifetime, secondLease);
        WorldSession.EntityPositionUpdate secondUpdate = PositionUpdate(
            secondGuid, positionSequence: 2, teleportSequence: 0,
            forcePositionSequence: 0, positionX: 16f, isGrounded: true);
        Assert.True(lifetime.TryApplyPosition(
            secondUpdate, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition secondDisposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, secondDisposition);
        RuntimeInitialCreateExecutionReceipt secondReceipt = RunToCompletion(
            lifetime, second, secondLease.Token, NoContact);
        RuntimeInitialCreateExecutedAction secondPositionAction = Assert.Single(
            secondReceipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.NoPositionOperation,
            secondPositionAction.PositionDisposition);
    }

    [Fact]
    public void BindLiveInputs_ThrowsOnASecondBindAndUnboundExecutorsUseTheCallerStructUnchanged()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        lifetime.InitialCreateExecution.BindLiveInputs(
            static () => true, static () => Vector3.Zero);
        Assert.Throws<InvalidOperationException>(() =>
            lifetime.InitialCreateExecution.BindLiveInputs(
                static () => false, static () => Vector3.Zero));

        using RuntimeEntityObjectLifetime unbound = EngineLifetime();
        Bind(unbound, 403UL);
        const uint guid = 0x70024004u;
        RuntimeEntityRecord canonical = unbound
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), isLocalPlayer: true)
            .Canonical!;
        Assert.True(unbound.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(unbound, canonical);
        CompleteInitialPlacement(unbound, lease);
        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, positionSequence: 2, teleportSequence: 0,
            forcePositionSequence: 0, positionX: 15f, isGrounded: true);
        Assert.True(unbound.TryApplyPosition(
            update, isLocalPlayer: true, null, null, null,
            out PositionTimestampDisposition disposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        RuntimeInitialCreateExecutionReceipt receipt = RunToCompletion(
            unbound, canonical, lease.Token, NoContact);
        RuntimeInitialCreateExecutedAction positionAction = Assert.Single(
            receipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        // NoContact (UsePositionFromServer:false) -> not interpolated.
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.NoPositionOperation,
            positionAction.PositionDisposition);
    }

    [Fact]
    public void BindLiveInputs_PlayerDistanceIsReadFromANonNullBoundSourceAndFallsBackToTheCallerStructWhenNull()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 421UL);
        Vector3? boundPosition = new Vector3(30f, 20f, 7f);
        lifetime.InitialCreateExecution.BindLiveInputs(
            static () => false,
            () => boundPosition);

        const uint nearGuid = 0x70024021u;
        RuntimeEntityRecord near = lifetime
            .RegisterEntityWithInitialResidence(Spawn(nearGuid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            near, out RuntimeInitialCreateResidenceLease nearLease));
        AttachDormantBody(lifetime, near);
        CompleteInitialPlacement(lifetime, nearLease);
        WorldSession.EntityPositionUpdate nearUpdate = PositionUpdate(
            nearGuid, positionSequence: 2, teleportSequence: 0,
            forcePositionSequence: 0, positionX: 25f, isGrounded: true);
        Assert.True(lifetime.TryApplyPosition(
            nearUpdate, isLocalPlayer: false, null, null, null,
            out PositionTimestampDisposition nearDisposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, nearDisposition);
        var farStruct = new RuntimeInitialCreateExecutionInputs(
            UsePositionFromServer: false, PlayerDistance: 200f);

        RuntimeInitialCreateExecutionReceipt nearReceipt = RunToCompletion(
            lifetime, near, nearLease.Token, farStruct);

        RuntimeInitialCreateExecutedAction nearAction = Assert.Single(
            nearReceipt.Trace,
            static a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.Interpolate,
            nearAction.PositionDisposition);

        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot nearCompletion));
        Assert.Equal(
            RuntimePlacementProjectionKind.ExecutorCompleted,
            nearCompletion.Kind);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            nearCompletion.Token));

        boundPosition = null;
        const uint farGuid = 0x70024022u;
        RuntimeEntityRecord far = lifetime
            .RegisterEntityWithInitialResidence(Spawn(farGuid, 1), isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            far, out RuntimeInitialCreateResidenceLease farLease));
        AttachDormantBody(lifetime, far);
        CompleteInitialPlacement(lifetime, farLease);
        WorldSession.EntityPositionUpdate farUpdate = PositionUpdate(
            farGuid, positionSequence: 2, teleportSequence: 0,
            forcePositionSequence: 0, positionX: 25f, isGrounded: true);
        Assert.True(lifetime.TryApplyPosition(
            farUpdate, isLocalPlayer: false, null, null, null,
            out PositionTimestampDisposition farDisposition, out _, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, farDisposition);

        RuntimeInitialCreateExecutionStatus farStatus = lifetime
            .InitialCreateExecution.Execute(
                far, farLease.Token, farStruct, out _);

        Assert.Equal(
            RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement,
            farStatus);
        RuntimeEntityKey farKey = far.Key!.Value;
        Assert.True(lifetime.InitialCreateExecution.TryGetPendingContinuationRoute(
            farKey, out RuntimeAuthoritativePositionRoute farRoute));
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            farRoute.Disposition);
        Assert.True(farRoute.StopInterpolating);
    }

    // ---------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------

    private static RuntimeEntityObjectLifetime EngineLifetime()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return new RuntimeEntityObjectLifetime(engine);
    }

    private static RuntimeInitialCreateExecutionReceipt RunToCompletion(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord canonical,
        in RuntimeInitialCreateResidenceToken token,
        RuntimeInitialCreateExecutionInputs inputs,
        int maxSteps = 25)
    {
        for (int step = 0; step < maxSteps; step++)
        {
            RuntimeInitialCreateExecutionStatus status = lifetime.InitialCreateExecution.Execute(
                canonical, token, inputs, out RuntimeInitialCreateExecutionReceipt receipt);
            switch (status)
            {
                case RuntimeInitialCreateExecutionStatus.Completed:
                    return receipt;
                case RuntimeInitialCreateExecutionStatus.AwaitingContinuationPlacement:
                {
                    RuntimeEntityKey key = canonical.Key!.Value;
                    Assert.True(lifetime.InitialCreateExecution
                        .TryGetPendingContinuationRoute(key, out RuntimeAuthoritativePositionRoute route));
                    CompletePendingContinuationPlacement(lifetime, key, route);
                    continue;
                }
                default:
                    throw new InvalidOperationException(
                        $"RunToCompletion hit unexpected status {status}; drive its precondition explicitly instead.");
            }
        }
        throw new InvalidOperationException("RunToCompletion exceeded its step budget.");
    }

    private static void CompleteInitialPlacement(
        RuntimeEntityObjectLifetime lifetime,
        in RuntimeInitialCreateResidenceLease lease)
    {
        RuntimeSetPositionCommand command = Prepare(
            lifetime,
            lease.Placement,
            lease.Route.OperationKind,
            lease.Route.SetPositionFlags);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(lease.Placement, command);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(outcome.Projection));
    }

    private static void CompletePendingContinuationPlacement(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityKey key,
        in RuntimeAuthoritativePositionRoute route)
    {
        Assert.True(lifetime.InitialCreateExecution
            .TryGetPendingContinuationPlacement(key, out RuntimeEntityPlacementToken placement));
        RuntimeSetPositionCommand command = Prepare(
            lifetime, placement, route.OperationKind, route.SetPositionFlags);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(placement, command);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(outcome.Projection));
    }

    private static RuntimeSetPositionCommand Prepare(
        RuntimeEntityObjectLifetime lifetime,
        in RuntimeEntityPlacementToken placement,
        RuntimeSetPositionOperationKind operationKind,
        PhysicsSetPositionFlags flags)
    {
        var preparation = new RuntimeSetPositionMoverPreparation(
            RuntimeSetPositionMoverSetup.ResolvedAbsent,
            operationKind,
            GameTime: 1d,
            PhysicsPlacementClass.Ordinary,
            flags);
        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                placement, preparation, out RuntimeSetPositionCommand command));
        return command;
    }

    private static void AttachDormantBody(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord canonical,
        bool inContact = false)
    {
        var body = new PhysicsBody
        {
            State = canonical.FinalPhysicsState,
            Orientation = Quaternion.Identity,
            InWorld = false,
            TransientState = inContact ? TransientStateFlags.Contact : TransientStateFlags.None,
        };
        lifetime.Entities.SetPhysicsBody(canonical, body);
    }

    private static void ForceContact(RuntimeEntityRecord canonical, bool inContact)
    {
        if (canonical.PhysicsBody is not { } body)
            return;
        body.TransientState = inContact
            ? body.TransientState | TransientStateFlags.Contact
            : body.TransientState & ~TransientStateFlags.Contact;
    }

    private static void Bind(RuntimeEntityObjectLifetime lifetime, ulong generation)
    {
        var token = new RuntimeGenerationToken(generation);
        lifetime.BindEventContext(() => token, static () => 1UL);
    }

    private static WorldSession.EntityPositionUpdate PositionUpdate(
        uint guid,
        ushort positionSequence,
        ushort teleportSequence,
        ushort forcePositionSequence,
        float positionX,
        bool isGrounded = true)
    {
        return new WorldSession.EntityPositionUpdate(
            guid,
            new CreateObject.ServerPosition(Cell, positionX, 20f, 7f, 1f, 0f, 0f, 0f),
            new Vector3(positionSequence, 2f, 3f),
            PlacementId: positionSequence,
            IsGrounded: isGrounded,
            InstanceSequence: 1,
            PositionSequence: positionSequence,
            TeleportSequence: teleportSequence,
            ForcePositionSequence: forcePositionSequence);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort incarnation,
        bool includePosition = true,
        uint? parentGuid = null,
        ushort positionSequence = 1,
        float positionX = 10f,
        bool missile = false,
        ushort teleportSequence = 0,
        ushort forcePositionSequence = 0,
        ushort movementSequence = 1,
        ushort serverControlSequence = 1)
    {
        CreateObject.ServerPosition? position = includePosition
            ? new CreateObject.ServerPosition(Cell, positionX, 20f, 7f, 1f, 0f, 0f, 0f)
            : null;
        uint rawState = (uint)(PhysicsStateFlags.Gravity
            | (missile ? PhysicsStateFlags.Missile : 0));
        var timestamps = new PhysicsTimestamps(
            Position: positionSequence,
            Movement: movementSequence,
            State: 1,
            Vector: 1,
            Teleport: teleportSequence,
            ServerControlledMove: serverControlSequence,
            ForcePosition: forcePositionSequence,
            ObjDesc: 1,
            Instance: incarnation);
        var physics = new PhysicsSpawnData(
            rawState,
            position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: null,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: parentGuid is { } parent ? new PhysicsAttachment(parent, 1u) : null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            SetupTableId: null,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "initial-create",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: rawState,
            InstanceSequence: incarnation,
            MovementSequence: timestamps.Movement,
            ServerControlSequence: timestamps.ServerControlledMove,
            PositionSequence: positionSequence,
            ParentGuid: parentGuid,
            ParentLocation: parentGuid is null ? null : 1u,
            Physics: physics);
    }

    private sealed class EntityObserver(Action<RuntimeEntityDelta> onEntity)
        : IRuntimeEntityObjectObserver
    {
        public void OnEntity(in RuntimeEntityDelta delta) => onEntity(delta);
        public void OnInventory(in RuntimeInventoryDelta delta)
        {
        }
    }

    private sealed class PlacementObserver(
        Action<RuntimePlacementDelta> onPlacement)
        : IRuntimePlacementObserver
    {
        public void OnPlacement(in RuntimePlacementDelta delta) =>
            onPlacement(delta);
    }
}
