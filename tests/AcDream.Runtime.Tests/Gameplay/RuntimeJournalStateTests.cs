using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Journal;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeJournalStateTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static RuntimeJournalState WithPages(params string[] labels)
    {
        var state = new RuntimeJournalState();
        state.Load(labels.Select(l => new JournalPage(Label: l)).ToArray());
        return state;
    }

    [Fact]
    public void AFreshJournalHasNoPagesAndNoCurrentPage()
    {
        using var state = new RuntimeJournalState();

        RuntimeJournalSnapshot snapshot = state.View.Snapshot;

        Assert.Equal(0, snapshot.PageCount);
        Assert.Equal(0, snapshot.CurrentPage);
        Assert.Equal(JournalPage.Empty, state.View.Current);
    }

    [Fact]
    public void LoadingOpensOnTheFirstPageAndIsNotAnEdit()
    {
        using RuntimeJournalState state = WithPages("a", "b");

        Assert.Equal(1, state.View.Snapshot.CurrentPage);
        Assert.Equal("a", state.View.Current.Label);
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void NewPageAppendsAndMovesToIt()
    {
        using RuntimeJournalState state = WithPages("a");

        state.NewPage();

        Assert.Equal(2, state.View.Snapshot.PageCount);
        Assert.Equal(2, state.View.Snapshot.CurrentPage);
        Assert.Equal(JournalPage.Empty, state.View.Current);
        Assert.True(state.IsDirty);
    }

    [Fact]
    public void EditsLandOnTheCurrentPageOnly()
    {
        using RuntimeJournalState state = WithPages("a", "b");
        state.GotoPage(2);

        state.UpdateCurrent("label", "title", "notes");

        Assert.Equal("a", state.View.Pages[0].Label);
        Assert.Equal("label", state.View.Pages[1].Label);
        Assert.Equal("title", state.View.Pages[1].Title);
    }

    [Fact]
    public void AnEditThatChangesNothingDoesNotDirtyTheJournal()
    {
        using RuntimeJournalState state = WithPages("a");
        state.UpdateCurrent("a", string.Empty, string.Empty);

        Assert.False(state.IsDirty);
    }

    [Fact]
    public void EditsAreClippedToTheAuthoredMaximums()
    {
        using RuntimeJournalState state = WithPages("a");

        state.UpdateCurrent(new string('x', 100), new string('y', 100), "notes");

        Assert.Equal(JournalPage.MaxLabelLength, state.View.Current.Label.Length);
        Assert.Equal(JournalPage.MaxTitleLength, state.View.Current.Title.Length);
    }

    [Fact]
    public void DeletingTheLastRemainingPageEmptiesTheJournal()
    {
        // Inventing a replacement blank page would make the journal impossible
        // to empty.
        using RuntimeJournalState state = WithPages("only");

        Assert.True(state.DeletePage(1));

        Assert.Equal(0, state.View.Snapshot.PageCount);
        Assert.Equal(0, state.View.Snapshot.CurrentPage);
    }

    [Fact]
    public void DeletingPastTheEndPullsTheCurrentPageBack()
    {
        using RuntimeJournalState state = WithPages("a", "b", "c");
        state.GotoPage(3);

        state.DeletePage(3);

        Assert.Equal(2, state.View.Snapshot.CurrentPage);
        Assert.Equal("b", state.View.Current.Label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void AnOutOfRangePageIsRefusedRatherThanClamped(int page)
    {
        using RuntimeJournalState state = WithPages("a", "b", "c");

        Assert.False(state.GotoPage(page));
        Assert.False(state.DeletePage(page));
        Assert.Equal(1, state.View.Snapshot.CurrentPage);
    }

    [Fact]
    public void IsLastPageTracksTheCurrentPage()
    {
        using RuntimeJournalState state = WithPages("a", "b");

        Assert.False(state.IsLastPage);
        state.GotoPage(2);
        Assert.True(state.IsLastPage);
    }

    [Fact]
    public void RecordingALocationOfZeroStillCountsAsRecorded()
    {
        using RuntimeJournalState state = WithPages("a");

        state.RecordLocation(0f, 0f);

        Assert.True(state.View.Current.HasLocation);
    }

    // ── the timer ───────────────────────────────────────────────────────

    [Fact]
    public void StartingATimerNeedsADurationToCountDown()
    {
        using RuntimeJournalState state = WithPages("a");

        Assert.False(state.StartTimer(Now));

        state.SetTimer(0, 0, 5);
        Assert.True(state.StartTimer(Now));
    }

    [Fact]
    public void TheCountdownRunsAgainstTheClock()
    {
        using RuntimeJournalState state = WithPages("a");
        state.SetTimer(0, 1, 0);
        state.StartTimer(Now);

        Assert.Equal(3600d, state.View.RemainingTimerSeconds(Now));
        Assert.Equal(3540d, state.View.RemainingTimerSeconds(Now.AddMinutes(1)));
    }

    [Fact]
    public void AnExpiredCountdownStopsAtZeroRatherThanGoingNegative()
    {
        using RuntimeJournalState state = WithPages("a");
        state.SetTimer(0, 0, 1);
        state.StartTimer(Now);

        Assert.Equal(0d, state.View.RemainingTimerSeconds(Now.AddHours(1)));
    }

    [Fact]
    public void LeavingThePageStopsItsCountdown()
    {
        using RuntimeJournalState state = WithPages("a", "b");
        state.SetTimer(0, 1, 0);
        state.StartTimer(Now);

        state.GotoPage(2);

        Assert.Equal(0d, state.View.RemainingTimerSeconds(Now));
    }

    [Fact]
    public void TheSaveSnapshotFoldsInTheLiveCountdown()
    {
        // The page record holds the value at START; what belongs in the file
        // is what is left NOW, or a reload would resurrect the full duration.
        using RuntimeJournalState state = WithPages("a");
        state.SetTimer(0, 1, 0);
        state.StartTimer(Now);

        IReadOnlyList<JournalPage> saved = state.CaptureForSave(Now.AddMinutes(30));

        Assert.Equal(1800d, Assert.Single(saved).RunningTimerSeconds);
    }

    [Fact]
    public void MarkSavedClearsTheDirtyFlag()
    {
        using RuntimeJournalState state = WithPages("a");
        state.NewPage();
        Assert.True(state.IsDirty);

        state.MarkSaved();

        Assert.False(state.IsDirty);
    }

    // ── lifetime ────────────────────────────────────────────────────────

    [Fact]
    public void ResetSessionClearsBecauseTheJournalIsPerCharacter()
    {
        using RuntimeJournalState state = WithPages("a", "b");

        state.ResetSession();

        Assert.Equal(0, state.View.Snapshot.PageCount);
        Assert.Equal(0, state.View.Snapshot.CurrentPage);
    }

    [Fact]
    public void EveryMutationAdvancesTheRevision()
    {
        using RuntimeJournalState state = WithPages("a");
        long start = state.View.Snapshot.Revision;

        state.NewPage();
        long afterNew = state.View.Snapshot.Revision;
        state.UpdateCurrent("x", "y", "z");

        Assert.True(afterNew > start);
        Assert.True(state.View.Snapshot.Revision > afterNew);
    }

    [Fact]
    public void OwnershipConvergesOnlyAfterDisposal()
    {
        RuntimeJournalState state = WithPages("a");
        Assert.False(state.CaptureOwnership().IsConverged);

        state.Dispose();

        Assert.True(state.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void MutationsAfterDisposalAreIgnoredRatherThanThrowing()
    {
        RuntimeJournalState state = WithPages("a");
        state.Dispose();

        state.NewPage();
        state.UpdateCurrent("x", "y", "z");
        state.Load([new JournalPage(Label: "b")]);

        Assert.True(state.CaptureOwnership().IsConverged);
    }
}
