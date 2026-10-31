using System.Collections.Generic;
using AcDream.Core.Spells;
using static AcDream.Core.Spells.EnchantmentMath;

namespace AcDream.Core.Tests.Spells;

public sealed class EnchantmentMathTests
{
    private const uint Vital = (uint)EnchantmentTypeFlag.SecondAtt;
    private const uint Multiply = (uint)EnchantmentTypeFlag.Multiplicative;
    private const uint Add = (uint)EnchantmentTypeFlag.Additive;

    [Fact]
    public void Empty_ReturnsIdentity()
    {
        Assert.Equal(VitalMod.Identity, VitalModOf([], StatKey.MaxStamina));
    }

    [Fact]
    public void RecordsWithoutAStatMod_ContributeNothing()
    {
        var enchantments = new[]
        {
            new ActiveEnchantmentRecord(9999u, 1u, 60d, 0u),
            new ActiveEnchantmentRecord(8888u, 2u, 60d, 0u),
        };
        Assert.Equal(VitalMod.Identity, VitalModOf(enchantments, StatKey.MaxStamina));
    }

    [Fact]
    public void StatKey_ConstantsMatchAceEnum()
    {
        Assert.Equal(1u, StatKey.MaxHealth);
        Assert.Equal(3u, StatKey.MaxStamina);
        Assert.Equal(5u, StatKey.MaxMana);
    }

    [Fact]
    public void TypeFlags_MatchTheWireValues()
    {
        Assert.Equal(0x0000010u, (uint)EnchantmentTypeFlag.Skill);
        Assert.Equal(0x0000002u, (uint)EnchantmentTypeFlag.SecondAtt);
        Assert.Equal(0x0004000u, Multiply);
        Assert.Equal(0x0008000u, Add);
        Assert.Equal(0x0010000u, (uint)EnchantmentTypeFlag.AttackSkills);
        Assert.Equal(0x0020000u, (uint)EnchantmentTypeFlag.DefenseSkills);
    }

    [Fact]
    public void Identity_IsOneAndZero()
    {
        Assert.Equal(1.0f, VitalMod.Identity.Multiplier);
        Assert.Equal(0.0f, VitalMod.Identity.Additive);
        Assert.Empty(VitalMod.Identity.Steps);
    }

    [Fact]
    public void GetMod_MultiplicativeRecords_Multiply()
    {
        var mod = VitalModOf(
            [MakeMultRecord(10u, 1u, statKey: 3u, val: 1.2f), MakeMultRecord(11u, 2u, statKey: 3u, val: 1.1f)],
            StatKey.MaxStamina);
        Assert.Equal(1.32f, mod.Multiplier, precision: 4);
        Assert.Equal(0.0f, mod.Additive);
    }

    [Fact]
    public void GetMod_AdditiveRecords_Sum()
    {
        var mod = VitalModOf(
            [MakeAddRecord(20u, 1u, statKey: 5u, val: 25f), MakeAddRecord(21u, 2u, statKey: 5u, val: 50f)],
            StatKey.MaxMana);
        Assert.Equal(1.0f, mod.Multiplier);
        Assert.Equal(75.0f, mod.Additive);
    }

    [Fact]
    public void GetMod_StatKeyMismatch_DoesNotContribute()
    {
        // Buff modifies MaxHealth (key=1) but we ask for MaxStamina (key=3).
        Assert.Equal(
            VitalMod.Identity,
            VitalModOf([MakeMultRecord(30u, 1u, statKey: 1u, val: 1.5f)], StatKey.MaxStamina));
    }

    [Fact]
    public void GetMod_Vitae_MultipliesFirst()
    {
        var mod = VitalModOf(
            [MakeAddRecord(40u, 1u, statKey: 1u, val: 10f), MakeVitaeRecord(41u, 2u, statKey: 1u, val: 0.85f)],
            StatKey.MaxHealth);
        Assert.Equal([EnchantStep.Multiply(0.85f), EnchantStep.Add(10f)], mod.Steps);
        Assert.Equal(95f, mod.Apply(100f), precision: 3);
    }

