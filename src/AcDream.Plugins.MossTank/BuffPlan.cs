using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

/// <summary>Settings that shape a buff pass. Defaults follow Virindi Tank's.</summary>
public sealed class BuffSettings
{
    public bool Enabled { get; set; } = true;
    public bool IdleBuffTopoff { get; set; }
    public double IdleBuffTopoffSeconds { get; set; } = 1200.0;
    /// <summary>
    /// VTank recasts buffs once they drop below five minutes remaining
    /// ("all buff spells are recast when they go below 5 minutes").
    /// </summary>
    public double RebuffWhenUnderSeconds { get; set; } = 300.0;
    public double BuffCastRecastSeconds { get; set; } = 30d;
    public double BuffCastRecastResetSeconds { get; set; } = 30d;
    public bool FastCastBuffs { get; set; }
    public bool RandomHelperBuffs { get; set; }
    public double RandomHelperIntervalSeconds { get; set; } = 5d;
    public string BlacklistedSpellComponents { get; set; } = string.Empty;

    public int SkillExcessOverDifficulty { get; set; } = 5;

    /// <summary>Buff every attribute (VTank's default).</summary>
    public bool BuffAttributes { get; set; } = true;

    public bool BuffProtections { get; set; } = true;
    public string ProtectionElements { get; set; } = "ALFCBPS";
    public int ProtectionProfileMode { get; set; } = 2;

    public bool BuffAuras { get; set; } = true;

    public bool BuffBanes { get; set; } = true;
    public string BaneElements { get; set; } = "ALFCBPS";
    public int BaneProfileMode { get; set; } = 2;

    public bool BuffRegeneration { get; set; } = true;

    public bool BuffOther { get; set; }

    /// <summary>
    /// Buff trained and specialised skills only — VTank's stated default:
    /// "automatically buffs every Attribute and Skill you have trained".
    /// </summary>
    public bool BuffTrainedSkillsOnly { get; set; } = true;

    public int BuffWithUntrainedItemSkill { get; set; } = 80;
    public int BuffWithUntrainedCreatureSkill { get; set; } = 80;
    public int BuffWithUntrainedLifeSkill { get; set; } = 80;

