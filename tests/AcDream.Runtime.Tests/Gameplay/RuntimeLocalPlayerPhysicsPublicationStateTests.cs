using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World.Cells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using BSPNodeType = DatReaderWriter.Enums.BSPNodeType;
using CellBSPNode = DatReaderWriter.Types.CellBSPNode;
using CellBSPTree = DatReaderWriter.Types.CellBSPTree;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeLocalPlayerPhysicsPublicationStateTests
{
    private const uint Cell = 0xA9B40021u;
    private const uint SetupId = 0x02000001u;

    [Fact]
    public void PreparationOwnsPrivateBodyAndMutatesNoCanonicalOrSharedState()
    {
        using var fixture = new Fixture();
        uint fullCell = fixture.Record.FullCellId;
        ulong spatial = fixture.Record.SpatialAuthorityVersion;
        ulong physicsEpoch = fixture.Record.PhysicsOwnershipEpoch;
        ulong clockEpoch = fixture.Record.ObjectClockEpoch;
        RuntimePhysicsOwnershipSnapshot physics = fixture.Lifetime.Physics
            .CaptureOwnership();

        RuntimeLocalPlayerPhysicsPublicationToken token = fixture.Prepare();

        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(fullCell, fixture.Record.FullCellId);
        Assert.Equal(spatial, fixture.Record.SpatialAuthorityVersion);
        Assert.Equal(physicsEpoch, fixture.Record.PhysicsOwnershipEpoch);
        Assert.Equal(clockEpoch, fixture.Record.ObjectClockEpoch);
        Assert.Null(fixture.Record.PhysicsHost);
        Assert.Equal(physics.SpatialRootCount,
            fixture.Lifetime.Physics.CaptureOwnership().SpatialRootCount);
        Assert.Equal(physics.RetainedShadowRegistrationCount,
            fixture.Lifetime.Physics.CaptureOwnership()
                .RetainedShadowRegistrationCount);
        Assert.Equal(1, fixture.Owner.CaptureOwnership().CandidateCount);
        Assert.True(token.IsValid);
    }

    [Fact]
    public void ActivationIdExhaustionMutatesNoCandidateOrCanonicalOwner()
    {
        using var fixture = new Fixture();
        typeof(RuntimeLocalPlayerPhysicsPublicationState)
            .GetField(
                "_nextActivationId",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(fixture.Owner, ulong.MaxValue);

        Assert.Throws<OverflowException>(() => fixture.Prepare());

        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(0, fixture.Owner.CaptureOwnership().CandidateCount);
        Assert.Equal(0, fixture.Owner.CaptureOwnership().PendingActivationCount);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .IsExactPreparedPlacementCurrent(
                fixture.Record,
                fixture.Placement,
            fixture.Command));
    }

    [Fact]
    public void DormantOwnershipPreflightFailurePublishesNoCanonicalOwner()
    {
        using var fixture = new Fixture();
        RuntimeLocalPlayerPhysicsPublicationToken publication =
            fixture.Prepare();
        Assert.True(fixture.Lifetime.Physics.SetPosition.Cancel(
            fixture.Record,
            publishWithdrawal: false));
        var unownedBody = new PhysicsBody();

        Assert.Throws<InvalidOperationException>(() => fixture.Lifetime.Physics
            .SetPosition.PrepareDormantLocalActivationOwnership(
                fixture.Record,
                unownedBody,
                publication.Placement));

        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.False(unownedBody.InWorld);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.Equal(1, fixture.Owner.CaptureOwnership().CandidateCount);
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
    }

    [Fact]
    public void CommitOwnsExactDormantBodyAndControllerWithoutWorldEdges()
    {
        using var fixture = new Fixture();
        RuntimeLocalPlayerPhysicsPublicationToken token = fixture.Prepare();
        Assert.True(fixture.Owner.TryCaptureCandidateSnapshot(
            token,
            out RuntimeLocalPlayerPhysicsCandidateSnapshot prepared));
        uint fullCell = fixture.Record.FullCellId;
        ulong spatial = fixture.Record.SpatialAuthorityVersion;
        ulong bodyEpoch = fixture.Record.PhysicsOwnershipEpoch;
        ulong controllerEpoch = fixture.Movement.ControllerOwnershipEpoch;
        double clockPending = fixture.Record.ObjectClock.PendingSeconds;
        bool clockActive = fixture.Record.ObjectClock.IsActive;
        object? currentCell = fixture.Lifetime.Physics.DataCache.CellGraph.CurrCell;

        RuntimeLocalPlayerPhysicsPublicationStatus status = fixture.Owner
            .Commit(token);

        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed, status);
        PlayerMovementController candidate = Assert.IsType<PlayerMovementController>(
            fixture.Movement.Controller);
        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.Same(body, fixture.Record.PhysicsBody);
        Assert.Same(candidate, fixture.Movement.Controller);
        Assert.True(candidate.OwnsPhysicsBody(body));
        Assert.Equal(bodyEpoch + 1UL, fixture.Record.PhysicsOwnershipEpoch);
        Assert.Equal(controllerEpoch + 1UL,
            fixture.Movement.ControllerOwnershipEpoch);
        Assert.Equal(fullCell, fixture.Record.FullCellId);
        Assert.Equal(spatial, fixture.Record.SpatialAuthorityVersion);
        Assert.Same(currentCell,
            fixture.Lifetime.Physics.DataCache.CellGraph.CurrCell);
        Assert.Equal(clockPending, fixture.Record.ObjectClock.PendingSeconds);
        Assert.Equal(clockActive, fixture.Record.ObjectClock.IsActive);
        Assert.Equal(prepared.Position, body.Position);
        Assert.Equal(prepared.Orientation, body.Orientation);
        Assert.Equal(prepared.CellId, body.CellPosition.ObjCellId);
        Assert.Equal(prepared.CellLocalPosition,
            body.CellPosition.Frame.Origin);
        Assert.Equal(prepared.State, body.State);
        Assert.Equal(prepared.TransientState, body.TransientState);
        Assert.Equal(prepared.InWorld, body.InWorld);
        Assert.False(body.InWorld);
        Assert.False((body.TransientState & TransientStateFlags.Active) != 0);
        Assert.Null(fixture.Record.PhysicsHost);
        Assert.Equal(0, fixture.Lifetime.Physics.CaptureOwnership()
            .SpatialRootCount);
        Assert.Equal(0, fixture.Lifetime.Physics.CaptureOwnership()
            .RetainedShadowRegistrationCount);
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .IsExactPreparedPlacementCurrent(
                fixture.Record,
                fixture.Placement,
                fixture.Command));
        Assert.Equal(0, fixture.Owner.CaptureOwnership().CandidateCount);
        AssertNotLive(candidate);
        Assert.Equal(prepared.Position, body.Position);
        Assert.Equal(prepared.Orientation, body.Orientation);
        Assert.Equal(prepared.State, body.State);
        Assert.Equal(prepared.TransientState, body.TransientState);
        Assert.Equal(clockPending, fixture.Record.ObjectClock.PendingSeconds);
        Assert.Equal(clockActive, fixture.Record.ObjectClock.IsActive);
        Assert.Same(currentCell,
            fixture.Lifetime.Physics.DataCache.CellGraph.CurrCell);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.RejectedToken,
            fixture.Owner.Commit(token));
    }

    [Fact]
    public void InitialPhysicsVectorsAndCoefficientsSeedCanonicalActivation()
    {
        Vector3 omega = new(1f, 2f, 3f);
        using var fixture = new Fixture(
            residentWorld: true,
            initialVelocity: new Vector3(100f, 0f, 0f),
            initialOmega: omega,
            initialFriction: 0.25f,
            initialElasticity: 0.08f);

        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.Equal(new Vector3(50f, 0f, 0f), body.Velocity);
        Assert.Equal(omega, body.Omega);
        Assert.Equal(0.25f, body.Friction);
        Assert.Equal(0.08f, body.Elasticity);
        Assert.False(body.InWorld);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Active));

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out _));

        Assert.Equal(new Vector3(50f, 0f, 0f), body.Velocity);
        Assert.Equal(omega, body.Omega);
        Assert.Equal(0.25f, body.Friction);
        Assert.Equal(0.08f, body.Elasticity);
        Assert.True(body.InWorld);
    }

    [Fact]
    public void CommitActivationOnFlatGroundSeedsRetailFirstGravityFrameContact()
    {
        using var fixture = new Fixture(
            residentWorld: true,
            terrainHeight: 2.7f,
            moverSphereOriginZ: 0.475f);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out _));

        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.True(body.InWorld);
        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);
        Assert.True(body.ContactPlaneValid);
        Assert.True(body.ContactPlane.Normal.Z > 0.9f);
        Assert.InRange(body.Position.Z, 2.65f, 2.76f);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                fixture.Movement.Controller);
        Assert.True(controller.CanSendPositionEvent);
        Assert.True(controller.CaptureMovementResult(
            mouseLookEvent: false).IsOnGround);
    }

    [Fact]
    public void CommitActivationOverVoidLeavesFirstEntryGenuinelyAirborne()
    {
        using var fixture = new Fixture(
            residentWorld: true,
            moverSphereOriginZ: 0.475f);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out _));

        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.True(body.InWorld);
        Assert.False(body.InContact);
        Assert.False(body.OnWalkable);
        Assert.False(body.ContactPlaneValid);
        Assert.Equal(3f, body.Position.Z);
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                fixture.Movement.Controller);
        Assert.False(controller.CanSendPositionEvent);
        Assert.False(controller.CaptureMovementResult(
            mouseLookEvent: false).IsOnGround);
    }

    [Fact]
    public void CommitActivationArmsTheLoginConstraintLeashAtTheCommittedPlacement()
    {
        using var fixture = new Fixture(
            residentWorld: true,
            terrainHeight: 2.7f,
            moverSphereOriginZ: 0.475f);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out _));

        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                fixture.Movement.Controller);
        ConstraintManager? constraint =
            controller.PositionManager?.Constraint;
        Assert.NotNull(constraint);
        Assert.True(constraint!.IsConstrained);
        Assert.Equal(
            controller.PhysicsBody.CellPosition.ObjCellId,
            constraint.ConstraintPos.ObjCellId);
        Assert.InRange(
            constraint.ConstraintPos.Frame.Origin.Z, 2.65f, 2.76f);
        Assert.InRange(
            controller.PhysicsBody.Position.Z, 2.65f, 2.76f);
        Assert.Equal(
            ConstraintDistance.GetStartConstraintDistance(
                constraint.ConstraintPos.ObjCellId),
            constraint.ConstraintDistanceStart);
        Assert.Equal(
            ConstraintDistance.GetMaxConstraintDistance(
                constraint.ConstraintPos.ObjCellId),
            constraint.ConstraintDistanceMax);
    }

    [Fact]
    public void CommitActivationNeverRearmsTheLeashOnAStaleRetry()
    {
        using var fixture = new Fixture(
            residentWorld: true,
            terrainHeight: 2.7f,
            moverSphereOriginZ: 0.475f);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out _));
        PlayerMovementController controller =
            Assert.IsType<PlayerMovementController>(
                fixture.Movement.Controller);
        Assert.True(
            controller.PositionManager!.Constraint!.IsConstrained);

        controller.PositionManager.UnConstrain();
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedAuthority,
            fixture.Owner.CommitActivation(evaluation, out _));

        Assert.False(
            controller.PositionManager.Constraint.IsConstrained);
    }

    [Fact]
    public void DeferredActivationEvaluationLeavesExactOwnedGraphDormantAndRetryable()
    {
        using var fixture = new Fixture();
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        PhysicsBody body = fixture.Record.PhysicsBody!;
        Vector3 position = body.Position;
        Quaternion orientation = body.Orientation;
        EvaluationPuritySnapshot before = CaptureEvaluationPurity(fixture);

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var receipt));
        AssertEvaluationPurity(before, CaptureEvaluationPurity(fixture));

        Assert.True(receipt.IsValid);
        Assert.True(fixture.Owner.IsEvaluationCurrent(receipt));
        Assert.False(body.InWorld);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Active));
        Assert.Equal(position, body.Position);
        Assert.Equal(orientation, body.Orientation);
        Assert.Equal(Cell, fixture.Record.FullCellId);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.Equal(0, fixture.Lifetime.Physics.CaptureOwnership()
            .RetainedShadowRegistrationCount);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
        AssertNotLive(fixture.Movement.Controller!);

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var retry));
        AssertEvaluationPurity(before, CaptureEvaluationPurity(fixture));
        Assert.True(retry.EvaluationId > receipt.EvaluationId);
    }

    [Fact]
    public void CommittedEvaluationReceiptRemainsPureAndBindsExactDormantAuthority()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        PhysicsBody body = fixture.Record.PhysicsBody!;
        ulong clockEpoch = fixture.Record.ObjectClockEpoch;
        ulong placementCommit = fixture.Record.PlacementCommitVersion;
        uint fullCell = fixture.Record.FullCellId;
        Vector3 bodyPosition = body.Position;
        Quaternion bodyOrientation = body.Orientation;
        Assert.Equal(fixture.Record.Key, token.Entity);
        Assert.Equal(fixture.Record.PhysicsOwnershipEpoch,
            token.PhysicsOwnershipEpoch);
        Assert.Equal(fixture.Record.ObjectClockEpoch, token.ObjectClockEpoch);
        Assert.Equal(fixture.Movement.ControllerOwnershipEpoch,
            token.ControllerOwnershipEpoch);
        EvaluationPuritySnapshot before = CaptureEvaluationPurity(fixture);

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var receipt));
        AssertEvaluationPurity(before, CaptureEvaluationPurity(fixture));
        Assert.True(receipt.Placement.Result.IsCommitted);
        Assert.True(fixture.Owner.IsEvaluationCurrent(receipt));
        Assert.False(body.InWorld);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Active));
        Assert.Equal(bodyPosition, body.Position);
        Assert.Equal(bodyOrientation, body.Orientation);
        Assert.Equal(fullCell, fixture.Record.FullCellId);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.Equal(clockEpoch, fixture.Record.ObjectClockEpoch);
        Assert.Equal(placementCommit, fixture.Record.PlacementCommitVersion);
        Assert.Equal(0, fixture.Lifetime.Physics.CaptureOwnership()
            .RetainedShadowRegistrationCount);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
        Assert.False(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out _));
        AssertNotLive(fixture.Movement.Controller!);
    }

    [Fact]
    public void CommitActivationPublishesExactLiveRuntimeGraphOnce()
    {
        using var fixture = new Fixture(residentWorld: true);
        bool callbackSawExactGraph = false;
        var placements = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind is not RuntimePlacementProjectionKind.Place)
                return;
            callbackSawExactGraph = fixture.Record.PhysicsBody is { InWorld: true }
                && fixture.Record.PhysicsHost is not null
                && fixture.Movement.Controller is { IsRuntimePublished: true }
                && fixture.Movement.Controller.Movement.MoveTo is not null
                && fixture.Record.ObjectClock.IsActive
                && fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record);
        });
        using IDisposable subscription = fixture.Lifetime.Events
            .SubscribePlacement(placements);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));
        ulong clockEpoch = fixture.Record.ObjectClockEpoch;

        RuntimeDormantSetPositionCommitStatus status = fixture.Owner
            .CommitActivation(evaluation, out var projection);

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed, status);
        Assert.True(projection.IsValid);
        PhysicsBody body = Assert.IsType<PhysicsBody>(fixture.Record.PhysicsBody);
        Assert.True(body.InWorld);
        Assert.True(body.TransientState.HasFlag(TransientStateFlags.Active));
        Assert.NotNull(fixture.Record.PhysicsHost);
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.True(fixture.Record.ObjectClock.IsActive);
        Assert.Equal(clockEpoch + 1UL, fixture.Record.ObjectClockEpoch);
        Assert.True(fixture.Movement.Controller!.IsRuntimePublished);
        RuntimePlacementDelta place = Assert.Single(placements.Deltas);
        Assert.Equal(RuntimePlacementProjectionKind.Place,
            place.Placement.Kind);
        Assert.Equal(projection.Sequence,
            place.Placement.Token.Sequence);
        Assert.True(callbackSawExactGraph);
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedAuthority,
            fixture.Owner.CommitActivation(evaluation, out _));
        Assert.Single(placements.Deltas);
    }

    [Fact]
    public void DeferredCommitWaitsThenRearmsSameLeaseAfterExactGenerationWake()
    {
        using var fixture = new Fixture();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var deferred));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.DeferredCell,
            fixture.Owner.CommitActivation(deferred, out var noProjection));
        Assert.False(noProjection.IsValid);
        Assert.False(fixture.Record.PhysicsBody!.InWorld);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var waiting));
        Assert.False(waiting.IsValid);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);

        CommitProductionCollisionGeneration(fixture, Cell & 0xFFFF0000u);

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var ready));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(ready, out var projection));
        Assert.True(projection.IsValid);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
    }

    [Fact]
    public void DeferredCommitRearmsAfterProductionAdmissionCommitsItsGeneration()
    {
        using var fixture = new Fixture();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var deferred));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.DeferredCell,
            fixture.Owner.CommitActivation(deferred, out var noProjection));
        Assert.False(noProjection.IsValid);
        Assert.False(fixture.Record.PhysicsBody!.InWorld);
        Assert.False(fixture.Movement.Controller!.IsRuntimePublished);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);

        RuntimeCollisionAdmission admission = fixture.Lifetime.Physics
            .BeginCollisionAdmission(Cell & 0xFFFF0000u);
        // While the destination admission is in flight the lease must simply
        // keep waiting — never a rejection, never an early rearm.
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var waiting));
        Assert.False(waiting.IsValid);
        using PreparedLandblockCollisionGeneration prepared = fixture
            .Lifetime.Physics.PrepareCollisionGeneration(admission);
        fixture.Lifetime.Physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(Cell & 0xFFFF0000u));
        Assert.True(CommitPrepared(
            fixture.Lifetime.Physics,
            admission,
            prepared).Committed);

        Assert.Equal(
            admission.Generation,
            fixture.Lifetime.Physics.CollisionGenerationAuthority(Cell));
        Assert.NotEqual(
            admission.Generation,
            fixture.Lifetime.Physics.ExpectedCollisionGeneration(Cell));

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var ready));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(ready, out var projection));
        Assert.True(projection.IsValid);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);
        Assert.NotNull(fixture.Record.PhysicsHost);
        Assert.True(fixture.Movement.Controller!.IsRuntimePublished);
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndoorDormantActivationRecoversOnCommittedAuthorityWithoutAnotherAdmission(bool unbound)
    {
        const uint indoorCell = (Cell & 0xFFFF0000u) | 0x0101u;
        using var fixture = new Fixture(preparePlacement: false, cell: indoorCell);
        if (!unbound)
            CommitProductionCollisionGeneration(fixture, indoorCell & 0xFFFF0000u);
        fixture.RepreparePlacement();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var deferred));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.DeferredCell,
            fixture.Owner.CommitActivation(deferred, out _));
        if (unbound)
            CommitProductionCollisionGeneration(fixture, indoorCell & 0xFFFF0000u);
        ulong authority = fixture.Lifetime.Physics.CollisionGenerationAuthority(indoorCell);
        Assert.NotEqual(0UL, authority);
        Assert.Equal(unbound ? 1 : 0, fixture.Lifetime.Physics.CaptureOwnership()
            .UnboundDeferredSetPositionCellCount);
        Assert.False(fixture.Movement.Controller!.IsRuntimePublished);
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var waiting));
        Assert.False(waiting.IsValid);

        AddSyntheticIndoorCell(fixture.Lifetime.Physics.DataCache, indoorCell);
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var ready));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(ready, out var projection));
        Assert.True(projection.IsValid);
        Assert.True(fixture.Movement.Controller.IsRuntimePublished);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);
        Assert.Equal(indoorCell, fixture.Record.PhysicsBody.CellPosition.ObjCellId);
        Assert.Equal(authority, fixture.Lifetime.Physics.CollisionGenerationAuthority(indoorCell));
        Assert.Equal(0, fixture.Owner.CaptureOwnership().PendingActivationCount);
        Assert.Equal(0, fixture.Lifetime.Physics.CaptureOwnership().DeferredSetPositionBucketCount);
        Assert.Equal(0, fixture.Lifetime.Physics.CaptureOwnership().UnboundDeferredSetPositionCellCount);
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedAuthority,
            fixture.Owner.CommitActivation(ready, out _));
    }

    [Fact]
    public void DeferredIndoorEvaluationCommittedAfterCellArrivesCanRearmImmediately()
    {
        const uint indoorCell = (Cell & 0xFFFF0000u) | 0x0101u;
        using var fixture = new Fixture(preparePlacement: false, cell: indoorCell);
        CommitProductionCollisionGeneration(fixture, indoorCell & 0xFFFF0000u);
        fixture.RepreparePlacement();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var deferred));

        AddSyntheticIndoorCell(fixture.Lifetime.Physics.DataCache, indoorCell);
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.DeferredCell,
            fixture.Owner.CommitActivation(deferred, out _));
        // Already parked ready on the committed generation; no recovery is needed.
        Assert.Equal(1, fixture.Lifetime.Physics.CaptureOwnership().DeferredSetPositionBucketCount);
        Assert.False(fixture.Movement.Controller!.IsRuntimePublished);
        Assert.Equal(0, fixture.Lifetime.Physics.SetPosition
            .TryRecoverUnboundDeferredWhenSpawnReady(indoorCell));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var ready));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(ready, out _));
        Assert.True(fixture.Movement.Controller!.IsRuntimePublished);
        Assert.Equal(indoorCell, fixture.Record.PhysicsBody!.CellPosition.ObjCellId);
        Assert.Equal(0, fixture.Owner.CaptureOwnership().PendingActivationCount);
    }

    [Fact]
    public void SpawnReadyIndoorActivationStillWaitsForThePendingAdmission()
    {
        const uint indoorCell = (Cell & 0xFFFF0000u) | 0x0101u;
        using var fixture = new Fixture(preparePlacement: false, cell: indoorCell);
        CommitProductionCollisionGeneration(fixture, indoorCell & 0xFFFF0000u);
        fixture.RepreparePlacement();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var deferred));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.DeferredCell,
            fixture.Owner.CommitActivation(deferred, out _));

        RuntimeCollisionAdmission admission = fixture.Lifetime.Physics
            .BeginCollisionAdmission(indoorCell & 0xFFFF0000u);
        AddSyntheticIndoorCell(fixture.Lifetime.Physics.DataCache, indoorCell);
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var waiting));
        Assert.False(waiting.IsValid);
        Assert.False(fixture.Movement.Controller!.IsRuntimePublished);
        Assert.False(fixture.Record.PhysicsBody!.InWorld);

        using PreparedLandblockCollisionGeneration prepared = fixture.Lifetime.Physics
            .PrepareCollisionGeneration(admission);
        fixture.Lifetime.Physics.StageCollisionAssets(
            admission, prepared, CollisionAssets(indoorCell & 0xFFFF0000u));
        AddSyntheticIndoorCell(prepared.DataCache, indoorCell);
        Assert.True(CommitPrepared(fixture.Lifetime.Physics, admission, prepared).Committed);
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var ready));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(ready, out _));
        Assert.True(fixture.Movement.Controller.IsRuntimePublished);
        Assert.Equal(0, fixture.Owner.CaptureOwnership().PendingActivationCount);
    }

    [Fact]
    public void DeferredCommitStaysParkedWhileTheCommittingAdmissionIsStillRegistered()
    {
        using var fixture = new Fixture();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var deferred));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.DeferredCell,
            fixture.Owner.CommitActivation(deferred, out _));

        RuntimeCollisionAdmission admission = fixture.Lifetime.Physics
            .BeginCollisionAdmission(Cell & 0xFFFF0000u);
        fixture.Lifetime.Physics.Engine.AddLandblock(
            Cell & 0xFFFF0000u,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        fixture.Lifetime.Physics.SetPosition.CommitCollisionGeneration(
            Cell & 0xFFFF0000u,
            admission.Generation,
            ready: true);
        Assert.Equal(
            admission.Generation,
            fixture.Lifetime.Physics.CollisionGenerationAuthority(Cell));
        Assert.Equal(
            admission.Generation,
            fixture.Lifetime.Physics.ExpectedCollisionGeneration(Cell));
        Assert.False(fixture.Lifetime.Physics
            .IsCollisionEvaluationPrefixAdmissible(Cell));

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var stillWaiting));
        Assert.False(stillWaiting.IsValid);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
        Assert.False(fixture.Record.PhysicsBody!.InWorld);
        Assert.False(fixture.Movement.Controller!.IsRuntimePublished);

        using (PreparedLandblockCollisionGeneration prepared = fixture
            .Lifetime.Physics.PrepareCollisionGeneration(admission))
        {
            fixture.Lifetime.Physics.StageCollisionAssets(
                admission,
                prepared,
                CollisionAssets(Cell & 0xFFFF0000u));
            Assert.True(CommitPrepared(
                fixture.Lifetime.Physics,
                admission,
                prepared).Committed);
        }
        Assert.True(fixture.Lifetime.Physics
            .IsCollisionEvaluationPrefixAdmissible(Cell));

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var ready));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(ready, out var projection));
        Assert.True(projection.IsValid);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);
        Assert.True(fixture.Movement.Controller!.IsRuntimePublished);
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
    }

    [Fact]
    public void RejectedCommitAppliesResponseOnceAndRetainsRetryableLease()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        PhysicsBody body = fixture.Record.PhysicsBody!;
        Vector3 position = body.Position;
        uint fullCell = fixture.Record.FullCellId;
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (_, phase, _, observed) => phase
                is TransitionCellCollisionPhase.Objects
                    ? TransitionState.Collided
                    : observed;
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.RejectedPlacement,
            fixture.Owner.EvaluateActivation(token, out var rejected));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedPlacement,
            fixture.Owner.CommitActivation(rejected, out var projection));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .TryCaptureDormantLocalActivationResult(
                token.Placement,
                out PhysicsSetPositionResult rejectedResult));
        Assert.Equal(PhysicsSetPositionError.NoValidPosition,
            rejectedResult.Error);
        Assert.False(rejectedResult.CollisionHandlerResult);
        Assert.False(projection.IsValid);
        Assert.Equal(position, body.Position);
        Assert.Equal(fullCell, fixture.Record.FullCellId);
        Assert.False(body.InWorld);
        Assert.Null(fixture.Record.PhysicsHost);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedAuthority,
            fixture.Owner.CommitActivation(rejected, out _));

        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook = null;
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var retry));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(retry, out projection));
        Assert.True(projection.IsValid);
    }

    [Fact]
    public void UndefinedShadowDispositionIsRejectedBeforeCandidateConstruction()
    {
        using var fixture = new Fixture(residentWorld: true);
        var invalid = new RuntimeLocalPlayerPhysicsActivationPreparation(
            0.48f,
            1.835f,
            (RuntimeLocalPlayerShadowDisposition)0x7F);

        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority,
            fixture.Owner.Prepare(
                fixture.Record,
                fixture.Placement,
                fixture.Command,
                PlayerMovementConstructionOptions.Fallback,
                invalid,
                out var token));
        Assert.False(token.IsValid);
        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(0, fixture.Owner.CaptureOwnership().CandidateCount);
    }

    [Fact]
    public void RejectedCommitMapsEligibleEnvironmentCallbackToCollided()
    {
        using var fixture = new Fixture(residentWorld: true);
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (transition, _, _, _) =>
            {
                transition.CollisionInfo.CollidedWithEnvironment = true;
                return TransitionState.Collided;
            };
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.RejectedPlacement,
            fixture.Owner.EvaluateActivation(token, out var rejected));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedPlacement,
            fixture.Owner.CommitActivation(rejected, out _));
        Assert.True(fixture.Lifetime.Physics.SetPosition
            .TryCaptureDormantLocalActivationResult(
                token.Placement,
                out PhysicsSetPositionResult result));
        Assert.Equal(PhysicsSetPositionError.Collided, result.Error);
        Assert.True(result.CollisionHandlerResult);
        Assert.Equal(1, fixture.Lifetime.Physics.CollisionReports
            .CaptureOwnership().OwnerCount);

        Assert.True(fixture.Owner.DiscardActivation(token));

        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(0, fixture.Lifetime.Physics.SetPosition
            .CaptureOwnership().ActiveOperationCount);
        RuntimeCollisionReportingOwnershipSnapshot ownership = fixture.Lifetime
            .Physics.CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.TrackedObjectCount);
        Assert.Equal(0, ownership.ReversePeerCount);
        Assert.Equal(0, ownership.PendingSetPositionDispatchCount);
    }

    [Fact]
    public void RegisteredAuthoredShadowActivatesAndMovesExactRows()
    {
        using var fixture = new Fixture(
            residentWorld: true,
            shadowDisposition:
                RuntimeLocalPlayerShadowDisposition.RegisteredAuthoredPayload);
        uint localId = fixture.Record.Key!.Value.LocalEntityId;
        fixture.Lifetime.Physics.Engine.ShadowObjects.Register(
            localId,
            SetupId,
            fixture.Command.Physics.Position + Vector3.UnitX,
            Quaternion.Identity,
            0.48f,
            0f,
            0f,
            Cell & 0xFFFF0000u,
            ShadowCollisionType.Sphere,
            state: (uint)fixture.Record.FinalPhysicsState,
            seedCellId: Cell,
            isStatic: false);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out _));

        ShadowEntry row = Assert.Single(fixture.Lifetime.Physics.Engine
            .ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == localId);
        Assert.Equal(fixture.Record.PhysicsBody!.Position, row.Position);
    }

    [Fact]
    public void DeferredAuthoredActivationSuspendsRowsAndExactWakeRestoresThem()
    {
        using var fixture = new Fixture(
            shadowDisposition:
                RuntimeLocalPlayerShadowDisposition.RegisteredAuthoredPayload);
        uint localId = fixture.Record.Key!.Value.LocalEntityId;
        fixture.Lifetime.Physics.Engine.ShadowObjects.Register(
            localId, SetupId, fixture.Command.Physics.Position,
            Quaternion.Identity, 0.48f, 0f, 0f,
            Cell & 0xFFFF0000u, ShadowCollisionType.Sphere,
            state: (uint)fixture.Record.FinalPhysicsState,
            seedCellId: Cell, isStatic: false);
        Assert.Contains(fixture.Lifetime.Physics.Engine.ShadowObjects
            .AllEntriesForDebug(), row => row.EntityId == localId);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var deferred));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.DeferredCell,
            fixture.Owner.CommitActivation(deferred, out _));

        Assert.False(fixture.Record.PhysicsBody!.InWorld);
        Assert.DoesNotContain(fixture.Lifetime.Physics.Engine.ShadowObjects
            .AllEntriesForDebug(), row => row.EntityId == localId);
        Assert.Equal(1, fixture.Lifetime.Physics.Engine.ShadowObjects
            .SuspendedRegistrationCount);
        Assert.Equal(0, fixture.Lifetime.Physics.Engine.ShadowObjects
            .PendingSetPositionDispatchCount);

        CommitProductionCollisionGeneration(fixture, Cell & 0xFFFF0000u);
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var ready));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(ready, out _));
        Assert.Contains(fixture.Lifetime.Physics.Engine.ShadowObjects
            .AllEntriesForDebug(), row => row.EntityId == localId);
    }

    [Fact]
    public void DeferredDiscardTransfersSuspendedShadowToEntityForLaterReuse()
    {
        using var fixture = new Fixture(
            shadowDisposition:
                RuntimeLocalPlayerShadowDisposition.RegisteredAuthoredPayload);
        uint localId = fixture.Record.Key!.Value.LocalEntityId;
        ShadowObjectRegistry shadows = fixture.Lifetime.Physics.Engine
            .ShadowObjects;
        shadows.Register(
            localId, SetupId, fixture.Command.Physics.Position,
            Quaternion.Identity, 0.48f, 0f, 0f,
            Cell & 0xFFFF0000u, ShadowCollisionType.Sphere,
            state: (uint)fixture.Record.FinalPhysicsState,
            seedCellId: Cell, isStatic: false);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out var deferred));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.DeferredCell,
            fixture.Owner.CommitActivation(deferred, out _));

        Assert.True(fixture.Owner.DiscardActivation(token));

        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(0, fixture.Lifetime.Physics.SetPosition
            .CaptureOwnership().ActiveOperationCount);
        Assert.Equal(1, shadows.SuspendedRegistrationCount);
        Assert.Equal(0, shadows.PendingSetPositionDispatchCount);
        Assert.DoesNotContain(shadows.AllEntriesForDebug(),
            row => row.EntityId == localId);

        const ulong generation = 1UL;
        fixture.Lifetime.Physics.SetPosition.BeginCollisionGeneration(
            Cell & 0xFFFF0000u, generation);
        fixture.Lifetime.Physics.Engine.AddLandblock(
            Cell & 0xFFFF0000u,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        fixture.Lifetime.Physics.SetPosition.CommitCollisionGeneration(
            Cell & 0xFFFF0000u, generation, ready: true);
        fixture.RepreparePlacement();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var retry));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(retry, out var ready));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(ready, out _));

        Assert.Equal(0, shadows.SuspendedRegistrationCount);
        Assert.Contains(shadows.AllEntriesForDebug(),
            row => row.EntityId == localId);
    }

    [Fact]
    public void CollisionCallbackDeleteRetiresPrephaseWithoutShadowOrPlace()
    {
        using var fixture = new Fixture(residentWorld: true);
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (transition, phase, _, observed) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                    transition.CollisionInfo.CollidedWithEnvironment = true;
                return observed;
            };
        var placements = new PlacementObserver();
        using IDisposable placementSubscription = fixture.Lifetime.Events
            .SubscribePlacement(placements);
        bool deleted = false;
        var collisions = new PublicationCollisionObserver(ignoredReport =>
        {
            if (deleted)
                return;
            deleted = true;
            Assert.True(fixture.Lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(
                    fixture.Record.ServerGuid,
                    fixture.Record.Incarnation),
                isLocalPlayer: false,
                removeRetainedObject: false,
                out RuntimeEntityDeleteAcceptance acceptance));
            fixture.Lifetime.CompleteAcceptedDelete(acceptance);
            Assert.Null(fixture.Lifetime.RetireCanonicalOnly(fixture.Record));
        });
        using IDisposable collisionSubscription = fixture.Lifetime.Physics
            .CollisionReports.Subscribe(collisions);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedAuthority,
            fixture.Owner.CommitActivation(evaluation, out var projection));

        Assert.True(deleted);
        Assert.False(projection.IsValid);
        Assert.DoesNotContain(placements.Deltas,
            delta => delta.Placement.Kind is RuntimePlacementProjectionKind.Place);
        Assert.Equal(0, fixture.Owner.CaptureOwnership().PendingActivationCount);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(0, fixture.Lifetime.Physics.CollisionReports
            .CaptureOwnership().PendingSetPositionDispatchCount);
        Assert.Equal(0, fixture.Lifetime.Physics.Engine.ShadowObjects
            .PendingSetPositionDispatchCount);
    }

    [Fact]
    public void CollisionCallbackVectorRefreshesDormantBodyWithRetailClamp()
    {
        using var fixture = new Fixture(residentWorld: true);
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (transition, phase, _, observed) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                    transition.CollisionInfo.CollidedWithEnvironment = true;
                return observed;
            };
        bool updated = false;
        Vector3 omega = new(1f, 2f, 3f);
        var collisions = new PublicationCollisionObserver(ignoredReport =>
        {
            if (updated)
                return;
            updated = true;
            Assert.True(fixture.Lifetime.TryApplyVector(
                new VectorUpdate.Parsed(
                    fixture.Record.ServerGuid,
                    new Vector3(100f, 0f, 0f),
                    omega,
                    InstanceSequence: fixture.Record.Incarnation,
                    VectorSequence: 2),
                acknowledgeProjection: null,
                out _));
            _ = ignoredReport;
        });
        using IDisposable subscription = fixture.Lifetime.Physics
            .CollisionReports.Subscribe(collisions);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out _));

        Assert.True(updated);
        Assert.Equal(new Vector3(50f, 0f, 0f),
            fixture.Record.PhysicsBody!.Velocity);
        Assert.Equal(omega, fixture.Record.PhysicsBody.Omega);
    }

    [Fact]
    public void CollisionCallbackAuthorityChangeRetiresInstalledTracking()
    {
        using var fixture = new Fixture(residentWorld: true);
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (transition, phase, _, observed) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                    transition.CollisionInfo.CollidedWithEnvironment = true;
                return observed;
            };
        bool invalidated = false;
        var collisions = new PublicationCollisionObserver(_ =>
        {
            if (invalidated)
                return;
            invalidated = true;
            fixture.Lifetime.Entities.AdvanceObjDescAuthority(fixture.Record);
        });
        using IDisposable subscription = fixture.Lifetime.Physics
            .CollisionReports.Subscribe(collisions);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedAuthority,
            fixture.Owner.CommitActivation(evaluation, out _));

        RuntimeCollisionReportingOwnershipSnapshot ownership = fixture.Lifetime
            .Physics.CollisionReports.CaptureOwnership();
        Assert.True(invalidated);
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.TrackedObjectCount);
        Assert.Equal(0, ownership.ReversePeerCount);
        Assert.Equal(0, ownership.PendingSetPositionDispatchCount);
    }

    [Fact]
    public void CollisionCallbackNewerPositionSuppressesReciprocalAndPreservesNewLease()
    {
        using var fixture = new Fixture(residentWorld: true);
        RuntimeEntityRecord target = fixture.Lifetime.RegisterEntity(
            Spawn(0x70003002u, incarnation: 1)).Canonical!;
        fixture.Lifetime.Entities.SetFullCell(
            target, Cell, Cell & 0xFFFF0000u);
        fixture.Lifetime.Entities.SetFinalPhysicsState(
            target, PhysicsStateFlags.ReportCollisions);
        var targetBody = new PhysicsBody
        {
            Position = new Vector3(1f, 2f, 3f),
            Orientation = Quaternion.Identity,
            State = PhysicsStateFlags.ReportCollisions,
            InWorld = true,
        };
        targetBody.SnapToCell(Cell, targetBody.Position, targetBody.Position);
        fixture.Lifetime.Entities.SetPhysicsBody(target, targetBody);
        fixture.Lifetime.Physics.AcknowledgeSpatialProjection(
            target, spatial: true);
        fixture.Lifetime.Physics.Engine.ShadowObjects.Register(
            target.Key!.Value.LocalEntityId,
            SetupId,
            targetBody.Position,
            Quaternion.Identity,
            0.48f,
            0f,
            0f,
            Cell & 0xFFFF0000u,
            ShadowCollisionType.Sphere,
            (uint)targetBody.State,
            Cell,
            isStatic: false);
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.CollideObjectGuids.Add(
                        target.Key.Value.LocalEntityId);
                }
                return observed;
            };
        bool updated = false;
        RuntimeEntityPlacementToken newer = default;
        var reports = new List<RuntimeCollisionReport>();
        var collisions = new PublicationCollisionObserver(report =>
        {
            reports.Add(report);
            if (updated || report.Kind is not RuntimeCollisionReportKind.ObjectCollision)
                return;
            updated = true;
            Assert.True(fixture.Lifetime.TryApplyPosition(
                new WorldSession.EntityPositionUpdate(
                    fixture.Record.ServerGuid,
                    new CreateObject.ServerPosition(
                        Cell, 4f, 5f, 3f, 1f, 0f, 0f, 0f),
                    Velocity: null,
                    PlacementId: null,
                    IsGrounded: true,
                    InstanceSequence: fixture.Record.Incarnation,
                    PositionSequence: 2,
                    TeleportSequence: 0,
                    ForcePositionSequence: 0),
                isLocalPlayer: true,
                forcePositionRotation: null,
                currentLocalVelocity: null,
                acknowledgeProjection: null,
                out _,
                out _,
                out _));
            newer = fixture.Lifetime.Physics.SetPosition.BeginAuthoredPlacement(
                fixture.Record,
                fixture.Record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative);
            Assert.True(newer.IsValid);
        });
        using IDisposable subscription = fixture.Lifetime.Physics
            .CollisionReports.Subscribe(collisions);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.RejectedPlacement,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedAuthority,
            fixture.Owner.CommitActivation(evaluation, out _));

        Assert.True(updated);
        Assert.DoesNotContain(reports, report =>
            report.Kind is RuntimeCollisionReportKind.ObjectCollision
            && report.Recipient == target.Key);
        Assert.Equal(1, fixture.Lifetime.Physics.SetPosition
            .CaptureOwnership().ActiveOperationCount);
        RuntimeCollisionReportingOwnershipSnapshot ownership = fixture.Lifetime
            .Physics.CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.TrackedObjectCount);
        Assert.Equal(0, ownership.ReversePeerCount);
    }

    [Fact]
    public void HitGroundReapplyStaysDormantUntilFinalActivationTail()
    {
        using var fixture = new Fixture(residentWorld: true);
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (transition, phase, _, observed) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, -3f), Cell, isWater: false);
                }
                return observed;
            };
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        PlayerMovementController controller = fixture.Movement.Controller!;
        bool observedDormant = false;
        controller.BeginDormantSetPositionGroundPhase();
        controller.Motion.InterpretedState.ForwardCommand =
            MotionCommand.RunForward;
        controller.Motion.InterpretedState.ForwardSpeed = 1f;
        controller.Motion.DefaultSink = new CallbackMotionSink(() =>
        {
            observedDormant = true;
            Assert.False(fixture.Record.PhysicsBody!.InWorld);
            Assert.False(fixture.Record.PhysicsBody.TransientState
                .HasFlag(TransientStateFlags.Active));
            Assert.Null(fixture.Record.PhysicsHost);
            Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
            Assert.False(fixture.Record.ObjectClock.IsActive);
        });
        controller.EndDormantSetPositionGroundPhase();
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out var projection));

        Assert.True(observedDormant);
        Assert.True(projection.IsValid);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);
        Assert.NotNull(fixture.Record.PhysicsHost);
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.True(fixture.Record.ObjectClock.IsActive);
        Assert.True(controller.IsRuntimePublished);
    }

    [Fact]
    public void HitGroundInvalidationRetiresInstalledBatchBeforeDispatch()
    {
        using var fixture = new Fixture(residentWorld: true);
        RuntimeEntityRecord target = fixture.Lifetime.RegisterEntity(
            Spawn(0x70003003u, incarnation: 1)).Canonical!;
        fixture.Lifetime.Entities.SetFullCell(
            target, Cell, Cell & 0xFFFF0000u);
        fixture.Lifetime.Entities.SetFinalPhysicsState(
            target, PhysicsStateFlags.ReportCollisions);
        var targetBody = new PhysicsBody
        {
            Position = new Vector3(100f, 100f, 3f),
            Orientation = Quaternion.Identity,
            State = PhysicsStateFlags.ReportCollisions,
            InWorld = true,
            TransientState = TransientStateFlags.Active,
        };
        targetBody.SnapToCell(Cell, targetBody.Position, targetBody.Position);
        fixture.Lifetime.Entities.SetPhysicsBody(target, targetBody);
        fixture.Lifetime.Physics.AcknowledgeSpatialProjection(target, true);
        fixture.Lifetime.Physics.Engine.ShadowObjects.Register(
            target.Key!.Value.LocalEntityId, SetupId, targetBody.Position,
            Quaternion.Identity, 0.48f, 0f, 0f, Cell & 0xFFFF0000u,
            ShadowCollisionType.Sphere, (uint)targetBody.State, Cell,
            isStatic: false);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out var token));
        PhysicsBody ownerBody = fixture.Record.PhysicsBody!;
        ownerBody.InWorld = true;
        ownerBody.TransientState |= TransientStateFlags.Active;
        Assert.True(fixture.Lifetime.Physics.CollisionReports.HandleReports(
            fixture.Record, ownerBody, 9d, false, false, false,
            [target.Key.Value.LocalEntityId]));
        ownerBody.InWorld = false;
        ownerBody.TransientState &= ~TransientStateFlags.Active;
        Assert.Equal(1, fixture.Lifetime.Physics.CollisionReports
            .CaptureOwnership().TrackedObjectCount);
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (transition, phase, _, observed) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, -3f), Cell, isWater: false);
                }
                return observed;
            };
        PlayerMovementController controller = fixture.Movement.Controller!;
        bool invalidated = false;
        controller.BeginDormantSetPositionGroundPhase();
        controller.Motion.InterpretedState.ForwardCommand =
            MotionCommand.RunForward;
        controller.Motion.DefaultSink = new CallbackMotionSink(() =>
        {
            if (invalidated)
                return;
            invalidated = true;
            fixture.Lifetime.Entities.AdvanceObjDescAuthority(fixture.Record);
        });
        controller.EndDormantSetPositionGroundPhase();
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.RejectedAuthority,
            fixture.Owner.CommitActivation(evaluation, out _));

        Assert.True(invalidated);
        RuntimeCollisionReportingOwnershipSnapshot ownership = fixture.Lifetime
            .Physics.CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.TrackedObjectCount);
        Assert.Equal(0, ownership.ReversePeerCount);
        Assert.Equal(0, ownership.PendingSetPositionDispatchCount);
        Assert.Equal(0, fixture.Lifetime.Physics.Engine.ShadowObjects
            .PendingSetPositionDispatchCount);
        Assert.Equal(0, fixture.Lifetime.Physics.SetPosition
            .CaptureOwnership().ActiveOperationCount);
    }

    [Fact]
    public void HiddenNoDrawStateDoesNotCancelDormantActivation()
    {
        using var fixture = new Fixture(residentWorld: true);
        PhysicsStateFlags hidden = fixture.Record.FinalPhysicsState
            | PhysicsStateFlags.Hidden
            | PhysicsStateFlags.NoDraw;
        fixture.Lifetime.Entities.SetFinalPhysicsState(fixture.Record, hidden);
        fixture.RepreparePlacement();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));

        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out var projection));

        Assert.True(projection.IsValid);
        Assert.True(fixture.Record.PhysicsBody!.InWorld);
        Assert.True(fixture.Record.PhysicsBody.State.HasFlag(
            PhysicsStateFlags.Hidden));
        Assert.True(fixture.Record.PhysicsBody.State.HasFlag(
            PhysicsStateFlags.NoDraw));
        Assert.True(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
    }

    [Fact]
    public void RejectedPlacementReceiptIsPureAndRetryableUnderSameLease()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        PhysicsBody body = fixture.Record.PhysicsBody!;
        Vector3 position = body.Position;
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (_, _, _, _) => TransitionState.Collided;
        EvaluationPuritySnapshot before = CaptureEvaluationPurity(fixture);

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.RejectedPlacement,
            fixture.Owner.EvaluateActivation(token, out var rejected));
        AssertEvaluationPurity(before, CaptureEvaluationPurity(fixture));
        Assert.True(rejected.IsValid);
        Assert.True(fixture.Owner.IsEvaluationCurrent(rejected));
        Assert.False(body.InWorld);
        Assert.Equal(position, body.Position);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));

        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook = null;
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var retry));
        AssertEvaluationPurity(before, CaptureEvaluationPurity(fixture));
        Assert.True(fixture.Owner.IsEvaluationCurrent(retry));
        Assert.False(fixture.Owner.IsEvaluationCurrent(rejected));
    }

    [Fact]
    public void StaleActivationReceiptCannotPublishAndNextEvaluationRetiresDormantOwners()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var receipt));
        fixture.Lifetime.Entities.SetFinalPhysicsState(
            fixture.Record,
            fixture.Record.FinalPhysicsState | PhysicsStateFlags.Frozen);

        Assert.False(fixture.Owner.IsEvaluationCurrent(receipt));
        PhysicsBody staleBody = fixture.Record.PhysicsBody!;
        Assert.False(staleBody.InWorld);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.False(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out _));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.RejectedAuthority,
            fixture.Owner.EvaluateActivation(token, out _));
        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(Cell, fixture.Record.FullCellId);
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.Equal(0, fixture.Lifetime.Physics.CaptureOwnership()
            .RetainedShadowRegistrationCount);
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
    }

    [Fact]
    public void CollisionAdmissionRejectsEvaluationUntilReplacementCommits()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var receipt));
        PhysicsBody body = fixture.Record.PhysicsBody!;

        RuntimeCollisionAdmission admission = fixture.Lifetime.Physics
            .BeginCollisionAdmission(Cell & 0xFFFF0000u);

        Assert.False(fixture.Owner.IsEvaluationCurrent(receipt));
        Assert.False(body.InWorld);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Active));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out _));
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);

        using PreparedLandblockCollisionGeneration prepared = fixture
            .Lifetime.Physics.PrepareCollisionGeneration(admission);
        fixture.Lifetime.Physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(Cell & 0xFFFF0000u));
        Assert.True(CommitPrepared(
            fixture.Lifetime.Physics,
            admission,
            prepared).Committed);

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var replacement));
        Assert.True(fixture.Owner.IsEvaluationCurrent(replacement));
    }

    [Fact]
    public void ReentrantCollisionWorldMutationRejectsOtherwiseCurrentEvaluation()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        bool mutated = false;
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (_, phase, _, observed) =>
            {
                if (!mutated
                    && phase is TransitionCellCollisionPhase.Environment)
                {
                    mutated = true;
                    RuntimeCollisionAdmission admission = fixture.Lifetime
                        .Physics.BeginCollisionAdmission(0x01010000u);
                    fixture.Lifetime.Physics.CancelCollisionGeneration(
                        admission);
                }
                return observed;
            };

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out _));
        Assert.True(mutated);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);

        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook = null;
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var replacement));
        Assert.True(fixture.Owner.IsEvaluationCurrent(replacement));
    }

    [Fact]
    public void ReentrantCollisionGenerationCommitRejectsOtherwiseCurrentEvaluation()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));

        RuntimeCollisionAdmission admission = fixture.Lifetime.Physics
            .BeginCollisionAdmission(0x01010000u);
        using PreparedLandblockCollisionGeneration prepared = fixture
            .Lifetime.Physics.PrepareCollisionGeneration(admission);
        fixture.Lifetime.Physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(0x01010000u));
        bool committed = false;
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (_, phase, _, observed) =>
            {
                if (!committed
                    && phase is TransitionCellCollisionPhase.Environment)
                {
                    committed = CommitPrepared(
                        fixture.Lifetime.Physics,
                        admission,
                        prepared).Committed;
                }
                return observed;
            };

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out _));
        Assert.True(committed);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);

        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook = null;
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var replacement));
        Assert.True(fixture.Owner.IsEvaluationCurrent(replacement));
    }

    [Theory]
    [InlineData(ActivationInvalidation.Position)]
    [InlineData(ActivationInvalidation.Vector)]
    [InlineData(ActivationInvalidation.ObjectDescription)]
    [InlineData(ActivationInvalidation.Create)]
    [InlineData(ActivationInvalidation.Identity)]
    [InlineData(ActivationInvalidation.Body)]
    [InlineData(ActivationInvalidation.Controller)]
    [InlineData(ActivationInvalidation.Clock)]
    [InlineData(ActivationInvalidation.Spatial)]
    [InlineData(ActivationInvalidation.Host)]
    [InlineData(ActivationInvalidation.Remote)]
    [InlineData(ActivationInvalidation.Projectile)]
    [InlineData(ActivationInvalidation.PlacementCancellation)]
    public void EveryPostOwnershipAuthorityReplacementInvalidatesEvaluation(
        ActivationInvalidation invalidation)
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var receipt));

        switch (invalidation)
        {
            case ActivationInvalidation.Position:
                Assert.True(fixture.Lifetime.Physics.SetPosition
                    .BeginAuthoredPlacement(
                        fixture.Record,
                        fixture.Record.PositionAuthorityVersion,
                        RuntimeSetPositionOperationKind.LocalAuthoritative)
                    .IsValid);
                break;
            case ActivationInvalidation.Vector:
                fixture.Lifetime.Entities.AdvanceVectorAuthority(fixture.Record);
                break;
            case ActivationInvalidation.ObjectDescription:
                fixture.Lifetime.Entities.AdvanceObjDescAuthority(fixture.Record);
                break;
            case ActivationInvalidation.Create:
                fixture.Lifetime.Entities.AdvanceCreateAuthority(fixture.Record);
                break;
            case ActivationInvalidation.Identity:
                fixture.Identity.ServerGuid++;
                break;
            case ActivationInvalidation.Body:
                fixture.Lifetime.Entities.SetPhysicsBody(
                    fixture.Record,
                    new PhysicsBody());
                break;
            case ActivationInvalidation.Controller:
                fixture.Movement.Controller = new PlayerMovementController(
                    new PhysicsEngine());
                break;
            case ActivationInvalidation.Clock:
                fixture.Lifetime.Entities.SuspendObjectClock(fixture.Record);
                break;
            case ActivationInvalidation.Spatial:
                fixture.Lifetime.Entities.SetFullCell(
                    fixture.Record,
                    Cell + 1u,
                    (Cell & 0xFFFF0000u) | 0xFFFFu);
                break;
            case ActivationInvalidation.Host:
                fixture.Lifetime.Entities.SetPhysicsHost(
                    fixture.Record,
                    CreatePhysicsHost(fixture.Record.ServerGuid));
                break;
            case ActivationInvalidation.Remote:
                fixture.Lifetime.Entities.SetRemoteMotion(
                    fixture.Record,
                    new RemoteMotion());
                break;
            case ActivationInvalidation.Projectile:
                fixture.Lifetime.Entities.SetProjectile(
                    fixture.Record,
                    new RuntimeProjectile(
                        new PhysicsBody(),
                        new ProjectileCollisionSphere(
                            Vector3.Zero,
                            0.1f)));
                break;
            case ActivationInvalidation.PlacementCancellation:
                Assert.True(fixture.Lifetime.Physics.SetPosition.Cancel(
                    fixture.Record,
                    publishWithdrawal: false));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation));
        }

        Assert.False(fixture.Owner.IsEvaluationCurrent(receipt));
        Assert.False(fixture.Lifetime.Physics.IsSpatialRoot(fixture.Record));
        Assert.False(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out _));
    }

    [Fact]
    public void ShadowInsertMoveStateSuspendAndRemoveInvalidateReceipts()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var emptyWorld));
        Assert.True(fixture.Owner.IsEvaluationCurrent(emptyWorld));
        uint queriedCell = emptyWorld.Placement.Result.QueriedCellIds[0];
        const uint ownerId = 0x7000F001u;
        Vector3 far = new(100f, 100f, 0f);

        fixture.Lifetime.Physics.Engine.ShadowObjects.Register(
            ownerId,
            SetupId,
            far,
            Quaternion.Identity,
            0.25f,
            0f,
            0f,
            queriedCell & 0xFFFF0000u,
            ShadowCollisionType.Cylinder,
            cylHeight: 1f,
            seedCellId: queriedCell,
            isStatic: false);
        Assert.False(fixture.Owner.IsEvaluationCurrent(emptyWorld));

        RuntimeLocalPlayerPhysicsActivationReceipt current = Reevaluate();
        fixture.Lifetime.Physics.Engine.ShadowObjects.UpdatePosition(
            ownerId,
            far + Vector3.One,
            Quaternion.Identity,
            0f,
            0f,
            queriedCell & 0xFFFF0000u,
            queriedCell);
        Assert.False(fixture.Owner.IsEvaluationCurrent(current));

        current = Reevaluate();
        fixture.Lifetime.Physics.Engine.ShadowObjects.UpdatePhysicsState(
            ownerId,
            (uint)PhysicsStateFlags.Ethereal);
        Assert.False(fixture.Owner.IsEvaluationCurrent(current));

        current = Reevaluate();
        Assert.True(fixture.Lifetime.Physics.Engine.ShadowObjects.Suspend(
            ownerId));
        Assert.False(fixture.Owner.IsEvaluationCurrent(current));

        current = Reevaluate();
        fixture.Lifetime.Physics.Engine.ShadowObjects.Deregister(ownerId);
        Assert.False(fixture.Owner.IsEvaluationCurrent(current));

        RuntimeLocalPlayerPhysicsActivationReceipt Reevaluate()
        {
            Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
                fixture.Owner.EvaluateActivation(token, out var next));
            Assert.True(fixture.Owner.IsEvaluationCurrent(next));
            return next;
        }
    }

    [Theory]
    [InlineData(RestrictionObjectMutation.RemoveHouseObject)]
    [InlineData(RestrictionObjectMutation.HouseOwnerProperty)]
    [InlineData(RestrictionObjectMutation.HouseGuestList)]
    [InlineData(RestrictionObjectMutation.MoverMonarch)]
    public void RestrictionObjectMutationInvalidatesEvaluatedReceipt(
        RestrictionObjectMutation mutation)
    {
        using var fixture = new Fixture(residentWorld: true);
        SeedRestrictionObjects(fixture);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var receipt));

        ApplyRestrictionObjectMutation(fixture, mutation);

        Assert.False(fixture.Owner.IsEvaluationCurrent(receipt));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var replacement));
        Assert.True(fixture.Owner.IsEvaluationCurrent(replacement));
    }

    [Theory]
    [InlineData(RestrictionObjectMutation.RemoveHouseObject)]
    [InlineData(RestrictionObjectMutation.HouseOwnerProperty)]
    [InlineData(RestrictionObjectMutation.HouseGuestList)]
    [InlineData(RestrictionObjectMutation.MoverMonarch)]
    public void ReentrantRestrictionObjectMutationAbortsEvaluation(
        RestrictionObjectMutation mutation)
    {
        using var fixture = new Fixture(residentWorld: true);
        SeedRestrictionObjects(fixture);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        bool mutated = false;
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (_, phase, _, observed) =>
            {
                if (!mutated
                    && phase is TransitionCellCollisionPhase.Environment)
                {
                    mutated = true;
                    ApplyRestrictionObjectMutation(fixture, mutation);
                }
                return observed;
            };

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.DeferredCell,
            fixture.Owner.EvaluateActivation(token, out _));
        Assert.True(mutated);
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);

        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook = null;
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var replacement));
        Assert.True(fixture.Owner.IsEvaluationCurrent(replacement));
    }

    [Fact]
    public void ObjectTableNullFreshAndEqualRevisionAbaInvalidateReceipt()
    {
        using var fixture = new Fixture(residentWorld: true);
        SeedRestrictionObjects(fixture);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var originalReceipt));
        ClientObjectTable original = fixture.Lifetime.Objects;

        fixture.Lifetime.Physics.Engine.Objects = null;
        Assert.False(fixture.Owner.IsEvaluationCurrent(originalReceipt));

        // Returning to the same reference/revision is an ABA-shaped binding
        // replacement. The engine binding authority keeps the old receipt
        // stale even though object identity and mutation revision match again.
        fixture.Lifetime.Physics.Engine.Objects = original;
        Assert.False(fixture.Owner.IsEvaluationCurrent(originalReceipt));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var reboundReceipt));

        var fresh = new ClientObjectTable();
        fresh.AddOrUpdate(new ClientObject { ObjectId = 0x70003F10u });
        fresh.AddOrUpdate(new ClientObject { ObjectId = 0x70003F11u });
        Assert.Equal(original.MutationRevision, fresh.MutationRevision);
        fixture.Lifetime.Physics.Engine.Objects = fresh;

        Assert.False(fixture.Owner.IsEvaluationCurrent(reboundReceipt));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var freshReceipt));
        Assert.True(fixture.Owner.IsEvaluationCurrent(freshReceipt));
    }

    [Fact]
    public void HouseRestrictionGuestsAreAnImmutableInputSnapshot()
    {
        var sourceGuests = new Dictionary<uint, uint>();
        var restrictions = new HouseRestrictionRecord(
            OpenToPublic: false,
            AllegianceMonarchId: 0u,
            Guests: sourceGuests);
        var table = new ClientObjectTable();
        var house = new ClientObject
        {
            ObjectId = RestrictionObjectId,
            Restrictions = restrictions,
        };
        table.AddOrUpdate(house);
        ulong retainedRevision = table.MutationRevision;

        sourceGuests[0x70003F20u] = 1u;

        Assert.False(restrictions.IsAllowedIn(0x70003F20u, 0u));
        if (restrictions.Guests is IDictionary<uint, uint> dictionaryView)
        {
            Assert.Throws<NotSupportedException>(
                () => dictionaryView[0x70003F20u] = 1u);
        }
        Assert.False(restrictions.IsAllowedIn(0x70003F20u, 0u));
        Assert.Equal(retainedRevision, table.MutationRevision);
    }

    [Fact]
    public void RemovalAndClearUnbindDirectRestrictionMutationAuthority()
    {
        var table = new ClientObjectTable();
        var removed = new ClientObject { ObjectId = RestrictionObjectId };
        table.AddOrUpdate(removed);
        Assert.True(table.Remove(removed.ObjectId));
        ulong removedRevision = table.MutationRevision;

        removed.HouseOwnerId = 0x70003F21u;
        Assert.Equal(removedRevision, table.MutationRevision);

        var cleared = new ClientObject { ObjectId = 0x70003F22u };
        table.AddOrUpdate(cleared);
        table.Clear();
        ulong clearedRevision = table.MutationRevision;

        cleared.MonarchId = 0x70003F23u;
        Assert.Equal(clearedRevision, table.MutationRevision);
    }

    [Fact]
    public void AddOrUpdateReplacementTransfersDirectMutationAuthority()
    {
        var table = new ClientObjectTable();
        var displaced = new ClientObject { ObjectId = RestrictionObjectId };
        var replacement = new ClientObject { ObjectId = RestrictionObjectId };
        table.AddOrUpdate(displaced);
        table.AddOrUpdate(replacement);
        ulong replacementRevision = table.MutationRevision;

        displaced.MonarchId = 0x70003F24u;
        Assert.Equal(replacementRevision, table.MutationRevision);

        replacement.MonarchId = 0x70003F25u;
        Assert.True(table.MutationRevision > replacementRevision);
    }

    [Fact]
    public void SharedRetainedObjectNotifiesEveryOwningTable()
    {
        var first = new ClientObjectTable();
        var second = new ClientObjectTable();
        var shared = new ClientObject { ObjectId = RestrictionObjectId };
        first.AddOrUpdate(shared);
        second.AddOrUpdate(shared);
        ulong firstRevision = first.MutationRevision;
        ulong secondRevision = second.MutationRevision;

        shared.HouseOwnerId = 0x70003F26u;

        Assert.True(first.MutationRevision > firstRevision);
        Assert.True(second.MutationRevision > secondRevision);
    }

    [Theory]
    [InlineData(ReentrantActivationInvalidation.Reset)]
    [InlineData(ReentrantActivationInvalidation.DeleteAndGuidReuse)]
    public void ReentrantInvalidationDuringCoreEvaluationRetiresLease(
        ReentrantActivationInvalidation invalidation)
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        RuntimeEntityRecord? replacement = null;
        bool invalidated = false;
        fixture.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (_, phase, _, observed) =>
            {
                if (invalidated
                    || phase is not TransitionCellCollisionPhase.Environment)
                {
                    return observed;
                }
                invalidated = true;
                if (invalidation is ReentrantActivationInvalidation.Reset)
                {
                    fixture.Movement.ResetSession();
                }
                else
                {
                    uint guid = fixture.Record.ServerGuid;
                    Assert.True(fixture.Lifetime.TryAcceptDelete(
                        new DeleteObject.Parsed(
                            guid,
                            fixture.Record.Incarnation),
                        isLocalPlayer: false,
                        removeRetainedObject: false,
                        out RuntimeEntityDeleteAcceptance acceptance));
                    fixture.Lifetime.CompleteAcceptedDelete(acceptance);
                    Assert.Null(fixture.Lifetime.RetireCanonicalOnly(
                        fixture.Record));
                    replacement = fixture.Lifetime.RegisterEntity(
                        Spawn(guid, incarnation: 2)).Canonical!;
                }
                return observed;
            };

        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.RejectedAuthority,
            fixture.Owner.EvaluateActivation(token, out _));

        Assert.True(invalidated);
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
        Assert.Null(fixture.Movement.Controller);
        Assert.Null(replacement?.PhysicsBody);
    }

    [Fact]
    public void ExistingActivationLeaseCannotBeOverwrittenAfterExternalClear()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        fixture.Lifetime.Entities.SetPhysicsBody(fixture.Record, null);
        fixture.Movement.Controller = null;

        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority,
            fixture.Owner.Prepare(
                fixture.Record,
                fixture.Placement,
                fixture.Command,
                PlayerMovementConstructionOptions.Fallback,
                fixture.ActivationPreparation,
                out _));
        Assert.Equal(1, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);

        Assert.True(fixture.Owner.DiscardActivation(token));
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
        fixture.RepreparePlacement();
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Prepared,
            fixture.Owner.Prepare(
                fixture.Record,
                fixture.Placement,
                fixture.Command,
                PlayerMovementConstructionOptions.Fallback,
                fixture.ActivationPreparation,
                out _));
    }

    [Fact]
    public void ResetRetiresPendingActivationAndConvergesItsOwnershipLedger()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out _));

        fixture.Movement.ResetSession();

        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(0, fixture.Owner.CaptureOwnership()
            .PendingActivationCount);
    }

    [Fact]
    public void ExplicitDiscardAndDisposeRetirePendingActivationExactlyOnce()
    {
        var fixture = new Fixture(residentWorld: true);
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.True(fixture.Owner.DiscardActivation(token));
        Assert.False(fixture.Owner.DiscardActivation(token));
        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);

        fixture.RepreparePlacement();
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare(), out _));
        fixture.Owner.Dispose();
        Assert.True(fixture.Owner.CaptureOwnership().IsConverged);
        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        fixture.Dispose();
    }

    [Theory]
    [InlineData(PublicationInvalidation.SetPositionReplacement)]
    [InlineData(PublicationInvalidation.Vector)]
    [InlineData(PublicationInvalidation.FinalState)]
    [InlineData(PublicationInvalidation.ObjectDescription)]
    [InlineData(PublicationInvalidation.Create)]
    [InlineData(PublicationInvalidation.Remote)]
    [InlineData(PublicationInvalidation.Projectile)]
    [InlineData(PublicationInvalidation.Body)]
    [InlineData(PublicationInvalidation.Clock)]
    [InlineData(PublicationInvalidation.Controller)]
    [InlineData(PublicationInvalidation.CancelPlacement)]
    public void EveryNestedAuthorityReplacementDiscardsCandidateWithoutRollback(
        PublicationInvalidation invalidation)
    {
        using var fixture = new Fixture();
        RuntimeLocalPlayerPhysicsPublicationToken token = fixture.Prepare();
        PhysicsBody? replacementBody = null;
        PlayerMovementController? replacementController = null;

        switch (invalidation)
        {
            case PublicationInvalidation.SetPositionReplacement:
                Assert.True(fixture.Lifetime.Physics.SetPosition
                    .BeginAuthoredPlacement(
                        fixture.Record,
                        fixture.Record.PositionAuthorityVersion,
                        RuntimeSetPositionOperationKind.LocalAuthoritative)
                    .IsValid);
                break;
            case PublicationInvalidation.Vector:
                fixture.Lifetime.Entities.AdvanceVectorAuthority(fixture.Record);
                break;
            case PublicationInvalidation.FinalState:
                fixture.Lifetime.Entities.SetFinalPhysicsState(
                    fixture.Record,
                    fixture.Record.FinalPhysicsState | PhysicsStateFlags.Frozen);
                break;
            case PublicationInvalidation.ObjectDescription:
                fixture.Lifetime.Entities.AdvanceObjDescAuthority(fixture.Record);
                break;
            case PublicationInvalidation.Create:
                fixture.Lifetime.Entities.AdvanceCreateAuthority(fixture.Record);
                break;
            case PublicationInvalidation.Remote:
                fixture.Lifetime.Entities.SetRemoteMotion(
                    fixture.Record,
                    new RemoteMotion());
                break;
            case PublicationInvalidation.Projectile:
                fixture.Lifetime.Entities.SetProjectile(
                    fixture.Record,
                    new RuntimeProjectile(
                        new PhysicsBody(),
                        new ProjectileCollisionSphere(Vector3.Zero, 0.1f)));
                break;
            case PublicationInvalidation.Body:
                replacementBody = new PhysicsBody();
                fixture.Lifetime.Entities.SetPhysicsBody(
                    fixture.Record,
                    replacementBody);
                break;
            case PublicationInvalidation.Clock:
                fixture.Lifetime.Entities.SuspendObjectClock(fixture.Record);
                break;
            case PublicationInvalidation.Controller:
                replacementController = new PlayerMovementController(
                    new PhysicsEngine());
                fixture.Movement.Controller = replacementController;
                break;
            case PublicationInvalidation.CancelPlacement:
                Assert.True(fixture.Lifetime.Physics.SetPosition.Cancel(
                    fixture.Record,
                    publishWithdrawal: false));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation));
        }

        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority,
            fixture.Owner.Commit(token));
        Assert.Equal(0, fixture.Owner.CaptureOwnership().CandidateCount);
        if (replacementBody is not null)
            Assert.Same(replacementBody, fixture.Record.PhysicsBody);
        if (replacementController is not null)
            Assert.Same(replacementController, fixture.Movement.Controller);
    }

    [Fact]
    public void DeleteAndGuidReuseCannotPublishRetiredCandidate()
    {
        using var fixture = new Fixture();
        RuntimeLocalPlayerPhysicsPublicationToken token = fixture.Prepare();
        uint guid = fixture.Record.ServerGuid;
        Assert.True(fixture.Lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(guid, fixture.Record.Incarnation),
            isLocalPlayer: false,
            removeRetainedObject: false,
            out RuntimeEntityDeleteAcceptance acceptance));
        fixture.Lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(fixture.Lifetime.RetireCanonicalOnly(fixture.Record));
        RuntimeEntityRecord replacement = fixture.Lifetime.RegisterEntity(
            Spawn(guid, incarnation: 2)).Canonical!;

        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority,
            fixture.Owner.Commit(token));
        Assert.Null(replacement.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
        Assert.Equal(0, fixture.Owner.CaptureOwnership().CandidateCount);
    }

    [Fact]
    public void ResetAndDisposeDiscardCandidatesAndConvergeOwnership()
    {
        var fixture = new Fixture();
        _ = fixture.Prepare();
        fixture.Movement.ResetSession();
        Assert.Equal(0, fixture.Owner.CaptureOwnership().CandidateCount);

        fixture.RepreparePlacement();
        _ = fixture.Prepare();
        fixture.Movement.Dispose();
        Assert.True(fixture.Movement.CaptureOwnership().IsConverged);
        fixture.DisposeLifetimeOnly();
    }

    [Theory]
    [InlineData(PristineViolation.Body)]
    [InlineData(PristineViolation.Controller)]
    [InlineData(PristineViolation.Host)]
    [InlineData(PristineViolation.BodyAcquisition)]
    [InlineData(PristineViolation.Remote)]
    [InlineData(PristineViolation.RemoteBinding)]
    [InlineData(PristineViolation.Projectile)]
    [InlineData(PristineViolation.ProjectileBinding)]
    [InlineData(PristineViolation.RemotePlacement)]
    public void PreparationRejectsEveryNonPristineOwnershipGraph(
        PristineViolation violation)
    {
        using var fixture = new Fixture();
        switch (violation)
        {
            case PristineViolation.Body:
            {
                var activeBody = new PhysicsBody
                {
                    TransientState = TransientStateFlags.Active,
                };
                fixture.Lifetime.Entities.SetPhysicsBody(
                    fixture.Record,
                    activeBody);
                break;
            }
            case PristineViolation.Controller:
                fixture.Movement.Controller = new PlayerMovementController(
                    new PhysicsEngine());
                break;
            case PristineViolation.Host:
                fixture.Lifetime.Entities.SetPhysicsHost(
                    fixture.Record,
                    CreatePhysicsHost(fixture.Record.ServerGuid));
                break;
            case PristineViolation.BodyAcquisition:
                fixture.Lifetime.Entities.SetPhysicsBodyAcquisitionInProgress(
                    fixture.Record,
                    true);
                break;
            case PristineViolation.Remote:
                fixture.Lifetime.Entities.SetRemoteMotion(
                    fixture.Record,
                    new RemoteMotion());
                break;
            case PristineViolation.RemoteBinding:
                fixture.Lifetime.Entities.SetRemoteMotionBindingInProgress(
                    fixture.Record,
                    true);
                break;
            case PristineViolation.Projectile:
                fixture.Lifetime.Entities.SetProjectile(
                    fixture.Record,
                    new RuntimeProjectile(
                        new PhysicsBody(),
                        new ProjectileCollisionSphere(Vector3.Zero, 0.1f)));
                break;
            case PristineViolation.ProjectileBinding:
                fixture.Lifetime.Entities.SetProjectileBindingInProgress(
                    fixture.Record,
                    true);
                break;
            case PristineViolation.RemotePlacement:
                fixture.Lifetime.Entities.SetRequiresRemotePlacementRuntime(
                    fixture.Record,
                    true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(violation));
        }

        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority,
            fixture.Owner.Prepare(
                fixture.Record,
                fixture.Placement,
                fixture.Command,
                PlayerMovementConstructionOptions.Fallback,
                fixture.ActivationPreparation,
                out RuntimeLocalPlayerPhysicsPublicationToken token));
        Assert.False(token.IsValid);
        Assert.Equal(0, fixture.Owner.CaptureOwnership().CandidateCount);
    }

    [Fact]
    public void PreparationRejectsRecordWhichIsNotTheExactLocalIdentity()
    {
        using var fixture = new Fixture();
        fixture.Identity.ServerGuid = 0x70003002u;

        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority,
            fixture.Owner.Prepare(
                fixture.Record,
                fixture.Placement,
                fixture.Command,
                PlayerMovementConstructionOptions.Fallback,
                fixture.ActivationPreparation,
                out _));
    }

    [Fact]
    public void PreparationRejectsDisposedLocalIdentity()
    {
        using var fixture = new Fixture();
        fixture.Identity.Dispose();

        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority,
            fixture.Owner.Prepare(
                fixture.Record,
                fixture.Placement,
                fixture.Command,
                PlayerMovementConstructionOptions.Fallback,
                fixture.ActivationPreparation,
                out _));
    }

    [Fact]
    public void IdentityRevisionSwitchRejectsPreparedCandidate()
    {
        using var fixture = new Fixture();
        RuntimeLocalPlayerPhysicsPublicationToken token = fixture.Prepare();
        uint guid = fixture.Identity.ServerGuid;
        fixture.Identity.ServerGuid = guid + 1u;
        fixture.Identity.ServerGuid = guid;

        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.RejectedAuthority,
            fixture.Owner.Commit(token));
        Assert.Null(fixture.Record.PhysicsBody);
        Assert.Null(fixture.Movement.Controller);
    }

    [Theory]
    [InlineData(PublishedRetirement.Replacement)]
    [InlineData(PublishedRetirement.Reset)]
    [InlineData(PublishedRetirement.Dispose)]
    public void RuntimeOwnedControllerRejectsStaleOperationsAfterRetirement(
        PublishedRetirement retirement)
    {
        using var fixture = new Fixture();
        Assert.Equal(
            RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(fixture.Prepare()));
        PlayerMovementController stale = fixture.Movement.Controller!;

        switch (retirement)
        {
            case PublishedRetirement.Replacement:
                fixture.Movement.Controller = new PlayerMovementController(
                    new PhysicsEngine());
                break;
            case PublishedRetirement.Reset:
                fixture.Movement.ResetSession();
                break;
            case PublishedRetirement.Dispose:
                fixture.Movement.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(retirement));
        }

        AssertNotLive(stale);
    }

    [Fact]
    public void BodyAndControllerEpochsAdvanceOnlyOnOwnershipChanges()
    {
        using var fixture = new Fixture(preparePlacement: false);
        var firstBody = new PhysicsBody();
        var secondBody = new PhysicsBody();
        ulong bodyEpoch = fixture.Record.PhysicsOwnershipEpoch;
        fixture.Lifetime.Entities.SetPhysicsBody(fixture.Record, firstBody);
        Assert.Equal(bodyEpoch + 1UL, fixture.Record.PhysicsOwnershipEpoch);
        fixture.Lifetime.Entities.SetPhysicsBody(fixture.Record, firstBody);
        Assert.Equal(bodyEpoch + 1UL, fixture.Record.PhysicsOwnershipEpoch);
        fixture.Lifetime.Entities.SetPhysicsBody(fixture.Record, secondBody);
        Assert.Equal(bodyEpoch + 2UL, fixture.Record.PhysicsOwnershipEpoch);
        fixture.Lifetime.Entities.SetPhysicsBody(fixture.Record, null);
        Assert.Equal(bodyEpoch + 3UL, fixture.Record.PhysicsOwnershipEpoch);

        var first = new PlayerMovementController(new PhysicsEngine());
        var second = new PlayerMovementController(new PhysicsEngine());
        ulong controllerEpoch = fixture.Movement.ControllerOwnershipEpoch;
        fixture.Movement.Controller = first;
        Assert.Equal(controllerEpoch + 1UL,
            fixture.Movement.ControllerOwnershipEpoch);
        fixture.Movement.Controller = first;
        Assert.Equal(controllerEpoch + 1UL,
            fixture.Movement.ControllerOwnershipEpoch);
        fixture.Movement.Controller = second;
        Assert.Equal(controllerEpoch + 2UL,
            fixture.Movement.ControllerOwnershipEpoch);
        fixture.Movement.Controller = null;
        Assert.Equal(controllerEpoch + 3UL,
            fixture.Movement.ControllerOwnershipEpoch);
    }

    private static EvaluationPuritySnapshot CaptureEvaluationPurity(
        Fixture fixture)
    {
        PhysicsBody body = fixture.Record.PhysicsBody!;
        bool hasAwaiting = fixture.Lifetime.Physics.SetPosition
            .TryGetAwaitingPreparationToken(
                fixture.Record,
                out RuntimeEntityPlacementToken awaiting);
        return new EvaluationPuritySnapshot(
            fixture.Lifetime.Physics.CaptureOwnership(),
            fixture.Lifetime.Physics.SetPosition.CaptureOwnership(),
            fixture.Lifetime.Physics.CollisionReports.CaptureOwnership(),
            fixture.Lifetime.Physics.CollisionWorldAuthority,
            fixture.Lifetime.Physics.ShadowWorldAuthority,
            fixture.Lifetime.Physics.ObjectTable,
            fixture.Lifetime.Physics.ObjectTableBindingAuthority,
            fixture.Lifetime.Physics.ObjectTableAuthority,
            hasAwaiting,
            awaiting,
            fixture.Lifetime.Physics.SetPosition
                .IsExactPreparedPlacementCurrent(
                    fixture.Record,
                    fixture.Placement,
                    fixture.Command),
            fixture.Record.FullCellId,
            fixture.Record.SpatialAuthorityVersion,
            fixture.Record.PlacementCommitVersion,
            fixture.Record.ObjectClockEpoch,
            fixture.Record.ObjectClock.PendingSeconds,
            fixture.Record.ObjectClock.IsActive,
            CaptureBody(body),
            CaptureVectorBits(body.WalkableVertices));
    }

    private static void AssertEvaluationPurity(
        in EvaluationPuritySnapshot expected,
        in EvaluationPuritySnapshot actual)
    {
        Assert.Equal(expected.PhysicsOwnership, actual.PhysicsOwnership);
        Assert.Equal(expected.SetPositionOwnership, actual.SetPositionOwnership);
        Assert.Equal(expected.CollisionOwnership, actual.CollisionOwnership);
        Assert.Equal(expected.CollisionWorldAuthority,
            actual.CollisionWorldAuthority);
        Assert.Equal(expected.ShadowWorldAuthority,
            actual.ShadowWorldAuthority);
        Assert.Same(expected.ObjectTable, actual.ObjectTable);
        Assert.Equal(expected.ObjectTableBindingAuthority,
            actual.ObjectTableBindingAuthority);
        Assert.Equal(expected.ObjectTableAuthority,
            actual.ObjectTableAuthority);
        Assert.Equal(expected.HasAwaitingPlacement,
            actual.HasAwaitingPlacement);
        Assert.Equal(expected.AwaitingPlacement, actual.AwaitingPlacement);
        Assert.Equal(expected.ExactPlacementCurrent,
            actual.ExactPlacementCurrent);
        Assert.Equal(expected.FullCellId, actual.FullCellId);
        Assert.Equal(expected.SpatialAuthorityVersion,
            actual.SpatialAuthorityVersion);
        Assert.Equal(expected.PlacementCommitVersion,
            actual.PlacementCommitVersion);
        Assert.Equal(expected.ObjectClockEpoch, actual.ObjectClockEpoch);
        Assert.Equal(expected.ObjectClockPending,
            actual.ObjectClockPending);
        Assert.Equal(expected.ObjectClockActive, actual.ObjectClockActive);
        Assert.Equal(expected.Body, actual.Body);
        Assert.True(expected.WalkableVertexBits.AsSpan().SequenceEqual(
            actual.WalkableVertexBits.AsSpan()));
    }

    private static BodyPuritySnapshot CaptureBody(PhysicsBody body) => new(
        body.Position,
        body.CellPosition,
        body.InWorld,
        body.Orientation,
        body.Velocity,
        body.CachedVelocity,
        body.FramesStationaryFall,
        body.Acceleration,
        body.Omega,
        body.GroundNormal,
        body.SlidingNormal,
        body.ContactPlaneValid,
        body.ContactPlane,
        body.ContactPlaneCellId,
        body.ContactPlaneIsWater,
        body.WalkablePolygonValid,
        body.WalkablePlane,
        body.WalkableUp,
        body.Elasticity,
        body.Friction,
        body.State,
        body.TransientState,
        body.LastUpdateTime,
        body.IsFullyConstrained,
        body.LastMoveWasAutonomous);

    private static ImmutableArray<uint> CaptureVectorBits(
        Vector3[]? vectors)
    {
        if (vectors is null)
            return ImmutableArray<uint>.Empty;
        var result = ImmutableArray.CreateBuilder<uint>(vectors.Length * 3);
        foreach (Vector3 vector in vectors)
        {
            result.Add(BitConverter.SingleToUInt32Bits(vector.X));
            result.Add(BitConverter.SingleToUInt32Bits(vector.Y));
            result.Add(BitConverter.SingleToUInt32Bits(vector.Z));
        }
        return result.ToImmutable();
    }

    [Fact]
    public void PublishedLocalPlayerMoveToResolvesUninstalledTargetsThroughTheBoundObjectTableResolver()
    {
        using var fixture = new Fixture(residentWorld: true);
        Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Committed,
            fixture.Owner.Commit(
                fixture.Prepare(),
                out RuntimeLocalPlayerPhysicsActivationToken token));
        Assert.Equal(RuntimeLocalPlayerPhysicsActivationStatus.Evaluated,
            fixture.Owner.EvaluateActivation(token, out var evaluation));
        Assert.Equal(RuntimeDormantSetPositionCommitStatus.Committed,
            fixture.Owner.CommitActivation(evaluation, out _));
        Assert.NotNull(fixture.Record.PhysicsHost);
        MovementManager movement = fixture.Movement.Controller!.Movement;
        MoveToManager moveTo = movement.MoveTo!;

        const uint vendorGuid = 0x7C95B01Cu;
        Vector3 vendorPos =
            fixture.Record.PhysicsBody!.Position + new Vector3(10f, 0f, 0f);
        MovementStruct Approach() => new()
        {
            ObjectId = vendorGuid,
            TopLevelId = vendorGuid,
            Pos = new AcDream.Core.Physics.Position(
                Cell, vendorPos, Quaternion.Identity),
            Params = new MovementParameters
            {
                DistanceToObject = 3f,
                CanCharge = true,
            },
            Type = MovementType.MoveToObject,
            Radius = 0.5f,
            Height = 2f,
        };

        Assert.Equal(WeenieError.None, movement.PerformMovement(Approach()));
        Assert.True(moveTo.IsMovingTo());
        Assert.False(moveTo.Initialized);
        Assert.Empty(moveTo.PendingActions);
        moveTo.CancelMoveTo(WeenieError.ActionCancelled);

        var vendorHost = new EntityPhysicsHost(
            vendorGuid,
            getPosition: () => new AcDream.Core.Physics.Position(
                Cell, vendorPos, Quaternion.Identity),
            getVelocity: static () => Vector3.Zero,
            getRadius: static () => 0.5f,
            inContact: static () => true,
            minterpMaxSpeed: static () => null,
            curTime: static () => 0d,
            physicsTimerTime: static () => 0d,
            getObjectA: static _ => null,
            handleUpdateTarget: static _ => { },
            interruptCurrentMovement: static () => { });
        fixture.Lifetime.Physics.BindObjectTableHostResolver(
            guid => guid == vendorGuid ? vendorHost : null);

        Assert.Equal(WeenieError.None, movement.PerformMovement(Approach()));
        Assert.True(moveTo.IsMovingTo());
        Assert.True(moveTo.Initialized);
        Assert.NotEmpty(moveTo.PendingActions);
        moveTo.CancelMoveTo(WeenieError.ActionCancelled);
    }

    private readonly record struct EvaluationPuritySnapshot(
        RuntimePhysicsOwnershipSnapshot PhysicsOwnership,
        RuntimeSetPositionOwnershipSnapshot SetPositionOwnership,
        RuntimeCollisionReportingOwnershipSnapshot CollisionOwnership,
        ulong CollisionWorldAuthority,
        ulong ShadowWorldAuthority,
        ClientObjectTable? ObjectTable,
        ulong ObjectTableBindingAuthority,
        ulong ObjectTableAuthority,
        bool HasAwaitingPlacement,
        RuntimeEntityPlacementToken AwaitingPlacement,
        bool ExactPlacementCurrent,
        uint FullCellId,
        ulong SpatialAuthorityVersion,
        ulong PlacementCommitVersion,
        ulong ObjectClockEpoch,
        double ObjectClockPending,
        bool ObjectClockActive,
        BodyPuritySnapshot Body,
        ImmutableArray<uint> WalkableVertexBits);

    private readonly record struct BodyPuritySnapshot(
        Vector3 Position,
        AcDream.Core.Physics.Position CellPosition,
        bool InWorld,
        Quaternion Orientation,
        Vector3 Velocity,
        Vector3 CachedVelocity,
        int FramesStationaryFall,
        Vector3 Acceleration,
        Vector3 Omega,
        Vector3 GroundNormal,
        Vector3 SlidingNormal,
        bool ContactPlaneValid,
        Plane ContactPlane,
        uint ContactPlaneCellId,
        bool ContactPlaneIsWater,
        bool WalkablePolygonValid,
        Plane WalkablePlane,
        Vector3 WalkableUp,
        float Elasticity,
        float Friction,
        PhysicsStateFlags State,
        TransientStateFlags TransientState,
        double LastUpdateTime,
        bool IsFullyConstrained,
        bool LastMoveWasAutonomous);

    private sealed class Fixture : IDisposable
    {
        private bool _lifetimeDisposed;
        private readonly float _moverSphereOriginZ;

        internal Fixture(
            bool preparePlacement = true,
            bool residentWorld = false,
            RuntimeLocalPlayerShadowDisposition shadowDisposition =
                RuntimeLocalPlayerShadowDisposition.ProvenShapeless,
            float terrainHeight = 0f,
            float moverSphereOriginZ = 0f,
            Vector3? initialVelocity = null,
            Vector3? initialOmega = null,
            float? initialFriction = null,
            float? initialElasticity = null,
            uint cell = Cell)
        {
            _moverSphereOriginZ = moverSphereOriginZ;
            if (residentWorld)
            {
                var engine = new PhysicsEngine
                {
                    DataCache = new PhysicsDataCache(),
                };
                engine.AddLandblock(
                    Cell & 0xFFFF0000u,
                    new TerrainSurface(
                        new byte[81],
                        Enumerable.Repeat(terrainHeight, 256).ToArray()),
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
            Movement = new RuntimeLocalPlayerMovementState();
            Identity = new RuntimeLocalPlayerIdentityState();
            Owner = new RuntimeLocalPlayerPhysicsPublicationState(
                Lifetime.Entities,
                Lifetime.Physics,
                Movement,
                Identity);
            Movement.AttachPhysicsPublication(Owner);
            Record = Lifetime.RegisterEntity(
                Spawn(
                    0x70003001u,
                    incarnation: 1,
                    initialVelocity,
                    initialOmega,
                    initialFriction,
                    initialElasticity,
                    cell)).Canonical!;
            Identity.ServerGuid = Record.ServerGuid;
            ActivationPreparation = new(
                Radius: 0.48f,
                Height: 1.835f,
                shadowDisposition);
            if (preparePlacement)
                RepreparePlacement();
        }

        internal RuntimeEntityObjectLifetime Lifetime { get; }
        internal RuntimeLocalPlayerMovementState Movement { get; }
        internal RuntimeLocalPlayerIdentityState Identity { get; }
        internal RuntimeLocalPlayerPhysicsPublicationState Owner { get; }
        internal RuntimeEntityRecord Record { get; }
        internal RuntimeEntityPlacementToken Placement { get; private set; }
        internal RuntimeSetPositionCommand Command { get; private set; }
        internal RuntimeLocalPlayerPhysicsActivationPreparation
            ActivationPreparation { get; }

        internal void RepreparePlacement()
        {
            Placement = Lifetime.Physics.SetPosition.BeginAuthoredPlacement(
                Record,
                Record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative);
            Assert.True(Placement.IsValid);
            var setup = new FlatSetupCollision(
                ImmutableArray<FlatCollisionCylinder>.Empty,
                [new FlatCollisionSphere(
                    new Vector3(0f, 0f, _moverSphereOriginZ),
                    0.48f)],
                height: 0f,
                radius: 0f,
                stepUpHeight: 0.4f,
                stepDownHeight: 0.4f);
            Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
                Lifetime.Physics.SetPosition.PrepareMover(
                    Placement,
                    new RuntimeSetPositionMoverPreparation(
                        RuntimeSetPositionMoverSetup.Resolved(SetupId, setup),
                        RuntimeSetPositionOperationKind.LocalAuthoritative,
                        GameTime: 10d,
                        PhysicsPlacementClass.Ordinary,
                        PhysicsSetPositionFlags.Placement
                            | PhysicsSetPositionFlags.Slide),
                    out RuntimeSetPositionCommand command));
            Command = command;
        }

        internal RuntimeLocalPlayerPhysicsPublicationToken Prepare()
        {
            Assert.Equal(RuntimeLocalPlayerPhysicsPublicationStatus.Prepared,
                Owner.Prepare(
                    Record,
                    Placement,
                    Command,
                    PlayerMovementConstructionOptions.Fallback,
                    ActivationPreparation,
                    out RuntimeLocalPlayerPhysicsPublicationToken token));
            Assert.Equal(Record.Key, token.Entity);
            Assert.Equal(Placement, token.Placement);
            Assert.Equal(Identity.ServerGuid, token.LocalPlayerServerGuid);
            Assert.Equal(Identity.Revision, token.LocalPlayerIdentityRevision);
            return token;
        }

        internal void DisposeLifetimeOnly()
        {
            if (_lifetimeDisposed)
                return;
            Lifetime.Dispose();
            _lifetimeDisposed = true;
        }

        public void Dispose()
        {
            Movement.Dispose();
            Identity.Dispose();
            DisposeLifetimeOnly();
        }
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

    private sealed class PublicationCollisionObserver(
        Action<RuntimeCollisionReport> onReport)
        : IRuntimeCollisionReportObserver
    {
        public void OnCollisionReport(in RuntimeCollisionReport report) =>
            onReport(report);
    }

    private sealed class CallbackMotionSink(Action onApply)
        : IInterpretedMotionSink
    {
        public bool ApplyMotion(uint motion, float speed)
        {
            onApply();
            return true;
        }

        public bool StopMotion(uint motion)
        {
            onApply();
            return true;
        }
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort incarnation,
        Vector3? initialVelocity = null,
        Vector3? initialOmega = null,
        float? initialFriction = null,
        float? initialElasticity = null,
        uint cell = Cell)
    {
        var position = new CreateObject.ServerPosition(
            cell,
            1f,
            2f,
            3f,
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
            Instance: incarnation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)(PhysicsStateFlags.Gravity
                | PhysicsStateFlags.ReportCollisions),
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: SetupId,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: 1f,
            Friction: initialFriction,
            Elasticity: initialElasticity,
            Translucency: null,
            Velocity: initialVelocity,
            Acceleration: null,
            AngularVelocity: initialOmega,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            Guid: guid,
            Position: position,
            SetupTableId: SetupId,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: 1f,
            Name: "local-publication-fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: 0x09000001u,
            PhysicsState: physics.RawState,
            ObjectDescriptionFlags: 0x8u,
            Friction: initialFriction,
            Elasticity: initialElasticity,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private const uint RestrictionObjectId = 0x70003F01u;

    private static void SeedRestrictionObjects(Fixture fixture)
    {
        fixture.Lifetime.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = RestrictionObjectId,
            HouseOwnerId = 0x70003F02u,
            Restrictions = new HouseRestrictionRecord(
                OpenToPublic: false,
                AllegianceMonarchId: 0x70003F03u,
                Guests: new Dictionary<uint, uint>()),
        });
        fixture.Lifetime.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = fixture.Record.ServerGuid,
            MonarchId = 0x70003F03u,
        });
    }

    private static void ApplyRestrictionObjectMutation(
        Fixture fixture,
        RestrictionObjectMutation mutation)
    {
        ClientObjectTable objects = fixture.Lifetime.Objects;
        switch (mutation)
        {
            case RestrictionObjectMutation.RemoveHouseObject:
                Assert.True(objects.Remove(RestrictionObjectId));
                break;
            case RestrictionObjectMutation.HouseOwnerProperty:
            {
                ClientObject house = objects.Get(RestrictionObjectId)!;
                house.HouseOwnerId = 0x70003F04u;
                break;
            }
            case RestrictionObjectMutation.HouseGuestList:
                objects.Get(RestrictionObjectId)!.Restrictions =
                    new HouseRestrictionRecord(
                        OpenToPublic: false,
                        AllegianceMonarchId: 0x70003F03u,
                        Guests: new Dictionary<uint, uint>
                        {
                            [fixture.Record.ServerGuid] = 1u,
                        });
                break;
            case RestrictionObjectMutation.MoverMonarch:
            {
                ClientObject mover = objects.Get(fixture.Record.ServerGuid)!;
                mover.MonarchId = 0x70003F05u;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static void AddSyntheticIndoorCell(PhysicsDataCache cache, uint cellId)
    {
        var root = new CellBSPNode { Type = BSPNodeType.Leaf };
        cache.RegisterCellStructForTest(cellId, new CellPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            Portals = [new PortalInfo(0, 0, 0)],
            CellBSP = new CellBSPTree
            {
                Root = root,
            },
            FlatContainmentBsp = FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root),
            FlatPhysicsBsp = FlatCollisionAssetBuilder.FlattenPhysicsBsp(
                null, new Dictionary<ushort, ResolvedPolygon>()),
        });
        cache.CellGraph.Add(new EnvCell(
            cellId, Matrix4x4.Identity, Matrix4x4.Identity,
            Vector3.Zero, Vector3.One, Array.Empty<CellPortal>(),
            Array.Empty<uint>(), seenOutside: false, containmentBsp: null));
    }

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

    private static void CommitProductionCollisionGeneration(
        Fixture fixture,
        uint landblockId)
    {
        RuntimeCollisionAdmission admission = fixture.Lifetime.Physics
            .BeginCollisionAdmission(landblockId);
        using PreparedLandblockCollisionGeneration prepared = fixture
            .Lifetime.Physics.PrepareCollisionGeneration(admission);
        fixture.Lifetime.Physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(landblockId));
        Assert.True(CommitPrepared(
            fixture.Lifetime.Physics,
            admission,
            prepared).Committed);
    }

    private static RuntimeLandblockCollisionAssets CollisionAssets(
        uint landblockId) => new(
            landblockId,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f,
            0u);

    public enum PublicationInvalidation
    {
        SetPositionReplacement,
        Vector,
        FinalState,
        ObjectDescription,
        Create,
        Remote,
        Projectile,
        Body,
        Clock,
        Controller,
        CancelPlacement,
    }

    public enum PristineViolation
    {
        Body,
        Controller,
        Host,
        BodyAcquisition,
        Remote,
        RemoteBinding,
        Projectile,
        ProjectileBinding,
        RemotePlacement,
    }

    public enum PublishedRetirement
    {
        Replacement,
        Reset,
        Dispose,
    }

    public enum ActivationInvalidation
    {
        Position,
        Vector,
        ObjectDescription,
        Create,
        Identity,
        Body,
        Controller,
        Clock,
        Spatial,
        Host,
        Remote,
        Projectile,
        PlacementCancellation,
    }

    public enum ReentrantActivationInvalidation
    {
        Reset,
        DeleteAndGuidReuse,
    }

    public enum RestrictionObjectMutation
    {
        RemoveHouseObject,
        HouseOwnerProperty,
        HouseGuestList,
        MoverMonarch,
    }

    private static EntityPhysicsHost CreatePhysicsHost(uint id) => new(
        id,
        getPosition: static () => default,
        getVelocity: static () => Vector3.Zero,
        getRadius: static () => 0.48f,
        inContact: static () => true,
        minterpMaxSpeed: static () => null,
        curTime: static () => 0d,
        physicsTimerTime: static () => 0d,
        getObjectA: static _ => null,
        handleUpdateTarget: static _ => { },
        interruptCurrentMovement: static () => { });

    private static void AssertNotLive(PlayerMovementController controller)
    {
        Assert.Throws<InvalidOperationException>(() => controller.Update(
            1f / 60f,
            default));
        Assert.Throws<InvalidOperationException>(() => controller.TickHidden(
            1f / 60f));
        Assert.Throws<InvalidOperationException>(() =>
            controller.SuspendObjectUpdate(1f / 60f));
        Assert.Throws<InvalidOperationException>(() => controller.SeedPlacementForTest(
            Vector3.One,
            Cell,
            Vector3.One));
        Assert.Throws<InvalidOperationException>(() =>
            controller.CommitCanonicalForcePositionFrame());
        Assert.Throws<InvalidOperationException>(() =>
            controller.ApplyPhysicsState(PhysicsStateFlags.Frozen));
        Assert.Throws<InvalidOperationException>(() => controller.Yaw = 1f);
        Assert.Throws<InvalidOperationException>(() =>
            controller.CaptureMovementResult(mouseLookEvent: false));
        Assert.Throws<InvalidOperationException>(() =>
            controller.CapturePresentationResult());
        Assert.Throws<InvalidOperationException>(() =>
            controller.TryGetOutboundPosition(out _));
        Assert.Throws<InvalidOperationException>(() =>
            controller.NoteMovementSent(1f));
        Assert.Throws<InvalidOperationException>(() =>
            controller.NotePositionSent(default, default, 1f));
        Assert.Throws<InvalidOperationException>(() =>
            controller.ShouldSendPositionEvent(default, default, 1f));
        Assert.Throws<InvalidOperationException>(() =>
            controller.BeginMouseLook(default));
        Assert.Throws<InvalidOperationException>(() =>
            controller.SubmitMouseTurnAdjustment(1f, default));
        Assert.Throws<InvalidOperationException>(() =>
            controller.StopMouseDrift(default));
        Assert.Throws<InvalidOperationException>(() =>
            controller.EndMouseLook(default));
        Assert.Throws<InvalidOperationException>(() =>
            controller.PrepareForAttackRequest());
        Assert.Throws<InvalidOperationException>(() =>
            controller.RequestPosture(MotionCommand.Ready));
        Assert.Throws<InvalidOperationException>(() =>
            controller.ArmConstraintLeashAtCommittedPlacement());
        Assert.Throws<InvalidOperationException>(() =>
            controller.PreparePositionForCommit(Vector3.One, Cell, Vector3.One));
        Assert.Throws<InvalidOperationException>(() =>
            controller.SetCharacterSkills(1, 1));
        Assert.Throws<InvalidOperationException>(() =>
            controller.SetBodyOrientation(Quaternion.Identity));
        Assert.Throws<InvalidOperationException>(() =>
            controller.SetLastMoveWasAutonomous(true));
        Assert.Throws<InvalidOperationException>(() =>
            controller.State = PlayerState.PortalSpace);
        Assert.Throws<InvalidOperationException>(() =>
            controller.StepUpHeight = 1f);
        Assert.Throws<InvalidOperationException>(() =>
            controller.AttachCycleVelocityAccessor(static () => Vector3.One));
        Assert.Throws<InvalidOperationException>(() =>
            controller.AttachAnimationRootMotionSource(static (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => _ = controller.Movement);
        Assert.Throws<InvalidOperationException>(() => _ = controller.MoveTo);
        Assert.Throws<InvalidOperationException>(() => _ = controller.PositionManager);
        Assert.Throws<InvalidOperationException>(() => _ = controller.Motion);
        Assert.Throws<InvalidOperationException>(() => _ = controller.PhysicsBody);
    }
}
