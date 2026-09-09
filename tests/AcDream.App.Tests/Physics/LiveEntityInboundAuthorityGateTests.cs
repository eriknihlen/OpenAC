using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityInboundAuthorityGateTests
{
    private const uint Guid = 0x50000021u;

    [Fact]
    public void Motion_StrictFreshnessAndWrapAreOwnedBeforePresentation()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 7, movement: 0xFFFE));
        var published = new List<AcceptedPhysicsTimestamps>();
        var gate = new LiveEntityInboundAuthorityGate(
            runtime,
            (_, timestamps) => published.Add(timestamps));

        Assert.False(gate.TryAcceptMotion(
            Motion(instance: 7, movement: 0xFFFE, server: 1),
            retainPayload: true,
            out _,
            out bool equalAccepted));
        Assert.False(equalAccepted);

        Assert.True(gate.TryAcceptMotion(
            Motion(instance: 7, movement: 1, server: 1),
            retainPayload: true,
            out AcceptedMotionNetworkUpdate wrapped,
            out bool wrapAccepted));
        Assert.True(wrapAccepted);
        Assert.Equal((ulong)2, wrapped.Record.MovementAuthorityVersion);
        Assert.Single(published);

        Assert.False(gate.TryAcceptMotion(
            Motion(instance: 7, movement: 1, server: 1),
            retainPayload: true,
            out _,
            out bool duplicateAccepted));
        Assert.False(duplicateAccepted);
        Assert.Single(published);
    }

    [Fact]
    public void AutonomousLocalMotion_ConsumesTimestampButDoesNotPublishPayload()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 1));
        int publishCount = 0;
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => publishCount++);

        Assert.False(gate.TryAcceptMotion(
            Motion(instance: 1, movement: 2, server: 1, autonomous: true),
            retainPayload: false,
            out _,
            out bool timestampAccepted));

        Assert.True(timestampAccepted);
        Assert.Equal(1, publishCount);
        Assert.True(runtime.TryGetRecord(Guid, out LiveEntityRecord record));
        Assert.Equal((ulong)1, record.MovementAuthorityVersion);
    }

    [Fact]
    public void Vector_InvalidPayloadDoesNotConsumeSequenceAndDuplicateIsRejected()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 2));
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => { });
        var update = new VectorUpdate.Parsed(
            Guid,
            new Vector3(1f, 2f, 3f),
            Vector3.Zero,
            InstanceSequence: 2,
            VectorSequence: 2);

        Assert.False(gate.TryAcceptVector(update, payloadIsValid: false, out _));
        Assert.True(gate.TryAcceptVector(
            update,
            payloadIsValid: true,
            out AcceptedVectorNetworkUpdate accepted));
        Assert.Equal((ulong)2, accepted.VectorAuthorityVersion);
        Assert.False(gate.TryAcceptVector(update, payloadIsValid: true, out _));
    }

    [Fact]
    public void Vector_WrappedSequenceIsAccepted()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 2, vector: 0xFFFE));
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => { });

        Assert.True(gate.TryAcceptVector(
            new VectorUpdate.Parsed(
                Guid,
                Vector3.One,
                Vector3.Zero,
                InstanceSequence: 2,
                VectorSequence: 1),
            payloadIsValid: true,
            out _));
    }

    [Fact]
    public void State_EqualIsRejectedAndWrappedSequenceIsAccepted()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 3, state: 0xFFFE));
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => { });

        Assert.False(gate.TryAcceptState(
            new SetState.Parsed(Guid, (uint)PhysicsStateFlags.Hidden, 3, 0xFFFE),
            out _));
        Assert.True(gate.TryAcceptState(
            new SetState.Parsed(Guid, (uint)PhysicsStateFlags.Hidden, 3, 1),
            out AcceptedStateNetworkUpdate accepted));
        Assert.Equal((ulong)2, accepted.StateAuthorityVersion);
        Assert.False(gate.TryAcceptState(
            new SetState.Parsed(Guid, 0, 3, 1),
            out _));
    }

    [Fact]
    public void Position_UnknownLocalHintAndResetDoNotConsumeFuturePacket()
    {
        LiveEntityRuntime runtime = Runtime();
        int publishCount = 0;
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => publishCount++);
        WorldSession.EntityPositionUpdate update = Position(
            Guid,
            instance: 4,
            position: 2,
            cell: 0xAABB0001u);

        Assert.False(gate.TryAcceptPosition(
            update,
            Guid,
            Quaternion.Identity,
            Vector3.Zero,
            payloadIsValid: true,
            out _));
        Assert.Equal(0xAABB0001u, gate.LastLivePlayerLandblockId);
        Assert.Equal(0, publishCount);

        Register(runtime, Spawn(Guid, instance: 4, position: 1));
        Assert.True(gate.TryAcceptPosition(
            update,
            Guid,
            Quaternion.Identity,
            Vector3.Zero,
            payloadIsValid: true,
            out AcceptedPositionNetworkUpdate accepted));
        Assert.Equal(PositionTimestampDisposition.Apply, accepted.TimestampDisposition);
        Assert.Equal((ulong)2, accepted.PositionAuthorityVersion);
        Assert.True(runtime.TryGetCanonical(Guid, out RuntimeEntityRecord canonical));
        Assert.Same(canonical, accepted.Canonical);
        Assert.Equal(1, publishCount);

        gate.ResetSessionState();
        Assert.Null(gate.LastLivePlayerLandblockId);
    }

    [Fact]
    public void Position_InvalidPayloadDoesNotConsumeSequence()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 5, position: 1));
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => { });
        WorldSession.EntityPositionUpdate update = Position(Guid, 5, 2, 0x01010001u);

        Assert.False(gate.TryAcceptPosition(
            update, Guid, Quaternion.Identity, Vector3.Zero, false, out _));
        Assert.True(gate.TryAcceptPosition(
            update, Guid, Quaternion.Identity, Vector3.Zero, true, out _));
        Assert.False(gate.TryAcceptPosition(
            update, Guid, Quaternion.Identity, Vector3.Zero, true, out _));
    }

    [Fact]
    public void Position_WrappedSequenceIsAccepted()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 5, position: 0xFFFE));
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => { });

        Assert.True(gate.TryAcceptPosition(
            Position(Guid, 5, 1, 0x01010002u),
            Guid,
            Quaternion.Identity,
            Vector3.Zero,
            payloadIsValid: true,
            out _));
    }

    [Fact]
    public void Position_CanonicalInventoryObjectIsAcceptedBeforeProjectionExists()
    {
        LiveEntityRuntime runtime = Runtime();
        LiveEntityRegistrationResult registration =
            runtime.RegisterLiveEntity(Spawn(Guid, instance: 5, position: 1));
        Assert.NotNull(registration.Canonical);
        Assert.False(runtime.TryGetRecord(Guid, out _));
        int publishCount = 0;
        var gate = new LiveEntityInboundAuthorityGate(
            runtime,
            (_, _) => publishCount++);

        Assert.True(gate.TryAcceptPosition(
            Position(Guid, 5, 2, 0x01010002u),
            localPlayerGuid: 0u,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            payloadIsValid: true,
            out AcceptedPositionNetworkUpdate accepted));

        Assert.Same(registration.Canonical, accepted.Canonical);
        Assert.Equal((ulong)1, accepted.PositionAuthorityVersion);
        Assert.Equal(1, publishCount);
        Assert.False(runtime.TryGetRecord(Guid, out _));
    }

    [Fact]
    public void Vector_NewerSameIncarnationPacketInvalidatesCapturedAuthority()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 5, vector: 1));
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => { });
        Assert.True(gate.TryAcceptVector(
            new VectorUpdate.Parsed(Guid, Vector3.One, Vector3.Zero, 5, 2),
            payloadIsValid: true,
            out AcceptedVectorNetworkUpdate accepted));

        Assert.True(runtime.TryApplyVector(
            new VectorUpdate.Parsed(Guid, Vector3.UnitX, Vector3.Zero, 5, 3),
            out _));
        Assert.False(runtime.IsCurrentVectorAuthority(
            accepted.Record,
            accepted.VectorAuthorityVersion));
    }

    [Fact]
    public void State_NewerSameIncarnationPacketInvalidatesCapturedAuthority()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 5, state: 1));
        var gate = new LiveEntityInboundAuthorityGate(runtime, (_, _) => { });
        Assert.True(gate.TryAcceptState(
            new SetState.Parsed(Guid, (uint)PhysicsStateFlags.Hidden, 5, 2),
            out AcceptedStateNetworkUpdate accepted));

        Assert.True(runtime.TryApplyState(
            new SetState.Parsed(Guid, 0, 5, 3),
            out _));
        Assert.False(runtime.IsCurrentStateAuthority(
            accepted.Record,
            accepted.StateAuthorityVersion));
    }

    [Fact]
    public void Motion_PublisherReplacementInvalidatesCapturedIncarnation()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 6));
        var gate = new LiveEntityInboundAuthorityGate(
            runtime,
            (_, _) => runtime.RegisterLiveEntity(Spawn(Guid, instance: 7)));

        Assert.False(gate.TryAcceptMotion(
            Motion(instance: 6, movement: 2, server: 1),
            retainPayload: true,
            out _,
            out bool timestampAccepted));
        Assert.True(timestampAccepted);
        Assert.True(runtime.TryGetCanonical(Guid, out RuntimeEntityRecord replacement));
        Assert.Equal((ushort)7, replacement.Incarnation);
        Assert.NotNull(replacement.LocalEntityId);
        Assert.False(runtime.TryGetRecord(Guid, out _));
    }

    [Fact]
    public void Position_PublisherNewerChannelInvalidatesCapturedAuthority()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 8, position: 1));
        var nested = Position(Guid, 8, 3, 0x01010002u);
        LiveEntityInboundAuthorityGate? gate = null;
        gate = new LiveEntityInboundAuthorityGate(
            runtime,
            (_, _) => runtime.TryApplyPosition(
                nested,
                isLocalPlayer: true,
                forcePositionRotation: Quaternion.Identity,
                currentLocalVelocity: Vector3.Zero,
                out _,
                out _,
                out _));

        Assert.False(gate.TryAcceptPosition(
            Position(Guid, 8, 2, 0x01010001u),
            Guid,
            Quaternion.Identity,
            Vector3.Zero,
            payloadIsValid: true,
            out _));
        Assert.True(runtime.TryGetRecord(Guid, out LiveEntityRecord record));
        Assert.Equal((ulong)3, record.PositionAuthorityVersion);
    }

    [Fact]
    public void Position_PublisherNewerVectorPreservesIndependentPositionAuthority()
    {
        LiveEntityRuntime runtime = Runtime();
        Register(runtime, Spawn(Guid, instance: 9, position: 1, vector: 1));
        var gate = new LiveEntityInboundAuthorityGate(
            runtime,
            (_, _) => runtime.TryApplyVector(
                new VectorUpdate.Parsed(
                    Guid,
                    Vector3.UnitX,
                    Vector3.Zero,
                    InstanceSequence: 9,
                    VectorSequence: 2),
                out _));

        Assert.True(gate.TryAcceptPosition(
            Position(Guid, 9, 2, 0x01010003u),
            Guid,
            Quaternion.Identity,
            Vector3.Zero,
            payloadIsValid: true,
            out AcceptedPositionNetworkUpdate accepted));
        Assert.True(runtime.IsCurrentPositionAuthority(
            accepted.Canonical,
            accepted.PositionAuthorityVersion));
        Assert.False(runtime.IsCurrentVelocityAuthority(
            accepted.Canonical,
            accepted.VelocityAuthorityVersion));
    }

    private static LiveEntityRuntime Runtime() => LiveEntityRuntimeFixture.Create(
        new GpuWorldState(),
        new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));

    private static LiveEntityRecord Register(
        LiveEntityRuntime runtime,
        WorldSession.EntitySpawn spawn) =>
        runtime.RegisterAndMaterializeProjection(spawn);

    private static WorldSession.EntityMotionUpdate Motion(
        ushort instance,
        ushort movement,
        ushort server,
        bool autonomous = false) => new(
            Guid,
            default,
            instance,
            movement,
            server,
            autonomous);

    private static WorldSession.EntityPositionUpdate Position(
        uint guid,
        ushort instance,
        ushort position,
        uint cell) => new(
            guid,
            new CreateObject.ServerPosition(
                cell, 10f, 11f, 12f, 1f, 0f, 0f, 0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: instance,
            PositionSequence: position,
            TeleportSequence: 0,
            ForcePositionSequence: 0);

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        ushort position = 1,
        ushort movement = 1,
        ushort state = 1,
        ushort vector = 1)
    {
        var serverPosition = new CreateObject.ServerPosition(
            0x01010001u, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            position,
            movement,
            state,
            vector,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: serverPosition,
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
            serverPosition,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: instance,
            MovementSequence: movement,
            ServerControlSequence: 1,
            PositionSequence: position,
            Physics: physics);
    }
}
