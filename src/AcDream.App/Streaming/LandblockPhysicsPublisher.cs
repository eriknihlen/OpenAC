using System.Diagnostics;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.Types;

namespace AcDream.App.Streaming;

public sealed class LandblockPhysicsPublication : IDisposable
{
    internal LandblockPhysicsPublication(
        object owner,
        LandblockBuild build,
        Vector3 origin,
        uint currentCellId,
        BuildingInfo[] buildings,
        uint[] priorStaticOwnerIds,
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission collisionAdmission,
        PreparedLandblockCollisionGeneration preparedGeneration)
    {
        Owner = owner;
        Build = build;
        Origin = origin;
        CurrentCellId = currentCellId;
        Buildings = buildings;
        PriorStaticOwnerIds = priorStaticOwnerIds;
        Physics = physics;
        CollisionAdmission = collisionAdmission;
        PreparedGeneration = preparedGeneration;
    }

    internal object Owner { get; }
    internal LandblockBuild Build { get; }
    internal uint CurrentCellId { get; }
    internal BuildingInfo[] Buildings { get; }
    internal uint[] PriorStaticOwnerIds { get; }
    internal RuntimePhysicsState Physics { get; }
    internal RuntimeCollisionAdmission CollisionAdmission { get; }
    internal PreparedLandblockCollisionGeneration PreparedGeneration { get; }
    internal PhysicsDataCache StagingCache => PreparedGeneration.DataCache;
    internal PhysicsEngine StagingEngine => PreparedGeneration.Engine;
    internal SortedSet<uint> GfxObjectIdSet { get; } = new();
    internal uint[] GfxObjectIds { get; set; } = Array.Empty<uint>();
    internal int PreparationCursor { get; set; }
    internal bool PreparationCommitted { get; set; }
    internal bool PriorCacheRemoved { get; set; }
    internal TerrainSurface? TerrainSurface { get; set; }
    internal List<CellSurface> CellSurfaces { get; } = new();
    internal List<PortalPlane> PortalPlanes { get; } = new();
    internal uint CellCursor { get; set; }
    internal int BuildingCursor { get; set; }
    internal bool BaseCommitted { get; set; }
    internal int GfxCursor { get; set; }
    internal uint[] SetupObjectIds { get; set; } = Array.Empty<uint>();
    internal int SetupCursor { get; set; }
    internal int PriorStaticCursor { get; set; }
    internal int StaticCursor { get; set; }
    internal int BspOwnerCount { get; set; }
    internal int CylinderOwnerCount { get; set; }
    internal int NoCollisionCount { get; set; }
    internal int SceneryTried { get; set; }
    internal IReadOnlyList<uint>? RefloodOwnerIds { get; set; }
    internal int RefloodCursor { get; set; }
    internal bool RefloodCommitted { get; set; }
    internal bool SealCommitted { get; set; }
    internal bool EngineMutationCommitted { get; set; }
    internal bool RuntimeMutationPending { get; set; }
    internal bool BeginCommitted { get; set; }
    internal bool CompletionCommitted { get; set; }
    internal bool CancellationRequested { get; private set; }

    public void Dispose()
    {
        if (!TryCancel())
        {
            throw new InvalidOperationException(
                "Collision publication cancellation is waiting for exact placement acknowledgements and must remain retained.");
        }
    }

    internal bool TryCancel()
    {
        if (CompletionCommitted)
            return true;
        CancellationRequested = true;
        for (int poll = 0; poll < 2; poll++)
        {
            if (Physics.CancelCollisionGeneration(
                    CollisionAdmission,
                    PreparedGeneration))
            {
                return true;
            }
            if (Physics.CaptureOwnership()
                    .PendingCollisionPrefixProjectionCount != 0)
            {
                return false;
            }
        }
        return false;
    }

    public uint LandblockId => Build.Landblock.LandblockId;
    public Vector3 Origin { get; }
}

public readonly record struct LandblockPhysicsPublisherDiagnostics(
    long BeginCount,
    long CompleteCount,
    long BasePublishTicks,
    long GfxCacheTicks,
    long CompletePublishTicks,
    long CellSurfaceCount,
    long PortalPlaneCount,
    long BuildingCount,
    long StaticBspOwnerCount,
    long StaticCylinderOwnerCount,
    long RefloodCount,
    long DemotionCount,
    long FullRemovalCount);

public sealed class LandblockPhysicsPublisher
{
    private readonly object _receiptOwner = new();
    private readonly RuntimePhysicsState _physics;
    private readonly PhysicsEngine _physicsEngine;
    private readonly PhysicsDataCache _physicsDataCache;
    private readonly float[] _heightTable;

