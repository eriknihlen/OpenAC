using System.Globalization;
using System.Text;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;

namespace AcDream.App.UI.Layout;

public static class ItemAppraisalTextFormatter
{
    private static readonly (uint Requirement, uint Stat, uint Difficulty)[]
        WieldRequirements =
        [
            (158u, 159u, 160u),
            (270u, 271u, 272u),
            (273u, 274u, 275u),
            (276u, 277u, 278u),
        ];

    public static string Build(
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal,
        Func<uint, SpellMetadata?> resolveSpell,
        RetailAppraisalNameResolver? names = null)
        => BuildReport(obj, appraisal, resolveSpell, names).ToString();

    public static ItemAppraisalReport BuildReport(
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal,
        Func<uint, SpellMetadata?> resolveSpell,
        RetailAppraisalNameResolver? names = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(resolveSpell);
        names ??= RetailAppraisalNameResolver.Empty;

        PropertyBundle properties = appraisal.Properties;
        var report = new RetailReportBuilder();

        ShowValueAndBurden(report, properties);
        ShowTinkering(report, properties);
        ShowSetAndRatings(report, properties);
        ShowWeaponAndArmor(report, obj, appraisal);
        ShowDefenseModifiers(report, appraisal);
        ShowArmorModifiers(report, appraisal);
        ShowShortMagicInfo(report, appraisal, resolveSpell);
        ShowSpecialProperties(report, properties, names);
        ShowUsage(report, properties);
        ShowLevelLimits(report, properties);
        ShowWieldRequirements(report, properties, names);
        ShowUsageLimits(report, properties);
        ShowItemLevel(report, properties);
        ShowActivationRequirements(
            report,
            properties,
            appraisal.Success,
            names);
        ShowCasterData(report, appraisal);
        ShowBoostAndHealing(report, obj, appraisal);
        ShowCapacityAndLock(report, obj, appraisal);
        ShowManaStone(report, appraisal);
        ShowRemainingUses(report, obj, appraisal);
        ShowCraftsman(report, properties);
        ShowSaleAndRareInfo(report, properties);
        ShowMagicInfo(report, appraisal, resolveSpell);
        ShowDescription(report, properties, names);

        return report.Build();
    }

    private static void ShowValueAndBurden(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        report.Line(properties.Ints.TryGetValue(19u, out int value)
            ? $"Value: {value.ToString("N0", CultureInfo.InvariantCulture)}"
            : "Value: ???");
        report.Line(properties.Ints.TryGetValue(5u, out int burden)
            ? $"Burden: {burden.ToString("N0", CultureInfo.InvariantCulture)}"
            : "Burden: Unknown");
    }

    private static void ShowTinkering(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        if (properties.Ints.TryGetValue(171u, out int tinkers))
        {
            report.Line(
                $"This item has been tinkered {tinkers.ToString(CultureInfo.InvariantCulture)} "
                + (tinkers == 1 ? "time." : "times."));
        }

        string lastTinkeredBy = properties.GetString(39u);
        if (!string.IsNullOrWhiteSpace(lastTinkeredBy))
            report.Line($"Last tinkered by {lastTinkeredBy}.");
        string imbuedBy = properties.GetString(40u);
        if (!string.IsNullOrWhiteSpace(imbuedBy))
            report.Line($"Imbued by {imbuedBy}.");

        if (properties.Ints.TryGetValue(105u, out int workmanship))
        {
            if (properties.Ints.TryGetValue(170u, out int salvagedItems)
                && salvagedItems > 0)
            {
                double average = (double)workmanship / salvagedItems;
                int workmanshipBand = Math.Clamp(
                    (int)Math.Round(average),
                    0,
                    10);
                report.Line(
                    $"Workmanship: {WorkmanshipAdjective(workmanshipBand)} "
                    + $"({average.ToString("0.00", CultureInfo.InvariantCulture)})");
                report.Paragraph(
                    $"Salvaged from {salvagedItems.ToString(CultureInfo.InvariantCulture)} items.");
            }
            else
            {
                report.Line(
                    $"Workmanship: {WorkmanshipAdjective(Math.Clamp(workmanship, 0, 10))} "
                    + $"({workmanship.ToString(CultureInfo.InvariantCulture)})");
            }
        }

        report.BlankLine();
    }

