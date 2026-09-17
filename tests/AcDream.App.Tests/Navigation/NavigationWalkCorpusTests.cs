using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using AcDream.Runtime.Navigation;
using AcDream.App.Tests.Rendering;
using AcDream.Core.Navigation;
using AcDream.Runtime.Gameplay;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Navigation;

/// <summary>
/// Walks a fixed set of routes through real dungeons and towns from the installed
/// game files, start to goal, with the walk controller and a simulated character,
/// and compares what happened with the record kept beside this file: big and small
/// dungeons, a closed door that opens, a locked door on the only way and one with
/// another way, a walk from outdoors into a building, and walks from one building
/// out and into another. Each walk runs twice, cutting corners and turning in place
/// at them, and the character must never pass through a wall. After a change meant
/// to alter the walks, record them again with ACDREAM_UPDATE_WALK_CORPUS=1.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class NavigationWalkCorpusTests
{
    private const float Frame = 1f / 30f;
    private const float MaximumSeconds = 900f;
    private const uint Goal = 0x50000001u;
    private const uint DoorId = 0x7A000001u;
    private const float DoorRadius = 0.8f;
    private const string RecordFile = "NavigationWalkCorpus.txt";

    /// <summary>
    /// A character that runs 10 m/s and walks 3.12 m/s, and turns 1.5 radians a
    /// second at a walk and half again as fast standing or running.
    /// </summary>
    private const float RunSpeed = 10f;
    private const float WalkSpeed = 3.12f;
    private const float RunTurnDegreesPerSecond = 2.25f * (180f / MathF.PI);
    private const float WalkTurnDegreesPerSecond = 1.5f * (180f / MathF.PI);

    private static readonly uint[] Holtburg = [0xA9B4FFFFu, 0xAAB4FFFFu, 0xA9B3FFFFu, 0xAAB3FFFFu];

    /// <summary>The 3x3 landblocks around Holtburg, as many as the headless host keeps loaded around a character.</summary>
    private static readonly uint[] AroundHoltburg =
    [
        0xA9B4FFFFu, 0xA8B3FFFFu, 0xA9B3FFFFu, 0xAAB3FFFFu, 0xA8B4FFFFu,
        0xAAB4FFFFu, 0xA8B5FFFFu, 0xA9B5FFFFu, 0xAAB5FFFFu,
    ];

    /// <summary>The walks, those in the same landblocks together, so each world is published once.</summary>
    private static readonly WalkCase[] Cases =
    [
        new("big dungeon 0x0019, a north corridor to a far hall", [0x0019FFFFu], 0x00190215u, new(90f, -890f, 12.005f), new(80f, -120f, 6.005f), Dungeon: true),
        new("big dungeon 0x200F, across the mines", [0x200FFFFFu], 0x200F0480u, new(80f, -807f, -59.595f), new(430f, -477f, -59.595f), Dungeon: true),
        new("big dungeon 0x039E, down 42 m", [0x039EFFFFu], 0x039E02A8u, new(70f, -810f, 0.005f), new(30f, -240f, -41.995f), Dungeon: true),
        new("small dungeon 0x019E, the entrance to an upper room", [0x019EFFFFu], 0x019E0100u, new(0.38f, -0.13f, 0f), new(20.38f, -39.63f, 6f), Dungeon: true),
        new("small dungeon 0x0156, the bottom level to the top", [0x0156FFFFu], 0x01560100u, new(0.13f, -50.13f, -18f), new(39.88f, -2.63f, 0f), Dungeon: true),
        new("small dungeon 0x0156, corridors to a six-way room", [0x0156FFFFu], 0x01560139u, new(40.32f, -26.23f, -12f), new(59.88f, -40.13f, -6f), Dungeon: true),
        new("dungeon 0x0156, a closed door on the route that opens", [0x0156FFFFu], 0x01560100u, new(0.13f, -50.13f, -18f), new(46.38f, -29.38f, -12f), Dungeon: true, new CorpusDoor(new(51.38f, -27.63f, -12f), Locked: false)),
        new("dungeon 0x0156, a locked door on the only way", [0x0156FFFFu], 0x01560100u, new(0.13f, -50.13f, -18f), new(59.88f, -40.13f, -6f), Dungeon: true, new CorpusDoor(new(51.38f, -27.63f, -12f), Locked: true)),
        new("dungeon 0x01F8, across the mite maze", [0x01F8FFFFu], 0x01F80100u, new(109.88f, -40.13f, -6f), new(70.13f, -123.13f, 0f), Dungeon: true),
        new("dungeon 0x01F8, a locked door with another way", [0x01F8FFFFu], 0x01F80100u, new(109.88f, -40.13f, -6f), new(110.63f, -72.63f, 0f), Dungeon: true, new CorpusDoor(new(111.38f, -68.63f, 0f), Locked: true)),
        new("Holtburg, outdoors into a building", Holtburg, 0xA9B40029u, new(139.2f, 21.12f, 94f), new(79.63f, 37.38f, 94f), Dungeon: false),
        new("Holtburg, out of one building and into another", Holtburg, 0xA9B4016Au, new(79.63f, 37.38f, 94f), new(161.88f, 7.63f, 94f), Dungeon: false),
        new("Holtburg, beside the Contract Broker to Renald the Elder", Holtburg, 0xA9B40162u, new(110.4f, 35.52f, 94f), new(139.2f, 18.24f, 94f), Dungeon: false),
        new("Yaraq, the archmage's room out and up to the healer's deck", [0x7D64FFFFu], 0x7D64012Eu, new(83.96f, 90.86f, 15.205f), new(85.92f, 138.564f, 15.605f), Dungeon: false),
        new("around Holtburg, west to east across three landblocks in stages", AroundHoltburg, 0xA8B40024u, new(-100f, 100f, float.NaN), new(290f, 100f, float.NaN), Dungeon: false),
    ];

    private readonly ITestOutputHelper _output;

    public NavigationWalkCorpusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheCorpusIsWalkedAsRecorded() => WalkTheCorpus(asHeadless: false);

    /// <summary>
    /// The same walks over the collision the headless host loads, which must walk exactly as
    /// the client's do. This never records the corpus again.
    /// </summary>
    [Fact]
    public void TheCorpusIsWalkedAsRecordedOverTheCollisionTheHeadlessHostLoads() => WalkTheCorpus(asHeadless: true);

    private void WalkTheCorpus(bool asHeadless)
    {
        string? datDirectory = InstalledDatTestPath.Resolve();
        if (datDirectory is null)
        {
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }
        var lines = new List<string>();
        var throughWalls = new List<string>();
        string? publishedFor = null;
        PublishedLandblock? world = null;
        foreach (WalkCase walk in Cases)
        {
            string landblocks = string.Join(",", walk.Landblocks.Select(id => id.ToString("X8", CultureInfo.InvariantCulture)));
            if (landblocks != publishedFor)
            {
                world = null;
                world = PublishedLandblock.Load(datDirectory, walk.Landblocks, [], asHeadless);
                publishedFor = landblocks;
            }
            WalkResult cutting = Walk(walk, world!, cutCorners: true);
            WalkResult standing = Walk(walk, world!, cutCorners: false);
            lines.Add(walk.Name);
            lines.Add($"  cutting corners:  {cutting.Text}");
            lines.Add($"  turning in place: {standing.Text}");
            _output.WriteLine($"{walk.Name}\n  cutting corners:  {cutting.Text}\n  turning in place: {standing.Text}");
            if (cutting.ThroughWalls > 0 || standing.ThroughWalls > 0)
                throughWalls.Add(walk.Name);
        }

        CompareWithRecord(lines, mayRecord: !asHeadless);
        Assert.True(throughWalls.Count == 0, $"The character passed through a wall walking: {string.Join("; ", throughWalls)}");
    }

    private static WalkResult Walk(WalkCase walk, PublishedLandblock world, bool cutCorners)
    {
        walk = walk with { Start = OnTheGround(world, walk.Start), Goal = OnTheGround(world, walk.Goal) };
        var said = new List<string>();
        NavigationWalkControllerTests.FakeDoors? doors = walk.Door is { } door
            ? new NavigationWalkControllerTests.FakeDoors(
                new NavigationDoor(DoorId, "Door", door.At, DoorRadius),
                new Vector2(door.At.X, door.At.Y),
                opens: !door.Locked)
            {
                LockedWhenAppraised = door.Locked,
            }
            : null;
        var body = new NavigationWalkControllerTests.SimulatedBody(walk.Start)
        {
            CellId = walk.StartCell,
            RunSpeed = RunSpeed,
            WalkSpeed = WalkSpeed,
            TurnSpeed = RunTurnDegreesPerSecond,
            WalkTurnSpeed = WalkTurnDegreesPerSecond,
            Turning = cutCorners
                ? new RuntimeRouteTurning(RunSpeed, RunTurnDegreesPerSecond)
                : null,
            Obstacle = walk.Door is { } blocking ? (new Vector2(blocking.At.X, blocking.At.Y), DoorRadius) : null,
            ObstacleActive = doors is null ? null : () => !doors.Open,
        };
        if (doors is not null)
            doors.AcceptsUse = () => body.StillFrames >= 1;
        uint landblock = walk.StartCell >> 16;
        var controller = new NavigationWalkController(
            world.Engine,
            body,
            new NavigationWalkControllerTests.Goals { [Goal] = walk.Goal },
            said.Add,
            doors,
            walk.Dungeon ? cell => cell >> 16 == landblock && (cell & 0xFFFFu) >= 0x100u : null);

        controller.WalkTo(Goal);
        var clock = Stopwatch.StartNew();
        int offFloor = 0;
        int throughWalls = 0;
        NavigationWalkReport report = controller.Report;
        while (body.Seconds < MaximumSeconds && clock.Elapsed < TimeSpan.FromMinutes(5))
        {
            // A tick comes only once a search under way has finished, so each walk takes the same ticks however long it plans.
            while (controller.IsSearching && !controller.SearchFinished && clock.Elapsed < TimeSpan.FromMinutes(5))
                Thread.Sleep(1);
            controller.Tick(Frame);
            report = controller.Report;
            if (report.State is not (NavigationWalkState.Planning or NavigationWalkState.Walking or NavigationWalkState.Waiting))
                break;
            if (report.State == NavigationWalkState.Planning || controller.IsSearching)
                continue;
            Vector3 before = body.Position;
            body.Integrate(Frame);
            if (controller.Grid is not { } grid || body.Position == before)
                continue;
            float floor = FloorUnder(grid, body.Position);
            if (float.IsNaN(floor))
            {
                offFloor++;
                continue;
            }
            body.Place(body.Position with { Z = floor });
            if (!grid.IsOpenLine(before, body.Position))
                throughWalls++;
        }

        string route = said.FirstOrDefault(line => line.StartsWith("Route: ", StringComparison.Ordinal)) is { } planned
            ? string.Join(", ", planned["Route: ".Length..].Split(", ").Take(2))
            : "no route";
        string corners = said.LastOrDefault(line => line.Contains(": ran around ", StringComparison.Ordinal)) is { } counted
            ? counted[(counted.IndexOf(": ran around ", StringComparison.Ordinal) + 2)..]
            : "no corners";
        string ending = report.State == NavigationWalkState.Arrived ? string.Empty : $" ({report.Reason})";
        string doorText = doors is null ? string.Empty : $", door used {doors.Uses} and appraised {doors.Appraisals} times";
        int stages = said.Count(line => line.Contains(" walked; planning the next toward the goal", StringComparison.Ordinal));
        string stageText = stages == 0 ? string.Empty : $", {stages} stages walked before the last";
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"{report.State}{ending}, {report.RemainingMeters:0.0} m left, replans {report.Replans}; route {route}; "
            + $"{body.Seconds:0.0} s, {body.TravelStops} stops, {body.WalkingSeconds:0.0} s walking; {corners}{doorText}{stageText}; "
            + $"{offFloor} frames off the floor, {throughWalls} through a wall");
        return new WalkResult(text, throughWalls);
    }

    /// <summary>A point given no height stands on the terrain under it.</summary>
    private static Vector3 OnTheGround(PublishedLandblock world, Vector3 point) =>
        float.IsNaN(point.Z)
            ? point with { Z = Assert.NotNull(world.Engine.SampleTerrainZ(point.X, point.Y)) }
            : point;

    /// <summary>The height of the floor in a point's column within a step of it, or NaN where there is none.</summary>
    private static float FloorUnder(NavGrid grid, Vector3 point)
    {
        (int first, int count) = grid.NodesInColumn(
            (int)MathF.Floor((point.X - grid.OriginX) / grid.CellSize),
            (int)MathF.Floor((point.Y - grid.OriginY) / grid.CellSize));
        float floor = float.NaN;
        float nearest = MathF.Max(grid.Body.StepUpHeight, grid.Body.StepDownHeight) + 0.1f;
        for (int node = first; node < first + count; node++)
        {
            float rise = MathF.Abs(grid.Position(node).Z - point.Z);
            if (rise < nearest)
            {
                nearest = rise;
                floor = grid.Position(node).Z;
            }
        }
        return floor;
    }

    private static void CompareWithRecord(IReadOnlyList<string> lines, bool mayRecord, [CallerFilePath] string source = "")
    {
        string path = Path.Combine(Path.GetDirectoryName(source)!, RecordFile);
        bool recorded = File.Exists(path);
        bool update = mayRecord && System.Environment.GetEnvironmentVariable("ACDREAM_UPDATE_WALK_CORPUS") == "1";
        if (!recorded && !mayRecord)
        {
            Assert.Fail($"There is no record of the corpus in {path}; record it with the client's walks first.");
            return;
        }
        if (!recorded || update)
        {
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            Assert.True(update, $"There was no record of the corpus, so it was recorded in {path}; review it and run again.");
            return;
        }
        string[] expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var differences = new StringBuilder();
        for (int index = 0; index < Math.Max(expected.Length, lines.Count); index++)
        {
            string want = index < expected.Length ? expected[index] : "(nothing)";
            string got = index < lines.Count ? lines[index] : "(nothing)";
            if (want != got)
                differences.AppendLine($"line {index + 1}\n  recorded: {want}\n  walked:   {got}");
        }
        Assert.True(
            differences.Length == 0,
            $"The walks differ from {RecordFile}. If the change is meant, record them again with ACDREAM_UPDATE_WALK_CORPUS=1.\n{differences}");
    }

    private sealed record WalkCase(
        string Name,
        uint[] Landblocks,
        uint StartCell,
        Vector3 Start,
        Vector3 Goal,
        bool Dungeon,
        CorpusDoor? Door = null);

    /// <summary>A door standing across a doorway on the way, locked or opening when used.</summary>
    private sealed record CorpusDoor(Vector3 At, bool Locked);

    private readonly record struct WalkResult(string Text, int ThroughWalls);
}
