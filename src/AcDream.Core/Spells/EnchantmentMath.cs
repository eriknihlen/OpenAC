using System;
using System.Collections.Generic;

namespace AcDream.Core.Spells;

public static class EnchantmentMath
{
    public enum EnchantStepKind
    {
        /// <summary>The value is multiplied by the step's value.</summary>
        Multiply,

        /// <summary>The step's value is added to the value.</summary>
        Add,
    }

    /// <summary>One step an enchantment takes on a value, in single precision.</summary>
    public readonly record struct EnchantStep(EnchantStepKind Kind, float Value)
    {
        public static EnchantStep Multiply(float factor) => new(EnchantStepKind.Multiply, factor);

        public static EnchantStep Add(float amount) => new(EnchantStepKind.Add, amount);

        /// <summary>The step applied to <paramref name="value"/>, rounded to single precision.</summary>
        public float Apply(float value) => Kind == EnchantStepKind.Add
            ? Value + value
            : Value * value;

        /// <summary>
        /// The step a record takes: its type's additive bit adds (checked
        /// first), its multiplicative bit multiplies; a record with neither
        /// bit, or with no value, takes none.
        /// </summary>
        public static EnchantStep? Of(ActiveEnchantmentRecord record)
        {
            if (record.StatModValue is not float value) return null;
            uint type = record.StatModType.GetValueOrDefault();
            if ((type & (uint)EnchantmentTypeFlag.Additive) != 0) return Add(value);
            if ((type & (uint)EnchantmentTypeFlag.Multiplicative) != 0) return Multiply(value);
            return null;
        }
    }

    /// <summary>
    /// What the active enchantments do to one stat: the steps taken on its
    /// value, in order (vitae first, where it counts, then each record in
    /// effect). Every step rounds to single precision, so a truncated total
    /// can differ by one from scaling by the product of the factors.
    /// </summary>
    public readonly struct VitalMod : IEquatable<VitalMod>
    {
        private readonly EnchantStep[]? _steps;

        /// <summary>A multiplication followed by an addition.</summary>
        public VitalMod(float multiplier, float additive)
            : this([EnchantStep.Multiply(multiplier), EnchantStep.Add(additive)])
        {
        }

        private VitalMod(EnchantStep[] steps)
        {
            _steps = steps.Length == 0 ? null : steps;
        }

        /// <summary>No enchantment applies.</summary>
        public static VitalMod Identity => default;

        /// <summary>These steps, in this order.</summary>
        public static VitalMod FromSteps(IEnumerable<EnchantStep> steps)
        {
            ArgumentNullException.ThrowIfNull(steps);
            return new VitalMod([.. steps]);
        }

        /// <summary>The steps, in the order they are taken.</summary>
        public IReadOnlyList<EnchantStep> Steps => _steps ?? [];

        /// <summary>The product of the multiplications (1 with none).</summary>
        public float Multiplier
        {
            get
            {
                float product = 1.0f;
                foreach (EnchantStep step in _steps ?? [])
                    if (step.Kind == EnchantStepKind.Multiply) product *= step.Value;
                return product;
            }
        }

        /// <summary>The sum of the additions (0 with none).</summary>
        public float Additive
        {
            get
            {
                float sum = 0.0f;
                foreach (EnchantStep step in _steps ?? [])
                    if (step.Kind == EnchantStepKind.Add) sum += step.Value;
                return sum;
            }
        }

        /// <summary><paramref name="value"/> after every step in turn.</summary>
        public float Apply(float value)
        {
            if (_steps is null) return value;
            foreach (EnchantStep step in _steps)
                value = step.Apply(value);
            return value;
        }

        public bool Equals(VitalMod other) =>
            ((ReadOnlySpan<EnchantStep>)_steps).SequenceEqual(other._steps);

        public override bool Equals(object? obj) => obj is VitalMod other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (EnchantStep step in Steps) hash.Add(step);
            return hash.ToHashCode();
        }

        public override string ToString() =>
            $"VitalMod [{string.Join(", ", Steps)}]";

        public static bool operator ==(VitalMod left, VitalMod right) => left.Equals(right);

