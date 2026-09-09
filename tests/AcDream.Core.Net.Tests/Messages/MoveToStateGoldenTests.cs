using System;
using System.Buffers.Binary;
using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class MoveToStateGoldenTests
{
    private static readonly Vector3 Pos = new(96f, 96f, 50f);
    private static readonly Quaternion Rot = Quaternion.Identity;

    [Fact]
    public void Build_DefaultRawMotionState_FlagsAreZero_EnvelopePlusPositionPlusTimestampsPlusTrailingByte()
    {
        var body = MoveToState.Build(
            gameActionSequence: 1,
            rawMotionState: RawMotionState.Default,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Rot,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            contact: true,
            standingLongjump: false);

        // 12 (envelope) + 4 (flags=0, no fields) + 32 (Position) + 8 (timestamps)
        // + 1 (trailing byte) = 57, aligned to 60.
        Assert.Equal(60, body.Length);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.Equal(0u, flags);

        uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16));
        Assert.Equal(0xA9B40001u, cellId);

        Assert.Equal(0x01, body[56]);
    }

    [Fact]
    public void Build_WalkForward_OmitsForwardSpeedDefault()
    {
        // Walk-forward at default speed 1.0 -> forward_speed bit OMITTED (D1 fix).
        var state = new RawMotionState
        {
            CurrentHoldKey = HoldKey.None,       // default, omitted
            ForwardCommand = 0x45000005u,        // WalkForward
            ForwardHoldKey = HoldKey.None,       // differs from Invalid -> set
            ForwardSpeed   = 1.0f,                // default, omitted
        };

        var body = MoveToState.Build(
            gameActionSequence: 2,
            rawMotionState: state,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Rot,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            contact: true,
            standingLongjump: false);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.Equal(0x0000000Cu, flags); // ForwardCommand | ForwardHoldKey only

        // RawMotionState body = flags(4) + fwd_cmd(4) + fwd_holdkey(4) = 12.
        uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12 + 12));
        Assert.Equal(0xA9B40001u, cellId);
    }

    [Fact]
    public void Build_RunForward_IncludesHoldKeyAndSpeed()
    {
        var state = new RawMotionState
        {
            CurrentHoldKey = HoldKey.Run,
            ForwardCommand = 0x44000007u, // RunForward
            ForwardHoldKey = HoldKey.Run,
            ForwardSpeed   = 2.94f,
        };

        var body = MoveToState.Build(
            gameActionSequence: 3,
            rawMotionState: state,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Rot,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            contact: true,
            standingLongjump: false);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.Equal(0x0000001Du, flags); // holdkey | fwd_cmd | fwd_holdkey | fwd_speed

        // RawMotionState body = flags(4) + holdkey(4) + fwd_cmd(4) + fwd_holdkey(4) + fwd_speed(4) = 20.
        uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12 + 20));
        Assert.Equal(0xA9B40001u, cellId);
    }

    [Fact]
    public void Build_Sidestep_SetsSidestepBitsOnly()
    {
        var state = new RawMotionState
        {
            SidestepCommand = 0x6500000Fu, // SideStepRight
            SidestepHoldKey = HoldKey.None,
            SidestepSpeed   = 1.0f, // default -> omitted
        };

        var body = MoveToState.Build(
            gameActionSequence: 4,
            rawMotionState: state,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Rot,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            contact: true,
            standingLongjump: false);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.Equal(0x00000060u, flags); // SidestepCommand(0x20) | SidestepHoldKey(0x40)
    }

    [Fact]
    public void Build_Turn_SetsTurnBitsOnly()
    {
        var state = new RawMotionState
        {
            TurnCommand = 0x6500000Du, // TurnRight
            TurnHoldKey = HoldKey.None,
            TurnSpeed   = 1.0f, // default -> omitted
        };

        var body = MoveToState.Build(
            gameActionSequence: 5,
            rawMotionState: state,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Rot,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            contact: true,
            standingLongjump: false);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
        Assert.Equal(0x00000300u, flags); // TurnCommand(0x100) | TurnHoldKey(0x200)
    }

    [Theory]
    [InlineData(false, false, 0x00)]
    [InlineData(true, false, 0x01)]
    [InlineData(false, true, 0x02)]
    [InlineData(true, true, 0x03)]
    public void Build_TrailingByte_AllFourContactLongjumpCombinations(bool contact, bool standingLongjump, byte expected)
    {
        var body = MoveToState.Build(
            gameActionSequence: 6,
            rawMotionState: RawMotionState.Default,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Rot,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            contact: contact,
            standingLongjump: standingLongjump);

        // flags(4) + Position(32) + timestamps(8) = 44; trailing byte at 12+44=56.
        Assert.Equal(expected, body[56]);
    }

    [Fact]
    public void Build_TimestampOrder_MatchesMoveToStatePackPack()
    {
        var body = MoveToState.Build(
            gameActionSequence: 7,
            rawMotionState: RawMotionState.Default,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Rot,
            instanceSequence: 0x1111,
            serverControlSequence: 0x2222,
            teleportSequence: 0x3333,
            forcePositionSequence: 0x4444,
            contact: true,
            standingLongjump: false);

        // 12 (envelope) + 4 (flags) + 32 (Position) = 48.
        int tsOffset = 48;
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
    public void Build_IsAlignedTo4Bytes()
    {
        var body = MoveToState.Build(
            gameActionSequence: 8,
            rawMotionState: RawMotionState.Default,
            cellId: 0xA9B40001u,
            position: Pos,
            rotation: Rot,
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0,
            contact: true,
            standingLongjump: false);

        Assert.Equal(0, body.Length % 4);
    }
}
