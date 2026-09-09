using DatReaderWriter;
using AcDream.Content;

namespace AcDream.App.Streaming;

public sealed class LandblockBuildFactory
{
    private readonly IDatReaderWriter _dats;
    private readonly IPreparedCollisionSource _preparedCollisions;
    private readonly object _datLock;
    private readonly float[] _heightTable;
    private readonly bool _dumpSceneryZ;

    public LandblockBuildFactory(
        IDatReaderWriter dats,
        IPreparedCollisionSource preparedCollisions,
        object datLock,
        float[] heightTable,
        bool dumpSceneryZ = false)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _preparedCollisions = preparedCollisions ??
            throw new ArgumentNullException(nameof(preparedCollisions));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
        ArgumentNullException.ThrowIfNull(heightTable);
        if (heightTable.Length < 256)
            throw new ArgumentException(
                "The retail terrain height table must contain at least 256 entries.",
                nameof(heightTable));
        _heightTable = (float[])heightTable.Clone();
        _dumpSceneryZ = dumpSceneryZ;
    }


    public AcDream.App.Streaming.LandblockBuild? Build(
        AcDream.App.Streaming.LandblockBuildRequest request)
    {
        if (!request.Origin.IsSpecified)
            throw new ArgumentException(
                "A landblock build requires a specified captured origin.",
                nameof(request));

        LandblockBuild? build;
        if (AcDream.Core.Physics.PhysicsDiagnostics.ProbeTeleportEnabled)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            lock (_datLock)
            {
                long waitedMs = sw.ElapsedMilliseconds;
                sw.Restart();
                build = BuildLocked(request);
                AcDream.Core.Physics.PhysicsDiagnostics.LogTeleport(
                    "BUILD", request.LandblockId,
                    $"waited={waitedMs}ms held={sw.ElapsedMilliseconds}ms kind={request.Kind}");
            }
        }
        else
        {
            lock (_datLock)
                build = BuildLocked(request);
        }

        if (build is null ||
            request.Kind == LandblockStreamJobKind.LoadFar)
        {
            return build;
        }

        AcDream.Core.World.LandblockCollisionBuild collisions =
            LandblockPhysicsContentBuilder.BuildPreparedCollisionClosure(
                _preparedCollisions,
                build.Landblock);
        AcDream.Core.World.PhysicsDatBundle publicationDats =
            (build.Landblock.PhysicsDats
                ?? AcDream.Core.World.PhysicsDatBundle.Empty)
            .WithoutCollisionGraphs();
        return build with
        {
            Landblock = build.Landblock with
            {
                PhysicsDats = publicationDats,
            },
            Collisions = collisions,
        };
    }

    private AcDream.App.Streaming.LandblockBuild? BuildLocked(
        AcDream.App.Streaming.LandblockBuildRequest request)
    {
        uint landblockId = request.LandblockId;

        if (request.Kind == AcDream.App.Streaming.LandblockStreamJobKind.LoadFar)
        {
            var heightmapOnly = _dats.Get<DatReaderWriter.DBObjs.LandBlock>(landblockId);
            if (heightmapOnly is null) return null;
            (float maxZ, float minZ) = ComputeWalkZSlab(heightmapOnly.Height);
            return new AcDream.App.Streaming.LandblockBuild(
                new AcDream.Core.World.LoadedLandblock(
                    landblockId,
                    heightmapOnly,
                    System.Array.Empty<AcDream.Core.World.WorldEntity>(),
                    AcDream.Core.World.PhysicsDatBundle.Empty),
                Origin: request.Origin,
                TerrainBounds: new(maxZ, minZ));
        }

        var baseLoaded = AcDream.Core.World.LandblockLoader.Load(_dats, landblockId);
        if (baseLoaded is null) return null;

        int lbX = (int)((landblockId >> 24) & 0xFFu);
        int lbY = (int)((landblockId >> 16) & 0xFFu);
        var worldOffset = new System.Numerics.Vector3(
            (lbX - request.Origin.CenterX) * 192f,
            (lbY - request.Origin.CenterY) * 192f,
            0f);

        IReadOnlyList<AcDream.Core.World.WorldEntity> hydrated =
            LandblockPhysicsContentBuilder.HydrateStaticEntities(
                _dats,
                baseLoaded,
                worldOffset);

        // Task 8: merge stabs + scenery + interior into one entity list.
        var merged = new List<AcDream.Core.World.WorldEntity>(hydrated);
        merged.AddRange(
            _dumpSceneryZ
                ? BuildSceneryEntitiesForStreaming(
                    baseLoaded,
                    lbX,
                    lbY,
                    request.Origin)
                : LandblockPhysicsContentBuilder
                    .HydrateProceduralScenery(
                        _dats,
                        baseLoaded,
                        worldOffset,
                        _heightTable));
        var envCellBuild = new AcDream.App.Rendering.Wb.EnvCellLandblockBuildBuilder(landblockId);
        (float walkMaxZ, float walkMinZ) = ComputeWalkZSlab(baseLoaded.Heightmap.Height);
        envCellBuild.SetWalkZSlab(walkMaxZ, walkMinZ);
        merged.AddRange(BuildInteriorEntitiesForStreaming(
            landblockId,
            lbX,
            lbY,
            request.Origin,
            envCellBuild));

        var physicsDats = LandblockPhysicsContentBuilder.BuildDatBundle(
            _dats,
            landblockId,
            merged);
        var completedEnvCells = envCellBuild.Build();
        return new AcDream.App.Streaming.LandblockBuild(
            new AcDream.Core.World.LoadedLandblock(
                baseLoaded.LandblockId,
                baseLoaded.Heightmap,
                merged,
                physicsDats),
            completedEnvCells,
            request.Origin,
            TerrainBounds: new(walkMaxZ, walkMinZ));
    }
    private List<AcDream.Core.World.WorldEntity> BuildSceneryEntitiesForStreaming(
        AcDream.Core.World.LoadedLandblock lb,
        int lbX,
        int lbY,
        AcDream.App.Streaming.LandblockBuildOrigin origin)
    {
        var result = new List<AcDream.Core.World.WorldEntity>();

        var region = _dats.Get<DatReaderWriter.DBObjs.Region>(0x13000000u);
        if (region is null) return result;

        HashSet<int>? buildingCells = null;
        var lbInfo = _dats.Get<DatReaderWriter.DBObjs.LandBlockInfo>(
            (lb.LandblockId & 0xFFFF0000u) | 0xFFFEu);
        if (lbInfo is not null)
        {
            buildingCells = new HashSet<int>();
            foreach (var bldg in lbInfo.Buildings)
            {
                int cx = Math.Clamp((int)(bldg.Frame.Origin.X / 24f), 0, 8);
                int cy = Math.Clamp((int)(bldg.Frame.Origin.Y / 24f), 0, 8);
                buildingCells.Add(cx * 9 + cy);
            }
        }

        var spawns = AcDream.Core.World.SceneryGenerator.Generate(
            _dats, region, lb.Heightmap, lb.LandblockId, buildingCells, _heightTable);
        if (spawns.Count == 0) return result;

        var lbOffset = new System.Numerics.Vector3(
            (lbX - origin.CenterX) * 192f,
            (lbY - origin.CenterY) * 192f,
            0f);

        uint lbXByte = (lb.LandblockId >> 24) & 0xFFu;
        uint lbYByte = (lb.LandblockId >> 16) & 0xFFu;
        uint sceneryCounter = 0;

        foreach (var spawn in spawns)
        {
            // Resolve the object to a mesh (same GfxObj/Setup logic as Stabs).
            // Scale is baked into the root transform by wrapping each part's
            // transform with a scale matrix.
            var meshRefs = new List<AcDream.Core.World.MeshRef>();
            var sceneryBounds = new AcDream.Core.Meshing.LocalBoundsAccumulator();
            var scaleMat = System.Numerics.Matrix4x4.CreateScale(spawn.Scale);

            if ((spawn.ObjectId & 0xFF000000u) == 0x01000000u)
            {
                var gfx = _dats.Get<DatReaderWriter.DBObjs.GfxObj>(spawn.ObjectId);
                if (gfx is not null)
                {
                    var pb = AcDream.Core.Meshing.GfxObjBounds.Get(gfx);
                    if (pb is not null) sceneryBounds.Add(scaleMat, pb.Value);
                    meshRefs.Add(new AcDream.Core.World.MeshRef(spawn.ObjectId, scaleMat));
                }
            }
            else if ((spawn.ObjectId & 0xFF000000u) == 0x02000000u)
            {
                var setup = _dats.Get<DatReaderWriter.DBObjs.Setup>(spawn.ObjectId);
                if (setup is not null)
                {
                    var flat = AcDream.Core.Meshing.SetupMesh.Flatten(setup);
                    foreach (var mr in flat)
                    {
                        var gfx = _dats.Get<DatReaderWriter.DBObjs.GfxObj>(mr.GfxObjId);
                        if (gfx is null) continue;
                        var partXf = mr.PartTransform * scaleMat;
                        var pb = AcDream.Core.Meshing.GfxObjBounds.Get(gfx);
                        if (pb is not null) sceneryBounds.Add(partXf, pb.Value);
                        meshRefs.Add(new AcDream.Core.World.MeshRef(mr.GfxObjId, partXf));
                    }
                }
            }

            if (meshRefs.Count == 0) continue;

            // Sample terrain Z at (localX, localY) to lift scenery onto the
            // ground. Add BaseLoc.Z from the scenery ObjectDesc (passed in as
            // spawn.LocalPosition.Z) so meshes that specify a vertical offset
            // from the ground (e.g., flowers at -0.1m, roots below terrain)
            // settle properly.
            float localX = spawn.LocalPosition.X;
            float localY = spawn.LocalPosition.Y;
            var worldPx = localX + lbOffset.X;
            var worldPy = localY + lbOffset.Y;
            float groundZ = SampleTerrainZ(lb.Heightmap, _heightTable, localX, localY);
            float finalZ = groundZ + spawn.LocalPosition.Z;

            if (_dumpSceneryZ)
            {
                string source = "heightmap";
                foreach (var mr in meshRefs)
                {
                    var dgfx = _dats.Get<DatReaderWriter.DBObjs.GfxObj>(mr.GfxObjId);
                    if (dgfx is null) continue;

                    float zMin = float.PositiveInfinity, zMax = float.NegativeInfinity;
                    foreach (var v in dgfx.VertexArray.Vertices.Values)
                    {
                        if (v.Origin.Z < zMin) zMin = v.Origin.Z;
                        if (v.Origin.Z > zMax) zMax = v.Origin.Z;
                    }
                    if (float.IsPositiveInfinity(zMin)) { zMin = 0f; zMax = 0f; }

                    // Per-part transform offset inside the setup (post-spawn-scale).
                    // For setup spawns this is Setup.PlacementFrames[Default].Frames[i] *
                    // spawn.Scale. For single-GfxObj spawns it's identity * spawn.Scale.
                    var partT = mr.PartTransform.Translation;

                    bool hasDD = dgfx.Flags.HasFlag(DatReaderWriter.Enums.GfxObjFlags.HasDIDDegrade);
                    string ddInfo = string.Empty;
                    if (hasDD && dgfx.DIDDegrade != 0)
                    {
                        var ddi = _dats.Get<DatReaderWriter.DBObjs.GfxObjDegradeInfo>(dgfx.DIDDegrade);
                        if (ddi is not null && ddi.Degrades.Count > 0)
                        {
                            uint slot0Id = (uint)ddi.Degrades[0].Id;
                            float slot0Min = 0f;
                            var slot0Gfx = _dats.Get<DatReaderWriter.DBObjs.GfxObj>(slot0Id);
                            if (slot0Gfx is not null && slot0Gfx.VertexArray.Vertices.Count > 0)
                            {
                                slot0Min = float.PositiveInfinity;
                                foreach (var v in slot0Gfx.VertexArray.Vertices.Values)
                                    if (v.Origin.Z < slot0Min) slot0Min = v.Origin.Z;
                                if (float.IsPositiveInfinity(slot0Min)) slot0Min = 0f;
                            }
                            ddInfo = $" deg[0]=0x{slot0Id:X8} deg[0]ZMin={slot0Min:F3}";
                        }
                    }

                    // partWorldZMin = the lowest vertex of this part in world space.
                    // = finalZ (setup origin in world Z) + partT.Z (part offset) + zMin (mesh-local lowest vertex)
                    // If everything is right and the lowest part of the tree should
                    // touch the ground, we expect partWorldZMin <= groundZ for at
                    // least one part of a multi-part setup.
                    float partWorldZMin = finalZ + partT.Z + zMin;

                    Console.WriteLine(
                        $"[scenery-z] lb=0x{lb.LandblockId:X8} root=0x{spawn.ObjectId:X8} gfx=0x{mr.GfxObjId:X8}" +
                        $" source={source}" +
                        $" world=({worldPx:F2},{worldPy:F2}) localXY=({localX:F2},{localY:F2})" +
                        $" groundZ={groundZ:F3} BaseLoc.Z={spawn.LocalPosition.Z:F3} finalZ={finalZ:F3}" +
                        $" partT=({partT.X:F2},{partT.Y:F2},{partT.Z:F3}) spawnScale={spawn.Scale:F3}" +
                        $" zRange=[{zMin:F3}..{zMax:F3}] partWorldZMin={partWorldZMin:F3} delta={partWorldZMin - groundZ:F3}" +
                        $" hasDIDDegrade={hasDD}{ddInfo}");
                }
            }

            var hydrated = new AcDream.Core.World.WorldEntity
            {
                Id = AcDream.Core.World.ProceduralSceneryIdAllocator.Allocate(
                    lbXByte,
                    lbYByte,
                    ref sceneryCounter),
                SourceGfxObjOrSetupId = spawn.ObjectId,
                Position = new System.Numerics.Vector3(localX, localY, finalZ) + lbOffset,
                Rotation = spawn.Rotation,
                MeshRefs = meshRefs,
                Scale = spawn.Scale,
                EffectCellId = AcDream.Core.Physics.TerrainSurface.ComputeOutdoorCellId(
                    lb.LandblockId,
                    localX,
                    localY),
            };
            if (sceneryBounds.TryGet(out var scbMin, out var scbMax))
                hydrated.SetLocalBounds(scbMin, scbMax);
            result.Add(hydrated);
        }

        return result;

    }

    private List<AcDream.Core.World.WorldEntity> BuildInteriorEntitiesForStreaming(
        uint landblockId,
        int lbX,
        int lbY,
        AcDream.App.Streaming.LandblockBuildOrigin origin,
        AcDream.App.Rendering.Wb.EnvCellLandblockBuildBuilder envCellBuild)
    {
        var result = new List<AcDream.Core.World.WorldEntity>();

        var lbInfo = _dats.Get<DatReaderWriter.DBObjs.LandBlockInfo>((landblockId & 0xFFFF0000u) | 0xFFFEu);
        if (lbInfo is null || lbInfo.NumCells == 0) return result;

        var lbOffset = new System.Numerics.Vector3(
            (lbX - origin.CenterX) * 192f,
            (lbY - origin.CenterY) * 192f,
            0f);

        envCellBuild.AddWalkBuildings(
            AcDream.App.Rendering.Walk.WalkBuildingFactory.Build(
                _dats, landblockId, lbInfo.Buildings, lbOffset));

        uint interiorLbX = (landblockId >> 24) & 0xFFu;
        uint interiorLbY = (landblockId >> 16) & 0xFFu;
        uint localCounter = 0;

        uint firstCellId = (landblockId & 0xFFFF0000u) | 0x0100u;
        for (uint offset = 0; offset < lbInfo.NumCells; offset++)
        {
            uint envCellId = firstCellId + offset;
            var envCell = _dats.Get<DatReaderWriter.DBObjs.EnvCell>(envCellId);
            if (envCell is null)
            {
                Console.WriteLine($"[cell-miss] EnvCell 0x{envCellId:X8} null during interior hydration (NumCells={lbInfo.NumCells})");
                continue;
            }

            DatReaderWriter.Types.CellStruct? cellStruct = null;
            if (envCell.EnvironmentId != 0)
            {
                var environment = _dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell.EnvironmentId);
                if (environment is null)
                {
                    Console.WriteLine($"[cell-miss] Environment 0x{0x0D000000u | envCell.EnvironmentId:X8} null for EnvCell 0x{envCellId:X8} -> walls not registered");
                }
                if (environment is not null
                    && environment.Cells.TryGetValue(envCell.CellStructure, out cellStruct))
                {
                    var physicsCellOrigin = envCell.Position.Origin + lbOffset;
                    var cellOrigin = physicsCellOrigin;
                    var cellTransform =
                        System.Numerics.Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation) *
                        System.Numerics.Matrix4x4.CreateTranslation(cellOrigin);
                    var physicsCellTransform = cellTransform;

                    bool hasDrawableGeometry =
                        AcDream.Core.Meshing.CellMesh.HasDrawableGeometry(envCell, cellStruct, _dats);
                    envCellBuild.AddCell(
                        envCellId,
                        envCell,
                        cellStruct,
                        physicsCellOrigin,
                        physicsCellTransform,
                        cellOrigin,
                        cellTransform,
                        hasDrawableGeometry: hasDrawableGeometry);
                }
            }

            foreach (var stab in envCell.StaticObjects)
            {
                if ((stab.Id & 0xFF000000u) == 0x01000000u
                    && AcDream.Core.Meshing.GfxObjDegradeResolver.IsRuntimeHiddenMarker(_dats, stab.Id))
                    continue;

                var meshRefs = new List<AcDream.Core.World.MeshRef>();
                var interiorBounds = new AcDream.Core.Meshing.LocalBoundsAccumulator();
                int stabLightCount = 0;
                if ((stab.Id & 0xFF000000u) == 0x01000000u)
                {
                    var gfx = _dats.Get<DatReaderWriter.DBObjs.GfxObj>(stab.Id);
                    if (gfx is not null)
                    {
                        var pb = AcDream.Core.Meshing.GfxObjBounds.Get(gfx);
                        if (pb is not null) interiorBounds.Add(System.Numerics.Matrix4x4.Identity, pb.Value);
                        meshRefs.Add(new AcDream.Core.World.MeshRef(stab.Id, System.Numerics.Matrix4x4.Identity));
                    }
                }
                else if ((stab.Id & 0xFF000000u) == 0x02000000u)
                {
                    var setup = _dats.Get<DatReaderWriter.DBObjs.Setup>(stab.Id);
                    if (setup is not null)
                    {
                        stabLightCount = setup.Lights.Count;
                        var flat = AcDream.Core.Meshing.SetupMesh.Flatten(setup);
                        foreach (var mr in flat)
                        {
                            if (AcDream.Core.Meshing.GfxObjDegradeResolver.IsRuntimeHiddenMarker(_dats, mr.GfxObjId))
                                continue;
                            var gfx = _dats.Get<DatReaderWriter.DBObjs.GfxObj>(mr.GfxObjId);
                            if (gfx is null)
                            {
                                continue;
                            }
                            var pb = AcDream.Core.Meshing.GfxObjBounds.Get(gfx);
                            if (pb is not null) interiorBounds.Add(mr.PartTransform, pb.Value);
                            meshRefs.Add(mr);
                        }
                    }
                }

                if (!AcDream.Core.Meshing.EntityHydrationRules.ShouldKeepEntity(meshRefs.Count, stabLightCount))
                {
                    continue;
                }

                var worldPos = stab.Frame.Origin + lbOffset;
                var worldRot = stab.Frame.Orientation;

                var hydrated = new AcDream.Core.World.WorldEntity
                {
                    Id = AcDream.Core.World.InteriorEntityIdAllocator.Allocate(
                        interiorLbX,
                        interiorLbY,
                        ref localCounter),
                    SourceGfxObjOrSetupId = stab.Id,
                    Position = worldPos,
                    Rotation = worldRot,
                    MeshRefs = meshRefs,
                    ParentCellId = envCellId,
                };
                if (interiorBounds.TryGet(out var ibMin, out var ibMax))
                    hydrated.SetLocalBounds(ibMin, ibMax);

                result.Add(hydrated);
            }
        }

        return result;
    }


    private (float MaxZ, float MinZ) ComputeWalkZSlab(byte[] heights)
    {
        byte maxByte = 0, minByte = 255;
        foreach (byte h in heights)
        {
            if (h > maxByte) maxByte = h;
            if (h < minByte) minByte = h;
        }
        return (_heightTable[maxByte] + 200f, _heightTable[minByte] - 1f);
    }

    private static float SampleTerrainZ(DatReaderWriter.DBObjs.LandBlock block, float[] heightTable, float localX, float localY)
    {
        uint landblockX = (block.Id >> 24) & 0xFFu;
        uint landblockY = (block.Id >> 16) & 0xFFu;
        return AcDream.Core.Physics.TerrainSurface.SampleZFromHeightmap(
            block.Height, heightTable, landblockX, landblockY, localX, localY);
    }

}
