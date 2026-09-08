using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeProjectilePositionKindTests
{
    private const uint Cell = 0x0101FFFFu;
    private const uint OtherCell = 0x0102FFFFu;

    [Fact]
    public void MissileBitSetAndBound_ClassifiesProjectileAuthoritative()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new RuntimeGenerationToken(1), static () => 1UL);
        const uint guid = 0x70006001u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, Cell, instance: 1)).Canonical!;
        lifetime.Entities.SetFinalPhysicsState(
            canonical,
            canonical.FinalPhysicsState | PhysicsStateFlags.Missile);
        BindProjectile(lifetime, canonical, Cell);

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

        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord after));
        Assert.True((after.FinalPhysicsState & PhysicsStateFlags.Missile) != 0);
        Assert.NotNull(after.Projectile);
        RuntimeAuthoritativePositionRoute? route = lifetime.ClassifyRemoteAcceptedPosition(
            after, update, disposition, timestamps, playerDistance: 10f);

        Assert.NotNull(route);
        Assert.Equal(
            RuntimeSetPositionOperationKind.ProjectileAuthoritative,
            route!.Value.OperationKind);
    }

    [Fact]
    public void MissileBitSetButUnbound_ClassifiesRemoteAuthoritative()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new RuntimeGenerationToken(1), static () => 1UL);
        const uint guid = 0x70006005u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, Cell, instance: 1)).Canonical!;
        lifetime.Entities.SetFinalPhysicsState(
            canonical,
            canonical.FinalPhysicsState | PhysicsStateFlags.Missile);
        // Deliberately never bind a RuntimeProjectile.
        Assert.Null(canonical.Projectile);

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

        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord after));
        Assert.True((after.FinalPhysicsState & PhysicsStateFlags.Missile) != 0);
        Assert.Null(after.Projectile);
        RuntimeAuthoritativePositionRoute? route = lifetime.ClassifyRemoteAcceptedPosition(
            after, update, disposition, timestamps, playerDistance: 10f);

        Assert.NotNull(route);
        Assert.Equal(
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            route!.Value.OperationKind);
    }

    [Fact]
    public void MissileBitClear_ClassifiesRemoteAuthoritative()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new RuntimeGenerationToken(1), static () => 1UL);
        const uint guid = 0x70006002u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, Cell, instance: 1)).Canonical!;
        Assert.True((canonical.FinalPhysicsState & PhysicsStateFlags.Missile) == 0);

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

        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord after));
        RuntimeAuthoritativePositionRoute? route = lifetime.ClassifyRemoteAcceptedPosition(
            after, update, disposition, timestamps, playerDistance: 10f);

        Assert.NotNull(route);
        Assert.Equal(
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            route!.Value.OperationKind);
    }

    [Fact]
    public void MissileBitFlipMidLife_NextPositionReclassifies()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new RuntimeGenerationToken(1), static () => 1UL);
        const uint guid = 0x70006003u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, Cell, instance: 1)).Canonical!;
        Assert.True((canonical.FinalPhysicsState & PhysicsStateFlags.Missile) == 0);

        WorldSession.EntityPositionUpdate firstUpdate = PositionUpdate(
            guid, OtherCell, positionSequence: 2, teleportSequence: 0);
        Assert.True(lifetime.TryApplyPosition(
            firstUpdate,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition firstDisposition,
            out _,
            out AcceptedPhysicsTimestamps firstTimestamps));
        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord beforeFlip));
        RuntimeAuthoritativePositionRoute? beforeRoute =
            lifetime.ClassifyRemoteAcceptedPosition(
                beforeFlip, firstUpdate, firstDisposition, firstTimestamps, playerDistance: 10f);
        Assert.Equal(
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            beforeRoute!.Value.OperationKind);

        lifetime.Entities.SetFinalPhysicsState(
            beforeFlip,
            beforeFlip.FinalPhysicsState | PhysicsStateFlags.Missile);
        BindProjectile(lifetime, beforeFlip, OtherCell);

        WorldSession.EntityPositionUpdate secondUpdate = PositionUpdate(
            guid, Cell, positionSequence: 3, teleportSequence: 0);
        Assert.True(lifetime.TryApplyPosition(
            secondUpdate,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition secondDisposition,
            out _,
            out AcceptedPhysicsTimestamps secondTimestamps));
        Assert.True(lifetime.Entities.TryGetActive(guid, out RuntimeEntityRecord afterFlip));
        RuntimeAuthoritativePositionRoute? afterRoute =
            lifetime.ClassifyRemoteAcceptedPosition(
                afterFlip, secondUpdate, secondDisposition, secondTimestamps, playerDistance: 10f);

        Assert.NotNull(afterRoute);
        Assert.Equal(
            RuntimeSetPositionOperationKind.ProjectileAuthoritative,
            afterRoute!.Value.OperationKind);
    }

    private static void BindProjectile(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record,
        uint cellId)
    {
        if (record.PhysicsBody is not { } body)
        {
            body = new PhysicsBody
            {
                Position = new Vector3(10f, 20f, 5f),
                Orientation = Quaternion.Identity,
                LastUpdateTime = 1d,
                State = record.FinalPhysicsState,
                TransientState = TransientStateFlags.Active,
            };
            body.SnapToCell(cellId, body.Position, body.Position);
            lifetime.Entities.SetPhysicsBody(record, body);
        }
        lifetime.Physics.BindProjectile(
            record, body, new ProjectileCollisionSphere(Vector3.Zero, 0.1f, 1f));
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
            "remote-projectile-kind",
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