    private long _beginCount;
    private long _completeCount;
    private long _basePublishTicks;
    private long _gfxCacheTicks;
    private long _completePublishTicks;
    private long _cellSurfaceCount;
    private long _portalPlaneCount;
    private long _buildingCount;
    private long _staticBspOwnerCount;
    private long _staticCylinderOwnerCount;
    private long _refloodCount;
    private long _demotionCount;
    private long _fullRemovalCount;

    public LandblockPhysicsPublisher(
        RuntimePhysicsState physics,
        float[] heightTable)
    {
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(heightTable);
        if (heightTable.Length < 256)
            throw new ArgumentException(
                "The retail terrain height table must contain at least 256 entries.",
                nameof(heightTable));

        _physics = physics;
        _physicsEngine = physics.Engine;
        _physicsDataCache = physics.DataCache;
        _heightTable = (float[])heightTable.Clone();
    }

    public LandblockPhysicsPublisherDiagnostics Diagnostics => new(
        _beginCount,
        _completeCount,
        _basePublishTicks,
        _gfxCacheTicks,
        _completePublishTicks,
        _cellSurfaceCount,
        _portalPlaneCount,
        _buildingCount,
        _staticBspOwnerCount,
        _staticCylinderOwnerCount,
        _refloodCount,
        _demotionCount,
        _fullRemovalCount);

    public LandblockPhysicsPublication BeginPublication(
        LandblockRenderPublication renderPublication)
    {
        LandblockPhysicsPublication publication = PreparePublication(
            renderPublication);
        BeginPublication(publication);
        return publication;
    }

    public LandblockPhysicsPublication PreparePublication(
        LandblockRenderPublication renderPublication)
    {
        LandblockPhysicsPublication publication =
            CreatePublication(renderPublication);
        while (!AdvancePreparationOne(publication))
        {
        }
        return publication;
    }

    internal LandblockPhysicsPublication CreatePublication(
        LandblockRenderPublication renderPublication)
    {
        ArgumentNullException.ThrowIfNull(renderPublication);
        LandblockBuild build = renderPublication.Build;
        Vector3 origin = renderPublication.Origin;
        if (!IsFinite(origin))
            throw new ArgumentOutOfRangeException(nameof(renderPublication));

        PhysicsDatBundle datBundle =
            build.Landblock.PhysicsDats ?? PhysicsDatBundle.Empty;
        BuildingInfo[] buildings = datBundle.Info?.Buildings.ToArray()
            ?? Array.Empty<BuildingInfo>();
        RuntimeCollisionAdmission collisionAdmission =
            _physics.BeginCollisionAdmission(build.Landblock.LandblockId);
        PreparedLandblockCollisionGeneration? prepared = null;
        try
        {
            prepared = _physics.PrepareCollisionGeneration(collisionAdmission);
            var publication = new LandblockPhysicsPublication(
                _receiptOwner,
                build,
                origin,
                _physicsDataCache.CellGraph.CurrCell?.Id ?? 0u,
                buildings,
                _physicsEngine.ShadowObjects.CaptureStaticOwnersForLandblock(
                    build.Landblock.LandblockId),
                _physics,
                collisionAdmission,
                prepared);
            publication.SetupObjectIds = build.Collisions is { } collisions
                ? [.. collisions.SetupIds]
                : datBundle.Setups.Keys.Order().ToArray();
            publication.PreparedGeneration.SetAssetClosure(
                publication.GfxObjectIds,
                publication.SetupObjectIds);
            return publication;
        }
        catch (Exception publicationError)
        {
            if (!_physics.CancelCollisionGeneration(collisionAdmission, prepared))
            {
                throw new AggregateException(
                    "Collision preparation failed and its pre-engine cancellation did not converge.",
                    publicationError);
            }
            throw;
        }
    }

