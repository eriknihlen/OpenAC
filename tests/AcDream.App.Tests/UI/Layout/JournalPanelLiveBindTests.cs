using System;
using System.Collections.Generic;
using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Content;
using AcDream.Core.Journal;
using AcDream.Runtime.Gameplay;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class JournalPanelLiveBindTests
{
    private const uint OutdoorCell = 0xA9B4001Fu;

    private static ImportedLayout BuildPanel()
    {
        string? datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (string.IsNullOrWhiteSpace(datDir) || !Directory.Exists(datDir))
        {
            datDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "Asheron's Call");
        }

        if (!Directory.Exists(datDir))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ElementInfo? root = LayoutImporter.ImportInfos(
            adapter,
            JournalPanelController.HostLayoutId,
            JournalPanelController.SlotElementId);
        Assert.NotNull(root);

        var strings = new DatStringResolver(adapter);
        return LayoutImporter.Build(root!, _ => (0u, 0, 0), null, _ => null, strings.Resolve);
    }

    private static (JournalNotesPageController Controller, RuntimeJournalState State,
        UiElement Page) BindNotes(ImportedLayout layout, Func<uint>? playerCell = null)
    {
        UiElement? page = layout.FindElement(JournalPanelController.NotesPageId);
        Assert.NotNull(page);

        var state = new RuntimeJournalState();
        state.Load([new JournalPage()]);

        var controller = new JournalNotesPageController(
            page!,
            new JournalNotesPageController.Bindings(
                Journal: state.View,
                Commands: state,
                PlayerCell: playerCell ?? (() => OutdoorCell),
                Now: () => new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)));

        return (controller, state, page!);
    }

    [Fact]
    public void TheNotesPageWiresEveryButtonItOwns()
    {
        ImportedLayout layout = BuildPanel();
        (_, RuntimeJournalState state, UiElement page) = BindNotes(layout);

        var unwired = new List<string>();
        foreach ((uint id, string name) in new[]
        {
            (0x10000565u, "Previous"), (0x10000566u, "Next"), (0x10000567u, "New"),
            (0x1000056Fu, "First"), (0x10000571u, "Last"),
            (0x10000574u, "Record"), (0x1000057Du, "Start"),
        })
        {
            if (UiElement.FindDescendant(page, id) is not UiButton button)
                unwired.Add($"{name} (0x{id:X8}) not found under the notes page");
            else if (button.OnClick is null)
                unwired.Add($"{name} (0x{id:X8}) has no OnClick");
        }

        Assert.Empty(unwired);
        state.Dispose();
    }

    [Fact]
    public void RecordOnTheRealPageStampsAndDisplaysTheLocation()
    {
        ImportedLayout layout = BuildPanel();
        (_, RuntimeJournalState state, UiElement page) = BindNotes(layout);

        (UiElement.FindDescendant(page, 0x10000574u) as UiButton)!.OnClick!();

        Assert.True(state.View.Current.HasLocation);
        var readout = UiElement.FindDescendant(page, 0x10000573u) as UiField;
        Assert.NotNull(readout);
        Assert.NotEqual(string.Empty, readout!.Text);
        state.Dispose();
    }

    [Fact]
    public void RecordIndoorsDoesNothingBecauseThereAreNoCoordinates()
    {
        ImportedLayout layout = BuildPanel();
        (_, RuntimeJournalState state, UiElement page) =
            BindNotes(layout, playerCell: () => 0x01020304u);

        (UiElement.FindDescendant(page, 0x10000574u) as UiButton)!.OnClick!();

        Assert.False(state.View.Current.HasLocation);
        state.Dispose();
    }

    [Fact]
    public void StartOnTheRealPageSwapsToTheRunningReadout()
    {
        ImportedLayout layout = BuildPanel();
        (_, RuntimeJournalState state, UiElement page) = BindNotes(layout);

        var hours = UiElement.FindDescendant(page, 0x10000578u) as UiField;
        Assert.NotNull(hours);
        hours!.SetText("1");

        (UiElement.FindDescendant(page, 0x1000057Du) as UiButton)!.OnClick!();

        Assert.False(hours.Visible);
        UiElement? readout = UiElement.FindDescendant(page, 0x1000057Cu);
        Assert.NotNull(readout);
        Assert.True(readout!.Visible);
        state.Dispose();
    }

    [Fact]
    public void ThePageListWiresItsButtonsToo()
    {
        ImportedLayout layout = BuildPanel();
        UiElement? page = layout.FindElement(JournalPanelController.PageListPageId);
        Assert.NotNull(page);

        var state = new RuntimeJournalState();
        state.Load([new JournalPage(Title: "one")]);
        _ = new JournalPageListController(
            page!,
            new JournalPageListController.Bindings(
                Journal: state.View,
                Commands: state,
                OpenPage: _ => { },
                TemplateResolver: (_, _) => null));

        var unwired = new List<string>();
        foreach ((uint id, string name) in new[]
        {
            (0x10000585u, "Delete"), (0x10000588u, "Reset"),
        })
        {
            if (UiElement.FindDescendant(page!, id) is not UiButton button)
                unwired.Add($"{name} (0x{id:X8}) not found");
            else if (button.OnClick is null)
                unwired.Add($"{name} (0x{id:X8}) has no OnClick");
        }

        Assert.Empty(unwired);
        state.Dispose();
    }
}
