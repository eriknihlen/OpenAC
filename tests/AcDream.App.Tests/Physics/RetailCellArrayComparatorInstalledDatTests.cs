using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.Core.Meshing;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit;
using Xunit.Abstractions;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatGfxObj = DatReaderWriter.DBObjs.GfxObj;
using DatSetup = DatReaderWriter.DBObjs.Setup;

namespace AcDream.App.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public sealed class RetailCellArrayComparatorInstalledDatTests
{
    private const string SkipMessage =
        "Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.";

    private readonly ITestOutputHelper _out;
    public RetailCellArrayComparatorInstalledDatTests(ITestOutputHelper output) => _out = output;

    private static string? ResolveDatDir()
    {
        var fromEnv = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;
        var def = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }


    private static PhysicsEngine BuildOutdoorEngine(uint landblockId)
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(landblockId, new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        return engine;
    }

    private static PhysicsEngine BuildIndoorEngine(
        DatCollection dats, uint landblockId, uint lowStart, uint lowEnd)
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        for (uint low = lowStart; low <= lowEnd; low++)
        {
            uint id = landblockId | low;
            var datCell = dats.Get<DatEnvCell>(id);
            if (datCell is null) continue;
            var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(
                0x0D000000u | datCell.EnvironmentId);
            if (environment is null) continue;
            if (!environment.Cells.TryGetValue(datCell.CellStructure, out var cellStruct)
                || cellStruct is null)
            {
                continue;
            }
            var world = Matrix4x4.CreateFromQuaternion(datCell.Position.Orientation)
                * Matrix4x4.CreateTranslation(datCell.Position.Origin);
            cache.CacheCellStruct(id, datCell, cellStruct, world);
        }
        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(landblockId, new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        return engine;
    }

    private static uint DeriveOutdoorSeedForTest(
        Vector3 worldPos, float worldOffsetX, float worldOffsetY, uint landblockId)
    {
        if (landblockId == 0u) return 0u;
        float localX = worldPos.X - worldOffsetX;
        float localY = worldPos.Y - worldOffsetY;
        int cx = (int)Math.Clamp(localX / 24f, 0f, 7f);
        int cy = (int)Math.Clamp(localY / 24f, 0f, 7f);
        uint lbPrefix = landblockId & 0xFFFF0000u;
        return lbPrefix | (uint)(cx * 8 + cy + 1);
    }

    private readonly record struct ComparatorOutcome(
        string Label,
        uint EntityId,
        IReadOnlyList<uint> RetailCells,
        IReadOnlyList<uint> CollisionCells,
        RetailCellArrayRoute Route,
        int PartCount,
        int BspShapeCount,
        bool CollisionRegistered);