    internal bool AdvancePreparationOne(
        LandblockPhysicsPublication publication)
    {
        ValidateReceipt(publication);
        if (publication.PreparationCommitted)
            return true;

        if (!_physics.AdvanceCollisionGenerationPreparation(
                publication.CollisionAdmission,
                publication.PreparedGeneration).Completed)
        {
            return false;
        }

        IReadOnlyList<WorldEntity> entities =
            publication.Build.Landblock.Entities;
        if (publication.Build.Collisions is { } collisions)
        {
            publication.GfxObjectIds = [.. collisions.GfxObjIds];
            publication.PreparedGeneration.SetAssetClosure(
                publication.GfxObjectIds,
                publication.SetupObjectIds);
            publication.PreparationCursor = entities.Count;
            publication.PreparationCommitted = true;
            return true;
        }

        if (publication.PreparationCursor < entities.Count)
        {
            WorldEntity entity = entities[publication.PreparationCursor];
            for (int i = 0; i < entity.MeshRefs.Count; i++)
            {
                uint id = entity.MeshRefs[i].GfxObjId;
                if ((id & 0xFF000000u) == 0x01000000u)
                    publication.GfxObjectIdSet.Add(id);
            }
            publication.PreparationCursor++;
            return false;
        }

        publication.GfxObjectIds = publication.GfxObjectIdSet.ToArray();
        publication.PreparedGeneration.SetAssetClosure(
            publication.GfxObjectIds,
            publication.SetupObjectIds);
        publication.PreparationCommitted = true;
        return true;
    }

    public void BeginPublication(LandblockPhysicsPublication publication)
    {
        ValidateReceipt(publication);
        if (!publication.PreparationCommitted)
            throw new InvalidOperationException(
                "Physics publication cannot begin before preparation commits.");
        while (!AdvanceBeginOne(publication))
        {
        }
    }

    internal bool AdvanceBeginOne(LandblockPhysicsPublication publication)
    {
        ValidateReceipt(publication);
        if (publication.BeginCommitted)
            return true;

        long started = Stopwatch.GetTimestamp();
        LandblockBuild build = publication.Build;
        Vector3 origin = publication.Origin;
        LoadedLandblock landblock = build.Landblock;
        PhysicsDatBundle datBundle =
            landblock.PhysicsDats ?? PhysicsDatBundle.Empty;
        DatReaderWriter.DBObjs.LandBlockInfo? landblockInfo = datBundle.Info;

        if (!publication.PriorCacheRemoved)
        {
            publication.StagingCache.RemoveCellsForLandblock(landblock.LandblockId);
            publication.StagingCache.RemoveBuildingsForLandblock(landblock.LandblockId);
            publication.StagingCache.CellGraph.RemoveEnvCellsForLandblock(
                landblock.LandblockId);
            publication.PriorCacheRemoved = true;
        }
        else if (publication.TerrainSurface is null)
        {
            uint landblockX = (landblock.LandblockId >> 24) & 0xFFu;
            uint landblockY = (landblock.LandblockId >> 16) & 0xFFu;
            var terrainBytes = new byte[81];
            for (int i = 0; i < terrainBytes.Length; i++)
                terrainBytes[i] = (byte)(ushort)landblock.Heightmap.Terrain[i];
            publication.TerrainSurface = new TerrainSurface(
                landblock.Heightmap.Height,
                _heightTable,
                landblockX,
                landblockY,
                terrainBytes);
        }
        else if (landblockInfo is not null
            && publication.CellCursor < landblockInfo.NumCells)
        {
            PublishCell(publication, datBundle, publication.CellCursor);
            publication.CellCursor++;
        }
        else if (publication.BuildingCursor < publication.Buildings.Length)
        {
            PublishBuilding(
                landblock,
                datBundle,
                publication.StagingCache,
                publication.TerrainSurface,
                origin,
                publication.Buildings[publication.BuildingCursor]);
            publication.BuildingCursor++;
        }
        else if (!publication.BaseCommitted)
        {
            _physics.StageCollisionAssets(
                publication.CollisionAdmission,
                publication.PreparedGeneration,
                new RuntimeLandblockCollisionAssets(
                    landblock.LandblockId,
                    publication.TerrainSurface,
                    publication.CellSurfaces.ToArray(),
                    publication.PortalPlanes.ToArray(),
                    origin.X,
                    origin.Y,
                    publication.CurrentCellId));
            publication.BaseCommitted = true;
        }
        else
        {
            _cellSurfaceCount += publication.CellSurfaces.Count;
            _portalPlaneCount += publication.PortalPlanes.Count;
            _buildingCount += publication.Buildings.Length;
            publication.BeginCommitted = true;
            _beginCount++;
        }

        _basePublishTicks += Stopwatch.GetTimestamp() - started;
        return publication.BeginCommitted;
    }

    public bool CompletePublication(
        LandblockPhysicsPublication publication,
        Action<WorldEntity>? beforeStaticCollision = null)
    {
        ValidateReceipt(publication);
        if (!publication.BeginCommitted)
            throw new InvalidOperationException(
                "Physics publication cannot complete before its prefix commits.");
        while (!AdvanceCompleteOne(publication, beforeStaticCollision))
        {
            if (publication.RuntimeMutationPending)
            {
                if (!CanContinueMutationSynchronously())
                    return false;
                publication.RuntimeMutationPending = false;
            }
        }
        return true;
    }

