using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatTagMarkupTests
{
    private const string TellLine =
        @"<Tell:IIDString:1342177290:Dww>Dww<\Tell> tells you, ""hello""";

    [Fact]
    public void ARetailTellLineSplitsIntoATaggedNameAndPlainRemainder()
    {
        IReadOnlyList<ChatTextSpan> spans = ChatTagMarkup.Parse(TellLine);

        Assert.Equal(2, spans.Count);

        Assert.Equal("Dww", spans[0].Text);
        Assert.NotNull(spans[0].Tag);
        Assert.Equal("Tell", spans[0].Tag!.Value.Type);
        Assert.Equal("IIDString", spans[0].Tag!.Value.Format);

        Assert.Equal(@" tells you, ""hello""", spans[1].Text);
        Assert.Null(spans[1].Tag);
    }

    [Fact]
    public void TheTagCarriesTheSpeakersIdAndName()
    {
        ChatTextTag tag = Assert
            .Single(ChatTagMarkup.Parse(TellLine), s => s.Tag is not null)
            .Tag!.Value;

        Assert.True(tag.TryGetIidString(out uint objectId, out string name));
        Assert.Equal(1342177290u, objectId);
        Assert.Equal("Dww", name);
    }

    [Fact]
    public void AnyBracketedTextThatIsNotATagClosesTheOpenOne()
    {
        foreach (string closer in new[] { @"<\Tell>", "<Tell>", "<anything>" })
        {
            IReadOnlyList<ChatTextSpan> spans =
                ChatTagMarkup.Parse($"<Tell:IIDString:1:A>A{closer}B");

            Assert.Equal(2, spans.Count);
            Assert.Equal("A", spans[0].Text);
            Assert.NotNull(spans[0].Tag);
            Assert.Equal("B", spans[1].Text);
            Assert.Null(spans[1].Tag);
        }
    }

    [Fact]
    public void PlainTextIsOneUntaggedSpan()
    {
        ChatTextSpan span = Assert.Single(ChatTagMarkup.Parse("You say, \"hi\""));
        Assert.Equal("You say, \"hi\"", span.Text);
        Assert.Null(span.Tag);
    }

    [Fact]
    public void AnUnterminatedBracketIsOrdinaryText()
    {
        ChatTextSpan span = Assert.Single(ChatTagMarkup.Parse("is 3 < 4 really"));
        Assert.Equal("is 3 < 4 really", span.Text);
        Assert.Null(span.Tag);
    }

    [Fact]
    public void TheConcatenatedSpansAlwaysReproduceTheVisibleLine()
    {
        string visible = string.Concat(
            ChatTagMarkup.Parse(TellLine).Select(s => s.Text));

        Assert.Equal(@"Dww tells you, ""hello""", visible);
    }

    [Theory]
    [InlineData("<Tell:IIDString:notanumber:Dww>x", "a non-numeric id")]
    [InlineData("<Tell:IIDString:1>x", "no name")]
    [InlineData("<Tell:IIDString:>x", "empty payload")]
    [InlineData("<Tell:DID:1:x>x", "a different format")]
    public void AMalformedOrUnsupportedPayloadYieldsNoIidString(
        string markup, string why)
    {
        ChatTextSpan span = ChatTagMarkup.Parse(markup)[0];
        Assert.NotNull(span.Tag);
        Assert.False(span.Tag!.Value.TryGetIidString(out _, out _), why);
    }

    [Fact]
    public void ANameContainingAColonSurvivesIntact()
    {
        ChatTextTag tag = ChatTagMarkup.Parse("<Tell:IIDString:5:Odd:Name>x")[0].Tag!.Value;

        Assert.True(tag.TryGetIidString(out uint id, out string name));
        Assert.Equal(5u, id);
        Assert.Equal("Odd:Name", name);
    }
}
