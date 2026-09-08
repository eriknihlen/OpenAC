using AcDream.Core.Items;
using AcDream.Core.Player;
using AcDream.Core.Properties;
using AcDream.Core.Spells;

namespace AcDream.Core.Tests.Player;

public sealed class LocalPlayerStateTests
{
    [Fact]
    public void Defaults_AllVitalsNull_PercentsNull()
    {
        var s = new LocalPlayerState();

        Assert.Null(s.Get(LocalPlayerState.VitalKind.Health));
        Assert.Null(s.Get(LocalPlayerState.VitalKind.Stamina));
        Assert.Null(s.Get(LocalPlayerState.VitalKind.Mana));
        Assert.Null(s.HealthPercent);
        Assert.Null(s.StaminaPercent);
        Assert.Null(s.ManaPercent);
    }

    [Theory]
    [InlineData(1u, LocalPlayerState.VitalKind.Health)]
    [InlineData(2u, LocalPlayerState.VitalKind.Health)]
    [InlineData(3u, LocalPlayerState.VitalKind.Stamina)]
    [InlineData(4u, LocalPlayerState.VitalKind.Stamina)]
    [InlineData(5u, LocalPlayerState.VitalKind.Mana)]
    [InlineData(6u, LocalPlayerState.VitalKind.Mana)]
    // PlayerDescription attribute-block IDs.
    [InlineData(7u, LocalPlayerState.VitalKind.Health)]
    [InlineData(8u, LocalPlayerState.VitalKind.Stamina)]
    [InlineData(9u, LocalPlayerState.VitalKind.Mana)]
    public void VitalIdToKind_MapsBothIdSystems_ToSameKind(uint vitalId, LocalPlayerState.VitalKind expected)
    {
        Assert.Equal(expected, LocalPlayerState.VitalIdToKind(vitalId));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(10u)]
    [InlineData(99u)]
    public void VitalIdToKind_ReturnsNull_ForUnknownId(uint vitalId)
    {
        Assert.Null(LocalPlayerState.VitalIdToKind(vitalId));
    }

    [Theory]
    [InlineData(1u, LocalPlayerState.AttributeKind.Strength)]
    [InlineData(2u, LocalPlayerState.AttributeKind.Endurance)]
    [InlineData(3u, LocalPlayerState.AttributeKind.Quickness)]
    [InlineData(4u, LocalPlayerState.AttributeKind.Coordination)]
    [InlineData(5u, LocalPlayerState.AttributeKind.Focus)]
    [InlineData(6u, LocalPlayerState.AttributeKind.Self)]
    public void AttributeIdToKind_MapsPrimaryAttrIds(uint atType, LocalPlayerState.AttributeKind expected)
    {
        Assert.Equal(expected, LocalPlayerState.AttributeIdToKind(atType));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]   // Vitals are not primary attrs in this lookup.
    [InlineData(99u)]
    public void AttributeIdToKind_ReturnsNull_ForNonPrimaryIds(uint atType)
    {
        Assert.Null(LocalPlayerState.AttributeIdToKind(atType));
    }

    [Fact]
    public void OnVitalUpdate_PopulatesSnapshot_FromFullMessage()
    {
        var s = new LocalPlayerState();
        s.OnVitalUpdate(vitalId: 4u, ranks: 100u, start: 120u, xp: 50000u, current: 180u);

        var stam = s.Get(LocalPlayerState.VitalKind.Stamina);
        Assert.NotNull(stam);
        Assert.Equal(100u,   stam!.Value.Ranks);
        Assert.Equal(120u,   stam.Value.Start);
        Assert.Equal(50000u, stam.Value.Xp);
        Assert.Equal(180u,   stam.Value.Current);
    }

    [Fact]
    public void OnAttributeUpdate_PopulatesSnapshot()
    {
        var s = new LocalPlayerState();
        s.OnAttributeUpdate(atType: 2u, ranks: 50u, start: 100u, xp: 12345u);

        var endurance = s.GetAttribute(LocalPlayerState.AttributeKind.Endurance);
        Assert.NotNull(endurance);
        Assert.Equal(50u,     endurance!.Value.Ranks);
        Assert.Equal(100u,    endurance.Value.Start);
        Assert.Equal(12345u,  endurance.Value.Xp);
        Assert.Equal(150u,    endurance.Value.Current);
    }

