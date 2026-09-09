using AcDream.App.Tests.Rendering;
using AcDream.App.Rendering.Walk;
using DatReaderWriter;
using DatReaderWriter.Options;
using System.Text;

namespace AcDream.App.Tests.Rendering.Walk;

[Trait("Lane", "InstalledDat")]
public sealed class WalkBuildingJoinDiagnosticTests
{
    [Theory]
    [InlineData(0xA9B4001Au, new uint[] { 0xA9B4016Eu, 0xA9B4016Au, 0xA9B4016Cu })]
    [InlineData(0xA9B40022u, new uint[] { 0xA9B40164u, 0xA9B40162u, 0xA9B40167u, 0xA9B40169u })]
    public void Traced_lookin_cells_are_reachable_through_the_building_portal_table(
        uint buildingCellId, uint[] tracedLookInCells)
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory.");
        }
        using var dats = new DatCollection(datDir!, DatAccessType.Read);
        List<WalkWorldDatAdapter.BuildingEntry> entries =
            WalkWorldDatAdapter.BuildBuildings(dats, 0xA9B40000u);
        WalkBuilding building = Assert.Single(
            entries, e => e.Building.PositionCellId == buildingCellId).Building;

        var dump = new StringBuilder();
        dump.AppendLine($"building {buildingCellId:x8}: {building.Portals.Length} portals");
        var directCells = new HashSet<uint>();
        for (int i = 0; i < building.Portals.Length; i++)
        {
            ref WalkBldPortal p = ref building.Portals[i];
            directCells.Add(p.OtherCellId);
            dump.AppendLine(
                $"  portal[{i}]: side={p.PortalSide} exact={p.ExactMatch} "
                + $"other={p.OtherCellId:x8} otherPortal={p.OtherPortalId} "
                + $"stabs=[{string.Join(',', p.StabList.Select(s => s.ToString("x8")))}]");
        }
        var bspJoins = new List<int>();
        CollectPortalIndices(building.DrawingBsp, bspJoins);
        dump.AppendLine($"  BSP PortalRef indices: [{string.Join(',', bspJoins)}]");

        // Every BSP portal ref must join into the portal table.
        Assert.All(bspJoins, i => Assert.InRange(i, 0, building.Portals.Length - 1));

        var reachable = new HashSet<uint>(directCells);
        foreach (WalkBldPortal p in building.Portals)
            foreach (uint stab in p.StabList)
                reachable.Add(stab);
        foreach (uint traced in tracedLookInCells)
        {
            Assert.True(
                reachable.Contains(traced),
                $"traced look-in cell {traced:x8} is not reachable\n{dump}");
        }
    }

    private static void CollectPortalIndices(WalkBspNode? node, List<int> into)
    {
        if (node is null) return;
        if (node.InPortals is not null)
            into.AddRange(node.InPortals.Select(p => p.PortalIndex));
        CollectPortalIndices(node.PosNode, into);
        CollectPortalIndices(node.NegNode, into);
    }
}
