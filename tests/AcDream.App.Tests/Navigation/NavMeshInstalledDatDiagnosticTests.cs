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
/// Builds navigation grids from real landblocks in the installed game files,
/// published into a physics world the way the client streams them, and reports
/// what they found as numbers and as top-down images.
/// </summary>
[Trait("Lane", "InstalledDat")]
[Trait("Purpose", "Diagnostic")]
public sealed class NavMeshInstalledDatDiagnosticTests
{
    private const uint Yaraq = 0x7D64FFFFu;
    private const uint Holtburg = 0xA9B4FFFFu;
    private const uint HoltburgEast = 0xAAB4FFFFu;
    private const uint HoltburgSouth = 0xA9B3FFFFu;
    private const uint HoltburgSouthEast = 0xAAB3FFFFu;

    /// <summary>The cells a route from the archmage's room to the healer's deck passes, in order.</summary>
    private static readonly uint[] YaraqRouteCells =
        [0x7D64012Eu, 0x7D640131u, 0x7D640119u, 0x7D64011Au, 0x7D640110u, 0x7D64010Fu];

    private readonly ITestOutputHelper _output;

    public NavMeshInstalledDatDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void YaraqArchmageRoomRoutesOutUpAndOntoTheHealersDeck()
    {
        PublishedLandblock world = PublishedLandblock.Load(RequireDatDirectory(), Yaraq, YaraqRouteCells);
        NavGrid grid = BuildAndReport("yaraq", world);
        var from = new Vector3(83.96f, 90.86f, 15.205f);
        var to = new Vector3(85.92f, 138.564f, 15.605f);

        NavRoute route = NavRouter.Find(grid, from, to, arrivalRadius: 1f);

        Report("yaraq", grid, route, from, to);
        WriteImages("yaraq", grid, route);
        WriteReachability("yaraq", grid, world, from, to, minX: 55f, minY: 75f, maxX: 115f, maxY: 150f);
        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
    }

    [Fact]
    public void HoltburgIsBuiltAndDrawn()
    {
        PublishedLandblock world = PublishedLandblock.Load(RequireDatDirectory(), Holtburg, []);
        NavGrid grid = BuildAndReport("holtburg", world);

        WriteImages("holtburg", grid, route: null);
        Assert.True(grid.Report.ClearNodes > 0);
    }

    [Fact]
    public void HoltburgRoutesAcrossTheCornerWhereFourLandblocksMeet()
    {
        PublishedLandblock world = PublishedLandblock.Load(
            RequireDatDirectory(),
            [Holtburg, HoltburgEast, HoltburgSouth, HoltburgSouthEast],
            []);
        var clock = Stopwatch.StartNew();
        NavGeometry geometry = Assert.IsType<NavGeometry>(NavGeometry.Capture(world.Engine, 96f, -96f, 192f));
        double captureMilliseconds = clock.Elapsed.TotalMilliseconds;
        NavGrid grid = NavGrid.Build(geometry, world.Body);
        _output.WriteLine(
            $"holtburg-corner: published {geometry.LandblockIds.Count} landblocks in {world.Milliseconds:0} ms; captured "
            + $"{geometry.CellTriangles.Count} cell and {geometry.ObjectTriangles.Count} object triangles and "
            + $"{geometry.Cylinders.Count} cylinders in {captureMilliseconds:0} ms");
        _output.WriteLine(
            $"holtburg-corner: {grid.Report.Spans} spans, {grid.Report.Nodes} nodes, {grid.Report.ClearNodes} clear, "
            + $"built in {grid.Report.Milliseconds:0} ms");
        Vector3 from = OnTerrain(world.Engine, 150f, 40f);
        Vector3 to = OnTerrain(world.Engine, 240f, -40f);

        NavRoute route = NavRouter.Find(grid, from, to, arrivalRadius: 1f);

        Report("holtburg-corner", grid, route, from, to);
        WriteImages("holtburg-corner", grid, route);
        bool[] reached = Reachable(grid, grid.FindNode(from, NavRouter.StartRadius, NavRouter.StartHeightTolerance));
        int clearReached = reached.Count(value => value);
        _output.WriteLine(
            $"holtburg-corner: {clearReached} of {grid.Report.ClearNodes} clear nodes are reachable from the start");
        Assert.Equal(4, geometry.LandblockIds.Count);
        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.True(clearReached > grid.Report.ClearNodes / 2);
    }

