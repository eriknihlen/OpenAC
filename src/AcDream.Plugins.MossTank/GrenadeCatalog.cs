namespace AcDream.Plugins.MossTank;

internal readonly record struct GrenadeDefinition(
    string Name,
    uint SpellId,
    int Spellcraft,
    int RequiredAlchemy);

/// <summary>
/// VTank's exact 72-entry GameInfoDB GrenadeOptions table. The source is the
/// official Virindi update feed (DB version 9), not an inferred name pattern.
/// </summary>
internal static class GrenadeCatalog
{
    private readonly record struct Tier(
        string Name,
        int RequiredAlchemy,
        int Spellcraft,
        uint Imperil,
        uint Blade,
        uint Acid,
        uint Cold,
        uint Bludgeon,
        uint Fire,
        uint Piercing,
        uint Lightning,
        uint Fester);

    private static readonly Tier[] Tiers =
    [
        new("Iron", 75, 100, 1323, 1128, 522, 1061, 1049, 1104, 1152, 1085, 172),
        new("Copper", 125, 160, 1324, 1129, 523, 1062, 1050, 1105, 1153, 1086, 173),
        new("Silver", 175, 220, 1325, 1130, 524, 1063, 1051, 1106, 1154, 1087, 174),
        new("Gold", 225, 270, 1326, 1131, 525, 1064, 1052, 1107, 1155, 1088, 175),
        new("Pyreal", 275, 340, 1327, 1132, 526, 1065, 1053, 1108, 1156, 1089, 176),
        new("Platinum", 325, 400, 1327, 1132, 526, 1065, 1053, 1108, 1156, 1089, 176),
        new("Empowered Platinum", 375, 460, 1327, 1132, 526, 1065, 1053, 1108, 1156, 1089, 176),
        new("Mana", 400, 520, 2074, 2164, 2162, 2168, 2166, 2170, 2174, 2172, 2178),
    ];

    private static readonly IReadOnlyList<GrenadeDefinition> Entries = Build();
    private static readonly IReadOnlyDictionary<string, GrenadeDefinition> ByName =
        Entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);

    public static IReadOnlyList<GrenadeDefinition> All => Entries;

    public static bool TryGet(string exactName, out GrenadeDefinition definition) =>
        ByName.TryGetValue(exactName, out definition);

    private static IReadOnlyList<GrenadeDefinition> Build()
    {
        var result = new List<GrenadeDefinition>(72);
        foreach (Tier tier in Tiers)
        {
            Add(result, tier, "Imperil", tier.Imperil);
            Add(result, tier, "Blade Vulnerability", tier.Blade);
            Add(result, tier, "Acid Vulnerability", tier.Acid);
            Add(result, tier, "Cold Vulnerability", tier.Cold);
            Add(result, tier, "Bludgeon Vulnerability", tier.Bludgeon);
            Add(result, tier, "Fire Vulnerability", tier.Fire);
            Add(result, tier, "Piercing Vulnerability", tier.Piercing);
            Add(result, tier, "Lightning Vulnerability", tier.Lightning);
        }
        foreach (Tier tier in Tiers)
            Add(result, tier, "Fester", tier.Fester);
        return result;
    }

    private static void Add(
        ICollection<GrenadeDefinition> result,
        Tier tier,
        string effect,
        uint spellId) =>
        result.Add(new GrenadeDefinition(
            $"{tier.Name} Phial of {effect}",
            spellId,
            tier.Spellcraft,
            tier.RequiredAlchemy));
}
