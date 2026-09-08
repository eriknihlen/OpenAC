using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed partial class RuntimeCollisionPrefixQuiescenceTests
{
    private const uint CornerLandblock = 0x0000FFFFu;
    private const uint CornerPrefix = 0x00000000u;
    private const uint CornerCell = 0x00000001u;
    private const uint CornerCell2 = 0x00000002u;
    private const uint CornerIndoorCell = 0x00000100u;
    private const uint NeighborLandblock = 0x0001FFFFu;

    [Fact]
    public void CornerLandblockCollisionGenerationCommitsThroughTheProductionAdmissionChain()
    {
        using var fixture = new Fixture(
            bindGeneration: true,
            engine: new PhysicsEngine { DataCache = new PhysicsDataCache() });
        RuntimePhysicsState physics = fixture.Lifetime.Physics;

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(CornerLandblock);
        Assert.Equal(CornerLandblock, admission.LandblockId);
        using PreparedLandblockCollisionGeneration prepared =
            PrepareSealedMutation(physics, admission, CornerLandblock);
        RuntimeCollisionGenerationCommit commit =
            CommitToCompletion(physics, admission, prepared);
        Assert.True(commit.Completed);
        Assert.True(physics.Engine.IsLandblockTerrainResident(CornerLandblock));

        RuntimeCollisionAdmission neighborAdmission =
            physics.BeginCollisionAdmission(NeighborLandblock);
        using PreparedLandblockCollisionGeneration neighborPrepared =
            PrepareSealedMutation(physics, neighborAdmission, NeighborLandblock);
        RuntimeCollisionGenerationCommit neighborCommit =
            CommitToCompletion(physics, neighborAdmission, neighborPrepared);
        Assert.True(neighborCommit.Completed);
        Assert.True(
            physics.Engine.IsLandblockTerrainResident(NeighborLandblock));

        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, ownership.PendingCollisionPrefixProjectionCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void CornerResidentParksAndRestoresAcrossAnActivationReplacement()
    {
        using var fixture = new Fixture(
            bindGeneration: true,
            engine: CornerEngine());
        RuntimeEntityRecord record = fixture.Add(
            0x700031F1u,
            1,
            CornerCell,
            new Vector3(11f, 12f, 0f));
        RuntimeSetPositionOutcome seeded = fixture.Place(
            record,
            CornerCell,
            new Vector3(11.5f, 12f, 0f));
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            seeded.Status);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(seeded.Projection));

        RuntimePhysicsState physics = fixture.Lifetime.Physics;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(CornerLandblock);
        using PreparedLandblockCollisionGeneration prepared =
            PrepareSealedMutation(physics, admission, CornerLandblock);

        RuntimeCollisionGenerationCommit first =
            physics.CommitCollisionGeneration(admission, prepared);
        Assert.False(first.EngineCommitted);
        Assert.False(first.Completed);
        Assert.False(physics.IsSpatialRoot(record));
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawn));
        Assert.Equal(RuntimePlacementProjectionKind.Withdraw, withdrawn.Kind);

        Assert.True(physics.SetPosition.AcknowledgeProjection(withdrawn.Token));
        _ = SealMutation(physics, admission, prepared);
        RuntimeCollisionGenerationCommit transferred =
            physics.CommitCollisionGeneration(admission, prepared);
        Assert.True(transferred.EngineCommitted);
        Assert.False(transferred.Completed);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));
        Assert.Equal(RuntimePlacementProjectionKind.Place, restored.Kind);

        Assert.True(physics.SetPosition.AcknowledgeProjection(restored.Token));
        RuntimeCollisionGenerationCommit completed =
            physics.CommitCollisionGeneration(admission, prepared);
        Assert.True(completed.Completed);
        Assert.True(physics.IsSpatialRoot(record));
        Assert.Equal(CornerCell, record.FullCellId);
        Assert.Equal(0, physics.CaptureOwnership().CollisionPrefixMutationCount);
        Assert.Equal(
            0,
            physics.CaptureOwnership().CollisionPrefixQuiescenceCount);
    }

    [Fact]
    public void CornerPrefixQuiescenceHoldsAndReleasesExactlyLikeANonzeroPrefix()
    {
        using var corner = new Fixture(
            bindGeneration: true,
            engine: CornerEngine());
        using var control = new Fixture(bindGeneration: true);

        List<string> cornerLog = RunHeldPlacementQuiescenceCycle(
            corner,
            CornerLandblock,
            sourceCell: CornerCell,
            targetCell: CornerCell2,
            guid: 0x700031F2u);
        List<string> controlLog = RunHeldPlacementQuiescenceCycle(
            control,
            PrefixP,
            sourceCell: CellP,
            targetCell: PrefixP | 0x0002u,
            guid: 0x700031F3u);

        Assert.Equal(controlLog, cornerLog);
    }

    [Fact]
    public void CornerLandblockDemotesAndWithdrawsThroughRetirementMutations()
    {
        using (var demoteFixture = new Fixture(
            bindGeneration: true,
            engine: CornerEngine()))
        {
            RuntimeEntityRecord outdoor = demoteFixture.Add(
                0x700031F4u,
                1,
                CornerCell,
                new Vector3(12f, 41f, 0f));
            RuntimeEntityRecord indoor = demoteFixture.Add(
                0x700031F5u,
                1,
                CornerIndoorCell,
                new Vector3(13f, 41f, 0f));

            RuntimeCollisionMutationResult first = demoteFixture.Lifetime
                .Physics.DemoteCollisionToTerrain(CornerLandblock);
            Assert.False(first.Completed);
            Assert.True(demoteFixture.Lifetime.Physics.IsSpatialRoot(outdoor));
            Assert.False(demoteFixture.Lifetime.Physics.IsSpatialRoot(indoor));
            Assert.True(demoteFixture.Lifetime.Physics.SetPosition
                .TryPeekProjection(
                    out RuntimePlacementProjectionSnapshot withdrawal));
            Assert.Equal(indoor.Key, withdrawal.Token.Entity);
            Assert.True(demoteFixture.Lifetime.Physics.SetPosition
                .AcknowledgeProjection(withdrawal.Token));

            RuntimeCollisionMutationResult completed = demoteFixture.Lifetime
                .Physics.DemoteCollisionToTerrain(CornerLandblock);
            Assert.True(completed.Completed);
            Assert.True(completed.Ready);
        }

        using var withdrawFixture = new Fixture(
            bindGeneration: true,
            engine: CornerEngine());
        RuntimeEntityRecord record = withdrawFixture.Add(
            0x700031F6u,
            1,
            CornerCell,
            new Vector3(14f, 42f, 0f));
        RuntimeSetPositionOutcome seeded = withdrawFixture.Place(
            record,
            CornerCell,
            new Vector3(14.5f, 42f, 0f));
        Assert.True(withdrawFixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(seeded.Projection));
        RuntimePhysicsState physics = withdrawFixture.Lifetime.Physics;

        RuntimeCollisionMutationResult pending =
            physics.WithdrawCollision(CornerLandblock);
        Assert.False(pending.Completed);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot removed));
        Assert.True(physics.SetPosition.AcknowledgeProjection(removed.Token));
        RuntimeCollisionMutationResult withdrawn =
            physics.WithdrawCollision(CornerLandblock);
        Assert.True(withdrawn.Completed);
        Assert.False(withdrawn.Ready);
        Assert.False(physics.IsSpatialRoot(record));
    }

    [Fact]
    public void AbsentLandblockIdStillCannotBeginQuiescence()
    {
        using var fixture = new Fixture(
            bindGeneration: true,
            engine: CornerEngine());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => fixture.Begin(0u, 2UL, includeOutdoorCells: true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => fixture.Lifetime.Physics.BeginCollisionAdmission(0u));

        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(
            CornerLandblock,
            2UL,
            includeOutdoorCells: true);
        Assert.True(token.IsValid);
        Assert.Equal(CornerPrefix, token.LandblockPrefix);
        Assert.True(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token));
    }

    private static List<string> RunHeldPlacementQuiescenceCycle(
        Fixture fixture,
        uint landblockId,
        uint sourceCell,
        uint targetCell,
        uint guid)
    {
        var log = new List<string>();
        RuntimeEntityRecord record = fixture.Add(
            guid,
            1,
            sourceCell,
            new Vector3(10f, 22f, 0f));
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(
            landblockId,
            2UL,
            includeOutdoorCells: true);
        log.Add($"tokenValid={token.IsValid}");

        RuntimeSetPositionOutcome held = fixture.Place(
            record,
            targetCell,
            new Vector3(14f, 22f, 0f),
            currentCell: sourceCell);
        log.Add($"place={held.Status}");
        log.Add($"placeGeneration={held.Projection.CollisionGeneration}");
        log.Add($"placeCellLow={held.ExactCellId & 0xFFFFu:X4}");
        log.Add(
            $"root={fixture.Lifetime.Physics.IsSpatialRoot(record)}");
        log.Add($"ackWithdraw={fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(held.Projection)}");
        log.Add($"acquire1={fixture.TryAcquire(token, out _)}");
        log.Add($"acquire2={fixture.TryAcquire(token, out _)}");

        log.Add($"cancelRestorePending={fixture.Lifetime.Physics
            .CancelCollisionPrefixQuiescence(
                token,
                successorGeneration: 1UL,
                successorReady: true)}");
        bool peeked = fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored);
        log.Add($"restorePeeked={peeked}");
        log.Add($"restoreKind={restored.Kind}");
        log.Add($"restoreCellLow={restored.Token.ExactCellId & 0xFFFFu:X4}");
        log.Add($"ackRestore={fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(restored.Token)}");
        log.Add($"cancelCompleted={fixture.Lifetime.Physics
            .CancelCollisionPrefixQuiescence(
                token,
                successorGeneration: 1UL,
                successorReady: true)}");
        log.Add($"finalCellLow={record.FullCellId & 0xFFFFu:X4}");
        log.Add(
            $"finalRoot={fixture.Lifetime.Physics.IsSpatialRoot(record)}");
        RuntimePhysicsOwnershipSnapshot ownership =
            fixture.Lifetime.Physics.CaptureOwnership();
        log.Add($"quiescences={ownership.CollisionPrefixQuiescenceCount}");
        log.Add(
            $"pendingProjections={ownership.PendingCollisionPrefixProjectionCount}");
        return log;
    }

    private static RuntimeCollisionGenerationCommit CommitToCompletion(
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        for (int poll = 0; poll < 10_000; poll++)
        {
            RuntimeCollisionGenerationCommit commit =
                physics.CommitCollisionGeneration(admission, prepared);
            if (commit.Completed)
                return commit;
            while (physics.SetPosition.TryPeekProjection(
                       out RuntimePlacementProjectionSnapshot projection))
            {
                Assert.True(physics.SetPosition.AcknowledgeProjection(
                    projection.Token));
            }
            if (!commit.EngineCommitted)
                _ = SealMutation(physics, admission, prepared);
        }
        throw new InvalidOperationException(
            "Collision generation did not complete its mutation transaction.");
    }

    private static PhysicsEngine CornerEngine()
    {
        var engine = new PhysicsEngine
        {
            DataCache = new PhysicsDataCache(),
        };
        AddFlatLandblock(engine, CornerPrefix);
        return engine;
    }
}
