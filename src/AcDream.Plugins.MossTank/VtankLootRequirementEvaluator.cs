using System.Globalization;
using System.Text.RegularExpressions;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

/// <summary>Executes VTClassic's typed loot requirements as an AND set.</summary>
internal static class VtankLootRequirementEvaluator
{
    private const uint VtankIntBase = 218_103_808u;
    private const uint VtankDoubleBase = 167_772_160u;

    private static readonly IReadOnlyDictionary<uint, (uint Key, int Bonus)>
        IntSpellBonuses = new Dictionary<uint, (uint, int)>
        {
            [2598] = (VtankIntBase + 34, 2),
            [2586] = (VtankIntBase + 34, 4),
            [4661] = (VtankIntBase + 34, 7),
            [6089] = (VtankIntBase + 34, 10),
            [2604] = (28, 20),
            [2592] = (28, 40),
            [4667] = (28, 60),
            [6095] = (28, 80),
        };

    private static readonly IReadOnlyDictionary<uint, (uint Key, double Bonus, bool Change)>
        DoubleSpellBonuses = new Dictionary<uint, (uint, double, bool)>
        {
            [3251] = (152, .01, false), [3250] = (152, .03, false),
            [4670] = (152, .05, false), [6098] = (152, .07, false),
            [2603] = (VtankDoubleBase + 12, .03, false),
            [2591] = (VtankDoubleBase + 12, .05, false),
            [4666] = (VtankDoubleBase + 12, .07, false),
            [6094] = (VtankDoubleBase + 12, .09, false),
            [2600] = (29, .03, false), [3985] = (29, .04, false),
            [2588] = (29, .05, false), [4663] = (29, .07, false), [6091] = (29, .09, false),
            [3201] = (144, 1.05, true), [3199] = (144, 1.10, true),
            [3202] = (144, 1.15, true), [3200] = (144, 1.20, true),
            [6086] = (144, 1.25, true), [6087] = (144, 1.30, true),
        };

