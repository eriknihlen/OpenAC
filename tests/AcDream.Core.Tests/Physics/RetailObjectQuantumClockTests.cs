using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class RetailObjectQuantumClockTests
{
    [Fact]
    public void Advance_SubMinimumTime_IsRetainedUntilStrictThresholdIsCrossed()
    {
        var clock = new RetailObjectQuantumClock();

        Assert.Equal(0, clock.Advance(PhysicsBody.MinQuantum * 0.5).Count);
        Assert.Equal(0, clock.Advance(PhysicsBody.MinQuantum * 0.5).Count);
        RetailObjectQuantumBatch batch = clock.Advance(0.00001);

        Assert.Equal(1, batch.Count);
        Assert.True(batch.GetQuantum(0) > PhysicsBody.MinQuantum);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
    }

    [Fact]
    public void Advance_ExactFourTenths_EqualsTwoRetailMaxQuanta()
    {
        var clock = new RetailObjectQuantumClock();

        RetailObjectQuantumBatch batch = clock.Advance(0.4);

        Assert.Equal(2, batch.Count);
        Assert.Equal(PhysicsBody.MaxQuantum, batch.GetQuantum(0));
        Assert.Equal(PhysicsBody.MaxQuantum, batch.GetQuantum(1), 6);
    }

    [Fact]
    public void Advance_HugePlusEpsilon_DiscardsWithoutObjectTick()
    {
        var clock = new RetailObjectQuantumClock();

        RetailObjectQuantumBatch batch = clock.Advance(
            PhysicsBody.HugeQuantum + 0.00001);

        Assert.True(batch.Discarded);
        Assert.Equal(0, batch.Count);
        Assert.Equal(0.0, clock.PendingSeconds);
    }

    [Fact]
    public void Advance_ExactHugeQuantum_IsSubdividedNotDiscarded()
    {
        var clock = new RetailObjectQuantumClock();

        RetailObjectQuantumBatch batch = clock.Advance(PhysicsBody.HugeQuantum);

        Assert.False(batch.Discarded);
        Assert.Equal(10, batch.Count);
        for (int i = 0; i < batch.Count; i++)
            Assert.Equal(PhysicsBody.MaxQuantum, batch.GetQuantum(i), 6);
    }

    [Fact]
    public void Advance_MicroQuantum_IsConsumedRatherThanAccumulated()
    {
        var clock = new RetailObjectQuantumClock();

        for (int i = 0; i < 100; i++)
            Assert.Equal(0, clock.Advance(0.0001).Count);

        Assert.Equal(0.0, clock.PendingSeconds);
    }

    [Fact]
    public void DeactivateThenActivate_RebasesOnceAndDoesNotCatchUp()
    {
        var clock = new RetailObjectQuantumClock();
        Assert.Equal(0, clock.Advance(0.02).Count);

        clock.Deactivate();
        Assert.False(clock.IsActive);
        Assert.Equal(0.02, clock.PendingSeconds, 8);
        Assert.True(clock.Activate());
        Assert.True(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
        Assert.False(clock.Activate());
    }

    [Fact]
    public void ResetForEnterWorld_DiscardsFragmentAndActivatesImmediately()
    {
        var clock = new RetailObjectQuantumClock();
        Assert.Equal(0, clock.Advance(0.02).Count);

        clock.ResetForEnterWorld();

        Assert.True(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
        Assert.False(clock.Activate());
    }

    [Fact]
    public void ResetForEnterWorld_StaticObjectRebasesButRetainsInactiveState()
    {
        var clock = new RetailObjectQuantumClock();
        Assert.Equal(0, clock.Advance(0.02).Count);
        clock.Deactivate();

        clock.ResetForEnterWorld(isStatic: true);

        Assert.False(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
    }
}