    internal bool CanContinueMutationSynchronously()
    {
        RuntimePhysicsOwnershipSnapshot ownership = _physics.CaptureOwnership();
        return ownership.PendingCollisionPrefixProjectionCount == 0
            && ownership.PendingCollisionReportCount == 0
            && ownership.PendingCollisionSetPositionDispatchCount == 0
            && ownership.PendingShadowSetPositionDispatchCount == 0
            && !ownership.IsCollisionReportDispatching;
    }

    internal bool AdvanceCompleteOne(
        LandblockPhysicsPublication publication,
        Action<WorldEntity>? beforeStaticCollision = null)
    {
        ValidateReceipt(publication);
        if (publication.CancellationRequested)
            throw new InvalidOperationException(
                "A cancelled collision publication cannot resume.");
        if (!publication.BeginCommitted)
            throw new InvalidOperationException(
                "Physics publication cannot complete before its prefix commits.");
        if (publication.CompletionCommitted)
            return true;
        publication.RuntimeMutationPending = false;

        long started = Stopwatch.GetTimestamp();
        LoadedLandblock landblock = publication.Build.Landblock;
        PhysicsDatBundle datBundle =
            landblock.PhysicsDats ?? PhysicsDatBundle.Empty;

        if (publication.GfxCursor < publication.GfxObjectIds.Length)
        {
            long cacheStarted = Stopwatch.GetTimestamp();
            uint gfxObjectId = publication.GfxObjectIds[publication.GfxCursor];
            if (publication.Build.Collisions?.GfxObjs.TryGetValue(
                    gfxObjectId,
                    out FlatGfxObjCollisionAsset? prepared) == true)
            {
                publication.StagingCache.CacheGfxObj(gfxObjectId, prepared);
            }
            else if (datBundle.GfxObjs.TryGetValue(
                         gfxObjectId,
                         out var source))
            {
                publication.StagingCache.CacheGfxObj(gfxObjectId, source);
            }
            publication.GfxCursor++;
            _gfxCacheTicks += Stopwatch.GetTimestamp() - cacheStarted;
        }
        else if (publication.SetupCursor < publication.SetupObjectIds.Length)
        {
            uint setupId =
                publication.SetupObjectIds[publication.SetupCursor];
            if (publication.Build.Collisions?.Setups.TryGetValue(
                    setupId,
                    out FlatSetupCollision? prepared) == true)
            {
                publication.StagingCache.CacheSetup(setupId, prepared);
            }
            else if (datBundle.Setups.TryGetValue(setupId, out var source))
            {
                publication.StagingCache.CacheSetup(setupId, source);
            }
            publication.SetupCursor++;
        }
        else if (publication.PriorStaticCursor
            < publication.PriorStaticOwnerIds.Length)
        {
            publication.StagingEngine.ShadowObjects.DeregisterStaticOwnerForLandblock(
                publication.PriorStaticOwnerIds[
                    publication.PriorStaticCursor],
                landblock.LandblockId);
            publication.PriorStaticCursor++;
        }
        else if (publication.StaticCursor < landblock.Entities.Count)
        {
            WorldEntity entity = landblock.Entities[publication.StaticCursor];
            beforeStaticCollision?.Invoke(entity);
            PublishStaticEntity(publication, entity);
            publication.StaticCursor++;
        }
        else if (publication.RefloodOwnerIds is null)
        {
            RuntimeCollisionOwnerCaptureStep capture =
                _physics.AdvanceCollisionRetainedOwnerCapture(
                    publication.CollisionAdmission,
                    publication.PreparedGeneration);
            if (capture.Completed)
            {
                publication.RefloodOwnerIds =
                    publication.PreparedGeneration.RetainedOwnerIds;
            }
        }
        else if (publication.RefloodCursor
            < publication.RefloodOwnerIds.Count)
        {
            _physics.RefreshCollisionRetainedOwner(
                publication.CollisionAdmission,
                publication.PreparedGeneration,
                publication.RefloodOwnerIds[publication.RefloodCursor]);
            publication.RefloodCursor++;
        }
        else if (!publication.RefloodCommitted)
        {
            if (PhysicsDiagnostics.ProbeBuildingEnabled
                && publication.SceneryTried > 0)
            {
                Console.WriteLine(
                    $"lb 0x{landblock.LandblockId:X8}: scenery tried={publication.SceneryTried} " +
                    $"(outdoorNone={publication.NoCollisionCount})");
            }
            LogMissingSceneryBounds(landblock, publication.StagingCache);
            publication.RefloodCommitted = true;
        }
        else if (!publication.SealCommitted)
        {
            RuntimeCollisionSealStep seal =
                _physics.AdvanceCollisionGenerationSeal(
                publication.CollisionAdmission,
                publication.PreparedGeneration);
            if (seal.WorkUnits > 1)
            {
                throw new InvalidOperationException(
                    "Collision seal exceeded its one-unit publication budget.");
            }
            publication.SealCommitted = seal.Completed;
            if (seal.Restarted)
            {
                publication.RefloodOwnerIds = null;
                publication.RefloodCursor = 0;
                publication.RefloodCommitted = false;
            }
        }

        if (publication.SealCommitted && !publication.CompletionCommitted)
        {
            RuntimeCollisionGenerationCommit commit =
                _physics.CommitCollisionGeneration(
                    publication.CollisionAdmission,
                    publication.PreparedGeneration);
            publication.EngineMutationCommitted |= commit.EngineCommitted;
            if (!commit.Completed)
            {
                publication.RuntimeMutationPending = true;
                if (!commit.EngineCommitted)
                {
                    publication.SealCommitted = false;
                }
                _completePublishTicks += Stopwatch.GetTimestamp() - started;
                return false;
            }
            _refloodCount++;
            _staticBspOwnerCount += publication.BspOwnerCount;
            _staticCylinderOwnerCount += publication.CylinderOwnerCount;
            publication.CompletionCommitted = true;
            _completeCount++;
        }

        _completePublishTicks += Stopwatch.GetTimestamp() - started;
        return publication.CompletionCommitted;
    }

