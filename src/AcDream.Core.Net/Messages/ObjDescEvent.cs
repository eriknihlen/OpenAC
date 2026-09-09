using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class ObjDescEvent
{
    public const uint Opcode = 0xF625u;

    public readonly record struct Parsed(
        uint Guid,
        CreateObject.ModelData ModelData,
        ushort InstanceSequence,
        ushort ObjDescSequence);

    /// <summary>
    /// Parse an ObjDescEvent body (must start with the 4-byte opcode).
    /// Returns null on truncation or wrong opcode.
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

            var modelData = CreateObject.ReadModelData(body, ref pos);

            if (body.Length - pos != 4) return null;
            ushort instance = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
            ushort objDesc = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 2));
            return new Parsed(guid, modelData, instance, objDesc);
        }
        catch
        {
            return null;
        }
    }
}
