using System.Collections.Generic;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ChatTagClickTests
{
    private static ChatTextTag Tell(string name)
        => new("Tell", "IIDString", $"1342177290:{name}");

    private static IReadOnlyList<ChatTextSpan> TellSpans(string name, string rest)
        => new[] { new ChatTextSpan(name, Tell(name)), new ChatTextSpan(rest, null) };

    [Fact]
    public void TheNamesColumnsAreTaggedAndTheRestIsNot()
    {
        IReadOnlyList<(int Start, int Length, ChatTextTag Tag)>? ranges =
            ChatTranscriptRenderer.TaggedRangesForFragment(
                TellSpans("Dww", " tells you"),
                fragmentStart: 0,
                fragmentLength: "Dww tells you".Length);

        (int start, int length, ChatTextTag tag) = Assert.Single(ranges!);
        Assert.Equal(0, start);
        Assert.Equal(3, length);
        Assert.True(tag.TryGetIidString(out _, out string name));
        Assert.Equal("Dww", name);
    }

    [Fact]
    public void ColumnsAreRelativeToTheFragmentNotTheWholeLine()
    {
        IReadOnlyList<ChatTextSpan> spans = TellSpans("Dww", " tells you now");

        (int start, int length, _) = Assert.Single(
            ChatTranscriptRenderer.TaggedRangesForFragment(spans, 1, 5)!);

        Assert.Equal(0, start);
        Assert.Equal(2, length);   // "ww" — the part of the name inside the window
    }

    [Fact]
    public void AFragmentPastTheNameHasNoTaggedRanges()
    {
        Assert.Null(ChatTranscriptRenderer.TaggedRangesForFragment(
            TellSpans("Dww", " tells you"),
            fragmentStart: 6,
            fragmentLength: 4));
    }

    [Fact]
    public void AnUntaggedLineHasNoTaggedRanges()
    {
        Assert.Null(ChatTranscriptRenderer.TaggedRangesForFragment(
            new[] { new ChatTextSpan("Welcome.", null) },
            fragmentStart: 0,
            fragmentLength: 8));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(9, false)]
    public void OnlyColumnsInsideTheNameCount(int column, bool inside)
    {
        IReadOnlyList<(int Start, int Length, ChatTextTag Tag)> ranges =
            ChatTranscriptRenderer.TaggedRangesForFragment(
                TellSpans("Dww", " tells you"), 0, "Dww tells you".Length)!;

        (int start, int length, _) = ranges[0];
        Assert.Equal(inside, column >= start && column < start + length);
    }
}
