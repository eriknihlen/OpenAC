using System.Globalization;
using System.Text.RegularExpressions;
using AcDream.DrakBot.Meta;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Loot.Utl;

/// <summary>
/// What a VTank loot rule can ask about the character and an item's
/// appraisal: skills, level, pack room, the item's spells. Built once per
/// corpse pass so the same answers serve every rule.
/// </summary>
public sealed class UtlLootContext(IAutomationSurface surface)
{
    private readonly Dictionary<uint, PluginItemProperties?> _properties = new();
    private int? _mainPackFreeSlots;

    public ICharacterInfo Character => surface.Character;

    public int Level => Character.Level;

    public int MainPackFreeSlots => _mainPackFreeSlots ??= Character.MainPackFreeSlots;

    public (int Buffed, int Base, int Training) Skill(uint skillId) =>
        Character.TryGetSkill(skillId, out PluginSkillInfo skill)
            ? ((int)skill.Current, (int)skill.Base, (int)skill.Training)
            : (0, 0, 0);

    /// <summary>The item's appraised properties, when the client has them.</summary>
    public PluginItemProperties? Properties(uint objectId)
    {
        if (_properties.TryGetValue(objectId, out PluginItemProperties? cached))
            return cached;
        PluginItemProperties? properties = surface.Objects.TryCaptureProperties(objectId, out PluginItemProperties captured)
            ? captured
            : null;
        _properties[objectId] = properties;
        return properties;
    }

    public IReadOnlyList<uint> SpellIds(in PluginInventoryItem item) =>
        item.AppraisedSpellIds.Count > 0
            ? item.AppraisedSpellIds
            : surface.Objects.TryGet(item.ObjectId, out PluginWorldObject value) ? value.SpellIds : Array.Empty<uint>();

    public string SpellName(uint spellId) =>
        surface.Spells.TryGet(spellId, out PluginSpellInfo info) ? info.Name : string.Empty;
}

/// <summary>
/// Evaluates VTank loot rules against a corpse item, the way RynthAi's
/// evaluator does. The property keys a <c>.utl</c> carries are Decal's:
/// the game's own property ids for anything appraisal reports, plus the
/// keys Decal numbered for its own fields from 0x0D000000 (long),
/// 0x0A000000 (double) and 0x0B000000 (string), which are answered from the
/// item record. Rules about colour need the palette data and pass
/// optimistically, as VTank's port did.
/// </summary>
public static class UtlLootEvaluator
{
    // Decal's LongValueKey members, as its adapter numbers them.
    private const int DecalType = 218103808;            // weenie class id
    private const int DecalIcon = 218103809;
    private const int DecalContainer = 218103810;
    private const int DecalLandblock = 218103811;
    private const int DecalItemSlots = 218103812;
    private const int DecalPackSlots = 218103813;
    private const int DecalStackCount = 218103814;
    private const int DecalStackMax = 218103815;
    private const int DecalAssociatedSpell = 218103816;
    private const int DecalSlotLegacy = 218103817;
    private const int DecalWielder = 218103818;
    private const int DecalWieldingSlot = 218103819;
    private const int DecalMonarch = 218103820;
    private const int DecalCoverage = 218103821;
    private const int DecalEquipableSlots = 218103822;
    private const int DecalEquipType = 218103823;
    private const int DecalIconOutline = 218103824;
    private const int DecalMissileType = 218103825;
    private const int DecalUsageMask = 218103826;
    private const int DecalCategory = 218103834;        // the item type mask
    private const int DecalBehavior = 218103835;
    private const int DecalMagicDef = 218103836;
    private const int DecalSpecialProps = 218103837;
    private const int DecalSpellCount = 218103838;
    private const int DecalWeaponSpeed = 218103839;
    private const int DecalEquipSkill = 218103840;
    private const int DecalDamageType = 218103841;
    private const int DecalMaxDamage = 218103842;
    private const int DecalActiveSpellCount = 218103848;
    private const int DecalIconOverlay = 218103849;
    private const int DecalIconUnderlay = 218103850;
    private const int DecalSlot = 231735296;
    // Decal's DoubleValueKey members.
    private const int DecalSlashProtection = 167772160;
    private const int DecalPierceProtection = 167772161;
    private const int DecalBludgeonProtection = 167772162;
    private const int DecalAcidProtection = 167772163;
    private const int DecalLightningProtection = 167772164;
    private const int DecalFireProtection = 167772165;
    private const int DecalColdProtection = 167772166;
    private const int DecalSalvageWorkmanship = 167772169;
    private const int DecalVariance = 167772171;
    private const int DecalAttackBonus = 167772172;
    private const int DecalDamageBonus = 167772174;
    // Decal's StringValueKey member.
    private const int DecalSecondaryName = 184549376;
    // The game's property ids the rules and the fields above lean on.
    private const uint PropertyValue = 19u;
    private const uint PropertyDamage = 44u;
    private const uint PropertyDamageType = 45u;
    private const uint PropertyWeaponSkill = 48u;
    private const uint PropertyWeaponTime = 49u;
    private const uint PropertyDamageVariance = 22u;
    private const uint PropertyItemWorkmanship = 105u;
    private const uint PropertyMaterialType = 131u;
    private const uint PropertyStackSize = 12u;
    private const uint PropertyMaxStackSize = 11u;
    private const uint PropertyEncumbrance = 5u;
    private const uint PropertyElementalDamageMod = 152u;
    private const uint PropertyClothingPriority = 4u;   // the coverage mask
    private const uint PropertyWeaponOffense = 62u;
    private const uint PropertyDamageMod = 63u;
    private const uint PropertyArmorModVsSlash = 13u;
    private const uint PropertyArmorModVsPierce = 14u;
    private const uint PropertyArmorModVsBludgeon = 15u;
    private const uint PropertyArmorModVsCold = 16u;
    private const uint PropertyArmorModVsFire = 17u;
    private const uint PropertyArmorModVsAcid = 18u;
    private const uint PropertyArmorModVsElectric = 19u;
    private const uint DataIconOverlay = 50u;
    private const uint DataIconUnderlay = 52u;

