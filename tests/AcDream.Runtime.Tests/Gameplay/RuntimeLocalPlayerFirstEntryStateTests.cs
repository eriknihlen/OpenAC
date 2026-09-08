using System.Collections.Immutable;
using System.Numerics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeLocalPlayerFirstEntryStateTests
{
    private const uint Landblock = 0xA9B60000u;
    private const uint Cell = Landblock | 0x0001u;
    private const uint SetupId = 0x02000001u;
    private static readonly RuntimeInitialCreateExecutionInputs NoContact =
        new(UsePositionFromServer: false, PlayerDistance: 0f);

    // ---------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------

    [Fact]
    public void FullSequenceHappyPathReachesExecutorCompletedWithOneBodyIdentity()
    {
        using var fixture = new Fixture(residentWorld: true);

        RuntimeLocalPlayerFirstEntryStatus status = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.Completed, status);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Equal(RuntimeTeleportHookPhase.AfterEnterWorld,
            receipt.TeleportHookPhase);
        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.True(body.InWorld);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(fixture.Movement.Controller);
        Assert.Same(body, controller.PhysicsBody);
        Assert.True(controller.IsRuntimePublished);
        Assert.NotNull(fixture.Record.PhysicsHost);
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().CandidateCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().PendingActivationCount);
        Assert.False(fixture.Lifetime.TryGetInitialCreateResidence(
            fixture.Record, out _));
        Assert.Equal(0, fixture.Lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.Equal(0, fixture.Lifetime.CaptureOwnership()
            .InitialCreateExecutorProgressCount);

        // Retrying with the now-stale residence token is a distinct,
        // safe no-op — nothing left to resume.
        Assert.Equal(
            RuntimeLocalPlayerFirstEntryStatus.RejectedToken,
            fixture.Advance(out _));
    }

    // ---------------------------------------------------------------
    // Yield flavors + resume
    // ---------------------------------------------------------------

    [Fact]
    public void AwaitingCollisionSourceRetriesThenResumesOnceSetupLands()
    {
        using var fixture = new Fixture(residentWorld: true, setupTableId: SetupId);
        fixture.CollisionSource.Status = PreparedAssetReadStatus.Missing;

        RuntimeLocalPlayerFirstEntryStatus first = fixture.Advance(out _);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingCollisionSource,
            first);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().CandidateCount);

        RuntimeLocalPlayerFirstEntryStatus second = fixture.Advance(out _);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingCollisionSource,
            second);
        Assert.True(fixture.CollisionSource.ReadCount >= 2);

        fixture.CollisionSource.Status = PreparedAssetReadStatus.Loaded;
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.Completed,
            fixture.Advance(out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(Cell, receipt.FullCellId);
    }

    [Fact]
    public void AwaitingActivationRetriesWhileCellUnresolvedThenResumesAfterGenerationWake()
    {
        using var fixture = new Fixture(residentWorld: false);

        RuntimeLocalPlayerFirstEntryStatus first = fixture.Advance(out _);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation, first);
        Assert.Equal(1, fixture.Publication.CaptureOwnership().PendingActivationCount);
        Assert.False(fixture.Record.PhysicsBody!.InWorld);

        RuntimeLocalPlayerFirstEntryStatus second = fixture.Advance(out _);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation, second);
        Assert.Equal(1, fixture.Publication.CaptureOwnership().PendingActivationCount);

        CommitProductionCollisionGeneration(
            fixture.Lifetime.Physics,
            Cell & 0xFFFF0000u);

        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.Completed,
            fixture.Advance(out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.True(fixture.Record.PhysicsBody!.InWorld);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().PendingActivationCount);
    }

    [Fact]
    public void AwaitingReceiptAcknowledgementRetriesWhileNotFifoHeadThenResumes()
    {
        using var fixture = new Fixture(residentWorld: true);
        (RuntimeEntityRecord other, RuntimePlacementProjectionToken otherToken) =
            BeginPendingOrdinaryPlacement(fixture, 0x70099001u);

        RuntimeLocalPlayerFirstEntryStatus first = fixture.Advance(out _);
        Assert.Equal(
            RuntimeLocalPlayerFirstEntryStatus.AwaitingReceiptAcknowledgement,
            first);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().PendingActivationCount);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);

        RuntimeLocalPlayerFirstEntryStatus second = fixture.Advance(out _);
        Assert.Equal(
            RuntimeLocalPlayerFirstEntryStatus.AwaitingReceiptAcknowledgement,
            second);

        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(otherToken));

        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.Completed,
            fixture.Advance(out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(Cell, receipt.FullCellId);
        _ = other;
    }

    [Fact]
    public void DeleteWhileAwaitingReceiptAcknowledgementAbandonsInsteadOfRetryingForeverAndConverges()
    {
        using var fixture = new Fixture(residentWorld: true);
        (RuntimeEntityRecord other, RuntimePlacementProjectionToken otherToken) =
            BeginPendingOrdinaryPlacement(fixture, 0x70099002u);

        Assert.Equal(
            RuntimeLocalPlayerFirstEntryStatus.AwaitingReceiptAcknowledgement,
            fixture.Advance(out _));
        Assert.Equal(1, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);
        Assert.True(fixture.Lifetime.TryGetInitialCreateResidence(
            fixture.Record, out _));

        RuntimePlacementCancellationReceipt cancellation = fixture.Lifetime
            .Physics.SetPosition.Forget(
                fixture.Record,
                releasePreparedMover: true);
        fixture.Lifetime.Physics.SetPosition.PublishCancellation(cancellation);

        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority,
            fixture.Advance(out _));
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().PendingActivationCount);
        Assert.NotNull(fixture.Movement.Controller);
        Assert.True(fixture.Movement.Controller!.IsRuntimePublished);

        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(otherToken));
        _ = other;
    }

    [Fact]
    public void AwaitingContinuationPlacementPropagatesExecutorYieldThenResumes()
    {
        using var fixture = new Fixture(residentWorld: true);
        WorldSession.EntityPositionUpdate update = new(
            fixture.Record.ServerGuid,
            new CreateObject.ServerPosition(Cell, 40f, 20f, 7f, 1f, 0f, 0f, 0f),
            Velocity: null,
            PlacementId: 2,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 1,
            ForcePositionSequence: 0);
        Assert.True(fixture.Lifetime.TryApplyPosition(
            update,
            isLocalPlayer: true,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        RuntimeLocalPlayerFirstEntryStatus status = fixture.Advance(out _);
        Assert.Equal(
            RuntimeLocalPlayerFirstEntryStatus.AwaitingContinuationPlacement,
            status);
        Assert.Equal(1, fixture.Conductor.CaptureOwnership().ActiveCount);

        RuntimeEntityKey key = fixture.Record.Key!.Value;
        Assert.True(fixture.Lifetime.InitialCreateExecution
            .TryGetPendingContinuationPlacement(
                key, out RuntimeEntityPlacementToken placement));
        Assert.True(fixture.Lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(
                key, out RuntimeAuthoritativePositionRoute route));
        CompleteOrdinaryPlacement(fixture, placement, route);

        RuntimeLocalPlayerFirstEntryStatus resumed = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.Completed, resumed);
        Assert.Contains(receipt.Trace,
            a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }

    [Fact]
    public void ReentrantAdvanceDuringCollisionCallbackFailsClosedWithContentionAndOuterCallStillCompletes()
    {
        using var fixture = new Fixture(residentWorld: true);
        bool reentered = false;
        RuntimeLocalPlayerFirstEntryStatus? innerStatus = null;
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (_, phase, _, observed) =>
            {
                if (!reentered && phase is TransitionCellCollisionPhase.Environment)
                {
                    reentered = true;
                    innerStatus = fixture.Advance(out _);
                }
                return observed;
            };

        RuntimeLocalPlayerFirstEntryStatus outerStatus = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.True(reentered);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.Contention, innerStatus);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.Completed, outerStatus);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }

    // ---------------------------------------------------------------
    // Publication binding (H2)
    // ---------------------------------------------------------------

    [Fact]
    public void AdvanceWithUnboundPublicationThrowsTransactionallyBeforeAnyStateMutation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        var generation = new RuntimeGenerationToken(1UL);
        lifetime.BindEventContext(() => generation, static () => 1UL);
        RuntimeEntityRecord record = lifetime.RegisterEntityWithInitialResidence(
            Spawn(0x70090099u, incarnation: 1),
            isLocalPlayer: true).Canonical!;
        Assert.True(lifetime.TryGetInitialCreateResidence(
            record, out RuntimeInitialCreateResidenceLease lease));
        var collisionSource = new FakeCollisionSource(
            0u,
            new FlatSetupCollision(
                ImmutableArray<FlatCollisionCylinder>.Empty,
                [new FlatCollisionSphere(Vector3.Zero, 0.48f)],
                height: 0f,
                radius: 0f,
                stepUpHeight: 0.4f,
                stepDownHeight: 0.4f));

        Assert.Throws<InvalidOperationException>(() =>
            lifetime.LocalPlayerFirstEntry.Advance(
                record,
                lease.Token,
                PlayerMovementConstructionOptions.Fallback,
                new RuntimeLocalPlayerPhysicsActivationPreparation(
                    0.48f, 1.835f, RuntimeLocalPlayerShadowDisposition.ProvenShapeless),
                collisionSource,
                gameTime: 10d,
                NoContact,
                out _));

        Assert.Equal(0, lifetime.LocalPlayerFirstEntry.CaptureOwnership().ActiveCount);
        Assert.True(lifetime.TryGetInitialCreateResidence(
            record, out RuntimeInitialCreateResidenceLease stillLease));
        Assert.Equal(lease.Token, stillLease.Token);

        var movement = new RuntimeLocalPlayerMovementState();
        var identity = new RuntimeLocalPlayerIdentityState();
        try
        {
            identity.ServerGuid = record.ServerGuid;
            var publication = new RuntimeLocalPlayerPhysicsPublicationState(
                lifetime.Entities, lifetime.Physics, movement, identity);
            movement.AttachPhysicsPublication(publication);
            lifetime.LocalPlayerFirstEntry.BindPublication(publication);

            Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation,
                lifetime.LocalPlayerFirstEntry.Advance(
                    record,
                    lease.Token,
                    PlayerMovementConstructionOptions.Fallback,
                    new RuntimeLocalPlayerPhysicsActivationPreparation(
                        0.48f, 1.835f, RuntimeLocalPlayerShadowDisposition.ProvenShapeless),
                    collisionSource,
                    gameTime: 10d,
                    NoContact,
                    out _));
        }
        finally
        {
            lifetime.Dispose();
            movement.Dispose();
            identity.Dispose();
        }
    }

    [Fact]
    public void BindPublicationTwiceThrows()
    {
        using var fixture = new Fixture(residentWorld: false);
        Assert.Throws<InvalidOperationException>(
            () => fixture.Conductor.BindPublication(fixture.Publication));
    }

    // ---------------------------------------------------------------
    // Retry idempotency
    // ---------------------------------------------------------------

    [Fact]
    public void RetryAtMoverPreparationNeverCreatesAPublicationCandidate()
    {
        using var fixture = new Fixture(residentWorld: true, setupTableId: SetupId);
        fixture.CollisionSource.Status = PreparedAssetReadStatus.Missing;

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(
                RuntimeLocalPlayerFirstEntryStatus.AwaitingCollisionSource,
                fixture.Advance(out _));
            Assert.Equal(0, fixture.Publication.CaptureOwnership().CandidateCount);
            Assert.Null(fixture.Record.PhysicsBody);
            Assert.Null(fixture.Movement.Controller);
        }
    }

    [Fact]
    public void RetryAtAwaitingActivationNeverRecommitsPublicationOrDuplicatesTheBody()
    {
        using var fixture = new Fixture(residentWorld: false);
        PhysicsBody? body = null;
        PlayerMovementController? controller = null;

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(
                RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation,
                fixture.Advance(out _));
            Assert.Equal(0, fixture.Publication.CaptureOwnership().CandidateCount);
            Assert.Equal(1, fixture.Publication.CaptureOwnership().PendingActivationCount);
            body ??= fixture.Record.PhysicsBody;
            Assert.Same(body, fixture.Record.PhysicsBody);
            PlayerMovementController currentController =
                Assert.IsType<PlayerMovementController>(fixture.Movement.Controller);
            controller ??= currentController;
            Assert.Same(controller, currentController);
        }
    }


    [Fact]
    public void DeleteMidFlightDuringActivationAbandonsWithoutShadowOrPlaceAndConverges()
    {
        using var fixture = new Fixture(residentWorld: true);
        bool deleted = false;
        var placements = new List<RuntimePlacementDelta>();
        using IDisposable placementSubscription = fixture.Lifetime.Events
            .SubscribePlacement(new PlacementObserver(d => placements.Add(d)));
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (deleted || phase is not TransitionCellCollisionPhase.Environment)
                    return observed;
                deleted = true;
                DeleteEntity(fixture);
                return observed;
            };

        RuntimeLocalPlayerFirstEntryStatus status = fixture.Advance(out _);

        Assert.True(deleted);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority, status);
        Assert.DoesNotContain(placements,
            d => d.Placement.Kind is RuntimePlacementProjectionKind.Place);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().CandidateCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().PendingActivationCount);
        Assert.Null(fixture.Movement.Controller);
    }

    [Fact]
    public void DeleteWhileAwaitingActivationConvergesAutomaticallyThroughTheRetirementFanOut()
    {
        using var fixture = new Fixture(residentWorld: false);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation,
            fixture.Advance(out _));
        Assert.Equal(1, fixture.Publication.CaptureOwnership().PendingActivationCount);

        DeleteEntity(fixture);
        Assert.Null(fixture.Record.Key);

        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().PendingActivationCount);
        Assert.Null(fixture.Movement.Controller);

        // Retrying Advance afterward is a safe, distinct no-op.
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.RejectedToken,
            fixture.Advance(out _));
    }

    [Fact]
    public void MovementResetSessionMidFlightConvergesOwnershipThroughOrdinaryAdvance()
    {
        using var fixture = new Fixture(residentWorld: false);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation,
            fixture.Advance(out _));

        fixture.Movement.ResetSession();

        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.RejectedToken,
            fixture.Advance(out _));
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().PendingActivationCount);
    }

    // ---------------------------------------------------------------
    // GUID / LeaseId staleness
    // ---------------------------------------------------------------

    [Fact]
    public void DeleteAndSameGuidReincarnationAutomaticallyFreesThePublicationSlotForTheFreshIncarnation()
    {
        using var fixture = new Fixture(residentWorld: false);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation,
            fixture.Advance(out _));
        RuntimeEntityKey staleKey = fixture.Record.Key!.Value;
        Assert.Equal(1, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(1, fixture.Publication.CaptureOwnership().PendingActivationCount);

        uint guid = fixture.Record.ServerGuid;
        DeleteEntity(fixture);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(0, fixture.Publication.CaptureOwnership().PendingActivationCount);
        RuntimeEntityRecord reincarnated = fixture.Lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, incarnation: 2),
                isLocalPlayer: true)
            .Canonical!;
        // ReleaseLocalId (part of delete's teardown) returns the old
        // LocalEntityId to the free pool rather than pinning it per GUID, so
        // the reincarnation's key differs in BOTH fields, not just
        // Incarnation — either way it is a different _progress dictionary
        // key than the deleted incarnation's.
        Assert.NotEqual(staleKey, reincarnated.Key!.Value);
        fixture.Identity.ServerGuid = reincarnated.ServerGuid;
        Assert.True(fixture.Lifetime.TryGetInitialCreateResidence(
            reincarnated,
            out RuntimeInitialCreateResidenceLease freshLease));
        fixture.Record = reincarnated;
        fixture.Lease = freshLease;

        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.AwaitingActivation,
            fixture.Advance(out _));
        CommitProductionCollisionGeneration(
            fixture.Lifetime.Physics,
            Cell & 0xFFFF0000u);

        RuntimeLocalPlayerFirstEntryStatus status = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt);
        Assert.Equal(RuntimeLocalPlayerFirstEntryStatus.Completed, status);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Same(reincarnated, fixture.Record);
        Assert.Equal(reincarnated.Key!.Value.LocalEntityId,
            fixture.Movement.Controller!.LocalEntityId);
        Assert.Same(reincarnated.PhysicsBody, fixture.Movement.Controller.PhysicsBody);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static void CommitProductionCollisionGeneration(
        RuntimePhysicsState physics,
        uint landblockId)
    {
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(landblockId);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            new RuntimeLandblockCollisionAssets(
                landblockId,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                0f,
                0f,
                0u));
        for (int poll = 0; poll < 10_000; poll++)
        {
            while (physics.SetPosition.TryPeekProjection(
                       out RuntimePlacementProjectionSnapshot projection))
            {
                Assert.True(physics.SetPosition.AcknowledgeProjection(
                    projection.Token));
            }
            while (!physics.AdvanceCollisionRetainedOwnerCapture(
                       admission,
                       prepared).Completed)
            {
            }
            foreach (uint ownerId in prepared.RetainedOwnerIds)
                physics.RefreshCollisionRetainedOwner(admission, prepared, ownerId);
            RuntimeCollisionSealStep seal;
            do
            {
                seal = physics.AdvanceCollisionGenerationSeal(admission, prepared);
            }
            while (!seal.Completed && !seal.Restarted);
            if (!seal.Completed)
                continue;
            if (physics.CommitCollisionGeneration(admission, prepared).Completed)
                return;
        }

        throw new InvalidOperationException(
            "Collision generation did not complete its Runtime mutation transaction.");
    }

    private static (RuntimeEntityRecord Record, RuntimePlacementProjectionToken Token)
        BeginPendingOrdinaryPlacement(Fixture fixture, uint guid)
    {
        RuntimeEntityRecord record = fixture.Lifetime.RegisterEntity(
            Spawn(guid, incarnation: 1, includePosition: true)).Canonical!;
        var body = new PhysicsBody
        {
            Position = new Vector3(50f, 50f, 3f),
            Orientation = Quaternion.Identity,
            State = record.FinalPhysicsState,
        };
        body.SnapToCell(Cell, body.Position, body.Position);
        fixture.Lifetime.Entities.SetPhysicsBody(record, body);
        RuntimeEntityPlacementToken placement = fixture.Lifetime.Physics
            .SetPosition.BeginAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(placement.IsValid);
        var preparation = new RuntimeSetPositionMoverPreparation(
            RuntimeSetPositionMoverSetup.ResolvedAbsent,
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 1d,
            PhysicsPlacementClass.Ordinary,
            PhysicsSetPositionFlags.Placement | PhysicsSetPositionFlags.Slide);
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            fixture.Lifetime.Physics.SetPosition.PrepareMover(
                placement, preparation, out RuntimeSetPositionCommand command));
        RuntimeSetPositionOutcome outcome = fixture.Lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(placement, command);
        Assert.Equal(RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        return (record, outcome.Projection);
    }

    private static void CompleteOrdinaryPlacement(
        Fixture fixture,
        in RuntimeEntityPlacementToken placement,
        in RuntimeAuthoritativePositionRoute route)
    {
        var preparation = new RuntimeSetPositionMoverPreparation(
            RuntimeSetPositionMoverSetup.ResolvedAbsent,
            route.OperationKind,
            GameTime: 1d,
            PhysicsPlacementClass.Ordinary,
            route.SetPositionFlags);
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            fixture.Lifetime.Physics.SetPosition.PrepareMover(
                placement, preparation, out RuntimeSetPositionCommand command));
        RuntimeSetPositionOutcome outcome = fixture.Lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(placement, command);
        Assert.Equal(RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(outcome.Projection));
    }

    private static void DeleteEntity(Fixture fixture)
    {
        Assert.True(fixture.Lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(fixture.Record.ServerGuid, fixture.Record.Incarnation),
            isLocalPlayer: false,
            removeRetainedObject: false,
            out RuntimeEntityDeleteAcceptance acceptance));
        fixture.Lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(fixture.Lifetime.RetireCanonicalOnly(fixture.Record));
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort incarnation,
        bool includePosition = true,
        uint setupTableId = 0u)
    {
        CreateObject.ServerPosition? position = includePosition
            ? new CreateObject.ServerPosition(Cell, 1f, 2f, 3f, 1f, 0f, 0f, 0f)
            : null;
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: incarnation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)(PhysicsStateFlags.Gravity
                | PhysicsStateFlags.ReportCollisions),
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: setupTableId == 0u ? null : setupTableId,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: 1f,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            Guid: guid,
            Position: position,
            SetupTableId: setupTableId == 0u ? null : setupTableId,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: 1f,
            Name: "first-entry-fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: 0x09000001u,
            PhysicsState: physics.RawState,
            ObjectDescriptionFlags: 0x8u,
            Friction: null,
            Elasticity: null,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class PlacementObserver(Action<RuntimePlacementDelta> onPlacement)
        : IRuntimePlacementObserver
    {
        public void OnPlacement(in RuntimePlacementDelta delta) => onPlacement(delta);
    }

    private sealed class FakeCollisionSource(
        uint expectedSetupTableId,
        FlatSetupCollision setup) : IPreparedCollisionSource
    {
        internal int ReadCount { get; private set; }
        internal PreparedAssetReadStatus Status { get; set; } =
            PreparedAssetReadStatus.Loaded;

        public PreparedAssetPresence ProbeCollision(
            PakAssetType type, uint sourceFileId) =>
            PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            Assert.Equal(expectedSetupTableId, sourceFileId);
            return Status switch
            {
                PreparedAssetReadStatus.Loaded =>
                    PreparedCollisionReadResult<FlatSetupCollision>.Loaded(setup),
                PreparedAssetReadStatus.Corrupt =>
                    PreparedCollisionReadResult<FlatSetupCollision>.Corrupt,
                _ => PreparedCollisionReadResult<FlatSetupCollision>.Missing,
            };
        }

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Only ReadSetupCollision is exercised by these tests.");

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Only ReadSetupCollision is exercised by these tests.");

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Only ReadSetupCollision is exercised by these tests.");

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(bool residentWorld, uint setupTableId = 0u)
        {
            if (residentWorld)
            {
                var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
                engine.AddLandblock(
                    Cell & 0xFFFF0000u,
                    new TerrainSurface(new byte[81], new float[256]),
                    Array.Empty<CellSurface>(),
                    Array.Empty<PortalPlane>(),
                    worldOffsetX: 0f,
                    worldOffsetY: 0f);
                Lifetime = new RuntimeEntityObjectLifetime(engine);
            }
            else
            {
                Lifetime = new RuntimeEntityObjectLifetime();
            }
            var generation = new RuntimeGenerationToken(1UL);
            Lifetime.BindEventContext(() => generation, static () => 1UL);

            Movement = new RuntimeLocalPlayerMovementState();
            Identity = new RuntimeLocalPlayerIdentityState();
            Publication = new RuntimeLocalPlayerPhysicsPublicationState(
                Lifetime.Entities, Lifetime.Physics, Movement, Identity);
            Movement.AttachPhysicsPublication(Publication);
            Conductor = Lifetime.LocalPlayerFirstEntry;
            Conductor.BindPublication(Publication);

            Record = Lifetime.RegisterEntityWithInitialResidence(
                Spawn(0x70090001u, incarnation: 1, setupTableId: setupTableId),
                isLocalPlayer: true).Canonical!;
            Identity.ServerGuid = Record.ServerGuid;
            Assert.True(Lifetime.TryGetInitialCreateResidence(
                Record, out RuntimeInitialCreateResidenceLease lease));
            Lease = lease;

            CollisionSource = new FakeCollisionSource(
                setupTableId,
                new FlatSetupCollision(
                    ImmutableArray<FlatCollisionCylinder>.Empty,
                    [new FlatCollisionSphere(Vector3.Zero, 0.48f)],
                    height: 0f,
                    radius: 0f,
                    stepUpHeight: 0.4f,
                    stepDownHeight: 0.4f));
        }

        internal RuntimeEntityObjectLifetime Lifetime { get; }
        internal RuntimeLocalPlayerMovementState Movement { get; }
        internal RuntimeLocalPlayerIdentityState Identity { get; }
        internal RuntimeLocalPlayerPhysicsPublicationState Publication { get; }
        internal RuntimeLocalPlayerFirstEntryState Conductor { get; }
        internal RuntimeEntityRecord Record { get; set; }
        internal RuntimeInitialCreateResidenceLease Lease { get; set; }
        internal FakeCollisionSource CollisionSource { get; }

        internal RuntimeLocalPlayerFirstEntryStatus Advance(
            out RuntimeInitialCreateExecutionReceipt receipt) =>
            Conductor.Advance(
                Record,
                Lease.Token,
                PlayerMovementConstructionOptions.Fallback,
                new RuntimeLocalPlayerPhysicsActivationPreparation(
                    Radius: 0.48f,
                    Height: 1.835f,
                    RuntimeLocalPlayerShadowDisposition.ProvenShapeless),
                CollisionSource,
                gameTime: 10d,
                NoContact,
                out receipt);

        public void Dispose()
        {
            // F2: RuntimeEntityObjectLifetime's own Dispose runs
            // BeginSessionClear, which now reaches
            // LocalPlayerFirstEntry.DiscardAll() -> Publication.Discard for
            // any still-tracked entity — Publication must still be alive for
            // that. Lifetime must therefore be disposed BEFORE Movement
            // (whose Dispose tears down Publication), never after.
            Lifetime.Dispose();
            Movement.Dispose();
            Identity.Dispose();
        }
    }
}
