using System.Buffers.Binary;
using System.Text;

namespace AcDream.Core.Net.Messages;

public static class CharacterList
{
    public const uint Opcode = 0xF658u;

    public readonly record struct Character(uint Id, string Name, uint SecondsGreyedOut);

    public readonly record struct Selection(int ActiveIndex, Character Character);

    public sealed record Parsed(
        uint Status,
        IReadOnlyList<Character> Characters,
        IReadOnlyList<Character> DeletedCharacters,
        int SlotCount,
        string AccountName,
        bool UseTurbineChat,
        bool HasThroneOfDestiny);

    public static Parsed Parse(ReadOnlySpan<byte> body)
    {
        int pos = 0;

        uint opcode = ReadU32(body, ref pos);
        if (opcode != Opcode)
            throw new FormatException($"expected CharacterList opcode 0x{Opcode:X4}, got 0x{opcode:X8}");

        uint status = ReadU32(body, ref pos);
        Character[] characters = ReadCharacters(body, ref pos, "active");
        Character[] deletedCharacters = ReadCharacters(body, ref pos, "deleted");
        int slotCount = unchecked((int)ReadU32(body, ref pos));
        string accountName = ReadString16L(body, ref pos);
        bool useTurbineChat = ReadU32(body, ref pos) != 0;
        bool hasThroneOfDestiny = ReadU32(body, ref pos) != 0;

        return new Parsed(
            status,
            characters,
            deletedCharacters,
            slotCount,
            accountName,
            useTurbineChat,
            hasThroneOfDestiny);
    }

    public static bool TrySelectFirstAvailable(Parsed parsed, out Selection selection)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        for (int i = 0; i < parsed.Characters.Count; i++)
        {
            Character character = parsed.Characters[i];
            if (!IsAvailableActiveIdentity(character))
                continue;

            selection = new Selection(i, character);
            return true;
        }

        selection = default;
        return false;
    }

    public static bool IsAvailableActiveIdentity(Character character) =>
        character.Id != 0 && character.SecondsGreyedOut == 0;

    private static Character[] ReadCharacters(
        ReadOnlySpan<byte> body,
        ref int pos,
        string collectionName)
    {
        uint count = ReadU32(body, ref pos);
        if (count > (uint)((body.Length - pos) / 12))
            throw new FormatException(
                $"{collectionName} character count {count} exceeds remaining payload");

        var characters = new Character[checked((int)count)];
        for (int i = 0; i < characters.Length; i++)
        {
            uint id = ReadU32(body, ref pos);
            string name = ReadString16L(body, ref pos);
            uint secondsGreyedOut = ReadU32(body, ref pos);
            characters[i] = new Character(id, name, secondsGreyedOut);
        }

        return characters;
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
        pos += 4;
        return value;
    }

    private static string ReadString16L(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated String16L length");
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        if (source.Length - pos < length) throw new FormatException("truncated String16L body");
        string result = Encoding.GetEncoding(1252).GetString(source.Slice(pos, length));
        pos += length;
        int recordSize = 2 + length;
        int padding = (4 - (recordSize & 3)) & 3;
        if (source.Length - pos < padding)
            throw new FormatException("truncated String16L padding");
        pos += padding;
        return result;
    }
}
