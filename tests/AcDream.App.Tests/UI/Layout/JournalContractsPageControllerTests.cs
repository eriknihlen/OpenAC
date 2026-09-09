using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Core.Quests;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class JournalContractsPageControllerTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    // The authored ids, from the installed dats.
    private const uint ListId = 0x100005CFu;
    private const uint RowNameId = 0x100005D1u;
    private const uint RowStatusId = 0x100005D2u;
    private const uint StatusValueId = 0x100005DFu;
    private const uint ContactValueId = 0x100005E0u;
    private const uint ContactLocationValueId = 0x100005E1u;
    private const uint QuestLocationValueId = 0x100005E2u;
    private const uint DescriptionId = 0x100005DEu;
    private const uint TimedValueId = 0x100005E3u;

    private static readonly System.Numerics.Vector4 AuthoredTextColor =
        new(0.8f, 0.8f, 0.8f, 1f);

    private static UiText Text(uint id) => new()
    {
        DatElementId = id,
        Width = 200f,
        Height = 18f,
        DefaultColor = AuthoredTextColor,
    };

    private static (UiElement Page, UiTemplateListBox List) BuildPage()
    {
        var listInfo = new ElementInfo
        {
            Id = ListId, Type = 5, Width = 270, Height = 298,
        };
        var list = new UiTemplateListBox(
            listInfo,
            static _ => (0u, 0, 0),
            [new UiTemplateListEntry(
                JournalContractsPageController.RowTemplateLayoutId,
                JournalContractsPageController.RowTemplateElementId)],
            scrollbarElementId: 0u)
        {
            DatElementId = ListId,
        };

        var page = new UiPanel { Width = 300f, Height = 500f };
        page.AddChild(list);
        foreach (uint id in new[]
        {
            StatusValueId, ContactValueId, ContactLocationValueId,
            QuestLocationValueId, DescriptionId, TimedValueId,
        })
        {
            page.AddChild(Text(id));
        }

        return (page, list);
    }

    private static UiElement? RowTemplate(uint layoutId, uint elementId)
    {
        if (layoutId != JournalContractsPageController.RowTemplateLayoutId
            || elementId != JournalContractsPageController.RowTemplateElementId)
        {
            return null;
        }

        var row = new UiDatElement(
            new ElementInfo
            {
                Id = JournalContractsPageController.RowTemplateElementId,
                Type = 3,
                Width = 270,
                Height = 20,
            },
            static _ => (0u, 0, 0));
        row.AddChild(Text(RowNameId));
        row.AddChild(Text(RowStatusId));
        return row;
    }

    private static ContractCatalog Catalog(params ContractEntry[] entries)
        => new(entries.ToDictionary(e => e.ContractId));

    private static ContractEntry Entry(
        uint id,
        string name,
        string description = "",
        string contact = "",
        string progressFormat = "",
        string repeatFlag = "",
        uint contactCell = 0u,
        uint questCell = 0u)
        => ContractEntry.Unknown with
        {
            ContractId = id,
            ContractName = name,
            Description = description,
            NameNpcStart = contact,
            DescriptionProgress = progressFormat,
            QuestflagRepeatTime = repeatFlag,
            LocationNpcStartCell = contactCell,
            LocationQuestAreaCell = questCell,
        };

    private static JournalContractsPageController Bind(
        UiElement page,
        RuntimeContractState state,
        ContractCatalog catalog,
        DateTime? now = null)
        => new(page, new JournalContractsPageController.Bindings(
            Contracts: state.View,
            Catalog: () => catalog,
            Now: () => now ?? Now,
            TemplateResolver: RowTemplate));

    private static void Track(
        RuntimeContractState state,
        uint contractId,
        uint stage,
        double whenRepeats = 0d,
        double whenDone = 0d,
        bool setAsDisplay = false)
        => state.ApplyUpdate(new ContractTrackerUpdate(
            new ContractTracker(1u, contractId, (ContractStage)stage, whenDone, whenRepeats, Now),
            Delete: false,
            SetAsDisplay: setAsDisplay));

    private static string TextOf(UiElement page, uint id)
    {
        var text = UiElement.FindDescendant(page, id) as UiText;
        return text?.LinesProvider?.Invoke().FirstOrDefault().Text ?? string.Empty;
    }

    [Fact]
    public void RowsCarryTheAuthoredNameAndTheRetailProgressText()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);

        JournalContractsPageController page1 = Bind(
            page, state, Catalog(Entry(0x10u, "Aerlinthe Recall Ring")));

        Assert.Equal(new[] { 0x10u }, page1.RowContractIds.ToArray());
        UiText name = Assert.IsType<UiText>(Assert.Single(
            UiElement.FindDescendant(page, ListId)!.Children.SelectMany(Flatten),
            e => (e as UiText)?.DatElementId == RowNameId));
        Assert.Equal("Aerlinthe Recall Ring", name.LinesProvider!()[0].Text);
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

    [Fact]
    public void TheDetailPaneShowsTheSelectedContract()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);
        Track(state, 0x20u, stage: 1u);

        JournalContractsPageController controller = Bind(page, state, Catalog(
            Entry(0x10u, "First", description: "Kill the thing.", contact: "Bob"),
            Entry(0x20u, "Second", description: "Find the other thing.", contact: "Alice")));

        controller.Select(0x20u);

        Assert.Equal("Find the other thing.", TextOf(page, DescriptionId));
        Assert.Equal("Alice", TextOf(page, ContactValueId));
        Assert.Equal("Available", TextOf(page, StatusValueId));
    }

    [Fact]
    public void TheServersDisplayContractIsWhatOpensSelected()
    {
        // SetAsDisplayContract is the server nominating what to show. Ignoring
        // it and always selecting the first row would show the wrong quest
        // right after the one the player just accepted.
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);
        Track(state, 0x20u, stage: 2u, setAsDisplay: true);

        JournalContractsPageController controller = Bind(page, state, Catalog(
            Entry(0x10u, "First"), Entry(0x20u, "Second")));

        Assert.Equal(0x20u, controller.SelectedContractId);
    }

    [Fact]
    public void WithNoDisplayContractTheFirstRowStands()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x30u, stage: 2u);
        Track(state, 0x10u, stage: 2u);

        JournalContractsPageController controller = Bind(page, state, Catalog(
            Entry(0x10u, "First"), Entry(0x30u, "Third")));

        // GetContracts orders by id, so 0x10 is first.
        Assert.Equal(0x10u, controller.SelectedContractId);
    }

    [Fact]
    public void AnEmptyTrackerClearsTheDetailPaneRatherThanStrandingText()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);

        JournalContractsPageController controller = Bind(
            page, state, Catalog(Entry(0x10u, "First", description: "Kill it.")));
        Assert.Equal("Kill it.", TextOf(page, DescriptionId));

        state.ApplyTable(new Dictionary<uint, ContractTracker>());
        controller.Tick();

        Assert.Equal(0u, controller.SelectedContractId);
        Assert.Equal(string.Empty, TextOf(page, DescriptionId));
        Assert.Empty(controller.RowContractIds);
    }

    [Fact]
    public void TheListRebuildsOnlyWhenTheTrackerActuallyMoved()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        int builds = 0;

        var controller = new JournalContractsPageController(
            page,
            new JournalContractsPageController.Bindings(
                Contracts: state.View,
                Catalog: () => Catalog(Entry(0x10u, "First")),
                Now: () => Now,
                TemplateResolver: (l, e) => { builds++; return RowTemplate(l, e); }));

        Track(state, 0x10u, stage: 2u);
        controller.Tick();
        int afterFirstChange = builds;

        for (int frame = 0; frame < 10; frame++)
            controller.Tick();

        Assert.Equal(afterFirstChange, builds);
    }

    [Fact]
    public void AContractTheDatHasNeverHeardOfStillGetsARow()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0xDEADu, stage: 2u);

        JournalContractsPageController controller =
            Bind(page, state, ContractCatalog.Empty);

        Assert.Equal(new[] { 0xDEADu }, controller.RowContractIds.ToArray());
        Assert.Equal("In Progress", TextOf(page, StatusValueId));
    }

    [Fact]
    public void ALocationWithNoOutdoorCoordinatesReadsAsIndoors()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);

        Bind(page, state, Catalog(Entry(0x10u, "First", contactCell: 0x01020304u)));

        Assert.Equal("Indoors", TextOf(page, ContactLocationValueId));
    }

    [Fact]
    public void AnUnsetLocationIsBlankRatherThanIndoors()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);

        Bind(page, state, Catalog(Entry(0x10u, "First")));

        Assert.Equal(string.Empty, TextOf(page, QuestLocationValueId));
    }

    [Fact]
    public void TheTimedRowUsesTimeWhenDoneNotTimeWhenRepeats()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u, whenDone: 3661d, whenRepeats: 90d);

        Bind(page, state, Catalog(Entry(0x10u, "First")));

        Assert.Equal("1h 1m 1s", TextOf(page, TimedValueId));
    }

    [Fact]
    public void TheRepeatCountdownTicksWithoutRebuildingTheList()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 3u, whenRepeats: 600d);

        DateTime now = Now;
        var controller = new JournalContractsPageController(
            page,
            new JournalContractsPageController.Bindings(
                Contracts: state.View,
                Catalog: () => Catalog(Entry(0x10u, "First", repeatFlag: "f")),
                Now: () => now,
                TemplateResolver: RowTemplate));

        Assert.Equal("Done (10m 0s to Repeat)", TextOf(page, StatusValueId));

        now = Now.AddMinutes(5);
        controller.Tick();

        Assert.Equal("Done (5m 0s to Repeat)", TextOf(page, StatusValueId));
    }

    [Fact]
    public void RowsOptOutOfClickThroughOrTheListIsDead()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);

        Bind(page, state, Catalog(Entry(0x10u, "First")));

        UiDatElement row = Assert.IsAssignableFrom<UiDatElement>(Assert.Single(
            UiElement.FindDescendant(page, ListId)!.Children.SelectMany(Flatten),
            e => e is UiDatElement { OnClick: not null }));
        Assert.False(row.ClickThrough);
    }

    [Fact]
    public void ClickingARowSelectsIt()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);
        Track(state, 0x20u, stage: 2u);

        JournalContractsPageController controller = Bind(page, state, Catalog(
            Entry(0x10u, "First"), Entry(0x20u, "Second", description: "Second desc.")));
        Assert.Equal(0x10u, controller.SelectedContractId);

        UiDatElement second = Assert.IsAssignableFrom<UiDatElement>(
            UiElement.FindDescendant(page, ListId)!.Children
                .SelectMany(Flatten)
                .Where(e => e is UiDatElement { OnClick: not null })
                .ElementAt(1));
        second.OnClick!();

        Assert.Equal(0x20u, controller.SelectedContractId);
        Assert.Equal("Second desc.", TextOf(page, DescriptionId));
    }

    [Fact]
    public void TheSelectedRowIsHighlightedAndTheOthersAreNot()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);
        Track(state, 0x20u, stage: 2u);

        JournalContractsPageController controller = Bind(page, state, Catalog(
            Entry(0x10u, "First"), Entry(0x20u, "Second")));

        UiText[] names = UiElement.FindDescendant(page, ListId)!.Children
            .SelectMany(Flatten)
            .OfType<UiText>()
            .Where(t => t.DatElementId == RowNameId)
            .ToArray();
        Assert.Equal(2, names.Length);

        System.Numerics.Vector4 selectedColor = names[0].DefaultColor;
        System.Numerics.Vector4 unselectedColor = names[1].DefaultColor;
        Assert.NotEqual(unselectedColor, selectedColor);

        controller.Select(0x20u);

        Assert.Equal(unselectedColor, names[0].DefaultColor);
        Assert.Equal(selectedColor, names[1].DefaultColor);
    }

    [Fact]
    public void TheHighlightSurvivesARebuild()
    {
        // A rebuild discards the row objects, so a remembered selection has to
        // be re-PAINTED onto the new ones rather than merely kept.
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);
        Track(state, 0x20u, stage: 2u);

        JournalContractsPageController controller = Bind(page, state, Catalog(
            Entry(0x10u, "First"), Entry(0x20u, "Second")));
        controller.Select(0x20u);

        Track(state, 0x30u, stage: 2u);      // forces a rebuild
        controller.Tick();

        Assert.Equal(0x20u, controller.SelectedContractId);
        UiText selected = UiElement.FindDescendant(page, ListId)!.Children
            .SelectMany(Flatten)
            .OfType<UiText>()
            .Where(t => t.DatElementId == RowNameId)
            .ElementAt(1);
        UiText unselected = UiElement.FindDescendant(page, ListId)!.Children
            .SelectMany(Flatten)
            .OfType<UiText>()
            .Where(t => t.DatElementId == RowNameId)
            .ElementAt(0);
        Assert.NotEqual(unselected.DefaultColor, selected.DefaultColor);
    }

    // ── Abandon (game action 0x0316) ────────────────────────────────────

    [Fact]
    public void AbandonSendsTheSelectedContractAndRemovesNothingLocally()
    {
        // The row disappears when the SERVER answers with its own 0x0315
        // delete. Removing it optimistically would vanish a quest the server
        // refused to drop, and it would reappear on the next full table.
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);
        var abandoned = new List<uint>();

        var controller = new JournalContractsPageController(
            page,
            new JournalContractsPageController.Bindings(
                Contracts: state.View,
                Catalog: () => Catalog(Entry(0x10u, "First")),
                Now: () => Now,
                TemplateResolver: RowTemplate,
                Abandon: abandoned.Add));

        controller.AbandonSelected();

        Assert.Equal(new[] { 0x10u }, abandoned.ToArray());
        Assert.Equal(1, state.View.Snapshot.ContractCount);
    }

    [Fact]
    public void AbandonWithNothingSelectedSendsNothing()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        var abandoned = new List<uint>();

        var controller = new JournalContractsPageController(
            page,
            new JournalContractsPageController.Bindings(
                Contracts: state.View,
                Catalog: () => ContractCatalog.Empty,
                Now: () => Now,
                TemplateResolver: RowTemplate,
                Abandon: abandoned.Add));

        controller.AbandonSelected();

        Assert.Empty(abandoned);
    }

    [Fact]
    public void AProgressCounterRendersThroughTheAuthoredFormat()
    {
        (UiElement page, _) = BuildPage();
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 9u);       // ProgressCounter + 5

        Bind(page, state, Catalog(
            Entry(0x10u, "First", progressFormat: "%d/20 Tuskers")));

        Assert.Equal("5/20 Tuskers", TextOf(page, StatusValueId));
    }
}
