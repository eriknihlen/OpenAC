using AcDream.Runtime.Chat;

namespace AcDream.Runtime.Tests.Chat;

public sealed class ChatTextReplacementsTests
{
    private const string LastTeller = "Dww";

    [Theory]
    [InlineData("/r ")]
    [InlineData("/rp ")]
    [InlineData("/reply ")]
    [InlineData("@r ")]
    [InlineData("@rp ")]
    [InlineData("@reply ")]
    [InlineData("/R ")]
    public void AReplyAbbreviationExpandsToATellAtTheLastTeller(string typed)
        => Assert.Equal("@tell Dww, ", ChatTextReplacements.Expand(typed, LastTeller));

    [Fact]
    public void TheExpansionEndsInASpaceSoTypingContinuesCleanly()
    {
        string expanded = ChatTextReplacements.Expand("/r ", LastTeller)!;
        Assert.EndsWith(", ", expanded);
    }

    [Theory]
    [InlineData("/r")]
    [InlineData("/rep")]
    // A longer verb that merely STARTS with a reply verb.
    [InlineData("/roleplay ")]
    [InlineData("/rt ")]
    // Already has a message: the trigger is the abbreviation ALONE.
    [InlineData("/r hello ")]
    [InlineData("r ")]
    [InlineData("hello ")]
    [InlineData("")]
    public void AnythingElseIsLeftAlone(string typed)
        => Assert.Null(ChatTextReplacements.Expand(typed, LastTeller));

    [Fact]
    public void WithNobodyToReplyToNothingIsRewritten()
    {
        Assert.Null(ChatTextReplacements.Expand("/r ", lastTeller: null));
        Assert.Null(ChatTextReplacements.Expand("/r ", lastTeller: string.Empty));
    }

    [Fact]
    public void ANameWithSpacesIsPreserved()
    {
        Assert.Equal(
            "@tell Aunt Agatha, ",
            ChatTextReplacements.Expand("/r ", "Aunt Agatha"));
    }
}