    [Fact]
    public void GetMod_Vitae_AppliesEvenWhenStatModKeyIsZero()
    {
        var mod = VitalModOf([MakeVitaeRecord(666u, 0u, statKey: 0u, val: 0.95f)], StatKey.MaxStamina);
        Assert.Equal(0.95f, mod.Multiplier, precision: 3);
    }

    [Fact]
    public void GetMod_IncludeVitaeFalse_ExcludesVitaeEvenWhenActive()
    {
        var mod = GetMod(
            [MakeVitaeRecord(60u, 1u, statKey: 0u, val: 0.67f)],
            statKey: 1u, EnchantmentTypeFlag.Attribute, includeVitae: false);
        Assert.Equal(VitalMod.Identity, mod);
    }

    [Fact]
    public void Steps_RoundInTurn()
    {
        // 200 x 0.95 = 190 and 190 x 1.1 = 209 in single precision; 200 x
        // (0.95 x 1.1) is 208.99998.
        var mod = VitalMod.FromSteps([EnchantStep.Multiply(0.95f), EnchantStep.Multiply(1.1f)]);
        Assert.Equal(209f, mod.Apply(200f));
        var product = VitalMod.FromSteps([EnchantStep.Multiply(0.95f * 1.1f)]);
        Assert.True(product.Apply(200f) < 209f);
    }

    [Fact]
    public void EnchantStep_TheAdditiveBitIsCheckedFirst()
    {
        ActiveEnchantmentRecord record = MakeMultRecord(1u, 1u, statKey: 1u, val: 10f);
        Assert.Equal(EnchantStep.Add(10f), EnchantStep.Of(record with { StatModType = Vital | Multiply | Add }));
        Assert.Null(EnchantStep.Of(record with { StatModType = Vital }));
        Assert.Null(EnchantStep.Of(record with { StatModValue = null }));
    }

    [Fact]
    public void GetMod_OneCategoryKeepsItsStrongestRecord()
    {
        // Same category: the higher power wins, whatever the spell id.
        ActiveEnchantmentRecord weak = Ranked(MakeMultRecord(132u, 2u, statKey: 3u, val: 1.5f), category: 1u, power: 1u);
        ActiveEnchantmentRecord strong = Ranked(MakeMultRecord(10u, 1u, statKey: 3u, val: 1.1f), category: 1u, power: 7u);
        Assert.Equal(1.1f, VitalModOf([weak, strong], 3u).Multiplier, precision: 4);
        Assert.Equal(1.1f, VitalModOf([strong, weak], 3u).Multiplier, precision: 4);
    }

    [Fact]
    public void GetMod_CategoryZeroGroupsLikeAnyOther()
    {
        ActiveEnchantmentRecord a = Ranked(MakeAddRecord(1u, 1u, statKey: 5u, val: 40f), category: 0u, power: 0u);
        ActiveEnchantmentRecord b = Ranked(MakeAddRecord(2u, 2u, statKey: 5u, val: 56f), category: 0u, power: 0u);
        // An exact tie goes to the later challenger in list order.
        Assert.Equal(56f, VitalModOf([a, b], 5u).Additive);
        Assert.Equal(40f, VitalModOf([b, a], 5u).Additive);
    }

    [Fact]
    public void GetMod_SpellMissingFromTheTable_CountsAtTheRecordValue()
    {
        Assert.Equal(56f, VitalModOf([MakeAddRecord(9999u, 1u, statKey: 5u, val: 56f)], 5u).Additive);
    }

    [Fact]
    public void GetMod_OnlyRecordsForTheAskedStatCompeteInACategory()
    {
        // A stronger record of the same category on another stat must not
        // knock out the one that applies to the asked stat.
        ActiveEnchantmentRecord mana = Ranked(MakeAddRecord(90u, 1u, statKey: 5u, val: 25f), category: 50u, power: 100u);
        ActiveEnchantmentRecord health = Ranked(MakeAddRecord(91u, 2u, statKey: 1u, val: 99f), category: 50u, power: 900u);
        Assert.Equal(25f, VitalModOf([mana, health], StatKey.MaxMana).Additive);
    }

