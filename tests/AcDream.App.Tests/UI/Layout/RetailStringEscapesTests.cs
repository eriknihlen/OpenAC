using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailStringEscapesTests
{
    [Theory]
    [InlineData("line one\\nline two", "line one\nline two")]
    [InlineData("a\\tb", "a\tb")]
    [InlineData("a\\rb", "a\rb")]
    [InlineData("say \\qhi\\q", "say \"hi\"")]
    public void Unescape_DecodesTheFourCharacterEscapes(
        string raw, string expected)
        => Assert.Equal(expected, RetailStringEscapes.Unescape(raw));

    [Theory]
    [InlineData("\\[", "[")]
    [InlineData("\\]", "]")]
    [InlineData("\\!", "!")]
    [InlineData("\\{", "{")]
    [InlineData("\\}", "}")]
    [InlineData("\\#", "#")]
    [InlineData("\\\\", "\\")]
    [InlineData("\\|", "|")]
    [InlineData("\\^", "^")]
    [InlineData("\\$", "$")]
    public void Unescape_DecodesEveryMetalanguageSelfEscape(
        string raw, string expected)
        => Assert.Equal(expected, RetailStringEscapes.Unescape(raw));

    [Theory]
    [InlineData("\\z", "\\z")]
    [InlineData("C:\\path\\dir", "C:\\path\\dir")]
    [InlineData("ends with \\", "ends with \\")]
    [InlineData("\\N upper is not an escape", "\\N upper is not an escape")]
    public void Unescape_KeepsUnrecognizedPairsVerbatim(
        string raw, string expected)
        => Assert.Equal(expected, RetailStringEscapes.Unescape(raw));

    [Fact]
    public void Unescape_EscapedBackslashBeforeN_YieldsLiteralPair()
        => Assert.Equal("\\n", RetailStringEscapes.Unescape("\\\\n"));

    [Fact]
    public void Unescape_EmptyString_IsEmpty()
        => Assert.Equal(string.Empty, RetailStringEscapes.Unescape(string.Empty));

    /// <summary>No backslash → no allocation: the same instance returns.</summary>
    [Fact]
    public void Unescape_NoEscapes_ReturnsTheSameInstance()
    {
        const string plain = "Please Wait";
        Assert.Same(plain, RetailStringEscapes.Unescape(plain));
    }

    [Theory]
    [InlineData("line one\nline two", "line one\\nline two")]
    [InlineData("a\tb", "a\\tb")]
    [InlineData("a\rb", "a\\rb")]
    [InlineData("say \"hi\"", "say \\qhi\\q")]
    [InlineData("[x]", "\\[x\\]")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("plain", "plain")]
    public void Escape_IsTheStorageInverse(string plain, string expected)
        => Assert.Equal(expected, RetailStringEscapes.Escape(plain));

    [Theory]
    [InlineData("plain name")]
    [InlineData("Odd\\Name")]
    [InlineData("multi\nline")]
    [InlineData("tabs\tand \"quotes\"")]
    [InlineData("[]!{}#\\|^$")]
    [InlineData("")]
    public void UnescapeOfEscape_RoundTripsVerbatim(string value)
        => Assert.Equal(value, RetailStringEscapes.Unescape(
            RetailStringEscapes.Escape(value)));
}
