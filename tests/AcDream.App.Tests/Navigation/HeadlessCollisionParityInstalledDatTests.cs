using System.Globalization;
using System.Numerics;
using System.Text;
using AcDream.App.Tests.Rendering;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Navigation;

/// <summary>
/// The headless host loads a landblock's objects and collision its own way, with no
/// renderer's streaming. A walk there must see the world a walk in the client sees, so
/// the grid built over each landblock must come out the same both ways: a town with
/// buildings, a dungeon, and the landblocks the navigation examples use.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class HeadlessCollisionParityInstalledDatTests
{
    private readonly ITestOutputHelper _output;

    public HeadlessCollisionParityInstalledDatTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0x7E03FFFFu)]
    [InlineData(0xA9B4FFFFu)]
    [InlineData(0x0156FFFFu)]
    [InlineData(0x7D64FFFFu)]
    public void AGridOverALandblockTheHeadlessHostLoadsMatchesTheClients(uint landblockId)
    {
        string? datDirectory = InstalledDatTestPath.Resolve();
        if (datDirectory is null)
        {
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }

        PublishedLandblock client = PublishedLandblock.Load(datDirectory, [landblockId], []);
        PublishedLandblock headless = PublishedLandblock.Load(datDirectory, [landblockId], [], asHeadless: true);
        (NavGrid clientGrid, string clientObjects) = Built(client);
        (NavGrid headlessGrid, string headlessObjects) = Built(headless);

        string clientNodes = Nodes(clientGrid);
        string headlessNodes = Nodes(headlessGrid);
        _output.WriteLine($"client:   {clientGrid.Report.Nodes} nodes, {clientGrid.Report.ClearNodes} clear; {clientObjects}");
        _output.WriteLine($"headless: {headlessGrid.Report.Nodes} nodes, {headlessGrid.Report.ClearNodes} clear; {headlessObjects}");
        Assert.True(
            clientObjects == headlessObjects,
            $"The collision objects differ.\n  client:   {clientObjects}\n  headless: {headlessObjects}");
        Assert.True(
            clientNodes == headlessNodes,
            $"The grids differ: the client's has {clientGrid.Report.Nodes} nodes, {clientGrid.Report.ClearNodes} clear; "
            + $"the headless host's has {headlessGrid.Report.Nodes}, {headlessGrid.Report.ClearNodes} clear.");
    }

    /// <summary>A grid over the landblock's terrain square and every cell it holds, and what its collision objects came to.</summary>
    private static (NavGrid Grid, string Objects) Built(PublishedLandblock world)
    {
        PhysicsEngine engine = world.Engine;
        Assert.True(engine.TryGetLandblockCollision(world.LandblockId, out _, out _, out Vector3 offset));
        var minimum = new Vector2(offset.X, offset.Y);
        var maximum = minimum + new Vector2(NavGeometry.LandblockSize);
        if (NavGeometry.TryMeasureCells(engine, world.LandblockId, out Vector2 cellsMinimum, out Vector2 cellsMaximum))
        {
            minimum = Vector2.Min(minimum, cellsMinimum);
            maximum = Vector2.Max(maximum, cellsMaximum);
        }
        float size = MathF.Ceiling(MathF.Max(maximum.X - minimum.X, maximum.Y - minimum.Y) / 16f) * 16f;
        NavGeometry geometry = Assert.IsType<NavGeometry>(NavGeometry.Capture(engine, minimum.X, minimum.Y, size));
        return (NavGrid.Build(geometry, world.Body), Objects(engine));
    }

    /// <summary>Every collision part in the world, by what it is and where it stands, in a fixed order.</summary>
    private static string Objects(PhysicsEngine engine)
    {
        var entries = new List<ShadowEntry>();
        engine.ShadowObjects.CaptureEntries(entries);
        IEnumerable<string> parts = entries
            .Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.CollisionType}:{entry.GfxObjId:X8}@{entry.Position.X:0.00},{entry.Position.Y:0.00},{entry.Position.Z:0.00}"))
            .Distinct()
            .Order(StringComparer.Ordinal);
        return $"{entries.Count} parts; {Hash(string.Join(";", parts))}";
    }

    private static string Nodes(NavGrid grid)
    {
        var text = new StringBuilder();
        for (int node = 0; node < grid.NodeCount; node++)
        {
            Vector3 at = grid.Position(node);
            text.Append(CultureInfo.InvariantCulture, $"{at.X:0.00},{at.Y:0.00},{at.Z:0.000},{(grid.IsClear(node) ? 1 : 0)};");
        }
        return Hash(text.ToString());
    }

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
