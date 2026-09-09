using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Wb;

public class EnvCellLandblockBuildTests
{
    private const uint LandblockId = 0x8C04FFFFu;

    [Fact]
    public void Constructor_SnapshotsInputCollections()
    {
        var cells = new List<LoadedCell> { new() { CellId = 0x8C040100u } };
        var shells = new List<EnvCellShellPlacement> { Shell(0x8C040100u, 1) };

        var build = new EnvCellLandblockBuild(LandblockId, cells, shells);
        cells.Clear();
        shells.Clear();

        Assert.Single(build.VisibilityCells);
        Assert.Single(build.Shells);
    }

    [Fact]
    public void Constructor_RejectsCellFromAnotherLandblock()
    {
        var foreign = new LoadedCell { CellId = 0x00070100u };

        Assert.Throws<ArgumentException>(() =>
            new EnvCellLandblockBuild(LandblockId, new[] { foreign }, Array.Empty<EnvCellShellPlacement>()));
    }

    [Fact]
    public void CreateCommittedSnapshot_ContainsEveryShellInOneCompleteReplacement()
    {
        var shells = Enumerable.Range(0, 1123)
            .Select(index => Shell(0x8C040100u + (uint)index, (ulong)(index % 17 + 1)))
            .ToArray();
        var build = new EnvCellLandblockBuild(
            LandblockId,
            Array.Empty<LoadedCell>(),
            shells);

        var snapshot = EnvCellRenderer.CreateCommittedSnapshot(build);

        Assert.Equal(1123, snapshot.Instances.Count);
        Assert.Equal(1123, snapshot.EnvCellBounds.Count);
        Assert.Equal(1123, snapshot.BuildingPartGroups.Values.Sum(group => group.Count));
        Assert.True(snapshot.InstancesReady);
        Assert.True(snapshot.MeshDataReady);
        Assert.True(snapshot.GpuReady);
    }

    [Fact]
    public void IndependentBuilds_NeverSharePendingStorage()
    {
        var complete = new EnvCellLandblockBuild(
            LandblockId,
            Array.Empty<LoadedCell>(),
            Enumerable.Range(0, 1123)
                .Select(index => Shell(0x8C040100u + (uint)index, 1)));
        var second = new EnvCellLandblockBuild(
            LandblockId,
            Array.Empty<LoadedCell>(),
            Enumerable.Range(0, 11)
                .Select(index => Shell(0x8C040500u + (uint)index, 2)));

        var completeSnapshot = EnvCellRenderer.CreateCommittedSnapshot(complete);
        var secondSnapshot = EnvCellRenderer.CreateCommittedSnapshot(second);

        Assert.Equal(1123, completeSnapshot.Instances.Count);
        Assert.Equal(11, secondSnapshot.Instances.Count);
        Assert.Equal(1123, completeSnapshot.Instances.Count);
    }

    [Fact]
    public void Builder_CanPublishItsTransactionOnlyOnce()
    {
        var builder = new EnvCellLandblockBuildBuilder(LandblockId);

        _ = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Constructor_PrecomputesUniqueNonzeroWalkBuildingMeshDependencies()
    {
        WalkBuildingFactory.Entry[] buildings =
        [
            Building(0x01000010u, 0u, 0x01000020u, 0x01000010u),
            Building(0x01000020u, 0x01000030u),
        ];

        var build = new EnvCellLandblockBuild(
            LandblockId,
            Array.Empty<LoadedCell>(),
            Array.Empty<EnvCellShellPlacement>(),
            buildings);

        Assert.Equal<ulong>(
            [0x01000010ul, 0x01000020ul, 0x01000030ul],
            build.WalkBuildingMeshDependencies);
        buildings[0].Building.DegradeLevels = [];
        Assert.Equal<ulong>(
            [0x01000010ul, 0x01000020ul, 0x01000030ul],
            build.WalkBuildingMeshDependencies);
    }

    private static WalkBuildingFactory.Entry Building(params uint[] ids) =>
        new(
            new WalkBuilding
            {
                PositionCellId = 0x8C040001u,
                DegradeLevels = ids.Select(id =>
                    new WalkBuildingDegradeLevel(id, 1u, 0f, 10f, 20f, null)).ToArray(),
            },
            Matrix4x4.Identity,
            Matrix4x4.Identity);

    private static EnvCellShellPlacement Shell(uint cellId, ulong geometryId)
    {
        var min = new Vector3(cellId & 0xFF, 0, 0);
        var bounds = new WbBoundingBox(min, min + Vector3.One);
        return new EnvCellShellPlacement(
            cellId,
            geometryId,
            EnvironmentId: 1,
            CellStructure: 0,
            Surfaces: ImmutableArray.Create<ushort>(1),
            WorldPosition: min,
            Rotation: Quaternion.Identity,
            Transform: Matrix4x4.CreateTranslation(min),
            LocalBounds: new WbBoundingBox(Vector3.Zero, Vector3.One),
            WorldBounds: bounds);
    }
}
