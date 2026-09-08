using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Tests.Physics;

public sealed partial class RuntimeCollisionPrefixQuiescenceTests
{
    private const uint PrefixP = 0xA9B40000u;
    private const uint CellP = PrefixP | 0x0001u;
    private const uint PrefixQ = 0xAAB40000u;
    private const uint CellQ = PrefixQ | 0x0001u;

    [Fact]
    public void EarlierUnrelatedFifoHeadDrainsBeforeEveryResidentWithdraw()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord unrelated = fixture.Add(
            0x70003001u,
            1,
            CellQ,
            new Vector3(10f, 20f, 7f));
        RuntimeEntityRecord first = fixture.Add(
            0x70003002u,
            1,
            CellP,
            new Vector3(11f, 20f, 7f));
        RuntimeEntityRecord second = fixture.Add(
            0x70003003u,
            1,
            CellP,
            new Vector3(12f, 20f, 7f));
        RuntimeSetPositionOutcome unrelatedPlace = fixture.Place(
            unrelated,
            CellQ,
            new Vector3(13f, 20f, 7f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        Assert.False(fixture.TryAcquire(token, out _));
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(first));
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(second));

        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(unrelatedPlace.Projection));
        Assert.False(fixture.TryAcquire(token, out _));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(first));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(second));

        var withdrawals = new List<RuntimePlacementProjectionToken>();
        while (fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
                   out RuntimePlacementProjectionSnapshot pending))
        {
            Assert.Equal(RuntimePlacementProjectionKind.Withdraw, pending.Kind);
            withdrawals.Add(pending.Token);
            Assert.True(fixture.Lifetime.Physics.SetPosition
                .AcknowledgeProjection(pending.Token));
        }

        Assert.Equal(2, withdrawals.Count);
        Assert.True(fixture.TryAcquire(token, out var permission));
        Assert.Equal(withdrawals, permission.Withdrawals);
        Assert.True(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(permission));
        Assert.True(fixture.Lifetime.Physics
            .CancelCollisionPrefixQuiescence(
                token,
                successorGeneration: 2UL,
                successorReady: false));
        Assert.False(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(permission));
    }

    [Fact]
    public void ThrowAndFalseSinkKeepExactWithdrawRetryableUntilAck()
    {
        using var fixture = new Fixture(bindGeneration: true);
        _ = fixture.Add(
            0x70003004u,
            1,
            CellP,
            new Vector3(10f, 21f, 7f));
        int attempt = 0;
        var sink = new RecordingSink(_ => ++attempt switch
        {
            1 => throw new InvalidOperationException("host unavailable"),
            2 => false,
            _ => true,
        });
        using var subscription = fixture.Subscribe(sink);
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        Assert.False(fixture.TryAcquire(token, out _));
        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);
        Assert.False(fixture.TryAcquire(token, out _));
        Assert.Equal(1, fixture.Lifetime.Events.DispatchFailureCount);

        Assert.True(subscription.RetryPending());
        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);
        Assert.True(subscription.RetryPending());
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);

        Assert.True(fixture.TryAcquire(token, out var permission));
        Assert.Single(permission.Withdrawals);
        Assert.Equal(3, sink.Applied.Count);
        Assert.All(sink.Applied,
            projection => Assert.Equal(
                permission.Withdrawals[0].Sequence,
                projection.Token.Sequence));
    }

    [Fact]
    public void SourceToOutsidePlacementIsHeldThenRestoredBeforeBarrierOpens()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord record = fixture.Add(
            0x70003005u,
            1,
            CellP,
            new Vector3(10f, 22f, 7f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        RuntimeSetPositionOutcome held = fixture.Place(
            record,
            CellQ,
            new Vector3(14f, 22f, 7f),
            currentCell: CellP);

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, held.Status);
        Assert.Equal(2UL, held.Projection.CollisionGeneration);
        Assert.Equal(CellQ, held.ExactCellId);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(record));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(held.Projection));
        Assert.True(fixture.TryAcquire(token, out _));

        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));
        Assert.Equal(RuntimePlacementProjectionKind.Place, restored.Kind);
        Assert.Equal(CellQ, restored.Token.ExactCellId);
        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(restored.Token));
        Assert.True(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.Equal(CellQ, record.FullCellId);
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(record));
    }

    [Fact]
    public void OutsideToTargetPlacementIsHeldWithoutEvaluatingRetiringRows()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord record = fixture.Add(
            0x70003006u,
            1,
            CellQ,
            new Vector3(10f, 23f, 7f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        RuntimeSetPositionOutcome held = fixture.Place(
            record,
            CellP,
            new Vector3(15f, 23f, 7f),
            currentCell: CellQ);

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, held.Status);
        Assert.Equal(CellP, held.ExactCellId);
        Assert.Equal(2UL, held.Projection.CollisionGeneration);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(held.Projection));
        Assert.True(fixture.TryAcquire(token, out _));

        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));
        Assert.Equal(CellP, restored.Token.ExactCellId);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(restored.Token));
        Assert.True(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.Equal(restored.Token.ExactCellId, record.FullCellId);
    }

    [Fact]
    public void SupersessionTransfersParkedReceiptAndOnlySuccessorCanAcquire()
    {
        using var fixture = new Fixture();
        _ = fixture.Add(
            0x70003007u,
            1,
            CellP,
            new Vector3(10f, 24f, 7f));
        RuntimeCollisionPrefixQuiescenceToken first = fixture.Begin(2UL);
        Assert.False(fixture.TryAcquire(first, out _));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));

        RuntimeCollisionPrefixQuiescenceToken successor = fixture.Begin(3UL);

        Assert.False(fixture.TryAcquire(first, out _));
        Assert.False(fixture.TryAcquire(successor, out _));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(withdrawal.Token));
        Assert.True(fixture.TryAcquire(successor, out var permission));
        Assert.Equal(successor, permission.Quiescence);
        Assert.Single(permission.Withdrawals);
        Assert.Equal(withdrawal.Token.Sequence,
            permission.Withdrawals[0].Sequence);
    }

    [Fact]
    public void ResetAndDisposeConvergePrefixBarrierOwnership()
    {
        var fixture = new Fixture();
        _ = fixture.Add(
            0x70003008u,
            1,
            CellP,
            new Vector3(10f, 25f, 7f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);
        Assert.False(fixture.TryAcquire(token, out _));
        Assert.Equal(1, fixture.Lifetime.Physics.CaptureOwnership()
            .CollisionPrefixQuiescenceCount);

        fixture.Lifetime.Physics.SetPosition.ResetSession();

        RuntimePhysicsOwnershipSnapshot reset =
            fixture.Lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, reset.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, reset.PendingCollisionPrefixProjectionCount);
        fixture.Dispose();
        Assert.True(fixture.Lifetime.Physics.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void RevisedDiscardRetainsExactBarrierDebtUntilDiscardAck()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord record = fixture.Add(
            0x70003009u,
            1,
            CellP,
            new Vector3(10f, 26f, 7f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        Assert.False(fixture.TryAcquire(token, out _));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.Equal(RuntimePlacementProjectionKind.Withdraw, withdrawal.Kind);

        RuntimePlacementCancellationReceipt cancellation = fixture.Lifetime
            .Physics.SetPosition.Forget(record);
        Assert.True(cancellation.IsValid);
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot discard));
        Assert.Equal(RuntimePlacementProjectionKind.Discard, discard.Kind);
        Assert.Equal(withdrawal.Token.Sequence, discard.Token.Sequence);
        Assert.True(discard.Token.Revision > withdrawal.Token.Revision);
        Assert.False(fixture.TryAcquire(token, out _));

        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(discard.Token));
        Assert.True(fixture.TryAcquire(token, out var permission));
        Assert.Single(permission.Withdrawals);
        Assert.Equal(discard.Token, permission.Withdrawals[0]);
    }

    [Fact]
    public void AcceptedPreparationDebtKeepsParkingFailAtomicAndTokenRetryable()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord record = fixture.Add(
            0x7000300Au,
            1,
            CellP,
            new Vector3(10f, 27f, 7f));
        RuntimeEntityPlacementToken placement = fixture.Lifetime.Physics
            .SetPosition.BeginAcceptedPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        Assert.False(fixture.TryAcquire(token, out _));
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(record));
        Assert.False(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out _));
        Assert.True(fixture.Lifetime.Physics
            .CancelCollisionPrefixQuiescence(token));

        RuntimeSetPositionOutcome submitted = fixture.Lifetime.Physics
            .SetPosition.SubmitPreparedPlacement(
                placement,
                Command(CellP, new Vector3(11f, 27f, 7f), CellP));
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            submitted.Status);
    }

    [Fact]
    public void RetainedPreparationRetryIsCancelledByPrefixRetirement()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord record = fixture.Add(
            0x7000300Bu,
            1,
            CellP,
            new Vector3(10f, 28f, 7f));

        // A retained preparation retry: begun, never prepared — the shape a
        // RetrySetupUnavailable on an asset that never resolves leaves behind.
        RuntimeEntityPlacementToken retained = fixture.Lifetime.Physics
            .SetPosition.BeginAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(retained.IsValid);

        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        Assert.False(fixture.TryAcquire(token, out _));
        Assert.False(fixture.Lifetime.Physics.SetPosition
            .IsPlacementCurrent(retained));
        Assert.Equal(1, fixture.Lifetime.Physics.CaptureOwnership()
            .SetPositionOperationCount); // the replacement retirement park

        bool acquired = false;
        for (int poll = 0; poll < 32 && !acquired; poll++)
        {
            acquired = fixture.TryAcquire(token, out _);
            if (acquired)
                break;
            if (fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
                    out RuntimePlacementProjectionSnapshot projection))
            {
                Assert.True(fixture.Lifetime.Physics.SetPosition
                    .AcknowledgeProjection(projection.Token));
            }
        }
        Assert.True(
            acquired,
            "prefix retirement must converge after it supersedes an "
                + "unprepared mover operation");
    }

    [Fact]
    public void QueriedNeighborPrefixHoldsResultWithoutRequestDependency()
    {
        uint start = PrefixQ | 0x0101u;
        uint rejected = PrefixP | 0x0101u;
        uint winner = PrefixQ | 0x0102u;
        PhysicsEngine engine = FlatEngine();
        PhysicsDataCache cache = engine.DataCache!;
        cache.RegisterCellStructForTest(
            start,
            ContainmentCell(
                new Plane(new Vector3(0f, -1f, 0f), 3f),
                [rejected, winner]));
        cache.RegisterCellStructForTest(
            rejected,
            ContainmentCell(
                new Plane(new Vector3(0f, 1f, 0f), -20f),
                []));
        cache.RegisterCellStructForTest(
            winner,
            ContainmentCell(
                new Plane(new Vector3(0f, 1f, 0f), -7f),
                []));
        using var fixture = new Fixture(engine: engine);
        RuntimeEntityRecord record = fixture.Add(
            0x7000300Bu,
            1,
            start,
            new Vector3(0f, 8f, 1f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        RuntimeSetPositionOutcome held = fixture.Place(
            record,
            start,
            new Vector3(0f, 8f, 1f));

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, held.Status);
        Assert.Equal(winner, held.ExactCellId);
        Assert.Equal(2UL, held.Projection.CollisionGeneration);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(record));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(held.Projection));
        Assert.True(fixture.TryAcquire(token, out _));
    }

    [Fact]
    public void SimultaneousPrefixesParkAndDrainIndependentlyInOneFifo()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord first = fixture.Add(
            0x7000300Cu,
            1,
            CellP,
            new Vector3(10f, 28f, 7f));
        RuntimeEntityRecord second = fixture.Add(
            0x7000300Du,
            1,
            CellQ,
            new Vector3(11f, 28f, 7f));
        RuntimeCollisionPrefixQuiescenceToken p = fixture.Begin(
            PrefixP,
            2UL,
            includeOutdoorCells: true);
        RuntimeCollisionPrefixQuiescenceToken q = fixture.Begin(
            PrefixQ,
            3UL,
            includeOutdoorCells: true);

        Assert.False(fixture.TryAcquire(p, out _));
        Assert.False(fixture.TryAcquire(q, out _));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(first));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(second));

        while (fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
                   out RuntimePlacementProjectionSnapshot pending))
        {
            Assert.True(fixture.Lifetime.Physics.SetPosition
                .AcknowledgeProjection(pending.Token));
        }

        Assert.True(fixture.TryAcquire(p, out var pPermission));
        Assert.True(fixture.TryAcquire(q, out var qPermission));
        Assert.Single(pPermission.Withdrawals);
        Assert.Single(qPermission.Withdrawals);
        Assert.NotEqual(
            pPermission.Withdrawals[0].Sequence,
            qPermission.Withdrawals[0].Sequence);
    }

    [Fact]
    public void PermissionIsInvalidatedByNewPlacementAfterAcquire()
    {
        using var fixture = new Fixture();
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);
        Assert.True(fixture.TryAcquire(token, out var permission));
        Assert.True(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(permission));
        RuntimeEntityRecord record = fixture.Add(
            0x7000300Eu,
            1,
            CellQ,
            new Vector3(10f, 29f, 7f));

        RuntimeSetPositionOutcome held = fixture.Place(
            record,
            CellP,
            new Vector3(12f, 29f, 7f),
            currentCell: CellQ);

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, held.Status);
        Assert.False(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(permission));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(held.Projection));
        Assert.True(fixture.TryAcquire(token, out var replacement));
        Assert.NotEqual(permission.Withdrawals, replacement.Withdrawals);
    }

    [Fact]
    public void IndoorOnlyQuiescenceLeavesOutdoorResidentActive()
    {
        uint indoor = PrefixP | 0x0101u;
        using var fixture = new Fixture();
        RuntimeEntityRecord outdoor = fixture.Add(
            0x7000300Fu,
            1,
            CellP,
            new Vector3(10f, 30f, 7f));
        RuntimeEntityRecord interior = fixture.Add(
            0x70003010u,
            1,
            indoor,
            new Vector3(11f, 30f, 7f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(
            PrefixP,
            2UL,
            includeOutdoorCells: false);

        Assert.False(fixture.TryAcquire(token, out _));
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(outdoor));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(interior));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(withdrawal.Token));
        Assert.True(fixture.TryAcquire(token, out var permission));
        Assert.Single(permission.Withdrawals);
        Assert.Equal(interior.Key, permission.Withdrawals[0].Entity);
    }

    [Fact]
    public void WithdrawCallbackDeleteGuidReuseAndNewPlacementCannotEscapeBarrier()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord first = fixture.Add(
            0x70003011u,
            1,
            CellP,
            new Vector3(10f, 31f, 7f));
        RuntimeEntityRecord retired = fixture.Add(
            0x70003012u,
            1,
            CellP,
            new Vector3(11f, 31f, 7f));
        RuntimeEntityRecord? replacement = null;
        RuntimeSetPositionOutcome reentrant = default;
        var observer = new PlacementObserver(delta =>
        {
            if (replacement is not null
                || delta.Placement.Kind
                    is not RuntimePlacementProjectionKind.Withdraw
                || delta.Placement.Token.Entity != first.Key)
            {
                return;
            }

            Assert.True(fixture.Lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(retired.ServerGuid, retired.Incarnation),
                isLocalPlayer: false,
                removeRetainedObject: false,
                out RuntimeEntityDeleteAcceptance acceptance));
            fixture.Lifetime.CompleteAcceptedDelete(acceptance);
            Assert.Null(fixture.Lifetime.RetireCanonicalOnly(retired));
            replacement = fixture.Add(
                retired.ServerGuid,
                2,
                CellQ,
                new Vector3(12f, 31f, 7f));
            reentrant = fixture.Place(
                replacement,
                CellP,
                new Vector3(13f, 31f, 7f),
                currentCell: CellQ);
        });
        using IDisposable subscription = fixture.Lifetime.Events
            .SubscribePlacement(observer);
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        Assert.False(fixture.TryAcquire(token, out _));
        Assert.NotNull(replacement);
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, reentrant.Status);
        Assert.False(fixture.Lifetime.Entities.IsCurrent(retired));
        Assert.True(fixture.Lifetime.Entities.IsCurrent(replacement));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(replacement));

        int receipts = 0;
        while (fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
                   out RuntimePlacementProjectionSnapshot pending))
        {
            receipts++;
            Assert.True(fixture.Lifetime.Physics.SetPosition
                .AcknowledgeProjection(pending.Token));
        }
        Assert.Equal(3, receipts);
        Assert.True(fixture.TryAcquire(token, out var permission));
        Assert.Equal(3, permission.Withdrawals.Length);
    }

    [Fact]
    public void AbortReleaseKeepsNewcomerBehindBarrierUntilEveryPlaceAck()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord resident = fixture.Add(
            0x70003013u,
            1,
            CellP,
            new Vector3(10f, 32f, 7f));
        RuntimeSetPositionOutcome prepared = fixture.Place(
            resident,
            CellP,
            new Vector3(11f, 32f, 7f),
            currentCell: CellP);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(prepared.Projection));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);
        Assert.False(fixture.TryAcquire(token, out _));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot residentWithdrawal));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(residentWithdrawal.Token));
        Assert.True(fixture.TryAcquire(token, out _));

        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot residentPlace));
        Assert.Equal(RuntimePlacementProjectionKind.Place, residentPlace.Kind);

        RuntimeEntityRecord newcomer = fixture.Add(
            0x70003014u,
            1,
            CellQ,
            new Vector3(12f, 32f, 7f));
        RuntimeSetPositionOutcome newcomerHeld = fixture.Place(
            newcomer,
            CellP,
            new Vector3(13f, 32f, 7f),
            currentCell: CellQ);
        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, newcomerHeld.Status);
        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));

        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(residentPlace.Token));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot newcomerWithdrawal));
        Assert.Equal(
            RuntimePlacementProjectionKind.Withdraw,
            newcomerWithdrawal.Kind);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(newcomerWithdrawal.Token));
        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot newcomerPlace));
        Assert.Equal(RuntimePlacementProjectionKind.Place, newcomerPlace.Kind);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(newcomerPlace.Token));
        Assert.True(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(resident));
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(newcomer));
    }

    [Fact]
    public void ColdAbortRestoreWaitsForExactMoverPreparation()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord resident = fixture.Add(
            0x70003015u,
            1,
            CellP,
            new Vector3(10f, 33f, 7f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);
        Assert.False(fixture.TryAcquire(token, out _));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(withdrawal.Token));
        Assert.True(fixture.TryAcquire(token, out _));

        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.False(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out _));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .TryGetAwaitingPreparationToken(
                resident,
                out RuntimeEntityPlacementToken preparationToken));
        RuntimeSetPositionCommand command = PrepareAuthoredCommand(
            fixture.Lifetime,
            preparationToken);

        RuntimeSetPositionOutcome submitted = fixture.Lifetime.Physics
            .SetPosition.SubmitPreparedPlacement(preparationToken, command);

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, submitted.Status);
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));
        Assert.Equal(RuntimePlacementProjectionKind.Place, restored.Kind);
        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(restored.Token));
        Assert.True(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
    }

    [Fact]
    public void ResetConvergesPendingDiscardAndPendingRestoreReceipts()
    {
        using (var discardFixture = new Fixture())
        {
            RuntimeEntityRecord record = discardFixture.Add(
                0x70003016u,
                1,
                CellP,
                new Vector3(10f, 34f, 7f));
            RuntimeCollisionPrefixQuiescenceToken token =
                discardFixture.Begin(2UL);
            Assert.False(discardFixture.TryAcquire(token, out _));
            _ = discardFixture.Lifetime.Physics.SetPosition.Forget(record);
            Assert.Equal(1, discardFixture.Lifetime.Physics.CaptureOwnership()
                .PendingCollisionPrefixProjectionCount);

            discardFixture.Lifetime.Physics.SetPosition.ResetSession();

            RuntimePhysicsOwnershipSnapshot reset = discardFixture.Lifetime
                .Physics.CaptureOwnership();
            Assert.Equal(0, reset.CollisionPrefixQuiescenceCount);
            Assert.Equal(0, reset.PendingCollisionPrefixProjectionCount);
        }

        using var restoreFixture = new Fixture();
        RuntimeEntityRecord resident = restoreFixture.Add(
            0x70003017u,
            1,
            CellP,
            new Vector3(10f, 35f, 7f));
        RuntimeSetPositionOutcome prepared = restoreFixture.Place(
            resident,
            CellP,
            new Vector3(11f, 35f, 7f),
            currentCell: CellP);
        Assert.True(restoreFixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(prepared.Projection));
        RuntimeCollisionPrefixQuiescenceToken restoreToken =
            restoreFixture.Begin(2UL);
        Assert.False(restoreFixture.TryAcquire(restoreToken, out _));
        Assert.True(restoreFixture.Lifetime.Physics.SetPosition
            .TryPeekProjection(out RuntimePlacementProjectionSnapshot withdraw));
        Assert.True(restoreFixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(withdraw.Token));
        Assert.True(restoreFixture.TryAcquire(restoreToken, out _));
        Assert.False(restoreFixture.Lifetime.Physics
            .CancelCollisionPrefixQuiescence(
                restoreToken,
                successorGeneration: 1UL,
                successorReady: true));
        Assert.Equal(1, restoreFixture.Lifetime.Physics.CaptureOwnership()
            .PendingCollisionPrefixProjectionCount);

        restoreFixture.Lifetime.Physics.SetPosition.ResetSession();

        RuntimePhysicsOwnershipSnapshot restoreReset = restoreFixture.Lifetime
            .Physics.CaptureOwnership();
        Assert.Equal(0, restoreReset.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, restoreReset.PendingCollisionPrefixProjectionCount);
    }

    [Fact]
    public void CancelBeforeFirstAcquireCannotStrandNewHeldPlacement()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord record = fixture.Add(
            0x70003018u,
            1,
            CellQ,
            new Vector3(10f, 36f, 7f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);
        RuntimeSetPositionOutcome held = fixture.Place(
            record,
            CellP,
            new Vector3(11f, 36f, 7f),
            currentCell: CellQ);

        Assert.Equal(RuntimeSetPositionStatus.DeferredCell, held.Status);
        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token));
        Assert.Equal(1, fixture.Lifetime.Physics.CaptureOwnership()
            .CollisionPrefixQuiescenceCount);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(held.Projection));
        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token));

        Assert.False(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));
        Assert.Equal(RuntimePlacementProjectionKind.Place, restored.Kind);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(restored.Token));
        Assert.True(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 1UL,
            successorReady: true));
        Assert.Equal(restored.Token.ExactCellId, record.FullCellId);
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(record));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RuntimeGenerationToken _generation = new(7UL);

        internal Fixture(
            bool bindGeneration = false,
            PhysicsEngine? engine = null)
        {
            Lifetime = new RuntimeEntityObjectLifetime(engine ?? FlatEngine());
            if (bindGeneration)
            {
                Lifetime.BindEventContext(
                    () => _generation,
                    static () => 11UL);
            }
        }

        internal RuntimeEntityObjectLifetime Lifetime { get; }

        internal RuntimeEntityRecord Add(
            uint guid,
            ushort incarnation,
            uint cell,
            Vector3 position)
        {
            RuntimeEntityRecord record = Lifetime.RegisterEntity(
                Spawn(guid, incarnation, cell, position)).Canonical!;
            Lifetime.Entities.SetFinalPhysicsState(
                record,
                PhysicsStateFlags.Gravity);
            Lifetime.Entities.SetFullCell(
                record,
                cell,
                (cell & 0xFFFF0000u) | 0xFFFFu);
            var body = new PhysicsBody
            {
                Position = position,
                Orientation = Quaternion.Identity,
                LastUpdateTime = 1d,
                State = PhysicsStateFlags.Gravity,
                TransientState = TransientStateFlags.Active,
            };
            body.SnapToCell(cell, position, position);
            Lifetime.Entities.SetPhysicsBody(record, body);
            record.ObjectClock.Activate();
            Lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
            return record;
        }

        internal RuntimeSetPositionOutcome Place(
            RuntimeEntityRecord record,
            uint cell,
            Vector3 position,
            uint? currentCell = null) => Lifetime.Physics.SetPosition.Apply(
                record,
                record.PositionAuthorityVersion,
                Command(cell, position, currentCell));

        internal RuntimeCollisionPrefixQuiescenceToken Begin(
            ulong generation) => Lifetime.Physics
                .BeginCollisionPrefixQuiescence(
                    PrefixP,
                    generation,
                    includeOutdoorCells: true);

        internal RuntimeCollisionPrefixQuiescenceToken Begin(
            uint prefix,
            ulong generation,
            bool includeOutdoorCells) => Lifetime.Physics
                .BeginCollisionPrefixQuiescence(
                    prefix,
                    generation,
                    includeOutdoorCells);

        internal bool TryAcquire(
            RuntimeCollisionPrefixQuiescenceToken token,
            out RuntimeCollisionPrefixMutationPermission permission) =>
            Lifetime.Physics.TryAcquireCollisionPrefixMutationPermission(
                token,
                out permission);

        internal RuntimePlacementProjectionSubscription Subscribe(
            IRuntimePlacementProjectionSink sink) => new(
                Lifetime.Placements,
                () => _generation,
                sink,
                retryPendingOnSubscribe: true);

        public void Dispose() => Lifetime.Dispose();
    }

    private sealed class RecordingSink(
        Func<RuntimePlacementProjectionSnapshot, bool> apply)
        : IRuntimePlacementProjectionSink
    {
        internal List<RuntimePlacementProjectionSnapshot> Applied { get; } = [];

        public bool TryApply(in RuntimePlacementProjectionSnapshot projection)
        {
            Applied.Add(projection);
            return apply(projection);
        }
    }

    private sealed class PlacementObserver(
        Action<RuntimePlacementDelta>? onPlacement = null)
        : IRuntimePlacementObserver
    {
        public void OnPlacement(in RuntimePlacementDelta delta) =>
            onPlacement?.Invoke(delta);
    }

    private static RuntimeSetPositionCommand Command(
        uint cell,
        Vector3 position,
        uint? currentCell) => new(
            new PhysicsSetPositionRequest(
                position,
                Quaternion.Identity,
                cell,
                position,
                ImmutableArray<FlatCollisionSphere>.Empty,
                Scale: 1f,
                StepUpHeight: 0.4f,
                StepDownHeight: 0.4f,
                Flags: PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide,
                CurrentCellId: currentCell),
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 10d,
            ExpectedVelocityAuthorityVersion: 0UL);

    private static RuntimeSetPositionCommand PrepareAuthoredCommand(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityPlacementToken token)
    {
        var setup = new FlatSetupCollision(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            [new FlatCollisionSphere(Vector3.Zero, 0.4f)],
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

    private static PhysicsEngine FlatEngine()
    {
        var engine = new PhysicsEngine
        {
            DataCache = new PhysicsDataCache(),
        };
        AddFlatLandblock(engine, PrefixP);
        AddFlatLandblock(engine, PrefixQ);
        return engine;
    }

    private static void AddFlatLandblock(PhysicsEngine engine, uint prefix) =>
        engine.AddLandblock(
            prefix,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

    private static CellPhysics ContainmentCell(
        Plane plane,
        uint[] visibleCells) => new()
        {
            BSP = new PhysicsBSPTree
            {
                Root = new PhysicsBSPNode { Type = BSPNodeType.Leaf },
            },
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode
                {
                    SplittingPlane = plane,
                    PosNode = new CellBSPNode { Type = BSPNodeType.Leaf },
                },
            },
            Portals = [new PortalInfo(0xFFFF, 0, 0)],
            PortalPolygons = new Dictionary<ushort, ResolvedPolygon>(),
            VisibleCellIds = new HashSet<uint>(visibleCells),
        };

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        uint cell,
        Vector3 position)
    {
        var serverPosition = new CreateObject.ServerPosition(
            cell,
            position.X,
            position.Y,
            position.Z,
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
            Position: serverPosition,
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
            serverPosition,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "collision-prefix-quiescence-fixture",
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
}
