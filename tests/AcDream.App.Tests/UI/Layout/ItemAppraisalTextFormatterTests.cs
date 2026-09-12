using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ItemAppraisalTextFormatterTests
{
    [Fact]
    public void WeaponAndMagic_AreProjectedInRetailOrderWithDatDescriptions()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000001u,
            Name = "Flaming Sword",
            Type = ItemType.MeleeWeapon,
            ValidLocations = EquipMask.MeleeWeapon,
            Value = 12_500,
            Burden = 450,
            Workmanship = 8.4f,
        };
        var properties = new PropertyBundle();
        properties.Ints[353u] = 2;
        properties.Ints[19u] = 12_500;
        properties.Ints[5u] = 450;
        properties.Ints[204u] = 4;
        properties.Ints[105u] = 8;
        properties.Ints[106u] = 300;
        properties.Ints[107u] = 250;
        properties.Ints[108u] = 500;
        properties.Ints[117u] = 20;
        properties.Floats[29u] = 1.05d;
        properties.Strings[14u] = "Use this item to attack.";
        properties.Strings[16u] = "A finely balanced enchanted blade.";

        SpellMetadata flame = Spell(101u, "Flame Bolt VI", "Hurls a bolt of flame.");
        SpellMetadata armor = Spell(102u, "Impenetrability VI", "Increases the target's armor.");
        AppraiseInfoParser.Parsed appraisal = Parsed(
            properties,
            spells: [101u, 0x8000_0066u],
            weapon: new AppraiseInfoParser.WeaponProfile(
                DamageType: 1u,
                WeaponTime: 30u,
                WeaponSkill: 44u,
                Damage: 40u,
                DamageVariance: 0.25d,
                DamageMod: 1d,
                WeaponLength: 1d,
                MaxVelocity: 0d,
                WeaponOffense: 1.15d,
                MaxVelocityEstimated: 0u));

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            appraisal,
            id => id switch
            {
                101u => flame,
                102u => armor,
                _ => null,
            });

        Assert.Contains("Value: 12,500", report);
        Assert.Contains("Workmanship: Utterly flawless (8)", report);
        Assert.Contains("Skill: Heavy Weapons (Sword)", report);
        Assert.Contains("Damage: 30 - 40, Slashing", report);
        Assert.Contains("Elemental Damage Bonus: 4, Slashing.", report);
        Assert.Contains("Speed: Fast (30)", report);
        Assert.Contains("Bonus to Attack Skill: +15%.", report);
        Assert.Contains("Bonus to Melee Defense: +5%.", report);
        Assert.Contains("Spells: Flame Bolt VI", report);
        Assert.DoesNotContain("Spells: Flame Bolt VI, Impenetrability VI", report);
        Assert.Contains("Spellcraft: 300.", report);
        Assert.Contains("Mana: 250 / 500.", report);
        Assert.Contains("Mana Cost: 20.", report);
        Assert.Contains("Spell Descriptions:", report);
        Assert.Contains("~ Flame Bolt VI: Hurls a bolt of flame.", report);
        Assert.Contains("Enchantments:", report);
        Assert.Contains(
            "~ Impenetrability VI: Increases the target's armor.",
            report);
        Assert.True(
            report.IndexOf("Skill:", StringComparison.Ordinal)
            < report.IndexOf("Spells:", StringComparison.Ordinal));
        Assert.True(
            report.IndexOf("Spells:", StringComparison.Ordinal)
            < report.IndexOf("Spell Descriptions:", StringComparison.Ordinal));
        Assert.EndsWith("A finely balanced enchanted blade.", report);
    }

    [Fact]
    public void NpcLooksLikeObjectCreature_UsesRetailUnknownsAndSuppressesNegativeCapacity()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000005u,
            Name = "Black Phyntos Hive",
            Type = ItemType.Creature,
            ItemsCapacity = -1,
            ContainersCapacity = -1,
        };
        var properties = new PropertyBundle();
        properties.Strings[16u] =
            "A hollowed out tree trunk that has a Phyntos Wasp Hive in it.";

        ItemAppraisalReport report = ItemAppraisalTextFormatter.BuildReport(
            obj,
            Parsed(properties),
            _ => null);

        Assert.Equal(
            "Value: ???\nBurden: Unknown\n\n\n\n"
            + "A hollowed out tree trunk that has a Phyntos Wasp Hive in it.",
            report.ToString());
        Assert.DoesNotContain("Can hold", report.ToString());
        Assert.Collection(
            report.Fragments,
            value =>
            {
                Assert.Equal(ItemAppraisalSeparator.None, value.Separator);
                Assert.Equal("Value: ???", value.Text);
            },
            burden =>
            {
                Assert.Equal(ItemAppraisalSeparator.Line, burden.Separator);
                Assert.Equal("Burden: Unknown", burden.Text);
            },
            tinkeringBoundary =>
            {
                Assert.Equal(ItemAppraisalSeparator.Line, tinkeringBoundary.Separator);
                Assert.Equal(string.Empty, tinkeringBoundary.Text);
            },
            specialPropertiesBoundary =>
            {
                Assert.Equal(ItemAppraisalSeparator.Line, specialPropertiesBoundary.Separator);
                Assert.Equal(string.Empty, specialPropertiesBoundary.Text);
            },
            description =>
            {
                Assert.Equal(ItemAppraisalSeparator.Paragraph, description.Separator);
                Assert.Equal(properties.Strings[16u], description.Text);
            });
    }

    [Fact]
    public void HookWithHookProfile_SuppressesHooksOwnCapacity()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000015u,
            Name = "Weapon Hook",
            Type = ItemType.Container,
            ItemsCapacity = 24,
            ContainersCapacity = 1,
            HookItemTypes = (uint)ItemType.Weapon,
            HookType = 4u,
        };

        ItemAppraisalReport report = ItemAppraisalTextFormatter.BuildReport(
            obj,
            Parsed(
                new PropertyBundle(),
                hook: new AppraiseInfoParser.HookProfile(
                    Flags: 0u,
                    ValidLocations: 0u,
                    AmmoType: 0u)),
            _ => null);

        Assert.DoesNotContain("Can hold", report.ToString());
    }

    [Fact]
    public void OrdinaryContainer_PresentsCapacityAsRetailParagraph()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000006u,
            Name = "Backpack",
            Type = ItemType.Container,
            ItemsCapacity = 24,
            ContainersCapacity = 1,
        };

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            Parsed(new PropertyBundle()),
            _ => null);

        Assert.Equal(
            "Value: ???\nBurden: Unknown\n\n\n\n"
            + "Can hold up to 24 items and 1 containers.",
            report);
    }

    [Fact]
    public void Boots_PreserveRetailBlankSectionBoundaries()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000016u,
            Name = "Steel Toed Boots",
            Type = ItemType.Clothing,
            ValidLocations = EquipMask.FootWear,
            Priority = 0x0001_0000u,
        };
        var properties = new PropertyBundle();
        properties.Ints[19u] = 1_250;
        properties.Ints[5u] = 180;
        properties.Ints[105u] = 7;
        properties.Ints[28u] = 80;
        AppraiseInfoParser.Parsed appraisal = Parsed(
            properties,
            armor: new AppraiseInfoParser.ArmorProfile(
                SlashingProtection: 1f,
                PiercingProtection: 1f,
                BludgeoningProtection: 1f,
                ColdProtection: 1f,
                FireProtection: 1f,
                AcidProtection: 1f,
                NetherProtection: 1f,
                LightningProtection: 1f));

        ItemAppraisalReport report = ItemAppraisalTextFormatter.BuildReport(
            obj,
            appraisal,
            _ => null);

        Assert.Contains(
            "Workmanship: Flawless (7)\n\nCovers Feet\n\nArmor Level: 80",
            report.ToString());
        var text = new UiText { Width = 1_000f };
        string[] lines = ItemAppraisalTextLayout.Shape(text, report)
            .Select(line => line.Text)
            .ToArray();
        int workmanship = Array.IndexOf(lines, "Workmanship: Flawless (7)");
        Assert.True(workmanship >= 0);
        Assert.Equal(string.Empty, lines[workmanship + 1]);
        Assert.Equal("Covers Feet", lines[workmanship + 2]);
        Assert.Equal(string.Empty, lines[workmanship + 3]);
        Assert.Equal("Armor Level: 80", lines[workmanship + 4]);
    }

    [Fact]
    public void BookCapacity_UsesRetailUsedThenTotalPropertyOrder()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x5000000Bu,
            Name = "Book",
            Type = ItemType.Writable,
        };
        var properties = new PropertyBundle();
        properties.Ints[174u] = 3;
        properties.Ints[175u] = 10;

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            Parsed(properties),
            _ => null);

        Assert.EndsWith("3 of 10 pages full.", report);
    }

    [Theory]
    [InlineData((ushort)0x0000, ItemAppraisalFontStyle.Detrimental)]
    [InlineData((ushort)0x0008, ItemAppraisalFontStyle.Beneficial)]
    public void WeaponDamage_UsesRetailEnchantmentFontSelection(
        ushort highHalf,
        ItemAppraisalFontStyle expected)
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000007u,
            Name = "Sword",
            Type = ItemType.MeleeWeapon,
            ValidLocations = EquipMask.MeleeWeapon,
        };
        AppraiseInfoParser.Parsed parsed = Parsed(
            new PropertyBundle(),
            weapon: new AppraiseInfoParser.WeaponProfile(
                DamageType: 1u,
                WeaponTime: 30u,
                WeaponSkill: 44u,
                Damage: 40u,
                DamageVariance: 0d,
                DamageMod: 1d,
                WeaponLength: 1d,
                MaxVelocity: 0d,
                WeaponOffense: 1d,
                MaxVelocityEstimated: 0u),
            weaponEnchantments: ((ushort)0x0008, highHalf));

        ItemAppraisalReport report = ItemAppraisalTextFormatter.BuildReport(
            obj,
            parsed,
            _ => null);

        ItemAppraisalFragment damage = Assert.Single(
            report.Fragments,
            fragment => fragment.Text.StartsWith(
                "Damage:",
                StringComparison.Ordinal));
        Assert.Equal(expected, damage.Style);
    }

    [Fact]
    public void TextLayout_PreservesRetailPaletteAcrossParagraphs()
    {
        Vector4 normal = Vector4.One;
        var beneficial = new Vector4(0f, 1f, 0f, 1f);
        var detrimental = new Vector4(1f, 0f, 0f, 1f);
        var text = new UiText
        {
            Width = 1_000f,
            FontColorPalette = [normal, beneficial, detrimental],
        };
        var report = new ItemAppraisalReport(
        [
            new ItemAppraisalFragment(
                "Normal",
                ItemAppraisalSeparator.None,
                ItemAppraisalFontStyle.Normal),
            new ItemAppraisalFragment(
                "High",
                ItemAppraisalSeparator.Line,
                ItemAppraisalFontStyle.Beneficial),
            new ItemAppraisalFragment(
                "Low",
                ItemAppraisalSeparator.Paragraph,
                ItemAppraisalFontStyle.Detrimental),
        ]);

        IReadOnlyList<UiText.Line> lines = ItemAppraisalTextLayout.Shape(
            text,
            report);

        Assert.Equal(["Normal", "High", "", "Low"], lines.Select(line => line.Text));
        Assert.Equal(normal, lines[0].Color);
        Assert.Equal(beneficial, lines[1].Color);
        Assert.Equal(detrimental, lines[3].Color);
    }

    [Fact]
    public void LockInformation_UsesRetailPresenceRulesWordingAndParagraphs()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000009u,
            Name = "Locked Chest",
            Type = ItemType.Container,
        };
        var properties = new PropertyBundle();
        properties.Bools[3u] = true;
        properties.Ints[38u] = 212;
        properties.Ints[173u] = 42;

        ItemAppraisalReport report = ItemAppraisalTextFormatter.BuildReport(
            obj,
            Parsed(properties),
            _ => null);

        Assert.Contains(
            report.Fragments,
            fragment => fragment.Text == "Locked"
                        && fragment.Separator == ItemAppraisalSeparator.Paragraph);
        Assert.Contains(
            report.Fragments,
            fragment => fragment.Text
                == "The lock looks difficult to pick (Resistance 212)."
                && fragment.Separator == ItemAppraisalSeparator.Paragraph);
    }

    [Fact]
    public void MissingLockedFlag_ReportsLockpickBonusInsteadOfInventingLockState()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x5000000Au,
            Name = "Lockpick",
            Type = ItemType.Misc,
        };
        var properties = new PropertyBundle();
        properties.Ints[38u] = -7;

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            Parsed(properties),
            _ => null);

        Assert.Contains("Bonus to Lockpick Skill: -7", report);
        Assert.DoesNotContain("Locked", report);
        Assert.DoesNotContain("Unlocked", report);
    }

    [Fact]
    public void ArmorRequirementsAndUses_UseRetailLabelsAndResistanceBands()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000002u,
            Name = "Platemail Hauberk",
            Type = ItemType.Armor,
            ValidLocations = EquipMask.ChestArmor,
            Structure = 8,
            MaxStructure = 10,
        };
        var properties = new PropertyBundle();
        properties.Ints[28u] = 200;
        properties.Ints[158u] = 1;
        properties.Ints[159u] = 6;
        properties.Ints[160u] = 250;
        properties.Ints[324u] = 4;
        properties.Ints[369u] = 50;
        properties.Ints[370u] = 3;
        properties.Ints[92u] = 8;
        properties.Bools[69u] = false;
        properties.Strings[25u] = "Tester";
        AppraiseInfoParser.Parsed appraisal = Parsed(
            properties,
            armor: new AppraiseInfoParser.ArmorProfile(
                SlashingProtection: 1.2f,
                PiercingProtection: 0.8f,
                BludgeoningProtection: 0.4f,
                ColdProtection: 0f,
                FireProtection: 1.6f,
                AcidProtection: 2f,
                NetherProtection: 1f,
                LightningProtection: 0.2f));

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            appraisal,
            _ => null);

        Assert.Contains("Ratings: Dam 3", report);
        Assert.Contains("Armor Level: 200", report);
        Assert.Contains("Slashing: Above Average  (240)", report);
        Assert.Contains("Piercing: Average  (160)", report);
        Assert.Contains("Bludgeoning: Below Average  (80)", report);
        Assert.Contains("Cold: None  (0)", report);
        Assert.Contains("Fire: Excellent  (320)", report);
        Assert.Contains("Acid: Unparalleled  (400)", report);
        Assert.Contains("Electric: Poor  (40)", report);
        Assert.Contains("Wield requires Viamontian", report);
        Assert.Contains("Wield requires Melee Defense 250", report);
        Assert.Contains("Use requires level 50.", report);
        Assert.Contains("Number of uses remaining: 8", report);
        Assert.Contains("Created by Tester.", report);
        Assert.Contains("This item cannot be sold.", report);
    }

    [Fact]
    public void Launcher_UsesDamageBonusModifierAndAmmunitionWording()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000003u,
            Name = "Longbow",
            Type = ItemType.MissileWeapon,
            ValidLocations = EquipMask.MissileWeapon,
            AmmoType = 1,
        };
        AppraiseInfoParser.Parsed appraisal = Parsed(
            new PropertyBundle(),
            weapon: new AppraiseInfoParser.WeaponProfile(
                DamageType: 2u,
                WeaponTime: 45u,
                WeaponSkill: 47u,
                Damage: 12u,
                DamageVariance: 0d,
                DamageMod: 1.13d,
                WeaponLength: 1d,
                MaxVelocity: 50d,
                WeaponOffense: 1d,
                MaxVelocityEstimated: 1u));

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            appraisal,
            _ => null);

        Assert.Contains("Skill: Missile Weapons", report);
        Assert.Contains("Damage Bonus: 12", report);
        Assert.DoesNotContain("Damage Bonus: 12, Piercing", report);
        Assert.Contains("Damage Modifier: +13%.", report);
        Assert.Contains("Speed: Average (45)", report);
        Assert.Contains("Range: 85 yds. (based on STRENGTH 100)", report);
        Assert.Contains("Uses arrows as ammunition.", report);
    }

    [Fact]
    public void SpecialProperties_AreCombinedUsingRetailNames()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000004u,
            Name = "Imbued Katar",
            Type = ItemType.MeleeWeapon,
        };
        var properties = new PropertyBundle();
        properties.Ints[279u] = 3;
        properties.Ints[292u] = 4;
        properties.Ints[179u] = 0x0000_0481;
        properties.Ints[114u] = 1;
        properties.Ints[33u] = 1;
        properties.Floats[167u] = 90d;
        properties.Bools[99u] = true;
        properties.Bools[100u] = true;

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            Parsed(properties),
            _ => null);

        Assert.Contains("You can only carry 3 of these items.", report);
        Assert.Contains("Cooldown When Used: 1m 30s ", report);
        Assert.Contains("Cleave: 4 enemies in front arc.", report);
        Assert.Contains(
            "Properties: Critical Strike, Cold Rending, +1 Melee Defense, Attuned, Bonded, Ivoryable, Dyeable",
            report);
        Assert.Contains("This item cannot be further imbued.", report);
    }

    [Fact]
    public void SpecializedItemBranches_FollowRetailNamesWordingAndOrder()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000020u,
            Name = "Ruby Coat",
            Type = ItemType.Clothing,
            ValidLocations = EquipMask.ChestWear,
            Priority = 0x0008u | 0x0010u | 0x0020u | 0x0040u,
        };
        var properties = new PropertyBundle();
        properties.Ints[171u] = 2;
        properties.Strings[39u] = "Alyssa";
        properties.Strings[40u] = "Borelean";
        properties.Ints[105u] = 10;
        properties.Ints[170u] = 4;
        properties.Ints[265u] = 13;
        properties.Ints[370u] = 3;
        properties.Ints[371u] = -4;
        properties.Ints[379u] = 2;
        properties.Ints[86u] = 50;
        properties.Ints[87u] = 50;
        properties.Int64s[4u] = 300;
        properties.Int64s[5u] = 100;
        properties.Ints[319u] = 10;
        properties.Ints[320u] = 3;
        properties.Ints[109u] = 250;
        properties.Ints[188u] = 4;
        properties.Bools[108u] = true;
        properties.Ints[17u] = 42;
        properties.Ints[267u] = 1;
        properties.Ints[98u] = 1;
        properties.Ints[268u] = 90_061;
        properties.Strings[16u] = "Ruby sword";
        properties.Ints[172u] = 5;
        properties.Ints[131u] = 0x26;
        properties.Ints[177u] = 2;
        properties.Ints[178u] = 0x26;
        properties.Ints[111u] = 2 | 0x20 | 0x10;

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            Parsed(properties),
            _ => null,
            Names(
                materials: new Dictionary<uint, string> { [0x26u] = "Ruby" }));

        Assert.Contains("This item has been tinkered 2 times.", report);
        Assert.Contains("Last tinkered by Alyssa.", report);
        Assert.Contains("Imbued by Borelean.", report);
        Assert.Contains("Workmanship: Well-crafted (2.50)", report);
        Assert.Contains("Salvaged from 4 items.", report);
        Assert.Contains("Set: Soldier's", report);
        Assert.Contains("Ratings: Dam 3", report);
        Assert.DoesNotContain("Dam Resist", report);
        Assert.Contains("This item adds 2 Vitality.", report);
        Assert.Contains(
            "Covers Chest, Abdomen, Upper Arms, Lower Arms",
            report);
        Assert.Contains("Restricted to characters of Level 50.", report);
        Assert.Contains("Item Level: 3 / 10", report);
        Assert.Contains("Item XP: 300 / 600", report);
        Assert.Contains("Activation requires Arcane Lore: 250, Viamontian", report);
        Assert.Contains(
            "This rare item has a timer restriction of 3 minutes. "
            + "You will not be able to use another rare item with a timer "
            + "within 3 minutes of using this one.",
            report);
        Assert.Contains("Rare #42", report);
        Assert.Contains(
            "This item expires in 1 days, 1 hours, 1 minutes, 1 seconds.",
            report);
        Assert.Contains("Priceless Ruby sword, set with 2 Rubies", report);
        Assert.Contains("Player Killers may not use this portal.", report);
        Assert.Contains("This portal cannot be recalled nor linked to.", report);
        Assert.Contains("This portal cannot be summoned.", report);
        Assert.True(
            report.IndexOf("Set: Soldier's", StringComparison.Ordinal)
            < report.IndexOf("Covers Chest", StringComparison.Ordinal));
        Assert.True(
            report.IndexOf("Rare #42", StringComparison.Ordinal)
            < report.IndexOf("This item expires", StringComparison.Ordinal));
    }

    [Fact]
    public void SlayerAndWieldCreatureRequirements_UseRetailEnumNames()
    {
        var properties = new PropertyBundle();
        properties.Ints[166u] = 10;
        properties.Ints[158u] = 11;
        properties.Ints[159u] = 0;
        properties.Ints[160u] = 10;

        string report = ItemAppraisalTextFormatter.Build(
            new ClientObject
            {
                ObjectId = 0x50000021u,
                Name = "Drudge Slayer",
                Type = ItemType.MeleeWeapon,
            },
            Parsed(properties),
            _ => null,
            Names(creatures: new Dictionary<uint, string> { [10u] = "Drudge" }));

        Assert.Contains("Properties: Drudge slayer", report);
        Assert.Contains("Wield requires Drudge type", report);
        Assert.DoesNotContain("Creature type 10", report);
        Assert.DoesNotContain("creature 10", report);
    }

    [Fact]
    public void HealerProfile_UsesHealingRowsInsteadOfConsumableBoostProse()
    {
        var properties = new PropertyBundle();
        properties.Ints[89u] = 2;
        properties.Ints[90u] = 12;
        properties.Floats[100u] = 1.25d;
        var obj = new ClientObject
        {
            ObjectId = 0x50000022u,
            Name = "Healing Kit",
            Type = ItemType.Misc,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Healer,
        };

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            Parsed(properties),
            _ => null);

        Assert.Contains("Bonus to Healing Skill: 12", report);
        Assert.Contains("Restoration Bonus: 125%", report);
        Assert.DoesNotContain("Restores 12 Health when used.", report);
    }

    [Fact]
    public void FailedMagicAndLauncher_PreserveRetailUnknownRows()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000023u,
            Name = "Unknown Bow",
            Type = ItemType.MissileWeapon,
            ValidLocations = EquipMask.MissileWeapon,
            AmmoType = 1,
        };
        AppraiseInfoParser.Parsed appraisal = Parsed(
            new PropertyBundle(),
            spells: [101u],
            weapon: new AppraiseInfoParser.WeaponProfile(
                DamageType: 2u,
                WeaponTime: uint.MaxValue,
                WeaponSkill: 47u,
                Damage: uint.MaxValue,
                DamageVariance: 0d,
                DamageMod: 1d,
                WeaponLength: 1d,
                MaxVelocity: 0d,
                WeaponOffense: 1d,
                MaxVelocityEstimated: 0u),
            success: false);

        string report = ItemAppraisalTextFormatter.Build(
            obj,
            appraisal,
            _ => null);

        Assert.Equal(2, Count(report, "Spells: unknown."));
        Assert.Contains("Damage Bonus: Unknown", report);
        Assert.Contains("Damage Modifier: Unknown", report);
        Assert.Contains("Speed:  Unknown", report);
        Assert.Contains("Range:  Unknown", report);
    }

    [Fact]
    public void OrdinaryStaminaConsumable_UsesRetailConsumedWording()
    {
        var properties = new PropertyBundle();
        properties.Ints[89u] = 4;
        properties.Ints[90u] = -20;

        string report = ItemAppraisalTextFormatter.Build(
            new ClientObject
            {
                ObjectId = 0x50000024u,
                Name = "Stamina Food",
                Type = ItemType.Food,
            },
            Parsed(properties),
            _ => null);

        Assert.Contains("Depletes 20 Stamina when consumed.", report);
    }

    [Theory]
    [InlineData(1, 250L, "Item Level: 3 / 10", "Item XP: 250 / 300")]
    [InlineData(2, 300L, "Item Level: 3 / 10", "Item XP: 300 / 700")]
    public void ItemExperienceStyles_UseRetailCumulativeCurves(
        int style,
        long experience,
        string expectedLevel,
        string expectedExperience)
    {
        var properties = new PropertyBundle();
        properties.Int64s[4u] = experience;
        properties.Int64s[5u] = 100L;
        properties.Ints[319u] = 10;
        properties.Ints[320u] = style;

        string report = ItemAppraisalTextFormatter.Build(
            new ClientObject
            {
                ObjectId = 0x50000025u,
                Name = "Leveling Item",
                Type = ItemType.Misc,
            },
            Parsed(properties),
            _ => null);

        Assert.Contains(expectedLevel, report);
        Assert.Contains(expectedExperience, report);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void EndOfRetailDatNameResolver_LoadsMaterialAndCreatureNames()
    {
        string? datDirectory = ResolveDatDirectory();
        if (datDirectory is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats =
            new AcDream.App.Tests.BoundedTestDatCollection(datDirectory);
        CreatureDisplayNameResolver creatures =
            CreatureDisplayNameResolver.Load(dats);
        RetailAppraisalNameResolver names =
            RetailAppraisalNameResolver.Load(dats, creatures);

        Assert.Equal("Ruby", names.ResolveMaterial(0x26));
        Assert.Equal("Ghost", names.ResolveCreature(77));
    }

    // OpenAC #36: a weapon's damage-type mask names every set bit, joined
    // with "/", in the game's own order and spelling; only a mask with no
    // type at all is "unknown type".
    [Theory]
    [InlineData(0x0003u, "Damage: 30 - 40, Slashing/Piercing")]
    [InlineData(0x0006u, "Damage: 30 - 40, Piercing/Bludgeoning")]
    [InlineData(0x0040u, "Damage: 30 - 40, Electrical")]
    [InlineData(0x0400u, "Damage: 30 - 40, Nether")]
    [InlineData(0x1000_0000u, "Damage: 30 - 40, Prismatic")]
    [InlineData(0x1000_0001u, "Damage: 30 - 40, Slashing/Prismatic")]
    [InlineData(0x0000u, "Damage: 30 - 40, unknown type")]
    public void WeaponDamageLine_NamesEverySetDamageType(uint damageType, string expected)
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000001u,
            Name = "Spiked Sword",
            Type = ItemType.MeleeWeapon,
            ValidLocations = EquipMask.MeleeWeapon,
        };
        var properties = new PropertyBundle();
        properties.Ints[353u] = 2;
        AppraiseInfoParser.Parsed appraisal = Parsed(
            properties,
            weapon: new AppraiseInfoParser.WeaponProfile(
                DamageType: damageType,
                WeaponTime: 30u,
                WeaponSkill: 44u,
                Damage: 40u,
                DamageVariance: 0.25d,
                DamageMod: 1d,
                WeaponLength: 1d,
                MaxVelocity: 0d,
                WeaponOffense: 1d,
                MaxVelocityEstimated: 0u));

        string report = ItemAppraisalTextFormatter.Build(obj, appraisal, _ => null);

        Assert.Contains(expected, report);
    }

    [Fact]
    public void ElementalDamageBonus_NamesTheCombinedTypeTheSameWay()
    {
        var obj = new ClientObject
        {
            ObjectId = 0x50000001u,
            Name = "Crackling Sword",
            Type = ItemType.MeleeWeapon,
            ValidLocations = EquipMask.MeleeWeapon,
        };
        var properties = new PropertyBundle();
        properties.Ints[353u] = 2;
        properties.Ints[204u] = 4;
        AppraiseInfoParser.Parsed appraisal = Parsed(
            properties,
            weapon: new AppraiseInfoParser.WeaponProfile(
                DamageType: 0x0041u,
                WeaponTime: 30u,
                WeaponSkill: 44u,
                Damage: 40u,
                DamageVariance: 0.25d,
                DamageMod: 1d,
                WeaponLength: 1d,
                MaxVelocity: 0d,
                WeaponOffense: 1d,
                MaxVelocityEstimated: 0u));

        string report = ItemAppraisalTextFormatter.Build(obj, appraisal, _ => null);

        Assert.Contains("Damage: 30 - 40, Slashing/Electrical", report);
        Assert.Contains("Elemental Damage Bonus: 4, Slashing/Electrical.", report);
    }

    public static TheoryData<string, uint, uint, int, int, string> VendorParityCases()
        => new()
        {
            // name, validLocations, priority, itemsCapacity, containersCapacity,
            // a line the case must actually produce (so parity is never vacuous)
            { "weapon", (uint)EquipMask.MeleeWeapon, 0u, 0, 0, "Damage: 30 - 40" },
            { "armor", (uint)EquipMask.ChestArmor, 0x0C00u, 0, 0, "Covers Chest, Abdomen" },
            {
                "pack", 0u, 0u, 24, 1,
                "Can hold up to 24 items and 1 containers."
            },
        };

    [Theory]
    [MemberData(nameof(VendorParityCases))]
    public void Issue37_VendorListingAndSpawnedObject_AppraiseIdentically(
        string label,
        uint validLocations,
        uint priority,
        int itemsCapacity,
        int containersCapacity,
        string expectedLine)
    {
        Assert.False(string.IsNullOrEmpty(label));

        const uint ItemGuid = 0x50002000u;
        const uint VendorGuid = 0x40001000u;
        const uint HealerFlag = (uint)PublicWeenieFlags.Healer;

        // The one description both paths are handed.
        const string Name = "Silifi";
        const uint WeenieClassId = 42u;
        const uint IconId = 0x1234u;
        const int Value = 250;
        const int Burden = 450;
        const uint MaterialType = 60u;
        const uint TargetType = 0x00000080u;
        const byte CombatUse = 1;
        const ushort AmmoType = 3;
        const int Structure = 40;
        const int MaxStructure = 100;
        const float Workmanship = 8.5f;

        var vendorObjects = new ClientObjectTable();
        var vendor = new VendorState();
        using var materializer = new VendorShopItemMaterializer(vendor, vendorObjects);
        Assert.True(vendor.Apply(
            VendorGuid,
            default,
            new[]
            {
                new VendorShopItem(
                    ItemGuid,
                    StackSize: -1,
                    WeenieClassId: WeenieClassId,
                    Name: Name,
                    ItemType: (uint)ItemType.MeleeWeapon,
                    IconId: IconId,
                    Value: Value,
                    ValidLocations: validLocations,
                    Priority: priority,
                    ItemsCapacity: itemsCapacity,
                    ContainersCapacity: containersCapacity,
                    Structure: Structure,
                    MaxStructure: MaxStructure,
                    Workmanship: Workmanship,
                    Burden: Burden,
                    MaterialType: MaterialType,
                    TargetType: TargetType,
                    CombatUse: CombatUse,
                    AmmoType: AmmoType,
                    PublicWeenieBitfield: HealerFlag),
            }));
        ClientObject? listed = vendorObjects.Get(ItemGuid);
        Assert.NotNull(listed);

        var spawnedObjects = new ClientObjectTable();
        spawnedObjects.Ingest(ObjectTableWiring.ToWeenieData(
            new WorldSession.EntitySpawn(
                Guid: ItemGuid,
                Position: null,
                SetupTableId: null,
                AnimPartChanges: [],
                TextureChanges: [],
                SubPalettes: [],
                BasePaletteId: null,
                ObjScale: null,
                Name: Name,
                ItemType: (uint)ItemType.MeleeWeapon,
                MotionState: null,
                MotionTableId: null,
                ObjectDescriptionFlags: HealerFlag,
                TargetType: TargetType,
                IconId: IconId,
                WeenieClassId: WeenieClassId,
                Value: Value,
                Burden: Burden,
                ItemsCapacity: itemsCapacity,
                ContainersCapacity: containersCapacity,
                ValidLocations: validLocations,
                Priority: priority,
                Structure: Structure,
                MaxStructure: MaxStructure,
                Workmanship: Workmanship,
                CombatUse: CombatUse,
                AmmoType: AmmoType,
                MaterialType: MaterialType)));
        ClientObject? spawned = spawnedObjects.Get(ItemGuid);
        Assert.NotNull(spawned);

        var properties = new PropertyBundle();
        properties.Ints[19u] = Value;
        properties.Ints[5u] = Burden;
        properties.Ints[105u] = 8;
        properties.Ints[106u] = 300;
        properties.Ints[107u] = 250;
        properties.Ints[108u] = 500;
        AppraiseInfoParser.Parsed appraisal = Parsed(
            properties,
            weapon: new AppraiseInfoParser.WeaponProfile(
                DamageType: 1u,
                WeaponTime: 30u,
                WeaponSkill: 44u,
                Damage: 40u,
                DamageVariance: 0.25d,
                DamageMod: 1d,
                WeaponLength: 1d,
                MaxVelocity: 0d,
                WeaponOffense: 1d,
                MaxVelocityEstimated: 0u));

        string listedReport = ItemAppraisalTextFormatter.Build(
            listed!, appraisal, _ => null);
        string spawnedReport = ItemAppraisalTextFormatter.Build(
            spawned!, appraisal, _ => null);

        Assert.Equal(spawnedReport, listedReport);
        Assert.Contains(expectedLine, listedReport);
    }

    private static AppraiseInfoParser.Parsed Parsed(
        PropertyBundle properties,
        uint[]? spells = null,
        AppraiseInfoParser.WeaponProfile? weapon = null,
        AppraiseInfoParser.ArmorProfile? armor = null,
        AppraiseInfoParser.HookProfile? hook = null,
        (ushort Highlight, ushort Color)? weaponEnchantments = null,
        bool success = true)
        => new(
            Guid: 0x50000001u,
            Flags: AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable,
            Success: success,
            Properties: properties,
            SpellBook: spells ?? [],
            ArmorProfile: armor,
            CreatureProfile: null,
            WeaponProfile: weapon,
            HookProfile: hook,
            ArmorLevels: null,
            ArmorEnchantments: null,
            WeaponEnchantments: weaponEnchantments,
            ResistEnchantments: null);

    private static SpellMetadata Spell(
        uint id,
        string name,
        string description)
        => new(
            SpellId: id,
            Name: name,
            School: "War Magic",
            Family: id,
            IconId: 0u,
            SpellWords: string.Empty,
            Duration: 0f,
            ManaCost: 10,
            IsDebuff: false,
            IsFellowship: false,
            Description: description,
            SortKey: 0,
            Difficulty: 0,
            Flags: 0u,
            Generation: 6,
            IsFastWindup: false,
            IsOffensive: true,
            IsUntargeted: false,
            Speed: 0f,
            CasterEffect: 0u,
            TargetEffect: 0u,
            TargetMask: 0u,
            SpellType: 0);

    private static RetailAppraisalNameResolver Names(
        IReadOnlyDictionary<uint, string>? materials = null,
        IReadOnlyDictionary<uint, string>? creatures = null)
        => new(
            materials ?? new Dictionary<uint, string>(),
            new CreatureDisplayNameResolver(
                creatures ?? new Dictionary<uint, string>()));

    private static int Count(string source, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(
                   value,
                   start,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private static string? ResolveDatDirectory()
    {
        string? configured =
            Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured)
            && Directory.Exists(configured))
        {
            return configured;
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return Directory.Exists(fallback) ? fallback : null;
    }
}
