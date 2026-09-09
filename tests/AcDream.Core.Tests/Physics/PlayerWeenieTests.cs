using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PlayerWeenieTests
{
    [Fact]
    public void InqRunRate_Skill200_ReturnsCorrectRate()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        Assert.True(pw.InqRunRate(out float rate));
        Assert.Equal(2.375f, rate, precision: 3);
    }

    [Fact]
    public void InqRunRate_Skill800_ReturnsCap()
    {
        var pw = new PlayerWeenie(runSkill: 800, jumpSkill: 100);
        Assert.True(pw.InqRunRate(out float rate));
        Assert.Equal(4.5f, rate, precision: 3);
    }

    [Fact]
    public void InqRunRate_Skill0_ReturnsBase()
    {
        var pw = new PlayerWeenie(runSkill: 0, jumpSkill: 100);
        Assert.True(pw.InqRunRate(out float rate));
        Assert.Equal(1.0f, rate, precision: 3);
    }

    [Fact]
    public void InqJumpVelocity_FullExtent_Skill100()
    {
        var pw = new PlayerWeenie(runSkill: 100, jumpSkill: 100);
        Assert.True(pw.InqJumpVelocity(1.0f, out float vz));
        // height = (100/1400) * 22.2 + 0.05 ≈ 1.636
        // vz = sqrt(1.636 * 19.6) ≈ 5.663
        Assert.Equal(5.663f, vz, precision: 1);
    }

    [Fact]
    public void InqJumpVelocity_HalfExtent_Skill100()
    {
        var pw = new PlayerWeenie(runSkill: 100, jumpSkill: 100);
        Assert.True(pw.InqJumpVelocity(0.5f, out float vz));
        // height = (100/1400) * 22.2 * 0.5 + 0.05 ≈ 0.843
        float expectedHeight = 1.0f * (100f / 1400f * 22.2f + 0.05f) * 0.5f;
        float expectedVz = MathF.Sqrt(expectedHeight * 19.6f);
        Assert.Equal(expectedVz, vz, precision: 2);
    }

    [Fact]
    public void InqJumpVelocity_ZeroSkill_ClampsToMinHeight()
    {
        var pw = new PlayerWeenie(runSkill: 0, jumpSkill: 0);
        Assert.True(pw.InqJumpVelocity(1.0f, out float vz));
        // height = max(0.05 * 1.0, 0.35) = 0.35
        // vz = sqrt(0.35 * 19.6) ≈ 2.619
        Assert.Equal(MathF.Sqrt(0.35f * 19.6f), vz, precision: 2);
    }

    [Fact]
    public void GetBurdenMod_Unencumbered_Returns1()
    {
        Assert.Equal(1.0f, PlayerWeenie.GetBurdenMod(0f));
        Assert.Equal(1.0f, PlayerWeenie.GetBurdenMod(0.5f));
        Assert.Equal(1.0f, PlayerWeenie.GetBurdenMod(0.99f));
    }

    [Fact]
    public void GetBurdenMod_Overloaded_Returns0()
    {
        Assert.Equal(0.0f, PlayerWeenie.GetBurdenMod(2.0f));
        Assert.Equal(0.0f, PlayerWeenie.GetBurdenMod(3.0f));
    }

    [Fact]
    public void GetBurdenMod_PartialBurden_LinearDecrease()
    {
        Assert.Equal(0.5f, PlayerWeenie.GetBurdenMod(1.5f), precision: 3);
        Assert.Equal(0.75f, PlayerWeenie.GetBurdenMod(1.25f), precision: 3);
    }


    [Fact]
    public void CanJump_DefaultUnencumbered_ReturnsTrue()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        Assert.True(pw.CanJump(1.0f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.0f)]
    [InlineData(1.99f)]
    public void CanJump_BelowThreshold_ReturnsTrue(float burden)
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100, burden: burden);
        Assert.True(pw.CanJump(1.0f));
    }

    [Theory]
    [InlineData(2.0f)]
    [InlineData(2.5f)]
    [InlineData(3.0f)]
    public void CanJump_AtOrAboveThreshold_ReturnsFalse(float burden)
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100, burden: burden);
        Assert.False(pw.CanJump(1.0f));
    }

    [Fact]
    public void CanJump_SetBurden_UpdatesGateLive()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        Assert.True(pw.CanJump(1.0f));
        pw.SetBurden(2.5f);
        Assert.False(pw.CanJump(1.0f));
        pw.SetBurden(0.5f);
        Assert.True(pw.CanJump(1.0f));
    }

    [Fact]
    public void JumpStaminaCost_ReturnsRealNonzeroCost_AndAlwaysAffordable()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        Assert.Equal(6, cost);
    }

    [Fact]
    public void JumpStaminaCost_ScalesWithBurden()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100, burden: 1.0f);
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        Assert.Equal(14, cost);
    }


    private static float NowSeconds() => Environment.TickCount64 / 1000f;

    [Fact]
    public void JumpStaminaCost_NeverPushed_UsesNonPkFormula_TheInvariant()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        Assert.Equal(6, cost);
    }

    [Fact]
    public void JumpStaminaCost_PlayerKillerStatusRetailDefault_UsesNonPkFormula()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetPlayerKillerStatus(8, NowSeconds());
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        Assert.Equal(6, cost);
    }

    [Fact]
    public void JumpStaminaCost_PkStatusNoTimestamp_UsesNonPkFormula()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetPlayerKillerStatus(4, null);
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        Assert.Equal(6, cost);
    }

    [Fact]
    public void JumpStaminaCost_PkStatusActiveWithinWindow_UsesPkFormula()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetPlayerKillerStatus(4, NowSeconds()); // just attacked
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        // pk branch: (int)((power + 1.0) * 100.0) = (1.0+1.0)*100 = 200
        Assert.Equal(200, cost);
    }

    [Fact]
    public void JumpStaminaCost_PkLiteStatusActiveWithinWindow_UsesPkFormula()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetPlayerKillerStatus(0x40, NowSeconds());
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        Assert.Equal(200, cost);
    }

    [Fact]
    public void JumpStaminaCost_PkTimerExpired_UsesNonPkFormula()
    {
        // 30 seconds ago — past the 20-second recency window.
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetPlayerKillerStatus(4, NowSeconds() - 30f);
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        Assert.Equal(6, cost);
    }

    [Fact]
    public void JumpStaminaCost_PkTimerRestoredToNeverPushed_ReturnsToNonPkFormula()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetPlayerKillerStatus(4, NowSeconds());
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost));
        Assert.Equal(200, cost);

        pw.SetPlayerKillerStatus(null, null);
        Assert.True(pw.JumpStaminaCost(1.0f, out int cost2));
        Assert.Equal(6, cost2);
    }

    [Fact]
    public void InqRunRate_ZeroStamina_ZeroesEffectiveSkill()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetStamina(0);
        Assert.True(pw.InqRunRate(out float rate));
        // skill forced to 0 -> base rate 1.0 (matches InqRunRate_Skill0_ReturnsBase).
        Assert.Equal(1.0f, rate, precision: 3);
    }

    [Fact]
    public void InqRunRate_NonzeroStamina_UsesRealSkill()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetStamina(50);
        Assert.True(pw.InqRunRate(out float rate));
        Assert.Equal(2.375f, rate, precision: 3);
    }

    [Fact]
    public void InqRunRate_UnsetStamina_NeverGates()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        Assert.True(pw.InqRunRate(out float rate));
        Assert.Equal(2.375f, rate, precision: 3);
    }

    [Fact]
    public void InqJumpVelocity_ZeroStamina_FloorsAt0Point35Meters()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetStamina(0);
        Assert.True(pw.InqJumpVelocity(1.0f, out float vz));
        Assert.Equal(MathF.Sqrt(0.35f * 19.6f), vz, precision: 2);
    }

    [Fact]
    public void SetStamina_Null_RestoresUnknownSentinel()
    {
        var pw = new PlayerWeenie(runSkill: 200, jumpSkill: 100);
        pw.SetStamina(0);
        Assert.True(pw.InqRunRate(out float zeroedRate));
        Assert.Equal(1.0f, zeroedRate, precision: 3);

        pw.SetStamina(null);
        Assert.True(pw.InqRunRate(out float restoredRate));
        Assert.Equal(2.375f, restoredRate, precision: 3);
    }
}
