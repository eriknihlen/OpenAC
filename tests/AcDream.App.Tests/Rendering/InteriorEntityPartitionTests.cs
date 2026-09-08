using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.World;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class InteriorEntityPartitionTests
{
    private const uint CellA = 0xA9B40170;
    private const uint CellB = 0xA9B40171;
    private const uint HiddenCell = 0xA9B40199;
    private const uint OutdoorCell = 0xA9B40020;

    private static WorldEntity Ent(uint id, uint serverGuid, uint? parentCell) => new()
    {
        Id = id,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = 0x01000001,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = new[] { new MeshRef(0x01000001, Matrix4x4.Identity) },
        ParentCellId = parentCell,
    };

    private static IEnumerable<(uint, Vector3, Vector3, IReadOnlyList<WorldEntity>,
        IReadOnlyDictionary<uint, WorldEntity>?)> OneLb(uint lbId, params WorldEntity[] ents)
        => new[] { (lbId, Vector3.Zero, Vector3.Zero, (IReadOnlyList<WorldEntity>)ents,
                    (IReadOnlyDictionary<uint, WorldEntity>?)null) };

    [Fact]
    public void AllServerSpawned_GoToDynamics_StaticsSplitByCellAndOutdoor()
    {
        var unresolvedLive = Ent(1, serverGuid: 0x5000000A, parentCell: null);
        var liveNpcInCell = Ent(2, serverGuid: 0x80001234, parentCell: CellA);
        var staticA = Ent(3, serverGuid: 0, parentCell: CellA);
        var staticB = Ent(4, serverGuid: 0, parentCell: CellB);
        var scenery = Ent(5, serverGuid: 0, parentCell: null);
        var liveOutdoor = Ent(6, serverGuid: 0x80005678, parentCell: OutdoorCell);

        var visible = new HashSet<uint> { CellA, CellB };
        var result = InteriorEntityPartition.Partition(
            visible, OneLb(0xA9B4FFFF, unresolvedLive, liveNpcInCell, staticA, staticB, scenery, liveOutdoor));

        // Every server-spawned entity is a dynamic — drawn in the last pass.
        Assert.Equal(3, result.Dynamics.Count);
        Assert.Contains(unresolvedLive, result.Dynamics);
        Assert.Contains(liveNpcInCell, result.Dynamics);
        Assert.Contains(liveOutdoor, result.Dynamics);

        Assert.Single(result.ByCell[CellA]);
        Assert.Contains(staticA, result.ByCell[CellA]);
        Assert.Single(result.ByCell[CellB]);
        Assert.Contains(staticB, result.ByCell[CellB]);

        // Outdoor statics (shells/scenery) ride with the world pass.
        Assert.Single(result.OutdoorStatic);
        Assert.Contains(scenery, result.OutdoorStatic);
    }

    [Fact]
    public void HiddenCell_DropsStatics_ButNeverDynamics()
    {
        var staticHidden = Ent(3, serverGuid: 0, parentCell: HiddenCell);
        var liveHidden = Ent(4, serverGuid: 0x80001234, parentCell: HiddenCell);
        var visible = new HashSet<uint> { CellA };

        var result = InteriorEntityPartition.Partition(
            visible, OneLb(0xA9B4FFFF, staticHidden, liveHidden));

        Assert.False(result.ByCell.ContainsKey(HiddenCell));
        Assert.Empty(result.OutdoorStatic);

        Assert.Single(result.Dynamics);
        Assert.Contains(liveHidden, result.Dynamics);
    }

    [Fact]
    public void EntityWithNoMeshRefs_IsSkipped()
    {
        var noMesh = new WorldEntity
        {
            Id = 9, ServerGuid = 0, SourceGfxObjOrSetupId = 0x01000001,
            Position = Vector3.Zero, Rotation = Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(), ParentCellId = CellA,
        };
        var result = InteriorEntityPartition.Partition(
            new HashSet<uint> { CellA }, OneLb(0xA9B4FFFF, noMesh));

        Assert.False(result.ByCell.ContainsKey(CellA));
        Assert.Empty(result.Dynamics);
        Assert.Empty(result.OutdoorStatic);
    }

    [Fact]
    public void FrustumRejectsWholeLandblockBeforeWalkingItsEntities()
    {
        const uint culledLandblock = 0xA8B4FFFFu;
        const uint cameraLandblock = 0xA9B4FFFFu;
        var culled = Ent(10, serverGuid: 0x80000010u, parentCell: OutdoorCell);
        var camera = Ent(11, serverGuid: 0x80000011u, parentCell: OutdoorCell);
        var entries = new[]
        {
            (culledLandblock,
                new Vector3(10f),
                new Vector3(11f),
                (IReadOnlyList<WorldEntity>)new[] { culled },
                (IReadOnlyDictionary<uint, WorldEntity>?)null),
            (cameraLandblock,
                new Vector3(10f),
                new Vector3(11f),
                (IReadOnlyList<WorldEntity>)new[] { camera },
                (IReadOnlyDictionary<uint, WorldEntity>?)null),
        };

        InteriorEntityPartition.Result result =
            InteriorEntityPartition.Partition(
                new HashSet<uint>(),
                entries,
                FrustumPlanes.FromViewProjection(Matrix4x4.Identity),
                neverCullLandblockId: cameraLandblock);

        Assert.DoesNotContain(culled, result.Dynamics);
        Assert.Equal(camera, Assert.Single(result.Dynamics));
    }
}
