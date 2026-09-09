using System.Net;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests.Session;

public sealed class RuntimeLiveEntitySessionControllerTests
{
    [Fact]
    public void DirectSinkOwnsCanonicalCreateUpdateDeleteWithoutProjection()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        CommitLandblockCollision(runtime, 0x01010000u);
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection());
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            Spawn(0x70000001u, incarnation: 1);

        sink.Spawned(spawn);
        drive.DriveAll();
        Assert.Equal(0, drive.PendingCount);
        DrainPlacementFifo(runtime);
        sink.PositionUpdated(new WorldSession.EntityPositionUpdate(
            spawn.Guid,
            spawn.Position!.Value with
            {
                PositionX = 20f,
            },
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 0,
            ForcePositionSequence: 0));

        Assert.Equal(1, runtime.Entities.Count);
        Assert.Equal(1, runtime.Inventory.ObjectCount);
        Assert.True(
            runtime.EntityObjects.Entities.TryGetActive(
                spawn.Guid,
                out RuntimeEntityRecord canonical));
        Assert.Equal(
            20f,
            canonical.Snapshot.Position!.Value.PositionX);

        sink.Deleted(new DeleteObject.Parsed(spawn.Guid, 1));

        Assert.Equal(0, runtime.Entities.Count);
        Assert.Equal(0, runtime.Inventory.ObjectCount);
        Assert.Equal(
            0,
            runtime.EntityObjects.Entities.PendingTeardownCount);
    }

    [Fact]
    public void ContentLessDirectSink_KeepsPreFlipLegacyRegistration()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session);
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            Spawn(0x70000003u, incarnation: 1);

        sink.Spawned(spawn);

        Assert.True(
            runtime.EntityObjects.Entities.TryGetActive(
                spawn.Guid,
                out RuntimeEntityRecord canonical));
        Assert.Equal(
            spawn.Position!.Value.LandblockId,
            canonical.FullCellId);
        RuntimeEntityObjectOwnershipSnapshot ownership =
            runtime.EntityObjects.CaptureOwnership();
        Assert.Equal(0, ownership.InitialCreateResidenceLeaseCount);
        Assert.Equal(0, ownership.FirstEntryDrivePendingCount);
        Assert.Equal(1, runtime.Entities.Count);
        Assert.Equal(1, runtime.Inventory.ObjectCount);

        // Position packets flow the ordinary immediate path — nothing is
        // FIFO'd behind a pending residence.
        sink.PositionUpdated(new WorldSession.EntityPositionUpdate(
            spawn.Guid,
            spawn.Position!.Value with
            {
                PositionX = 20f,
            },
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 0,
            ForcePositionSequence: 0));
        Assert.Equal(
            20f,
            canonical.Snapshot.Position!.Value.PositionX);

        sink.Deleted(new DeleteObject.Parsed(spawn.Guid, 1));
        Assert.Equal(0, runtime.Entities.Count);
        Assert.Equal(
            0,
            runtime.EntityObjects.Entities.PendingTeardownCount);
    }

    [Fact]
    public void DirectSinkCompletesExactPortalAndSendsLoginComplete()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        const uint playerGuid = 0x50000001u;
        runtime.PlayerIdentity.ServerGuid = playerGuid;
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var gameActions = new List<byte[]>();
        session.GameActionCapture = body => gameActions.Add(body);
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session);
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            Spawn(playerGuid, incarnation: 1);
        sink.Spawned(spawn);

        Assert.Equal(2, gameActions.Count);
        Assert.Equal(GameActionLoginComplete.Build(), gameActions[0]);
        Assert.Equal(ClientCommandRequests.BuildHouseQuery(1u), gameActions[1]);
        sink.Spawned(spawn);
        Assert.Equal(2, gameActions.Count);
        gameActions.Clear();

        sink.TeleportStarted(1u);
        sink.PositionUpdated(new WorldSession.EntityPositionUpdate(
            playerGuid,
            spawn.Position!.Value with
            {
                LandblockId = 0x01020001u,
                PositionX = 30f,
            },
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 1,
            ForcePositionSequence: 0));

        Assert.Single(gameActions);
        Assert.Equal(GameActionLoginComplete.Build(), gameActions[0]);
        Assert.True(runtime.TransitOwner.CaptureOwnership().IsSessionIdle);
        Assert.True(runtime.Portal.Snapshot.Completed);
        Assert.Equal(0x01020001u, runtime.Portal.Snapshot.DestinationCell);
    }

    [Fact]
    public void DirectSinkContentLessSpawn_InvokesOnLoginCompleteSentHookExactlyOnce()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        const uint playerGuid = 0x50000004u;
        runtime.PlayerIdentity.ServerGuid = playerGuid;
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        session.GameActionCapture = _ => { };
        int hookInvocations = 0;
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            onLoginCompleteSent: () => hookInvocations++);
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            Spawn(playerGuid, incarnation: 1);

        sink.Spawned(spawn);
        Assert.Equal(1, hookInvocations);

        sink.Spawned(spawn);
        Assert.Equal(1, hookInvocations);
    }

    [Fact]
    public void DirectSinkPortalCompletion_InvokesOnLoginCompleteSentHookAfterTeleportEnds()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        const uint playerGuid = 0x50000005u;
        runtime.PlayerIdentity.ServerGuid = playerGuid;
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        session.GameActionCapture = _ => { };
        int hookInvocations = 0;
        bool? sessionIdleAtSecondHookInvocation = null;
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            onLoginCompleteSent: () =>
            {
                hookInvocations++;
                if (hookInvocations == 2)
                {
                    sessionIdleAtSecondHookInvocation =
                        runtime.TransitOwner.CaptureOwnership().IsSessionIdle;
                }
            });
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            Spawn(playerGuid, incarnation: 1);
        sink.Spawned(spawn);
        Assert.Equal(1, hookInvocations);

        sink.TeleportStarted(1u);
        sink.PositionUpdated(new WorldSession.EntityPositionUpdate(
            playerGuid,
            spawn.Position!.Value with
            {
                LandblockId = 0x01020001u,
                PositionX = 30f,
            },
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 1,
            ForcePositionSequence: 0));

        Assert.Equal(2, hookInvocations);
        Assert.True(runtime.Portal.Snapshot.Completed);
        Assert.True(sessionIdleAtSecondHookInvocation);
    }

    [Fact]
    public void DirectSinkProjectsAcceptedLocalWorldStateThroughOneHostSeam()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        const uint playerGuid = 0x50000002u;
        runtime.PlayerIdentity.ServerGuid = playerGuid;
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        session.GameActionCapture = _ => { };
        var projection = new FixtureWorldProjection();
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: projection);
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            Spawn(playerGuid, incarnation: 1);

        sink.Spawned(spawn);
        sink.TeleportStarted(1u);
        sink.PositionUpdated(new WorldSession.EntityPositionUpdate(
            playerGuid,
            spawn.Position!.Value with
            {
                LandblockId = 0x01020001u,
                PositionX = 30f,
            },
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 1,
            ForcePositionSequence: 0));

        Assert.Equal(1, projection.SpawnCount);
        Assert.Equal(1, projection.PositionCount);
        Assert.Equal(1, projection.TeleportStartCount);
        Assert.Equal(1, projection.PrepareCount);
        Assert.True(projection.LastSpawnWasLocal);
        Assert.True(projection.LastPositionWasLocal);
        Assert.Equal(
            PositionTimestampDisposition.Apply,
            projection.LastPositionDisposition);
        Assert.Equal(playerGuid, projection.LastRecord?.ServerGuid);
        Assert.Equal(0x01020001u, projection.LastDestination.CellId);
        Assert.True(runtime.TransitOwner.CaptureOwnership().IsSessionIdle);
        Assert.True(runtime.Portal.Snapshot.Completed);
    }

    [Fact]
    public void ForcePositionWithoutAnAcceptedPositionDrive_StillCentersAndFallsBackToProjectPosition()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        const uint playerGuid = 0x50000005u;
        runtime.PlayerIdentity.ServerGuid = playerGuid;
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        session.GameActionCapture = _ => { };
        var projection = new FixtureWorldProjection();
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: projection);
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            Spawn(playerGuid, incarnation: 1);

        sink.Spawned(spawn);
        Assert.Equal(1, projection.SpawnCount);
        Assert.Equal(0, projection.CenterOnForceCount);

        sink.PositionUpdated(new WorldSession.EntityPositionUpdate(
            playerGuid,
            spawn.Position!.Value with
            {
                PositionX = 30f,
            },
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 0,
            ForcePositionSequence: 1));

        Assert.Equal(1, projection.CenterOnForceCount);
        Assert.Equal(playerGuid, projection.LastCenteredRecord?.ServerGuid);
        // The fallback ran: ProjectPosition observed this exact
        // ForcePosition disposition, not just the earlier Apply-shaped spawn
        // follow-up.
        Assert.Equal(1, projection.PositionCount);
        Assert.Equal(
            PositionTimestampDisposition.ForcePosition,
            projection.LastPositionDisposition);
    }

    [Fact]
    public void DirectSink_D5_StandaloneParentEventCommitsChildToParentsExactCell()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        CommitLandblockCollision(runtime, 0x01010000u);
        CommitLandblockCollision(runtime, 0x01020000u);
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection());
        LiveEntitySessionSink sink = controller.CreateSink();

        const uint parentGuid = 0x70000020u;
        const uint childGuid = 0x70000021u;
        sink.Spawned(SpawnAt(parentGuid, incarnation: 1, 0x01010001u));
        drive.DriveAll();
        DrainPlacementFifo(runtime);
        sink.Spawned(SpawnAt(childGuid, incarnation: 1, 0x01020001u));
        drive.DriveAll();
        DrainPlacementFifo(runtime);

        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            parentGuid, out RuntimeEntityRecord parent));
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.NotEqual(0u, parent.FullCellId);
        Assert.NotEqual(parent.FullCellId, child.FullCellId);

        sink.ParentUpdated(new ParentEvent.Parsed(
            parentGuid,
            childGuid,
            ParentLocation: 0u,
            PlacementId: 0u,
            ParentInstanceSequence: 1,
            ChildPositionSequence: 2));

        Assert.Equal(parent.FullCellId, child.FullCellId);
        Assert.NotEqual(0u, child.FullCellId);
    }

    [Fact]
    public void DirectSink_D5_DeferredParentEventCommitsOnceTheParentBecomesAddressable()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        CommitLandblockCollision(runtime, 0x01010000u);
        CommitLandblockCollision(runtime, 0x01020000u);
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection());
        LiveEntitySessionSink sink = controller.CreateSink();

        const uint parentGuid = 0x70000022u;
        const uint childGuid = 0x70000023u;
        sink.Spawned(SpawnAt(childGuid, incarnation: 1, 0x01020001u));
        drive.DriveAll();
        DrainPlacementFifo(runtime);
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        uint childOriginalCell = child.FullCellId;

        sink.ParentUpdated(new ParentEvent.Parsed(
            parentGuid,
            childGuid,
            ParentLocation: 0u,
            PlacementId: 0u,
            ParentInstanceSequence: 1,
            ChildPositionSequence: 2));
        Assert.Equal(childOriginalCell, child.FullCellId);

        sink.Spawned(SpawnAt(parentGuid, incarnation: 1, 0x01010001u));
        drive.DriveAll();
        DrainPlacementFifo(runtime);

        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            parentGuid, out RuntimeEntityRecord parent));
        Assert.Equal(parent.FullCellId, child.FullCellId);
        Assert.NotEqual(0u, child.FullCellId);
    }

    [Fact]
    public void FirstEntryDriveServesOneRouteAtATimeAndScopesClearToTheOwner()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        var routeA = new object();
        var routeB = new object();

        drive.AttachRoute(routeA);
        // Re-attaching the same owner is a no-op; a SECOND route asserts.
        drive.AttachRoute(routeA);
        Assert.Throws<InvalidOperationException>(
            () => drive.AttachRoute(routeB));

        _ = runtime.EntityObjects.RegisterEntityWithInitialResidence(
            Spawn(0x70000004u, incarnation: 1),
            isLocalPlayer: false);
        Assert.Equal(1, drive.PendingCount);

        drive.DetachRoute(routeB);
        Assert.Equal(1, drive.PendingCount);

        drive.DetachRoute(routeA);
        Assert.Equal(0, drive.PendingCount);
        // After the owner detached, a replacement route may attach.
        drive.AttachRoute(routeB);
        drive.DetachRoute(routeB);
    }

    [Fact]
    public void FirstEntryDriveSignalsLocalCompletionOnceAfterCanonicalPlacement()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        const uint playerGuid = 0x50000003u;
        runtime.PlayerIdentity.ServerGuid = playerGuid;
        CommitLandblockCollision(runtime, 0x01010000u);
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        var route = new object();
        int completed = 0;
        drive.AttachRoute(route, record =>
        {
            Assert.Equal(playerGuid, record.ServerGuid);
            completed++;
        });
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection());
        LiveEntitySessionSink sink = controller.CreateSink();

        sink.Spawned(Spawn(playerGuid, incarnation: 1));
        Assert.Equal(0, completed);

        DrainFirstEntry(runtime, drive);

        Assert.Equal(1, completed);
        Assert.Equal(0, drive.PendingCount);
        drive.DetachRoute(route);
    }

    [Fact]
    public void AcceptedRemotePosition_AdvancesCanonicalResidencyInANoWindowHost()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        CommitLandblockCollision(runtime, 0x01010000u);
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection());
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            SpawnAt(0x70000020u, incarnation: 1, 0x01010001u);

        sink.Spawned(spawn);
        DrainFirstEntry(runtime, drive);

        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            spawn.Guid,
            out RuntimeEntityRecord remote));
        uint placedCell = remote.FullCellId;
        Assert.NotEqual(0u, placedCell);

        const uint movedCell = 0x01010013u;
        Assert.NotEqual(movedCell, placedCell);
        sink.PositionUpdated(PositionUpdate(
            spawn.Guid,
            movedCell,
            positionX: 40f,
            positionSequence: 2));

        Assert.Equal(movedCell, remote.FullCellId);
        Assert.Equal(0x0101FFFFu, remote.CanonicalLandblockId);
        // The bot-visible projection, which is the observable this defect
        // actually broke.
        Assert.True(runtime.Entities.TryGet(
            spawn.Guid,
            out RuntimeEntitySnapshot view));
        Assert.Equal(movedCell, view.CellId);
    }

    [Fact]
    public void AcceptedLocalPlayerPosition_AdvancesCanonicalResidencyInANoWindowHost()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        const uint playerGuid = 0x50000020u;
        runtime.PlayerIdentity.ServerGuid = playerGuid;
        CommitLandblockCollision(runtime, 0x01010000u);
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection());
        LiveEntitySessionSink sink = controller.CreateSink();

        sink.Spawned(SpawnAt(playerGuid, incarnation: 1, 0x01010001u));
        DrainFirstEntry(runtime, drive);

        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            playerGuid,
            out RuntimeEntityRecord player));
        uint placedCell = player.FullCellId;
        Assert.NotEqual(0u, placedCell);

        const uint movedCell = 0x01010021u;
        Assert.NotEqual(movedCell, placedCell);
        sink.PositionUpdated(PositionUpdate(
            playerGuid,
            movedCell,
            positionX: 55f,
            positionSequence: 2));

        Assert.Equal(movedCell, player.FullCellId);
        Assert.Equal(0x0101FFFFu, player.CanonicalLandblockId);
    }

    [Fact]
    public void WireCellCommit_HonoursRejection_Residence_AndTheLandblockPreserveRule()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        CommitLandblockCollision(runtime, 0x01010000u);
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection());
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            SpawnAt(0x70000021u, incarnation: 1, 0x01010001u);

        // (b) The residence is open between Spawned and the drive's drain.
        sink.Spawned(spawn);
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            spawn.Guid,
            out RuntimeEntityRecord remote));
        Assert.True(runtime.EntityObjects.TryGetInitialCreateResidence(
            remote,
            out _));
        uint duringResidence = remote.FullCellId;
        sink.PositionUpdated(PositionUpdate(
            spawn.Guid,
            0x01010031u,
            positionX: 12f,
            positionSequence: 2));
        Assert.Equal(duringResidence, remote.FullCellId);

        DrainFirstEntry(runtime, drive);
        uint placedCell = remote.FullCellId;
        Assert.NotEqual(0u, placedCell);

        sink.PositionUpdated(PositionUpdate(
            spawn.Guid,
            0x01010011u,
            positionX: 13f,
            positionSequence: 1));
        Assert.Equal(placedCell, remote.FullCellId);

        Assert.True(runtime.EntityObjects.CommitWireCellRebucket(
            remote,
            0x0202FFFFu));
        Assert.Equal(placedCell, remote.FullCellId);
        Assert.Equal(0x0202FFFFu, remote.CanonicalLandblockId);
    }

    [Fact]
    public void InvalidPositionPayload_IsRefusedBeforeTheMerge_InANoWindowHost()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        CommitLandblockCollision(runtime, 0x01010000u);
        RuntimeFirstEntryDriveController drive = CreateDrive(runtime);
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session,
            worldProjection: new FixtureWorldProjection());
        LiveEntitySessionSink sink = controller.CreateSink();
        WorldSession.EntitySpawn spawn =
            SpawnAt(0x70000051u, incarnation: 1, 0x01010001u);

        sink.Spawned(spawn);
        DrainFirstEntry(runtime, drive);
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            spawn.Guid,
            out RuntimeEntityRecord remote));
        uint placedCell = remote.FullCellId;
        uint placedLandblock = remote.CanonicalLandblockId;
        float placedX = remote.Snapshot.Position!.Value.PositionX;
        Assert.NotEqual(0u, placedCell);

        const ushort refusedSequence = 7;

        // (a) LandblockId 0 — the withdrawal shape.
        sink.PositionUpdated(PositionUpdate(
            spawn.Guid,
            0u,
            positionX: 20f,
            positionSequence: refusedSequence));
        Assert.Equal(placedCell, remote.FullCellId);
        Assert.Equal(placedLandblock, remote.CanonicalLandblockId);
        Assert.Equal(placedX, remote.Snapshot.Position!.Value.PositionX);

        sink.PositionUpdated(PositionUpdate(
            spawn.Guid,
            0x01010031u,
            positionX: float.NaN,
            positionSequence: refusedSequence));
        Assert.Equal(placedCell, remote.FullCellId);
        Assert.Equal(placedX, remote.Snapshot.Position!.Value.PositionX);

        // (c) A non-finite velocity — the graphical gate's third term.
        WorldSession.EntityPositionUpdate infiniteVelocity = PositionUpdate(
            spawn.Guid,
            0x01010031u,
            positionX: 22f,
            positionSequence: refusedSequence) with
        {
            Velocity = new System.Numerics.Vector3(
                float.PositiveInfinity,
                0f,
                0f),
        };
        sink.PositionUpdated(infiniteVelocity);
        Assert.Equal(placedCell, remote.FullCellId);
        Assert.Equal(placedX, remote.Snapshot.Position!.Value.PositionX);

        const uint movedCell = 0x01010031u;
        sink.PositionUpdated(PositionUpdate(
            spawn.Guid,
            movedCell,
            positionX: 23f,
            positionSequence: refusedSequence));
        Assert.Equal(movedCell, remote.FullCellId);
        Assert.Equal(23f, remote.Snapshot.Position!.Value.PositionX);
    }

    [Fact]
    public void BoundProjectilePosition_CommitsNoWireCell_UnboundMissileDoes()
    {
        using StartedRuntime started = StartRuntime();
        GameRuntime runtime = started.Runtime;
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new FixtureTransport());
        var controller = new RuntimeLiveEntitySessionController(
            runtime,
            session);
        LiveEntitySessionSink sink = controller.CreateSink();

        const uint boundGuid = 0x70000041u;
        const uint unboundGuid = 0x70000042u;
        sink.Spawned(SpawnAt(boundGuid, incarnation: 1, 0x01010001u));
        sink.Spawned(SpawnAt(unboundGuid, incarnation: 1, 0x01010001u));
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            boundGuid,
            out RuntimeEntityRecord bound));
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            unboundGuid,
            out RuntimeEntityRecord unbound));

        foreach (RuntimeEntityRecord missile in new[] { bound, unbound })
        {
            runtime.EntityObjects.Entities.SetFinalPhysicsState(
                missile,
                missile.FinalPhysicsState | PhysicsStateFlags.Missile);
        }

        var body = new PhysicsBody
        {
            Position = new System.Numerics.Vector3(10f, 10f, 5f),
            Orientation = System.Numerics.Quaternion.Identity,
            LastUpdateTime = 1d,
            State = bound.FinalPhysicsState,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(0x01010001u, body.Position, body.Position);
        runtime.EntityObjects.Entities.SetPhysicsBody(bound, body);
        runtime.EntityObjects.Physics.BindProjectile(
            bound,
            body,
            new ProjectileCollisionSphere(
                System.Numerics.Vector3.Zero,
                0.1f,
                1f));
        Assert.NotNull(bound.Projectile);
        Assert.Null(unbound.Projectile);

        const uint movedCell = 0x01010012u;
        sink.PositionUpdated(PositionUpdate(
            boundGuid,
            movedCell,
            positionX: 30f,
            positionSequence: 2));
        sink.PositionUpdated(PositionUpdate(
            unboundGuid,
            movedCell,
            positionX: 30f,
            positionSequence: 2));

        Assert.Equal(0x01010001u, bound.FullCellId);
        Assert.Equal(movedCell, unbound.FullCellId);
    }

    private static WorldSession.EntityPositionUpdate PositionUpdate(
        uint guid,
        uint cellId,
        float positionX,
        ushort positionSequence) =>
        new(
            guid,
            new CreateObject.ServerPosition(
                cellId,
                positionX,
                10f,
                5f,
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
            ForcePositionSequence: 0);

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

    private static void CommitLandblockCollision(
        GameRuntime runtime,
        uint landblockId)
    {
        runtime.EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
            landblockId, 1UL);
        runtime.EntityObjects.Physics.Engine.AddLandblock(
            landblockId,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        runtime.EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
            landblockId, 1UL, ready: true);

        runtime.EntityObjects.Physics.ObserveLocalWorldFrame(
            landblockId | 0x0001u,
            teleportAdvanced: false);
    }

    private static RuntimeFirstEntryDriveController CreateDrive(
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
                out AcDream.Runtime.Physics.RuntimePlacementProjectionSnapshot head))
        {
            if (!runtime.EntityObjects.Physics.SetPosition
                    .AcknowledgeProjection(head.Token))
            {
                break;
            }
        }
    }

    private sealed class UnusedCollisionSource
        : AcDream.Content.IPreparedCollisionSource
    {
        public AcDream.Content.PreparedAssetPresence ProbeCollision(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            AcDream.Content.PreparedAssetPresence.Available;

        public AcDream.Content.PreparedCollisionReadResult<
            AcDream.Core.Physics.FlatSetupCollision> ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            AcDream.Content.PreparedCollisionReadResult<
                AcDream.Core.Physics.FlatSetupCollision>.Missing;

        public AcDream.Content.PreparedCollisionReadResult<
            AcDream.Core.Physics.FlatGfxObjCollisionAsset> ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            AcDream.Core.Physics.FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            AcDream.Core.Physics.FlatEnvCellTopology> ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionSourceStats CollisionStats =>
            default;

        public void Dispose()
        {
        }
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
                [new CharacterList.Character(0x50000001u, "Direct", 0u)],
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

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort incarnation) =>
        SpawnAt(guid, incarnation, 0x01010001u);

    private static WorldSession.EntitySpawn SpawnAt(
        uint guid,
        ushort incarnation,
        uint landblockId)
    {
        var position = new CreateObject.ServerPosition(
            landblockId,
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
            Instance: incarnation);
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
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
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

    private sealed class FixtureWorldProjection
        : IRuntimeDirectWorldProjection
    {
        public int SpawnCount { get; private set; }
        public int PositionCount { get; private set; }
        public int TeleportStartCount { get; private set; }
        public int PrepareCount { get; private set; }
        public int CenterOnForceCount { get; private set; }
        public RuntimeEntityRecord? LastCenteredRecord { get; private set; }
        public bool LastSpawnWasLocal { get; private set; }
        public bool LastPositionWasLocal { get; private set; }
        public PositionTimestampDisposition LastPositionDisposition
        {
            get;
            private set;
        }
        public RuntimeEntityRecord? LastRecord { get; private set; }
        public RuntimeTeleportDestination LastDestination { get; private set; }

        public void ProjectSpawn(
            RuntimeEntityRecord record,
            bool isLocalPlayer)
        {
            SpawnCount++;
            LastRecord = record;
            LastSpawnWasLocal = isLocalPlayer;
        }

        public void ProjectPosition(
            RuntimeEntityRecord record,
            bool isLocalPlayer,
            PositionTimestampDisposition disposition)
        {
            PositionCount++;
            LastRecord = record;
            LastPositionWasLocal = isLocalPlayer;
            LastPositionDisposition = disposition;
        }

        public void CenterOnAcceptedForcePosition(RuntimeEntityRecord record)
        {
            CenterOnForceCount++;
            LastCenteredRecord = record;
        }

        public void BeginTeleport() => TeleportStartCount++;

        public RuntimeDestinationReadiness PrepareDestination(
            long revealGeneration,
            RuntimeTeleportDestination destination,
            RuntimeWorldHostProjectionToken portal)
        {
            PrepareCount++;
            LastDestination = destination;
            bool indoor = (destination.CellId & 0xFFFFu) >= 0x0100u;
            return new RuntimeDestinationReadiness(
                revealGeneration,
                destination.CellId,
                indoor,
                IsUnhydratable: false,
                RequiredRenderRadius: indoor ? 0 : 1,
                IsRenderNeighborhoodReady: true,
                AreCompositeTexturesReady: true,
                IsCollisionReady: true);
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
