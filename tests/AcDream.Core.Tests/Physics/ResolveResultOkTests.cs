using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ResolveResultOkTests
{
    [Fact]
    public void NoStartCell_ReportsNotOk()
    {
        var engine = new PhysicsEngine();

        var r = engine.ResolveWithTransition(
            currentPos: Vector3.Zero, targetPos: new Vector3(1f, 0f, 0f), cellId: 0u,
            sphereRadius: 0.3f, sphereHeight: 0f, stepUpHeight: 0f, stepDownHeight: 0f,
            isOnGround: false);

        Assert.False(r.Ok);
    }

    [Fact]
    public void ZeroMovementValidCell_ReportsOk()
    {
        var engine = new PhysicsEngine();

        var r = engine.ResolveWithTransition(
            currentPos: new Vector3(5f, 5f, 5f), targetPos: new Vector3(5f, 5f, 5f), cellId: 0xA9B40001u,
            sphereRadius: 0.3f, sphereHeight: 0f, stepUpHeight: 0f, stepDownHeight: 0f,
            isOnGround: false);

        Assert.True(r.Ok);
    }
}
