using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class CharacterError
{
    public const uint Opcode = 0xF659u;

    public enum Code : uint
    {
        Undefined = 0x00,

        Logon = 0x01,

        LoggedOn = 0x02,

        AccountLogon = 0x03,

        ServerCrash = 0x04,

        Logoff = 0x05,

        Delete = 0x06,

        NoPremade = 0x07,

        AccountInUse = 0x08,

        AccountInvalid = 0x09,

        AccountDoesntExist = 0x0A,

        EnterGameGeneric = 0x0B,

        EnterGameStressAccount = 0x0C,

        EnterGameCharacterInWorld = 0x0D,

        EnterGamePlayerAccountMissing = 0x0E,

        EnterGameCharacterNotOwned = 0x0F,

        EnterGameCharacterInWorldServer = 0x10,

        EnterGameOldCharacter = 0x11,

        EnterGameCorruptCharacter = 0x12,

        EnterGameStartServerDown = 0x13,

        EnterGameCouldntPlaceCharacter = 0x14,

        LogonServerFull = 0x15,

        CharacterIsBooted = 0x16,

        EnterGameCharacterLocked = 0x17,

        SubscriptionExpired = 0x18,

        NumErrors = 0x19,
    }

    public readonly record struct Parsed(uint RawErrorCode)
    {
        public Code AsCode => (Code)RawErrorCode;
    }

    public static Parsed Parse(ReadOnlySpan<byte> body)
    {
        int pos = 0;

        uint opcode = ReadU32(body, ref pos);
        if (opcode != Opcode)
            throw new FormatException($"expected CharacterError opcode 0x{Opcode:X4}, got 0x{opcode:X8}");

        uint errorCode = ReadU32(body, ref pos);
        return new Parsed(errorCode);
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
        pos += 4;
        return value;
    }
}
