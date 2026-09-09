using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Journal;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class JournalNotesPageControllerTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private const uint PreviousButtonId = 0x10000565u;
    private const uint NextButtonId = 0x10000566u;
    private const uint NewButtonId = 0x10000567u;
    private const uint LabelFieldId = 0x10000569u;
    private const uint TitleFieldId = 0x1000056Bu;
    private const uint NotesFieldId = 0x1000056Du;
    private const uint FirstButtonId = 0x1000056Fu;
    private const uint PageNumberId = 0x10000570u;
    private const uint LastButtonId = 0x10000571u;
    private const uint LocationFieldId = 0x10000573u;
    private const uint RecordButtonId = 0x10000574u;
    private const uint TimerDaysFieldId = 0x10000576u;
    private const uint TimerHoursFieldId = 0x10000578u;
    private const uint TimerMinutesFieldId = 0x1000057Au;
    private const uint TimerDaysLabelId = 0x10000577u;
    private const uint TimerHoursLabelId = 0x10000579u;
    private const uint TimerMinutesLabelId = 0x1000057Bu;
    private const uint RunningTimerTextId = 0x1000057Cu;
    private const uint StartButtonId = 0x1000057Du;

    private const uint OutdoorCell = 0xA9B4001Fu;

    private static UiField Field(uint id) => new() { ElementId = id, DatElementId = id, Width = 100f, Height = 18f };

    private static UiText Text(uint id) => new() { DatElementId = id, Width = 120f, Height = 18f };

    private static UiButton Button(uint id) => new(
        new ElementInfo { Id = id, Type = 1, Width = 60, Height = 20 },
        static _ => (0u, 0, 0))
    {
        DatElementId = id,
    };

    private static UiElement BuildPage()
    {
        var page = new UiPanel { Width = 300f, Height = 500f };
        foreach (uint id in new[]
        {
            LabelFieldId, TitleFieldId, NotesFieldId, LocationFieldId,
            TimerDaysFieldId, TimerHoursFieldId, TimerMinutesFieldId,
        })
        {
            page.AddChild(Field(id));
        }

        page.AddChild(Text(PageNumberId));
        page.AddChild(Text(RunningTimerTextId));
        page.AddChild(Text(TimerDaysLabelId));
        page.AddChild(Text(TimerHoursLabelId));
        page.AddChild(Text(TimerMinutesLabelId));
        foreach (uint id in new[]
        {
            PreviousButtonId, NextButtonId, NewButtonId,
            FirstButtonId, LastButtonId, RecordButtonId, StartButtonId,
        })
        {
            page.AddChild(Button(id));
        }

        return page;
    }

    private static (JournalNotesPageController Controller, RuntimeJournalState State,
        UiElement Page) Bind(Func<DateTime>? now = null, params JournalPage[] pages)
    {
        var state = new RuntimeJournalState();
        state.Load(pages);
        UiElement page = BuildPage();

        var controller = new JournalNotesPageController(
            page,
            new JournalNotesPageController.Bindings(
                Journal: state.View,
                Commands: state,
                PlayerCell: () => OutdoorCell,
                Now: now ?? (() => Now)));

        return (controller, state, page);
    }

    private static void Click(UiElement page, uint id)
        => (UiElement.FindDescendant(page, id) as UiButton)!.OnClick!();

    private static UiField FieldOf(UiElement page, uint id)
        => (UiField)UiElement.FindDescendant(page, id)!;

    private static string TextOf(UiElement page, uint id)
    {
        var text = UiElement.FindDescendant(page, id) as UiText;
        return text?.LinesProvider?.Invoke().FirstOrDefault().Text ?? string.Empty;
    }

    // ── Record ──────────────────────────────────────────────────────────

    [Fact]
    public void RecordPutsTheLocationOnSCREENAndNotJustInTheModel()
    {
        // The readout is authored EDITABLE, so it builds as a UiField. Binding
        // it as UiText yielded null and threw the write away: the value
        // reached the model and the file, and the player saw nothing.
        var (_, state, page) = Bind(pages: new JournalPage());

        Click(page, RecordButtonId);

        Assert.True(state.View.Current.HasLocation);
        Assert.NotEqual(string.Empty, FieldOf(page, LocationFieldId).Text);
        state.Dispose();
    }

    [Fact]
    public void RecordOnAnEmptyJournalDoesNothing()
    {
        var (_, state, page) = Bind();

        Click(page, RecordButtonId);

        Assert.Equal(string.Empty, FieldOf(page, LocationFieldId).Text);
        state.Dispose();
    }

    // ── the timer ───────────────────────────────────────────────────────

    [Fact]
    public void StartingATimerSwapsTheFieldsForTheRunningReadout()
    {
        // The readout is authored at the SAME x as the three number boxes, so
        // both visible at once overlaps illegibly.
        var (_, state, page) = Bind(pages: new JournalPage());
        FieldOf(page, TimerHoursFieldId).SetText("1");

        Click(page, StartButtonId);

        Assert.False(FieldOf(page, TimerDaysFieldId).Visible);
        Assert.False(FieldOf(page, TimerHoursFieldId).Visible);
        Assert.False(FieldOf(page, TimerMinutesFieldId).Visible);
        Assert.True(UiElement.FindDescendant(page, RunningTimerTextId)!.Visible);
        Assert.Equal("1h 0s", TextOf(page, RunningTimerTextId));

        // The unit labels go with their boxes. Leaving them drew "d h m"
        // through the readout: "1d 2h 51sh   m".
        Assert.False(UiElement.FindDescendant(page, TimerDaysLabelId)!.Visible);
        Assert.False(UiElement.FindDescendant(page, TimerHoursLabelId)!.Visible);
        Assert.False(UiElement.FindDescendant(page, TimerMinutesLabelId)!.Visible);
        state.Dispose();
    }

    [Fact]
    public void StartWithNoDurationEnteredDoesNothing()
    {
        var (_, state, page) = Bind(pages: new JournalPage());

        Click(page, StartButtonId);

        Assert.True(FieldOf(page, TimerDaysFieldId).Visible);
        Assert.Equal(string.Empty, TextOf(page, RunningTimerTextId));
        state.Dispose();
    }

    [Fact]
    public void TheRunningTimerCountsDownOnTick()
    {
        DateTime now = Now;
        var (controller, state, page) = Bind(now: () => now, pages: new JournalPage());
        FieldOf(page, TimerHoursFieldId).SetText("1");
        Click(page, StartButtonId);
        Assert.Equal("1h 0s", TextOf(page, RunningTimerTextId));

        now = Now.AddMinutes(30);
        controller.Tick();

        Assert.Equal("30m 0s", TextOf(page, RunningTimerTextId));
        state.Dispose();
    }

    [Fact]
    public void PressingStartAgainStopsTheCountdownAndRestoresTheFields()
    {
        var (_, state, page) = Bind(pages: new JournalPage());
        FieldOf(page, TimerHoursFieldId).SetText("1");
        Click(page, StartButtonId);

        Click(page, StartButtonId);

        Assert.True(FieldOf(page, TimerDaysFieldId).Visible);
        Assert.True(UiElement.FindDescendant(page, TimerDaysLabelId)!.Visible);
        Assert.Equal(string.Empty, TextOf(page, RunningTimerTextId));
        state.Dispose();
    }

    // ── navigation ──────────────────────────────────────────────────────

    [Fact]
    public void NewAppendsAPageAndShowsIt()
    {
        var (_, state, page) = Bind();

        Click(page, NewButtonId);

        Assert.Single(state.View.Pages);
        Assert.Equal("~ 1 ~", TextOf(page, PageNumberId));
        state.Dispose();
    }

    [Fact]
    public void EveryNavigationCommitsTheCurrentPageFirst()
    {
        var (_, state, page) = Bind(pages: new[] { new JournalPage(), new JournalPage() });
        FieldOf(page, TitleFieldId).SetText("typed but not committed");

        Click(page, NextButtonId);

        Assert.Equal("typed but not committed", state.View.Pages[0].Title);
        state.Dispose();
    }

    [Fact]
    public void FirstAndLastJumpToTheEnds()
    {
        var (_, state, page) = Bind(
            pages: new[] { new JournalPage(), new JournalPage(), new JournalPage() });

        Click(page, LastButtonId);
        Assert.Equal("~ 3 ~", TextOf(page, PageNumberId));

        Click(page, FirstButtonId);
        Assert.Equal("~ 1 ~", TextOf(page, PageNumberId));
        state.Dispose();
    }

    [Fact]
    public void PreviousAndNextStopAtTheEndsRatherThanWrapping()
    {
        var (_, state, page) = Bind(pages: new[] { new JournalPage(), new JournalPage() });

        Click(page, PreviousButtonId);          // already on page 1
        Assert.Equal("~ 1 ~", TextOf(page, PageNumberId));

        Click(page, NextButtonId);
        Click(page, NextButtonId);              // already on the last
        Assert.Equal("~ 2 ~", TextOf(page, PageNumberId));
        state.Dispose();
    }

    [Fact]
    public void AnEmptyJournalShowsNoPageNumber()
    {
        // "~ 0 ~" would name a page that does not exist.
        var (_, state, page) = Bind();

        Assert.Equal(string.Empty, TextOf(page, PageNumberId));
        state.Dispose();
    }

    [Fact]
    public void SwitchingPagesShowsThatPagesText()
    {
        var (_, state, page) = Bind(pages: new[]
        {
            new JournalPage(Title: "first", Notes: "one"),
            new JournalPage(Title: "second", Notes: "two"),
        });

        Click(page, NextButtonId);

        Assert.Equal("second", FieldOf(page, TitleFieldId).Text);
        Assert.Equal("two", FieldOf(page, NotesFieldId).Text);
        state.Dispose();
    }
}
