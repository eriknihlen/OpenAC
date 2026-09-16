using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class SelfBuffBehaviorTests
{
    private static readonly BuffSettings TwoBuffs = new()
    {
        Spells = ["Strength Self", "Endurance Self"],
        RebuffWhenRemainingSeconds = 60d,
    };

    private static (FakeAutomationSurface Surface, SelfBuffBehavior Behavior, TickClock Clock) Build(BuffSettings settings)
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(10, "Strength Self VI", 100, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(11, "Endurance Self VI", 101, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(12, "Focus Self VI", 102, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(13, "Blood Drinker Self VI", 103, 6));
        surface.CombatSpells.Add(Spell.Debuff(14, "Impenetrability VI", 104, 6));
        var clock = new TickClock();
        var casts = new CastTracker(surface, clock);
        var behavior = new SelfBuffBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            () => settings,
            surface);
        return (surface, behavior, clock);
    }

    private static PluginInventoryItem Armor(uint id, string name) => new(
        id, 0u, name, 0u, 0u, 0x50000001u, 0u, 0x100u, 0u, 0u, 0u, 1, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        ObjectClass = PluginObjectClass.Armor,
    };

    [Fact]
    public void TheReportSaysHowEachConfiguredBuffStands()
    {
        var settings = new BuffSettings
        {
            Spells = ["Strength Self", "Endurance Self", "Fireball Self"],
            WeaponSpells = ["Blood Drinker Self"],
            ArmorSpells = ["Impenetrability"],
            BuffWeapon = true,
            BuffArmor = true,
            RebuffWhenRemainingSeconds = 60d,
        };
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(settings);
        surface.Enchantments.Add(new PluginActiveEnchantment(10u, 100u, 6, 1000d)); // strength up for a while
        surface.Enchantments.Add(new PluginActiveEnchantment(11u, 101u, 6, 30d));   // endurance about to lapse
        surface.OwnedItems.Add(Armor(0x8000_0001u, "Coat"));
        surface.MarkAppraised(0x8000_0001u); // asked, and nothing on it

        IReadOnlyList<BuffStatus> report = behavior.Report(Context(surface, clock).Board);

        BuffStatus strength = report.Single(b => b.Configured == "Strength Self");
        Assert.Equal("Strength Self VI", strength.SpellName);
        Assert.Equal(1000d, strength.SecondsRemaining);
        Assert.True(strength.IsUp);
        Assert.False(strength.Due);
        BuffStatus endurance = report.Single(b => b.Configured == "Endurance Self");
        Assert.True(endurance.IsUp);
        Assert.True(endurance.Due);
        BuffStatus unknown = report.Single(b => b.Configured == "Fireball Self");
        Assert.Null(unknown.SpellName);
        Assert.NotNull(unknown.Problem);
        BuffStatus aura = report.Single(b => b.Kind == "weapon");
        Assert.Equal("Blood Drinker Self VI", aura.SpellName);
        Assert.False(aura.IsUp);
        Assert.True(aura.Due);
        BuffStatus armor = report.Single(b => b.Kind == "armor");
        Assert.Equal("Coat", armor.ItemName);
        Assert.Equal("Impenetrability VI", armor.SpellName);
        Assert.True(armor.Due);
    }

    [Fact]
    public void WeaponAurasAndArmorSpellsFollowThePlayerBuffs()
    {
        var settings = new BuffSettings
        {
            Spells = ["Strength Self"],
            WeaponSpells = ["Blood Drinker Self"],
            ArmorSpells = ["Impenetrability"],
            BuffWeapon = true,
            BuffArmor = true,
            RebuffWhenRemainingSeconds = 60d,
        };
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(settings);
        surface.Enchantments.Add(new PluginActiveEnchantment(10u, 100u, 6, 1800d)); // strength up for longer than a pass reaches
        surface.OwnedItems.Add(Armor(0x8000_0001u, "Coat"));
        surface.OwnedItems.Add(Armor(0x8000_0002u, "Leggings"));

        // The aura first: a self-cast, judged by the registry and the landed record.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Contains("Blood Drinker", reason);
        behavior.Execute(Context(surface, clock));
        Assert.Equal("cast:13", surface.Commands[^1]);
        surface.CompleteCast(13, target: surface.ObjectId);
        surface.ReportCast(surface.ObjectId, 13u, 1800d);
        behavior.Execute(Context(surface, clock));

        // The armor is asked about first - what is on a piece is only ever
        // learnt from the server - then Impenetrability is cast once, on
        // the character: the server dresses every worn piece with it, and
        // the record says so for each until its next appraisal.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out reason));
        Assert.Equal("asking about Coat", reason);
        behavior.Execute(Context(surface, clock));
        Assert.Equal("appraise:2147483649", surface.Commands[^1]);
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out reason));
        Assert.Contains("Impenetrability", reason);
        behavior.Execute(Context(surface, clock));
        Assert.Equal($"cast:14@{surface.ObjectId}", surface.Commands[^1]);
        surface.CompleteCast(14, target: surface.ObjectId);
        behavior.Execute(Context(surface, clock));
        Assert.Equal(2, surface.Landed.Count(l => l.SpellId == 14u));   // the Coat and the Leggings both on record
        behavior.Execute(Context(surface, clock));
        Assert.Equal("appraise:2147483650", surface.Commands[^1]);   // the Leggings still get asked about

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.DoesNotContain("cast:14@2147483649", surface.Commands);
    }

    [Fact]
    public void ArmorAlreadyBuffedByTheAppraisalIsLeftAloneUntilTheAppraisalSaysOtherwise()
    {
        // After a relogin the client has no record of what it cast on the
        // armor, and nothing of it is in the character's registry; the
        // appraisal shows the buff on the piece, and that is enough. Once
        // a later appraisal no longer lists it, it is put back.
        var settings = new BuffSettings
        {
            Spells = [],
            ArmorSpells = ["Impenetrability"],
            BuffArmor = true,
            RebuffWhenRemainingSeconds = 60d,
        };
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(settings);
        surface.OwnedItems.Add(Armor(0x8000_0001u, "Coat"));
        surface.OnItem[0x8000_0001u] = [14u];

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("asking about Coat", reason);
        behavior.Execute(Context(surface, clock));
        Assert.Equal(["appraise:2147483649"], surface.Commands);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        BuffStatus status = behavior.Report(Context(surface, clock).Board).Single();
        Assert.True(status.IsUp);
        Assert.True(status.IsUpUntimed);
        Assert.False(status.Due);

        // Two minutes on, the piece is asked about again; the buff has lapsed.
        clock.Advance(SelfBuffBehavior.ArmorAppraiseSeconds + 1d);
        int index = surface.OwnedItems.FindIndex(i => i.ObjectId == 0x8000_0001u);
        surface.OwnedItems[index] = surface.OwnedItems[index] with { AppraisalAgeSeconds = SelfBuffBehavior.ArmorAppraiseSeconds + 1d };
        surface.OnItem[0x8000_0001u] = [];
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out reason));
        Assert.Equal("asking about Coat", reason);
        behavior.Execute(Context(surface, clock));
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out reason));
        Assert.Equal("Impenetrability VI due", reason);
        behavior.Execute(Context(surface, clock));
        Assert.Equal($"cast:14@{surface.ObjectId}", surface.Commands[^1]);   // on the character, for every piece
    }

    private static BehaviorContext Context(FakeAutomationSurface surface, TickClock clock) =>
        new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

    [Fact]
    public void OnceTheWandIsOutEverythingExpiringSoonGoesInTheSamePass()
    {
        // Endurance crosses the minute and brings the wand out; Strength
        // has fifteen minutes left, which on its own is not due - but the
        // wand is out, so it goes in the same pass rather than bringing the
        // wand out again in fourteen minutes. Coordination, with an hour
        // left, waits.
        var settings = new BuffSettings
        {
            Spells = ["Strength Self", "Endurance Self", "Focus Self"],
            RebuffWhenRemainingSeconds = 60d,
            RebuffTogetherWithinSeconds = 1200d,
        };
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(settings);
        surface.Enchantments.Add(new PluginActiveEnchantment(10u, 100u, 6, 900d));   // strength: 15 min
        surface.Enchantments.Add(new PluginActiveEnchantment(11u, 101u, 6, 30d));    // endurance: due
        surface.Enchantments.Add(new PluginActiveEnchantment(12u, 102u, 6, 3600d));  // focus: an hour

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Endurance Self VI due", reason);
        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        surface.CompleteCast(11);
        surface.Enchantments[1] = new PluginActiveEnchantment(11u, 101u, 6, 1800d);
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);

        // The pass is open: strength is due now, focus is not.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out reason));
        Assert.Equal("Strength Self VI due", reason);
        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        surface.CompleteCast(10);
        surface.Enchantments[0] = new PluginActiveEnchantment(10u, 100u, 6, 1800d);
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);   // nothing left: the pass closes
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));

        // With the pass closed, fifteen minutes left is not due on its own.
        surface.Enchantments[2] = new PluginActiveEnchantment(12u, 102u, 6, 900d);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void CastsConfiguredBuffsInOrderAndOnlyThose()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Strength Self VI due", reason);

        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        surface.CompleteCast(10);
        surface.Enchantments.Add(new PluginActiveEnchantment(10, 100, 6, 1800d));
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);

        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        surface.CompleteCast(11);
        surface.Enchantments.Add(new PluginActiveEnchantment(11, 101, 6, 1800d));
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.Equal(["mode:Magic", "cast:10", "cast:11"], surface.Commands);
    }

    [Fact]
    public void RebuffsWhenTheActiveOneIsAboutToExpire()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);
        surface.Enchantments.Add(new PluginActiveEnchantment(10, 100, 6, 30d));
        surface.Enchantments.Add(new PluginActiveEnchantment(11, 101, 6, 900d));

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Strength Self VI due", reason);
    }

    [Fact]
    public void DoesNotOvercastAHigherTierAlreadyInForce()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);
        surface.Enchantments.Add(new PluginActiveEnchantment(999, 100, 7, 20d));
        surface.Enchantments.Add(new PluginActiveEnchantment(11, 101, 6, 900d));

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void ARefusedCastFailsTheStepAndBacksOff()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);
        surface.NextCastResult = PluginCastRequestResult.MissingComponents;

        BehaviorStep step = behavior.Execute(Context(surface, clock));

        Assert.Equal(StepResult.Failed, step.Result);
        // Strength is backing off; Endurance is next in line.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Endurance Self VI due", reason);
    }

    [Fact]
    public void ABuffThatSucceedsWithoutLandingIsRestedAfterThreeGoes()
    {
        // The host called an Other tier sent with no target a success, nine
        // thousand times in five minutes, with nothing ever in the
        // registry: three such in a row and the spell is put aside.
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);

        for (int go = 0; go < SelfBuffBehavior.NoEffectStrikes; go++)
        {
            Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
            surface.CompleteCast(10);
            Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);
            clock.Advance(0.1d);
        }
        BehaviorStep step = behavior.Execute(Context(surface, clock));

        Assert.Equal(StepResult.Failed, step.Result);
        Assert.Contains("never landed", step.Reason);
        Assert.Equal(SelfBuffBehavior.NoEffectStrikes, surface.Commands.Count(c => c == "cast:10"));
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Endurance Self VI due", reason);

        // A real cast that lands, then the next buff, is no strike at all.
        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        surface.CompleteCast(11);
        surface.Enchantments.Add(new PluginActiveEnchantment(11, 101, 6, 1800d));
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void DisabledSettingsMeanNoInterest()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) =
            Build(TwoBuffs with { Enabled = false });

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }
}
