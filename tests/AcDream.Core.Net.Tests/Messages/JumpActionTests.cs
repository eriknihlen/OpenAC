using System;
using System.Buffers.Binary;
using System.Numerics;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class JumpActionTests
{
    [Fact]
    public void Build_ProducesValidGameAction()
    {
        var body = JumpAction.Build(
            gameActionSequence: 9,
            extent: 0.5f,
            velocity: new Vector3(1f, 2f, 3f),
            cellId: 0xA9B40001u,
            position: new Vector3(96f, 96f, 50f),
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        Assert.Equal(0xF7B1u, opcode);

        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        Assert.Equal(9u, seq);

        uint actionType = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        Assert.Equal(0xF61Bu, actionType);
    }

    [Fact]
    public void Build_ExtentAndVelocity_FollowEnvelope()
    {
        var body = JumpAction.Build(
            gameActionSequence: 1,
            extent: 0.75f,
            velocity: new Vector3(1.5f, -2.5f, 9.81f),
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        // 12-byte envelope, then extent(4), vx(4), vy(4), vz(4).
        float extent = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(12));
        float vx = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(16));
        float vy = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(20));
        float vz = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(24));

        Assert.Equal(0.75f, extent);
        Assert.Equal(1.5f, vx);
        Assert.Equal(-2.5f, vy);
        Assert.Equal(9.81f, vz);
    }

    [Fact]
    public void Build_PositionFollowsVelocity_CellIdThenOriginThenQuaternion()
    {
        var body = JumpAction.Build(
            gameActionSequence: 2,
            extent: 0f,
            velocity: Vector3.Zero,
            cellId: 0xDEADBEEFu,
            position: new Vector3(12.5f, 34.0f, 56.75f),
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        int positionOffset = 28;
        uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(positionOffset));
        float x = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(positionOffset + 4));
        float y = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(positionOffset + 8));
        float z = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(positionOffset + 12));
        float qw = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(positionOffset + 16));
        float qx = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(positionOffset + 20));
        float qy = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(positionOffset + 24));
        float qz = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(positionOffset + 28));

        Assert.Equal(0xDEADBEEFu, cellId);
        Assert.Equal(12.5f, x);
        Assert.Equal(34.0f, y);
        Assert.Equal(56.75f, z);
        Assert.Equal(1.0f, qw);
        Assert.Equal(0.0f, qx);
        Assert.Equal(0.0f, qy);
        Assert.Equal(0.0f, qz);
    }

    [Fact]
    public void Build_TimestampsFollowPosition_InRetailOrder()
    {
        var body = JumpAction.Build(
            gameActionSequence: 3,
            extent: 0f,
            velocity: Vector3.Zero,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0x1111,
            serverControlSequence: 0x2222,
            teleportSequence: 0x3333,
            forcePositionSequence: 0x4444);

        int tsOffset = 60;
        ushort instance = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(tsOffset));
        ushort serverControl = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(tsOffset + 2));
        ushort teleport = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(tsOffset + 4));
        ushort forcePosition = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(tsOffset + 6));

        Assert.Equal((ushort)0x1111, instance);
        Assert.Equal((ushort)0x2222, serverControl);
        Assert.Equal((ushort)0x3333, teleport);
        Assert.Equal((ushort)0x4444, forcePosition);
    }

    [Fact]
    public void Build_NoObjectGuidOrSpellId_JumpPackBodyLengthIs56()
    {
        var body = JumpAction.Build(
            gameActionSequence: 4,
            extent: 0f,
            velocity: Vector3.Zero,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        Assert.Equal(56, body.Length - 12);
        Assert.Equal(68, body.Length);
        Assert.Equal(0, body.Length % 4);
    }
}