    public bool DemoteToTerrain(uint landblockId)
    {
        for (int poll = 0; poll < 2; poll++)
        {
            if (AdvanceDemotion(landblockId))
                return true;
            if (_physics.CaptureOwnership()
                    .PendingCollisionPrefixProjectionCount != 0)
            {
                return false;
            }
        }
        return false;
    }

    internal bool AdvanceDemotion(uint landblockId)
    {
        RuntimeCollisionMutationResult result =
            _physics.DemoteCollisionToTerrain(landblockId);
        if (!result.Completed)
            return false;
        _demotionCount++;
        return true;
    }

    public bool RemoveLandblock(uint landblockId)
    {
        for (int poll = 0; poll < 2; poll++)
        {
            if (AdvanceRemoval(landblockId))
                return true;
            if (_physics.CaptureOwnership()
                    .PendingCollisionPrefixProjectionCount != 0)
            {
                return false;
            }
        }
        return false;
    }

    internal bool AdvanceRemoval(uint landblockId)
    {
        RuntimeCollisionMutationResult result =
            _physics.WithdrawCollision(landblockId);
        if (!result.Completed)
            return false;
        _fullRemovalCount++;
        return true;
    }

    private void PublishCell(
        LandblockPhysicsPublication publication,
        PhysicsDatBundle datBundle,
        uint offset)
    {
        LoadedLandblock landblock = publication.Build.Landblock;
        uint envCellId =
            (landblock.LandblockId & 0xFFFF0000u) | (0x0100u + offset);
        if (!datBundle.EnvCells.TryGetValue(envCellId, out var envCell))
            return;

        if (publication.Build.Collisions is not { } collisions)
        {
            PublishGraphFixtureCell(
                publication,
                datBundle,
                envCellId,
                envCell);
            return;
        }

        if (!collisions.CellStructures.TryGetValue(
                envCellId,
                out FlatCellStructureCollisionAsset? preparedStructure)
            || !collisions.EnvCells.TryGetValue(
                envCellId,
                out FlatEnvCellTopology? preparedTopology))
        {
            return;
        }

        Quaternion rotation = envCell.Position.Orientation;
        Vector3 cellOriginWorld = envCell.Position.Origin + publication.Origin;
        Matrix4x4 physicsCellTransform =
            Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(cellOriginWorld);

        publication.StagingCache.CacheCellStruct(
            envCellId,
            envCell,
            physicsCellTransform,
            preparedStructure,
            preparedTopology);

        FlatPolygonTable physicsPolygons =
            preparedStructure.PhysicsBsp.PolygonTable;

        var portalPlanes = new List<PortalPlane>();
        FlatPolygonTable portalPolygons = preparedStructure.PortalPolygons;
        for (int portalIndex = 0;
            portalIndex < preparedTopology.Portals.Length;
            portalIndex++)
        {
            FlatEnvCellPortal portal = preparedTopology.Portals[portalIndex];
            if ((uint)portal.PolygonIndex
                >= (uint)portalPolygons.Polygons.Length)
            {
                continue;
            }
            FlatCollisionPolygon polygon =
                portalPolygons.Polygons[portal.PolygonIndex];
            if (polygon.VertexRange.Count < 3)
                continue;
            var portalVertices = new Vector3[polygon.VertexRange.Count];
            for (int index = 0; index < portalVertices.Length; index++)
            {
                Vector3 local = portalPolygons.Vertices[
                    polygon.VertexRange.Start + index];
                portalVertices[index] =
                    Vector3.Transform(local, rotation) + cellOriginWorld;
            }

            portalPlanes.Add(PortalPlane.FromVertices(
                portalVertices.AsSpan(),
                portal.OtherCellId,
                envCellId & 0xFFFFu,
                (ushort)portal.Flags));
        }

        publication.CellSurfaces.Add(new CellSurface(
            envCellId,
            physicsPolygons,
            rotation,
            cellOriginWorld));
        publication.PortalPlanes.AddRange(portalPlanes);
    }