    public ISet<string> ExtraBuffSpellNames { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public ISet<string> BlacklistedBuffFamilyNames { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    internal IList<BuffItemEnchantRow> ItemEnchantRows { get; } =
        new List<BuffItemEnchantRow>();
}

public interface IBuffCastability
{
    /// <summary><c>item.HasScarabsInInventory &amp;&amp; !b(item)</c>.</summary>
    bool IsCastable(PluginSpellInfo tier);

    bool IsCastable(PluginSpellInfo tier, out string? reason)
    {
        reason = null;
        return IsCastable(tier);
    }

    /// <summary><c>eq.cs:504-508</c>'s once-per-run "buff SKIPPED." line.</summary>
    void NoteNoCastableTier(BuffLine line);

    void NoteTierPick(
        BuffLine line, PluginSpellInfo? pick, IReadOnlyList<BuffTierRejection> rejections)
    {
    }
}

public readonly record struct BuffTierRejection(PluginSpellInfo Spell, string Reason);

public static class BuffPlan
{
    public static List<PluginSpellInfo> Build(
        IReadOnlyList<BuffLine> lines,
        IReadOnlyList<PluginSkillInfo> skills,
        IReadOnlyList<PluginAttributeInfo> attributes,
        IReadOnlyList<PluginActiveEnchantment> active,
        BuffSettings settings,
        bool force = false,
        double? rebuffWhenUnderSeconds = null,
        int characterLevel = 0,
        IReadOnlySet<uint>? forcedSpellIds = null,
        IBuffCastability? castability = null)
    {
        if (!settings.Enabled)
            return [];
        var trainedSkills = new Dictionary<string, PluginSkillInfo>(
            StringComparer.OrdinalIgnoreCase);
        foreach (PluginSkillInfo skill in skills)
        {
            if (!settings.BuffTrainedSkillsOnly
                || skill.Training is PluginSkillTraining.Trained
                    or PluginSkillTraining.Specialized)
            {
                trainedSkills[skill.Name] = skill;
            }
        }

        var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginAttributeInfo attribute in attributes)
            attributeNames.Add(attribute.Name);

        var inForce = new Dictionary<uint, List<(int Tier, double Seconds)>>();
        foreach (PluginActiveEnchantment enchantment in active)
        {
            if (enchantment.Family == 0)
                continue;
            double seconds = force
                || (forcedSpellIds is not null
                    && forcedSpellIds.Contains(enchantment.SpellId))
                ? 0d
                : enchantment.SecondsRemaining;
            if (!inForce.TryGetValue(enchantment.Family, out var entries))
            {
                entries = [];
                inForce[enchantment.Family] = entries;
            }
            entries.Add((enchantment.Tier, seconds));
        }

        var skillLevels = new Dictionary<uint, uint>();
        foreach (PluginSkillInfo skill in skills)
            skillLevels[skill.SkillId] = skill.Current;

        var plan = new List<(int Rank, PluginSpellInfo Spell)>();

        foreach (BuffLine line in lines)
        {
            uint school = line.Tiers.Count == 0 ? 0u : line.Tiers[0].School;
            bool schoolAvailable = IsSchoolAvailable(
                school,
                skills,
                settings,
                characterLevel);
            bool wanted = line.Kind switch
            {
                BuffTargetKind.Attribute =>
                    schoolAvailable && settings.BuffAttributes
                        && attributeNames.Contains(line.TargetName),
                BuffTargetKind.Skill => schoolAvailable
                    && (trainedSkills.ContainsKey(line.TargetName)
                        || IsMagicSchoolName(line.TargetName)),
                BuffTargetKind.Protection =>
                    schoolAvailable && settings.BuffProtections
                        && ProfileAllows(line, settings),
                BuffTargetKind.Aura => false,
                BuffTargetKind.Bane => false,
                BuffTargetKind.Regeneration =>
                    schoolAvailable && settings.BuffRegeneration,
                BuffTargetKind.Other => schoolAvailable && settings.BuffOther,
                _ => false,
            };
            if (!wanted)
                continue;

            bool resolved = TryPickTier(
                line, skillLevels, settings, castability, out PluginSpellInfo pick);
            double threshold = rebuffWhenUnderSeconds
                ?? settings.RebuffWhenUnderSeconds;
            if (inForce.TryGetValue(line.Family, out var held)
                && IsCovered(held, resolved ? pick.Tier : -1, threshold))
            {
                continue;
            }

            if (!resolved)
            {
                castability?.NoteNoCastableTier(line);
                continue;
            }

            plan.Add((CastRank(line, pick), pick));
        }

        plan.Sort(static (a, b) =>
        {
            if (a.Rank != b.Rank)
                return a.Rank.CompareTo(b.Rank);
            if (a.Spell.ManaCost != b.Spell.ManaCost)
                return a.Spell.ManaCost.CompareTo(b.Spell.ManaCost);
            return a.Spell.SpellId.CompareTo(b.Spell.SpellId);
        });

        var ordered = new List<PluginSpellInfo>(plan.Count);
        foreach ((int _, PluginSpellInfo spell) in plan)
            ordered.Add(spell);
        return ordered;
    }

    private static bool IsCovered(
        List<(int Tier, double Seconds)> entries,
        int quality,
        double thresholdSeconds)
    {
        foreach ((int tier, double seconds) in entries)
        {
            if (tier >= quality && seconds >= thresholdSeconds)
                return true;
        }
        return false;
    }

    private static bool ProfileAllows(
        BuffLine line,
        BuffSettings settings)
    {
        string enabled = settings.ProtectionProfileMode switch
        {
            1 => settings.ProtectionElements,
            2 => "ALFCBPS",
            3 => string.Empty,
            4 => "B",
            5 => "BPS",
            6 => "BPSA",
            7 => "ALFC",
            8 => "BPSAC",
            _ => string.Empty,
        };
        char element = ElementCode(line);
        return element == '\0' || enabled.IndexOf(element) >= 0;
    }

    private static char ElementCode(BuffLine line)
    {
        string text = line.TargetName + " "
            + (line.Tiers.Count == 0 ? string.Empty : line.Tiers[0].Name)
            + " "
            + (line.Tiers.Count == 0 ? string.Empty : line.Tiers[0].Description);
        if (text.Contains("acid", StringComparison.OrdinalIgnoreCase))
            return 'A';
        if (text.Contains("lightning", StringComparison.OrdinalIgnoreCase)
            || text.Contains("electric", StringComparison.OrdinalIgnoreCase))
            return 'L';
        if (text.Contains("fire", StringComparison.OrdinalIgnoreCase))
            return 'F';
        if (text.Contains("cold", StringComparison.OrdinalIgnoreCase)
            || text.Contains("frost", StringComparison.OrdinalIgnoreCase))
            return 'C';
        if (text.Contains("bludgeon", StringComparison.OrdinalIgnoreCase))
            return 'B';
        if (text.Contains("pierc", StringComparison.OrdinalIgnoreCase))
            return 'P';
        if (text.Contains("slash", StringComparison.OrdinalIgnoreCase))
            return 'S';
        return '\0';
    }

    private static bool IsMagicSchoolName(string name) =>
        name.Equals("Item Enchantment", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Creature Enchantment", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Life Magic", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSchoolAvailable(
        uint school,
        IReadOnlyList<PluginSkillInfo> skills,
        BuffSettings settings,
        int characterLevel)
    {
        if (school is not (ItemEnchantmentSkill
                or CreatureEnchantmentSkill
                or LifeMagicSkill))
        {
            return true;
        }
        foreach (PluginSkillInfo skill in skills)
        {
            if (skill.SkillId == school
                && skill.Training is PluginSkillTraining.Trained
                    or PluginSkillTraining.Specialized)
            {
                return true;
            }
        }
        int limit = school switch
        {
            ItemEnchantmentSkill => settings.BuffWithUntrainedItemSkill,
            CreatureEnchantmentSkill => settings.BuffWithUntrainedCreatureSkill,
            LifeMagicSkill => settings.BuffWithUntrainedLifeSkill,
            _ => int.MaxValue,
        };
        return characterLevel <= limit;
    }

    private const uint CreatureEnchantmentSkill = 31;
    private const uint ItemEnchantmentSkill = 32;
    private const uint LifeMagicSkill = 33;

    public static int CastRank(BuffLine line, PluginSpellInfo pick)
    {
        int school = pick.School switch
        {
            CreatureEnchantmentSkill => 0,
            ItemEnchantmentSkill => 1,
            LifeMagicSkill => 2,
            _ => 3,   // war/void and anything unschooled trail the rest
        };

        int within = school switch
        {
            0 => CreatureOrder(line),
            2 => LifeOrder(line),
            _ => 0,   // the item group has no internal order
        };
        return (school * 10) + within;
    }

    private static int LifeOrder(BuffLine line) =>
        line.Kind == BuffTargetKind.Regeneration ? 1 : 0;

    private static int CreatureOrder(BuffLine line)
    {
        if (line.Kind == BuffTargetKind.Skill
            && Named(line.TargetName, "Creature Enchantment"))
        {
            return 0;
        }

        if (line.Kind == BuffTargetKind.Attribute)
        {
            if (Named(line.TargetName, "Focus"))
                return 1;
            if (Named(line.TargetName, "Self"))
                return 2;
            if (Named(line.TargetName, "Endurance"))
                return 3;
        }

        return 4;
    }

    private static bool Named(string target, string name) =>
        string.Equals(target, name, StringComparison.OrdinalIgnoreCase);

    public static bool TryPickTier(
        BuffLine line,
        IReadOnlyDictionary<uint, uint> skillLevels,
        BuffSettings settings,
        out PluginSpellInfo pick) =>
        TryPickTier(line, skillLevels, settings, castability: null, out pick);

    public static bool TryPickTier(
        BuffLine line,
        IReadOnlyDictionary<uint, uint> skillLevels,
        BuffSettings settings,
        IBuffCastability? castability,
        out PluginSpellInfo pick) =>
        TryPickTier(
            line,
            skillLevels,
            settings.SkillExcessOverDifficulty,
            castability,
            out pick);

    public static bool TryPickTier(
        BuffLine line,
        IReadOnlyDictionary<uint, uint> skillLevels,
        int skillExcessOverDifficulty,
        IBuffCastability? castability,
        out PluginSpellInfo pick)
    {
        pick = default;
        if (line.Tiers.Count == 0)
            return false;

        // fk.cs:189's first five terms, all against the family's reference
        // spell (fk.a's A_0). See MatchesReference.
        PluginSpellInfo reference = line.Reference;

        List<BuffTierRejection>? rejections = castability is null ? null : [];

        PluginSpellInfo weakest = default;
        bool haveWeakest = false;
        foreach (PluginSpellInfo tier in line.Tiers)
        {
            if (!MatchesReference(tier, reference, out string? mismatchReason))
            {
                rejections?.Add(new BuffTierRejection(tier, mismatchReason!));
                continue;
            }
            weakest = tier;
            haveWeakest = true;
            if (tier.School == 0 || !skillLevels.TryGetValue(tier.School, out uint level))
            {
                rejections?.Add(new BuffTierRejection(tier, "skill unknown"));
                continue;
            }
            int needed = tier.Difficulty + skillExcessOverDifficulty;
            if (level < needed)
            {
                rejections?.Add(new BuffTierRejection(tier, $"skill {level} < {needed}"));
                continue;
            }
            if (castability is not null && !castability.IsCastable(tier, out string? castReason))
            {
                rejections?.Add(new BuffTierRejection(tier, castReason ?? "not castable"));
                continue;
            }
            pick = tier;
            castability?.NoteTierPick(line, pick, rejections!);
            return true;
        }

        if (!haveWeakest)
        {
            // nothing in the family shares the reference's line
            castability?.NoteTierPick(line, null, rejections!);
            return false;
        }
        if (weakest.School != 0 && skillLevels.ContainsKey(weakest.School))
        {
            // school known, but even the weakest tier is out of reach
            castability?.NoteTierPick(line, null, rejections!);
            return false;
        }
        if (castability is not null && !castability.IsCastable(weakest, out _))
        {
            castability.NoteTierPick(line, null, rejections!);
            return false;
        }

        pick = weakest;
        castability?.NoteTierPick(line, pick, rejections!);
        return true;
    }

    private static bool MatchesReference(
        in PluginSpellInfo tier,
        in PluginSpellInfo reference,
        out string? mismatchReason)
    {
        if (tier.School != reference.School
            || tier.IsFellowship != reference.IsFellowship
            || tier.IsUntargeted != reference.IsUntargeted)
        {
            mismatchReason = "unknown";
            return false;
        }
        if (tier.ComponentSet != reference.ComponentSet)
        {
            mismatchReason = "comp set";
            return false;
        }
        mismatchReason = null;
        return true;
    }
}
