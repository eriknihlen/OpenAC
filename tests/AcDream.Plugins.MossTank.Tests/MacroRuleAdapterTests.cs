using AcDream.Plugins.MossTank;

namespace AcDream.Plugins.MossTank.Tests;

public class MacroRuleAdapterTests
{
    [Fact]
    public void AbsentRuleIsNeverValidAndSwallowsRunning()
    {
        var rule = new AbsentMacroRule("Slot", "not ported");
        var context = new MacroPassContext(1d, CanAct: true);

        Assert.False(rule.ValidNow(in context));
        rule.Running = true;
        Assert.False(rule.Running);
        Assert.Equal("not ported", rule.Reason);
    }

    [Fact]
    public void GateBlockedRuleStillTicksItsControllerByDefault()
    {
        var seen = new List<bool>();
        var rule = new ControllerMacroRule(
            "rule",
            context =>
            {
                seen.Add(context.CanAct);
                return true;
            },
            gate: () => false);

        Assert.False(rule.ValidNow(new MacroPassContext(1d, CanAct: true)));
        Assert.Equal([false], seen);
    }

    [Fact]
    public void GateBlockedTieredRuleDoesNotTickItsController()
    {
        int calls = 0;
        var rule = new ControllerMacroRule(
            "rule",
            _ =>
            {
                calls++;
                return true;
            },
            gate: () => false,
            bookkeepWhenBlocked: false);

        Assert.False(rule.ValidNow(new MacroPassContext(1d, CanAct: true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ARuleBelowTheWinnerNeverClaimsEvenIfItsControllerSaysYes()
    {
        var rule = new ControllerMacroRule("rule", _ => true);

        Assert.False(rule.ValidNow(new MacroPassContext(1d, CanAct: false)));
        Assert.True(rule.ValidNow(new MacroPassContext(1d, CanAct: true)));
    }

    [Fact]
    public void ATieredRuleBelowTheWinnerIsStillTickedWithCanActFalse()
    {
        var seen = new List<bool>();
        var rule = new ControllerMacroRule(
            "rule",
            context =>
            {
                seen.Add(context.CanAct);
                return true;
            },
            gate: () => true,
            bookkeepWhenBlocked: false);

        Assert.False(rule.ValidNow(new MacroPassContext(1d, CanAct: false)));
        Assert.Equal([false], seen);

        Assert.True(rule.ValidNow(new MacroPassContext(1d, CanAct: true)));
        Assert.Equal([false, true], seen);
    }

    [Fact]
    public void TeardownRunsOnceOnTheTrueToFalseEdge()
    {
        int teardowns = 0;
        var rule = new ControllerMacroRule(
            "rule",
            _ => true,
            onLostTurn: () => teardowns++);

        rule.Running = false;
        Assert.Equal(0, teardowns);

        rule.Running = true;
        rule.Running = false;
        Assert.Equal(1, teardowns);

        rule.Running = false;
        Assert.Equal(1, teardowns);
    }
}
