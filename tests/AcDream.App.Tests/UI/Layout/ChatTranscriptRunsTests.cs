using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ChatTranscriptRunsTests
{
    private static readonly Vector4 LineColor = new(0.8f, 0.8f, 0.8f, 1f);

    private static readonly Vector4 TagGreen = new(0f, 178f / 255f, 0f, 1f);

    private static float Measure(string text) => text.Length * 10f;

    private static IReadOnlyList<ChatTextSpan> TellSpans(string name, string rest)
        => new[]
        {
            new ChatTextSpan(name, new ChatTextTag("Tell", "IIDString", $"1342177290:{name}")),
            new ChatTextSpan(rest, null),
        };

    [Fact]
    public void TheNameTakesTheTagColourAndTheRestTakesTheLineColour()
    {
        IReadOnlyList<UiText.TextRun>? runs = ChatTranscriptRenderer.RunsForFragment(
            TellSpans("Dww", " tells you"),
            fragmentStart: 0,
            fragmentLength: "Dww tells you".Length,
            LineColor,
            TagGreen);

        Assert.NotNull(runs);
        Assert.Equal(2, runs!.Count);
        Assert.Equal(("Dww", TagGreen), (runs[0].Text, runs[0].Color));
        Assert.Equal((" tells you", LineColor), (runs[1].Text, runs[1].Color));
    }

    [Fact]
    public void AFragmentWithNoTagInItGetsNoRunsAtAll()
    {
        IReadOnlyList<ChatTextSpan> spans = TellSpans("Dww", " tells you, hello there");

        Assert.Null(ChatTranscriptRenderer.RunsForFragment(
            spans,
            fragmentStart: 10,          // well past the name
            fragmentLength: 5,
            LineColor,
            TagGreen));
    }

    [Fact]
    public void ATagStraddlingAWrapBreakIsSplitAcrossBothFragments()
    {
        IReadOnlyList<ChatTextSpan> spans = TellSpans("Bartholomew", " tells you");

        IReadOnlyList<UiText.TextRun>? first = ChatTranscriptRenderer.RunsForFragment(
            spans, fragmentStart: 0, fragmentLength: 5, LineColor, TagGreen);
        IReadOnlyList<UiText.TextRun>? second = ChatTranscriptRenderer.RunsForFragment(
            spans, fragmentStart: 5, fragmentLength: 6, LineColor, TagGreen);

        Assert.Equal("Barth", Assert.Single(first!).Text);
        Assert.Equal(TagGreen, first![0].Color);
        Assert.Equal("olomew", Assert.Single(second!).Text);
        Assert.Equal(TagGreen, second![0].Color);
    }

    [Fact]
    public void RunsAlwaysReproduceTheFragmentTheyCover()
    {
        IReadOnlyList<ChatTextSpan> spans = TellSpans("Dww", " tells you, \"hi\"");
        string line = string.Concat(spans.Select(s => s.Text));

        for (int start = 0; start < line.Length; start++)
        {
            for (int len = 1; len <= line.Length - start; len++)
            {
                IReadOnlyList<UiText.TextRun>? runs =
                    ChatTranscriptRenderer.RunsForFragment(
                        spans, start, len, LineColor, TagGreen);
                if (runs is null)
                    continue;

                string fragment = line.Substring(start, len);
                Assert.True(
                    UiText.RunsMatchLine(runs, fragment),
                    $"runs disagree with fragment [{start}..{start + len})");
            }
        }
    }

    [Fact]
    public void BuildLines_EmitsOneRunEntryPerWrappedFragment()
    {
        var detailed = new List<FormattedLine>
        {
            new("Dww tells you, hello there friend", ChatKind.Tell, null, 0x03u,
                TellSpans("Dww", " tells you, hello there friend")),
            new("Welcome.", ChatKind.System, null, 0x05u),
        };
        var runs = new List<IReadOnlyList<UiText.TextRun>?>();

        List<UiText.Line> lines = ChatTranscriptRenderer.BuildLines(
            detailed,
            maxW: 150f,               // forces the first entry to wrap
            Measure,
            accept: null,
            defaultColor: LineColor,
            tagColor: TagGreen,
            runsPerLine: runs);

        Assert.True(lines.Count > 2, "the first line should have wrapped");
        Assert.Equal(lines.Count, runs.Count);

        // The very first fragment holds the name, so it is the one with runs.
        Assert.NotNull(runs[0]);
        Assert.Equal(TagGreen, runs[0]![0].Color);

        // The untagged system line never gets runs.
        Assert.Null(runs[^1]);
    }

    [Fact]
    public void BuildLines_WithoutATagColourFallsBackToTheLineColour()
    {
        var detailed = new List<FormattedLine>
        {
            new("Dww tells you", ChatKind.Tell, null, 0x03u,
                TellSpans("Dww", " tells you")),
        };
        var runs = new List<IReadOnlyList<UiText.TextRun>?>();

        ChatTranscriptRenderer.BuildLines(
            detailed, maxW: 1000f, Measure, accept: null,
            defaultColor: LineColor, tagColor: null, runsPerLine: runs);

        IReadOnlyList<UiText.TextRun> line = Assert.Single(runs)!;
        Assert.All(line, run => Assert.Equal(line[0].Color, run.Color));
    }


    private static FormattedLine Plain(string text, uint logTextType = 0x02u)
        => new(text, ChatKind.LocalSpeech, null, logTextType);

    [Fact]
    public void AShortHistoryIsKeptWhole()
    {
        var detailed = new List<FormattedLine> { Plain("one"), Plain("two") };

        Assert.Equal(0, ChatTranscriptRenderer.FirstLineWithinBudget(detailed, accept: null));
    }

    [Fact]
    public void TheOldestLinesDropOnceTheBudgetIsExceeded()
    {
        var detailed = new List<FormattedLine>
        {
            Plain(new string('a', 10)),
            Plain(new string('b', 10)),
            Plain(new string('c', 10)),
            Plain(new string('d', 10)),
        };

        Assert.Equal(
            2,
            ChatTranscriptRenderer.FirstLineWithinBudget(detailed, accept: null, budget: 25));
    }

    [Fact]
    public void FilteredOutLinesDoNotConsumeBudget()
    {
        // A line this window filters out is not in its buffer at all, so it
        // must not push older lines off the top — otherwise turning a filter
        // OFF would silently shorten the visible history.
        var detailed = new List<FormattedLine>
        {
            Plain(new string('a', 10), logTextType: 0x02u),
            Plain(new string('x', 100), logTextType: 0x06u),   // filtered
            Plain(new string('b', 10), logTextType: 0x02u),
        };

        Assert.Equal(
            0,
            ChatTranscriptRenderer.FirstLineWithinBudget(
                detailed, accept: type => type == 0x02u, budget: 25));
    }

    [Fact]
    public void BuildLines_RendersOnlyTheLinesInsideTheBudget()
    {
        var detailed = new List<FormattedLine>();
        for (int i = 0; i < 40; i++)
            detailed.Add(Plain(new string((char)('a' + (i % 26)), 500)));

        List<UiText.Line> lines = ChatTranscriptRenderer.BuildLines(
            detailed, maxW: 100000f, Measure, accept: null, defaultColor: LineColor);

        Assert.True(lines.Count < detailed.Count, "the oldest lines should have dropped");
        Assert.Equal(detailed[^1].Text, lines[^1].Text);
    }

    [Fact]
    public void OversizedMultilineServerMessageKeepsItsNewestCompleteLines()
    {
        string response = string.Join(
            '\n',
            Enumerable.Range(0, 1_500).Select(i => $"@command-{i:D4}"));
        var detailed = new List<FormattedLine> { Plain(response) };

        List<UiText.Line> lines = ChatTranscriptRenderer.BuildLines(
            detailed,
            maxW: 100_000f,
            Measure,
            accept: null,
            defaultColor: LineColor);

        Assert.NotEmpty(lines);
        Assert.Equal("@command-1499", lines[^1].Text);
        Assert.DoesNotContain(lines, line => line.Text == "@command-0000");
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line.Text)));
    }

    [Fact]
    public void MultilineServerMessageWithinBudgetRendersEveryAuthoredLine()
    {
        var detailed = new List<FormattedLine>
        {
            Plain("@acecommands\n@help\n@teleport"),
        };

        List<UiText.Line> lines = ChatTranscriptRenderer.BuildLines(
            detailed,
            maxW: 100_000f,
            Measure,
            accept: null,
            defaultColor: LineColor);

        Assert.Equal(
            new[] { "@acecommands", "@help", "@teleport" },
            lines.Select(line => line.Text));
    }

    [Fact]
    public void TheBudgetIsRetailsOwnNumber()
        => Assert.Equal(0x2710, ChatTranscriptRenderer.MaxTranscriptCharacters);


    [Fact]
    public void TheTimestampIsGreyWhateverColourTheMessageIs()
    {
        var spans = new[]
        {
            new ChatTextSpan("13:05:09 ", null, ChatSpanRole.Timestamp),
            new ChatTextSpan("Dww says, \"hi\"", null),
        };

        IReadOnlyList<UiText.TextRun>? runs = ChatTranscriptRenderer.RunsForFragment(
            spans,
            fragmentStart: 0,
            fragmentLength: spans[0].Text.Length + spans[1].Text.Length,
            LineColor,
            TagGreen);

        Assert.NotNull(runs);
        Assert.Equal(2, runs!.Count);
        Assert.Equal("13:05:09 ", runs[0].Text);
        Assert.NotEqual(LineColor, runs[0].Color);
        Assert.Equal(LineColor, runs[1].Color);      // the body still is
    }

    [Fact]
    public void ATimestampedLineGetsRunsEvenWithNoTaggedSender()
    {
        var spans = new[]
        {
            new ChatTextSpan("13:05:09 ", null, ChatSpanRole.Timestamp),
            new ChatTextSpan("Welcome.", null),
        };

        Assert.NotNull(ChatTranscriptRenderer.RunsForFragment(
            spans, 0, spans[0].Text.Length + spans[1].Text.Length,
            LineColor, TagGreen));
    }

    [Fact]
    public void ATimestampAndATaggedNameKeepSeparateColours()
    {
        var spans = new[]
        {
            new ChatTextSpan("13:05:09 ", null, ChatSpanRole.Timestamp),
            new ChatTextSpan("Dww", new ChatTextTag("Tell", "IIDString", "1:Dww")),
            new ChatTextSpan(" tells you", null),
        };

        IReadOnlyList<UiText.TextRun> runs = ChatTranscriptRenderer.RunsForFragment(
            spans, 0, 9 + 3 + 10, LineColor, TagGreen)!;

        Assert.Equal(3, runs.Count);
        Assert.Equal(TagGreen, runs[1].Color);
        Assert.NotEqual(TagGreen, runs[0].Color);
        Assert.NotEqual(runs[0].Color, runs[2].Color);
    }
}