    private static void ShowSetAndRatings(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        string? setName = properties.Ints.TryGetValue(265u, out int setId)
            ? EquipmentSetName(setId)
            : null;
        bool setShown = setName is not null;
        if (setShown)
            report.Line($"Set: {setName}");

        (uint Property, string Label)[] ratingProperties =
        [
            (370u, "Dam"),
            (371u, "Dam Resist"),
            (372u, "Crit"),
            (374u, "Crit Dam"),
            (373u, "Crit Resist"),
            (375u, "Crit Dam Resist"),
            (376u, "Heal Boost"),
            (377u, "Nether Resist"),
            (378u, "Life Resist"),
        ];
        string[] ratings = ratingProperties
            .Where(pair => properties.Ints.TryGetValue(pair.Property, out int value)
                           && value > 0)
            .Select(pair =>
                $"{pair.Label} {properties.GetInt(pair.Property).ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        bool ratingsShown = ratings.Length != 0;
        if (ratingsShown)
            report.Line($"Ratings: {string.Join(", ", ratings)}");
        if (properties.GetInt(379u) is int vitality && vitality > 0)
        {
            report.Line(
                $"This item adds {vitality.ToString(CultureInfo.InvariantCulture)} Vitality.");
            ratingsShown = true;
        }

        if (ratingsShown)
            report.BlankLine();
        if (setShown || ratingsShown)
            report.BlankLine();
    }

    private static void ShowWeaponAndArmor(
        RetailReportBuilder report,
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal)
    {
        PropertyBundle properties = appraisal.Properties;
        uint validLocations = obj.IsHook && appraisal.HookProfile is { } hook
            ? hook.ValidLocations
            : (uint)obj.ValidLocations;
        uint ammoType = obj.IsHook && appraisal.HookProfile is { } hookProfile
            ? hookProfile.AmmoType
            : obj.AmmoType ?? 0u;
        const uint weaponAndShieldLocations = 0x03F0_0000u;
        bool hasWeaponOrShieldLocation =
            (validLocations & weaponAndShieldLocations) != 0u;

        if (hasWeaponOrShieldLocation
            && (validLocations & (uint)EquipMask.Shield) != 0)
        {
            if (properties.Ints.TryGetValue(28u, out int shieldLevel))
                report.Line(
                    $"Base Shield Level: {shieldLevel.ToString(CultureInfo.InvariantCulture)}",
                    EnchantmentStyle(
                        appraisal.ArmorEnchantments,
                        0x0001u));
            else
                report.Line("Shield Level: Unknown");
        }

        if (hasWeaponOrShieldLocation
            && appraisal.WeaponProfile is { } weapon)
        {
            string skill = SkillName((int)weapon.WeaponSkill);
            int weaponType = properties.GetInt(353u);
            report.Line(
                $"Skill: {skill}{WeaponSubtype(weaponType)}");

            bool launcher = (validLocations & (uint)EquipMask.MissileWeapon) != 0
                            && ammoType != 0u;
            string damageLabel = launcher ? "Damage Bonus" : "Damage";
            string damage;
            if (weapon.Damage == uint.MaxValue)
            {
                damage = "Unknown";
            }
            else
            {
                double minimumDamage =
                    (1d - weapon.DamageVariance) * weapon.Damage;
                damage = weapon.Damage - minimumDamage > 0.0002d
                    ? $"{FormatRetailDamage(minimumDamage)}"
                      + $" - {weapon.Damage.ToString(CultureInfo.InvariantCulture)}"
                    : weapon.Damage.ToString(CultureInfo.InvariantCulture);
                if (!launcher)
                {
                    damage += TryDamageTypeName(weapon.DamageType, out string? type)
                        ? $", {type}"
                        : ", unknown type";
                }
            }
            ItemAppraisalFontStyle damageStyle = EnchantmentStyle(
                appraisal.WeaponEnchantments,
                0x0008u);
            if (damageStyle == ItemAppraisalFontStyle.Normal)
            {
                damageStyle = EnchantmentStyle(
                    appraisal.WeaponEnchantments,
                    0x0010u);
            }
            report.Line($"{damageLabel}: {damage}", damageStyle);

            int elementalBonus = properties.GetInt(204u);
            if (elementalBonus > 0)
                report.Line(
                    $"Elemental Damage Bonus: "
                    + $"{elementalBonus.ToString(CultureInfo.InvariantCulture)}, "
                    + $"{DamageTypeName(weapon.DamageType)}.");

            if (launcher)
            {
                report.Line(
                    appraisal.Success
                        ? $"Damage Modifier: {FormatModifier(weapon.DamageMod)}."
                        : "Damage Modifier: Unknown",
                    EnchantmentStyle(
                        appraisal.WeaponEnchantments,
                        0x0020u));
            }

            const uint timedWeaponLocations =
                (uint)(EquipMask.MeleeWeapon
                       | EquipMask.MissileWeapon
                       | EquipMask.TwoHanded);
            if ((validLocations & timedWeaponLocations) != 0)
            {
                report.Line(
                    weapon.WeaponTime == uint.MaxValue
                        ? "Speed:  Unknown"
                        : $"Speed: {WeaponTimeName((int)weapon.WeaponTime)} "
                          + $"({weapon.WeaponTime.ToString(CultureInfo.InvariantCulture)})",
                    EnchantmentStyle(
                        appraisal.WeaponEnchantments,
                        0x0004u));
            }

            if (launcher)
            {
                if (!appraisal.Success)
                {
                    report.Line("Range:  Unknown");
                }
                else
                {
                    double rawRange = Math.Min(
                        85d,
                        2d * Math.Pow(weapon.MaxVelocity, 2d)
                        * (1d / 9.8d)
                        * 1.094d);
                    int range = rawRange < 10d
                        ? (int)Math.Ceiling(rawRange)
                        : (int)rawRange - (int)rawRange % 5;
                    report.Line(
                        $"Range: {range.ToString(CultureInfo.InvariantCulture)} yds."
                        + (weapon.MaxVelocityEstimated != 0u
                            ? " (based on STRENGTH 100)"
                            : string.Empty));
                }
            }

            if (!launcher && Math.Abs(weapon.WeaponOffense - 1d) > 0.000001d)
                report.Line(
                    $"Bonus to Attack Skill: "
                    + $"{FormatSignedPercent(weapon.WeaponOffense - 1d)}.",
                    EnchantmentStyle(
                        appraisal.WeaponEnchantments,
                        0x0001u));
        }

        ShowAmmunitionDescription(report, validLocations, ammoType);

        if (!hasWeaponOrShieldLocation
            && (validLocations & 0x0800_7FFFu) != 0u
            && ClothingCoverage(obj.Priority) is { Length: > 0 } coverage)
        {
            report.Line($"Covers {coverage}");
        }
    }

    private static void ShowArmorModifiers(
        RetailReportBuilder report,
        AppraiseInfoParser.Parsed appraisal)
    {
        if (appraisal.ArmorProfile is not { } armor
            || !appraisal.Properties.Ints.TryGetValue(28u, out int armorLevel)
            || armorLevel <= 0)
        {
            return;
        }

        report.Paragraph(
            $"Armor Level: {armorLevel.ToString(CultureInfo.InvariantCulture)}",
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0001u));
        ShowProtection(
            report, "Slashing", armorLevel, armor.SlashingProtection,
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0002u));
        ShowProtection(
            report, "Piercing", armorLevel, armor.PiercingProtection,
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0004u));
        ShowProtection(
            report, "Bludgeoning", armorLevel, armor.BludgeoningProtection,
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0008u));
        ShowProtection(
            report, "Fire", armorLevel, armor.FireProtection,
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0020u));
        ShowProtection(
            report, "Cold", armorLevel, armor.ColdProtection,
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0010u));
        ShowProtection(
            report, "Acid", armorLevel, armor.AcidProtection,
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0040u));
        ShowProtection(
            report, "Electric", armorLevel, armor.LightningProtection,
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0080u));
        ShowProtection(
            report, "Nether", armorLevel, armor.NetherProtection,
            EnchantmentStyle(appraisal.ArmorEnchantments, 0x0100u));
    }

    private static void ShowAmmunitionDescription(
        RetailReportBuilder report,
        uint validLocations,
        uint ammoType)
    {
        if (ammoType == 0u)
            return;

        AmmoType baseAmmoType = (AmmoType)ammoType switch
        {
            AmmoType.ArrowCrystal or AmmoType.ArrowChorizite => AmmoType.Arrow,
            AmmoType.BoltCrystal or AmmoType.BoltChorizite => AmmoType.Bolt,
            AmmoType.AtlatlCrystal or AmmoType.AtlatlChorizite => AmmoType.Atlatl,
            var other => other,
        };
        bool launcher = (validLocations & (uint)EquipMask.MissileWeapon) != 0;
        string? description = (launcher, baseAmmoType) switch
        {
            (true, AmmoType.Arrow) => "Uses arrows as ammunition.",
            (true, AmmoType.Bolt) => "Uses quarrels as ammunition.",
            (true, AmmoType.Atlatl) => "Uses atlatl darts as ammunition.",
            (false, AmmoType.Arrow) => "Used as ammunition by bows.",
            (false, AmmoType.Bolt) => "Used as ammunition by crossbows.",
            (false, AmmoType.Atlatl) => "Used as ammunition by atlatls.",
            _ => null,
        };
        if (description is not null)
            report.Line(description);
    }

    private static void ShowProtection(
        RetailReportBuilder report,
        string damageType,
        int armorLevel,
        float modifier,
        ItemAppraisalFontStyle style)
    {
        string quality = modifier switch
        {
            <= 0.0002f => "None",
            < 0.4f => "Poor",
            < 0.8f => "Below Average",
            < 1.2f => "Average",
            < 1.6f => "Above Average",
            < 2.0f => "Excellent",
            _ => "Unparalleled",
        };
        double effective = armorLevel * modifier;
        report.Line(
            $"{damageType}: {quality}  "
            + $"({effective.ToString("0", CultureInfo.InvariantCulture)})",
            style);
    }

    private static void ShowDefenseModifiers(
        RetailReportBuilder report,
        AppraiseInfoParser.Parsed appraisal)
    {
        PropertyBundle properties = appraisal.Properties;
        ShowModifier(
            report,
            properties,
            29u,
            "Bonus to Melee Defense",
            EnchantmentStyle(appraisal.WeaponEnchantments, 0x0002u));
        ShowModifier(report, properties, 149u, "Bonus to Missile Defense");
        ShowModifier(report, properties, 150u, "Bonus to Magic Defense");
    }

    private static void ShowModifier(
        RetailReportBuilder report,
        PropertyBundle properties,
        uint property,
        string label,
        ItemAppraisalFontStyle style = ItemAppraisalFontStyle.Normal)
    {
        if (!properties.Floats.TryGetValue(property, out double modifier)
            || Math.Abs(modifier - 1d) <= 0.000001d)
            return;
        report.Line(
            $"{label}: {FormatSignedPercent(modifier - 1d)}.",
            style);
    }

    private static void ShowShortMagicInfo(
        RetailReportBuilder report,
        AppraiseInfoParser.Parsed appraisal,
        Func<uint, SpellMetadata?> resolveSpell)
    {
        if (appraisal.SpellBook.Length == 0)
            return;
        if (!appraisal.Success)
        {
            report.Paragraph("Spells: unknown.");
            return;
        }

        uint[] ordinary = appraisal.SpellBook
            .Where(raw => (raw & 0x8000_0000u) == 0u)
            .ToArray();
        if (ordinary.Length == 0)
            return;

        string names = string.Join(
            ", ",
            ordinary.Select(raw =>
            {
                return resolveSpell(raw)?.Name
                       ?? $"Spell {raw.ToString(CultureInfo.InvariantCulture)}";
            }));
        report.Paragraph($"Spells: {names}");
    }

    private static void ShowSpecialProperties(
        RetailReportBuilder report,
        PropertyBundle properties,
        RetailAppraisalNameResolver names)
    {
        report.BlankLine();

        int carryLimit = properties.GetInt(279u);
        if (carryLimit > 0)
        {
            report.BlankLine();
            report.Line(
                $"You can only carry "
                + $"{carryLimit.ToString("N0", CultureInfo.InvariantCulture)} "
                + "of these items.");
        }

        if (properties.Floats.TryGetValue(167u, out double cooldown)
            && cooldown > 0d)
        {
            report.Line($"Cooldown When Used: {FormatDeltaTime(cooldown)}");
            if (properties.Ints.ContainsKey(280u))
                report.BlankLine();
        }

        int cleave = properties.GetInt(292u);
        if (cleave > 1)
        {
            report.Line(
                $"Cleave: {cleave.ToString(CultureInfo.InvariantCulture)} enemies in front arc.");
            report.BlankLine();
        }

        var special = new List<string>();
        int slayer = properties.GetInt(166u);
        if (slayer != 0)
        {
            string slayerName = slayer == 31
                ? "Bael'Zharon's Hate"
                : names.ResolveCreature(slayer);
            if (!string.IsNullOrWhiteSpace(slayerName))
            {
                special.Add(slayer == 31
                    ? slayerName
                    : $"{slayerName} slayer");
            }
        }
        if ((properties.GetInt(47u) & 0x79E0) != 0)
            special.Add("Multi-Strike");

        uint imbuedEffects = unchecked((uint)(
            properties.GetInt(179u)
            | properties.GetInt(303u)
            | properties.GetInt(304u)
            | properties.GetInt(305u)
            | properties.GetInt(306u)));
        AppendImbuedEffects(special, imbuedEffects);

        if (properties.Floats.ContainsKey(159u))
            special.Add("Magic Absorbing");
        if (properties.GetInt(36u) >= 9_999)
            special.Add("Unenchantable");

        int attuned = properties.GetInt(114u);
        if (attuned is 1 or 2)
            special.Add("Attuned");
        int bonded = properties.GetInt(33u);
        switch (bonded)
        {
            case -2:
                special.Add("Destroyed on Death");
                break;
            case -1:
                special.Add("Dropped on Death");
                break;
            case 1:
                special.Add("Bonded");
                break;
        }

        if (properties.GetBool(91u))
            special.Add("Retained");
        if (properties.Floats.ContainsKey(136u))
            special.Add("Crushing Blow");
        if (properties.Floats.ContainsKey(147u))
            special.Add("Biting Strike");
        if (properties.Floats.ContainsKey(155u))
            special.Add("Armor Cleaving");
        if (properties.Floats.ContainsKey(157u)
            && properties.Ints.TryGetValue(263u, out int resistanceType))
            special.Add(
                $"Resistance Cleaving: {DamageTypeName((uint)resistanceType)}");
        if (properties.DataIds.ContainsKey(55u))
            special.Add("Cast on Strike");
        if (properties.GetBool(99u))
            special.Add("Ivoryable");
        if (properties.GetBool(100u))
            special.Add("Dyeable");

        if (special.Count != 0)
            report.Line($"Properties: {string.Join(", ", special)}");
        if (imbuedEffects != 0u)
            report.Line("This item cannot be further imbued.");
        if (properties.GetBool(130u))
            report.Line("This item is tethered to the left side.");
    }

    private static void AppendImbuedEffects(
        List<string> properties,
        uint imbuedEffects)
    {
        (uint Flag, string Text)[] names =
        [
            (0x0000_0001u, "Critical Strike"),
            (0x0000_0002u, "Crippling Blow"),
            (0x0000_0004u, "Armor Rending"),
            (0x0000_0008u, "Slash Rending"),
            (0x0000_0010u, "Pierce Rending"),
            (0x0000_0020u, "Bludgeon Rending"),
            (0x0000_0040u, "Acid Rending"),
            (0x0000_0080u, "Cold Rending"),
            (0x0000_0100u, "Lightning Rending"),
            (0x0000_0200u, "Fire Rending"),
            (0x0000_0400u, "+1 Melee Defense"),
            (0x0000_0800u, "+1 Missile Defense"),
            (0x0000_1000u, "+1 Magic Defense"),
            (0x0000_4000u, "Nether Rending"),
            (0x8000_0000u, "Phantasmal"),
        ];
        foreach ((uint flag, string text) in names)
            if ((imbuedEffects & flag) != 0)
                properties.Add(text);
    }

    private static void ShowUsage(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        string use = properties.GetString(14u);
        if (!string.IsNullOrWhiteSpace(use))
            report.Paragraph(use);
    }

    private static void ShowLevelLimits(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        int minimum = properties.GetInt(86u);
        int maximum = properties.GetInt(87u);
        if (minimum > 0 && maximum > 0)
        {
            report.Paragraph(minimum == maximum
                ? $"Restricted to characters of Level "
                  + $"{minimum.ToString(CultureInfo.InvariantCulture)}."
                : $"Restricted to characters of Levels "
                  + $"{minimum.ToString(CultureInfo.InvariantCulture)} to "
                  + $"{maximum.ToString(CultureInfo.InvariantCulture)}.");
        }
        else if (minimum > 0)
            report.Paragraph(
                $"Restricted to characters of Level "
                + $"{minimum.ToString(CultureInfo.InvariantCulture)} or greater.");
        else if (maximum > 0)
            report.Paragraph(
                $"Restricted to characters of Level "
                + $"{maximum.ToString(CultureInfo.InvariantCulture)} or below.");

        string destination = properties.GetString(38u);
        if (!string.IsNullOrWhiteSpace(destination))
            report.Paragraph($"Destination: {destination}");
    }

    private static void ShowWieldRequirements(
        RetailReportBuilder report,
        PropertyBundle properties,
        RetailAppraisalNameResolver names)
    {
        if (properties.GetBool(85u))
        {
            string owner = properties.GetString(25u);
            report.Line(
                $"Wield requires "
                + $"{(string.IsNullOrWhiteSpace(owner) ? "the original owner" : owner)}");
        }
        if (properties.GetInt(26u) == 1)
            report.Line("Use requires Throne of Destiny.");

        if (properties.Ints.TryGetValue(324u, out int heritage))
        {
            string heritageName = names.ResolveHeritage(heritage);
            if (!string.IsNullOrEmpty(heritageName))
                report.Line($"Wield requires {heritageName}");
        }

        for (int index = 0; index < WieldRequirements.Length; index++)
        {
            (uint requirementId, uint statId, uint difficultyId) =
                WieldRequirements[index];
            if (!properties.Ints.TryGetValue(requirementId, out int requirement)
                || !properties.Ints.TryGetValue(statId, out int stat)
                || !properties.Ints.TryGetValue(difficultyId, out int difficulty))
                continue;

            string quality = RequirementQuality(
                requirement,
                stat,
                difficulty,
                names);
            if (requirement == 8)
            {
                string training = difficulty == 3
                    ? index == 3 ? "Specialized" : "specialized"
                    : index == 3 ? "Trained" : "trained";
                report.Line($"Wield requires {training} {quality}");
            }
            else if (requirement == 11)
            {
                report.Line($"Wield requires {quality} type");
            }
            else if (requirement == 12)
            {
                report.Line($"Wield requires {quality} race");
            }
            else if (!string.IsNullOrEmpty(quality))
            {
                report.Line(
                    $"Wield requires {quality} "
                    + $"{difficulty.ToString(CultureInfo.InvariantCulture)}");
            }
        }
    }

    private static string RequirementQuality(
        int requirement,
        int stat,
        int difficulty,
        RetailAppraisalNameResolver names)
    {
        string basePrefix = requirement is 2 or 4 or 6 ? "base " : string.Empty;
        return requirement switch
        {
            1 or 2 or 8 => basePrefix + SkillName(stat),
            3 or 4 => basePrefix + PrimaryAttributeName(stat),
            5 or 6 => basePrefix + SecondaryAttributeName(stat),
            7 => "level",
            9 or 10 => stat switch
            {
                0x11F => "Standing with the Celestial Hand",
                0x120 => "Standing with the Eldrytch Web",
                0x121 => "Standing with the Radiant Blood",
                _ => "unknown quality",
            },
            11 => names.ResolveCreature(difficulty),
            12 => names.ResolveHeritage(difficulty),
            _ => string.Empty,
        };
    }

    private static void ShowUsageLimits(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        int level = properties.GetInt(369u);
        int skill = properties.GetInt(366u);
        int difficulty = properties.GetInt(367u);
        int specializedSkill = properties.GetInt(368u);
        if (level > 0
            || (skill > 0 && difficulty > 0)
            || specializedSkill > 0)
        {
            report.BlankLine();
        }

        if (level > 0)
            report.Line(
                $"Use requires level {level.ToString(CultureInfo.InvariantCulture)}.");
        if (skill > 0 && difficulty > 0)
            report.Line(
                $"Use requires {UsageSkillName(skill)} of at least "
                + $"{difficulty.ToString(CultureInfo.InvariantCulture)}.");
        if (specializedSkill > 0)
            report.Line(
                $"Use requires specialized {UsageSkillName(specializedSkill)}.");
    }

    private static void ShowItemLevel(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        if (!properties.Int64s.TryGetValue(5u, out long baseExperience)
            || baseExperience <= 0
            || !properties.Ints.TryGetValue(319u, out int maximum)
            || maximum <= 0
            || !properties.Ints.TryGetValue(320u, out int style)
            || style <= 0)
        {
            return;
        }

        long experience = Math.Max(0, properties.GetInt64(4u));
        int currentLevel = ItemTotalXpToLevel(
            experience,
            baseExperience,
            maximum,
            style);
        int displayedLevel = Math.Min(currentLevel + 1, maximum);
        long nextExperience = ItemLevelToTotalXp(
            Math.Min(currentLevel + 1, maximum),
            baseExperience,
            maximum,
            style);

        report.Line(
            $"Item Level: {displayedLevel.ToString(CultureInfo.InvariantCulture)} / "
            + $"{maximum.ToString(CultureInfo.InvariantCulture)}");
        report.Line(
            $"Item XP: {experience.ToString("N0", CultureInfo.InvariantCulture)} / "
            + $"{nextExperience.ToString("N0", CultureInfo.InvariantCulture)}");
        report.BlankLine();

        if (properties.GetInt(352u) == 2)
        {
            report.Line(
                "This cloak has a chance to reduce an incoming attack by 200 damage.");
            report.BlankLine();
        }
    }

    private static void ShowActivationRequirements(
        RetailReportBuilder report,
        PropertyBundle properties,
        bool appraisalSucceeded,
        RetailAppraisalNameResolver names)
    {
        if (!appraisalSucceeded)
            return;

        var requirements = new List<string>();
        AddRequirement(requirements, "Arcane Lore", properties.GetInt(109u));
        AddRequirement(requirements, "Allegiance Rank", properties.GetInt(110u));

        int heritage = properties.GetInt(188u);
        if (heritage != 0
            && names.ResolveHeritage(heritage) is { Length: > 0 } heritageName)
        {
            requirements.Add(heritageName);
        }

        int skillLevel = properties.GetInt(115u);
        int skill = properties.GetInt(176u);
        if (skillLevel > 0 && skill > 0)
            requirements.Add(
                $"{UsageSkillName(skill)}: {skillLevel.ToString(CultureInfo.InvariantCulture)}");
        int attributeLevel = properties.GetInt(258u);
        int attribute = properties.GetInt(257u);
        if (attributeLevel > 0 && attribute > 0)
            requirements.Add(
                $"{PrimaryAttributeName(attribute)}: "
                + $"{attributeLevel.ToString(CultureInfo.InvariantCulture)}");
        int secondaryLevel = properties.GetInt(260u);
        int secondary = properties.GetInt(259u);
        if (secondaryLevel > 0 && secondary > 0)
            requirements.Add(
                $"{SecondaryAttributeName(secondary)}: "
                + $"{secondaryLevel.ToString(CultureInfo.InvariantCulture)}");

        if (requirements.Count != 0)
            report.Line($"Activation requires {string.Join(", ", requirements)}");
        if (properties.GetBool(94u))
        {
            string owner = properties.GetString(25u);
            report.Line(
                $"This item can only be activated by "
                + $"{(string.IsNullOrWhiteSpace(owner) ? "the original owner" : owner)}.");
        }
    }

    private static void AddRequirement(
        List<string> requirements,
        string name,
        int value)
    {
        if (value > 0)
            requirements.Add(
                $"{name}: {value.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void ShowCasterData(
        RetailReportBuilder report,
        AppraiseInfoParser.Parsed appraisal)
    {
        PropertyBundle properties = appraisal.Properties;
        if (properties.Floats.TryGetValue(144u, out double manaConversion))
            report.Paragraph(
                $"Bonus to Mana Conversion: "
                + $"{FormatSignedPercent(manaConversion)}.",
                EnchantmentStyle(
                    appraisal.ResistEnchantments,
                    0x1000u));

        if (properties.Floats.TryGetValue(152u, out double elemental)
            && properties.Ints.TryGetValue(45u, out int damageType))
        {
            report.Paragraph(
                $"Damage bonus for {DamageTypeName((uint)damageType)} spells:",
                EnchantmentStyle(
                    appraisal.ResistEnchantments,
                    0x2000u));
            report.Line($" vs. Monsters: {FormatSignedPercent(elemental - 1d)}.");
            double playerModifier = 1d + (elemental - 1d) * 0.25d;
            report.Line(
                $" vs. Players: {FormatSignedPercent(playerModifier - 1d)}.");
        }
    }

    private static void ShowBoostAndHealing(
        RetailReportBuilder report,
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal)
    {
        PropertyBundle properties = appraisal.Properties;
        bool healer =
            ((PublicWeenieFlags)obj.PublicWeenieBitfield.GetValueOrDefault()
             & PublicWeenieFlags.Healer) != 0
            || obj.IsHook
            && appraisal.HookProfile is { } hook
            && (hook.Flags & 0x2u) != 0u;
        int boost = properties.GetInt(90u);
        string? boostText = properties.GetInt(89u) switch
        {
            2 => $"{(boost >= 0 ? "Restores" : "Depletes")} "
                 + $"{Math.Abs(boost).ToString(CultureInfo.InvariantCulture)} "
                 + "Health when used.",
            4 => $"{(boost >= 0 ? "Restores" : "Depletes")} "
                 + $"{Math.Abs(boost).ToString(CultureInfo.InvariantCulture)} "
                 + "Stamina when consumed.",
            6 => $"{(boost >= 0 ? "Restores" : "Depletes")} "
                 + $"{Math.Abs(boost).ToString(CultureInfo.InvariantCulture)} "
                 + "Mana when used.",
            _ => null,
        };
        if (!healer && boostText is not null
            && properties.Ints.ContainsKey(90u))
        {
            report.Paragraph(boostText);
        }

        if (!healer)
            return;

        if (properties.Ints.TryGetValue(90u, out int healingBonus))
            report.Paragraph(
                $"Bonus to Healing Skill: {healingBonus.ToString(CultureInfo.InvariantCulture)}");
        if (properties.Floats.TryGetValue(100u, out double healKitModifier))
            report.Line(
                $"Restoration Bonus: "
                + $"{(healKitModifier * 100d).ToString("0", CultureInfo.InvariantCulture)}%");
    }

    private static void ShowCapacityAndLock(
        RetailReportBuilder report,
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal)
    {
        PropertyBundle properties = appraisal.Properties;
        if (!obj.IsHook || appraisal.HookProfile is null)
        {
            if (obj.ItemsCapacity > 0 && obj.ContainersCapacity > 0)
                report.Paragraph(
                    $"Can hold up to {obj.ItemsCapacity.ToString(CultureInfo.InvariantCulture)} "
                    + $"items and {obj.ContainersCapacity.ToString(CultureInfo.InvariantCulture)} containers.");
            else if (obj.ItemsCapacity > 0)
                report.Paragraph(
                    $"Can hold up to {obj.ItemsCapacity.ToString(CultureInfo.InvariantCulture)} items.");
            else if (obj.ContainersCapacity > 0)
                report.Paragraph(
                    $"Can hold up to {obj.ContainersCapacity.ToString(CultureInfo.InvariantCulture)} containers.");

            int pages = properties.GetInt(175u);
            int pagesUsed = properties.GetInt(174u);
            if (pages > 0)
                report.Paragraph(
                    $"{pagesUsed.ToString(CultureInfo.InvariantCulture)} of "
                    + $"{pages.ToString(CultureInfo.InvariantCulture)} pages full.");
        }

        if (obj.IsHook)
            return;

        bool hasLocked = properties.Bools.TryGetValue(3u, out bool locked);
        bool hasResistance = properties.Ints.TryGetValue(
            38u,
            out int resistance);
        if (!hasLocked)
        {
            if (hasResistance && resistance != 0)
            {
                report.Paragraph(
                    $"Bonus to Lockpick Skill: "
                    + $"{resistance.ToString("+0;-0;0", CultureInfo.InvariantCulture)}");
            }
            return;
        }

        if (!locked)
        {
            report.Paragraph("Unlocked");
            return;
        }

        report.Paragraph("Locked");
        if (!hasResistance)
        {
            report.Paragraph("You can't tell how hard the lock is to pick.");
            return;
        }

        if (properties.Ints.TryGetValue(173u, out int chance)
            && LockpickDifficulty(chance) is { } difficulty)
        {
            report.Paragraph(
                $"The lock looks {difficulty} to pick "
                + $"(Resistance {resistance.ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    private static void ShowManaStone(
        RetailReportBuilder report,
        AppraiseInfoParser.Parsed appraisal)
    {
        if (appraisal.SpellBook.Length != 0)
            return;
        PropertyBundle properties = appraisal.Properties;
        if (properties.Ints.TryGetValue(107u, out int storedMana))
            report.Line(
                $"Stored Mana: {storedMana.ToString(CultureInfo.InvariantCulture)}");
        if (properties.Floats.TryGetValue(87u, out double efficiency))
            report.Line(
                $"Efficiency: {(efficiency * 100d).ToString("0", CultureInfo.InvariantCulture)}%");
        if (properties.Floats.TryGetValue(137u, out double destruction))
            report.Line(
                $"Chance of Destruction: "
                + $"{(destruction * 100d).ToString("0", CultureInfo.InvariantCulture)}%");
    }

    private static void ShowRemainingUses(
        RetailReportBuilder report,
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal)
    {
        PropertyBundle properties = appraisal.Properties;
        if (properties.Ints.TryGetValue(193u, out int keys))
            report.Line(
                $"Contains {keys.ToString(CultureInfo.InvariantCulture)} "
                + (keys == 1 ? "key." : "keys."));

        if (properties.GetBool(63u))
        {
            report.Line("Number of uses remaining:  Unlimited");
            return;
        }

        if (properties.Ints.TryGetValue(92u, out int uses))
        {
            report.Line(
                $"Number of uses remaining: {uses.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        var publicFlags =
            (PublicWeenieFlags)obj.PublicWeenieBitfield.GetValueOrDefault();
        if (!appraisal.Success
            && ((publicFlags
                 & (PublicWeenieFlags.Healer | PublicWeenieFlags.Lockpick)) != 0
                || appraisal.HookProfile is { Flags: var flags }
                && (flags & 0xAu) != 0))
        {
            report.Paragraph("Number of uses remaining:  Unknown");
        }
    }

    private static void ShowCraftsman(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        string craftsman = properties.GetString(25u);
        if (!string.IsNullOrWhiteSpace(craftsman)
            && !properties.GetBool(85u)
            && !properties.GetBool(94u))
            report.Line($"Created by {craftsman}.");
    }

    private static void ShowSaleAndRareInfo(
        RetailReportBuilder report,
        PropertyBundle properties)
    {
        if (properties.Bools.TryGetValue(69u, out bool sellable) && !sellable)
            report.Line("This item cannot be sold.");
        if (properties.GetBool(108u))
        {
            report.Paragraph(
                "This rare item has a timer restriction of 3 minutes. "
                + "You will not be able to use another rare item with a timer "
                + "within 3 minutes of using this one.");
        }
        int rare = properties.GetInt(17u);
        if (rare > 0)
            report.Paragraph($"Rare #{rare.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void ShowMagicInfo(
        RetailReportBuilder report,
        AppraiseInfoParser.Parsed appraisal,
        Func<uint, SpellMetadata?> resolveSpell)
    {
        if (appraisal.SpellBook.Length == 0)
            return;
        if (!appraisal.Success)
        {
            report.Paragraph("Spells: unknown.");
            return;
        }

        var ordinary = new List<(uint Id, SpellMetadata? Metadata)>();
        var enchantments = new List<(uint Id, SpellMetadata? Metadata)>();
        foreach (uint rawSpellId in appraisal.SpellBook)
        {
            uint spellId = rawSpellId & 0x7FFF_FFFFu;
            var spell = (spellId, resolveSpell(spellId));
            if ((rawSpellId & 0x8000_0000u) == 0)
                ordinary.Add(spell);
            else
                enchantments.Add(spell);
        }

        PropertyBundle properties = appraisal.Properties;
        if (ordinary.Count != 0)
        {
            if (properties.Ints.TryGetValue(106u, out int spellcraft))
                report.Line(
                    $"Spellcraft: {spellcraft.ToString(CultureInfo.InvariantCulture)}.");
            if (properties.Ints.TryGetValue(107u, out int currentMana)
                && properties.Ints.TryGetValue(108u, out int maximumMana))
                report.Line(
                    $"Mana: {currentMana.ToString(CultureInfo.InvariantCulture)} / "
                    + $"{maximumMana.ToString(CultureInfo.InvariantCulture)}.");

            if (properties.Floats.TryGetValue(5u, out double manaRate)
                && Math.Abs(manaRate) > 0.000001d)
            {
                int seconds = (int)Math.Round(1d / manaRate);
                report.Line(
                    $"Mana Cost: 1 point per {seconds.ToString(CultureInfo.InvariantCulture)} "
                    + (seconds == 1 ? "second." : "seconds."));
            }
            else if (properties.Ints.TryGetValue(117u, out int manaCost))
            {
                string manaCostText =
                    $"Mana Cost: {manaCost.ToString(CultureInfo.InvariantCulture)}.";
                if (manaCost > 0)
                {
                    manaCostText +=
                        "\n(Can be reduced by the Mana Conversion skill).";
                }
                report.Paragraph(manaCostText);
            }

            report.Paragraph(BuildSpellDescriptionBlock(
                "Spell Descriptions:",
                ordinary));
        }

        if (enchantments.Count != 0)
        {
            report.Paragraph(BuildSpellDescriptionBlock(
                "Enchantments:",
                enchantments));
        }
    }

    private static string BuildSpellDescriptionBlock(
        string heading,
        IReadOnlyList<(uint Id, SpellMetadata? Metadata)> spells)
    {
        var text = new StringBuilder(heading);
        foreach ((uint id, SpellMetadata? metadata) in spells)
        {
            string name = !string.IsNullOrWhiteSpace(metadata?.Name)
                ? metadata.Name
                : $"Spell {id.ToString(CultureInfo.InvariantCulture)}";
            text.Append("\n~ ").Append(name).Append(": ");
            if (!string.IsNullOrWhiteSpace(metadata?.Description))
                text.Append(metadata.Description);
        }
        return text.ToString();
    }

    private static void ShowDescription(
        RetailReportBuilder report,
        PropertyBundle properties,
        RetailAppraisalNameResolver names)
    {
        if (properties.Ints.ContainsKey(267u)
            && properties.Ints.ContainsKey(98u)
            && properties.Ints.TryGetValue(268u, out int remainingLifetime))
        {
            report.Line(remainingLifetime < 0
                ? "This item is in the act of disintegrating."
                : $"This item expires in {FormatExpiry(remainingLifetime)}");
        }

        string longDescription = properties.GetString(16u);
        if (string.IsNullOrWhiteSpace(longDescription))
        {
            string shortDescription = properties.GetString(15u);
            if (!string.IsNullOrWhiteSpace(shortDescription))
                report.Paragraph(shortDescription);
        }
        else
        {
            string description = properties.GetString(52u);
            if (string.IsNullOrWhiteSpace(description))
                description = longDescription;

            string prefix = string.Empty;
            string suffix = string.Empty;
            if (properties.Ints.TryGetValue(172u, out int decorations))
            {
                if ((decorations & 1) != 0
                    && properties.Ints.TryGetValue(105u, out int workmanship))
                {
                    prefix += WorkmanshipAdjective(
                        Math.Clamp(workmanship, 0, 10)) + " ";
                }

                int materialType = properties.GetInt(131u);
                string material = names.ResolveMaterial(materialType);
                if (!string.IsNullOrWhiteSpace(material))
                {
                    prefix += material + " ";
                    description = RemoveFirst(description, material).Trim();
                }

                if ((decorations & 4) != 0
                    && properties.Ints.TryGetValue(177u, out int gemCount)
                    && properties.Ints.TryGetValue(178u, out int gemMaterial))
                {
                    string gemName = gemCount == 1
                        ? names.ResolveMaterial(gemMaterial)
                        : PluralizedGemName(
                            gemMaterial,
                            names.ResolveMaterial(gemMaterial));
                    if (!string.IsNullOrWhiteSpace(gemName))
                    {
                        suffix =
                            $", set with {gemCount.ToString(CultureInfo.InvariantCulture)} "
                            + gemName;
                    }
                }
            }

            report.Paragraph(prefix + description + suffix);
        }

        int portalRestrictions = properties.GetInt(111u);
        if (portalRestrictions != 0)
        {
            var restrictions = new List<string>();
            if ((portalRestrictions & 2) != 0)
                restrictions.Add("Player Killers may not use this portal.");
            if ((portalRestrictions & 4) != 0)
                restrictions.Add("Lite Player Killers may not use this portal.");
            if ((portalRestrictions & 8) != 0)
                restrictions.Add("Non-Player Killers may not use this portal.");
            if ((portalRestrictions & 0x20) != 0)
                restrictions.Add("This portal cannot be recalled nor linked to.");
            if ((portalRestrictions & 0x10) != 0)
                restrictions.Add("This portal cannot be summoned.");
            if (restrictions.Count != 0)
                report.Paragraph(string.Join('\n', restrictions));
        }
    }

    private static string FormatRetailDamage(double damage)
        => damage.ToString(
            damage > 10d ? "G4" : "G3",
            CultureInfo.InvariantCulture);

    private static bool TryDamageTypeName(uint type, out string? name)
    {
        name = type switch
        {
            1u => "Slashing",
            2u => "Piercing",
            4u => "Bludgeoning",
            8u => "Cold",
            16u => "Fire",
            32u => "Acid",
            64u => "Electric",
            128u => "Health",
            256u => "Stamina",
            512u => "Mana",
            1024u => "Nether",
            _ => null,
        };
        return name is not null;
    }

    private static string ClothingCoverage(uint priority)
    {
        var coverage = new List<string>();
        if ((priority & 0x4000u) != 0u)
            coverage.Add("Head");
        if ((priority & (0x0008u | 0x0400u)) != 0u)
            coverage.Add("Chest");
        if ((priority & (0x0010u | 0x0800u)) != 0u)
            coverage.Add("Abdomen");
        if ((priority & (0x0020u | 0x1000u)) != 0u)
            coverage.Add("Upper Arms");
        if ((priority & (0x0040u | 0x2000u)) != 0u)
            coverage.Add("Lower Arms");
        if ((priority & 0x0080u) != 0u)
            coverage.Add("Hands");
        if ((priority & (0x0002u | 0x0100u)) != 0u)
            coverage.Add("Upper Legs");
        if ((priority & (0x0004u | 0x0200u)) != 0u)
            coverage.Add("Lower Legs");
        if ((priority & 0x10000u) != 0u)
            coverage.Add("Feet");
        return string.Join(", ", coverage);
    }

    private static long ItemLevelToTotalXp(
        int level,
        long baseExperience,
        int maximumLevel,
        int style)
    {
        int cappedLevel = Math.Clamp(level, 0, maximumLevel);
        return style switch
        {
            1 => baseExperience * cappedLevel,
            2 => SumGeometricExperience(baseExperience, cappedLevel),
            3 => baseExperience * cappedLevel * (cappedLevel + 1L) / 2L,
            _ => 0L,
        };
    }

    private static int ItemTotalXpToLevel(
        long totalExperience,
        long baseExperience,
        int maximumLevel,
        int style)
    {
        if (totalExperience <= 0 || baseExperience <= 0 || maximumLevel <= 0)
            return 0;
        if (style == 1)
        {
            return (int)Math.Min(
                maximumLevel,
                totalExperience / baseExperience);
        }

        long remaining = totalExperience;
        long nextCost = baseExperience;
        int level = 0;
        while (level < maximumLevel && remaining >= nextCost)
        {
            remaining -= nextCost;
            level++;
            if (style == 2)
                nextCost *= 2L;
            else if (style == 3)
                nextCost = baseExperience * (level + 1L);
            else
                return 0;
        }
        return level;
    }

    private static long SumGeometricExperience(
        long baseExperience,
        int levels)
    {
        long total = 0;
        long cost = baseExperience;
        for (int i = 0; i < levels; i++)
        {
            total += cost;
            cost *= 2L;
        }
        return total;
    }

    private static string FormatExpiry(int remainingSeconds)
    {
        int remaining = Math.Max(0, remainingSeconds);
        var text = new StringBuilder();
        if (remaining > 31_536_000)
        {
            text.Append(remaining / 31_536_000).Append(" years, ");
            remaining %= 31_536_000;
        }
        if (remaining > 86_400)
        {
            text.Append(remaining / 86_400).Append(" days, ");
            remaining %= 86_400;
        }
        if (remaining > 3_600)
        {
            text.Append(remaining / 3_600).Append(" hours, ");
            remaining %= 3_600;
        }
        if (remaining > 60)
        {
            text.Append(remaining / 60).Append(" minutes, ");
            remaining %= 60;
        }
        text.Append(remaining).Append(" seconds.");
        return text.ToString();
    }

    private static string PluralizedGemName(int materialType, string material)
    {
        if (materialType == 0x26)
            return "Rubies";
        if (materialType is 0x0B or 0x18 or 0x1B or 0x1D or 0x20
            or 0x25 or 0x28 or 0x2E or 0x24 or 0x2D)
        {
            return $"pieces of {material}";
        }
        if (materialType is 0x1A or 0x31)
            return material + "es";
        return materialType == 0x1C ? material : material + "s";
    }

    private static string RemoveFirst(string value, string remove)
    {
        int index = value.IndexOf(remove, StringComparison.Ordinal);
        return index < 0
            ? value
            : value.Remove(index, remove.Length);
    }

    private static string? EquipmentSetName(int setId)
    {
        if (setId is >= 94 and <= 129)
        {
            string tier = ((setId - 94) / 12) switch
            {
                0 => "Minor",
                1 => "Major",
                _ => "Blackfire",
            };
            int member = (setId - 94) % 12;
            string effect = (member % 4) switch
            {
                0 => "Stinging",
                1 => "Sparking",
                2 => "Smoldering",
                _ => "Shivering",
            };
            string soul = (member / 4) switch
            {
                0 => "Shrouded Soul",
                1 => "Darkened Mind",
                _ => "Clouded Spirit",
            };
            return $"{tier} {effect} {soul}";
        }

        return setId switch
        {
            4 => "Carraida's Benediction",
            5 => "Noble Relic",
            6 => "Ancient Relic",
            7 => "Alduressa Relic",
            8 => "Shou-jen",
            9 => "Empyrean Rings",
            10 => "Arm, Mind, Heart",
            11 => "Coat of Perfect Light",
            12 => "Leggings of Perfect Light",
            13 => "Soldier's",
            14 => "Adept's",
            15 => "Archer's",
            16 => "Defender's",
            17 => "Tinker's",
            18 => "Crafter's",
            19 => "Hearty",
            20 => "Dexterous",
            21 => "Wise",
            22 => "Swift",
            23 => "Hardened",
            24 => "Reinforced",
            25 => "Interlocking",
            26 => "Flame Proof",
            27 => "Acid Proof",
            28 => "Cold Proof",
            29 => "Lightning Proof",
            30 => "Dedication",
            31 => "Gladiatorial Clothing",
            32 => "Ceremonial Clothing",
            33 => "Protective Clothing",
            35 => "Sigil of Defense",
            36 => "Sigil of Destruction",
            37 => "Sigil of Fury",
            38 => "Sigil of Growth",
            39 => "Sigil of Vigor",
            40 => "Heroic Protector",
            41 => "Heroic Destroyer",
            49 => "Weave of Alchemy",
            50 => "Weave of Arcane Lore",
            51 => "Weave of Armor Tinkering",
            52 => "Weave of Assess Person",
            53 or 67 or 74 or 75 or 79 => "Weave of Light Weapons",
            54 or 57 or 77 => "Weave of Missile Weapons",
            55 => "Weave of Cooking",
            56 => "Weave of Creature Enchantment",
            58 => "Weave of Finesse Weapons",
            59 => "Weave of Deception",
            60 => "Weave of Fletching",
            61 => "Weave of Healing",
            62 => "Weave of Item Enchantment",
            63 => "Weave of Item Tinkering",
            64 => "Weave of Leadership",
            65 => "Weave of Life Magic",
            66 => "Weave of Loyalty",
            68 => "Weave of Magic Defense",
            69 => "Weave of Magic Item Tinkering",
            70 => "Weave of Mana Conversion",
            71 => "Weave of Melee Defense",
            72 => "Weave of Missile Defense",
            73 => "Weave of Salvaging",
            76 => "Weave of Heavy Weapons",
            78 => "Weave of Two Handed Combat",
            80 => "Weave of Void Magic",
            81 => "Weave of War Magic",
            82 => "Weave of Weapon Tinkering",
            83 => "Weave of Assess Creature",
            84 => "Weave of Dirty Fighting",
            85 => "Weave of Dual Wield",
            86 => "Weave of Recklessness",
            87 => "Weave of Shield",
            88 => "Weave of Sneak Attack",
            89 => "Shou-jen Shozoku",
            90 => "Weave of Summoning",
            91 => "Shrouded Soul",
            92 => "Darkened Mind",
            93 => "Clouded Spirit",
            130 => "Shimmering Shadows",
            _ => null,
        };
    }

    private static string WeaponTimeName(int weaponTime) => weaponTime switch
    {
        < 11 => "Very Fast",
        < 31 => "Fast",
        < 50 => "Average",
        < 80 => "Slow",
        _ => "Very Slow",
    };

    private static string WorkmanshipAdjective(int workmanship) => workmanship switch
    {
        <= 0 => string.Empty,
        1 => "Poorly crafted",
        2 => "Well-crafted",
        3 => "Finely crafted",
        4 => "Exquisitely crafted",
        5 => "Magnificent",
        6 => "Nearly flawless",
        7 => "Flawless",
        8 => "Utterly flawless",
        9 => "Incomparable",
        _ => "Priceless",
    };

    private static string FormatDeltaTime(double seconds)
    {
        int remaining = Math.Max(0, (int)Math.Truncate(seconds));
        int months = remaining / 2_592_000;
        remaining %= 2_592_000;
        int days = remaining / 86_400;
        remaining %= 86_400;
        int hours = remaining / 3_600;
        remaining %= 3_600;
        int minutes = remaining / 60;
        int finalSeconds = remaining % 60;

        var result = new StringBuilder();
        if (months != 0)
            result.Append(months).Append("mo ");
        if (days != 0)
            result.Append(days).Append("d ");
        if (hours != 0)
            result.Append(hours).Append("h ");
        if (minutes != 0)
            result.Append(minutes).Append("m ");
        result.Append(finalSeconds).Append("s ");
        return result.ToString();
    }

    private static string FormatModifier(double modifier)
        => FormatSignedPercent(modifier - 1d);

    private static string FormatSignedPercent(double modifier)
        => modifier.ToString("+0%;-0%;0%", CultureInfo.InvariantCulture);

    private static string? LockpickDifficulty(int successPercent)
        => successPercent switch
        {
            < 0 => null,
            0 => "impossible",
            < 5 => "ridiculously difficult",
            < 15 => "extremely difficult",
            < 35 => "quite difficult",
            < 50 => "difficult",
            < 70 => "challenging",
            < 85 => "mildly challenging",
            < 95 => "easy",
            _ => "trivial",
        };

    private static ItemAppraisalFontStyle EnchantmentStyle(
        (ushort Highlight, ushort Color)? encoded,
        uint lowBit)
    {
        if (encoded is not { } halves)
            return ItemAppraisalFontStyle.Normal;

        uint bitfield = halves.Highlight | ((uint)halves.Color << 16);
        if ((bitfield & lowBit) == 0u)
            return ItemAppraisalFontStyle.Normal;
        return (bitfield & (lowBit << 16)) != 0u
            ? ItemAppraisalFontStyle.Beneficial
            : ItemAppraisalFontStyle.Detrimental;
    }

    private static string DamageTypeName(uint type)
        => TryDamageTypeName(type, out string? name)
            ? name!
            : $"type {type.ToString(CultureInfo.InvariantCulture)}";

    private static string WeaponSubtype(int type) => type switch
    {
        1 => " (Unarmed Weapon)",
        2 => " (Sword)",
        3 => " (Axe)",
        4 => " (Mace)",
        5 => " (Spear)",
        6 => " (Dagger)",
        7 => " (Staff)",
        8 => " (Bow)",
        9 => " (Crossbow)",
        10 => " (Thrown)",
        _ => string.Empty,
    };

    internal static string SkillName(int skill) => skill switch
    {
        1 => "Axe",
        2 => "Bow",
        3 => "Crossbow",
        4 => "Dagger",
        5 => "Mace",
        6 => "Melee Defense",
        7 => "Missile Defense",
        8 => "Sling",
        9 => "Spear",
        10 => "Staff",
        11 => "Sword",
        12 => "Thrown Weapon",
        13 => "Unarmed Combat",
        14 => "Arcane Lore",
        15 => "Magic Defense",
        16 => "Mana Conversion",
        17 => "Spellcraft",
        18 => "Item Tinkering",
        19 => "Person Appraisal",
        20 => "Deception",
        21 => "Healing",
        22 => "Jump",
        23 => "Lockpick",
        24 => "Run",
        25 => "Awareness",
        26 => "Armor Repair",
        27 => "Creature Appraisal",
        28 => "Weapon Tinkering",
        29 => "Armor Tinkering",
        30 => "Magic Item Tinkering",
        31 => "Creature Enchantment",
        32 => "Item Enchantment",
        33 => "Life Magic",
        34 => "War Magic",
        35 => "Leadership",
        36 => "Loyalty",
        37 => "Fletching",
        38 => "Alchemy",
        39 => "Cooking",
        40 => "Salvaging",
        41 => "Two Handed Combat",
        42 => "Gearcraft",
        43 => "Void Magic",
        44 => "Heavy Weapons",
        45 => "Light Weapons",
        46 => "Finesse Weapons",
        47 => "Missile Weapons",
        49 => "Dual Wield",
        50 => "Recklessness",
        51 => "Sneak Attack",
        52 => "Dirty Fighting",
        53 => "Challenge",
        54 => "Summoning",
        _ => $"Skill {skill.ToString(CultureInfo.InvariantCulture)}",
    };

    private static string UsageSkillName(int skill)
    {
        string name = SkillName(skill);
        return name.StartsWith("Skill ", StringComparison.Ordinal)
            ? "Unknown Skill"
            : name;
    }

    private static string PrimaryAttributeName(int attribute) => attribute switch
    {
        1 => "Strength",
        2 => "Endurance",
        3 => "Quickness",
        4 => "Coordination",
        5 => "Focus",
        6 => "Self",
        _ => $"Attribute {attribute.ToString(CultureInfo.InvariantCulture)}",
    };

    private static string SecondaryAttributeName(int attribute) => attribute switch
    {
        1 => "Max Health",
        2 => "Health",
        3 => "Max Stamina",
        4 => "Stamina",
        5 => "Max Mana",
        6 => "Mana",
        _ => $"Vital {attribute.ToString(CultureInfo.InvariantCulture)}",
    };

    private sealed class RetailReportBuilder
    {
        private readonly ItemAppraisalReportBuilder _report = new();

        public void Line(
            string value,
            ItemAppraisalFontStyle style = ItemAppraisalFontStyle.Normal)
            => _report.Line(value, style);

        public void Paragraph(
            string value,
            ItemAppraisalFontStyle style = ItemAppraisalFontStyle.Normal)
            => _report.Paragraph(value, style);

        public void BlankLine(
            ItemAppraisalFontStyle style = ItemAppraisalFontStyle.Normal)
            => _report.BlankLine(style);

        public ItemAppraisalReport Build() => _report.Build();
    }
}