    [Fact]
    public void HealthPercent_UsesEnduranceContribution_DividedByTwo()
    {
        var s = new LocalPlayerState();
        s.OnAttributeUpdate(atType: 2u, ranks: 50u, start: 150u, xp: 0u);          // Endurance
        s.OnVitalUpdate(vitalId: 7u, ranks: 0u, start: 0u, xp: 0u, current: 80u);  // Health (PD id)

        Assert.Equal(100u, s.GetMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(0.8f, s.HealthPercent!.Value, precision: 3);
    }

    [Fact]
    public void StaminaPercent_UsesEnduranceContribution_FullValue()
    {
        var s = new LocalPlayerState();
        s.OnAttributeUpdate(atType: 2u, ranks: 50u, start: 150u, xp: 0u);
        s.OnVitalUpdate(vitalId: 8u, ranks: 0u, start: 0u, xp: 0u, current: 150u);

        Assert.Equal(200u, s.GetMaxApprox(LocalPlayerState.VitalKind.Stamina));
        Assert.Equal(0.75f, s.StaminaPercent!.Value, precision: 3);
    }

    [Fact]
    public void ManaPercent_UsesSelfContribution()
    {
        var s = new LocalPlayerState();
        s.OnAttributeUpdate(atType: 6u, ranks: 50u, start: 50u, xp: 0u);            // Self
        s.OnVitalUpdate(vitalId: 9u, ranks: 20u, start: 80u, xp: 0u, current: 100u); // Mana

        Assert.Equal(200u, s.GetMaxApprox(LocalPlayerState.VitalKind.Mana));
        Assert.Equal(0.5f, s.ManaPercent!.Value, precision: 3);
    }

    [Fact]
    public void Percent_ZeroWhenAttributeAndVitalBothZero()
    {
        // Without any attribute or vital ranks, MaxApprox=0 → percent null
        // (no /0). Vital received but no useful information.
        var s = new LocalPlayerState();
        s.OnVitalUpdate(vitalId: 8u, ranks: 0u, start: 0u, xp: 0u, current: 0u);

        Assert.Null(s.StaminaPercent);
    }

    [Fact]
    public void Percent_ClampsToOne_WhenCurrentExceedsMax()
    {
        var s = new LocalPlayerState();
        s.OnAttributeUpdate(atType: 2u, ranks: 50u, start: 50u, xp: 0u);
        s.OnVitalUpdate(vitalId: 8u, ranks: 0u, start: 0u, xp: 0u, current: 150u);

        Assert.Equal(1f, s.StaminaPercent!.Value);
    }

    [Fact]
    public void GetMaxApprox_PrimaryAttributeModifierWithCollidingKeyDoesNotAffectHealth()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u,
            LayerId: 1u,
            Duration: 60d,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute,
            StatModKey: 1u,
            StatModValue: 0.99999f,
            Bucket: 1u));
        var s = new LocalPlayerState(book);
        s.OnVitalUpdate(
            vitalId: 7u,
            ranks: 99_999u,
            start: 0u,
            xp: 0u,
            current: 99_999u);

        Assert.Equal(99_999u, s.GetBaseMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(99_999u, s.GetMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(1f, s.HealthPercent);
    }

    [Fact]
    public void GetMaxApprox_SecondaryAttributeModifierTruncatesLikeRetail()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u,
            LayerId: 1u,
            Duration: 60d,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.SecondAtt,
            StatModKey: EnchantmentMath.StatKey.MaxHealth,
            StatModValue: 0.75f,
            Bucket: 2u));
        var s = new LocalPlayerState(book);
        s.OnVitalUpdate(
            vitalId: 7u,
            ranks: 100u,
            start: 0u,
            xp: 0u,
            current: 100u);

        Assert.Equal(100u, s.GetMaxApprox(LocalPlayerState.VitalKind.Health));
    }

    [Fact]
    public void GetMaxApprox_PrimaryAttributeBuffsFeedVitalFormula_ExactLiveRegression()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u,
            LayerId: 1u,
            Duration: 60d,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute,
            StatModKey: 2u, // Endurance
            StatModValue: 15f,
            Bucket: 2u));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u,
            LayerId: 2u,
            Duration: 60d,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute,
            StatModKey: 6u, // Self
            StatModValue: 15f,
            Bucket: 2u));
        var s = new LocalPlayerState(book);
        s.OnAttributeUpdate(atType: 2u, ranks: 0u, start: 30u, xp: 0u);
        s.OnAttributeUpdate(atType: 6u, ranks: 0u, start: 10u, xp: 0u);
        s.OnVitalUpdate(vitalId: 7u, ranks: 0u, start: 15u, xp: 0u, current: 38u);
        s.OnVitalUpdate(vitalId: 8u, ranks: 0u, start: 30u, xp: 0u, current: 75u);
        s.OnVitalUpdate(vitalId: 9u, ranks: 0u, start: 0u, xp: 0u, current: 25u);

        Assert.Equal(30u, s.GetBaseMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(60u, s.GetBaseMaxApprox(LocalPlayerState.VitalKind.Stamina));
        Assert.Equal(10u, s.GetBaseMaxApprox(LocalPlayerState.VitalKind.Mana));
        Assert.Equal(38u, s.GetMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(75u, s.GetMaxApprox(LocalPlayerState.VitalKind.Stamina));
        Assert.Equal(25u, s.GetMaxApprox(LocalPlayerState.VitalKind.Mana));
        Assert.Equal(1f, s.HealthPercent);
        Assert.Equal(1f, s.StaminaPercent);
        Assert.Equal(1f, s.ManaPercent);
    }

    [Fact]
    public void GetMaxApprox_HealthRoundsHalfEnduranceAndIncludesGearMaxHealth()
    {
        var s = new LocalPlayerState();
        var properties = new PropertyBundle();
        properties.Ints[(uint)PropertyInt.GearMaxHealth] = 7;
        s.OnProperties(properties);
        s.OnAttributeUpdate(atType: 2u, ranks: 0u, start: 45u, xp: 0u);
        s.OnVitalUpdate(vitalId: 7u, ranks: 0u, start: 10u, xp: 0u, current: 40u);

        Assert.Equal(40u, s.GetBaseMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(40u, s.GetMaxApprox(LocalPlayerState.VitalKind.Health));
    }

    [Fact]
    public void OnVitalCurrent_UpdatesOnlyCurrent_LeavesRanksStartXpAlone()
    {
        var s = new LocalPlayerState();
        s.OnAttributeUpdate(atType: 2u, ranks: 50u, start: 150u, xp: 0u);
        s.OnVitalUpdate(vitalId: 8u, ranks: 0u, start: 0u, xp: 50000u, current: 180u);
        s.OnVitalCurrent(vitalId: 8u, current: 90u);

        var stam = s.Get(LocalPlayerState.VitalKind.Stamina)!.Value;
        Assert.Equal(0u,     stam.Ranks);
        Assert.Equal(0u,     stam.Start);
        Assert.Equal(50000u, stam.Xp);
        Assert.Equal(90u,    stam.Current);
        Assert.Equal(0.45f, s.StaminaPercent!.Value, precision: 3);
    }

    [Fact]
    public void OnVitalCurrent_NoOp_WhenNoFullUpdateYet()
    {
        var s = new LocalPlayerState();
        s.OnVitalCurrent(vitalId: 4u, current: 90u);

        Assert.Null(s.Get(LocalPlayerState.VitalKind.Stamina));
        Assert.Null(s.StaminaPercent);
    }

    [Fact]
    public void Changed_FiresOnFullVitalUpdate_WithCorrectKind()
    {
        var s = new LocalPlayerState();
        var seen = new List<LocalPlayerState.VitalKind>();
        s.Changed += k => seen.Add(k);

        s.OnVitalUpdate(vitalId: 2u, ranks: 1u, start: 1u, xp: 0u, current: 1u);
        s.OnVitalUpdate(vitalId: 4u, ranks: 1u, start: 1u, xp: 0u, current: 1u);
        s.OnVitalUpdate(vitalId: 6u, ranks: 1u, start: 1u, xp: 0u, current: 1u);

        Assert.Equal(new[]
        {
            LocalPlayerState.VitalKind.Health,
            LocalPlayerState.VitalKind.Stamina,
            LocalPlayerState.VitalKind.Mana,
        }, seen);
    }

    [Fact]
    public void AttributeChanged_FiresOnPrimaryAttrUpdate()
    {
        var s = new LocalPlayerState();
        var seen = new List<LocalPlayerState.AttributeKind>();
        s.AttributeChanged += k => seen.Add(k);

        s.OnAttributeUpdate(atType: 2u, ranks: 1u, start: 1u, xp: 0u); // Endurance
        s.OnAttributeUpdate(atType: 6u, ranks: 1u, start: 1u, xp: 0u); // Self

        Assert.Equal(new[]
        {
            LocalPlayerState.AttributeKind.Endurance,
            LocalPlayerState.AttributeKind.Self,
        }, seen);
    }

    [Fact]
    public void OnAttributeUpdate_DoesNotAffectVitals_DirectlyButRefreshesPercent()
    {
        var s = new LocalPlayerState();
        s.OnVitalUpdate(vitalId: 8u, ranks: 0u, start: 0u, xp: 0u, current: 100u);

        // Pre-attribute: percent null because MaxApprox = 0.
        Assert.Null(s.StaminaPercent);

        s.OnAttributeUpdate(atType: 2u, ranks: 50u, start: 150u, xp: 0u);
        // Now MaxApprox = 0 + 200 = 200; percent = 100/200 = 0.5.
        Assert.Equal(0.5f, s.StaminaPercent!.Value, precision: 3);
    }

    [Fact]
    public void OnProperties_ClonesBundleAndExposesInt64()
    {
        var s = new LocalPlayerState();
        var props = new PropertyBundle();
        props.Ints[0x19u] = 126;
        props.Int64s[1u] = 1_234_567_890L;

        s.OnProperties(props);
        props.Ints[0x19u] = 1;
        props.Int64s[1u] = 2L;

        Assert.Equal(126, s.Properties.GetInt(0x19u));
        Assert.Equal(1_234_567_890L, s.Properties.GetInt64(1u));
    }

    [Fact]
    public void OnInt64PropertyUpdate_ReplacesValueAndFiresCharacterChanged()
    {
        var s = new LocalPlayerState();
        var props = new PropertyBundle();
        props.Int64s[1u] = 100L;
        s.OnProperties(props);
        int changed = 0;
        s.CharacterChanged += () => changed++;

        s.OnInt64PropertyUpdate(1u, 1_234_567_890L);

        Assert.Equal(1_234_567_890L, s.Properties.GetInt64(1u));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void OnSkillUpdate_StoresFormulaAdjustedCurrent()
    {
        var s = new LocalPlayerState();
        int changed = 0;
        s.CharacterChanged += () => changed++;

        s.OnSkillUpdate(
            skillId: 24u,
            ranks: 12u,
            status: 2u,
            xp: 3456u,
            init: 30u,
            resistance: 0u,
            lastUsed: 1.5,
            formulaBonus: 80u);

        var run = s.GetSkill(24u);
        Assert.NotNull(run);
        Assert.Equal(122u, run!.Value.CurrentLevel);
        Assert.Equal(2u, run.Value.Status);
        Assert.Equal(3456u, run.Value.Xp);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Clear_ReturnsEveryCharacterSnapshotToPreLoginState()
    {
        var s = new LocalPlayerState();
        var properties = new PropertyBundle();
        properties.Ints[1u] = 42;
        s.OnVitalUpdate(7u, 1u, 2u, 3u, 4u);
        s.OnAttributeUpdate(1u, 5u, 6u, 7u);
        s.OnSkillUpdate(8u, 9u, 2u, 10u, 11u, 12u, 13d, 14u);
        s.OnPositions(new Dictionary<uint, AcDream.Core.Physics.Position>
        {
            [1u] = new AcDream.Core.Physics.Position(
                0xA9B40001u,
                new System.Numerics.Vector3(1f, 2f, 3f),
                System.Numerics.Quaternion.Identity),
        });
        s.OnProperties(properties);
        int characterChanges = 0;
        var vitalChanges = new List<LocalPlayerState.VitalKind>();
        var attributeChanges = new List<LocalPlayerState.AttributeKind>();
        s.CharacterChanged += () => characterChanges++;
        s.Changed += vitalChanges.Add;
        s.AttributeChanged += attributeChanges.Add;

        s.Clear();

        Assert.Null(s.Get(LocalPlayerState.VitalKind.Health));
        Assert.Null(s.GetAttribute(LocalPlayerState.AttributeKind.Strength));
        Assert.Empty(s.Skills);
        Assert.Empty(s.Positions);
        Assert.Empty(s.Properties.Ints);
        Assert.Equal(Enum.GetValues<LocalPlayerState.VitalKind>(), vitalChanges);
        Assert.Equal(Enum.GetValues<LocalPlayerState.AttributeKind>(), attributeChanges);
        Assert.Equal(1, characterChanges);

        s.Clear();
        Assert.Null(s.Get(LocalPlayerState.VitalKind.Health));
        Assert.Empty(s.Skills);
        Assert.Empty(s.Properties.Ints);
    }


    [Fact]
    public void GetEffectiveAttribute_NoSpellbook_ReturnsBaseValue()
    {
        var s = new LocalPlayerState();
        s.OnAttributeUpdate(atType: 1u, ranks: 100u, start: 100u, xp: 0u);   // Strength, base 200

        Assert.Equal(200, s.GetEffectiveAttribute(LocalPlayerState.AttributeKind.Strength));
    }

    [Fact]
    public void GetEffectiveAttribute_Unseen_ReturnsNull()
    {
        var s = new LocalPlayerState(new Spellbook());
        Assert.Null(s.GetEffectiveAttribute(LocalPlayerState.AttributeKind.Strength));
    }

    [Fact]
    public void GetEffectiveAttribute_VitaeActive_AttributesAreVitaeImmune()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: -1d, CasterGuid: 0u,
            StatModType: 0u, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));
        var s = new LocalPlayerState(book);
        s.OnAttributeUpdate(atType: 1u, ranks: 100u, start: 100u, xp: 0u);   // Strength, base 200

        Assert.Equal(200, s.GetEffectiveAttribute(LocalPlayerState.AttributeKind.Strength));
    }

    [Fact]
    public void GetEffectiveAttribute_Buff_AppliesMultiplierAndTruncates()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u, LayerId: 1u, Duration: 60d, CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute,
            StatModKey: 1u /* Strength */, StatModValue: 1.1f, Bucket: 1u));
        var s = new LocalPlayerState(book);
        s.OnAttributeUpdate(atType: 1u, ranks: 100u, start: 100u, xp: 0u);   // Strength, base 200

        // 200 * 1.1 = 220.
        Assert.Equal(220, s.GetEffectiveAttribute(LocalPlayerState.AttributeKind.Strength));
    }

    [Fact]
    public void GetEffectiveSkill_NoSpellbook_ReturnsBaseValue()
    {
        var s = new LocalPlayerState();
        s.OnSkillUpdate(skillId: 6u, ranks: 300u, status: 2u, xp: 0u,
            init: 3u, resistance: 0u, lastUsed: 0d, formulaBonus: 0u);   // base 303

        Assert.Equal(303, s.GetEffectiveSkill(6u));
        Assert.Equal(0, s.GetSkillVitaeModifier(6u));
    }

    [Fact]
    public void GetEffectiveSkill_Unseen_ReturnsNull()
    {
        var s = new LocalPlayerState(new Spellbook());
        Assert.Null(s.GetEffectiveSkill(6u));
    }

    [Fact]
    public void GetEffectiveSkill_ThirtyThreePercentVitae_MatchesUserReportedGolden()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: -1d, CasterGuid: 0u,
            StatModType: 0u, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));
        var s = new LocalPlayerState(book);
        s.OnSkillUpdate(skillId: 6u, ranks: 300u, status: 2u, xp: 0u,
            init: 3u, resistance: 0u, lastUsed: 0d, formulaBonus: 0u);   // base 303

        Assert.Equal(203, s.GetEffectiveSkill(6u));
        Assert.Equal(-100, s.GetSkillVitaeModifier(6u));
    }

    [Fact]
    public void GetEffectiveSkill_BuffPlusVitaeComposition()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: -1d, CasterGuid: 0u,
            StatModType: 0u, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));   // 33% vitae
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u, LayerId: 2u, Duration: 60d, CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Skill,
            StatModKey: 6u, StatModValue: 50f, Bucket: 2u));   // +50 additive buff
        var s = new LocalPlayerState(book);
        s.OnSkillUpdate(skillId: 6u, ranks: 297u, status: 2u, xp: 0u,
            init: 3u, resistance: 0u, lastUsed: 0d, formulaBonus: 0u);   // base 300

        Assert.Equal(251, s.GetEffectiveSkill(6u));
        Assert.Equal(-99, s.GetSkillVitaeModifier(6u));
    }

    private static SpellMetadata TestSpell(uint spellId) => new(
        spellId, "Test", "War Magic", 0u, 0u, "", 0f, 0,
        false, false, "", 0, 0, 0u, 0, false, false, true,
        0f, 0u, 0u, 0u, 0);


    [Fact]
    public void AttributeUpdateFansOutToDerivedVitalObserversAndCharacterSheet()
    {
        var s = new LocalPlayerState();
        var vitalEvents = new List<LocalPlayerState.VitalKind>();
        int attributeEvents = 0, characterEvents = 0;
        s.Changed += k => vitalEvents.Add(k);
        s.AttributeChanged += _ => attributeEvents++;
        s.CharacterChanged += () => characterEvents++;

        s.OnAttributeUpdate(atType: 2u /* Endurance */, ranks: 10u, start: 100u, xp: 500u);
        Assert.Equal(
            [LocalPlayerState.VitalKind.Health, LocalPlayerState.VitalKind.Stamina],
            vitalEvents);
        Assert.Equal(1, attributeEvents);
        Assert.Equal(1, characterEvents);

        vitalEvents.Clear();
        s.OnAttributeUpdate(atType: 6u /* Self */, ranks: 5u, start: 100u, xp: 250u);
        Assert.Equal([LocalPlayerState.VitalKind.Mana], vitalEvents);

        vitalEvents.Clear();
        s.OnAttributeUpdate(atType: 3u /* Quickness */, ranks: 1u, start: 100u, xp: 10u);
        Assert.Empty(vitalEvents); // no vital derives from Quickness
        Assert.Equal(3, attributeEvents);
    }

    [Fact]
    public void SkillWireUpdatePreservesTheLoginFormulaBonus()
    {
        var s = new LocalPlayerState();
        s.OnSkillUpdate(skillId: 6u, ranks: 10u, status: 2u, xp: 100u,
            init: 0u, resistance: 0u, lastUsed: 0d, formulaBonus: 120u);

        s.OnSkillWireUpdate(skillId: 6u, ranks: 11u, status: 2u, xp: 2000u,
            init: 0u, resistance: 0u, lastUsed: 5.0d);

        var snap = s.Skills[6u];
        Assert.Equal(11u, snap.Ranks);
        Assert.Equal(2000u, snap.Xp);
        Assert.Equal(120u, snap.FormulaBonus);
        Assert.Equal(131u, snap.BaseLevel); // 120 formula + 0 init + 11 ranks
    }

    [Fact]
    public void FreshlyTrainedSkillDerivesItsFormulaBonusThroughTheResolver()
    {
        var s = new LocalPlayerState
        {
            SkillFormulaBonusResolver = (skillId, attrs) =>
                skillId == 33u && attrs.TryGetValue(4u, out uint coordination)
                    ? coordination / 4u
                    : 0u,
        };
        s.OnAttributeUpdate(atType: 4u, ranks: 0u, start: 80u, xp: 0u);

        s.OnSkillWireUpdate(skillId: 33u, ranks: 0u, status: 2u, xp: 0u,
            init: 0u, resistance: 0u, lastUsed: 0d);

        Assert.Equal(20u, s.Skills[33u].FormulaBonus); // 80 / 4
    }
}
