using AcDream.App.Tests.Rendering;
using AcDream.App.Rendering.Walk;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.Rendering.Walk;

[Trait("Lane", "InstalledDat")]
public sealed class WalkWorldDatAdapterTests
{
    private static DatCollection OpenDats()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }
        return new DatCollection(datDir!, DatAccessType.Read);
    }

    public static readonly TheoryData<uint, uint[]> TraceBuildingRosters = new()
    {
        {
            0xF4180000u,
            new[]
            {
                0xF4180011u, 0xF4180001u, 0xF4180009u, 0xF4180002u, 0xF4180004u,
                0xF4180014u, 0xF418000Au, 0xF418000Cu, 0xF418000Bu,
            }
        },
        { 0xF5180000u, new[] { 0xF518002Eu } },
        { 0xF4170000u, new[] { 0xF4170011u, 0xF4170012u, 0xF4170002u } },
        { 0xF3180000u, new[] { 0xF3180020u } },
        {
            0xA9B40000u,
            new[]
            {
                0xA9B4000Fu, 0xA9B40017u, 0xA9B4002Fu, 0xA9B40016u, 0xA9B4001Eu,
                0xA9B40026u, 0xA9B40036u, 0xA9B4001Au, 0xA9B40022u, 0xA9B40032u,
                0xA9B40031u, 0xA9B40029u,
            }
        },
        { 0xA9B30000u, new[] { 0xA9B3003Cu } },
        { 0xAAB50000u, new[] { 0xAAB50002u } },
    };

    [Theory]
    [MemberData(nameof(TraceBuildingRosters))]
    public void Every_building_retail_drew_exists_at_its_exact_position_cell(
        uint landblockId, uint[] tracedBuildingIds)
    {
        using DatCollection dats = OpenDats();

        List<WalkWorldDatAdapter.BuildingEntry> buildings =
            WalkWorldDatAdapter.BuildBuildings(dats, landblockId);
        var roster = buildings.Select(e => e.Building.PositionCellId).ToHashSet();

        foreach (uint traced in tracedBuildingIds)
            Assert.Contains(traced, roster);
    }

    [Fact]
    public void Traced_buildings_carry_portals_with_stab_lists_and_drawing_bsp_portals()
    {
        using DatCollection dats = OpenDats();

        List<WalkWorldDatAdapter.BuildingEntry> buildings =
            WalkWorldDatAdapter.BuildBuildings(dats, 0xF4180000u);
        WalkBuilding sanctuary = Assert.Single(
            buildings, e => e.Building.PositionCellId == 0xF418000Au).Building;

        Assert.NotEmpty(sanctuary.Portals);
        Assert.All(sanctuary.Portals, p => Assert.NotEmpty(p.StabList));
        // The look-in machinery needs PORT nodes in the drawing BSP.
        Assert.NotNull(sanctuary.DrawingBsp);
        Assert.True(
            CountPortalRefs(sanctuary.DrawingBsp) > 0,
            "the sanctuary building's drawing BSP must carry portal polygons");
    }

    [Fact]
    public void The_traced_look_in_cells_build_with_portals_and_polygons()
    {
        using DatCollection dats = OpenDats();

        foreach (uint cellId in new[] { 0xF4180104u, 0xF4180106u, 0xF418010Fu })
        {
            WalkCell? cell = WalkWorldDatAdapter.BuildCell(dats, cellId);
            Assert.NotNull(cell);
            Assert.NotEmpty(cell!.Portals);
            Assert.Equal(cell.Portals.Length, cell.PortalPolygons.Length);
            Assert.All(cell.PortalPolygons, p => Assert.True(p.Vertices.Length >= 3));
            Assert.NotEmpty(cell.StabList);
        }
    }

    private static int CountPortalRefs(WalkBspNode? node)
    {
        if (node is null) return 0;
        int count = node.InPortals?.Length ?? 0;
        return count + CountPortalRefs(node.PosNode) + CountPortalRefs(node.NegNode);
    }
}
