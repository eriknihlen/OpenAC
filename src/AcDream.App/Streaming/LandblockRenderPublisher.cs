using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Terrain;

namespace AcDream.App.Streaming;

public sealed class LandblockRenderPublication
{
    private readonly IReadOnlyDictionary<uint, LoadedCell> _visibilityCells;

    internal LandblockRenderPublication(
        object owner,
        LandblockBuild build,
        LandblockMeshData meshData,
        Vector3 origin,
        Dictionary<uint, LoadedCell> visibilityCells,
        Vector3 aabbMin,
        Vector3 aabbMax,
        BuildingRegistryPublication? buildingPublication,
        EnvCellLandblockPublication? envCellPublication)
    {
        Owner = owner;
        Build = build;
        MeshData = meshData;
        Origin = origin;
        _visibilityCells = new ReadOnlyDictionary<uint, LoadedCell>(visibilityCells);
        AabbMin = aabbMin;
        AabbMax = aabbMax;
        BuildingPublication = buildingPublication;
        EnvCellPublication = envCellPublication;
    }

    internal object Owner { get; }
    internal LandblockBuild Build { get; }
    internal LandblockMeshData MeshData { get; }
    internal Vector3 AabbMin { get; }
    internal Vector3 AabbMax { get; }
    internal BuildingRegistryPublication? BuildingPublication { get; }
    internal EnvCellLandblockPublication? EnvCellPublication { get; }
    internal bool TerrainCommitted { get; set; }
    internal bool VisibilityCommitted { get; set; }
    internal bool AabbCommitted { get; set; }
    internal bool BeginCommitted { get; set; }
    internal bool BuildingRegistryCommitted { get; set; }
    internal bool EnvCellsCommitted { get; set; }
    internal bool CompletionCommitted { get; set; }

    public uint LandblockId => Build.Landblock.LandblockId;
    public Vector3 Origin { get; }
    public IReadOnlyDictionary<uint, LoadedCell> VisibilityCells => _visibilityCells;
}

public readonly record struct LandblockRenderPublisherDiagnostics(
    long BeginCount,
    long CompleteCount,
    long TerrainPublishTicks,
    long BeginPublishTicks,
    long CompletePublishTicks,
    long EnvCellPreparationCount,
    long TerrainRemovalCount,
    long CellVisibilityRemovalCount,
    long BuildingRegistryRemovalCount,
    long EnvCellRemovalCount);

public sealed class LandblockRenderPublisher
{
    private readonly object _receiptOwner = new();
    private readonly Action<uint, LandblockMeshData, Vector3> _publishTerrain;
    private readonly Action<uint> _removeTerrain;
    private readonly CellVisibility _cellVisibility;
    private readonly GpuWorldState _worldState;
    private readonly Action<EnvCellLandblockBuild>? _commitEnvCells;
    private readonly IEnvCellLandblockPublisher? _envCellPublisher;
    private readonly Action<EnvCellLandblockBuild>? _prepareEnvCells;
    private readonly Action<uint>? _removeEnvCells;
    private readonly Dictionary<uint, BuildingRegistry> _buildingRegistries = new();
    private readonly WalkBuildingRegistry _walkBuildingRegistry = new();
    private readonly WalkLandscapeAssembler _walkLandscape = new();

    private long _beginCount;
    private long _completeCount;
    private long _terrainPublishTicks;
    private long _beginPublishTicks;
    private long _completePublishTicks;
    private long _envCellPreparationCount;
    private long _terrainRemovalCount;
    private long _cellVisibilityRemovalCount;
    private long _buildingRegistryRemovalCount;
    private long _envCellRemovalCount;

    public LandblockRenderPublisher(
        Action<uint, LandblockMeshData, Vector3> publishTerrain,
        Action<uint> removeTerrain,
        CellVisibility cellVisibility,
        GpuWorldState worldState,
        Action<EnvCellLandblockBuild>? commitEnvCells = null,
        Action<EnvCellLandblockBuild>? prepareEnvCells = null,
        Action<uint>? removeEnvCells = null,
        IEnvCellLandblockPublisher? envCellPublisher = null)
    {
        ArgumentNullException.ThrowIfNull(publishTerrain);
        ArgumentNullException.ThrowIfNull(removeTerrain);
        ArgumentNullException.ThrowIfNull(cellVisibility);
        ArgumentNullException.ThrowIfNull(worldState);

        _publishTerrain = publishTerrain;
        _removeTerrain = removeTerrain;
        _cellVisibility = cellVisibility;
        _worldState = worldState;
        _commitEnvCells = commitEnvCells;
        _envCellPublisher = envCellPublisher;
        if (_commitEnvCells is not null && _envCellPublisher is not null)
        {
            throw new ArgumentException(
                "Supply either the retained EnvCell publisher or the compatibility callback, not both.",
                nameof(envCellPublisher));
        }
        _prepareEnvCells = prepareEnvCells;
        _removeEnvCells = removeEnvCells;
    }

