using AcDream.DrakBot.Combat;

namespace AcDream.DrakBot.Spells;

/// <summary>
/// The names the game gives its war, void and creature-debuff spell
/// families, keyed the way a monster rule names an element. These are
/// game data (the spell table's own names); the tier is left off because
/// the catalog knows every tier of a family once one is found.
/// </summary>
public static class WarSpellNames
{
    /// <summary>War magic: [Arc, Ring, Streak, Bolt] per element.</summary>
    private static readonly Dictionary<string, string[]> War = new(StringComparer.OrdinalIgnoreCase)
    {
        // As the spell table names them (Aeshnidae's world database, retail's
        // names): Shock Wave is the bludgeoning bolt, Lightning Bolt the
        // lightning one; rings are Flame, Glacial, Lightning, Acid and Force
        // - there is no blade or bludgeoning ring, and an empty name is
        // simply never found.
        ["Fire"] = ["Flame Arc", "Flame Ring", "Flame Streak", "Flame Bolt"],
        ["Cold"] = ["Frost Arc", "Glacial Ring", "Frost Streak", "Frost Bolt"],
        ["Lightning"] = ["Lightning Arc", "Lightning Ring", "Lightning Streak", "Lightning Bolt"],
        ["Acid"] = ["Acid Arc", "Acid Ring", "Acid Streak", "Acid Stream"],
        ["Blade"] = ["Blade Arc", "", "Blade Streak", "Whirling Blade"],
        ["Slash"] = ["Blade Arc", "", "Blade Streak", "Whirling Blade"],
        ["Pierce"] = ["Force Arc", "Force Ring", "Force Streak", "Force Bolt"],
        ["Bludgeon"] = ["Shock Arc", "", "Shock Wave Streak", "Shock Wave"],
    };

    /// <summary>Void magic: only nether does damage; a corrosion line stands in for fire.</summary>
    private static readonly Dictionary<string, string[]> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nether"] = ["Nether Arc", "Nether Ring", "Nether Streak", "Nether Bolt"],
        ["Fire"] = ["Corrosion Arc", "Corrosion Ring", "Corrosion Streak", "Nether Bolt"],
    };

    private static readonly Dictionary<string, string> Vulnerabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fire"] = "Fire Vulnerability Other",
        ["Cold"] = "Cold Vulnerability Other",
        ["Lightning"] = "Lightning Vulnerability Other",
        ["Acid"] = "Acid Vulnerability Other",
        ["Blade"] = "Blade Vulnerability Other",
        ["Slash"] = "Blade Vulnerability Other",
        ["Pierce"] = "Piercing Vulnerability Other",
        ["Bludgeon"] = "Bludgeoning Vulnerability Other",
    };

    public static readonly IReadOnlyList<string> Elements =
        ["Fire", "Cold", "Lightning", "Acid", "Blade", "Pierce", "Bludgeon", "Nether"];

    /// <summary>The offensive family names to try for an element and shape, most wanted first.</summary>
    public static IEnumerable<string> Offensive(string element, SpellShape shape, bool ring)
    {
        int index = ring ? 1 : shape switch
        {
            SpellShape.Arc => 0,
            SpellShape.Streak => 2,
            _ => 3,
        };
        bool nether = element.Equals("Nether", StringComparison.OrdinalIgnoreCase);
        if (!nether && War.TryGetValue(element, out string[]? war) && war[index].Length > 0)
            yield return war[index];
        if (Void.TryGetValue(element, out string[]? voidLine))
            yield return voidLine[index];
        else if (nether || !War.ContainsKey(element))
            yield return Void["Nether"][index];
    }

    /// <summary>The creature-enchantment family a debuff casts; null when the element has none.</summary>
    public static string? Debuff(DebuffKind kind, string element) => kind switch
    {
        DebuffKind.Imperil => "Imperil Other",
        DebuffKind.Fester => "Fester Other",
        DebuffKind.Yield => "Magic Yield Other",
        DebuffKind.Broadside => "Missile Weapon Ineptitude Other",
        DebuffKind.GravityWell => "Vulnerability Other",
        DebuffKind.Vulnerability => Vulnerabilities.TryGetValue(element, out string? name) ? name : null,
        _ => null,
    };
}
