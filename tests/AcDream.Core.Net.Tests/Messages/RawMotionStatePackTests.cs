using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class RawMotionStatePackTests
{
    private static byte[] Pack(RawMotionState state)
    {
        var w = new PacketWriter(64);
        RawMotionStatePacker.Pack(w, state);
        return w.ToArray();
    }

    [Fact]
    public void Pack_DefaultState_EmitsOnlyZeroFlags()
    {
        var body = Pack(RawMotionState.Default);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, body);
    }

    [Fact]
    public void Pack_ShiftWalk_OmitsForwardSpeedAndCurrentHoldKey()
    {
        var state = new RawMotionState
        {
            CurrentHoldKey  = HoldKey.None,             // default -> omitted
            ForwardCommand  = 0x45000005u,               // WalkForward -> differs -> set
            ForwardHoldKey  = HoldKey.None,               // differs from Invalid -> set
            ForwardSpeed    = 1.0f,                       // default -> omitted (the D1 fix)
        };

        var body = Pack(state);

        Assert.Equal(12, body.Length);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        Assert.Equal(0x0000000Cu, flags);

        uint fwdCommand = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        Assert.Equal(0x45000005u, fwdCommand);

        uint fwdHoldKey = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        Assert.Equal(1u, fwdHoldKey); // HoldKey.None

        byte[] expected =
        {
            0x0C, 0x00, 0x00, 0x00,
            0x05, 0x00, 0x00, 0x45,
            0x01, 0x00, 0x00, 0x00,
        };
        Assert.Equal(expected, body);
    }

    [Fact]
    public void Pack_RunForward_SetsHoldKeyForwardCommandHoldKeyAndSpeed()
    {
        var state = new RawMotionState
        {
            CurrentHoldKey = HoldKey.Run,
            ForwardCommand = 0x44000007u,
            ForwardHoldKey = HoldKey.Run,
            ForwardSpeed   = 3.0f,
        };

        var body = Pack(state);

        Assert.Equal(20, body.Length);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        Assert.Equal(0x0000001Du, flags);

        byte[] expected =
        {
            0x1D, 0x00, 0x00, 0x00, // flags
            0x02, 0x00, 0x00, 0x00,
            0x07, 0x00, 0x00, 0x44,
            0x02, 0x00, 0x00, 0x00, // forward_holdkey = Run(2)
            0x00, 0x00, 0x40, 0x40,
        };
        Assert.Equal(expected, body);
    }

    [Fact]
    public void Pack_NonDefaultCurrentStyle_SetsStyleBitAndEmitsValue()
    {
        var state = new RawMotionState
        {
            CurrentStyle = 0x80000042u,
        };

        var body = Pack(state);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        Assert.Equal(0x002u, flags);

        uint style = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        Assert.Equal(0x80000042u, style);

        Assert.Equal(8, body.Length);
    }

    [Fact]
    public void Pack_PopulatedActionsList_SetsNumActionsBitsAndEmitsPairs()
    {
        var state = new RawMotionState
        {
            Actions = new[]
            {
                new RawMotionAction(Command: 0x0150, Stamp: 0x0001, Autonomous: false),
                new RawMotionAction(Command: 0x0163, Stamp: 0x7FFF, Autonomous: true),
            },
        };

        var body = Pack(state);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        Assert.Equal(0x1000u, flags);

        // Body: flags(4) + action0(4) + action1(4) = 12 bytes.
        Assert.Equal(12, body.Length);

        ushort cmd0 = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4));
        ushort stamp0 = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
        Assert.Equal((ushort)0x0150, cmd0);
        Assert.Equal((ushort)0x0001, stamp0);

        ushort cmd1 = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(8));
        ushort stamp1 = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(10));
        Assert.Equal((ushort)0x0163, cmd1);
        Assert.Equal((ushort)0xFFFF, stamp1); // (0x7FFF & 0x7FFF) | 0x8000 = 0xFFFF
    }

    [Fact]
    public void Pack_SidestepAndTurnNonDefault_SetExpectedBitsInOrder()
    {
        // sidestep_command(0x020), sidestep_holdkey(0x040), sidestep_speed(0x080),
        // turn_command(0x100), turn_holdkey(0x200), turn_speed(0x400) all non-default.
        var state = new RawMotionState
        {
            SidestepCommand = 0x44000009u,
            SidestepHoldKey = HoldKey.Run,
            SidestepSpeed   = 1.248f,
            TurnCommand     = 0x4400000Du,
            TurnHoldKey     = HoldKey.Run,
            TurnSpeed       = 1.5f,
        };

        var body = Pack(state);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0));
        Assert.Equal(0x000007E0u, flags); // 0x20|0x40|0x80|0x100|0x200|0x400

        // Body order after flags: sidestep_command, sidestep_holdkey,
        // sidestep_speed, turn_command, turn_holdkey, turn_speed.
        uint ssCmd = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        uint ssHold = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        float ssSpeed = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(12));
        uint turnCmd = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16));
        uint turnHold = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20));
        float turnSpeed = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(24));

        Assert.Equal(0x44000009u, ssCmd);
        Assert.Equal(2u, ssHold);
        Assert.Equal(1.248f, ssSpeed);
        Assert.Equal(0x4400000Du, turnCmd);
        Assert.Equal(2u, turnHold);
        Assert.Equal(1.5f, turnSpeed);

        Assert.Equal(28, body.Length);
    }
}
