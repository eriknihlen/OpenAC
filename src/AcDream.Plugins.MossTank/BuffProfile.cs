using System.Text.RegularExpressions;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

/// <summary>What a buff line does, which is also how it is toggled.</summary>
public enum BuffTargetKind
{
    Unknown = 0,
    Skill,
    Attribute,
    /// <summary>
    /// Defensive self-buff: the elemental/physical protections, and Armor Self.
    /// </summary>
    Protection,
    Aura,
    Bane,
    Regeneration,
    /// <summary>Any other self-targeted duration buff.</summary>
    Other,
}

/// <summary>One buff line: a family, what it does, and its known tiers.</summary>
public sealed record BuffLine(
    uint Family,
    BuffTargetKind Kind,
    string TargetName,
    List<PluginSpellInfo> Tiers);

public static partial class BuffProfile
{
    [GeneratedRegex(
        @"^Increases (?:the caster's|your) (?<target>.+?)(?<skill>\s+skill)? by ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IncreasesPattern();

    [GeneratedRegex(
        @"^Reduces damage (?:the caster|you) takes? from (?<target>.+?) by ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtectionPattern();

    [GeneratedRegex(
        @"\b(a weapon's|weapon or magic caster|magic caster|missile weapon's|magic casting implement's)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuraPattern();

    [GeneratedRegex(
        @"Target yourself to cast this spell on all of your equipped",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BanePattern();

    [GeneratedRegex(
        "natural healing rate|Health Regeneration Rate",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HealthRegenPattern();

    [GeneratedRegex(
        "rate at which the caster regains Stamina|Stamina Regeneration Rate",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StaminaRegenPattern();

    [GeneratedRegex(
        "natural mana rate|Mana Regeneration Rate",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ManaRegenPattern();

    private static readonly Dictionary<string, string> SkillNameAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Assess Monster"] = "Assess Creature",
        };

    private static readonly HashSet<string> AttributeNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Strength", "Endurance", "Quickness", "Coordination", "Focus", "Self",
        };

    public static List<BuffLine> Build(IReadOnlyList<PluginSpellInfo> knownSelfBuffs)
    {
        var byFamily = new Dictionary<uint, BuffLine>();

        foreach (PluginSpellInfo spell in knownSelfBuffs)
        {
            // Instantaneous spells (the vital transfers, heals) are not buffs;
            // they also share families across unrelated lines, so grouping them
            // by family would be wrong twice over.
            if (spell.DurationSeconds <= 0f)
                continue;

            Classify(spell.Description, out BuffTargetKind kind, out string target);
            if (kind == BuffTargetKind.Unknown)
            {
                // Nothing self-targeted is discarded for being unrecognised.
                // Dropping what the patterns do not match is how protections
                // and weapon auras went missing without a word; an unknown
                // spell belongs in Other, which the user can switch on.
                kind = BuffTargetKind.Other;
                target = spell.Name;
            }

            if (!byFamily.TryGetValue(spell.Family, out BuffLine? line))
            {
                line = new BuffLine(spell.Family, kind, target, new List<PluginSpellInfo>());
                byFamily.Add(spell.Family, line);
            }
            line.Tiers.Add(spell);
        }

        foreach (BuffLine line in byFamily.Values)
            line.Tiers.Sort(static (a, b) => b.Tier.CompareTo(a.Tier));   // strongest first

        return byFamily.Values.ToList();
    }

    public static void Classify(
        string? description, out BuffTargetKind kind, out string target)
    {
        kind = BuffTargetKind.Unknown;
        target = string.Empty;
        if (string.IsNullOrWhiteSpace(description))
            return;

        // Banes first: their text also mentions armour resistance, and the
        // "target yourself" instruction is what actually identifies them.
        if (BanePattern().IsMatch(description))
        {
            kind = BuffTargetKind.Bane;
            target = "equipped armor";
            return;
        }

        if (HealthRegenPattern().IsMatch(description))
        {
            kind = BuffTargetKind.Regeneration;
            target = "Health";
            return;
        }

        if (StaminaRegenPattern().IsMatch(description))
        {
            kind = BuffTargetKind.Regeneration;
            target = "Stamina";
            return;
        }

        if (ManaRegenPattern().IsMatch(description))
        {
            kind = BuffTargetKind.Regeneration;
            target = "Mana";
            return;
        }

        if (AuraPattern().IsMatch(description))
        {
            kind = BuffTargetKind.Aura;
            target = "weapon";
            return;
        }

        Match protection = ProtectionPattern().Match(description);
        if (protection.Success)
        {
            kind = BuffTargetKind.Protection;
            target = protection.Groups["target"].Value.Trim();
            return;
        }

        Match increases = IncreasesPattern().Match(description);
        if (!increases.Success)
            return;

        target = increases.Groups["target"].Value.Trim();
        if (target.Length == 0)
            return;

        if (increases.Groups["skill"].Success)
        {
            kind = BuffTargetKind.Skill;
            if (SkillNameAliases.TryGetValue(target, out string? alias))
                target = alias;
            return;
        }

        if (AttributeNames.Contains(target))
        {
            kind = BuffTargetKind.Attribute;
            return;
        }

        kind = target.Contains("armor", StringComparison.OrdinalIgnoreCase)
            ? BuffTargetKind.Protection
            : BuffTargetKind.Other;
    }

    public static bool TryParseTarget(
        string? description, out BuffTargetKind kind, out string target)
    {
        Classify(description, out kind, out target);
        return kind != BuffTargetKind.Unknown;
    }
}
