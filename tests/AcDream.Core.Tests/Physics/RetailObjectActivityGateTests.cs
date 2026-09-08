using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class RetailObjectActivityGateTests
{
    [Fact]
    public void FarPartArrayObject_ConsumesInactiveClockAndRebasesWhenNearAgain()
    {
        var clock = new RetailObjectQuantumClock();
        var body = new PhysicsBody
        {
            TransientState = TransientStateFlags.Active,
        };
        Assert.Equal(0, clock.Advance(0.02).Count);

        Assert.Equal(
            RetailObjectActivityResult.Inactive,
            RetailObjectActivityGate.Evaluate(
                clock,
                body,
                lifecycleEligible: true,
                hasPartArray: true,
                isStatic: false,
                objectPosition: new Vector3(96.01f, 0f, 0f),
                playerPosition: Vector3.Zero,
                elapsedSeconds: 0.02));
        Assert.False(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Active));

        Assert.Equal(
            RetailObjectActivityResult.Reactivated,
            RetailObjectActivityGate.Evaluate(
                clock,
                body,
                lifecycleEligible: true,
                hasPartArray: true,
                isStatic: false,
                objectPosition: new Vector3(95.99f, 0f, 0f),
                playerPosition: Vector3.Zero,
                elapsedSeconds: 0.02));
        Assert.True(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
        Assert.True(body.TransientState.HasFlag(TransientStateFlags.Active));
    }

    [Fact]
    public void FrozenInterval_IsDiscardedByInactiveToActiveRebase()
    {
        var clock = new RetailObjectQuantumClock();
        Assert.Equal(0, clock.Advance(0.02).Count);

        Assert.Equal(
            RetailObjectActivityResult.Suspended,
            RetailObjectActivityGate.Evaluate(
                clock,
                body: null,
                lifecycleEligible: false,
                hasPartArray: true,
                isStatic: false,
                objectPosition: Vector3.Zero,
                playerPosition: Vector3.Zero,
                elapsedSeconds: 0.5));
        Assert.Equal(
            RetailObjectActivityResult.Reactivated,
            RetailObjectActivityGate.Evaluate(
                clock,
                body: null,
                lifecycleEligible: true,
                hasPartArray: true,
                isStatic: false,
                objectPosition: Vector3.Zero,
                playerPosition: Vector3.Zero,
                elapsedSeconds: 0.01));

        Assert.Equal(0.0, clock.PendingSeconds, 8);
        Assert.Equal(0, clock.Advance(0.02).Count);
    }

    [Fact]
    public void PartArrayLessObject_RemainsActiveBeyondDistanceGate()
    {
        var clock = new RetailObjectQuantumClock();

        Assert.Equal(
            RetailObjectActivityResult.Active,
            RetailObjectActivityGate.Evaluate(
                clock,
                body: null,
                lifecycleEligible: true,
                hasPartArray: false,
                isStatic: false,
                objectPosition: new Vector3(500f, 0f, 0f),
                playerPosition: Vector3.Zero,
                elapsedSeconds: 0.02));
        Assert.True(clock.IsActive);
    }

    [Fact]
    public void MissingPlayer_PreservesPriorInactiveStateWithoutReactivation()
    {
        var clock = new RetailObjectQuantumClock();
        clock.Deactivate();

        Assert.Equal(
            RetailObjectActivityResult.Inactive,
            RetailObjectActivityGate.Evaluate(
                clock,
                body: null,
                lifecycleEligible: true,
                hasPartArray: true,
                isStatic: false,
                objectPosition: Vector3.Zero,
                playerPosition: null,
                elapsedSeconds: 0.04));

        Assert.False(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
    }

    [Fact]
    public void NearStaticObject_DoesNotReactivateOrdinaryObjectPath()
    {
        var clock = new RetailObjectQuantumClock();
        clock.Deactivate();
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Static,
            TransientState = TransientStateFlags.None,
        };

        Assert.Equal(
            RetailObjectActivityResult.Inactive,
            RetailObjectActivityGate.Evaluate(
                clock,
                body,
                lifecycleEligible: true,
                hasPartArray: true,
                isStatic: true,
                objectPosition: Vector3.Zero,
                playerPosition: Vector3.Zero,
                elapsedSeconds: 0.04));

        Assert.False(clock.IsActive);
        Assert.Equal(0.0, clock.PendingSeconds, 8);
        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Active));
    }
}