    private ComparatorOutcome RegisterAndCompare(
        string label,
        PhysicsEngine engine,
        PhysicsDataCache cache,
        uint entityId,
        IReadOnlyList<MeshRef> meshRefs,
        DatSetup? setupFallback,
        Vector3 worldPos,
        Quaternion worldRot,
        float worldOffsetX,
        float worldOffsetY,
        uint landblockId,
        uint seedCellId)
    {
        IReadOnlyList<ShadowShape> bspShapes = ShadowShapeBuilder.FromLandblockBspParts(
            meshRefs, isBuildingShell: false, cache.GetGfxObj);

        IReadOnlyList<ShadowShape> partArray = ShadowShapeBuilder.FromStaticRenderParts(
            meshRefs, cache.GetGfxObj, cache.GetVisualBounds, out bool hasPhysicsBsp);

        uint resolvedSeed = seedCellId != 0u
            ? seedCellId
            : DeriveOutdoorSeedForTest(worldPos, worldOffsetX, worldOffsetY, landblockId);

        bool registered = false;
        if (bspShapes.Count > 0)
        {
            engine.ShadowObjects.RegisterMultiPart(
                entityId, worldPos, worldRot, bspShapes, 0u, EntityCollisionFlags.None,
                worldOffsetX, worldOffsetY, landblockId,
                seedCellId: resolvedSeed, isStatic: true, partArray: partArray);
            registered = true;
        }
        else if (setupFallback is not null)
        {
            FlatSetupCollision flatSetup = FlatCollisionAssetBuilder.FlattenSetup(setupFallback);
            const float scale = 1f;
            var setupShapes = new List<ShadowShape>();
            for (int i = 0; i < flatSetup.Cylinders.Length; i++)
            {
                FlatCollisionCylinder cyl = flatSetup.Cylinders[i];
                float radius = cyl.Radius * scale;
                float baseHeight = cyl.Height > 0f ? cyl.Height : cyl.Radius * 4f;
                if (radius <= 0f) continue;
                setupShapes.Add(ShadowShape.Cylinder(
                    gfxObjId: 0u,
                    localPosition: cyl.Origin * scale,
                    localRotation: Quaternion.Identity,
                    scale: scale,
                    radius: radius,
                    cylHeight: baseHeight * scale));
            }
            if (flatSetup.Cylinders.Length == 0)
            {
                for (int i = 0; i < flatSetup.Spheres.Length; i++)
                {
                    FlatCollisionSphere sph = flatSetup.Spheres[i];
                    if (sph.Radius <= 0f) continue;
                    setupShapes.Add(ShadowShape.Sphere(
                        gfxObjId: 0u,
                        localPosition: sph.Origin * scale,
                        localRotation: Quaternion.Identity,
                        scale: scale,
                        radius: sph.Radius * scale));
                }
            }
            if (setupShapes.Count > 0)
            {
                engine.ShadowObjects.RegisterMultiPart(
                    entityId, worldPos, worldRot, setupShapes, 0u, EntityCollisionFlags.None,
                    worldOffsetX, worldOffsetY, landblockId,
                    seedCellId: resolvedSeed, isStatic: true, partArray: partArray);
                registered = true;
            }
        }

        engine.ShadowObjects.TryGetRetailCellArray(entityId, out IReadOnlyList<uint> retailCells);
        IReadOnlyList<uint> collisionCells = engine.ShadowObjects.GetOwnerCells(entityId);
        RetailCellArrayRoute route = engine.ShadowObjects.GetRetailCellArrayRoute(entityId);

        PrintOutcome(
            label, entityId, retailCells, collisionCells, route,
            partArray.Count, bspShapes.Count, hasPhysicsBsp, registered, resolvedSeed);

        return new ComparatorOutcome(
            label, entityId, retailCells, collisionCells, route,
            partArray.Count, bspShapes.Count, registered);
    }

    private void PrintOutcome(
        string label,
        uint entityId,
        IReadOnlyList<uint> retailCells,
        IReadOnlyList<uint> collisionCells,
        RetailCellArrayRoute route,
        int partCount,
        int bspShapeCount,
        bool hasPhysicsBsp,
        bool collisionRegistered,
        uint seedCellId)
    {
        static string CellSet(IEnumerable<uint> ids) =>
            "[" + string.Join(",", ids.Select(id => FormattableString.Invariant($"0x{id:X8}"))) + "]";

        _out.WriteLine(FormattableString.Invariant(
            $"--- {label} (entity=0x{entityId:X8} seed=0x{seedCellId:X8}) ---"));
        _out.WriteLine(FormattableString.Invariant(
            $"  parts={partCount} bspShapes={bspShapeCount} hasPhysicsBsp={hasPhysicsBsp} collisionRegistered={collisionRegistered} route={route}"));
        _out.WriteLine($"  retail    n={retailCells.Count} {CellSet(retailCells)}");
        _out.WriteLine($"  collision n={collisionCells.Count} {CellSet(collisionCells)}");

        foreach (uint cellId in retailCells)
        {
            int entries = 0;
            foreach (RetailPartEntry entry in _lastEngine!.ShadowObjects.GetRetailPartEntriesInCell(cellId))
                if (entry.EntityId == entityId) entries++;
            _out.WriteLine(FormattableString.Invariant(
                $"    cell 0x{cellId:X8}: {entries} retail part entries"));
        }

        IEnumerable<uint> retailOnly = retailCells.Except(collisionCells);
        IEnumerable<uint> collisionOnly = collisionCells.Except(retailCells);
        if (retailOnly.Any() || collisionOnly.Any())
        {
            _out.WriteLine(
                $"  retail-vs-collision difference: "
                + $"retailOnly={CellSet(retailOnly)} collisionOnly={CellSet(collisionOnly)}");
        }
    }

