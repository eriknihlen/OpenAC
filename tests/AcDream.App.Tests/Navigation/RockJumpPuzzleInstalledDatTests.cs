using System.Numerics;
using AcDream.Content;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Navigation;

/// <summary>
/// Deewain's rock jump puzzle, built from the installed game files with the rocks the
/// server places in it, and climbed: every rock top is floor the grid knows, and a
/// route leaves one rock for the next.
///
/// The rocks belong to no landblock, so the game files alone hold none of them. The
/// server places one of seven sets; this is the set a live server placed, taken from
/// what the client reported seeing and matched against the world data.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class RockJumpPuzzleInstalledDatTests
{
    private const uint Landblock = 0x7E03FFFFu;

    /// <summary>The three rocks the puzzle is built from, as the server describes them.</summary>
    private const uint SmallRock = 0x02001A44u;
    private const uint WideRock = 0x02001A43u;
    private const uint TallRock = 0x02001A45u;

    /// <summary>Where the character stood at the top, as the client reported it.</summary>
    private static readonly Vector3 TopPlatform = new(100.000359f, -234.453659f, 24.004999f);

    /// <summary>
    /// The rocks of the set the live server placed, lowest first: which rock, the cell
    /// it stands in, and where it stands.
    /// </summary>
    private static readonly (uint Setup, uint Cell, Vector3 At)[] Rocks =
    [
        (SmallRock, 0x7E030142u, new Vector3(101.230f, -274.100f, -2.100f)),
        (SmallRock, 0x7E030124u, new Vector3(94.870f, -273.130f, -0.360f)),
        (SmallRock, 0x7E030142u, new Vector3(103.420f, -269.500f, -0.230f)),
        (TallRock, 0x7E030407u, new Vector3(92.590f, -265.000f, 1.130f)),
        (TallRock, 0x7E03040Eu, new Vector3(97.890f, -268.780f, 2.130f)),
        (TallRock, 0x7E030407u, new Vector3(94.870f, -263.130f, 5.640f)),
        (TallRock, 0x7E03061Fu, new Vector3(113.300f, -258.000f, 6.450f)),
        (WideRock, 0x7E030610u, new Vector3(92.590f, -255.000f, 7.130f)),
        (WideRock, 0x7E030618u, new Vector3(98.770f, -254.100f, 9.900f)),
        (WideRock, 0x7E0308C6u, new Vector3(86.700f, -248.000f, 12.450f)),
        (SmallRock, 0x7E0308CCu, new Vector3(100.630f, -245.900f, 12.790f)),
        (WideRock, 0x7E0308D0u, new Vector3(107.410f, -245.000f, 13.130f)),
        (SmallRock, 0x7E0308C6u, new Vector3(93.780f, -246.800f, 13.600f)),
        (SmallRock, 0x7E0308CBu, new Vector3(103.420f, -240.500f, 16.770f)),
        (WideRock, 0x7E03096Au, new Vector3(101.230f, -236.100f, 18.220f)),
        (WideRock, 0x7E030966u, new Vector3(88.770f, -241.130f, 18.760f)),
        (TallRock, 0x7E03096Eu, new Vector3(106.220f, -243.200f, 21.600f)),
    ];

    /// <summary>The cells the puzzle climbs through, so their positions can be reported.</summary>
    private static readonly uint[] PuzzleCells =
    [
        0x7E030124u, 0x7E030142u, 0x7E030407u, 0x7E03040Eu, 0x7E030414u, 0x7E030610u,
        0x7E030618u, 0x7E03061Fu, 0x7E0308C6u, 0x7E0308CBu, 0x7E0308CCu, 0x7E0308D0u,
        0x7E030966u, 0x7E03096Au, 0x7E03096Eu, 0x7E0309AFu,
    ];

    /// <summary>The region the grid covers: the whole climb, and the platform above it.</summary>
    private const float RegionOriginX = 76f;
    private const float RegionOriginY = -288f;
    private const float RegionSize = 64f;

    private readonly ITestOutputHelper _output;

    public RockJumpPuzzleInstalledDatTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheRockJumpPuzzleIsClimbedRockByRock()
    {
        string datDirectory = RequireDatDirectory();
        PublishedLandblock world = PublishedLandblock.Load(datDirectory, Landblock, PuzzleCells);
        var placed = new HashSet<uint>(PlaceRocks(datDirectory, world));

        NavGeometry withoutRocks =
            NavGeometry.CaptureDungeon(world.Engine, Landblock, RegionOriginX, RegionOriginY, RegionSize)!;
        NavGeometry geometry = NavGeometry.CaptureDungeon(
            world.Engine,
            Landblock,
            RegionOriginX,
            RegionOriginY,
            RegionSize,
            standsOn: placed.Contains)!;
        NavGrid bare = NavGrid.Build(withoutRocks, world.Body);
        NavGrid grid = NavGrid.Build(geometry, world.Body);

        _output.WriteLine(
            $"{geometry.ObjectTriangles.Count} object triangles from {geometry.ObjectIds.Count} rocks; "
            + $"{bare.Report.Nodes} nodes without them, {grid.Report.Nodes} with");

        var tops = new Vector3[Rocks.Length];
        for (int index = 0; index < Rocks.Length; index++)
        {
            (uint setup, uint cell, Vector3 at) = Rocks[index];
            (int node, float height) = RockTop(grid, at);
            tops[index] = node >= 0 ? grid.Position(node) : at;
            _output.WriteLine(
                $"rock {index + 1,2} setup 0x{setup:X8} cell 0x{cell:X8} at ({at.X,7:0.00} {at.Y,8:0.00} {at.Z,6:0.00}): "
                + (node < 0
                    ? "NO NODE"
                    : $"top z {height,6:0.00}, {(grid.IsClear(node) ? "clear" : "perch")}, "
                        + $"border {grid.BorderDistance(node)}"));
            Assert.True(node >= 0, $"the grid put no floor on rock {index + 1}");
        }

        // Every hop the puzzle asks for: each rock to a rock near it, no higher than a jump.
        NavLeapAbility climber = Climber;
        int near = 0;
        int routed = 0;
        for (int from = 0; from < Rocks.Length; from++)
        {
            var missed = new List<string>();
            int reached = 0;
            int candidates = 0;
            for (int to = 0; to < Rocks.Length; to++)
            {
                if (to == from)
                    continue;
                float along = Vector2.Distance(Flat(tops[from]), Flat(tops[to]));
                float rise = tops[to].Z - tops[from].Z;
                if (along > 12f || rise > 6.4f || rise < -11f)
                    continue;
                candidates++;
                if (NavRouter.Find(grid, tops[from], tops[to], 0.6f, leaps: climber).Reason == "routed")
                    reached++;
                else
                    missed.Add($"{to + 1}({along:0.0}m,{rise:+0.0;-0.0}m)");
            }
            near += candidates;
            routed += reached;
            _output.WriteLine(
                $"from rock {from + 1,2}: {reached}/{candidates} near rocks reached"
                + (missed.Count == 0 ? "" : $"; missed {string.Join(" ", missed)}"));
        }
        _output.WriteLine($"{routed} of {near} near-rock hops routed");

        NavRoute up = NavRouter.Find(grid, FloorBelowFirstRock(grid), tops[^1], 0.6f, leaps: climber);
        _output.WriteLine($"the floor to the highest rock: {up.Reason}, {up.Leaps.Count} leaps");

        // A rock top holds no floor to walk on, so a body standing anywhere on it must be
        // able to take the rock's jumps. Rock 12 to rock 11 is level and a jump apart.
        NavRoute across = NavRouter.Find(grid, tops[11], tops[10], 0.6f, leaps: climber);

        Assert.NotEmpty(geometry.ObjectIds);
        Assert.True(across.Reason == "routed", $"no route leaves rock 12 for rock 11: {across.Reason}");
        Assert.True(up.Reason == "routed", $"no route climbs the puzzle: {up.Reason}");
        Assert.True(routed >= 55, $"only {routed} of {near} near-rock hops routed");
    }

    /// <summary>
    /// Places the rocks the server placed into the world, the way the client places an
    /// object it is told about: its parts' collision, at its own position.
    /// </summary>
    private uint[] PlaceRocks(string datDirectory, PublishedLandblock world)
    {
        using var dats = new BoundedTestDatCollection(datDirectory);
        var bounded = (IDatReaderWriter)dats;
        PhysicsDataCache cache = world.Engine.DataCache!;
        var prepared = new Dictionary<uint, FlatGfxObjCollisionAsset>();

        // Collision the way the client's prepared content gives it: gameplay is not
        // allowed to read a parsed graph out of the game files.
        FlatGfxObjCollisionAsset? Prepare(uint gfxObjId)
        {
            if (prepared.TryGetValue(gfxObjId, out FlatGfxObjCollisionAsset? already))
                return already;
            if (bounded.Get<GfxObj>(gfxObjId) is not { } mesh)
                return null;
            FlatGfxObjCollisionAsset asset = FlatCollisionAssetBuilder.FlattenGfxObj(mesh);
            prepared[gfxObjId] = asset;
            cache.CacheGfxObj(gfxObjId, asset);
            return asset;
        }

        bool HasPhysicsBsp(uint gfxObjId) =>
            Prepare(gfxObjId) is { } asset && asset.PhysicsBsp.RootIndex >= 0;

        ShadowPartGeometry? PartBounds(uint gfxObjId) =>
            Prepare(gfxObjId) is { } asset && asset.PhysicsBsp.RootIndex >= 0
                ? ShadowPartGeometry.Create(
                    asset.PhysicsBsp.Nodes[asset.PhysicsBsp.RootIndex].BoundingSphere,
                    asset.VisualBounds)
                : null;

        var ids = new uint[Rocks.Length];
        var shapesBySetup = new Dictionary<uint, IReadOnlyList<ShadowShape>>();
        for (int index = 0; index < Rocks.Length; index++)
        {
            (uint setupId, uint cell, Vector3 at) = Rocks[index];
            if (!shapesBySetup.TryGetValue(setupId, out IReadOnlyList<ShadowShape>? shapes))
            {
                Setup setup = Assert.IsType<Setup>(bounded.Get<Setup>(setupId));
                for (int part = 0; part < setup.Parts.Count; part++)
                    _ = Prepare((uint)setup.Parts[part]);
                shapes = ShadowShapeBuilder.FromSetup(setup, 1f, HasPhysicsBsp, physicsBspBounds: PartBounds);
                shapesBySetup[setupId] = shapes;
                int bsps = 0;
                int cylinders = 0;
                foreach (ShadowShape shape in shapes)
                {
                    if (shape.CollisionType == ShadowCollisionType.Cylinder)
                        cylinders++;
                    else
                        bsps++;
                }
                _output.WriteLine(
                    $"rock setup 0x{setupId:X8}: {setup.Parts.Count} parts, "
                    + $"{shapes.Count} shapes (bsp {bsps}, cylinder {cylinders})");
            }

            uint entityId = 0x80050690u + (uint)index;
            ids[index] = entityId;
            world.Engine.ShadowObjects.RegisterMultiPart(
                entityId,
                at,
                Quaternion.Identity,
                shapes,
                state: (uint)(PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.IgnoreCollisions),
                flags: EntityCollisionFlags.HasWeenie,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: Landblock,
                seedCellId: cell,
                isStatic: false);
        }

        int registered = 0;
        foreach (ShadowEntry entry in world.Engine.ShadowObjects.AllEntriesForDebug())
        {
            if (Array.IndexOf(ids, entry.EntityId) >= 0)
                registered++;
        }
        _output.WriteLine($"placed {ids.Length} rocks as {registered} collision parts");
        return ids;
    }

    private static Vector2 Flat(Vector3 at) => new(at.X, at.Y);

    /// <summary>The floor the grid put on top of a rock: the highest node just above it.</summary>
    private static (int Node, float Height) RockTop(NavGrid grid, Vector3 at)
    {
        int best = -1;
        float height = float.NegativeInfinity;
        for (int node = 0; node < grid.NodeCount; node++)
        {
            Vector3 position = grid.Position(node);
            float dx = position.X - at.X;
            float dy = position.Y - at.Y;
            if ((dx * dx) + (dy * dy) > 1.2f * 1.2f)
                continue;
            if (position.Z <= height || position.Z < at.Z - 0.3f || position.Z > at.Z + 1.5f)
                continue;
            height = position.Z;
            best = node;
        }
        return (best, height);
    }

    /// <summary>The floor under the lowest rock, where a climb begins.</summary>
    private static Vector3 FloorBelowFirstRock(NavGrid grid)
    {
        Vector3 at = Rocks[0].At;
        int best = -1;
        float height = float.NegativeInfinity;
        for (int node = 0; node < grid.NodeCount; node++)
        {
            Vector3 position = grid.Position(node);
            if (Vector2.Distance(Flat(position), Flat(at)) > 6f)
                continue;
            if (position.Z > at.Z || position.Z <= height || !grid.IsClear(node))
                continue;
            height = position.Z;
            best = node;
        }
        return best >= 0 ? grid.Position(best) : at with { Z = at.Z - 2f };
    }

    /// <summary>A character who jumps as high as the one that walked the puzzle.</summary>
    private static NavLeapAbility Climber =>
        new(
            WalkSpeed: 3.12f,
            RunSpeed: 7.3f,
            FullJumpHeight: 6.42f,
            MaximumDrop: 12f,
            Physics: new NavLeapPhysics(StepSeconds: 1f / 30f, Elasticity: 0.05f, Friction: 0.95f));

    private static string RequireDatDirectory()
    {
        string? directory = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Set ACDREAM_DAT_DIR to the installed game files.");
        Assert.True(Directory.Exists(directory), $"The game files are missing: '{directory}'.");
        return directory!;
    }
}
