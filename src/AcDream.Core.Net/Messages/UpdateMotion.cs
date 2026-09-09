using System.Buffers.Binary;
using System.Collections.Generic;

namespace AcDream.Core.Net.Messages;

public static class UpdateMotion
{
    public const uint Opcode = 0xF74Cu;

    public readonly record struct Parsed(
        uint Guid,
        CreateObject.ServerMotionState MotionState,
        ushort InstanceSequence,
        ushort MovementSequence,
        ushort ServerControlSequence,
        bool IsAutonomous);

    /// <summary>
    /// Parse a reassembled UpdateMotion body. <paramref name="body"/> must
    /// start with the 4-byte opcode. Returns null on malformed input
    /// (truncated fields, wrong opcode, malformed InterpretedMotionState).
    /// </summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        try
        {
            int pos = 0;

            if (body.Length - pos < 4) return null;
            uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;
            if (opcode != Opcode) return null;

            if (body.Length - pos < 4) return null;
            uint guid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;

            if (body.Length - pos < 2) return null;
            ushort instanceSequence = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
            pos += 2;

            if (body.Length - pos < 6) return null;
            ushort movementSequence = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
            pos += 2;
            ushort serverControlSequence = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
            pos += 2;
            bool isAutonomous = body[pos] != 0;
            pos += 2; // u8 isAutonomous + Align(4) pad byte

            if (body.Length - pos < 4) return null;
            byte movementType = body[pos]; pos += 1;
            byte motionFlags = body[pos]; pos += 1;
            ushort currentStyle = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
            pos += 2;

            if (Environment.GetEnvironmentVariable("ACDREAM_DUMP_MOTION") == "1")
            {
                int preHex = Math.Min(body.Length, 32);
                var hex = new System.Text.StringBuilder();
                for (int i = 0; i < preHex; i++) hex.Append($"{body[i]:X2} ");
                System.Console.WriteLine(
                    $"  UM raw: mt=0x{movementType:X2} mf=0x{motionFlags:X2} cs=0x{currentStyle:X4}  | {hex}");
            }

            ushort? forwardCommand = null;
            float? forwardSpeed = null;
            ushort? sidestepCommand = null;
            float? sidestepSpeed = null;
            ushort? turnCommand = null;
            float? turnSpeed = null;
            uint? moveToParameters = null;
            float? moveToSpeed = null;
            float? moveToRunRate = null;
            CreateObject.MoveToPathData? moveToPath = null;
            CreateObject.TurnToPathData? turnToPath = null;
            uint? stickyObjectGuid = null;
            List<CreateObject.MotionItem>? commands = null;

            if (movementType == 0)
            {
                if (body.Length - pos < 4) return new Parsed(guid, new CreateObject.ServerMotionState(currentStyle, null, MovementType: movementType), instanceSequence, movementSequence, serverControlSequence, isAutonomous);
                uint packed = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
                uint flags = packed & 0x7Fu;
                uint numCommands = packed >> 7;


                if ((flags & 0x1u) != 0)
                {
                    if (body.Length - pos < 2) return new Parsed(guid, new CreateObject.ServerMotionState(currentStyle, null, MovementType: movementType), instanceSequence, movementSequence, serverControlSequence, isAutonomous);
                    currentStyle = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
                    pos += 2;
                }
                if ((flags & 0x2u) != 0)
                {
                    if (body.Length - pos < 2) return new Parsed(guid, new CreateObject.ServerMotionState(currentStyle, null, MovementType: movementType), instanceSequence, movementSequence, serverControlSequence, isAutonomous);
                    forwardCommand = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
                    pos += 2;
                }
                // SideStepCommand — ushort, bit 0x8
                if ((flags & 0x8u) != 0)
                {
                    if (body.Length - pos < 2) goto done;
                    sidestepCommand = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
                    pos += 2;
                }
                // TurnCommand — ushort, bit 0x20
                if ((flags & 0x20u) != 0)
                {
                    if (body.Length - pos < 2) goto done;
                    turnCommand = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
                    pos += 2;
                }
                // ForwardSpeed — float, bit 0x4
                if ((flags & 0x4u) != 0)
                {
                    if (body.Length - pos < 4) goto done;
                    forwardSpeed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                    pos += 4;
                }
                // SideStepSpeed — float, bit 0x10
                if ((flags & 0x10u) != 0)
                {
                    if (body.Length - pos < 4) goto done;
                    sidestepSpeed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                    pos += 4;
                }
                // TurnSpeed — float, bit 0x40
                if ((flags & 0x40u) != 0)
                {
                    if (body.Length - pos < 4) goto done;
                    turnSpeed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                    pos += 4;
                }

                if (numCommands > 0 && numCommands < 1024)
                {
                    commands = new List<CreateObject.MotionItem>((int)numCommands);
                    for (int i = 0; i < numCommands; i++)
                    {
                        if (body.Length - pos < 8) break;
                        ushort cmd = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
                        ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 2));
                        float speed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 4));
                        pos += 8;
                        commands.Add(new CreateObject.MotionItem(cmd, seq, speed));
                    }
                }

                if ((motionFlags & 0x1) != 0 && body.Length - pos >= 4)
                {
                    stickyObjectGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                    pos += 4;
                }
                done:;
            }
            else if (movementType is 6 or 7)
            {
                TryParseMoveToPayload(
                    body,
                    pos,
                    movementType,
                    out moveToParameters,
                    out moveToSpeed,
                    out moveToRunRate,
                    out moveToPath);
            }
            else if (movementType is 8 or 9)
            {
                TryParseTurnToPayload(
                    body,
                    pos,
                    movementType,
                    out turnToPath);
            }

            return new Parsed(guid, new CreateObject.ServerMotionState(
                currentStyle, forwardCommand, forwardSpeed, commands,
                sidestepCommand, sidestepSpeed, turnCommand, turnSpeed,
                movementType,
                moveToParameters,
                moveToSpeed,
                moveToRunRate,
                moveToPath,
                turnToPath,
                stickyObjectGuid,
                (motionFlags & 0x2) != 0),
                instanceSequence, movementSequence, serverControlSequence, isAutonomous);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseMoveToPayload(
        ReadOnlySpan<byte> body,
        int pos,
        byte movementType,
        out uint? movementParameters,
        out float? speed,
        out float? runRate,
        out CreateObject.MoveToPathData? path)
    {
        movementParameters = null;
        speed = null;
        runRate = null;
        path = null;

        uint? targetGuid = null;
        if (movementType == 6)
        {
            if (body.Length - pos < 4) return false;
            targetGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;
        }

        if (body.Length - pos < 16 + 28 + 4) return false;

        uint originCellId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
        pos += 4;
        float originX = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        float originY = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        float originZ = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;

        movementParameters = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
        pos += 4;
        float distanceToObject = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        float minDistance = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        float failDistance = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        speed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        float walkRunThreshold = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        float desiredHeading = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        runRate = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));

        path = new CreateObject.MoveToPathData(
            targetGuid,
            originCellId,
            originX,
            originY,
            originZ,
            distanceToObject,
            minDistance,
            failDistance,
            walkRunThreshold,
            desiredHeading,
            movementParameters ?? 0u); // R4-V4: the raw flags dword -> FromWire
        return true;
    }

    private static bool TryParseTurnToPayload(
        ReadOnlySpan<byte> body,
        int pos,
        byte movementType,
        out CreateObject.TurnToPathData? path)
    {
        path = null;

        uint? targetGuid = null;
        float? wireHeading = null;
        if (movementType == 8)
        {
            if (body.Length - pos < 8) return false;
            targetGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;
            wireHeading = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
            pos += 4;
        }

        // TurnToParameters / UnPackNet 3-dword TurnTo form (0xc bytes):
        // bitfield, speed, desired_heading.
        if (body.Length - pos < 12) return false;

        uint bitfield = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
        pos += 4;
        float speed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        float desiredHeading = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));

        path = new CreateObject.TurnToPathData(
            targetGuid,
            wireHeading,
            bitfield,
            speed,
            desiredHeading);
        return true;
    }
}
