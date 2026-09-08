using System;
using AcDream.Core.Quests;
using AcDream.Core.Ui;

namespace AcDream.Core.Tests.Quests;

public sealed class ContractProgressTextTests
{
    private static readonly DateTime Arrival = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static ContractEntry Entry(
        string descriptionProgress = "", string questflagRepeatTime = "")
        => ContractEntry.Unknown with
        {
            DescriptionProgress = descriptionProgress,
            QuestflagRepeatTime = questflagRepeatTime,
        };

    // ── DeltaTimeToString ───────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(60, "1m 0s")]
    [InlineData(3600, "1h 0s")]              // minutes are OMITTED when zero
    [InlineData(3661, "1h 1m 1s")]
    [InlineData(86400, "1d 0s")]
    [InlineData(2592000, "1mo 0s")]          // a "month" is a flat 30 days
    [InlineData(2592000 + 86400 + 3600 + 61, "1mo 1d 1h 1m 1s")]
    public void DeltaTimeFormatsLargestUnitFirstAndAlwaysShowsSeconds(
        double seconds, string expected)
        => Assert.Equal(expected, RetailDurationText.Format(seconds));

    [Fact]
    public void DeltaTimeHasNoTrailingSpace()
    {
        string text = RetailDurationText.Format(30);

        Assert.Equal("30s", text);
        Assert.DoesNotContain("  ", ContractProgressText.Build(
            3u, 30d, Arrival, Entry(questflagRepeatTime: "flag"), Arrival));
    }

    [Fact]
    public void DeltaTimeTruncatesTowardZeroLikeRetailsFtol()
    {
        Assert.Equal("59s", RetailDurationText.Format(59.99));
    }

    // ── the stage arms ──────────────────────────────────────────────────

    [Fact]
    public void StageOneIsAvailable()
        => Assert.Equal("Available", ContractProgressText.Build(
            1u, 0d, Arrival, Entry(), Arrival));

    [Fact]
    public void StageTwoIsInProgress()
        => Assert.Equal("In Progress", ContractProgressText.Build(
            2u, 0d, Arrival, Entry(), Arrival));

    [Fact]
    public void StageThreeWithNoRepeatFlagIsDoneForGood()
    {
        Assert.Equal("Done", ContractProgressText.Build(
            3u, 0d, Arrival, Entry(questflagRepeatTime: ""), Arrival));
    }

    [Fact]
    public void StageThreeWithARepeatFlagAndNoTimerIsAvailableAgain()
    {
        Assert.Equal("Available", ContractProgressText.Build(
            3u, 0d, Arrival, Entry(questflagRepeatTime: "SomeQuestRepeat"), Arrival));
    }

    [Fact]
    public void StageThreeWithATimerStillRunningCountsDownToTheRepeat()
    {
        string text = ContractProgressText.Build(
            3u,
            timeWhenRepeats: 3661d,
            Arrival,
            Entry(questflagRepeatTime: "SomeQuestRepeat"),
            now: Arrival);

        Assert.Equal("Done (1h 1m 1s to Repeat)", text);
    }

    [Fact]
    public void TheCountdownIsAnchoredAtArrivalNotRecomputedFromTheServerValue()
    {
        // The server sends the remaining seconds ONCE and never sends the
        // instant it measured them from. Anchoring at arrival is what makes
        // the timer tick; without it the same number would be shown forever.
        string atArrival = ContractProgressText.Build(
            3u, 600d, Arrival, Entry(questflagRepeatTime: "f"), Arrival);
        string tenMinutesLater = ContractProgressText.Build(
            3u, 600d, Arrival, Entry(questflagRepeatTime: "f"),
            Arrival.AddMinutes(5));

        Assert.Equal("Done (10m 0s to Repeat)", atArrival);
        Assert.Equal("Done (5m 0s to Repeat)", tenMinutesLater);
    }

    [Fact]
    public void ATimerThatHasRunOutSinceArrivalReadsAsAvailable()
    {
        Assert.Equal("Available", ContractProgressText.Build(
            3u, 600d, Arrival, Entry(questflagRepeatTime: "f"),
            now: Arrival.AddHours(1)));
    }

    [Fact]
    public void TimeWhenDoneNeverReachesThisText()
    {
        string text = ContractProgressText.Build(
            3u, timeWhenRepeats: 0d, Arrival, Entry(questflagRepeatTime: ""), Arrival);

        Assert.Equal("Done", text);
    }


    [Theory]
    [InlineData(4u, "0/20 Tuskers")]
    [InlineData(9u, "5/20 Tuskers")]
    [InlineData(24u, "20/20 Tuskers")]
    public void StageFourAndAboveSubstitutesTheCountIntoTheAuthoredFormat(
        uint stage, string expected)
    {
        Assert.Equal(expected, ContractProgressText.Build(
            stage, 0d, Arrival, Entry(descriptionProgress: "%d/20 Tuskers"), Arrival));
    }

    [Fact]
    public void AProgressStageWithNoAuthoredFormatFallsBackToInProgress()
    {
        Assert.Equal("In Progress", ContractProgressText.Build(
            7u, 0d, Arrival, Entry(descriptionProgress: ""), Arrival));
    }

    [Fact]
    public void AFormatWithoutASpecifierIsShownVerbatim()
    {
        Assert.Equal("Gathering herbs", ContractProgressText.Build(
            6u, 0d, Arrival, Entry(descriptionProgress: "Gathering herbs"), Arrival));
    }

    [Fact]
    public void OnlyTheFirstSpecifierIsSubstitutedBecauseRetailPassesOneArgument()
    {
        Assert.Equal("3 of %d", ContractProgressText.Build(
            7u, 0d, Arrival, Entry(descriptionProgress: "%d of %d"), Arrival));
    }

    [Fact]
    public void AnUnknownStageProducesNothingRatherThanGuessing()
    {
        Assert.Equal(string.Empty, ContractProgressText.Build(
            0u, 0d, Arrival, Entry(), Arrival));
    }
}