    /// <summary>Whether any of the rule's conditions reads appraisal-only data.</summary>
    public static bool NeedsAppraisal(VTankLootRule rule)
    {
        foreach (VTankLootCondition condition in rule.Conditions)
        {
            if (NeedsAppraisal(condition))
                return true;
        }
        return false;
    }

    /// <summary>Whether a condition reads what only an appraisal reports.</summary>
    public static bool NeedsAppraisal(VTankLootCondition condition)
    {
        switch (condition.NodeType)
        {
            case VTankNodeTypes.ObjectClass:
            case VTankNodeTypes.DisabledRule:
            case VTankNodeTypes.CharacterLevelGE:
            case VTankNodeTypes.CharacterLevelLE:
            case VTankNodeTypes.CharacterSkillGE:
            case VTankNodeTypes.CharacterBaseSkill:
            case VTankNodeTypes.CharacterMainPackEmptySlotsGE:
                return false;
            case VTankNodeTypes.StringValueMatch:
                return !(condition.DataLines.Count >= 2 && condition.DataLines[1].Trim() == "1"); // the name is known
            case VTankNodeTypes.LongValKeyLE:
            case VTankNodeTypes.LongValKeyGE:
            case VTankNodeTypes.LongValKeyE:
            case VTankNodeTypes.LongValKeyNE:
            case VTankNodeTypes.LongValKeyFlagExists:
                return !(condition.DataLines.Count >= 2 && int.TryParse(condition.DataLines[1], out int key) && KnownBeforeAppraisal(key));
            default:
                return true;
        }
    }

