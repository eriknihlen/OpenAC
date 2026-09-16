using AcDream.Core.Chat;
using Xunit;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatLanguageFilterTests
{
    [Theory]
    [InlineData("zork", "zork", true)]
    [InlineData("zork", "Zork", false)]
    [InlineData("zork", "zorks", false)]
    [InlineData("zork", "zor*", true)]
    [InlineData("zork", "*ork", true)]
    [InlineData("zork", "z*k", true)]
    [InlineData("zork", "*zork*", true)]
    [InlineData("zork", "*", true)]
    [InlineData("", "*", true)]
    [InlineData("", "", true)]
    [InlineData("zork", "", false)]
    [InlineData("zork", "gnome", false)]
    public void MatchesPattern_WildcardConsumesTheWholeWord(string word, string pattern, bool expected)
    {
        Assert.Equal(expected, ChatLanguageFilter.MatchesPattern(word, pattern));
    }

    [Fact]
    public void Censor_ReplacesOnlyMatchingWords()
    {
        string result = ChatLanguageFilter.Censor("a zork b", new[] { "zork" });

        Assert.Equal("a **** b", result);
    }

    [Fact]
    public void Censor_MultipleSpaces_PreservesSpacing()
    {
        string result = ChatLanguageFilter.Censor("a  zork  b", new[] { "zork" });

        Assert.Equal("a  ****  b", result);
    }

    [Theory]
    [InlineData("a\tzork\tb", "a\tzork\tb")]
    [InlineData("a\nzork\r\nb", "a\nzork\r\nb")]
    [InlineData("a\u00A0zork b", "a\u00A0zork b")]
    public void Censor_OnlyAsciiSpaceIsAWordBoundary(string line, string expected)
    {
        Assert.Equal(expected, ChatLanguageFilter.Censor(line, new[] { "zork" }));
    }

    [Fact]
    public void Censor_WildcardPatternSwallowsATabWhenNoSpaceSeparatesIt()
    {
        string result = ChatLanguageFilter.Censor("zork\tb", new[] { "zork*" });

        Assert.Equal("****", result);
    }

    [Fact]
    public void Censor_PunctuationStaysPartOfTheWord()
    {
        string result = ChatLanguageFilter.Censor("zork!", new[] { "zork" });

        Assert.Equal("zork!", result);
    }

    [Fact]
    public void Censor_WildcardPatternCoversPunctuation()
    {
        string result = ChatLanguageFilter.Censor("zork!", new[] { "zork*" });

        Assert.Equal("****", result);
    }

    [Fact]
    public void Censor_CaseInsensitive()
    {
        string result = ChatLanguageFilter.Censor("ZoRk", new[] { "zork" });

        Assert.Equal("****", result);
    }

    [Fact]
    public void Censor_NoPatterns_ReturnsSameText()
    {
        string result = ChatLanguageFilter.Censor("hello world", Array.Empty<string>());

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Censor_PatternsMissing_CensorsEveryWord()
    {
        string result = ChatLanguageFilter.Censor("hello world", null);

        Assert.Equal("**** ****", result);
    }

    [Fact]
    public void Censor_NoMatch_ReturnsSameText()
    {
        string result = ChatLanguageFilter.Censor("hello world", new[] { "zork" });

        Assert.Equal("hello world", result);
    }
}
