using System.Text.RegularExpressions;

namespace AcDream.DrakBot.Combat;

/// <summary>The shape of war spell a rule casts; a ring overrides it when enough hostiles crowd in.</summary>
public enum SpellShape
{
    Bolt = 0,
    Arc,
    Streak,
}

/// <summary>
/// What to do about one kind of monster, the way a VTank monster list says
/// it: whether and how eagerly to fight it, the element and shape of the
/// war spell, and the debuffs to land first. <see cref="Name"/> is matched
/// against the monster's name as a regular expression, falling back to a
/// case-insensitive substring when it is not a valid one. A rule named
/// <c>Default</c> covers everything no other rule matches.
/// </summary>
public sealed record MonsterRule
{
    public const string DefaultName = "Default";

    public string Name { get; init; } = "New Monster";

    /// <summary>Fought first when higher; zero means never fought.</summary>
    public int Priority { get; init; } = 1;

    /// <summary>Fire, Cold, Lightning, Acid, Blade, Pierce, Bludgeon, Nether, or Auto for the profile's element.</summary>
    public string Element { get; init; } = "Auto";

    public SpellShape Shape { get; init; } = SpellShape.Bolt;

    /// <summary>Cast a ring instead when at least the profile's minimum ring targets are within ring range.</summary>
    public bool UseRing { get; init; }

    public bool Imperil { get; init; }

    /// <summary>The vulnerability matching the war element.</summary>
    public bool Vulnerability { get; init; }

    /// <summary>A second vulnerability of another element, or empty.</summary>
    public string ExtraVulnerability { get; init; } = string.Empty;

    public bool Fester { get; init; }

    public bool Yield { get; init; }

    public bool Broadside { get; init; }

    public bool GravityWell { get; init; }

    /// <summary>A weapon to wield for this monster, by name; empty keeps the style's.</summary>
    public string Weapon { get; init; } = string.Empty;

    public bool IsDefault => Name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase);

    public bool Matches(string monsterName)
    {
        if (IsDefault)
            return false;
        Regex? pattern = RegexFor(Name);
        return pattern is not null
            ? pattern.IsMatch(monsterName)
            : monsterName.Contains(Name, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, Regex?> Patterns = new(StringComparer.Ordinal);

    private static Regex? RegexFor(string name)
    {
        lock (Patterns)
        {
            if (Patterns.TryGetValue(name, out Regex? cached))
                return cached;
            Regex? pattern;
            try
            {
                pattern = new Regex(name, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                pattern = null;
            }
            if (Patterns.Count > 512)
                Patterns.Clear();
            Patterns[name] = pattern;
            return pattern;
        }
    }
}

/// <summary>The debuffs a rule can ask for, in the order they are landed.</summary>
public enum DebuffKind
{
    Imperil,
    Vulnerability,
    Fester,
    Yield,
    Broadside,
    GravityWell,
}

public static class MonsterRules
{
    /// <summary>
    /// The rule for a monster: the first matching one, else the Default
    /// rule. There is always a Default - the list is a list of exceptions
    /// to it, and a monster nothing names is fought its way - so a list
    /// without one written down answers with the built-in one, which
    /// fights with the profile's settings.
    /// </summary>
    public static MonsterRule For(IReadOnlyList<MonsterRule> rules, string monsterName)
    {
        MonsterRule? fallback = null;
        foreach (MonsterRule rule in rules)
        {
            if (rule.IsDefault)
            {
                fallback ??= rule;
                continue;
            }
            if (rule.Matches(monsterName))
                return rule;
        }
        return fallback ?? Default;
    }

    /// <summary>The Default rule a list has when none is written in it.</summary>
    public static MonsterRule Default { get; } = new() { Name = MonsterRule.DefaultName };

    /// <summary>
    /// The list with its Default rule first and exactly once, as the
    /// Monsters window shows it: the Default cannot be deleted, only set
    /// to priority zero to leave unlisted monsters alone.
    /// </summary>
    public static List<MonsterRule> WithDefaultFirst(IReadOnlyList<MonsterRule> rules)
    {
        var result = new List<MonsterRule>(rules.Count + 1);
        MonsterRule? first = null;
        foreach (MonsterRule rule in rules)
        {
            if (rule.IsDefault)
                first ??= rule;
        }
        result.Add(first ?? Default);
        foreach (MonsterRule rule in rules)
        {
            if (!rule.IsDefault)
                result.Add(rule);
        }
        return result;
    }

    /// <summary>The debuffs a rule wants, each with the element it applies to (empty for the untyped ones).</summary>
    public static IEnumerable<(DebuffKind Kind, string Element)> Debuffs(MonsterRule rule, string element)
    {
        if (rule.Imperil) yield return (DebuffKind.Imperil, string.Empty);
        if (rule.Vulnerability) yield return (DebuffKind.Vulnerability, element);
        if (rule.ExtraVulnerability.Length > 0 && !rule.ExtraVulnerability.Equals("None", StringComparison.OrdinalIgnoreCase))
            yield return (DebuffKind.Vulnerability, rule.ExtraVulnerability);
        if (rule.Fester) yield return (DebuffKind.Fester, string.Empty);
        if (rule.Yield) yield return (DebuffKind.Yield, string.Empty);
        if (rule.Broadside) yield return (DebuffKind.Broadside, string.Empty);
        if (rule.GravityWell) yield return (DebuffKind.GravityWell, string.Empty);
    }
}
