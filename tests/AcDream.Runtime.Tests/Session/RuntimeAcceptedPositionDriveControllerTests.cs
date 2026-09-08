using System.Net;
using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests.Session;

public sealed class RuntimeAcceptedPositionDriveControllerTests
{
    private const uint PlayerGuid = 0x50000001u;
    private const uint SpawnLandblock = 0x01010000u;
    private const float SpawnHeight = 5f;

    private const uint DestinationLandblock = 0x02020000u;

    private const uint NeighbourLandblock = 0x02010000u;

    private const uint SpawnSeamCell = SpawnLandblock | 57u;

    [Fact]
    public void NotApplicable_WhenDispositionIsNotForcePosition()
    {
        using StartedRuntime started = StartRuntime();
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(started.Runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(started.Runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedLocalPosition(
                record,
                ForceUpdate(new Vector3(30f, 30f, 5f)),
                PositionTimestampDisposition.Apply,
                Timestamps(teleport: 0),
                previousTeleportSequence: 0);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.NotApplicable, status);
        Assert.Empty(gameActions);
    }

    [Fact]
    public void NotApplicable_WhenRecordIsNotTheLocalPlayer()
    {
        using StartedRuntime started = StartRuntime();
        (RuntimeEntityRecord record, _) = EnterLocalPlayer(started.Runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(started.Runtime, out _);

        // Same record, but the drive's own local-player-guid accessor no
        // longer matches it (as if it belonged to some other entity).
        started.Runtime.PlayerIdentity.ServerGuid = 0x70000099u;

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedLocalPosition(
                record,
                ForceUpdate(new Vector3(30f, 30f, 5f)),
                PositionTimestampDisposition.ForcePosition,
                Timestamps(teleport: 0),
                previousTeleportSequence: 0);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.NotApplicable, status);
    }

    [Fact]
    public void NotApplicable_WhileAnInitialCreateResidenceIsStillActive()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        runtime.PlayerIdentity.ServerGuid = PlayerGuid;
        CommitLandblockCollision(runtime, SpawnLandblock);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                Spawn(PlayerGuid), isLocalPlayer: true)
            .Canonical!;
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedLocalPosition(
                record,
                ForceUpdate(new Vector3(30f, 30f, 5f)),
                PositionTimestampDisposition.ForcePosition,
                Timestamps(teleport: 0),
                previousTeleportSequence: 0);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.NotApplicable, status);
        Assert.Empty(gameActions);
    }

    [Fact]
    public void Committed_WhenForceAuthorityCarriesANewerTeleportStamp()
    {
        using StartedRuntime started = StartRuntime();
        (RuntimeEntityRecord record, _) =
            EnterLocalPlayer(started.Runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(started.Runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedLocalPosition(
                record,
                ForceUpdate(new Vector3(30f, 30f, 5f)),
                PositionTimestampDisposition.ForcePosition,
                Timestamps(teleport: 6),
                previousTeleportSequence: 5);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.Committed, status);
        Assert.Single(gameActions);
        AssertConverged(started.Runtime);
    }

    [Fact]
    public void Committed_MovesTheBodyPreservesHeadingAndAcksExactlyOnceAfterCommit()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        Quaternion headingBeforeCorrection = controller.BodyOrientation;

        var corrected = new Vector3(30f, 32f, 5f);
        WorldSession.EntityPositionUpdate wire = ForceUpdate(corrected);
        wire = wire with
        {
            Position = wire.Position with
            {
                RotationX = 0f,
                RotationY = 0f,
                RotationZ = 1f,
                RotationW = 0f,
            },
        };
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, wire);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedLocalPosition(
                record,
                wire,
                disposition,
                timestamps,
                timestamps.PreviousTeleport);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.Committed, status);
        Assert.Equal(corrected, controller.Position);
        Assert.Equal(headingBeforeCorrection, controller.BodyOrientation);
        Assert.Single(gameActions);
        AssertConverged(runtime);
    }

    [Fact]
    public void Contention_WhenTheEntityAlreadyOwnsAnActiveOperation()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        Vector3 positionBefore = controller.Position;
        WorldSession.EntityPositionUpdate wire =
            ForceUpdate(new Vector3(30f, 32f, 5f));
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, wire);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        RuntimeEntityPlacementToken displaced = runtime.EntityObjects.Physics
            .SetPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative);
        Assert.True(displaced.IsValid);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedLocalPosition(
                record,
                wire,
                disposition,
                timestamps,
                timestamps.PreviousTeleport);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.Contention, status);
        Assert.Equal(positionBefore, controller.Position);
        Assert.Empty(gameActions);

        RuntimePlacementCancellationReceipt cancellation = runtime.EntityObjects
            .Physics.SetPosition.ForgetExactPlacement(displaced);
        if (cancellation.IsValid)
        {
            runtime.EntityObjects.Physics.SetPosition
                .PublishCancellation(cancellation);
        }
    }

    [Fact]
    public void ContendedForcePosition_WritesNoResidencyAnywhere()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        uint committedCellBefore = record.FullCellId;
        Assert.NotEqual(0u, committedCellBefore);

        const uint wireCell = SpawnLandblock | 0x0002u;
        Assert.NotEqual(wireCell, committedCellBefore);
        WorldSession.EntityPositionUpdate wire = ForceUpdate(
            new Vector3(30f, 32f, SpawnHeight),
            landblockId: wireCell);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, wire);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        // (1) The merge itself wrote no residency.
        Assert.Equal(committedCellBefore, record.FullCellId);

        RuntimeEntityPlacementToken displaced = runtime.EntityObjects.Physics
            .SetPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative);
        Assert.True(displaced.IsValid);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.Contention,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                wire,
                disposition,
                timestamps,
                timestamps.PreviousTeleport));

        Assert.Equal(committedCellBefore, record.FullCellId);
        Assert.Empty(gameActions);

        // (3) And nothing rebounds it on the settle.
        drive.Advance();
        Assert.Equal(committedCellBefore, record.FullCellId);

        RuntimePlacementCancellationReceipt cancellation = runtime.EntityObjects
            .Physics.SetPosition.ForgetExactPlacement(displaced);
        if (cancellation.IsValid)
        {
            runtime.EntityObjects.Physics.SetPosition
                .PublishCancellation(cancellation);
        }
    }

    [Fact]
    public void DeferredCell_ParksThenCommitsAndNeverDoubleAcksAfterTheCollisionGenerationWakes()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        const uint deferredLandblock = 0x02020000u;
        var deferredPosition = new Vector3(10f, 10f, SpawnHeight);
        WorldSession.EntityPositionUpdate parkedUpdate = ForceUpdate(
            deferredPosition,
            landblockId: deferredLandblock | 0x0001u);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, parkedUpdate);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        RuntimeAcceptedPositionExecutionStatus parked =
            drive.TryExecuteAcceptedLocalPosition(
                record,
                parkedUpdate,
                disposition,
                timestamps,
                timestamps.PreviousTeleport);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.DeferredCell, parked);
        Assert.Empty(gameActions);
        Assert.Equal(1, drive.PendingCount);

        // Advance() before the destination is ready makes no progress.
        drive.Advance();
        Assert.Empty(gameActions);
        Assert.Equal(1, drive.PendingCount);

        CommitLandblockCollision(runtime, deferredLandblock);
        DrainPlacementFifo(runtime);

        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(deferredPosition + new Vector3(192f, 192f, 0f), controller.Position);
        int acksAfterFirstResolve = gameActions.Count;

        drive.Advance();
        drive.Advance();
        Assert.Equal(acksAfterFirstResolve, gameActions.Count);
        AssertConverged(runtime);
    }

    [Fact]
    public void Equal_ClearsPendingWithoutReissuingWhenNoNewerAcceptedAuthorityArrived()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        const uint deferredLandblock = 0x02020000u;
        var deferredPosition = new Vector3(10f, 10f, SpawnHeight);
        WorldSession.EntityPositionUpdate parkedUpdate = ForceUpdate(
            deferredPosition,
            landblockId: deferredLandblock | 0x0001u);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, parkedUpdate);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        RuntimeAcceptedPositionExecutionStatus parked =
            drive.TryExecuteAcceptedLocalPosition(
                record,
                parkedUpdate,
                disposition,
                timestamps,
                timestamps.PreviousTeleport);
        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.DeferredCell, parked);
        Assert.Equal(1, drive.PendingCount);
        ulong authorityAtPark = record.PositionAuthorityVersion;
        Vector3 positionAtPark = controller.Position;

        RuntimePlacementCancellationReceipt cancellation =
            runtime.EntityObjects.Physics.SetPosition.Forget(record);
        if (cancellation.IsValid)
        {
            runtime.EntityObjects.Physics.SetPosition
                .PublishCancellation(cancellation);
        }

        Assert.Equal(authorityAtPark, record.PositionAuthorityVersion);

        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(positionAtPark, controller.Position);
        Assert.Single(gameActions);

        drive.Advance();
        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        Assert.Single(gameActions);
        Assert.Equal(positionAtPark, controller.Position);
        AssertConverged(runtime);
    }

    [Fact]
    public void Advanced_ReissuesWhenTheNewestAcceptedEventIsStillAForcePosition()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        const uint deferredLandblock = 0x02020000u;
        var firstCorrection = new Vector3(10f, 10f, SpawnHeight);
        WorldSession.EntityPositionUpdate parkedUpdate = ForceUpdate(
            firstCorrection,
            landblockId: deferredLandblock | 0x0001u);
        (PositionTimestampDisposition parkedDisposition,
            AcceptedPhysicsTimestamps parkedTimestamps) =
            MergeAccepted(runtime, controller, parkedUpdate);
        Assert.Equal(
            PositionTimestampDisposition.ForcePosition, parkedDisposition);
        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.DeferredCell,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                parkedUpdate,
                parkedDisposition,
                parkedTimestamps,
                parkedTimestamps.PreviousTeleport));

        CommitLandblockCollision(runtime, deferredLandblock);
        DrainPlacementFifo(runtime);

        var secondCorrection = new Vector3(14f, 12f, SpawnHeight);
        WorldSession.EntityPositionUpdate secondUpdate = ForceUpdate(
            secondCorrection,
            landblockId: deferredLandblock | 0x0001u,
            positionSequence: 3,
            forcePositionSequence: 2);
        (PositionTimestampDisposition secondDisposition,
            AcceptedPhysicsTimestamps secondTimestamps) =
            MergeAccepted(runtime, controller, secondUpdate);
        Assert.Equal(
            PositionTimestampDisposition.ForcePosition, secondDisposition);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.Contention,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                secondUpdate,
                secondDisposition,
                secondTimestamps,
                secondTimestamps.PreviousTeleport));

        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            secondCorrection + new Vector3(192f, 192f, 0f),
            controller.Position);

        // Further pumps are no-ops: the funnel settled on the Equal branch.
        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(
            secondCorrection + new Vector3(192f, 192f, 0f),
            controller.Position);
        AssertConverged(runtime);
    }

    [Fact]
    public void Advanced_DoesNotReissueWhenTheNewestAcceptedEventIsAnOrdinaryApply()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        const uint deferredLandblock = 0x02020000u;
        WorldSession.EntityPositionUpdate parkedUpdate = ForceUpdate(
            new Vector3(10f, 10f, SpawnHeight),
            landblockId: deferredLandblock | 0x0001u);
        (PositionTimestampDisposition parkedDisposition,
            AcceptedPhysicsTimestamps parkedTimestamps) =
            MergeAccepted(runtime, controller, parkedUpdate);
        Assert.Equal(
            PositionTimestampDisposition.ForcePosition, parkedDisposition);
        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.DeferredCell,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                parkedUpdate,
                parkedDisposition,
                parkedTimestamps,
                parkedTimestamps.PreviousTeleport));
        Assert.Equal(1, drive.PendingCount);
        ulong authorityAtPark = record.PositionAuthorityVersion;
        Vector3 positionAtPark = controller.Position;

        var ordinaryPose = new Vector3(31f, 33f, SpawnHeight);
        (PositionTimestampDisposition ordinaryDisposition, _) = MergeAccepted(
            runtime,
            controller,
            OrdinaryUpdate(ordinaryPose, positionSequence: 3));
        Assert.Equal(PositionTimestampDisposition.Apply, ordinaryDisposition);
        Assert.NotEqual(authorityAtPark, record.PositionAuthorityVersion);

        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(positionAtPark, controller.Position);
        Assert.NotEqual(ordinaryPose, controller.Position);
        Assert.Single(gameActions);

        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(positionAtPark, controller.Position);
        Assert.Single(gameActions);
        AssertConverged(runtime);
    }

    [Fact]
    public void OneServerCorrectionProducesExactlyOnePlacementAndOneAck()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        const uint deferredLandblock = 0x02020000u;
        WorldSession.EntityPositionUpdate parkedUpdate = ForceUpdate(
            new Vector3(10f, 10f, SpawnHeight),
            landblockId: deferredLandblock | 0x0001u);
        (PositionTimestampDisposition parkedDisposition,
            AcceptedPhysicsTimestamps parkedTimestamps) =
            MergeAccepted(runtime, controller, parkedUpdate);
        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.DeferredCell,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                parkedUpdate,
                parkedDisposition,
                parkedTimestamps,
                parkedTimestamps.PreviousTeleport));
        Assert.Equal(1, drive.PendingCount);
        Assert.Empty(gameActions);

        var corrected = new Vector3(30f, 32f, SpawnHeight);
        WorldSession.EntityPositionUpdate correction = ForceUpdate(
            corrected,
            positionSequence: 3,
            forcePositionSequence: 2);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, correction);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.Committed,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                correction,
                disposition,
                timestamps,
                timestamps.PreviousTeleport));

        Assert.Equal(0, drive.PendingCount);
        Assert.Single(gameActions);
        Assert.Equal(corrected, controller.Position);
        ulong placementsAfterCorrection = record.PlacementCommitVersion;

        drive.Advance();
        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Single(gameActions);
        Assert.Equal(corrected, controller.Position);
        Assert.Equal(placementsAfterCorrection, record.PlacementCommitVersion);
        AssertConverged(runtime);
    }

    [Fact]
    public void TerminalWithoutCommit_SendsExactlyOnePositionEventAndLeavesTheBodyUnmoved()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        Assert.True(controller.CanSendPositionEvent);

        const uint deferredLandblock = 0x02020000u;
        WorldSession.EntityPositionUpdate correction = ForceUpdate(
            new Vector3(10f, 10f, SpawnHeight),
            landblockId: deferredLandblock | 0x0001u);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, correction);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);
        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.DeferredCell,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                correction,
                disposition,
                timestamps,
                timestamps.PreviousTeleport));

        Vector3 poseAtPark = controller.Position;
        ulong placementVersionAtPark = record.PlacementCommitVersion;

        Assert.Empty(gameActions);

        RuntimePlacementCancellationReceipt cancellation =
            runtime.EntityObjects.Physics.SetPosition.Forget(record);
        if (cancellation.IsValid)
        {
            runtime.EntityObjects.Physics.SetPosition
                .PublishCancellation(cancellation);
        }

        drive.Advance();

        Assert.Equal(poseAtPark, controller.Position);
        Assert.Equal(placementVersionAtPark, record.PlacementCommitVersion);
        Assert.Single(gameActions);
        Assert.Equal(0, drive.PendingCount);

        drive.Advance();
        drive.Advance();
        Assert.Single(gameActions);
        Assert.Equal(poseAtPark, controller.Position);
        Assert.Equal(placementVersionAtPark, record.PlacementCommitVersion);
        AssertConverged(runtime);
    }

    [Fact]
    public void Committed_SendsExactlyOnePositionEventAcrossTheCommitAndTheSettle()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        var corrected = new Vector3(30f, 32f, SpawnHeight);
        WorldSession.EntityPositionUpdate correction = ForceUpdate(corrected);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, correction);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.Committed,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                correction,
                disposition,
                timestamps,
                timestamps.PreviousTeleport));

        Assert.Equal(corrected, controller.Position);
        Assert.Single(gameActions);
        Assert.Equal(0, drive.PendingCount);

        drive.Advance();
        drive.Advance();
        Assert.Single(gameActions);
        AssertConverged(runtime);
    }

    [Fact]
    public void QuiescingDestinationPrefix_ForcePositionParkIsNotRestored()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        PhysicsBody body = Assert.IsType<PhysicsBody>(record.PhysicsBody);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out _);

        runtime.EntityObjects.Physics.SetPosition.BeginCollisionPrefixQuiescence(
            DestinationLandblock,
            collisionGeneration: 2UL,
            includeOutdoorCells: true);

        WorldSession.EntityPositionUpdate correction = ForceUpdate(
            new Vector3(10f, 10f, SpawnHeight),
            landblockId: DestinationLandblock | 0x0001u);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, correction);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.DeferredCell,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                correction,
                disposition,
                timestamps,
                timestamps.PreviousTeleport));
        Assert.False(body.InWorld);

        MergeAccepted(
            runtime,
            controller,
            OrdinaryUpdate(new Vector3(31f, 33f, SpawnHeight), positionSequence: 3));

        Assert.False(runtime.EntityObjects.Physics.IsSpatialRoot(record));
        Assert.False(body.InWorld);
        Assert.False(record.ObjectClock.IsActive);

        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        AssertConverged(runtime);
    }

    [Fact]
    public void QuiescingSweptNeighbour_ForcePositionParkIsRestoredByTheNextPacket()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        CommitLandblockCollision(
            runtime, NeighbourLandblock, worldOffsetX: 192f);
        PhysicsBody body = Assert.IsType<PhysicsBody>(record.PhysicsBody);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out _);

        runtime.EntityObjects.Physics.SetPosition.BeginCollisionPrefixQuiescence(
            NeighbourLandblock,
            collisionGeneration: 2UL,
            includeOutdoorCells: true);
        // Neither the source nor the destination is quiescing — the whole
        // point of the shape.
        Assert.False(
            runtime.EntityObjects.Physics.SetPosition.IsCollisionPrefixQuiescing(
                SpawnLandblock));

        WorldSession.EntityPositionUpdate correction = ForceUpdate(
            new Vector3(191.95f, 10f, SpawnHeight),
            landblockId: SpawnSeamCell);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, correction);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.DeferredCell,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                correction,
                disposition,
                timestamps,
                timestamps.PreviousTeleport));
        Assert.False(body.InWorld);

        MergeAccepted(
            runtime,
            controller,
            OrdinaryUpdate(new Vector3(31f, 33f, SpawnHeight), positionSequence: 3));

        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(runtime.EntityObjects.Physics.IsSpatialRoot(record));

        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        AssertConverged(runtime);
    }

    [Fact]
    public void QuiescingOwnPrefix_SeamCrossingParkIsRestoredAtTheReDerivedNeighbourCell()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        PhysicsBody body = Assert.IsType<PhysicsBody>(record.PhysicsBody);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out _);

        runtime.EntityObjects.Physics.SetPosition.BeginCollisionPrefixQuiescence(
            SpawnLandblock,
            collisionGeneration: 2UL,
            includeOutdoorCells: true);

        WorldSession.EntityPositionUpdate correction = ForceUpdate(
            new Vector3(192.05f, 10f, SpawnHeight),
            landblockId: SpawnSeamCell);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, correction);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.DeferredCell,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                correction,
                disposition,
                timestamps,
                timestamps.PreviousTeleport));
        Assert.False(body.InWorld);

        Assert.Equal(SpawnLandblock, correction.Position.LandblockId & 0xFFFF0000u);
        Assert.Equal(
            NeighbourLandblock,
            body.CellPosition.ObjCellId & 0xFFFF0000u);
        Assert.False(
            runtime.EntityObjects.Physics.SetPosition.IsCollisionPrefixQuiescing(
                NeighbourLandblock));

        MergeAccepted(
            runtime,
            controller,
            OrdinaryUpdate(new Vector3(31f, 33f, SpawnHeight), positionSequence: 3));

        Assert.True(body.InWorld);
        Assert.True(record.ObjectClock.IsActive);
        Assert.True(runtime.EntityObjects.Physics.IsSpatialRoot(record));

        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        AssertConverged(runtime);
    }

    [Fact]
    public void QuiescenceOpenedAfterTheParkDeclinesTheRestoreAtMergeTime()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        CommitLandblockCollision(
            runtime, NeighbourLandblock, worldOffsetX: 192f);
        PhysicsBody body = Assert.IsType<PhysicsBody>(record.PhysicsBody);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out _);

        runtime.EntityObjects.Physics.SetPosition.BeginCollisionPrefixQuiescence(
            NeighbourLandblock,
            collisionGeneration: 2UL,
            includeOutdoorCells: true);

        WorldSession.EntityPositionUpdate correction = ForceUpdate(
            new Vector3(191.95f, 10f, SpawnHeight),
            landblockId: SpawnSeamCell);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, correction);
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        Assert.Equal(
            RuntimeAcceptedPositionExecutionStatus.DeferredCell,
            drive.TryExecuteAcceptedLocalPosition(
                record,
                correction,
                disposition,
                timestamps,
                timestamps.PreviousTeleport));
        Assert.False(body.InWorld);
        Assert.Equal(
            SpawnLandblock,
            body.CellPosition.ObjCellId & 0xFFFF0000u);

        // …and now streaming retires that very landblock, mid-park.
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionPrefixQuiescence(
            SpawnLandblock,
            collisionGeneration: 3UL,
            includeOutdoorCells: true);

        MergeAccepted(
            runtime,
            controller,
            OrdinaryUpdate(new Vector3(31f, 33f, SpawnHeight), positionSequence: 3));

        Assert.False(runtime.EntityObjects.Physics.IsSpatialRoot(record));

        drive.Advance();
        Assert.Equal(0, drive.PendingCount);
        AssertConverged(runtime);
    }

    #region Portal arm route

    [Fact]
    public void PortalCommitted_UnderServerControlSendsNoMovementEvent()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        Assert.True(runtime.CharacterOwner.TrySetAutonomyLevel(0u));
        Assert.True(runtime.CharacterOwner.UsePositionFromServer);

        const ushort teleportSequence = 6;
        var destinationPosition = new Vector3(31f, 33f, SpawnHeight);
        WorldSession.EntityPositionUpdate destinationUpdate = PortalDestinationUpdate(
            destinationPosition, SpawnLandblock | 0x0001u, teleportSequence);
        MergeAccepted(runtime, controller, destinationUpdate);
        (RuntimePortalPlacementAuthority portal, RuntimeTeleportDestination destination) =
            BeginPortal(runtime, SpawnLandblock | 0x0001u, teleportSequence, destinationUpdate);
        using var portalHostGuard = new PortalHostConvergenceGuard(
            runtime, portal.RevealGeneration, portal.Projection);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedPortalArrival(destination, portal);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.Committed, status);
        Assert.Equal(destinationPosition, controller.Position);
        Assert.False(runtime.MovementOwner.AutoRunActive);
        Assert.Empty(gameActions);
        ConvergePortalHost(runtime, portal.RevealGeneration, portal.Projection);
        AssertConverged(runtime);
    }

    [Fact]
    public void PortalCommitted_MovesBodyArmsLeashOnceCancelsAutorunAndSendsExactlyOneMovementEvent()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);

        var staleAnchor = new Position(
            SpawnLandblock | 0x0001u,
            new Vector3(1f, 1f, SpawnHeight),
            Quaternion.Identity);
        controller.PositionManager!.ConstrainTo(staleAnchor, 1f, 2f);
        Assert.True(controller.PositionManager.Constraint!.IsConstrained);

        runtime.MovementOwner.Execute(RuntimeMovementCommand.ToggleRunLock);
        Assert.True(runtime.MovementOwner.AutoRunActive);

        const ushort teleportSequence = 5;
        var destinationPosition = new Vector3(30f, 32f, SpawnHeight);
        WorldSession.EntityPositionUpdate destinationUpdate = PortalDestinationUpdate(
            destinationPosition, SpawnLandblock | 0x0001u, teleportSequence);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps timestamps) =
            MergeAccepted(runtime, controller, destinationUpdate);
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.True(timestamps.TeleportAdvanced);

        (RuntimePortalPlacementAuthority portal, RuntimeTeleportDestination destination) =
            BeginPortal(runtime, SpawnLandblock | 0x0001u, teleportSequence, destinationUpdate);
        using var portalHostGuard = new PortalHostConvergenceGuard(
            runtime, portal.RevealGeneration, portal.Projection);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedPortalArrival(destination, portal);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.Committed, status);
        Assert.Equal(destinationPosition, controller.Position);
        Assert.Equal(Vector3.Zero, controller.BodyVelocity);
        Assert.True(controller.PositionManager.Constraint!.IsConstrained);
        Assert.Equal(
            controller.Position,
            controller.PositionManager.Constraint.ConstraintPos.Frame.Origin);
        Assert.False(runtime.MovementOwner.AutoRunActive);
        // Exactly one outbound wire packet total: the movement-event refresh.
        // Zero AutonomousPosition - the portal route never sends one.
        Assert.Single(gameActions);
        ConvergePortalHost(runtime, portal.RevealGeneration, portal.Projection);
        AssertConverged(runtime);
    }

    [Fact]
    public void PortalProducerInvalidAuthority_ArmDoesNotRunAndNothingMutates()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        Vector3 positionBefore = controller.Position;
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        var destination = new RuntimeTeleportDestination(
            PlayerGuid,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 5,
            ForcePositionSequence: 0,
            new Position(
                SpawnLandblock | 0x0001u,
                new Vector3(30f, 32f, SpawnHeight),
                Quaternion.Identity));
        // Present but structurally invalid: RevealGeneration 0 fails
        // RuntimePortalPlacementAuthority.IsValid outright - the exact shape
        // a stale-generation TryRegisterHostProjection re-derivation refusal
        // would leave the producer holding (default).
        var invalidAuthority = new RuntimePortalPlacementAuthority(
            Present: true,
            RevealGeneration: 0,
            TeleportSequence: 5,
            Projection: default);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedPortalArrival(destination, invalidAuthority);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.NotApplicable, status);
        Assert.Equal(positionBefore, controller.Position);
        Assert.Empty(gameActions);
        AssertConverged(runtime);
    }

    [Fact]
    public void PortalBeginCellMismatch_RefusesWithoutMutatingBodyOrTransit()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);

        const ushort teleportSequence = 6;
        var destinationPosition = new Vector3(30f, 32f, SpawnHeight);
        WorldSession.EntityPositionUpdate destinationUpdate = PortalDestinationUpdate(
            destinationPosition, SpawnLandblock | 0x0001u, teleportSequence);
        MergeAccepted(runtime, controller, destinationUpdate);
        (RuntimePortalPlacementAuthority portal, RuntimeTeleportDestination destination) =
            BeginPortal(runtime, SpawnLandblock | 0x0001u, teleportSequence, destinationUpdate);
        using var portalHostGuard = new PortalHostConvergenceGuard(
            runtime, portal.RevealGeneration, portal.Projection);

        const uint otherLandblock = 0x02020000u;
        (PositionTimestampDisposition secondDisposition, _) = MergeAccepted(
            runtime,
            controller,
            PortalDestinationUpdate(
                new Vector3(1f, 1f, SpawnHeight),
                otherLandblock | 0x0001u,
                teleportSequence,
                positionSequence: 3));
        Assert.Equal(PositionTimestampDisposition.Apply, secondDisposition);
        Vector3 positionBefore = controller.Position;
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedPortalArrival(destination, portal);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.Contention, status);
        Assert.Equal(positionBefore, controller.Position);
        Assert.Empty(gameActions);
        Assert.True(runtime.TransitOwner.IsTeleportActive);
        Assert.False(runtime.TransitOwner.Snapshot.Cancelled);
        ConvergePortalHost(runtime, portal.RevealGeneration, portal.Projection);
        AssertConverged(runtime);
    }

    [Fact]
    public void PortalContention_WhenTheEntityAlreadyOwnsAnActiveOperation()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        Vector3 positionBefore = controller.Position;

        const ushort teleportSequence = 7;
        WorldSession.EntityPositionUpdate destinationUpdate = PortalDestinationUpdate(
            new Vector3(30f, 32f, SpawnHeight), SpawnLandblock | 0x0001u, teleportSequence);
        MergeAccepted(runtime, controller, destinationUpdate);
        (RuntimePortalPlacementAuthority portal, RuntimeTeleportDestination destination) =
            BeginPortal(runtime, SpawnLandblock | 0x0001u, teleportSequence, destinationUpdate);
        using var portalHostGuard = new PortalHostConvergenceGuard(
            runtime, portal.RevealGeneration, portal.Projection);

        RuntimeEntityPlacementToken displaced = runtime.EntityObjects.Physics
            .SetPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative);
        Assert.True(displaced.IsValid);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        RuntimeAcceptedPositionExecutionStatus status =
            drive.TryExecuteAcceptedPortalArrival(destination, portal);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.Contention, status);
        Assert.Equal(positionBefore, controller.Position);
        Assert.Empty(gameActions);

        RuntimePlacementCancellationReceipt cancellation = runtime.EntityObjects
            .Physics.SetPosition.ForgetExactPlacement(displaced);
        if (cancellation.IsValid)
        {
            runtime.EntityObjects.Physics.SetPosition
                .PublishCancellation(cancellation);
        }
        ConvergePortalHost(runtime, portal.RevealGeneration, portal.Projection);
        AssertConverged(runtime);
    }

    [Fact]
    public void PortalDeferredCell_ParksThenCommitsExactlyOnceOnTheCollisionGenerationWake()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(runtime, out List<byte[]> gameActions);

        const uint deferredLandblock = 0x02020000u;
        var deferredPosition = new Vector3(10f, 10f, SpawnHeight);
        const ushort teleportSequence = 8;
        WorldSession.EntityPositionUpdate destinationUpdate = PortalDestinationUpdate(
            deferredPosition, deferredLandblock | 0x0001u, teleportSequence);
        (PositionTimestampDisposition disposition, AcceptedPhysicsTimestamps mergeTimestamps) =
            MergeAccepted(runtime, controller, destinationUpdate);
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.True(mergeTimestamps.TeleportAdvanced);
        (RuntimePortalPlacementAuthority portal, RuntimeTeleportDestination destination) =
            BeginPortal(runtime, deferredLandblock | 0x0001u, teleportSequence, destinationUpdate);
        using var portalHostGuard = new PortalHostConvergenceGuard(
            runtime, portal.RevealGeneration, portal.Projection);

        RuntimeAcceptedPositionExecutionStatus parked =
            drive.TryExecuteAcceptedPortalArrival(destination, portal);

        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.DeferredCell, parked);
        Assert.Empty(gameActions);
        Assert.Equal(1, drive.PendingCount);

        drive.Advance();
        Assert.Equal(1, drive.PendingCount);

        CommitLandblockCollision(runtime, deferredLandblock);
        DrainPlacementFifo(runtime);
        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Equal(deferredPosition, controller.Position);

        Assert.Single(gameActions);

        drive.Advance();
        drive.Advance();
        Assert.Single(gameActions);
        ConvergePortalHost(runtime, portal.RevealGeneration, portal.Projection);
        AssertConverged(runtime);
    }

    [Fact]
    public void PortalDeferredCell_WakeAbandonsInsteadOfReconcilingWhenAuthorityWentStale()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        (RuntimeEntityRecord record, PlayerMovementController controller) =
            EnterLocalPlayer(runtime);
        RuntimeAcceptedPositionDriveController drive =
            CreateAcceptedPositionDrive(
                runtime,
                out List<byte[]> gameActions,
                isPortalAuthorityCurrent: static _ => false);

        const uint deferredLandblock = 0x02020000u;
        var deferredPosition = new Vector3(10f, 10f, SpawnHeight);
        const ushort teleportSequence = 9;
        WorldSession.EntityPositionUpdate destinationUpdate = PortalDestinationUpdate(
            deferredPosition, deferredLandblock | 0x0001u, teleportSequence);
        MergeAccepted(runtime, controller, destinationUpdate);
        (RuntimePortalPlacementAuthority portal, RuntimeTeleportDestination destination) =
            BeginPortal(runtime, deferredLandblock | 0x0001u, teleportSequence, destinationUpdate);
        using var portalHostGuard = new PortalHostConvergenceGuard(
            runtime, portal.RevealGeneration, portal.Projection);

        RuntimeAcceptedPositionExecutionStatus parked =
            drive.TryExecuteAcceptedPortalArrival(destination, portal);
        Assert.Equal(RuntimeAcceptedPositionExecutionStatus.DeferredCell, parked);
        Assert.Equal(1, drive.PendingCount);

        CommitLandblockCollision(runtime, deferredLandblock);
        DrainPlacementFifo(runtime);
        drive.Advance();

        Assert.Equal(0, drive.PendingCount);
        Assert.Empty(gameActions);
        Assert.Equal(deferredPosition, controller.Position);

        ConvergePortalHost(runtime, portal.RevealGeneration, portal.Projection);
        AssertConverged(runtime);
    }

    private static WorldSession.EntityPositionUpdate PortalDestinationUpdate(
        Vector3 position,
        uint landblockId,
        ushort teleportSequence,
        ushort positionSequence = 2) =>
        new(
            PlayerGuid,
            new CreateObject.ServerPosition(
                landblockId,
                position.X,
                position.Y,
                position.Z,
                1f,
                0f,
                0f,
                0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: positionSequence,
            TeleportSequence: teleportSequence,
            ForcePositionSequence: 0);

    private static (RuntimePortalPlacementAuthority Portal, RuntimeTeleportDestination Destination)
        BeginPortal(
            GameRuntime runtime,
            uint destinationCell,
            ushort teleportSequence,
            in WorldSession.EntityPositionUpdate destinationUpdate)
    {
        RuntimeWorldTransitState transit = runtime.TransitOwner;
        Assert.True(transit.TryQueueTeleportStart(teleportSequence));
        Assert.True(transit.ActivateQueuedTeleport());
        var destination = new RuntimeTeleportDestination(
            PlayerGuid,
            InstanceSequence: 1,
            PositionSequence: destinationUpdate.PositionSequence,
            TeleportSequence: teleportSequence,
            ForcePositionSequence: 0,
            new Position(
                destinationCell,
                new Vector3(
                    destinationUpdate.Position.PositionX,
                    destinationUpdate.Position.PositionY,
                    destinationUpdate.Position.PositionZ),
                Quaternion.Identity));
        Assert.True(transit.OfferTeleportDestination(
            destination,
            teleportTimestampAdvanced: true));
        Assert.True(transit.TryBeginPortalReveal(
            teleportSequence,
            destinationCell,
            out long generation));
        Assert.True(transit.TryRegisterHostProjection(
            generation,
            destinationCell,
            out RuntimeWorldHostProjectionToken host));
        return (
            new RuntimePortalPlacementAuthority(true, generation, teleportSequence, host),
            destination);
    }

    private static void ConvergePortalHost(
        GameRuntime runtime,
        long generation,
        RuntimeWorldHostProjectionToken projection)
    {
        RuntimeWorldTransitState transit = runtime.TransitOwner;
        if (!transit.Snapshot.Cancelled && !transit.Snapshot.Completed)
            transit.Cancel(generation);
        transit.AcknowledgeHostProjection(new RuntimeWorldHostAcknowledgement(
            projection, RuntimeWorldHostAcknowledgementStage.SimulationReleaseProjected));
        transit.AcknowledgeHostProjection(new RuntimeWorldHostAcknowledgement(
            projection, RuntimeWorldHostAcknowledgementStage.DestinationReservationReleased));
        transit.AcknowledgeHostProjection(new RuntimeWorldHostAcknowledgement(
            projection, RuntimeWorldHostAcknowledgementStage.TerminalProjected));
        transit.EndTeleport();
    }

    private readonly struct PortalHostConvergenceGuard : IDisposable
    {
        private readonly GameRuntime _runtime;
        private readonly long _generation;
        private readonly RuntimeWorldHostProjectionToken _projection;

        public PortalHostConvergenceGuard(
            GameRuntime runtime,
            long generation,
            RuntimeWorldHostProjectionToken projection)
        {
            _runtime = runtime;
            _generation = generation;
            _projection = projection;
        }

        public void Dispose() =>
            ConvergePortalHost(_runtime, _generation, _projection);
    }

    #endregion

    private static void AssertConverged(GameRuntime runtime)
    {
        RuntimeEntityObjectOwnershipSnapshot ownership =
            runtime.EntityObjects.CaptureOwnership();
        Assert.Equal(0, ownership.AcceptedPositionDrivePendingCount);
    }

    private static (PositionTimestampDisposition Disposition, AcceptedPhysicsTimestamps Timestamps)
        MergeAccepted(
            GameRuntime runtime,
            PlayerMovementController controller,
            in WorldSession.EntityPositionUpdate update)
    {
        Assert.True(runtime.EntityObjects.TryApplyPosition(
            update,
            isLocalPlayer: true,
            forcePositionRotation: controller.BodyOrientation,
            currentLocalVelocity: controller.BodyVelocity,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));
        return (disposition, timestamps);
    }

    private static WorldSession.EntityPositionUpdate ForceUpdate(
        Vector3 position,
        uint landblockId = SpawnLandblock | 0x0001u,
        ushort positionSequence = 2,
        ushort forcePositionSequence = 1) =>
        new(
            PlayerGuid,
            new CreateObject.ServerPosition(
                landblockId,
                position.X,
                position.Y,
                position.Z,
                1f,
                0f,
                0f,
                0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: positionSequence,
            TeleportSequence: 0,
            ForcePositionSequence: forcePositionSequence);

    private static WorldSession.EntityPositionUpdate OrdinaryUpdate(
        Vector3 position,
        ushort positionSequence,
        ushort forcePositionSequence = 1,
        uint landblockId = SpawnLandblock | 0x0001u) =>
        ForceUpdate(
            position,
            landblockId,
            positionSequence,
            forcePositionSequence);

    private static AcceptedPhysicsTimestamps Timestamps(ushort teleport) =>
        new(
            Instance: 1,
            ServerControlledMove: 1,
            Teleport: teleport,
            ForcePosition: 1,
            TeleportAdvanced: false,
            PreviousTeleport: teleport);

    private static (RuntimeEntityRecord Record, PlayerMovementController Controller)
        EnterLocalPlayer(GameRuntime runtime)
    {
        runtime.PlayerIdentity.ServerGuid = PlayerGuid;
        CommitLandblockCollision(runtime, SpawnLandblock);
        RuntimeFirstEntryDriveController firstEntry = CreateFirstEntryDrive(runtime);
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var sessionController = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection(firstEntry));
        LiveEntitySessionSink sink = sessionController.CreateSink();

        sink.Spawned(Spawn(PlayerGuid));
        DrainFirstEntry(runtime, firstEntry);

        RuntimeEntityRecord record = Assert.IsType<RuntimeEntityRecord>(
            GetActive(runtime, PlayerGuid));
        PlayerMovementController controller = Assert.IsType<PlayerMovementController>(
            runtime.MovementOwner.Controller);
        return (record, controller);
    }

    private static RuntimeEntityRecord GetActive(GameRuntime runtime, uint guid)
    {
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            guid, out RuntimeEntityRecord record));
        return record;
    }

    private static RuntimeAcceptedPositionDriveController CreateAcceptedPositionDrive(
        GameRuntime runtime,
        out List<byte[]> gameActions,
        Func<RuntimePortalPlacementAuthority, bool>? isPortalAuthorityCurrent = null)
    {
        var captured = new List<byte[]>();
        gameActions = captured;
        var liveSession = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9001),
            new FixtureTransport())
        {
            GameActionCapture = body => captured.Add(body),
        };
        return new RuntimeAcceptedPositionDriveController(
            runtime.EntityObjects,
            runtime.Clock,
            new UnusedCollisionSource(),
            new LocalPlayerOutboundController((_, _, _, _, _, _) => { }),
            () => runtime.Generation,
            () => runtime.PlayerIdentity.ServerGuid,
            () => runtime.MovementOwner.Controller,
            () => runtime.CharacterOwner.UsePositionFromServer,
            () => liveSession,
            // C4 route 3: the portal arm's PlayerTeleported port needs the
            // autorun latch owner. Unused on the force arm this factory has
            // always served, so every existing route-2 test is unaffected.
            () => runtime.MovementOwner,
            isPortalAuthorityCurrent);
    }

    private static void CommitLandblockCollision(
        GameRuntime runtime,
        uint landblockId,
        float worldOffsetX = 0f)
    {
        // Mirrors HeadlessSessionHostTests.AddFlatLandblock's exact
        // proven-working shape (every heightmap byte and every table entry
        // participate) rather than a sparse table — a resolve near a
        // landblock edge samples neighbouring grid entries too.
        var heights = new byte[81];
        Array.Fill(heights, (byte)SpawnHeight);
        var heightTable = new float[256];
        for (int index = 0; index < heightTable.Length; index++)
            heightTable[index] = index;
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            landblockId, 1UL);
        runtime.EntityObjects.Physics.Engine.AddLandblock(
            landblockId,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX,
            worldOffsetY: 0f);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            landblockId, 1UL, ready: true);
        runtime.EntityObjects.Physics.ObserveLocalWorldFrame(
            landblockId | 0x0001u,
            teleportAdvanced: false);
    }

    private static RuntimeFirstEntryDriveController CreateFirstEntryDrive(
        GameRuntime runtime) =>
        new(
            runtime.EntityObjects,
            runtime.Clock,
            new UnusedCollisionSource(),
            () => PlayerMovementConstructionOptions.Fallback,
            static _ => new RuntimeLocalPlayerPhysicsActivationPreparation(
                Radius: 0.48f,
                Height: 1.835f,
                RuntimeLocalPlayerShadowDisposition.ProvenShapeless));

    private static void DrainFirstEntry(
        GameRuntime runtime,
        RuntimeFirstEntryDriveController drive)
    {
        for (int attempt = 0; attempt < 8 && drive.PendingCount != 0; attempt++)
        {
            drive.DriveAll();
            DrainPlacementFifo(runtime);
        }
        Assert.Equal(0, drive.PendingCount);
    }

    private static void DrainPlacementFifo(GameRuntime runtime)
    {
        while (runtime.EntityObjects.Physics.SetPosition.TryPeekProjection(
                out RuntimePlacementProjectionSnapshot head))
        {
            if (!runtime.EntityObjects.Physics.SetPosition
                    .AcknowledgeProjection(head.Token))
            {
                break;
            }
        }
    }

    private static WorldSession.EntitySpawn Spawn(uint guid)
    {
        var position = new CreateObject.ServerPosition(
            SpawnLandblock | 0x0001u,
            10f,
            10f,
            5f,
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
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: null,
            MotionTableId: null,
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
            null,
            [],
            [],
            [],
            null,
            null,
            "direct entity",
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class UnusedCollisionSource
        : AcDream.Content.IPreparedCollisionSource
    {
        public AcDream.Content.PreparedAssetPresence ProbeCollision(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            AcDream.Content.PreparedAssetPresence.Available;

        public AcDream.Content.PreparedCollisionReadResult<
            FlatSetupCollision> ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            AcDream.Content.PreparedCollisionReadResult<
                FlatSetupCollision>.Missing;

        public AcDream.Content.PreparedCollisionReadResult<
            FlatGfxObjCollisionAsset> ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            FlatCellStructureCollisionAsset> ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            FlatEnvCellTopology> ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionSourceStats CollisionStats =>
            default;

        public void Dispose()
        {
        }
    }

    private sealed class FixtureTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
        }

        public void Send(
            IPEndPoint remote,
            ReadOnlySpan<byte> datagram)
        {
        }

        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<NetReceiveResult>(
                new OperationCanceledException(cancellationToken));

        public void Dispose()
        {
        }
    }

    private sealed class FixtureWorldProjection : IRuntimeDirectWorldProjection
    {
        private readonly RuntimeFirstEntryDriveController _firstEntry;

        internal FixtureWorldProjection(RuntimeFirstEntryDriveController firstEntry) =>
            _firstEntry = firstEntry;

        public void ProjectSpawn(RuntimeEntityRecord record, bool isLocalPlayer) =>
            _firstEntry.DriveAll();

        public void ProjectPosition(
            RuntimeEntityRecord record,
            bool isLocalPlayer,
            PositionTimestampDisposition disposition)
        {
        }

        public void CenterOnAcceptedForcePosition(RuntimeEntityRecord record)
        {
        }

        public void BeginTeleport()
        {
        }

        public RuntimeDestinationReadiness PrepareDestination(
            long revealGeneration,
            RuntimeTeleportDestination destination,
            RuntimeWorldHostProjectionToken portal) =>
            new(
                revealGeneration,
                destination.CellId,
                IsIndoor: false,
                IsUnhydratable: false,
                RequiredRenderRadius: 1,
                IsRenderNeighborhoodReady: true,
                AreCompositeTexturesReady: true,
                IsCollisionReady: true);
    }

    private sealed class StartedRuntime : IDisposable
    {
        internal required GameRuntime Runtime { get; init; }
        internal required LiveSessionHost Live { get; init; }

        public void Dispose()
        {
            _ = Live.Stop(Runtime.Generation);
            Runtime.Dispose();
        }
    }

    private static StartedRuntime StartRuntime()
    {
        var operations = new FixtureGameplayOperations();
        var sessionOperations = new FixtureSessionOperations();
        var runtime = new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations,
            SessionOperations: sessionOperations));
        operations.Bind(runtime);
        var resetHost = new FixtureResetHost();
        var options = new LiveSessionConnectOptions(
            true,
            "127.0.0.1",
            9000,
            "account",
            "password");
        var live = new LiveSessionHost(
            runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new FixtureEventRoute(),
                    _ => new FixtureCommandRoute()),
                generation => runtime.ResetGeneration(generation, resetHost),
                new LiveSessionSelectionBindings(
                    id => runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    runtime.CommunicationOwner.Chat.SetLocalPlayerGuid,
                    _ => { },
                    _ => { },
                    runtime.ActionOwner.Combat.Clear),
                new LiveSessionEnteredWorldBindings(
                    _ => { },
                    () => { },
                    () => { },
                    _ => { },
                    () => { }),
                (_, _, _) => { },
                () => { },
                _ => { },
                _ => { }),
            options);
        LiveSessionStartResult startResult = live.Start(options);
        Assert.Equal(LiveSessionStartStatus.Connected, startResult.Status);
        Assert.NotEqual(0UL, runtime.Generation.Value);
        return new StartedRuntime { Runtime = runtime, Live = live };
    }

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new(endpoint, new FixtureTransport());

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(PlayerGuid, "Direct", 0u)],
                [],
                11,
                "account",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) =>
            session.Dispose();
    }

    private sealed class FixtureEventRoute : ILiveSessionEventRouting
    {
        public void Attach()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixtureCommandRoute : ILiveSessionCommandRouting
    {
        public void Activate()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixtureResetHost : IRuntimeGenerationResetHost
    {
        public void RetireEntityProjection(RuntimeEntityRecord entity)
        {
        }

        public void DrainEntityProjectionBoundary()
        {
        }

        public void CompleteEntityProjectionRetirement()
        {
        }
    }

    private sealed class FixtureGameplayOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        private GameRuntime? _runtime;

        public void Bind(GameRuntime runtime) => _runtime = runtime;
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest()
        {
        }

        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack()
        {
        }

        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => _runtime?.Session.IsInWorld == true;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest()
        {
        }

        public void SendChangeCombatMode(CombatMode mode)
        {
        }

        public uint LocalPlayerId =>
            _runtime?.PlayerIdentity.ServerGuid ?? 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;

        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => false;

        public void StopCompletely()
        {
        }

        public void SendUntargeted(uint spellId)
        {
        }

        public void SendTargeted(uint targetId, uint spellId)
        {
        }

        public void DisplayMessage(string message)
        {
        }

        public void IncrementBusy()
        {
        }
    }
}