    [Fact]
    public void HoltburgRoutesBetweenBuildingInteriorsWithoutPassingThroughWalls()
    {
        uint[] cells = [.. Enumerable.Range(0x100, 0x100).Select(offset => (Holtburg & 0xFFFF0000u) | (uint)offset)];
        PublishedLandblock world = PublishedLandblock.Load(RequireDatDirectory(), Holtburg, cells);
        NavGrid grid = BuildAndReport("holtburg-interiors", world);
        var standing = new List<(uint Cell, Vector3 At)>();
        foreach ((uint cell, Vector3 origin) in world.CellOrigins.OrderBy(pair => pair.Key))
        {
            int node = grid.FindWalkableNode(origin, 1f, 1.5f);
            if (node >= 0)
                standing.Add((cell, grid.Position(node)));
        }

        const int wanted = 40;
        var outcomes = new SortedDictionary<NavRouteOutcome, int>();
        int routes = 0;
        int throughWalls = 0;
        int nearWalls = 0;
        var clock = Stopwatch.StartNew();
        for (int first = 0; first < standing.Count && routes < wanted; first++)
        {
            for (int second = first + 1; second < standing.Count && routes < wanted; second++)
            {
                (uint fromCell, Vector3 from) = standing[first];
                (uint toCell, Vector3 to) = standing[second];
                float apart = Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(to.X, to.Y));
                if (apart < 15f || apart > 60f)
                    continue;
                routes++;
                NavRoute route = NavRouter.Find(grid, from, to, arrivalRadius: 1f);
                outcomes[route.Outcome] = outcomes.GetValueOrDefault(route.Outcome) + 1;
                if (route.Outcome != NavRouteOutcome.Routed)
                    _output.WriteLine($"  0x{fromCell:X8} to 0x{toCell:X8}, {apart:0.0} m apart: {route.Outcome}, {route.Reason}");
                for (int leg = 1; leg < route.Legs.Count; leg++)
                {
                    if (!grid.IsOpenLine(route.Legs[leg - 1], route.Legs[leg]))
                    {
                        throughWalls++;
                        _output.WriteLine($"  0x{fromCell:X8} to 0x{toCell:X8}: leg {leg} passes through a wall");
                    }
                    else if (!grid.CanSweep(route.Legs[leg - 1], route.Legs[leg]))
                    {
                        nearWalls++;
                    }
                }
            }
        }

