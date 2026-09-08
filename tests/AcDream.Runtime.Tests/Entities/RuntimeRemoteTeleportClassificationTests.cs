using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeRemoteTeleportClassificationTests
{
    private const uint Cell = 0x0101FFFFu;
    private const uint OtherCell = 0x0102FFFFu;

    [Fact]
    public void CellLessRecord_ClassifiesSetPosition_EvenWithoutATeleportAdvance()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new RuntimeGenerationToken(1), static () => 1UL);
        const uint guid = 0x70005001u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, Cell, instance: 1)).Canonical!;
        Assert.Equal(Cell, canonical.FullCellId);

        lifetime.Entities.SetFullCell(canonical, 0u, 0u);
        Assert.Equal(0u, canonical.FullCellId);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, OtherCell, positionSequence: 2, teleportSequence: 0);

        Assert.True(lifetime.TryApplyPosition(
            update,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        // The measured pre-merge value is the honest 0 — not a fabrication,
        // and not re-read after the merge (which would already show OtherCell).
        Assert.Equal(0u, timestamps.PreMergeCommittedCellId);
        Assert.False(timestamps.TeleportAdvanced);

        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord after));
        RuntimeAuthoritativePositionRoute? route = lifetime.ClassifyRemoteAcceptedPosition(
            after, update, disposition, timestamps, playerDistance: 10f);

        Assert.NotNull(route);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPosition,
            route!.Value.Disposition);
        Assert.True(
            (route.Value.SetPositionFlags & PhysicsSetPositionFlags.Teleport) != 0);
        Assert.True(RuntimeRemoteTeleportPosition.OwnsTeleportPlacement(route));
    }

    [Fact]
    public void CompanionTest_NonzeroPreMergeCellWithNoTeleportAdvance_DoesNotClassifySetPosition()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new RuntimeGenerationToken(1), static () => 1UL);
        const uint guid = 0x70005002u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, Cell, instance: 1)).Canonical!;
        Assert.Equal(Cell, canonical.FullCellId);
        // Deliberately NO SetFullCell(0, 0) — the record stays resident.

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, OtherCell, positionSequence: 2, teleportSequence: 0);

        Assert.True(lifetime.TryApplyPosition(
            update,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.Equal(Cell, timestamps.PreMergeCommittedCellId);
        Assert.False(timestamps.TeleportAdvanced);

        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord after));
        RuntimeAuthoritativePositionRoute? route = lifetime.ClassifyRemoteAcceptedPosition(
            after, update, disposition, timestamps, playerDistance: 10f);

        Assert.NotNull(route);
        Assert.NotEqual(
            RuntimeAuthoritativePositionDisposition.SetPosition,
            route!.Value.Disposition);
        Assert.False(RuntimeRemoteTeleportPosition.OwnsTeleportPlacement(route));
    }

    [Fact]
    public void FreshTeleportTimestamp_ClassifiesSetPosition_WithAResidentPreMergeCell()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new RuntimeGenerationToken(1), static () => 1UL);
        const uint guid = 0x70005003u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, Cell, instance: 1)).Canonical!;
        Assert.Equal(Cell, canonical.FullCellId);

        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid, OtherCell, positionSequence: 2, teleportSequence: 5);

        Assert.True(lifetime.TryApplyPosition(
            update,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.Equal(Cell, timestamps.PreMergeCommittedCellId);
        Assert.True(timestamps.TeleportAdvanced);

        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord after));
        RuntimeAuthoritativePositionRoute? route = lifetime.ClassifyRemoteAcceptedPosition(
            after, update, disposition, timestamps, playerDistance: 10f);

        Assert.NotNull(route);
        Assert.Equal(
            RuntimeAuthoritativePositionDisposition.SetPosition,
            route!.Value.Disposition);
        Assert.True(RuntimeRemoteTeleportPosition.OwnsTeleportPlacement(route));
    }

    private static WorldSession.EntityPositionUpdate PositionUpdate(
        uint guid,
        uint cellId,
        ushort positionSequence,
        ushort teleportSequence) =>
        new(
            guid,
            new CreateObject.ServerPosition(
                cellId, 12f, 14f, 7f, 1f, 0f, 0f, 0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: positionSequence,
            TeleportSequence: teleportSequence,
            ForcePositionSequence: 0);

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        uint cellId,
        ushort instance)
    {
        var position = new CreateObject.ServerPosition(
            cellId, 10f, 20f, 5f, 1f, 0f, 0f, 0f);
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
            RawState: 0x408u,
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
            "remote-teleport-classification",
            null,
            null,
            0x09000001u,
            PhysicsState: 0x408u,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
