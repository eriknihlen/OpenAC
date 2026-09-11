using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;
using DatReaderWriter.Enums;

namespace AcDream.App.Tests.Streaming;

public sealed class LocalPlayerTeleportControllerTests
{
    [Fact]
    public void DeferredNetworkOwnedBindingReleasesExactlyAndAllowsRebind()
    {
        var deferred = new DeferredLocalPlayerTeleportNetworkSink();
        var first = new FakeNetworkSink();
        var second = new FakeNetworkSink();

        IDisposable firstBinding = deferred.BindOwned(first);
        Assert.Throws<InvalidOperationException>(() => deferred.BindOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = deferred.BindOwned(second);
        firstBinding.Dispose();
        deferred.OnTeleportStarted(9);

        Assert.Empty(first.Starts);
        Assert.Equal([9u], second.Starts);
    }

    [Fact]
    public void DestinationBeforeStart_IsReplayedOnceAfterPresentationActivates()
    {
        var harness = new Harness();
        RuntimeTeleportDestination destination = Position(
            cellId: 0x20210001u,
            teleportSequence: 7,
            x: 12f,
            y: 34f,
            z: 5f);

        harness.OfferDestination(
            destination,
            teleportTimestampAdvanced: true);
        Assert.Empty(harness.Streaming.Reservations);

        harness.Controller.OnTeleportStarted(7);

        Assert.Equal(1, harness.Input.EndCount);
        Assert.Equal(1, harness.Mode.EnterPortalCount);
        Assert.Equal(Matrix4x4.Identity, harness.Presentation.BeginProjection);
        Assert.Equal(
            (1L, 0x20210001u, harness.RevealWindow.FarRadius),
            Assert.Single(harness.Streaming.Reservations));
        Assert.True(harness.Reveal.Snapshot.IsActive);
        Assert.Equal(RuntimePortalKind.Portal, harness.Reveal.Snapshot.Kind);

        harness.OfferDestination(
            destination,
            teleportTimestampAdvanced: false);
        Assert.Single(harness.Streaming.Reservations);
    }

    [Fact]
    public void MissingPlayerController_KeepsStartAndDestinationPendingUntilProjectionExists()
    {
        var harness = new Harness();
        harness.Mode.Controller = null;

        harness.Controller.OnTeleportStarted(2);
        harness.OfferDestination(
            Position(0x20210001u, 2, 7f, 8f, 9f),
            teleportTimestampAdvanced: true);

        Assert.False(harness.Controller.IsActive);
        Assert.Equal(0u, harness.Controller.ActiveDestinationCell);
        Assert.Null(harness.Presentation.BeginProjection);

        harness.Mode.Controller = new PlayerMovementController(new PhysicsEngine());
        harness.Mode.Controller.SeedPlacementForTest(
            Vector3.Zero,
            0x20210001u,
            Vector3.Zero);
        harness.Controller.Tick(0.016f);

        Assert.True(harness.Controller.IsActive);
        Assert.Equal(0x20210001u, harness.Controller.ActiveDestinationCell);
        Assert.NotNull(harness.Presentation.BeginProjection);
        Assert.Single(harness.Streaming.Reservations);
    }

    [Fact]
    public void ControllerWithdrawnAfterActivation_KeepsAcceptedDestinationUntilItReturns()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(14);
        Assert.True(harness.Controller.IsActive);

        harness.Mode.Controller = null;
        harness.OfferDestination(
            Position(0x20210001u, 14, 7f, 8f, 9f),
            teleportTimestampAdvanced: true);

        Assert.Equal(0u, harness.Controller.ActiveDestinationCell);
        Assert.Empty(harness.Streaming.Reservations);

        harness.Mode.Controller = new PlayerMovementController(new PhysicsEngine());
        harness.Mode.Controller.SeedPlacementForTest(Vector3.Zero, 0x20210001u, Vector3.Zero);
        harness.Controller.Tick(0.016f);

        Assert.Equal(0x20210001u, harness.Controller.ActiveDestinationCell);
        Assert.Single(harness.Streaming.Reservations);
    }

