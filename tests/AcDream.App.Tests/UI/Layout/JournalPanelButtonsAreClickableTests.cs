using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class JournalPanelButtonsAreClickableTests
{
    private static readonly (uint Id, string Name)[] Buttons =
    [
        (0x100005DCu, "Abandon (contracts)"),
        (0x10000567u, "New"),
        (0x1000056Fu, "First"),
        (0x10000571u, "Last"),
        (0x10000565u, "Previous"),
        (0x10000566u, "Next"),
        (0x10000574u, "Record"),
        (0x1000057Du, "Start"),
        (0x10000585u, "Delete (page list)"),
        (0x10000588u, "Reset (page list)"),
    ];

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
        {
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; "
                + "see docs/release-gate.md.");
        }

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

    [Fact]
    public void EveryJournalPanelButtonBuildsEnabled()
    {
        ImportedLayout layout = BuildPanel();

        var dead = new List<string>();
        foreach ((uint id, string name) in Buttons)
        {
            if (layout.FindElement(id) is not UiButton button)
            {
                dead.Add($"{name} (0x{id:X8}) did not build as a UiButton");
                continue;
            }

            if (!button.Enabled)
                dead.Add($"{name} (0x{id:X8}) built DISABLED");
        }

        Assert.Empty(dead);
    }

    [Fact]
    public void EveryJournalPanelButtonIsReachableByAClickAtItsCentre()
    {
        ImportedLayout layout = BuildPanel();

        var unreachable = new List<string>();
        foreach ((uint id, string name) in Buttons)
        {
            if (layout.FindElement(id) is not UiButton button)
                continue;

            UiElement? hit = HitTestSelf(button);
            if (!ReferenceEquals(hit, button))
                unreachable.Add($"{name} (0x{id:X8}) hit-tested to {hit?.GetType().Name ?? "nothing"}");
        }

        Assert.Empty(unreachable);
    }

    private static UiElement? HitTestSelf(UiButton button)
    {
        var hitTest = typeof(UiElement).GetMethod(
            "HitTest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return hitTest.Invoke(
            button,
            [button.Width / 2f, button.Height / 2f]) as UiElement;
    }
}
