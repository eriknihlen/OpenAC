using System;
using System.Linq;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Player;
using AcDream.Core.Properties;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterSheetProviderTests
{
    private const uint PlayerGuid = 0x50000001u;

    private static DatReaderWriter.DBObjs.ExperienceTable MakeXpTable() => new()
    {
        Levels = new ulong[] { 0, 100, 250, 450 },
        Attributes = new uint[] { 0, 10, 30, 60, 100 },
        Vitals = new uint[] { 0, 4, 12, 24 },
        TrainedSkills = new uint[] { 0, 5, 15, 30 },
        SpecializedSkills = new uint[] { 0, 8, 24, 48 },
    };

    private sealed class Harness
    {
        public ClientObjectTable Table { get; } = new();
        public LocalPlayerState Player { get; } = new();
        public CharacterSheetProvider Provider { get; }
        public (uint statId, ulong cost)? SentAttribute;
        public (uint skillId, uint credits)? SentTrain;
        public bool CanSend = true;

        public Harness()
        {
            Provider = new CharacterSheetProvider(
                Table, Player,
                playerGuid: () => PlayerGuid,
                activeToonName: () => "default",
                fallbackSheet: name => new CharacterSheet { Name = name, Level = -1 },
                canSendRaise: () => CanSend,
                sendRaiseAttribute: (statId, cost) => SentAttribute = (statId, cost),
                sendRaiseVital: (_, _) => { },
                sendRaiseSkill: (_, _) => { },
                sendTrainSkill: (skillId, credits) => SentTrain = (skillId, credits))
            {
                ExperienceTable = MakeXpTable(),
            };
        }

        public ClientObject AddPlayerObject(long unassignedXp = 1000L)
        {
            var player = new ClientObject { ObjectId = PlayerGuid, Name = "Testy" };
            player.Properties.Ints[0x19u] = 1;        // level
            player.Properties.Int64s[1u] = 150L;      // total XP — mid 100..250 band
            player.Properties.Int64s[2u] = unassignedXp;
            player.Properties.Ints[0x18u] = 4;
            Table.AddOrUpdate(player);
            return player;
        }
    }

    [Fact]
    public void BuildSheet_NoLiveData_UsesFallbackSheet()
    {
        var h = new Harness();

        var sheet = h.Provider.BuildSheet();

        Assert.Equal(-1, sheet.Level);          // fallback marker
        Assert.Equal("Player", sheet.Name);     // toon key "default" + no object → "Player"
    }

    [Fact]
    public void BuildSheet_LiveData_ComputesLevelBandAndRaiseCosts()
    {
        var h = new Harness();
        h.AddPlayerObject(unassignedXp: 777L);
        h.Player.OnAttributeUpdate(atType: 1u, ranks: 1u, start: 10u, xp: 10u); // Strength

        var sheet = h.Provider.BuildSheet();

        Assert.Equal("Testy", sheet.Name);      // live object name beats "Player"
        Assert.Equal(1, sheet.Level);
        Assert.Equal(150L, sheet.TotalXp);
        Assert.Equal(777L, sheet.UnassignedXp);
        // Level band 100..250, at 150: 100 XP to next, 1/3 through the band.
        Assert.Equal(100L, sheet.XpToNextLevel);
        Assert.Equal(1f / 3f, sheet.XpFraction, precision: 4);
        Assert.Equal(11, sheet.Strength);       // ranks + start
        Assert.Equal(4, sheet.SkillCredits);
        Assert.Equal(20L, sheet.AttributeRaiseCosts[0]);
        Assert.Equal(90L, sheet.AttributeRaise10Costs[0]);
    }


    [Fact]
    public void BuildSheet_WithAllegianceRank_PrefixesNameWithRankTitle()
    {
        var h = new Harness();
        var player = h.AddPlayerObject();
        player.Properties.Ints[0x1Eu] = 3;  // AllegianceRank
        player.Properties.Ints[0xBCu] = 1;  // HeritageGroup: Aluvian
        player.Properties.Ints[0x71u] = 2;  // Gender: Female

        var sheet = h.Provider.BuildSheet();

        Assert.Equal("Baroness Testy", sheet.Name);
    }

    [Fact]
    public void BuildSheet_WithoutAllegianceRank_NameStaysPlain()
    {
        var h = new Harness();
        h.AddPlayerObject();

        var sheet = h.Provider.BuildSheet();

        Assert.Equal("Testy", sheet.Name);
    }

    [Fact]
    public void BuildSheet_AfterLiveInt64Updates_RefreshesBothXpWindowsAndMeter()
    {
        var h = new Harness();
        h.AddPlayerObject(unassignedXp: 0L);

        Assert.True(h.Table.UpdateInt64Property(PlayerGuid, 1u, 200L));
        Assert.True(h.Table.UpdateInt64Property(PlayerGuid, 2u, 75L));

        var sheet = h.Provider.BuildSheet();

        Assert.Equal(200L, sheet.TotalXp);
        Assert.Equal(75L, sheet.UnassignedXp);
        Assert.Equal(50L, sheet.XpToNextLevel);
        Assert.Equal(2f / 3f, sheet.XpFraction, precision: 4);
    }

    [Fact]
    public void BuildSheet_Skills_MapsAdvancementAndCurveCosts()
    {
        var h = new Harness();
        h.AddPlayerObject();
        h.Player.OnSkillUpdate(skillId: 6u, ranks: 1u, status: 2u, xp: 5u,
            init: 0u, resistance: 0u, lastUsed: 0, formulaBonus: 0u);   // trained
        h.Player.OnSkillUpdate(skillId: 7u, ranks: 0u, status: 0u, xp: 0u,
            init: 0u, resistance: 0u, lastUsed: 0, formulaBonus: 0u);   // inactive → excluded

        var sheet = h.Provider.BuildSheet();

        var skill = Assert.Single(sheet.Skills);
        Assert.Equal(6u, skill.Id);
        Assert.Equal("Skill 6", skill.Name);    // no SkillTable → id fallback name
        Assert.Equal(CharacterSkillAdvancementClass.Trained, skill.AdvancementClass);
        Assert.Equal(10L, skill.RaiseCost);
        Assert.Equal(25L, skill.Raise10Cost);
    }

    [Fact]
    public void HandleRaiseRequest_Attribute_SendsWithoutMutation_AndLatchesOneInFlight()
    {
        var h = new Harness();
        h.AddPlayerObject(unassignedXp: 1000L);
        h.Player.OnAttributeUpdate(atType: 1u, ranks: 1u, start: 10u, xp: 10u);
        int tableUpdates = 0;
        h.Table.ObjectUpdated += _ => tableUpdates++;

        h.Provider.HandleRaiseRequest(new CharacterStatController.RaiseRequest(
            CharacterStatController.RaiseTargetKind.Attribute, StatId: 1u, Cost: 20L, Amount: 1));

        Assert.Equal((1u, 20ul), h.SentAttribute);
        var strength = h.Player.GetAttribute(LocalPlayerState.AttributeKind.Strength);
        Assert.Equal(1u, strength!.Value.Ranks);                        // unchanged
        Assert.Equal(1000L, h.Table.Get(PlayerGuid)!.Properties.GetInt64(2u)); // undebited
        Assert.Equal(0, tableUpdates);
        Assert.True(h.Provider.BuildSheet().AwaitingRaise);

        h.SentAttribute = null;
        h.Provider.HandleRaiseRequest(new CharacterStatController.RaiseRequest(
            CharacterStatController.RaiseTargetKind.Attribute, StatId: 1u, Cost: 20L, Amount: 1));
        Assert.Null(h.SentAttribute);
    }

    [Fact]
    public void HandleRaiseRequest_Blocked_WhenCanSendIsFalse()
    {
        var h = new Harness();
        h.AddPlayerObject(unassignedXp: 1000L);
        h.Player.OnAttributeUpdate(atType: 1u, ranks: 1u, start: 10u, xp: 10u);
        h.CanSend = false;

        h.Provider.HandleRaiseRequest(new CharacterStatController.RaiseRequest(
            CharacterStatController.RaiseTargetKind.Attribute, StatId: 1u, Cost: 20L, Amount: 1));

        Assert.Null(h.SentAttribute);
        Assert.Equal(1u, h.Player.GetAttribute(LocalPlayerState.AttributeKind.Strength)!.Value.Ranks);
        Assert.Equal(1000L, h.Table.Get(PlayerGuid)!.Properties.GetInt64(2u));
    }

    [Fact]
    public void HandleRaiseRequest_TrainSkill_SendsExactDatCostWithoutMutation()
    {
        var h = new Harness();
        var player = h.AddPlayerObject();
        player.Properties.Ints[0x18u] = 4;
        h.Player.OnSkillUpdate(skillId: 6u, ranks: 0u, status: 1u, xp: 0u,
            init: 0u, resistance: 0u, lastUsed: 0, formulaBonus: 0u);   // untrained

        h.Provider.HandleRaiseRequest(new CharacterStatController.RaiseRequest(
            CharacterStatController.RaiseTargetKind.TrainSkill, StatId: 6u, Cost: 4L, Amount: 1));

        Assert.Equal((6u, 4u), h.SentTrain);
        Assert.Equal(1u, h.Player.GetSkill(6u)!.Value.Status);          // still untrained
        Assert.Equal(4, player.Properties.GetInt(0x18u));
        Assert.True(h.Provider.BuildSheet().AwaitingRaise);
    }

    [Fact]
    public void AwaitingRaise_ReleasesOnTheAuthoritativeRecord_AndOnPanelUnmount()
    {
        var h = new Harness();
        h.AddPlayerObject(unassignedXp: 1000L);
        h.Player.OnAttributeUpdate(atType: 1u, ranks: 1u, start: 10u, xp: 10u);
        int rebuilds = 0;
        using (h.Provider.SubscribeChanged(() => rebuilds++))
        {
            h.Provider.HandleRaiseRequest(new CharacterStatController.RaiseRequest(
                CharacterStatController.RaiseTargetKind.Attribute, StatId: 1u, Cost: 20L, Amount: 1));
            Assert.True(h.Provider.BuildSheet().AwaitingRaise);

            // The authoritative attribute record releases + refreshes.
            h.Player.OnAttributeUpdate(atType: 1u, ranks: 2u, start: 10u, xp: 30u);
            Assert.False(h.Provider.BuildSheet().AwaitingRaise);
            Assert.True(rebuilds >= 1);

            // A vital regen tick outside a raise must NOT rebuild the sheet.
            int before = rebuilds;
            h.Player.OnVitalCurrent(vitalId: 2u, current: 50u);
            Assert.Equal(before, rebuilds);

            // But the full vital record answering a RaiseVital releases.
            h.Provider.HandleRaiseRequest(new CharacterStatController.RaiseRequest(
                CharacterStatController.RaiseTargetKind.Vital, StatId: 1u, Cost: 20L, Amount: 1));
            Assert.True(h.Provider.BuildSheet().AwaitingRaise);
            h.Player.OnVitalUpdate(vitalId: 1u, ranks: 1u, start: 10u, xp: 20u, current: 15u);
            Assert.False(h.Provider.BuildSheet().AwaitingRaise);
        }

        // Panel unmount resets a still-held gate (silent-rejection recovery).
        using (h.Provider.SubscribeChanged(() => { }))
        {
            h.Provider.HandleRaiseRequest(new CharacterStatController.RaiseRequest(
                CharacterStatController.RaiseTargetKind.Attribute, StatId: 1u, Cost: 20L, Amount: 1));
            Assert.True(h.Provider.BuildSheet().AwaitingRaise);
        }
        Assert.False(h.Provider.BuildSheet().AwaitingRaise);
    }


    private static SpellMetadata TestSpell(uint spellId) => new(
        spellId, "Test", "War Magic", 0u, 0u, "", 0f, 0,
        false, false, "", 0, 0, 0u, 0, false, false, true,
        0f, 0u, 0u, 0u, 0);

    private sealed class VitaeHarness
    {
        public ClientObjectTable Table { get; } = new();
        public Spellbook Book { get; }
        public LocalPlayerState Player { get; }
        public CharacterSheetProvider Provider { get; }

        public VitaeHarness()
        {
            Book = new Spellbook(SpellTable.Create([TestSpell(1u), TestSpell(2u)]));
            Player = new LocalPlayerState(Book);
            Provider = new CharacterSheetProvider(
                Table, Player,
                playerGuid: () => 0u,
                activeToonName: () => "default",
                fallbackSheet: name => new CharacterSheet { Name = name, Level = -1 });
        }
    }

    [Fact]
    public void BuildSheet_AttributeBuff_ShowsEffectiveValueAndBasePair()
    {
        var h = new VitaeHarness();
        h.Player.OnAttributeUpdate(atType: 1u, ranks: 100u, start: 100u, xp: 0u);   // Strength, base 200
        h.Book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u, LayerId: 1u, Duration: 60d, CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute,
            StatModKey: 1u, StatModValue: 1.1f, Bucket: 1u));

        var sheet = h.Provider.BuildSheet();

        Assert.Equal(220, sheet.Strength);                    // effective: 200 * 1.1
        Assert.Equal(200, sheet.AttributeBaseValues[0]);       // base unaffected
    }

    [Fact]
    public void BuildSheet_AttributeUnderVitae_AttributesAreVitaeImmune()
    {
        var h = new VitaeHarness();
        h.Player.OnAttributeUpdate(atType: 1u, ranks: 100u, start: 100u, xp: 0u);   // Strength, base 200
        h.Book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: -1d, CasterGuid: 0u,
            StatModType: 0u, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));   // 33% vitae

        var sheet = h.Provider.BuildSheet();

        Assert.Equal(200, sheet.Strength);          // unaffected by vitae
        Assert.Equal(200, sheet.AttributeBaseValues[0]);
    }

    [Fact]
    public void BuildSheet_SkillUnderVitae_ShowsEffectiveLevelAndVitaeModifier()
    {
        var h = new VitaeHarness();
        h.Player.OnSkillUpdate(skillId: 6u, ranks: 300u, status: 2u, xp: 0u,
            init: 3u, resistance: 0u, lastUsed: 0d, formulaBonus: 0u);   // base 303
        h.Book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: -1d, CasterGuid: 0u,
            StatModType: 0u, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));   // 33% vitae

        var sheet = h.Provider.BuildSheet();

        var skill = Assert.Single(sheet.Skills);
        Assert.Equal(303, skill.BaseLevel);
        Assert.Equal(203, skill.CurrentLevel);      // 303 * 0.67, truncated
        Assert.Equal(-100, skill.VitaeModifier);
    }

    [Fact]
    public void BuildSheet_SkillAugmentations_UseRetailBeforeAndAfterOrdering()
    {
        var h = new VitaeHarness();
        var properties = new PropertyBundle();
        properties.Ints[(uint)PropertyInt.LumAugAllSkills] = 3;
        properties.Ints[(uint)PropertyInt.AugmentationSkilledMagic] = 1;
        properties.Ints[(uint)PropertyInt.AugmentationJackOfAllTrades] = 1;
        properties.Ints[(uint)PropertyInt.LumAugSkilledSpec] = 4;
        h.Player.OnProperties(properties);
        h.Player.OnSkillUpdate(
            skillId: 0x1Fu,
            ranks: 100u,
            status: 3u,
            xp: 0u,
            init: 0u,
            resistance: 0u,
            lastUsed: 0d,
            formulaBonus: 0u);

        CharacterSkill skill = Assert.Single(h.Provider.BuildSheet().Skills);

        Assert.Equal(113, skill.BaseLevel);
        Assert.Equal(126, skill.CurrentLevel);
        Assert.Equal(0, skill.VitaeModifier);
    }

    [Fact]
    public void BuildSheet_VitalPairsCarryBaseAndVitaeContribution()
    {
        var h = new VitaeHarness();
        h.Player.OnAttributeUpdate(
            atType: 2u,
            ranks: 100u,
            start: 100u,
            xp: 0u);
        h.Player.OnVitalUpdate(
            vitalId: 7u,
            ranks: 0u,
            start: 100u,
            xp: 0u,
            current: 150u);
        h.Book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u,
            LayerId: 1u,
            Duration: -1d,
            CasterGuid: 0u,
            StatModType: 0u,
            StatModKey: 0u,
            StatModValue: 0.8f,
            Bucket: 4u));

        CharacterSheet sheet = h.Provider.BuildSheet();

        Assert.Equal(200, sheet.VitalBaseMaxValues[0]);
        Assert.Equal(-40, sheet.VitalVitaeModifiers[0]);
        Assert.Equal(160, sheet.HealthMax);
    }

    [Fact]
    public void SubscribeChanged_FiresOnEnchantmentsChanged_AndRebuildReflectsNewValue()
    {
        var h = new VitaeHarness();
        h.Player.OnAttributeUpdate(atType: 1u, ranks: 100u, start: 100u, xp: 0u);   // Strength, base 200
        int changed = 0;
        using IDisposable subscription = h.Provider.SubscribeChanged(() => changed++);

        Assert.Equal(200, h.Provider.BuildSheet().Strength);   // no buff yet

        h.Book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 2u, LayerId: 1u, Duration: 60d, CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute,
            StatModKey: 1u, StatModValue: 1.1f, Bucket: 1u));

        Assert.True(changed >= 1);                              // live-refresh notice fired
        Assert.Equal(220, h.Provider.BuildSheet().Strength);     // and the rebuilt sheet reflects it
    }

    [Fact]
    public void SubscribeChanged_Dispose_UnsubscribesFromEnchantmentsChanged()
    {
        var h = new VitaeHarness();
        int changed = 0;
        IDisposable subscription = h.Provider.SubscribeChanged(() => changed++);
        subscription.Dispose();

        h.Book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u, LayerId: 1u, Duration: -1d, CasterGuid: 0u,
            StatModType: 0u, StatModKey: 0u, StatModValue: 0.67f, Bucket: 4u));

        Assert.Equal(0, changed);
    }


    [Theory]
    [InlineData(0x4, "ID_StatManagement_Header_PKStatus_PK")]
    [InlineData(0x40, "ID_StatManagement_Header_PKStatus_PKL")]
    [InlineData(0x2, "ID_StatManagement_Header_PKStatus_NPK")]   // plain NPK bit
    [InlineData(0x0, "ID_StatManagement_Header_PKStatus_NPK")]   // Undef — still resolves NPK, not omitted
    public void BuildSheet_PkStatus_ResolvesCorrectKeyByStatus(int rawStatus, string expectedKey)
    {
        var objects = new ClientObjectTable();
        var player = new LocalPlayerState();
        string? capturedKey = null;
        var provider = new CharacterSheetProvider(
            objects, player,
            playerGuid: () => PlayerGuid,
            resolveUiString: key =>
            {
                capturedKey = key;
                return key;   // echo — the test asserts on the KEY, not invented English
            });

        var obj = new ClientObject { ObjectId = PlayerGuid, Name = "Testy" };
        objects.AddOrUpdate(obj);
        objects.UpdateIntProperty(PlayerGuid, 134u, rawStatus);

        CharacterSheet sheet = provider.BuildSheet();

        Assert.Equal(expectedKey, capturedKey);
        Assert.Equal(expectedKey, sheet.PkStatus);
    }

    [Fact]
    public void BuildSheet_PkStatus_NoResolver_LeavesPkStatusNull()
    {
        var objects = new ClientObjectTable();
        var player = new LocalPlayerState();
        var provider = new CharacterSheetProvider(objects, player, playerGuid: () => PlayerGuid);

        var obj = new ClientObject { ObjectId = PlayerGuid, Name = "Testy" };
        objects.AddOrUpdate(obj);
        objects.UpdateIntProperty(PlayerGuid, 134u, 0x4);   // PK

        Assert.Null(provider.BuildSheet().PkStatus);
    }

    [Fact]
    public void BuildSheet_Level_NullWhenPropertyAbsent_PresentOtherwise()
    {
        var objects = new ClientObjectTable();
        var player = new LocalPlayerState();
        var provider = new CharacterSheetProvider(objects, player, playerGuid: () => PlayerGuid);

        var obj = new ClientObject { ObjectId = PlayerGuid, Name = "Testy" };
        obj.Properties.Ints[0x18u] = 1;   // some OTHER property present so HasLiveData() is true
        objects.AddOrUpdate(obj);
        Assert.Null(provider.BuildSheet().Level);

        obj.Properties.Ints[0x19u] = 42;
        objects.AddOrUpdate(obj);
        Assert.Equal(42, provider.BuildSheet().Level);
    }

    [Fact]
    public void BuildSheet_Title_ResolvesDisplayTitleIdThroughResolver()
    {
        var objects = new ClientObjectTable();
        var player = new LocalPlayerState();
        var titles = new RuntimeCharacterTitleState();
        var provider = new CharacterSheetProvider(
            objects, player,
            playerGuid: () => PlayerGuid,
            titles: titles,
            resolveDisplayTitle: id => id == 13u ? "War Mage" : null);

        var obj = new ClientObject { ObjectId = PlayerGuid, Name = "Testy" };
        obj.Properties.Ints[0x19u] = 1;   // some property present so HasLiveData() is true
        objects.AddOrUpdate(obj);

        Assert.Null(provider.BuildSheet().Title);   // no display title seeded yet

        titles.ReplaceTable(13u, new uint[] { 13u });

        Assert.Equal("War Mage", provider.BuildSheet().Title);
    }

    [Fact]
    public void SubscribeChanged_FiresOnTitlesTableReplacedAndDisplayTitleChanged_AndUnsubscribesOnDispose()
    {
        var objects = new ClientObjectTable();
        var player = new LocalPlayerState();
        var titles = new RuntimeCharacterTitleState();
        var provider = new CharacterSheetProvider(
            objects, player, playerGuid: () => PlayerGuid, titles: titles);
        int changed = 0;
        IDisposable subscription = provider.SubscribeChanged(() => changed++);

        titles.ReplaceTable(1u, new uint[] { 1u });          // TableReplaced (+ DisplayTitleChanged, id 0->1)
        Assert.True(changed >= 1);

        int afterFirst = changed;
        titles.ApplyUpdateTitle(2u, setAsDisplay: true);      // UpdateTitle → DisplayTitleChanged (1->2)
        Assert.True(changed > afterFirst);

        subscription.Dispose();
        int afterDispose = changed;
        titles.ReplaceTable(3u, new uint[] { 3u });
        Assert.Equal(afterDispose, changed);
    }

    [Fact]
    public void BuildSheet_Luminance_ReadsInt64Properties6And7()
    {
        var objects = new ClientObjectTable();
        var player = new LocalPlayerState();
        var provider = new CharacterSheetProvider(objects, player, playerGuid: () => PlayerGuid);

        var obj = new ClientObject { ObjectId = PlayerGuid, Name = "Testy" };
        obj.Properties.Int64s[6u] = 1_500_000L;
        obj.Properties.Int64s[7u] = 25_000_000L;
        objects.AddOrUpdate(obj);

        var sheet = provider.BuildSheet();

        Assert.Equal(1_500_000L, sheet.AvailableLuminance);
        Assert.Equal(25_000_000L, sheet.MaximumLuminance);
    }
}
