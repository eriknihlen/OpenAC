using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class UpdateMotionTests
{
    [Fact]
    public void RejectsWrongOpcode()
    {
        var body = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xDEADBEEFu);
        Assert.Null(UpdateMotion.TryParse(body));
    }

    [Fact]
    public void RejectsTruncated()
    {
        Assert.Null(UpdateMotion.TryParse(new byte[3]));
        Assert.Null(UpdateMotion.TryParse(Array.Empty<byte>()));
    }

    [Fact]
    public void ParsesStanceOnly_WhenForwardCommandFlagUnset()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4 + 2];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x12345678u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0001); p += 2;
        // 8-byte header slot — leave zero
        p += 6;
        body[p++] = 0;     // movementType = Invalid
        body[p++] = 0;     // motionFlags
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0042); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x1u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0005); p += 2;

        var result = UpdateMotion.TryParse(body);
        Assert.NotNull(result);
        Assert.Equal(0x12345678u, result!.Value.Guid);
        Assert.Equal((ushort)0x0005, result.Value.MotionState.Stance);
        Assert.Null(result.Value.MotionState.ForwardCommand);
    }

    [Fact]
    public void ParsesStanceAndForwardCommand()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4 + 2 + 2];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xABCDEF01u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0010); p += 2;
        p += 6;   // MovementData header slot
        body[p++] = 0;
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0000); p += 2;  // outer style = 0
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x3u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x000D); p += 2;  // stance = 0xD
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0007); p += 2;

        var result = UpdateMotion.TryParse(body);
        Assert.NotNull(result);
        Assert.Equal(0xABCDEF01u, result!.Value.Guid);
        Assert.Equal((ushort)0x000D, result.Value.MotionState.Stance);
        Assert.Equal((ushort)0x0007, result.Value.MotionState.ForwardCommand);
    }

    [Fact]
    public void ParsesNoFlagsSet_KeepsOuterStance()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x55555555u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;
        body[p++] = 0;
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x00AA); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0u); p += 4;  // no flags

        var result = UpdateMotion.TryParse(body);
        Assert.NotNull(result);
        Assert.Equal((ushort)0x00AA, result!.Value.MotionState.Stance);
        Assert.Null(result.Value.MotionState.ForwardCommand);
    }

    [Fact]
    public void ParsesForwardSpeed_WhenSpeedFlagSet()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4 + 2 + 2 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x1A2B3C4Du); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;  // MovementData header
        body[p++] = 0;
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x7u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;  // NonCombat
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0007); p += 2;  // RunForward
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.5f); p += 4;    // speed

        var result = UpdateMotion.TryParse(body);
        Assert.NotNull(result);
        Assert.Equal((ushort)0x003D, result!.Value.MotionState.Stance);
        Assert.Equal((ushort)0x0007, result.Value.MotionState.ForwardCommand);
        Assert.Equal(1.5f, result.Value.MotionState.ForwardSpeed);
    }

    [Fact]
    public void ParsesCommandsList_Wave()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4 + 2 + 2 + 8];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xDEADBEEFu); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;
        body[p++] = 0;
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x83u); p += 4; // flags=0x3 + numCommands=1
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2; // stance
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0003); p += 2; // fwd cmd = Ready

        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0087); p += 2; // Wave
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0001); p += 2;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.0f); p += 4;

        var result = UpdateMotion.TryParse(body);
        Assert.NotNull(result);
        Assert.Equal((ushort)0x003D, result!.Value.MotionState.Stance);
        Assert.Equal((ushort)0x0003, result.Value.MotionState.ForwardCommand);

        Assert.NotNull(result.Value.MotionState.Commands);
        Assert.Single(result.Value.MotionState.Commands!);
        var wave = result.Value.MotionState.Commands![0];
        Assert.Equal((ushort)0x0087, wave.Command);
        Assert.Equal(1.0f, wave.Speed);
    }

    [Fact]
    public void HandlesNonInvalidMovementType_GracefullyReturnsOuterStance()
    {
        // movementType != 0 means one of the Move* variants; a truncated
        // non-Invalid payload still returns the outer state.
        // The parser must still return a valid Parsed with the outer stance
        // and a null ForwardCommand rather than failing the whole message.
        var body = new byte[4 + 4 + 2 + 6 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x99999999u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;
        body[p++] = 7;    // movementType = MoveToPosition (non-Invalid)
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x00CC); p += 2;

        var result = UpdateMotion.TryParse(body);
        Assert.NotNull(result);
        Assert.Equal((ushort)0x00CC, result!.Value.MotionState.Stance);
        Assert.Null(result.Value.MotionState.ForwardCommand);
        Assert.Equal((byte)7, result.Value.MotionState.MovementType);
        Assert.True(result.Value.MotionState.IsServerControlledMoveTo);
    }

    [Fact]
    public void ParsesMoveToPositionSpeedAndRunRate()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 16 + 28 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80001234u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;
        body[p++] = 7; // MoveToPosition
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xA8B4000Eu); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 10f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 20f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 30f); p += 4;

        const uint canWalkCanRunMoveTowards = 0x1u | 0x2u | 0x200u;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), canWalkCanRunMoveTowards); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 0.6f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 0.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), float.MaxValue); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.25f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 15.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 90.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.5f); p += 4;

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal((byte)7, result!.Value.MotionState.MovementType);
        Assert.True(result.Value.MotionState.IsServerControlledMoveTo);
        Assert.Equal((ushort)0x003D, result.Value.MotionState.Stance);
        Assert.Null(result.Value.MotionState.ForwardCommand);
        Assert.Equal(canWalkCanRunMoveTowards, result.Value.MotionState.MoveToParameters);
        Assert.Equal(1.25f, result.Value.MotionState.MoveToSpeed);
        Assert.Equal(1.5f, result.Value.MotionState.MoveToRunRate);
        Assert.True(result.Value.MotionState.MoveToCanRun);
        Assert.True(result.Value.MotionState.MoveTowards);

        Assert.NotNull(result.Value.MotionState.MoveToPath);
        var path = result.Value.MotionState.MoveToPath!.Value;
        Assert.Null(path.TargetGuid);
        Assert.Equal(0xA8B4000Eu, path.OriginCellId);
        Assert.Equal(10f, path.OriginX);
        Assert.Equal(20f, path.OriginY);
        Assert.Equal(30f, path.OriginZ);
        Assert.Equal(0.6f, path.DistanceToObject);
        Assert.Equal(0.0f, path.MinDistance);
        Assert.Equal(float.MaxValue, path.FailDistance);
        Assert.Equal(15.0f, path.WalkRunThreshold);
        Assert.Equal(90.0f, path.DesiredHeading);
    }

    [Fact]
    public void ParsesAttackHigh1_AsActionForwardCommand()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4 + 2 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x800003B5u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6; // header padding

        body[p++] = 0; // mt = Invalid (interpreted)
        body[p++] = 0; // motion_flags
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003C); p += 2; // stance: HandCombat

        // InterpretedMotionState: flags = ForwardCommand (0x02) | ForwardSpeed (0x04)
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x06u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0062); p += 2; // AttackHigh1 low bits
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.25f); p += 4; // animSpeed

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal((byte)0, result!.Value.MotionState.MovementType);
        Assert.False(result.Value.MotionState.IsServerControlledMoveTo);
        Assert.Equal((ushort)0x0062, result.Value.MotionState.ForwardCommand);
        Assert.Equal(1.25f, result.Value.MotionState.ForwardSpeed);
    }

    [Fact]
    public void ParsesSequenceNumbersAndAutonomyFlag()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x50001234u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0102); p += 2;  // instanceSeq
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0304); p += 2;  // movementSeq
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0506); p += 2;  // serverControlSeq
        body[p++] = 1;   // isAutonomous
        p += 1;          // Align(4) pad
        body[p++] = 0;   // movementType = Invalid
        body[p++] = 0;   // motionFlags
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;  // outer stance
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0u); p += 4;      // no IMS flags

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal((ushort)0x0102, result!.Value.InstanceSequence);
        Assert.Equal((ushort)0x0304, result.Value.MovementSequence);
        Assert.Equal((ushort)0x0506, result.Value.ServerControlSequence);
        Assert.True(result.Value.IsAutonomous);
    }

    [Fact]
    public void ParsesMoveToObjectTargetGuidAndOrigin()
    {
        // Type 6 (MoveToObject) prepends a u32 target guid before the
        // standard Origin + MovementParameters + runRate payload.
        // Body size: 20 (header) + 4 (guid) + 16 (origin) + 28 (params) + 4 (runRate) = 72.
        var body = new byte[20 + 4 + 16 + 28 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80004321u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6; // MovementData header padding

        body[p++] = 6; // MoveToObject
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80001234u); p += 4; // target guid

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xA8B4000Eu); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 5f); p += 4;          // origin x
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 6f); p += 4;          // origin y
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 7f); p += 4;          // origin z

        const uint flags = 0x1u | 0x2u | 0x200u;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), flags); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 0.6f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 0.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), float.MaxValue); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 15.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.57f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.25f); p += 4;       // runRate

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal((byte)6, result!.Value.MotionState.MovementType);
        Assert.True(result.Value.MotionState.IsServerControlledMoveTo);
        Assert.NotNull(result.Value.MotionState.MoveToPath);
        var path = result.Value.MotionState.MoveToPath!.Value;
        Assert.Equal(0x80001234u, path.TargetGuid);
        Assert.Equal(0xA8B4000Eu, path.OriginCellId);
        Assert.Equal(5f, path.OriginX);
        Assert.Equal(6f, path.OriginY);
        Assert.Equal(7f, path.OriginZ);
        Assert.Equal(1.25f, result.Value.MotionState.MoveToRunRate);
    }


    [Fact]
    public void ParsesTurnToObject_GuidWireHeadingAndParams()
    {
        // Header (20 bytes) + guid (4) + wireHeading (4) + TurnToParameters (12) = 40.
        var body = new byte[20 + 4 + 4 + 12];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80005678u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6; // MovementData header padding

        body[p++] = 8; // TurnToObject
        body[p++] = 0; // motionFlags
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80009999u); p += 4; // target guid
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 42.0f); p += 4;       // standalone wire_heading (field2)

        const uint flags = 0x1u | 0x2u | 0x200u;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), flags); p += 4;        // TurnToParameters.bitfield
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.5f); p += 4;         // TurnToParameters.speed
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 199.0f); p += 4;       // TurnToParameters.desired_heading (field5) — DELIBERATELY != field2

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal((byte)8, result!.Value.MotionState.MovementType);
        Assert.True(result.Value.MotionState.IsServerControlledTurnTo);
        Assert.False(result.Value.MotionState.IsServerControlledMoveTo);
        Assert.NotNull(result.Value.MotionState.TurnToPath);

        var path = result.Value.MotionState.TurnToPath!.Value;
        Assert.Equal(0x80009999u, path.TargetGuid);
        Assert.Equal(42.0f, path.WireHeading);       // field2 — distinguished by OFFSET
        Assert.Equal(flags, path.Bitfield);
        Assert.Equal(1.5f, path.Speed);
        Assert.Equal(199.0f, path.DesiredHeading);   // field5 — distinct from field2

        var mp = MovementParameters.FromWireTurnTo(path.Bitfield, path.Speed, path.DesiredHeading);
        Assert.True(mp.CanRun);
        Assert.Equal(1.5f, mp.Speed);
        Assert.Equal(199.0f, mp.DesiredHeading);
    }

    [Fact]
    public void ParsesTurnToObject_UnresolvableFallback_BothHeadingsSurvivedDistinctly()
    {
        var body = new byte[20 + 4 + 4 + 12];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x8000AAAAu); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;

        body[p++] = 8;
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xDEADBEEFu); p += 4; // unresolvable guid
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 270.0f); p += 4;      // wire_heading — the fallback source

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x1u); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 0.0f); p += 4;        // params.desired_heading (would be overwritten by wire_heading on fallback)

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        var path = result!.Value.MotionState.TurnToPath!.Value;
        Assert.Equal(0xDEADBEEFu, path.TargetGuid);
        Assert.Equal(270.0f, path.WireHeading);
        Assert.Equal(0.0f, path.DesiredHeading);
        Assert.NotEqual(path.WireHeading, path.DesiredHeading);
    }

    [Fact]
    public void ParsesTurnToHeading_ThreeDwordFormOnly_NoGuidOrWireHeading()
    {
        // Header (20 bytes) + TurnToParameters (12) = 32. No guid, no
        // standalone heading field — TurnToHeadingExtensions.Write emits
        // ONLY the 3-dword UnPackNet form (TurnToHeading.cs:18-21).
        var body = new byte[20 + 12];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x8000BBBBu); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;

        body[p++] = 9; // TurnToHeading
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;

        const uint flags = 0x2u | 0x800u;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), flags); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 2.0f); p += 4;   // speed
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 315.0f); p += 4; // desired_heading

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal((byte)9, result!.Value.MotionState.MovementType);
        Assert.True(result.Value.MotionState.IsServerControlledTurnTo);
        Assert.NotNull(result.Value.MotionState.TurnToPath);

        var path = result.Value.MotionState.TurnToPath!.Value;
        Assert.Null(path.TargetGuid);
        Assert.Null(path.WireHeading);
        Assert.Equal(flags, path.Bitfield);
        Assert.Equal(2.0f, path.Speed);
        Assert.Equal(315.0f, path.DesiredHeading);
    }

    [Theory]
    [InlineData(0u)]                 // no flags
    [InlineData(0x1u | 0x2u)]
    [InlineData(0x10u)]
    [InlineData(0x3FFFFu)]
    public void ParsesTurnToHeading_FlagPermutations_BitfieldRoundTrips(uint bitfield)
    {
        var body = new byte[20 + 12];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80001111u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;

        body[p++] = 9;
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), bitfield); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 0.0f); p += 4;

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal(bitfield, result!.Value.MotionState.TurnToPath!.Value.Bitfield);
    }


    [Fact]
    public void MoveToPositionPath_FeedsFromWire_AllSevenFieldsSurvive()
    {
        var body = new byte[20 + 16 + 28 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80002222u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;

        body[p++] = 7; // MoveToPosition
        body[p++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;

        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xA8B4000Eu); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 11f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 22f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 33f); p += 4;

        const uint flags = 0x1u | 0x2u | 0x4u | 0x8u | 0x10u | 0x200u | 0x400u;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), flags); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 0.6f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 0.1f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 50.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 1.25f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 15.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 123.0f); p += 4;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(p), 2.75f); p += 4; // runRate

        var result = UpdateMotion.TryParse(body);
        Assert.NotNull(result);
        var path = result!.Value.MotionState.MoveToPath!.Value;

        var mp = MovementParameters.FromWire(
            flags,
            path.DistanceToObject,
            path.MinDistance,
            path.FailDistance,
            result.Value.MotionState.MoveToSpeed!.Value,
            path.WalkRunThreshold,
            path.DesiredHeading);

        Assert.True(mp.CanWalk);
        Assert.True(mp.CanRun);
        Assert.True(mp.CanSidestep);
        Assert.True(mp.CanWalkBackwards);
        Assert.True(mp.CanCharge);
        Assert.True(mp.MoveTowards);
        Assert.True(mp.UseSpheres);
        Assert.Equal(0.6f, mp.DistanceToObject);
        Assert.Equal(0.1f, mp.MinDistance);
        Assert.Equal(50.0f, mp.FailDistance);
        Assert.Equal(1.25f, mp.Speed);
        Assert.Equal(15.0f, mp.WalkRunThreshhold);
        Assert.Equal(123.0f, mp.DesiredHeading);
        Assert.Equal(2.75f, result.Value.MotionState.MoveToRunRate);
    }


    [Fact]
    public void ParsesStickyGuidTrailer_WhenMotionFlagsBitSet()
    {
        // motionFlags byte1&0x1 (StickToObject) set; InterpretedMotionState
        // flags = 0 (no fields), so the sticky guid dword immediately
        // follows the packed flags dword.
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80003333u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;

        body[p++] = 0;    // movementType = Invalid
        body[p++] = 0x1;  // motionFlags = StickToObject
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0u); p += 4;           // no IMS flags
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80004444u); p += 4;  // sticky object guid

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal(0x80004444u, result!.Value.MotionState.StickyObjectGuid);
    }

    [Fact]
    public void SkipsStickyGuidTrailer_WhenMotionFlagsBitClear()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80005555u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;

        body[p++] = 0;    // movementType = Invalid
        body[p++] = 0;    // motionFlags = none
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x003D); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0u); p += 4;           // no IMS flags

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Null(result!.Value.MotionState.StickyObjectGuid);
    }

    [Fact]
    public void ParsesStickyGuidTrailer_CursorHonesty_BytesAfterTrailerStillParseCorrectly()
    {
        var body = new byte[4 + 4 + 2 + 6 + 4 + 4 + 2 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0xF74Cu); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80006666u); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;
        p += 6;

        body[p++] = 0;    // movementType = Invalid
        body[p++] = 0x1;  // motionFlags = StickToObject
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0); p += 2;            // outer stance
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x2u); p += 4;         // IMS flags = ForwardCommand only
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(p), 0x0007); p += 2;       // ForwardCommand = RunForward
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 0x80007777u); p += 4;  // sticky object guid — AFTER ForwardCommand

        var result = UpdateMotion.TryParse(body);

        Assert.NotNull(result);
        Assert.Equal((ushort)0x0007, result!.Value.MotionState.ForwardCommand);
        Assert.Equal(0x80007777u, result.Value.MotionState.StickyObjectGuid);
    }
}
