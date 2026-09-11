using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

/// <summary>
/// A remote whose per-tick sweep finds no valid position must stay where
/// it was, with a zero step velocity, instead of committing the checked
/// (penetrating) position. Terrain alone never fails a sweep, so the failed
/// half exercises the commit helper with a failed result; the successful
/// half runs a real sweep on the ramp harness.
/// </summary>
public sealed class RuntimeRemoteFailedSweepTests
{
    private const uint CommittedCell = 0x01010001u;

    [Fact]
    public void FailedSweepHoldsThePreStepOriginZeroesTheStepVelocityAndKeepsTheCell()
    {
        var pre = new Vector3(10f, 10f, 5f);
        var body = new PhysicsBody { Position = pre, CachedVelocity = new Vector3(1f, 1f, 1f) };
        var failed = new ResolveResult(
            Position: pre + new Vector3(0f, -0.2f, 0f),
            CellId: 0x0101000Au,
            IsOnGround: true,
            Ok: false);

        uint cell = RuntimeRemotePhysicsUpdater.CommitSweepOutcome(
            body, failed, pre, 1f / 30f, CommittedCell);

        Assert.Equal(pre, body.Position);
        Assert.Equal(Vector3.Zero, body.CachedVelocity);
        Assert.Equal(CommittedCell, cell);
    }

    [Fact]
    public void SuccessfulSweepCommitsTheResolvedOriginCellAndStepVelocity()
    {
        var pre = new Vector3(10f, 10f, 5f);
        var body = new PhysicsBody { Position = pre };
        var resolved = pre + new Vector3(0f, -0.2f, 0f);
        var ok = new ResolveResult(
            Position: resolved,
            CellId: 0x0101000Au,
            IsOnGround: true);
        const float dt = 1f / 30f;

        uint cell = RuntimeRemotePhysicsUpdater.CommitSweepOutcome(
            body, ok, pre, dt, CommittedCell);

        Assert.Equal(resolved, body.Position);
        Assert.Equal((resolved - pre) / dt, body.CachedVelocity);
        Assert.Equal(0x0101000Au, cell);
    }

    [Fact]
    public void ARealSweepOnTheHarnessPublishesTheStepVelocity()
    {
        using RemoteRampHarness harness = RemoteRampHarness.OnRamp(gradient: 0f);
        Vector3 before = harness.Remote.Body.Position;
        const float dt = 1f / 30f;

        harness.Tick(1, new Vector3(0f, 0.1f, 0f), dt);

        Vector3 moved = harness.Remote.Body.Position - before;
        Assert.NotEqual(Vector3.Zero, moved);
        Assert.Equal(moved / dt, harness.Remote.Body.CachedVelocity);
    }
}