    private void PublishGraphFixtureCell(
        LandblockPhysicsPublication publication,
        PhysicsDatBundle datBundle,
        uint envCellId,
        DatReaderWriter.DBObjs.EnvCell envCell)
    {
        if (envCell.EnvironmentId == 0
            || !datBundle.Environments.TryGetValue(
                0x0D000000u | envCell.EnvironmentId,
                out var environment)
            || !environment.Cells.TryGetValue(
                envCell.CellStructure,
                out var cellStruct))
        {
            return;
        }

        Quaternion rotation = envCell.Position.Orientation;
        Vector3 cellOriginWorld =
            envCell.Position.Origin + publication.Origin;
        Matrix4x4 physicsCellTransform =
            Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(cellOriginWorld);
        publication.StagingCache.CacheCellStruct(
            envCellId,
            envCell,
            cellStruct,
            physicsCellTransform);

        var worldVertices = new Dictionary<ushort, Vector3>(
            cellStruct.VertexArray.Vertices.Count);
        foreach ((ushort vertexId, var vertex) in
            cellStruct.VertexArray.Vertices)
        {
            worldVertices[vertexId] =
                Vector3.Transform(vertex.Origin, rotation)
                + cellOriginWorld;
        }

        var polygonVertexIds = new List<List<short>>(
            cellStruct.PhysicsPolygons.Count);
        foreach (var polygon in cellStruct.PhysicsPolygons.Values)
            polygonVertexIds.Add([.. polygon.VertexIds]);

        var portalPlanes = new List<PortalPlane>();
        foreach (var portal in envCell.CellPortals)
        {
            if (!cellStruct.Polygons.TryGetValue(
                    portal.PolygonId,
                    out var polygon)
                || polygon.VertexIds.Count < 3)
            {
                continue;
            }

            var portalVertices = new Vector3[polygon.VertexIds.Count];
            bool allFound = true;
            for (int index = 0; index < polygon.VertexIds.Count; index++)
            {
                if (!worldVertices.TryGetValue(
                        (ushort)polygon.VertexIds[index],
                        out portalVertices[index]))
                {
                    allFound = false;
                    break;
                }
            }
            if (!allFound)
                continue;

            portalPlanes.Add(PortalPlane.FromVertices(
                portalVertices,
                portal.OtherCellId,
                envCellId & 0xFFFFu,
                (ushort)portal.Flags));
        }

        publication.CellSurfaces.Add(new CellSurface(
            envCellId,
            worldVertices,
            polygonVertexIds));
        publication.PortalPlanes.AddRange(portalPlanes);
    }

    private void PublishBuilding(
        LoadedLandblock landblock,
        PhysicsDatBundle datBundle,
        PhysicsDataCache cache,
        TerrainSurface terrainSurface,
        Vector3 origin,
        BuildingInfo building)
    {
        uint landblockPrefix = landblock.LandblockId & 0xFFFF0000u;
        var portals = new List<BldPortalInfo>(building.Portals.Count);
        foreach (var portal in building.Portals)
        {
            portals.Add(new BldPortalInfo(
                otherCellId: landblockPrefix | (uint)portal.OtherCellId,
                otherPortalId: unchecked((short)portal.OtherPortalId),
                flags: (ushort)portal.Flags));
        }

        Vector3 buildingOrigin = building.Frame.Origin + origin;
        Matrix4x4 buildingTransform =
            Matrix4x4.CreateFromQuaternion(building.Frame.Orientation)
            * Matrix4x4.CreateTranslation(buildingOrigin);
        uint landcellLow = terrainSurface.ComputeOutdoorCellId(
            building.Frame.Origin.X,
            building.Frame.Origin.Y);
        uint landcellId = landblockPrefix | landcellLow;

        uint shellPartZero = building.ModelId;
        if ((shellPartZero & 0xFF000000u) == 0x02000000u)
        {
            datBundle.Setups.TryGetValue(
                building.ModelId,
                out var setup);
            shellPartZero = setup is not null && setup.Parts.Count > 0
                ? setup.Parts[0]
                : 0u;
        }
        cache.CacheBuilding(
            landcellId,
            portals,
            buildingTransform,
            modelId: shellPartZero);
    }