    [Fact]
    public void GetMod_MultiplicativeAndAdditiveRecordsShareOneList()
    {
        ActiveEnchantmentRecord scale = Ranked(MakeMultRecord(1u, 1u, statKey: 5u, val: 1.1f), category: 9u, power: 10u);
        ActiveEnchantmentRecord plus = Ranked(MakeAddRecord(2u, 2u, statKey: 5u, val: 10f), category: 9u, power: 5u);
        Assert.Equal([EnchantStep.Multiply(1.1f)], VitalModOf([scale, plus], 5u).Steps);

        plus = plus with { PowerLevel = 20u };
        Assert.Equal([EnchantStep.Add(10f)], VitalModOf([scale, plus], 5u).Steps);
        // The additive list is culled after the multiplicative one, whatever
        // order the records come in.
        Assert.Equal([EnchantStep.Add(10f)], VitalModOf([plus, scale], 5u).Steps);
    }

    [Fact]
    public void GetMod_RequiredType_ExcludesCrossDomainKeyCollision()
    {
        var enchantments = new[]
        {
            MakeTypedMultRecord(50u, 1u, statKey: 1u, statModType: (uint)EnchantmentTypeFlag.Attribute, val: 1.5f),
        };

        Assert.Equal(VitalMod.Identity, GetMod(enchantments, statKey: 1u, EnchantmentTypeFlag.SecondAtt));
        Assert.Equal(
            1.5f,
            GetMod(enchantments, statKey: 1u, EnchantmentTypeFlag.Attribute).Multiplier,
            precision: 3);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public void GetMod_MultipleStat_AppliesToEveryAttributeWhateverItsKey(uint blessingKey)
    {
        uint blessingType = (uint)(EnchantmentTypeFlag.Attribute | EnchantmentTypeFlag.MultipleStat);
        var enchantments = new[]
        {
            MakeTypedAddRecord(70u, 1u, statKey: 1u, statModType: (uint)EnchantmentTypeFlag.Attribute, val: 40f),
            MakeTypedAddRecord(71u, 2u, statKey: blessingKey, statModType: blessingType, val: 9f),
        };

        for (uint key = 1; key <= 6; key++)
        {
            var mod = GetMod(enchantments, statKey: key, EnchantmentTypeFlag.Attribute);
            Assert.Equal(key == 1u ? 49f : 9f, mod.Additive);
        }
    }

    [Fact]
    public void GetMod_KeyZeroWithoutMultipleStat_DoesNotContribute()
    {
        var enchantments = new[]
        {
            MakeTypedAddRecord(72u, 1u, statKey: 0u, statModType: (uint)EnchantmentTypeFlag.Attribute, val: 9f),
        };
        Assert.Equal(VitalMod.Identity, GetMod(enchantments, statKey: 1u, EnchantmentTypeFlag.Attribute));
    }

    [Fact]
    public void GetMod_MultipleStatTypedAttribute_DoesNotLeakIntoSecondAttOrSkillQuery()
    {
        var enchantments = new[]
        {
            MakeTypedAddRecord(73u, 1u, statKey: 0u,
                statModType: (uint)(EnchantmentTypeFlag.Attribute | EnchantmentTypeFlag.MultipleStat),
                val: 9f),
        };

        Assert.Equal(VitalMod.Identity, GetMod(enchantments, statKey: 1u, EnchantmentTypeFlag.SecondAtt));
        Assert.Equal(VitalMod.Identity, GetSkillMod(enchantments, skillId: 1u));
    }

    [Fact]
    public void AttackAndDefenseSkillRecords_NameTheirSkills()
    {
        uint[] attackSkills = [33, 34, 41, 43, 44, 45, 46, 47, 49];
        uint[] defenseSkills = [6, 7, 15, 48];
        uint skill = (uint)EnchantmentTypeFlag.Skill;
        ActiveEnchantmentRecord attack = MakeTypedAddRecord(80u, 1u, statKey: 0u, statModType: skill | 0x10000u, val: 10f);
        ActiveEnchantmentRecord defense = MakeTypedAddRecord(81u, 2u, statKey: 0u, statModType: skill | 0x20000u, val: 10f);
        for (uint id = 0; id <= 60; id++)
        {
            Assert.Equal(attackSkills.Contains(id), AffectsAttackSkills(attack, id));
            Assert.Equal(defenseSkills.Contains(id), AffectsDefenseSkills(defense, id));
            Assert.False(AffectsAttackSkills(defense, id));
            Assert.False(AffectsDefenseSkills(attack, id));
        }

        // The family bit is still required.
        ActiveEnchantmentRecord attackAttribute =
            MakeTypedAddRecord(82u, 3u, statKey: 0u, statModType: 0x10001u, val: 10f);
        Assert.False(AffectsStat(attackAttribute, EnchantmentTypeFlag.Skill, 45u));
        Assert.True(AffectsStat(attack, EnchantmentTypeFlag.Skill, 45u));
    }

    [Fact]
    public void EnchantAttribute_NoMods_ReturnsBaseTruncated()
    {
        Assert.Equal(200, EnchantAttribute(VitalMod.Identity, 200u));
    }

    [Fact]
    public void EnchantAttribute_Buff_AppliesMultiplierAndTruncates()
    {
        // 200 base with a +10% buff (unrelated to vitae — attributes are
        // vitae-immune) → 220.
        Assert.Equal(220, EnchantAttribute(new VitalMod(1.1f, 0f), 200u));
    }

    [Fact]
    public void EnchantAttribute_FloorsAtOne_WhenBaseBelowTenAndDebuffed()
    {
        Assert.Equal(1, EnchantAttribute(new VitalMod(0.1f, 0f), 5u));
    }

    [Fact]
    public void EnchantAttribute_FloorsAtTen_WhenBaseAtOrAboveTenAndDebuffed()
    {
        Assert.Equal(10, EnchantAttribute(new VitalMod(0.1f, 0f), 50u));
    }

    [Fact]
    public void EnchantSkill_ThirtyThreePercentVitae_MatchesGoldenValue()
    {
        // 33% vitae penalty on a base-303 skill: 303 * 0.67 = 203.01 -> 203.
        Assert.Equal(203, EnchantSkill(new VitalMod(0.67f, 0f), 303u));
    }

    [Fact]
    public void EnchantSkill_BuffPlusVitaeComposition_MatchesGoldenValue()
    {
        Assert.Equal(251, EnchantSkill(new VitalMod(0.67f, 50f), 300u));
    }

    [Fact]
    public void EnchantSkill_ZeroFloorsBelowHalf()
    {
        Assert.Equal(0, EnchantSkill(new VitalMod(0.001f, 0f), 10u));
    }

    [Fact]
    public void GetVitaeMultiplier_NoVitae_ReturnsOne()
    {
        Assert.Equal(1.0f, GetVitaeMultiplier([MakeMultRecord(1u, 1u, statKey: 1u, val: 1.5f)]));
    }

    [Fact]
    public void GetVitaeMultiplier_WithVitae_ReturnsItsValue()
    {
        var enchantments = new[]
        {
            MakeVitaeRecord(1u, 1u, statKey: 0u, val: 0.67f),
            MakeMultRecord(2u, 2u, statKey: 1u, val: 1.5f),  // must not affect vitae isolation
        };
        Assert.Equal(0.67f, GetVitaeMultiplier(enchantments), precision: 3);
    }

    [Fact]
    public void SkillVitaeModifier_ThirtyThreePercent_MatchesGoldenValue()
    {
        Assert.Equal(-100, SkillVitaeModifier([MakeVitaeRecord(1u, 1u, statKey: 0u, val: 0.67f)], baseValue: 303u));
    }

    [Fact]
    public void SkillVitaeModifier_NoVitae_ReturnsZero()
    {
        Assert.Equal(0, SkillVitaeModifier(Array.Empty<ActiveEnchantmentRecord>(), baseValue: 303u));
    }

    [Fact]
    public void GetSkillMod_Empty_ReturnsIdentity()
    {
        Assert.Equal(VitalMod.Identity, GetSkillMod(Array.Empty<ActiveEnchantmentRecord>(), skillId: 24u));
    }

    [Fact]
    public void GetSkillMod_MultiplicativeSkillBuff_AppliesWhenSkillIdMatches()
    {
        var mod = GetSkillMod([MakeSkillMultRecord(50u, 1u, skillId: 24u, val: 1.2f)], skillId: 24u);
        Assert.Equal(1.2f, mod.Multiplier, precision: 4);
    }

    [Fact]
    public void GetSkillMod_AdditiveSkillBuff_AppliesWhenSkillIdMatches()
    {
        var mod = GetSkillMod([MakeSkillAddRecord(51u, 1u, skillId: 22u, val: 15f)], skillId: 22u);
        Assert.Equal(15.0f, mod.Additive, precision: 4);
    }

    [Fact]
    public void GetSkillMod_SkillIdMismatch_DoesNotContribute()
    {
        // Buff targets skill id 7 (some melee skill), we ask for Run (24).
        Assert.Equal(
            VitalMod.Identity,
            GetSkillMod([MakeSkillMultRecord(52u, 1u, skillId: 7u, val: 1.5f)], skillId: 24u));
    }

    [Fact]
    public void GetSkillMod_NamespaceCollision_VitalTypedRecordDoesNotLeakIntoSkillQuery()
    {
        var enchantments = new[] { MakeMultRecord(53u, 1u, statKey: 24u, val: 1.5f) };
        Assert.Equal(VitalMod.Identity, GetSkillMod(enchantments, skillId: 24u));
    }

    [Fact]
    public void GetSkillMod_Vitae_AppliesUnconditionallyLikeVitals()
    {
        var mod = GetSkillMod([MakeVitaeRecord(54u, 0u, statKey: 0u, val: 0.9f)], skillId: 24u);
        Assert.Equal(0.9f, mod.Multiplier, precision: 3);
    }

    private static VitalMod VitalModOf(IEnumerable<ActiveEnchantmentRecord> enchantments, uint statKey) =>
        GetMod(enchantments, statKey, EnchantmentTypeFlag.SecondAtt);

    private static ActiveEnchantmentRecord Ranked(
        ActiveEnchantmentRecord record, uint category, uint power, double start = 0d) =>
        record with { SpellCategory = category, PowerLevel = power, StartTime = start };

    /// <summary>
    /// A record as the server sends one: its operation bit matches its
    /// list, and its spell category is its own (the spell id).
    /// </summary>
    private static ActiveEnchantmentRecord Record(
        uint spellId, uint layer, uint bucket, uint statModType, uint statKey, float val)
    {
        uint operation = bucket switch
        {
            1u => Multiply,
            2u => Add,
            _ => 0u,
        };
        return new ActiveEnchantmentRecord(
            spellId, layer, 60d, 0u,
            StatModType: statModType | operation,
            StatModKey: statKey,
            StatModValue: val,
            Bucket: bucket,
            SpellCategory: spellId);
    }

    private static ActiveEnchantmentRecord MakeMultRecord(uint spellId, uint layer, uint statKey, float val) =>
        Record(spellId, layer, bucket: 1u, Vital, statKey, val);

    private static ActiveEnchantmentRecord MakeAddRecord(uint spellId, uint layer, uint statKey, float val) =>
        Record(spellId, layer, bucket: 2u, Vital, statKey, val);

    private static ActiveEnchantmentRecord MakeTypedMultRecord(
        uint spellId, uint layer, uint statKey, uint statModType, float val) =>
        Record(spellId, layer, bucket: 1u, statModType, statKey, val);

    private static ActiveEnchantmentRecord MakeTypedAddRecord(
        uint spellId, uint layer, uint statKey, uint statModType, float val) =>
        Record(spellId, layer, bucket: 2u, statModType, statKey, val);

    private static ActiveEnchantmentRecord MakeSkillMultRecord(uint spellId, uint layer, uint skillId, float val) =>
        Record(spellId, layer, bucket: 1u, (uint)EnchantmentTypeFlag.Skill, skillId, val);

    private static ActiveEnchantmentRecord MakeSkillAddRecord(uint spellId, uint layer, uint skillId, float val) =>
        Record(spellId, layer, bucket: 2u, (uint)EnchantmentTypeFlag.Skill, skillId, val);

    private static ActiveEnchantmentRecord MakeVitaeRecord(uint spellId, uint layer, uint statKey, float val) =>
        new(spellId, layer, -1d, 0u, StatModType: 0u, StatModKey: statKey, StatModValue: val, Bucket: 4u);
}