        public static bool operator !=(VitalMod left, VitalMod right) => !left.Equals(right);
    }

    [Flags]
    public enum EnchantmentTypeFlag : uint
    {
        Attribute = 0x0000001,

        /// <summary>Secondary attribute — vital max (MaxHealth/MaxStamina/
        /// MaxMana). The vital-maximum query filters on this bit and applies
        /// vitae first.</summary>
        SecondAtt = 0x0000002,

        /// <summary>Skill. The skill query filters on this bit and applies
        /// vitae first.</summary>
        Skill = 0x0000010,

        /// <summary>Applies to every key of the queried stat family instead
        /// of one key.</summary>
        MultipleStat = 0x0002000,

        /// <summary>Multiplies the stat by its value.</summary>
        Multiplicative = 0x0004000,

        /// <summary>Adds its value to the stat.</summary>
        Additive = 0x0008000,

        /// <summary>Applies to every attack skill.</summary>
        AttackSkills = 0x0010000,

        /// <summary>Applies to every defense skill.</summary>
        DefenseSkills = 0x0020000,
    }

    /// <summary>
    /// The modifier the active enchantments give one stat.
    /// <paramref name="enchantments"/> are in list order: each list (the
    /// multiplicative, the additive and the vitae records) from its head.
    /// With <paramref name="includeVitae"/>, each vitae record multiplies
    /// first; then the records in effect for the stat
    /// (<see cref="CullEnchantments"/>) take their steps
    /// (<see cref="EnchantStep.Of"/>) in the order they ended up in.
    /// </summary>
    public static VitalMod GetMod(
        IEnumerable<ActiveEnchantmentRecord> enchantments,
        uint statKey,
        EnchantmentTypeFlag family,
        bool includeVitae = true)
    {
        ArgumentNullException.ThrowIfNull(enchantments);
        ActiveEnchantmentRecord[] source = enchantments as ActiveEnchantmentRecord[]
            ?? [.. enchantments];
        var steps = new List<EnchantStep>();
        if (includeVitae)
        {
            foreach (ActiveEnchantmentRecord record in source)
            {
                if (record.Bucket == 4u && record.StatModValue is float penalty)
                    steps.Add(EnchantStep.Multiply(penalty));
            }
        }
        foreach (ActiveEnchantmentRecord record in CullEnchantments(source, family, statKey))
        {
            if (EnchantStep.Of(record) is EnchantStep step)
                steps.Add(step);
        }
        return VitalMod.FromSteps(steps);
    }

    /// <summary>
    /// The records in effect for one stat. The records that apply to it
    /// (<see cref="AffectsStat"/>) are taken from the multiplicative list,
    /// then from the additive list, each walked in order, into one list that
    /// holds one record per spell category (category 0 included). So a
    /// multiplication and an addition of one category compete, and the local
    /// spell table plays no part: a spell it lacks counts like any other.
    /// </summary>
    public static IReadOnlyList<ActiveEnchantmentRecord> CullEnchantments(
        IReadOnlyList<ActiveEnchantmentRecord> enchantments,
        EnchantmentTypeFlag family,
        uint statKey)
    {
        ArgumentNullException.ThrowIfNull(enchantments);
        var inEffect = new List<ActiveEnchantmentRecord>();
        CullList(enchantments, bucket: 1u, family, statKey, inEffect);
        CullList(enchantments, bucket: 2u, family, statKey, inEffect);
        return inEffect;
    }

    private static void CullList(
        IReadOnlyList<ActiveEnchantmentRecord> enchantments,
        uint bucket,
        EnchantmentTypeFlag family,
        uint statKey,
        List<ActiveEnchantmentRecord> inEffect)
    {
        for (int index = 0; index < enchantments.Count; index++)
        {
            ActiveEnchantmentRecord record = enchantments[index];
            if (record.Bucket == bucket && AffectsStat(record, family, statKey))
                EnchantmentRegistryProjection.Duel(record, inEffect);
        }
    }

    /// <summary>
    /// Whether a record applies to the stat <paramref name="statKey"/> of
    /// <paramref name="family"/> (attribute, vital maximum or skill): its
    /// type must name the family, and then it applies when it is a
    /// multiple-stat record (whatever its key), when its key is the stat's,
    /// or when it raises all attack or all defense skills and the stat is
    /// one of them.
    /// </summary>
    public static bool AffectsStat(
        ActiveEnchantmentRecord record,
        EnchantmentTypeFlag family,
        uint statKey)
    {
        uint type = record.StatModType.GetValueOrDefault();
        if ((type & (uint)family) == 0) return false;
        return (type & (uint)EnchantmentTypeFlag.MultipleStat) != 0
            || record.StatModKey == statKey
            || AffectsAttackSkills(record, statKey)
            || AffectsDefenseSkills(record, statKey);
    }

    /// <summary>
    /// Whether an all-attack-skills record raises the skill: life magic,
    /// war magic, two-handed combat, void magic, heavy, light and finesse
    /// weapons, missile weapons and dual wield.
    /// </summary>
    public static bool AffectsAttackSkills(ActiveEnchantmentRecord record, uint skillId) =>
        (record.StatModType.GetValueOrDefault() & (uint)EnchantmentTypeFlag.AttackSkills) != 0
        && skillId is 33u or 34u or 41u or 43u or 44u or 45u or 46u or 47u or 49u;

    /// <summary>
    /// Whether an all-defense-skills record raises the skill: melee defense,
    /// missile defense, magic defense and shield.
    /// </summary>
    public static bool AffectsDefenseSkills(ActiveEnchantmentRecord record, uint skillId) =>
        (record.StatModType.GetValueOrDefault() & (uint)EnchantmentTypeFlag.DefenseSkills) != 0
        && skillId is 6u or 7u or 15u or 48u;

    /// <summary>
    /// An attribute after enchantments: the modifier's steps on the base,
    /// floored at 1 for a base below 10 and at 10 otherwise, then truncated.
    /// </summary>
    public static int EnchantAttribute(VitalMod mod, uint baseValue)
    {
        float value = mod.Apply(baseValue);
        float floor = baseValue < 10u ? 1f : 10f;
        if (value < floor) value = floor;
        return (int)value;
    }

    /// <summary>
    /// A skill after enchantments: the modifier's steps on the base, zero
    /// below one half, then truncated.
    /// </summary>
    public static int EnchantSkill(VitalMod mod, uint baseValue)
    {
        float value = mod.Apply(baseValue);
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

    /// <summary>The modifier a skill gets: <see cref="GetMod"/> for the skill family, vitae included.</summary>
    public static VitalMod GetSkillMod(
        IEnumerable<ActiveEnchantmentRecord> enchantments,
        uint skillId) =>
        GetMod(enchantments, skillId, EnchantmentTypeFlag.Skill);

    public static class StatKey
    {
        public const uint MaxHealth  = 1;
        public const uint MaxStamina = 3;
        public const uint MaxMana    = 5;
    }
}