    private PhysicsEngine? _lastEngine;

    private ComparatorOutcome RunFixture(
        string label,
        PhysicsEngine engine,
        PhysicsDataCache cache,
        uint entityId,
        IReadOnlyList<MeshRef> meshRefs,
        DatSetup? setupFallback,
        Vector3 worldPos,
        Quaternion worldRot,
        float worldOffsetX,
        float worldOffsetY,
        uint landblockId,
        uint seedCellId)
    {
        _lastEngine = engine;
        return RegisterAndCompare(
            label, engine, cache, entityId, meshRefs, setupFallback,
            worldPos, worldRot, worldOffsetX, worldOffsetY, landblockId, seedCellId);
    }

    private static void AssertSameCells(IReadOnlyList<uint> retail, IReadOnlyList<uint> collision)
    {
        Assert.Equal(
            collision.OrderBy(id => id).ToArray(),
            retail.OrderBy(id => id).ToArray());
    }


    [Fact]
    public void FacilityHubStair_RetailCellArrayMatchesCollisionCells()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail(SkipMessage);
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint FacilityHub = 0x8A020000u;
        const uint setupId = 0x02000623u;

        PhysicsEngine engine = BuildIndoorEngine(dats, FacilityHub, 0x0100u, 0x01FFu);
        var cache = (PhysicsDataCache)engine.DataCache!;

        DatSetup setup = Assert.IsType<DatSetup>(dats.Get<DatSetup>(setupId));
        IReadOnlyList<MeshRef> meshRefs = SetupMesh.Flatten(setup);
        foreach (MeshRef meshRef in meshRefs)
        {
            DatGfxObj gfxObj = Assert.IsType<DatGfxObj>(dats.Get<DatGfxObj>(meshRef.GfxObjId));
            cache.CacheGfxObj(meshRef.GfxObjId, gfxObj);
        }

        DatEnvCell parent = Assert.IsType<DatEnvCell>(
            dats.Get<DatEnvCell>(FacilityHub | 0x015Fu));
        var stair = Assert.Single(parent.StaticObjects, s => s.Id == setupId);
        Vector3 worldPos = new(
            stair.Frame.Origin.X, stair.Frame.Origin.Y, stair.Frame.Origin.Z);
        Quaternion worldRot = stair.Frame.Orientation;

        const uint entityId = 0x7F000001u;
        ComparatorOutcome outcome = RunFixture(
            "1: Facility Hub stair Setup 0x02000623",
            engine, cache, entityId, meshRefs, setup,
            worldPos, worldRot,
            worldOffsetX: 0f, worldOffsetY: 0f,
            landblockId: FacilityHub,
            seedCellId: FacilityHub | 0x015Fu);

