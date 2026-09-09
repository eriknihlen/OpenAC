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
public sealed class LayoutImporterMediaBearingChildSweepTests
{
    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    private readonly record struct Finding(uint LayoutId, uint ElementId, uint[] MediaBearingChildIds);

    [InstalledDatFact]
    public void MediaBearingChildSweep_EnumeratesEveryAffectedType12Element()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var findings = new List<Finding>();
        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>().OrderBy(x => x))
        {
            ElementInfo? tree = LayoutImporter.ImportInfos(dats, layoutId);
            if (tree is null) continue;
            Walk(layoutId, tree, findings);
        }

        Console.WriteLine($"[SWEEP] {findings.Count} affected Type-12 elements across "
            + $"{findings.Select(f => f.LayoutId).Distinct().Count()} layouts.");
        foreach (Finding f in findings.OrderBy(f => f.LayoutId).ThenBy(f => f.ElementId))
        {
            Console.WriteLine(
                $"[SWEEP]   layout=0x{f.LayoutId:X8} element=0x{f.ElementId:X8} "
                + $"mediaBearingChildren=[{string.Join(",", f.MediaBearingChildIds.Select(id => $"0x{id:X8}"))}]");
        }

        Assert.Contains(findings, f => f.LayoutId == 0x21000005u && f.ElementId == 0x1000059Au);
        Assert.Contains(findings, f => f.LayoutId == 0x2100006Fu && f.ElementId == 0x10000011u);

        Assert.Contains(findings, f => f.ElementId == 0x100003E0u); // Profession
        Assert.Contains(findings, f => f.ElementId == 0x10000409u); // Town
        Assert.Contains(findings, f => f.ElementId == 0x10000404u); // Summary how-to

        Assert.True(findings.Count > 0, "the sweep must find at least the known chargen landmarks.");
    }

    [InstalledDatFact]
    public void MainGameUiAndChatInput_MediaBearingChildrenNowBuildAsRealWidgets()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        AssertChildrenBuild(
            dats, layoutId: 0x21000005u, elementId: 0x1000059Au,
            expectedChildIds:
            [
                0x100002DEu, 0x100002DFu, 0x100002E0u, 0x100002E1u,
                0x100000E8u, 0x100002E2u, 0x100002E3u, 0x100000EAu,
            ],
            expectedInvisible: []);

        AssertChildrenBuild(
            dats, layoutId: 0x2100006Fu, elementId: 0x10000011u,
            expectedChildIds: [0x1000048Cu],
            expectedInvisible: [0x1000048Cu]);
    }

    private static void AssertChildrenBuild(
        IDatReaderWriter dats, uint layoutId, uint elementId, uint[] expectedChildIds,
        uint[] expectedInvisible)
    {
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, layoutId);
        Assert.NotNull(tree);
        ElementInfo? target = FindInfo(tree!, elementId);
        Assert.NotNull(target);

        UiElement built = LayoutImporter.Build(
            target!, _ => (0u, 0, 0), null).Root;
        Assert.IsType<UiText>(built);
        foreach (uint childId in expectedChildIds)
        {
            UiElement? child = UiElement.FindDescendant(built, childId);
            Assert.NotNull(child);
            bool shouldBeInvisible = expectedInvisible.Contains(childId);
            Assert.Equal(!shouldBeInvisible, child!.Visible);
        }
    }

    private static ElementInfo? FindInfo(ElementInfo node, uint id)
    {
        if (node.Id == id) return node;
        foreach (ElementInfo child in node.Children)
        {
            ElementInfo? found = FindInfo(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private readonly record struct InvisibleChildFinding(uint LayoutId, uint ParentElementId, uint ChildElementId);

    [InstalledDatFact]
    public void MediaBearingChildSweep_EnumeratesWhichAffectedChildrenAuthorInvisible()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var invisibleFindings = new List<InvisibleChildFinding>();
        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>().OrderBy(x => x))
        {
            ElementInfo? tree = LayoutImporter.ImportInfos(dats, layoutId);
            if (tree is null) continue;
            WalkForInvisibleMediaBearingChildren(layoutId, tree, invisibleFindings);
        }

        Console.WriteLine($"[SWEEP-INV] {invisibleFindings.Count} media-bearing children of the "
            + "Batch C carve-out author Invisible=true themselves.");
        foreach (InvisibleChildFinding f in invisibleFindings)
        {
            Console.WriteLine($"[SWEEP-INV]   layout=0x{f.LayoutId:X8} parent=0x{f.ParentElementId:X8} "
                + $"child=0x{f.ChildElementId:X8}");
        }

        uint[] goldFramePieceIds =
        [
            0x100002DEu, 0x100002DFu, 0x100002E0u, 0x100002E1u,
            0x100000E8u, 0x100002E2u, 0x100002E3u, 0x100000EAu,
        ];
        foreach (uint pieceId in goldFramePieceIds)
        {
            Assert.DoesNotContain(invisibleFindings, f => f.ChildElementId == pieceId);
        }
    }

    private static void WalkForInvisibleMediaBearingChildren(
        uint layoutId, ElementInfo node, List<InvisibleChildFinding> findings)
    {
        if (node.Type == 12u)
        {
            bool passToChildren = node.States.Values.Any(static s => s.PassToChildren);
            if (!passToChildren)
            {
                foreach (ElementInfo child in node.Children)
                {
                    if (child.StateMedia.Count > 0 && child.Invisible)
                        findings.Add(new InvisibleChildFinding(layoutId, node.Id, child.Id));
                }
            }
        }

        foreach (ElementInfo child in node.Children)
            WalkForInvisibleMediaBearingChildren(layoutId, child, findings);
    }

    private static void Walk(uint layoutId, ElementInfo node, List<Finding> findings)
    {
        if (node.Type == 12u)
        {
            bool passToChildren = node.States.Values.Any(static s => s.PassToChildren);
            if (!passToChildren)
            {
                uint[] mediaBearingChildren = node.Children
                    .Where(static c => c.StateMedia.Count > 0)
                    .Select(static c => c.Id)
                    .ToArray();
                if (mediaBearingChildren.Length > 0)
                    findings.Add(new Finding(layoutId, node.Id, mediaBearingChildren));
            }
        }

        foreach (ElementInfo child in node.Children)
            Walk(layoutId, child, findings);
    }
}
