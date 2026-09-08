using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class A6P7DispatchRulesTests
{
    [Fact]
    public void PhysicsStateFlags_HasPhysicsBsp_Is_Bit_16()
    {
        Assert.Equal(0x00010000u, (uint)PhysicsStateFlags.HasPhysicsBsp);
    }

    [Theory]
    [InlineData(0x00010008u, true)]
    [InlineData(0x00010000u, true)]   // bare HAS_BSP
    [InlineData(0x00110000u, true)]
    [InlineData(0x00000008u, false)]
    [InlineData(0x00000000u, false)]  // empty state
    [InlineData(0x0000FFFFu, false)]
    public void BspOnlyDispatch_RespectsHasPhysicsBspFlag(
        uint entityState, bool expected)
    {
        Assert.Equal(expected, Transition.BspOnlyDispatch(entityState));
    }

    [Fact]
    public void NonMissileMover_DoesNotIgnoreOrdinaryTarget()
    {
        var mover = new ObjectInfo();
        Assert.False(mover.MissileIgnore(
            targetEntityId: 1u,
            targetPhysicsState: 0u,
            targetFlags: EntityCollisionFlags.HasWeenie));
    }

    [Fact]
    public void BspOnlyDispatch_DoorStateStillDispatchesBspOnly()
    {
        const uint doorState = 0x00010008u;
        Assert.True(Transition.BspOnlyDispatch(doorState));
    }
}
