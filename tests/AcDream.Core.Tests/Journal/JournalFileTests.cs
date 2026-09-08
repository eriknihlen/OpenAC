using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Journal;

namespace AcDream.Core.Tests.Journal;

public sealed class JournalFileTests
{
    [Fact]
    public void APageRoundTripsThroughTheFile()
    {
        var page = new JournalPage(
            Label: "Aerlinthe",
            Title: "Recall ring",
            Notes: "Talk to the archmage first.",
            TimerDays: 1,
            TimerHours: 2,
            TimerMinutes: 3,
            LocationX: 12.5f,
            LocationY: -8.25f,
            HasLocation: true,
            RunningTimerSeconds: 90d);

        JournalReadResult result = JournalFile.Read(JournalFile.Write([page]));

        Assert.Null(result.Error);
        Assert.Equal(page, Assert.Single(result.Pages));
    }

    [Fact]
    public void SeveralPagesKeepTheirFileOrder()
    {
        // <PNUM> is written but page order IS file order — a reader that
        // trusted the number would reshuffle a hand-edited file.
        List<JournalPage> pages =
        [
            new(Label: "one"), new(Label: "two"), new(Label: "three"),
        ];

        JournalReadResult result = JournalFile.Read(JournalFile.Write(pages));

        Assert.Equal(
            new[] { "one", "two", "three" },
            result.Pages.Select(p => p.Label).ToArray());
    }

    [Fact]
    public void AFileThatDoesNotOpenWithAPageMarkerIsRefused()
    {
        JournalReadResult result = JournalFile.Read("<TITL> orphaned\n<NEWP>\n");

        Assert.Equal(JournalFile.MalformedFileMessage, result.Error);
        Assert.Empty(result.Pages);
    }

    [Fact]
    public void AnEmptyFileIsACharacterWhoHasWrittenNothing()
    {
        JournalReadResult result = JournalFile.Read(string.Empty);

        Assert.Null(result.Error);
        Assert.Empty(result.Pages);
    }

    [Fact]
    public void AnUnknownTagIsSkippedRatherThanRefused()
    {
        JournalReadResult result = JournalFile.Read(
            "<NEWP>\n<TITL> kept\n<ZZZZ> whatever\n");

        Assert.Null(result.Error);
        Assert.Equal("kept", Assert.Single(result.Pages).Title);
    }

    [Fact]
    public void TheWrittenFormIsRetailsTagOrder()
    {
        string text = JournalFile.Write([new JournalPage(Label: "L", Title: "T")]);

        string[] tags = text
            .Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length >= 6 ? line[..6] : line)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "<NEWP>", "<PNUM>", "<LABE>", "<TITL>", "<NOTE>",
                "<DAYS>", "<HOUR>", "<MINU>", "<TIME>",
            },
            tags);
    }

    [Fact]
    public void ARecordedLocationOfZeroIsStillARecordedLocation()
    {
        // (0, 0) is a real place. Writing the tags only when the numbers are
        // non-zero would lose the distinction between "recorded there" and
        // "never recorded".
        var page = new JournalPage(LocationX: 0f, LocationY: 0f, HasLocation: true);

        JournalPage read = Assert.Single(JournalFile.Read(JournalFile.Write([page])).Pages);

        Assert.True(read.HasLocation);
    }

    [Fact]
    public void APageWithNoLocationDoesNotGainOne()
    {
        JournalPage read = Assert.Single(
            JournalFile.Read(JournalFile.Write([new JournalPage(Title: "T")])).Pages);

        Assert.False(read.HasLocation);
    }

    [Fact]
    public void ANewlineInsideNotesCannotSplitThePage()
    {
        // The notes box is multi-line while the file is line-oriented. An
        // embedded newline would read back as a tagless line and silently
        // truncate the notes — or worse, land inside the next page.
        var page = new JournalPage(Notes: "first line\nsecond line", Title: "kept");

        JournalReadResult result = JournalFile.Read(JournalFile.Write([page]));

        JournalPage read = Assert.Single(result.Pages);
        Assert.Equal("first line second line", read.Notes);
        Assert.Equal("kept", read.Title);
    }

    [Fact]
    public void OverlongFieldsAreClippedToTheirAuthoredMaximums()
    {
        // The edit boxes enforce these; a hand-edited file does not.
        string text =
            "<NEWP>\n"
            + "<LABE> " + new string('a', 100) + "\n"
            + "<TITL> " + new string('b', 100) + "\n";

        JournalPage page = Assert.Single(JournalFile.Read(text).Pages);

        Assert.Equal(JournalPage.MaxLabelLength, page.Label.Length);
        Assert.Equal(JournalPage.MaxTitleLength, page.Title.Length);
    }

    [Theory]
    [InlineData("Frostfell", "Acdream", "Journal-Frostfell-Acdream.txt")]
    // A name is outside data and must not be able to redirect a write.
    [InlineData("a/b", "c\\d", "Journal-a_b-c_d.txt")]
    public void TheFileNameFollowsRetailsPattern(
        string server, string character, string expected)
        => Assert.Equal(expected, JournalFile.FileNameFor(server, character));

    [Fact]
    public void NumbersAreCultureInvariant()
    {
        string text = JournalFile.Write(
            [new JournalPage(LocationX: 1.5f, LocationY: 2.25f, HasLocation: true)]);

        Assert.Contains("<LOCX> 1.5", text);
        Assert.DoesNotContain(",", text);
    }
}
