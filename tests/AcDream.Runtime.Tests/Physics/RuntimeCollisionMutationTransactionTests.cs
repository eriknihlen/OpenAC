using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed partial class RuntimeCollisionPrefixQuiescenceTests
{
    [Fact]
    public void ActivationWaitsForExactWithdrawAndPlaceReceipts()
    {
        using var fixture = new Fixture(bindGeneration: true);
        RuntimeEntityRecord record = fixture.Add(
            0x70003101u,
            1,
            CellP,
            new Vector3(11f, 40f, 0f));
        RuntimeSetPositionOutcome seeded = fixture.Place(
            record,
            CellP,
            new Vector3(11.5f, 40f, 0f));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(seeded.Projection));

        RuntimePhysicsState physics = fixture.Lifetime.Physics;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(PrefixP);
        using PreparedLandblockCollisionGeneration prepared =
            PrepareSealedMutation(physics, admission, PrefixP);
        int committedNotifications = 0;
        physics.CollisionGenerationCommitted += _ => committedNotifications++;

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
        Assert.True(completed.EngineCommitted);
        Assert.True(completed.Completed);
        Assert.True(physics.IsSpatialRoot(record));
        Assert.Equal(1, committedNotifications);
        Assert.Equal(0, physics.CaptureOwnership().CollisionPrefixMutationCount);
        Assert.Equal(0, physics.CaptureOwnership().CollisionPrefixQuiescenceCount);
    }

    [Fact]
    public void DemotionParksOnlyIndoorResidentsAndLeavesOutdoorPresentationLive()
    {
        using var fixture = new Fixture(bindGeneration: true);
        RuntimeEntityRecord outdoor = fixture.Add(
            0x70003102u,
            1,
            CellP,
            new Vector3(12f, 41f, 0f));
        RuntimeEntityRecord indoor = fixture.Add(
            0x70003103u,
            1,
            PrefixP | 0x0100u,
            new Vector3(13f, 41f, 0f));

        RuntimeCollisionMutationResult first =
            fixture.Lifetime.Physics.DemoteCollisionToTerrain(PrefixP);
        Assert.False(first.Completed);
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(outdoor));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(indoor));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.Equal(indoor.Key, withdrawal.Token.Entity);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(withdrawal.Token));

        RuntimeCollisionMutationResult completed =
            fixture.Lifetime.Physics.DemoteCollisionToTerrain(PrefixP);
        Assert.True(completed.Completed);
        Assert.True(completed.Ready);
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(outdoor));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(indoor));
        Assert.False(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(out _));
    }

    [Fact]
    public void WithdrawalLeavesResidentUnboundUntilLaterGenerationWakesIt()
    {
        using var fixture = new Fixture(bindGeneration: true);
        RuntimeEntityRecord record = fixture.Add(
            0x70003104u,
            1,
            CellP,
            new Vector3(14f, 42f, 0f));
        RuntimeSetPositionOutcome seeded = fixture.Place(
            record,
            CellP,
            new Vector3(14.5f, 42f, 0f));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(seeded.Projection));
        RuntimePhysicsState physics = fixture.Lifetime.Physics;

        RuntimeCollisionMutationResult withdrawal =
            physics.WithdrawCollision(PrefixP);
        Assert.False(withdrawal.Completed);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot removed));
        Assert.True(physics.SetPosition.AcknowledgeProjection(removed.Token));
        withdrawal = physics.WithdrawCollision(PrefixP);
        Assert.True(withdrawal.Completed);
        Assert.False(withdrawal.Ready);
        Assert.False(physics.IsSpatialRoot(record));

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(PrefixP);
        using PreparedLandblockCollisionGeneration prepared =
            PrepareSealedMutation(physics, admission, PrefixP);
        RuntimeCollisionGenerationCommit completed =
            physics.CommitCollisionGeneration(admission, prepared);
        Assert.True(completed.EngineCommitted);
        Assert.False(completed.Completed);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));
        Assert.Equal(RuntimePlacementProjectionKind.Place, restored.Kind);
        Assert.True(physics.SetPosition.AcknowledgeProjection(restored.Token));

        completed = physics.CommitCollisionGeneration(admission, prepared);
        Assert.True(completed.Completed);
        Assert.True(physics.IsSpatialRoot(record));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionResetClearsPreAndPostEngineMutationDebt(bool postEngine)
    {
        using var fixture = new Fixture(bindGeneration: true);
        RuntimeEntityRecord record = fixture.Add(
            0x70003105u,
            1,
            CellP,
            new Vector3(15f, 43f, 0f));
        RuntimeSetPositionOutcome seeded = fixture.Place(
            record,
            CellP,
            new Vector3(15.5f, 43f, 0f));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .AcknowledgeProjection(seeded.Projection));
        RuntimePhysicsState physics = fixture.Lifetime.Physics;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(PrefixP);
        using PreparedLandblockCollisionGeneration prepared =
            PrepareSealedMutation(physics, admission, PrefixP);

        Assert.False(physics.CommitCollisionGeneration(
            admission,
            prepared).Completed);
        if (postEngine)
        {
            Assert.True(physics.SetPosition.TryPeekProjection(
                out RuntimePlacementProjectionSnapshot withdrawal));
            Assert.True(physics.SetPosition.AcknowledgeProjection(
                withdrawal.Token));
            _ = SealMutation(physics, admission, prepared);
            RuntimeCollisionGenerationCommit transferred =
                physics.CommitCollisionGeneration(admission, prepared);
            Assert.True(transferred.EngineCommitted);
            Assert.False(transferred.Completed);
        }

        _ = fixture.Lifetime.BeginSessionClear();
        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CommittedCollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, ownership.PendingCollisionPrefixProjectionCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void CancelledActivationCannotCommitWhileRestoreAckIsPending()
    {
        using var fixture = new Fixture(bindGeneration: true);
        RuntimePhysicsState physics = fixture.Lifetime.Physics;
        RuntimeCollisionAdmission baselineAdmission =
            physics.BeginCollisionAdmission(PrefixP);
        using (PreparedLandblockCollisionGeneration baseline =
               PrepareSealedMutation(physics, baselineAdmission, PrefixP))
        {
            Assert.True(physics.CommitCollisionGeneration(
                baselineAdmission,
                baseline).Completed);
        }

        RuntimeEntityRecord record = fixture.Add(
            0x70003106u,
            1,
            CellP,
            new Vector3(16f, 44f, 0f));
        RuntimeSetPositionOutcome seeded = fixture.Place(
            record,
            CellP,
            new Vector3(16.5f, 44f, 0f));
        Assert.True(physics.SetPosition.AcknowledgeProjection(
            seeded.Projection));
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(PrefixP);
        using PreparedLandblockCollisionGeneration prepared =
            PrepareSealedMutation(physics, admission, PrefixP);

        RuntimeCollisionGenerationCommit parked =
            physics.CommitCollisionGeneration(admission, prepared);
        Assert.False(parked.EngineCommitted);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.True(physics.SetPosition.AcknowledgeProjection(withdrawal.Token));

        Assert.False(physics.CancelCollisionGeneration(admission, prepared));
        RuntimeCollisionGenerationCommit forbidden =
            physics.CommitCollisionGeneration(admission, prepared);
        Assert.False(forbidden.EngineCommitted);
        Assert.False(forbidden.Completed);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));
        Assert.Equal(RuntimePlacementProjectionKind.Place, restored.Kind);
        Assert.True(physics.SetPosition.AcknowledgeProjection(restored.Token));
        Assert.True(physics.CancelCollisionGeneration(admission, prepared));

        Assert.True(physics.IsSpatialRoot(record));
        Assert.True(physics.Engine.IsLandblockTerrainResident(PrefixP));
        Assert.Equal(0, physics.CaptureOwnership().CollisionPrefixMutationCount);
        Assert.Equal(0, physics.CaptureOwnership().CollisionPrefixQuiescenceCount);
        Assert.Equal(0, physics.CaptureOwnership().CollisionAdmissionCount);
    }

    [Fact]
    public void SupersededAdmissionCancellationRestoresExactBaselineGeneration()
    {
        using var fixture = new Fixture(bindGeneration: true);
        RuntimePhysicsState physics = fixture.Lifetime.Physics;
        RuntimeCollisionAdmission baseline =
            physics.BeginCollisionAdmission(PrefixP);
        using (PreparedLandblockCollisionGeneration preparedBaseline =
               PrepareSealedMutation(physics, baseline, PrefixP))
        {
            Assert.True(physics.CommitCollisionGeneration(
                baseline,
                preparedBaseline).Completed);
        }

        RuntimeEntityRecord record = fixture.Add(
            0x70003107u,
            1,
            CellP,
            new Vector3(17f, 45f, 0f));
        RuntimeSetPositionOutcome seeded = fixture.Place(
            record,
            CellP,
            new Vector3(17.5f, 45f, 0f));
        Assert.True(physics.SetPosition.AcknowledgeProjection(
            seeded.Projection));

        RuntimeCollisionAdmission admissionA =
            physics.BeginCollisionAdmission(PrefixP);
        using PreparedLandblockCollisionGeneration preparedA =
            PrepareSealedMutation(physics, admissionA, PrefixP);
        RuntimeCollisionAdmission admissionB =
            physics.BeginCollisionAdmission(PrefixP);
        Assert.Equal(baseline.Generation, admissionB.PreviousGeneration);
        using PreparedLandblockCollisionGeneration preparedB =
            PrepareSealedMutation(physics, admissionB, PrefixP);

        RuntimeCollisionGenerationCommit parked =
            physics.CommitCollisionGeneration(admissionB, preparedB);
        Assert.False(parked.EngineCommitted);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));

        Assert.False(physics.CancelCollisionGeneration(admissionB, preparedB));
        Assert.True(physics.SetPosition.AcknowledgeProjection(withdrawal.Token));
        Assert.False(physics.CancelCollisionGeneration(admissionB, preparedB));
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));
        Assert.Equal(RuntimePlacementProjectionKind.Place, restored.Kind);
        Assert.True(physics.SetPosition.AcknowledgeProjection(restored.Token));
        Assert.True(physics.CancelCollisionGeneration(admissionB, preparedB));

        Assert.True(physics.IsSpatialRoot(record));
        Assert.Equal(restored.Token.ExactCellId, record.FullCellId);
        Assert.True(physics.Engine.IsLandblockTerrainResident(PrefixP));
        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void CancellationAfterEngineCommitFinishesExactPlaceAckWithoutRollback()
    {
        using var fixture = new Fixture(bindGeneration: true);
        RuntimePhysicsState physics = fixture.Lifetime.Physics;
        RuntimeCollisionAdmission baseline =
            physics.BeginCollisionAdmission(PrefixP);
        using (PreparedLandblockCollisionGeneration preparedBaseline =
               PrepareSealedMutation(physics, baseline, PrefixP))
        {
            Assert.True(physics.CommitCollisionGeneration(
                baseline,
                preparedBaseline).Completed);
        }

        RuntimeEntityRecord record = fixture.Add(
            0x70003108u,
            1,
            CellP,
            new Vector3(18f, 46f, 0f));
        RuntimeSetPositionOutcome seeded = fixture.Place(
            record,
            CellP,
            new Vector3(18.5f, 46f, 0f));
        Assert.True(physics.SetPosition.AcknowledgeProjection(
            seeded.Projection));
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(PrefixP);
        using PreparedLandblockCollisionGeneration prepared =
            PrepareSealedMutation(physics, admission, PrefixP);
        int committedNotifications = 0;
        physics.CollisionGenerationCommitted += _ => committedNotifications++;

        Assert.False(physics.CommitCollisionGeneration(
            admission,
            prepared).Completed);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.True(physics.SetPosition.AcknowledgeProjection(withdrawal.Token));
        _ = SealMutation(physics, admission, prepared);
        RuntimeCollisionGenerationCommit transferred =
            physics.CommitCollisionGeneration(admission, prepared);
        Assert.True(transferred.EngineCommitted);
        Assert.False(transferred.Completed);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot restored));

        Assert.False(physics.CancelCollisionGeneration(admission, prepared));
        Assert.True(physics.Engine.IsLandblockTerrainResident(PrefixP));
        Assert.Equal(0, committedNotifications);
        Assert.True(physics.SetPosition.AcknowledgeProjection(restored.Token));
        Assert.True(physics.CancelCollisionGeneration(admission, prepared));

        Assert.True(physics.IsSpatialRoot(record));
        Assert.True(physics.Engine.IsLandblockTerrainResident(PrefixP));
        Assert.Equal(1, committedNotifications);
        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
    }

    [Fact]
    public void ColdFirstGenerationCancellationReleasesToUnavailableWithoutDebt()
    {
        using var fixture = new Fixture(bindGeneration: true);
        RuntimePhysicsState physics = fixture.Lifetime.Physics;
        RuntimeEntityRecord record = fixture.Add(
            0x70003109u,
            1,
            CellP,
            new Vector3(19f, 47f, 0f));
        RuntimeSetPositionOutcome seeded = fixture.Place(
            record,
            CellP,
            new Vector3(19.5f, 47f, 0f));
        Assert.True(physics.SetPosition.AcknowledgeProjection(
            seeded.Projection));
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(PrefixP);
        Assert.Equal(0UL, admission.PreviousGeneration);
        using PreparedLandblockCollisionGeneration prepared =
            PrepareSealedMutation(physics, admission, PrefixP);

        RuntimeCollisionGenerationCommit parked =
            physics.CommitCollisionGeneration(admission, prepared);
        Assert.False(parked.EngineCommitted);
        Assert.True(physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        Assert.True(physics.SetPosition.AcknowledgeProjection(withdrawal.Token));
        Assert.True(physics.CancelCollisionGeneration(admission, prepared));

        Assert.True(physics.Engine.IsLandblockTerrainResident(PrefixP));
        Assert.False(physics.IsSpatialRoot(record));
        RuntimePhysicsOwnershipSnapshot ownership = physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
        Assert.Equal(0, ownership.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, ownership.PendingCollisionPrefixProjectionCount);
        Assert.Equal(0, ownership.CollisionAdmissionCount);
        RuntimeSetPositionOwnershipSnapshot placement =
            physics.SetPosition.CaptureOwnership();
        Assert.Equal(1, placement.UnboundDeferredCellCount);
        Assert.Equal(0, placement.DeferredBucketCount);
    }

    private static PreparedLandblockCollisionGeneration PrepareSealedMutation(
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission admission,
        uint landblock)
    {
        PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            new RuntimeLandblockCollisionAssets(
                landblock,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                0f,
                0f,
                0u));
        _ = SealMutation(physics, admission, prepared);
        return prepared;
    }

    private static uint[] SealMutation(
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        while (true)
        {
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
                seal = physics.AdvanceCollisionGenerationSeal(
                    admission,
                    prepared);
            }
            while (!seal.Completed && !seal.Restarted);
            if (seal.Completed)
                return [.. prepared.RetainedOwnerIds];
        }
    }
}
