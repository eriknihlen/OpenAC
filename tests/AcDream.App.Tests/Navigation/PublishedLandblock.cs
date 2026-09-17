using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using AcDream.App.Streaming;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit.Abstractions;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using DatGfxObj = DatReaderWriter.DBObjs.GfxObj;
using DatSetup = DatReaderWriter.DBObjs.Setup;

namespace AcDream.App.Tests.Navigation;

/// <summary>
/// Landblocks from the installed game files published into one physics world the
/// way the client streams them, with the body that walks them and where cells stand.
/// </summary>
internal sealed record PublishedLandblock(
    PhysicsEngine Engine,
    uint LandblockId,
    NavBody Body,
    IReadOnlyDictionary<uint, Vector3> CellOrigins,
    double Milliseconds)
{
    private const uint HumanSetup = 0x0200004Eu;

    public static PublishedLandblock Load(string datDirectory, uint landblockId, IReadOnlyList<uint> cellsToLocate) =>
        Load(datDirectory, [landblockId], cellsToLocate);

    /// <summary>
    /// Publishes landblocks into one physics world the way the client streams
    /// them, placed around the first, whose south-west corner is the origin. With
    /// <paramref name="asHeadless"/>, the landblocks' objects and collision are loaded
    /// the way the headless host loads them instead, with no renderer's streaming.
    /// </summary>
    public static PublishedLandblock Load(
        string datDirectory,
        IReadOnlyList<uint> landblockIds,
        IReadOnlyList<uint> cellsToLocate,
        bool asHeadless = false)
    {
        var clock = Stopwatch.StartNew();
        using var dats = new BoundedTestDatCollection(datDirectory);
        var bounded = (IDatReaderWriter)dats;
        Region region = Assert.IsType<Region>(bounded.Get<Region>(0x13000000u));
        float[] heights = region.LandDefs.LandHeightTable;
        DatSetup human = Assert.IsType<DatSetup>(bounded.Get<DatSetup>(HumanSetup));

        using var collisionSource = new InstalledDatCollisionSource(bounded);
        var factory = new LandblockBuildFactory(bounded, collisionSource, new object(), heights);
        var cache = PhysicsDataCache.CreateProduction();
        var engine = new PhysicsEngine { DataCache = cache };
        int baseX = (int)((landblockIds[0] >> 24) & 0xFFu);
        int baseY = (int)((landblockIds[0] >> 16) & 0xFFu);
        var offsets = new Dictionary<uint, Vector3>();
        foreach (uint landblockId in landblockIds)
        {
            int blockX = (int)((landblockId >> 24) & 0xFFu);
            int blockY = (int)((landblockId >> 16) & 0xFFu);
            var origin = new Vector3((blockX - baseX) * 192f, (blockY - baseY) * 192f, 0f);
            offsets[landblockId & 0xFFFF0000u] = origin;
            LoadedLandblock landblock;
            LandblockCollisionBuild collisions;
            if (asHeadless)
            {
                Assert.True(LandblockPhysicsContentBuilder.TryLoadCollisionLandblock(
                    bounded,
                    collisionSource,
                    heights,
                    landblockId,
                    origin,
                    out landblock,
                    out collisions));
            }
            else
            {
                var request = new LandblockBuildRequest(
                    landblockId,
                    LandblockStreamJobKind.LoadNear,
                    Generation: 1,
                    new LandblockBuildOrigin(baseX, baseY));
                LandblockBuild build = Assert.IsType<LandblockBuild>(factory.Build(request));
                landblock = build.Landblock;
                collisions = Assert.IsType<LandblockCollisionBuild>(build.Collisions);
            }

            TerrainSurface terrain = LandblockPhysicsContentBuilder.BuildTerrainSurface(landblock, heights);
            var cellSurfaces = new List<CellSurface>();
            var portalPlanes = new List<PortalPlane>();
            LandblockPhysicsContentBuilder.PublishPreparedCells(
                cache,
                landblock,
                collisions,
                origin,
                cellSurfaces,
                portalPlanes);
            LandblockPhysicsContentBuilder.CacheBuildings(cache, landblock, terrain, origin);
            LandblockPhysicsContentBuilder.CachePreparedObjects(cache, collisions);
            engine.AddLandblock(landblock.LandblockId, terrain, cellSurfaces, portalPlanes, origin.X, origin.Y);
            _ = LandblockPhysicsContentBuilder.PublishStaticCollision(engine, cache, landblock, collisions, origin);
        }

        var cellOrigins = new Dictionary<uint, Vector3>();
        foreach (uint cellId in cellsToLocate)
        {
            if (bounded.Get<DatEnvCell>(cellId) is { } cell
                && offsets.TryGetValue(cellId & 0xFFFF0000u, out Vector3 offset))
            {
                cellOrigins[cellId] = cell.Position.Origin + offset;
            }
        }

        return new PublishedLandblock(
            engine,
            landblockIds[0],
            NavBody.Player(human.StepUpHeight, human.StepDownHeight),
            cellOrigins,
            clock.Elapsed.TotalMilliseconds);
    }
}
