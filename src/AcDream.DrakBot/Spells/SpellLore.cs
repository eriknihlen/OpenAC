namespace AcDream.DrakBot.Spells;

/// <summary>
/// The other names a spell family goes by in the book. The seventh tier
/// of most buffs carries a lore name in place of the plain one - "Might of
/// the Lugians" is Strength Self VII - and the eighth is "Incantation of"
/// the plain name. A family is usually found through any plainly named
/// tier the character knows; these names are for the book that holds only
/// the top tiers. From RynthAi's spell manager, with retail's own
/// misspellings kept, since they are what the book says.
/// </summary>
public static class SpellLore
{
    private static readonly Dictionary<string, string[]> SeventhTierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Strength"] = ["Might of the Lugians"],
        ["Endurance"] = ["Preservance", "Perseverance"],
        ["Coordination"] = ["Honed Control"],
        ["Quickness"] = ["Hastening"],
        ["Focus"] = ["Inner Calm"],
        ["Willpower"] = ["Mind Blossom"],
        ["Invulnerability"] = ["Aura of Defense"],
        ["Impregnability"] = ["Aura of Deflection"],
        ["Magic Resistance"] = ["Aura of Resistance"],
        ["Armor"] = ["Executor's Blessing"],
        ["Acid Protection"] = ["Caustic Blessing"],
        ["Blade Protection"] = ["Blessing of the Blade Turner"],
        ["Bludgeon Protection"] = ["Blessing of the Mace Turner"],
        ["Bludgeoning Protection"] = ["Blessing of the Mace Turner"],
        ["Cold Protection"] = ["Icy Blessing"],
        ["Frost Protection"] = ["Icy Blessing"],
        ["Fire Protection"] = ["Fiery Blessing"],
        ["Flame Protection"] = ["Fiery Blessing"],
        ["Lightning Protection"] = ["Storm's Blessing"],
        ["Mana Renewal"] = ["Battlemage's Blessing"],
        ["Pierce Protection"] = ["Blessing of the Arrow Turner"],
        ["Piercing Protection"] = ["Blessing of the Arrow Turner"],
        ["Regeneration"] = ["Robustify"],
        ["Rejuvenation"] = ["Unflinching Persistence", "Unfinching Persistance"],
        ["Heal"] = ["Adja's Intervention"],
        ["Revitalize"] = ["Robustification"],
        ["Stamina to Mana"] = ["Meditative Trance"],
        ["Stamina to Health"] = ["Rushed Recovery"],
        ["Monster Attunement"] = ["Topheron's Blessing"],
        ["Person Attunement"] = ["Kaluhc's Blessing"],
        ["Arcane Enlightenment"] = ["Aliester's Blessing"],
        ["Armor Tinkering Expertise"] = ["Jibril's Blessing"],
        ["Item Tinkering Expertise"] = ["Yoshi's Blessing"],
        ["Weapon Tinkering Expertise"] = ["Koga's Blessing"],
        ["Mana Conversion Mastery"] = ["Nuhmidura's Blessing"],
        ["Sprint"] = ["Saladur's Blessing"],
        ["Jumping Mastery"] = ["Jahannan's Blessing"],
        ["Fealty"] = ["Odif's Blessing", "Odif's Boon"],
        ["Leadership Mastery"] = ["Ar-Pei's Blessing"],
        ["Deception Mastery"] = ["Ketnan's Blessing"],
        ["Healing Mastery"] = ["Avalenne's Blessing"],
        ["Lockpick Mastery"] = ["Oswald's Blessing"],
        ["Cooking Mastery"] = ["Morimoto's Blessing"],
        ["Fletching Mastery"] = ["Lilitha's Blessing"],
        ["Alchemy Mastery"] = ["Silencia's Blessing"],
        ["Creature Enchantment Mastery"] = ["Adja's Blessing"],
        ["Item Enchantment Mastery"] = ["Celcynd's Blessing"],
        ["Life Magic Mastery"] = ["Harlune's Blessing"],
        ["War Magic Mastery"] = ["Hieromancer's Blessing"],
        ["Blood Drinker"] = ["Aura of Infected Caress"],
        ["Hermetic Link"] = ["Aura of Mystic's Blessing"],
        ["Heart Seeker"] = ["Aura of Elysa's Sight"],
        ["Spirit Drinker"] = ["Aura of Infected Spirit Carress", "Aura of Infected Spirit Caress"],
        ["Swift Killer"] = ["Aura of Atlan's Alacrity"],
        ["Defender"] = ["Aura of Cragstone's Will"],
        ["Impenetrability"] = ["Brogard's Defiance"],
        ["Acid Bane"] = ["Olthoi's Bane"],
        ["Blade Bane"] = ["Swordsman's Bane", "Swordman's Bane"],
        ["Bludgeoning Bane"] = ["Tusker's Bane"],
        ["Flame Bane"] = ["Inferno's Bane"],
        ["Frost Bane"] = ["Gelidite's Bane"],
        ["Lightning Bane"] = ["Astyrrian's Bane"],
        ["Piercing Bane"] = ["Archer's Bane"],
    };

    private static readonly string[] TargetSuffixes = [" Self", " Other"];

    /// <summary>
    /// The book is not of one mind about its elements - Fire Protection
    /// but Flame Bane, Cold Protection but Frost Bane, Piercing here and
    /// Pierce there - and VTank profiles and old habit pick either. Both
    /// sides of a comparison are spelt one way first, so any of them
    /// finds the family.
    /// </summary>
    private static readonly (string From, string To)[] ElementSpellings =
    [
        ("Flame", "Fire"),
        ("Frost", "Cold"),
        ("Piercing", "Pierce"),
        ("Bludgeoning", "Bludgeon"),
    ];

    private static string Normalize(string baseName)
    {
        string[] words = baseName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool changed = false;
        for (int index = 0; index < words.Length; index++)
        {
            foreach ((string from, string to) in ElementSpellings)
            {
                if (string.Equals(words[index], from, StringComparison.OrdinalIgnoreCase))
                {
                    words[index] = to;
                    changed = true;
                }
            }
        }
        return changed ? string.Join(' ', words) : baseName;
    }

    private static readonly Dictionary<string, string[]> SeventhTierNamesNormalized = Build();

    private static Dictionary<string, string[]> Build()
    {
        var table = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach ((string root, string[] lore) in SeventhTierNames)
            table[Normalize(root)] = lore;
        return table;
    }

    /// <summary>
    /// Whether a spell in the book, by its tierless name, is a tier of the
    /// family the user named: the same name, "Incantation of" it, or the
    /// seventh tier's lore name (with the same Self/Other suffix, or none,
    /// as the book spells them).
    /// </summary>
    public static bool IsTierOf(string bookBaseName, string wantedBaseName)
    {
        bookBaseName = Normalize(bookBaseName);
        wantedBaseName = Normalize(wantedBaseName);
        if (string.Equals(bookBaseName, wantedBaseName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (bookBaseName.StartsWith("Incantation of ", StringComparison.OrdinalIgnoreCase)
            && string.Equals(bookBaseName["Incantation of ".Length..], wantedBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        (string root, string suffix) = Split(wantedBaseName);
        if (!SeventhTierNamesNormalized.TryGetValue(root, out string[]? lore))
            return false;
        foreach (string name in lore)
        {
            if (string.Equals(bookBaseName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(bookBaseName, name + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True for a name ending in Self, false for Other, null when it names neither.</summary>
    public static bool? WantsSelf(string baseName)
    {
        if (baseName.EndsWith(" Self", StringComparison.OrdinalIgnoreCase))
            return true;
        if (baseName.EndsWith(" Other", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    private static (string Root, string Suffix) Split(string baseName)
    {
        foreach (string suffix in TargetSuffixes)
        {
            if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (baseName[..^suffix.Length].TrimEnd(), suffix);
        }
        return (baseName, string.Empty);
    }
}