    private void PublishStaticEntity(
        LandblockPhysicsPublication publication,
        WorldEntity entity)
    {
        LoadedLandblock landblock = publication.Build.Landblock;
        if (entity.IsBuildingShell)
            return;

        int entityBspCount = 0;
        int entityCylinderCount = 0;
        uint sourcePrefix = entity.SourceGfxObjOrSetupId & 0xFF000000u;
        bool isOutdoorMesh =
            (entity.Id & 0x80000000u) != 0
            || (entity.Id < 0x40000000u
                && (sourcePrefix == 0x01000000u
                    || sourcePrefix == 0x02000000u));
        if (isOutdoorMesh)
            publication.SceneryTried++;

        IReadOnlyList<ShadowShape> bspShapes =
            ShadowShapeBuilder.FromLandblockBspParts(
                entity.MeshRefs,
                entity.IsBuildingShell,
                publication.StagingCache.GetGfxObj);

        IReadOnlyList<ShadowShape> partArray =
            ShadowShapeBuilder.FromStaticRenderParts(
                entity.MeshRefs,
                publication.StagingCache.GetGfxObj,
                publication.StagingCache.GetVisualBounds,
                out _);

        entityBspCount = bspShapes.Count;
        if (entityBspCount > 0)
        {
            publication.StagingEngine.ShadowObjects.RegisterMultiPart(
                entity.Id,
                entity.Position,
                entity.Rotation,
                bspShapes,
                0u,
                EntityCollisionFlags.None,
                publication.Origin.X,
                publication.Origin.Y,
                landblock.LandblockId,
                seedCellId: entity.ParentCellId ?? 0u,
                isStatic: true,
                partArray: partArray);
            LogMultipartRegistration(landblock, entity, bspShapes);
        }

        FlatSetupCollision? setup =
            publication.StagingCache.GetFlatSetup(entity.SourceGfxObjOrSetupId);
        if (setup is null
            && publication.StagingCache.GetSetup(
                entity.SourceGfxObjOrSetupId) is { } graphSetup)
        {
            setup = FlatCollisionAssetBuilder.FlattenSetup(graphSetup);
        }
        if (setup is not null && entityBspCount == 0)
        {
            float scale = entity.Scale > 0f ? entity.Scale : 1f;
            var setupShapes = new List<ShadowShape>();
            for (int cylinderIndex = 0;
                cylinderIndex < setup.Cylinders.Length;
                cylinderIndex++)
            {
                FlatCollisionCylinder cylinder =
                    setup.Cylinders[cylinderIndex];
                float radius = cylinder.Radius * scale;
                float baseHeight = cylinder.Height > 0f
                    ? cylinder.Height
                    : cylinder.Radius * 4f;
                float height = baseHeight * scale;
                if (radius <= 0f)
                    continue;

                Vector3 localOffset = cylinder.Origin * scale;
                setupShapes.Add(ShadowShape.Cylinder(
                    gfxObjId: entity.SourceGfxObjOrSetupId,
                    localPosition: localOffset,
                    localRotation: Quaternion.Identity,
                    scale: scale,
                    radius: radius,
                    cylHeight: height));
            }

            if (setup.Cylinders.Length == 0)
            {
                for (int sphereIndex = 0;
                    sphereIndex < setup.Spheres.Length;
                    sphereIndex++)
                {
                    FlatCollisionSphere sphere =
                        setup.Spheres[sphereIndex];
                    if (sphere.Radius <= 0f)
                        continue;

                    float radius = sphere.Radius * scale;
                    Vector3 localOffset = sphere.Origin * scale;
                    setupShapes.Add(
                    ShadowShape.Sphere(
                        gfxObjId: entity.SourceGfxObjOrSetupId,
                        localPosition: localOffset,
                        localRotation: Quaternion.Identity,
                        scale: scale,
                        radius: radius));
                }
            }

            if (setupShapes.Count > 0)
            {
                publication.StagingEngine.ShadowObjects.RegisterMultiPart(
                    entity.Id,
                    entity.Position,
                    entity.Rotation,
                    setupShapes,
                    0u,
                    EntityCollisionFlags.None,
                    publication.Origin.X,
                    publication.Origin.Y,
                    landblock.LandblockId,
                    seedCellId: entity.ParentCellId ?? 0u,
                    isStatic: true,
                    partArray: partArray);
                LogSetupRegistration(landblock, entity, setupShapes);
                entityCylinderCount = setupShapes.Count;
            }
        }

        if (entityBspCount == 0 && entityCylinderCount == 0
            && partArray.Count > 0)
        {
            publication.StagingEngine.ShadowObjects.RegisterMultiPart(
                entity.Id,
                entity.Position,
                entity.Rotation,
                Array.Empty<ShadowShape>(),
                0u,
                EntityCollisionFlags.None,
                publication.Origin.X,
                publication.Origin.Y,
                landblock.LandblockId,
                seedCellId: entity.ParentCellId ?? 0u,
                isStatic: true,
                partArray: partArray);
        }

        if (entityBspCount > 0)
            publication.BspOwnerCount++;
        if (entityCylinderCount > 0)
            publication.CylinderOwnerCount++;
        if (entityBspCount == 0 && entityCylinderCount == 0
            && (sourcePrefix == 0x01000000u
                || sourcePrefix == 0x02000000u))
        {
            publication.NoCollisionCount++;
        }
    }