    public IReadOnlyCollection<BuildingRegistry> BuildingRegistries =>
        _buildingRegistries.Values;

    public WalkBuildingRegistry WalkBuildings => _walkBuildingRegistry;

    public WalkLandscapeAssembler WalkLandscape => _walkLandscape;

    public LandblockRenderPublisherDiagnostics Diagnostics => new(
        _beginCount,
        _completeCount,
        _terrainPublishTicks,
        _beginPublishTicks,
        _completePublishTicks,
        _envCellPreparationCount,
        _terrainRemovalCount,
        _cellVisibilityRemovalCount,
        _buildingRegistryRemovalCount,
        _envCellRemovalCount);

    internal bool MatchesState(GpuWorldState state) =>
        ReferenceEquals(_worldState, state);

    public LandblockRenderPublication BeginPublication(
        LandblockBuild build,
        LandblockMeshData meshData)
    {
        LandblockRenderPublication publication = PreparePublication(
            build,
            meshData);
        BeginPublication(publication);
        return publication;
    }

    public LandblockRenderPublication PreparePublication(
        LandblockBuild build,
        LandblockMeshData meshData)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(meshData);
        if (!build.Origin.IsSpecified)
            throw new ArgumentException(
                "Render publication requires the build's captured origin.",
                nameof(build));

        uint landblockId = build.Landblock.LandblockId;
        if (build.EnvCells is { } candidateEnvCells &&
            candidateEnvCells.LandblockId != landblockId)
        {
            throw new InvalidOperationException(
                $"EnvCell transaction 0x{candidateEnvCells.LandblockId:X8} was attached to " +
                $"landblock 0x{landblockId:X8}.");
        }

        Vector3 origin = ComputeOrigin(landblockId, build.Origin);