    [Fact]
    public void ControllerWithdrawnAfterAim_HoldsPresentationUntilModeRebuildsIt()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(15);
        harness.OfferDestination(
            Position(0x20210001u, 15, 7f, 8f, 9f),
            teleportTimestampAdvanced: true);
        harness.Mode.Controller = null;
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);

        harness.Controller.Tick(0.016f);

        Assert.False(harness.Placement.Called);
        Assert.Empty(harness.Presentation.WorldReadyValues);

        harness.Mode.RebuildOnEnter = () =>
        {
            var controller = new PlayerMovementController(new PhysicsEngine());
            controller.SeedPlacementForTest(Vector3.Zero, 0x20210001u, Vector3.Zero);
            return controller;
        };
        harness.Controller.Tick(0.016f);

        Assert.NotNull(harness.Mode.Controller);
        Assert.True(harness.Placement.Called);
        Assert.Equal(
            new Vector3(7f, 8f, 9f),
            harness.Movement.Controller!.Position);
        Assert.Equal(0x20210001u, harness.Movement.Controller.CellId);
    }

    [Fact]
    public void SameLandblockDestination_DoesNotRecenterAndKeepsTranslatedPosition()
    {
        var harness = new Harness(centerX: 0x20, centerY: 0x21);
        harness.AddSyntheticIndoorCell(0x20210123u);
        harness.Controller.OnTeleportStarted(3);
        harness.OfferDestination(
            Position(0x20210123u, 3, 20f, 30f, 4f),
            teleportTimestampAdvanced: true);
        harness.Presentation.EmitPlaceWhenReady = true;

        harness.Controller.Tick(0.016f);

        Assert.Empty(harness.Streaming.Recenters);
        Assert.True(harness.Placement.Called);
        Assert.Equal(
            new Vector3(20f, 30f, 4f),
            harness.Movement.Controller!.Position);
        Assert.Equal(0x20210123u, harness.Movement.Controller.CellId);
    }

    [Fact]
    public void RefusedPlace_HoldsTheStreamAndConvergesOnlyAfterContentionClears()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(50);
        harness.OfferDestination(
            Position(0x20210001u, 50, 11f, 12f, 13f),
            teleportTimestampAdvanced: true);
        harness.Presentation.EmitPlaceWhenReady = true;
        Vector3 positionBefore = harness.Movement.Controller!.Position;

        RuntimeEntityRecord record = harness.Lifetime.Entities
            .TryGetActive(0x50000001u, out RuntimeEntityRecord active)
            ? active
            : throw new InvalidOperationException("fixture entity missing");
        RuntimeEntityPlacementToken displaced = harness.Lifetime.Physics
            .SetPosition.TryBeginExclusiveAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative);
        Assert.True(displaced.IsValid);

        for (int i = 0; i < 100; i++)
            harness.Controller.Tick(0.1f);

        Assert.False(harness.Placement.Called);
        Assert.Equal(positionBefore, harness.Movement.Controller.Position);
        Assert.True(harness.Controller.IsActive);
        Assert.False(harness.Reveal.Snapshot.Completed);
        Assert.Equal(0, harness.Session.LoginCompleteCount);

        RuntimePlacementCancellationReceipt cancellation = harness.Lifetime
            .Physics.SetPosition.ForgetExactPlacement(displaced);
        if (cancellation.IsValid)
            harness.Lifetime.Physics.SetPosition.PublishCancellation(cancellation);

        harness.Controller.Tick(0.1f);

        Assert.True(harness.Placement.Called);
        Assert.Equal(
            new Vector3(11f, 12f, 13f),
            harness.Movement.Controller.Position);
    }

    [Fact]
    public void ParkedPlace_ForgottenByOrdinaryMergeDoesNotLatchAsCommitted()
    {
        var harness = new Harness();
        const uint destinationLandblock = 0x40410000u;
        harness.Controller.OnTeleportStarted(60);
        harness.OfferDestination(
            Position(destinationLandblock | 0x0001u, 60, 21f, 22f, 23f),
            teleportTimestampAdvanced: true);
        harness.Presentation.EmitPlaceWhenReady = true;

        harness.Controller.Tick(0.016f);
        Assert.False(harness.Placement.Called);
        Assert.Equal(1, harness.AcceptedPositionDrive.PendingCount);

        harness.MergeOrdinaryPosition(
            new Vector3(48f, 49f, 50f), 0x20210001u, teleportSequence: 60);
        harness.AcceptedPositionDrive.Advance();
        Assert.Equal(0, harness.AcceptedPositionDrive.PendingCount);

        for (int i = 0; i < 100; i++)
            harness.Controller.Tick(0.1f);

        Assert.False(harness.Placement.Called);
        Assert.Equal(0, harness.Reveal.PortalMaterializationCount);
        Assert.Equal(0, harness.Session.LoginCompleteCount);
        Assert.True(harness.Controller.IsActive);
        Assert.False(harness.Reveal.Snapshot.Completed);
    }

    [Fact]
    public void CrossLandblockDestination_RecentersBeforeItCanBecomeReady()
    {
        var harness = new Harness(centerX: 0x20, centerY: 0x21);
        harness.CommitLandblockCollision(0x30310000u);
        harness.AddSyntheticIndoorCell(0x30310100u);
        harness.Mode.Controller!.SeedPlacementForTest(
            Vector3.Zero,
            0x20210001u,
            Vector3.Zero);
        harness.Controller.OnTeleportStarted(4);
        harness.Streaming.RecenterPending = true;
        harness.OfferDestination(
            Position(0x30310100u, 4, -2f, 8f, 9f),
            teleportTimestampAdvanced: true);
        harness.Presentation.EmitPlaceWhenReady = true;

        harness.Controller.Tick(1f);

        Assert.Equal((0x30, 0x31, true), Assert.Single(harness.Streaming.Recenters));
        Assert.False(Assert.Single(harness.Presentation.WorldReadyValues));
        Assert.False(harness.Placement.Called);

        harness.Streaming.RecenterPending = false;
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Placement.Called);
        Assert.Equal(
            new Vector3(-2f, 8f, 9f),
            harness.Movement.Controller!.Position);
        Assert.Equal(0x30310100u, harness.Movement.Controller.CellId);
    }

    [Fact]
    public void ReadinessHold_NeverRevealsIncompleteWorldAndShowsWaitCueAfterFiveSeconds()
    {
        var harness = new Harness(worldReady: false);
        harness.Controller.OnTeleportStarted(5);
        harness.OfferDestination(
            Position(0x20210001u, 5, 1f, 2f, 3f),
            teleportTimestampAdvanced: true);
        harness.Presentation.EmitPlaceWhenReady = true;

        harness.Controller.Tick(4.9f);
        Assert.False(Assert.Single(harness.Presentation.WorldReadyValues));
        Assert.False(harness.Presentation.WaitCueValues[^1]);
        Assert.False(harness.Placement.Called);

        harness.Controller.Tick(0.1f);
        harness.Controller.Tick(30f);

        Assert.All(harness.Presentation.WorldReadyValues, Assert.False);
        Assert.True(harness.Presentation.WaitCueValues[^1]);
        Assert.False(harness.Placement.Called);
        Assert.True(harness.Reveal.WaitCueShown);
    }

    [Fact]
    public void Place_ReconcilesInsidePlacementBeforeRevealMaterialized()
    {
        var order = new List<string>();
        var harness = new Harness(order: order);
        harness.Controller.OnTeleportStarted(8);
        harness.OfferDestination(
            Position(0x20210001u, 8, 4f, 5f, 6f),
            teleportTimestampAdvanced: true);
        order.Clear();
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);

        harness.Controller.Tick(0.016f);

        Assert.True(Index(order, "placement") < Index(order, "[world-reveal] event=materialized"));
        Assert.Equal(1, harness.Reveal.PortalMaterializationCount);
    }

    [Fact]
    public void Place_RechecksExactRuntimeLifetimeBeforeMutatingPresentation()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(81);
        harness.OfferDestination(
            Position(0x20210001u, 81, 4f, 5f, 6f),
            teleportTimestampAdvanced: true);
        long generation = harness.Reveal.Snapshot.Generation;
        Assert.True(harness.Transit.Cancel(generation));
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);

        harness.Controller.Tick(0.016f);

        Assert.False(harness.Placement.Called);
        Assert.Equal(0, harness.Reveal.PortalMaterializationCount);
    }

    [Fact]
    public void LoginComplete_EntersWorldThenSendsThenCompletesAndResets()
    {
        var order = new List<string>();
        var harness = new Harness(order: order);
        harness.Controller.OnTeleportStarted(9);
        harness.OfferDestination(
            Position(0x20210001u, 9, 4f, 5f, 6f),
            teleportTimestampAdvanced: true);
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);
        harness.Controller.Tick(0.016f);
        order.Clear();
        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);

        harness.Controller.Tick(0.016f);

        Assert.True(Index(order, "enter-world") < Index(order, "login-complete"));
        Assert.True(Index(order, "login-complete") < Index(order, "[world-reveal] event=complete"));
        Assert.True(Index(order, "[world-reveal] event=complete") < Index(order, "presentation-reset"));
        Assert.False(harness.Controller.IsActive);
        Assert.Equal(0u, harness.Controller.ActiveDestinationCell);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
        Assert.True(harness.Reveal.Snapshot.Completed);
        Assert.False(harness.Reveal.Snapshot.Cancelled);
    }

    [Fact]
    public void ExitSound_HidesTunnelBeforeReleasingDestinationAndKeepsProtocolActive()
    {
        var order = new List<string>();
        var harness = new Harness(order: order);
        harness.Controller.OnTeleportStarted(91);
        harness.OfferDestination(
            Position(0x20210001u, 91, 4f, 5f, 6f),
            teleportTimestampAdvanced: true);
        harness.Streaming.ReservationEnds.Clear();
        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);

        harness.Controller.Tick(0.016f);

        Assert.True(Index(order, "presentation-exit") < Index(order, "reservation-end"));
        Assert.True(Index(order, "reservation-end") < Index(order, "exit-cue"));
        Assert.False(harness.Presentation.IsPortalViewportVisible);
        Assert.True(harness.Controller.IsActive);
        Assert.False(harness.Reveal.Snapshot.Completed);
        Assert.Single(harness.Streaming.ReservationEnds);
    }

    [Fact]
    public void NewerStart_ReplacesOldDestinationWithoutReusingIt()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(10);
        harness.OfferDestination(
            Position(0x20210001u, 10, 1f, 1f, 1f),
            teleportTimestampAdvanced: true);

        harness.Controller.OnTeleportStarted(11);
        harness.Controller.Tick(30f);

        Assert.False(harness.Placement.Called);
        Assert.Equal(0u, harness.Controller.ActiveDestinationCell);

        harness.OfferDestination(
            Position(0x20210001u, 11, 2f, 2f, 2f),
            teleportTimestampAdvanced: true);
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Placement.Called);
        Assert.Equal(2f, harness.Movement.Controller!.Position.X);
        Assert.Equal(2f, harness.Movement.Controller.Position.Y);
        Assert.Equal(0x20210001u, harness.Movement.Controller.CellId);
    }

    [Fact]
    public void SessionReset_ClearsTransitAndLetsRecenterConvergeAcrossLaterFrames()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(12);
        harness.OfferDestination(
            Position(0x20210001u, 12, 1f, 2f, 3f),
            teleportTimestampAdvanced: true);

        harness.Controller.ResetSession();

        Assert.False(harness.Controller.IsActive);
        Assert.Equal(RuntimePortalSnapshot.Idle, harness.Reveal.Snapshot);
        Assert.True(harness.Streaming.LastResetWasSessionEnding);

        var failed = new Harness();
        failed.Streaming.ResetResult = false;
        failed.Controller.ResetSession();
        Assert.False(failed.Controller.IsActive);
        Assert.True(failed.Streaming.LastResetWasSessionEnding);
    }

    [Fact]
    public void StaleStart_DoesNotMutateInputModeOrPresentation()
    {
        var harness = new Harness();
        harness.Authority.IsFresh = false;

        harness.Controller.OnTeleportStarted(13);

        Assert.Equal(0, harness.Input.EndCount);
        Assert.Equal(0, harness.Mode.EnterPortalCount);
        Assert.Null(harness.Presentation.BeginProjection);
        Assert.False(harness.Controller.IsActive);
    }

    [Fact]
    public void ReentrantNewStartDuringPlacement_CannotBeCompletedByOldTail()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(20);
        harness.OfferDestination(
            Position(0x20210001u, 20, 1f, 2f, 3f),
            teleportTimestampAdvanced: true);
        harness.Streaming.ReservationEnds.Clear();
        harness.Placement.OnPlace = () => harness.Controller.OnTeleportStarted(21);
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);

        harness.Controller.Tick(0.016f);

        Assert.True(harness.Controller.IsActive);
        Assert.Equal(0u, harness.Controller.ActiveDestinationCell);
        Assert.Equal(0, harness.Reveal.PortalMaterializationCount);
        Assert.Single(harness.Streaming.ReservationEnds);
    }

    [Fact]
    public void ReentrantNewStartDuringLoginComplete_PreservesReplacementLifetime()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(30);
        harness.OfferDestination(
            Position(0x20210001u, 30, 1f, 2f, 3f),
            teleportTimestampAdvanced: true);
        harness.Session.OnSend = () => harness.Controller.OnTeleportStarted(31);
        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);

        harness.Controller.Tick(0.016f);

        Assert.True(harness.Controller.IsActive);
        Assert.Equal(0u, harness.Controller.ActiveDestinationCell);
        Assert.False(harness.Reveal.Snapshot.Completed);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
    }

    [Fact]
    public void DisposeFailure_CanBeRetriedWithoutDoubleDisposingAfterSuccess()
    {
        var harness = new Harness();
        harness.Presentation.DisposeFailuresRemaining = 1;

        Assert.Throws<InvalidOperationException>(() => harness.Controller.Dispose());
        harness.Controller.Dispose();
        harness.Controller.Dispose();

        Assert.Equal(2, harness.Presentation.DisposeCount);
    }

    [Fact]
    public void ConcretePlacement_CommitsEntityControllerAndOneSpatialReconcile()
    {
        const uint guid = 0x50000001u;
        const uint cell = 0x20210001u;
        var world = new GpuWorldState();
        world.AddLandblock(new LoadedLandblock(
            0x2021FFFFu,
            new DatReaderWriter.DBObjs.LandBlock(),
            Array.Empty<WorldEntity>()));
        var runtime = LiveEntityRuntimeFixture.Create(world, new NullResources());
        runtime.RegisterLiveEntity(Spawn(guid, cell));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            cell,
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
            })!;
        var identity = new LocalPlayerIdentityState { ServerGuid = guid };
        var controllerSlot = new RuntimeLocalPlayerMovementState
        {
            Controller = new PlayerMovementController(new PhysicsEngine()),
        };
        var position = new Vector3(12f, 24f, 6f);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        controllerSlot.Controller.SeedPlacementForTest(position, cell, position);
        controllerSlot.Controller.SetBodyOrientation(rotation);
        var cameras = new ChaseCameraInputState
        {
            Legacy = new ChaseCamera(),
            Retail = new RetailChaseCamera(),
        };
        var spatial = new FakeSpatialReconcile(() => new PlacementSnapshot(
            entity.Position,
            entity.ParentCellId ?? 0u,
            entity.Rotation,
            controllerSlot.Controller.Position,
            controllerSlot.Controller.CellId,
            controllerSlot.Controller.BodyOrientation));
        var placement = new LocalPlayerTeleportPlacement(
            runtime,
            identity,
            controllerSlot,
            new LocalPlayerPhysicsHostSlot(),
            cameras,
            spatial);

        placement.Place(rotation);

        Assert.Equal(entity.Position, controllerSlot.Controller.Position);
        Assert.Equal(entity.ParentCellId, controllerSlot.Controller.CellId);
        Assert.Equal(cell & 0xFFFF0000u, entity.ParentCellId & 0xFFFF0000u);
        Assert.Equal(rotation, entity.Rotation);
        Assert.Equal(rotation, controllerSlot.Controller.BodyOrientation);
        Assert.Equal(1, spatial.Count);
        Assert.Equal(entity.Position, spatial.Snapshot.EntityPosition);
        Assert.Equal(entity.ParentCellId, spatial.Snapshot.EntityCell);
        Assert.Equal(entity.Rotation, spatial.Snapshot.EntityRotation);
        Assert.Equal(
            controllerSlot.Controller.Position,
            spatial.Snapshot.ControllerPosition);
        Assert.Equal(
            controllerSlot.Controller.CellId,
            spatial.Snapshot.ControllerCell);
        Assert.Equal(
            controllerSlot.Controller.BodyOrientation,
            spatial.Snapshot.ControllerRotation);
    }

    [Fact]
    public void ConcretePlacement_CommitsDestinationSpatialBucketBeforeReconcile()
    {
        const uint guid = 0x50000001u;
        const uint sourceCell = 0x20210001u;
        const uint destinationCell = 0x30310001u;
        var world = new GpuWorldState();
        world.AddLandblock(new LoadedLandblock(
            sourceCell & 0xFFFF0000u | 0xFFFFu,
            new DatReaderWriter.DBObjs.LandBlock(),
            Array.Empty<WorldEntity>()));
        world.AddLandblock(new LoadedLandblock(
            destinationCell & 0xFFFF0000u | 0xFFFFu,
            new DatReaderWriter.DBObjs.LandBlock(),
            Array.Empty<WorldEntity>()));
        var runtime = LiveEntityRuntimeFixture.Create(world, new NullResources());
        runtime.RegisterLiveEntity(Spawn(guid, sourceCell));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            sourceCell,
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
            })!;
        var identity = new LocalPlayerIdentityState { ServerGuid = guid };
        var controllerSlot = new RuntimeLocalPlayerMovementState
        {
            Controller = new PlayerMovementController(new PhysicsEngine()),
        };
        controllerSlot.Controller.SeedPlacementForTest(
            Vector3.Zero,
            sourceCell,
            Vector3.Zero);
        var destinationPosition = new Vector3(12f, 24f, 6f);
        controllerSlot.Controller.SeedPlacementForTest(
            destinationPosition,
            destinationCell,
            destinationPosition);
        var cameras = new ChaseCameraInputState
        {
            Legacy = new ChaseCamera(),
            Retail = new RetailChaseCamera(),
        };
        LiveEntityRecord? recordAtReconcile = null;
        var spatial = new FakeSpatialReconcile(() =>
        {
            Assert.True(runtime.TryGetRecord(guid, out recordAtReconcile));
            return new PlacementSnapshot(
                entity.Position,
                entity.ParentCellId ?? 0u,
                entity.Rotation,
                controllerSlot.Controller.Position,
                controllerSlot.Controller.CellId,
                controllerSlot.Controller.BodyOrientation);
        });
        var placement = new LocalPlayerTeleportPlacement(
            runtime,
            identity,
            controllerSlot,
            new LocalPlayerPhysicsHostSlot(),
            cameras,
            spatial);

        placement.Place(Quaternion.Identity);

        Assert.NotNull(recordAtReconcile);
        uint resolvedDestinationCell = controllerSlot.Controller.CellId;
        Assert.Equal(
            destinationCell & 0xFFFF0000u,
            resolvedDestinationCell & 0xFFFF0000u);
        Assert.Equal(resolvedDestinationCell, recordAtReconcile.FullCellId);
        Assert.Equal(
            destinationCell & 0xFFFF0000u | 0xFFFFu,
            recordAtReconcile.CanonicalLandblockId);
        Assert.True(recordAtReconcile.IsSpatiallyProjected);
        Assert.True(recordAtReconcile.IsSpatiallyVisible);
        Assert.Contains(entity, world.Entities);
        Assert.Equal(resolvedDestinationCell, entity.ParentCellId);
        Assert.Equal(1, spatial.Count);
    }

    private static int Index(IReadOnlyList<string> events, string prefix)
    {
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].StartsWith(prefix, StringComparison.Ordinal))
                return i;
        }

        return int.MaxValue;
    }

    private static RuntimeTeleportDestination Position(
        uint cellId,
        ushort teleportSequence,
        float x,
        float y,
        float z) => new(
            EntityGuid: 0x50000001u,
            InstanceSequence: 1,
            PositionSequence: 1,
            TeleportSequence: teleportSequence,
            ForcePositionSequence: 1,
            Position: new Position(
                cellId,
                new Vector3(x, y, z),
                Quaternion.Identity));

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell) => new WorldSession.EntitySpawn(
        Guid: guid,
        Position: new CreateObject.ServerPosition(
            cell,
            0f,
            0f,
            0f,
            1f,
            0f,
            0f,
            0f),
        SetupTableId: 0x02000001u,
        AnimPartChanges: [],
        TextureChanges: [],
        SubPalettes: [],
        BasePaletteId: null,
        ObjScale: null,
        Name: "player",
        ItemType: null,
        MotionState: null,
        MotionTableId: null).WithConsistentPhysics();

    private sealed class NullResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }

    private sealed class FakeSpatialReconcile : ILiveSpatialReconcilePhase
    {
        private readonly Func<PlacementSnapshot> _capture;

        public FakeSpatialReconcile(Func<PlacementSnapshot> capture) =>
            _capture = capture;

        public int Count;
        public PlacementSnapshot Snapshot;

        public void Reconcile()
        {
            Snapshot = _capture();
            Count++;
        }
    }

    private readonly record struct PlacementSnapshot(
        Vector3 EntityPosition,
        uint EntityCell,
        Quaternion EntityRotation,
        Vector3 ControllerPosition,
        uint ControllerCell,
        Quaternion ControllerRotation);

    private sealed class Harness
    {
        private const uint PlayerGuid = 0x50000001u;
        private const uint HomeCell = 0x20210001u;
        private ushort _mergePositionSequence = 1;

        public readonly FakeAuthority Authority = new();
        public readonly FakeInput Input = new();
        public readonly FakeMode Mode;
        public readonly FakeStreaming Streaming;
        public readonly FakePlacement Placement;
        public readonly FakeSession Session;
        public readonly FakePresentation Presentation;
        public readonly RuntimeWorldTransitState Transit;
        public readonly WorldRevealCoordinator Reveal;

        public StreamingRevealWindow RevealWindow { get; set; } =
            new(NearRadius: 1, FarRadius: 1);
        public readonly LocalPlayerTeleportController Controller;
        public readonly RuntimeEntityObjectLifetime Lifetime;
        public readonly RuntimeAcceptedPositionDriveController AcceptedPositionDrive;
        public readonly RuntimeLocalPlayerMovementState Movement;
        public IPreparedCollisionSource DiagnosticCollisionSource => new UnusedCollisionSource();

        public bool WorldReady;

        public RuntimeCharacterSelectionLifecycle SelectionLifecycle
        {
            get => LoginLifecycle.SelectionLifecycle;
            set => LoginLifecycle.SelectionLifecycle = value;
        }

        public readonly FakeLoginLifecycleSource LoginLifecycle = new();

        public readonly FakeLogoutOperations Logout = new();

        public Harness(
            int centerX = 0x20,
            int centerY = 0x21,
            bool worldReady = true,
            List<string>? order = null)
        {
            WorldReady = worldReady;
            order ??= new List<string>();
            Mode = new FakeMode(order);
            Streaming = new FakeStreaming(centerX, centerY, order);
            Placement = new FakePlacement(order);
            Session = new FakeSession(order);
            Presentation = new FakePresentation(order);
            Transit = new RuntimeWorldTransitState(order.Add);
            Reveal = new WorldRevealCoordinator(
                Transit,
                revealWindow: () => RevealWindow,
                isRenderNeighborhoodReady: (_, _, _) => WorldReady,
                isSpawnCellReady: _ => WorldReady,
                isTerrainNeighborhoodReady: (_, _) => WorldReady,
                areCompositeTexturesReady: () => WorldReady,
                prepareCompositeTextures: (_, _) => { },
                invalidateCompositeTextures: () => { },
                isSpawnClaimUnhydratable: _ => false,
                streaming: Streaming);

            var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
            var heights = new byte[81];
            Array.Fill(heights, (byte)5f);
            var heightTable = new float[256];
            for (int i = 0; i < heightTable.Length; i++)
                heightTable[i] = i;
            engine.AddLandblock(
                HomeCell & 0xFFFF0000u,
                new TerrainSurface(heights, heightTable),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            Lifetime = new RuntimeEntityObjectLifetime(engine);
            Lifetime.Physics.SetPosition.BeginCollisionGeneration(
                HomeCell & 0xFFFF0000u, 1UL);
            Lifetime.Physics.SetPosition.CommitCollisionGeneration(
                HomeCell & 0xFFFF0000u, 1UL, ready: true);
            Lifetime.Physics.ObserveLocalWorldFrame(HomeCell, teleportAdvanced: false);
            Lifetime.BindEventContext(
                static () => new RuntimeGenerationToken(1UL),
                static () => 1UL);
            Movement = new RuntimeLocalPlayerMovementState();
            RuntimeLocalPlayerMovementState movement = Movement;
            var identity = new RuntimeLocalPlayerIdentityState
            {
                ServerGuid = PlayerGuid,
            };
            movement.AttachPhysicsPublication(new RuntimeLocalPlayerPhysicsPublicationState(
                Lifetime.Entities, Lifetime.Physics, movement, identity));
            Lifetime.LocalPlayerFirstEntry.BindPublication(movement.PhysicsPublication);
            Lifetime.BindLiveInputs(() => false, () => movement.Controller?.Position);
            var clock = new GameRuntimeClock();
            var firstEntry = new RuntimeFirstEntryDriveController(
                Lifetime,
                clock,
                new UnusedCollisionSource(),
                () => PlayerMovementConstructionOptions.Fallback,
                static _ => new RuntimeLocalPlayerPhysicsActivationPreparation(
                    0.48f, 1.835f, RuntimeLocalPlayerShadowDisposition.ProvenShapeless));
            // Production's OnSpawned shape: RegisterEntityWithInitialResidence
            // then ApplyAcceptedSpawn (RuntimeLiveEntitySessionController.cs).
            RuntimeEntityRegistrationResult registration =
                Lifetime.RegisterEntityWithInitialResidence(
                    Spawn(PlayerGuid, HomeCell), isLocalPlayer: true);
            RuntimeEntityRecord canonical = registration.Canonical
                ?? throw new InvalidOperationException(
                    "fixture failed to register the local player");
            Lifetime.ApplyAcceptedSpawn(
                canonical,
                canonical.CreateIntegrationVersion,
                canonical.Snapshot,
                replaceGeneration: registration.Inbound.Disposition
                    is CreateObjectTimestampDisposition.NewGeneration);
            for (int attempt = 0; attempt < 8 && firstEntry.PendingCount != 0; attempt++)
            {
                firstEntry.DriveAll();
                DrainPlacementFifo();
            }
            Assert.Equal(0, firstEntry.PendingCount);
            Assert.True(
                Lifetime.Entities.TryGetActive(PlayerGuid, out RuntimeEntityRecord seeded),
                "fixture failed to register the local player");
            Assert.False(
                Lifetime.TryGetInitialCreateResidence(seeded, out _),
                "fixture left the local player's initial-create residence open");

            AcceptedPositionDrive = new RuntimeAcceptedPositionDriveController(
                Lifetime,
                clock,
                new UnusedCollisionSource(),
                new LocalPlayerOutboundController((_, _, _, _, _, _) => { }),
                () => new RuntimeGenerationToken(1UL),
                () => PlayerGuid,
                () => movement.Controller,
                () => false,
                () => null,
                () => movement);

            Controller = new LocalPlayerTeleportController(
                Authority,
                Input,
                Mode,
                Streaming,
                Transit,
                Reveal,
                Placement,
                Session,
                Presentation,
                AcceptedPositionDrive,
                LoginLifecycle,
                Logout);
        }

        public void OfferDestination(
            RuntimeTeleportDestination destination,
            bool teleportTimestampAdvanced)
        {
            _mergePositionSequence++;
            var update = new WorldSession.EntityPositionUpdate(
                destination.EntityGuid,
                new CreateObject.ServerPosition(
                    destination.Position.ObjCellId,
                    destination.Position.Frame.Origin.X,
                    destination.Position.Frame.Origin.Y,
                    destination.Position.Frame.Origin.Z,
                    destination.Position.Frame.Orientation.W,
                    destination.Position.Frame.Orientation.X,
                    destination.Position.Frame.Orientation.Y,
                    destination.Position.Frame.Orientation.Z),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: _mergePositionSequence,
                TeleportSequence: destination.TeleportSequence,
                ForcePositionSequence: 0);
            Lifetime.TryApplyPosition(
                update,
                isLocalPlayer: true,
                forcePositionRotation: Quaternion.Identity,
                currentLocalVelocity: Vector3.Zero,
                acknowledgeProjection: null,
                out _,
                out _,
                out _);
            Controller.OfferDestination(destination, teleportTimestampAdvanced);
        }

        public void MergeOrdinaryPosition(
            Vector3 position, uint cellId, ushort teleportSequence)
        {
            _mergePositionSequence++;
            var update = new WorldSession.EntityPositionUpdate(
                PlayerGuid,
                new CreateObject.ServerPosition(
                    cellId,
                    position.X,
                    position.Y,
                    position.Z,
                    1f, 0f, 0f, 0f),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: _mergePositionSequence,
                TeleportSequence: teleportSequence,
                ForcePositionSequence: 0);
            Lifetime.TryApplyPosition(
                update,
                isLocalPlayer: true,
                forcePositionRotation: Quaternion.Identity,
                currentLocalVelocity: Vector3.Zero,
                acknowledgeProjection: null,
                out _,
                out _,
                out _);
        }

        public void CommitLandblockCollision(uint landblockId)
        {
            var heights = new byte[81];
            Array.Fill(heights, (byte)5f);
            var heightTable = new float[256];
            for (int i = 0; i < heightTable.Length; i++)
                heightTable[i] = i;
            Lifetime.Physics.SetPosition.BeginCollisionGeneration(landblockId, 1UL);
            Lifetime.Physics.Engine.AddLandblock(
                landblockId,
                new TerrainSurface(heights, heightTable),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            Lifetime.Physics.SetPosition.CommitCollisionGeneration(
                landblockId, 1UL, ready: true);
        }

        public void AddSyntheticIndoorCell(uint envCellId)
        {
            Lifetime.Physics.DataCache.RegisterCellStructForTest(
                envCellId,
                new CellPhysics
                {
                    WorldTransform = Matrix4x4.Identity,
                    InverseWorldTransform = Matrix4x4.Identity,
                    Resolved = new Dictionary<ushort, ResolvedPolygon>(),
                    Portals = [new PortalInfo(0, 0, 0)],
                    CellBSP = new DatReaderWriter.Types.CellBSPTree
                    {
                        Root = new DatReaderWriter.Types.CellBSPNode
                        {
                            Type = BSPNodeType.Leaf,
                        },
                    },
                });
            Lifetime.Physics.DataCache.CellGraph.Add(
                new AcDream.Core.World.Cells.EnvCell(
                    envCellId,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    Vector3.Zero,
                    Vector3.One,
                    Array.Empty<AcDream.Core.World.Cells.CellPortal>(),
                    Array.Empty<uint>(),
                    seenOutside: false,
                    containmentBsp: null));
        }

        public void DrainPlacementFifo()
        {
            while (Lifetime.Physics.SetPosition.TryPeekProjection(
                    out RuntimePlacementProjectionSnapshot head))
            {
                if (!Lifetime.Physics.SetPosition.AcknowledgeProjection(head.Token))
                    break;
            }
        }

        private static WorldSession.EntitySpawn Spawn(uint guid, uint cell)
        {
            var position = new CreateObject.ServerPosition(
                cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
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
                "teleport-controller-fixture",
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

        private sealed class UnusedCollisionSource : IPreparedCollisionSource
        {
            public PreparedAssetPresence ProbeCollision(
                AcDream.Content.Pak.PakAssetType type, uint sourceFileId) =>
                PreparedAssetPresence.Available;

            public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
                uint sourceFileId, CancellationToken cancellationToken = default) =>
                PreparedCollisionReadResult<FlatSetupCollision>.Missing;

            public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
                uint sourceFileId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
                ReadCellStructureCollision(
                    uint sourceFileId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
                uint sourceFileId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public PreparedCollisionSourceStats CollisionStats => default;

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeAuthority : ILocalPlayerTeleportAuthority
    {
        public bool IsFresh = true;
        public bool IsFreshStart(ushort sequence) => IsFresh;
    }

    private sealed class FakeInput : ILocalPlayerTeleportInputLifetime
    {
        public int EndCount;
        public void EndMouseLook() => EndCount++;
    }

    private sealed class FakeMode : ILocalPlayerTeleportModeOperations
    {
        private readonly List<string> _order;

        public FakeMode(List<string> order)
        {
            _order = order;
            Controller = new PlayerMovementController(new PhysicsEngine());
            Controller.SeedPlacementForTest(Vector3.Zero, 0x20210001u, Vector3.Zero);
        }

        public PlayerMovementController? Controller { get; set; }
        public Matrix4x4 Projection => Matrix4x4.Identity;
        public int EnterPortalCount;
        public int EnterPortalForLoginCount;
        public bool BlockLoginEnter;
        public Func<PlayerMovementController?>? RebuildOnEnter;

        public bool TryEnterPortalSpace()
        {
            EnterPortalCount++;
            Controller ??= RebuildOnEnter?.Invoke();
            if (Controller is null)
                return false;
            Controller.State = PlayerState.PortalSpace;
            return true;
        }

        public bool TryEnterPortalSpaceForLogin()
        {
            EnterPortalForLoginCount++;
            if (BlockLoginEnter)
                return false;
            return TryEnterPortalSpace();
        }

        public void EnterWorld()
        {
            _order.Add("enter-world");
            if (Controller is { } controller)
                controller.State = PlayerState.InWorld;
        }
    }

    private sealed class FakeStreaming
        : ILocalPlayerTeleportStreamingOperations,
          IWorldRevealStreamingScheduler
    {
        private readonly List<string> _order;

        public FakeStreaming(int centerX, int centerY, List<string> order)
        {
            CenterX = centerX;
            CenterY = centerY;
            _order = order;
        }

        public int CenterX { get; }
        public int CenterY { get; }
        public bool IsRecenterPending => RecenterPending;
        public bool RecenterPending;
        public bool ResetResult = true;
        public int ResetCalls;
        public bool LastResetWasSessionEnding;
        public readonly List<(int X, int Y, bool Sealed)> Recenters = new();
        public readonly List<(long Generation, uint Cell, int Radius)>
            Reservations = new();
        public readonly List<long> ReservationEnds = new();

        public bool BeginRecenter(int x, int y, bool isSealedDungeon)
        {
            Recenters.Add((x, y, isSealedDungeon));
            return !RecenterPending;
        }

        public bool ResetRecenter(bool sessionEnding)
        {
            ResetCalls++;
            LastResetWasSessionEnding = sessionEnding;
            return ResetResult;
        }

        public bool IsSealedDungeon(uint cellId) =>
            (cellId & 0xFFFFu) >= 0x0100u;

        public void BeginDestinationReservation(
            long revealGeneration,
            uint destinationCell,
            int requiredRenderRadius) =>
            Reservations.Add(
                (revealGeneration, destinationCell, requiredRenderRadius));

        public void EndDestinationReservation(long revealGeneration)
        {
            ReservationEnds.Add(revealGeneration);
            _order.Add("reservation-end");
        }
    }

    private sealed class FakePlacement : ILocalPlayerTeleportPlacement
    {
        private readonly List<string> _order;

        public FakePlacement(List<string> order) => _order = order;

        public bool Called;
        public Quaternion Rotation;
        public Action? OnPlace;

        public void Place(Quaternion rotation)
        {
            _order.Add("placement");
            Called = true;
            Rotation = rotation;
            OnPlace?.Invoke();
        }
    }

    private sealed class FakeSession : ILocalPlayerTeleportSession
    {
        private readonly List<string> _order;

        public FakeSession(List<string> order) => _order = order;

        public int LoginCompleteCount;
        public Action? OnSend;

        public void SendLoginComplete()
        {
            _order.Add("login-complete");
            LoginCompleteCount++;
            OnSend?.Invoke();
        }
    }

    private sealed class FakeNetworkSink : ILocalPlayerTeleportNetworkSink
    {
        public List<uint> Starts { get; } = [];
        public int FirstEntryCompletions;
        public int LoginTunnelArms;
        public void OnTeleportStarted(uint sequence) => Starts.Add(sequence);
        public void OfferDestination(
            RuntimeTeleportDestination destination,
            bool teleportTimestampAdvanced)
        {
        }
        public void OnLocalPlayerFirstEntryCompleted() => FirstEntryCompletions++;
        public void ArmLoginTunnel() => LoginTunnelArms++;
        public int LogoutRequests;
        public void RequestLogout() => LogoutRequests++;
        public void ResetSession()
        {
        }
        public void ResetGenerationPresentation()
        {
        }
    }

    [Fact]
    public void PortalCues_FireOnTheSequencersOwnSoundEvents_NotOnTheTunnelVisuals()
    {
        var harness = new Harness();
        harness.Controller.OnTeleportStarted(21);

        harness.Presentation.Enqueue(TeleportAnimEvent.PlayEnterSound);
        harness.Controller.Tick(0.016f);
        Assert.Equal(["enter"], harness.Presentation.Cues);

        // The tunnel becoming visible must NOT emit a second cue.
        harness.Presentation.Enqueue(TeleportAnimEvent.EnterTunnel);
        harness.Controller.Tick(0.016f);
        Assert.Equal(["enter"], harness.Presentation.Cues);
        Assert.True(harness.Presentation.IsPortalViewportVisible);

        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);
        harness.Controller.Tick(0.016f);
        Assert.Equal(["enter", "exit"], harness.Presentation.Cues);
        Assert.False(harness.Presentation.IsPortalViewportVisible);
    }


    [Fact]
    public void LoginReveal_ArmsThePortalSpacePresentation_WithRetailCues()
    {
        var order = new List<string>();
        var harness = new Harness(worldReady: false, order: order);

        harness.Reveal.BeginLogin(0x20210001u);
        Assert.Equal(RuntimePortalKind.Login, harness.Reveal.Snapshot.Kind);

        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Mode.EnterPortalCount);
        Assert.Equal(Matrix4x4.Identity, harness.Presentation.BeginProjection);
        Assert.Equal(0x20210001u, harness.Controller.ActiveDestinationCell);

        harness.Presentation.Enqueue(TeleportAnimEvent.PlayEnterSound);
        harness.Controller.Tick(0.016f);
        Assert.Equal(["enter"], harness.Presentation.Cues);

        harness.Presentation.Enqueue(TeleportAnimEvent.EnterTunnel);
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Presentation.IsPortalViewportVisible);

        Assert.All(harness.Presentation.WorldReadyValues, value => Assert.False(value));
        Assert.Equal(0, harness.Session.LoginCompleteCount);

        harness.WorldReady = true;
        harness.Controller.Tick(0.016f);
        Assert.False(harness.Presentation.WorldReadyValues[^1]);

        harness.Controller.OnLocalPlayerFirstEntryCompleted();
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Presentation.WorldReadyValues[^1]);

        harness.Presentation.Enqueue(TeleportAnimEvent.Place);
        harness.Controller.Tick(0.016f);
        Assert.False(harness.Placement.Called);

        order.Clear();
        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);
        harness.Controller.Tick(0.016f);
        Assert.True(Index(order, "presentation-exit") < Index(order, "reservation-end"));
        Assert.True(Index(order, "reservation-end") < Index(order, "exit-cue"));
        Assert.Equal(["enter", "exit"], harness.Presentation.Cues);
        Assert.False(harness.Presentation.IsPortalViewportVisible);
        Assert.Single(harness.Streaming.ReservationEnds);

        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);
        harness.Controller.Tick(0.016f);
        Assert.Contains("enter-world", order);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
        Assert.True(harness.Reveal.Snapshot.Completed);
        Assert.Equal(0u, harness.Controller.ActiveDestinationCell);

        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Mode.EnterPortalCount);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
    }

    [Fact]
    public void LoginPump_SendsLoginCompleteOnlyAtThePresentationEnd()
    {
        var harness = new Harness(worldReady: true);
        harness.Reveal.BeginLogin(0x20210001u);
        harness.Controller.OnLocalPlayerFirstEntryCompleted();

        // Ticks before the FireLoginComplete edge never send.
        for (int i = 0; i < 5; i++)
            harness.Controller.Tick(0.016f);
        Assert.Equal(0, harness.Session.LoginCompleteCount);

        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);
        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
        Assert.True(harness.Reveal.Snapshot.Completed);
    }

    [Fact]
    public void RealTeleportStart_SupersedesTheLoginPresentation()
    {
        var order = new List<string>();
        var harness = new Harness(worldReady: true, order: order);
        harness.Reveal.BeginLogin(0x20210001u);
        harness.Controller.OnLocalPlayerFirstEntryCompleted();
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.EnterTunnel);
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Presentation.IsPortalViewportVisible);

        harness.Controller.OnTeleportStarted(3);
        Assert.Contains("presentation-reset", order);
        Assert.True(harness.Controller.IsActive);
        Assert.Equal(0, harness.Session.LoginCompleteCount);

        int loginCompletes = harness.Session.LoginCompleteCount;
        harness.Controller.Tick(0.016f);
        Assert.Equal(loginCompletes, harness.Session.LoginCompleteCount);
    }


    [Fact]
    public void LoginPlaceEdge_ReleasesWorldSimulationWhileTunnelInFront()
    {
        var harness = new Harness(worldReady: false);
        harness.Reveal.BeginLogin(0x20210001u);
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.EnterTunnel);
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Presentation.IsPortalViewportVisible);
        Assert.False(harness.Transit.IsWorldSimulationAvailable);

        harness.WorldReady = true;
        harness.Controller.OnLocalPlayerFirstEntryCompleted();
        harness.Controller.Tick(0.016f);

        harness.Presentation.Enqueue(TeleportAnimEvent.Place);
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Presentation.IsPortalViewportVisible);
        Assert.True(harness.Transit.IsWorldSimulationAvailable);
        Assert.True(harness.Transit.Snapshot.Materialized);
        Assert.False(harness.Transit.Snapshot.Completed);
        Assert.False(harness.Placement.Called);

        // Swap edge: the tunnel retires on this same tick and the world is
        // already available — no frame can exist where neither presents.
        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);
        harness.Controller.Tick(0.016f);
        Assert.False(harness.Presentation.IsPortalViewportVisible);
        Assert.True(harness.Transit.IsWorldSimulationAvailable);

        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);
        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
        Assert.True(harness.Reveal.Snapshot.Completed);
    }

    [Fact]
    public void LoginCover_PresentsPortalViewportShapeUntilChaseModeEntered()
    {
        var harness = new Harness(worldReady: false);
        var login = new StubLoginState { IsWaitingForLogin = true };
        var source = new LocalPlayerTeleportRenderStateSource(
            harness.Controller, login);

        Assert.False(harness.Presentation.IsPortalViewportVisible);
        Assert.True(source.IsPortalViewportVisible);

        harness.Reveal.BeginLogin(0x20210001u);
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.EnterTunnel);
        harness.Controller.Tick(0.016f);
        login.IsWaitingForLogin = false;
        Assert.True(source.IsPortalViewportVisible);

        harness.WorldReady = true;
        harness.Controller.OnLocalPlayerFirstEntryCompleted();
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);
        harness.Controller.Tick(0.016f);
        Assert.False(source.IsPortalViewportVisible);
    }


    [Fact]
    public void ArmLoginTunnel_ShowsTunnelAndPlaysCueSynchronously()
    {
        var order = new List<string>();
        var harness = new Harness(worldReady: false, order: order);

        harness.Presentation.Enqueue(
            TeleportAnimEvent.PlayEnterSound,
            TeleportAnimEvent.EnterTunnel);
        harness.Controller.ArmLoginTunnel();

        Assert.Contains("presentation-begin", order);
        Assert.Equal(["enter"], harness.Presentation.Cues);
        Assert.True(harness.Presentation.IsPortalViewportVisible);

        // Pre-reveal ticks keep the tunnel animating (worldReady pinned
        // false — the sequencer holds in Tunnel) and never re-fire the cue.
        harness.Controller.Tick(0.016f);
        harness.Controller.Tick(0.016f);
        Assert.Equal(["enter"], harness.Presentation.Cues);
        Assert.True(harness.Presentation.IsPortalViewportVisible);
        Assert.All(
            harness.Presentation.WorldReadyValues,
            value => Assert.False(value));
        Assert.Contains("tunnel-tick", order);

        int begins = order.Count(entry => entry == "presentation-begin");
        harness.Controller.ArmLoginTunnel();
        Assert.Equal(begins, order.Count(entry => entry == "presentation-begin"));
    }

    [Fact]
    public void ArmedLoginTunnel_IsAdoptedByTheRevealWithoutRestartOrSecondCue()
    {
        var order = new List<string>();
        var harness = new Harness(worldReady: false, order: order);
        harness.Presentation.Enqueue(
            TeleportAnimEvent.PlayEnterSound,
            TeleportAnimEvent.EnterTunnel);
        harness.Controller.ArmLoginTunnel();
        int beginsAtArm = order.Count(entry => entry == "presentation-begin");

        harness.Reveal.BeginLogin(0x20210001u);
        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Mode.EnterPortalCount);
        Assert.Equal(
            beginsAtArm,
            order.Count(entry => entry == "presentation-begin"));
        Assert.Equal(["enter"], harness.Presentation.Cues);
        Assert.True(harness.Presentation.IsPortalViewportVisible);
        Assert.Equal(0x20210001u, harness.Controller.ActiveDestinationCell);

        harness.WorldReady = true;
        harness.Controller.OnLocalPlayerFirstEntryCompleted();
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Presentation.WorldReadyValues[^1]);
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);
        harness.Controller.Tick(0.016f);
        Assert.Equal(["enter", "exit"], harness.Presentation.Cues);
        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);
        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
        Assert.True(harness.Reveal.Snapshot.Completed);
    }

    [Fact]
    public void ArmedLoginTunnel_IsAdoptedEvenWhenLoginModeEntryIsBlocked()
    {
        // Indoor DreamWeave strand: first-entry stays unpublished so
        // TryEnterPortalSpaceForLogin fails. The armed tunnel must still be
        // adopted so presentation can Place once placement completes.
        var order = new List<string>();
        var harness = new Harness(worldReady: false, order: order);
        harness.Mode.BlockLoginEnter = true;
        harness.Presentation.Enqueue(
            TeleportAnimEvent.PlayEnterSound,
            TeleportAnimEvent.EnterTunnel);
        harness.Controller.ArmLoginTunnel();
        int beginsAtArm = order.Count(entry => entry == "presentation-begin");

        harness.Reveal.BeginLogin(0x526A0293u);
        harness.Controller.Tick(0.016f);
        Assert.Equal(0, harness.Mode.EnterPortalCount);
        Assert.Equal(1, harness.Mode.EnterPortalForLoginCount);
        Assert.Equal(
            beginsAtArm,
            order.Count(entry => entry == "presentation-begin"));
        Assert.Equal(0x526A0293u, harness.Controller.ActiveDestinationCell);
        Assert.True(harness.Presentation.IsPortalViewportVisible);

        harness.Mode.BlockLoginEnter = false;
        harness.WorldReady = true;
        harness.Controller.OnLocalPlayerFirstEntryCompleted();
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Presentation.WorldReadyValues[^1]);
        harness.Presentation.Enqueue(TeleportAnimEvent.Place);
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);
        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
        Assert.True(harness.Reveal.Snapshot.Completed);
    }

    [Fact]
    public void ArmedLoginTunnel_DisarmsWhenTheEnterFallsBackToSelection()
    {
        var order = new List<string>();
        var harness = new Harness(worldReady: false, order: order);
        harness.Presentation.Enqueue(
            TeleportAnimEvent.PlayEnterSound,
            TeleportAnimEvent.EnterTunnel);
        harness.Controller.ArmLoginTunnel();
        Assert.True(harness.Presentation.IsPortalViewportVisible);

        harness.SelectionLifecycle =
            RuntimeCharacterSelectionLifecycle.AwaitingSelection;
        harness.Controller.Tick(0.016f);
        Assert.False(harness.Presentation.IsPortalViewportVisible);
        Assert.Contains("presentation-reset", order);

        // A later successful Enter arms a fresh tunnel.
        harness.SelectionLifecycle =
            RuntimeCharacterSelectionLifecycle.EnteringWorld;
        harness.Presentation.Enqueue(
            TeleportAnimEvent.PlayEnterSound,
            TeleportAnimEvent.EnterTunnel);
        harness.Controller.ArmLoginTunnel();
        Assert.True(harness.Presentation.IsPortalViewportVisible);
        Assert.Equal(["enter", "enter"], harness.Presentation.Cues);
    }

    [Fact]
    public void ArmedLoginTunnel_PresentsTunnelNotBlack_FromTheClickFrame()
    {
        var harness = new Harness(worldReady: false);
        var login = new StubLoginState { IsWaitingForLogin = true };
        var source = new LocalPlayerTeleportRenderStateSource(
            harness.Controller, login);

        harness.Presentation.Enqueue(
            TeleportAnimEvent.PlayEnterSound,
            TeleportAnimEvent.EnterTunnel);
        harness.Controller.ArmLoginTunnel();
        Assert.True(source.IsPortalViewportVisible);
        Assert.True(harness.Presentation.IsPortalViewportVisible);
    }


    [Fact]
    public void LogoutRequest_SendsWireThenHoldsThreeSeconds_ThenBeginsWormhole()
    {
        var order = new List<string>();
        var harness = new Harness(worldReady: true, order: order);

        Assert.True(harness.Controller.TryRequestLogout());
        Assert.Equal(1, harness.Logout.BeginCalls);
        Assert.Equal(RuntimeLogoutStage.Requested, harness.Transit.LogoutStage);
        Assert.Equal(1, harness.Input.EndCount);

        // The 3 s hold: no presentation, the server-broadcast LogOut motion
        // is playing in-world.
        harness.Controller.Tick(1.0f);
        harness.Controller.Tick(1.0f);
        Assert.DoesNotContain("presentation-begin-logout", order);
        Assert.Equal(RuntimeLogoutStage.Requested, harness.Transit.LogoutStage);

        // Hold elapses → the wormhole begins at WorldFadeOut with the enter
        // cue (BeginTeleportAnimation plays it unconditionally).
        harness.Presentation.Enqueue(TeleportAnimEvent.PlayEnterSound);
        harness.Controller.Tick(1.05f);
        Assert.Contains("presentation-begin-logout", order);
        Assert.Equal(
            RuntimeLogoutStage.PresentationActive,
            harness.Transit.LogoutStage);
        Assert.Equal(["enter"], harness.Presentation.Cues);

        // The tunnel edge arrives on its own sequencer event.
        harness.Presentation.Enqueue(TeleportAnimEvent.EnterTunnel);
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Presentation.IsPortalViewportVisible);
        Assert.All(
            harness.Presentation.WorldReadyValues,
            value => Assert.False(value));
    }

    [Fact]
    public void LogoutConfirmation_RunsTheHandoffOnceAndCompletesTheLifecycle()
    {
        var order = new List<string>();
        var harness = new Harness(worldReady: true, order: order);
        Assert.True(harness.Controller.TryRequestLogout());
        harness.Presentation.Enqueue(
            TeleportAnimEvent.PlayEnterSound,
            TeleportAnimEvent.EnterTunnel);
        harness.Controller.Tick(3.05f);
        Assert.True(harness.Presentation.IsPortalViewportVisible);

        harness.Logout.IsCharacterLogOffConfirmed = true;
        harness.Logout.OnComplete = () =>
            harness.Controller.ResetGenerationPresentation();
        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Logout.CompleteCalls);
        Assert.Equal(1, harness.Streaming.ResetCalls);
        Assert.Equal(RuntimeLogoutStage.None, harness.Transit.LogoutStage);
        // The transaction's world reset retired the presentation.
        Assert.Contains("presentation-reset", order);
        Assert.False(harness.Presentation.IsPortalViewportVisible);
        // No exit cue on logout — the swap preempts the tail.
        Assert.Equal(["enter"], harness.Presentation.Cues);

        // The retired lifecycle stays quiet.
        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Logout.CompleteCalls);
    }

    [Fact]
    public void LogoutConfirmation_WaitsForOldStreamingWindowBeforeFreshGeneration()
    {
        var harness = new Harness(worldReady: true);
        Assert.True(harness.Controller.TryRequestLogout());
        harness.Logout.IsCharacterLogOffConfirmed = true;
        harness.Streaming.ResetResult = false;
        harness.Logout.OnComplete = () =>
            harness.Controller.ResetGenerationPresentation();

        harness.Controller.Tick(0.016f);

        Assert.Equal(RuntimeLogoutStage.Confirmed, harness.Transit.LogoutStage);
        Assert.Equal(0, harness.Logout.CompleteCalls);
        Assert.Equal(1, harness.Streaming.ResetCalls);

        harness.Streaming.ResetResult = true;
        harness.Controller.Tick(0.016f);

        Assert.Equal(RuntimeLogoutStage.None, harness.Transit.LogoutStage);
        Assert.Equal(1, harness.Logout.CompleteCalls);
        Assert.Equal(2, harness.Streaming.ResetCalls);
    }

    [Fact]
    public void LogoutConfirmationBeforeHoldEnd_SkipsTheWormholeEntirely()
    {
        var order = new List<string>();
        var harness = new Harness(worldReady: true, order: order);
        Assert.True(harness.Controller.TryRequestLogout());

        harness.Logout.IsCharacterLogOffConfirmed = true;
        harness.Logout.OnComplete = () =>
            harness.Controller.ResetGenerationPresentation();
        harness.Controller.Tick(0.016f);
        Assert.Equal(1, harness.Logout.CompleteCalls);
        Assert.Equal(RuntimeLogoutStage.None, harness.Transit.LogoutStage);
        Assert.DoesNotContain("presentation-begin-logout", order);
        Assert.Empty(harness.Presentation.Cues);
    }

    [Fact]
    public void LogoutRequest_RefusedWireRollsTheLifecycleBack()
    {
        var harness = new Harness(worldReady: true);
        harness.Logout.BeginResult = false;

        Assert.False(harness.Controller.TryRequestLogout());
        Assert.Equal(RuntimeLogoutStage.None, harness.Transit.LogoutStage);
        Assert.Equal(1, harness.Logout.BeginCalls);
    }

    [Fact]
    public void LogoutRequest_UsesThePlayerKillerHold()
    {
        var harness = new Harness(worldReady: true);
        harness.Logout.IsLocalPlayerKiller = true;
        Assert.True(harness.Controller.TryRequestLogout());

        harness.Controller.Tick(3.5f);
        Assert.Equal(RuntimeLogoutStage.Requested, harness.Transit.LogoutStage);
        harness.Controller.Tick(19.6f);
        Assert.Equal(
            RuntimeLogoutStage.PresentationActive,
            harness.Transit.LogoutStage);
    }

    [Fact]
    public void TeleportStart_IsIgnoredWhileLogoutIsActive()
    {
        var harness = new Harness(worldReady: true);
        Assert.True(harness.Controller.TryRequestLogout());

        harness.Controller.OnTeleportStarted(7);
        Assert.False(harness.Controller.IsActive);
        Assert.False(harness.Transit.HasPendingTeleportStart);
        Assert.Equal(RuntimeLogoutStage.Requested, harness.Transit.LogoutStage);
    }

    [Fact]
    public void LogoutRequest_RefusedDuringTeleportOrLogin()
    {
        var harness = new Harness(worldReady: true);
        harness.Controller.OnTeleportStarted(3);
        Assert.False(harness.Controller.TryRequestLogout());
        Assert.Equal(0, harness.Logout.BeginCalls);

        var loginHarness = new Harness(worldReady: false);
        loginHarness.Presentation.Enqueue(
            TeleportAnimEvent.PlayEnterSound,
            TeleportAnimEvent.EnterTunnel);
        loginHarness.Controller.ArmLoginTunnel();
        Assert.False(loginHarness.Controller.TryRequestLogout());
        Assert.Equal(0, loginHarness.Logout.BeginCalls);
    }

    private sealed class StubLoginState : IRenderLoginStateSource
    {
        public bool IsWaitingForLogin { get; set; }
    }

    private sealed class FakeLoginLifecycleSource
        : ILocalPlayerLoginLifecycleSource
    {
        public RuntimeCharacterSelectionLifecycle SelectionLifecycle
        {
            get;
            set;
        } = RuntimeCharacterSelectionLifecycle.EnteringWorld;
    }

    private sealed class FakeLogoutOperations : ILocalPlayerLogoutOperations
    {
        public bool IsLocalPlayerKiller { get; set; }
        public bool BeginResult = true;
        public int BeginCalls;
        public bool IsCharacterLogOffConfirmed { get; set; }
        public bool CompleteResult = true;
        public int CompleteCalls;
        public Action? OnComplete;

        public bool BeginCharacterLogOff()
        {
            BeginCalls++;
            return BeginResult;
        }

        public bool CompleteCharacterLogOff()
        {
            CompleteCalls++;
            OnComplete?.Invoke();
            return CompleteResult;
        }
    }

    private sealed class FakePresentation : ILocalPlayerTeleportPresentation
    {
        private readonly List<string> _order;
        private readonly Queue<IReadOnlyList<TeleportAnimEvent>> _events = new();

        public FakePresentation(List<string> order) => _order = order;

        public bool IsPortalViewportVisible { get; private set; }
        public int CurrentTunnelFrame => 72;
        public Matrix4x4? BeginProjection;
        public bool EmitPlaceWhenReady;
        public readonly List<bool> WorldReadyValues = new();
        public readonly List<bool> WaitCueValues = new();
        public int DisposeFailuresRemaining;
        public int DisposeCount;

        public void Enqueue(params TeleportAnimEvent[] events) => _events.Enqueue(events);

        public void Begin(Matrix4x4 projection)
        {
            BeginProjection = projection;
            _order.Add("presentation-begin");
        }

        public void BeginLogout(Matrix4x4 projection)
        {
            BeginProjection = projection;
            _order.Add("presentation-begin-logout");
        }

        public (TeleportAnimSnapshot Snapshot, IReadOnlyList<TeleportAnimEvent> Events)
            Tick(float deltaSeconds, bool worldReady)
        {
            WorldReadyValues.Add(worldReady);
            if (_events.Count > 0)
                return (default, _events.Dequeue());
            if (EmitPlaceWhenReady && worldReady)
            {
                EmitPlaceWhenReady = false;
                return (default, new[] { TeleportAnimEvent.Place });
            }

            return (default, Array.Empty<TeleportAnimEvent>());
        }

        public void TickTunnel(float deltaSeconds) => _order.Add("tunnel-tick");
        public readonly List<string> Cues = new();
        public void PlayEnterCue() => Cues.Add("enter");
        public void PlayExitCue()
        {
            Cues.Add("exit");
            _order.Add("exit-cue");
        }
        public void EnterTunnel() => IsPortalViewportVisible = true;
        public void ExitTunnel()
        {
            IsPortalViewportVisible = false;
            _order.Add("presentation-exit");
        }
        public void SetWaitCue(bool visible) => WaitCueValues.Add(visible);

        public void Reset()
        {
            _order.Add("presentation-reset");
            IsPortalViewportVisible = false;
            BeginProjection = null;
            _events.Clear();
        }

        public Matrix4x4 ApplyViewPlane(Matrix4x4 projection) => projection;
        public ICamera ApplyViewPlane(ICamera camera) => camera;
        public void DrawPortalViewport(int width, int height, Matrix4x4 projection) { }
        public void Dispose()
        {
            DisposeCount++;
            if (DisposeFailuresRemaining-- > 0)
                throw new InvalidOperationException("injected disposal failure");
        }
    }
}
