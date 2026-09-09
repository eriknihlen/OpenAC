using System.IO;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class LayoutImporterInvisibleSweepTests
{
    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    private readonly record struct Finding(uint LayoutId, uint ElementId);

    [InstalledDatFact]
    public void EveryAuthoredInvisibleWidget_StartsHiddenAcrossAllLayouts()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var authored = new List<Finding>();
        var built = new List<Finding>();
        var incorrectlyVisible = new List<Finding>();

        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>().OrderBy(static id => id))
        {
            ElementInfo? tree = LayoutImporter.ImportInfos(dats, layoutId);
            if (tree is null) continue;

            CollectAuthored(layoutId, tree, authored);

            ImportedLayout layout = LayoutImporter.Build(
                tree, _ => (0u, 0, 0), datFont: null, sourceLayoutDid: layoutId);
            CollectBuilt(layoutId, layout.Root, built, incorrectlyVisible);
        }

        foreach (IGrouping<uint, Finding> group in authored
                     .GroupBy(static f => f.LayoutId)
                     .OrderBy(static g => g.Key))
        {
            Console.WriteLine(
                $"[INVISIBLE] layout=0x{group.Key:X8} count={group.Count()} ids=["
                + string.Join(",", group.Select(static f => $"0x{f.ElementId:X8}"))
                + "]");
        }

        Console.WriteLine(
            $"[INVISIBLE] authored={authored.Count} layouts="
            + $"{authored.Select(static f => f.LayoutId).Distinct().Count()} "
            + $"built={built.Count} incorrectlyVisible={incorrectlyVisible.Count}");

        // Keep the global threshold resilient to an installed DAT revision while
        // pinning landmarks from independent screens in both data and widgets.
        Assert.True(authored.Count >= 1_000,
            $"Expected the known client-wide 0x3B population, found {authored.Count}.");
        Assert.Contains(authored, static f => f.ElementId == 0x10000403u);
        Assert.Contains(authored, static f => f.ElementId == 0x10000494u);
        Assert.Contains(authored, static f => f.ElementId == 0x100006A4u);
        Assert.Contains(authored, static f => f.ElementId == 0x1000048Cu);

        Assert.Contains(built, static f => f.ElementId == 0x100006A4u);
        Assert.Contains(built, static f => f.ElementId == 0x1000048Cu);
        Assert.Empty(incorrectlyVisible);
    }

    private static void CollectAuthored(uint layoutId, ElementInfo node, List<Finding> findings)
    {
        if (node.Invisible)
            findings.Add(new Finding(layoutId, node.Id));

        foreach (ElementInfo child in node.Children)
            CollectAuthored(layoutId, child, findings);
    }

    private static void CollectBuilt(
        uint layoutId,
        UiElement node,
        List<Finding> built,
        List<Finding> incorrectlyVisible)
    {
        if (node.AuthoredInvisible)
        {
            var finding = new Finding(layoutId, node.DatElementId);
            built.Add(finding);
            if (node.Visible)
                incorrectlyVisible.Add(finding);
        }

        foreach (UiElement child in node.Children)
            CollectBuilt(layoutId, child, built, incorrectlyVisible);
    }
}
