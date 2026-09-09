using System;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class MovementSystemTests
{
    [Fact]
    public void GetRunRate_Skill800Sentinel_IsExactEqualityOnly()
    {
        Assert.Equal(4.5f, MovementSystem.GetRunRate(0f, 800), precision: 5);
        Assert.Equal(3.1994f, MovementSystem.GetRunRate(0f, 799), precision: 3);
        Assert.Equal(3.2005f, MovementSystem.GetRunRate(0f, 801), precision: 3);
    }

    [Fact]
    public void GetRunRate_MaxedSkills_UseGeneralFormula_VitaeDifferentiates()
    {
        Assert.True(MathF.Abs(MovementSystem.GetRunRate(0f, 10200) - 3.6971f) < 1e-3f);
        Assert.True(MathF.Abs(MovementSystem.GetRunRate(0f, 14463) - 3.7125f) < 1e-3f);
        // InqMaxRunRate's skill=9999 probe: general formula, not 4.5.
        Assert.True(MathF.Abs(MovementSystem.GetRunRate(0f, 9999) - 3.6961f) < 1e-3f);
    }

    [Fact]
    public void GetRunRate_Skill200_MatchesFormula()
    {
        // (1.0 * (200/400 * 11) + 4) / 4 = (5.5 + 4) / 4 = 2.375
        Assert.Equal(2.375f, MovementSystem.GetRunRate(0f, 200), precision: 3);
    }

    [Fact]
    public void GetRunRate_Skill0_ReturnsBase()
    {
        Assert.Equal(1.0f, MovementSystem.GetRunRate(0f, 0), precision: 3);
    }

    [Theory]
    [InlineData(0f, 1f)]     // unencumbered
    [InlineData(0.99f, 1f)]  // just under 100%
    [InlineData(1.0f, 1f)]   // exactly 100% — still full effectiveness
    [InlineData(1.5f, 0.5f)] // knee: halfway to 200%
    [InlineData(2.0f, 0f)]   // knee floor
    [InlineData(3.0f, 0f)]   // fully overloaded
    public void GetRunRate_LoadKnees_ScaleLinearly(float burden, float expectedLoadMod)
    {
        // At runSkill=200: rate = (loadMod * 5.5 + 4) / 4.
        float expected = (expectedLoadMod * 5.5f + 4f) / 4f;
        Assert.Equal(expected, MovementSystem.GetRunRate(burden, 200), precision: 3);
    }

    [Fact]
    public void GetJumpHeight_FullExtent_Skill100_MatchesFormula()
    {
        // height = 1.0 * (100/1400 * 22.2 + 0.05) * 1.0 = 1.636...
        float expected = (100f / 1400f * 22.2f + 0.05f);
        Assert.Equal(expected, MovementSystem.GetJumpHeight(0f, 100, 1.0f), precision: 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public void GetJumpHeight_ExtentScalesLinearly(float extent)
    {
        float unscaled = 100f / 1400f * 22.2f + 0.05f;
        float expected = System.Math.Max(unscaled * extent, 0.35f);
        Assert.Equal(expected, MovementSystem.GetJumpHeight(0f, 100, extent), precision: 3);
    }

    [Fact]
    public void GetJumpHeight_ClampsExtentAbove1()
    {
        Assert.Equal(
            MovementSystem.GetJumpHeight(0f, 100, 1.0f),
            MovementSystem.GetJumpHeight(0f, 100, 5.0f),
            precision: 5);
    }

    [Fact]
    public void GetJumpHeight_ClampsExtentBelow0()
    {
        Assert.Equal(0.35f, MovementSystem.GetJumpHeight(0f, 100, -3.0f), precision: 5);
    }

    [Fact]
    public void GetJumpHeight_ZeroSkill_FloorsAt0Point35()
    {
        Assert.Equal(0.35f, MovementSystem.GetJumpHeight(0f, 0, 1.0f), precision: 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    [InlineData(3.0f)]
    public void GetJumpHeight_AtHeavyLoad_NeverGoesBelowFloor(float burden)
    {
        Assert.True(MovementSystem.GetJumpHeight(burden, 100, 1.0f) >= 0.35f);
    }

    [Theory]
    [InlineData(0f, 0f, 2)]
    [InlineData(0f, 1f, 6)]
    [InlineData(1f, 1f, 14)]
    [InlineData(2f, 1f, 22)]
    [InlineData(0.3f, 0.7f, 7)]
    public void JumpStaminaCost_NonPk_CeilsCorrectly(float burden, float power, int expected)
    {
        Assert.Equal(expected, MovementSystem.JumpStaminaCost(power, burden, pk: false));
    }

    [Fact]
    public void JumpStaminaCost_Pk_UsesAceTiebreakerFormula()
    {
        Assert.Equal(150, MovementSystem.JumpStaminaCost(0.5f, burden: 1f, pk: true));
        Assert.Equal(100, MovementSystem.JumpStaminaCost(0f, burden: 1f, pk: true));
    }

    [Fact]
    public void GetJumpPower_IsAlgebraicInverseOfJumpStaminaCost()
    {
        float burden = 1f;
        uint stamina = 20u;
        float power = MovementSystem.GetJumpPower(stamina, burden, pk: false);
        // (stamina - 2) / (burden*8 + 4) = 18 / 12 = 1.5
        Assert.Equal(1.5f, power, precision: 3);
    }
}
