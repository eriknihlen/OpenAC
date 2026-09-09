using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.App.Tests.World;

public sealed class PendingSplitToWorldProjectionTests
{
    [Fact]
    public void Resolve_ClonesSourceAppearanceIntoAuthoritativeGroundPlacement()
    {
        var pending = new PendingSplitToWorldProjection();
        WorldSession.EntitySpawn source = SourceSpawn();
        pending.Record(7u, source, amount: 3u, now: 12.0);
        WorldSession.EntityPositionUpdate update = PositionUpdate(
            guid: 0x80000435u,
            instance: 4,
            position: 9,
            teleport: 2,
            forcePosition: 3);

        Assert.True(pending.TryResolve(update, now: 12.25, out var spawn));

        Assert.Equal(update.Guid, spawn.Guid);
        Assert.Equal(source.SetupTableId, spawn.SetupTableId);
        Assert.Equal(source.AnimPartChanges, spawn.AnimPartChanges);
        Assert.Equal(source.TextureChanges, spawn.TextureChanges);
        Assert.Equal(source.SubPalettes, spawn.SubPalettes);
        Assert.Equal(source.BasePaletteId, spawn.BasePaletteId);
        Assert.Equal(source.WeenieClassId, spawn.WeenieClassId);
        Assert.Equal(3, spawn.StackSize);
        Assert.Equal(0u, spawn.ContainerId);
        Assert.Equal(0u, spawn.WielderId);
        Assert.Equal(0u, spawn.CurrentWieldedLocation);
        Assert.Null(spawn.ParentGuid);
        Assert.Null(spawn.ParentLocation);
        Assert.Equal(update.Position, spawn.Position);
        Assert.Equal(update.PlacementId, spawn.PlacementId);
        Assert.Equal(update.InstanceSequence, spawn.InstanceSequence);
        Assert.Equal(update.PositionSequence, spawn.PositionSequence);

        PhysicsSpawnData physics = Assert.IsType<PhysicsSpawnData>(spawn.Physics);
        Assert.Equal(update.Position, physics.Position);
        Assert.Null(physics.Parent);
        Assert.Equal(update.Velocity, physics.Velocity);
        Assert.Equal(update.InstanceSequence, physics.Timestamps.Instance);
        Assert.Equal(update.PositionSequence, physics.Timestamps.Position);
        Assert.Equal(update.TeleportSequence, physics.Timestamps.Teleport);
        Assert.Equal(
            update.ForcePositionSequence,
            physics.Timestamps.ForcePosition);
        Assert.False(pending.HasPending);
    }

    [Fact]
    public void Resolve_ConsumesOneRetailPendingIdentity()
    {
        var pending = new PendingSplitToWorldProjection();
        pending.Record(1u, SourceSpawn(), amount: 1u, now: 5.0);

        Assert.True(pending.TryResolve(
            PositionUpdate(0x80000435u),
            now: 5.1,
            out _));
        Assert.False(pending.TryResolve(
            PositionUpdate(0x80000436u),
            now: 5.2,
            out _));
    }

    [Fact]
    public void Resolve_RejectsAtRetailTenSecondBoundary()
    {
        var pending = new PendingSplitToWorldProjection();
        pending.Record(1u, SourceSpawn(), amount: 1u, now: 5.0);

        Assert.False(pending.TryResolve(
            PositionUpdate(0x80000435u),
            now: 15.0,
            out _));
        Assert.False(pending.HasPending);
    }

    [Fact]
    public void Resolve_ClonesRelationshipAndAnimationStateWholesaleFromSource()
    {
        var pending = new PendingSplitToWorldProjection();
        WorldSession.EntitySpawn baseSource = SourceSpawn();
        WorldSession.EntitySpawn source = baseSource with
        {
            SetupTableId = 0x0200ABCDu,
            Physics = baseSource.Physics!.Value with
            {
                Children = new[] { new PhysicsAttachment(0x50000ABCu, 3u) },
                Movement = new PhysicsMovementData(
                    new byte[] { 1, 2, 3 },
                    MotionState: null,
                    IsAutonomous: true),
                AnimationFrame = 42u,
            },
        };
        pending.Record(9u, source, amount: 2u, now: 1.0);

        Assert.True(pending.TryResolve(
            PositionUpdate(0x80000777u),
            now: 1.5,
            out WorldSession.EntitySpawn spawn));

        Assert.Equal(source.SetupTableId, spawn.SetupTableId);
        PhysicsSpawnData physics = Assert.IsType<PhysicsSpawnData>(spawn.Physics);
        Assert.True(physics.Children.HasValue);
        Assert.Equal(
            source.Physics!.Value.Children!.Value.ToArray(),
            physics.Children!.Value.ToArray());
        Assert.Equal(source.Physics.Value.Movement, physics.Movement);
        Assert.Equal(source.Physics.Value.AnimationFrame, physics.AnimationFrame);
    }

    [Fact]
    public void Resolve_SourceGuidDoesNotConsumePendingIdentity()
    {
        var pending = new PendingSplitToWorldProjection();
        WorldSession.EntitySpawn source = SourceSpawn();
        pending.Record(1u, source, amount: 1u, now: 5.0);

        Assert.False(pending.TryResolve(
            PositionUpdate(source.Guid),
            now: 5.1,
            out _));
        Assert.True(pending.HasPending);
    }

    private static WorldSession.EntitySpawn SourceSpawn()
    {
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 2,
            State: 3,
            Vector: 4,
            Teleport: 5,
            ServerControlledMove: 6,
            ForcePosition: 7,
            ObjDesc: 8,
            Instance: 9);
        var physics = new PhysicsSpawnData(
            RawState: 0x00000408u,
            Position: null,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x0200030Bu,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: new PhysicsAttachment(0x50000001u, 0u),
            Children: null,
            Scale: 1f,
            Friction: 0.5f,
            Elasticity: 0.05f,
            Translucency: null,
            Velocity: Vector3.Zero,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);

        return new WorldSession.EntitySpawn(
            Guid: 0x50000010u,
            Position: null,
            SetupTableId: 0x0200030Bu,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: 0x04000001u,
            ObjScale: 1f,
            Name: "Copper Pea",
            ItemType: 0x00000001u,
            MotionState: null,
            MotionTableId: 0x09000001u,
            WeenieClassId: 273u,
            StackSize: 100,
            StackSizeMax: 1000,
            ContainerId: 0x50000001u,
            WielderId: 0u,
            CurrentWieldedLocation: 0u,
            InstanceSequence: 9,
            PositionSequence: 1,
            ParentGuid: 0x50000001u,
            ParentLocation: 0u,
            Physics: physics);
    }

    private static WorldSession.EntityPositionUpdate PositionUpdate(
        uint guid,
        ushort instance = 4,
        ushort position = 9,
        ushort teleport = 2,
        ushort forcePosition = 3)
    {
        var serverPosition = new CreateObject.ServerPosition(
            0xA9B4001Au,
            12f,
            34f,
            1.5f,
            1f,
            0f,
            0f,
            0f);
        return new WorldSession.EntityPositionUpdate(
            guid,
            serverPosition,
            Velocity: Vector3.Zero,
            PlacementId: 0x65u,
            IsGrounded: true,
            InstanceSequence: instance,
            PositionSequence: position,
            TeleportSequence: teleport,
            ForcePositionSequence: forcePosition);
    }
}
