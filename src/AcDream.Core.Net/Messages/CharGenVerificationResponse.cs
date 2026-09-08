using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class CharGenVerificationResponse
{
    public const uint ResponseOpcode = 0xF643u;

    public enum Code : uint
    {
        Undef = 0,
        Ok = 1,
        Pending = 2,
        NameInUse = 3,
        NameBanned = 4,
        Corrupt = 5,
        DatabaseDown = 6,
        AdminPrivilegeDenied = 7,
    }

    public readonly record struct Parsed(
        uint RawCode,
        uint? Guid,
        string? Name,
        uint? SecondsGreyedOut)
    {
        public Code AsCode => (Code)RawCode;

        /// <summary>True when the trailing identity fields are present.</summary>
        public bool IsOk => RawCode == (uint)Code.Ok;
    }

    /// <summary>
    /// Parse a <c>0xF643</c> body. <paramref name="body"/> must start with
    /// the 4-byte opcode.
    /// </summary>
    public static Parsed Parse(ReadOnlySpan<byte> body)
    {
        int pos = 0;

        uint opcode = ReadU32(body, ref pos);
        if (opcode != ResponseOpcode)
            throw new FormatException(
                $"expected CharacterGenerationVerificationResponse opcode 0x{ResponseOpcode:X4}, got 0x{opcode:X8}");

        uint rawCode = ReadU32(body, ref pos);
        if (rawCode != (uint)Code.Ok)
            return new Parsed(rawCode, null, null, null);

        uint guid = ReadU32(body, ref pos);
        string name = StringReader.ReadString16L(body, ref pos);
        uint secondsGreyedOut = ReadU32(body, ref pos);

        return new Parsed(rawCode, guid, name, secondsGreyedOut);
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
        pos += 4;
        return value;
    }
}
