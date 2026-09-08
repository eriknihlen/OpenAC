using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CharGenVerificationResponseTests
{
    [Fact]
    public void Parse_Ok_PopulatesIdentityPayload()
    {
        var w = AceWireWriter.GameMessage(CharGenVerificationResponse.ResponseOpcode)
            .Write((uint)CharGenVerificationResponse.Code.Ok)
            .WriteGuid(0x5000000Bu)
            .WriteString16L("+NewChar")
            .Write(0u);

        CharGenVerificationResponse.Parsed parsed =
            CharGenVerificationResponse.Parse(w.ToArray());

        Assert.Equal(1u, parsed.RawCode);
        Assert.Equal(CharGenVerificationResponse.Code.Ok, parsed.AsCode);
        Assert.True(parsed.IsOk);
        Assert.Equal(0x5000000Bu, parsed.Guid);
        Assert.Equal("+NewChar", parsed.Name);
        Assert.Equal(0u, parsed.SecondsGreyedOut);
    }

    [Theory]
    [InlineData(0u, CharGenVerificationResponse.Code.Undef)]
    [InlineData(2u, CharGenVerificationResponse.Code.Pending)]
    [InlineData(3u, CharGenVerificationResponse.Code.NameInUse)]
    [InlineData(4u, CharGenVerificationResponse.Code.NameBanned)]
    [InlineData(5u, CharGenVerificationResponse.Code.Corrupt)]
    [InlineData(6u, CharGenVerificationResponse.Code.DatabaseDown)]
    [InlineData(7u, CharGenVerificationResponse.Code.AdminPrivilegeDenied)]
    public void Parse_EveryNonOkCode_IsFlagOnlyWithNullTrailingFields(
        uint rawCode,
        CharGenVerificationResponse.Code expectedCode)
    {
        var w = AceWireWriter.GameMessage(CharGenVerificationResponse.ResponseOpcode)
            .Write(rawCode);

        CharGenVerificationResponse.Parsed parsed =
            CharGenVerificationResponse.Parse(w.ToArray());

        Assert.Equal(rawCode, parsed.RawCode);
        Assert.Equal(expectedCode, parsed.AsCode);
        Assert.False(parsed.IsOk);
        Assert.Null(parsed.Guid);
        Assert.Null(parsed.Name);
        Assert.Null(parsed.SecondsGreyedOut);
    }

    [Fact]
    public void Parse_UnknownCode_NeverThrowsOnTheCast()
    {
        var w = AceWireWriter.GameMessage(CharGenVerificationResponse.ResponseOpcode)
            .Write(99u);

        CharGenVerificationResponse.Parsed parsed =
            CharGenVerificationResponse.Parse(w.ToArray());

        Assert.Equal(99u, parsed.RawCode);
        Assert.Equal((CharGenVerificationResponse.Code)99u, parsed.AsCode);
        Assert.False(parsed.IsOk);
    }

    [Fact]
    public void Parse_WrongOpcode_Throws()
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEADBEEFu);

        Assert.Throws<FormatException>(() => CharGenVerificationResponse.Parse(bytes));
    }

    [Fact]
    public void Parse_TruncatedAfterCode_Throws()
    {
        var w = AceWireWriter.GameMessage(CharGenVerificationResponse.ResponseOpcode)
            .Write((uint)CharGenVerificationResponse.Code.Ok);

        Assert.Throws<FormatException>(() => CharGenVerificationResponse.Parse(w.ToArray()));
    }

    [Fact]
    public void Parse_TruncatedBeforeCode_Throws()
    {
        var w = AceWireWriter.GameMessage(CharGenVerificationResponse.ResponseOpcode);

        Assert.Throws<FormatException>(() => CharGenVerificationResponse.Parse(w.ToArray()));
    }

    [Fact]
    public void ResponseOpcode_MatchesCharacterRestoresResponseOpcode()
    {
        Assert.Equal(CharacterRestore.ResponseOpcode, CharGenVerificationResponse.ResponseOpcode);
    }
}
