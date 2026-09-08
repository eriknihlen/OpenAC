using System;
using System.Collections.Generic;

namespace AcDream.Core.Spells;

public static class EnchantmentMath
{
    public readonly record struct VitalMod(float Multiplier, float Additive)
    {
        /// <summary>Identity modifier — <c>(1.0, 0.0)</c>. No active
        /// buffs apply.</summary>
        public static readonly VitalMod Identity = new(1.0f, 0.0f);
    }

    [Flags]
    public enum EnchantmentTypeFlag : uint
    {
        Attribute = 0x0000001,

        /// <summary>Secondary attribute — vital max (MaxHealth/MaxStamina/
        /// MaxMana). <c>EnchantAttribute2nd</c> filters on this bit and DOES
        /// apply vitae first.</summary>
        SecondAtt = 0x0000002,

        /// <summary>Skill. <c>EnchantSkill</c> filters on this bit and DOES
        /// apply vitae first.</summary>
        Skill = 0x0000010,
    }

    public static VitalMod GetMod(
        IEnumerable<ActiveEnchantmentRecord> enchantments,
        SpellTable table,
        uint statKey,
        EnchantmentTypeFlag? requiredType = null,
        bool includeVitae = true)
    {
        var stronger = new Dictionary<uint, ActiveEnchantmentRecord>();
        foreach (var ench in enchantments)
        {
            if (!table.TryGet(ench.SpellId, out var meta))
            {
                if (ench.Bucket == 4)
                {
                    string droppedValue = ench.StatModValue is float dv
                        ? dv.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                        : "NULL";
                    System.Console.WriteLine(
                        System.FormattableString.Invariant(
                            $"[stat-chain] VITAE DROPPED by spell-table lookup: spell={ench.SpellId} value={droppedValue}"));
                }
                continue;
            }
            // Family 0 means "no stacking bucket" — these don't dedup;
            // pass them through with a synthetic key per layer.
            uint bucket = meta.Family == 0 ? ench.LayerId | 0x80000000u : meta.Family;
            if (!stronger.TryGetValue(bucket, out var current) ||
                ench.SpellId > current.SpellId)
            {
                stronger[bucket] = ench;
            }
        }

        float multiplier = 1.0f;
        float additive = 0.0f;
        float vitae = 1.0f;
        foreach (var ench in stronger.Values)
        {
            if (ench.StatModValue is not float val) continue;

            if (ench.Bucket == 4)
            {
                if (includeVitae) vitae *= val;
                continue;
            }

            if (ench.StatModKey is not uint key || key != statKey) continue;
            if (requiredType is EnchantmentTypeFlag type
                && (ench.StatModType.GetValueOrDefault() & (uint)type) == 0)
                continue;
            switch (ench.Bucket)
            {
                case 1: multiplier *= val; break;
                case 2: additive += val; break;
            }
        }
        multiplier *= vitae;
        return multiplier == 1.0f && additive == 0.0f
            ? VitalMod.Identity
            : new VitalMod(multiplier, additive);
    }

    public static int EnchantAttribute(VitalMod mod, uint baseValue)
    {
        float value = (baseValue * mod.Multiplier) + mod.Additive;
        float floor = baseValue < 10u ? 1f : 10f;
        if (value < floor) value = floor;
        return (int)value;
    }

    public static int EnchantSkill(VitalMod mod, uint baseValue)
    {
        float value = (baseValue * mod.Multiplier) + mod.Additive;
        if (value < 0.5f) value = 0f;
        return (int)value;
    }

    public static float GetVitaeMultiplier(IEnumerable<ActiveEnchantmentRecord> enchantments)
    {
        ArgumentNullException.ThrowIfNull(enchantments);
        float vitae = 1.0f;
        foreach (ActiveEnchantmentRecord ench in enchantments)
        {
            if (ench.Bucket == 4 && ench.StatModValue is float val)
                vitae *= val;
        }
        return vitae;
    }

    public static int SkillVitaeModifier(
        IEnumerable<ActiveEnchantmentRecord> enchantments,
        uint baseValue)
        => SkillVitaeModifier(GetVitaeMultiplier(enchantments), baseValue);

    public static int SkillVitaeModifier(float vitaeMultiplier, uint baseValue)
    {
        if (vitaeMultiplier == 1.0f) return 0;
        return (int)(baseValue * vitaeMultiplier) - (int)baseValue;
    }

    public static VitalMod GetSkillMod(
        IEnumerable<ActiveEnchantmentRecord> enchantments,
        SpellTable table,
        uint skillId) =>
        GetMod(enchantments, table, skillId, EnchantmentTypeFlag.Skill);

    public static class StatKey
    {
        public const uint MaxHealth  = 1;
        public const uint MaxStamina = 3;
        public const uint MaxMana    = 5;
    }
}
