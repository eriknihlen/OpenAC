using System.Collections.Generic;
using AcDream.Core.Spells;

namespace AcDream.Core.Tests.Spells;

public sealed class EnchantmentMathTests
{
    [Fact]
    public void Empty_ReturnsIdentity()
    {
        var mod = EnchantmentMath.GetMod(
            new List<ActiveEnchantmentRecord>(),
            SpellTable.Empty,
            EnchantmentMath.StatKey.MaxStamina);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);
    }

    [Fact]
    public void NoMatchingTableEntries_ReturnsIdentity()
    {
        // Active enchantments exist but none of them have entries in the
        // SpellTable (so we can't read Family) — they're skipped.
        var enchantments = new[]
        {
            new ActiveEnchantmentRecord(SpellId: 9999u, LayerId: 1u, Duration: 60f, CasterGuid: 0u),
            new ActiveEnchantmentRecord(SpellId: 8888u, LayerId: 2u, Duration: 60f, CasterGuid: 0u),
        };
        var mod = EnchantmentMath.GetMod(enchantments, SpellTable.Empty,
                                         EnchantmentMath.StatKey.MaxStamina);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);
    }

    [Fact]
    public void StatKey_ConstantsMatchAceEnum()
    {
        Assert.Equal(1u, EnchantmentMath.StatKey.MaxHealth);
        Assert.Equal(3u, EnchantmentMath.StatKey.MaxStamina);
        Assert.Equal(5u, EnchantmentMath.StatKey.MaxMana);
    }

    [Fact]
    public void Identity_IsOneAndZero()
    {
        Assert.Equal(1.0f, EnchantmentMath.VitalMod.Identity.Multiplier);
        Assert.Equal(0.0f, EnchantmentMath.VitalMod.Identity.Additive);
    }

    [Fact]
    public void FamilyStacking_DeduplicatesByFamily_KeepsHigherSpellId()
    {
        var table = LoadTable(
            (1u, "Strength I", 1u),
            (132u, "Strength VII", 1u));   // same family
        var enchantments = new[]
        {
            new ActiveEnchantmentRecord(SpellId: 1u,   LayerId: 100u, Duration: 60f, CasterGuid: 0u),
            new ActiveEnchantmentRecord(SpellId: 132u, LayerId: 101u, Duration: 60f, CasterGuid: 0u),
        };
        var mod = EnchantmentMath.GetMod(enchantments, table,
                                         EnchantmentMath.StatKey.MaxStamina);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);
    }

    [Fact]
    public void Family_Zero_DoesNotDedup()
    {
        var table = LoadTable(
            (10u, "Buff A", 0u),
            (20u, "Buff B", 0u));
        var enchantments = new[]
        {
            new ActiveEnchantmentRecord(SpellId: 10u, LayerId: 100u, Duration: 60f, CasterGuid: 0u),
            new ActiveEnchantmentRecord(SpellId: 20u, LayerId: 101u, Duration: 60f, CasterGuid: 0u),
        };
        var mod = EnchantmentMath.GetMod(enchantments, table,
                                         EnchantmentMath.StatKey.MaxStamina);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);
    }

    [Fact]
    public void GetMod_MultiplicativeBucket_AppliesProductWhenStatKeyMatches()
    {
        // Two multiplicative enchantments on MaxStamina (key=3): values
        // 1.2 and 1.1 → final multiplier = 1.2 × 1.1 = 1.32.
        // Different families so neither dedups the other.
        var table = LoadTable(
            (10u, "Buff10", 100u),
            (11u, "Buff11", 200u));
        var enchantments = new[]
        {
            MakeMultRecord(spellId: 10, layer: 1, statKey: 3u, val: 1.2f),
            MakeMultRecord(spellId: 11, layer: 2, statKey: 3u, val: 1.1f),
        };
        var mod = EnchantmentMath.GetMod(enchantments, table,
                                         EnchantmentMath.StatKey.MaxStamina);
        Assert.Equal(1.32f, mod.Multiplier, precision: 4);
        Assert.Equal(0.0f, mod.Additive);
    }

    [Fact]
    public void GetMod_AdditiveBucket_SumsValueWhenStatKeyMatches()
    {
        var table = LoadTable(
            (20u, "Add1", 300u),
            (21u, "Add2", 301u));
        var enchantments = new[]
        {
            MakeAddRecord(spellId: 20, layer: 1, statKey: 5u /* MaxMana */, val: 25f),
            MakeAddRecord(spellId: 21, layer: 2, statKey: 5u, val: 50f),
        };
        var mod = EnchantmentMath.GetMod(enchantments, table,
                                         EnchantmentMath.StatKey.MaxMana);
        Assert.Equal(1.0f, mod.Multiplier);
        Assert.Equal(75.0f, mod.Additive);
    }

    [Fact]
    public void GetMod_StatKeyMismatch_DoesNotContribute()
    {
        var table = LoadTable((30u, "Health buff", 500u));
        // Buff modifies MaxHealth (key=1) but we ask for MaxStamina (key=3).
        var enchantments = new[]
        {
            MakeMultRecord(spellId: 30, layer: 1, statKey: 1u /* MaxHealth */, val: 1.5f),
        };
        var mod = EnchantmentMath.GetMod(enchantments, table,
                                         EnchantmentMath.StatKey.MaxStamina);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);
    }

    [Fact]
    public void GetMod_VitaeBucket_AppliedMultiplicativelyAfterBuffs()
    {
        // Vitae = 0.85 (15% death penalty) on MaxHealth, plus a +10
        // additive from a Restoration buff. Family 0 means each is its
        // own bucket.
        var table = LoadTable(
            (40u, "Restoration", 0u),
            (41u, "Vitae",       0u));
        var enchantments = new[]
        {
            MakeAddRecord(spellId: 40, layer: 1, statKey: 1u /* MaxHealth */, val: 10f),
            MakeVitaeRecord(spellId: 41, layer: 2, statKey: 1u, val: 0.85f),
        };
        var mod = EnchantmentMath.GetMod(enchantments, table,
                                         EnchantmentMath.StatKey.MaxHealth);
        // Vitae multiplier 0.85, additive 10.
        Assert.Equal(0.85f, mod.Multiplier, precision: 3);
        Assert.Equal(10.0f, mod.Additive);
    }

    [Fact]
    public void GetMod_Vitae_AppliesEvenWhenStatModKeyIsZero()
    {
        var table = LoadTable((666u, "Vitae", 0u));
        var enchantments = new[]
        {
            MakeVitaeRecord(spellId: 666, layer: 0, statKey: 0u /* "any" */, val: 0.95f),
        };
        // Query for MaxStamina; Vitae key=0 should still apply.
        var mod = EnchantmentMath.GetMod(enchantments, table,
                                         EnchantmentMath.StatKey.MaxStamina);
        Assert.Equal(0.95f, mod.Multiplier, precision: 3);
    }

    [Fact]
    public void GetMod_FamilyStacking_PicksHigherSpellId()
    {
        var table = LoadTable(
            (10u, "Strength I",    1u),    // Family=1
            (132u, "Strength VII", 1u));   // same family
        var enchantments = new[]
        {
            MakeMultRecord(spellId: 10u,  layer: 1, statKey: 3u, val: 1.1f),
            MakeMultRecord(spellId: 132u, layer: 2, statKey: 3u, val: 1.5f),
        };
        var mod = EnchantmentMath.GetMod(enchantments, table,
                                         EnchantmentMath.StatKey.MaxStamina);
        // Only the higher-id buff (1.5) applies.
        Assert.Equal(1.5f, mod.Multiplier, precision: 3);
    }


    [Fact]
    public void GetMod_RequiredType_ExcludesCrossDomainKeyCollision()
    {
        var table = LoadTable((50u, "Strength Buff", 0u));
        var enchantments = new[]
        {
            MakeTypedMultRecord(spellId: 50, layer: 1, statKey: 1u,
                statModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute, val: 1.5f),
        };

        var secondAttMod = EnchantmentMath.GetMod(enchantments, table, statKey: 1u,
            EnchantmentMath.EnchantmentTypeFlag.SecondAtt);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, secondAttMod);

        var attributeMod = EnchantmentMath.GetMod(enchantments, table, statKey: 1u,
            EnchantmentMath.EnchantmentTypeFlag.Attribute);
        Assert.Equal(1.5f, attributeMod.Multiplier, precision: 3);
    }

    [Fact]
    public void GetMod_IncludeVitaeFalse_ExcludesVitaeEvenWhenActive()
    {
        var table = LoadTable((60u, "Vitae", 0u));
        var enchantments = new[]
        {
            MakeVitaeRecord(spellId: 60, layer: 1, statKey: 0u, val: 0.67f),
        };

        var mod = EnchantmentMath.GetMod(enchantments, table, statKey: 1u,
            EnchantmentMath.EnchantmentTypeFlag.Attribute, includeVitae: false);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);
    }

    [Fact]
    public void EnchantAttribute_NoMods_ReturnsBaseTruncated()
    {
        Assert.Equal(200, EnchantmentMath.EnchantAttribute(EnchantmentMath.VitalMod.Identity, 200u));
    }

    [Fact]
    public void EnchantAttribute_Buff_AppliesMultiplierAndTruncates()
    {
        // 200 base with a +10% buff (unrelated to vitae — attributes are
        // vitae-immune) → 220.
        var mod = new EnchantmentMath.VitalMod(1.1f, 0f);
        Assert.Equal(220, EnchantmentMath.EnchantAttribute(mod, 200u));
    }

    [Fact]
    public void EnchantAttribute_FloorsAtOne_WhenBaseBelowTenAndDebuffed()
    {
        var mod = new EnchantmentMath.VitalMod(0.1f, 0f);
        Assert.Equal(1, EnchantmentMath.EnchantAttribute(mod, 5u));
    }

    [Fact]
    public void EnchantAttribute_FloorsAtTen_WhenBaseAtOrAboveTenAndDebuffed()
    {
        var mod = new EnchantmentMath.VitalMod(0.1f, 0f);
        Assert.Equal(10, EnchantmentMath.EnchantAttribute(mod, 50u));
    }

    [Fact]
    public void EnchantSkill_ThirtyThreePercentVitae_MatchesGoldenValue()
    {
        // 33% vitae penalty on a base-303 skill: 303 * 0.67 = 203.01 -> 203.
        var mod = new EnchantmentMath.VitalMod(0.67f, 0f);
        Assert.Equal(203, EnchantmentMath.EnchantSkill(mod, 303u));
    }

    [Fact]
    public void EnchantSkill_BuffPlusVitaeComposition_MatchesGoldenValue()
    {
        var mod = new EnchantmentMath.VitalMod(0.67f, 50f);
        Assert.Equal(251, EnchantmentMath.EnchantSkill(mod, 300u));
    }

    [Fact]
    public void EnchantSkill_ZeroFloorsBelowHalf()
    {
        var mod = new EnchantmentMath.VitalMod(0.001f, 0f);
        Assert.Equal(0, EnchantmentMath.EnchantSkill(mod, 10u));
    }

    [Fact]
    public void GetVitaeMultiplier_NoVitae_ReturnsOne()
    {
        var enchantments = new[]
        {
            MakeMultRecord(spellId: 1, layer: 1, statKey: 1u, val: 1.5f),
        };
        Assert.Equal(1.0f, EnchantmentMath.GetVitaeMultiplier(enchantments));
    }

    [Fact]
    public void GetVitaeMultiplier_WithVitae_ReturnsItsValue()
    {
        var enchantments = new[]
        {
            MakeVitaeRecord(spellId: 1, layer: 1, statKey: 0u, val: 0.67f),
            MakeMultRecord(spellId: 2, layer: 2, statKey: 1u, val: 1.5f),  // must not affect vitae isolation
        };
        Assert.Equal(0.67f, EnchantmentMath.GetVitaeMultiplier(enchantments), precision: 3);
    }

    [Fact]
    public void SkillVitaeModifier_ThirtyThreePercent_MatchesGoldenValue()
    {
        var enchantments = new[]
        {
            MakeVitaeRecord(spellId: 1, layer: 1, statKey: 0u, val: 0.67f),
        };
        Assert.Equal(-100, EnchantmentMath.SkillVitaeModifier(enchantments, baseValue: 303u));
    }

    [Fact]
    public void SkillVitaeModifier_NoVitae_ReturnsZero()
    {
        Assert.Equal(0, EnchantmentMath.SkillVitaeModifier(
            System.Array.Empty<ActiveEnchantmentRecord>(), baseValue: 303u));
    }

    private static ActiveEnchantmentRecord MakeTypedMultRecord(
        uint spellId, uint layer, uint statKey, uint statModType, float val) =>
        new(spellId, layer, 60f, 0u, StatModType: statModType, StatModKey: statKey,
            StatModValue: val, Bucket: 1u);

    private static ActiveEnchantmentRecord MakeMultRecord(uint spellId, uint layer, uint statKey, float val) =>
        new(spellId, layer, 60f, 0u, StatModType: 0, StatModKey: statKey, StatModValue: val, Bucket: 1u);

    private static ActiveEnchantmentRecord MakeAddRecord(uint spellId, uint layer, uint statKey, float val) =>
        new(spellId, layer, 60f, 0u, StatModType: 0, StatModKey: statKey, StatModValue: val, Bucket: 2u);

    private static ActiveEnchantmentRecord MakeVitaeRecord(uint spellId, uint layer, uint statKey, float val) =>
        new(spellId, layer, -1f, 0u, StatModType: 0, StatModKey: statKey, StatModValue: val, Bucket: 4u);


    private static ActiveEnchantmentRecord MakeSkillMultRecord(
        uint spellId, uint layer, uint skillId, float val) =>
        new(
            spellId, layer, 60f, 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Skill,
            StatModKey: skillId,
            StatModValue: val,
            Bucket: 1u);

    private static ActiveEnchantmentRecord MakeSkillAddRecord(
        uint spellId, uint layer, uint skillId, float val) =>
        new(
            spellId, layer, 60f, 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Skill,
            StatModKey: skillId,
            StatModValue: val,
            Bucket: 2u);

    [Fact]
    public void EnchantmentTypeFlag_Skill_MatchesAceEnchantmentTypeFlags()
    {
        Assert.Equal(0x0000010u, (uint)EnchantmentMath.EnchantmentTypeFlag.Skill);
        Assert.Equal(0x0000002u, (uint)EnchantmentMath.EnchantmentTypeFlag.SecondAtt);
    }

    [Fact]
    public void GetSkillMod_Empty_ReturnsIdentity()
    {
        var mod = EnchantmentMath.GetSkillMod(
            Array.Empty<ActiveEnchantmentRecord>(), SpellTable.Empty, skillId: 24u);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);
    }

    [Fact]
    public void GetSkillMod_MultiplicativeSkillBuff_AppliesWhenSkillIdMatches()
    {
        var table = LoadTable((50u, "Run Buff", 400u));
        var enchantments = new[] { MakeSkillMultRecord(50, 1, skillId: 24u, val: 1.2f) };
        var mod = EnchantmentMath.GetSkillMod(enchantments, table, skillId: 24u);
        Assert.Equal(1.2f, mod.Multiplier, precision: 4);
    }

    [Fact]
    public void GetSkillMod_AdditiveSkillBuff_AppliesWhenSkillIdMatches()
    {
        var table = LoadTable((51u, "Jump Buff", 401u));
        var enchantments = new[] { MakeSkillAddRecord(51, 1, skillId: 22u, val: 15f) };
        var mod = EnchantmentMath.GetSkillMod(enchantments, table, skillId: 22u);
        Assert.Equal(15.0f, mod.Additive, precision: 4);
    }

    [Fact]
    public void GetSkillMod_SkillIdMismatch_DoesNotContribute()
    {
        var table = LoadTable((52u, "Melee Buff", 402u));
        // Buff targets skill id 7 (some melee skill), we ask for Run (24).
        var enchantments = new[] { MakeSkillMultRecord(52, 1, skillId: 7u, val: 1.5f) };
        var mod = EnchantmentMath.GetSkillMod(enchantments, table, skillId: 24u);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);
    }

    [Fact]
    public void GetSkillMod_NamespaceCollision_VitalTypedRecordDoesNotLeakIntoSkillQuery()
    {
        var table = LoadTable((53u, "Vital Buff", 403u));
        var enchantments = new[]
        {
            new ActiveEnchantmentRecord(
                53u, 1u, 60f, 0u,
                StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.SecondAtt,
                StatModKey: 24u,
                StatModValue: 1.5f,
                Bucket: 1u),
        };
        var mod = EnchantmentMath.GetSkillMod(enchantments, table, skillId: 24u);
        Assert.Equal(EnchantmentMath.VitalMod.Identity, mod);

        var vitalMod = EnchantmentMath.GetMod(enchantments, table, statKey: 24u);
        Assert.Equal(1.5f, vitalMod.Multiplier, precision: 4);
    }

    [Fact]
    public void GetSkillMod_Vitae_AppliesUnconditionallyLikeVitals()
    {
        var table = LoadTable((54u, "Vitae", 0u));
        var enchantments = new[] { MakeVitaeRecord(54, 0, statKey: 0u, val: 0.9f) };
        var mod = EnchantmentMath.GetSkillMod(enchantments, table, skillId: 24u);
        Assert.Equal(0.9f, mod.Multiplier, precision: 3);
    }

    private static SpellTable LoadTable(params (uint id, string name, uint family)[] rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Spell ID,Spell ID [Hex],Name,SortKey,IconId [Hex],Difficulty,Duration,Family,Flags [Hex],Generation,IsDebuff,IsFastWindup,IsFellowship,IsIrresistible,IsOffensive,IsUntargetted,Mana,School,Speed,Spell Words,CasterEffect,TargetEffect,TargetMask [Hex],Type,Description,Unknown1,Unknown2,Unknown3,Unknown4,Unknown5,Unknown6,Unknown7,Unknown8,Unknown9,Unknown10");
        foreach (var (id, name, family) in rows)
        {
            sb.Append(id).Append(',').Append("0x").Append(id.ToString("X")).Append(',')
              .Append(name).Append(",0,0x0,1,1,").Append(family).Append(",0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0")
              .AppendLine();
        }
        return SpellTable.LoadFromReader(new System.IO.StringReader(sb.ToString()));
    }
}
