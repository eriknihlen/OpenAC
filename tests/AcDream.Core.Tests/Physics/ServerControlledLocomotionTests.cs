using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class ServerControlledLocomotionTests
{



    [Fact]
    public void PlanFromVelocity_StopsBelowRetailNoiseThreshold()
    {
        var plan = ServerControlledLocomotion.PlanFromVelocity(
            new Vector3(0.10f, 0.12f, 3.0f));

        Assert.False(plan.IsMoving);
        Assert.Equal(MotionCommand.Ready, plan.Motion);
        Assert.Equal(1.0f, plan.SpeedMod);
    }

    [Fact]
    public void PlanFromVelocity_WalksForSlowServerControlledMotion()
    {
        var plan = ServerControlledLocomotion.PlanFromVelocity(
            new Vector3(0.0f, 0.80f, 0.0f));

        Assert.True(plan.IsMoving);
        Assert.Equal(MotionCommand.WalkForward, plan.Motion);
        Assert.InRange(plan.SpeedMod, 0.25f, 0.27f);
    }

    [Fact]
    public void PlanFromVelocity_RunsAtRetailRunScale()
    {
        var plan = ServerControlledLocomotion.PlanFromVelocity(
            new Vector3(0.0f, MotionInterpreter.RunAnimSpeed, 0.0f));

        Assert.True(plan.IsMoving);
        Assert.Equal(MotionCommand.RunForward, plan.Motion);
        Assert.Equal(1.0f, plan.SpeedMod, precision: 4);
    }

    [Fact]
    public void PlanFromVelocity_ClampsVeryFastSnapshots()
    {
        var plan = ServerControlledLocomotion.PlanFromVelocity(
            new Vector3(0.0f, 30.0f, 0.0f));

        Assert.True(plan.IsMoving);
        Assert.Equal(MotionCommand.RunForward, plan.Motion);
        Assert.Equal(ServerControlledLocomotion.MaxSpeedMod, plan.SpeedMod);
    }

    [Theory]
    [InlineData(MotionCommand.Ready)]
    [InlineData(MotionCommand.WalkForward)]
    [InlineData(MotionCommand.RunForward)]
    public void CanApplyVelocityCycle_AllowsOnlyLocomotionFamily(uint motion)
        => Assert.True(ServerControlledLocomotion.CanApplyVelocityCycle(motion));

    [Theory]
    [InlineData(MotionCommand.Dead)]
    [InlineData(0x10000058u)] // ThrustMed action
    [InlineData(0x10000051u)] // Twitch1 hit reaction
    public void CanApplyVelocityCycle_PreservesAuthoritativeNonLocomotion(uint motion)
        => Assert.False(ServerControlledLocomotion.CanApplyVelocityCycle(motion));
}
