using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class Issue32LastKnownContactPlaneTests
{
    private const uint Cell = 0x0001_0001u;

    /// <summary>The flat ground the mover is standing on before the rim.</summary>
    private static readonly Plane Walkable = new(Vector3.UnitZ, 0f);

    private static readonly Plane Steep =
        new(Vector3.Normalize(new Vector3(-0.954f, 0f, 0.301f)), 0f);

    [Fact]
    public void MidTransitionSteepContact_LeavesTheWalkableLastKnownPlaneIntact()
    {
        var ci = new CollisionInfo();

        ci.InitContactPlane(Walkable, Cell);

        ci.SetContactPlane(Steep, Cell);

        Assert.True(ci.ContactPlaneValid);
        Assert.Equal(Steep.Normal, ci.ContactPlane.Normal);

        Assert.True(
            ci.LastKnownContactPlaneValid,
            "the seeded last-known plane must survive a steep contact — "
            + "cliff_slide has no second vector without it.");
        Assert.Equal(Walkable.Normal, ci.LastKnownContactPlane.Normal);

        Vector3 slide = Vector3.Cross(Steep.Normal, ci.LastKnownContactPlane.Normal);
        Assert.True(
            slide.Length() > 1e-4f,
            $"cliff_slide's direction is degenerate: |cross| = {slide.Length():E3}. "
            + $"contactN = {Steep.Normal}, lastN = {ci.LastKnownContactPlane.Normal}.");
    }

    [Fact]
    public void InitContactPlane_SeedsBothGroups()
    {
        var ci = new CollisionInfo();

        Assert.False(ci.LastKnownContactPlaneValid);

        ci.InitContactPlane(Walkable, Cell, isWater: true);

        Assert.True(ci.ContactPlaneValid);
        Assert.Equal(Walkable.Normal, ci.ContactPlane.Normal);
        Assert.Equal(Cell, ci.ContactPlaneCellId);
        Assert.True(ci.ContactPlaneIsWater);

        Assert.True(ci.LastKnownContactPlaneValid);
        Assert.Equal(Walkable.Normal, ci.LastKnownContactPlane.Normal);
        Assert.Equal(Cell, ci.LastKnownContactPlaneCellId);
        Assert.True(ci.LastKnownContactPlaneIsWater);
    }

    [Fact]
    public void RepeatedSteepContact_StillLeavesLastKnownIntact()
    {
        var ci = new CollisionInfo();
        ci.InitContactPlane(Walkable, Cell);

        ci.SetContactPlane(Steep, Cell);
        ci.SetContactPlane(Steep, Cell);   // hits the no-op guard
        ci.SetContactPlane(Steep, Cell);

        Assert.Equal(Walkable.Normal, ci.LastKnownContactPlane.Normal);
    }
}
