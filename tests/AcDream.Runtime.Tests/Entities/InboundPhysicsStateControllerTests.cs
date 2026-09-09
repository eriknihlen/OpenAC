using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Entities;

public sealed class InboundPhysicsStateControllerTests
{
    [Fact]
    public void SameGenerationCreate_RoutesChannelsIndependently()
    {
        var controller = new InboundPhysicsStateController();
        CreateObject.ServerPosition original = Position(0x0101FFFFu, 10f);
        CreateObject.ServerPosition incoming = Position(0x0101FFFFu, 20f);
        controller.AcceptCreate(Spawn(0x70000001u, 1, 10, 10, original, 0x408u));

        InboundCreateResult refresh = controller.AcceptCreate(
            Spawn(0x70000001u, 1, 9, 11, incoming, 0x448u));

        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration, refresh.Disposition);
        Assert.NotNull(refresh.SameGenerationEvents);
        SameGenerationCreateObjectEvents events = refresh.SameGenerationEvents.Value;
        Assert.True(controller.TryApplyPosition(
            events.Position!.Value,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition positionResult,
            out _,
            out _));
        Assert.Equal(PositionTimestampDisposition.Rejected, positionResult);
        Assert.True(controller.TryApplyState(events.State, out _));
        Assert.True(controller.TryGetSnapshot(0x70000001u, out WorldSession.EntitySpawn accepted));
        Assert.Equal(original, accepted.Position);
        Assert.Equal(0x448u, accepted.PhysicsState);
    }

    [Fact]
    public void PickupRetainsGeneration_AndFreshPositionReturnsToWorld()
    {
        var controller = new InboundPhysicsStateController();
        controller.AcceptCreate(Spawn(
            0x70000002u, 7, 20, 1, Position(0x0101FFFFu, 10f), 0x408u));

        Assert.True(controller.TryApplyPickup(
            new PickupEvent.Parsed(0x70000002u, 7, 21), out WorldSession.EntitySpawn pickedUp));
        Assert.Null(pickedUp.Position);

        var update = new WorldSession.EntityPositionUpdate(
            0x70000002u,
            Position(0x0101FFFFu, 30f),
            null,
            null,
            true,
            7,
            22,
            0,
            0);
        Assert.True(controller.TryApplyPosition(
            update, false, null, null, out var disposition, out WorldSession.EntitySpawn returned, out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.Equal(30f, returned.Position!.Value.PositionX);
    }

    [Fact]
    public void PositionPlacementAbsentAndPresentZeroBothApplyRetailZero()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn seed = Spawn(
            0x70000009u,
            7,
            20,
            1,
            Position(0x0101FFFFu, 10f),
            0x408u,
            motionTableId: null);
        seed = seed with
        {
            PlacementId = 7,
            Physics = seed.Physics!.Value with { AnimationFrame = 7 },
        };
        controller.AcceptCreate(seed);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                seed.Guid,
                Position(0x0101FFFFu, 20f),
                null,
                null,
                true,
                7,
                21,
                0,
                0),
            false,
            null,
            null,
            out PositionTimestampDisposition firstDisposition,
            out WorldSession.EntitySpawn first,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, firstDisposition);
        Assert.Equal((uint)0, first.PlacementId);
        Assert.Equal((uint)0, first.Physics!.Value.AnimationFrame);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                seed.Guid,
                Position(0x0101FFFFu, 30f),
                null,
                0,
                true,
                7,
                22,
                0,
                0),
            false,
            null,
            null,
            out PositionTimestampDisposition secondDisposition,
            out WorldSession.EntitySpawn second,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, secondDisposition);
        Assert.Equal((uint)0, second.PlacementId);
        Assert.Equal((uint)0, second.Physics!.Value.AnimationFrame);
    }

    [Fact]
    public void ApplyOnAnimatedEntity_NeverInstallsTheWirePlacementFrame()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn seed = Spawn(
            0x8000C001u,
            7,
            20,
            1,
            Position(0x0101FFFFu, 10f),
            0x408u,
            motionTableId: 0x09000001u);
        seed = seed with
        {
            PlacementId = 7,
            Physics = seed.Physics!.Value with { AnimationFrame = 7 },
        };
        controller.AcceptCreate(seed);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                seed.Guid,
                Position(0x0101FFFFu, 20f),
                Velocity: null,
                PlacementId: 5,
                IsGrounded: true,
                InstanceSequence: 7,
                PositionSequence: 21,
                TeleportSequence: 0,
                ForcePositionSequence: 0),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out _));

        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        // The gate above SetPlacementFrame never opened.
        Assert.Equal((uint)7, accepted.PlacementId);
        Assert.Equal((uint)7, accepted.Physics!.Value.AnimationFrame);
        // The rest of the merge is unaffected: pose and POSITION_TS still land.
        Assert.Equal(20f, accepted.Position!.Value.PositionX);
        Assert.Equal((ushort)21, accepted.PositionSequence);
        Assert.Null(accepted.ParentGuid);
    }

    [Fact]
    public void ApplyOnNonAnimatedEntity_InstallsTheWirePlacementFrame()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn seed = Spawn(
            0x8000C002u,
            7,
            20,
            1,
            Position(0x0101FFFFu, 10f),
            0x408u,
            motionTableId: null);
        seed = seed with
        {
            PlacementId = 7,
            Physics = seed.Physics!.Value with { AnimationFrame = 7 },
        };
        controller.AcceptCreate(seed);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                seed.Guid,
                Position(0x0101FFFFu, 20f),
                Velocity: null,
                PlacementId: 5,
                IsGrounded: true,
                InstanceSequence: 7,
                PositionSequence: 21,
                TeleportSequence: 0,
                ForcePositionSequence: 0),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out _));

        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.Equal((uint)5, accepted.PlacementId);
        Assert.Equal((uint)5, accepted.Physics!.Value.AnimationFrame);
    }

    [Fact]
    public void ForcePositionOnParentedLocalPlayer_RetainsTheParentAttachment()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn seed = WithTimestamps(
            Spawn(0x50000021u, 3, 10, 1, Position(0x0101FFFFu, 10f), 0x408u),
            teleport: 10,
            forcePosition: 0);
        seed = seed with
        {
            ParentGuid = 0x70004444u,
            ParentLocation = 9u,
            Physics = seed.Physics!.Value with
            {
                Parent = new PhysicsAttachment(0x70004444u, 9u),
            },
        };
        controller.AcceptCreate(seed);
        var preserved = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);

        Assert.True(controller.TryApplyPosition(
            PositionUpdate(
                seed.Guid,
                instance: 3,
                position: 9,
                teleport: 10,
                forcePosition: 1),
            isLocalPlayer: true,
            forcePositionRotation: preserved,
            currentLocalVelocity: Vector3.Zero,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out _));

        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);
        Assert.Equal(0x70004444u, accepted.ParentGuid);
        Assert.Equal(9u, accepted.ParentLocation);
        Assert.Equal(
            new PhysicsAttachment(0x70004444u, 9u),
            accepted.Physics!.Value.Parent);
        Assert.Equal(preserved.W, accepted.Position!.Value.RotationW);
        Assert.Equal(preserved.X, accepted.Position!.Value.RotationX);
    }

    [Theory]
    // guid, isLocalPlayer, animated, parented, force
    [InlineData(0x50000031u, true, true, true, false)]
    [InlineData(0x50000032u, true, true, false, false)]
    [InlineData(0x50000033u, true, false, true, false)]
    [InlineData(0x50000034u, true, false, false, false)]
    [InlineData(0x50000035u, true, true, true, true)]
    [InlineData(0x50000036u, true, true, false, true)]
    [InlineData(0x50000037u, true, false, true, true)]
    [InlineData(0x50000038u, true, false, false, true)]
    [InlineData(0x80000031u, false, true, true, false)]
    [InlineData(0x80000032u, false, true, false, false)]
    [InlineData(0x80000033u, false, false, true, false)]
    [InlineData(0x80000034u, false, false, false, false)]
    public void MergedPrePlacementFieldsUseSharedRetailFlags(
        uint guid,
        bool isLocalPlayer,
        bool animated,
        bool parented,
        bool force)
    {
        WorldSession.EntitySpawn seed = WithTimestamps(
            Spawn(
                guid,
                3,
                10,
                1,
                Position(0x0101FFFFu, 10f),
                0x408u,
                motionTableId: animated ? 0x09000001u : null),
            teleport: 10,
            forcePosition: 0);
        seed = seed with
        {
            PlacementId = 7,
            Physics = seed.Physics!.Value with { AnimationFrame = 7 },
        };
        if (parented)
        {
            seed = seed with
            {
                ParentGuid = 0x70004444u,
                ParentLocation = 9u,
                Physics = seed.Physics!.Value with
                {
                    Parent = new PhysicsAttachment(0x70004444u, 9u),
                },
            };
        }

        WorldSession.EntityPositionUpdate update = new(
            guid,
            Position(0x0101FFFFu, 20f),
            Velocity: null,
            PlacementId: 5,
            IsGrounded: true,
            InstanceSequence: 3,
            PositionSequence: force ? (ushort)9 : (ushort)11,
            TeleportSequence: 10,
            ForcePositionSequence: force ? (ushort)1 : (ushort)0);

        var merging = new InboundPhysicsStateController();
        merging.AcceptCreate(seed);
        Assert.True(merging.TryApplyPosition(
            update,
            isLocalPlayer,
            forcePositionRotation: isLocalPlayer ? Quaternion.Identity : null,
            currentLocalVelocity: isLocalPlayer ? Vector3.Zero : null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn merged,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(
            force
                ? PositionTimestampDisposition.ForcePosition
                : PositionTimestampDisposition.Apply,
            disposition);

        using RuntimeEntityObjectLifetime oracle = OracleLifetime();
        RuntimeEntityRecord canonical = RegisterOracleSnapshot(oracle, seed);
        RuntimeAuthoritativePositionRoute route =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
                RuntimeAcceptedPositionRouteRequests.Build(
                    new RuntimeGenerationToken(7),
                    canonical,
                    new RuntimeEntityKey(guid, 1),
                    update,
                    isLocalPlayer
                        ? RuntimePositionEntityKind.LocalPlayer
                        : RuntimePositionEntityKind.Remote,
                    RuntimeAcceptedPositionSource.PositionEvent,
                    disposition,
                    timestamps.PreviousTeleport,
                    timestamps.Teleport,
                    playerDistance: 0f,
                    usePositionFromServer: true,
                    committedCellId: 0x0101FFFFu));

        var classified = new InboundPhysicsStateController();
        classified.AcceptCreate(seed);
        Assert.True(classified.ApplyAcceptedPositionSnapshot(
            guid,
            update,
            disposition,
            timestamps,
            isLocalPlayer,
            isLocalPlayer ? Quaternion.Identity : null,
            isLocalPlayer ? Vector3.Zero : null,
            installPlacementFrame: route.ApplyPlacementFrameBeforeRouting,
            clearParent: route.UnparentBeforeRouting,
            out WorldSession.EntitySpawn expected));

        Assert.Equal(expected.PlacementId, merged.PlacementId);
        Assert.Equal(
            expected.Physics!.Value.AnimationFrame,
            merged.Physics!.Value.AnimationFrame);
        Assert.Equal(expected.ParentGuid, merged.ParentGuid);
        Assert.Equal(expected.ParentLocation, merged.ParentLocation);
        Assert.Equal(
            expected.Physics!.Value.Parent,
            merged.Physics!.Value.Parent);
    }

    [Theory]
    [InlineData(PositionTimestampDisposition.Rejected, false, false, false)]
    [InlineData(PositionTimestampDisposition.Rejected, true, false, false)]
    [InlineData(PositionTimestampDisposition.ForcePosition, false, false, false)]
    [InlineData(PositionTimestampDisposition.ForcePosition, true, false, false)]
    [InlineData(PositionTimestampDisposition.Apply, false, true, true)]
    [InlineData(PositionTimestampDisposition.Apply, true, true, false)]
    public void SharedPrePlacementFlagsMatchRetailTruthTable(
        PositionTimestampDisposition disposition,
        bool hasAnimations,
        bool expectedUnparent,
        bool expectedPlacementFrame)
    {
        RuntimeAcceptedPositionPrePlacementFlags flags =
            RuntimeAuthoritativePositionRouteClassifier.DerivePrePlacementFlags(
                disposition,
                hasAnimations);

        Assert.Equal(expectedUnparent, flags.UnparentBeforeRouting);
        Assert.Equal(expectedPlacementFrame, flags.ApplyPlacementFrameBeforeRouting);
    }

    [Fact]
    public void RemotePositionWithoutVelocityAppliesUnpackedZeroVector()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn seed = Spawn(
            0x7000000Au, 7, 20, 1, Position(0x0101FFFFu, 10f), 0x408u);
        seed = seed with
        {
            Physics = seed.Physics!.Value with { Velocity = new Vector3(4f, 5f, 6f) },
        };
        controller.AcceptCreate(seed);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                seed.Guid,
                Position(0x0101FFFFu, 20f),
                null,
                null,
                true,
                7,
                21,
                0,
                0),
            false,
            null,
            null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out _));

        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.Equal(Vector3.Zero, accepted.Physics!.Value.Velocity);
    }

    [Fact]
    public void LocalNormalCorrectionRetainsLiveVelocity_ButFreshTeleportZerosIt()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn seed = WithTimestamps(
            Spawn(0x5000000Au, 7, 20, 1, Position(0x0101FFFFu, 10f), 0x408u),
            teleport: 10);
        controller.AcceptCreate(seed);
        Vector3 liveVelocity = new(4f, 5f, 6f);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                seed.Guid,
                Position(0x0101FFFFu, 20f),
                new Vector3(90f, 80f, 70f),
                null,
                true,
                7,
                21,
                10,
                0),
            true,
            null,
            liveVelocity,
            out PositionTimestampDisposition normalDisposition,
            out WorldSession.EntitySpawn normal,
            out AcceptedPhysicsTimestamps normalTimestamps));
        Assert.Equal(PositionTimestampDisposition.Apply, normalDisposition);
        Assert.Equal(liveVelocity, normal.Physics!.Value.Velocity);
        Assert.False(normalTimestamps.TeleportAdvanced);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                seed.Guid,
                Position(0x0101FFFFu, 30f),
                new Vector3(9f, 8f, 7f),
                null,
                true,
                7,
                22,
                11,
                0),
            true,
            null,
            liveVelocity,
            out PositionTimestampDisposition teleportDisposition,
            out WorldSession.EntitySpawn teleported,
            out AcceptedPhysicsTimestamps teleportTimestamps));
        Assert.Equal(PositionTimestampDisposition.Apply, teleportDisposition);
        Assert.Equal(Vector3.Zero, teleported.Physics!.Value.Velocity);
        Assert.True(teleportTimestamps.TeleportAdvanced);
    }

    [Fact]
    public void ParentEventWaitsForParent_ThenStagesTimestampUntilValidatedCommit()
    {
        var controller = new InboundPhysicsStateController();
        controller.AcceptCreate(Spawn(
            0x70000003u, 3, 4, 1, Position(0x0101FFFFu, 10f), 0x408u));
        var update = new ParentEvent.Parsed(
            0x70000004u, 0x70000003u, 1, 2, 9, 5);

        Assert.False(controller.TryApplyParent(update, out _));
        controller.AcceptCreate(Spawn(
            0x70000004u, 9, 1, 1, Position(0x0101FFFFu, 15f), 0x408u));
        Assert.True(controller.TryApplyParent(update, out WorldSession.EntitySpawn staged));
        Assert.Null(staged.ParentGuid);
        Assert.NotNull(staged.Position);
        Assert.Equal((ushort)5, staged.PositionSequence);
        Assert.True(controller.TryCommitParent(
            update.ChildGuid,
            update.ParentGuid,
            update.ParentLocation,
            update.PlacementId,
            update.ChildPositionSequence,
            out WorldSession.EntitySpawn attached));
        Assert.Equal(0x70000004u, attached.ParentGuid);
        Assert.Null(attached.Position);
        Assert.False(controller.TryApplyParent(update, out _));
    }

    [Fact]
    public void LocalDeleteIsRejected_AndClearDropsSessionState()
    {
        var controller = new InboundPhysicsStateController();
        controller.AcceptCreate(Spawn(
            0x50000001u, 4, 1, 1, Position(0x0101FFFFu, 10f), 0x408u));

        Assert.False(controller.TryDelete(new DeleteObject.Parsed(0x50000001u, 4), true));
        Assert.True(controller.TryGetSnapshot(0x50000001u, out _));

        controller.Clear();
        Assert.Empty(controller.Snapshots);
        Assert.False(controller.IsFreshTeleportStart(0x50000001u, 1));
    }

    [Fact]
    public void DeleteForUnknownGenerationIsRejected()
    {
        var controller = new InboundPhysicsStateController();

        Assert.False(controller.TryDelete(
            new DeleteObject.Parsed(0x5000FFFFu, 9), isLocalPlayer: false));
    }

    [Fact]
    public void TeleportStartIsComparedWithoutAdvancingTeleportTimestamp()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = Spawn(
            0x50000002u, 2, 1, 1, Position(0x0101FFFFu, 10f), 0x408u);
        PhysicsSpawnData physics = spawn.Physics!.Value with
        {
            Timestamps = spawn.Physics.Value.Timestamps with { Teleport = 10 },
        };
        controller.AcceptCreate(spawn with { Physics = physics });

        Assert.True(controller.IsFreshTeleportStart(0x50000002u, 10));
        Assert.False(controller.IsFreshTeleportStart(0x50000002u, 9));
        Assert.True(controller.IsFreshTeleportStart(0x50000002u, 11));
        Assert.True(controller.IsFreshTeleportStart(0x50000002u, 11));
    }

    [Fact]
    public void RejectedServerControlStillMirrorsConsumedMovementTimestamp()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x70000005u, 3, 1, 1, Position(0x0101FFFFu, 10f), 0x408u),
            movement: 1,
            serverControl: 5);
        controller.AcceptCreate(spawn);

        bool applied = controller.TryApplyMotion(
            new WorldSession.EntityMotionUpdate(
                spawn.Guid,
                new CreateObject.ServerMotionState(0x3d, 0x11),
                3,
                2,
                4,
                false),
            retainPayload: true,
            out _,
            out _);

        Assert.False(applied);
        Assert.True(controller.TryGetSnapshot(spawn.Guid, out WorldSession.EntitySpawn retained));
        Assert.Equal((ushort)2, retained.MovementSequence);
        Assert.Equal((ushort)5, retained.ServerControlSequence);
        Assert.Equal((ushort)2, retained.Physics!.Value.Timestamps.Movement);
        Assert.Equal((ushort)5, retained.Physics.Value.Timestamps.ServerControlledMove);
        Assert.Equal(spawn.MotionState, retained.MotionState);
    }

    [Fact]
    public void AutonomousLocalEchoRetainsPayloadButMirrorsAcceptedTimestamps()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x50000006u, 3, 1, 1, Position(0x0101FFFFu, 10f), 0x408u),
            movement: 1,
            serverControl: 5) with
        {
            MotionState = new CreateObject.ServerMotionState(0x3d, 0x10),
        };
        controller.AcceptCreate(spawn);

        Assert.True(controller.TryApplyMotion(
            new WorldSession.EntityMotionUpdate(
                spawn.Guid,
                new CreateObject.ServerMotionState(0x3d, 0x12),
                3,
                2,
                5,
                true),
            retainPayload: false,
            out WorldSession.EntitySpawn retained,
            out _));

        Assert.Equal(spawn.MotionState, retained.MotionState);
        Assert.Equal((ushort)2, retained.MovementSequence);
        Assert.Equal((ushort)5, retained.ServerControlSequence);
        Assert.Equal((ushort)2, retained.Physics!.Value.Timestamps.Movement);
    }

    [Fact]
    public void StaleMovementTimestampLeavesLegacySnapshotByteIdentical()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x70000008u, 3, 1, 1, Position(0x0101FFFFu, 10f), 0x408u),
            movement: 5,
            serverControl: 5);
        controller.AcceptCreate(spawn);
        Assert.True(controller.TryGetSnapshot(spawn.Guid, out WorldSession.EntitySpawn before));

        bool applied = controller.TryApplyMotion(
            new WorldSession.EntityMotionUpdate(
                spawn.Guid,
                new CreateObject.ServerMotionState(0x3d, 0x99),
                InstanceSequence: 3,
                MovementSequence: 3,
                ServerControlSequence: 9,
                IsAutonomous: false),
            retainPayload: true,
            out WorldSession.EntitySpawn accepted,
            out _);

        Assert.False(applied);
        Assert.Equal(default, accepted);
        Assert.True(controller.TryGetSnapshot(spawn.Guid, out WorldSession.EntitySpawn after));
        Assert.Equal(before, after);
    }

    [Fact]
    public void InstanceMismatchedMovementLeavesLegacySnapshotByteIdentical()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x70000009u, 3, 1, 1, Position(0x0101FFFFu, 10f), 0x408u),
            movement: 1,
            serverControl: 1);
        controller.AcceptCreate(spawn);
        Assert.True(controller.TryGetSnapshot(spawn.Guid, out WorldSession.EntitySpawn before));

        bool applied = controller.TryApplyMotion(
            new WorldSession.EntityMotionUpdate(
                spawn.Guid,
                new CreateObject.ServerMotionState(0x3d, 0x99),
                InstanceSequence: 4,
                MovementSequence: 9,
                ServerControlSequence: 9,
                IsAutonomous: false),
            retainPayload: true,
            out WorldSession.EntitySpawn accepted,
            out _);

        Assert.False(applied);
        Assert.Equal(default, accepted);
        Assert.True(controller.TryGetSnapshot(spawn.Guid, out WorldSession.EntitySpawn after));
        Assert.Equal(before, after);
    }

    [Fact]
    public void FreshForceWithOlderTeleportMirrorsForceButRejectsPose()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x50000007u, 3, 10, 1, Position(0x0101FFFFu, 10f), 0x408u),
            teleport: 10,
            forcePosition: 0);
        controller.AcceptCreate(spawn);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                spawn.Guid,
                Position(0x0101FFFFu, 99f),
                null,
                null,
                true,
                3,
                11,
                9,
                1),
            isLocalPlayer: true,
            forcePositionRotation: Quaternion.Identity,
            currentLocalVelocity: Vector3.Zero,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn retained,
            out _));

        Assert.Equal(PositionTimestampDisposition.Rejected, disposition);
        Assert.Equal(10f, retained.Position!.Value.PositionX);
        Assert.Equal((ushort)10, retained.PositionSequence);
        Assert.Equal((ushort)1, retained.Physics!.Value.Timestamps.ForcePosition);
        Assert.Equal((ushort)10, retained.Physics.Value.Timestamps.Position);
    }

    [Fact]
    public void ForcePositionStoresPreservedLocalHeadingForLaterHydration()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x50000008u, 3, 10, 1, Position(0x0101FFFFu, 10f), 0x408u),
            teleport: 10,
            forcePosition: 0);
        controller.AcceptCreate(spawn);
        Quaternion heading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.75f);
        Vector3 liveVelocity = new(1f, 2f, 3f);

        Assert.True(controller.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                spawn.Guid,
                Position(0x0101FFFFu, 99f),
                new Vector3(90f, 80f, 70f),
                null,
                true,
                3,
                9,
                10,
                1),
            isLocalPlayer: true,
            forcePositionRotation: heading,
            currentLocalVelocity: liveVelocity,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn retained,
            out _));

        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);
        Assert.Equal(99f, retained.Position!.Value.PositionX);
        Assert.Equal(heading.W, retained.Position.Value.RotationW, precision: 6);
        Assert.Equal(heading.X, retained.Position.Value.RotationX, precision: 6);
        Assert.Equal(heading.Y, retained.Position.Value.RotationY, precision: 6);
        Assert.Equal(heading.Z, retained.Position.Value.RotationZ, precision: 6);
        Assert.Equal(retained.Position, retained.Physics!.Value.Position);
        Assert.Equal(liveVelocity, retained.Physics.Value.Velocity);
    }

    [Fact]
    public void TryApplyPosition_ReportsThePreEventTeleportStamp()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x50000010u, 3, 10, 1, Position(0x0101FFFFu, 10f), 0x408u),
            teleport: 10,
            forcePosition: 0);
        controller.AcceptCreate(spawn);

        Assert.True(controller.TryApplyPosition(
            PositionUpdate(spawn.Guid, instance: 3, position: 11, teleport: 10),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition steady,
            out _,
            out AcceptedPhysicsTimestamps steadyStamps));
        Assert.Equal(PositionTimestampDisposition.Apply, steady);
        Assert.Equal((ushort)10, steadyStamps.PreviousTeleport);
        Assert.Equal((ushort)10, steadyStamps.Teleport);
        Assert.False(steadyStamps.TeleportAdvanced);

        Assert.True(controller.TryApplyPosition(
            PositionUpdate(spawn.Guid, instance: 3, position: 12, teleport: 11),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition advanced,
            out _,
            out AcceptedPhysicsTimestamps advancedStamps));
        Assert.Equal(PositionTimestampDisposition.Apply, advanced);
        Assert.Equal((ushort)10, advancedStamps.PreviousTeleport);
        Assert.Equal((ushort)11, advancedStamps.Teleport);
        Assert.True(advancedStamps.TeleportAdvanced);
    }

    [Fact]
    public void LocalPlayerForcePositionAfterATeleport_ClassifiesAsAnAcceptedForceCorrection()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x50000011u, 3, 10, 1, Position(0x0101FFFFu, 10f), 0x408u),
            teleport: 10,
            forcePosition: 0);
        controller.AcceptCreate(spawn);

        Assert.True(controller.TryApplyPosition(
            PositionUpdate(
                spawn.Guid,
                instance: 3,
                position: 9,
                teleport: 10,
                forcePosition: 1),
            isLocalPlayer: true,
            forcePositionRotation: Quaternion.Identity,
            currentLocalVelocity: Vector3.Zero,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);

        // Built exactly as the route-2 drive builds it from these outputs.
        var authority = new RuntimeAuthoritativePositionAuthority(
            new RuntimeGenerationToken(7),
            new RuntimeEntityKey(spawn.Guid, 1),
            PositionAuthorityVersion: 4UL,
            AcceptedPositionSequence: 9,
            timestamps.PreviousTeleport,
            timestamps.Teleport,
            disposition);
        RuntimeAuthoritativePositionRoute route =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
                new RuntimeAcceptedPositionRouteRequest(
                    authority,
                    RuntimePositionEntityKind.LocalPlayer,
                    RuntimeAcceptedPositionSource.PositionEvent,
                    accepted.Position!.Value,
                    PlacementFrame: 0u,
                    PositionPackVelocity: Vector3.Zero,
                    CommittedCellId: 0x0101FFFFu,
                    HasContact: true,
                    PlayerDistance: 0f,
                    UsePositionFromServer: true,
                    HasAnimations: false,
                    default));

        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            route.Disposition);
        Assert.True(route.SendPositionImmediately);
        Assert.Equal((ushort)10, timestamps.PreviousTeleport);
        Assert.Equal((ushort)10, timestamps.Teleport);
    }

    [Fact]
    public void LocalPlayerForcePositionWithNewerTeleportLeavesAStaleEqualAcceptedPair()
    {
        var controller = new InboundPhysicsStateController();
        WorldSession.EntitySpawn spawn = WithTimestamps(
            Spawn(0x50000012u, 3, 10, 1, Position(0x0101FFFFu, 10f), 0x408u),
            teleport: 10,
            forcePosition: 0);
        controller.AcceptCreate(spawn);

        Assert.True(controller.TryApplyPosition(
            PositionUpdate(
                spawn.Guid,
                instance: 3,
                position: 11,
                teleport: 11,
                forcePosition: 1),
            isLocalPlayer: true,
            forcePositionRotation: Quaternion.Identity,
            currentLocalVelocity: new Vector3(1f, 2f, 3f),
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn accepted,
            out AcceptedPhysicsTimestamps timestamps));

        Assert.Equal(PositionTimestampDisposition.ForcePosition, disposition);
        Assert.Equal((ushort)10, timestamps.PreviousTeleport);
        Assert.Equal((ushort)10, timestamps.Teleport);
        Assert.False(timestamps.TeleportAdvanced);
        Assert.Equal(new Vector3(1f, 2f, 3f), accepted.Physics!.Value.Velocity);
    }

    private static WorldSession.EntityPositionUpdate PositionUpdate(
        uint guid,
        ushort instance,
        ushort position,
        ushort teleport,
        ushort forcePosition = 0) =>
        new(
            guid,
            Position(0x0101FFFFu, 20f),
            null,
            null,
            true,
            instance,
            position,
            teleport,
            forcePosition);

    [Theory]
    // topLevelMotionTableId, physicsMotionTableId, expectedAnimated
    [InlineData(null, 0x09000001u, true)]
    [InlineData(0x09000001u, null, true)]
    [InlineData(0x09000001u, 0x09000002u, true)]
    [InlineData(null, null, false)]
    [InlineData(0u, 0x09000001u, false)]
    [InlineData(null, 0u, false)]
    public void MixedMotionTableSourcesDriveTheSamePlacementFrameGateAsTheRoute(
        uint? topLevelMotionTableId,
        uint? physicsMotionTableId,
        bool expectedAnimated)
    {
        const uint guid = 0x80000041u;
        WorldSession.EntitySpawn seed = WithTimestamps(
            Spawn(guid, 3, 10, 1, Position(0x0101FFFFu, 10f), 0x408u),
            teleport: 10,
            forcePosition: 0);
        seed = seed with
        {
            PlacementId = 7,
            MotionTableId = topLevelMotionTableId,
            Physics = seed.Physics!.Value with
            {
                AnimationFrame = 7,
                MotionTableId = physicsMotionTableId,
            },
        };

        WorldSession.EntityPositionUpdate update = new(
            guid,
            Position(0x0101FFFFu, 20f),
            Velocity: null,
            PlacementId: 5,
            IsGrounded: true,
            InstanceSequence: 3,
            PositionSequence: 11,
            TeleportSequence: 10,
            ForcePositionSequence: 0);

        var merging = new InboundPhysicsStateController();
        merging.AcceptCreate(seed);
        Assert.True(merging.TryApplyPosition(
            update,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out PositionTimestampDisposition disposition,
            out WorldSession.EntitySpawn merged,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        using RuntimeEntityObjectLifetime oracle = OracleLifetime();
        RuntimeEntityRecord canonical = RegisterOracleSnapshot(oracle, seed);
        RuntimeAuthoritativePositionRoute route =
            RuntimeAuthoritativePositionRouteClassifier.ClassifyAcceptedPosition(
                RuntimeAcceptedPositionRouteRequests.Build(
                    new RuntimeGenerationToken(7),
                    canonical,
                    new RuntimeEntityKey(guid, 1),
                    update,
                    RuntimePositionEntityKind.Remote,
                    RuntimeAcceptedPositionSource.PositionEvent,
                    disposition,
                    timestamps.PreviousTeleport,
                    timestamps.Teleport,
                    playerDistance: 0f,
                    usePositionFromServer: true,
                    committedCellId: 0x0101FFFFu));

        Assert.Equal(!expectedAnimated, route.ApplyPlacementFrameBeforeRouting);
        Assert.Equal(expectedAnimated ? 7u : 5u, merged.PlacementId);
        Assert.Equal(
            expectedAnimated ? 7u : 5u,
            merged.Physics!.Value.AnimationFrame);
    }

    private static RuntimeEntityObjectLifetime OracleLifetime()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            0x01010000u,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return new RuntimeEntityObjectLifetime(engine);
    }

    private static RuntimeEntityRecord RegisterOracleSnapshot(
        RuntimeEntityObjectLifetime lifetime,
        WorldSession.EntitySpawn seed)
    {
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(seed).Canonical!;
        Assert.Equal(seed.MotionTableId, canonical.Snapshot.MotionTableId);
        Assert.Equal(
            seed.Physics!.Value.MotionTableId,
            canonical.Snapshot.Physics!.Value.MotionTableId);
        return canonical;
    }

    private static WorldSession.EntitySpawn WithTimestamps(
        WorldSession.EntitySpawn spawn,
        ushort? movement = null,
        ushort? serverControl = null,
        ushort? teleport = null,
        ushort? forcePosition = null)
    {
        PhysicsSpawnData physics = spawn.Physics!.Value;
        PhysicsTimestamps timestamps = physics.Timestamps with
        {
            Movement = movement ?? physics.Timestamps.Movement,
            ServerControlledMove = serverControl ?? physics.Timestamps.ServerControlledMove,
            Teleport = teleport ?? physics.Timestamps.Teleport,
            ForcePosition = forcePosition ?? physics.Timestamps.ForcePosition,
        };
        return spawn with
        {
            MovementSequence = timestamps.Movement,
            ServerControlSequence = timestamps.ServerControlledMove,
            Physics = physics with { Timestamps = timestamps },
        };
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        ushort positionSequence,
        ushort stateSequence,
        CreateObject.ServerPosition? position,
        uint state,
        uint? motionTableId = 0x09000001u)
    {
        var timestamps = new PhysicsTimestamps(
            positionSequence,
            1,
            stateSequence,
            1,
            0,
            1,
            0,
            1,
            instance);
        var physics = new PhysicsSpawnData(
            RawState: state,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: motionTableId,
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
            "fixture",
            null,
            null,
            motionTableId,
            PhysicsState: state,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: positionSequence,
            Physics: physics);
    }

    private static CreateObject.ServerPosition Position(uint cell, float x) =>
        new(cell, x, 10f, 5f, 1f, 0f, 0f, 0f);
}
