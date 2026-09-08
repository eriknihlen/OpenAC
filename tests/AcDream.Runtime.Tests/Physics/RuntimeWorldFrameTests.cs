using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeWorldFrameTests
{
    private const uint CenterLandblock = 0xA9B60000u;
    private const uint CenterCell = CenterLandblock | 0x0001u;

    [Fact]
    public void LocalPlayerCreate_PublishesTheWorldFrameAtItsLandblock()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        BindGeneration(lifetime);

        Assert.False(lifetime.Physics.TryGetWorldFrameOffset(
            CenterCell,
            out _,
            out _));

        lifetime.RegisterEntityWithInitialResidence(
            Spawn(0x50000001u, CenterCell),
            isLocalPlayer: true);

        Assert.True(lifetime.Physics.TryGetWorldFrameOffset(
            CenterCell,
            out float offsetX,
            out float offsetY));
        Assert.Equal(0f, offsetX);
        Assert.Equal(0f, offsetY);
    }

    [Fact]
    public void RemoteCreate_NeverPublishesTheWorldFrame()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        BindGeneration(lifetime);

        lifetime.RegisterEntityWithInitialResidence(
            Spawn(0x70000001u, CenterCell),
            isLocalPlayer: false);

        Assert.False(lifetime.Physics.TryGetWorldFrameOffset(
            CenterCell,
            out _,
            out _));
    }

    [Theory]
    // One landblock east is +192 m on X; one north is +192 m on Y.
    [InlineData(0xAAB60001u, 192f, 0f)]
    [InlineData(0xA8B60001u, -192f, 0f)]
    [InlineData(0xA9B70001u, 0f, 192f)]
    [InlineData(0xA9B50001u, 0f, -192f)]
    public void NeighbouringLandblocks_ConvertAt192MetresPerStep(
        uint cellId,
        float expectedX,
        float expectedY)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        BindGeneration(lifetime);
        lifetime.Physics.ObserveLocalWorldFrame(
            CenterCell,
            teleportAdvanced: false);

        Assert.True(lifetime.Physics.TryGetWorldFrameOffset(
            cellId,
            out float offsetX,
            out float offsetY));
        Assert.Equal(expectedX, offsetX);
        Assert.Equal(expectedY, offsetY);
    }

    [Fact]
    public void OrdinaryMovementAcrossALandblockBoundary_DoesNotRebaseTheFrame()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        BindGeneration(lifetime);
        lifetime.Physics.ObserveLocalWorldFrame(
            CenterCell,
            teleportAdvanced: false);

        lifetime.Physics.ObserveLocalWorldFrame(
            0xAAB60001u,
            teleportAdvanced: false);

        Assert.True(lifetime.Physics.TryGetWorldFrameOffset(
            0xAAB60001u,
            out float offsetX,
            out float offsetY));
        Assert.Equal(192f, offsetX);
        Assert.Equal(0f, offsetY);
    }

    [Fact]
    public void AcceptedTeleport_RebasesTheFrameOnTheDestination()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        BindGeneration(lifetime);
        lifetime.Physics.ObserveLocalWorldFrame(
            CenterCell,
            teleportAdvanced: false);

        lifetime.Physics.ObserveLocalWorldFrame(
            0xAAB60001u,
            teleportAdvanced: true);

        // The destination is now the origin, and the departure landblock sits
        // one step west of it.
        Assert.True(lifetime.Physics.TryGetWorldFrameOffset(
            0xAAB60001u,
            out float destinationX,
            out float destinationY));
        Assert.Equal(0f, destinationX);
        Assert.Equal(0f, destinationY);

        Assert.True(lifetime.Physics.TryGetWorldFrameOffset(
            CenterCell,
            out float sourceX,
            out _));
        Assert.Equal(-192f, sourceX);
    }

    [Fact]
    public void ALocalPlayerCreateWithNoLandblock_MakesTheFrameUnreachable()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        BindGeneration(lifetime);

        // Before the local player is seen at all, waiting is legitimate.
        lifetime.Physics.ThrowIfWorldFrameUnreachable(CenterCell);

        lifetime.Physics.ObserveLocalPlayerCreate(0u);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() =>
                lifetime.Physics.ThrowIfWorldFrameUnreachable(CenterCell));
        Assert.Contains("world frame is unreachable", error.Message);
    }

    [Fact]
    public void AnAcceptedLocalPlayerCreate_LeavesTheFrameReachable()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        BindGeneration(lifetime);

        lifetime.Physics.ObserveLocalPlayerCreate(CenterCell);

        // The frame exists, so nothing is unreachable and nothing throws.
        lifetime.Physics.ThrowIfWorldFrameUnreachable(CenterCell);
        Assert.True(lifetime.Physics.TryGetWorldFrameOffset(
            CenterCell,
            out _,
            out _));
    }

    [Fact]
    public void ParkReasons_AreDistinctAndBothRetryable()
    {
        Assert.True(RuntimeSetPositionMoverPreparationStatus
            .RetrySetupUnavailable.IsRetryable());
        Assert.True(RuntimeSetPositionMoverPreparationStatus
            .RetryWorldFrameUnavailable.IsRetryable());
        Assert.False(RuntimeSetPositionMoverPreparationStatus
            .RejectedAuthority.IsRetryable());
        Assert.False(RuntimeSetPositionMoverPreparationStatus
            .InvalidData.IsRetryable());
        Assert.False(RuntimeSetPositionMoverPreparationStatus
            .Prepared.IsRetryable());

        Assert.Equal(
            RuntimeSetPositionParkReason.AwaitingSetupCollision,
            RuntimeSetPositionMoverPreparationStatus.RetrySetupUnavailable
                .ParkReason());
        Assert.Equal(
            RuntimeSetPositionParkReason.AwaitingWorldFrame,
            RuntimeSetPositionMoverPreparationStatus.RetryWorldFrameUnavailable
                .ParkReason());
        Assert.Equal(
            RuntimeSetPositionParkReason.None,
            RuntimeSetPositionMoverPreparationStatus.Prepared.ParkReason());
    }

    [Fact]
    public void AZeroCellIdNeitherPublishesNorResolves()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        BindGeneration(lifetime);

        lifetime.Physics.ObserveLocalWorldFrame(0u, teleportAdvanced: false);
        Assert.False(lifetime.Physics.TryGetWorldFrameOffset(
            CenterCell,
            out _,
            out _));

        lifetime.Physics.ObserveLocalWorldFrame(
            CenterCell,
            teleportAdvanced: false);
        Assert.False(lifetime.Physics.TryGetWorldFrameOffset(
            0u,
            out _,
            out _));
    }

    private static void BindGeneration(RuntimeEntityObjectLifetime lifetime)
    {
        var generation = new RuntimeGenerationToken(1UL);
        lifetime.BindEventContext(() => generation, static () => 1UL);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell)
    {
        var position = new CreateObject.ServerPosition(
            cell, 1f, 2f, 3f, 1f, 0f, 0f, 0f);
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
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: 1f,
            Friction: 0.5f,
            Elasticity: 0.05f,
            Translucency: null,
            Velocity: Vector3.Zero,
            Acceleration: null,
            AngularVelocity: Vector3.Zero,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            Guid: guid,
            Position: position,
            SetupTableId: null,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: 1f,
            Name: "world-frame-fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: 0x09000001u,
            PhysicsState: physics.RawState,
            ObjectDescriptionFlags: 0x8u,
            Friction: 0.5f,
            Elasticity: 0.05f,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