    private static void LogSetupRegistration(
        LoadedLandblock landblock,
        WorldEntity entity,
        IReadOnlyList<ShadowShape> shapes)
    {
        if (!PhysicsDiagnostics.ProbeBuildingEnabled)
            return;

        for (int index = 0; index < shapes.Count; index++)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[entity-source] id=0x{entity.Id:X8} entityId=0x{entity.Id:X8} src=0x{entity.SourceGfxObjOrSetupId:X8} gfxObj=0x{shapes[index].GfxObjId:X8} lb=0x{landblock.LandblockId:X8} type={shapes[index].CollisionType} note=setup-part{index} state=0x{0u:X8} flags={EntityCollisionFlags.None}"));
        }
    }

    private static void LogMultipartRegistration(
        LoadedLandblock landblock,
        WorldEntity entity,
        IReadOnlyList<ShadowShape> shapes)
    {
        if (!PhysicsDiagnostics.ProbeBuildingEnabled)
            return;

        for (int index = 0; index < shapes.Count; index++)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[entity-source] id=0x{entity.Id:X8} entityId=0x{entity.Id:X8} src=0x{entity.SourceGfxObjOrSetupId:X8} gfxObj=0x{shapes[index].GfxObjId:X8} lb=0x{landblock.LandblockId:X8} type=BSP note=multipart-part{index} hasPhys=true state=0x{0u:X8} flags={EntityCollisionFlags.None}"));
        }
    }

    private static void LogMissingSceneryBounds(
        LoadedLandblock landblock,
        PhysicsDataCache cache)
    {
        if (!PhysicsDiagnostics.ProbeBuildingEnabled)
            return;

        int missingCount = 0;
        var samples = new List<uint>();
        foreach (WorldEntity entity in landblock.Entities)
        {
            if ((entity.Id & 0x80000000u) == 0)
                continue;

            bool hasBounds = false;
            foreach (MeshRef meshRef in entity.MeshRefs)
            {
                GfxObjVisualBounds? bounds =
                    cache.GetVisualBounds(meshRef.GfxObjId);
                if (bounds is not null && bounds.Radius > 0f)
                {
                    hasBounds = true;
                    break;
                }
            }
            if (hasBounds)
                continue;

            missingCount++;
            if (samples.Count < 3)
                samples.Add(entity.SourceGfxObjOrSetupId);
        }

        if (missingCount > 0)
        {
            string sampleText = string.Join(",", samples.Select(
                value => $"0x{value:X8}"));
            Console.WriteLine(
                $"  → {missingCount} scenery entities had no visual bounds cached. " +
                $"Samples: {sampleText}");
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    private void ValidateReceipt(LandblockPhysicsPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!ReferenceEquals(publication.Owner, _receiptOwner))
        {
            throw new ArgumentException(
                "The physics publication receipt belongs to another publisher.",
                nameof(publication));
        }
        if (!publication.CompletionCommitted
            && !publication.EngineMutationCommitted)
        {
            ObjectDisposedException.ThrowIf(
                publication.PreparedGeneration.IsDisposed,
                publication);
        }
    }
}
