using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public class BuildingLoaderTests
{
    private static LandBlockInfo MakeInfo(params (uint modelId, uint[] portalOtherCellIds)[] buildings)
    {
        var bls = new List<BuildingInfo>();
        foreach (var (modelId, portals) in buildings)
        {
            var portalList = new List<BuildingPortal>();
            foreach (var ocid in portals)
            {
                portalList.Add(new BuildingPortal
                {
                    OtherCellId  = (ushort)(ocid & 0xFFFFu),
                    Flags        = 0,
                    OtherPortalId = 0,
                    StabList     = new List<ushort>(),
                });
            }
            bls.Add(new BuildingInfo
            {
                ModelId  = modelId,
                Frame    = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
                Portals  = portalList,
            });
        }
        return new LandBlockInfo
        {
            Objects   = new List<Stab>(),
            Buildings = bls,
        };
    }

    [Fact]
    public void Empty_NoBuildings_EmptyRegistry()
    {
        var info = new LandBlockInfo { Objects = new List<Stab>(), Buildings = new List<BuildingInfo>() };
        var reg = BuildingLoader.Build(info, landblockId: 0xA9B40000u, cellsByCellId: new Dictionary<uint, AcDream.App.Rendering.LoadedCell>());
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void OneBuilding_OnePortal_MapsToOneCell()
    {
        var info = MakeInfo((modelId: 0x02000123u, portalOtherCellIds: new[] { 0x0150u }));
        var reg = BuildingLoader.Build(info, landblockId: 0xA9B40000u, cellsByCellId: new Dictionary<uint, AcDream.App.Rendering.LoadedCell>());
        Assert.Equal(1, reg.Count);
        var building = System.Linq.Enumerable.First(reg.All());
        Assert.Contains(0xA9B40150u, building.EnvCellIds);
    }

    [Fact]
    public void OneBuilding_MultiplePortals_MapsToMultipleCells()
    {
        var info = MakeInfo((0x02000123u, new[] { 0x0150u, 0x0151u, 0x0152u }));
        var reg = BuildingLoader.Build(info, 0xA9B40000u, new Dictionary<uint, AcDream.App.Rendering.LoadedCell>());
        var building = System.Linq.Enumerable.First(reg.All());
        Assert.Equal(3, building.EnvCellIds.Count);
        Assert.Contains(0xA9B40150u, building.EnvCellIds);
        Assert.Contains(0xA9B40151u, building.EnvCellIds);
        Assert.Contains(0xA9B40152u, building.EnvCellIds);
    }

    [Fact]
    public void TwoBuildings_AllocateSequentialIds()
    {
        var info = MakeInfo(
            (0x02000001u, new[] { 0x0150u }),
            (0x02000002u, new[] { 0x0160u }));
        var reg = BuildingLoader.Build(info, 0xA9B40000u, new Dictionary<uint, AcDream.App.Rendering.LoadedCell>());
        Assert.Equal(2, reg.Count);
        var ids = new SortedSet<uint>();
        foreach (var b in reg.All()) ids.Add(b.BuildingId);
        Assert.Equal(new SortedSet<uint> { 1, 2 }, ids);  // sequential 1, 2
    }

    [Fact]
    public void RetainedPublication_AdvancesOneBuildingAndCommitsCellStampsAtomically()
    {
        LandBlockInfo info = MakeInfo(
            (0x02000001u, new[] { 0x0150u }),
            (0x02000002u, new[] { 0x0160u }));
        LoadedCell cell150 = MinimalCell(0xA9B40150u);
        LoadedCell cell160 = MinimalCell(0xA9B40160u);
        var cells = new Dictionary<uint, LoadedCell>
        {
            [cell150.CellId] = cell150,
            [cell160.CellId] = cell160,
        };

        BuildingRegistryPublication publication =
            BuildingLoader.PreparePublication(info, 0xA9B40000u, cells);

        Assert.False(publication.PreparationCommitted);
        Assert.False(BuildingLoader.AdvancePreparationOne(publication));
        Assert.Equal(1, publication.BuildingCursor);
        Assert.Equal(1, publication.Registry.Count);
        Assert.Null(cell150.BuildingId);
        Assert.Null(cell160.BuildingId);

        Assert.True(BuildingLoader.AdvancePreparationOne(publication));
        Assert.Equal(2, publication.BuildingCursor);
        Assert.Equal(2, publication.Registry.Count);
        Assert.Null(cell150.BuildingId);
        Assert.Null(cell160.BuildingId);

        BuildingLoader.CommitPublication(publication);

        Assert.Equal(1u, cell150.BuildingId);
        Assert.Equal(2u, cell160.BuildingId);
        Assert.True(publication.PublicationCommitted);
    }

    [Fact]
    public void Build_StampsLoadedCellBuildingId()
    {
        var cell150 = new AcDream.App.Rendering.LoadedCell
        {
            CellId = 0xA9B40150u,
            Portals = new List<AcDream.App.Rendering.CellPortalInfo>(),
            PortalPolygons = new List<Vector3[]>(),
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            LocalBoundsMin = new Vector3(-5, -5, -5),
            LocalBoundsMax = new Vector3(5, 5, 5),
            ClipPlanes = new List<AcDream.App.Rendering.PortalClipPlane>(),
        };
        var cell151 = new AcDream.App.Rendering.LoadedCell
        {
            CellId = 0xA9B40151u,
            Portals = new List<AcDream.App.Rendering.CellPortalInfo>(),
            PortalPolygons = new List<Vector3[]>(),
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            LocalBoundsMin = new Vector3(-5, -5, -5),
            LocalBoundsMax = new Vector3(5, 5, 5),
            ClipPlanes = new List<AcDream.App.Rendering.PortalClipPlane>(),
        };
        var cells = new Dictionary<uint, AcDream.App.Rendering.LoadedCell>
        {
            { 0xA9B40150u, cell150 },
            { 0xA9B40151u, cell151 },
        };

        var info = MakeInfo((0x02000123u, new[] { 0x0150u, 0x0151u }));
        var reg = BuildingLoader.Build(info, 0xA9B40000u, cells);

        Assert.Equal(1, reg.Count);
        var b = System.Linq.Enumerable.First(reg.All());
        Assert.Equal(b.BuildingId, cell150.BuildingId);
        Assert.Equal(b.BuildingId, cell151.BuildingId);
    }

    [Fact]
    public void Build_ComputesExitPortalBounds()
    {
        var cell150 = new AcDream.App.Rendering.LoadedCell
        {
            CellId = 0xA9B40150u,
            Portals = new List<AcDream.App.Rendering.CellPortalInfo>
            {
                new(0xFFFF, 0, 0, 0),
            },
            PortalPolygons = new List<Vector3[]>
            {
                new[]
                {
                    new Vector3(-1, 2, 3),
                    new Vector3(4, 5, 6),
                    new Vector3(7, -8, 9),
                },
            },
            WorldTransform = Matrix4x4.CreateTranslation(10, 20, 30),
            InverseWorldTransform = Matrix4x4.Identity,
            LocalBoundsMin = new Vector3(-5, -5, -5),
            LocalBoundsMax = new Vector3(5, 5, 5),
            ClipPlanes = new List<AcDream.App.Rendering.PortalClipPlane>(),
        };

        var info = MakeInfo((0x02000123u, new[] { 0x0150u }));
        var reg = BuildingLoader.Build(info, 0xA9B40000u,
            new Dictionary<uint, AcDream.App.Rendering.LoadedCell>
            {
                { 0xA9B40150u, cell150 },
            });

        var b = System.Linq.Enumerable.First(reg.All());
        Assert.True(b.HasPortalBounds);
        Assert.Equal(new Vector3(9, 12, 33), b.PortalBounds.Min);
        Assert.Equal(new Vector3(17, 25, 39), b.PortalBounds.Max);
    }

    private static LoadedCell MinimalCell(uint cellId) => new()
    {
        CellId = cellId,
        Portals = new List<AcDream.App.Rendering.CellPortalInfo>(),
        PortalPolygons = new List<Vector3[]>(),
        WorldTransform = Matrix4x4.Identity,
        InverseWorldTransform = Matrix4x4.Identity,
        LocalBoundsMin = new Vector3(-5, -5, -5),
        LocalBoundsMax = new Vector3(5, 5, 5),
        ClipPlanes = new List<AcDream.App.Rendering.PortalClipPlane>(),
    };
}