        var visibilityCells = new Dictionary<uint, LoadedCell>();
        if (build.EnvCells is { } envCells)
        {
            foreach (LoadedCell cell in envCells.VisibilityCells)
                visibilityCells[cell.CellId] = cell;
        }
        (Vector3 aabbMin, Vector3 aabbMax) = ComputeAabb(meshData, origin);
        BuildingRegistryPublication? buildingPublication =
            build.Landblock.PhysicsDats?.Info is { } info
                ? BuildingLoader.PreparePublication(
                    info,
                    landblockId,
                    visibilityCells)
                : null;
        EnvCellLandblockPublication? envCellPublication =
            build.EnvCells is { } retainedEnvCells
                ? _envCellPublisher?.PreparePublication(retainedEnvCells)
                : null;
        return new LandblockRenderPublication(
            _receiptOwner,
            build,
            meshData,
            origin,
            visibilityCells,
            aabbMin,
            aabbMax,
            buildingPublication,
            envCellPublication);
    }

    public void BeginPublication(LandblockRenderPublication publication)
    {
        ValidateReceipt(publication);
        while (!AdvanceBeginOne(publication))
        {
        }
    }

    internal bool AdvanceBeginOne(LandblockRenderPublication publication)
    {
        ValidateReceipt(publication);
        if (publication.BeginCommitted)
            return true;

        long started = Stopwatch.GetTimestamp();
        LandblockBuild build = publication.Build;
        uint landblockId = publication.LandblockId;
        if (!publication.TerrainCommitted)
        {
            long terrainStarted = Stopwatch.GetTimestamp();
            _publishTerrain(landblockId, publication.MeshData, publication.Origin);
            _terrainPublishTicks += Stopwatch.GetTimestamp() - terrainStarted;
            publication.TerrainCommitted = true;
        }
        else if (!publication.VisibilityCommitted)
        {
            if (build.EnvCells is { } envCells)
                _cellVisibility.CommitLandblock(landblockId, envCells.VisibilityCells);
            publication.VisibilityCommitted = true;
        }
        else if (!publication.AabbCommitted)
        {
            _worldState.SetLandblockAabb(
                landblockId,
                publication.AabbMin,
                publication.AabbMax);
            publication.AabbCommitted = true;
        }
        else
        {
            publication.BeginCommitted = true;
            _beginCount++;
        }

        _beginPublishTicks += Stopwatch.GetTimestamp() - started;
        return publication.BeginCommitted;
    }

    public void CompletePublication(LandblockRenderPublication publication)
    {
        ValidateReceipt(publication);
        if (!publication.BeginCommitted)
            throw new InvalidOperationException(
                "Render publication cannot complete before its prefix commits.");
        while (!AdvanceCompleteOne(publication))
        {
        }
    }

    internal bool AdvanceCompleteOne(LandblockRenderPublication publication)
    {
        ValidateReceipt(publication);
        if (!publication.BeginCommitted)
            throw new InvalidOperationException(
                "Render publication cannot complete before its prefix commits.");
        if (publication.CompletionCommitted)
            return true;

        long started = Stopwatch.GetTimestamp();
        LandblockBuild build = publication.Build;
        uint landblockId = publication.LandblockId;
        if (publication.BuildingPublication is { PreparationCommitted: false }
            buildingPublication)
        {
            BuildingLoader.AdvancePreparationOne(buildingPublication);
        }
        else if (!publication.BuildingRegistryCommitted)
        {
            if (publication.BuildingPublication is { } completedBuildings)
            {
                BuildingLoader.CommitPublication(completedBuildings);
                uint registryKey = landblockId & 0xFFFF0000u;
                _buildingRegistries[registryKey] =
                    completedBuildings.Registry;
            }
            // Terrain exists at both tiers; only near builds supply buildings.
            // Keep both publications in this retained receipt's existing step.
            if (build.EnvCells is { } walkEnvCells)
                _walkBuildingRegistry.Publish(landblockId, walkEnvCells.WalkBuildings);
            else
                _walkBuildingRegistry.Retire(landblockId);
            _walkLandscape.PublishLandblock(
                landblockId,
                build.TerrainBounds.MaxZ,
                build.TerrainBounds.MinZ,
                build.EnvCells is { } cells
                    ? cells.WalkBuildings
                    : Array.Empty<WalkBuildingFactory.Entry>());
            publication.BuildingRegistryCommitted = true;
        }
        else if (publication.EnvCellPublication is { } envCellPublication
            && !envCellPublication.PreparationCommitted)
        {
            _envCellPublisher?.AdvancePreparationOne(envCellPublication);
        }
        else if (!publication.EnvCellsCommitted)
        {
            if (publication.EnvCellPublication is { } retained)
            {
                (_envCellPublisher
                    ?? throw new InvalidOperationException(
                        "A retained EnvCell receipt has no owning publisher."))
                    .CommitPublication(retained);
            }
            else if (build.EnvCells is { } envCells)
            {
                _commitEnvCells?.Invoke(envCells);
            }
            publication.EnvCellsCommitted = true;
        }
        else
        {
            publication.CompletionCommitted = true;
            _completeCount++;
        }

        _completePublishTicks += Stopwatch.GetTimestamp() - started;
        return publication.CompletionCommitted;
    }

    public void PrepareAfterRenderPins(EnvCellLandblockBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _prepareEnvCells?.Invoke(build);
        _envCellPreparationCount++;
    }

    public void RemoveTerrain(uint landblockId)
    {
        _removeTerrain(landblockId);
        _walkLandscape.RetireLandblock(landblockId);
        _terrainRemovalCount++;
    }

    public void RemoveCellVisibility(uint landblockId)
    {
        _cellVisibility.RemoveLandblock((landblockId >> 16) & 0xFFFFu);
        _cellVisibilityRemovalCount++;
    }

    public void RemoveBuildingRegistry(uint landblockId)
    {
        _buildingRegistries.Remove(landblockId & 0xFFFF0000u);
        // Demotion retires buildings, not the retained far-tier terrain.
        _walkBuildingRegistry.Retire(landblockId);
        _walkLandscape.ClearBuildings(landblockId);
        _buildingRegistryRemovalCount++;
    }

    public void RemoveEnvironmentCells(uint landblockId)
    {
        _removeEnvCells?.Invoke(landblockId);
        _envCellRemovalCount++;
    }

    private static Vector3 ComputeOrigin(
        uint landblockId,
        LandblockBuildOrigin capturedOrigin)
    {
        int landblockX = (int)((landblockId >> 24) & 0xFFu);
        int landblockY = (int)((landblockId >> 16) & 0xFFu);
        return new Vector3(
            (landblockX - capturedOrigin.CenterX) * 192f,
            (landblockY - capturedOrigin.CenterY) * 192f,
            0f);
    }

    private static (Vector3 Min, Vector3 Max) ComputeAabb(
        LandblockMeshData meshData,
        Vector3 origin)
    {
        float zMin = float.MaxValue;
        float zMax = float.MinValue;
        foreach (TerrainVertex vertex in meshData.Vertices)
        {
            float z = vertex.Position.Z;
            if (z < zMin) zMin = z;
            if (z > zMax) zMax = z;
        }
        if (zMin == float.MaxValue)
        {
            zMin = 0f;
            zMax = 0f;
        }

        zMax += 50f;
        zMin -= 10f;
        return (
            new Vector3(origin.X, origin.Y, zMin),
            new Vector3(origin.X + 192f, origin.Y + 192f, zMax));
    }

    private void ValidateReceipt(LandblockRenderPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!ReferenceEquals(publication.Owner, _receiptOwner))
        {
            throw new ArgumentException(
                "The render publication receipt belongs to another publisher.",
                nameof(publication));
        }
    }
}
