using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MovementParametersFromWireTests
{
    [Fact]
    public void FromWire_AllBitsSet_EveryFlagTrue()
    {
        var p = MovementParameters.FromWire(
            bitfield: 0x3FFFFu,
            distanceToObject: 1f,
            minDistance: 2f,
            failDistance: 3f,
            speed: 4f,
            walkRunThreshhold: 5f,
            desiredHeading: 6f);

        Assert.True(p.CanWalk);
        Assert.True(p.CanRun);
        Assert.True(p.CanSidestep);
        Assert.True(p.CanWalkBackwards);
        Assert.True(p.CanCharge);
        Assert.True(p.FailWalk);
        Assert.True(p.UseFinalHeading);
        Assert.True(p.Sticky);
        Assert.True(p.MoveAway);
        Assert.True(p.MoveTowards);
        Assert.True(p.UseSpheres);
        Assert.True(p.SetHoldKey);
        Assert.True(p.Autonomous);
        Assert.True(p.ModifyRawState);
        Assert.True(p.ModifyInterpretedState);
        Assert.True(p.CancelMoveTo);
        Assert.True(p.StopCompletelyFlag);
        Assert.True(p.DisableJumpDuringLink);
    }

    [Fact]
    public void FromWire_ZeroBitfield_EveryFlagFalse_NoCtorDefaultsSurvive()
    {
        var p = MovementParameters.FromWire(
            bitfield: 0u,
            distanceToObject: 1f, minDistance: 2f, failDistance: 3f,
            speed: 4f, walkRunThreshhold: 5f, desiredHeading: 6f);

        Assert.False(p.CanWalk);
        Assert.False(p.CanRun);
        Assert.False(p.CanSidestep);
        Assert.False(p.CanWalkBackwards);
        Assert.False(p.CanCharge);
        Assert.False(p.MoveTowards);
        Assert.False(p.UseSpheres);
        Assert.False(p.SetHoldKey);
        Assert.False(p.ModifyRawState);
        Assert.False(p.ModifyInterpretedState);
        Assert.False(p.CancelMoveTo);
        Assert.False(p.StopCompletelyFlag);
    }

    [Fact]
    public void FromWire_CanChargeBit_DecodesIndependently()
    {
        var p = MovementParameters.FromWire(
            bitfield: 0x10u,
            distanceToObject: 0f, minDistance: 0f, failDistance: 0f,
            speed: 0f, walkRunThreshhold: 0f, desiredHeading: 0f);

        Assert.True(p.CanCharge);
        Assert.False(p.CanWalk);
        Assert.False(p.CanRun);
    }

    [Theory]
    [InlineData(0x1u)]
    [InlineData(0x2u)]
    [InlineData(0x4u)]
    [InlineData(0x8u)]
    [InlineData(0x10u)]
    [InlineData(0x20u)]
    [InlineData(0x40u)]
    [InlineData(0x80u)]
    [InlineData(0x100u)]
    [InlineData(0x200u)]
    [InlineData(0x400u)]
    [InlineData(0x800u)]
    [InlineData(0x1000u)]
    [InlineData(0x2000u)]
    [InlineData(0x4000u)]
    [InlineData(0x8000u)]
    [InlineData(0x10000u)]
    [InlineData(0x20000u)]
    public void FromWire_SingleBitMaskRoundTrips(uint mask)
    {
        var p = MovementParameters.FromWire(
            bitfield: mask,
            distanceToObject: 0f, minDistance: 0f, failDistance: 0f,
            speed: 0f, walkRunThreshhold: 0f, desiredHeading: 0f);

        Assert.Equal(mask, ToBitfield(p));
    }

    [Fact]
    public void FromWire_ScalarFields_CopiedInWireOrder()
    {
        var p = MovementParameters.FromWire(
            bitfield: 0u,
            distanceToObject: 1.5f,
            minDistance: 2.5f,
            failDistance: 3.5f,
            speed: 4.5f,
            walkRunThreshhold: 5.5f,
            desiredHeading: 6.5f);

        Assert.Equal(1.5f, p.DistanceToObject);
        Assert.Equal(2.5f, p.MinDistance);
        Assert.Equal(3.5f, p.FailDistance);
        Assert.Equal(4.5f, p.Speed);
        Assert.Equal(5.5f, p.WalkRunThreshhold);
        Assert.Equal(6.5f, p.DesiredHeading);
    }

    [Fact]
    public void FromWireTurnTo_ThreeDwordForm_LeavesDistanceFieldsAtDefault()
    {
        var p = MovementParameters.FromWireTurnTo(
            bitfield: 0x2u,
            speed: 2f,
            desiredHeading: 90f);

        Assert.True(p.CanRun);
        Assert.Equal(2f, p.Speed);
        Assert.Equal(90f, p.DesiredHeading);

        Assert.Equal(0.6f, p.DistanceToObject);
        Assert.Equal(0f, p.MinDistance);
        Assert.Equal(float.MaxValue, p.FailDistance);
        Assert.Equal(15f, p.WalkRunThreshhold);
    }

    private static uint ToBitfield(MovementParameters p)
    {
        uint bitfield = 0;
        if (p.CanWalk) bitfield |= 0x1;
        if (p.CanRun) bitfield |= 0x2;
        if (p.CanSidestep) bitfield |= 0x4;
        if (p.CanWalkBackwards) bitfield |= 0x8;
        if (p.CanCharge) bitfield |= 0x10;
        if (p.FailWalk) bitfield |= 0x20;
        if (p.UseFinalHeading) bitfield |= 0x40;
        if (p.Sticky) bitfield |= 0x80;
        if (p.MoveAway) bitfield |= 0x100;
        if (p.MoveTowards) bitfield |= 0x200;
        if (p.UseSpheres) bitfield |= 0x400;
        if (p.SetHoldKey) bitfield |= 0x800;
        if (p.Autonomous) bitfield |= 0x1000;
        if (p.ModifyRawState) bitfield |= 0x2000;
        if (p.ModifyInterpretedState) bitfield |= 0x4000;
        if (p.CancelMoveTo) bitfield |= 0x8000;
        if (p.StopCompletelyFlag) bitfield |= 0x10000;
        if (p.DisableJumpDuringLink) bitfield |= 0x20000;
        return bitfield;
    }
}
