using System.Numerics;
using System.Reflection;
using System.Collections.Immutable;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockRenderPublisherTests
{
    private const uint LandblockId = 0xA9B4FFFFu;
    private static readonly LandblockBuildOrigin CapturedOrigin = new(0xA8, 0xB3);

    [Fact]
    public void BeginPublication_PublishesTerrainVisibilityAndAabbFromCapturedOrigin()
    {
        var calls = new List<string>();
        var visibility = new CellVisibility();
        var state = new GpuWorldState();
        LoadedCell cell = VisibilityCell(0xA9B40100u);
        LandblockBuild build = Build(
            new EnvCellLandblockBuild(
                LandblockId,
                [cell],
                Array.Empty<EnvCellShellPlacement>()));
        LandblockMeshData mesh = MeshAtHeights(7f, 19f);
        var publisher = Publisher(
            calls,
            visibility,
            state,
            out _,
            out _,
            out _);

        LandblockRenderPublication receipt = publisher.BeginPublication(build, mesh);

        Assert.Equal(new Vector3(192f, 192f, 0f), receipt.Origin);
        Assert.Equal(["terrain:A9B4FFFF:192:192"], calls);
        Assert.True(visibility.TryGetCell(cell.CellId, out LoadedCell? committed));
        Assert.Same(cell, committed);
        Assert.Single(receipt.VisibilityCells);

        state.AddLandblock(build.Landblock);
        var bounds = Assert.Single(state.LandblockBounds);
        Assert.Equal(new Vector3(192f, 192f, -3f), bounds.AabbMin);
        Assert.Equal(new Vector3(384f, 384f, 69f), bounds.AabbMax);
    }

    [Fact]
    public void BeginPublication_VisibilityOnlyCellDoesNotRequireDrawableShell()
    {
        var visibility = new CellVisibility();
        LoadedCell cell = VisibilityCell(0xA9B40100u);
        LandblockBuild build = Build(new EnvCellLandblockBuild(
            LandblockId,
            [cell],
            Array.Empty<EnvCellShellPlacement>()));
        var publisher = Publisher(
            new List<string>(),
            visibility,
            new GpuWorldState(),
            out _,
            out _,
            out _);

        LandblockRenderPublication receipt = publisher.BeginPublication(
            build,
            EmptyMesh());
        publisher.CompletePublication(receipt);

        Assert.True(visibility.TryGetCell(cell.CellId, out _));
        Assert.Single(publisher.BuildingRegistries);
        Assert.Empty(Assert.Single(publisher.BuildingRegistries).All());
    }

    [Fact]
    public void BeginPublication_MismatchedEnvCellTransactionHasNoSideEffects()
    {
        var calls = new List<string>();
        var visibility = new CellVisibility();
        var state = new GpuWorldState();
        LoadedCell cell = VisibilityCell(0xAAB40100u);
        LandblockBuild build = Build(new EnvCellLandblockBuild(
            0xAAB4FFFFu,
            [cell],
            Array.Empty<EnvCellShellPlacement>()));
        var publisher = Publisher(
            calls,
            visibility,
            state,
            out _,
            out _,
            out _);

        Assert.Throws<InvalidOperationException>(() =>
            publisher.BeginPublication(build, EmptyMesh()));

        Assert.Empty(calls);
        Assert.False(visibility.TryGetCell(cell.CellId, out _));
        Assert.Null(state.DetachLandblock(LandblockId));
        Assert.Equal(0, publisher.Diagnostics.BeginCount);
    }

    [Fact]
    public void CompletePublication_CommitsBuildingThenEnvCellsAndIsIdempotent()
    {
        var calls = new List<string>();
        LandblockBuild build = Build(EmptyEnvCells());
        int envCommits = 0;
        LandblockRenderPublisher? publisher = null;
        publisher = new LandblockRenderPublisher(
            publishTerrain: (_, _, _) => calls.Add("terrain"),
            removeTerrain: _ => { },
            cellVisibility: new CellVisibility(),
            worldState: new GpuWorldState(),
            commitEnvCells: _ =>
            {
                Assert.Single(publisher!.BuildingRegistries);
                calls.Add("env-cells");
                envCommits++;
            });
        LandblockRenderPublication receipt = publisher.BeginPublication(build, EmptyMesh());

        publisher.CompletePublication(receipt);
        publisher.CompletePublication(receipt);

        Assert.Equal(["terrain", "env-cells"], calls);
        Assert.Equal(1, envCommits);
        Assert.Single(publisher.BuildingRegistries);
        Assert.Equal(1, publisher.Diagnostics.CompleteCount);
    }

    [Fact]
    public void CompletePublication_FailedEnvCommitRetriesWithoutDuplicatingRegistry()
    {
        int attempts = 0;
        LandblockBuild build = Build(EmptyEnvCells());
        var publisher = new LandblockRenderPublisher(
            publishTerrain: (_, _, _) => { },
            removeTerrain: _ => { },
            cellVisibility: new CellVisibility(),
            worldState: new GpuWorldState(),
            commitEnvCells: _ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("injected EnvCell failure");
            });
        LandblockRenderPublication receipt = publisher.BeginPublication(build, EmptyMesh());

        Assert.Throws<InvalidOperationException>(() =>
            publisher.CompletePublication(receipt));
        publisher.CompletePublication(receipt);

        Assert.Equal(2, attempts);
        Assert.Single(publisher.BuildingRegistries);
        Assert.Equal(1, publisher.Diagnostics.CompleteCount);
    }

    [Fact]
    public void CompletePublication_ForeignReceiptIsRejectedWithoutConsumingIt()
    {
        var first = Publisher(
            new List<string>(),
            new CellVisibility(),
            new GpuWorldState(),
            out int[] firstCommits,
            out _,
            out _);
        var second = Publisher(
            new List<string>(),
            new CellVisibility(),
            new GpuWorldState(),
            out int[] secondCommits,
            out _,
            out _);
        LandblockRenderPublication receipt = first.BeginPublication(
            Build(EmptyEnvCells()),
            EmptyMesh());

        Assert.Throws<ArgumentException>(() =>
            second.CompletePublication(receipt));

        Assert.Empty(second.BuildingRegistries);
        Assert.Equal(0, secondCommits[0]);
        Assert.Equal(0, second.Diagnostics.CompleteCount);

        first.CompletePublication(receipt);
        Assert.Single(first.BuildingRegistries);
        Assert.Equal(1, firstCommits[0]);
        Assert.Equal(1, first.Diagnostics.CompleteCount);
    }

    [Fact]
    public void RetainedEnvCellPublisher_AdvancesOneShellBeforeAtomicCommit()
    {
        var envPublisher = new RecordingEnvCellPublisher();
        EnvCellLandblockBuild envCells = new(
            LandblockId,
            Array.Empty<LoadedCell>(),
            [
                Shell(0xA9B40100u, 1),
                Shell(0xA9B40101u, 2),
                Shell(0xA9B40102u, 3),
            ]);
        var publisher = new LandblockRenderPublisher(
            publishTerrain: static (_, _, _) => { },
            removeTerrain: static _ => { },
            cellVisibility: new CellVisibility(),
            worldState: new GpuWorldState(),
            envCellPublisher: envPublisher);
        LandblockRenderPublication receipt = publisher.PreparePublication(
            Build(envCells),
            EmptyMesh());
        publisher.BeginPublication(receipt);

        Assert.False(publisher.AdvanceCompleteOne(receipt));
        Assert.Equal(0, envPublisher.AdvancedShells);
        Assert.Equal(0, envPublisher.CommitCount);
        for (int shell = 1; shell <= 3; shell++)
        {
            Assert.False(publisher.AdvanceCompleteOne(receipt));
            Assert.Equal(shell, envPublisher.AdvancedShells);
            Assert.Equal(0, envPublisher.CommitCount);
        }

        Assert.False(publisher.AdvanceCompleteOne(receipt));
        Assert.Equal(0, envPublisher.CommitCount);
        Assert.False(publisher.AdvanceCompleteOne(receipt));
        Assert.Equal(1, envPublisher.CommitCount);
        Assert.True(publisher.AdvanceCompleteOne(receipt));
        Assert.Equal(1, publisher.Diagnostics.CompleteCount);
    }

    [Fact]
    public void PrepareAndRetirementOperationsStayBalancedAtTheirOwnerBoundary()
    {
        var calls = new List<string>();
        var visibility = new CellVisibility();
        var publisher = Publisher(
            calls,
            visibility,
            new GpuWorldState(),
            out _,
            out int[] preparations,
            out int[] envRemovals);
        LoadedCell cell = VisibilityCell(0xA9B40100u);
        EnvCellLandblockBuild envCells = new(
            LandblockId,
            [cell],
            Array.Empty<EnvCellShellPlacement>());
        LandblockRenderPublication receipt = publisher.BeginPublication(
            Build(envCells),
            EmptyMesh());
        publisher.CompletePublication(receipt);

        publisher.PrepareAfterRenderPins(envCells);
        publisher.RemoveTerrain(LandblockId);
        publisher.RemoveCellVisibility(LandblockId);
        publisher.RemoveBuildingRegistry(LandblockId);
        publisher.RemoveEnvironmentCells(LandblockId);

        Assert.Equal(1, preparations[0]);
        Assert.Equal(1, envRemovals[0]);
        Assert.Equal(
            [
                "terrain:A9B4FFFF:192:192",
                "prepare",
                "remove-terrain:A9B4FFFF",
                "remove-env:A9B4FFFF",
            ],
            calls);
        Assert.False(visibility.TryGetCell(cell.CellId, out _));
        Assert.Empty(publisher.BuildingRegistries);
        LandblockRenderPublisherDiagnostics diagnostics = publisher.Diagnostics;
        Assert.Equal(1, diagnostics.EnvCellPreparationCount);
        Assert.Equal(1, diagnostics.TerrainRemovalCount);
        Assert.Equal(1, diagnostics.CellVisibilityRemovalCount);
        Assert.Equal(1, diagnostics.BuildingRegistryRemovalCount);
        Assert.Equal(1, diagnostics.EnvCellRemovalCount);
    }

    [Fact]
    public void PublisherSurface_HasNoDatResidencyPolicyOrLiveOriginDependency()
    {
        Type type = typeof(LandblockRenderPublisher);
        Type[] dependencies = type
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .Concat(type.GetConstructors().SelectMany(ctor =>
                ctor.GetParameters().Select(parameter => parameter.ParameterType)))
            .ToArray();

        Assert.DoesNotContain(dependencies, dependency =>
            dependency.FullName?.Contains("DatReader", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(dependencies, dependency =>
            dependency.FullName?.Contains("StreamingRegion", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(dependencies, dependency =>
            dependency.FullName?.Contains("LiveWorldOrigin", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void GameWindow_HasNoDirectRenderPublicationBodies()
    {
        IReadOnlyList<CompiledCall> calls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(TerrainModernRenderer)
                && call.Target.Name == nameof(TerrainModernRenderer.AddLandblockWithMesh));
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(CellVisibility)
                && call.Target.Name == nameof(CellVisibility.CommitLandblock));
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(EnvCellRenderer)
                && call.Target.Name == nameof(EnvCellRenderer.CommitLandblock));

        Assert.Null(typeof(GameWindow).GetMethod(
            "EnsureEnvCellMeshesAfterPin",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.DoesNotContain(
            typeof(GameWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_buildingRegistries");
    }

    private static LandblockRenderPublisher Publisher(
        List<string> calls,
        CellVisibility visibility,
        GpuWorldState state,
        out int[] envCommits,
        out int[] preparations,
        out int[] envRemovals)
    {
        envCommits = [0];
        preparations = [0];
        envRemovals = [0];
        int[] commitCounter = envCommits;
        int[] preparationCounter = preparations;
        int[] removalCounter = envRemovals;
        return new LandblockRenderPublisher(
            publishTerrain: (id, _, origin) => calls.Add(
                $"terrain:{id:X8}:{origin.X:0}:{origin.Y:0}"),
            removeTerrain: id => calls.Add($"remove-terrain:{id:X8}"),
            cellVisibility: visibility,
            worldState: state,
            commitEnvCells: _ => commitCounter[0]++,
            prepareEnvCells: _ =>
            {
                preparationCounter[0]++;
                calls.Add("prepare");
            },
            removeEnvCells: id =>
            {
                removalCounter[0]++;
                calls.Add($"remove-env:{id:X8}");
            });
    }

    private static LandblockBuild Build(EnvCellLandblockBuild envCells)
    {
        var physics = new PhysicsDatBundle(
            new LandBlockInfo(),
            new Dictionary<uint, EnvCell>(),
            new Dictionary<uint, DatReaderWriter.DBObjs.Environment>(),
            new Dictionary<uint, Setup>(),
            new Dictionary<uint, GfxObj>());
        return new LandblockBuild(
            new LoadedLandblock(
                LandblockId,
                new LandBlock(),
                Array.Empty<WorldEntity>(),
                physics),
            envCells,
            CapturedOrigin);
    }

    private static EnvCellLandblockBuild EmptyEnvCells() => new(
        LandblockId,
        Array.Empty<LoadedCell>(),
        Array.Empty<EnvCellShellPlacement>());

    private static LoadedCell VisibilityCell(uint cellId) => new()
    {
        CellId = cellId,
        WorldTransform = Matrix4x4.Identity,
        InverseWorldTransform = Matrix4x4.Identity,
    };

    private static EnvCellShellPlacement Shell(uint cellId, ulong geometryId) =>
        new(
            cellId,
            geometryId,
            EnvironmentId: 1u,
            CellStructure: 0,
            Surfaces: ImmutableArray<ushort>.Empty,
            WorldPosition: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Transform: Matrix4x4.Identity,
            LocalBounds: new WbBoundingBox(Vector3.Zero, Vector3.One),
            WorldBounds: new WbBoundingBox(Vector3.Zero, Vector3.One));

    private static LandblockMeshData MeshAtHeights(float first, float second) => new(
        [
            new TerrainVertex(new Vector3(0f, 0f, first), Vector3.UnitZ, 0, 0, 0, 0),
            new TerrainVertex(new Vector3(1f, 1f, second), Vector3.UnitZ, 0, 0, 0, 0),
        ],
        Array.Empty<uint>());

    private static LandblockMeshData EmptyMesh() =>
        new(Array.Empty<TerrainVertex>(), Array.Empty<uint>());

    private sealed class RecordingEnvCellPublisher :
        IEnvCellLandblockPublisher
    {
        private readonly object _owner = new();

        public int AdvancedShells { get; private set; }
        public int CommitCount { get; private set; }

        public EnvCellLandblockPublication PreparePublication(
            EnvCellLandblockBuild build) =>
            new(_owner, build);

        public bool AdvancePreparationOne(
            EnvCellLandblockPublication publication)
        {
            Assert.Same(_owner, publication.Owner);
            if (publication.PreparationCommitted)
                return true;
            if (publication.ShellCursor < publication.Build.Shells.Length)
            {
                publication.ShellCursor++;
                AdvancedShells++;
                return false;
            }

            publication.PreparationCommitted = true;
            return true;
        }

        public void CommitPublication(
            EnvCellLandblockPublication publication)
        {
            Assert.True(publication.PreparationCommitted);
            publication.PublicationCommitted = true;
            CommitCount++;
        }
    }
}
