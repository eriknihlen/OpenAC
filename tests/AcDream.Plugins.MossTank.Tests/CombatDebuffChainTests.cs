namespace AcDream.Plugins.MossTank.Tests;

public sealed class CombatDebuffChainTests
{
    [Fact]
    public void ChainOrderIsRetailsTwelveSteps()
    {
        IReadOnlyList<CombatDebuffStep> steps = CombatDebuffChain.Build(
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Yield
                    | MonsterActionFlags.WeakeningCurse
                    | MonsterActionFlags.FesteringCurse
                    | MonsterActionFlags.Corruption
                    | MonsterActionFlags.DestructiveCurse
                    | MonsterActionFlags.Corrosion
                    | MonsterActionFlags.Imperil
                    | MonsterActionFlags.Vulnerability
                    | MonsterActionFlags.GravityWell
                    | MonsterActionFlags.Broadside
                    | MonsterActionFlags.Fester,
                DamageType = MonsterDamageType.Fire,
                ExtraVulnerability = MonsterDamageType.Acid,
            },
            MonsterDamageType.Fire,
            MonsterDamageType.Acid);

        Assert.Equal(
            [
                new DebuffIdentity(MonsterActionFlags.Yield, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.WeakeningCurse, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.FesteringCurse, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.Corruption, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.DestructiveCurse, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.Corrosion, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.Imperil, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.Vulnerability, MonsterDamageType.Fire),
                new DebuffIdentity(MonsterActionFlags.Vulnerability, MonsterDamageType.Acid),
                new DebuffIdentity(MonsterActionFlags.GravityWell, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.Broadside, MonsterDamageType.Auto),
                new DebuffIdentity(MonsterActionFlags.Fester, MonsterDamageType.Auto),
            ],
            steps.Select(static step => step.Identity));
    }

    [Fact]
    public void OnlyCorruptionDestructiveCurseAndCorrosionAreZeroTolerance()
    {
        IReadOnlyList<CombatDebuffStep> steps = CombatDebuffChain.Build(
            new MonsterRuleActions
            {
                Flags = (MonsterActionFlags)0x3FFF,
                DamageType = MonsterDamageType.Fire,
            },
            MonsterDamageType.Fire,
            MonsterDamageType.None);

        Assert.Equal(
            [
                MonsterActionFlags.Corruption,
                MonsterActionFlags.DestructiveCurse,
                MonsterActionFlags.Corrosion,
            ],
            steps.Where(static step => step.ZeroTolerance)
                .Select(static step => step.Identity.Flag));
    }

    [Fact]
    public void NaturalVulnUsesTheAttackElementAndExtraVulnIsIndependentOfTheColumn()
    {
        IReadOnlyList<CombatDebuffStep> columnOff = CombatDebuffChain.Build(
            new MonsterRuleActions { Flags = MonsterActionFlags.None },
            MonsterDamageType.Fire,
            MonsterDamageType.Acid);
        Assert.Equal(
            [new DebuffIdentity(MonsterActionFlags.Vulnerability, MonsterDamageType.Acid)],
            columnOff.Select(static step => step.Identity));

        IReadOnlyList<CombatDebuffStep> columnOn = CombatDebuffChain.Build(
            new MonsterRuleActions { Flags = MonsterActionFlags.Vulnerability },
            MonsterDamageType.Cold,
            MonsterDamageType.None);
        Assert.Equal(
            [new DebuffIdentity(MonsterActionFlags.Vulnerability, MonsterDamageType.Cold)],
            columnOn.Select(static step => step.Identity));
    }

    [Fact]
    public void AnElementlessPlanHasNoNaturalVulnStep()
    {
        // fk.a(None, Vuln) returns MySpell.InvalidSpell (fk.cs:433), so the
        // step's own dm.b(guid, null) is TimeSpan.MaxValue — never due.
        IReadOnlyList<CombatDebuffStep> steps = CombatDebuffChain.Build(
            new MonsterRuleActions { Flags = MonsterActionFlags.Vulnerability },
            MonsterDamageType.None,
            MonsterDamageType.None);

        Assert.Empty(steps);
    }

    [Fact]
    public void ChooseTakesTheFirstDueStepAndStops()
    {
        // hi.cs:123-206 — every arm ends in `return;`. There is no second kind
        // this tick and no ranking between kinds.
        IReadOnlyList<CombatDebuffStep> steps = CombatDebuffChain.Build(
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil
                    | MonsterActionFlags.Fester,
            },
            MonsterDamageType.None,
            MonsterDamageType.None);

        var asked = new List<MonsterActionFlags>();
        CombatDebuffStep? chosen = CombatDebuffChain.Choose(steps, step =>
        {
            asked.Add(step.Identity.Flag);
            return step.Identity.Flag != MonsterActionFlags.Yield;
        });

        Assert.Equal(MonsterActionFlags.Imperil, chosen?.Identity.Flag);
        Assert.Equal(
            [MonsterActionFlags.Yield, MonsterActionFlags.Imperil],
            asked);
    }

    [Fact]
    public void NeedsDebuffIsTheSameTwelveTestsAsAFlatOr()
    {
        IReadOnlyList<CombatDebuffStep> steps = CombatDebuffChain.Build(
            new MonsterRuleActions { Flags = MonsterActionFlags.Fester },
            MonsterDamageType.None,
            MonsterDamageType.None);

        Assert.True(CombatDebuffChain.NeedsDebuff(steps, static _ => true));
        Assert.False(CombatDebuffChain.NeedsDebuff(steps, static _ => false));
        Assert.False(CombatDebuffChain.NeedsDebuff([], static _ => true));
    }
}
