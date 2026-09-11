using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class InterpolationRecoveryTests
{
    [Fact]
    public void SlidingAlongWall_DiscardsUnreachableNodeForRecovery()
    {
        var manager = new InterpolationManager();
        var body = Vector3.Zero;
        var target = new Vector3(1f, 2f, 0f);
        manager.Enqueue(target, 0f, false, body);

        // The wall blocks X. Slow tangential movement makes measurable progress,
        // but cannot reach the target on its opposite side.
        for (int frame = 0; frame < 10; frame++)
        {
            manager.AdjustOffset(1d / 30d, body, 4f);
            body.Y += 0.002f;
        }

        Assert.Equal(1, manager.DiagnosticInterpolationState.FailCount);
        Assert.Equal(0, manager.DiagnosticInterpolationState.Depth);
        Assert.True(manager.TryGetRecoveryTarget(out var recovery));
        Assert.Equal(target, recovery.Position);
        Assert.True(manager.CompleteRecovery(recovery));
        Assert.False(manager.TryGetRecoveryTarget(out _));
    }

    [Fact]
    public void PassingWindow_PreservesFailuresUntilFourthFailureRequestsTail()
    {
        var manager = new InterpolationManager();
        for (int i = 0; i < 5; i++)
            manager.Enqueue(new Vector3(10f + i, 0f, 0f), 0f, false, Vector3.Zero);
        for (int frame = 0; frame < 10; frame++)
            manager.AdjustOffset(1d / 30d, Vector3.Zero, 4f);
        Assert.Equal((4, 1), manager.DiagnosticInterpolationState);

        var body = Vector3.Zero;
        for (int frame = 0; frame < 5; frame++)
        {
            body.X += 0.2f;
            manager.AdjustOffset(1d / 30d, body, 4f);
        }
        Assert.Equal((4, 1), manager.DiagnosticInterpolationState);
        Assert.False(manager.TryGetRecoveryTarget(out _));

        for (int frame = 0; frame < 15; frame++)
            manager.AdjustOffset(1d / 30d, body, 4f);
        Assert.Equal((1, 4), manager.DiagnosticInterpolationState);
        Assert.True(manager.TryGetRecoveryTarget(out var recovery));
        Assert.Equal(new Vector3(14f, 0f, 0f), recovery.Position);
    }

    [Fact]
    public void StickyTarget_SkipsFailuresWithoutConsumingTheNode()
    {
        var manager = new InterpolationManager();
        manager.Enqueue(new Vector3(2f, 0f, 0f), 0f, true, Vector3.Zero);
        for (int frame = 0; frame < 50; frame++)
            manager.AdjustOffset(1d / 30d, Vector3.Zero, 4f, isSticky: true);
        Assert.Equal((1, 0), manager.DiagnosticInterpolationState);
        Assert.False(manager.TryGetRecoveryTarget(out _));
    }

    [Fact]
    public void BlockedNearNode_CompletesWithoutFailure()
    {
        var manager = new InterpolationManager();
        manager.Enqueue(new Vector3(0.15f, 0f, 0f), 0f, false, Vector3.Zero);
        for (int frame = 0; frame < 10; frame++)
            manager.AdjustOffset(1d / 30d, Vector3.Zero, 4f);
        Assert.Equal((0, 0), manager.DiagnosticInterpolationState);
        Assert.False(manager.TryGetRecoveryTarget(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FarCorrection_ArmsPlacementWithOrWithoutExistingTail(bool existingTail)
    {
        var manager = new InterpolationManager();
        if (existingTail)
            manager.Enqueue(Vector3.UnitX, 0f, false, Vector3.Zero);
        var target = new Vector3(25f, 0f, 0f);
        manager.Enqueue(target, Quaternion.Identity, false, Vector3.Zero,
            targetCellId: 0x5A4801E5, autonomyBlipDistance: 20f);
        Assert.Equal(4, manager.DiagnosticInterpolationState.FailCount);
        Assert.True(manager.TryGetRecoveryTarget(out var recovery));
        Assert.Equal(target, recovery.Position);
        Assert.Equal(0x5A4801E5u, recovery.CellId);
        // Recovery is a placement request, never a large swept movement delta.
        var delta = manager.AdjustOffset(1d / 30d, Vector3.Zero, 4f);
        Assert.InRange(delta.Length(), 0f, 0.27f);
        Assert.True(manager.TryGetRecoveryTarget(out var retry));
        Assert.Equal(recovery, retry);
        Assert.True(manager.CompleteRecovery(retry));
        Assert.Equal((0, 0), manager.DiagnosticInterpolationState);
    }

    [Fact]
    public void UnacknowledgedRecovery_PersistsAndCannotClearNewerCorrection()
    {
        var manager = new InterpolationManager();
        manager.Enqueue(new Vector3(150f, 0f, 0f), 0f, false, Vector3.Zero);
        Assert.True(manager.TryGetRecoveryTarget(out var first));
        Assert.True(manager.TryGetRecoveryTarget(out var retry));
        Assert.Equal(first, retry);
        manager.Enqueue(new Vector3(160f, 0f, 0f), 0f, false, Vector3.Zero);
        Assert.False(manager.CompleteRecovery(first));
        Assert.True(manager.TryGetRecoveryTarget(out var newer));
        Assert.Equal(new Vector3(160f, 0f, 0f), newer.Position);
        Assert.True(manager.CompleteRecovery(newer));
    }
}
