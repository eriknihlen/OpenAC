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
public sealed class UiButtonCorpusSweepTests
{
    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    private readonly record struct ButtonFinding(uint LayoutId, uint ElementId);

    [InstalledDatFact]
    public void LabelBoxPath_EnumeratesEveryMatchingButton()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var findings = new List<ButtonFinding>();
        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>().OrderBy(x => x))
        {
            ElementInfo? tree = LayoutImporter.ImportInfos(dats, layoutId);
            if (tree is null) continue;
            WalkButtons(layoutId, tree, findings, MatchesLabelBoxShape);
        }

        Console.WriteLine($"[SWEEP-A] {findings.Count} buttons take the LabelBox path across "
            + $"{findings.Select(f => f.LayoutId).Distinct().Count()} layouts.");
        foreach (ButtonFinding f in findings.OrderBy(f => f.LayoutId).ThenBy(f => f.ElementId))
            Console.WriteLine($"[SWEEP-A]   layout=0x{f.LayoutId:X8} element=0x{f.ElementId:X8}");

        Assert.Contains(findings, f => IsTownButton(f.ElementId));
    }

    [InstalledDatFact]
    public void CustomSelectionPair_NeverCoexistsWithStandardNormalHighlightMedia()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var findings = new List<ButtonFinding>();
        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>().OrderBy(x => x))
        {
            ElementInfo? tree = LayoutImporter.ImportInfos(dats, layoutId);
            if (tree is null) continue;
            WalkButtons(layoutId, tree, findings, MatchesConflictingPairShape);
        }

        Console.WriteLine($"[SWEEP-B] {findings.Count} buttons author BOTH the custom "
            + "Unselected/Selected pair AND standard Normal/Highlight media.");
        foreach (ButtonFinding f in findings)
            Console.WriteLine($"[SWEEP-B]   layout=0x{f.LayoutId:X8} element=0x{f.ElementId:X8}");

        Assert.Empty(findings);
    }

    [InstalledDatFact]
    public void PerStateLabelColorMap_EnumeratesEveryButtonBeyondChargen()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var findings = new List<ButtonFinding>();
        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>().OrderBy(x => x))
        {
            ElementInfo? tree = LayoutImporter.ImportInfos(dats, layoutId);
            if (tree is null) continue;
            WalkButtons(layoutId, tree, findings, MatchesPerStateLabelStyleShape);
        }

        Console.WriteLine($"[SWEEP-C] {findings.Count} buttons carry a genuine per-state "
            + "label color/outline map.");
        foreach (ButtonFinding f in findings.OrderBy(f => f.LayoutId).ThenBy(f => f.ElementId))
            Console.WriteLine($"[SWEEP-C]   layout=0x{f.LayoutId:X8} element=0x{f.ElementId:X8}");

        Assert.Contains(findings, f =>
            f.ElementId == CharacterCreationAppearancePage.HairSpinId);
        Assert.Contains(findings, f => IsTownButton(f.ElementId));
    }

    private static bool IsTownButton(uint elementId) => elementId is
        0x1000040Bu or 0x1000040Du or 0x1000040Eu or 0x1000040Fu;

    // ── Shared predicates (re-derived from DatWidgetFactory.BuildButton) ──

    private static bool MatchesLabelBoxShape(ElementInfo info)
    {
        if (info.StateMedia.Count != 0)
            return false;
        ElementInfo[] faces = FindStatefulFaceChildren(info);
        if (faces.Length != 1)
            return false;

        bool ownCaption = HasStringInfoProperty(info);
        if (ownCaption)
            return false;
        return info.Children.Any(child => child.Type == 12u && HasStringInfoProperty(child));
    }

    private static bool MatchesConflictingPairShape(ElementInfo info)
    {
        bool hasCustomPair = info.StateMedia.ContainsKey("Unselected") && info.StateMedia.ContainsKey("Selected");
        bool hasStandardPair = info.StateMedia.ContainsKey("Normal") || info.StateMedia.ContainsKey("Highlight");
        return hasCustomPair && hasStandardPair;
    }

    private static bool MatchesPerStateLabelStyleShape(ElementInfo info)
    {
        ElementInfo labelInfo = HasStringInfoProperty(info)
            ? info
            : info.Children.FirstOrDefault(child => child.Type == 12u && HasStringInfoProperty(child)) ?? info;

        return ElementReader.BuildPerStateColorMap(labelInfo, 0x1Bu) is not null
            || ElementReader.BuildPerStateBoolMap(labelInfo, 0x21u) is not null;
    }

    private static bool HasStringInfoProperty(ElementInfo info) =>
        info.TryGetEffectiveProperty(0x17u, out UiPropertyValue property)
        && property.Kind == UiPropertyKind.StringInfo;

    private static ElementInfo[] FindStatefulFaceChildren(ElementInfo info) =>
        [.. info.Children
            .Where(child =>
                child.StateMedia.Count != 0
                && child.StateMedia.Keys.Any(childState =>
                    info.States.Values.Any(parentState =>
                        string.Equals(parentState.Name, childState, StringComparison.Ordinal))))
            .OrderBy(child => child.ReadOrder)];

    private static void WalkButtons(
        uint layoutId,
        ElementInfo node,
        List<ButtonFinding> findings,
        Func<ElementInfo, bool> predicate)
    {
        if (node.Type == 1u && predicate(node))
            findings.Add(new ButtonFinding(layoutId, node.Id));

        foreach (ElementInfo child in node.Children)
            WalkButtons(layoutId, child, findings, predicate);
    }
}
