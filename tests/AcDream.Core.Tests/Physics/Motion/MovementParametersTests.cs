using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MovementParametersTests
{
    [Fact]
    public void Ctor_SetFlags_MatchRetail0x1EE0FExpansion()
    {
        var p = new MovementParameters();

        Assert.True(p.CanWalk);                 // 0x1
        Assert.True(p.CanRun);                  // 0x2
        Assert.True(p.CanSidestep);              // 0x4
        Assert.True(p.CanWalkBackwards);         // 0x8
        Assert.True(p.MoveTowards);              // 0x200
        Assert.True(p.UseSpheres);               // 0x400
        Assert.True(p.SetHoldKey);               // 0x800
        Assert.True(p.ModifyRawState);           // 0x2000
        Assert.True(p.ModifyInterpretedState);   // 0x4000
        Assert.True(p.CancelMoveTo);             // 0x8000
        Assert.True(p.StopCompletelyFlag);
    }

    [Fact]
    public void Ctor_ClearFlags_MatchRetail0x1EE0FExpansion()
    {
        var p = new MovementParameters();

        Assert.False(p.CanCharge);
        Assert.False(p.FailWalk);                // 0x20
        Assert.False(p.UseFinalHeading);          // 0x40
        Assert.False(p.Sticky);                   // 0x80
        Assert.False(p.MoveAway);                 // 0x100
        Assert.False(p.Autonomous);                // 0x1000
        Assert.False(p.DisableJumpDuringLink);
    }

    [Fact]
    public void Ctor_Bitfield_ReconstitutesExactly0x1EE0F()
    {
        var p = new MovementParameters();

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

        Assert.Equal(0x1EE0Fu, bitfield);
    }

    [Fact]
    public void Ctor_ScalarDefaults_MatchRetail()
    {
        var p = new MovementParameters();

        Assert.Equal(0f, p.MinDistance);
        Assert.Equal(0.6f, p.DistanceToObject);
        Assert.Equal(float.MaxValue, p.FailDistance);
        Assert.Equal(0f, p.DesiredHeading);
        Assert.Equal(1f, p.Speed);
        Assert.Equal(15f, p.WalkRunThreshhold);
        Assert.Equal(0u, p.ContextId);
        Assert.Equal(HoldKey.Invalid, p.HoldKeyToApply);
        Assert.Equal(0u, p.ActionStamp);
    }

    [Fact]
    public void Ctor_CanCharge_DefaultsFalse_NotAceTrue()
    {
        var p = new MovementParameters();
        Assert.False(p.CanCharge);
    }

    [Fact]
    public void Ctor_WalkRunThreshhold_Is15_NotAce1()
    {
        var p = new MovementParameters();
        Assert.Equal(15f, p.WalkRunThreshhold);
    }
}
