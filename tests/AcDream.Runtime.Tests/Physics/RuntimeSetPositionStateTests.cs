using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World.Cells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeSetPositionStateTests
{
    private const uint SourceLandblock = 0xA9B40000u;
    private const uint SourceCell = SourceLandblock | 0x0001u;
    private const uint DestinationLandblock = 0xAAB40000u;
    private const uint DestinationCell = DestinationLandblock | 0x0001u;
    private const uint DestinationIndoorCell = DestinationLandblock | 0x0100u;

    [Fact]
    public void AcceptedTokenExistsBeforeHostPreparationAndRejectsInvalidPortalAuthority()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001001u, 1);

        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);

        Assert.True(token.IsValid);
        Assert.Equal(record.Key, token.Entity);
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);

        var invalidPortal = new RuntimePortalPlacementAuthority(
            Present: true,
            RevealGeneration: 7,
            TeleportSequence: 0,
            new RuntimeWorldHostProjectionToken(7, DestinationCell));
        Assert.False(lifetime.Physics.SetPosition.BeginAcceptedPlacement(
            record,
            record.PositionAuthorityVersion,
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            invalidPortal).IsValid);

        RuntimeEntityPlacementToken portalToken = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative,
                invalidPortal);
        Assert.False(portalToken.IsValid);

        var matchingPortal = invalidPortal with
        {
            Projection = new RuntimeWorldHostProjectionToken(7, SourceCell),
        };
        Assert.True(lifetime.Physics.SetPosition.BeginAuthoredPlacement(
            record,
            record.PositionAuthorityVersion,
            RuntimeSetPositionOperationKind.LocalAuthoritative,
            matchingPortal).IsValid);
    }

    [Fact]
    public void ImmediateCommitIsCanonicalBeforeProjectionAndExactTokenRetriesUntilAck()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001002u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver(delta =>
        {
            Assert.Equal(RuntimePlacementProjectionKind.Place,
                delta.Placement.Kind);
            Assert.Same(body, record.PhysicsBody);
            Assert.Equal(SourceCell, record.FullCellId);
            Assert.Equal(new Vector3(12f, 18f, 7f), body.Position);
            Assert.True(body.InWorld);
            Assert.True(lifetime.Physics.IsSpatialRoot(record));
        });
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(12f, 18f, 7f))));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Single(observer.Deltas);
        RuntimePlacementProjectionToken token = outcome.Projection;
        lifetime.Physics.SetPosition.RetryPendingProjections();
        Assert.Equal(2, observer.Deltas.Count);
        Assert.Equal(token, observer.Deltas[0].Placement.Token);
        Assert.Equal(token, observer.Deltas[1].Placement.Token);
        Assert.True(observer.Deltas[1].Stamp.Sequence
            > observer.Deltas[0].Stamp.Sequence);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(token));
        RuntimeSetPositionOwnershipSnapshot ownership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(0, ownership.ActiveOperationCount);
        Assert.Equal(0, ownership.PendingProjectionAcknowledgementCount);
        Assert.Equal(1, ownership.PreparedMoverCount);
    }

    [Fact]
    public void WarmedPendingProjectionRetryDoesNotAllocateSnapshotArray()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001042u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        var observer = new CountingPlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(13f, 18f, 7f))));

        // Prime both retained retry scratch and the event stream's dispatch
        // storage before measuring the normal N>0 per-tick path.
        lifetime.Physics.SetPosition.RetryPendingProjections();
        const int iterations = 256;
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < iterations; iteration++)
            lifetime.Physics.SetPosition.RetryPendingProjections();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated / iterations, 0L, 128L);
        Assert.Equal(iterations + 2, observer.Count);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void PendingProjectionRetryKeepsIndependentSnapshotWhenReentered()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001044u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        bool retrying = false;
        bool nested = false;
        var observer = new PlacementObserver(_ =>
        {
            if (!retrying || nested)
                return;
            nested = true;
            lifetime.Physics.SetPosition.RetryPendingProjections();
        });
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(13f, 19f, 7f))));

        retrying = true;
        lifetime.Physics.SetPosition.RetryPendingProjections();

        Assert.True(nested);
        Assert.Equal(3, observer.Deltas.Count);
        Assert.All(observer.Deltas,
            delta => Assert.Equal(outcome.Projection, delta.Placement.Token));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Theory]
    [InlineData(PhysicsStateFlags.Hidden)]
    [InlineData(PhysicsStateFlags.Hidden | PhysicsStateFlags.NoDraw)]
    public void HiddenObjectCommitsResidenceWhileSuppressingCollisionReports(
        PhysicsStateFlags suppressedState)
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001022u, 1);
        PhysicsStateFlags state = PhysicsStateFlags.Gravity | suppressedState;
        PhysicsBody body = AttachBody(lifetime, record, SourceCell, state);
        var collisionObserver = new CollisionReportObserver();
        using IDisposable collisionSubscription = lifetime.Physics
            .CollisionReports.Subscribe(collisionObserver);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(12f, 18f, 7f))));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.True(body.InWorld);
        Assert.Equal(SourceCell, record.FullCellId);
        Assert.Equal(new Vector3(12f, 18f, 7f), body.Position);
        Assert.True(lifetime.Physics.IsSpatialRoot(record));
        Assert.Empty(collisionObserver.Reports);
        RuntimeCollisionReportingOwnershipSnapshot reporting = lifetime
            .Physics.CollisionReports.CaptureOwnership();
        Assert.Equal(0, reporting.OwnerCount);
        Assert.Equal(0, reporting.PendingSetPositionDispatchCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void PublicPlacementChannelObservesRetriesAndAcknowledgesExactToken()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        var generation = new RuntimeGenerationToken(7UL);
        lifetime.BindEventContext(() => generation, static () => 11UL);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001012u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Placements.Subscribe(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(14f, 19f, 7f))));

        Assert.Single(observer.Deltas);
        Assert.True(lifetime.Placements.TryPeek(generation, out var pending));
        Assert.Equal(outcome.Projection, pending.Token);
        Assert.True(lifetime.Placements.RetryPending(generation));
        Assert.Equal(2, observer.Deltas.Count);
        Assert.Equal(
            observer.Deltas[0].Placement,
            observer.Deltas[1].Placement);

        RuntimePlacementProjectionToken stale = outcome.Projection with
        {
            Revision = outcome.Projection.Revision + 1UL,
        };
        Assert.False(lifetime.Placements.Acknowledge(generation, stale));
        Assert.False(lifetime.Placements.RetryPending(
            new RuntimeGenerationToken(8UL)));
        Assert.False(lifetime.Placements.TryPeek(
            new RuntimeGenerationToken(8UL),
            out _));
        Assert.True(lifetime.Placements.Acknowledge(
            generation,
            outcome.Projection));
        Assert.Equal(0, lifetime.Placements.PendingCount);
        Assert.False(lifetime.Placements.TryPeek(generation, out _));
        Assert.False(lifetime.Placements.Acknowledge(
            generation,
            outcome.Projection));
    }

    [Fact]
    public void InWorldSetPositionPreservesObjectClockEpochPendingTimeAndActiveState()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001110u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        body.LastUpdateTime = 3d;
        _ = record.ObjectClock.Advance(0.01d);
        ulong epoch = record.ObjectClockEpoch;

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(11f, 18f, 7f))));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(epoch, record.ObjectClockEpoch);
        Assert.Equal(3d, body.LastUpdateTime);
        Assert.Equal(0.01d, record.ObjectClock.PendingSeconds, precision: 12);
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(body.TransientState.HasFlag(TransientStateFlags.Active));
    }

    [Fact]
    public void InWorldSetPositionDoesNotWakeAnInactiveBody()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001111u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        body.LastUpdateTime = 4d;
        record.ObjectClock.Deactivate();
        body.TransientState &= ~TransientStateFlags.Active;
        ulong epoch = record.ObjectClockEpoch;

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(11f, 19f, 7f))));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(epoch, record.ObjectClockEpoch);
        Assert.Equal(4d, body.LastUpdateTime);
        Assert.False(record.ObjectClock.IsActive);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Active));
    }

    [Fact]
    public void WarmedImmediateCommitAllocationIsMeasuredBeforeProductionCutover()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000111Cu, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionCommand command = Command(Request(
            SourceCell,
            new Vector3(11f, 18f, 7f)));
        for (int warm = 0; warm < 64; warm++)
        {
            RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
                record,
                record.PositionAuthorityVersion,
                command);
            if (!lifetime.Physics.SetPosition.AcknowledgeProjection(
                    outcome.Projection))
            {
                throw new InvalidOperationException("Warmup acknowledgement failed.");
            }
        }

        const int iterations = 1_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
                record,
                record.PositionAuthorityVersion,
                command);
            if (!lifetime.Physics.SetPosition.AcknowledgeProjection(
                    outcome.Projection))
            {
                throw new InvalidOperationException("Measured acknowledgement failed.");
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated / iterations, 1L, 1_536L);
    }

    [Fact]
    public void MissingCrossLandblockCellParksExactBodyThenMatchingCellGenerationWakesIt()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        var gameClock = new GameRuntimeClock();
        _ = gameClock.Advance(35d);
        var time = new ManualTimeProvider(
            DateTimeOffset.UnixEpoch.AddSeconds(100d));
        using var lifetime = new RuntimeEntityObjectLifetime(
            engine,
            timeProvider: time,
            gameClock: gameClock);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001003u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var remote = new RemoteMotion(body)
        {
            HasServerVelocity = true,
            ServerVelocity = Vector3.UnitX,
        };
        lifetime.Physics.SetRemoteMotion(record, remote);
        body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable
            | TransientStateFlags.WaterContact
            | TransientStateFlags.Sliding;
        body.ContactPlaneValid = true;
        body.ContactPlaneIsWater = true;
        body.SlidingNormal = Vector3.UnitX;
        body.set_velocity(new Vector3(3f, 4f, 5f));
        engine.ShadowObjects.Register(
            record.Key!.Value.LocalEntityId,
            0x01000001u,
            body.Position,
            body.Orientation,
            0.48f,
            0f,
            0f,
            SourceLandblock,
            seedCellId: SourceCell,
            isStatic: false);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, deferred.Status);
        Assert.Equal(DestinationCell, deferred.ExactCellId);
        Assert.Same(body, record.PhysicsBody);
        Assert.Equal(DestinationCell, body.CellPosition.ObjCellId);
        Assert.Equal(0u, record.FullCellId);
        Assert.False(body.InWorld);
        Assert.Equal(1d, body.LastUpdateTime);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Active));
        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);
        Assert.True(body.ContactPlaneIsWater);
        Assert.Equal(Vector3.UnitX, body.SlidingNormal);
        Assert.Equal(new Vector3(3f, 4f, 5f), body.Velocity);
        Assert.False(lifetime.Physics.IsSpatialRoot(record));
        Assert.Equal(1, engine.ShadowObjects.SuspendedRegistrationCount);
        Assert.Equal(RuntimePlacementProjectionKind.Withdraw,
            Assert.Single(observer.Deltas).Placement.Kind);

        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));
        ulong suspendedEpoch = record.ObjectClockEpoch;
        Assert.False(record.ObjectClock.IsActive);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 2,
            ready: true);
        Assert.Single(observer.Deltas);

        AddFlatLandblock(engine, DestinationLandblock, 192f);
        _ = gameClock.Advance(5d);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 1,
            ready: true);

        Assert.Equal(2, observer.Deltas.Count);
        RuntimePlacementProjectionSnapshot placed = observer.Deltas[1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.Equal(DestinationCell, placed.Token.ExactCellId);
        Assert.Same(body, record.PhysicsBody);
        Assert.Equal(DestinationCell, record.FullCellId);
        Assert.True(body.InWorld);
        Assert.True(double.IsFinite(body.LastUpdateTime));
        Assert.Equal(40d, body.LastUpdateTime);
        Assert.Equal(suspendedEpoch + 1UL, record.ObjectClockEpoch);
        Assert.Equal(0d, record.ObjectClock.PendingSeconds);
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(body.TransientState.HasFlag(TransientStateFlags.Active));
        Assert.True(lifetime.Physics.IsSpatialRoot(record));
        Assert.Equal(100d, remote.LastServerPosTime);
        var remoteUpdater = new RuntimeRemotePhysicsUpdater(
            lifetime.Physics);
        Assert.True(remoteUpdater.Tick(
            record,
            remote,
            objectScale: 1f,
            sequencer: null,
            dt: 0.01f,
            objectClockEpoch: record.ObjectClockEpoch,
            new MotionDeltaFrame
            {
                Orientation = Quaternion.Identity,
            },
            radius: 0.48f,
            height: 1.835f,
            liveCenterX: 1,
            liveCenterY: 1));
        Assert.True(remote.HasServerVelocity);
        Assert.Equal(Vector3.UnitX, remote.ServerVelocity);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            placed.Token));
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
    }

    [Fact]
    public void FailedDeferredWakeRetainsLostOwnerDeadlineAndRetries()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001121u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        AddFlatLandblock(engine, DestinationLandblock, 192f);
        engine.TransitionCellCollisionTestHook =
            static (_, _, _, _) => TransitionState.Collided;
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 1,
            ready: true);

        RuntimeSetPositionOwnershipSnapshot failed = lifetime.Physics
            .SetPosition.CaptureOwnership();
        Assert.Equal(1, failed.ActiveOperationCount);
        Assert.Equal(1, failed.DeferredCellCount);
        Assert.Equal(1, failed.LostDeadlineCount);
        Assert.Equal(1, failed.DeferredBucketCount);
        Assert.True(failed.IndexesConsistent);
        Assert.Equal(0u, record.FullCellId);
        Assert.False(body.InWorld);
        Assert.Single(observer.Deltas);

        engine.TransitionCellCollisionTestHook = null;
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 1,
            ready: true);
        RuntimePlacementProjectionSnapshot placed = observer.Deltas[^1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.Equal(record.Key, placed.Token.Entity);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            placed.Token));
        Assert.Equal(DestinationCell, record.FullCellId);
        Assert.True(body.InWorld);
    }

    [Fact]
    public void StandaloneDeferredWakeRetainsAcceptedSimulationTimeFallback()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001126u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));
        AddFlatLandblock(engine, DestinationLandblock, 192f);

        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 1,
            ready: true);

        Assert.Equal(10d, body.LastUpdateTime);
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot placed));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            placed.Token));
    }

    [Fact]
    public void LegacyDeferredWakeCannotBypassNewAuthoredSealAfterAuthorityStales()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000112Bu, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(
            observer);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        lifetime.Entities.AdvanceObjDescAuthority(record);
        AddFlatLandblock(engine, DestinationLandblock, 192f);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 1,
            ready: true);

        Assert.False(body.InWorld);
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            record,
            out RuntimeEntityPlacementToken token));
        Assert.Equal(RuntimeEntityPlacementPreparationKind.LegacyDirect,
            token.PreparationKind);
        RuntimeSetPositionOutcome bypass = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, Command(CrossLandblockRequest()));
        Assert.Equal(RuntimeSetPositionStatus.Rejected, bypass.Status);
        Assert.False(body.InWorld);

        RuntimeSetPositionCommand prepared = PrepareAuthoredCommand(
            lifetime,
            token,
            [new FlatCollisionSphere(Vector3.Zero, 0.4f)]);
        RuntimeSetPositionOutcome resumed = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, prepared);
        Assert.Equal(PhysicsResidenceDisposition.Committed, resumed.Residence);
        RuntimePlacementProjectionSnapshot placed = observer.Deltas[^1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.True(body.InWorld);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            placed.Token));
    }

    [Theory]
    [InlineData(DeferredPhysicsStateMutation.ChildNoDraw)]
    [InlineData(DeferredPhysicsStateMutation.StopMissile)]
    [InlineData(DeferredPhysicsStateMutation.DirectFinalState)]
    public void DeferredWakeRequiresRepreparationAfterFinalPhysicsStateMutation(
        DeferredPhysicsStateMutation mutation)
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000112Cu, 1);
        PhysicsStateFlags initial = PhysicsStateFlags.Gravity;
        if (mutation is DeferredPhysicsStateMutation.StopMissile)
        {
            initial |= PhysicsStateFlags.Missile
                | PhysicsStateFlags.AlignPath
                | PhysicsStateFlags.PathClipped;
        }
        PhysicsBody body = AttachBody(lifetime, record, SourceCell, initial);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));
        ulong priorMutation = record.PhysicsStateMutationVersion;

        switch (mutation)
        {
            case DeferredPhysicsStateMutation.ChildNoDraw:
                lifetime.Entities.SetChildNoDraw(record, noDraw: true);
                break;
            case DeferredPhysicsStateMutation.StopMissile:
                Assert.True(lifetime.Entities.StopMissileAfterCollision(
                    record,
                    requireCurrentMissile: true));
                break;
            case DeferredPhysicsStateMutation.DirectFinalState:
                lifetime.Entities.SetFinalPhysicsState(
                    record,
                    record.FinalPhysicsState | PhysicsStateFlags.Frozen);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Equal(priorMutation + 1UL,
            record.PhysicsStateMutationVersion);
        if (mutation is DeferredPhysicsStateMutation.DirectFinalState)
            Assert.NotEqual(record.FinalPhysicsState, body.State);
        else
            Assert.Equal(record.FinalPhysicsState, body.State);

        AddFlatLandblock(engine, DestinationLandblock, 192f);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 1,
            ready: true);

        Assert.False(body.InWorld);
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            record,
            out _));
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);
    }

    [Fact]
    public void DirectFinalPhysicsStateMutationAdvancesOnlyOnChangeAndLeavesBodyToItsOwner()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000112Du, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        PhysicsStateFlags original = record.FinalPhysicsState;
        ulong version = record.PhysicsStateMutationVersion;

        lifetime.Entities.SetFinalPhysicsState(record, original);
        Assert.Equal(version, record.PhysicsStateMutationVersion);
        Assert.Equal(original, body.State);

        PhysicsStateFlags changed = original | PhysicsStateFlags.Frozen;
        lifetime.Entities.SetFinalPhysicsState(record, changed);
        Assert.Equal(version + 1UL, record.PhysicsStateMutationVersion);
        Assert.Equal(changed, record.FinalPhysicsState);
        Assert.Equal(original, body.State);

        lifetime.Entities.SetFinalPhysicsState(record, changed);
        Assert.Equal(version + 1UL, record.PhysicsStateMutationVersion);
        Assert.Equal(original, body.State);
    }

    [Fact]
    public void RejectedPreparationKeepsValidatedMoverAndExactTokenRetryable()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001122u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        ImmutableArray<FlatCollisionSphere> validated =
        [
            new FlatCollisionSphere(new Vector3(0.25f, 0f, 0f), 0.3f),
            new FlatCollisionSphere(new Vector3(-0.2f, 0f, 0.4f), 0.2f),
        ];
        RuntimeSetPositionOutcome seeded = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(
                SourceCell,
                new Vector3(12f, 18f, 7f)) with
            {
                Spheres = validated,
            }));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            seeded.Projection));
        Assert.True(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out int sphereCount));
        Assert.Equal(2, sphereCount);

        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        RuntimeSetPositionCommand replacement = Command(Request(
            SourceCell,
            new Vector3(13f, 18f, 7f)) with
        {
            Spheres =
            [
                new FlatCollisionSphere(Vector3.Zero, 0.4f),
            ],
        });
        engine.TransitionCellCollisionTestHook =
            static (_, _, _, _) => TransitionState.Collided;
        RuntimeSetPositionOutcome rejected = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, replacement);
        Assert.Equal(RuntimeSetPositionStatus.Rejected, rejected.Status);
        Assert.True(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out sphereCount));
        Assert.Equal(2, sphereCount);

        RuntimeSetPositionOutcome malformed = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                token,
                replacement with { GameTime = double.NaN });
        Assert.Equal(PhysicsSetPositionError.InvalidArguments, malformed.Error);
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            record,
            out RuntimeEntityPlacementToken retryToken));
        Assert.Equal(token, retryToken);

        engine.TransitionCellCollisionTestHook = null;
        RuntimeSetPositionOutcome retried = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(retryToken, replacement);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            retried.Status);
        Assert.True(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out sphereCount));
        Assert.Equal(1, sphereCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            retried.Projection));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(SourceLandblock | 0x0041u)]
    public void InvalidRetailCellFrameRejectsBeforeCoreAndPreparedCache(
        uint invalidCellId)
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001128u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        int collisionCalls = 0;
        engine.TransitionCellCollisionTestHook = (_, _, _, _) =>
        {
            collisionCalls++;
            return TransitionState.OK;
        };

        RuntimeSetPositionOutcome rejected = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                token,
                Command(Request(
                    invalidCellId,
                    new Vector3(12f, 18f, 7f))));

        Assert.Equal(RuntimeSetPositionStatus.Rejected, rejected.Status);
        Assert.Equal(PhysicsSetPositionError.InvalidArguments, rejected.Error);
        Assert.Equal(0, collisionCalls);
        Assert.False(lifetime.Physics.SetPosition
            .TryGetPreparedMoverSphereCount(record, out _));
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            record,
            out RuntimeEntityPlacementToken retry));
        Assert.Equal(token, retry);
    }

    [Fact]
    public void InvalidRetailQuaternionRejectsBeforeCoreAndPreparedCache()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001129u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        int collisionCalls = 0;
        engine.TransitionCellCollisionTestHook = (_, _, _, _) =>
        {
            collisionCalls++;
            return TransitionState.OK;
        };
        Quaternion[] invalid =
        [
            Quaternion.Zero,
            new Quaternion(0f, 0f, 0f, 0.9f),
        ];

        foreach (Quaternion orientation in invalid)
        {
            RuntimeSetPositionOutcome rejected = lifetime.Physics.SetPosition
                .SubmitPreparedPlacement(
                    token,
                    Command(Request(
                        SourceCell,
                        new Vector3(12f, 18f, 7f)) with
                    {
                        Orientation = orientation,
                    }));
            Assert.Equal(RuntimeSetPositionStatus.Rejected, rejected.Status);
            Assert.Equal(
                PhysicsSetPositionError.InvalidArguments,
                rejected.Error);
        }

        Assert.Equal(0, collisionCalls);
        Assert.False(lifetime.Physics.SetPosition
            .TryGetPreparedMoverSphereCount(record, out _));
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            record,
            out RuntimeEntityPlacementToken retry));
        Assert.Equal(token, retry);
    }

    [Fact]
    public void ExtremeScatterAttemptsRejectWithoutEnteringSynchronousLoop()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000112Au, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        int randomDraws = 0;
        engine.SetPositionRandomUnit = () =>
        {
            randomDraws++;
            return 0.5d;
        };

        RuntimeSetPositionOutcome rejected = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                token,
                Command(Request(
                    SourceCell,
                    new Vector3(12f, 18f, 7f)) with
                {
                    Flags = PhysicsSetPositionFlags.RandomScatter,
                    ScatterRadiusX = 1f,
                    ScatterRadiusY = 1f,
                    ScatterAttempts = uint.MaxValue,
                }));

        Assert.Equal(RuntimeSetPositionStatus.Rejected, rejected.Status);
        Assert.Equal(PhysicsSetPositionError.InvalidArguments, rejected.Error);
        Assert.Equal(0, randomDraws);
        Assert.False(lifetime.Physics.SetPosition
            .TryGetPreparedMoverSphereCount(record, out _));
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            record,
            out RuntimeEntityPlacementToken retry));
        Assert.Equal(token, retry);
    }

    [Fact]
    public void BodylessWithdrawalCancellationDoesNotInventBodyOrProjection()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001123u, 1);
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(token.IsValid);

        Assert.True(lifetime.Physics.SetPosition.Cancel(
            record,
            publishWithdrawal: true));

        Assert.Null(record.PhysicsBody);
        Assert.False(lifetime.Physics.SetPosition.TryPeekProjection(out _));
        RuntimeSetPositionOwnershipSnapshot ownership = lifetime.Physics
            .SetPosition.CaptureOwnership();
        Assert.Equal(0, ownership.ActiveOperationCount);
        Assert.Equal(0, ownership.PendingProjectionAcknowledgementCount);
        Assert.True(ownership.IndexesConsistent);
    }

    [Fact]
    public void ZeroExpectedVelocityPreservesBeginVersionAcrossInterveningVector()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001124u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        lifetime.Entities.AdvanceVectorAuthority(record);
        body.set_velocity(new Vector3(-2f, 0f, 0f));
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                    transition.CollisionInfo.SetCollisionNormal(Vector3.UnitX);
                return observed;
            };

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                token,
                Command(Request(
                    SourceCell,
                    new Vector3(14f, 18f, 7f))));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(new Vector3(-2f, 0f, 0f), body.Velocity);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void CanonicalCommitReplacesDerivedBitsAndPublishesSlopeNormal()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001125u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        body.TransientState |= TransientStateFlags.Sliding
            | TransientStateFlags.StationaryStop
            | TransientStateFlags.StationaryStuck
            | TransientStateFlags.WaterContact;
        body.SlidingNormal = Vector3.UnitY;
        Vector3 slope = Vector3.Normalize(new Vector3(0.2f, 0f, 1f));
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(slope, 0f),
                        SourceCell);
                    transition.CollisionInfo.FramesStationaryFall = 1;
                }
                return observed;
            };

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(
                SourceCell,
                new Vector3(15f, 18f, 7f))));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(slope, body.GroundNormal);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Sliding));
        Assert.Equal(Vector3.Zero, body.SlidingNormal);
        Assert.True(body.TransientState.HasFlag(
            TransientStateFlags.StationaryFall));
        Assert.False(body.TransientState.HasFlag(
            TransientStateFlags.StationaryStop));
        Assert.False(body.TransientState.HasFlag(
            TransientStateFlags.StationaryStuck));
        Assert.False(body.TransientState.HasFlag(
            TransientStateFlags.WaterContact));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void ReentrantNewPlacementConvertsPublishedTokenToOrderedDiscard()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001004u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeEntityPlacementToken replacement = default;
        var observer = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind is RuntimePlacementProjectionKind.Place)
            {
                replacement = lifetime.Physics.SetPosition
                    .BeginAcceptedPlacement(
                        record,
                        record.PositionAuthorityVersion,
                        RuntimeSetPositionOperationKind.RemoteAuthoritative);
            }
        });
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(13f, 18f, 7f))));

        Assert.True(replacement.IsValid);
        Assert.Equal(2, observer.Deltas.Count);
        Assert.Equal(RuntimePlacementProjectionKind.Place,
            observer.Deltas[0].Placement.Kind);
        Assert.Equal(RuntimePlacementProjectionKind.Discard,
            observer.Deltas[1].Placement.Kind);
        Assert.Equal(outcome.Projection.TokenSequence(),
            observer.Deltas[1].Placement.Token.TokenSequence());
        Assert.False(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            observer.Deltas[1].Placement.Token));
        lifetime.Physics.SetPosition.Forget(
            record,
            releasePreparedMover: true);
        Assert.True(lifetime.Physics.SetPosition.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void ReentrantGroundEdgePlacementCannotBeCancelledByDisplacedOperation()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f),
                        SourceCell);
                }
                return observed;
            };
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001042u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        engine.ShadowObjects.Register(
            record.Key!.Value.LocalEntityId,
            0x01000001u,
            body.Position,
            body.Orientation,
            0.48f,
            0f,
            0f,
            SourceLandblock,
            seedCellId: SourceCell,
            isStatic: false);
        RuntimeEntityPlacementToken replacement = default;
        var remote = new ReentrantRemotePlacement(body)
        {
            CellId = SourceCell,
            OnHitGround = () =>
            {
                replacement = lifetime.Physics.SetPosition
                    .BeginAcceptedPlacement(
                        record,
                        record.PositionAuthorityVersion,
                        RuntimeSetPositionOperationKind.RemoteAuthoritative);
            },
        };
        lifetime.Entities.SetRemoteMotion(record, remote);

        RuntimeSetPositionOutcome displaced = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(13f, 18f, 7f))));

        Assert.Equal(RuntimeSetPositionStatus.Cancelled, displaced.Status);
        Assert.True(replacement.IsValid);
        RuntimeSetPositionOwnershipSnapshot ownership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(1, ownership.ActiveOperationCount);
        Assert.Equal(1, ownership.AwaitingPreparationCount);
        Assert.Equal(13f, body.Position.X);
        Assert.Equal(18f, body.Position.Y);
        Assert.Equal(SourceCell, record.FullCellId);
        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);
        Assert.True(lifetime.Physics.IsSpatialRoot(record));
        ShadowEntry shadow = Assert.Single(
            engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(body.Position, shadow.Position);

        RuntimeSetPositionOutcome retained = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                replacement,
                Command(Request(
                    SourceCell,
                    new Vector3(14f, 18f, 7f))));
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            retained.Status);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            retained.Projection));
        lifetime.Physics.SetPosition.Forget(
            record,
            releasePreparedMover: true);
        Assert.True(lifetime.Physics.SetPosition.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void ReentrantCancelThenBeginRecyclesInstanceButCollisionReportUsesPreCallbackValues()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f),
                        SourceCell);
                }
                return observed;
            };
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001046u, 1);
        PhysicsBody body = AttachBody(
            lifetime,
            record,
            SourceCell,
            PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions);
        var collisionObserver = new CollisionReportObserver();
        using IDisposable collisionSubscription = lifetime.Physics
            .CollisionReports.Subscribe(collisionObserver);

        RuntimeEntityPlacementToken recycled = default;
        var remote = new ReentrantRemotePlacement(body)
        {
            CellId = SourceCell,
            OnHitGround = () =>
            {
                lifetime.Physics.SetPosition.Cancel(
                    record,
                    publishWithdrawal: false);
                recycled = lifetime.Physics.SetPosition.BeginAcceptedPlacement(
                    record,
                    record.PositionAuthorityVersion,
                    RuntimeSetPositionOperationKind.RemoteAuthoritative);
            },
        };
        lifetime.Entities.SetRemoteMotion(record, remote);

        _ = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(13f, 18f, 7f))));

        Assert.True(recycled.IsValid);
        Assert.True(body.OnWalkable);
        RuntimeCollisionReport report = Assert.Single(collisionObserver.Reports);
        Assert.Equal(RuntimeCollisionReportKind.EnvironmentCollision, report.Kind);
        Assert.False(report.RecipientWasInContact);
    }

    [Fact]
    public void ReentrantBeginDuringCancelPublishCannotBeOverwrittenByOuterWithdraw()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001250u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome pending = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(18f, 18f, 7f))));
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            pending.Status);

        RuntimeEntityPlacementToken inner = default;
        var observer = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind is RuntimePlacementProjectionKind.Discard)
            {
                inner = lifetime.Physics.SetPosition.BeginAcceptedPlacement(
                    record,
                    record.PositionAuthorityVersion,
                    RuntimeSetPositionOperationKind.RemoteAuthoritative);
            }
        });
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        bool removed = lifetime.Physics.SetPosition.Cancel(
            record,
            publishWithdrawal: true);

        Assert.True(removed);
        Assert.True(inner.IsValid);
        RuntimePlacementDelta delta = Assert.Single(observer.Deltas);
        Assert.Equal(RuntimePlacementProjectionKind.Discard, delta.Placement.Kind);
        Assert.True(lifetime.Physics.SetPosition.IsPlacementCurrent(inner));
        RuntimeSetPositionOwnershipSnapshot ownership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(1, ownership.ActiveOperationCount);
        Assert.Equal(1, ownership.AwaitingPreparationCount);
    }

    [Fact]
    public void ReentrantCancelThenBeginDuringCommitFailureLeavesInnerOperationUncancelled()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f),
                        SourceCell);
                }
                return observed;
            };
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001251u, 1);
        PhysicsBody body = AttachBody(
            lifetime,
            record,
            SourceCell,
            PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions);

        RuntimeEntityPlacementToken inner = default;
        var remote = new ReentrantRemotePlacement(body)
        {
            CellId = SourceCell,
            OnHitGround = () =>
            {
                lifetime.Physics.SetPosition.Cancel(
                    record,
                    publishWithdrawal: false);
                lifetime.Entities.AdvancePlacementCommit(record);
                inner = lifetime.Physics.SetPosition.BeginAcceptedPlacement(
                    record,
                    record.PositionAuthorityVersion,
                    RuntimeSetPositionOperationKind.RemoteAuthoritative);
            },
        };
        lifetime.Entities.SetRemoteMotion(record, remote);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(13f, 18f, 7f))));

        Assert.Equal(RuntimeSetPositionStatus.Cancelled, outcome.Status);
        Assert.True(inner.IsValid);
        Assert.True(lifetime.Physics.SetPosition.IsPlacementCurrent(inner));
        RuntimeSetPositionOwnershipSnapshot ownership =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.Equal(1, ownership.ActiveOperationCount);
        Assert.Equal(1, ownership.AwaitingPreparationCount);
    }

    [Fact]
    public void RetrySnapshotPreservesOrderWhenObserverAcknowledgesTwoPendingTokens()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord first = CreateRecord(lifetime, 0x70001040u, 1);
        RuntimeEntityRecord second = CreateRecord(lifetime, 0x70001041u, 1);
        _ = AttachBody(lifetime, first, SourceCell);
        _ = AttachBody(lifetime, second, SourceCell);
        bool acknowledgeRetries = false;
        var retryOrder = new List<ulong>();
        var observer = new PlacementObserver(delta =>
        {
            if (!acknowledgeRetries)
                return;
            retryOrder.Add(delta.Placement.Token.Sequence);
            Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
                delta.Placement.Token));
        });
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        RuntimeSetPositionOutcome firstOutcome = lifetime.Physics.SetPosition.Apply(
            first,
            first.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(14f, 18f, 7f))));
        RuntimeSetPositionOutcome secondOutcome = lifetime.Physics.SetPosition.Apply(
            second,
            second.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(16f, 18f, 7f))));

        acknowledgeRetries = true;
        lifetime.Physics.SetPosition.RetryPendingProjections();

        Assert.Equal(
            [firstOutcome.Projection.Sequence, secondOutcome.Projection.Sequence],
            retryOrder);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .PendingSetPositionHostAcknowledgementCount);
    }

    [Fact]
    public void ThrowingObserverDoesNotRollBackCanonicalCommitAndRetryCanAck()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001043u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        using IDisposable throwing = lifetime.Events.SubscribePlacement(
            new ThrowingPlacementObserver());

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(17f, 18f, 7f))));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(new Vector3(17f, 18f, 7f), body.Position);
        Assert.Equal(1, lifetime.Events.DispatchFailureCount);
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .PendingSetPositionHostAcknowledgementCount);

        throwing.Dispose();
        var healthy = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(healthy);
        lifetime.Physics.SetPosition.RetryPendingProjections();
        Assert.Equal(outcome.Projection,
            Assert.Single(healthy.Deltas).Placement.Token);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void ForgetRevisesPendingTokenAndRetryPublishesDiscardBeforeAck()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001117u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        RuntimeSetPositionOutcome pending = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(17f, 17f, 7f))));

        RuntimePlacementCancellationReceipt cancellation =
            lifetime.Physics.SetPosition.Forget(record);

        Assert.True(cancellation.IsValid);
        Assert.Single(observer.Deltas);
        Assert.False(lifetime.Physics.SetPosition.AcknowledgeProjection(
            pending.Projection));
        lifetime.Physics.SetPosition.RetryPendingProjections();
        RuntimePlacementProjectionSnapshot discard = observer.Deltas[^1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Discard, discard.Kind);
        Assert.Equal(pending.Projection.Sequence, discard.Token.Sequence);
        Assert.True(discard.Token.Revision > pending.Projection.Revision);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            discard.Token));
    }

    [Fact]
    public void ReplacementRetainsUnacknowledgedLostWithdrawalUntilPreparedCommandCanPlace()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000111Au, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        using IDisposable throwing = lifetime.Events.SubscribePlacement(
            new ThrowingPlacementObserver());
        RuntimeSetPositionOutcome original = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, original.Status);

        RuntimeEntityPlacementToken replacement = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(replacement.IsValid);
        RuntimeSetPositionOutcome waiting = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                replacement,
                Command(Request(SourceCell, new Vector3(18f, 18f, 7f))));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, waiting.Status);
        Assert.Equal(original.Projection, waiting.Projection);
        Assert.Equal(0u, record.FullCellId);

        throwing.Dispose();
        var healthy = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(healthy);
        lifetime.Physics.SetPosition.RetryPendingProjections();
        RuntimePlacementProjectionSnapshot withdrawal =
            Assert.Single(healthy.Deltas).Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Withdraw, withdrawal.Kind);
        Assert.Equal(original.Projection, withdrawal.Token);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            withdrawal.Token));

        Assert.Equal(2, healthy.Deltas.Count);
        RuntimePlacementProjectionSnapshot placed = healthy.Deltas[1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.Equal(SourceCell, record.FullCellId);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            placed.Token));
    }

    [Fact]
    public void CancellingWakeableParkLeavesEntityWithdrawnWithNothingToWakeIt()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000411Bu, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);

        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(SourceCell, record.FullCellId);

        RuntimeSetPositionOutcome parked = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, parked.Status);
        Assert.True(lifetime.Physics.SetPosition.IsDeferred(record));
        Assert.False(body.InWorld);
        Assert.False(record.ObjectClock.IsActive);
        Assert.Equal(0u, record.FullCellId);
        Vector3 parkedPosition = body.Position;

        _ = lifetime.Physics.SetPosition.Forget(
            record,
            restoreCancelledPark: true);

        Assert.False(lifetime.Physics.SetPosition.IsDeferred(record));
        Assert.True(
            body.InWorld,
            "cancelled park left the entity withdrawn: InWorld=false");
        Assert.True(
            record.ObjectClock.IsActive,
            "cancelled park left the object clock suspended");
        Assert.True(
            record.FullCellId != 0u,
            "cancelled park left the entity without canonical residency");
        Assert.True(
            lifetime.Physics.IsSpatialRoot(record),
            "cancelled park left the entity out of the physics workset");
        Assert.Equal(
            TransientStateFlags.Active,
            body.TransientState & TransientStateFlags.Active);

        Assert.Equal(parkedPosition, body.Position);
        Assert.Equal(body.CellPosition.ObjCellId, record.FullCellId);
    }

    [Fact]
    public void AcceptedPositionCancellingWakeableParkPublishesRebucketedThroughTheMerge()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        var generation = new RuntimeGenerationToken(1UL);
        lifetime.BindEventContext(() => generation, static () => 1UL);
        const uint guid = 0x7000411Fu;
        RuntimeEntityRecord record = CreateRecord(lifetime, guid, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);

        RuntimeSetPositionOutcome parked = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, parked.Status);
        Assert.Equal(0u, record.FullCellId);
        Assert.Equal(DestinationCell, body.CellPosition.ObjCellId);

        var observer = new EntityObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);

        Assert.True(lifetime.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                guid,
                new CreateObject.ServerPosition(
                    SourceCell, 11f, 21f, 7f, 1f, 0f, 0f, 0f),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: 2,
                TeleportSequence: 0,
                ForcePositionSequence: 0),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        Assert.Equal(DestinationCell, record.FullCellId);
        Assert.NotEqual(SourceCell, record.FullCellId);
        Assert.True(body.InWorld);

        RuntimeEntityDelta delta = Assert.Single(
            observer.Deltas,
            d => d.Entity.Identity.ServerGuid == guid);
        Assert.Equal(RuntimeEntityChange.Rebucketed, delta.Change);
        Assert.Equal(DestinationCell, delta.Entity.CellId);
    }

    [Fact]
    public void CancellingWakeableParkPublishesTheWithdrawalRestorationReceipt()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000411Du, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var placements = new PlacementObserver();
        using IDisposable subscription =
            lifetime.Events.SubscribePlacement(placements);

        RuntimeSetPositionOutcome parked = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, parked.Status);
        Assert.Equal(
            RuntimePlacementProjectionKind.Withdraw,
            Assert.Single(placements.Deltas).Placement.Kind);

        _ = lifetime.Physics.SetPosition.Forget(
            record,
            restoreCancelledPark: true);

        Assert.True(body.InWorld);
        Assert.NotEqual(0u, record.FullCellId);

        Assert.Equal(2, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot head));
        Assert.Equal(RuntimePlacementProjectionKind.Discard, head.Kind);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            head.Token));

        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restoration));
        Assert.Equal(
            RuntimePlacementProjectionKind.WithdrawalRestored,
            restoration.Kind);
        Assert.Equal(record.Key, restoration.Token.Entity);
        Assert.Equal(record.FullCellId, restoration.Token.ExactCellId);
        Assert.Equal(body.Position, restoration.WorldPosition);

        // The host's per-frame RetryPending pump is what delivers it, exactly
        // as it delivers any receipt that was not the head when published.
        placements.Deltas.Clear();
        lifetime.Physics.SetPosition.RetryPendingProjections();
        Assert.Equal(
            restoration,
            Assert.Single(placements.Deltas).Placement);

        // Acknowledge-only: no operation backs it, and the stream drains.
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            restoration.Token));
        Assert.Equal(
            0,
            lifetime.Physics.SetPosition.PendingProjectionCount);
    }

    [Fact]
    public void CancellingParkIntoQuiescingPrefixPublishesNoRestorationReceipt()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000411Eu, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);

        RuntimeSetPositionOutcome parked = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, parked.Status);
        uint parkedCell = body.CellPosition.ObjCellId;
        _ = lifetime.Physics.BeginCollisionPrefixQuiescence(
            (parkedCell & 0xFFFF0000u) | 0xFFFFu,
            collisionGeneration: 1UL,
            includeOutdoorCells: true);
        int pendingBefore =
            lifetime.Physics.SetPosition.PendingProjectionCount;

        _ = lifetime.Physics.SetPosition.Forget(
            record,
            restoreCancelledPark: true);

        Assert.Equal(0u, record.FullCellId);
        Assert.Equal(
            pendingBefore,
            lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot head));
        Assert.Equal(RuntimePlacementProjectionKind.Discard, head.Kind);
    }

    [Fact]
    public void CancellingWakeableParkByExactTokenAlsoRestoresTheEntity()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000411Cu, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);

        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(token.IsValid);
        RuntimeSetPositionOutcome parked = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, Command(CrossLandblockRequest()));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, parked.Status);
        Assert.False(body.InWorld);
        Assert.False(record.ObjectClock.IsActive);
        Assert.Equal(0u, record.FullCellId);

        _ = lifetime.Physics.SetPosition.ForgetExactPlacement(
            token,
            restoreCancelledPark: true);

        Assert.False(lifetime.Physics.SetPosition.IsDeferred(record));
        Assert.True(body.InWorld, "exact-token cancel left the entity withdrawn");
        Assert.True(
            record.ObjectClock.IsActive,
            "exact-token cancel left the object clock suspended");
        Assert.True(
            record.FullCellId != 0u,
            "exact-token cancel left the entity without canonical residency");
        Assert.True(lifetime.Physics.IsSpatialRoot(record));
    }

    [Fact]
    public void ReentrantNewerPositionDuringPickupDiscardSuppressesStalePickupDelta()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001118u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome pending = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(17f, 16f, 7f))));
        var entities = new EntityObserver();
        using IDisposable entitySubscription = lifetime.Events.Subscribe(entities);
        bool reentered = false;
        RuntimeEventStamp discardStamp = default;
        var placements = new PlacementObserver(delta =>
        {
            if (reentered
                || delta.Placement.Kind
                    is not RuntimePlacementProjectionKind.Discard)
            {
                return;
            }
            reentered = true;
            discardStamp = delta.Stamp;
            Assert.True(lifetime.TryApplyPosition(
                new WorldSession.EntityPositionUpdate(
                    record.ServerGuid,
                    new CreateObject.ServerPosition(
                        SourceCell, 12f, 20f, 7f, 1f, 0f, 0f, 0f),
                    Velocity: null,
                    PlacementId: null,
                    IsGrounded: true,
                    InstanceSequence: record.Incarnation,
                    PositionSequence: 3,
                    TeleportSequence: 0,
                    ForcePositionSequence: 0),
                isLocalPlayer: false,
                forcePositionRotation: null,
                currentLocalVelocity: null,
                acknowledgeProjection: null,
                out _,
                out _,
                out _));
        });
        using IDisposable placementSubscription =
            lifetime.Events.SubscribePlacement(placements);

        Assert.False(lifetime.TryApplyPickup(
            new PickupEvent.Parsed(record.ServerGuid, 1, 2),
            acknowledgeProjection: null,
            out _));

        Assert.True(reentered);
        RuntimeEntityDelta only = Assert.Single(entities.Deltas);
        Assert.Equal(RuntimeEntityChange.Updated, only.Change);
        Assert.Equal(0u, record.FullCellId);
        Assert.True(only.Stamp.Sequence > discardStamp.Sequence);
        Assert.False(lifetime.Physics.SetPosition.AcknowledgeProjection(
            pending.Projection));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            Assert.Single(placements.Deltas).Placement.Token));
    }

    [Fact]
    public void SyntheticWithdrawalAcknowledgementTerminatesOperation()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001044u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        Assert.True(lifetime.Physics.SetPosition.Cancel(
            record,
            publishWithdrawal: true));
        RuntimePlacementProjectionToken token = Assert.Single(observer.Deltas)
            .Placement.Token;
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(token));
        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
    }

    [Fact]
    public void ReentrantBeginDuringDiscardCannotBeOverwrittenByOuterBegin()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001045u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome pending = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(18f, 18f, 7f))));
        RuntimeEntityPlacementToken inner = default;
        var observer = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind is RuntimePlacementProjectionKind.Discard)
            {
                inner = lifetime.Physics.SetPosition.BeginAcceptedPlacement(
                    record,
                    record.PositionAuthorityVersion,
                    RuntimeSetPositionOperationKind.RemoteAuthoritative);
            }
        });
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        RuntimeEntityPlacementToken outer = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);

        Assert.False(outer.IsValid);
        Assert.True(inner.IsValid);
        Assert.False(lifetime.Physics.SetPosition.AcknowledgeProjection(
            pending.Projection));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            Assert.Single(observer.Deltas).Placement.Token));
        RuntimeSetPositionOutcome committed = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                inner,
                Command(Request(SourceCell, new Vector3(19f, 18f, 7f))));
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            committed.Status);
    }

    [Fact]
    public void CollisionRetirementRejectsActivePlacementBeforeResidentMutation()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001046u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);

        Assert.Throws<InvalidOperationException>(() =>
            lifetime.Physics.SetPosition.ParkCollisionResidents(
                SourceLandblock,
                includeOutdoorCells: true));
        Assert.Equal(SourceCell, record.FullCellId);
        Assert.True(lifetime.Physics.IsSpatialRoot(record));
        RuntimeSetPositionOutcome submitted = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(
                token,
                Command(Request(SourceCell, new Vector3(20f, 18f, 7f))));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            submitted.Status);
    }

    [Fact]
    public void CollisionRetirementRejectsHostAckPendingPlacementBeforeResidentMutation()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000111Bu, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome pending = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(20f, 19f, 7f))));

        Assert.Throws<InvalidOperationException>(() =>
            lifetime.Physics.SetPosition.ParkCollisionResidents(
                SourceLandblock,
                includeOutdoorCells: true));
        Assert.Equal(SourceCell, record.FullCellId);
        Assert.True(lifetime.Physics.IsSpatialRoot(record));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            pending.Projection));
    }

    [Fact]
    public void CollisionRetirementInstallsEveryRootBeforeWithdrawObserverReentry()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord first = CreateRecord(lifetime, 0x7000111Du, 1);
        RuntimeEntityRecord second = CreateRecord(lifetime, 0x7000111Eu, 1);
        _ = AttachBody(lifetime, first, SourceCell);
        _ = AttachBody(lifetime, second, SourceCell);
        RuntimeEntityPlacementToken replacement = default;
        RuntimeSetPositionOutcome submitted = default;
        var observer = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind is RuntimePlacementProjectionKind.Withdraw
                && delta.Placement.Token.Entity == first.Key)
            {
                replacement = lifetime.Physics.SetPosition
                    .BeginAcceptedPlacement(
                        second,
                        second.PositionAuthorityVersion,
                        RuntimeSetPositionOperationKind.RemoteAuthoritative);
                submitted = lifetime.Physics.SetPosition
                    .SubmitPreparedPlacement(
                        replacement,
                        Command(Request(
                            SourceCell,
                            new Vector3(21f, 19f, 7f))));
            }
        });
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        lifetime.Physics.SetPosition.ParkCollisionResidents(
            SourceLandblock,
            includeOutdoorCells: true);

        Assert.True(replacement.IsValid);
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, submitted.Status);
        Assert.Equal(2, observer.Deltas.Count);
        Assert.All(observer.Deltas, delta => Assert.Equal(
            RuntimePlacementProjectionKind.Withdraw,
            delta.Placement.Kind));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            observer.Deltas[0].Placement.Token));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            observer.Deltas[1].Placement.Token));
        RuntimePlacementProjectionSnapshot placed = observer.Deltas[^1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.Equal(second.Key, placed.Token.Entity);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            placed.Token));
    }

    [Fact]
    public void RootAndCurrentDirectChildOwnIndependentExactLostDeadlines()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(
            engine,
            timeProvider: time);
        RuntimeEntityRecord root = CreateRecord(lifetime, 0x70001005u, 3);
        RuntimeEntityRecord child = CreateRecord(lifetime, 0x70001006u, 9);
        _ = AttachBody(lifetime, root, SourceCell);
        var relation = new ParentAttachmentRelation(
            root.ServerGuid,
            child.ServerGuid,
            ParentLocation: 1,
            PlacementId: 2,
            ParentInstanceSequence: root.Incarnation,
            ChildPositionSequence: 1);
        lifetime.Entities.ParentAttachments.AcceptCreateObjectRelation(relation);
        Assert.True(lifetime.Entities.ParentAttachments.CommitProjection(relation));

        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            root,
            root.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));

        Assert.Equal(2, lifetime.Physics.CaptureOwnership().LostCellDeadlineCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));
        time.Advance(TimeSpan.FromSeconds(26));
        lifetime.Physics.SetPosition.TickLostCellDeadlines();
        Assert.Equal(2, lifetime.Physics.CaptureOwnership()
            .ExpiredLostCellCount);

        var expired = new HashSet<RuntimeEntityKey>();
        while (lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(out var key))
            expired.Add(key);
        Assert.Equal(2, expired.Count);
        Assert.Contains(root.Key!.Value, expired);
        Assert.Contains(child.Key!.Value, expired);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership().LostCellDeadlineCount);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .ExpiredLostCellCount);
    }

    [Fact]
    public void ExpiredRootDeletionDoesNotConsumeIndependentChildExpiry()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine, timeProvider: time);
        RuntimeEntityRecord root = CreateRecord(lifetime, 0x70001047u, 1);
        RuntimeEntityRecord child = CreateRecord(lifetime, 0x70001048u, 1);
        _ = AttachBody(lifetime, root, SourceCell);
        CommitParent(lifetime, root, child);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            root,
            root.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));
        time.Advance(TimeSpan.FromSeconds(26));
        lifetime.Physics.SetPosition.TickLostCellDeadlines();

        Assert.True(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(
            out RuntimeEntityKey expiredRoot));
        Assert.Equal(root.Key, expiredRoot);
        lifetime.Physics.SetPosition.Forget(root);
        Assert.True(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(
            out RuntimeEntityKey expiredChild));
        Assert.Equal(child.Key, expiredChild);
    }

    [Fact]
    public void ChildPickupCancelsOnlyItsCapturedLostDeadline()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine, timeProvider: time);
        RuntimeEntityRecord root = CreateRecord(lifetime, 0x70001049u, 1);
        RuntimeEntityRecord child = CreateRecord(lifetime, 0x7000104Au, 1);
        _ = AttachBody(lifetime, root, SourceCell);
        CommitParent(lifetime, root, child);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            root,
            root.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        Assert.True(lifetime.TryApplyPickup(
            new PickupEvent.Parsed(child.ServerGuid, 1, 2),
            acknowledgeProjection: null,
            out _));
        time.Advance(TimeSpan.FromSeconds(26));
        lifetime.Physics.SetPosition.TickLostCellDeadlines();

        Assert.True(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(
            out RuntimeEntityKey only));
        Assert.Equal(root.Key, only);
        Assert.False(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(out _));
    }

    [Fact]
    public void LostDeadlineUsesMonotonicTimeAndResetClearsExpiredOwnership()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine, timeProvider: time);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000104Bu, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        time.JumpUtc(TimeSpan.FromDays(30));
        lifetime.Physics.SetPosition.TickLostCellDeadlines();
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .ExpiredLostCellCount);
        time.Advance(TimeSpan.FromSeconds(26));
        lifetime.Physics.SetPosition.TickLostCellDeadlines();
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .ExpiredLostCellCount);

        _ = lifetime.BeginSessionClear();
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .ExpiredLostCellCount);
        Assert.False(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(out _));
    }

    [Fact]
    public void LostDeadlineTickIsZeroAllocationWhenEmptyAndExpiresByDeadlinePriority()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(
            engine,
            timeProvider: time);
        lifetime.Physics.SetPosition.TickLostCellDeadlines();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
            lifetime.Physics.SetPosition.TickLostCellDeadlines();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        RuntimeEntityRecord first = CreateRecord(lifetime, 0x70001101u, 1);
        RuntimeEntityRecord middle = CreateRecord(lifetime, 0x70001102u, 1);
        RuntimeEntityRecord last = CreateRecord(lifetime, 0x70001103u, 1);
        foreach (RuntimeEntityRecord record in new[] { first, middle, last })
        {
            _ = AttachBody(lifetime, record, SourceCell);
            RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
                record,
                record.PositionAuthorityVersion,
                Command(CrossLandblockRequest()));
            Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
                deferred.Projection));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        lifetime.Physics.SetPosition.LeaveWorld(middle);
        time.Advance(TimeSpan.FromSeconds(24));
        lifetime.Physics.SetPosition.TickLostCellDeadlines();

        Assert.True(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(
            out RuntimeEntityKey firstExpired));
        Assert.True(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(
            out RuntimeEntityKey secondExpired));
        Assert.Equal(first.Key, firstExpired);
        Assert.Equal(last.Key, secondExpired);
        Assert.False(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(out _));
    }

    [Fact]
    public void RearmedLostDeadlineSupersedesItsStalePriorityEntry()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(
            engine,
            timeProvider: time);
        RuntimeEntityRecord first = CreateRecord(lifetime, 0x70001112u, 1);
        RuntimeEntityRecord second = CreateRecord(lifetime, 0x70001113u, 1);
        _ = AttachBody(lifetime, first, SourceCell);
        _ = AttachBody(lifetime, second, SourceCell);

        RuntimeSetPositionOutcome firstLost = lifetime.Physics.SetPosition.Apply(
            first,
            first.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            firstLost.Projection));
        time.Advance(TimeSpan.FromSeconds(1));
        RuntimeSetPositionOutcome secondLost = lifetime.Physics.SetPosition.Apply(
            second,
            second.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            secondLost.Projection));
        time.Advance(TimeSpan.FromSeconds(1));

        RuntimeEntityPlacementToken replacement = lifetime.Physics.SetPosition
            .BeginAcceptedPlacement(
                first,
                first.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        RuntimeSetPositionOutcome rearmed = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(replacement, Command(CrossLandblockRequest()));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, rearmed.Status);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            rearmed.Projection));

        time.Advance(TimeSpan.FromSeconds(24.5));
        lifetime.Physics.SetPosition.TickLostCellDeadlines();
        Assert.True(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(
            out RuntimeEntityKey expired));
        Assert.Equal(second.Key, expired);
        Assert.False(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(out _));
        time.Advance(TimeSpan.FromSeconds(1));
        lifetime.Physics.SetPosition.TickLostCellDeadlines();
        Assert.True(lifetime.Physics.SetPosition.TryDequeueExpiredLostCell(
            out expired));
        Assert.Equal(first.Key, expired);
    }

    [Fact]
    public void RepeatedLostDeadlineRearmAndCancelKeepsExactHeapBounded()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001119u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        for (int iteration = 0; iteration < 100; iteration++)
        {
            RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
                .BeginAcceptedPlacement(
                    record,
                    record.PositionAuthorityVersion,
                    RuntimeSetPositionOperationKind.RemoteAuthoritative);
            deferred = lifetime.Physics.SetPosition.SubmitPreparedPlacement(
                token,
                Command(CrossLandblockRequest()));
            Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
                deferred.Projection));
            RuntimeSetPositionOwnershipSnapshot snapshot = lifetime.Physics
                .SetPosition.CaptureOwnership();
            Assert.True(snapshot.IndexesConsistent);
            Assert.Equal(1, snapshot.LostDeadlineCount);
            Assert.Equal(1, snapshot.LostDeadlineNodeCount);
            Assert.Equal(1, snapshot.LostDeadlineIndexCount);
        }

        lifetime.Physics.SetPosition.LeaveWorld(record);
        RuntimeSetPositionOwnershipSnapshot final = lifetime.Physics
            .SetPosition.CaptureOwnership();
        Assert.True(final.IndexesConsistent);
        Assert.Equal(0, final.LostDeadlineCount);
        Assert.Equal(0, final.LostDeadlineNodeCount);
        Assert.Equal(0, final.LostDeadlineIndexCount);
    }

    [Fact]
    public void CommittedGenerationWithoutExactIndoorCellRebindsAndWakesOnNextGeneration()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001114u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(
                DestinationIndoorCell,
                new Vector3(10f, 12f, 7f))));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, deferred.Status);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        RuntimeCollisionAdmission missing = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        using (PreparedLandblockCollisionGeneration prepared =
               lifetime.Physics.PrepareCollisionGeneration(missing))
        {
            lifetime.Physics.StageCollisionAssets(
                missing,
                prepared,
                CollisionAssets(DestinationLandblock));
            Assert.True(CommitPrepared(
                lifetime.Physics,
                missing,
                prepared).Committed);
        }
        Assert.Single(observer.Deltas);
        RuntimePhysicsOwnershipSnapshot unbound = lifetime.Physics
            .CaptureOwnership();
        Assert.Equal(1, unbound.UnboundDeferredSetPositionCellCount);
        Assert.Equal(1, unbound.UnboundDeferredSetPositionCellOrderCount);

        RuntimeCollisionAdmission resident = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        using PreparedLandblockCollisionGeneration preparedResident =
            lifetime.Physics.PrepareCollisionGeneration(resident);
        lifetime.Physics.StageCollisionAssets(
            resident,
            preparedResident,
            CollisionAssets(DestinationLandblock));
        AddSyntheticCell(preparedResident.DataCache, DestinationIndoorCell);
        Assert.True(CommitPrepared(
            lifetime.Physics,
            resident,
            preparedResident).Committed);

        RuntimePlacementProjectionSnapshot placed = observer.Deltas[^1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.Equal(DestinationIndoorCell, placed.Token.ExactCellId);
        RuntimePhysicsOwnershipSnapshot final = lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, final.DeferredSetPositionBucketCount);
        Assert.Equal(0, final.UnboundDeferredSetPositionCellCount);
    }

    [Fact]
    public void CommittedGenerationWithoutExactIndoorCell_RecoversWhenSpawnBecomesReadyWithoutNewAdmission()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001134u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(
                DestinationIndoorCell,
                new Vector3(10f, 12f, 7f))));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, deferred.Status);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        RuntimeCollisionAdmission missing = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        using (PreparedLandblockCollisionGeneration prepared =
               lifetime.Physics.PrepareCollisionGeneration(missing))
        {
            lifetime.Physics.StageCollisionAssets(
                missing,
                prepared,
                CollisionAssets(DestinationLandblock));
            Assert.True(CommitPrepared(
                lifetime.Physics,
                missing,
                prepared).Committed);
        }
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .UnboundDeferredSetPositionCellCount);
        Assert.Single(observer.Deltas);

        // Spawn EnvCell arrives on the live cache after commit, with no later
        // collision admission — the login first-entry strand case.
        Assert.NotNull(engine.DataCache);
        AddSyntheticCell(engine.DataCache, DestinationIndoorCell);
        Assert.Equal(
            1,
            lifetime.Physics.SetPosition.TryRecoverUnboundDeferredWhenSpawnReady(
                DestinationIndoorCell));

        RuntimePlacementProjectionSnapshot placed = observer.Deltas[^1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.Equal(DestinationIndoorCell, placed.Token.ExactCellId);
        RuntimePhysicsOwnershipSnapshot final = lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, final.UnboundDeferredSetPositionCellCount);
        Assert.Equal(0, final.DeferredSetPositionBucketCount);
    }

    [Fact]
    public void ParkedOnExpectedAfterAuthority_RecoversWhenSpawnBecomesReadyWithoutNewAdmission()
    {
        // Indoor login race: landblock collision already at authority N, then
        // set-position parks on Expected N+1 because the EnvCell is still
        // missing. When the EnvCell arrives with no later admission, recover
        // must rebind onto N — otherwise first-entry stays unpublished forever.
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001135u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        RuntimeCollisionAdmission prior = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        using (PreparedLandblockCollisionGeneration prepared =
               lifetime.Physics.PrepareCollisionGeneration(prior))
        {
            lifetime.Physics.StageCollisionAssets(
                prior,
                prepared,
                CollisionAssets(DestinationLandblock));
            Assert.True(CommitPrepared(
                lifetime.Physics,
                prior,
                prepared).Committed);
        }
        Assert.Equal(
            1UL,
            lifetime.Physics.CollisionGenerationAuthority(DestinationIndoorCell));
        Assert.Equal(
            2UL,
            lifetime.Physics.ExpectedCollisionGeneration(DestinationIndoorCell));

        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(
                DestinationIndoorCell,
                new Vector3(10f, 12f, 7f))));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, deferred.Status);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .DeferredSetPositionBucketCount);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .UnboundDeferredSetPositionCellCount);
        Assert.Single(observer.Deltas);

        Assert.NotNull(engine.DataCache);
        AddSyntheticCell(engine.DataCache, DestinationIndoorCell);
        Assert.Equal(
            1,
            lifetime.Physics.SetPosition.TryRecoverUnboundDeferredWhenSpawnReady(
                DestinationIndoorCell));

        RuntimePlacementProjectionSnapshot placed = observer.Deltas[^1].Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.Equal(DestinationIndoorCell, placed.Token.ExactCellId);
        RuntimePhysicsOwnershipSnapshot final = lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, final.UnboundDeferredSetPositionCellCount);
        Assert.Equal(0, final.DeferredSetPositionBucketCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpawnReadyRecoveryRetainsOrderedSurvivorsWhenObserverStartsAdmission(bool unbound)
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord[] records = Enumerable.Range(0, 3)
            .Select(index => CreateRecord(lifetime, 0x70001140u + (uint)index, 1))
            .ToArray();
        RuntimeCollisionAdmission? replacement = null;
        var placed = new List<RuntimeEntityKey>();
        var observer = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind != RuntimePlacementProjectionKind.Place)
                return;
            placed.Add(delta.Placement.Token.Entity);
            Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
                delta.Placement.Token));
            if (replacement is null)
                replacement = lifetime.Physics.BeginCollisionAdmission(DestinationLandblock);
        });
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        void CommitMissingCell()
        {
            RuntimeCollisionAdmission admission = lifetime.Physics
                .BeginCollisionAdmission(DestinationLandblock);
            using PreparedLandblockCollisionGeneration prepared = lifetime.Physics
                .PrepareCollisionGeneration(admission);
            lifetime.Physics.StageCollisionAssets(
                admission, prepared, CollisionAssets(DestinationLandblock));
            Assert.True(CommitPrepared(lifetime.Physics, admission, prepared).Committed);
        }

        if (!unbound)
            CommitMissingCell();
        foreach (RuntimeEntityRecord record in records)
        {
            _ = AttachBody(lifetime, record, SourceCell);
            RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
                record,
                record.PositionAuthorityVersion,
                Command(Request(DestinationIndoorCell, new Vector3(10f, 12f, 7f))));
            Assert.Equal(RuntimeSetPositionStatus.DeferredCell, deferred.Status);
            Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(deferred.Projection));
        }
        if (unbound)
            CommitMissingCell();
        Assert.Equal(unbound ? 1 : 0, lifetime.Physics.CaptureOwnership()
            .UnboundDeferredSetPositionCellCount);

        AddSyntheticCell(engine.DataCache!, DestinationIndoorCell);
        Assert.Equal(3, lifetime.Physics.SetPosition
            .TryRecoverUnboundDeferredWhenSpawnReady(DestinationIndoorCell));
        Assert.NotNull(replacement);
        Assert.Equal(new[] { records[0].Key!.Value }, placed);
        Assert.True(lifetime.Physics.SetPosition.IsDeferred(records[1]));
        Assert.True(lifetime.Physics.SetPosition.IsDeferred(records[2]));
        Assert.Equal(1, lifetime.Physics.CaptureOwnership().DeferredSetPositionBucketCount);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership().UnboundDeferredSetPositionCellCount);
        Assert.Equal(0, lifetime.Physics.SetPosition
            .TryRecoverUnboundDeferredWhenSpawnReady(DestinationIndoorCell));

        using PreparedLandblockCollisionGeneration ready = lifetime.Physics
            .PrepareCollisionGeneration(replacement);
        lifetime.Physics.StageCollisionAssets(
            replacement, ready, CollisionAssets(DestinationLandblock));
        AddSyntheticCell(ready.DataCache, DestinationIndoorCell);
        Assert.True(CommitPrepared(lifetime.Physics, replacement, ready).Committed);
        Assert.Equal(
            new[] { records[1].Key!.Value, records[2].Key!.Value },
            placed.Where(key => key != records[0].Key));
        Assert.All(records, record => Assert.False(lifetime.Physics.SetPosition.IsDeferred(record)));
        Assert.Equal(0, lifetime.Physics.CaptureOwnership().DeferredSetPositionBucketCount);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership().UnboundDeferredSetPositionCellCount);
    }

    [Fact]
    public void SpawnReadyRecoveryKeepsUnacknowledgedWithdrawalIndexed()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeCollisionAdmission admission = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        using (PreparedLandblockCollisionGeneration prepared = lifetime.Physics
               .PrepareCollisionGeneration(admission))
        {
            lifetime.Physics.StageCollisionAssets(
                admission, prepared, CollisionAssets(DestinationLandblock));
            Assert.True(CommitPrepared(lifetime.Physics, admission, prepared).Committed);
        }
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001144u, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record, record.PositionAuthorityVersion,
            Command(Request(DestinationIndoorCell, new Vector3(10f, 12f, 7f))));
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, deferred.Status);
        AddSyntheticCell(engine.DataCache!, DestinationIndoorCell);
        Assert.Equal(1, lifetime.Physics.SetPosition
            .TryRecoverUnboundDeferredWhenSpawnReady(DestinationIndoorCell));
        Assert.Single(observer.Deltas);
        Assert.True(lifetime.Physics.SetPosition.IsDeferred(record));
        Assert.Equal(1, lifetime.Physics.CaptureOwnership().DeferredSetPositionBucketCount);

        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(deferred.Projection));
        Assert.Equal(RuntimePlacementProjectionKind.Place, observer.Deltas[^1].Placement.Kind);
        Assert.False(lifetime.Physics.SetPosition.IsDeferred(record));
        Assert.Equal(0, lifetime.Physics.CaptureOwnership().DeferredSetPositionBucketCount);
    }

    [Fact]
    public void CollisionAdmissionSupersessionAndInvalidationPreserveDeferredSurvivorOrder()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord first = CreateRecord(lifetime, 0x70001115u, 1);
        RuntimeEntityRecord second = CreateRecord(lifetime, 0x70001116u, 1);
        _ = AttachBody(lifetime, first, SourceCell);
        _ = AttachBody(lifetime, second, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        foreach (RuntimeEntityRecord record in new[] { first, second })
        {
            RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
                record,
                record.PositionAuthorityVersion,
                Command(Request(
                    DestinationIndoorCell,
                    new Vector3(10f, 12f, 7f))));
            Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
                deferred.Projection));
        }
        observer.Deltas.Clear();

        RuntimeCollisionAdmission superseded = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        RuntimeCollisionAdmission invalidated = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        Assert.Throws<InvalidOperationException>(() =>
            lifetime.Physics.PrepareCollisionGeneration(superseded));
        lifetime.Physics.CancelCollisionGeneration(invalidated);
        RuntimeCollisionAdmission current = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        using PreparedLandblockCollisionGeneration prepared = lifetime.Physics
            .PrepareCollisionGeneration(current);
        lifetime.Physics.StageCollisionAssets(
            current,
            prepared,
            CollisionAssets(DestinationLandblock));
        AddSyntheticCell(prepared.DataCache, DestinationIndoorCell);
        Assert.True(CommitPrepared(
            lifetime.Physics,
            current,
            prepared).Committed);

        Assert.Equal(2, observer.Deltas.Count);
        Assert.Equal(first.Key, observer.Deltas[0].Placement.Token.Entity);
        Assert.Equal(second.Key, observer.Deltas[1].Placement.Token.Entity);
        Assert.All(observer.Deltas, delta => Assert.Equal(
            RuntimePlacementProjectionKind.Place,
            delta.Placement.Kind));
        Assert.True(lifetime.Physics.CaptureOwnership()
            .DeferredSetPositionBucketCount == 0);
    }

    [Fact]
    public void NextAdmissionMergesOlderUnboundAndNewerFutureBoundSurvivors()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord older = CreateRecord(lifetime, 0x7000111Fu, 1);
        RuntimeEntityRecord newer = CreateRecord(lifetime, 0x70001120u, 1);
        _ = AttachBody(lifetime, older, SourceCell);
        _ = AttachBody(lifetime, newer, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);

        RuntimeSetPositionOutcome olderLost = lifetime.Physics.SetPosition.Apply(
            older,
            older.PositionAuthorityVersion,
            Command(Request(
                DestinationIndoorCell,
                new Vector3(10f, 12f, 7f))));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            olderLost.Projection));
        RuntimeCollisionAdmission cancelled = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        lifetime.Physics.CancelCollisionGeneration(cancelled);

        RuntimeSetPositionOutcome newerLost = lifetime.Physics.SetPosition.Apply(
            newer,
            newer.PositionAuthorityVersion,
            Command(Request(
                DestinationIndoorCell,
                new Vector3(11f, 12f, 7f))));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            newerLost.Projection));
        RuntimePhysicsOwnershipSnapshot split = lifetime.Physics.CaptureOwnership();
        Assert.Equal(1, split.UnboundDeferredSetPositionCellCount);
        Assert.Equal(1, split.DeferredSetPositionBucketCount);
        observer.Deltas.Clear();

        RuntimeCollisionAdmission current = lifetime.Physics
            .BeginCollisionAdmission(DestinationLandblock);
        RuntimePhysicsOwnershipSnapshot merged = lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, merged.UnboundDeferredSetPositionCellCount);
        Assert.Equal(1, merged.DeferredSetPositionBucketCount);
        using PreparedLandblockCollisionGeneration prepared = lifetime.Physics
            .PrepareCollisionGeneration(current);
        lifetime.Physics.StageCollisionAssets(
            current,
            prepared,
            CollisionAssets(DestinationLandblock));
        AddSyntheticCell(prepared.DataCache, DestinationIndoorCell);
        Assert.True(CommitPrepared(
            lifetime.Physics,
            current,
            prepared).Committed);

        Assert.Equal(2, observer.Deltas.Count);
        Assert.Equal(older.Key, observer.Deltas[0].Placement.Token.Entity);
        Assert.Equal(newer.Key, observer.Deltas[1].Placement.Token.Entity);
        Assert.All(observer.Deltas, delta => Assert.Equal(
            RuntimePlacementProjectionKind.Place,
            delta.Placement.Kind));
    }

    [Theory]
    [InlineData(false, 0x0101u)]
    [InlineData(true, 0x0001u)]
    public void CollisionRetirementParksOnlyAffectedDynamicParentlessRoots(
        bool fullWithdrawal,
        uint lowCell)
    {
        const uint landblock = 0x01010000u;
        PhysicsEngine engine = FlatEngine(landblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord dynamicRoot = CreateRecord(lifetime, 0x70001007u, 1);
        uint cell = landblock | lowCell;
        PhysicsBody dynamicBody = AttachBody(
            lifetime,
            dynamicRoot,
            landblock | 0x0001u);
        ImmutableArray<FlatCollisionSphere> authoredSpheres =
        [
            new FlatCollisionSphere(new Vector3(0.25f, 0f, 0f), 0.3f),
            new FlatCollisionSphere(new Vector3(-0.2f, 0.1f, 0.4f), 0.2f),
        ];
        RuntimeSetPositionOutcome prepared = lifetime.Physics.SetPosition.Apply(
            dynamicRoot,
            dynamicRoot.PositionAuthorityVersion,
            Command(Request(
                landblock | 0x0001u,
                new Vector3(12f, 18f, 7f)) with
            {
                Spheres = authoredSpheres,
            }));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            prepared.Projection));
        Assert.True(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            dynamicRoot,
            out int sphereCount));
        Assert.Equal(2, sphereCount);
        lifetime.Entities.SetFullCell(
            dynamicRoot,
            cell,
            landblock | 0xFFFFu);
        dynamicBody.SnapToCell(cell, dynamicBody.Position, dynamicBody.Position);
        RuntimeEntityRecord staticRoot = CreateRecord(lifetime, 0x70001008u, 1);
        _ = AttachBody(lifetime, staticRoot, cell, PhysicsStateFlags.Static);
        RuntimeEntityRecord parent = CreateRecord(lifetime, 0x7000104Cu, 1);
        _ = AttachBody(lifetime, parent, SourceCell);
        RuntimeEntityRecord attached = CreateRecord(lifetime, 0x7000104Du, 1);
        _ = AttachBody(lifetime, attached, cell);
        CommitParent(lifetime, parent, attached);

        lifetime.Physics.SetPosition.ParkCollisionResidents(
            landblock,
            includeOutdoorCells: fullWithdrawal);

        Assert.True(lifetime.Physics.SetPosition.IsDeferred(dynamicRoot));
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);
        Assert.Equal(0u, dynamicRoot.FullCellId);
        Assert.False(lifetime.Physics.IsSpatialRoot(dynamicRoot));
        Assert.False(lifetime.Physics.SetPosition.IsDeferred(staticRoot));
        Assert.Equal(cell, staticRoot.FullCellId);
        Assert.True(lifetime.Physics.IsSpatialRoot(staticRoot));
        Assert.False(lifetime.Physics.SetPosition.IsDeferred(attached));
        Assert.Equal(cell, attached.FullCellId);

        lifetime.Entities.ParentAttachments.EndChildProjection(
            attached.ServerGuid);
        lifetime.Physics.SetPosition.ParkCollisionResidents(
            landblock,
            includeOutdoorCells: fullWithdrawal);
        Assert.True(lifetime.Physics.SetPosition.IsDeferred(attached));
    }

    [Fact]
    public void PickupStyleLeaveWorldCancelsLostWakeAndTerminalDisposeConverges()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001009u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        lifetime.Physics.SetPosition.LeaveWorld(record);
        AddFlatLandblock(engine, DestinationLandblock, 192f);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 1,
            ready: true);

        Assert.Equal(0u, record.FullCellId);
        Assert.Equal(0u, body.CellPosition.ObjCellId);
        Assert.False(body.InWorld);
        Assert.False(lifetime.Physics.SetPosition.IsDeferred(record));

        lifetime.Dispose();
        Assert.True(lifetime.Physics.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void OperationPoolClearsOnResetSession()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001048u, 1);
        _ = AttachBody(lifetime, record, SourceCell);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(11f, 18f, 7f))));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));

        RuntimeSetPositionOwnershipSnapshot beforeReset =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.True(beforeReset.PooledOperationCount >= 1);

        lifetime.Physics.SetPosition.ResetSession();

        Assert.Equal(
            0,
            lifetime.Physics.SetPosition.CaptureOwnership().PooledOperationCount);
    }

    [Fact]
    public void OperationPoolClearsOnDispose()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001049u, 1);
        _ = AttachBody(lifetime, record, SourceCell);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(11f, 18f, 7f))));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));

        RuntimeSetPositionOwnershipSnapshot beforeDispose =
            lifetime.Physics.SetPosition.CaptureOwnership();
        Assert.True(beforeDispose.PooledOperationCount >= 1);

        lifetime.Dispose();

        Assert.Equal(
            0,
            lifetime.Physics.SetPosition.CaptureOwnership().PooledOperationCount);
    }

    [Fact]
    public void OperationResetAllFieldsToDefaultTouchesEveryDeclaredField()
    {
        Type? operationType = typeof(RuntimeSetPositionState).GetNestedType(
            "Operation",
            BindingFlags.NonPublic);
        Assert.NotNull(operationType);

        FieldInfo[] actualFields = operationType!.GetFields(
            BindingFlags.Instance
                | BindingFlags.NonPublic
                | BindingFlags.Public);
        string[] actualFieldNames = actualFields
            .Select(field => field.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] expectedPropertyNames =
        [
            "Record",
            "Body",
            "Token",
            "Key",
            "PositionAuthorityVersion",
            "SessionLifetimeVersion",
            "SourceSpatialAuthorityVersion",
            "SourceVelocityAuthorityVersion",
            "PreviousContact",
            "PreviousOnWalkable",
            "Command",
            "Result",
            "SpatialAuthorityVersion",
            "PlacementCommitVersion",
            "ExactCellId",
            "CollisionGeneration",
            "CollisionPrefix",
            "WithdrawalAcknowledged",
            "CollisionGenerationReady",
            "CollisionQuiescenceHeld",
            "ProjectionSequence",
            "WakeableLostCell",
            "Stage",
            "Kind",
            "Portal",
            "RequiresPreparation",
            "Expired",
            "LostFamilyKeys",
            "InheritedLostDeadline",
            "EnteringWorldFromCelllessResidence",
            "DormantLocalActivation",
            "ParkReason",
            "PreparedCommandAwaitingWithdrawalAck",
            "ParkWithdrawal",
            "InPool",
        ];
        string[] expectedFieldNames = expectedPropertyNames
            .Select(name => $"<{name}>k__BackingField")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedFieldNames, actualFieldNames);

        MethodInfo? resetMethod = operationType.GetMethod(
            "ResetAllFieldsToDefault",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resetMethod);
    }

    [Fact]
    public void CommittedParentDoesNotLeakAcrossChildGuidReuse()
    {
        const uint childGuid = 0x7000104Eu;
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord parent = CreateRecord(lifetime, 0x7000104Fu, 1);
        RuntimeEntityRecord retired = CreateRecord(lifetime, childGuid, 1);
        _ = AttachBody(lifetime, parent, SourceCell);
        _ = AttachBody(lifetime, retired, SourceCell);
        CommitParent(lifetime, parent, retired);
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(childGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: false,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(lifetime.RetireCanonicalOnly(retired));

        RuntimeEntityRecord replacement = CreateRecord(lifetime, childGuid, 2);
        _ = AttachBody(lifetime, replacement, SourceCell);
        lifetime.Physics.SetPosition.ParkCollisionResidents(
            SourceLandblock,
            includeOutdoorCells: true);

        Assert.True(lifetime.Physics.SetPosition.IsDeferred(replacement));
    }

    [Fact]
    public void NewerPositionPickupAndParentEachCancelExactLostOperation()
    {
        VerifyPositionChannelCancellation(CancellationChannel.Position);
        VerifyPositionChannelCancellation(CancellationChannel.Pickup);
        VerifyPositionChannelCancellation(CancellationChannel.Parent);
    }

    [Fact]
    public void DeleteGuidReuseAndSessionResetCannotWakeStaleIncarnation()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        const uint guid = 0x7000100Du;
        RuntimeEntityRecord retired = CreateRecord(lifetime, guid, 1);
        _ = AttachBody(lifetime, retired, SourceCell);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            retired,
            retired.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(guid, 1),
            isLocalPlayer: false,
            removeRetainedObject: false,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(lifetime.RetireCanonicalOnly(retired));

        RuntimeEntityRecord replacement = CreateRecord(lifetime, guid, 2);
        PhysicsBody replacementBody = AttachBody(
            lifetime,
            replacement,
            SourceCell);
        AddFlatLandblock(engine, DestinationLandblock, 192f);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock,
            generation: 1,
            ready: true);
        Assert.Equal(SourceCell, replacement.FullCellId);
        Assert.Equal(SourceCell, replacementBody.CellPosition.ObjCellId);
        Assert.False(lifetime.Physics.SetPosition.IsDeferred(replacement));

        RuntimeSetPositionOutcome pending = lifetime.Physics.SetPosition.Apply(
            replacement,
            replacement.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(15f, 18f, 7f))));
        Assert.True(pending.Projection.IsValid);
        IReadOnlyList<RuntimeEntityRecord> retiring = lifetime.BeginSessionClear();
        Assert.Single(retiring);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .SetPositionOperationCount);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .PendingSetPositionHostAcknowledgementCount);
    }

    [Fact]
    public void ColdCollisionRetirementWaitsForExactAuthoredMoverPreparation()
    {
        const uint landblock = 0x01010000u;
        PhysicsEngine engine = FlatEngine(landblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x7000100Eu, 1);
        _ = AttachBody(lifetime, record, landblock | 0x0101u);

        lifetime.Physics.SetPosition.ParkCollisionResidents(
            landblock,
            includeOutdoorCells: false);

        Assert.True(lifetime.Physics.SetPosition.IsDeferred(record));
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            record,
            out RuntimeEntityPlacementToken token));
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            withdrawal.Token));
        ImmutableArray<FlatCollisionSphere> authored =
        [
            new FlatCollisionSphere(new Vector3(0.4f, 0f, 0f), 0.35f),
            new FlatCollisionSphere(new Vector3(-0.3f, 0f, 0.5f), 0.2f),
        ];
        RuntimeSetPositionCommand preparedCommand = PrepareAuthoredCommand(
            lifetime,
            token,
            authored);
        RuntimeSetPositionOutcome supplied = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, preparedCommand);

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, supplied.Status);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);
        Assert.False(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out _));
        AddSyntheticCell(engine.DataCache!, landblock | 0x0101u);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            landblock,
            generation: 1,
            ready: true);
        Assert.True(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out int sphereCount));
        Assert.Equal(2, sphereCount);
        Assert.Equal(landblock | 0x0101u,
            record.PhysicsBody!.CellPosition.ObjCellId);
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot placed));
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            placed.Token));
    }

    [Fact]
    public void ColdMalformedWakeReindexesUntilCorrectedPreparationAndNextAdmission()
    {
        const uint landblock = 0x01010000u;
        uint exactCell = landblock | 0x0101u;
        PhysicsEngine engine = FlatEngine(landblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001127u, 1);
        _ = AttachBody(lifetime, record, exactCell);
        var observer = new PlacementObserver();
        using IDisposable subscription = lifetime.Events.SubscribePlacement(observer);
        lifetime.Physics.SetPosition.ParkCollisionResidents(
            landblock,
            includeOutdoorCells: false);
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            record,
            out RuntimeEntityPlacementToken token));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            Assert.Single(observer.Deltas).Placement.Token));
        observer.Deltas.Clear();

        var malformedSetup = new FlatSetupCollision(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            [new FlatCollisionSphere(
                new Vector3(float.NaN, 0f, 0f),
                0.4f)],
            height: 0f,
            radius: 0f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f);
        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.InvalidData,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                new RuntimeSetPositionMoverPreparation(
                    RuntimeSetPositionMoverSetup.Resolved(
                        0x02000001u,
                        malformedSetup),
                    RuntimeSetPositionOperationKind.RemoteAuthoritative,
                    GameTime: 10d,
                    PhysicsPlacementClass.Ordinary,
                    PhysicsSetPositionFlags.Placement
                        | PhysicsSetPositionFlags.Slide),
                out _));

        RuntimeCollisionAdmission first = lifetime.Physics
            .BeginCollisionAdmission(landblock);
        using (PreparedLandblockCollisionGeneration prepared = lifetime.Physics
               .PrepareCollisionGeneration(first))
        {
            lifetime.Physics.StageCollisionAssets(
                first,
                prepared,
                CollisionAssets(landblock));
            AddSyntheticCell(prepared.DataCache, exactCell);
            Assert.True(CommitPrepared(
                lifetime.Physics,
                first,
                prepared).Committed);
        }
        RuntimeSetPositionOwnershipSnapshot invalidWake = lifetime.Physics
            .SetPosition.CaptureOwnership();
        Assert.Equal(1, invalidWake.AwaitingPreparationCount);
        Assert.Equal(0, invalidWake.DeferredBucketCount);
        Assert.Equal(1, invalidWake.LostDeadlineCount);
        Assert.Empty(observer.Deltas);
        Assert.False(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out _));

        RuntimeSetPositionCommand corrected = PrepareAuthoredCommand(
            lifetime,
            token,
            [new FlatCollisionSphere(Vector3.Zero, 0.4f)]);
        RuntimeSetPositionOutcome correctedPending = lifetime.Physics
            .SetPosition.SubmitPreparedPlacement(token, corrected);
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell,
            correctedPending.Status);
        Assert.Equal(PhysicsResidenceDisposition.Committed,
            correctedPending.Residence);
        Assert.Equal(exactCell, correctedPending.ExactCellId);

        RuntimePlacementProjectionSnapshot placed = Assert.Single(observer.Deltas)
            .Placement;
        Assert.Equal(RuntimePlacementProjectionKind.Place, placed.Kind);
        Assert.True(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out int sphereCount));
        Assert.Equal(1, sphereCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            placed.Token));
    }


    [Fact]
    public void TryPrepareAndSubmitAuthoredPlacement_ChainsSetupReadThroughPrepareMoverToSubmit()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001101u, 1);
        AttachBody(lifetime, record, SourceCell);
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(token.IsValid);

        ImmutableArray<FlatCollisionSphere> spheres =
        [
            new FlatCollisionSphere(new Vector3(0f, 0f, 0.5f), 0.4f),
            new FlatCollisionSphere(new Vector3(0f, 0f, 1.2f), 0.4f),
        ];
        var source = new FakeCollisionSource(
            0x02000001u,
            new FlatSetupCollision(
                ImmutableArray<FlatCollisionCylinder>.Empty,
                spheres,
                height: 0f,
                radius: 0f,
                stepUpHeight: 0.4f,
                stepDownHeight: 0.35f));

        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.TryPrepareAndSubmitAuthoredPlacement(
                record,
                token,
                RuntimeSetPositionOperationKind.RemoteAuthoritative,
                PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide,
                source,
                gameTime: 10d,
                out RuntimeSetPositionOutcome outcome));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(1, source.ReadCount);
        Assert.True(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out int sphereCount));
        Assert.Equal(2, sphereCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void TryPrepareAndSubmitAuthoredPlacement_YieldsRetryOnAMissingSetupReadWithoutMutatingStage()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001102u, 1);
        AttachBody(lifetime, record, SourceCell);
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(token.IsValid);

        var source = new FakeCollisionSource(
            0x02000001u,
            setup: null,
            status: PreparedAssetReadStatus.Missing);

        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.RetrySetupUnavailable,
            lifetime.Physics.SetPosition.TryPrepareAndSubmitAuthoredPlacement(
                record,
                token,
                RuntimeSetPositionOperationKind.RemoteAuthoritative,
                PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide,
                source,
                gameTime: 10d,
                out RuntimeSetPositionOutcome outcome));

        Assert.Equal(default, outcome);
        Assert.Equal(1, source.ReadCount);
        // Never manufactured a fallback while the read is in flight - the
        // token can still be prepared once the asset lands.
        Assert.True(lifetime.Physics.SetPosition.IsPlacementCurrent(token));
        Assert.False(lifetime.Physics.SetPosition.TryGetPreparedMoverSphereCount(
            record,
            out _));
    }


    [Fact]
    public void PublishExecutorCompletion_PublishesAcknowledgeOnlyReceiptAndConverges()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord record = CreateRecord(lifetime, 0x70001201u, 1);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var observer = new PlacementObserver();
        using IDisposable subscription =
            lifetime.Events.SubscribePlacement(observer);

        RuntimePlacementProjectionToken token =
            lifetime.Physics.SetPosition.PublishExecutorCompletion(record);

        Assert.True(token.IsValid);
        Assert.Equal(record.Key, token.Entity);
        RuntimePlacementProjectionSnapshot published =
            Assert.Single(observer.Deltas).Placement;
        Assert.Equal(RuntimePlacementProjectionKind.ExecutorCompleted,
            published.Kind);
        Assert.Equal(token, published.Token);
        Assert.Equal(body.Position, published.WorldPosition);
        Assert.Equal(1, lifetime.Physics.SetPosition.PendingProjectionCount);

        // Acknowledge-only, exactly like Discard - no operation to resume.
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(token));
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.False(lifetime.Physics.SetPosition.AcknowledgeProjection(token));
    }

    [Fact]
    public void PublishExecutorCompletion_RespectsExactHeadOrderingAcrossEntities()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        RuntimeEntityRecord first = CreateRecord(lifetime, 0x70001202u, 1);
        AttachBody(lifetime, first, SourceCell);
        RuntimeEntityRecord second = CreateRecord(lifetime, 0x70001203u, 1);
        AttachBody(lifetime, second, SourceCell);

        RuntimePlacementProjectionToken firstToken =
            lifetime.Physics.SetPosition.PublishExecutorCompletion(first);
        RuntimePlacementProjectionToken secondToken =
            lifetime.Physics.SetPosition.PublishExecutorCompletion(second);

        Assert.Equal(2, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.False(lifetime.Physics.SetPosition.AcknowledgeProjection(
            secondToken));
        Assert.Equal(2, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            firstToken));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            secondToken));
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
    }


    [Fact]
    public void TryCommitParent_CancelsASeparateActiveOrdinaryPendingPlacement()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        const uint guid = 0x70001301u;
        RuntimeEntityRecord record = CreateRecord(lifetime, guid, 1);
        AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(12f, 18f, 7f))));
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
            ParentGuid: 0x70001400u,
            ChildGuid: guid,
            ParentLocation: 1u,
            PlacementId: 1u,
            ParentInstanceSequence: 1,
            ChildPositionSequence: 1);
        Assert.True(lifetime.TryCommitParent(relation, null, out _));

        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
        Assert.Equal(1, lifetime.Physics.SetPosition.PendingProjectionCount);
        RuntimePlacementProjectionSnapshot discard = Assert.Single(discards);
        Assert.Equal(record.Key, discard.Token.Entity);
        Assert.Equal(outcome.Projection.Sequence, discard.Token.Sequence);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            discard.Token));
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
    }

    [Fact]
    public void CommitWithdrawal_CancelsAnActiveOrdinaryPendingPlacementSymmetricallyWithPickup()
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        const uint guid = 0x70001302u;
        RuntimeEntityRecord record = CreateRecord(lifetime, guid, 1);
        AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(Request(SourceCell, new Vector3(12f, 18f, 7f))));
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

        Assert.True(lifetime.CommitWithdrawal(record));

        Assert.Equal(0, lifetime.Physics.SetPosition.CaptureOwnership()
            .ActiveOperationCount);
        Assert.Equal(1, lifetime.Physics.SetPosition.PendingProjectionCount);
        RuntimePlacementProjectionSnapshot discard = Assert.Single(discards);
        Assert.Equal(record.Key, discard.Token.Entity);
        Assert.Equal(outcome.Projection.Sequence, discard.Token.Sequence);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            discard.Token));
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
    }

    private static void VerifyPositionChannelCancellation(
        CancellationChannel channel)
    {
        PhysicsEngine engine = FlatEngine(SourceLandblock, 0f);
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        uint guid = 0x7000100Au + (uint)channel;
        RuntimeEntityRecord record = CreateRecord(lifetime, guid, 1);
        _ = AttachBody(lifetime, record, SourceCell);
        RuntimeSetPositionOutcome deferred = lifetime.Physics.SetPosition.Apply(
            record,
            record.PositionAuthorityVersion,
            Command(CrossLandblockRequest()));
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            deferred.Projection));

        switch (channel)
        {
            case CancellationChannel.Position:
                Assert.True(lifetime.TryApplyPosition(
                    new WorldSession.EntityPositionUpdate(
                        guid,
                        new CreateObject.ServerPosition(
                            SourceCell, 11f, 20f, 7f, 1f, 0f, 0f, 0f),
                        Velocity: null,
                        PlacementId: null,
                        IsGrounded: true,
                        InstanceSequence: 1,
                        PositionSequence: 2,
                        TeleportSequence: 0,
                        ForcePositionSequence: 0),
                    isLocalPlayer: false,
                    forcePositionRotation: null,
                    currentLocalVelocity: null,
                    acknowledgeProjection: null,
                    out _,
                    out _,
                    out _));
                break;
            case CancellationChannel.Pickup:
                Assert.True(lifetime.TryApplyPickup(
                    new PickupEvent.Parsed(guid, 1, 2),
                    acknowledgeProjection: null,
                    out _));
                break;
            case CancellationChannel.Parent:
                RuntimeEntityRecord parent = CreateRecord(
                    lifetime,
                    guid + 0x100u,
                    4);
                Assert.True(lifetime.TryApplyParent(
                    new ParentEvent.Parsed(
                        parent.ServerGuid,
                        guid,
                        ParentLocation: 1,
                        PlacementId: 2,
                        ParentInstanceSequence: parent.Incarnation,
                        ChildPositionSequence: 2),
                    acknowledgeProjection: null,
                    out _));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Assert.False(lifetime.Physics.SetPosition.IsDeferred(record));
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .DeferredSetPositionCount);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .LostCellDeadlineCount);

        if (channel is CancellationChannel.Position)
        {
            Assert.True(
                record.PhysicsBody?.InWorld ?? false,
                "the cancellation channel must roll the park back");
            Assert.True(
                lifetime.Physics.IsSpatialRoot(record),
                "the cancellation channel must restore the spatial root");
        }
        else
        {
            Assert.False(
                record.PhysicsBody?.InWorld ?? false,
                "a withdrawal channel must not leave the body InWorld");
            Assert.False(
                lifetime.Physics.IsSpatialRoot(record),
                "a withdrawal channel must not leave the entity a spatial root");
        }
    }

    private static void CommitParent(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord parent,
        RuntimeEntityRecord child)
    {
        var relation = new ParentAttachmentRelation(
            parent.ServerGuid,
            child.ServerGuid,
            ParentLocation: 1,
            PlacementId: 2,
            ParentInstanceSequence: parent.Incarnation,
            ChildPositionSequence: 1);
        lifetime.Entities.ParentAttachments.AcceptCreateObjectRelation(relation);
        Assert.True(lifetime.Entities.ParentAttachments.CommitProjection(relation));
    }

    private static RuntimeSetPositionCommand Command(
        PhysicsSetPositionRequest request) => new(
            request,
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 10d,
            ExpectedVelocityAuthorityVersion: 0UL,
            ShadowWorldOffsetX: request.CellId == SourceCell ? 0f : 192f,
            ShadowWorldOffsetY: 0f);

    private static RuntimeSetPositionCommand PrepareAuthoredCommand(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityPlacementToken token,
        ImmutableArray<FlatCollisionSphere> spheres)
    {
        var setup = new FlatSetupCollision(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            spheres,
            height: 0f,
            radius: 0f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f);
        var preparation = new RuntimeSetPositionMoverPreparation(
            RuntimeSetPositionMoverSetup.Resolved(0x02000001u, setup),
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 10d,
            PhysicsPlacementClass.Ordinary,
            PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Slide);
        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                preparation,
                out RuntimeSetPositionCommand command));
        return command;
    }

    private static PhysicsSetPositionRequest CrossLandblockRequest() =>
        Request(
            SourceCell,
            new Vector3(193f, 12f, 7f),
            new Vector3(193f, 12f, 7f));

    private static PhysicsSetPositionRequest Request(
        uint cellId,
        Vector3 position,
        Vector3? cellLocal = null) => new(
            position,
            Quaternion.Identity,
            cellId,
            cellLocal ?? position,
            ImmutableArray<FlatCollisionSphere>.Empty,
            Scale: 1f,
            StepUpHeight: 0.4f,
            StepDownHeight: 0.4f,
            Flags: PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Slide);

    private static RuntimeEntityRecord CreateRecord(
        RuntimeEntityObjectLifetime lifetime,
        uint guid,
        ushort incarnation)
    {
        RuntimeEntityRecord record = lifetime.RegisterEntity(
            Spawn(guid, incarnation)).Canonical!;
        lifetime.Entities.SetFinalPhysicsState(record, PhysicsStateFlags.Gravity);
        return record;
    }

    private static PhysicsBody AttachBody(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record,
        uint cellId,
        PhysicsStateFlags state = PhysicsStateFlags.Gravity)
    {
        lifetime.Entities.SetFullCell(
            record,
            cellId,
            (cellId & 0xFFFF0000u) | 0xFFFFu);
        lifetime.Entities.SetFinalPhysicsState(record, state);
        var body = new PhysicsBody
        {
            Position = new Vector3(10f, 20f, 7f),
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = state,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(cellId, body.Position, body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
        return body;
    }

    private static PhysicsEngine FlatEngine(uint landblock, float worldOffsetX)
    {
        var engine = new PhysicsEngine
        {
            DataCache = new PhysicsDataCache(),
        };
        AddFlatLandblock(engine, landblock, worldOffsetX);
        return engine;
    }

    private static RuntimeLandblockCollisionAssets CollisionAssets(
        uint landblockId) => new(
            landblockId,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            WorldOffsetX: 192f,
            WorldOffsetY: 0f,
            CurrentCellId: 0u);

    private static RuntimeCollisionGenerationCommit CommitPrepared(
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        bool engineCommitted = false;
        for (int poll = 0; poll < 10_000; poll++)
        {
            if (!engineCommitted)
            {
                while (!physics.AdvanceCollisionRetainedOwnerCapture(
                           admission,
                           prepared).Completed)
                {
                }
                foreach (uint ownerId in prepared.RetainedOwnerIds)
                {
                    physics.RefreshCollisionRetainedOwner(
                        admission,
                        prepared,
                        ownerId);
                }
                RuntimeCollisionSealStep seal;
                do
                {
                    seal = physics.AdvanceCollisionGenerationSeal(
                        admission,
                        prepared);
                }
                while (!seal.Completed && !seal.Restarted);
                if (!seal.Completed)
                    continue;
            }
            RuntimeCollisionGenerationCommit result =
                physics.CommitCollisionGeneration(admission, prepared);
            if (result.Completed)
                return result;
            while (physics.SetPosition.TryPeekProjection(
                       out RuntimePlacementProjectionSnapshot projection))
            {
                Assert.True(physics.SetPosition.AcknowledgeProjection(
                    projection.Token));
            }
            engineCommitted = result.EngineCommitted;
        }
        throw new InvalidOperationException(
            "Collision generation did not complete its Runtime mutation transaction.");
    }

    private static void AddSyntheticCell(
        PhysicsDataCache cache,
        uint cellId)
    {
        cache.RegisterCellStructForTest(
            cellId,
            new CellPhysics
            {
                WorldTransform = Matrix4x4.Identity,
                InverseWorldTransform = Matrix4x4.Identity,
                Resolved = new Dictionary<ushort, ResolvedPolygon>(),
                Portals = [new PortalInfo(0, 0, 0)],
                CellBSP = new CellBSPTree
                {
                    Root = new CellBSPNode { Type = BSPNodeType.Leaf },
                },
            });
        cache.CellGraph.Add(new EnvCell(
            cellId,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Array.Empty<AcDream.Core.World.Cells.CellPortal>(),
            Array.Empty<uint>(),
            seenOutside: false,
            containmentBsp: null));
    }

    private static void AddFlatLandblock(
        PhysicsEngine engine,
        uint landblock,
        float worldOffsetX)
    {
        engine.AddLandblock(
            landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX,
            worldOffsetY: 0f);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort instance)
    {
        var position = new CreateObject.ServerPosition(
            SourceCell,
            10f,
            20f,
            7f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.Gravity,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
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
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "set-position-fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.Gravity,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class PlacementObserver(
        Action<RuntimePlacementDelta>? onPlacement = null)
        : IRuntimePlacementObserver
    {
        internal List<RuntimePlacementDelta> Deltas { get; } = [];

        public void OnPlacement(in RuntimePlacementDelta delta)
        {
            Deltas.Add(delta);
            onPlacement?.Invoke(delta);
        }
    }

    private sealed class CountingPlacementObserver : IRuntimePlacementObserver
    {
        internal int Count { get; private set; }

        public void OnPlacement(in RuntimePlacementDelta delta) => Count++;
    }

    private sealed class FakeCollisionSource(
        uint expectedSetupTableId,
        FlatSetupCollision? setup,
        PreparedAssetReadStatus status = PreparedAssetReadStatus.Loaded)
        : IPreparedCollisionSource
    {
        internal int ReadCount { get; private set; }

        public PreparedAssetPresence ProbeCollision(
            PakAssetType type, uint sourceFileId) =>
            PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Only ReadSetupCollision is exercised by C0-3.");

        public PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            ReadCount++;
            Assert.Equal(expectedSetupTableId, sourceFileId);
            return status switch
            {
                PreparedAssetReadStatus.Loaded when setup is not null =>
                    PreparedCollisionReadResult<FlatSetupCollision>.Loaded(
                        setup),
                PreparedAssetReadStatus.Corrupt =>
                    PreparedCollisionReadResult<FlatSetupCollision>.Corrupt,
                _ => PreparedCollisionReadResult<FlatSetupCollision>.Missing,
            };
        }

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Only ReadSetupCollision is exercised by C0-3.");

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Only ReadSetupCollision is exercised by C0-3.");

        public PreparedCollisionSourceStats CollisionStats =>
            new(ReadCount, ReadCount, ReadCount, 0, 0);

        public void Dispose()
        {
        }
    }

    private sealed class CollisionReportObserver
        : IRuntimeCollisionReportObserver
    {
        internal List<RuntimeCollisionReport> Reports { get; } = [];

        public void OnCollisionReport(in RuntimeCollisionReport report) =>
            Reports.Add(report);
    }

    private sealed class EntityObserver : IRuntimeEntityObjectObserver
    {
        internal List<RuntimeEntityDelta> Deltas { get; } = [];

        public void OnEntity(in RuntimeEntityDelta delta) => Deltas.Add(delta);

        public void OnInventory(in RuntimeInventoryDelta delta)
        {
        }
    }

    private sealed class ThrowingPlacementObserver : IRuntimePlacementObserver
    {
        public void OnPlacement(in RuntimePlacementDelta delta) =>
            throw new InvalidOperationException("fixture observer failure");
    }

    private sealed class ReentrantRemotePlacement(PhysicsBody body)
        : IRuntimeRemotePlacement
    {
        private Func<uint>? _readCell;
        private Action<uint>? _writeCell;
        private uint _cellId;

        public PhysicsBody Body { get; } = body;
        public uint CellId
        {
            get => _readCell?.Invoke() ?? _cellId;
            set
            {
                _cellId = value;
                _writeCell?.Invoke(value);
            }
        }
        public bool Airborne { get; set; }
        public Vector3 LastServerPosition { get; set; }
        public double LastServerPositionTime { get; set; }
        public Vector3 LastShadowSyncPosition { get; set; }
        public Quaternion LastShadowSyncOrientation { get; set; }
        internal Action? OnHitGround { get; init; }
        internal Action? OnLeaveGround { get; init; }

        public void BindCanonicalCell(Func<uint> read, Action<uint> write)
        {
            _readCell = read;
            _writeCell = write;
        }

        public void HitGround() => OnHitGround?.Invoke();

        public void LeaveGround() => OnLeaveGround?.Invoke();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
        }

        internal void JumpUtc(TimeSpan duration) => _utcNow += duration;
    }

    public enum DeferredPhysicsStateMutation
    {
        ChildNoDraw,
        StopMissile,
        DirectFinalState,
    }

    private enum CancellationChannel : uint
    {
        Position,
        Pickup,
        Parent,
    }
}

internal static class RuntimePlacementProjectionTokenTestExtensions
{
    internal static ulong TokenSequence(
        this RuntimePlacementProjectionToken token) => token.Sequence;
}
