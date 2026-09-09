using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeInitialCreateResidenceStateTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;

    [Theory]
    [InlineData(true, false,
        RuntimeSetPositionOperationKind.InitialLogin,
        RuntimeTeleportHookPhase.AfterEnterWorld)]
    [InlineData(false, false,
        RuntimeSetPositionOperationKind.RemoteAuthoritative,
        RuntimeTeleportHookPhase.None)]
    [InlineData(false, true,
        RuntimeSetPositionOperationKind.ProjectileAuthoritative,
        RuntimeTeleportHookPhase.None)]
    internal void TopLevelRegistrationOwnsExactCelllessLeaseBeforeObservers(
        bool isLocalPlayer,
        bool missile,
        RuntimeSetPositionOperationKind expectedOperation,
        RuntimeTeleportHookPhase expectedHook)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 7UL);
        RuntimeInitialCreateResidenceLease observed = default;
        uint observedCell = uint.MaxValue;
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta =>
            {
                if (delta.Change is not RuntimeEntityChange.Registered)
                    return;
                Assert.True(lifetime.Entities.TryGetByLocalId(
                    delta.Entity.Identity.LocalEntityId,
                    out RuntimeEntityRecord record));
                observedCell = record.FullCellId;
                Assert.True(lifetime.TryGetInitialCreateResidence(
                    record,
                    out observed));
            }));

        RuntimeEntityRegistrationResult registration = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003001u, 1, missile: missile),
                isLocalPlayer);
        RuntimeEntityRecord canonical = registration.Canonical!;

        Assert.Equal(0u, observedCell);
        Assert.Equal(0u, canonical.FullCellId);
        Assert.Equal(Cell, canonical.Snapshot.Position!.Value.LandblockId);
        Assert.True(observed.IsValid);
        Assert.Equal(observed.Token.Entity, observed.Route.Authority.Entity);
        Assert.Equal(expectedOperation, observed.Route.OperationKind);
        Assert.Equal(expectedHook, observed.Route.TeleportHookPhase);
        Assert.Equal(0x11u, (uint)observed.Route.SetPositionFlags);
        Assert.True(observed.Placement.IsValid);
        Assert.Equal(1, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FreshParentedAndPickedCreateRemainCelllessWithoutWithdrawal(
        bool parented)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 8UL);
        if (parented)
            _ = lifetime.RegisterEntity(Spawn(0x70004000u, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    0x70003002u,
                    1,
                    includePosition: false,
                    parentGuid: parented ? 0x70004000u : null),
                isLocalPlayer: false)
            .Canonical!;

        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(0u, canonical.FullCellId);
        Assert.False(lease.Placement.IsValid);
        Assert.Equal(RuntimeAuthoritativePositionDisposition.AwaitFreshPosition,
            lease.Route.Disposition);
        Assert.False(lease.Route.LeaveWorld);
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt receipt));
        Assert.Equal(0u, receipt.FullCellId);
        Assert.Equal(RuntimeTeleportHookPhase.None,
            receipt.TeleportHookPhase);
        Assert.True(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            receipt.Adoption));
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
    }

    [Fact]
    public void MissingCellDefersExactLeaseWithoutCommittingWireResidence()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 9UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003003u, 1, setupId: null),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionCommand command = Prepare(
            lifetime,
            lease,
            RuntimeSetPositionMoverSetup.ResolvedAbsent);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(lease.Placement, command);

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, outcome.Status);
        Assert.Equal(0u, canonical.FullCellId);
        Assert.Equal(Cell, canonical.Snapshot.Position!.Value.LandblockId);
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.PendingPlacement,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out _));
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease retained));
        Assert.Equal(lease, retained);
    }

    [Theory]
    [InlineData(true, RuntimeTeleportHookPhase.AfterEnterWorld)]
    [InlineData(false, RuntimeTeleportHookPhase.None)]
    internal void CommittedAndAcknowledgedCreateYieldsExactEnterWorldReceipt(
        bool isLocalPlayer,
        RuntimeTeleportHookPhase expectedHook)
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 10UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003004u, 1, setupId: null),
                isLocalPlayer)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionCommand command = Prepare(
            lifetime,
            lease,
            RuntimeSetPositionMoverSetup.ResolvedAbsent);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(lease.Placement, command);

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(Cell, canonical.FullCellId);
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.PendingPlacement,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out _));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt receipt));
        Assert.Equal(expectedHook, receipt.TeleportHookPhase);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Equal(canonical.PlacementCommitVersion,
            receipt.PlacementCommitVersion);
        Assert.True(receipt.Adoption.IsValid);
        Assert.Empty(receipt.Continuations);
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt repeated));
        Assert.Equal(receipt, repeated);
        Assert.False(lifetime.Physics.SetPosition
            .TryBeginExclusiveAuthoredPlacement(
                canonical,
                canonical.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative)
            .IsValid);
        Assert.True(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            receipt.Adoption));
        RuntimeEntityPlacementToken afterAdoption = lifetime.Physics.SetPosition
            .TryBeginExclusiveAuthoredPlacement(
                canonical,
                canonical.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(afterAdoption.IsValid);
        _ = lifetime.Physics.SetPosition.ForgetExactPlacement(afterAdoption);
        Assert.False(lifetime.TryGetInitialCreateResidence(
            canonical,
            out _));
    }

    [Fact]
    public void GenerationReplacementForgetsOldLeaseAndOwnsOnlySuccessor()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 11UL);
        RuntimeEntityRecord first = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003005u, 1),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            first,
            out RuntimeInitialCreateResidenceLease oldLease));

        RuntimeEntityRecord second = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003005u, 2),
                isLocalPlayer: false)
            .Canonical!;

        Assert.False(lifetime.TryGetInitialCreateResidence(first, out _));
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.RejectedToken,
            lifetime.CompleteInitialCreateResidence(
                first,
                oldLease.Token,
                out _));
        Assert.True(lifetime.TryGetInitialCreateResidence(
            second,
            out RuntimeInitialCreateResidenceLease successor));
        Assert.NotEqual(oldLease.Token, successor.Token);
        Assert.Equal(1, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
    }

    [Fact]
    public void RegistrationObserverReentryCannotResurrectOuterLease()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 12UL);
        RuntimeEntityRegistrationResult nested = default;
        bool reentered = false;
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta =>
            {
                if (reentered
                    || delta.Change is not RuntimeEntityChange.Registered)
                    return;
                reentered = true;
                nested = lifetime.RegisterEntityWithInitialResidence(
                    Spawn(0x70003006u, 2),
                    isLocalPlayer: false);
            }));

        RuntimeEntityRegistrationResult outer = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003006u, 1),
                isLocalPlayer: false);

        Assert.Equal(CreateObjectTimestampDisposition.StaleGeneration,
            outer.Inbound.Disposition);
        RuntimeEntityRecord current = nested.Canonical!;
        Assert.True(lifetime.Entities.IsCurrent(current));
        Assert.Equal((ushort)2, current.Incarnation);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            current,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(current.Key, lease.Token.Entity);
        Assert.Equal(1, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
    }

    [Fact]
    public void DeleteResetAndDisposeConvergeEveryInitialResidenceOwner()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 13UL);
        RuntimeEntityRecord deleted = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003007u, 1),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(deleted.ServerGuid, deleted.Incarnation),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(lifetime.RetireCanonicalOnly(deleted));

        _ = lifetime.RegisterEntityWithInitialResidence(
            Spawn(0x70003008u, 1),
            isLocalPlayer: false);
        IReadOnlyList<RuntimeEntityRecord> retirements =
            lifetime.BeginSessionClear();
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        foreach (RuntimeEntityRecord record in retirements)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());

        lifetime.Dispose();
        Assert.True(lifetime.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void ResetCancelsCompletedUnadoptedContinuationBatch()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 32UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x7000301Eu, 1, setupId: null),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                lease.Placement,
                Prepare(
                    lifetime,
                    lease,
                    RuntimeSetPositionMoverSetup.ResolvedAbsent));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt receipt));
        Assert.Equal(1, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);

        IReadOnlyList<RuntimeEntityRecord> retirements =
            lifetime.BeginSessionClear();

        Assert.False(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            receipt.Adoption));
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        RuntimeSetPositionOwnershipSnapshot ownership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(0, ownership.ActiveOperationCount);
        Assert.Equal(0, ownership.PlacementCompletionWatchCount);
        Assert.Equal(0, ownership.AcknowledgedPlacementCompletionCount);
        foreach (RuntimeEntityRecord record in retirements)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
    }

    [Fact]
    public void PostCompletionRebucketRejectsTopLevelAdoptionAndConverges()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 33UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x7000301Fu, 1, setupId: null),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                lease.Placement,
                Prepare(
                    lifetime,
                    lease,
                    RuntimeSetPositionMoverSetup.ResolvedAbsent));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt receipt));

        lifetime.Entities.SetFullCell(
            canonical,
            Landblock | 0x0002u,
            Landblock);

        Assert.False(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            receipt.Adoption));
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        RuntimeSetPositionOwnershipSnapshot ownership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(0, ownership.ActiveOperationCount);
        Assert.Equal(0, ownership.PlacementCompletionWatchCount);
        Assert.Equal(0, ownership.AcknowledgedPlacementCompletionCount);
    }

    [Fact]
    public void PostCompletionAuthorityChangeRejectsCelllessAdoption()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 34UL);
        _ = lifetime.RegisterEntity(Spawn(0x70004020u, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    0x70003020u,
                    1,
                    includePosition: false,
                    parentGuid: 0x70004020u),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt receipt));

        lifetime.Entities.AdvancePositionAuthority(canonical);

        Assert.False(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            receipt.Adoption));
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.True(lifetime.Physics.SetPosition.CaptureOwnership()
            .IsConverged);
    }

    [Fact]
    public void CelllessPendingAdoptionRevisionsForFreshPositionWithoutLeak()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 36UL);
        const uint guid = 0x70003024u;
        _ = lifetime.RegisterEntity(Spawn(0x70004024u, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    guid,
                    1,
                    includePosition: false,
                    parentGuid: 0x70004024u),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        ulong positionAuthority = canonical.PositionAuthorityVersion;
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt original));
        int callbacks = 0;
        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid,
            positionSequence: 2,
            teleportSequence: 1,
            forcePositionSequence: 0,
            positionX: 25f);

        Assert.True(lifetime.TryApplyPosition(
            update,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: _ => callbacks++,
            out PositionTimestampDisposition disposition,
            out _,
            out _));

        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.Equal(0, callbacks);
        Assert.Equal(positionAuthority, canonical.PositionAuthorityVersion);
        Assert.Null(canonical.Snapshot.Position);
        Assert.Equal(0u, canonical.FullCellId);
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt revised));
        Assert.Equal(original.Adoption.Revision + 1UL,
            revised.Adoption.Revision);
        RuntimeInitialCreateResidenceContinuation continuation =
            Assert.Single(revised.Continuations);
        RuntimeInitialCreateTailAction position = PositionAction(continuation);
        Assert.Equal(25f, position.Position!.Value.Position.PositionX);
        Assert.Equal((ushort)1, position.AcceptedTimestamps.Teleport);
        Assert.False(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            original.Adoption));
        Assert.False(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            revised.Adoption));
        Assert.Equal(1, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        ConvergeSessionClear(lifetime);
    }

    [Fact]
    public void ResetSnapshotsAllResidenceOwnersBeforeReentrantDiscardObserver()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 35UL);
        RuntimeEntityRecord completedRecord = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    0x70003023u,
                    1,
                    setupId: null,
                    positionX: 70f),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            completedRecord,
            out RuntimeInitialCreateResidenceLease completedLease));
        AttachDormantBody(lifetime, completedRecord);
        RuntimeSetPositionOutcome completedOutcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                completedLease.Placement,
                Prepare(
                    lifetime,
                    completedLease,
                    RuntimeSetPositionMoverSetup.ResolvedAbsent));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            completedOutcome.Projection));
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                completedRecord,
                completedLease.Token,
                out _));

        var pending = new List<RuntimeSetPositionOutcome>();
        uint[] pendingGuids = [0x70003021u, 0x70003022u];
        for (int index = 0; index < pendingGuids.Length; index++)
        {
            uint guid = pendingGuids[index];
            RuntimeEntityRecord record = lifetime
                .RegisterEntityWithInitialResidence(
                    Spawn(
                        guid,
                        1,
                        setupId: null,
                        positionX: 10f + index * 30f),
                    isLocalPlayer: false)
                .Canonical!;
            Assert.True(lifetime.TryGetInitialCreateResidence(
                record,
                out RuntimeInitialCreateResidenceLease lease));
            AttachDormantBody(lifetime, record);
            pending.Add(lifetime.Physics.SetPosition.SubmitPreparedPlacement(
                lease.Placement,
                Prepare(
                    lifetime,
                    lease,
                    RuntimeSetPositionMoverSetup.ResolvedAbsent)));
        }

        var discards = new List<ulong>();
        int nestedRetirementCount = -1;
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            new PlacementObserver(delta =>
            {
                if (delta.Placement.Kind
                    is not RuntimePlacementProjectionKind.Discard)
                {
                    return;
                }
                discards.Add(delta.Placement.Token.Sequence);
                if (nestedRetirementCount < 0)
                {
                    nestedRetirementCount =
                        lifetime.BeginSessionClear().Count;
                }
            }));

        IReadOnlyList<RuntimeEntityRecord> retirements =
            lifetime.BeginSessionClear();

        Assert.Equal(0, nestedRetirementCount);
        Assert.Equal(
            pending.Select(static item => item.Projection.Sequence)
                .Order(),
            discards.Order());
        Assert.Equal(0, lifetime.Events.DispatchFailureCount);
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        foreach (RuntimeEntityRecord record in retirements)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
    }

    [Fact]
    public void InvalidGenerationFailsBeforeInboundOrCanonicalAcceptance()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        ulong generation = 0UL;
        lifetime.BindEventContext(
            () => new RuntimeGenerationToken(generation),
            static () => 1UL);
        WorldSession.EntitySpawn spawn = Spawn(0x70003009u, 1);

        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(spawn, isLocalPlayer: false));
        Assert.Equal(0, lifetime.Entities.Count);
        Assert.Equal(0, lifetime.Entities.ClaimedLocalIdCount);
        Assert.Empty(lifetime.Entities.Snapshots);
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.True(lifetime.Physics.SetPosition.CaptureOwnership()
            .IsConverged);

        generation = 14UL;
        RuntimeEntityRegistrationResult recovered = lifetime
            .RegisterEntityWithInitialResidence(spawn, isLocalPlayer: false);
        Assert.Equal(CreateObjectTimestampDisposition.InitialGeneration,
            recovered.Inbound.Disposition);
        Assert.NotNull(recovered.Canonical);
    }

    [Fact]
    public void UnboundGenerationFailsWithoutFabricatingSessionAuthority()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();

        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003014u, 1),
                isLocalPlayer: false));

        Assert.Empty(lifetime.Entities.Snapshots);
        Assert.Equal(0, lifetime.Entities.Count);
        Assert.Equal(0, lifetime.Entities.ClaimedLocalIdCount);
    }

    [Theory]
    [InlineData((ushort)1)]
    [InlineData((ushort)2)]
    public void EqualOrStaleMalformedCreateDoesNotDisplaceCurrentAdmission(
        ushort currentIncarnation)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 24UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003015u, currentIncarnation),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease initial));
        ushort incomingIncarnation = 1;

        RuntimeEntityRegistrationResult result = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    0x70003015u,
                    incomingIncarnation,
                    positionX: float.NaN),
                isLocalPlayer: false);

        Assert.Equal(
            currentIncarnation == incomingIncarnation
                ? CreateObjectTimestampDisposition.ExistingGeneration
                : CreateObjectTimestampDisposition.StaleGeneration,
            result.Inbound.Disposition);
        Assert.True(lifetime.Entities.IsCurrent(canonical));
        Assert.Equal(10f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease retained));
        Assert.Equal(initial, retained);
    }

    [Fact]
    public void MalformedInitialPositionDoesNotPoisonSameInstanceRecovery()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 15UL);
        WorldSession.EntitySpawn malformed = Spawn(
            0x7000300Au,
            1,
            positionX: float.NaN);

        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(
                malformed,
                isLocalPlayer: false));
        Assert.Empty(lifetime.Entities.Snapshots);
        Assert.Equal(0, lifetime.Entities.Count);

        WorldSession.EntitySpawn corrected = Spawn(0x7000300Au, 1);
        RuntimeEntityRegistrationResult recovered = lifetime
            .RegisterEntityWithInitialResidence(
                corrected,
                isLocalPlayer: false);
        Assert.Equal(CreateObjectTimestampDisposition.InitialGeneration,
            recovered.Inbound.Disposition);
        Assert.Equal(corrected.Position,
            recovered.Canonical!.Snapshot.Position);
        Assert.Equal(0u, recovered.Canonical.FullCellId);
    }

    [Fact]
    public void ExistingPlacementCollisionCannotStealOrPartiallyOwnLease()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 16UL);
        RuntimeEntityRegistrationResult registration = lifetime.RegisterEntity(
            Spawn(0x7000300Bu, 1));
        RuntimeEntityRecord canonical = registration.Canonical!;
        lifetime.Entities.SetFullCell(canonical, 0u, 0u);
        lifetime.Entities.AdvancePositionAuthority(canonical);
        RuntimeEntityPlacementToken conflict = lifetime.Physics.SetPosition
            .BeginAuthoredPlacement(
                canonical,
                canonical.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(conflict.IsValid);

        RuntimeInitialCreateResidenceLease rejected =
            lifetime.InitialCreateResidences.Begin(
                canonical,
                registration.Inbound,
                isLocalPlayer: false);

        Assert.False(rejected.IsValid);
        Assert.True(lifetime.Physics.SetPosition.IsPlacementCurrent(conflict));
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .PlacementCompletionWatchCount);
    }

    [Fact]
    public void ParentPrecedesCombinedWirePositionAndCreatesNoWorldPlacement()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 17UL);
        _ = lifetime.RegisterEntity(Spawn(0x70004001u, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    0x7000300Cu,
                    1,
                    includePosition: true,
                    parentGuid: 0x70004001u),
                isLocalPlayer: false)
            .Canonical!;

        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(RuntimeAuthoritativePositionDisposition.AwaitFreshPosition,
            lease.Route.Disposition);
        Assert.False(lease.Placement.IsValid);
        Assert.Equal(0u, canonical.FullCellId);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentAndPresentZeroCellCreateAwaitFreshPosition(
        bool presentZeroCell)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 25UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    presentZeroCell ? 0x70003016u : 0x70003017u,
                    1,
                    includePosition: presentZeroCell,
                    positionCell: 0u),
                isLocalPlayer: false)
            .Canonical!;

        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(RuntimeAuthoritativePositionDisposition.AwaitFreshPosition,
            lease.Route.Disposition);
        Assert.False(lease.Placement.IsValid);
        Assert.Equal(0u, canonical.FullCellId);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
    }

    [Fact]
    public void EqualAndNewerSameGenerationCreateDoNotChurnPendingPlacement()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 18UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x7000300Du, 1),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease initial));
        ulong positionAuthority = canonical.PositionAuthorityVersion;
        ulong spatialAuthority = canonical.SpatialAuthorityVersion;

        RuntimeEntityRegistrationResult duplicate = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x7000300Du, 1),
                isLocalPlayer: false);
        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration,
            duplicate.Inbound.Disposition);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease afterDuplicate));
        Assert.Equal(initial.Token, afterDuplicate.Token);
        Assert.Equal(initial.Route, afterDuplicate.Route);
        Assert.Equal(initial.Placement, afterDuplicate.Placement);
        RuntimeInitialCreateResidenceContinuation duplicateEnvelope =
            Assert.Single(afterDuplicate.Continuations);
        Assert.Equal(RuntimeInitialCreateContinuationKind.SameIncarnationCreate,
            duplicateEnvelope.Kind);
        Assert.DoesNotContain(
            duplicateEnvelope.Actions,
            static action => action.Kind
                is RuntimeInitialCreateTailActionKind.Position);
        Assert.Equal(positionAuthority, canonical.PositionAuthorityVersion);
        Assert.Equal(spatialAuthority, canonical.SpatialAuthorityVersion);

        RuntimeEntityRegistrationResult newer = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    0x7000300Du,
                    1,
                    positionSequence: 2,
                    positionX: 30f),
                isLocalPlayer: false);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease beforeFreshRoute));
        Assert.Equal(2, beforeFreshRoute.Continuations.Length);
        Assert.Equal(10f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.Null(newer.Inbound.SameGenerationEvents);
        Assert.Equal(0u, canonical.FullCellId);
        Assert.Equal(10f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease successor));
        Assert.Equal(initial.Token, successor.Token);
        Assert.Equal(RuntimeSetPositionOperationKind.RemoteAuthoritative,
            successor.Route.OperationKind);
        RuntimeInitialCreateResidenceContinuation continuation =
            successor.Continuations[1];
        RuntimeInitialCreateTailAction position = PositionAction(continuation);
        Assert.Equal(30f, position.Position!.Value.Position.PositionX);
        Assert.Equal((ushort)2, position.Position.Value.PositionSequence);
        Assert.Equal(PositionTimestampDisposition.Apply,
            position.PositionDisposition);
        Assert.True(successor.Placement.IsValid);
        Assert.Equal(1, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
    }

    [Fact]
    public void AlreadyPlacedSameGenerationCreateCannotReclaimWireCell()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 19UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x7000300Eu, 1, setupId: null),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                lease.Placement,
                Prepare(
                    lifetime,
                    lease,
                    RuntimeSetPositionMoverSetup.ResolvedAbsent));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out _));
        ulong spatialAuthority = canonical.SpatialAuthorityVersion;

        _ = lifetime.RegisterEntityWithInitialResidence(
            Spawn(0x7000300Eu, 1, setupId: null),
            isLocalPlayer: false);

        Assert.Equal(Cell, canonical.FullCellId);
        Assert.Equal(spatialAuthority, canonical.SpatialAuthorityVersion);
        Assert.False(lifetime.TryGetInitialCreateResidence(canonical, out _));
    }

    [Fact]
    public void LocalNoTeleportFreshPositionRemainsInitialWorldAdmission()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 22UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003012u, 1),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease initial));
        RuntimeEntityRegistrationResult newer = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    0x70003012u,
                    1,
                    positionSequence: 2,
                    positionX: 40f),
                isLocalPlayer: true);
        Assert.Null(newer.Inbound.SameGenerationEvents);
        Assert.Equal(0u, canonical.FullCellId);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease successor));
        Assert.Equal(initial.Token, successor.Token);
        Assert.Equal(RuntimeAuthoritativePositionDisposition.SetPosition,
            successor.Route.Disposition);
        Assert.Equal(RuntimeSetPositionOperationKind.InitialLogin,
            successor.Route.OperationKind);
        Assert.Equal(RuntimeTeleportHookPhase.AfterEnterWorld,
            successor.Route.TeleportHookPhase);
        Assert.True(successor.Placement.IsValid);
        RuntimeInitialCreateResidenceContinuation continuation =
            Assert.Single(successor.Continuations);
        RuntimeInitialCreateTailAction position = PositionAction(continuation);
        Assert.Equal(PositionTimestampDisposition.Apply,
            position.PositionDisposition);
        Assert.False(position.AcceptedTimestamps.TeleportAdvanced);
        Assert.Equal((ushort)0, position.PreviousTeleportSequence);
        Assert.Equal((ushort)0, position.Position!.Value.TeleportSequence);
    }

    [Fact]
    public void LocalForcePositionSuffixIsQueuedBehindInitialAdmission()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 26UL);
        RuntimeInitialCreateResidenceLease successor = ApplyFreshSuccessor(
            lifetime,
            guid: 0x70003018u,
            isLocalPlayer: true,
            teleportSequence: 0,
            forcePositionSequence: 1,
            out PositionTimestampDisposition disposition);

        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);
        Assert.Equal(RuntimeSetPositionOperationKind.InitialLogin,
            successor.Route.OperationKind);
        RuntimeInitialCreateResidenceContinuation continuation =
            Assert.Single(successor.Continuations);
        RuntimeInitialCreateTailAction position = PositionAction(continuation);
        Assert.Equal(PositionTimestampDisposition.ForcePosition,
            position.PositionDisposition);
        Assert.Equal((ushort)1,
            position.Position!.Value.ForcePositionSequence);
        Assert.Equal((ushort)0, position.PreviousTeleportSequence);
        Assert.Equal((ushort)0, position.Position.Value.TeleportSequence);
    }

    [Fact]
    public void LocalTeleportSuffixIsQueuedBehindInitialAdmission()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 27UL);
        RuntimeInitialCreateResidenceLease successor = ApplyFreshSuccessor(
            lifetime,
            guid: 0x70003019u,
            isLocalPlayer: true,
            teleportSequence: 1,
            forcePositionSequence: 0,
            out PositionTimestampDisposition disposition);

        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        RuntimeInitialCreateResidenceContinuation continuation =
            Assert.Single(successor.Continuations);
        RuntimeInitialCreateTailAction position = PositionAction(continuation);
        Assert.Equal((ushort)0, position.PreviousTeleportSequence);
        Assert.Equal((ushort)1, position.Position!.Value.TeleportSequence);
        Assert.Null(position.Position.Value.Velocity);
    }

    [Fact]
    public void RemoteTeleportSuffixIsQueuedBehindInitialAdmission()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 28UL);
        RuntimeInitialCreateResidenceLease successor = ApplyFreshSuccessor(
            lifetime,
            guid: 0x7000301Au,
            isLocalPlayer: false,
            teleportSequence: 1,
            forcePositionSequence: 0,
            out PositionTimestampDisposition disposition);

        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        RuntimeInitialCreateResidenceContinuation continuation =
            Assert.Single(successor.Continuations);
        RuntimeInitialCreateTailAction position = PositionAction(continuation);
        Assert.Equal((ushort)0, position.PreviousTeleportSequence);
        Assert.Equal((ushort)1, position.Position!.Value.TeleportSequence);
    }

    [Fact]
    public void AcceptedPositionsRemainMonotonicRawFifoWithoutMutatingInitialAdmission()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 29UL);
        const uint guid = 0x7000301Bu;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease initial));
        ulong positionAuthority = canonical.PositionAuthorityVersion;
        int callbacks = 0;

        ApplyQueuedPosition(
            lifetime,
            guid,
            positionSequence: 2,
            teleportSequence: 0,
            forcePositionSequence: 0,
            positionX: 20f,
            () => callbacks++);
        ApplyQueuedPosition(
            lifetime,
            guid,
            positionSequence: 3,
            teleportSequence: 1,
            forcePositionSequence: 0,
            positionX: 30f,
            () => callbacks++);
        ApplyQueuedPosition(
            lifetime,
            guid,
            positionSequence: 4,
            teleportSequence: 2,
            forcePositionSequence: 0,
            positionX: 40f,
            () => callbacks++);

        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease retained));
        Assert.Equal(initial.Token, retained.Token);
        Assert.Equal(initial.Route, retained.Route);
        Assert.Equal(initial.Placement, retained.Placement);
        Assert.Equal(10f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.Equal(positionAuthority, canonical.PositionAuthorityVersion);
        Assert.Equal(0, callbacks);
        Assert.Equal([1UL, 2UL, 3UL],
            retained.Continuations.Select(static item => item.Sequence));
        Assert.Equal([20f, 30f, 40f],
            retained.Continuations.Select(
                static item => PositionAction(item).Position!.Value.Position.PositionX));
        Assert.Equal([(ushort)0, (ushort)0, (ushort)1],
            retained.Continuations.Select(
                static item => PositionAction(item).PreviousTeleportSequence));
        Assert.Equal([(ushort)0, (ushort)1, (ushort)2],
            retained.Continuations.Select(
                static item => PositionAction(item).Position!.Value.TeleportSequence));
        Assert.Null(PositionAction(retained.Continuations[0])
            .AcceptedTimestamps.PreMergeCommittedCellId);
        Assert.Null(PositionAction(retained.Continuations[1])
            .AcceptedTimestamps.PreMergeCommittedCellId);
    }

    [Fact]
    public void MalformedQueuedPositionIsRejectedBeforeTimestampConsumption()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 30UL);
        const uint guid = 0x7000301Cu;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease initial));
        WorldSession.EntityPositionUpdate malformed = PositionUpdate(
            guid,
            positionSequence: 2,
            teleportSequence: 0,
            forcePositionSequence: 0,
            positionX: float.NaN);

        Assert.False(lifetime.TryApplyPosition(
            malformed,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out _));

        Assert.Equal(PositionTimestampDisposition.Rejected, disposition);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease retained));
        Assert.Equal(initial, retained);
        Assert.Equal(10f, canonical.Snapshot.Position!.Value.PositionX);

        ApplyQueuedPosition(
            lifetime,
            guid,
            positionSequence: 2,
            teleportSequence: 0,
            forcePositionSequence: 0,
            positionX: 25f,
            callback: null,
            isLocalPlayer: false);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out retained));
        Assert.Equal((ushort)2,
            PositionAction(Assert.Single(retained.Continuations))
                .Position!.Value.PositionSequence);
    }

    [Fact]
    public void CompletionRequiresExactAcknowledgedOriginalPlacement()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 20UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x7000300Fu, 1, setupId: null),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease original));
        AttachDormantBody(lifetime, canonical);

        RuntimeEntityPlacementToken replacement = lifetime.Physics.SetPosition
            .BeginAuthoredPlacement(
                canonical,
                canonical.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        var preparation = new RuntimeSetPositionMoverPreparation(
            RuntimeSetPositionMoverSetup.ResolvedAbsent,
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 1d,
            PhysicsPlacementClass.Ordinary,
            original.Route.SetPositionFlags);
        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                replacement,
                preparation,
                out RuntimeSetPositionCommand command));
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(replacement, command);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));

        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.RejectedAuthority,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                original.Token,
                out _));
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .AcknowledgedPlacementCompletionCount);
    }

    [Fact]
    public void FreshPositionQueuesBehindCommittedUnacknowledgedInitialAdmission()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 23UL);
        int deleted = 0;
        using IDisposable subscription = lifetime.Events.Subscribe(
            new EntityObserver(delta =>
            {
                if (delta.Change is RuntimeEntityChange.Deleted)
                    deleted++;
            }));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003013u, 1, setupId: null),
                isLocalPlayer: true)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease initial));
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionOutcome first = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                initial.Placement,
                Prepare(
                    lifetime,
                    initial,
                    RuntimeSetPositionMoverSetup.ResolvedAbsent));
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            first.Status);
        Assert.Equal(Cell, canonical.FullCellId);

        RuntimeEntityRegistrationResult newer = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    0x70003013u,
                    1,
                    setupId: null,
                    positionSequence: 2,
                    positionX: 45f),
                isLocalPlayer: true);
        Assert.Null(newer.Inbound.SameGenerationEvents);
        Assert.Equal(0, deleted);
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot place));
        Assert.Equal(RuntimePlacementProjectionKind.Place, place.Kind);
        Assert.Equal(first.Projection.Sequence, place.Token.Sequence);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            place.Token));
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease successor));
        Assert.Equal(initial.Token, successor.Token);
        Assert.Equal(initial.Route, successor.Route);
        Assert.Equal(initial.Placement, successor.Placement);
        Assert.Equal(RuntimeTeleportHookPhase.AfterEnterWorld,
            successor.Route.TeleportHookPhase);
        RuntimeInitialCreateResidenceContinuation continuation =
            Assert.Single(successor.Continuations);
        Assert.Equal(45f, PositionAction(continuation)
            .Position!.Value.Position.PositionX);
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                successor.Token,
                out RuntimeInitialCreateResidenceReceipt receipt));
        Assert.Equal(RuntimeTeleportHookPhase.AfterEnterWorld,
            receipt.TeleportHookPhase);
        Assert.Equal(first.Projection, receipt.Projection);
        Assert.Equal(successor.Continuations, receipt.Continuations);
        ApplyQueuedPosition(
            lifetime,
            canonical.ServerGuid,
            positionSequence: 3,
            teleportSequence: 1,
            forcePositionSequence: 0,
            positionX: 55f,
            callback: null);
        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                successor.Token,
                out RuntimeInitialCreateResidenceReceipt repeated));
        Assert.Equal(receipt.Adoption.Revision + 1UL,
            repeated.Adoption.Revision);
        Assert.Equal(2, repeated.Continuations.Length);
        Assert.Equal(55f,
            PositionAction(repeated.Continuations[1])
                .Position!.Value.Position.PositionX);
        Assert.False(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            receipt.Adoption));
        Assert.False(lifetime.AcknowledgeInitialCreateResidenceAdoption(
            canonical,
            repeated.Adoption));
        Assert.Equal(1, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.Equal(0, deleted);
        ConvergeSessionClear(lifetime);
    }

    [Fact]
    public void PreAcknowledgementSpatialCorruptionRetiresOperationAsDiscard()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 31UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x7000301Du, 1, setupId: null),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                lease.Placement,
                Prepare(
                    lifetime,
                    lease,
                    RuntimeSetPositionMoverSetup.ResolvedAbsent));
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);

        lifetime.Entities.SetFullCell(
            canonical,
            Landblock | 0x0002u,
            Landblock);

        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.RejectedAuthority,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out _));
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot discard));
        Assert.Equal(RuntimePlacementProjectionKind.Discard, discard.Kind);
        Assert.Equal(outcome.Projection.Sequence, discard.Token.Sequence);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            discard.Token));
        RuntimeSetPositionOwnershipSnapshot ownership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(0, ownership.ActiveOperationCount);
        Assert.Equal(0, ownership.PlacementCompletionWatchCount);
        Assert.Equal(0, ownership.AcknowledgedPlacementCompletionCount);
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
    }

    [Fact]
    public void CorruptedPostAcknowledgementAuthorityRetiresProofAndLease()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 21UL);
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(0x70003010u, 1, setupId: null),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                lease.Placement,
                Prepare(
                    lifetime,
                    lease,
                    RuntimeSetPositionMoverSetup.ResolvedAbsent));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
        lifetime.Entities.AdvancePlacementCommit(canonical);

        Assert.Equal(
            RuntimeInitialCreateResidenceCompletionStatus.RejectedAuthority,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out _));
        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        RuntimeSetPositionOwnershipSnapshot ownership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(0, ownership.PlacementCompletionWatchCount);
        Assert.Equal(0, ownership.AcknowledgedPlacementCompletionCount);
    }

    [Fact]
    public void LegacyRegistrationAdvancesSpatialAuthorityOnlyOnce()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord canonical = lifetime.RegisterEntity(
            Spawn(0x70003011u, 1)).Canonical!;

        Assert.Equal(Cell, canonical.FullCellId);
        Assert.Equal(1UL, canonical.SpatialAuthorityVersion);
    }

    [Fact]
    public void MixedAcceptedPacketsRemainOneFrozenArrivalOrderedFifo()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 40UL);
        const uint parentGuid = 0x70004100u;
        const uint guid = 0x70003100u;
        _ = lifetime.RegisterEntity(Spawn(parentGuid, 1));
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1),
                isLocalPlayer: false)
            .Canonical!;
        WorldSession.EntitySpawn frozenCanonical = canonical.Snapshot;
        Assert.True(lifetime.Entities.TryGetSnapshot(
            guid,
            out WorldSession.EntitySpawn frozenWire));
        ulong lastPublished = lifetime.Events.LastSequence;
        int callbacks = 0;

        var appearance = new ObjDescEvent.Parsed(
            guid,
            new CreateObject.ModelData(
                0x04000001u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(
            appearance,
            _ => callbacks++,
            out _));
        Assert.True(lifetime.TryApplyPosition(
            PositionUpdate(guid, 2, 0, 0, 20f),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: _ => callbacks++,
            out _,
            out _,
            out _));
        var motion = new WorldSession.EntityMotionUpdate(
            guid,
            new CreateObject.ServerMotionState(0x3d, 0x11),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 1,
            IsAutonomous: false);
        Assert.True(lifetime.TryApplyMotion(
            motion,
            retainPayload: true,
            acknowledgeProjection: _ => callbacks++,
            out _,
            out _));
        var state = new SetState.Parsed(
            guid,
            (uint)(PhysicsStateFlags.Gravity | PhysicsStateFlags.Hidden),
            InstanceSequence: 1,
            StateSequence: 2);
        Assert.True(lifetime.TryApplyState(
            state,
            acknowledgeProjection: (_, _) => callbacks++,
            out _,
            out _));
        var vector = new VectorUpdate.Parsed(
            guid,
            new Vector3(4f, 5f, 6f),
            new Vector3(0f, 0f, 0.5f),
            InstanceSequence: 1,
            VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(
            vector,
            _ => callbacks++,
            out _));
        var pickup = new PickupEvent.Parsed(
            guid,
            InstanceSequence: 1,
            PositionSequence: 3);
        Assert.True(lifetime.TryApplyPickup(
            pickup,
            _ => callbacks++,
            out _));
        var parent = new ParentEvent.Parsed(
            parentGuid,
            guid,
            ParentLocation: 1u,
            PlacementId: 7u,
            ParentInstanceSequence: 1,
            ChildPositionSequence: 4);
        Assert.True(lifetime.TryApplyParent(
            parent,
            _ => callbacks++,
            out _));

        WorldSession.EntitySpawn sameCreate = Spawn(
            guid,
            1,
            positionSequence: 5,
            positionX: 50f) with
        {
            Name = "deferred-description",
        };
        PhysicsSpawnData samePhysics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            Physics = samePhysics with
            {
                Movement = new PhysicsMovementData(
                    new byte[] { 1 },
                    new CreateObject.ServerMotionState(0x3d, 0x12),
                    IsAutonomous: false),
                Velocity = new Vector3(7f, 8f, 9f),
                AngularVelocity = new Vector3(0f, 0f, 1f),
                Timestamps = samePhysics.Timestamps with
                {
                    Position = 5,
                    Movement = 3,
                    State = 3,
                    Vector = 3,
                    ObjDesc = 3,
                },
            },
            MovementSequence = 3,
        };
        RuntimeEntityRegistrationResult same = lifetime
            .RegisterEntityWithInitialResidence(
                sameCreate,
                isLocalPlayer: false);

        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration,
            same.Inbound.Disposition);
        Assert.Null(same.Inbound.SameGenerationEvents);
        Assert.Equal(0, callbacks);
        Assert.Equal(lastPublished, lifetime.Events.LastSequence);
        Assert.Equal(frozenCanonical, canonical.Snapshot);
        Assert.True(lifetime.Entities.TryGetSnapshot(guid, out var wireAfter));
        Assert.Equal(frozenWire, wireAfter);
        Assert.False(lifetime.Entities.ParentAttachments.HasCommittedParent(guid));

        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(
            [
                RuntimeInitialCreateContinuationKind.ObjDesc,
                RuntimeInitialCreateContinuationKind.Position,
                RuntimeInitialCreateContinuationKind.Movement,
                RuntimeInitialCreateContinuationKind.State,
                RuntimeInitialCreateContinuationKind.Vector,
                RuntimeInitialCreateContinuationKind.Pickup,
                RuntimeInitialCreateContinuationKind.Parent,
                RuntimeInitialCreateContinuationKind.SameIncarnationCreate,
            ],
            lease.Continuations.Select(static item => item.Kind));
        Assert.Equal(Enumerable.Range(1, 8).Select(static value => (ulong)value),
            lease.Continuations.Select(static item => item.Sequence));
        RuntimeInitialCreateResidenceContinuation atomic =
            lease.Continuations[^1];
        Assert.Equal(RuntimeAcceptedPositionSource.SameIncarnationCreate,
            atomic.PositionSource);
        Assert.Equal(RuntimeInitialCreateTailActionKind.WeenieDescription,
            atomic.Actions[^2].Kind);
        Assert.Equal(RuntimeInitialCreateTailActionKind.ResidentCellCleanup,
            atomic.Actions[^1].Kind);
        Assert.Equal(50f,
            PositionAction(atomic).Position!.Value.Position.PositionX);
    }

    [Fact]
    public void ZeroInstancePendingResidenceAdmitsSameInstanceContinuation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 48UL);
        const uint guid = 0x7000310Bu;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 0), false)
            .Canonical!;
        WorldSession.EntitySpawn frozen = canonical.Snapshot;
        ulong eventSequence = lifetime.Events.LastSequence;

        var update = new VectorUpdate.Parsed(
            guid,
            new Vector3(2f, 3f, 4f),
            new Vector3(0f, 0f, 0.5f),
            InstanceSequence: 0,
            VectorSequence: 2);
        Assert.True(lifetime.TryApplyVector(update, null, out var accepted));

        Assert.Equal(frozen, accepted);
        Assert.Equal(frozen, canonical.Snapshot);
        Assert.Equal(eventSequence, lifetime.Events.LastSequence);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        RuntimeInitialCreateResidenceContinuation queued =
            Assert.Single(lease.Continuations);
        Assert.Equal(RuntimeInitialCreateContinuationKind.Vector, queued.Kind);
        Assert.Equal((ushort)0, Assert.Single(queued.Actions)
            .Vector!.Value.InstanceSequence);
    }

    [Fact]
    public void MissingParentCreateIsRawUnacceptedAndExactDeleteCancelsIt()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 41UL);
        const uint childGuid = 0x70003101u;
        const uint missingParent = 0x70004101u;
        var rawParts = new[] { new CreateObject.AnimPartChange(1, 10u) };
        byte[] rawMovement = [1, 2];
        WorldSession.EntitySpawn raw = Spawn(
            childGuid,
            incarnation: 0,
            includePosition: false,
            parentGuid: missingParent);
        PhysicsSpawnData rawPhysics = raw.Physics!.Value;
        raw = raw with
        {
            AnimPartChanges = rawParts,
            Physics = rawPhysics with
            {
                Movement = new PhysicsMovementData(
                    rawMovement,
                    MotionState: null,
                    IsAutonomous: false),
            },
        };

        RuntimeEntityRegistrationResult deferred = lifetime
            .RegisterEntityWithInitialResidence(raw, isLocalPlayer: false);

        Assert.True(deferred.DeferredForParent);
        Assert.Null(deferred.Canonical);
        Assert.Empty(lifetime.Entities.Snapshots);
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.True(lifetime.Entities.ParentAttachments.ContainsDeferredCreate(
            childGuid,
            instanceSequence: 0));
        rawParts[0] = new CreateObject.AnimPartChange(9, 99u);
        rawMovement[0] = 99;
        Assert.True(lifetime.Entities.ParentAttachments.TryPeekDeferredCreate(
            missingParent,
            out DeferredParentCreate retained));
        Assert.Equal((byte)1, retained.Spawn.AnimPartChanges[0].PartIndex);
        Assert.Equal((byte)1,
            retained.Spawn.Physics!.Value.Movement!.Value.RawData.Span[0]);

        Assert.False(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(childGuid, InstanceSequence: 0),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out _));
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.Empty(lifetime.Entities.Snapshots);
    }

    [Fact]
    public void DeferredRawChildGenerationFiltersPreserveEqualReplacementAndFuture()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 42UL);
        const uint childGuid = 0x70003102u;
        const uint missingParent = 0x70004102u;
        foreach (ushort incarnation in new ushort[] { 1, 2, 3 })
        {
            Assert.True(lifetime.RegisterEntityWithInitialResidence(
                Spawn(
                    childGuid,
                    incarnation,
                    includePosition: false,
                    parentGuid: missingParent),
                isLocalPlayer: false).DeferredForParent);
        }

        lifetime.Entities.ParentAttachments.EndGeneration(
            missingParent,
            replacementGeneration: 7);
        lifetime.Entities.ParentAttachments.DeleteGeneration(
            missingParent,
            deletedGeneration: 7);
        Assert.Equal(3, lifetime.CaptureOwnership().DeferredParentCreateCount);

        lifetime.Entities.ParentAttachments.EndGeneration(
            childGuid,
            replacementGeneration: 2);
        Assert.Equal(2, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.True(lifetime.Entities.ParentAttachments.ContainsDeferredCreate(
            childGuid,
            2));
        Assert.True(lifetime.Entities.ParentAttachments.ContainsDeferredCreate(
            childGuid,
            3));

        lifetime.Entities.ParentAttachments.DeleteGeneration(
            childGuid,
            deletedGeneration: 2);
        Assert.Equal(1, lifetime.CaptureOwnership().DeferredParentCreateCount);
        Assert.True(lifetime.Entities.ParentAttachments.ContainsDeferredCreate(
            childGuid,
            3));
        lifetime.Entities.ParentAttachments.DeleteGeneration(
            childGuid,
            deletedGeneration: 3);
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
    }

    [Fact]
    public void MalformedDeferredVectorDoesNotConsumeItsSequence()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 43UL);
        const uint guid = 0x70003103u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), false)
            .Canonical!;
        WorldSession.EntitySpawn frozen = canonical.Snapshot;
        var malformed = new VectorUpdate.Parsed(
            guid,
            new Vector3(float.NaN, 0f, 0f),
            Vector3.Zero,
            InstanceSequence: 1,
            VectorSequence: 2);
        Assert.False(lifetime.TryApplyVector(
            malformed,
            acknowledgeProjection: null,
            out _));
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease before));
        Assert.Empty(before.Continuations);

        var corrected = malformed with { Velocity = new Vector3(1f, 2f, 3f) };
        Assert.True(lifetime.TryApplyVector(
            corrected,
            acknowledgeProjection: null,
            out _));
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease after));
        Assert.Equal(RuntimeInitialCreateContinuationKind.Vector,
            Assert.Single(after.Continuations).Kind);
        Assert.Equal(frozen, canonical.Snapshot);
        Assert.True(lifetime.Entities.TryGetSnapshot(guid, out var wire));
        Assert.Equal(frozen, wire);
    }

    [Fact]
    public void DeleteAndGuidReuseCannotLeakPriorIncarnationFifo()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 44UL);
        const uint guid = 0x70003104u;
        RuntimeEntityRecord first = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), false)
            .Canonical!;
        Assert.True(lifetime.TryApplyVector(
            new VectorUpdate.Parsed(
                guid,
                Vector3.One,
                Vector3.Zero,
                InstanceSequence: 1,
                VectorSequence: 2),
            acknowledgeProjection: null,
            out _));
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(guid, 1),
            isLocalPlayer: false,
            removeRetainedObject: false,
            out RuntimeEntityDeleteAcceptance deletion));
        lifetime.CompleteAcceptedDelete(deletion);
        Assert.Null(lifetime.RetireCanonicalOnly(first));

        RuntimeEntityRecord replacement = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 2), false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            replacement,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Empty(lease.Continuations);
        Assert.Equal((ushort)2, replacement.Incarnation);
        Assert.Equal(1, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
    }

    [Fact]
    public void RetainedAdmissionDeepFreezesEveryParserOwnedCollection()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 45UL);
        const uint guid = 0x70003105u;

        var initialParts = new[] { new CreateObject.AnimPartChange(1, 10u) };
        var initialTextures = new[]
        {
            new CreateObject.TextureChange(2, 20u, 21u),
        };
        var initialPalettes = new[]
        {
            new CreateObject.SubPaletteSwap(30u, 3, 4),
        };
        var initialCommands = new[]
        {
            new CreateObject.MotionItem(0x11, 1, 1f),
        };
        var movementCommands = new[]
        {
            new CreateObject.MotionItem(0x12, 2, 1f),
        };
        byte[] rawMovement = [1, 2, 3];
        var children = new[] { new PhysicsAttachment(0x70004105u, 5u) };
        WorldSession.EntitySpawn initial = Spawn(guid, 1);
        PhysicsSpawnData initialPhysics = initial.Physics!.Value;
        initial = initial with
        {
            AnimPartChanges = initialParts,
            TextureChanges = initialTextures,
            SubPalettes = initialPalettes,
            MotionState = new CreateObject.ServerMotionState(
                0x3d,
                0x10,
                Commands: initialCommands),
            Physics = initialPhysics with
            {
                Movement = new PhysicsMovementData(
                    rawMovement,
                    new CreateObject.ServerMotionState(
                        0x3d,
                        0x10,
                        Commands: movementCommands),
                    IsAutonomous: false),
                Children = children,
            },
        };

        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(initial, false)
            .Canonical!;
        initialParts[0] = new CreateObject.AnimPartChange(9, 99u);
        initialTextures[0] = new CreateObject.TextureChange(9, 99u, 100u);
        initialPalettes[0] = new CreateObject.SubPaletteSwap(99u, 9, 9);
        initialCommands[0] = new CreateObject.MotionItem(0x99, 9, 9f);
        movementCommands[0] = new CreateObject.MotionItem(0x98, 8, 8f);
        rawMovement[0] = 99;
        children[0] = new PhysicsAttachment(0x70009999u, 99u);

        var objParts = new[] { new CreateObject.AnimPartChange(4, 40u) };
        var objTextures = new[]
        {
            new CreateObject.TextureChange(5, 50u, 51u),
        };
        var objPalettes = new[]
        {
            new CreateObject.SubPaletteSwap(60u, 6, 7),
        };
        Assert.True(lifetime.TryApplyObjDesc(
            new ObjDescEvent.Parsed(
                guid,
                new CreateObject.ModelData(
                    70u,
                    objPalettes,
                    objTextures,
                    objParts),
                InstanceSequence: 1,
                ObjDescSequence: 2),
            acknowledgeProjection: null,
            out _));
        objParts[0] = new CreateObject.AnimPartChange(9, 99u);
        objTextures[0] = new CreateObject.TextureChange(9, 99u, 100u);
        objPalettes[0] = new CreateObject.SubPaletteSwap(99u, 9, 9);

        var motionCommands = new[]
        {
            new CreateObject.MotionItem(0x13, 3, 1f),
        };
        Assert.True(lifetime.TryApplyMotion(
            new WorldSession.EntityMotionUpdate(
                guid,
                new CreateObject.ServerMotionState(
                    0x3d,
                    0x13,
                    Commands: motionCommands),
                InstanceSequence: 1,
                MovementSequence: 2,
                ServerControlSequence: 1,
                IsAutonomous: false),
            retainPayload: true,
            acknowledgeProjection: null,
            out _,
            out _));
        motionCommands[0] = new CreateObject.MotionItem(0x97, 7, 7f);

        var createParts = new[] { new CreateObject.AnimPartChange(8, 80u) };
        var createCommands = new[]
        {
            new CreateObject.MotionItem(0x14, 4, 1f),
        };
        byte[] createRawMovement = [4, 5, 6];
        WorldSession.EntitySpawn sameCreate = Spawn(
            guid,
            1,
            positionSequence: 2,
            positionX: 30f);
        PhysicsSpawnData samePhysics = sameCreate.Physics!.Value;
        sameCreate = sameCreate with
        {
            AnimPartChanges = createParts,
            Physics = samePhysics with
            {
                Movement = new PhysicsMovementData(
                    createRawMovement,
                    new CreateObject.ServerMotionState(
                        0x3d,
                        0x14,
                        Commands: createCommands),
                    IsAutonomous: false),
                Timestamps = samePhysics.Timestamps with
                {
                    ObjDesc = 3,
                    Movement = 3,
                },
            },
            MovementSequence = 3,
        };
        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration,
            lifetime.RegisterEntityWithInitialResidence(sameCreate, false)
                .Inbound.Disposition);
        createParts[0] = new CreateObject.AnimPartChange(9, 99u);
        createCommands[0] = new CreateObject.MotionItem(0x96, 6, 6f);
        createRawMovement[0] = 99;

        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal((byte)1, lease.InitialCreate.AnimPartChanges[0].PartIndex);
        Assert.Equal((byte)2, lease.InitialCreate.TextureChanges[0].PartIndex);
        Assert.Equal(30u, lease.InitialCreate.SubPalettes[0].SubPaletteId);
        Assert.Equal((ushort)0x11,
            lease.InitialCreate.MotionState!.Value.Commands![0].Command);
        Assert.Equal((byte)1,
            lease.InitialCreate.Physics!.Value.Movement!.Value.RawData.Span[0]);
        Assert.Equal((ushort)0x12,
            lease.InitialCreate.Physics.Value.Movement.Value.MotionState!
                .Value.Commands![0].Command);
        Assert.Equal(0x70004105u,
            lease.InitialCreate.Physics.Value.Children!.Value.Span[0].Guid);

        RuntimeInitialCreateTailAction objDesc = Assert.Single(
            lease.Continuations[0].Actions);
        Assert.Equal((byte)4,
            objDesc.ObjDesc!.Value.ModelData.AnimPartChanges[0].PartIndex);
        Assert.Equal((byte)5,
            objDesc.ObjDesc.Value.ModelData.TextureChanges[0].PartIndex);
        Assert.Equal(60u,
            objDesc.ObjDesc.Value.ModelData.SubPalettes[0].SubPaletteId);
        RuntimeInitialCreateTailAction motion = Assert.Single(
            lease.Continuations[1].Actions);
        Assert.Equal((ushort)0x13,
            motion.Movement!.Value.MotionState.Commands![0].Command);
        RuntimeInitialCreateResidenceContinuation atomic =
            lease.Continuations[2];
        RuntimeInitialCreateTailAction description = Assert.Single(
            atomic.Actions.Where(static action => action.Kind is
                RuntimeInitialCreateTailActionKind.PreTailDescriptionAdaptation));
        Assert.Equal((byte)4,
            description.Description!.Value.Movement!.Value.RawData.Span[0]);
        Assert.Equal((ushort)0x14,
            description.Description.Value.Movement.Value.MotionState!
                .Value.Commands![0].Command);
        RuntimeInitialCreateTailAction weenie = Assert.Single(
            atomic.Actions.Where(static action => action.Kind is
                RuntimeInitialCreateTailActionKind.WeenieDescription));
        Assert.Equal((byte)8,
            weenie.WeenieDescription!.Value.AnimPartChanges[0].PartIndex);
        Assert.Equal((byte)4,
            weenie.WeenieDescription.Value.Physics!.Value.Movement!.Value
                .RawData.Span[0]);
    }

    [Fact]
    public void CreateIdentityAndParentMismatchFailBeforeAnyAdmissionMutation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 46UL);
        const uint guid = 0x70003106u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(Spawn(guid, 1), false)
            .Canonical!;
        WorldSession.EntitySpawn frozen = canonical.Snapshot;
        ulong eventSequence = lifetime.Events.LastSequence;

        WorldSession.EntitySpawn instanceMismatch = Spawn(
            guid,
            1,
            positionSequence: 2,
            positionX: 20f);
        PhysicsSpawnData mismatchedPhysics = instanceMismatch.Physics!.Value;
        instanceMismatch = instanceMismatch with
        {
            Physics = mismatchedPhysics with
            {
                Timestamps = mismatchedPhysics.Timestamps with { Instance = 2 },
            },
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(instanceMismatch, false));

        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease unchanged));
        Assert.Empty(unchanged.Continuations);
        Assert.Equal(frozen, canonical.Snapshot);
        Assert.Equal(eventSequence, lifetime.Events.LastSequence);
        Assert.True(lifetime.Entities.TryGetSnapshot(guid, out var wire));
        Assert.Equal(frozen, wire);

        RuntimeEntityRegistrationResult corrected = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, positionSequence: 2, positionX: 20f),
                false);
        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration,
            corrected.Inbound.Disposition);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease admitted));
        Assert.Equal(RuntimeInitialCreateContinuationKind.SameIncarnationCreate,
            Assert.Single(admitted.Continuations).Kind);

        const uint child = 0x70003107u;
        WorldSession.EntitySpawn parentMismatch = Spawn(
            child,
            1,
            includePosition: false,
            parentGuid: 0x70004107u);
        PhysicsSpawnData parentPhysics = parentMismatch.Physics!.Value;
        parentMismatch = parentMismatch with
        {
            Physics = parentPhysics with
            {
                Parent = new PhysicsAttachment(0x70004108u, 1u),
            },
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(parentMismatch, false));
        Assert.False(lifetime.Entities.ParentAttachments.ContainsDeferredCreate(
            child,
            1));
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);

        WorldSession.EntitySpawn flattenedOnlyParent = Spawn(
            child,
            1,
            includePosition: false,
            parentGuid: 0x70004107u);
        PhysicsSpawnData flattenedOnlyPhysics =
            flattenedOnlyParent.Physics!.Value;
        flattenedOnlyParent = flattenedOnlyParent with
        {
            Physics = flattenedOnlyPhysics with { Parent = null },
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(flattenedOnlyParent, false));

        WorldSession.EntitySpawn physicsOnlyParent = Spawn(
            child,
            1,
            includePosition: false,
            parentGuid: 0x70004107u) with
        {
            ParentGuid = null,
            ParentLocation = null,
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(physicsOnlyParent, false));

        WorldSession.EntitySpawn noPhysicsTopParent = Spawn(
            child,
            0,
            includePosition: false,
            parentGuid: 0x70004107u) with
        {
            Physics = null,
            MovementSequence = 0,
            ServerControlSequence = 0,
            PositionSequence = 0,
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(noPhysicsTopParent, false));
        Assert.False(lifetime.Entities.ParentAttachments.ContainsDeferredCreate(
            child,
            0));
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);

        const uint noPhysicsProjectionGuid = 0x7000310Cu;
        WorldSession.EntitySpawn noPhysicsSetup = Spawn(
            noPhysicsProjectionGuid,
            0,
            includePosition: false) with
        {
            Physics = null,
            PhysicsState = null,
            MovementSequence = 0,
            ServerControlSequence = 0,
            PositionSequence = 0,
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(noPhysicsSetup, false));
        Assert.False(lifetime.Entities.TryGetActive(
            noPhysicsProjectionGuid,
            out _));
        Assert.Equal(eventSequence, lifetime.Events.LastSequence);

        WorldSession.EntitySpawn placementMismatch = Spawn(
            child,
            1,
            includePosition: false,
            parentGuid: 0x70004107u) with
        {
            PlacementId = 7u,
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(placementMismatch, false));
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);

        WorldSession.EntitySpawn timestampMismatch = Spawn(
            child,
            1,
            includePosition: false,
            parentGuid: 0x70004107u) with
        {
            MovementSequence = 2,
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(timestampMismatch, false));

        WorldSession.EntitySpawn positionProjectionMismatch = Spawn(
            guid,
            1,
            positionSequence: 2,
            positionX: 20f);
        PhysicsSpawnData positionProjectionPhysics =
            positionProjectionMismatch.Physics!.Value;
        positionProjectionMismatch = positionProjectionMismatch with
        {
            Physics = positionProjectionPhysics with
            {
                Position = positionProjectionPhysics.Position!.Value with
                {
                    PositionX = 21f,
                },
            },
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(
                positionProjectionMismatch,
                false));
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease stillUnchanged));
        Assert.Single(stillUnchanged.Continuations);
        Assert.Equal(frozen, canonical.Snapshot);
        Assert.Equal(eventSequence, lifetime.Events.LastSequence);

        WorldSession.EntitySpawn rawInstanceMismatch = Spawn(
            child,
            1,
            includePosition: false,
            parentGuid: 0x70004107u);
        PhysicsSpawnData rawInstancePhysics =
            rawInstanceMismatch.Physics!.Value;
        rawInstanceMismatch = rawInstanceMismatch with
        {
            Physics = rawInstancePhysics with
            {
                Timestamps = rawInstancePhysics.Timestamps with { Instance = 2 },
            },
        };
        Assert.Throws<InvalidOperationException>(() => lifetime
            .RegisterEntityWithInitialResidence(rawInstanceMismatch, false));
        Assert.Equal(0, lifetime.CaptureOwnership().DeferredParentCreateCount);
    }

    [Fact]
    public void DeferredParentCreateAdmissionTokenPreventsResetAbaConsumption()
    {
        var state = new ParentAttachmentState();
        const uint parent = 0x70004109u;
        WorldSession.EntitySpawn spawn = Spawn(
            0x70003109u,
            1,
            includePosition: false,
            parentGuid: parent);
        state.EnqueueDeferredCreate(spawn, false);
        Assert.True(state.TryPeekDeferredCreate(parent, out var oldAdmission));
        state.Clear();
        state.EnqueueDeferredCreate(spawn, false);
        Assert.True(state.TryPeekDeferredCreate(parent, out var newAdmission));

        Assert.NotEqual(oldAdmission.AdmissionId, newAdmission.AdmissionId);
        Assert.False(state.ConsumeDeferredCreate(parent, oldAdmission));
        Assert.True(state.ConsumeDeferredCreate(parent, newAdmission));
        Assert.Equal(0, state.DeferredCreateCount);

        FieldInfo admissionField = typeof(ParentAttachmentState).GetField(
            "_nextDeferredCreateAdmissionId",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        admissionField.SetValue(state, ulong.MaxValue);
        Assert.Throws<InvalidOperationException>(() =>
            state.EnqueueDeferredCreate(spawn, false));
        Assert.Equal(0, state.DeferredCreateCount);
    }

    [Fact]
    public void CompletedFifoSaturationFailsBeforeConsumingWireSequence()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 47UL);
        const uint guid = 0x7000310Au;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, includePosition: false),
                false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(RuntimeInitialCreateResidenceCompletionStatus.Completed,
            lifetime.CompleteInitialCreateResidence(
                canonical,
                lease.Token,
                out RuntimeInitialCreateResidenceReceipt receipt));

        SetCompletedAdoptionRevision(
            lifetime.InitialCreateResidences,
            canonical.Key!.Value,
            ulong.MaxValue);
        var update = new VectorUpdate.Parsed(
            guid,
            Vector3.One,
            Vector3.Zero,
            InstanceSequence: 1,
            VectorSequence: 2);
        Assert.False(lifetime.TryApplyVector(update, null, out _));
        Assert.Equal(canonical.Snapshot,
            lifetime.Entities.Snapshots[guid]);

        SetCompletedAdoptionRevision(
            lifetime.InitialCreateResidences,
            canonical.Key.Value,
            receipt.Adoption.Revision);
        Assert.True(lifetime.TryApplyVector(update, null, out _));
        Assert.True(lifetime.InitialCreateResidences.TryGetTransaction(
            canonical,
            out RuntimeInitialCreateResidenceLease retained));
        Assert.Equal(RuntimeInitialCreateContinuationKind.Vector,
            Assert.Single(retained.Continuations).Kind);
    }


    [Fact]
    public void TryCommitParent_CancelsActiveInitialResidenceAndItsPendingPlacement()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        Bind(lifetime, 200UL);
        const uint guid = 0x70004001u;
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1, setupId: null),
                isLocalPlayer: false)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease lease));
        Assert.True(lease.Placement.IsValid);
        AttachDormantBody(lifetime, canonical);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                lease.Placement,
                Prepare(lifetime, lease, RuntimeSetPositionMoverSetup.ResolvedAbsent));
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(1, lifetime.Physics.SetPosition.PendingProjectionCount);

        var discards = new List<RuntimePlacementProjectionSnapshot>();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            new PlacementObserver(delta =>
            {
                if (delta.Placement.Kind is RuntimePlacementProjectionKind.Discard)
                    discards.Add(delta.Placement);
            }));

        var relation = new ParentAttachmentRelation(
            ParentGuid: 0x70004100u,
            ChildGuid: guid,
            ParentLocation: 1u,
            PlacementId: 1u,
            ParentInstanceSequence: 1,
            ChildPositionSequence: 1);
        Assert.True(lifetime.TryCommitParent(relation, null, out _));

        Assert.Equal(0, lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
        // The old Place receipt is REPLACED by a Discard at the same
        // sequence, not removed outright - still awaiting host ack.
        Assert.Equal(1, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.False(lifetime.Physics.SetPosition.IsPlacementCurrent(
            lease.Placement));
        RuntimePlacementProjectionSnapshot discard = Assert.Single(discards);
        Assert.Equal(canonical.Key, discard.Token.Entity);
        Assert.Equal(outcome.Projection.Sequence, discard.Token.Sequence);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            discard.Token));
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
    }

    private static RuntimeSetPositionCommand Prepare(
        RuntimeEntityObjectLifetime lifetime,
        in RuntimeInitialCreateResidenceLease lease,
        in RuntimeSetPositionMoverSetup setup)
    {
        var preparation = new RuntimeSetPositionMoverPreparation(
            setup,
            lease.Route.OperationKind,
            GameTime: 1d,
            PhysicsPlacementClass.Ordinary,
            lease.Route.SetPositionFlags);
        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                lease.Placement,
                preparation,
                out RuntimeSetPositionCommand command));
        return command;
    }

    private static RuntimeInitialCreateTailAction PositionAction(
        in RuntimeInitialCreateResidenceContinuation continuation) =>
        Assert.Single(continuation.Actions.Where(
            static action => action.Kind
                is RuntimeInitialCreateTailActionKind.Position));

    private static void SetCompletedAdoptionRevision(
        RuntimeInitialCreateResidenceState state,
        RuntimeEntityKey key,
        ulong revision)
    {
        FieldInfo completedField = typeof(RuntimeInitialCreateResidenceState)
            .GetField("_completed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var completed = (IDictionary)completedField.GetValue(state)!;
        object entry = completed[key]!;
        PropertyInfo receiptProperty = entry.GetType().GetProperty(
            "Receipt",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var receipt = (RuntimeInitialCreateResidenceReceipt)
            receiptProperty.GetValue(entry)!;
        receiptProperty.SetValue(
            entry,
            receipt with
            {
                Adoption = receipt.Adoption with { Revision = revision },
            });
    }

    private static void ConvergeSessionClear(
        RuntimeEntityObjectLifetime lifetime)
    {
        foreach (RuntimeEntityRecord record in lifetime.BeginSessionClear())
            lifetime.CompleteSessionEntityRetirement(record);
        lifetime.ClearObjects();
        Assert.True(lifetime.CompleteSessionClearIfConverged());
    }

    private static RuntimeInitialCreateResidenceLease ApplyFreshSuccessor(
        RuntimeEntityObjectLifetime lifetime,
        uint guid,
        bool isLocalPlayer,
        ushort teleportSequence,
        ushort forcePositionSequence,
        out PositionTimestampDisposition disposition)
    {
        RuntimeEntityRecord canonical = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, 1),
                isLocalPlayer)
            .Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease initial));
        RuntimeEntityRegistrationResult newer = lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(
                    guid,
                    1,
                    positionSequence: 2,
                    positionX: 40f,
                    teleportSequence: teleportSequence,
                    forcePositionSequence: forcePositionSequence),
                isLocalPlayer);
        Assert.Null(newer.Inbound.SameGenerationEvents);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            canonical,
            out RuntimeInitialCreateResidenceLease retained));
        disposition = PositionAction(Assert.Single(retained.Continuations))
            .PositionDisposition;
        Assert.Equal(initial.Token, retained.Token);
        Assert.Equal(initial.Route, retained.Route);
        Assert.Equal(initial.Placement, retained.Placement);
        return retained;
    }

    private static void ApplyQueuedPosition(
        RuntimeEntityObjectLifetime lifetime,
        uint guid,
        ushort positionSequence,
        ushort teleportSequence,
        ushort forcePositionSequence,
        float positionX,
        Action? callback,
        bool isLocalPlayer = true)
    {
        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid,
            positionSequence,
            teleportSequence,
            forcePositionSequence,
            positionX);
        Assert.True(lifetime.TryApplyPosition(
            update,
            isLocalPlayer,
            forcePositionRotation: Quaternion.Identity,
            currentLocalVelocity: new Vector3(1f, 2f, 3f),
            acknowledgeProjection: callback is null
                ? null
                : _ => callback(),
            out PositionTimestampDisposition disposition,
            out _,
            out _));
        Assert.True(disposition is PositionTimestampDisposition.Apply
            or PositionTimestampDisposition.ForcePosition);
    }

    private static WorldSession.EntityPositionUpdate PositionUpdate(
        uint guid,
        ushort positionSequence,
        ushort teleportSequence,
        ushort forcePositionSequence,
        float positionX)
    {
        return new WorldSession.EntityPositionUpdate(
            guid,
            new CreateObject.ServerPosition(
                Cell,
                positionX,
                20f,
                7f,
                1f,
                0f,
                0f,
                0f),
            new Vector3(positionSequence, 2f, 3f),
            PlacementId: positionSequence,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: positionSequence,
            TeleportSequence: teleportSequence,
            ForcePositionSequence: forcePositionSequence);
    }

    private static void AttachDormantBody(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord canonical)
    {
        var body = new PhysicsBody
        {
            State = canonical.FinalPhysicsState,
            Orientation = Quaternion.Identity,
            InWorld = false,
        };
        lifetime.Entities.SetPhysicsBody(canonical, body);
    }

    [Fact]
    public void RetirementNotificationBoundReentrantlyDuringDispatchDoesNotCorruptTheCurrentIteration()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        Bind(lifetime, 20UL);
        var lateBoundKeys = new List<RuntimeEntityKey>();
        bool reentrantBindAttempted = false;
        lifetime.InitialCreateResidences.BindRetirementNotification(key =>
        {
            if (reentrantBindAttempted)
                return;
            reentrantBindAttempted = true;
            lifetime.InitialCreateResidences.BindRetirementNotification(
                lateBoundKeys.Add);
        });

        RuntimeEntityRecord first = lifetime.RegisterEntityWithInitialResidence(
            Spawn(0x70003F80u, 1), isLocalPlayer: false).Canonical!;
        RuntimeEntityKey firstKey = first.Key!.Value;
        Exception? thrown = Record.Exception(() =>
            lifetime.InitialCreateResidences.Forget(first, out _, out _));

        Assert.Null(thrown);
        Assert.True(reentrantBindAttempted);
        Assert.DoesNotContain(firstKey, lateBoundKeys);

        RuntimeEntityRecord second = lifetime.RegisterEntityWithInitialResidence(
            Spawn(0x70003F81u, 1), isLocalPlayer: false).Canonical!;
        RuntimeEntityKey secondKey = second.Key!.Value;
        Assert.True(lifetime.InitialCreateResidences.Forget(
            second, out _, out _));

        Assert.Contains(secondKey, lateBoundKeys);
    }

    private static void Bind(
        RuntimeEntityObjectLifetime lifetime,
        ulong generation)
    {
        var token = new RuntimeGenerationToken(generation);
        lifetime.BindEventContext(() => token, static () => 1UL);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort incarnation,
        bool missile = false,
        bool includePosition = true,
        uint? parentGuid = null,
        uint? setupId = 0x02000001u,
        ushort positionSequence = 1,
        float positionX = 10f,
        uint positionCell = Cell,
        ushort teleportSequence = 0,
        ushort forcePositionSequence = 0)
    {
        CreateObject.ServerPosition? position = includePosition
            ? new CreateObject.ServerPosition(
                positionCell,
                positionX,
                20f,
                7f,
                1f,
                0f,
                0f,
                0f)
            : null;
        uint rawState = (uint)(PhysicsStateFlags.Gravity
            | (missile ? PhysicsStateFlags.Missile : 0));
        var timestamps = new PhysicsTimestamps(
            Position: positionSequence,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: teleportSequence,
            ServerControlledMove: 1,
            ForcePosition: forcePositionSequence,
            ObjDesc: 1,
            Instance: incarnation);
        var physics = new PhysicsSpawnData(
            rawState,
            position,
            Movement: null,
            AnimationFrame: null,
            setupId,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: parentGuid is { } parent
                ? new PhysicsAttachment(parent, 1u)
                : null,
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
            setupId,
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