    private static readonly IReadOnlyDictionary<string, int[]> ArmorColorSlots =
        new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            ["Amuli Coat (Chest)"] = [0],
            ["Amuli Coat (Collar/Shoulder)"] = [1, 2],
            ["Amuli Coat (Arms/Trim)"] = [3, 4, 5, 6, 7],
            ["Amuli Legs (Base)"] = [0, 1],
            ["Amuli Legs (Trim)"] = [2, 3],
            ["Celdon (Base)"] = [0],
            ["Celdon (Veins)"] = [1, 2],
            ["Chiran Coat (Base/Arms)"] = [0, 1],
            ["Chiran Coat (Stripes)"] = [2, 3, 4],
            ["Chiran Legs (Girth)"] = [1],
            ["Chiran Legs (Legs)"] = [2, 3],
            ["Chiran Legs (Trim)"] = [0],
            ["Chiran Helm (Horns)"] = [0],
            ["Chiran Helm (Base)"] = [1],
            ["Haebrean BP (Chest) *"] = [0],
            ["Haebrean BP (Ornaments)"] = [1],
            ["Haebrean BP (Trim)"] = [2],
            ["Haebrean Girth (Base) *"] = [0],
            ["Haebrean Girth (Belt/Scales)"] = [1, 2],
            ["Haebrean Helm (Base)"] = [0],
            ["Haebrean Helm (Mask)"] = [1],
            ["Haebrean Pauldrons (Base) *"] = [0],
            ["Haebrean Pauldrons (Ornaments)"] = [1],
            ["Lorica BP (Veins)"] = [0, 1],
            ["Lorica BP (Base)"] = [2, 3],
            ["Lorica BP (Neck/Trim) *"] = [4],
            ["Lorica Legs (Base)"] = [0],
            ["Lorica Legs (Knees/Belt/Crotch) *"] = [1, 2],
            ["Lorica Legs (Legs) *"] = [3],
            ["Nariyid BP (Circle/Lines)"] = [0, 1],
            ["Nariyid BP (Base)"] = [2],
            ["Nariyid BP (Shoulders)"] = [3],
            ["Nariyid Girth (Base) *"] = [0],
            ["Nariyid Girth (Belt/Lines)"] = [2],
            ["Nariyid Girth (Ornaments)"] = [3],
            ["Nariyid Sleeves (Shoulders)"] = [0],
            ["Nariyid Sleeves (Upper Arm)"] = [1, 2],
            ["Nariyid Sleeves (Lower Arm)"] = [3],
            ["Olthoi BP (Base)"] = [0],
            ["Olthoi BP (Veins)"] = [1],
            ["Olthoi Alduressa Legs (Girth: Base)"] = [0, 1, 2],
            ["Olthoi Alduressa Legs (Girth: Lines)"] = [3],
            ["Olthoi Alduressa Legs (Legs: Lines)"] = [4, 5],
            ["Olthoi Amuli Coat (Base) *"] = [0, 1],
            ["Olthoi Amuli Coat (Trim)"] = [2],
            ["Olthoi Amuli Coat (Shoulders)"] = [3],
            ["Olthoi Amuli Legs (Trim)"] = [6, 7, 8],
            ["Olthoi Koujia Kabuton (Base)"] = [0],
            ["Olthoi Koujia Kabuton (Horns)"] = [1],
            ["Olthoi Koujia Legs (Base)"] = [0, 1, 2],
            ["Olthoi Koujia Legs (Sides/Shins)"] = [3, 4, 5],
            ["Scalemail Cuirass (Base)"] = [0],
            ["Scalemail Cuirass (Bumps)"] = [1],
            ["Scalemail Cuirass (Belt)"] = [2],
            ["Tenassa Legs (Line at Side)"] = [0],
            ["Tenassa Legs (Base)"] = [1],
            ["Tenassa Legs (Hilight)"] = [2],
            ["Tenassa BP (Shoulders)"] = [0],
            ["Tenassa BP (Base)"] = [1],
            ["Yoroi Cuirass (Base)"] = [0, 1],
            ["Yoroi Cuirass (Belt)"] = [2],
            ["Yoroi Girth (Base)"] = [0],
            ["Yoroi Girth (Belt)"] = [1],
        };

    public static bool IsMatch(
        IReadOnlyList<VtankLootRequirement> requirements,
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        IPluginHost? host,
        out string? error)
    {
        try
        {
            foreach (VtankLootRequirement requirement in requirements)
            {
                if (!IsMatch(requirement, item, properties, host))
                {
                    error = null;
                    return false;
                }
            }
            error = null;
            return true;
        }
        catch (Exception failure) when (
            failure is FormatException or ArgumentException or OverflowException)
        {
            error = failure.Message;
            return false;
        }
    }

    private static bool IsMatch(
        VtankLootRequirement requirement,
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        IPluginHost? host)
    {
        string[] values = Lines(requirement.Payload);
        return requirement.Type switch
        {
            0 => SpellNames(item, host).Any(name => Rx(values, 0).IsMatch(name)),
            1 => Rx(values, 0).IsMatch(StringValue(
                U32(values, 1), item, properties)),
            2 => IntValue(U32(values, 1), item, properties) <= I32(values, 0),
            3 => IntValue(U32(values, 1), item, properties) >= I32(values, 0),
            4 => (float)DoubleValue(U32(values, 1), item, properties)
                <= (float)F64(values, 0),
            5 => (float)DoubleValue(U32(values, 1), item, properties)
                >= (float)F64(values, 0),
            // VTClassic deliberately retired this requirement; its own Match
            // method always returns false.
            6 => false,
            7 => (int)item.ObjectClass == I32(values, 0),
            8 => item.AppraisedSpellIds.Count >= I32(values, 0),
            9 => SpellMatch(values, item, host),
            10 => MinimumDamage(item) >= F64(values, 0),
            11 => (IntValue(U32(values, 1), item, properties)
                & I32(values, 0)) > 0,
            12 => IntValue(U32(values, 1), item, properties) == I32(values, 0),
            13 => IntValue(U32(values, 1), item, properties) != I32(values, 0),
            14 => ColorMatch(values, item.Palettes),
            15 => ArmorColorMatch(values, item.Palettes),
            16 => SlotColorMatch(values, item.Palettes),
            17 => ExactPalette(values, item.Palettes),
            1000 => CharacterSkill(host, U32(values, 1), buffed: true)
                >= I32(values, 0),
            1001 => (host?.Automation.Character.MainPackFreeSlots ?? 0)
                >= I32(values, 0),
            1002 => (host?.Automation.Character.Level ?? 0) >= I32(values, 0),
            1003 => (host?.Automation.Character.Level ?? 0) <= I32(values, 0),
            1004 => CharacterBaseSkillRange(values, host),
            2000 => BuffedMedianDamage(item, properties) >= F64(values, 0),
            2001 => BuffedMissileDamage(item, properties) >= F64(values, 0),
            2003 => BuffedInt(
                U32(values, 1), item, properties) >= F64(values, 0),
            2005 => (float)BuffedDouble(
                U32(values, 1), item, properties) >= (float)F64(values, 0),
            2006 => BuffedTinkedDamage(item, properties) >= F64(values, 0),
            2007 => TotalRatings(item, properties) >= F64(values, 0),
            2008 => CanReachTarget(values, item, properties),
            9999 => !Bool(values, 0),
            _ => false,
        };
    }

    private static bool SpellMatch(
        string[] values,
        in PluginInventoryItem item,
        IPluginHost? host)
    {
        Regex include = Rx(values, 0);
        Regex exclude = Rx(values, 1);
        bool excludeEmpty = Value(values, 1).Trim().Length == 0;
        int required = I32(values, 2);
        int count = 0;
        foreach (string name in SpellNames(item, host))
        {
            if (include.IsMatch(name)
                && (excludeEmpty || !exclude.IsMatch(name))
                && ++count >= required)
            {
                return true;
            }
        }
        return false;
    }

    private static bool ColorMatch(
        string[] values,
        IReadOnlyList<PluginPaletteInfo> palettes)
    {
        for (int index = 0; index < palettes.Count; index++)
        {
            if (SimilarColor(values, palettes[index]))
                return true;
        }
        return false;
    }

    private static bool ArmorColorMatch(
        string[] values,
        IReadOnlyList<PluginPaletteInfo> palettes)
    {
        if (!ArmorColorSlots.TryGetValue(Value(values, 5), out int[]? slots))
            return false;
        foreach (int slot in slots)
        {
            if (slot >= 0 && slot < palettes.Count
                && SimilarColor(values, palettes[slot]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SlotColorMatch(
        string[] values,
        IReadOnlyList<PluginPaletteInfo> palettes)
    {
        int slot = I32(values, 5);
        return slot >= 0 && slot < palettes.Count
            && SimilarColor(values, palettes[slot]);
    }

    private static bool ExactPalette(
        string[] values,
        IReadOnlyList<PluginPaletteInfo> palettes)
    {
        int slot = I32(values, 0);
        uint expected = U32(values, 1) & 0x00FF_FFFFu;
        return slot >= 0 && slot < palettes.Count
            && (palettes[slot].PaletteId & 0x00FF_FFFFu) == expected;
    }

    private static bool SimilarColor(
        string[] values,
        in PluginPaletteInfo palette)
    {
        Hsv(
            checked((byte)I32(values, 0)),
            checked((byte)I32(values, 1)),
            checked((byte)I32(values, 2)),
            out double targetHue,
            out double targetSaturation,
            out double targetValue);
        Hsv(
            palette.Red,
            palette.Green,
            palette.Blue,
            out double hue,
            out double saturation,
            out double value);
        if (Math.Abs(hue - targetHue) > F64(values, 3))
            return false;
        double sd = saturation - targetSaturation;
        double vd = value - targetValue;
        return Math.Sqrt((sd * sd) + (vd * vd)) <= F64(values, 4);
    }

    private static void Hsv(
        byte red,
        byte green,
        byte blue,
        out double hue,
        out double saturation,
        out double value)
    {
        int maximum = Math.Max(red, Math.Max(green, blue));
        int minimum = Math.Min(red, Math.Min(green, blue));
        int delta = maximum - minimum;
        if (delta == 0)
        {
            hue = 0d;
        }
        else if (maximum == red)
        {
            hue = 60d * (green - blue) / delta;
            if (hue < 0d)
                hue += 360d;
        }
        else if (maximum == green)
        {
            hue = (60d * (blue - red) / delta) + 120d;
        }
        else
        {
            hue = (60d * (red - green) / delta) + 240d;
        }
        saturation = maximum == 0 ? 0d : 1d - ((double)minimum / maximum);
        value = maximum / 255d;
    }

    private static IEnumerable<string> SpellNames(
        PluginInventoryItem item,
        IPluginHost? host)
    {
        if (host is null)
            yield break;
        foreach (uint spellId in item.AppraisedSpellIds)
        {
            if (host.Automation.Spells.TryGet(spellId, out PluginSpellInfo spell))
                yield return spell.Name;
        }
    }

    private static int CharacterSkill(
        IPluginHost? host,
        uint skillId,
        bool buffed)
    {
        if (host?.Automation.Character.TryGetSkill(
            skillId,
            out PluginSkillInfo skill) != true)
        {
            return 0;
        }
        return checked((int)(buffed ? skill.Current : skill.Base));
    }

    private static bool CharacterBaseSkillRange(
        string[] values,
        IPluginHost? host)
    {
        int level = CharacterSkill(host, U32(values, 0), buffed: false);
        return level >= I32(values, 1) && level <= I32(values, 2);
    }

    private static string StringValue(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties) => key switch
    {
        1 => item.Name,
        _ => properties.Strings?.TryGetValue(key, out string? value) == true
            ? value
            : string.Empty,
    };

    private static bool TryIntValue(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        out int value)
    {
        switch (key)
        {
            case 5: value = item.Burden; return true;
            case 19: value = item.Value; return true;
            case 105: value = checked((int)item.Workmanship); return true;
            case 107: value = item.ItemCurrentMana; return true;
            case 108: value = item.ItemMaximumMana; return true;
            case 131: value = checked((int)item.MaterialType); return true;
            case VtankIntBase + 0: value = checked((int)item.WeenieClassId); return true;
            case VtankIntBase + 2: value = checked((int)item.ContainerObjectId); return true;
            case VtankIntBase + 4: value = item.ItemsCapacity; return true;
            case VtankIntBase + 5: value = item.ContainersCapacity; return true;
            case VtankIntBase + 6: value = item.StackSize; return true;
            case VtankIntBase + 7: value = item.MaximumStackSize; return true;
            case VtankIntBase + 8: value = checked((int)item.SpellId); return true;
            case VtankIntBase + 9: value = item.ContainerSlot; return true;
            case VtankIntBase + 10: value = checked((int)item.WielderObjectId); return true;
            case VtankIntBase + 11: value = checked((int)item.EquippedLocation); return true;
            case VtankIntBase + 14: value = checked((int)item.ValidLocations); return true;
            case VtankIntBase + 18: value = checked((int)item.Useability); return true;
            case VtankIntBase + 23: value = checked((int)item.PublicFlags); return true;
            case VtankIntBase + 31: value = item.CombatUse; return true;
            case VtankIntBase + 32: value = item.WeaponSkill; return true;
            case VtankIntBase + 33: value = item.DamageType; return true;
            case VtankIntBase + 34: value = item.Damage; return true;
            case VtankIntBase + 38: value = item.AppraisedSpellIds.Count; return true;
            default:
                value = 0;
                return properties.Ints?.TryGetValue(key, out value) == true;
        }
    }

    private static int IntValue(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties) =>
        TryIntValue(key, item, properties, out int value) ? value : 0;

    private static bool TryDoubleValue(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        out double value)
    {
        switch (key)
        {
            case VtankDoubleBase + 9: value = item.Workmanship; return true;
            case VtankDoubleBase + 11: value = item.DamageVariance; return true;
            case VtankDoubleBase + 12:
                TryRawFloat(properties, 62, out value);
                return true;
            case VtankDoubleBase + 14:
                TryRawFloat(properties, 63, out value);
                return true;
            default:
                return TryRawFloat(properties, key, out value);
        }
    }

    private static double DoubleValue(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties) =>
        TryDoubleValue(key, item, properties, out double value) ? value : 0d;

    private static bool TryRawFloat(in PluginItemProperties properties, uint key, out double value)
    {
        value = 0d;
        return properties.Floats?.TryGetValue(key, out value) == true;
    }

    private static int BuffedInt(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties)
    {
        int value = IntValue(key, item, properties);
        if (!IntKeyExists(key, item, properties))
            return value;
        foreach (uint spellId in item.AppraisedSpellIds)
        {
            if (IntSpellBonuses.TryGetValue(spellId, out var bonus)
                && bonus.Key == key)
            {
                value += bonus.Bonus;
            }
        }
        return value;
    }

    private static double BuffedDouble(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties)
    {
        double value = DoubleValue(key, item, properties);
        if (!DoubleKeyExists(key, item, properties))
            return value;
        foreach (uint spellId in item.AppraisedSpellIds)
        {
            if (!DoubleSpellBonuses.TryGetValue(spellId, out var bonus)
                || bonus.Key != key)
            {
                continue;
            }
            value = bonus.Change ? value * bonus.Bonus : value + bonus.Bonus;
        }
        return value;
    }

    private static bool IntKeyExists(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties) =>
        TryIntValue(key, item, properties, out _);

    private static bool DoubleKeyExists(
        uint key,
        in PluginInventoryItem item,
        in PluginItemProperties properties) =>
        TryDoubleValue(key, item, properties, out _);

    private static double MinimumDamage(in PluginInventoryItem item) =>
        item.Damage - (item.DamageVariance * item.Damage);

    private static double BuffedMedianDamage(
        in PluginInventoryItem item,
        in PluginItemProperties properties)
    {
        int maximum = BuffedInt(VtankIntBase + 34, item, properties);
        double minimum = maximum - (item.DamageVariance * maximum);
        return (minimum + maximum) / 2d;
    }

    private static double BuffedMissileDamage(
        in PluginInventoryItem item,
        in PluginItemProperties properties) =>
        BuffedInt(VtankIntBase + 34, item, properties)
        + (((BuffedDouble(VtankDoubleBase + 14, item, properties) - 1d)
            * 100d) / 3d)
        + BuffedInt(204, item, properties);

    private static double BuffedTinkedDamage(
        in PluginInventoryItem item,
        in PluginItemProperties properties)
    {
        double variance = item.DamageVariance;
        int maximum = BuffedInt(VtankIntBase + 34, item, properties);
        int tinks = Math.Max(10 - IntValue(171, item, properties), 0);
        if (IntValue(179, item, properties) == 0)
            tinks--;
        if (IntValue(131, item, properties) == 0)
            tinks = 0;
        for (int index = 1; index <= tinks; index++)
        {
            double iron = DamageOverTime(maximum + 25, variance);
            double granite = DamageOverTime(maximum + 24, variance * .8d);
            if (iron >= granite)
                maximum++;
            else
                variance *= .8d;
        }
        return DamageOverTime(maximum + 24, variance);
    }

    private static int TotalRatings(
        in PluginInventoryItem item,
        in PluginItemProperties properties) =>
        item.GearDamage + item.GearDamageResistance
        + item.GearCriticalChance + item.GearCriticalResistance
        + item.GearCriticalDamage + item.GearCriticalDamageResistance
        + IntValue(376, item, properties) + IntValue(379, item, properties);

    private static bool CanReachTarget(
        string[] values,
        in PluginInventoryItem item,
        in PluginItemProperties properties)
    {
        double targetDamage = F64(values, 0);
        double targetDefense = F64(values, 1);
        double targetAttack = F64(values, 2);
        double defense = BuffedDouble(29, item, properties);
        double attack = BuffedDouble(VtankDoubleBase + 12, item, properties);
        double variance = item.DamageVariance;
        int maximum = BuffedInt(VtankIntBase + 34, item, properties);
        int tinks = Math.Max(10 - IntValue(171, item, properties), 0);
        if (IntValue(179, item, properties) == 0)
            tinks--;
        if (IntValue(131, item, properties) == 0)
            tinks = 0;
        for (int index = 1; index <= tinks; index++)
        {
            if (defense < targetDefense)
                defense += .01d;
            else if (attack < targetAttack)
                attack += .01d;
            else if (DamageOverTime(maximum + 25, variance)
                >= DamageOverTime(maximum + 24, variance * .8d))
                maximum++;
            else
                variance *= .8d;
        }
        return DamageOverTime(maximum + 24, variance) >= targetDamage
            && defense >= targetDefense
            && attack >= targetAttack;
    }

    private static double DamageOverTime(int maximum, double variance) =>
        maximum * ((.9d * (2d - variance) / 2d) + .2d);

    private static string[] Lines(string? payload) =>
        (payload ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static string Value(string[] values, int index) =>
        index >= 0 && index < values.Length
            ? values[index]
            : throw new FormatException("A VTClassic loot requirement is truncated.");

    private static Regex Rx(string[] values, int index) => new(
        Value(values, index),
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static int I32(string[] values, int index) =>
        int.TryParse(Value(values, index), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw new FormatException("A VTClassic integer is invalid.");

    private static uint U32(string[] values, int index) =>
        uint.TryParse(Value(values, index), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out uint parsed)
                ? parsed
                : throw new FormatException("A VTClassic key is invalid.");

    private static double F64(string[] values, int index) =>
        double.TryParse(Value(values, index).Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : throw new FormatException("A VTClassic number is invalid.");

    private static bool Bool(string[] values, int index) =>
        bool.TryParse(Value(values, index), out bool parsed)
            ? parsed
            : throw new FormatException("A VTClassic boolean is invalid.");
}