        Assert.Contains(FacilityHub | 0x015Fu, outcome.RetailCells);
        Assert.Contains(FacilityHub | 0x015Eu, outcome.RetailCells);
        AssertSameCells(outcome.RetailCells, outcome.CollisionCells);
    }


    private static (uint ParentCellId, Stab Stab)? FindStaticParent(
        DatCollection dats, uint landblockId, uint staticId, uint lowStart, uint lowEnd)
    {
        for (uint low = lowStart; low <= lowEnd; low++)
        {
            uint id = landblockId | low;
            DatEnvCell? cell = dats.Get<DatEnvCell>(id);
            if (cell?.StaticObjects is null) continue;
            foreach (Stab stab in cell.StaticObjects)
                if (stab.Id == staticId)
                    return (id, stab);
        }
        return null;
    }

    [Fact]
    public void CathedralRamp_RetailCellArrayMatchesCollisionCells()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail(SkipMessage);
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint Cathedral = 0xF4180000u;
        const uint setupId = 0x020009A2u;

        (uint ParentCellId, Stab Stab)? found =
            FindStaticParent(dats, Cathedral, setupId, 0x0100u, 0x01FFu)
            ?? FindStaticParent(dats, Cathedral, setupId, 0x0200u, 0x03FFu);
        Assert.True(found is not null, "Cathedral ramp 0x020009A2 parent EnvCell not found in 0x0100-0x03FF.");
        (uint parentCellId, Stab stab) = found!.Value;
        _out.WriteLine(FormattableString.Invariant(
            $"  resolved cathedral ramp parent cell = 0x{parentCellId:X8}"));

        PhysicsEngine engine = BuildIndoorEngine(dats, Cathedral, 0x0100u, 0x03FFu);
        var cache = (PhysicsDataCache)engine.DataCache!;

        DatSetup setup = Assert.IsType<DatSetup>(dats.Get<DatSetup>(setupId));
        IReadOnlyList<MeshRef> meshRefs = SetupMesh.Flatten(setup);
        foreach (MeshRef meshRef in meshRefs)
        {
            DatGfxObj gfxObj = Assert.IsType<DatGfxObj>(dats.Get<DatGfxObj>(meshRef.GfxObjId));
            cache.CacheGfxObj(meshRef.GfxObjId, gfxObj);
        }

        Vector3 worldPos = new(stab.Frame.Origin.X, stab.Frame.Origin.Y, stab.Frame.Origin.Z);
        Quaternion worldRot = stab.Frame.Orientation;

        const uint entityId = 0x7F000002u;
        ComparatorOutcome outcome = RunFixture(
            "2: Cathedral ramp Setup 0x020009A2",
            engine, cache, entityId, meshRefs, setup,
            worldPos, worldRot,
            worldOffsetX: 0f, worldOffsetY: 0f,
            landblockId: Cathedral,
            seedCellId: parentCellId);

        AssertSameCells(outcome.RetailCells, outcome.CollisionCells);
    }


    [Fact]
    public void NeftetFormation_RetailCellArrayMatchesCollisionCells()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail(SkipMessage);
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint NeftetLandblock = 0x87640000u;
        const uint NeftetLandblockInfo = 0x8764FFFEu;
        const uint FormationGfxObj = 0x010046D8u;

        LandBlockInfo? info = dats.Get<LandBlockInfo>(NeftetLandblockInfo);
        Assert.NotNull(info);
        Stab formation = info!.Objects.First(o => o.Id == FormationGfxObj);

        DatGfxObj gfx = Assert.IsType<DatGfxObj>(dats.Get<DatGfxObj>(FormationGfxObj));

        PhysicsEngine engine = BuildOutdoorEngine(NeftetLandblock);
        var cache = (PhysicsDataCache)engine.DataCache!;
        cache.CacheGfxObj(FormationGfxObj, gfx);

        var meshRefs = new List<MeshRef> { new(FormationGfxObj, Matrix4x4.Identity) };
        Vector3 worldPos = formation.Frame.Origin;
        Quaternion worldRot = formation.Frame.Orientation;

        const uint entityId = 0x7F000003u;
        ComparatorOutcome outcome = RunFixture(
            "3: Neftet formation GfxObj 0x010046D8",
            engine, cache, entityId, meshRefs, setupFallback: null,
            worldPos, worldRot,
            worldOffsetX: 0f, worldOffsetY: 0f,
            landblockId: NeftetLandblock,
            seedCellId: 0u);

        AssertSameCells(outcome.RetailCells, outcome.CollisionCells);
    }


    private static (uint Id, Stab Stab)? FindEdgeCrossingGfxObjStatic(
        DatCollection dats, PhysicsDataCache scanCache, uint landblockInfoId, uint excludeGfxObjId)
    {
        LandBlockInfo? info = dats.Get<LandBlockInfo>(landblockInfoId);
        if (info is null) return null;
        foreach (Stab stab in info.Objects)
        {
            if ((stab.Id & 0xFF000000u) != 0x01000000u) continue;
            if (stab.Id == excludeGfxObjId) continue;
            DatGfxObj? gfx = dats.Get<DatGfxObj>(stab.Id);
            if (gfx is null) continue;
            scanCache.CacheGfxObj(stab.Id, gfx);
            GfxObjPhysics? physics = scanCache.GetGfxObj(stab.Id);
            if (physics?.BSP?.Root is null) continue; // must take the BSP route.
            if (physics.VisualBounds is not { } box) continue;
            float x = stab.Frame.Origin.X;
            float y = stab.Frame.Origin.Y;
            bool crosses = x + box.Min.X < 0f || x + box.Max.X > 192f
                || y + box.Min.Y < 0f || y + box.Max.Y > 192f;
            if (crosses)
                return (stab.Id, stab);
        }
        return null;
    }

    [Fact]
    public void OutdoorLandblockEdgeCrosser_RetailCellArrayMatchesCollisionCells()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail(SkipMessage);
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint ArwicLandblock = 0xC6A90000u;
        const uint ArwicLandblockInfo = 0xC6A9FFFEu;
        const uint NeftetLandblock = 0x87640000u;
        const uint NeftetLandblockInfo = 0x8764FFFEu;
        const uint NeftetFormationGfxObj = 0x010046D8u; // fixture 3's own object — excluded here.

        var scanCache = new PhysicsDataCache();
        (uint Id, Stab Stab)? candidate =
            FindEdgeCrossingGfxObjStatic(dats, scanCache, ArwicLandblockInfo, NeftetFormationGfxObj);
        uint landblockId = ArwicLandblock;
        if (candidate is null)
        {
            candidate = FindEdgeCrossingGfxObjStatic(
                dats, scanCache, NeftetLandblockInfo, NeftetFormationGfxObj);
            landblockId = NeftetLandblock;
        }
        Assert.True(
            candidate is not null,
            "No BSP-bearing edge-crossing GfxObj static found scanning Arwic/Neftet Objects.");
        (uint gfxObjId, Stab stab) = candidate!.Value;
        _out.WriteLine(FormattableString.Invariant(
            $"  resolved edge crosser: landblock=0x{landblockId:X8} gfxObj=0x{gfxObjId:X8} origin=({stab.Frame.Origin.X:F2},{stab.Frame.Origin.Y:F2})"));

        DatGfxObj gfx = Assert.IsType<DatGfxObj>(dats.Get<DatGfxObj>(gfxObjId));
        PhysicsEngine engine = BuildOutdoorEngine(landblockId);
        var cache = (PhysicsDataCache)engine.DataCache!;
        cache.CacheGfxObj(gfxObjId, gfx);

        var meshRefs = new List<MeshRef> { new(gfxObjId, Matrix4x4.Identity) };
        Vector3 worldPos = stab.Frame.Origin;
        Quaternion worldRot = stab.Frame.Orientation;

        const uint entityId = 0x7F000004u;
        ComparatorOutcome outcome = RunFixture(
            "4: outdoor landblock-edge crosser",
            engine, cache, entityId, meshRefs, setupFallback: null,
            worldPos, worldRot,
            worldOffsetX: 0f, worldOffsetY: 0f,
            landblockId: landblockId,
            seedCellId: 0u);

        IEnumerable<uint> foreignLandblockCells = outcome.RetailCells
            .Where(id => (id & 0xFFFF0000u) != landblockId);
        _out.WriteLine(
            $"  cells in a NEIGHBOR landblock (edge-crossing evidence): "
            + $"[{string.Join(",", foreignLandblockCells.Select(id => FormattableString.Invariant($"0x{id:X8}")))}]");

        AssertSameCells(outcome.RetailCells, outcome.CollisionCells);
    }

}
