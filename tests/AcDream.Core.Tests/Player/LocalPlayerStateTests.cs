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
            StatModType: AttributeType | MultiplicativeBit,
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
    public void GetMaxApprox_SecondaryAttributeModifierRoundsToNearest()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u,
            LayerId: 1u,
            Duration: 60d,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.SecondAtt | AdditiveBit,
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

        Assert.Equal(101u, s.GetMaxApprox(LocalPlayerState.VitalKind.Health));
    }

    [Theory]
    [InlineData(1u, LocalPlayerState.VitalKind.Health)]
    [InlineData(3u, LocalPlayerState.VitalKind.Stamina)]
    [InlineData(5u, LocalPlayerState.VitalKind.Mana)]
    public void VitalMaximumAndVitaePenaltyRoundBeforePresentation(uint key, LocalPlayerState.VitalKind kind)
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            1u, 1u, -1d, 0u, StatModType: MultiplicativeBit, StatModKey: 0u, StatModValue: 0.97f, Bucket: 4u));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            2u, 2u, 60d, 0u, StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.SecondAtt | AdditiveBit,
            StatModKey: key, StatModValue: 23f, Bucket: 2u));
        var state = new LocalPlayerState(book);
        state.OnVitalUpdate(key, 180u, 0u, 0u, 198u);

        Assert.Equal(198u, state.GetMaxApprox(kind));
        Assert.Equal(-5, state.GetVitalVitaeModifier(kind));
        Assert.Equal(180u, state.GetBaseMaxApprox(kind));
    }

    [Theory]
    [InlineData(1u, 0.1f, 1u, -1)]
    [InlineData(5u, 0.6f, 5u, -2)]
    [InlineData(100u, 0.975f, 98u, -2)]
    [InlineData(100u, 0.974f, 97u, -3)]
    [InlineData(100u, 1f, 100u, 0)]
    public void VitalRoundingPreservesMinimumAndHalfUpBoundary(uint baseValue, float vitae, uint maximum, int delta)
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            1u, 1u, -1d, 0u, StatModType: MultiplicativeBit, StatModKey: 0u, StatModValue: vitae, Bucket: 4u));
        var state = new LocalPlayerState(book);
        state.OnVitalUpdate(1u, baseValue, 0u, 0u, 0u);
        Assert.Equal(maximum, state.GetMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(delta, state.GetVitalVitaeModifier(LocalPlayerState.VitalKind.Health));
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
            StatModType: AttributeType | AdditiveBit,
            StatModKey: 2u, // Endurance
            StatModValue: 15f,
            Bucket: 2u));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u,
            LayerId: 2u,
            Duration: 60d,
            CasterGuid: 0u,
            StatModType: AttributeType | AdditiveBit,
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
    public void GetMaxApprox_HealthAddsTwoPerEnlightenmentBeforeVitae()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            1u, 1u, -1d, 0u, StatModType: 0u, StatModKey: 0u, StatModValue: 0.95f, Bucket: 4u));
        var s = new LocalPlayerState(book);
        var properties = new PropertyBundle();
        properties.Ints[(uint)PropertyInt.Enlightenment] = 4;
        s.OnProperties(properties);
        s.OnVitalUpdate(vitalId: 1u, ranks: 100u, start: 0u, xp: 0u, current: 103u);
        s.OnVitalUpdate(vitalId: 3u, ranks: 100u, start: 0u, xp: 0u, current: 95u);

        // (100 + 4 x 2) x 0.95 = 102.6, shown as 103; stamina has no such term.
        Assert.Equal(108u, s.GetBaseMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(103u, s.GetMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(95u, s.GetMaxApprox(LocalPlayerState.VitalKind.Stamina));
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
            StatModType: MultiplicativeBit, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));
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
            StatModType: AttributeType | MultiplicativeBit,
            StatModKey: 1u /* Strength */, StatModValue: 1.1f, Bucket: 1u));
        var s = new LocalPlayerState(book);
        s.OnAttributeUpdate(atType: 1u, ranks: 100u, start: 100u, xp: 0u);   // Strength, base 200

        // 200 * 1.1 = 220.
        Assert.Equal(220, s.GetEffectiveAttribute(LocalPlayerState.AttributeKind.Strength));
    }

    [Fact]
    public void GetEffectiveAttribute_AllAttributeEnchantment_StacksWithANormalBuffAndFeedsHealthAndSkills()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: 60d, CasterGuid: 0u,
            StatModType: AttributeType | AdditiveBit,
            StatModKey: 1u /* Strength */, StatModValue: 40f, Bucket: 2u, SpellCategory: 1u));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u, LayerId: 2u, Duration: 60d, CasterGuid: 0u,
            StatModType: (uint)(EnchantmentMath.EnchantmentTypeFlag.Attribute
                | EnchantmentMath.EnchantmentTypeFlag.MultipleStat) | AdditiveBit,
            StatModKey: 0u, StatModValue: 9f, Bucket: 2u, SpellCategory: 2u));   // Society Knight's Blessing
        var s = new LocalPlayerState(book);
        s.SkillFormulaBonusResolver = (skillId, status, attrs) => attrs[2u];   // Endurance, unscaled
        s.OnAttributeUpdate(atType: 1u, ranks: 0u, start: 300u, xp: 0u);   // Strength base 300
        s.OnAttributeUpdate(atType: 2u, ranks: 0u, start: 200u, xp: 0u);   // Endurance base 200
        s.OnVitalUpdate(vitalId: 7u, ranks: 0u, start: 0u, xp: 0u, current: 0u);
        s.OnSkillUpdate(skillId: 24u, ranks: 0u, status: 2u, xp: 0u,
            init: 0u, resistance: 0u, lastUsed: 0d, formulaBonus: 200u);

        Assert.Equal(349, s.GetEffectiveAttribute(LocalPlayerState.AttributeKind.Strength));
        Assert.Equal(100u, s.GetBaseMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(105u, s.GetMaxApprox(LocalPlayerState.VitalKind.Health));
        Assert.Equal(9, s.AttributeEnchantmentSkillDelta(24u));
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
            StatModType: MultiplicativeBit, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));
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
            StatModType: MultiplicativeBit, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));   // 33% vitae
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u, LayerId: 2u, Duration: 60d, CasterGuid: 0u,
            StatModType: SkillType | AdditiveBit,
            StatModKey: 6u, StatModValue: 50f, Bucket: 2u));   // +50 additive buff
        var s = new LocalPlayerState(book);
        s.OnSkillUpdate(skillId: 6u, ranks: 297u, status: 2u, xp: 0u,
            init: 3u, resistance: 0u, lastUsed: 0d, formulaBonus: 0u);   // base 300

        Assert.Equal(251, s.GetEffectiveSkill(6u));
        Assert.Equal(-99, s.GetSkillVitaeModifier(6u));
    }

    [Fact]
    public void GetSkillValue_AttributeBuffsRaiseTheFormulaTerm_ColdeveCreatureEnchantmentPin()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u), TestSpell(3u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: 60d, CasterGuid: 0u,
            StatModType: AttributeType | AdditiveBit,
            StatModKey: 5u, StatModValue: 45f, Bucket: 2u));   // Focus VII
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u, LayerId: 2u, Duration: 60d, CasterGuid: 0u,
            StatModType: AttributeType | AdditiveBit,
            StatModKey: 6u, StatModValue: 45f, Bucket: 2u));   // Self VII
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 3u, LayerId: 3u, Duration: 60d, CasterGuid: 0u,
            StatModType: SkillType | AdditiveBit,
            StatModKey: 0x1Fu, StatModValue: 50f, Bucket: 2u));
        var s = new LocalPlayerState(book);
        s.SkillFormulaBonusResolver = (skillId, status, attrs) => skillId == 0x1Fu
            ? (uint)Math.Floor((attrs[5u] + attrs[6u]) / 4d + 0.5d)
            : 0u;
        s.OnAttributeUpdate(atType: 5u, ranks: 0u, start: 251u, xp: 0u);
        s.OnAttributeUpdate(atType: 6u, ranks: 0u, start: 251u, xp: 0u);
        s.OnSkillUpdate(skillId: 0x1Fu, ranks: 200u, status: 2u, xp: 0u,
            init: 15u, resistance: 0u, lastUsed: 0d, formulaBonus: 126u);

        PlayerSkillMath.Value value = s.GetSkillValue(0x1Fu)!.Value;
        Assert.Equal(22, s.AttributeEnchantmentSkillDelta(0x1Fu));
        Assert.Equal(341, value.UnenchantedLevel);
        Assert.Equal(413, value.EffectiveLevel);
        Assert.Equal(72, value.EffectiveLevel - value.UnenchantedLevel);
        Assert.Equal(413, s.GetEffectiveSkill(0x1Fu));
    }

    [Fact]
    public void AttributeEnchantmentSkillDelta_IsZeroWithoutAResolverOrWithoutAttributeBuffs()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u)]));
        var s = new LocalPlayerState(book);
        s.OnAttributeUpdate(atType: 3u, ranks: 0u, start: 100u, xp: 0u);
        s.OnSkillUpdate(skillId: 24u, ranks: 50u, status: 2u, xp: 0u,
            init: 0u, resistance: 0u, lastUsed: 0d, formulaBonus: 100u);

        Assert.Equal(0, s.AttributeEnchantmentSkillDelta(24u));

        s.SkillFormulaBonusResolver = (skillId, status, attrs) => attrs[3u];
        Assert.Equal(0, s.AttributeEnchantmentSkillDelta(24u));
        Assert.Equal(150, s.GetEffectiveSkill(24u));

        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: 60d, CasterGuid: 0u,
            StatModType: AttributeType | AdditiveBit,
            StatModKey: 3u, StatModValue: 20f, Bucket: 2u));   // Quickness +20
        Assert.Equal(20, s.AttributeEnchantmentSkillDelta(24u));
        Assert.Equal(170, s.GetEffectiveSkill(24u));
        Assert.Equal(150, s.GetSkillValue(24u)!.Value.UnenchantedLevel);
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
            SkillFormulaBonusResolver = (skillId, status, attrs) =>
                skillId == 33u && attrs.TryGetValue(4u, out uint coordination)
                    ? coordination / 4u
                    : 0u,
        };
        s.OnAttributeUpdate(atType: 4u, ranks: 0u, start: 80u, xp: 0u);

        s.OnSkillWireUpdate(skillId: 33u, ranks: 0u, status: 2u, xp: 0u,
            init: 0u, resistance: 0u, lastUsed: 0d);

        Assert.Equal(20u, s.Skills[33u].FormulaBonus); // 80 / 4
    }

    // Stacking for the attribute, vital and skill panels: the records that
    // apply to the stat are culled from the multiplicative list, then the
    // additive list, each walked from its head (the newest record), into one
    // list holding one record per spell category.

    private const uint Strength = 1u;
    private const uint MeleeDefense = 6u;
    private const uint MultiplicativeBit = 0x4000u;
    private const uint AdditiveBit = 0x8000u;
    private const uint AttributeType = (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute;
    private const uint SkillType = (uint)EnchantmentMath.EnchantmentTypeFlag.Skill;

    [Fact]
    public void Stacking_ASpellMissingFromTheSpellTableCounts()
    {
        ActiveEnchantmentRecord record = Typed(9999u, 1u, AttributeType, Strength, 40f, bucket: 2u);
        Assert.Equal(240, StrengthIn(BookWithTable(SpellTable.Empty, record)));
    }

    [Fact]
    public void Stacking_RecordsGroupByTheirOwnSpellCategory()
    {
        // Both records name category 7; the table's family (0 here, which
        // would keep them apart) plays no part. The stronger one counts alone.
        ActiveEnchantmentRecord weak = Ranked(
            Typed(1u, 1u, AttributeType, Strength, 40f, bucket: 2u), category: 7u, power: 400u);
        ActiveEnchantmentRecord strong = Ranked(
            Typed(2u, 2u, AttributeType, Strength, 56f, bucket: 2u), category: 7u, power: 16300u);
        static SpellTable Table() => SpellTable.Create([TestSpell(1u), TestSpell(2u)]);

        Assert.Equal(256, StrengthIn(BookWithTable(Table(), weak, strong)));
        Assert.Equal(256, StrengthIn(BookWithTable(Table(), strong, weak)));
    }

    [Fact]
    public void Stacking_SpellCategoryZeroIsACategoryLikeAnyOther()
    {
        ActiveEnchantmentRecord first = Ranked(
            Typed(1u, 1u, AttributeType, Strength, 40f, bucket: 2u), category: 0u, power: 1u);
        ActiveEnchantmentRecord second = Ranked(
            Typed(2u, 2u, AttributeType, Strength, 30f, bucket: 2u), category: 0u, power: 2u);

        Assert.Equal(230, StrengthWith(first, second));
    }

    [Fact]
    public void Stacking_AHigherPowerLevelBeatsAHigherSpellId()
    {
        // A +56 timed buff of high power against a +45 permanent one of a
        // higher spell id, same category.
        ActiveEnchantmentRecord timed = Ranked(
            Typed(2245u, 2u, SkillType, MeleeDefense, 56f, bucket: 2u), category: 37u, power: 16300u);
        ActiveEnchantmentRecord permanent = Ranked(
            Typed(4560u, 1u, SkillType, MeleeDefense, 45f, bucket: 2u), category: 37u, power: 400u);

        Assert.Equal(630, SkillWith(MeleeDefense, 574u, timed, permanent));
        Assert.Equal(630, SkillWith(MeleeDefense, 574u, permanent, timed));
    }

    [Fact]
    public void Stacking_AtEqualPowerTheLaterStartWins()
    {
        ActiveEnchantmentRecord earlier = Ranked(
            Typed(4560u, 1u, SkillType, MeleeDefense, 45f, bucket: 2u), category: 37u, power: 400u, start: 10d);
        ActiveEnchantmentRecord later = Ranked(
            Typed(2245u, 2u, SkillType, MeleeDefense, 56f, bucket: 2u), category: 37u, power: 400u, start: 20d);

        Assert.Equal(630, SkillWith(MeleeDefense, 574u, earlier, later));
        Assert.Equal(630, SkillWith(MeleeDefense, 574u, later, earlier));
    }

    [Fact]
    public void Stacking_AnExactTieGoesToTheOlderRecord()
    {
        // A new record goes to the head of its list and the head is culled
        // first, so the record that arrived first is the later challenger and
        // takes an exact tie. The spell id plays no part.
        ActiveEnchantmentRecord older = Ranked(
            Typed(1u, 1u, AttributeType, Strength, 40f, bucket: 2u), category: 7u, power: 400u, start: 5d);
        ActiveEnchantmentRecord newer = Ranked(
            Typed(2u, 2u, AttributeType, Strength, 56f, bucket: 2u), category: 7u, power: 400u, start: 5d);

        Assert.Equal(240, StrengthWith(older, newer));
        Assert.Equal(256, StrengthWith(newer, older));
    }

    [Fact]
    public void Stacking_AMultiplierAndAnAdditionOfOneCategoryCompete()
    {
        ActiveEnchantmentRecord scale = Ranked(
            Typed(1u, 1u, AttributeType, Strength, 1.1f, bucket: 1u), category: 5u, power: 10u);
        ActiveEnchantmentRecord add = Ranked(
            Typed(2u, 2u, AttributeType, Strength, 10f, bucket: 2u), category: 5u, power: 5u);

        Assert.Equal(220, StrengthWith(scale, add));
        Assert.Equal(210, StrengthWith(scale, add with { PowerLevel = 20u }));
    }

    [Fact]
    public void Stacking_AMultipleStatRecordAppliesWhateverItsKey()
    {
        ActiveEnchantmentRecord blessing = Typed(
            1u, 1u,
            (uint)(EnchantmentMath.EnchantmentTypeFlag.Attribute
                | EnchantmentMath.EnchantmentTypeFlag.MultipleStat),
            key: 3u, 9f, bucket: 2u);

        Assert.Equal(209, StrengthWith(blessing));
    }

    [Fact]
    public void Stacking_AnAllAttackSkillsRecordRaisesTheAttackSkillsOnly()
    {
        ActiveEnchantmentRecord attack = Typed(1u, 1u, SkillType | 0x10000u, 0u, 10f, bucket: 2u);
        foreach (uint skill in new uint[] { 33, 34, 41, 43, 44, 45, 46, 47, 49 })
            Assert.True(SkillWith(skill, 100u, attack) == 110, $"skill {skill}");
        foreach (uint skill in new uint[] { 1, 6, 24, 35, 36, 42, 48, 50 })
            Assert.True(SkillWith(skill, 100u, attack) == 100, $"skill {skill}");
    }

    [Fact]
    public void Stacking_AnAllDefenseSkillsRecordRaisesTheDefenseSkillsOnly()
    {
        ActiveEnchantmentRecord defense = Typed(1u, 1u, SkillType | 0x20000u, 0u, 10f, bucket: 2u);
        foreach (uint skill in new uint[] { 6, 7, 15, 48 })
            Assert.True(SkillWith(skill, 100u, defense) == 110, $"skill {skill}");
        foreach (uint skill in new uint[] { 5, 8, 14, 16, 24, 45, 47, 49 })
            Assert.True(SkillWith(skill, 100u, defense) == 100, $"skill {skill}");
    }

    [Fact]
    public void Stacking_TheTypeBitsDecideAddOrMultiplyWithAddFirst()
    {
        // A record carrying both operation bits adds, whichever list holds it.
        ActiveEnchantmentRecord both = Typed(1u, 1u, AttributeType, Strength, 10f, bucket: 1u)
            with { StatModType = AttributeType | MultiplicativeBit | AdditiveBit };
        Assert.Equal(210, StrengthWith(both));

        // One with neither bit changes nothing.
        ActiveEnchantmentRecord neither = Typed(2u, 2u, AttributeType, Strength, 10f, bucket: 2u)
            with { StatModType = AttributeType };
        Assert.Equal(200, StrengthWith(neither));
    }

    [Fact]
    public void Stacking_VitaeThenEachRecordRoundsToAFloatInTurn()
    {
        // 200 x 0.95 = 190, x 1.1 = 209 exactly in single precision; scaling
        // by the product 0.95 x 1.1 first gives 208.99998, shown as 208.
        ActiveEnchantmentRecord vitae = new(
            1u, 1u, -1d, 0u, StatModType: MultiplicativeBit, StatModKey: 0u, StatModValue: 0.95f, Bucket: 4u);
        Assert.Equal(209, SkillWith(
            MeleeDefense, 200u, vitae, Typed(2u, 2u, SkillType, MeleeDefense, 1.1f, bucket: 1u)));
    }

    /// <summary>
    /// A record as the server sends one: its operation bit (multiply or add)
    /// matches its list, and its spell category is its own (the spell id).
    /// </summary>
    private static ActiveEnchantmentRecord Typed(
        uint spellId, uint layer, uint statModType, uint key, float value, uint bucket)
    {
        uint operation = bucket switch
        {
            1u => MultiplicativeBit,
            2u => AdditiveBit,
            _ => 0u,
        };
        return new ActiveEnchantmentRecord(
            spellId, layer, 60d, 0u,
            StatModType: statModType | operation,
            StatModKey: key,
            StatModValue: value,
            Bucket: bucket,
            SpellCategory: spellId);
    }

    private static ActiveEnchantmentRecord Ranked(
        ActiveEnchantmentRecord record, uint category, uint power, double start = 0d) =>
        record with { SpellCategory = category, PowerLevel = power, StartTime = start };

    /// <summary>
    /// A spellbook holding <paramref name="records"/> (each arriving in
    /// turn), whose spell table knows every spell, with the record's own
    /// category as its family.
    /// </summary>
    private static Spellbook BookHolding(params ActiveEnchantmentRecord[] records) =>
        BookWithTable(
            SpellTable.Create(records
                .Select(record => TestSpell(record.SpellId) with { Family = record.SpellCategory })
                .ToArray()),
            records);

    private static Spellbook BookWithTable(SpellTable table, params ActiveEnchantmentRecord[] records)
    {
        var book = new Spellbook(table);
        foreach (ActiveEnchantmentRecord record in records)
            book.OnEnchantmentAdded(record);
        return book;
    }

    private static int? StrengthIn(Spellbook book)
    {
        var s = new LocalPlayerState(book);
        s.OnAttributeUpdate(atType: Strength, ranks: 100u, start: 100u, xp: 0u);
        return s.GetEffectiveAttribute(LocalPlayerState.AttributeKind.Strength);
    }

    private static int? StrengthWith(params ActiveEnchantmentRecord[] records) =>
        StrengthIn(BookHolding(records));

    private static int? SkillWith(uint skillId, uint baseLevel, params ActiveEnchantmentRecord[] records)
    {
        var s = new LocalPlayerState(BookHolding(records));
        s.OnSkillUpdate(skillId, ranks: baseLevel, status: 2u, xp: 0u,
            init: 0u, resistance: 0u, lastUsed: 0d, formulaBonus: 0u);
        return s.GetEffectiveSkill(skillId);
    }
}
