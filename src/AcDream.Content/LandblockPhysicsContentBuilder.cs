using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Meshing;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.Content;

public static class LandblockPhysicsContentBuilder
{
    public readonly record struct StaticCollisionPublication(
        int BspOwnerCount,
        int SetupOwnerCount,
        int NoCollisionCount);

    public static IReadOnlyList<WorldEntity> HydrateStaticEntities(
        IDatReaderWriter dats,
        LoadedLandblock source,
        Vector3 worldOffset,
        bool includeVisualBounds = true)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(source);

        var hydrated = new List<WorldEntity>(source.Entities.Count);
        foreach (WorldEntity candidate in source.Entities)
        {
            var meshRefs = new List<MeshRef>();
            var bounds = new LocalBoundsAccumulator();
            uint sourceId = candidate.SourceGfxObjOrSetupId;
            if ((sourceId & 0xFF000000u) == 0x01000000u)
            {
                GfxObj? gfx = dats.Get<GfxObj>(sourceId);
                if (gfx is not null)
                {
                    if (includeVisualBounds
                        && GfxObjBounds.Get(gfx) is { } partBounds)
                    {
                        bounds.Add(
                            Matrix4x4.Identity,
                            partBounds);
                    }
                    meshRefs.Add(new MeshRef(
                        sourceId,
                        Matrix4x4.Identity));
                }
            }
            else if ((sourceId & 0xFF000000u) == 0x02000000u)
            {
                Setup? setup = dats.Get<Setup>(sourceId);
                if (setup is not null)
                {
                    foreach (MeshRef meshRef in SetupMesh.Flatten(setup))
                    {
                        GfxObj? gfx =
                            dats.Get<GfxObj>(meshRef.GfxObjId);
                        if (gfx is null)
                            continue;
                        if (includeVisualBounds
                            && GfxObjBounds.Get(gfx) is { } partBounds)
                        {
                            bounds.Add(
                                meshRef.PartTransform,
                                partBounds);
                        }
                        meshRefs.Add(meshRef);
                    }
                }
            }

            if (meshRefs.Count == 0)
                continue;

            var entity = new WorldEntity
            {
                Id = candidate.Id,
                SourceGfxObjOrSetupId = sourceId,
                Position = candidate.Position + worldOffset,
                Rotation = candidate.Rotation,
                MeshRefs = meshRefs,
                EffectCellId = candidate.EffectCellId,
                IsBuildingShell = candidate.IsBuildingShell,
                BuildingShellAnchorCellId =
                    candidate.BuildingShellAnchorCellId,
            };
            if (bounds.TryGet(out Vector3 min, out Vector3 max))
                entity.SetLocalBounds(min, max);
            hydrated.Add(entity);
        }
        return hydrated;
    }

    public static IReadOnlyList<WorldEntity> HydrateProceduralScenery(
        IDatReaderWriter dats,
        LoadedLandblock source,
        Vector3 worldOffset,
        ReadOnlySpan<float> heightTable,
        bool includeVisualBounds = true)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(source);
        if (heightTable.Length < 256)
        {
            throw new ArgumentException(
                "The retail terrain height table must contain at least 256 entries.",
                nameof(heightTable));
        }

        Region? region = dats.Get<Region>(0x13000000u);
        if (region is null)
            return [];

        HashSet<int>? buildingCells = null;
        LandBlockInfo? info = dats.Get<LandBlockInfo>(
            (source.LandblockId & 0xFFFF0000u) | 0xFFFEu);
        if (info is not null)
        {
            buildingCells = [];
            foreach (BuildingInfo building in info.Buildings)
            {
                int cellX = Math.Clamp(
                    (int)(building.Frame.Origin.X / 24f),
                    0,
                    8);
                int cellY = Math.Clamp(
                    (int)(building.Frame.Origin.Y / 24f),
                    0,
                    8);
                buildingCells.Add(cellX * 9 + cellY);
            }
        }

        IReadOnlyList<SceneryGenerator.ScenerySpawn> spawns =
            SceneryGenerator.Generate(
                dats,
                region,
                source.Heightmap,
                source.LandblockId,
                buildingCells);
        if (spawns.Count == 0)
            return [];

        var entities = new List<WorldEntity>(spawns.Count);
        uint landblockX =
            (source.LandblockId >> 24) & 0xFFu;
        uint landblockY =
            (source.LandblockId >> 16) & 0xFFu;
        uint sceneryCounter = 0u;
        foreach (SceneryGenerator.ScenerySpawn spawn in spawns)
        {
            var meshRefs = new List<MeshRef>();
            var bounds = new LocalBoundsAccumulator();
            Matrix4x4 scale =
                Matrix4x4.CreateScale(spawn.Scale);
            if ((spawn.ObjectId & 0xFF000000u) == 0x01000000u)
            {
                GfxObj? gfx = dats.Get<GfxObj>(spawn.ObjectId);
                if (gfx is not null)
                {
                    if (includeVisualBounds
                        && GfxObjBounds.Get(gfx) is { } partBounds)
                    {
                        bounds.Add(scale, partBounds);
                    }
                    meshRefs.Add(new MeshRef(spawn.ObjectId, scale));
                }
            }
            else if ((spawn.ObjectId & 0xFF000000u) == 0x02000000u)
            {
                Setup? setup = dats.Get<Setup>(spawn.ObjectId);
                if (setup is not null)
                {
                    foreach (MeshRef meshRef in SetupMesh.Flatten(setup))
                    {
                        GfxObj? gfx =
                            dats.Get<GfxObj>(meshRef.GfxObjId);
                        if (gfx is null)
                            continue;
                        Matrix4x4 partTransform =
                            meshRef.PartTransform * scale;
                        if (includeVisualBounds
                            && GfxObjBounds.Get(gfx) is { } partBounds)
                        {
                            bounds.Add(partTransform, partBounds);
                        }
                        meshRefs.Add(new MeshRef(
                            meshRef.GfxObjId,
                            partTransform));
                    }
                }
            }
            if (meshRefs.Count == 0)
                continue;

            float localX = spawn.LocalPosition.X;
            float localY = spawn.LocalPosition.Y;
            float groundZ = TerrainSurface.SampleZFromHeightmap(
                source.Heightmap.Height,
                heightTable,
                landblockX,
                landblockY,
                localX,
                localY);
            var entity = new WorldEntity
            {
                Id = ProceduralSceneryIdAllocator.Allocate(
                    landblockX,
                    landblockY,
                    ref sceneryCounter),
                SourceGfxObjOrSetupId = spawn.ObjectId,
                Position = new Vector3(
                    localX,
                    localY,
                    groundZ + spawn.LocalPosition.Z)
                    + worldOffset,
                Rotation = spawn.Rotation,
                MeshRefs = meshRefs,
                Scale = spawn.Scale,
                EffectCellId =
                    TerrainSurface.ComputeOutdoorCellId(
                        source.LandblockId,
                        localX,
                        localY),
            };
            if (bounds.TryGet(out Vector3 min, out Vector3 max))
                entity.SetLocalBounds(min, max);
            entities.Add(entity);
        }
        return entities;
    }

    public static PhysicsDatBundle BuildDatBundle(
        IDatReaderWriter dats,
        uint landblockId,
        IReadOnlyList<WorldEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(entities);

        var envCells = new Dictionary<uint, EnvCell>();
        var environments = new Dictionary<uint, DatEnvironment>();
        var setups = new Dictionary<uint, Setup>();
        var gfxObjs = new Dictionary<uint, GfxObj>();
        LandBlockInfo? info = dats.Get<LandBlockInfo>(
            (landblockId & 0xFFFF0000u) | 0xFFFEu);

        if (info is not null)
        {
            uint firstCellId =
                (landblockId & 0xFFFF0000u) | 0x0100u;
            for (uint offset = 0; offset < info.NumCells; offset++)
            {
                uint envCellId = firstCellId + offset;
                EnvCell? envCell = dats.Get<EnvCell>(envCellId);
                if (envCell is null)
                    continue;
                envCells[envCellId] = envCell;
                if (envCell.EnvironmentId == 0)
                    continue;
                uint environmentId =
                    0x0D000000u | envCell.EnvironmentId;
                if (!environments.ContainsKey(environmentId)
                    && dats.Get<DatEnvironment>(
                        environmentId) is { } environment)
                {
                    environments[environmentId] = environment;
                }
            }

            foreach (var building in info.Buildings)
            {
                uint setupId = building.ModelId;
                if ((setupId & 0xFF000000u) == 0x02000000u
                    && !setups.ContainsKey(setupId)
                    && dats.Get<Setup>(setupId) is { } setup)
                {
                    setups[setupId] = setup;
                }
            }
        }

        foreach (WorldEntity entity in entities)
        {
            uint sourceId = entity.SourceGfxObjOrSetupId;
            if ((sourceId & 0xFF000000u) == 0x02000000u
                && !setups.ContainsKey(sourceId)
                && dats.Get<Setup>(sourceId) is { } setup)
            {
                setups[sourceId] = setup;
            }

            foreach (MeshRef meshRef in entity.MeshRefs)
            {
                uint gfxObjId = meshRef.GfxObjId;
                if ((gfxObjId & 0xFF000000u) != 0x01000000u
                    || gfxObjs.ContainsKey(gfxObjId))
                {
                    continue;
                }
                if (dats.Get<GfxObj>(gfxObjId) is { } gfxObj)
                    gfxObjs[gfxObjId] = gfxObj;
            }
        }

        return new PhysicsDatBundle(
            info,
            envCells,
            environments,
            setups,
            gfxObjs);
    }

    public static LandblockCollisionBuild BuildPreparedCollisionClosure(
        IPreparedCollisionSource source,
        LoadedLandblock landblock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(landblock);

        PhysicsDatBundle dats =
            landblock.PhysicsDats ?? PhysicsDatBundle.Empty;
        ImmutableArray<uint> gfxObjIds =
            [.. dats.GfxObjs.Keys.Order()];
        ImmutableArray<uint> setupIds =
            [.. dats.Setups.Keys.Order()];
        ImmutableArray<uint> envCellIds =
            [.. dats.EnvCells.Keys.Order()];
        var gfxObjs = ImmutableDictionary.CreateBuilder<
            uint,
            FlatGfxObjCollisionAsset>();
        var setups = ImmutableDictionary.CreateBuilder<
            uint,
            FlatSetupCollision>();
        var cellStructures = ImmutableDictionary.CreateBuilder<
            uint,
            FlatCellStructureCollisionAsset>();
        var envCells = ImmutableDictionary.CreateBuilder<
            uint,
            FlatEnvCellTopology>();

        foreach (uint id in gfxObjIds)
        {
            gfxObjs.Add(
                id,
                Require(
                    source.ReadGfxObjCollision(id),
                    "GfxObj collision",
                    id));
        }
        foreach (uint id in setupIds)
        {
            setups.Add(
                id,
                Require(
                    source.ReadSetupCollision(id),
                    "Setup collision",
                    id));
        }
        foreach (uint id in envCellIds)
        {
            cellStructures.Add(
                id,
                Require(
                    source.ReadCellStructureCollision(id),
                    "CellStruct collision",
                    id));
            envCells.Add(
                id,
                Require(
                    source.ReadEnvCellTopology(id),
                    "EnvCell topology",
                    id));
        }

        return new LandblockCollisionBuild(
            gfxObjs.ToImmutable(),
            setups.ToImmutable(),
            cellStructures.ToImmutable(),
            envCells.ToImmutable(),
            gfxObjIds,
            setupIds,
            envCellIds);
    }

    public static TerrainSurface BuildTerrainSurface(
        LoadedLandblock landblock,
        ReadOnlySpan<float> heightTable)
    {
        ArgumentNullException.ThrowIfNull(landblock);
        if (heightTable.Length < 256)
        {
            throw new ArgumentException(
                "The retail terrain height table must contain at least 256 entries.",
                nameof(heightTable));
        }

        uint landblockX = (landblock.LandblockId >> 24) & 0xFFu;
        uint landblockY = (landblock.LandblockId >> 16) & 0xFFu;
        var terrainBytes = new byte[81];
        for (int index = 0; index < terrainBytes.Length; index++)
        {
            terrainBytes[index] =
                (byte)(ushort)landblock.Heightmap.Terrain[index];
        }
        return new TerrainSurface(
            landblock.Heightmap.Height,
            heightTable,
            landblockX,
            landblockY,
            terrainBytes);
    }

    public static void PublishPreparedCells(
        PhysicsDataCache cache,
        LoadedLandblock landblock,
        LandblockCollisionBuild collisions,
        Vector3 origin,
        ICollection<CellSurface> cellSurfaces,
        ICollection<PortalPlane> portalPlanes)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(landblock);
        ArgumentNullException.ThrowIfNull(collisions);
        ArgumentNullException.ThrowIfNull(cellSurfaces);
        ArgumentNullException.ThrowIfNull(portalPlanes);

        PhysicsDatBundle dats =
            landblock.PhysicsDats ?? PhysicsDatBundle.Empty;
        uint count = dats.Info?.NumCells ?? 0u;
        for (uint offset = 0; offset < count; offset++)
        {
            uint envCellId =
                (landblock.LandblockId & 0xFFFF0000u)
                | (0x0100u + offset);
            if (!dats.EnvCells.TryGetValue(
                    envCellId,
                    out EnvCell? envCell)
                || !collisions.CellStructures.TryGetValue(
                    envCellId,
                    out FlatCellStructureCollisionAsset? structure)
                || !collisions.EnvCells.TryGetValue(
                    envCellId,
                    out FlatEnvCellTopology? topology))
            {
                continue;
            }

            Quaternion rotation = envCell.Position.Orientation;
            Vector3 cellOriginWorld =
                envCell.Position.Origin + origin;
            Matrix4x4 transform =
                Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(cellOriginWorld);
            cache.CacheCellStruct(
                envCellId,
                envCell,
                transform,
                structure,
                topology);

            FlatPolygonTable portalPolygons =
                structure.PortalPolygons;
            for (int portalIndex = 0;
                portalIndex < topology.Portals.Length;
                portalIndex++)
            {
                FlatEnvCellPortal portal =
                    topology.Portals[portalIndex];
                if ((uint)portal.PolygonIndex
                    >= (uint)portalPolygons.Polygons.Length)
                {
                    continue;
                }
                FlatCollisionPolygon polygon =
                    portalPolygons.Polygons[portal.PolygonIndex];
                if (polygon.VertexRange.Count < 3)
                    continue;

                var vertices =
                    new Vector3[polygon.VertexRange.Count];
                for (int index = 0; index < vertices.Length; index++)
                {
                    Vector3 local = portalPolygons.Vertices[
                        polygon.VertexRange.Start + index];
                    vertices[index] =
                        Vector3.Transform(local, rotation)
                        + cellOriginWorld;
                }
                portalPlanes.Add(PortalPlane.FromVertices(
                    vertices.AsSpan(),
                    portal.OtherCellId,
                    envCellId & 0xFFFFu,
                    portal.Flags));
            }

            cellSurfaces.Add(new CellSurface(
                envCellId,
                structure.PhysicsBsp.PolygonTable,
                rotation,
                cellOriginWorld));
        }
    }

    public static void CacheBuildings(
        PhysicsDataCache cache,
        LoadedLandblock landblock,
        TerrainSurface terrain,
        Vector3 origin)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(landblock);
        ArgumentNullException.ThrowIfNull(terrain);
        PhysicsDatBundle dats =
            landblock.PhysicsDats ?? PhysicsDatBundle.Empty;
        if (dats.Info is not { } info)
            return;

        uint prefix = landblock.LandblockId & 0xFFFF0000u;
        foreach (BuildingInfo building in info.Buildings)
        {
            var portals =
                new List<BldPortalInfo>(building.Portals.Count);
            foreach (var portal in building.Portals)
            {
                portals.Add(new BldPortalInfo(
                    prefix | (uint)portal.OtherCellId,
                    unchecked((short)portal.OtherPortalId),
                    (ushort)portal.Flags));
            }

            Vector3 buildingOrigin = building.Frame.Origin + origin;
            Matrix4x4 transform =
                Matrix4x4.CreateFromQuaternion(
                    building.Frame.Orientation)
                * Matrix4x4.CreateTranslation(buildingOrigin);
            uint landcellId = prefix
                | terrain.ComputeOutdoorCellId(
                    building.Frame.Origin.X,
                    building.Frame.Origin.Y);
            uint shellPartZero = building.ModelId;
            if ((shellPartZero & 0xFF000000u) == 0x02000000u)
            {
                dats.Setups.TryGetValue(
                    building.ModelId,
                    out Setup? setup);
                shellPartZero =
                    setup is not null && setup.Parts.Count > 0
                        ? setup.Parts[0]
                        : 0u;
            }
            cache.CacheBuilding(
                landcellId,
                portals,
                transform,
                shellPartZero);
        }
    }

    public static void CachePreparedObjects(
        PhysicsDataCache cache,
        LandblockCollisionBuild collisions)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(collisions);
        foreach ((uint id, FlatGfxObjCollisionAsset asset) in
            collisions.GfxObjs)
        {
            cache.CacheGfxObj(id, asset);
        }
        foreach ((uint id, FlatSetupCollision setup) in
            collisions.Setups)
        {
            cache.CacheSetup(id, setup);
        }
    }

    public static StaticCollisionPublication PublishStaticCollision(
        PhysicsEngine engine,
        PhysicsDataCache cache,
        LoadedLandblock landblock,
        LandblockCollisionBuild collisions,
        Vector3 origin)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(landblock);
        ArgumentNullException.ThrowIfNull(collisions);

        int bspOwners = 0;
        int setupOwners = 0;
        int noCollision = 0;
        foreach (WorldEntity entity in landblock.Entities)
        {
            if (entity.IsBuildingShell)
                continue;

            IReadOnlyList<ShadowShape> bspShapes =
                ShadowShapeBuilder.FromLandblockBspParts(
                    entity.MeshRefs,
                    entity.IsBuildingShell,
                    cache.GetGfxObj);

            IReadOnlyList<ShadowShape> partArray =
                ShadowShapeBuilder.FromStaticRenderParts(
                    entity.MeshRefs,
                    cache.GetGfxObj,
                    cache.GetVisualBounds,
                    out _);

            if (bspShapes.Count > 0)
            {
                engine.ShadowObjects.RegisterMultiPart(
                    entity.Id,
                    entity.Position,
                    entity.Rotation,
                    bspShapes,
                    0u,
                    EntityCollisionFlags.None,
                    worldOffsetX: origin.X,
                    worldOffsetY: origin.Y,
                    landblockId: landblock.LandblockId,
                    seedCellId: entity.ParentCellId ?? 0u,
                    isStatic: true,
                    partArray: partArray);
                bspOwners++;
                continue;
            }

            FlatSetupCollision? setup =
                cache.GetFlatSetup(
                    entity.SourceGfxObjOrSetupId);
            if (setup is null)
            {
                RegisterRenderOnlyStatic(
                    engine, entity, partArray, landblock, origin);
                noCollision++;
                continue;
            }

            float scale = entity.Scale > 0f ? entity.Scale : 1f;
            var setupShapes = new List<ShadowShape>();
            for (int index = 0;
                index < setup.Cylinders.Length;
                index++)
            {
                FlatCollisionCylinder cylinder =
                    setup.Cylinders[index];
                float radius = cylinder.Radius * scale;
                float height = (cylinder.Height > 0f
                    ? cylinder.Height
                    : cylinder.Radius * 4f) * scale;
                if (radius <= 0f)
                    continue;
                setupShapes.Add(ShadowShape.Cylinder(
                    entity.SourceGfxObjOrSetupId,
                    cylinder.Origin * scale,
                    Quaternion.Identity,
                    scale,
                    radius,
                    height));
            }

            if (setup.Cylinders.Length == 0)
            {
                for (int index = 0;
                    index < setup.Spheres.Length;
                    index++)
                {
                    FlatCollisionSphere sphere = setup.Spheres[index];
                    if (sphere.Radius <= 0f)
                        continue;
                    float radius = sphere.Radius * scale;
                    Vector3 localOffset = sphere.Origin * scale;
                    setupShapes.Add(
                    ShadowShape.Sphere(
                        entity.SourceGfxObjOrSetupId,
                        localOffset,
                        Quaternion.Identity,
                        scale,
                        radius));
                }
            }

            if (setupShapes.Count == 0)
            {
                RegisterRenderOnlyStatic(
                    engine, entity, partArray, landblock, origin);
                noCollision++;
                continue;
            }
            engine.ShadowObjects.RegisterMultiPart(
                entity.Id,
                entity.Position,
                entity.Rotation,
                setupShapes,
                0u,
                EntityCollisionFlags.None,
                worldOffsetX: origin.X,
                worldOffsetY: origin.Y,
                landblockId: landblock.LandblockId,
                seedCellId: entity.ParentCellId ?? 0u,
                isStatic: true,
                partArray: partArray);
            setupOwners++;
        }

        uint[] reflood =
            engine.ShadowObjects.CaptureRefloodOwnersForLandblock(
                landblock.LandblockId);
        foreach (uint ownerId in reflood)
        {
            engine.ShadowObjects.RefloodOwnerForLandblock(
                ownerId,
                landblock.LandblockId);
        }
        return new StaticCollisionPublication(
            bspOwners,
            setupOwners,
            noCollision);
    }

    private static void RegisterRenderOnlyStatic(
        PhysicsEngine engine,
        WorldEntity entity,
        IReadOnlyList<ShadowShape> partArray,
        LoadedLandblock landblock,
        Vector3 origin)
    {
        if (partArray.Count == 0)
            return;
        engine.ShadowObjects.RegisterMultiPart(
            entity.Id,
            entity.Position,
            entity.Rotation,
            Array.Empty<ShadowShape>(),
            0u,
            EntityCollisionFlags.None,
            worldOffsetX: origin.X,
            worldOffsetY: origin.Y,
            landblockId: landblock.LandblockId,
            seedCellId: entity.ParentCellId ?? 0u,
            isStatic: true,
            partArray: partArray);
    }

    private static T Require<T>(
        PreparedCollisionReadResult<T> result,
        string kind,
        uint id)
        where T : class
    {
        if (result.Status == PreparedAssetReadStatus.Loaded
            && result.Data is not null)
        {
            return result.Data;
        }

        throw new InvalidDataException(
            $"{kind} 0x{id:X8} is {result.Status}. "
            + "The complete near-tier generation cannot be published.");
    }
}
