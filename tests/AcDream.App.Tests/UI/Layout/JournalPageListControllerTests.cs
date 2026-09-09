using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Journal;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class JournalPageListControllerTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private const uint ListId = 0x10000583u;
    private const uint SearchFieldId = 0x10000587u;
    private const uint DeleteButtonId = 0x10000585u;
    private const uint RowNumberId = 0x1000058Au;
    private const uint RowTitleId = 0x1000058Bu;


    [Fact]
    public void SearchMatchesLabelTitleOrNotes()
    {
        var page = new JournalPage(Label: "lab", Title: "tit", Notes: "not");

        Assert.True(JournalPageListController.PageContainsString(page, "lab"));
        Assert.True(JournalPageListController.PageContainsString(page, "tit"));
        Assert.True(JournalPageListController.PageContainsString(page, "not"));
        Assert.False(JournalPageListController.PageContainsString(page, "zzz"));
    }

    [Fact]
    public void SearchIsCaseSensitiveBecauseRetailUsesWcsstr()
    {
        // Making it insensitive would be friendlier and would be a divergence.
        var page = new JournalPage(Title: "Aerlinthe");

        Assert.True(JournalPageListController.PageContainsString(page, "Aer"));
        Assert.False(JournalPageListController.PageContainsString(page, "aer"));
    }

    [Fact]
    public void AnEmptySearchMatchesEverything()
    {
        Assert.True(JournalPageListController.PageContainsString(
            new JournalPage(), string.Empty));
    }

    // ── the list ────────────────────────────────────────────────────────

    private static UiText Text(uint id) => new()
    {
        DatElementId = id,
        Width = 90f,
        Height = 20f,
        DefaultColor = new System.Numerics.Vector4(0.8f, 0.8f, 0.8f, 1f),
    };

    private static UiElement? RowTemplate(uint layoutId, uint elementId)
    {
        if (layoutId != JournalPageListController.RowTemplateLayoutId
            || elementId != JournalPageListController.RowTemplateElementId)
        {
            return null;
        }

        // A UiDatElement, as the real Type-3 template resolves to.
        var row = new UiDatElement(
            new ElementInfo { Type = 3, Width = 270, Height = 20 },
            static _ => (0u, 0, 0));
        row.AddChild(Text(RowNumberId));
        row.AddChild(Text(RowTitleId));
        return row;
    }

    private static (UiElement Page, UiField Search) BuildPage()
    {
        var list = new UiTemplateListBox(
            new ElementInfo { Id = ListId, Type = 5, Width = 270, Height = 430 },
            static _ => (0u, 0, 0),
            [new UiTemplateListEntry(
                JournalPageListController.RowTemplateLayoutId,
                JournalPageListController.RowTemplateElementId)],
            scrollbarElementId: 0u)
        {
            DatElementId = ListId,
        };

        var search = new UiField { ElementId = SearchFieldId, Width = 118f, Height = 18f };
        search.DatElementId = SearchFieldId;

        var deleteButton = new UiButton(
            new ElementInfo { Id = DeleteButtonId, Type = 1, Width = 60, Height = 18 },
            static _ => (0u, 0, 0))
        {
            DatElementId = DeleteButtonId,
        };

        var page = new UiPanel { Width = 300f, Height = 500f };
        page.AddChild(list);
        page.AddChild(search);
        page.AddChild(deleteButton);
        return (page, search);
    }

    private static (JournalPageListController Controller, RuntimeJournalState State,
        UiElement Page, UiField Search, List<int> Opened) Bind(params JournalPage[] pages)
    {
        var state = new RuntimeJournalState();
        state.Load(pages);
        (UiElement page, UiField search) = BuildPage();
        var opened = new List<int>();

        var controller = new JournalPageListController(
            page,
            new JournalPageListController.Bindings(
                Journal: state.View,
                Commands: state,
                OpenPage: opened.Add,
                TemplateResolver: RowTemplate,
                Now: () => Now));

        return (controller, state, page, search, opened);
    }

    [Fact]
    public void EveryPageIsListedWithItsNumber()
    {
        var (controller, state, page, _, _) = Bind(
            new JournalPage(Title: "one"), new JournalPage(Title: "two"));

        Assert.Equal(new[] { 1, 2 }, controller.RowPages.ToArray());
        string[] numbers = Flatten(page)
            .OfType<UiText>()
            .Where(t => t.DatElementId == RowNumberId)
            .Select(t => t.LinesProvider!()[0].Text)
            .ToArray();
        Assert.Equal(new[] { "1", "2" }, numbers);
        state.Dispose();
    }

    [Fact]
    public void SearchingFiltersTheListButKeepsRealPageNumbers()
    {
        // The row number must name the page in the JOURNAL, not its position
        // in the filtered list — otherwise opening row 1 of a filtered list
        // opens the wrong page.
        var (controller, state, _, search, _) = Bind(
            new JournalPage(Title: "alpha"),
            new JournalPage(Title: "beta"),
            new JournalPage(Title: "gamma"));

        search.SetText("beta");
        controller.Tick();

        Assert.Equal(new[] { 2 }, controller.RowPages.ToArray());
        state.Dispose();
    }

    [Fact]
    public void AFilteredOutSelectionIsDroppedSoDeleteCannotHitAHiddenPage()
    {
        var (controller, state, _, search, _) = Bind(
            new JournalPage(Title: "alpha"), new JournalPage(Title: "beta"));
        controller.Select(1);

        search.SetText("beta");
        controller.Tick();

        Assert.Equal(0, controller.SelectedPage);
        state.Dispose();
    }

    [Fact]
    public void DeleteWithNothingSelectedDoesNothing()
    {
        var (_, state, page, _, _) = Bind(new JournalPage(Title: "only"));

        (UiElement.FindDescendant(page, DeleteButtonId) as UiButton)!.OnClick!();

        Assert.Single(state.View.Pages);
        state.Dispose();
    }

    [Fact]
    public void DeleteRemovesTheSelectedPage()
    {
        var (controller, state, page, _, _) = Bind(
            new JournalPage(Title: "one"), new JournalPage(Title: "two"));
        controller.Select(1);

        (UiElement.FindDescendant(page, DeleteButtonId) as UiButton)!.OnClick!();

        Assert.Equal("two", Assert.Single(state.View.Pages).Title);
        Assert.Equal(0, controller.SelectedPage);
        state.Dispose();
    }


    [Fact]
    public void OneClickSelectsAndDoesNotOpen()
    {
        var (controller, state, _, _, opened) = Bind(new JournalPage(Title: "one"));

        controller.Click(1);

        Assert.Equal(1, controller.SelectedPage);
        Assert.Empty(opened);
        state.Dispose();
    }

    [Fact]
    public void TwoClicksOnTheSameRowOpenIt()
    {
        var (controller, state, _, _, opened) = Bind(new JournalPage(Title: "one"));

        controller.Click(1);
        controller.Click(1);

        Assert.Equal(new[] { 1 }, opened.ToArray());
        state.Dispose();
    }

    [Fact]
    public void AThirdClickDoesNotReopenBecauseFiringResetsTheTracker()
    {
        var (controller, state, _, _, opened) = Bind(new JournalPage(Title: "one"));

        controller.Click(1);
        controller.Click(1);
        controller.Click(1);

        Assert.Single(opened);
        state.Dispose();
    }

    [Fact]
    public void ClicksOnDifferentRowsAreNotADoubleClick()
    {
        var (controller, state, _, _, opened) = Bind(
            new JournalPage(Title: "one"), new JournalPage(Title: "two"));

        controller.Click(1);
        controller.Click(2);

        Assert.Empty(opened);
        Assert.Equal(2, controller.SelectedPage);
        state.Dispose();
    }

    [Fact]
    public void TheDoubleClickWindowIsAFullSecond()
    {
        var state = new RuntimeJournalState();
        state.Load([new JournalPage(Title: "one")]);
        (UiElement page, _) = BuildPage();
        var opened = new List<int>();
        DateTime now = Now;

        var controller = new JournalPageListController(
            page,
            new JournalPageListController.Bindings(
                Journal: state.View,
                Commands: state,
                OpenPage: opened.Add,
                TemplateResolver: RowTemplate,
                Now: () => now));

        controller.Click(1);
        now = Now.AddMilliseconds(900);
        controller.Click(1);
        Assert.Single(opened);

        opened.Clear();
        now = Now.AddSeconds(10);
        controller.Click(1);
        now = now.AddMilliseconds(1100);
        controller.Click(1);
        Assert.Empty(opened);

        state.Dispose();
    }

    private static IEnumerable<UiElement> Flatten(UiElement e)
    {
        yield return e;
        foreach (UiElement child in e.Children)
        {
            foreach (UiElement descendant in Flatten(child))
                yield return descendant;
        }
    }
}
