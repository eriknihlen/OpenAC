using System;
using System.Buffers.Binary;
using System.Numerics;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class AutonomousPositionTests
{
    [Fact]
    public void Build_ProducesValidGameAction()
    {
        var body = AutonomousPosition.Build(
            gameActionSequence: 5,
            cellId: 0xA9B40001u,
            position: new Vector3(100f, 100f, 50f),
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        Assert.Equal(0xF7B1u, opcode);

        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        Assert.Equal(5u, seq);

        uint actionType = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        Assert.Equal(0xF753u, actionType);
    }

    [Fact]
    public void Build_ContainsCellIdAfterHeader()
    {
        var body = AutonomousPosition.Build(
            gameActionSequence: 1,
            cellId: 0xDEADBEEFu,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.Equal(0xDEADBEEFu, cellId);
    }

    [Fact]
    public void Build_ContainsPosition_AfterCellId()
    {
        var body = AutonomousPosition.Build(
            gameActionSequence: 2,
            cellId: 0xA9B40001u,
            position: new Vector3(12.5f, 34.0f, 56.75f),
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        float x = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(16));
        float y = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(20));
        float z = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(24));
        Assert.Equal(12.5f, x);
        Assert.Equal(34.0f, y);
        Assert.Equal(56.75f, z);
    }

    [Fact]
    public void Build_IsAlignedTo4Bytes()
    {
        var body = AutonomousPosition.Build(
            gameActionSequence: 3,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        Assert.Equal(0, body.Length % 4);
    }

    [Fact]
    public void Build_TotalLengthIsCorrect_NoCommandsNoExtraFields()
    {
        var body = AutonomousPosition.Build(
            gameActionSequence: 4,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        Assert.Equal(56, body.Length);
    }

    [Fact]
    public void Build_UsesExplicitAirborneContactByte()
    {
        var body = AutonomousPosition.Build(
            gameActionSequence: 7,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            lastContact: 0);

        Assert.Equal(0, body[52]);
    }

    [Fact]
    public void Build_TimestampOrder_MatchesAutonomousPositionPackPack()
    {
        var body = AutonomousPosition.Build(
            gameActionSequence: 8,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0x1111,
            serverControlSequence: 0x2222,
            teleportSequence: 0x3333,
            forcePositionSequence: 0x4444);

        // 12 (envelope) + 32 (Position) = 44.
        int tsOffset = 44;
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
    public void Build_ContactByte_IsContactOnly_NoLongjumpBit()
    {
        var bodyOnGround = AutonomousPosition.Build(
            gameActionSequence: 9,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            lastContact: 1);

        Assert.Equal(1, bodyOnGround[52]);

        var bodyAirborne = AutonomousPosition.Build(
            gameActionSequence: 10,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            lastContact: 0);

        Assert.Equal(0, bodyAirborne[52]);
    }

    [Fact]
    public void Build_ContainsIdentityRotation_AfterPosition()
    {
        var body = AutonomousPosition.Build(
            gameActionSequence: 6,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,  // W=1, X=Y=Z=0
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        // Rotation starts at offset 28: rotW(4), rotX(4), rotY(4), rotZ(4)
        float rotW = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(28));
        float rotX = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(32));
        float rotY = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(36));
        float rotZ = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(40));
        Assert.Equal(1.0f, rotW);
        Assert.Equal(0.0f, rotX);
        Assert.Equal(0.0f, rotY);
        Assert.Equal(0.0f, rotZ);
    }
}
