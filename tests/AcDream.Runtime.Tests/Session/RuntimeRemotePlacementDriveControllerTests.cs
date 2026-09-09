using System.Numerics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

public sealed class RuntimeRemotePlacementDriveControllerTests
{
    private const uint SourceLandblock = 0xB1000000u;
    private const uint SourceCell = SourceLandblock | 0x0001u;
    private const uint DestinationLandblock = 0xB2000000u;
    private const uint DestinationCell = DestinationLandblock | 0x0001u;

    private const uint NeighbourLandblock = 0xB3000000u;

    private const uint DestinationSeamCell = DestinationLandblock | 57u;
    private const float SpawnHeight = 7f;

    [Fact]
    public void NotApplicable_WhenDispositionIsNotOwnedByThisRoute()
    {
        foreach (RuntimeAuthoritativePositionDisposition disposition in
            new[]
            {
                RuntimeAuthoritativePositionDisposition.Interpolate,
                RuntimeAuthoritativePositionDisposition.NoPositionOperation,
                RuntimeAuthoritativePositionDisposition.RejectedAuthority,
                RuntimeAuthoritativePositionDisposition.RejectedData,
                RuntimeAuthoritativePositionDisposition.AwaitFreshPosition,
            })
        {
            using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
            RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003001u);
            AttachBody(lifetime, record, SourceCell);
            var window = new FakeServiceWindow();
            window.Allow(DestinationLandblock);
            RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

            RuntimeAuthoritativePositionRoute route = MakeRoute(
                record, disposition, DestinationCell);

            Assert.Equal(
                RuntimeRemotePlacementExecutionStatus.NotApplicable,
                drive.TryExecuteAcceptedRemotePosition(record, route));
            Assert.Equal(0, drive.PendingCount);
            AssertConverged(lifetime);
        }
    }

    [Fact]
    public void NotApplicable_WhenTheEntityHasNoCanonicalBody()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003002u);
        // Deliberately never attach a body.
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.NotApplicable,
            drive.TryExecuteAcceptedRemotePosition(record, route));
    }

    [Fact]
    public void OwnsPlacement_FalseWhenOperationKindIsNotRemoteOrProjectileAuthoritative()
    {
        foreach (RuntimeSetPositionOperationKind operationKind in
            new[]
            {
                RuntimeSetPositionOperationKind.InitialLogin,
                RuntimeSetPositionOperationKind.LocalAuthoritative,
            })
        {
            using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
            RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x7000300Fu);

            RuntimeAuthoritativePositionRoute route = MakeRoute(
                record,
                RuntimeAuthoritativePositionDisposition.SetPositionSimple,
                DestinationCell,
                operationKind: operationKind);

            Assert.False(
                RuntimeRemotePlacementDriveController.OwnsPlacement(route));

            var window = new FakeServiceWindow();
            window.Allow(DestinationLandblock);
            RuntimeRemotePlacementDriveController drive =
                CreateDrive(lifetime, window);
            Assert.Equal(
                RuntimeRemotePlacementExecutionStatus.NotApplicable,
                drive.TryExecuteAcceptedRemotePosition(record, route));
            Assert.Equal(0, drive.PendingCount);
        }
    }

    [Fact]
    public void OwnsPlacement_FalseForARemoteTopLevelCreateShapedRoute()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003014u);

        RuntimeAuthoritativePositionRoute createRoute = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            operationKind: RuntimeSetPositionOperationKind.RemoteAuthoritative,
            setPositionFlags: PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Slide);

        Assert.False(
            RuntimeRemotePlacementDriveController.OwnsPlacement(createRoute));

        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive =
            CreateDrive(lifetime, window);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.NotApplicable,
            drive.TryExecuteAcceptedRemotePosition(record, createRoute));
        Assert.Equal(0, drive.PendingCount);

        RuntimeAuthoritativePositionRoute positionRoute = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell);
        Assert.True(
            RuntimeRemotePlacementDriveController.OwnsPlacement(positionRoute));
    }

    [Fact]
    public void Refused_WhenDestinationIsNotWithinServiceWindow_NoOperationOpensAndBodyStaysInWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003003u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var window = new FakeServiceWindow();
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell);

        RuntimeRemotePlacementExecutionStatus status =
            drive.TryExecuteAcceptedRemotePosition(record, route);

        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Refused, status);
        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(
            0,
            lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void CentralDecision_RefusedPlacementThenNextAcceptedPositionForgetsNothing_EntityStaysInWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003004u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var window = new FakeServiceWindow();
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell);

        // Packet N: destination not placeable now.
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            drive.TryExecuteAcceptedRemotePosition(record, route));
        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);

        RuntimePlacementCancellationReceipt cancellation =
            lifetime.Physics.SetPosition.Forget(record);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);

        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(SourceCell, record.FullCellId);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Committed_WhenDestinationIsWithinServiceWindowAndCollisionGenerationCommitted()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003005u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination);

        RuntimeRemotePlacementExecutionStatus status =
            drive.TryExecuteAcceptedRemotePosition(record, route);

        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Committed, status);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.Equal(0, drive.PendingCount);
        DrainPlacementFifo(lifetime);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Committed_UnacknowledgedOperationStaysVisibleInTheLedgerUntilAcknowledged()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003010u);
        AttachBody(lifetime, record, SourceCell);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination);

        RuntimeRemotePlacementExecutionStatus status =
            drive.TryExecuteAcceptedRemotePosition(record, route);

        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Committed, status);
        // No host subscription is wired in this bare fixture, so the Place
        // receipt is still genuinely unacknowledged — the ledger must SEE it.
        Assert.Equal(
            1, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        Assert.Equal(
            1,
            lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);

        DrainPlacementFifo(lifetime);

        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Contention_WhenTheEntityAlreadyOwnsAnActiveOperation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003006u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        Vector3 positionBefore = body.Position;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        // An external placement authority (portal/teleport/another route)
        // already owns this entity's SetPosition operation.
        RuntimeEntityPlacementToken displaced = lifetime.Physics.SetPosition
            .TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(displaced.IsValid);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.TryExecuteAcceptedRemotePosition(record, route));
        Assert.Equal(positionBefore, body.Position);
        Assert.Equal(0, drive.PendingCount);

        RuntimePlacementCancellationReceipt cancellation = lifetime.Physics
            .SetPosition.ForgetExactPlacement(displaced);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);
    }

    [Fact]
    public void PerEntityIndependence_TwoRemotesInterleavedDoNotBlockEachOther()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord first = CreateRemoteRecord(lifetime, 0x70003007u);
        RuntimeEntityRecord second = CreateRemoteRecord(lifetime, 0x70003008u);
        PhysicsBody firstBody = AttachBody(lifetime, first, SourceCell);
        PhysicsBody secondBody = AttachBody(lifetime, second, SourceCell);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        // First entity's own operation is still outstanding (an external
        // authority holds it) when the second entity's Position arrives.
        RuntimeEntityPlacementToken firstDisplaced = lifetime.Physics.SetPosition
            .TryBeginExclusiveAuthoredPlacement(
                first,
                first.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(firstDisplaced.IsValid);

        RuntimeAuthoritativePositionRoute firstRoute = MakeRoute(
            first, RuntimeAuthoritativePositionDisposition.SetPosition, DestinationCell);
        RuntimeAuthoritativePositionRoute secondRoute = MakeRoute(
            second, RuntimeAuthoritativePositionDisposition.SetPosition, DestinationCell,
            new Vector3(20f, 22f, SpawnHeight));

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.TryExecuteAcceptedRemotePosition(first, firstRoute));
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            drive.TryExecuteAcceptedRemotePosition(second, secondRoute));

        Assert.Equal(
            new Vector3(20f, 22f, SpawnHeight) + new Vector3(192f, 0f, 0f),
            secondBody.Position);
        Assert.Equal(0, drive.PendingCount);

        RuntimePlacementCancellationReceipt cancellation = lifetime.Physics
            .SetPosition.ForgetExactPlacement(firstDisplaced);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);
        DrainPlacementFifo(lifetime);
        _ = firstBody;
    }

    [Fact]
    public void PerEntityIndependence_TwoConcurrentPreparationRetriesDoNotCollide()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord first = CreateRemoteRecord(
            lifetime, 0x7000300Du, setupTableId: 0x02000001u);
        RuntimeEntityRecord second = CreateRemoteRecord(
            lifetime, 0x7000300Eu, setupTableId: 0x02000001u);
        AttachBody(lifetime, first, SourceCell);
        AttachBody(lifetime, second, SourceCell);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute firstRoute = MakeRoute(
            first, RuntimeAuthoritativePositionDisposition.SetPosition, DestinationCell);
        RuntimeAuthoritativePositionRoute secondRoute = MakeRoute(
            second, RuntimeAuthoritativePositionDisposition.SetPosition, DestinationCell);

        // Both entities' Setup assets are unresolved (Missing) — both retry.
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.TryExecuteAcceptedRemotePosition(first, firstRoute));
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.TryExecuteAcceptedRemotePosition(second, secondRoute));
        Assert.Equal(2, drive.PendingCount);

        RuntimePlacementCancellationReceipt cancellation =
            lifetime.Physics.SetPosition.Forget(first);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);

        drive.Advance();

        Assert.Equal(1, drive.PendingCount);
        Assert.Equal(
            1, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);

        RuntimePlacementCancellationReceipt secondCancellation =
            lifetime.Physics.SetPosition.Forget(second);
        if (secondCancellation.IsValid)
        {
            lifetime.Physics.SetPosition
                .PublishCancellation(secondCancellation);
        }
        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
    }

    [Fact]
    public void Advance_DropsRetainedRetryWhenTheServiceWindowNoLongerCoversItsDestination()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(
            lifetime, 0x70003011u, setupTableId: 0x02000001u);
        AttachBody(lifetime, record, SourceCell);
        CommitLandblockCollision(lifetime, DestinationLandblock);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record, RuntimeAuthoritativePositionDisposition.SetPosition, DestinationCell);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.TryExecuteAcceptedRemotePosition(record, route));
        Assert.Equal(1, drive.PendingCount);
        Assert.Equal(
            1, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);

        window.Forbid(DestinationLandblock);

        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void StalePreparationRetry_SelfHealsRatherThanLeakingWhenTheNextPacketNeverTouchesPending()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(
            lifetime, 0x70003009u, setupTableId: 0x02000001u);
        AttachBody(lifetime, record, SourceCell);
        CommitLandblockCollision(lifetime, DestinationLandblock);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record, RuntimeAuthoritativePositionDisposition.SetPosition, DestinationCell);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.TryExecuteAcceptedRemotePosition(record, route));
        Assert.Equal(1, drive.PendingCount);

        RuntimePlacementCancellationReceipt cancellation =
            lifetime.Physics.SetPosition.Forget(record);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);

        window.Forbid(DestinationLandblock);
        RuntimeAuthoritativePositionRoute refusedRoute = MakeRoute(
            record, RuntimeAuthoritativePositionDisposition.SetPosition, DestinationCell);

        RuntimeRemotePlacementExecutionStatus status =
            drive.TryExecuteAcceptedRemotePosition(record, refusedRoute);
        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Refused, status);
        Assert.Equal(0, drive.PendingCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void LedgerConverges_AfterDetachRouteClearsTrackedEntries()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(
            lifetime, 0x7000300Au, setupTableId: 0x02000001u);
        AttachBody(lifetime, record, SourceCell);
        CommitLandblockCollision(lifetime, DestinationLandblock);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);
        var route = new object();
        drive.AttachRoute(route);

        RuntimeAuthoritativePositionRoute positionRoute = MakeRoute(
            record, RuntimeAuthoritativePositionDisposition.SetPosition, DestinationCell);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.TryExecuteAcceptedRemotePosition(record, positionRoute));
        Assert.Equal(1, drive.PendingCount);
        Assert.Equal(
            1, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);

        drive.DetachRoute(route);

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(0, lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
    }

    [Fact]
    public void LedgerConverges_AfterDetachRouteCancelsAnUnacknowledgedCommit()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003012u);
        AttachBody(lifetime, record, SourceCell);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);
        var route = new object();
        drive.AttachRoute(route);

        RuntimeAuthoritativePositionRoute positionRoute = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            new Vector3(12f, 14f, SpawnHeight));
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            drive.TryExecuteAcceptedRemotePosition(record, positionRoute));
        Assert.Equal(
            1, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        Assert.Equal(
            1, lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);

        drive.DetachRoute(route);

        Assert.Equal(
            0, lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
    }

    [Fact]
    public void CountLiveAwaitingAcknowledgement_SurvivesReadAfterDisposeWithoutThrowing()
    {
        var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003013u);
        AttachBody(lifetime, record, SourceCell);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute positionRoute = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            new Vector3(12f, 14f, SpawnHeight));
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            drive.TryExecuteAcceptedRemotePosition(record, positionRoute));

        lifetime.Dispose();
        int pendingAfterDispose = -1;
        Exception? thrown = Record.Exception(() =>
            pendingAfterDispose =
                lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);

        Assert.Null(thrown);
        Assert.Equal(1, pendingAfterDispose);
    }

    [Fact]
    public void AttachRoute_ThrowsForADifferentRouteWhileTheFirstIsStillLive()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        var window = new FakeServiceWindow();
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);
        var firstRoute = new object();
        var secondRoute = new object();
        drive.AttachRoute(firstRoute);

        Assert.Throws<InvalidOperationException>(
            () => drive.AttachRoute(secondRoute));

        drive.DetachRoute(firstRoute);
        drive.AttachRoute(secondRoute);
    }

    [Fact]
    public void ParkCollisionResidents_StaysUnreachable_AfterRefusalAndAfterCommit()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord refusedEntity = CreateRemoteRecord(lifetime, 0x7000300Bu);
        AttachBody(lifetime, refusedEntity, DestinationCell);
        RuntimeEntityRecord committedEntity = CreateRemoteRecord(lifetime, 0x7000300Cu);
        AttachBody(lifetime, committedEntity, SourceCell);

        var refusingWindow = new FakeServiceWindow();
        RuntimeRemotePlacementDriveController refusingDrive =
            CreateDrive(lifetime, refusingWindow);
        RuntimeAuthoritativePositionRoute refusedRoute = MakeRoute(
            refusedEntity,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            refusingDrive.TryExecuteAcceptedRemotePosition(refusedEntity, refusedRoute));

        var allowingWindow = new FakeServiceWindow();
        allowingWindow.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController committingDrive =
            CreateDrive(lifetime, allowingWindow);
        RuntimeAuthoritativePositionRoute committedRoute = MakeRoute(
            committedEntity,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            new Vector3(30f, 30f, SpawnHeight));
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            committingDrive.TryExecuteAcceptedRemotePosition(
                committedEntity, committedRoute));
        DrainPlacementFifo(lifetime);

        // Both entities now sit resident in the destination prefix with NO
        // active operation. Retiring that prefix must not throw.
        Exception? thrown = Record.Exception(() =>
            lifetime.Physics.SetPosition.ParkCollisionResidents(
                DestinationLandblock, includeOutdoorCells: true));
        Assert.Null(thrown);
    }

    // ── C4 route 4b-2: the far-snap arm ──────────────────────────────────

    [Fact]
    public void FarSnap_ClearsTheInterpolationQueue_IndependentlyOfThePlacementOutcome()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003020u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        remote.Interp.Enqueue(
            new Vector3(40f, 40f, SpawnHeight),
            Quaternion.Identity,
            isMovingTo: false,
            currentBodyPosition: body.Position,
            currentBodyOrientation: body.Orientation);
        Assert.True(remote.Interp.IsActive);

        // The service window allows nothing, so the placement refuses.
        RuntimeRemotePlacementDriveController drive =
            CreateDrive(lifetime, new FakeServiceWindow());
        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        Assert.False(remote.Interp.IsActive);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(positionBefore, body.Position);
        Assert.True(body.InWorld);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_ContendedByAnotherAuthority_StillStoresTheDestinationPose()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003024u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeEntityPlacementToken displaced = lifetime.Physics.SetPosition
            .TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(displaced.IsValid);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(positionBefore, body.Position);
        Assert.Equal(0, drive.PendingCount);

        RuntimePlacementCancellationReceipt cancellation = lifetime.Physics
            .SetPosition.ForgetExactPlacement(displaced);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_RetryablePreparation_StoresThePoseAndStillRetainsTheRetry()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(
            lifetime, 0x70003025u, setupTableId: 0x02000001u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));
        Assert.Equal(1, drive.PendingCount);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(positionBefore, body.Position);

        drive.Advance();
        Assert.Equal(1, drive.PendingCount);
        RuntimePlacementCancellationReceipt cancellation =
            lifetime.Physics.SetPosition.Forget(record);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);
        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_DeferredCellPark_IsCancelledAndRolledBackAtTheDestination()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        lifetime.Physics.ObserveLocalWorldFrame(
            SourceCell, teleportAdvanced: false);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003026u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Deferred,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        // The park was rolled back, not left standing.
        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.NotEqual(0u, record.FullCellId);
        // …and the body still tracked the server.
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_QuiescingDestinationPrefix_RefusesWithoutOpeningANonRestorablePark()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003028u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        lifetime.Physics.SetPosition.BeginCollisionPrefixQuiescence(
            DestinationLandblock,
            collisionGeneration: 1UL,
            includeOutdoorCells: true);
        Assert.True(window.IsWithinServiceWindow(DestinationCell));
        Assert.True(
            lifetime.Physics.SetPosition.IsCollisionPrefixQuiescing(
                DestinationLandblock));

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        // No park was ever opened, so there is nothing to fail to restore.
        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_QuiescingSourceLandblock_RestoresTheRemoteIntoTheWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003030u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        lifetime.Physics.SetPosition.BeginCollisionPrefixQuiescence(
            SourceLandblock,
            collisionGeneration: 1UL,
            includeOutdoorCells: true);
        Assert.True(window.IsWithinServiceWindow(DestinationCell));
        Assert.False(
            lifetime.Physics.SetPosition.IsCollisionPrefixQuiescing(
                DestinationLandblock));
        Assert.True(
            lifetime.Physics.SetPosition.IsCollisionPrefixQuiescing(
                SourceLandblock));

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Deferred,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.NotEqual(0u, record.FullCellId);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_QuiescingSweptNeighbour_RestoresTheRemoteIntoTheWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        CommitLandblockCollision(
            lifetime, NeighbourLandblock, worldOffsetX: 384f);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003031u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        lifetime.Physics.SetPosition.BeginCollisionPrefixQuiescence(
            NeighbourLandblock,
            collisionGeneration: 1UL,
            includeOutdoorCells: true);
        // Neither half of the pre-flight can see the neighbour.
        Assert.True(window.IsWithinServiceWindow(DestinationSeamCell));
        Assert.False(
            lifetime.Physics.SetPosition.IsCollisionPrefixQuiescing(
                DestinationLandblock));

        var destination = new Vector3(191.95f, 10f, SpawnHeight - 1f);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationSeamCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Deferred,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.NotEqual(0u, record.FullCellId);
        Assert.Equal(SpawnHeight, body.Position.Z);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_ConcurrentQuiescences_RefusesBeforeOpeningAPark()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003037u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeCollisionPrefixQuiescenceToken sourceQuiescence =
            lifetime.Physics.SetPosition.BeginCollisionPrefixQuiescence(
                SourceLandblock,
                collisionGeneration: 2UL,
                includeOutdoorCells: true);
        RuntimeCollisionPrefixQuiescenceToken destinationQuiescence =
            lifetime.Physics.SetPosition.BeginCollisionPrefixQuiescence(
                DestinationLandblock,
                collisionGeneration: 2UL,
                includeOutdoorCells: true);
        Assert.True(
            sourceQuiescence.OperationId < destinationQuiescence.OperationId);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        // No park was opened, so nothing was ever withdrawn — and nothing was
        // re-admitted into either retiring prefix.
        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_EngineRefusedTheDestination_LeavesTheBodyWhereItWas()
    {
        PhysicsEngine engine = FlatEngine();
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003032u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        engine.TransitionCellCollisionTestHook =
            static (_, _, _, _) => TransitionState.Collided;

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.RejectedByPlacement,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        Assert.Equal(positionBefore, body.Position);
        Assert.NotEqual(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_CancelledAfterTheCommitSettled_KeepsTheSettledPose()
    {
        PhysicsEngine engine = FlatEngine();
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f),
                        DestinationCell);
                }
                return observed;
            };
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003033u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        var supersedingDestination = new Vector3(77f, 88f, SpawnHeight);
        Vector3 settled = Vector3.Zero;
        RuntimeEntityPlacementToken displaced = default;
        remote.Movement.Minterp.RemoveLinkAnimations = () =>
        {
            settled = body.Position;
            record.Snapshot = record.Snapshot with
            {
                Position = new CreateObject.ServerPosition(
                    DestinationCell,
                    supersedingDestination.X,
                    supersedingDestination.Y,
                    supersedingDestination.Z,
                    1f,
                    0f,
                    0f,
                    0f),
            };
            displaced = lifetime.Physics.SetPosition.BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        };

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.RejectedByPlacement,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        Assert.True(displaced.IsValid);
        Assert.NotEqual(Vector3.Zero, settled);
        Assert.Equal(settled, body.Position);
        Assert.NotEqual(
            supersedingDestination + new Vector3(192f, 0f, 0f),
            body.Position);

        RuntimePlacementCancellationReceipt cancellation = lifetime.Physics
            .SetPosition.ForgetExactPlacement(displaced);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_PreparationRejected_StillStoresTheDestinationPose()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003034u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationLandblock,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.RejectedPreparation,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(positionBefore, body.Position);
        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_SupersededIncarnation_DoesNotStoreThroughTheStaleRecord()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003036u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        RuntimeRemotePlacementDriveController drive =
            CreateDrive(lifetime, new FakeServiceWindow());

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.True(lifetime.Entities.RemoveActive(record));
        Assert.False(lifetime.Entities.IsCurrent(record));
        Assert.NotNull(record.PhysicsBody);
        Assert.NotNull(record.Key);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        Assert.Equal(positionBefore, body.Position);
        Assert.NotEqual(destination + new Vector3(192f, 0f, 0f), body.Position);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Advance_DestinationLeavesTheWindow_StoresTheNewestDestinationPose()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(
            lifetime, 0x70003035u, setupTableId: 0x02000001u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var firstDestination = new Vector3(12f, 14f, SpawnHeight);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.ApplyAcceptedRemoteFarSnap(
                record,
                remote,
                MakeRoute(
                    record,
                    RuntimeAuthoritativePositionDisposition.SetPositionSimple,
                    DestinationCell,
                    firstDestination,
                    stopInterpolating: true)));
        Assert.Equal(1, drive.PendingCount);
        Assert.Equal(
            firstDestination + new Vector3(192f, 0f, 0f), body.Position);

        var newestDestination = new Vector3(40f, 50f, SpawnHeight);
        record.Snapshot = record.Snapshot with
        {
            Position = new CreateObject.ServerPosition(
                DestinationCell,
                newestDestination.X,
                newestDestination.Y,
                newestDestination.Z,
                1f,
                0f,
                0f,
                0f),
        };
        window.Forbid(DestinationLandblock);

        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            newestDestination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void RemotePlacementLedger_ConvergesAcrossGuidReuse_WithoutTeardown()
    {
        const uint reusedGuid = 0x70003027u;
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);
        var route = new object();
        drive.AttachRoute(route);

        RuntimeEntityRecord first = CreateRemoteRecord(
            lifetime, reusedGuid, setupTableId: 0x02000001u);
        AttachBody(lifetime, first, SourceCell);
        RemoteMotion firstRemote = lifetime.Physics.GetOrCreateRemoteMotion(first);
        uint firstIncarnation = first.Incarnation;
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.ApplyAcceptedRemoteFarSnap(
                first,
                firstRemote,
                MakeRoute(
                    first,
                    RuntimeAuthoritativePositionDisposition.SetPositionSimple,
                    DestinationCell,
                    new Vector3(12f, 14f, SpawnHeight),
                    stopInterpolating: true)));
        Assert.Equal(1, drive.PendingCount);

        RuntimeEntityRecord second = CreateRemoteRecord(
            lifetime, reusedGuid, instanceSequence: 1);
        Assert.NotEqual(firstIncarnation, second.Incarnation);
        AttachBody(lifetime, second, SourceCell);

        Assert.Equal(
            0,
            lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);
        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);

        drive.DetachRoute(route);
    }

    [Fact]
    public void FarSnap_HonoursTheRoutesOwnStopInterpolatingFlag()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003021u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        remote.Interp.Enqueue(
            new Vector3(40f, 40f, SpawnHeight),
            Quaternion.Identity,
            isMovingTo: false,
            currentBodyPosition: body.Position,
            currentBodyOrientation: body.Orientation);

        RuntimeRemotePlacementDriveController drive =
            CreateDrive(lifetime, new FakeServiceWindow());
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            stopInterpolating: false);

        _ = drive.ApplyAcceptedRemoteFarSnap(record, remote, route);

        Assert.True(remote.Interp.IsActive);
    }

    [Fact]
    public void FarSnap_CommitsThroughTheCanonicalPlacementOwner()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003022u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            drive.ApplyAcceptedRemoteFarSnap(record, remote, route));

        Assert.Same(body, remote.Body);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.Equal(DestinationCell, record.FullCellId);
        DrainPlacementFifo(lifetime);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void FarSnap_ThrowsForARouteThisArmDoesNotOwn()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003023u);
        AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        RuntimeRemotePlacementDriveController drive =
            CreateDrive(lifetime, new FakeServiceWindow());

        foreach (RuntimeAuthoritativePositionDisposition disposition in
            new[]
            {
                RuntimeAuthoritativePositionDisposition.SetPosition,
                RuntimeAuthoritativePositionDisposition.Interpolate,
                RuntimeAuthoritativePositionDisposition.NoPositionOperation,
                RuntimeAuthoritativePositionDisposition.RejectedData,
            })
        {
            RuntimeAuthoritativePositionRoute route = MakeRoute(
                record, disposition, DestinationCell, stopInterpolating: true);
            Assert.Throws<ArgumentException>(
                () => drive.ApplyAcceptedRemoteFarSnap(record, remote, route));
        }
    }


    [Fact]
    public void Teleport_Committed_PlacesFromCanonicalDestination()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003040u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination);

        RuntimeRemotePlacementExecutionStatus status =
            drive.ApplyAcceptedRemoteTeleport(record, remote, route);

        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Committed, status);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        DrainPlacementFifo(lifetime);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Teleport_RefusedByServiceWindow_StillStoresTheDestinationPose()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        // The world frame must still be published (store_position resolves
        // through it); the service window is what refuses — mirrors
        // FarSnap_ClearsTheInterpolationQueue_IndependentlyOfThePlacementOutcome's
        // setup, which is the far arm's own Refused-fallback test.
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003041u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        // Deliberately does not Allow(DestinationLandblock) — the service
        // window refuses.
        var window = new FakeServiceWindow();
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            drive.ApplyAcceptedRemoteTeleport(record, remote, route));

        Assert.NotEqual(positionBefore, body.Position);
        Assert.True(body.InWorld);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Teleport_EngineRefusedTheDestination_LeavesTheBodyWhereItWas()
    {
        PhysicsEngine engine = FlatEngine();
        using var lifetime = new RuntimeEntityObjectLifetime(engine);
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003042u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        engine.TransitionCellCollisionTestHook =
            static (_, _, _, _) => TransitionState.Collided;

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.RejectedByPlacement,
            drive.ApplyAcceptedRemoteTeleport(record, remote, route));

        Assert.Equal(positionBefore, body.Position);
        Assert.NotEqual(destination + new Vector3(192f, 0f, 0f), body.Position);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Teleport_DoesNotClearTheInterpolationQueueItself()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003043u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        remote.Interp.Enqueue(
            new Vector3(40f, 40f, SpawnHeight),
            Quaternion.Identity,
            isMovingTo: false,
            currentBodyPosition: body.Position,
            currentBodyOrientation: body.Orientation);
        Assert.True(remote.Interp.IsActive);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            new Vector3(12f, 14f, SpawnHeight));
        Assert.False(route.StopInterpolating);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            drive.ApplyAcceptedRemoteTeleport(record, remote, route));

        Assert.True(remote.Interp.IsActive);
        DrainPlacementFifo(lifetime);
    }

    [Fact]
    public void Teleport_ThrowsForARouteThisArmDoesNotOwn()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003044u);
        AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        RuntimeRemotePlacementDriveController drive =
            CreateDrive(lifetime, new FakeServiceWindow());

        foreach (RuntimeAuthoritativePositionDisposition disposition in
            new[]
            {
                RuntimeAuthoritativePositionDisposition.SetPositionSimple,
                RuntimeAuthoritativePositionDisposition.Interpolate,
                RuntimeAuthoritativePositionDisposition.NoPositionOperation,
                RuntimeAuthoritativePositionDisposition.RejectedData,
            })
        {
            RuntimeAuthoritativePositionRoute route = MakeRoute(
                record, disposition, DestinationCell);
            Assert.Throws<ArgumentException>(
                () => drive.ApplyAcceptedRemoteTeleport(record, remote, route));
        }
    }

    [Fact]
    public void Teleport_SupersededIncarnation_DoesNotStoreThroughTheStaleRecord()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70003045u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        RuntimeRemotePlacementDriveController drive =
            CreateDrive(lifetime, new FakeServiceWindow());

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination);

        Assert.True(lifetime.Entities.RemoveActive(record));
        Assert.False(lifetime.Entities.IsCurrent(record));
        Assert.NotNull(record.PhysicsBody);
        Assert.NotNull(record.Key);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            drive.ApplyAcceptedRemoteTeleport(record, remote, route));

        Assert.Equal(positionBefore, body.Position);
        Assert.NotEqual(destination + new Vector3(192f, 0f, 0f), body.Position);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Teleport_LedgerConverges_AfterDetachRouteClearsARetainedRetry()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        RuntimeEntityRecord record = CreateRemoteRecord(
            lifetime, 0x70003046u, setupTableId: 0x02000001u);
        PhysicsBody body = AttachBody(lifetime, record, SourceCell);
        RemoteMotion remote = lifetime.Physics.GetOrCreateRemoteMotion(record);
        Vector3 positionBefore = body.Position;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);
        var route = new object();
        drive.AttachRoute(route);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute teleportRoute = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.ApplyAcceptedRemoteTeleport(record, remote, teleportRoute));
        Assert.Equal(1, drive.PendingCount);
        Assert.Equal(
            1, lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);
        Assert.Equal(
            destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(positionBefore, body.Position);

        drive.DetachRoute(route);

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            0, lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);
        AssertConverged(lifetime);
    }

    // ── C4 route 5: projectile arm (D-P2/D-P3/D-P4/D-P5) ───────────────────

    [Fact]
    public void OwnsPlacement_TrueForProjectileAuthoritative_SetPositionAndSetPositionSimple()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70004001u);

        foreach (RuntimeAuthoritativePositionDisposition disposition in
            new[]
            {
                RuntimeAuthoritativePositionDisposition.SetPosition,
                RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            })
        {
            RuntimeAuthoritativePositionRoute route = MakeRoute(
                record,
                disposition,
                DestinationCell,
                operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);
            Assert.True(RuntimeRemotePlacementDriveController.OwnsPlacement(route));
        }

        RuntimeAuthoritativePositionRoute createRoute = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative,
            setPositionFlags: PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Slide);
        Assert.False(RuntimeRemotePlacementDriveController.OwnsPlacement(createRoute));
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_Null_WhenOperationKindIsNotProjectileAuthoritative()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        (RuntimeEntityRecord record, _) = CreateProjectileRecord(
            lifetime, 0x70004002u, SourceCell);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            operationKind: RuntimeSetPositionOperationKind.RemoteAuthoritative);

        Assert.Null(drive.ApplyAcceptedProjectilePosition(record, route));
        Assert.Equal(0, drive.PendingCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_Null_WhenNoProjectileComponentIsBound()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        RuntimeEntityRecord record = CreateRemoteRecord(lifetime, 0x70004003u);
        AttachBody(lifetime, record, SourceCell);
        // Deliberately never BindProjectile — record.Projectile stays null.
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

        Assert.Null(drive.ApplyAcceptedProjectilePosition(record, route));
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_TeleportCommit_MovesBodyForceEndsCollisionNoVelocityNoConstraint()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        (RuntimeEntityRecord record, RuntimeProjectile projectile) =
            CreateProjectileRecord(lifetime, 0x70004004u, SourceCell);
        PhysicsBody body = record.PhysicsBody!;
        var inFlightVelocity = new Vector3(5f, 0f, -2f);
        body.set_velocity(inFlightVelocity);
        ulong predictionBefore = projectile.PredictionAuthorityVersion;
        SeedCollisionOwner(lifetime, record, 0x70004104u, SourceCell);
        Assert.Equal(
            1, lifetime.Physics.CollisionReports.CaptureOwnership().OwnerCount);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight + 10f);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

        RuntimeRemotePlacementExecutionStatus? status =
            drive.ApplyAcceptedProjectilePosition(record, route);

        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Committed, status);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(predictionBefore, projectile.PredictionAuthorityVersion);
        Assert.Equal(inFlightVelocity, body.Velocity);
        Assert.Null(record.RemoteMotion);
        Assert.Equal(
            0, lifetime.Physics.CollisionReports.CaptureOwnership().OwnerCount);
        // A4 fix (review round): the shadow-sync half of
        // SyncProjectilePresentation, asserted directly rather than left
        // vacuous — the shadow row moves to the RESOLVED body position.
        Assert.True(body.InWorld);
        ShadowEntry shadowEntry = Assert.Single(
            lifetime.Physics.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == record.Key!.Value.LocalEntityId);
        Assert.Equal(body.Position, shadowEntry.Position);
        DrainPlacementFifo(lifetime);
        AssertConverged(lifetime);
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_FarCommit_MovesBodyNoVelocityNoConstraint()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        (RuntimeEntityRecord record, RuntimeProjectile projectile) =
            CreateProjectileRecord(lifetime, 0x70004005u, SourceCell);
        PhysicsBody body = record.PhysicsBody!;
        var inFlightVelocity = new Vector3(0f, 7f, 1f);
        body.set_velocity(inFlightVelocity);
        ulong predictionBefore = projectile.PredictionAuthorityVersion;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight + 10f);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

        RuntimeRemotePlacementExecutionStatus? status =
            drive.ApplyAcceptedProjectilePosition(record, route);

        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Committed, status);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(predictionBefore, projectile.PredictionAuthorityVersion);
        Assert.Equal(inFlightVelocity, body.Velocity);
        Assert.Null(record.RemoteMotion);
        Assert.True(body.InWorld);
        ShadowEntry shadowEntry = Assert.Single(
            lifetime.Physics.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == record.Key!.Value.LocalEntityId);
        Assert.Equal(body.Position, shadowEntry.Position);
        DrainPlacementFifo(lifetime);
        AssertConverged(lifetime);
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_TeleportCommit_HiddenSuspendsShadowStaysInWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        (RuntimeEntityRecord record, _) =
            CreateProjectileRecord(lifetime, 0x7000400Bu, SourceCell);
        Assert.Equal(1, lifetime.Physics.Engine.ShadowObjects.TotalRegistered);
        lifetime.Entities.SetFinalPhysicsState(
            record, record.FinalPhysicsState | PhysicsStateFlags.Hidden);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight + 10f);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            drive.ApplyAcceptedProjectilePosition(record, route));

        Assert.True(record.PhysicsBody!.InWorld);
        Assert.Equal(0, lifetime.Physics.Engine.ShadowObjects.TotalRegistered);
        DrainPlacementFifo(lifetime);
        AssertConverged(lifetime);
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_Refused_NonSpatialDeactivatesAndSuspends()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        lifetime.Physics.ObserveLocalWorldFrame(SourceCell, teleportAdvanced: false);
        (RuntimeEntityRecord record, _) =
            CreateProjectileRecord(lifetime, 0x7000400Cu, SourceCell);
        Assert.Equal(1, lifetime.Physics.Engine.ShadowObjects.TotalRegistered);
        // Withdraw spatial-root status — AcknowledgeSpatialProjection
        // (spatial: false) is a no-op (only its `true` branch touches
        // _spatialRoots); RemoveSpatialProjection is the actual withdrawal.
        lifetime.Physics.RemoveSpatialProjection(record);
        var window = new FakeServiceWindow();
        // Deliberately NOT allowed — the pre-flight refuses, so the store
        // fallback runs without ever touching spatial registration.
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight + 10f);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Refused,
            drive.ApplyAcceptedProjectilePosition(record, route));

        Assert.False(record.PhysicsBody!.InWorld);
        Assert.Equal(
            TransientStateFlags.None,
            record.PhysicsBody.TransientState & TransientStateFlags.Active);
        Assert.Equal(0, lifetime.Physics.Engine.ShadowObjects.TotalRegistered);
        DrainPlacementFifo(lifetime);
        AssertConverged(lifetime);
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_TeleportCommit_ReenteringWorldReactivatesBody()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        (RuntimeEntityRecord record, _) =
            CreateProjectileRecord(lifetime, 0x7000400Du, SourceCell);
        PhysicsBody body = record.PhysicsBody!;
        body.InWorld = false;
        body.TransientState &= ~TransientStateFlags.Active;
        body.LastUpdateTime = -1d;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight + 10f);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Committed,
            drive.ApplyAcceptedProjectilePosition(record, route));

        Assert.True(body.InWorld);
        Assert.Equal(
            TransientStateFlags.Active,
            body.TransientState & TransientStateFlags.Active);
        Assert.NotEqual(-1d, body.LastUpdateTime);
        DrainPlacementFifo(lifetime);
        AssertConverged(lifetime);
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_Refused_StillAdvancesPoseNoParkPredictionInvalidated()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        lifetime.Physics.ObserveLocalWorldFrame(SourceCell, teleportAdvanced: false);
        (RuntimeEntityRecord record, RuntimeProjectile projectile) =
            CreateProjectileRecord(lifetime, 0x70004006u, SourceCell);
        PhysicsBody body = record.PhysicsBody!;
        ulong predictionBefore = projectile.PredictionAuthorityVersion;
        var window = new FakeServiceWindow();
        // Deliberately NOT allowed — the pre-flight refuses.
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

        RuntimeRemotePlacementExecutionStatus? status =
            drive.ApplyAcceptedProjectilePosition(record, route);

        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Refused, status);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(predictionBefore, projectile.PredictionAuthorityVersion);
        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.Equal(0, drive.PendingCount);
        ShadowEntry shadowEntry = Assert.Single(
            lifetime.Physics.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == record.Key!.Value.LocalEntityId);
        Assert.Equal(body.Position, shadowEntry.Position);
        AssertConverged(lifetime);
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_PinnedNoOps_BodyAndPredictionUnchanged()
    {
        foreach (RuntimeAuthoritativePositionDisposition disposition in
            new[]
            {
                RuntimeAuthoritativePositionDisposition.Interpolate,
                RuntimeAuthoritativePositionDisposition.NoPositionOperation,
            })
        {
            using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
            (RuntimeEntityRecord record, RuntimeProjectile projectile) =
                CreateProjectileRecord(lifetime, 0x70004007u, SourceCell);
            PhysicsBody body = record.PhysicsBody!;
            Vector3 positionBefore = body.Position;
            Quaternion orientationBefore = body.Orientation;
            ulong predictionBefore = projectile.PredictionAuthorityVersion;
            var window = new FakeServiceWindow();
            window.Allow(DestinationLandblock);
            RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

            RuntimeAuthoritativePositionRoute route = MakeRoute(
                record,
                disposition,
                DestinationCell,
                operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

            Assert.Null(drive.ApplyAcceptedProjectilePosition(record, route));

            Assert.Equal(positionBefore, body.Position);
            Assert.Equal(orientationBefore, body.Orientation);
            Assert.Equal(predictionBefore, projectile.PredictionAuthorityVersion);
            Assert.Null(record.RemoteMotion);
            Assert.Equal(0, drive.PendingCount);
            AssertConverged(lifetime);
        }
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_RejectedClassification_Swallowed()
    {
        foreach (RuntimeAuthoritativePositionDisposition disposition in
            new[]
            {
                RuntimeAuthoritativePositionDisposition.RejectedAuthority,
                RuntimeAuthoritativePositionDisposition.RejectedData,
            })
        {
            using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
            (RuntimeEntityRecord record, RuntimeProjectile projectile) =
                CreateProjectileRecord(lifetime, 0x70004008u, SourceCell);
            PhysicsBody body = record.PhysicsBody!;
            Vector3 positionBefore = body.Position;
            ulong predictionBefore = projectile.PredictionAuthorityVersion;
            var window = new FakeServiceWindow();
            window.Allow(DestinationLandblock);
            RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

            RuntimeAuthoritativePositionRoute route = MakeRoute(
                record,
                disposition,
                DestinationCell,
                operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

            Assert.Null(drive.ApplyAcceptedProjectilePosition(record, route));
            Assert.Equal(positionBefore, body.Position);
            Assert.Equal(predictionBefore, projectile.PredictionAuthorityVersion);
            Assert.Equal(0, drive.PendingCount);
            AssertConverged(lifetime);
        }
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_LedgerConverges_AfterDetachRouteClearsARetainedRetry()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        (RuntimeEntityRecord record, _) = CreateProjectileRecord(
            lifetime, 0x70004009u, SourceCell);
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);
        var routeOwner = new object();
        drive.AttachRoute(routeOwner);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);

        RuntimeRemotePlacementExecutionStatus? status =
            drive.ApplyAcceptedProjectilePosition(record, route);

        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Committed, status);
        Assert.Equal(
            1, lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);

        drive.DetachRoute(routeOwner);

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            0, lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Advance_ProjectileRetryReParksAsContention_PredictionNotInvalidatedASecondTime()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        lifetime.Physics.ObserveLocalWorldFrame(SourceCell, teleportAdvanced: false);
        (RuntimeEntityRecord record, RuntimeProjectile projectile) =
            CreateProjectileRecord(
                lifetime,
                0x7000400Bu,
                SourceCell,
                setupTableId: 0x02000001u);
        PhysicsBody body = record.PhysicsBody!;
        Vector3 positionBefore = body.Position;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative,
            stopInterpolating: true);

        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.ApplyAcceptedProjectilePosition(record, route));
        Assert.Equal(1, drive.PendingCount);
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(positionBefore, body.Position);
        ulong predictionAfterEntry = projectile.PredictionAuthorityVersion;

        drive.Advance();

        Assert.Equal(1, drive.PendingCount);
        Assert.Equal(
            predictionAfterEntry, projectile.PredictionAuthorityVersion);
        // The re-park wrote nothing — the stored pose from the entry point
        // is untouched.
        Assert.Equal(destination + new Vector3(192f, 0f, 0f), body.Position);

        RuntimePlacementCancellationReceipt cancellation =
            lifetime.Physics.SetPosition.Forget(record);
        if (cancellation.IsValid)
            lifetime.Physics.SetPosition.PublishCancellation(cancellation);
        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void Advance_ProjectileRetryDestinationLeavesTheWindow_StoresNewestPoseInvalidatesPredictionSyncsShadow()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        lifetime.Physics.ObserveLocalWorldFrame(SourceCell, teleportAdvanced: false);
        (RuntimeEntityRecord record, RuntimeProjectile projectile) =
            CreateProjectileRecord(
                lifetime,
                0x7000400Cu,
                SourceCell,
                setupTableId: 0x02000001u);
        PhysicsBody body = record.PhysicsBody!;
        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);

        var firstDestination = new Vector3(12f, 14f, SpawnHeight);
        Assert.Equal(
            RuntimeRemotePlacementExecutionStatus.Contention,
            drive.ApplyAcceptedProjectilePosition(
                record,
                MakeRoute(
                    record,
                    RuntimeAuthoritativePositionDisposition.SetPositionSimple,
                    DestinationCell,
                    firstDestination,
                    operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative,
                    stopInterpolating: true)));
        Assert.Equal(1, drive.PendingCount);
        ulong predictionAfterEntry = projectile.PredictionAuthorityVersion;

        var newestDestination = new Vector3(40f, 50f, SpawnHeight);
        record.Snapshot = record.Snapshot with
        {
            Position = new CreateObject.ServerPosition(
                DestinationCell,
                newestDestination.X,
                newestDestination.Y,
                newestDestination.Z,
                1f,
                0f,
                0f,
                0f),
        };
        window.Forbid(DestinationLandblock);

        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            newestDestination + new Vector3(192f, 0f, 0f), body.Position);
        Assert.NotEqual(
            predictionAfterEntry, projectile.PredictionAuthorityVersion);
        // SyncProjectilePresentation ran on the retry arm too — the shadow
        // row followed the body to the newest stored pose.
        ShadowEntry shadowEntry = Assert.Single(
            lifetime.Physics.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == record.Key!.Value.LocalEntityId);
        Assert.Equal(body.Position, shadowEntry.Position);
        Assert.Equal(
            0, lifetime.Physics.CaptureOwnership().SetPositionOperationCount);
        AssertConverged(lifetime);
    }

    [Fact]
    public void ApplyAcceptedProjectilePosition_DuringOpenQuantum_CompleteAbortsAfterPredictionInvalidated()
    {
        using var lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        CommitLandblockCollision(lifetime, DestinationLandblock);
        (RuntimeEntityRecord record, RuntimeProjectile projectile) =
            CreateProjectileRecord(lifetime, 0x7000400Au, SourceCell);
        var updater = new RuntimeProjectilePhysicsUpdater(lifetime.Physics);
        Assert.True(updater.TryBegin(
            record,
            quantum: 0.05f,
            record.ObjectClockEpoch,
            externalOwnerValid: null,
            out RuntimeProjectilePhysicsCommit commit));

        var window = new FakeServiceWindow();
        window.Allow(DestinationLandblock);
        RuntimeRemotePlacementDriveController drive = CreateDrive(lifetime, window);
        var destination = new Vector3(12f, 14f, SpawnHeight);
        RuntimeAuthoritativePositionRoute route = MakeRoute(
            record,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            DestinationCell,
            destination,
            operationKind: RuntimeSetPositionOperationKind.ProjectileAuthoritative);
        RuntimeRemotePlacementExecutionStatus? status =
            drive.ApplyAcceptedProjectilePosition(record, route);
        Assert.Equal(RuntimeRemotePlacementExecutionStatus.Committed, status);
        Vector3 committedPosition = record.PhysicsBody!.Position;

        bool completed = updater.Complete(
            commit,
            liveCenterX: 0,
            liveCenterY: 0,
            acknowledgeProjection: static _ => true);

        Assert.False(completed);
        Assert.Equal(committedPosition, record.PhysicsBody.Position);
        DrainPlacementFifo(lifetime);
        AssertConverged(lifetime);
    }

    private static (RuntimeEntityRecord Record, RuntimeProjectile Projectile) CreateProjectileRecord(
        RuntimeEntityObjectLifetime lifetime,
        uint guid,
        uint cellId,
        bool registerShadow = true,
        uint? setupTableId = null)
    {
        RuntimeEntityRecord record = CreateRemoteRecord(
            lifetime, guid, setupTableId);
        lifetime.Entities.SetFinalPhysicsState(
            record,
            PhysicsStateFlags.Gravity
                | PhysicsStateFlags.Missile
                | PhysicsStateFlags.ReportCollisions);
        PhysicsBody body = AttachBody(lifetime, record, cellId);
        var sphere = new ProjectileCollisionSphere(Vector3.Zero, 0.1f, 1f);
        var projectile = (RuntimeProjectile)lifetime.Physics.BindProjectile(
            record, body, sphere);
        if (registerShadow)
        {
            lifetime.Physics.Engine.ShadowObjects.Register(
                record.Key!.Value.LocalEntityId,
                gfxObjId: 0u,
                body.Position,
                body.Orientation,
                radius: 0.1f,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                cellId & 0xFFFF0000u,
                ShadowCollisionType.Sphere,
                state: (uint)record.FinalPhysicsState,
                seedCellId: cellId,
                isStatic: false);
        }
        return (record, projectile);
    }

    private static void SeedCollisionOwner(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord owner,
        uint peerGuid,
        uint cellId)
    {
        RuntimeEntityRecord peer = CreateRemoteRecord(lifetime, peerGuid);
        AttachBody(lifetime, peer, cellId);
        uint peerLocalId = peer.Key!.Value.LocalEntityId;
        lifetime.Physics.Engine.ShadowObjects.Register(
            peerLocalId,
            gfxObjId: 0u,
            peer.PhysicsBody!.Position,
            Quaternion.Identity,
            radius: 0.4f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            cellId & 0xFFFF0000u,
            ShadowCollisionType.Sphere,
            state: (uint)peer.FinalPhysicsState,
            seedCellId: cellId,
            isStatic: false);
        var report = new PhysicsSetPositionCollisionReport(
            ContactPlaneValid: false,
            ContactPlane: default,
            ContactPlaneCellId: 0u,
            ContactPlaneIsWater: false,
            LastKnownContactPlaneValid: false,
            LastKnownContactPlane: default,
            LastKnownContactPlaneCellId: 0u,
            LastKnownContactPlaneIsWater: false,
            SlidingNormalValid: false,
            SlidingNormal: default,
            CollisionNormalValid: false,
            CollisionNormal: default,
            CollidedWithEnvironment: false,
            FramesStationaryFall: 0,
            AdjustOffset: default,
            LastCollidedObjectId: peerLocalId,
            CollidedObjectIds: System.Collections.Immutable.ImmutableArray
                .Create(peerLocalId));
        Assert.True(lifetime.Physics.HandleSetPositionCollisions(
            owner,
            owner.PositionAuthorityVersion,
            owner.SpatialAuthorityVersion,
            owner.VelocityAuthorityVersion,
            physicsTime: 1d,
            previousContact: false,
            previousOnWalkable: false,
            report));
    }

    // ── Fixture ──────────────────────────────────────────────────────────

    private static void DrainPlacementFifo(RuntimeEntityObjectLifetime lifetime)
    {
        while (lifetime.Physics.SetPosition.TryPeekProjection(
                out RuntimePlacementProjectionSnapshot head))
        {
            if (!lifetime.Physics.SetPosition.AcknowledgeProjection(head.Token))
                break;
        }
    }

    private static void AssertConverged(RuntimeEntityObjectLifetime lifetime)
    {
        Assert.Equal(
            0,
            lifetime.CaptureOwnership().RemotePlacementDrivePendingCount);
    }

    private static RuntimeRemotePlacementDriveController CreateDrive(
        RuntimeEntityObjectLifetime lifetime,
        IRuntimeRemotePlacementServiceWindow window) =>
        new(
            lifetime,
            new GameRuntimeClock(),
            new UnusedCollisionSource(),
            window);

    private static RuntimeAuthoritativePositionRoute MakeRoute(
        RuntimeEntityRecord record,
        RuntimeAuthoritativePositionDisposition disposition,
        uint destinationCellId,
        Vector3? destinationPosition = null,
        RuntimeSetPositionOperationKind operationKind =
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
        PhysicsSetPositionFlags? setPositionFlags = null,
        bool stopInterpolating = false)
    {
        Vector3 position = destinationPosition ?? new Vector3(10f, 10f, SpawnHeight);
        record.Snapshot = record.Snapshot with
        {
            Position = new CreateObject.ServerPosition(
                destinationCellId,
                position.X,
                position.Y,
                position.Z,
                1f,
                0f,
                0f,
                0f),
        };

        var authority = new RuntimeAuthoritativePositionAuthority(
            new RuntimeGenerationToken(1),
            record.Key!.Value,
            record.PositionAuthorityVersion,
            AcceptedPositionSequence: 2,
            PreviousTeleportSequence: 0,
            AcceptedTeleportSequence: 0,
            PositionTimestampDisposition.Apply);

        bool performsSetPosition = disposition is
            RuntimeAuthoritativePositionDisposition.SetPosition
            or RuntimeAuthoritativePositionDisposition.SetPositionSimple;

        return new RuntimeAuthoritativePositionRoute(
            authority,
            disposition,
            operationKind,
            setPositionFlags ?? (performsSetPosition
                ? PhysicsSetPositionFlags.Teleport
                    | PhysicsSetPositionFlags.Slide
                : PhysicsSetPositionFlags.None),
            PlacementFrame: 0u,
            UnparentBeforeRouting: true,
            ApplyPlacementFrameBeforeRouting: false,
            LeaveWorld: false,
            TeleportHookPhase: RuntimeTeleportHookPhase.None,
            stopInterpolating,
            ConstrainPhase: RuntimePositionConstrainPhase.AfterPositionOperation,
            PreserveHeading: false,
            ZeroVelocity: false,
            SendPositionImmediately: false,
            CollisionBatchEligible: true);
    }

    private static RuntimeEntityRecord CreateRemoteRecord(
        RuntimeEntityObjectLifetime lifetime,
        uint guid,
        uint? setupTableId = null,
        ushort instanceSequence = 0)
    {
        RuntimeEntityRecord record = lifetime.RegisterEntity(
            Spawn(guid, setupTableId, instanceSequence)).Canonical!;
        lifetime.Entities.SetFinalPhysicsState(record, PhysicsStateFlags.Gravity);
        return record;
    }

    private static PhysicsBody AttachBody(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record,
        uint cellId)
    {
        lifetime.Entities.SetFullCell(
            record, cellId, (cellId & 0xFFFF0000u) | 0xFFFFu);
        var body = new PhysicsBody
        {
            Position = new Vector3(10f, 10f, SpawnHeight),
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(cellId, body.Position, body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
        return body;
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        uint? setupTableId,
        ushort instanceSequence = 0) =>
        new(
            guid,
            new CreateObject.ServerPosition(
                SourceCell, 10f, 10f, SpawnHeight, 1f, 0f, 0f, 0f),
            setupTableId,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "remote",
            ItemType: null,
            MotionState: null,
            MotionTableId: 0x09000001u,
            InstanceSequence: instanceSequence);

    private static PhysicsEngine FlatEngine()
    {
        var engine = new PhysicsEngine
        {
            DataCache = new PhysicsDataCache(),
        };
        engine.AddLandblock(
            SourceLandblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }

    private static void CommitLandblockCollision(
        RuntimeEntityObjectLifetime lifetime,
        uint landblockId,
        float worldOffsetX = 192f)
    {
        var heights = new byte[81];
        Array.Fill(heights, (byte)SpawnHeight);
        var heightTable = new float[256];
        for (int index = 0; index < heightTable.Length; index++)
            heightTable[index] = index;
        lifetime.Physics.ObserveLocalWorldFrame(
            SourceCell, teleportAdvanced: false);
        lifetime.Physics.SetPosition.BeginCollisionGeneration(landblockId, 1UL);
        lifetime.Physics.Engine.AddLandblock(
            landblockId,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX,
            worldOffsetY: 0f);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            landblockId, 1UL, ready: true);
    }

    private sealed class FakeServiceWindow : IRuntimeRemotePlacementServiceWindow
    {
        private readonly HashSet<uint> _within = [];

        internal void Allow(uint landblockId) =>
            _within.Add(Canonical(landblockId));

        internal void Forbid(uint landblockId) =>
            _within.Remove(Canonical(landblockId));

        public bool IsWithinServiceWindow(uint landblockId) =>
            _within.Contains(Canonical(landblockId));

        private static uint Canonical(uint landblockId) =>
            (landblockId & 0xFFFF0000u) | 0xFFFFu;
    }

    private sealed class UnusedCollisionSource : IPreparedCollisionSource
    {
        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatSetupCollision>.Missing;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset> ReadCellStructureCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }
}
