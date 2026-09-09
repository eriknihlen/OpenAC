using System.Collections.Immutable;
using System.Numerics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeRemoteFirstEntryStateTests
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
    public void FullSequenceRemoteEntryConstructsGatedBodyPlacesAndCompletes()
    {
        using var fixture = new Fixture(residentWorld: true);

        RuntimeRemoteFirstEntryStatus status = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt,
            out RuntimeRemoteBodyConstructionReceipt construction);

        Assert.Equal(RuntimeRemoteFirstEntryStatus.Completed, status);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Equal(RuntimeTeleportHookPhase.None, receipt.TeleportHookPhase);
        Assert.True(construction.MotionTableGatePassed);
        Assert.Equal(0x09000001u, construction.MotionTableId);
        Assert.False(construction.MovementBranch);
        Assert.True(construction.PlacementFrameStaged);
        Assert.True(construction.FrictionApplied);
        Assert.Equal(0.5f, construction.Friction);
        Assert.False(construction.TranslucencyApplied);
        Assert.Equal(0f, construction.TranslucencyOriginal);
        Assert.True(construction.VelocityApplied);
        Assert.True(construction.OmegaApplied);
        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.True(body.InWorld);
        Assert.Equal(Cell, fixture.Record.FullCellId);
        Assert.Equal(0.5f, body.Friction);
        Assert.Equal(0.05f, body.Elasticity);
        Assert.Equal(fixture.Record.FinalPhysicsState, body.State);
        Assert.Equal(new Vector3(0f, 0f, 0.25f), body.Omega);
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.False(fixture.Conductor.TryGetConstruction(
            fixture.Key, out _));
        Assert.False(fixture.Lifetime.TryGetInitialCreateResidence(
            fixture.Record, out _));
        Assert.Equal(0, fixture.Lifetime.CaptureOwnership()
            .InitialCreateResidenceLeaseCount);
        Assert.Equal(0, fixture.Lifetime.CaptureOwnership()
            .InitialCreateExecutorProgressCount);
        Assert.Equal(0, fixture.Lifetime.CaptureOwnership()
            .RemoteFirstEntryActiveCount);

        // Retrying with the now-stale residence token is a distinct, safe
        // no-op — nothing left to resume.
        Assert.Equal(
            RuntimeRemoteFirstEntryStatus.RejectedToken,
            fixture.Advance(out _));
    }

    [Fact]
    public void ProjectileFlavorFullSequenceCompletesWithMissileState()
    {
        using var fixture = new Fixture(
            residentWorld: true,
            rawState: (uint)(PhysicsStateFlags.Gravity
                | PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.Missile
                | PhysicsStateFlags.Inelastic
                | PhysicsStateFlags.PathClipped
                | PhysicsStateFlags.AlignPath));

        Assert.True(fixture.Lifetime.TryGetInitialCreateResidence(
            fixture.Record, out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(
            RuntimeSetPositionOperationKind.ProjectileAuthoritative,
            lease.Route.OperationKind);

        RuntimeRemoteFirstEntryStatus status = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.Equal(RuntimeRemoteFirstEntryStatus.Completed, status);
        Assert.Equal(Cell, receipt.FullCellId);
        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.True(body.InWorld);
        Assert.True(body.State.HasFlag(PhysicsStateFlags.Missile));
        Assert.Equal(fixture.Record.FinalPhysicsState, body.State);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }


    [Theory]
    [InlineData(-1f, false, 0.95f)]
    [InlineData(0f, true, 0f)]
    [InlineData(0.5f, true, 0.5f)]
    [InlineData(1f, true, 1f)]
    [InlineData(1.5f, false, 0.95f)]
    [InlineData(float.NaN, false, 0.95f)]
    public void FrictionGateAppliesOnlyInsideClosedUnitInterval(
        float wireFriction,
        bool expectedApplied,
        float expectedBodyFriction)
    {
        (PhysicsBody body, RuntimeRemoteBodyConstructionReceipt receipt) =
            ConstructDirect(friction: wireFriction);

        Assert.Equal(expectedApplied, receipt.FrictionApplied);
        Assert.Equal(expectedBodyFriction, body.Friction);
        Assert.Equal(body.Friction, receipt.Friction);
    }

    [Fact]
    public void FrictionAbsentOnWireTakesTheDescConstructorDefault()
    {
        (PhysicsBody body, RuntimeRemoteBodyConstructionReceipt receipt) =
            ConstructDirect(friction: null);

        Assert.True(receipt.FrictionApplied);
        Assert.Equal(0.95f, body.Friction);
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.5f, true)]
    [InlineData(-0.25f, true)]
    [InlineData(float.NaN, true)]
    public void TranslucencyGateAppliesOnlyWhenNonZeroAndOriginalIsAlwaysRecorded(
        float wireTranslucency,
        bool expectedApplied)
    {
        (_, RuntimeRemoteBodyConstructionReceipt receipt) =
            ConstructDirect(translucency: wireTranslucency);

        Assert.Equal(wireTranslucency, receipt.TranslucencyOriginal);
        Assert.Equal(expectedApplied, receipt.TranslucencyApplied);
    }

    [Fact]
    public void TranslucencyAbsentOnWireIsZeroOriginalAndNoLiveApply()
    {
        (_, RuntimeRemoteBodyConstructionReceipt receipt) =
            ConstructDirect(translucency: null);

        Assert.Equal(0f, receipt.TranslucencyOriginal);
        Assert.False(receipt.TranslucencyApplied);
    }

    [Theory]
    [InlineData(-0.5f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.05f, 0.05f)]
    [InlineData(0.1f, 0.1f)]
    [InlineData(0.2f, 0.1f)]
    [InlineData(float.NaN, 0f)]
    public void ElasticityClampMatchesRetailSetter(
        float wireElasticity,
        float expectedBodyElasticity)
    {
        (PhysicsBody body, RuntimeRemoteBodyConstructionReceipt receipt) =
            ConstructDirect(elasticity: wireElasticity);

        Assert.Equal(expectedBodyElasticity, body.Elasticity);
        Assert.Equal(body.Elasticity, receipt.Elasticity);
    }

    // ---------------------------------------------------------------
    // Placement-frame-vs-movement branch (set_description step 4)
    // ---------------------------------------------------------------

    [Fact]
    public void MovementPayloadSuppressesPlacementFrameAndWritesAutonomy()
    {
        (PhysicsBody body, RuntimeRemoteBodyConstructionReceipt receipt) =
            ConstructDirect(movement: new PhysicsMovementData(
                RawData: new byte[] { 0x01 },
                MotionState: null,
                IsAutonomous: true));

        Assert.True(receipt.MovementBranch);
        Assert.True(receipt.LastMoveWasAutonomous);
        Assert.True(body.LastMoveWasAutonomous);
        Assert.False(receipt.PlacementFrameStaged);
        Assert.Equal(0u, body.CellPosition.ObjCellId);
        Assert.False(body.InWorld);
    }

    [Fact]
    public void MovementFlagWithEmptyBufferTakesThePlacementBranch()
    {
        (PhysicsBody body, RuntimeRemoteBodyConstructionReceipt receipt) =
            ConstructDirect(movement: new PhysicsMovementData(
                RawData: ReadOnlyMemory<byte>.Empty,
                MotionState: null,
                IsAutonomous: true));

        Assert.False(receipt.MovementBranch);
        Assert.True(receipt.PlacementFrameStaged);
        Assert.Equal(Cell, body.CellPosition.ObjCellId);
        Assert.False(body.InWorld);
        Assert.False(receipt.LastMoveWasAutonomous);
        Assert.False(body.LastMoveWasAutonomous);
    }

    [Fact]
    public void NoMovementPayloadStagesTheDormantPlacementFrame()
    {
        (PhysicsBody body, RuntimeRemoteBodyConstructionReceipt receipt) =
            ConstructDirect(movement: null);

        Assert.False(receipt.MovementBranch);
        Assert.True(receipt.PlacementFrameStaged);
        Assert.Equal(Cell, body.CellPosition.ObjCellId);
        // SetPlacementFrameInternal is NOT enter_world — the submission owns
        // world residence.
        Assert.False(body.InWorld);
        Assert.False(body.LastMoveWasAutonomous);
    }

    // ---------------------------------------------------------------
    // Yield flavors + retry idempotency per stage
    // ---------------------------------------------------------------

    [Fact]
    public void AwaitingCollisionSourceRetriesThenResumesOnceSetupLands()
    {
        using var fixture = new Fixture(residentWorld: true, setupTableId: SetupId);
        fixture.CollisionSource.Status = PreparedAssetReadStatus.Missing;

        RuntimeRemoteFirstEntryStatus first = fixture.Advance(out _);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.AwaitingCollisionSource, first);
        // No progress entry, no body, nothing mutated while the Setup is
        // outstanding.
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Null(fixture.Record.PhysicsBody);

        RuntimeRemoteFirstEntryStatus second = fixture.Advance(out _);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.AwaitingCollisionSource, second);
        Assert.True(fixture.CollisionSource.ReadCount >= 2);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Null(fixture.Record.PhysicsBody);

        fixture.CollisionSource.Status = PreparedAssetReadStatus.Loaded;
        Assert.Equal(RuntimeRemoteFirstEntryStatus.Completed,
            fixture.Advance(out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.NotNull(fixture.Record.PhysicsBody);
    }

    [Fact]
    public void DeferredPlacementRetainsOneBodyIdentityAcrossRetriesThenWakesAndCompletes()
    {
        using var fixture = new Fixture(residentWorld: false);

        RuntimeRemoteFirstEntryStatus first = fixture.Advance(out _);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.AwaitingPlacement, first);
        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.False(body.InWorld);
        Assert.Equal(0u, fixture.Record.FullCellId);
        Assert.True(fixture.Conductor.TryGetConstruction(
            fixture.Key, out RuntimeRemoteBodyConstructionReceipt construction));
        Assert.True(construction.FrictionApplied);
        Assert.Equal(0.5f, body.Friction);
        Assert.True(construction.VelocityApplied);
        Assert.Equal(new Vector3(1f, 2f, 0.5f), body.Velocity);
        Assert.True(construction.OmegaApplied);
        Assert.Equal(1, fixture.Conductor.CaptureOwnership().ActiveCount);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(RuntimeRemoteFirstEntryStatus.AwaitingPlacement,
                fixture.Advance(out _));
            Assert.Same(body, fixture.Record.PhysicsBody);
            Assert.Equal(1, fixture.Conductor.CaptureOwnership().ActiveCount);
        }

        const ulong generation = 1UL;
        fixture.Lifetime.Physics.SetPosition.BeginCollisionGeneration(
            Landblock, generation);
        fixture.Lifetime.Physics.Engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        fixture.Lifetime.Physics.SetPosition.CommitCollisionGeneration(
            Landblock, generation, ready: true);

        Assert.Equal(RuntimeRemoteFirstEntryStatus.Completed,
            fixture.Advance(out RuntimeInitialCreateExecutionReceipt receipt));
        Assert.Same(body, fixture.Record.PhysicsBody);
        Assert.True(body.InWorld);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Equal(Cell, fixture.Record.FullCellId);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }

    [Fact]
    public void ReentrantAdvanceDuringCollisionCallbackFailsClosedWithContentionAndOuterCallStillCompletes()
    {
        using var fixture = new Fixture(residentWorld: true);
        bool reentered = false;
        RuntimeRemoteFirstEntryStatus? innerStatus = null;
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

        RuntimeRemoteFirstEntryStatus outerStatus = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt);

        Assert.True(reentered);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.Contention, innerStatus);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.Completed, outerStatus);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }

    [Fact]
    public void AwaitingReceiptAcknowledgementWhileAnotherEntityHoldsTheFifoHeadThenResumes()
    {
        using var fixture = new Fixture(residentWorld: true);
        (RuntimeEntityRecord other, RuntimePlacementProjectionToken otherToken) =
            BeginPendingOrdinaryPlacement(fixture, 0x70099001u);

        RuntimeRemoteFirstEntryStatus first = fixture.Advance(out _);
        Assert.Equal(
            RuntimeRemoteFirstEntryStatus.AwaitingReceiptAcknowledgement,
            first);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);

        RuntimeRemoteFirstEntryStatus second = fixture.Advance(out _);
        Assert.Equal(
            RuntimeRemoteFirstEntryStatus.AwaitingReceiptAcknowledgement,
            second);

        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(otherToken));

        Assert.Equal(RuntimeRemoteFirstEntryStatus.Completed,
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
            RuntimeRemoteFirstEntryStatus.AwaitingReceiptAcknowledgement,
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

        Assert.Equal(RuntimeRemoteFirstEntryStatus.RejectedAuthority,
            fixture.Advance(out _));
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);

        // The unrelated entity's own placement is untouched and still
        // acknowledgeable — the abandonment never reached past our own
        // entity's projection.
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
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        RuntimeRemoteFirstEntryStatus status = fixture.Advance(out _);
        Assert.Equal(
            RuntimeRemoteFirstEntryStatus.AwaitingContinuationPlacement,
            status);
        Assert.Equal(1, fixture.Conductor.CaptureOwnership().ActiveCount);

        RuntimeEntityKey key = fixture.Key;
        Assert.True(fixture.Lifetime.InitialCreateExecution
            .TryGetPendingContinuationPlacement(
                key, out RuntimeEntityPlacementToken placement));
        Assert.True(fixture.Lifetime.InitialCreateExecution
            .TryGetPendingContinuationRoute(
                key, out RuntimeAuthoritativePositionRoute route));
        CompleteOrdinaryPlacement(fixture, placement, route);

        RuntimeRemoteFirstEntryStatus resumed = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.Completed, resumed);
        Assert.Contains(receipt.Trace,
            a => a.Kind is RuntimeInitialCreateExecutedActionKind.Position);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }


    [Fact]
    public void LocalPlayerLeaseIsRefusedWithoutTrackingAnything()
    {
        using var fixture = new Fixture(residentWorld: true, isLocalPlayer: true);

        Assert.Equal(RuntimeRemoteFirstEntryStatus.RejectedToken,
            fixture.Advance(out _));
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Null(fixture.Record.PhysicsBody);
        Assert.True(fixture.Lifetime.TryGetInitialCreateResidence(
            fixture.Record, out RuntimeInitialCreateResidenceLease lease));
        Assert.Equal(fixture.Lease.Token, lease.Token);
    }

    [Fact]
    public void ForeignBodyBoundOutOfBandAbandonsWithoutClobbering()
    {
        using var fixture = new Fixture(residentWorld: true);
        var foreign = new PhysicsBody
        {
            Position = new Vector3(1f, 2f, 3f),
            State = fixture.Record.FinalPhysicsState,
        };
        fixture.Lifetime.Entities.SetPhysicsBody(fixture.Record, foreign);

        Assert.Equal(RuntimeRemoteFirstEntryStatus.RejectedAuthority,
            fixture.Advance(out _));
        // The foreign body was never replaced or mutated toward ours.
        Assert.Same(foreign, fixture.Record.PhysicsBody);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }


    [Fact]
    public void DeleteWhileAwaitingPlacementConvergesAutomaticallyThroughTheRetirementFanOut()
    {
        using var fixture = new Fixture(residentWorld: false);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.AwaitingPlacement,
            fixture.Advance(out _));
        Assert.Equal(1, fixture.Conductor.CaptureOwnership().ActiveCount);

        DeleteEntity(fixture);
        Assert.Null(fixture.Record.Key);

        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);

        // Retrying Advance afterward is a safe, distinct no-op.
        Assert.Equal(RuntimeRemoteFirstEntryStatus.RejectedToken,
            fixture.Advance(out _));
    }

    [Fact]
    public void SessionClearMidFlightConvergesOwnership()
    {
        using var fixture = new Fixture(residentWorld: false);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.AwaitingPlacement,
            fixture.Advance(out _));
        Assert.Equal(1, fixture.Conductor.CaptureOwnership().ActiveCount);

        _ = fixture.Lifetime.BeginSessionClear();

        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
        Assert.Equal(0, fixture.Lifetime.CaptureOwnership()
            .RemoteFirstEntryActiveCount);
    }

    [Fact]
    public void DeleteAndSameGuidReincarnationStartsAFreshSequence()
    {
        using var fixture = new Fixture(residentWorld: false);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.AwaitingPlacement,
            fixture.Advance(out _));
        RuntimeEntityKey staleKey = fixture.Key;
        uint guid = fixture.Record.ServerGuid;

        DeleteEntity(fixture);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);

        RuntimeEntityRecord reincarnated = fixture.Lifetime
            .RegisterEntityWithInitialResidence(
                Spawn(guid, incarnation: 2),
                isLocalPlayer: false)
            .Canonical!;
        Assert.NotEqual(staleKey, reincarnated.Key!.Value);
        Assert.True(fixture.Lifetime.TryGetInitialCreateResidence(
            reincarnated,
            out RuntimeInitialCreateResidenceLease freshLease));
        fixture.Record = reincarnated;
        fixture.Lease = freshLease;

        Assert.Equal(RuntimeRemoteFirstEntryStatus.AwaitingPlacement,
            fixture.Advance(out _));
        const ulong generation = 1UL;
        fixture.Lifetime.Physics.SetPosition.BeginCollisionGeneration(
            Landblock, generation);
        fixture.Lifetime.Physics.Engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        fixture.Lifetime.Physics.SetPosition.CommitCollisionGeneration(
            Landblock, generation, ready: true);

        RuntimeRemoteFirstEntryStatus status = fixture.Advance(
            out RuntimeInitialCreateExecutionReceipt receipt);
        Assert.Equal(RuntimeRemoteFirstEntryStatus.Completed, status);
        Assert.Equal(Cell, receipt.FullCellId);
        Assert.NotNull(reincarnated.PhysicsBody);
        Assert.True(reincarnated.PhysicsBody!.InWorld);
        Assert.Equal(0, fixture.Conductor.CaptureOwnership().ActiveCount);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static (PhysicsBody Body, RuntimeRemoteBodyConstructionReceipt Receipt)
        ConstructDirect(
            float? friction = 0.5f,
            float? elasticity = 0.05f,
            float? translucency = null,
            PhysicsMovementData? movement = null)
    {
        var record = new RuntimeEntityRecordFactory();
        RuntimeEntityRecord canonical = record.Lifetime
            .RegisterEntity(
                Spawn(
                    0x70090077u,
                    incarnation: 1,
                    friction: friction,
                    elasticity: elasticity,
                    translucency: translucency,
                    movement: movement))
            .Canonical!;
        var command = new RuntimeSetPositionCommand(
            new PhysicsSetPositionRequest(
                new Vector3(1f, 2f, 3f),
                Quaternion.Identity,
                Cell,
                new Vector3(1f, 2f, 3f),
                ImmutableArray<FlatCollisionSphere>.Empty,
                Scale: 1f,
                StepUpHeight: 0f,
                StepDownHeight: 0f,
                canonical.FinalPhysicsState,
                ObjectInfoState.None,
                canonical.Key?.LocalEntityId ?? 0u,
                PhysicsPlacementClass.Ordinary,
                PhysicsSetPositionFlags.Placement | PhysicsSetPositionFlags.Slide),
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 1d,
            ExpectedVelocityAuthorityVersion: 0UL);

        PhysicsBody body = RuntimeRemoteBodyDescription.Construct(
            canonical,
            canonical.Snapshot.Physics,
            command,
            out RuntimeRemoteBodyConstructionReceipt receipt);
        record.Dispose();
        return (body, receipt);
    }

    private sealed class RuntimeEntityRecordFactory : IDisposable
    {
        internal RuntimeEntityObjectLifetime Lifetime { get; } = new();

        internal RuntimeEntityRecordFactory()
        {
            var generation = new RuntimeGenerationToken(1UL);
            Lifetime.BindEventContext(() => generation, static () => 1UL);
        }

        public void Dispose() => Lifetime.Dispose();
    }

    private static (RuntimeEntityRecord Record, RuntimePlacementProjectionToken Token)
        BeginPendingOrdinaryPlacement(Fixture fixture, uint guid)
    {
        RuntimeEntityRecord record = fixture.Lifetime.RegisterEntity(
            Spawn(guid, incarnation: 1)).Canonical!;
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
            new DeleteObject.Parsed(
                fixture.Record.ServerGuid, fixture.Record.Incarnation),
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
        uint setupTableId = 0u,
        uint rawState = (uint)(PhysicsStateFlags.Gravity
            | PhysicsStateFlags.ReportCollisions),
        float? friction = 0.5f,
        float? elasticity = 0.05f,
        float? translucency = null,
        PhysicsMovementData? movement = null)
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
            RawState: rawState,
            Position: position,
            Movement: movement,
            AnimationFrame: null,
            SetupTableId: setupTableId == 0u ? null : setupTableId,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: 1f,
            Friction: friction,
            Elasticity: elasticity,
            Translucency: translucency,
            Velocity: new Vector3(1f, 2f, 0.5f),
            Acceleration: null,
            AngularVelocity: new Vector3(0f, 0f, 0.25f),
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
            Name: "remote-entry-fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: 0x09000001u,
            PhysicsState: physics.RawState,
            ObjectDescriptionFlags: 0x8u,
            Friction: friction,
            Elasticity: elasticity,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
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
        internal Fixture(
            bool residentWorld,
            uint setupTableId = 0u,
            uint rawState = (uint)(PhysicsStateFlags.Gravity
                | PhysicsStateFlags.ReportCollisions),
            bool isLocalPlayer = false)
        {
            if (residentWorld)
            {
                var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
                engine.AddLandblock(
                    Landblock,
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

            Lifetime.Physics.ObserveLocalWorldFrame(
                Cell,
                teleportAdvanced: false);

            Conductor = Lifetime.RemoteFirstEntry;

            Record = Lifetime.RegisterEntityWithInitialResidence(
                Spawn(
                    0x70090002u,
                    incarnation: 1,
                    setupTableId: setupTableId,
                    rawState: rawState),
                isLocalPlayer).Canonical!;
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
        internal RuntimeRemoteFirstEntryState Conductor { get; }
        internal RuntimeEntityRecord Record { get; set; }
        internal RuntimeInitialCreateResidenceLease Lease { get; set; }
        internal FakeCollisionSource CollisionSource { get; }

        internal RuntimeEntityKey Key => Record.Key!.Value;

        internal RuntimeRemoteFirstEntryStatus Advance(
            out RuntimeInitialCreateExecutionReceipt receipt) =>
            Advance(out receipt, out _);

        internal RuntimeRemoteFirstEntryStatus Advance(
            out RuntimeInitialCreateExecutionReceipt receipt,
            out RuntimeRemoteBodyConstructionReceipt construction) =>
            Conductor.Advance(
                Record,
                Lease.Token,
                CollisionSource,
                gameTime: 10d,
                NoContact,
                out receipt,
                out construction);

        public void Dispose() => Lifetime.Dispose();
    }
}
