using System;
using System.Buffers.Binary;
using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class MoveToStateTests
{
    private static readonly Vector3 Pos = new(96f, 96f, 50f);

    [Fact]
    public void Build_IdleState_ProducesValidGameAction()
    {
        var body = MoveToState.Build(
            gameActionSequence: 1,
            rawMotionState: RawMotionState.Default,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        // First 4 bytes: GameAction opcode 0xF7B1
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        Assert.Equal(0xF7B1u, opcode);

        // Bytes 4-7: game action sequence
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        Assert.Equal(1u, seq);

        // Bytes 8-11: MoveToState action type 0xF61C
        uint actionType = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        Assert.Equal(0xF61Cu, actionType);
    }

    [Fact]
    public void Build_WalkForward_DefaultSpeedOmitted_OnlyCommandAndHoldKeyFlagsSet()
    {
        var state = new RawMotionState
        {
            ForwardCommand = 0x45000005u, // WalkForward
            ForwardHoldKey = HoldKey.None, // differs from Invalid -> set
            ForwardSpeed   = 1.0f,         // default -> omitted
        };

        var body = MoveToState.Build(
            gameActionSequence: 2,
            rawMotionState: state,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.True((flags & 0x4u) != 0, "ForwardCommand flag (0x4) should be set");
        Assert.True((flags & 0x8u) != 0, "ForwardHoldKey flag (0x8) should be set");
        Assert.False((flags & 0x10u) != 0, "ForwardSpeed flag (0x10) must be OMITTED at the retail default 1.0");
    }

    [Fact]
    public void Build_IdleState_RawMotionFlagsAreZero()
    {
        var body = MoveToState.Build(
            gameActionSequence: 3,
            rawMotionState: RawMotionState.Default,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.Equal(0u, flags);
    }

    [Fact]
    public void Build_IdleState_WorldPositionFollowsZeroFlagMotionState()
    {
        var body = MoveToState.Build(
            gameActionSequence: 4,
            rawMotionState: RawMotionState.Default,
            cellId: 0xDEADBEEFu,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16));
        Assert.Equal(0xDEADBEEFu, cellId);
    }

    [Fact]
    public void Build_IsAlignedTo4Bytes()
    {
        var body = MoveToState.Build(
            gameActionSequence: 5,
            rawMotionState: RawMotionState.Default,
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
    public void Build_UsesExplicitAirborneContact()
    {
        var body = MoveToState.Build(
            gameActionSequence: 7,
            rawMotionState: RawMotionState.Default,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            contact: false);

        // flags(4) + Position(32) + timestamps(8) = 44; trailing byte at 12+44=56.
        Assert.Equal(0, body[56]);
    }

    [Fact]
    public void Build_WithHoldKey_IncludesHoldKeyFlag()
    {
        var state = new RawMotionState { CurrentHoldKey = HoldKey.Run };

        var body = MoveToState.Build(
            gameActionSequence: 6,
            rawMotionState: state,
            cellId: 0xA9B40001u,
            position: Vector3.Zero,
            rotation: Quaternion.Identity,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.True((flags & 0x1u) != 0, "CurrentHoldKey flag (0x1) should be set");

        // The hold key value (u32 = 2) should immediately follow the flags
        uint holdKeyValue = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16));
        Assert.Equal(2u, holdKeyValue);
    }
}