    /// <summary>
    /// Whether the rule's conditions that need no appraisal all hold, so an
    /// appraisal is worth asking for; a rule whose name or class test
    /// already fails never gets one.
    /// </summary>
    public static bool CouldMatchAfterAppraisal(VTankLootRule rule, in PluginInventoryItem item, UtlLootContext context)
    {
        try
        {
            foreach (VTankLootCondition condition in rule.Conditions)
            {
                if (!NeedsAppraisal(condition) && !Match(condition, item, context))
                    return false;
            }
            return true;
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>The first enabled rule the item satisfies, in file order; null for none.</summary>
    public static VTankLootRule? FirstMatch(VTankLootProfile profile, in PluginInventoryItem item, UtlLootContext context)
    {
        foreach (VTankLootRule rule in profile.Rules)
        {
            if (rule.Enabled && Match(rule, item, context))
                return rule;
        }
        return null;
    }

    public static bool Match(VTankLootRule rule, in PluginInventoryItem item, UtlLootContext context)
    {
        if (rule.Conditions.Count == 0)
            return true;
        try
        {
            foreach (VTankLootCondition condition in rule.Conditions)
            {
                if (!Match(condition, item, context))
                    return false;
            }
            return true;
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool Match(VTankLootCondition condition, in PluginInventoryItem item, UtlLootContext context)
    {
        IList<string> d = condition.DataLines;
        PluginItemProperties? properties = context.Properties(item.ObjectId);
        switch (condition.NodeType)
        {
            case VTankNodeTypes.SpellNameMatch:
            {
                Regex? pattern = RegexCache.Get(d[0], RegexOptions.IgnoreCase);
                if (pattern is null) return false;
                foreach (uint spellId in context.SpellIds(item))
                {
                    string name = context.SpellName(spellId);
                    if (name.Length > 0 && pattern.IsMatch(name))
                        return true;
                }
                return false;
            }
            case VTankNodeTypes.StringValueMatch:
                return RegexCache.IsMatch(StringValue(item, properties, ReadInt(d, 1)), d[0], RegexOptions.IgnoreCase);
            case VTankNodeTypes.LongValKeyLE:
                return LongValue(item, properties, ReadInt(d, 1)) <= ReadInt(d, 0);
            case VTankNodeTypes.LongValKeyGE:
                return LongValue(item, properties, ReadInt(d, 1)) >= ReadInt(d, 0);
            case VTankNodeTypes.LongValKeyE:
                return LongValue(item, properties, ReadInt(d, 1)) == ReadInt(d, 0);
            case VTankNodeTypes.LongValKeyNE:
                return LongValue(item, properties, ReadInt(d, 1)) != ReadInt(d, 0);
            case VTankNodeTypes.LongValKeyFlagExists:
                return (LongValue(item, properties, ReadInt(d, 1)) & ReadInt(d, 0)) != 0;
            case VTankNodeTypes.DoubleValKeyLE:
                return DoubleValue(item, properties, ReadInt(d, 1)) <= ReadDouble(d, 0);
            case VTankNodeTypes.DoubleValKeyGE:
                return DoubleValue(item, properties, ReadInt(d, 1)) >= ReadDouble(d, 0);
            case VTankNodeTypes.BuffedLongValKeyGE:
                return LongValue(item, properties, ReadInt(d, 1)) >= ReadDouble(d, 0);
            case VTankNodeTypes.BuffedDoubleValKeyGE:
                return DoubleValue(item, properties, ReadInt(d, 1)) >= ReadDouble(d, 0);
            case VTankNodeTypes.DamagePercentGE:
            case VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE:
                return false; // VTank itself never matched these
            case VTankNodeTypes.ObjectClass:
                return (int)item.ObjectClass == ReadInt(d, 0);
            case VTankNodeTypes.SpellCountGE:
                return context.SpellIds(item).Count >= ReadInt(d, 0);
            case VTankNodeTypes.SpellMatch:
            {
                Regex? wanted = RegexCache.Get(d[0], RegexOptions.IgnoreCase);
                if (wanted is null) return false;
                Regex? unwanted = string.IsNullOrWhiteSpace(d[1]) ? null : RegexCache.Get(d[1], RegexOptions.IgnoreCase);
                int needed = ReadInt(d, 2);
                int count = 0;
                foreach (uint spellId in context.SpellIds(item))
                {
                    string name = context.SpellName(spellId);
                    if (name.Length == 0 || !wanted.IsMatch(name) || (unwanted is not null && unwanted.IsMatch(name)))
                        continue;
                    if (++count >= needed)
                        return true;
                }
                return false;
            }
            case VTankNodeTypes.MinDamageGE:
            {
                int max = LongValue(item, properties, (int)PropertyDamage);
                if (max == 0) return false;
                double variance = DoubleValue(item, properties, (int)PropertyDamageVariance);
                return max - variance * max >= ReadDouble(d, 0);
            }
            case VTankNodeTypes.BuffedMedianDamageGE:
            {
                int max = LongValue(item, properties, (int)PropertyDamage);
                if (max == 0) return false;
                double variance = DoubleValue(item, properties, (int)PropertyDamageVariance);
                double min = max - variance * max;
                return (min + max) / 2d >= ReadDouble(d, 0);
            }
            case VTankNodeTypes.BuffedMissileDamageGE:
            {
                int max = LongValue(item, properties, (int)PropertyDamage);
                if (max == 0) return false;
                double elemental = Math.Max(1d, DoubleValue(item, properties, (int)PropertyElementalDamageMod, 1d));
                return max * elemental >= ReadDouble(d, 0);
            }
            case VTankNodeTypes.CalcdBuffedTinkedDamageGE:
                return LongValue(item, properties, (int)PropertyDamage) >= ReadDouble(d, 0);
            case VTankNodeTypes.TotalRatingsGE:
            {
                int total = 0;
                foreach (uint key in new uint[] { 370u, 371u, 372u, 373u, 374u, 375u, 376u, 379u })
                    total += LongValue(item, properties, (int)key);
                return total >= ReadDouble(d, 0);
            }
            case VTankNodeTypes.AnySimilarColor:
            case VTankNodeTypes.SimilarColorArmorType:
            case VTankNodeTypes.SlotSimilarColor:
            case VTankNodeTypes.SlotExactPalette:
                return true; // colour rules need the palette data; pass optimistically
            case VTankNodeTypes.CharacterSkillGE:
                return context.Skill((uint)ReadInt(d, 1)).Buffed >= ReadInt(d, 0);
            case VTankNodeTypes.CharacterMainPackEmptySlotsGE:
                return context.MainPackFreeSlots >= ReadInt(d, 0);
            case VTankNodeTypes.CharacterLevelGE:
                return context.Level >= ReadInt(d, 0);
            case VTankNodeTypes.CharacterLevelLE:
                return context.Level <= ReadInt(d, 0);
            case VTankNodeTypes.CharacterBaseSkill:
            {
                int baseSkill = context.Skill((uint)ReadInt(d, 0)).Base;
                return baseSkill >= ReadInt(d, 1) && baseSkill <= ReadInt(d, 2);
            }
            case VTankNodeTypes.DisabledRule:
                return !(d.Count > 0 && d[0].Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    /// <summary>The Decal keys the item record answers without an appraisal.</summary>
    private static bool KnownBeforeAppraisal(int key) => key is DecalType or DecalIcon or DecalContainer
        or DecalItemSlots or DecalPackSlots or DecalStackCount or DecalStackMax or DecalAssociatedSpell
        or DecalSlot or DecalSlotLegacy or DecalWielder or DecalWieldingSlot or DecalEquipableSlots or DecalCategory;

    private static int LongValue(in PluginInventoryItem item, PluginItemProperties? properties, int key)
    {
        switch (key)
        {
            case DecalType: return (int)item.WeenieClassId;
            case DecalIcon: return (int)item.IconId;
            case DecalContainer: return (int)item.ContainerObjectId;
            case DecalItemSlots: return item.ItemsCapacity;
            case DecalPackSlots: return item.ContainersCapacity;
            case DecalStackCount: return item.StackSize;
            case DecalStackMax: return item.MaximumStackSize;
            case DecalAssociatedSpell: return (int)item.SpellId;
            case DecalSlot:
            case DecalSlotLegacy: return item.ContainerSlot;
            case DecalWielder: return (int)item.WielderObjectId;
            case DecalWieldingSlot: return (int)item.EquippedLocation;
            case DecalEquipableSlots: return (int)item.ValidLocations;
            case DecalCategory: return (int)item.ItemType;
            case DecalCoverage: return Int(properties, PropertyClothingPriority);
            case DecalDamageType: return item.DamageType != 0 ? item.DamageType : Int(properties, PropertyDamageType);
            case DecalMaxDamage: return item.Damage != 0 ? item.Damage : Int(properties, PropertyDamage);
            case DecalEquipSkill: return item.WeaponSkill != 0 ? item.WeaponSkill : Int(properties, PropertyWeaponSkill);
            case DecalWeaponSpeed: return Int(properties, PropertyWeaponTime);
            case DecalSpellCount: return item.AppraisedSpellIds.Count;
            case DecalIconOverlay: return DataId(properties, DataIconOverlay);
            case DecalIconUnderlay: return DataId(properties, DataIconUnderlay);
            case DecalLandblock:
            case DecalMonarch:
            case DecalEquipType:
            case DecalIconOutline:
            case DecalMissileType:
            case DecalUsageMask:
            case DecalBehavior:
            case DecalMagicDef:
            case DecalSpecialProps:
            case DecalActiveSpellCount:
                return 0;
        }
        if (properties is { } known && known.Ints.TryGetValue((uint)key, out int fromAppraisal))
            return fromAppraisal;
        return (uint)key switch
        {
            PropertyValue => item.Value,
            PropertyDamage => item.Damage,
            PropertyDamageType => item.DamageType,
            PropertyWeaponSkill => item.WeaponSkill,
            PropertyItemWorkmanship => (int)Math.Round(item.Workmanship),
            PropertyMaterialType => (int)item.MaterialType,
            PropertyStackSize => item.StackSize,
            PropertyMaxStackSize => item.MaximumStackSize,
            PropertyEncumbrance => item.Burden,
            _ => 0,
        };
    }

    private static double DoubleValue(in PluginInventoryItem item, PluginItemProperties? properties, int key, double fallback = 0d)
    {
        switch (key)
        {
            case DecalSalvageWorkmanship: return item.Workmanship;
            case DecalVariance: return item.DamageVariance != 0d ? item.DamageVariance : Float(properties, PropertyDamageVariance, fallback);
            case DecalAttackBonus: return Float(properties, PropertyWeaponOffense, fallback);
            case DecalDamageBonus: return Float(properties, PropertyDamageMod, fallback);
            case DecalSlashProtection: return Float(properties, PropertyArmorModVsSlash, fallback);
            case DecalPierceProtection: return Float(properties, PropertyArmorModVsPierce, fallback);
            case DecalBludgeonProtection: return Float(properties, PropertyArmorModVsBludgeon, fallback);
            case DecalAcidProtection: return Float(properties, PropertyArmorModVsAcid, fallback);
            case DecalLightningProtection: return Float(properties, PropertyArmorModVsElectric, fallback);
            case DecalFireProtection: return Float(properties, PropertyArmorModVsFire, fallback);
            case DecalColdProtection: return Float(properties, PropertyArmorModVsCold, fallback);
        }
        if (properties is { } known && known.Floats.TryGetValue((uint)key, out double value))
            return value;
        return (uint)key switch
        {
            PropertyDamageVariance => item.DamageVariance,
            PropertyItemWorkmanship => item.Workmanship,
            _ => fallback,
        };
    }

    private static string StringValue(in PluginInventoryItem item, PluginItemProperties? properties, int key)
    {
        if (key is 1 or DecalSecondaryName)
            return item.Name;
        if (properties is { } known && known.Strings.TryGetValue((uint)key, out string? value))
            return value;
        return string.Empty;
    }

    private static int Int(PluginItemProperties? properties, uint key) =>
        properties is { } known && known.Ints.TryGetValue(key, out int value) ? value : 0;

    private static double Float(PluginItemProperties? properties, uint key, double fallback) =>
        properties is { } known && known.Floats.TryGetValue(key, out double value) ? value : fallback;

    private static int DataId(PluginItemProperties? properties, uint key) =>
        properties is { } known && known.DataIds.TryGetValue(key, out uint value) ? (int)value : 0;

    private static int ReadInt(IList<string> d, int index) =>
        int.Parse(d[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ReadDouble(IList<string> d, int index) =>
        double.Parse(d[index].Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
}
