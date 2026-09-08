using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class CharacterRestore
{
    public const uint RequestOpcode = 0xF7D9u;
    public const uint ResponseOpcode = 0xF643u;

    public readonly record struct Parsed(
        uint VerificationFlag,
        uint? Guid,
        string? Name,
        uint? SecondsGreyedOut)
    {
        public bool IsOk => VerificationFlag == 1u;
    }

    public static byte[] BuildRequestBody(uint characterGuid)
    {
        var w = new PacketWriter(8);
        w.WriteUInt32(RequestOpcode);
        w.WriteUInt32(characterGuid);
        return w.ToArray();
    }

    public static Parsed Parse(ReadOnlySpan<byte> body)
    {
        CharGenVerificationResponse.Parsed shared = CharGenVerificationResponse.Parse(body);
        return new Parsed(shared.RawCode, shared.Guid, shared.Name, shared.SecondsGreyedOut);
    }
}
