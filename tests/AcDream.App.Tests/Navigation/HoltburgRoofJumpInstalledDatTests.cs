using System.Numerics;
using AcDream.App.Tests.Rendering;
using AcDream.Core.Navigation;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Navigation;

/// <summary>
/// A running jump in Holtburg from a porch onto the flat roof of a building with a room under the
/// roof's edge, as a player with jump skill 443 makes it. The jump is about 22 m across, farther
/// than a fixed search reach allowed, and passes high over the room under the roof's edge, which
/// once counted as its ceiling.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class HoltburgRoofJumpInstalledDatTests
{
    private static readonly uint[] Holtburg = [0xA9B4FFFFu, 0xAAB4FFFFu, 0xA9B3FFFFu, 0xAAB3FFFFu];
    private static readonly Vector3 Porch = new(116.8918f, 26.095654f, 97.505005f);
    private static readonly Vector3 OnRoof = new(136.324829f, 8.420344f, 102.005005f);

    /// <summary>Jump skill 443 with nothing carried, running about 10.6 m/s.</summary>
    private static NavLeapAbility Skill443 =>
        new(
            WalkSpeed: 3.12f,
            RunSpeed: 10.6f,
            FullJumpHeight: (443f / (443f + 1300f) * 22.2f) + 0.05f,
            MaximumDrop: 12f,
            Physics: new NavLeapPhysics(StepSeconds: 1f / 30f, Elasticity: 0.05f, Friction: 0.95f));

    private readonly ITestOutputHelper _output;

    public HoltburgRoofJumpInstalledDatTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ThePorchJumpOntoTheRoofIsPlanned()
    {
        string? datDirectory = InstalledDatTestPath.Resolve();
        if (datDirectory is null)
        {
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }
        PublishedLandblock world = PublishedLandblock.Load(datDirectory, Holtburg, []);
        NavGeometry geometry = Assert.IsType<NavGeometry>(NavGeometry.Capture(world.Engine, 88f, -40f, 96f));
        NavGrid grid = NavGrid.Build(geometry, world.Body);

        NavRoute route = NavRouter.Find(grid, Porch, OnRoof, 2.5f, leaps: Skill443, arriveOnGoalFloor: true, goalRadius: 0.5f);

        _output.WriteLine($"{route.Outcome} {route.Reason}; {route.Leaps.Count} leaps in {route.Milliseconds:0} ms");
        Assert.Equal(NavRouteOutcome.Routed, route.Outcome);
        Assert.Equal("routed", route.Reason);
        NavRouteLeap leap = Assert.Single(route.Leaps);
        Assert.Equal(97.5f, route.Legs[leap.LegIndex - 1].Z, 1);
        Assert.Equal(102f, route.Legs[leap.LegIndex].Z, 1);
    }
}
