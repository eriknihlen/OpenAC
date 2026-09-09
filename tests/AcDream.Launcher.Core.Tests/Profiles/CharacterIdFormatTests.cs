using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Profiles;

public sealed class CharacterIdFormatTests
{
    [Fact]
    public void ToHexStringFormatsEightDigitUppercaseWithPrefix()
    {
        Assert.Equal("0x5000000A", CharacterIdFormat.ToHexString(0x5000000Au));
        Assert.Equal("0x00000001", CharacterIdFormat.ToHexString(1u));
    }

    [Theory]
    [InlineData("0x5000000A", 0x5000000Au)]
    [InlineData("0x5000000a", 0x5000000Au)]
    [InlineData("0X5000000A", 0x5000000Au)]
    public void TryParseAcceptsThe0xPrefixCaseInsensitively(string text, uint expected)
    {
        Assert.True(CharacterIdFormat.TryParse(text, out uint id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-hex")]
    [InlineData("5000000A")]
    [InlineData("12345678")]
    public void TryParseRejectsNullEmptyNonHexOrAnUnprefixedString(string? text)
    {
        // "5000000A"/"12345678" are all-hex-digit strings that would
        // parse fine as hex WITHOUT the "0x" prefix
        // requires the prefix precisely so a hand-typed decimal id (which
        // is ALSO syntactically valid hex) is never silently
        // misinterpreted as one.
        Assert.False(CharacterIdFormat.TryParse(text, out uint id));
        Assert.Equal(0u, id);
    }

    [Fact]
    public void RoundTripsThroughToHexStringAndTryParse()
    {
        const uint original = 0x5000000Au;
        string text = CharacterIdFormat.ToHexString(original);
        Assert.True(CharacterIdFormat.TryParse(text, out uint parsed));
        Assert.Equal(original, parsed);
    }
}