        _output.WriteLine(
            $"holtburg-interiors: {world.CellOrigins.Count} cells, {standing.Count} with a clear spot at their origin; "
            + $"{routes} routes between cells 15 to 60 m apart in {clock.Elapsed.TotalMilliseconds:0} ms: "
            + string.Join(", ", outcomes.Select(pair => $"{pair.Value} {pair.Key}"))
            + $"; {throughWalls} legs pass through a wall, {nearWalls} pass nearer one than the body fits");
        Assert.True(routes > 0);
        Assert.Equal(0, throughWalls);
    }

    [Fact]
    public void HoltburgRoutesPastACreatureKeepAsClearOfWallsAsWithoutIt()
    {
        uint[] cells = [.. Enumerable.Range(0x100, 0x100).Select(offset => (Holtburg & 0xFFFF0000u) | (uint)offset)];
        PublishedLandblock world = PublishedLandblock.Load(RequireDatDirectory(), Holtburg, cells);
        NavGrid grid = BuildAndReport("holtburg-creatures", world);

        RoutePastCreatures("holtburg-creatures", world, grid);
    }

    [Fact]
    public void DungeonRoutesPastACreatureKeepAsClearOfWallsAsWithoutIt()
    {
        const uint dungeon = 0x0019FFFFu;
        uint[] cells = [.. Enumerable.Range(0x100, 0x300).Select(offset => (dungeon & 0xFFFF0000u) | (uint)offset)];
        PublishedLandblock world = PublishedLandblock.Load(RequireDatDirectory(), dungeon, cells);
        Assert.True(NavGeometry.TryMeasureCells(world.Engine, dungeon, out Vector2 minimum, out Vector2 maximum));
        float size = MathF.Ceiling((MathF.Max(maximum.X - minimum.X, maximum.Y - minimum.Y) + 48f) / 16f) * 16f;
        float originX = MathF.Floor((((minimum.X + maximum.X) * 0.5f) - (size * 0.5f)) / NavGrid.DefaultCellSize) * NavGrid.DefaultCellSize;
        float originY = MathF.Floor((((minimum.Y + maximum.Y) * 0.5f) - (size * 0.5f)) / NavGrid.DefaultCellSize) * NavGrid.DefaultCellSize;
        NavGrid grid = NavGrid.Build(NavGeometry.CaptureDungeon(world.Engine, dungeon, originX, originY, size)!, world.Body);

        RoutePastCreatures("dungeon-0x0019-creatures", world, grid);
    }

    /// <summary>
    /// Routes pairs of cells 15 to 60 m apart, then routes each again with a creature
    /// standing at the middle of its longest leg. A route past the creature never walks
    /// nearer walls than the route without it, beyond the planner's allowance, and no
    /// leg of it passes through a wall.
    /// </summary>
    private void RoutePastCreatures(string label, PublishedLandblock world, NavGrid grid)
    {
        var standing = new List<(uint Cell, Vector3 At)>();
        foreach ((uint cell, Vector3 origin) in world.CellOrigins.OrderBy(pair => pair.Key))
        {
            int node = grid.FindWalkableNode(origin, 1f, 1.5f);
            if (node >= 0)
                standing.Add((cell, grid.Position(node)));
        }

        const int wanted = 40;
        float reach = 0.8f + grid.Body.Radius;
        int routes = 0;
        int around = 0;
        int through = 0;
        int endedDifferently = 0;
        int scrapier = 0;
        int throughWalls = 0;
        int nearWalls = 0;
        int plainNearWalls = 0;
        float plainScrape = 0f;
        float passingScrape = 0f;
        float longer = 0f;
        var clock = Stopwatch.StartNew();
        for (int first = 0; first < standing.Count && routes < wanted; first++)
        {
            for (int second = first + 1; second < standing.Count && routes < wanted; second++)
            {
                (uint fromCell, Vector3 from) = standing[first];
                (uint toCell, Vector3 to) = standing[second];
                float apart = Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(to.X, to.Y));
                if (apart < 15f || apart > 60f)
                    continue;
                NavRoute plain = NavRouter.Find(grid, from, to, arrivalRadius: 1f);
                if (plain.Outcome != NavRouteOutcome.Routed || plain.Legs.Count < 2)
                    continue;
                routes++;
                int longest = 1;
                for (int leg = 2; leg < plain.Legs.Count; leg++)
                {
                    if (Vector3.Distance(plain.Legs[leg - 1], plain.Legs[leg])
                        > Vector3.Distance(plain.Legs[longest - 1], plain.Legs[longest]))
                    {
                        longest = leg;
                    }
                }
                Vector3 middle = Vector3.Lerp(plain.Legs[longest - 1], plain.Legs[longest], 0.5f);
                NavRoute passing = NavRouter.Find(grid, from, to, arrivalRadius: 1f, crowd: [new NavAvoidance(middle, reach)]);
                if (passing.Outcome != plain.Outcome || (passing.Reason == "routed") != (plain.Reason == "routed"))
                {
                    endedDifferently++;
                    _output.WriteLine($"  0x{fromCell:X8} to 0x{toCell:X8}: '{plain.Reason}' without the creature, {passing.Outcome} '{passing.Reason}' with it");
                }
                if (passing.Outcome != NavRouteOutcome.Routed)
                    continue;
                if (passing.Crowding < 0.05f)
                    around++;
                else
                    through++;
                if (passing.Scrape > plain.Scrape + 0.25f)
                {
                    scrapier++;
                    _output.WriteLine($"  0x{fromCell:X8} to 0x{toCell:X8}: scrapes {passing.Scrape:0.00} m² past the creature, {plain.Scrape:0.00} m² without it");
                }
                plainScrape += plain.Scrape;
                passingScrape += passing.Scrape;
                longer += passing.Length - plain.Length;
                for (int leg = 1; leg < passing.Legs.Count; leg++)
                {
                    if (!grid.IsOpenLine(passing.Legs[leg - 1], passing.Legs[leg]))
                        throughWalls++;
                    else if (!grid.CanSweep(passing.Legs[leg - 1], passing.Legs[leg]))
                        nearWalls++;
                }
                for (int leg = 1; leg < plain.Legs.Count; leg++)
                {
                    if (grid.IsOpenLine(plain.Legs[leg - 1], plain.Legs[leg]) && !grid.CanSweep(plain.Legs[leg - 1], plain.Legs[leg]))
                        plainNearWalls++;
                }
            }
        }

        _output.WriteLine(
            $"{label}: {routes} routes, each again with a creature at the middle of its longest leg, in {clock.Elapsed.TotalMilliseconds:0} ms: "
            + $"{around} go around it and {through} through it, {longer / Math.Max(1, around + through):0.0} m longer on average; "
            + $"{scrapier} walk nearer walls than without it; {endedDifferently} end differently; "
            + $"scrape {plainScrape:0.0} m² without creatures and {passingScrape:0.0} m² with them; "
            + $"{throughWalls} legs pass through a wall; {nearWalls} legs pass nearer one than the body fits, {plainNearWalls} without creatures");
        Assert.True(routes > 0);
        Assert.Equal(0, throughWalls);
        Assert.Equal(0, scrapier);
    }

    [Fact]
    public void HoltburgReplaysTheLiveWalkFromBesideTheContractBrokerToRenaldTheElder()
    {
        const uint startCell = 0xA9B40162u;
        const uint goalCell = 0xA9B40141u;
        PublishedLandblock world = PublishedLandblock.Load(RequireDatDirectory(), Holtburg, [startCell, goalCell]);
        NavGrid grid = BuildAndReport("holtburg-renald", world);
        Vector3 reportedFrom = FromMapCoordinates(startCell, northSouth: 42.198, eastWest: 33.710, elevation: 0.39);
        Vector3 reportedTo = FromMapCoordinates(goalCell, northSouth: 42.126, eastWest: 33.830, elevation: 0.39);
        _output.WriteLine(
            $"holtburg-renald: reported start {PointText(reportedFrom)}, its cell's origin {OriginText(world.CellOrigins, startCell)}; "
            + $"reported goal {PointText(reportedTo)}, its cell's origin {OriginText(world.CellOrigins, goalCell)}");
        Vector3 from = OnFloor(grid, reportedFrom);
        Vector3 to = OnFloor(grid, reportedTo);
        DescribeNear(grid, "start", from);
        DescribeNear(grid, "goal", to);

        int nearestStart = grid.FindNode(from, NavRouter.StartRadius, NavRouter.StartHeightTolerance);
        int walkableStart = grid.FindWalkableNode(from, NavRouter.StartRadius, NavRouter.StartHeightTolerance);
        int nearestGoal = grid.FindNode(to, 2.5f, 3f);
        _output.WriteLine(
            $"holtburg-renald: start on the floor at {PointText(from)}; the nearest clear node {NodeText(grid, nearestStart)} "
            + $"{(nearestStart >= 0 && !grid.IsOpenLine(from, grid.Position(nearestStart)) ? "is behind a wall" : "can be walked to")}; "
            + $"the nearest walkable node is {NodeText(grid, walkableStart)}");
        _output.WriteLine(
            $"holtburg-renald: goal on the floor at {PointText(to)}; the nearest clear node {NodeText(grid, nearestGoal)} "
            + $"{(nearestGoal >= 0 && !grid.CanSee(nearestGoal, to) ? "cannot see it" : "can see it")}");

        NavRoute route = NavRouter.Find(grid, from, to, arrivalRadius: 2.5f);

        Report("holtburg-renald", grid, route, from, to);
        _output.WriteLine(
            $"holtburg-renald: the route ends at {(route.Path.Count > 0 ? PointText(route.Path[^1]) : "nothing")}: {route.Reason}");
        for (int leg = 1; leg < route.Legs.Count; leg++)
        {
            _output.WriteLine(
                $"  leg {leg}: passes no wall {grid.IsOpenLine(route.Legs[leg - 1], route.Legs[leg])}, "
                + $"keeps the body clear {grid.CanSweep(route.Legs[leg - 1], route.Legs[leg])}");
        }
        WriteImages("holtburg-renald", grid, route);
        WriteReachability(
            "holtburg-renald",
            grid,
            world,
            from,
            to,
            minX: 95f,
            minY: 0f,
            maxX: 165f,
            maxY: 60f,
            bands: [("floor", 92.5f, 95.5f), ("raised", 95.5f, 99f), ("upper", 99f, 104f)]);

        bool[] fromStart = Reachable(grid, grid.FindWalkableNode(from, NavRouter.StartRadius, NavRouter.StartHeightTolerance));
        bool[] fromGoal = Reachable(grid, grid.FindNode(to, 1f, NavRouter.GoalHeightTolerance));
        DrawCharacterMap(grid, fromStart, fromGoal, minX: 131f, minY: 10f, maxX: 147f, maxY: 23f, low: 92.5f, high: 95.5f);
        DescribeGeometryAround(world, minX: 129f, minY: 8f, maxX: 149f, maxY: 25f);

        Vector3 window = OnFloor(grid, FromMapCoordinates(0xA9B40029u, northSouth: 42.138, eastWest: 33.830, elevation: 0.39));
        int windowNode = grid.FindNode(window, 0.5f, 1.5f);
        _output.WriteLine(
            $"holtburg-renald: players use Renald from outdoors at {PointText(window)}, {Vector2.Distance(new Vector2(window.X, window.Y), new Vector2(to.X, to.Y)):0.00} m "
            + $"from him; its node {NodeText(grid, windowNode)} "
            + $"{(windowNode >= 0 && fromStart[windowNode] ? "is reachable" : "is not reachable")}, "
            + $"can see him: {windowNode >= 0 && grid.CanSee(windowNode, to)}");
        if (windowNode >= 0)
        {
            foreach ((int x, int y, float low, float high, float line) in grid.SightBlockers(windowNode, to))
            {
                _output.WriteLine(
                    $"  hidden by a wall piece in column ({x}, {y}) at ({grid.OriginX + ((x + 0.5f) * grid.CellSize):0.00}, "
                    + $"{grid.OriginY + ((y + 0.5f) * grid.CellSize):0.00}) spanning {low:0.00} to {high:0.00}, where the line is at {line:0.00}");
            }
        }

        NavGeometry geometry = Assert.IsType<NavGeometry>(NavGeometry.CaptureLandblock(world.Engine, world.LandblockId));
        int listed = 0;
        foreach ((string source, IReadOnlyList<NavTriangle> triangles) in new[] { ("cell", geometry.CellTriangles), ("object", geometry.ObjectTriangles) })
        {
            foreach (NavTriangle triangle in triangles)
            {
                float lowX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
                float highX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
                float lowY = MathF.Min(triangle.A.Y, MathF.Min(triangle.B.Y, triangle.C.Y));
                float highY = MathF.Max(triangle.A.Y, MathF.Max(triangle.B.Y, triangle.C.Y));
                float lowZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z));
                float highZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));
                if (highX < 138.4f || lowX > 140f || highY < 18.4f || lowY > 20.2f || highZ < 94.2f || lowZ > 96.8f || listed >= 90)
                    continue;
                listed++;
                Vector3 normal = Vector3.Normalize(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A));
                _output.WriteLine(
                    $"  window {source} triangle {PointText(triangle.A)} {PointText(triangle.B)} {PointText(triangle.C)} "
                    + $"normal ({normal.X:0.00}, {normal.Y:0.00}, {normal.Z:0.00})");
            }
        }
        NavGrid slim = NavGrid.Build(geometry, world.Body with { Radius = 0.3f });
        NavRoute slimRoute = NavRouter.Find(slim, from, to, arrivalRadius: 2.5f);
        _output.WriteLine(
            $"holtburg-renald: for a body of radius 0.3 m: {slimRoute.Outcome} ({slimRoute.Reason}), "
            + $"{Math.Max(0, slimRoute.Legs.Count - 1)} legs, {slimRoute.Length:0.0} m");
        foreach (Vector3 leg in slimRoute.Legs)
            _output.WriteLine($"  slim leg {PointText(leg)}");
        if (slimRoute.Outcome == NavRouteOutcome.Routed)
        {
            string slimImage = Path.Combine(ImageDirectory(), "holtburg-renald-slim-route.png");
            NavGridImage.WriteRouteDetail(slimImage, slim, slimRoute, scale: 4);
            _output.WriteLine($"holtburg-renald: image {slimImage}");
        }
        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.InRange(
            Vector2.Distance(new Vector2(route.Path[^1].X, route.Path[^1].Y), new Vector2(window.X, window.Y)),
            0f,
            1.5f);
    }

    /// <summary>
    /// A character map of one floor band, north up, one character per column:
    /// '*' clear and reachable from both ends, 's' from the start only, 'g' from
    /// the goal only, '.' clear but reachable from neither, 'w' too near a wall,
    /// 'e' on a ledge's edge, '#' no standing point in the band.
    /// </summary>
    private void DrawCharacterMap(
        NavGrid grid,
        bool[] fromStart,
        bool[] fromGoal,
        float minX,
        float minY,
        float maxX,
        float maxY,
        float low,
        float high)
    {
        int x0 = (int)MathF.Floor((minX - grid.OriginX) / grid.CellSize);
        int x1 = (int)MathF.Floor((maxX - grid.OriginX) / grid.CellSize);
        int y0 = (int)MathF.Floor((minY - grid.OriginY) / grid.CellSize);
        int y1 = (int)MathF.Floor((maxY - grid.OriginY) / grid.CellSize);
        var header = new StringBuilder("  xmeter  ");
        for (int x = x0; x < x1; x++)
            header.Append(((x - x0) % 4) == 0 ? ((int)(grid.OriginX + (x * grid.CellSize)) % 10).ToString() : " ");
        _output.WriteLine($"  map x from {minX} to {maxX}, y from {maxY} down to {minY}");
        _output.WriteLine(header.ToString());
        for (int y = y1 - 1; y >= y0; y--)
        {
            var line = new StringBuilder($"  y{grid.OriginY + (y * grid.CellSize),6:0.00} ");
            for (int x = x0; x < x1; x++)
            {
                (int first, int count) = grid.NodesInColumn(x, y);
                int chosen = -1;
                for (int node = first; node < first + count; node++)
                {
                    float z = grid.Position(node).Z;
                    if (z >= low && z <= high)
                        chosen = node;
                }
                char mark = chosen < 0 ? '#'
                    : !grid.IsClear(chosen) ? (grid.WallDistance(chosen) < grid.NearestWall ? 'w' : 'e')
                    : fromStart[chosen] && fromGoal[chosen] ? '*'
                    : fromStart[chosen] ? 's'
                    : fromGoal[chosen] ? 'g'
                    : '.';
                line.Append(mark);
            }
            _output.WriteLine(line.ToString());
        }
    }

    /// <summary>Lists the building shells and objects placed in a box.</summary>
    private void DescribeGeometryAround(PublishedLandblock world, float minX, float minY, float maxX, float maxY)
    {
        PhysicsDataCache cache = Assert.IsType<PhysicsDataCache>(world.Engine.DataCache);
        uint prefix = world.LandblockId & 0xFFFF0000u;
        foreach (uint landcellId in cache.BuildingIds)
        {
            if ((landcellId & 0xFFFF0000u) != prefix || cache.GetBuilding(landcellId) is not { } building)
                continue;
            Vector3 at = building.WorldTransform.Translation;
            if (at.X < minX - 20f || at.X > maxX + 20f || at.Y < minY - 20f || at.Y > maxY + 20f)
                continue;
            int polygons = cache.GetGfxObj(building.ModelId) is { } shell
                ? shell.FlatPhysicsBsp?.PolygonTable?.Polygons.Length ?? shell.PhysicsPolygons?.Count ?? 0
                : -1;
            _output.WriteLine(
                $"  building at 0x{landcellId:X8}: model 0x{building.ModelId:X8} at {PointText(at)}, physics polygons {polygons}");
        }
        var owners = new HashSet<uint>(world.Engine.ShadowObjects.CaptureStaticOwnersForLandblock(world.LandblockId));
        foreach (ShadowEntry entry in world.Engine.ShadowObjects.AllEntriesForDebug())
        {
            if (entry.Position.X < minX || entry.Position.X > maxX || entry.Position.Y < minY || entry.Position.Y > maxY)
                continue;
            _output.WriteLine(
                $"  object 0x{entry.EntityId:X8} ({(owners.Contains(entry.EntityId) ? "static" : "not static")}): "
                + $"{entry.CollisionType} gfx 0x{entry.GfxObjId:X8} at {PointText(entry.Position)}, radius {entry.Radius:0.00}, "
                + $"height {entry.CylHeight:0.00}, state 0x{entry.State:X}");
        }
    }

    /// <summary>A point given as its cell and the map coordinates the agent reports, in the published world's frame.</summary>
    private static Vector3 FromMapCoordinates(uint cellId, double northSouth, double eastWest, double elevation)
    {
        int blockX = (int)((cellId >> 24) & 0xFFu);
        int blockY = (int)((cellId >> 16) & 0xFFu);
        return new Vector3(
            (float)((eastWest * 240d) + 84d - ((blockX - 127) * 192d)),
            (float)((northSouth * 240d) + 84d - ((blockY - 127) * 192d)),
            (float)(elevation * 240d));
    }

    /// <summary>The point moved onto the nearest floor within 1.5 m of its height, because reported elevations are rounded.</summary>
    private static Vector3 OnFloor(NavGrid grid, Vector3 point)
    {
        int centreX = (int)MathF.Floor((point.X - grid.OriginX) / grid.CellSize);
        int centreY = (int)MathF.Floor((point.Y - grid.OriginY) / grid.CellSize);
        float nearest = 1.5f;
        float height = point.Z;
        for (int y = centreY - 2; y <= centreY + 2; y++)
        {
            for (int x = centreX - 2; x <= centreX + 2; x++)
            {
                (int first, int count) = grid.NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    float rise = MathF.Abs(grid.Position(node).Z - point.Z);
                    if (rise < nearest)
                    {
                        nearest = rise;
                        height = grid.Position(node).Z;
                    }
                }
            }
        }
        return point with { Z = height };
    }

    private static string PointText(Vector3 point) => $"({point.X:0.00}, {point.Y:0.00}, {point.Z:0.00})";

    private static string NodeText(NavGrid grid, int node) => node < 0 ? "none" : PointText(grid.Position(node));

    private static string OriginText(IReadOnlyDictionary<uint, Vector3> origins, uint cell) =>
        origins.TryGetValue(cell, out Vector3 at) ? PointText(at) : "unknown";

    /// <summary>A point on the terrain of whichever published landblock holds it.</summary>
    private static Vector3 OnTerrain(PhysicsEngine engine, float x, float y)
    {
        foreach (uint landblockId in engine.LandblockIds)
        {
            Assert.True(engine.TryGetLandblockCollision(landblockId, out TerrainSurface terrain, out _, out Vector3 offset));
            float localX = x - offset.X;
            float localY = y - offset.Y;
            if (localX >= 0f && localX < NavGeometry.LandblockSize && localY >= 0f && localY < NavGeometry.LandblockSize)
                return new Vector3(x, y, terrain.SampleSurfacePolygon(localX, localY).Z);
        }
        throw new InvalidOperationException($"No published landblock holds ({x}, {y}).");
    }

    private NavGrid BuildAndReport(string name, PublishedLandblock world)
    {
        var clock = Stopwatch.StartNew();
        NavGeometry geometry = Assert.IsType<NavGeometry>(NavGeometry.CaptureLandblock(world.Engine, world.LandblockId));
        double captureMilliseconds = clock.Elapsed.TotalMilliseconds;
        NavGrid grid = NavGrid.Build(geometry, world.Body);
        NavGridBuildReport report = grid.Report;
        _output.WriteLine(
            $"{name}: published in {world.Milliseconds:0} ms; body radius {world.Body.Radius}, height {world.Body.Height}, "
            + $"step up {world.Body.StepUpHeight}, step down {world.Body.StepDownHeight}");
        _output.WriteLine(
            $"{name}: captured {geometry.CellTriangles.Count} cell triangles, {geometry.ObjectTriangles.Count} object "
            + $"triangles and {geometry.Cylinders.Count} cylinders in {captureMilliseconds:0} ms");
        _output.WriteLine(
            $"{name}: {report.Spans} spans, {report.Nodes} nodes, {report.ClearNodes} clear, built in {report.Milliseconds:0} ms");
        return grid;
    }

    private void Report(string name, NavGrid grid, NavRoute route, Vector3 from, Vector3 to)
    {
        _output.WriteLine(
            $"{name}: {route.Outcome} ({route.Reason}), {route.Path.Count} nodes, {Math.Max(0, route.Legs.Count - 1)} legs, "
            + $"{route.Length:0.0} m, {route.Expansions} expansions in {route.Milliseconds:0} ms");
        foreach (Vector3 leg in route.Legs)
            _output.WriteLine($"  leg ({leg.X:0.00}, {leg.Y:0.00}, {leg.Z:0.00})");
        if (route.Outcome == NavRouteOutcome.Routed)
            return;
        DescribeNear(grid, "start", from);
        DescribeNear(grid, "goal", to);
    }

    private void DescribeNear(NavGrid grid, string label, Vector3 point)
    {
        int centreX = (int)MathF.Floor((point.X - grid.OriginX) / grid.CellSize);
        int centreY = (int)MathF.Floor((point.Y - grid.OriginY) / grid.CellSize);
        for (int y = centreY - 2; y <= centreY + 2; y++)
        {
            for (int x = centreX - 2; x <= centreX + 2; x++)
            {
                (int first, int count) = grid.NodesInColumn(x, y);
                var levels = new StringBuilder();
                for (int node = first; node < first + count; node++)
                {
                    levels.Append(
                        $" {grid.Position(node).Z:0.00}{(grid.IsClear(node) ? "" : "!")}(b{grid.BorderDistance(node)})");
                }
                _output.WriteLine($"  {label} column ({x}, {y}):{(count == 0 ? " none" : levels.ToString())}");
            }
        }
    }

    private void WriteReachability(
        string name,
        NavGrid grid,
        PublishedLandblock world,
        Vector3 from,
        Vector3 to,
        float minX,
        float minY,
        float maxX,
        float maxY,
        (string Band, float Low, float High)[]? bands = null)
    {
        int start = grid.FindNode(from, NavRouter.StartRadius, NavRouter.StartHeightTolerance);
        int goal = grid.FindNode(to, 1f, NavRouter.GoalHeightTolerance);
        bool[] fromStart = Reachable(grid, start);
        bool[] fromGoal = Reachable(grid, goal);
        _output.WriteLine(
            $"{name}: {fromStart.Count(reached => reached)} nodes reachable from the start, "
            + $"{fromGoal.Count(reached => reached)} from the goal");
        foreach ((uint cell, Vector3 at) in world.CellOrigins)
            _output.WriteLine($"{name}: cell 0x{cell:X8} origin ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00})");
        foreach ((string band, float low, float high) in bands ?? [("ground", 11f, 13.4f), ("middle", 12.2f, 15.4f), ("upper", 13.4f, 18f)])
        {
            string path = Path.Combine(ImageDirectory(), $"{name}-reach-{band}.png");
            NavGridImage.WriteReachability(
                path, grid, fromStart, fromGoal, minX, minY, maxX, maxY, low, high, world.CellOrigins.Values, scale: 4);
            _output.WriteLine($"{name}: image {path}");
        }
    }

    private static bool[] Reachable(NavGrid grid, int start)
    {
        var reached = new bool[grid.NodeCount];
        if (start < 0)
            return reached;
        var frontier = new Queue<int>();
        reached[start] = true;
        frontier.Enqueue(start);
        while (frontier.TryDequeue(out int node))
        {
            for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
            {
                int next = grid.Link(node, direction);
                if (next < 0 || reached[next] || !grid.IsClear(next))
                    continue;
                reached[next] = true;
                frontier.Enqueue(next);
            }
        }
        return reached;
    }

    private static string ImageDirectory()
    {
        string directory = System.Environment.GetEnvironmentVariable("ACDREAM_NAV_IMAGE_DIR")
            ?? Path.Combine(Path.GetTempPath(), "acdream-navmesh");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void WriteImages(string name, NavGrid grid, NavRoute? route)
    {
        string directory = ImageDirectory();
        string whole = Path.Combine(directory, $"{name}-landblock.png");
        NavGridImage.WriteLandblock(whole, grid, route);
        _output.WriteLine($"{name}: image {whole}");
        if (route is { Outcome: NavRouteOutcome.Routed })
        {
            string detail = Path.Combine(directory, $"{name}-route.png");
            NavGridImage.WriteRouteDetail(detail, grid, route, scale: 4);
            _output.WriteLine($"{name}: image {detail}");
        }
    }

    private static string RequireDatDirectory()
    {
        string? directory = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Set ACDREAM_DAT_DIR to the installed game files.");
        Assert.True(Directory.Exists(directory), $"The game files are missing: '{directory}'.");
        return directory!;
    }

    /// <summary>Top-down pictures of a grid, north up, a pixel per column.</summary>
    private static class NavGridImage
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static void WriteLandblock(string path, NavGrid grid, NavRoute? route)
        {
            byte[] pixels = Paint(grid, 0, 0, grid.Side, grid.Side);
            if (route is { Outcome: NavRouteOutcome.Routed })
                DrawRoute(pixels, grid.Side, grid.Side, grid, route, 0, 0, scale: 1);
            WritePng(path, grid.Side, grid.Side, pixels);
        }

        public static void WriteRouteDetail(string path, NavGrid grid, NavRoute route, int scale)
        {
            const int margin = 32;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (Vector3 point in route.Path)
            {
                int x = (int)MathF.Floor((point.X - grid.OriginX) / grid.CellSize);
                int y = (int)MathF.Floor((point.Y - grid.OriginY) / grid.CellSize);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            minX = Math.Max(0, minX - margin);
            minY = Math.Max(0, minY - margin);
            maxX = Math.Min(grid.Side - 1, maxX + margin);
            maxY = Math.Min(grid.Side - 1, maxY + margin);
            int width = maxX - minX + 1;
            int height = maxY - minY + 1;
            byte[] small = Paint(grid, minX, minY, width, height);
            byte[] large = new byte[width * scale * height * scale * 3];
            for (int row = 0; row < height * scale; row++)
            {
                for (int column = 0; column < width * scale; column++)
                {
                    int source = (((row / scale) * width) + (column / scale)) * 3;
                    int target = ((row * width * scale) + column) * 3;
                    large[target] = small[source];
                    large[target + 1] = small[source + 1];
                    large[target + 2] = small[source + 2];
                }
            }
            DrawRoute(large, width * scale, height * scale, grid, route, minX, minY, scale);
            WritePng(path, width * scale, height * scale, large);
        }

        public static void WriteReachability(
            string path,
            NavGrid grid,
            bool[] fromStart,
            bool[] fromGoal,
            float minX,
            float minY,
            float maxX,
            float maxY,
            float low,
            float high,
            IEnumerable<Vector3> markers,
            int scale)
        {
            int x0 = Math.Max(0, (int)MathF.Floor((minX - grid.OriginX) / grid.CellSize));
            int y0 = Math.Max(0, (int)MathF.Floor((minY - grid.OriginY) / grid.CellSize));
            int x1 = Math.Min(grid.Side - 1, (int)MathF.Floor((maxX - grid.OriginX) / grid.CellSize));
            int y1 = Math.Min(grid.Side - 1, (int)MathF.Floor((maxY - grid.OriginY) / grid.CellSize));
            int width = x1 - x0 + 1;
            int height = y1 - y0 + 1;
            var pixels = new byte[width * scale * height * scale * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    (int first, int count) = grid.NodesInColumn(x0 + x, y0 + y);
                    int chosen = -1;
                    for (int node = first; node < first + count; node++)
                    {
                        float z = grid.Position(node).Z;
                        if (z >= low && z <= high)
                            chosen = node;
                    }
                    (byte red, byte green, byte blue) colour =
                        chosen < 0 ? (count == 0 ? ((byte)18, (byte)18, (byte)24) : ((byte)55, (byte)55, (byte)65))
                        : !grid.IsClear(chosen) ? ((byte)150, (byte)40, (byte)30)
                        : fromStart[chosen] && fromGoal[chosen] ? ((byte)255, (byte)255, (byte)255)
                        : fromStart[chosen] ? ((byte)240, (byte)200, (byte)40)
                        : fromGoal[chosen] ? ((byte)230, (byte)60, (byte)220)
                        : ((byte)60, (byte)150, (byte)80);
                    int top = (height - 1 - y) * scale;
                    for (int row = top; row < top + scale; row++)
                    {
                        for (int column = x * scale; column < (x + 1) * scale; column++)
                            Set(pixels, ((row * width * scale) + column) * 3, colour.red, colour.green, colour.blue);
                    }
                }
            }
            foreach (Vector3 marker in markers)
            {
                int centreX = (int)((((marker.X - grid.OriginX) / grid.CellSize) - x0) * scale);
                int centreY = (height * scale) - 1 - (int)((((marker.Y - grid.OriginY) / grid.CellSize) - y0) * scale);
                for (int dy = -3; dy <= 3; dy++)
                {
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int px = centreX + dx;
                        int py = centreY + dy;
                        if ((uint)px < (uint)(width * scale) && (uint)py < (uint)(height * scale))
                            Set(pixels, ((py * width * scale) + px) * 3, 0, 230, 255);
                    }
                }
            }
            WritePng(path, width * scale, height * scale, pixels);
        }

        private static byte[] Paint(NavGrid grid, int startX, int startY, int width, int height)
        {
            float low = float.PositiveInfinity;
            float high = float.NegativeInfinity;
            for (int node = 0; node < grid.NodeCount; node++)
            {
                float z = grid.Position(node).Z;
                low = MathF.Min(low, z);
                high = MathF.Max(high, z);
            }
            float span = MathF.Max(1f, high - low);

            var pixels = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                int row = height - 1 - y;
                for (int x = 0; x < width; x++)
                {
                    int offset = ((row * width) + x) * 3;
                    (int first, int count) = grid.NodesInColumn(startX + x, startY + y);
                    if (count == 0)
                    {
                        Set(pixels, offset, 18, 18, 24);
                        continue;
                    }

                    int shown = first + count - 1;
                    int clearLevels = 0;
                    for (int node = first + count - 1; node >= first; node--)
                    {
                        if (!grid.IsClear(node))
                            continue;
                        if (clearLevels == 0)
                            shown = node;
                        clearLevels++;
                    }
                    float t = (grid.Position(shown).Z - low) / span;
                    if (clearLevels == 0)
                        Set(pixels, offset, 130, 40, 30);
                    else if (clearLevels > 1)
                        Set(pixels, offset, (byte)(60 + (100 * t)), (byte)(110 + (100 * t)), 240);
                    else
                        Set(pixels, offset, (byte)(40 + (140 * t)), (byte)(100 + (150 * t)), (byte)(50 + (90 * t)));
                }
            }
            return pixels;
        }

        private static void DrawRoute(
            byte[] pixels,
            int width,
            int height,
            NavGrid grid,
            NavRoute route,
            int startX,
            int startY,
            int scale)
        {
            foreach (Vector3 point in route.Path)
                Plot(pixels, width, height, grid, point, startX, startY, scale, 255, 220, 0, radius: scale / 2);
            foreach (Vector3 leg in route.Legs)
                Plot(pixels, width, height, grid, leg, startX, startY, scale, 255, 255, 255, radius: scale + 1);
        }

        private static void Plot(
            byte[] pixels,
            int width,
            int height,
            NavGrid grid,
            Vector3 point,
            int startX,
            int startY,
            int scale,
            byte red,
            byte green,
            byte blue,
            int radius)
        {
            int centreX = (int)((((point.X - grid.OriginX) / grid.CellSize) - startX) * scale);
            int centreY = height - 1 - (int)((((point.Y - grid.OriginY) / grid.CellSize) - startY) * scale);
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = centreX + dx;
                    int y = centreY + dy;
                    if ((uint)x < (uint)width && (uint)y < (uint)height)
                        Set(pixels, ((y * width) + x) * 3, red, green, blue);
                }
            }
        }

        private static void Set(byte[] pixels, int offset, byte red, byte green, byte blue)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
        }

        private static void WritePng(string path, int width, int height, byte[] rgb)
        {
            using FileStream file = File.Create(path);
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            header[9] = 2;
            WriteChunk(file, "IHDR", header);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (int row = 0; row < height; row++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgb, row * width * 3, width * 3);
                }
            }
            WriteChunk(file, "IDAT", compressed.ToArray());
            WriteChunk(file, "IEND", []);
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var scratch = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(scratch, data.Length);
            stream.Write(scratch);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in typeBytes)
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            foreach (byte value in data)
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            BinaryPrimitives.WriteUInt32BigEndian(scratch, crc ^ 0xFFFFFFFFu);
            stream.Write(scratch);
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }
}
