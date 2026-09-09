using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CharacterErrorTests
{
    [Theory]
    [InlineData(0x00u, CharacterError.Code.Undefined)]
    [InlineData(0x01u, CharacterError.Code.Logon)]
    [InlineData(0x02u, CharacterError.Code.LoggedOn)]
    [InlineData(0x03u, CharacterError.Code.AccountLogon)]
    [InlineData(0x04u, CharacterError.Code.ServerCrash)]
    [InlineData(0x05u, CharacterError.Code.Logoff)]
    [InlineData(0x06u, CharacterError.Code.Delete)]
    [InlineData(0x07u, CharacterError.Code.NoPremade)]
    [InlineData(0x08u, CharacterError.Code.AccountInUse)]
    [InlineData(0x09u, CharacterError.Code.AccountInvalid)]
    [InlineData(0x0Au, CharacterError.Code.AccountDoesntExist)]
    [InlineData(0x0Bu, CharacterError.Code.EnterGameGeneric)]
    [InlineData(0x0Cu, CharacterError.Code.EnterGameStressAccount)]
    [InlineData(0x0Du, CharacterError.Code.EnterGameCharacterInWorld)]
    [InlineData(0x0Eu, CharacterError.Code.EnterGamePlayerAccountMissing)]
    [InlineData(0x0Fu, CharacterError.Code.EnterGameCharacterNotOwned)]
    [InlineData(0x10u, CharacterError.Code.EnterGameCharacterInWorldServer)]
    [InlineData(0x11u, CharacterError.Code.EnterGameOldCharacter)]
    [InlineData(0x12u, CharacterError.Code.EnterGameCorruptCharacter)]
    [InlineData(0x13u, CharacterError.Code.EnterGameStartServerDown)]
    [InlineData(0x14u, CharacterError.Code.EnterGameCouldntPlaceCharacter)]
    [InlineData(0x15u, CharacterError.Code.LogonServerFull)]
    [InlineData(0x16u, CharacterError.Code.CharacterIsBooted)]
    [InlineData(0x17u, CharacterError.Code.EnterGameCharacterLocked)]
    [InlineData(0x18u, CharacterError.Code.SubscriptionExpired)]
    [InlineData(0x19u, CharacterError.Code.NumErrors)]
    public void Parse_EveryRetailCode_RoundTripsRawAndNamedValue(uint raw, CharacterError.Code expected)
    {
        var w = AceWireWriter.GameMessage(CharacterError.Opcode).Write(raw);

        CharacterError.Parsed parsed = CharacterError.Parse(w.ToArray());

        Assert.Equal(raw, parsed.RawErrorCode);
        Assert.Equal(expected, parsed.AsCode);
        Assert.Equal((uint)expected, raw);
    }

    [Fact]
    public void Parse_UnknownErrorCode_DoesNotThrow_PreservesRawValue()
    {
        var w = AceWireWriter.GameMessage(CharacterError.Opcode).Write(0xDEADBEEFu);

        CharacterError.Parsed parsed = CharacterError.Parse(w.ToArray());

        Assert.Equal(0xDEADBEEFu, parsed.RawErrorCode);
        Assert.Equal((CharacterError.Code)0xDEADBEEFu, parsed.AsCode);
    }

    [Fact]
    public void Parse_MaxUintErrorCode_DoesNotThrow()
    {
        var w = AceWireWriter.GameMessage(CharacterError.Opcode).Write(uint.MaxValue);

        CharacterError.Parsed parsed = CharacterError.Parse(w.ToArray());

        Assert.Equal(uint.MaxValue, parsed.RawErrorCode);
    }

    [Fact]
    public void Parse_ExactByteSequence_MatchesAceSerializer()
    {
        byte[] body = AceWireWriter.GameMessage(CharacterError.Opcode)
            .Write((uint)CharacterError.Code.Delete)
            .ToArray();

        byte[] expected =
        [
            0x59, 0xF6, 0x00, 0x00,  // opcode 0xF659 LE
            0x06, 0x00, 0x00, 0x00,
        ];

        Assert.Equal(expected, body);

        CharacterError.Parsed parsed = CharacterError.Parse(body);
        Assert.Equal(CharacterError.Code.Delete, parsed.AsCode);
    }

    [Fact]
    public void Parse_WrongOpcode_Throws()
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEADBEEFu);

        Assert.Throws<FormatException>(() => CharacterError.Parse(bytes));
    }

    [Fact]
    public void Parse_Truncated_Throws()
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, CharacterError.Opcode);

        Assert.Throws<FormatException>(() => CharacterError.Parse(bytes));
    }

    [Fact]
    public void Parse_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(() => CharacterError.Parse([]));
    }
}
